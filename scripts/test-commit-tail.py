import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("commit_tail", Path(__file__).with_name("analyze-commit-tail.py"))
diagnostic = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostic)


def snapshot(overflow, pending=0, enqueued=0, dequeued=0):
    return dict(overflow=overflow, pending=pending, enqueued=enqueued, dequeued=dequeued)


def span(stage, start, end, before, after):
    return dict(stage=stage, start_ns=start, end_ns=end, before=snapshot(before), after=snapshot(after))


class CommitTailTests(unittest.TestCase):
    def test_legacy_prefix_is_not_extrapolated_to_total_and_output_is_bounded(self):
        profile = dict(batches=[[300, 256, 1000, 200], [600, 1, 8, 100]], omitted_batches=2)
        metadata = dict(backend=dict(pipeline=dict(queue_overflow=1000, overflow_first_ns=[150, 200, 550], overflow_last_ns=900)))
        result = diagnostic.analyze(profile, metadata, max_windows=1)
        self.assertEqual(result["overflow"]["unrecorded_timestamps"], 997)
        self.assertEqual(result["legacy_batches"]["recorded_overflows_in_flush_windows"], 3)
        self.assertEqual(result["legacy_batches"]["retained_interval_union_ns"], 300)
        self.assertEqual(len(result["legacy_batches"]["displayed_windows"]), 1)
        self.assertEqual(result["legacy_batches"]["undisplayed_windows"], 1)
        self.assertEqual(result["legacy_batches"]["omitted_batches"], 2)
        self.assertEqual(result["queue_spans"][diagnostic.STAGES[0]]["distinct_overflow_increments"], 0)

    def test_nested_commit_and_flush_are_not_added_and_pending_is_not_balance(self):
        commit = span(diagnostic.STAGES[0], 120, 180, 2, 8)
        commit["before"] = snapshot(2, pending=9000, enqueued=10, dequeued=10)
        commit["after"] = snapshot(8, pending=1, enqueued=12, dequeued=11)
        profile = dict(queue_spans=[span(diagnostic.STAGES[1], 100, 200, 1, 9), commit], omitted_queue_spans=5)
        result = diagnostic.analyze(profile, dict(queue_overflow=10))
        self.assertEqual(result["queue_spans"][diagnostic.STAGES[0]]["distinct_overflow_increments"], 6)
        self.assertEqual(result["queue_spans"][diagnostic.STAGES[1]]["distinct_overflow_increments"], 8)
        self.assertEqual(result["queue_spans"][diagnostic.STAGES[0]]["fraction_of_total_overflow"], 0.6)
        self.assertEqual(result["omitted_queue_spans"], 5)
        self.assertEqual(result["queue_spans"][diagnostic.STAGES[0]]["displayed_windows"][0]["pending_before"], 9000)

    def test_overlapping_spans_count_each_increment_once_within_stage(self):
        stage = diagnostic.STAGES[0]
        profile = dict(queue_spans=[span(stage, 10, 30, 1, 5), span(stage, 20, 40, 3, 7)])
        summary = diagnostic.analyze(profile, dict(queue_overflow=8))["queue_spans"][stage]
        self.assertEqual(summary["overflow_delta_sum"], 8)
        self.assertEqual(summary["distinct_overflow_increments"], 6)
        self.assertEqual(summary["overlapping_counter_increments"], 2)
        self.assertEqual(summary["retained_interval_union_ns"], 30)

    def test_missing_total_is_unknown_and_zero_loss_is_not_divided(self):
        result = diagnostic.analyze({}, dict(pipeline=dict(overflow_first_ns=[])))
        self.assertIsNone(result["overflow"]["total"])
        self.assertIsNone(result["overflow"]["unrecorded_timestamps"])
        result = diagnostic.analyze({}, dict(pipeline=dict(queue_overflow=0, overflow_last_ns=0)))
        self.assertIsNone(result["queue_spans"][diagnostic.STAGES[0]]["fraction_of_total_overflow"])
        self.assertIsNone(result["overflow"]["last_overflow_ns"])

    def test_windows_are_half_open_and_duplicate_overlapping_windows_do_not_double_count(self):
        profile = dict(batches=[[100, 1, 1, 100], [200, 1, 1, 100], [150, 1, 1, 100]])
        result = diagnostic.analyze(profile, dict(queue_overflow=3, overflow_first_ns=[0, 100, 200]))
        self.assertEqual(result["legacy_batches"]["recorded_overflows_in_flush_windows"], 2)
        self.assertEqual(result["legacy_batches"]["recorded_overflows_outside_flush_windows"], 1)
        self.assertEqual(result["legacy_batches"]["retained_interval_union_ns"], 200)

    def test_counter_regression_and_mismatched_totals_are_rejected(self):
        good = span(diagnostic.STAGES[0], 1, 2, 0, 1)
        for changes in (dict(start_ns=3), dict(before=snapshot(2)), dict(after=snapshot(11))):
            bad = copy.deepcopy(good)
            bad.update(changes)
            with self.assertRaises(ValueError):
                diagnostic.analyze(dict(queue_spans=[bad]), dict(queue_overflow=10))
        with self.assertRaises(ValueError):
            diagnostic.analyze({}, dict(queue_overflow=0, overflow_first_ns=[100]))
        with self.assertRaises(ValueError):
            diagnostic.analyze(dict(batches=[[10, 1, 1, 11]]), dict(queue_overflow=0))

    def test_metadata_wrappers_and_cli_avoid_copying_capture_data(self):
        for wrapper in ("measurement", "metadata", "result"):
            wrapped = {wrapper: dict(backend=dict(pipeline=dict(queue_overflow=0)), events=[{"resource": "private-marker"}])}
            self.assertNotIn("private-marker", json.dumps(diagnostic.analyze({}, wrapped)))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            root.joinpath("profile.json").write_text('{"batches":[]}', encoding="utf-8-sig")
            root.joinpath("result.json").write_text('{"backend":{"pipeline":{"queue_overflow":0}},"resource":"private-marker"}', encoding="utf-8")
            run = subprocess.run([sys.executable, str(Path(diagnostic.__file__)), str(root / "profile.json")], capture_output=True, text=True)
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertTrue(json.loads(run.stdout)["diagnostic_only"])
            self.assertNotIn("private-marker", run.stdout)


if __name__ == "__main__":
    unittest.main()
