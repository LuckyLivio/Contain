import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('profile_summary', Path(__file__).with_name('summarize-receiver-profile.py'))
M = importlib.util.module_from_spec(spec); spec.loader.exec_module(M)

class ProfileTests(unittest.TestCase):
    def test_exclusive_budget_not_nested_legacy_timers(self):
        row = dict(count=1, elapsed_ns=100, max_elapsed_ns=100, units=256, enqueued=8, overflow=4, dequeued=0,
                   max_pending_before=4096, max_pending_after=4096, cpu_count=1, cpu_ns=20)
        profile = dict(queue_stage_totals={'receiver_lock_wait': row, 'receiver_follow_pending_scan': dict(row, cpu_ns=80),
                        'nested_private_path': dict(row, overflow=999)}, stages={'parent': {'total_ns': 99999}}, omitted_queue_spans=10000)
        result = M.summarize(profile, {'backend': {'pipeline': {'queue_overflow': 9}}})
        self.assertEqual(result['assigned_exclusive_overflow'], 8)
        self.assertEqual(result['unassigned_overflow'], 1)
        self.assertEqual(result['exclusive_receiver_stages']['receiver_follow_pending_scan']['cpu_share_percent'], 80)
        self.assertNotIn('private', str(result))
        self.assertEqual(result['omitted_queue_spans'], 10000)
    def test_missing_cpu_and_fields_are_not_measured_zero(self):
        result = M.summarize({'queue_stage_totals': {'receiver_lock_wait': {'count': 1, 'cpu_count': 0, 'cpu_ns': 0}}}, {})
        self.assertIsNone(result['exclusive_receiver_stages']['receiver_lock_wait']['cpu_ns'])
        self.assertIsNone(result['source_queue_overflow'])
        self.assertIsNone(result['unassigned_overflow'])
    def test_inconsistent_counts_are_not_clamped_to_green(self):
        result = M.summarize({'queue_stage_totals': {'receiver_lock_wait': {'overflow': 5}}}, {'backend': {'pipeline': {'queue_overflow': 4}}})
        self.assertTrue(result['overlapping_or_inconsistent_overflow'])
        self.assertIsNone(result['unassigned_overflow'])
    def test_payloads_bad_types_and_paths_never_export(self):
        result = M.summarize({'queue_stage_totals': {'receiver_lock_wait': {'count': True, 'elapsed_ns': -1, 'path': 'SECRET'}},
                              'counters': {'follow_scan_calls': 'SECRET', 'private': 'SECRET'}}, {})
        self.assertNotIn('SECRET', str(result)); self.assertIsNone(result['counters']['follow_scan_calls'])
        self.assertIsNone(result['exclusive_receiver_stages']['receiver_lock_wait']['count'])

if __name__ == '__main__': unittest.main()
