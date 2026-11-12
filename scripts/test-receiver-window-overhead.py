import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("receiver_window_overhead", Path(__file__).with_name("receiver-window-overhead.py"))
OVERHEAD = importlib.util.module_from_spec(spec)
spec.loader.exec_module(OVERHEAD)


class OverheadTests(unittest.TestCase):
    @staticmethod
    def samples():
        return [{"number": index + 1, "mode": mode, "warmup": False,
                 "result": {"spans": 256, "elapsed_ns": 256 * {"off": 100, "existing": 120, "window": 125}[mode],
                            "output_sha256": "a" * 64, "snapshot_input": {"enqueued": 4096, "overflow": 1, "dequeued": 4096, "pending": 0}}}
                for index, mode in enumerate(OVERHEAD.SAMPLE_ORDER)]

    def test_all_alternating_samples_are_retained_and_increments_have_named_denominators(self):
        rows = self.samples()
        before = repr(rows)
        summary = OVERHEAD.summarize(rows, 256)
        self.assertEqual(summary["median_ns_per_span"], {"off": 100, "existing": 120, "window": 125})
        self.assertEqual(summary["existing_profile_increment_ns_per_span"], 20)
        self.assertEqual(summary["window_increment_ns_per_span"], 5)
        self.assertTrue(summary["fixed_work_and_snapshot_inputs_equal"])
        self.assertEqual(repr(rows), before)

    def test_changed_work_or_snapshot_inputs_are_not_accepted_as_overhead(self):
        for field, value in (("output_sha256", "b" * 64), ("snapshot_input", {"pending": 1}), ("spans", 512)):
            rows = self.samples()
            rows[4]["result"][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                OVERHEAD.summarize(rows, 256)

    def test_missing_sample_is_not_replaced_or_ignored(self):
        rows = self.samples()
        rows.pop(4)
        with self.assertRaises(ValueError):
            OVERHEAD.summarize(rows, 256)

    def test_warmups_are_retained_and_checked_but_not_in_median(self):
        rows = self.samples()
        warmup = {**rows[0], "warmup": True, "result": {**rows[0]["result"], "elapsed_ns": 999999999}}
        summary = OVERHEAD.summarize([warmup] + rows, 256)
        self.assertEqual(summary["median_ns_per_span"]["off"], 100)
        warmup["result"]["output_sha256"] = "c" * 64
        with self.assertRaises(ValueError):
            OVERHEAD.summarize([warmup] + rows, 256)

    def test_window_configuration_and_context_must_match_declared_bounds(self):
        windows = {"receiver_observations": 9000, "backlog_threshold_records": 1024,
                   "pre_capacity": 64, "post_capacity": 64, "maximum_records_per_window": 129}
        for name in ("first_backlog", "first_overflow", "maximum_backlog"):
            windows[name] = {"records": [{}] * 129, "pre_records": 64, "post_records": 64, "post_capacity_full": True}
        self.assertEqual(OVERHEAD.validate_profile({"backlog_windows": windows}, "window", 9000),
                         {name: 129 for name in ("first_backlog", "first_overflow", "maximum_backlog")})
        self.assertIsNone(OVERHEAD.validate_profile({}, "existing", 9000))
        with self.assertRaises(ValueError):
            OVERHEAD.validate_profile({"backlog_windows": windows}, "existing", 9000)
        windows["first_overflow"]["records"].append({})
        with self.assertRaises(ValueError):
            OVERHEAD.validate_profile({"backlog_windows": windows}, "window", 9000)


if __name__ == "__main__":
    unittest.main()
