"""Synthetic guard inputs: window validity, loss retention, safety, and privacy."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('backlog', Path(__file__).with_name('summarize-backlog-comparison.py'))
S = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(S)


def profile(overflow=0, backlog=False):
    snapshot = dict(enqueued=100, overflow=overflow, dequeued=100, pending=0, high_water=2000 if backlog else 10,
        max_delay_ns=20, first_backlog_ns=5 if backlog else None, first_overflow_ns=7 if overflow else None)
    record = dict(stage='receiver_follow_classify', start_ns=0, end_ns=10, units=10, cpu_ns=None,
        before=snapshot.copy(), after=snapshot.copy(), follow=dict(pending_records=0, pending_bytes=0, scan_visits=5,
            drops=[0]*6, pending_reason_drops=[0]*3, pending_attempts=[0]*3))
    window = dict(trigger_ns=10, observed_at_ns=10, detection_lag_ns=0, queue_depth=snapshot['high_water'],
        pre_records=0, post_records=0, pre_capacity_full=False, post_capacity_full=False,
        earlier_observations_omitted=0, later_observations_omitted=0, trigger_before_retained_context=False,
        trigger_contained_by_completed_span=True, records=[record])
    value = dict(protocol='contain-receiver-backlog-window-1', backlog_threshold_records=1024,
        pre_capacity=64, post_capacity=64, maximum_records_per_window=129, receiver_observations=1,
        source_high_water=snapshot['high_water'], source_max_delay_ns=20,
        first_backlog_ns=5 if backlog else None, first_overflow_ns=7 if overflow else None,
        first_backlog=(copy.deepcopy(window) | dict(trigger_ns=5, detection_lag_ns=5)) if backlog else None,
        first_overflow=(copy.deepcopy(window) | dict(trigger_ns=7, detection_lag_ns=3)) if overflow else None,
        maximum_backlog=window, drop_reason_order=S.DROP_ORDER, pending_reason_order=S.PENDING_ORDER)
    return dict(thread_cpu_enabled=True, backlog_windows=value), dict(queue_overflow=overflow, queue_high_water=snapshot['high_water'])


def inputs(reproduced=False):
    raw, pipeline = profile(4 if reproduced else 0, reproduced)
    sample = dict(measurement_valid=True, strict_oracle_accepted=not reproduced,
        receiver_backlog=S.safe_profile(raw, pipeline))
    plan = dict(protocol='contain-receiver-backlog-plan-1', order=S.ORDER, maximum_native_captures=4,
        retries=0, background_files=12000, profile=True, backlog_window=True, thread_cpu=True,
        build_identity=dict(configuration='debug', target='x86_64-pc-windows-msvc', binaries={
            'baseline-contain.exe': dict(sha256='a'*64, bytes=100),
            'candidate-contain.exe': dict(sha256='b'*64, bytes=110)}))
    execution = dict(attempted=S.ORDER, not_run=[], stop_reason=None)
    return plan, execution, {name: copy.deepcopy(sample) for name in S.ORDER}


class Windows(unittest.TestCase):
    def test_actual_B1_identical_cores_veto_comparison_even_when_every_sample_passes(self):
        args = inputs()
        fixture = json.loads((Path(__file__).parent/'fixtures/receiver-backlog-identical-binaries.json').read_text(encoding='utf-8'))
        args[0]['build_identity']['binaries'] = fixture['binaries']
        result = S.summarize(*args)
        self.assertTrue(result['complete'])
        self.assertTrue(all(case['measurement_valid'] for case in result['cases'].values()))
        self.assertFalse(result['comparison_identity_valid'])
        self.assertEqual(result['comparison_identity_errors'], ['baseline_candidate_binaries_identical'])
        self.assertEqual(result['outcome'], 'measurement_or_test_failed')
        self.assertFalse(result['measurement_valid'])

    def test_missing_or_malformed_build_identity_cannot_be_a_valid_comparison(self):
        for identity in (None, {}, dict(configuration='debug', target='x86_64-pc-windows-msvc', binaries={
                'baseline-contain.exe': dict(sha256='a'*64, bytes=0),
                'candidate-contain.exe': dict(sha256='b'*64, bytes=1)})):
            args = inputs()
            args[0]['build_identity'] = identity
            self.assertFalse(S.summarize(*args)['measurement_valid'])

    def test_real_bounds_and_missing_cpu_are_preserved(self):
        raw, saved = profile()
        result = S.safe_profile(raw, saved)
        self.assertTrue(result['valid'], result)
        self.assertIsNone(result['maximum_backlog']['records'][0]['cpu_ns'])
        self.assertFalse(result['cpu_samples_available'])
        self.assertFalse(result['maximum_backlog']['pre_capacity_full'])

    def test_measured_schema_excerpt_and_actual_page_checkpoint_label_are_accepted(self):
        fixture = json.loads((Path(__file__).parent/'fixtures/receiver-backlog-measured-window.json').read_text())
        self.assertIs(fixture['real_etw'], False)
        row = fixture['record']
        actual_window = dict(trigger_ns=row['end_ns'], observed_at_ns=row['end_ns'], detection_lag_ns=0,
            queue_depth=row['after']['pending'], pre_records=0, post_records=0,
            pre_capacity_full=False, post_capacity_full=False, earlier_observations_omitted=0, later_observations_omitted=0,
            trigger_before_retained_context=False, trigger_contained_by_completed_span=True, records=[row])
        self.assertIsNone(S.safe_window(actual_window)[1])
        page_window = copy.deepcopy(actual_window)
        page_window['records'][0]['stage'] = 'receiver_follow_page_observation'
        clean, error = S.safe_window(page_window)
        self.assertIsNone(error)
        self.assertEqual(clean['records'][0]['stage'], 'receiver_follow_page_observation')

    def test_first_trigger_before_receiver_context_cannot_be_an_effective_causal_window(self):
        raw, saved = profile(4, True)
        first = raw['backlog_windows']['first_backlog']
        first['records'][0]['start_ns'] = 6
        first['trigger_before_retained_context'] = True
        first['trigger_contained_by_completed_span'] = False
        clean = S.safe_profile(raw, saved)
        self.assertFalse(clean['valid'])
        self.assertIn('first_trigger_stage_context_unavailable', clean['errors'])
        self.assertEqual(clean['first_backlog']['trigger_ns'], 5)
        self.assertEqual(clean['first_backlog']['records'][0]['start_ns'], 6)
        args = inputs(True)
        args[2]['r1/candidate'].update(measurement_valid=False, receiver_backlog=clean)
        self.assertEqual(S.summarize(*args)['outcome'], 'measurement_or_test_failed')

    def test_late_milestone_or_counter_disagreement_is_measurement_failure(self):
        for flaw in ('missing', 'wrong_capacity', 'record_count', 'time', 'overflow', 'coverage'):
            raw, saved = profile(4, True)
            value = raw['backlog_windows']
            if flaw == 'missing': value['first_overflow'] = None
            if flaw == 'wrong_capacity': value['pre_capacity'] = 65
            if flaw == 'record_count': value['maximum_backlog']['records'] *= 130
            if flaw == 'time': value['first_overflow']['detection_lag_ns'] = 99
            if flaw == 'overflow': saved['queue_overflow'] = 0
            if flaw == 'coverage': value['first_overflow']['trigger_before_retained_context'] = True
            self.assertFalse(S.safe_profile(raw, saved)['valid'], flaw)

    def test_arbitrary_labels_payloads_and_paths_are_not_exported(self):
        raw, saved = profile()
        raw['SECRET-user'] = 'C:/private/DB'
        raw['backlog_windows']['maximum_backlog']['records'][0]['follow']['SECRET-path'] = 'C:/private/DB'
        self.assertNotIn('SECRET', json.dumps(S.safe_profile(raw, saved)))
        raw['backlog_windows']['maximum_backlog']['records'][0]['stage'] = 'receiver_SECRET-user'
        encoded = json.dumps(S.safe_profile(raw, saved))
        self.assertNotIn('SECRET', encoded)
        self.assertIn('window_unknown_stage', encoded)

    def test_three_conclusions_and_strict_oracle_failure_retention(self):
        result = S.summarize(*inputs(False))
        self.assertEqual(result['outcome'], 'not_reproduced')
        result = S.summarize(*inputs(True))
        self.assertEqual(result['outcome'], 'reproduced_with_window')
        self.assertFalse(result['cases']['r1/candidate']['strict_oracle_accepted'])
        args = inputs(True)
        args[2]['r2/candidate']['measurement_valid'] = False
        result = S.summarize(*args)
        self.assertEqual(result['outcome'], 'measurement_or_test_failed')
        self.assertFalse(result['product_fix_claim'])
        self.assertFalse(result['release_accepted'])

    def test_reordered_retried_or_incomplete_plan_cannot_pass(self):
        for flaw in ('order', 'retry', 'notrun', 'cpu'):
            args = inputs()
            if flaw == 'order': args[0]['order'] = list(reversed(S.ORDER))
            if flaw == 'retry': args[0]['retries'] = 1
            if flaw == 'notrun': args[1]['not_run'] = ['r2/baseline']
            if flaw == 'cpu': args[0]['thread_cpu'] = False
            self.assertFalse(S.summarize(*args)['measurement_valid'])

    def test_loss_does_not_hide_valid_measurement_but_false_attribution_stops(self):
        raw, saved = profile(4, True)
        existing = dict(capture_exit_code=0, all_owned_processes_exited=True, workload_complete=True,
            accepted=False, pipeline=saved, background=dict(files=12000, falsely_attributed_events=0))
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            (directory/'private').mkdir()
            (directory/'private/receiver-profile.json').write_text(json.dumps(raw))
            with patch.object(S.PAIR, 'summarize', return_value=existing), patch.object(S, 'attribution_safety', return_value=dict(verified=True, target_false_promotions=0)):
                result = S.summarize_sample(directory)
                self.assertTrue(result['measurement_valid'], result)
                self.assertFalse(result['strict_oracle_accepted'])
            with patch.object(S.PAIR, 'summarize', return_value=existing), patch.object(S, 'attribution_safety', return_value=dict(verified=True, target_false_promotions=1)):
                self.assertEqual(S.summarize_sample(directory)['continuation_stop_reason'], 'attribution_safety_failed_or_unverified')


class ExistingBench(unittest.TestCase):
    def fixture(self, directory):
        gates = dict(invocations=[dict(mode='Bench', source_revision='a'*40, completed=True, passed=True, invocation_id='one')],
            gates=[dict(invocation_id='one', label='bench', exit_code=0)])
        row = dict(work_items_per_sample=20000, input_records_per_sample=20000, measured_samples=7, warmup_samples=2,
            validated_checksum=20000, median_ns_per_work_item=12.5, min_ns_per_work_item=11.0, p95_ns_per_work_item=14.0,
            median_work_items_per_second=80000000, sample_ns_per_work_item=[11,12,12,12.5,13,13.5,14])
        raw = dict(source_commit='a'*40, profile='release', measurement_kind='synthetic_in_memory',
            etw_providers_started=False, sqlite_measured=False, private_path='C:/SECRET-user/db',
            results=[dict(row, name=name) for name in S.BENCH_NAMES])
        (directory/'gates.json').write_text(json.dumps(gates))
        (directory/'event-pipeline.json').write_text(json.dumps(raw))
        return gates, raw

    def test_original_bench_gate_and_all_numeric_samples_are_retained_without_paths(self):
        with tempfile.TemporaryDirectory() as name:
            directory=Path(name);self.fixture(directory)
            result=S.summarize_benchmark(directory, 'a'*40)
            self.assertTrue(result['accepted'])
            self.assertEqual([len(row['sample_ns_per_work_item']) for row in result['results']], [7]*4)
            self.assertNotIn('SECRET', json.dumps(result))
            self.assertFalse(result['real_etw'])

    def test_failed_bench_changed_work_or_missing_sample_cannot_satisfy_preflight(self):
        with tempfile.TemporaryDirectory() as name:
            directory=Path(name)
            for flaw in ('gate', 'work', 'sample', 'source'):
                gates,raw=self.fixture(directory)
                if flaw=='gate': gates['invocations'][0]['passed']=False
                if flaw=='work': raw['results'][0]['work_items_per_sample']=19999
                if flaw=='sample': raw['results'][0]['sample_ns_per_work_item'].pop()
                if flaw=='source': raw['source_commit']='b'*40
                (directory/'gates.json').write_text(json.dumps(gates))
                (directory/'event-pipeline.json').write_text(json.dumps(raw))
                with self.assertRaises(ValueError): S.summarize_benchmark(directory, 'a'*40)


if __name__ == '__main__':
    unittest.main()
