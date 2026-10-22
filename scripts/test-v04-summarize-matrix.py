import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("summary", Path(__file__).with_name("v04-summarize-matrix.py"))
SUMMARY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SUMMARY)


class MatrixSummaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "public.json"
        base = dict(mode="candidate", files=1000, workers=4, noise_files=100, seed_index=0, trial=1,
                    status="completed", acceptance_pass=True, scored_rows=[{"resource": "<FIXTURE>/private"}], truth_rows=[{}])
        self.document = dict(planned_attempts=3, completed_attempts=2, acceptance_pass=False, environment=dict(snapshot_only=True), provenance={}, trials=[
            dict(base, attempt=1, elapsed_seconds=2, parent_io=dict(write_bytes=100)),
            dict(base, attempt=2, elapsed_seconds=8, acceptance_pass=False, parent_io=dict(write_bytes=500)),
            dict(base, attempt=3, status="failed", acceptance_pass=False)])

    def summarize(self):
        self.path.write_text(json.dumps(self.document))
        return SUMMARY.summarize([("core", self.path)], "a" * 40, "b" * 40)

    def test_failures_remain_and_missing_metrics_are_not_zero_or_success(self):
        report = self.summarize()
        self.assertEqual(report["recorded_attempts"], 3)
        self.assertEqual(report["completed_attempts"], 2)
        self.assertEqual(report["fallback_passes"], 1)
        self.assertEqual(report["groups"][0]["medians"]["elapsed_seconds"], 5)
        self.assertEqual(report["groups"][0]["measured_samples"]["elapsed_seconds"], 2)
        self.assertIsNone(report["groups"][0]["medians"]["sqlite_bytes"])
        self.assertEqual(report["groups"][0]["parent_io_medians"]["write_bytes"], 300)
        self.assertEqual(report["groups"][0]["parent_io_measured_samples"]["write_bytes"], 2)
        self.assertIsNone(report["etw_release_gate"])
        self.assertNotIn("private", json.dumps(report["trials"]))
        self.assertEqual([row["scored_row_count"] for row in report["trials"]], [1, 1, 1])

    def test_missing_or_duplicate_planned_attempts_are_rejected(self):
        self.document["trials"].pop()
        with self.assertRaises(ValueError):
            self.summarize()
        self.document["planned_attempts"] = 2
        self.document["trials"][1]["attempt"] = 1
        with self.assertRaises(ValueError):
            self.summarize()

    def test_etw_or_unmeasured_capture_mode_cannot_be_relabelled_as_fallback(self):
        for value in (False, None, 1, "true"):
            with self.subTest(snapshot_only=value):
                self.document["environment"]["snapshot_only"] = value
                with self.assertRaises(ValueError):
                    self.summarize()
        self.document["environment"] = {}
        with self.assertRaises(ValueError):
            self.summarize()


if __name__ == "__main__":
    unittest.main()
