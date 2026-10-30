import copy
import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


AUDIT = module('etw_clock_audit', 'analyze-etw-clock.py')
FIXTURE = module('etw_clock_fixture', 'test-v04-clock-protocol.py')


class ClockAuditTests(unittest.TestCase):
    def setUp(self):
        base = FIXTURE.ClockProtocolTests()
        base.setUp()
        base.native_provenance()
        document = base.companion()
        base.event['id'] = '11111111-1111-4111-8111-111111111111'
        base.event.update(success=True, dimensions=dict(actor='High', operation='High', resource='High'))
        base.event['evidence'].update(source='EtwFile', thread_id=9)
        base.event['raw']['file_name'] = dict(path_source='supplied_FileName', observed_ticks='100150',
            source_event_id=base.event['id'], current_path_verified=False)
        self.raw = base.companion_bytes(document)
        self.capture = base.capture
        self.capture.update(watch_roots=['C:\\fixture'], registry_key=None, capture_state='finished',
            stats=dict(events_dropped=0, drain_timed_out=False))
        self.capture['backend'].update(etw_file='active', etw_process='active', etw_registry='active',
            dropped_events=0, context_losses=0, decode_errors=0, etw_events_lost=0, etw_buffers_lost=0,
            pipeline=dict(queue_overflow=0, enqueue_disconnected=0, queue_pending=0, context_evictions=0,
                          retention_dropped=0, failed_without_output=0),
            handoff=dict(overflow=0, oversized=0, disconnected=0, serialize_failed=0, pending=0,
                         receiver_finished=True, writer_finished=True),
            stream=dict(failed=0, quota_dropped=0, error=None))
        self.capture['quality'] = dict(level='Degraded')
        self.capture['raw_events'] = copy.deepcopy(self.capture['events'])
        self.capture['raw_events'][0].update(confidence='Unknown', success=None)
        self.capture['raw_events'][0]['evidence'].update(pid=None, process_creation_time=None)
        self.truth = [base.truth]
        self.score = AUDIT.SCORE.score(self.capture, self.truth)

    def analyze(self, raw=True):
        return AUDIT.analyze(self.capture, self.truth, self.score, self.raw if raw else None)

    def write_inputs(self, root, database):
        capture = copy.deepcopy(self.capture)
        capture.pop('raw_events')
        capture['id'] = 'synthetic-session'
        (root/'capture').mkdir()
        for name, value in (('capture.json', dict(schema_version=3, kind='inspect', data=capture)),
                            ('ground-truth.json', self.truth), ('reliability.json', self.score)):
            (root/'capture'/name).write_text(json.dumps(value))
        (root/'raw-clock.json').write_bytes(self.raw)
        names = ('ci-etw-clock.ps1', 'demo.ps1', 'demo-provenance.ps1', 'demo-measure.py', 'analyze-etw-clock.py',
                 'v04-clock-protocol.py', 'run-write-diagnostic.py', 'verify-layered-evidence.py',
                 'analyze-write-diagnostic.py', 'score-fixture.py', 'score-fixture.ps1')
        scripts = [dict(path='scripts/'+name, sha256=AUDIT.sha(Path(__file__).with_name(name))) for name in names]
        source = dict(head='a'*40, runtime_tree_sha256='b'*64, status=[])
        before = [dict(path=str(root/name), sha256='c'*64, bytes=100) for name in ('contain.exe', 'contain-test-installer.exe')]
        identity = dict(database=str(database), fixture_clock='1', raw_clock_path=str(root/'raw-clock.json'), build_exit_code=0,
            source_stable_during_build=True, source_before_build=source, source_after_build=source,
            binary_before=before, binary_after=dict(unchanged=True, binaries=[dict(before=row, after=row, unchanged=True, error=None) for row in before]))
        attempt = dict(source_sha='a'*40, source_after_sha='a'*40, source_unchanged=True,
            source_clean_before=True, source_clean_after=True, source_status_before=[], source_status_after=[],
            demo_started=True, demo_exit_code=1, phase='diagnostic', planned_attempts=1, sid='',
            fixture_clock_requested='1', raw_clock_requested=True, scripts_before=scripts, scripts_after=scripts, scripts_unchanged=True)
        (root/'attempt.json').write_text(json.dumps(attempt))
        (root/'capture/run-identity.json').write_text(json.dumps(identity))
        clock = AUDIT.CLOCK.analyze(capture, self.truth, raw_clock_bytes=self.raw, raw_clock_path=root/'raw-clock.json')
        clock['input_sha256'] = dict(capture=AUDIT.sha(root/'capture/capture.json'), truth=[AUDIT.sha(root/'capture/ground-truth.json')], raw_clock=AUDIT.sha(root/'raw-clock.json'))
        clock['script_lf_sha256'] = AUDIT.hashlib.sha256(Path(AUDIT.CLOCK.__file__).read_bytes().replace(b'\r\n', b'\n')).hexdigest()
        (root/'capture/clock-diagnostic.json').write_text(json.dumps(clock))

    def run_cli(self, private, public):
        return subprocess.run([sys.executable, str(Path(__file__).with_name('analyze-etw-clock.py')),
            '--directory', str(private), '--public-output', str(public)], capture_output=True, text=True)

    def wal_source(self, database, state='finished'):
        writer = sqlite3.connect(database)
        writer.execute('PRAGMA journal_mode=WAL')
        writer.execute('PRAGMA wal_autocheckpoint=0')
        writer.execute('CREATE TABLE capture_runs (session_id TEXT, state TEXT)')
        writer.execute('CREATE TABLE raw_stream (session_id TEXT, sequence INTEGER, document TEXT)')
        writer.commit()
        writer.execute('PRAGMA wal_checkpoint(TRUNCATE)')
        writer.execute('INSERT INTO capture_runs VALUES (?, ?)', ('synthetic-session', state))
        writer.execute('INSERT INTO raw_stream VALUES (?, ?, ?)', ('synthetic-session', 1, json.dumps(self.capture['raw_events'][0])))
        writer.commit()
        return writer

    def test_wal_only_finished_state_and_raw_rows_survive_read_only_snapshot(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); private = root/'private'; private.mkdir()
            database = root/'source.db'
            writer = self.wal_source(database)
            try:
                self.write_inputs(private, database)
                before = {suffix:AUDIT.sha(Path(str(database)+suffix)) for suffix in ('', '-wal')}
                result = self.run_cli(private, root/'public')
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                report = json.loads((root/'public/summary.json').read_text())
                self.assertTrue(report['diagnostic_evidence_complete'])
                self.assertEqual(report['raw_join_status'], 'verified_same_event_id')
                exported = json.loads((private/'capture/private-raw.json').read_text())
                self.assertEqual(exported['data']['raw_events'], self.capture['raw_events'])
                self.assertEqual(before, {suffix:AUDIT.sha(Path(str(database)+suffix)) for suffix in ('', '-wal')})
            finally:
                writer.close()

    def test_raw_export_failure_preserves_bound_final_view_clock_and_old_score(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); private = root/'private'; private.mkdir()
            self.truth[0].update(start_ticks='99990', end_ticks='100100')
            self.score = AUDIT.SCORE.score(self.capture, self.truth)
            self.write_inputs(private, root/'missing-source.db')
            result = self.run_cli(private, root/'public')
            self.assertEqual(result.returncode, 2, result.stderr)
            report = json.loads((root/'public/summary.json').read_text())
            self.assertFalse(report['diagnostic_evidence_complete'])
            self.assertEqual(report['raw_join_status'], 'unavailable')
            self.assertEqual(report['events_view_scope'], 'final_view_only_no_raw_verification')
            self.assertTrue(report['existing_clock_report_input_hashes_verified'])
            self.assertEqual(report['legacy_score']['false_positive_target_events'], 1)
            self.assertEqual(report['legacy_score']['unexpected_positive_events'], [self.capture['events'][0]['id']])
            self.assertTrue(report['legacy_score_recomputed_equal'])
            self.assertEqual(report['clock']['original_qpc_links'], 1)
            self.assertIsNone(report['supported_events_private'])
            self.assertIsNone(report['events'][0]['exact_qpc_truth_rows'])
            self.assertIsNone(report['events'][0]['unique_qpc_candidate'])
            self.assertEqual(report['events'][0]['comparison_status'], 'not_run')
            self.assertEqual(report['existing_final_view_clock_rows'][0]['status'], 'unique_interval_candidate')
            self.assertEqual(report['independent_truth'][0]['clock']['start_qpc'], '100')
            self.assertIsNone(report['acceptance_pass'])

    def test_residual_wal_without_writer_and_empty_wal_initialization(self):
        for residual in (True, False):
            with self.subTest(residual=residual), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); private = root/'private'; private.mkdir()
                source = root/'source.db'
                writer = self.wal_source(source)
                if residual:
                    closed_source = root/'residual.db'
                    for suffix in ('', '-wal'):
                        Path(str(closed_source)+suffix).write_bytes(Path(str(source)+suffix).read_bytes())
                    writer.close(); source = closed_source
                else:
                    writer.close()
                    self.assertFalse(Path(str(source)+'-wal').exists())
                self.write_inputs(private, source)
                result = self.run_cli(private, root/'public')
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                report = json.loads((root/'public/summary.json').read_text())
                self.assertTrue(report['snapshot']['source_db_wal_unchanged'])
                self.assertEqual(report['snapshot']['source_before'], report['snapshot']['source_after_close'])
                self.assertEqual(report['snapshot']['empty_wal_initialized_by_read_open'], not residual)

    def test_unfinished_or_wrong_session_is_rejected_without_discarding_prior_evidence(self):
        for kind in ('capturing', 'finalizing', 'wrong_session'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); private = root/'private'; private.mkdir()
                database = root/'source.db'
                writer = self.wal_source(database, state=kind if kind != 'wrong_session' else 'finished')
                try:
                    self.write_inputs(private, database)
                    if kind == 'wrong_session':
                        writer.execute("UPDATE capture_runs SET session_id='other-session'"); writer.commit()
                    result = self.run_cli(private, root/'public')
                    self.assertEqual(result.returncode, 2, result.stderr)
                    report = json.loads((root/'public/summary.json').read_text())
                    self.assertIn('source_session_not_uniquely_finished', report['missing_evidence'])
                    self.assertEqual(report['legacy_score']['expected'], 1)
                    self.assertFalse(report['diagnostic_evidence_complete'])
                finally:
                    writer.close()

    def test_snapshot_target_is_exclusive_and_concurrent_source_change_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); private = root/'private'; private.mkdir()
            database = root/'source.db'
            writer = self.wal_source(database)
            try:
                self.write_inputs(private, database)
                target = private/'existing.db'; target.write_bytes(b'preserve this owned sentinel')
                with self.assertRaisesRegex(ValueError, 'snapshot_or_export_destination_exists'):
                    AUDIT.snapshot_export(database, private/'capture/capture.json', target, private/'out.json', {})
                self.assertEqual(target.read_bytes(), b'preserve this owned sentinel')
                original = AUDIT.database_files
                observed = 0
                def change_during_copy(path):
                    nonlocal observed
                    observed += 1
                    if observed == 3:
                        writer.execute('INSERT INTO raw_stream VALUES (?, ?, ?)', ('synthetic-session', 2, json.dumps(self.capture['raw_events'][0])))
                        writer.commit()
                    return original(path)
                record = {}
                with mock.patch.object(AUDIT, 'database_files', side_effect=change_during_copy):
                    with self.assertRaisesRegex(ValueError, 'source_database_changed_during_snapshot'):
                        AUDIT.snapshot_export(database, private/'capture/capture.json', private/'new.db', private/'out.json', record)
                self.assertNotEqual(record['source_before'], record['source_after'])
                self.assertFalse(record['source_db_wal_unchanged'])
                self.assertFalse((private/'out.json').exists())
            finally:
                writer.close()

    def test_missing_or_unbound_prior_clock_keeps_manifest_observation_without_links(self):
        for kind in ('missing', 'unbound'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); private = root/'private'; private.mkdir()
                self.write_inputs(private, root/'missing-source.db')
                clock_path = private/'capture/clock-diagnostic.json'
                if kind == 'missing': clock_path.unlink()
                else:
                    prior = json.loads(clock_path.read_text()); prior['input_sha256']['capture'] = '0'*64
                    clock_path.write_text(json.dumps(prior))
                result = self.run_cli(private, root/'public')
                self.assertEqual(result.returncode, 2, result.stderr)
                report = json.loads((root/'public/summary.json').read_text())
                self.assertFalse(report['existing_clock_report_input_hashes_verified'])
                self.assertIsNone(report['clock']['original_qpc_links'])
                self.assertEqual(report['raw_manifest_observation']['stats']['stored'], 1)
                self.assertEqual(report['raw_manifest_observation']['scope'], 'manifest_fields_only_not_event_links')

    def test_same_id_join_uses_final_actor_without_mutating_raw_or_truth(self):
        before = copy.deepcopy((self.capture, self.truth))
        value = self.analyze()
        self.assertTrue(value['clock_evidence_complete'])
        self.assertEqual(value['events'][0]['evidence']['pid'], 7)
        self.assertEqual(value['relations'][0]['exact_qpc_truth_rows'], [0])
        self.assertTrue(value['relations'][0]['unique_qpc_candidate'])
        self.assertIsNone(value['acceptance_pass'])
        self.assertEqual((self.capture, self.truth), before)

    def test_wrong_actor_resource_operation_and_outside_interval_never_match(self):
        for kind in ('birth', 'pid', 'resource', 'operation', 'outside'):
            with self.subTest(kind=kind):
                self.setUp()
                final, raw = self.capture['events'][0], self.capture['raw_events'][0]
                if kind == 'birth': final['evidence']['process_creation_time'] = '1001'
                if kind == 'pid': final['evidence']['pid'] = 8
                if kind == 'resource': final['resource'] = raw['resource'] = 'C:\\fixture\\wrong'
                if kind == 'operation': final['operation'] = raw['operation'] = 'delete_requested'
                if kind == 'outside':
                    self.truth[0]['clock'].update(start_qpc='151', end_qpc='200')
                value = self.analyze()
                self.assertEqual(value['relations'][0]['exact_qpc_truth_rows'], [])
                self.assertFalse(value['relations'][0]['unique_qpc_candidate'])

    def test_truth_never_supplies_missing_production_actor(self):
        self.capture['events'][0]['evidence'].update(pid=None, process_creation_time=None)
        value = self.analyze()
        self.assertIsNone(value['events'][0]['evidence']['pid'])
        self.assertEqual(value['relations'][0]['candidate_truth_rows'], [])
        self.assertIsNone(value['relations'][0]['comparisons'][0]['same_actor'])

    def test_overlapping_truth_candidates_remain_ambiguous(self):
        other = copy.deepcopy(self.truth[0]); other['clock']['sequence'] = 2
        self.truth.append(other)
        value = self.analyze()
        self.assertEqual(value['relations'][0]['exact_qpc_truth_rows'], [0, 1])
        self.assertFalse(value['relations'][0]['unique_qpc_candidate'])

    def test_two_distinct_source_keys_in_one_interval_are_not_unique(self):
        event_id = '22222222-2222-4222-8222-222222222222'
        key = '0000000000000002'
        for view in ('raw_events', 'events'):
            other = copy.deepcopy(self.capture[view][0])
            other['id'] = event_id
            other['raw']['clock']['event_key'] = key
            other['raw']['file_name']['source_event_id'] = event_id
            self.capture[view].append(other)
        document = json.loads(self.raw)
        other = copy.deepcopy(document['records'][0])
        other.update(event_key=key, sequence=2, header_timestamp='151')
        document['records'].append(other)
        document['stats'].update(received=2, stored=2, bytes=160)
        base = FIXTURE.ClockProtocolTests(); base.capture = self.capture
        self.raw = base.companion_bytes(document)
        value = self.analyze()
        self.assertTrue(value['clock_evidence_complete'])
        self.assertEqual([row['exact_qpc_truth_rows'] for row in value['relations']], [[0], [0]])
        self.assertEqual([row['unique_qpc_candidate'] for row in value['relations']], [False, False])
        self.assertEqual(len(AUDIT.public_summary(value)['events']), 2)

    def test_duplicate_and_conflicting_views_fail_closed(self):
        for kind in ('duplicate_raw', 'duplicate_final', 'resource', 'clock', 'file_name', 'registry_object', 'registry_fields'):
            with self.subTest(kind=kind):
                self.setUp()
                if kind == 'duplicate_raw': self.capture['raw_events'] *= 2
                elif kind == 'duplicate_final': self.capture['events'] *= 2
                elif kind == 'resource': self.capture['events'][0]['resource'] = 'C:\\fixture\\other'
                elif kind == 'clock': self.capture['events'][0]['raw']['clock']['event_key'] = '0000000000009999'
                elif kind == 'file_name': self.capture['events'][0]['raw']['file_name']['current_path_verified'] = True
                else: self.capture['events'][0]['raw'][kind] = 'different-object-or-fields'
                value = self.analyze()
                self.assertFalse(value['clock_evidence_complete'])
                self.assertTrue(value['join_issues'])
                self.assertFalse(any(row['unique_qpc_candidate'] for row in value['relations']))

    def test_missing_raw_view_and_missing_qpc_never_fall_back(self):
        self.assertFalse(self.analyze(raw=False)['clock_evidence_complete'])
        del self.capture['raw_events']
        value = self.analyze()
        self.assertFalse(value['clock_evidence_complete'])
        self.assertIn('raw_events_missing', value['join_issues'])

    def test_loss_and_unknown_measurements_are_not_zero(self):
        for field, bad in (('etw_events_lost', None), ('etw_buffers_lost', 1)):
            with self.subTest(field=field):
                self.setUp(); self.capture['backend'][field] = bad
                value = self.analyze()
                self.assertFalse(value['clock_evidence_complete'])
                self.assertEqual(value['integrity']['checks']['backend.' + field]['actual'], bad)

    def test_public_projection_excludes_machine_paths_sid_and_unrelated_actor(self):
        value = self.analyze()
        value['clock']['trace_metadata']['private_value'] = 'SECRET_MACHINE'
        value['events'][0]['evidence']['process_image'] = 'C:\\SECRET_USER\\private.exe'
        value['events'][0]['raw']['file_name']['path_source'] = 'SECRET_NAME'
        value['events'][0]['raw']['clock']['record_identity'].update(process_id=999, thread_id=998)
        public = AUDIT.public_summary(value)
        text = json.dumps(public)
        for secret in ('SECRET_MACHINE', 'SECRET_USER', 'SECRET_NAME', 'C:\\\\fixture', 'machine_name_sha256', 'boot_identifier'):
            self.assertNotIn(secret, text)
        event = public['events'][0]
        self.assertIsNone(event['record_identity']['process_id'])
        self.assertIsNone(event['record_identity']['thread_id'])
        self.assertEqual(event['resource'], '<fixture-resource-1>')
        self.assertIsNone(public['acceptance_pass'])

    def test_legacy_score_and_extra_positives_are_preserved(self):
        self.truth[0].update(start_ticks='99990', end_ticks='100100')
        self.score = AUDIT.SCORE.score(self.capture, self.truth)
        value = self.analyze()
        public = AUDIT.public_summary(value)
        self.assertTrue(value['legacy_score_recomputed_equal'])
        self.assertEqual(public['legacy_score']['false_positive_target_events'], 1)
        self.assertEqual(public['legacy_score']['unexpected_positive_events'], [self.capture['events'][0]['id']])
        self.assertEqual(public['events'][0]['comparisons'][0]['filetime_relation'], 'after')
        self.assertEqual(public['events'][0]['comparisons'][0]['qpc_relation'], 'inside')
        self.assertIsNone(public['acceptance_pass'])

    def test_source_script_and_binary_identity_bindings_reject_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            names = ('ci-etw-clock.ps1', 'demo.ps1', 'demo-provenance.ps1', 'demo-measure.py', 'analyze-etw-clock.py',
                     'v04-clock-protocol.py', 'run-write-diagnostic.py', 'verify-layered-evidence.py',
                     'analyze-write-diagnostic.py', 'score-fixture.py', 'score-fixture.ps1', 'write-stack.wprp')
            scripts = [dict(path='scripts/'+name, sha256=AUDIT.sha(Path(__file__).with_name(name))) for name in names]
            revision = 'a' * 40
            source = dict(head=revision, runtime_tree_sha256='b'*64, status=[])
            before = [dict(path=str(root/name), sha256='c'*64, bytes=100) for name in ('contain.exe', 'contain-test-installer.exe')]
            identity = dict(fixture_clock='1', raw_clock_path=str(root/'raw-clock.json'), build_exit_code=0,
                source_stable_during_build=True, source_before_build=source, source_after_build=source,
                binary_before=before, binary_after=dict(unchanged=True, binaries=[dict(before=row, after=row, unchanged=True, error=None) for row in before]))
            attempt = dict(source_sha=revision, source_after_sha=revision, source_unchanged=True,
                source_clean_before=True, source_clean_after=True, source_status_before=[], source_status_after=[],
                demo_started=True, demo_exit_code=1, phase='diagnostic', planned_attempts=1,
                fixture_clock_requested='1', raw_clock_requested=True, scripts_before=scripts, scripts_after=scripts, scripts_unchanged=True)
            self.assertEqual(AUDIT.identities(attempt, identity, root)[1], [])
            for phase, count, index, valid in (('diagnostic', 1, 1, True), ('verification', 2, 1, True),
                    ('verification', 2, 2, True), ('verification', 1, 1, False), ('diagnostic', 1, 2, False),
                    ('verification', 2, 3, False), ('verification', 2, True, False)):
                with self.subTest(phase=phase, count=count, index=index):
                    current = dict(attempt, phase=phase, planned_attempts=count, attempt_index=index)
                    errors = AUDIT.identities(current, identity, root)[1]
                    self.assertEqual('fixed_diagnostic_plan_missing' not in errors, valid)
            for kind in ('source', 'script', 'binary', 'disabled_clock', 'path_escape'):
                with self.subTest(kind=kind):
                    a, i = copy.deepcopy(attempt), copy.deepcopy(identity)
                    if kind == 'source': a['source_after_sha'] = 'd'*40
                    if kind == 'script': a['scripts_after'] = []
                    if kind == 'binary': i['binary_before'] = []
                    if kind == 'disabled_clock': i['fixture_clock'] = None
                    if kind == 'path_escape': a['scripts_before'][0]['path'] = 'scripts/../private.py'; a['scripts_after'] = a['scripts_before']
                    self.assertTrue(AUDIT.identities(a, i, root)[1])

    def test_clock_failure_codes_are_specific_and_private_errors_are_not_copied(self):
        value = self.analyze()
        value['clock']['raw_companion']['reasons'] = ['raw_companion_binary_writer_incomplete', 'C:\\SECRET_USER\\private.db']
        value['clock']['clock_provenance']['reason'] = 'fixture_collector_machine_or_boot_mismatch'
        public = AUDIT.public_summary(value)
        self.assertIn('raw_companion_binary_writer_incomplete', public['missing_evidence'])
        self.assertIn('fixture_collector_machine_or_boot_mismatch', public['missing_evidence'])
        self.assertIn('private_clock_detail_retained', public['missing_evidence'])
        self.assertNotIn('SECRET_USER', json.dumps(public))

    def test_cli_retains_missing_evidence_without_private_error_publication(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            private, public = root/'private', root/'public'
            private.mkdir()
            result = subprocess.run([sys.executable, str(Path(__file__).with_name('analyze-etw-clock.py')),
                '--directory', str(private), '--public-output', str(public)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2, result.stderr)
            report = json.loads((public/'summary.json').read_text())
            self.assertFalse(report['diagnostic_evidence_complete'])
            self.assertIn('attempt_file_missing', report['missing_evidence'])
            self.assertIsNone(report['supported_events_private'])
            self.assertIsNone(report['supported_events_public'])
            self.assertEqual(report['events_view_scope'], 'unavailable')
            self.assertTrue((private/'clock-analysis.json').is_file())
            self.assertNotIn(str(root), json.dumps(report))
            self.assertIsNone(report['acceptance_pass'])


if __name__ == '__main__':
    unittest.main()
