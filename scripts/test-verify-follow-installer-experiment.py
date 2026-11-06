"""Independent oracle guard tests; synthetic objects provide no live ETW credit."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('follow_oracle', Path(__file__).with_name('verify-follow-installer-experiment.py'))
VERIFY = importlib.util.module_from_spec(spec)
spec.loader.exec_module(VERIFY)


class OracleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.explanations = Path(self.temp.name)
        self.expected = dict(capture_exit_code=0,independent_exit_code=0,source_sha='a'*40, directory_b_existed_before=False,product_watch_arguments=[], excluded_io_paths=[r'C:\private\capture.db',r'C:\private\stdout.log'],excluded_io_directories=[r'C:\private'], targets=[
            dict(label='parent', role='followparent', path=r'C:\fixture\A\parent-target.txt', content_sha256='a'*64, actual_content_sha256='a'*64),
            dict(label='child', role='followchild', path=r'C:\fixture\B\child-target.txt', content_sha256='b'*64, actual_content_sha256='b'*64),
            dict(label='unrelated', role='followunrelated', path=r'C:\fixture\A\unrelated-target.txt', content_sha256='c'*64, actual_content_sha256='c'*64),
        ])
        self.truth = [dict(role=t['role'], resource=t['path'], operation='write_requested', success=True,
            pid=pid, creation_time=birth, start_ticks='140', end_ticks='160')
            for t, pid, birth in zip(self.expected['targets'], (10,20,30), ('100','120','90'))]
        events = []
        for t, o, rule in zip(self.expected['targets'][:2], self.truth, ('InstallerPid','DescendantProcess')):
            events.append(dict(id='event-'+t['label'], event_type='file', operation='write_requested',
                timestamp_ticks='150', resource=t['path'], confidence='High', success=None,
                state_validated=False, dimensions=dict(actor='High',resource='High',operation='Unknown'),
                evidence=dict(source='EtwFile', pid=o['pid'], process_creation_time=o['creation_time'],
                    process_image=r'C:\package\fixture.exe', rule=rule, session_id='session-one'),
                raw=dict(resource_resolved=True, file_name=dict(current_path_verified=False, path_source='file_object_cache'))))
        self.capture = dict(id='session-one', capture_state='finished', watch_roots=[], events=events,
            stats=dict(drain_timed_out=False), backend=dict(follow_installer=dict(direct_children_only=True,baseline_available=False,candidate_record_limit=2048,candidate_byte_limit=8388608,resource_limit=4096,candidate_dropped=0,candidate_high_water_records=3,candidate_high_water_bytes=3000),stream=dict(quota_bytes=67108864,committed_bytes=3000,persisted=3,failed=0,quota_dropped=0,error=None),etw_process='active',etw_file='active',
                etw_events_lost=0,etw_buffers_lost=0,dropped_events=0,decode_errors=0,context_losses=0),
            processes=[dict(pid=10,creation_time='100',confidence='Certain',image=r'C:\package\fixture.exe', ended_at='200'),
                dict(pid=20,creation_time='120',parent_pid=10,parent_creation_time='100',confidence='High',image=r'C:\package\fixture.exe',ended_at='180')],
            files=[], warnings=[VERIFY.BASELINE_WARNING])
        self.save_explanations()

    def save_explanations(self):
        for event in self.capture['events']:
            value=dict(schema_version=3,kind='explain',data=dict(app_id='session-one',event=event,operations=[],edges=[]))
            (self.explanations/(event['id']+'.json')).write_text(json.dumps(value))

    def verify(self):
        return VERIFY.verify(self.capture,self.truth,self.expected,self.explanations,'a'*40)

    def test_valid_actor_association_keeps_unknown_completion_and_ownership(self):
        result=self.verify()
        self.assertTrue(result['accepted'],result)
        self.assertEqual(result['not_run'],0)
        self.assertEqual(len(result['checks']),len(VERIFY.PLAN))
        self.assertIsNone(result['observations'][0]['completion'])
        self.assertFalse(result['ownership_claim'])
        self.assertEqual(result['observations'][-1]['classification'],'unobserved')

    def test_selection_does_not_depend_on_attribution_answers(self):
        self.capture['events'][0]['confidence']='Unknown'
        self.capture['events'][0]['evidence']['rule']='MissingWriter'
        self.assertEqual(VERIFY.select(self.capture,self.expected)['candidate_event_ids'],['event-parent','event-child'])
        self.assertFalse(self.verify()['accepted'])

    def test_pid_reuse_creation_time_cannot_match_oracle(self):
        self.capture['events'][0]['evidence']['process_creation_time']='101'
        self.save_explanations()
        result=self.verify()
        self.assertFalse(result['accepted'])
        self.assertIn('parent_verified_source_write',result['unresolved'])
        self.assertGreater(result['not_run'],0)
        self.assertEqual(result['candidate_evidence'][0]['events'][0]['event_id'],'event-parent')

    def test_child_requires_exact_direct_parent_instance(self):
        self.capture['processes'][1]['parent_creation_time']='99'
        self.assertFalse(self.verify()['accepted'])

    def test_source_event_must_fall_inside_independent_syscall_interval(self):
        self.capture['events'][0]['timestamp_ticks']='161'
        self.save_explanations()
        self.assertFalse(self.verify()['accepted'])

    def test_numeric_time_fields_cannot_silently_lose_precision(self):
        self.capture['events'][0]['timestamp_ticks']=150
        self.assertFalse(self.verify()['accepted'])

    def test_unrelated_write_cannot_be_promoted(self):
        event=copy.deepcopy(self.capture['events'][0])
        event.update(id='event-unrelated',resource=self.expected['targets'][2]['path'])
        event['evidence'].update(pid=30,process_creation_time='90')
        self.capture['events'].append(event)
        self.assertFalse(self.verify()['accepted'])

    def test_late_source_loss_disqualifies_experiment(self):
        self.capture['backend']['etw_events_lost']=1
        self.assertFalse(self.verify()['accepted'])

    def test_unknown_preinstall_hash_alone_is_not_baseline_disclosure(self):
        self.capture['warnings']=[]
        self.assertFalse(self.verify()['accepted'])

    def test_post_only_state_must_not_be_promoted_to_ownership(self):
        self.capture['files']=[dict(path=self.expected['targets'][0]['path'],confidence='High')]
        self.assertFalse(self.verify()['accepted'])

    def test_reopened_explanation_must_match_full_original_event(self):
        p=self.explanations/'event-child.json'
        value=json.loads(p.read_text());value['data']['event']['operation']='read'
        p.write_text(json.dumps(value))
        self.assertFalse(self.verify()['accepted'])

    def test_content_answer_is_independent_and_fixed(self):
        self.expected['targets'][1]['actual_content_sha256']='f'*64
        self.assertFalse(self.verify()['accepted'])

    def test_product_must_not_receive_preset_roots(self):
        self.capture['watch_roots']=[r'C:\fixture']
        self.assertFalse(self.verify()['accepted'])

    def test_resource_paths_are_not_published(self):
        summary=json.dumps(self.verify())
        self.assertNotIn('C:\\\\fixture',summary)
        self.assertNotIn('C:\\\\package',summary)


    def test_pending_overflow_must_fail_instead_of_silent_success(self):
        self.capture['backend']['follow_installer']['candidate_dropped']=1
        self.assertFalse(self.verify()['accepted'])

    def test_storage_limit_must_be_real_and_lossless(self):
        self.capture['backend']['stream']['quota_bytes']=67108865
        self.assertFalse(self.verify()['accepted'])

    def test_private_database_and_journal_io_are_not_retained(self):
        self.capture['events'].append(dict(id='private-io',event_type='file',resource=r'C:\private\capture.db-wal'))
        self.assertFalse(self.verify()['accepted'])


if __name__=='__main__':
    unittest.main()
