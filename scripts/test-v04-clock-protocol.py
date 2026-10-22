import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("clock_protocol", Path(__file__).with_name("v04-clock-protocol.py"))
CLOCK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CLOCK)


class ClockProtocolTests(unittest.TestCase):
    def setUp(self):
        sample = lambda lo, wall, hi: dict(qpc_before=str(lo), filetime_ticks=str(wall), qpc_after=str(hi))
        self.truth = dict(pid=7, creation_time="1000", operation="write_requested", resource="C:\\fixture\\x",
                          role="worker", success=True, start_ticks="100000", end_ticks="100200",
                          clock=dict(schema_version=1, qpc_frequency="10000000", sequence=1, thread_id=9,
                                     start_qpc="100", end_qpc="200", before=sample(90, 100095, 99),
                                     after=sample(201, 100205, 210)))
        self.event = dict(id="source", event_type="file", operation="write_requested", resource=self.truth["resource"],
                          confidence="High", timestamp_ticks="100150", evidence=dict(pid=7, process_creation_time="1000"),
                          raw=dict(clock=dict(raw_qpc="150", header_timestamp="150", timestamp_domain="qpc")))
        self.capture = dict(events=[self.event], backend=dict(clock=dict(schema_version=1, client_context=1,
            clock_type="qpc", process_trace_mode=0x10001100, delivered_timestamp_domain="qpc",
            raw_qpc_available=True, trace_header_available=True, qpc_frequency="10000000", errors=[])))
        self.capture["backend"]["clock"]["calibrations"] = [dict(sample(tick, 100000 + tick, tick + 1), phase=phase)
            for tick, phase in ((1, "trace_start_requested"), (10, "consumer_ready"),
                                (400, "trace_stop_requested"), (450, "consumer_stopped"))]

    def analyze(self, truth=None):
        return CLOCK.analyze(self.capture, [self.truth] if truth is None else truth)

    def test_legacy_capture_and_truth_are_unavailable_never_rescored(self):
        self.capture["backend"] = {}
        self.truth.pop("clock")
        value = self.analyze()
        self.assertEqual(value["status"], "unavailable")
        self.assertIn("trace_clock_metadata_missing", value["source_reasons"])
        self.assertIsNone(value["release_gate"])
        self.assertFalse(value["attribution_scored"])
        self.assertFalse(value["legacy_score_recomputed"])

    def test_converted_filetime_cannot_be_relabelled_as_original_qpc(self):
        metadata = self.capture["backend"]["clock"]
        metadata.update(process_trace_mode=0x10000100, delivered_timestamp_domain="filetime_100ns",
                        raw_qpc_available=False, trace_header_available=False)
        value = self.analyze()
        self.assertEqual(value["status"], "unavailable")
        self.assertEqual(value["truth_rows"][0]["event_ids"], [])
        # A non-null raw_qpc payload cannot override the actual consumer mode.
        self.assertIn("consumer_did_not_request_raw_timestamp", value["source_reasons"])

    def test_strict_qpc_boundary_and_one_tick_outside_get_no_tolerance(self):
        for tick, status in ((100, "unique_interval_candidate"), (200, "unique_interval_candidate"),
                             (99, "no_in_interval_source"), (201, "no_in_interval_source")):
            with self.subTest(tick=tick):
                self.event["raw"]["clock"].update(raw_qpc=str(tick), header_timestamp=str(tick))
                result = self.analyze()
                self.assertEqual(result["truth_rows"][0]["status"], status)
                self.assertIsNone(result["release_gate"])

    def test_wall_clock_adjustment_is_diagnostic_and_never_widens_qpc(self):
        self.truth["clock"]["after"]["filetime_ticks"] = "90000"
        self.event["raw"]["clock"].update(raw_qpc="201", header_timestamp="201")
        row = self.analyze()["truth_rows"][0]
        self.assertEqual(row["status"], "no_in_interval_source")
        self.assertFalse(row["calibration"]["sampled_constant_offset_compatible"])
        self.assertFalse(row["calibration"]["constant_offset_proven"])
        self.assertLess(int(row["calibration"]["wall_delta_100ns"]), 0)

    def test_overlapping_same_identity_operations_remain_ambiguous(self):
        other = copy.deepcopy(self.truth)
        other["clock"]["sequence"] = 2
        value = self.analyze([self.truth, other])
        self.assertEqual(value["counts"], {"ambiguous_interval_candidates": 2})

    def test_duplicate_or_regressed_or_unbracketed_truth_is_invalid(self):
        self.assertEqual(self.analyze([self.truth, copy.deepcopy(self.truth)])["status"], "invalid")
        for updates in ({"start_qpc": "201"}, {"end_qpc": "202"}, {"qpc_frequency": "0"},
                        {"thread_id": 0}, {"start_qpc": True}):
            with self.subTest(updates=updates):
                row = copy.deepcopy(self.truth)
                row["clock"].update(updates)
                self.assertEqual(self.analyze([row])["status"], "invalid")

    def test_frequency_mismatch_and_missing_actor_are_unavailable(self):
        self.truth["clock"]["qpc_frequency"] = "1000000"
        self.assertEqual(self.analyze()["truth_rows"][0]["reason"], "trace_fixture_frequency_mismatch")
        self.truth["clock"]["qpc_frequency"] = "10000000"
        self.event["evidence"].pop("process_creation_time")
        self.assertEqual(self.analyze()["status"], "unavailable")

    def test_raw_header_mismatch_rejects_fabricated_conversion(self):
        self.event["raw"]["clock"]["header_timestamp"] = "100150"
        self.assertEqual(self.analyze()["status"], "unavailable")

    def test_missing_phase_and_wrong_trace_window_do_not_validate(self):
        metadata = self.capture["backend"]["clock"]
        metadata["calibrations"][2].update(qpc_before="205", qpc_after="206")
        self.assertEqual(self.analyze()["truth_rows"][0]["reason"], "fixture_outside_recorded_trace_qpc_window")
        metadata["calibrations"].pop()
        value = self.analyze()
        self.assertEqual(value["status"], "unavailable")
        self.assertIn("trace_calibration_unavailable_or_invalid", value["source_reasons"])

    def test_exact_calibration_arithmetic_preserves_large_ticks(self):
        tick = 134_350_000_000_000_017
        before = dict(qpc_before=str(tick), qpc_after=str(tick + 1), filetime_ticks=str(tick + 2))
        value = CLOCK.calibration_diagnostic(before, before, 3)
        expected = CLOCK.Fraction(tick + 2) - CLOCK.Fraction((tick + 1) * 10_000_000, 3)
        self.assertEqual(value["offset_envelopes_100ns"][0][0],
                         dict(numerator=str(expected.numerator), denominator=str(expected.denominator)))
        self.assertFalse(value["constant_offset_proven"])

    def test_zero_process_identity_never_matches(self):
        self.truth["creation_time"] = "0"
        self.event["evidence"]["process_creation_time"] = "0"
        result = self.analyze()
        self.assertEqual(result["status"], "invalid")
        self.assertEqual(result["truth_rows"][0]["event_ids"], [])
        self.assertIn("source_original_qpc_or_actor_missing", result["source_reasons"])

    def test_boolean_metadata_and_invalid_mode_do_not_declare_qpc(self):
        for changes in ({"schema_version": True}, {"client_context": True},
                        {"process_trace_mode": -1}, {"process_trace_mode": 0x100001100},
                        {"process_trace_mode": CLOCK.RAW_TIMESTAMP}):
            with self.subTest(changes=changes):
                capture = copy.deepcopy(self.capture)
                capture["backend"]["clock"].update(changes)
                result = CLOCK.analyze(capture, [self.truth])
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_cli_refuses_to_overwrite_existing_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            capture = directory / "capture.json"
            truth = directory / "truth.json"
            capture.write_text(json.dumps(self.capture))
            truth.write_text(json.dumps([self.truth]))
            original = capture.read_bytes()
            process = subprocess.run([sys.executable, str(Path(CLOCK.__file__)), "--capture", str(capture),
                                      "--truth", str(truth), "--output", str(capture)], capture_output=True)
            self.assertNotEqual(process.returncode, 0)
            self.assertEqual(capture.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
