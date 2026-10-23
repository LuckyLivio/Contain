import copy
import hashlib
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

    def companion(self):
        descriptor = dict(provider="11111111-1111-1111-1111-111111111111", event_id=16,
                          version=0, channel=0, level=0, opcode=0, task=0,
                          keyword="0000000000000010", process_id=7, thread_id=9)
        self.event["raw"]["clock"].update(timestamp_domain="filetime_100ns", raw_qpc=None,
                                         header_timestamp="100150", event_key="0000000000000001",
                                         record_identity=descriptor)
        header = dict(process_trace_mode=0x10001100, reserved_flags=1, perf_frequency="10000000",
                      start_time="1000", boot_time="900", end_time="0", events_lost=0, buffers_lost=0)
        self.capture["backend"]["clock"].update(process_trace_mode=0x10000100, delivered_timestamp_domain="filetime_100ns",
                                                raw_qpc_available=False, trace_header=dict(header, process_trace_mode=0x10000100))
        return dict(schema_version=1, protocol=CLOCK.COMPANION_PROTOCOL, session_name="fixture-session",
                    header=header, ready=True, stopped=True, producer_stopped=True,
                    header_sample_phase="open_before_process_trace", consumer_loss_counters_available=False, errors=[],
                    stats=dict(received=1, stored=1, missing_event_key=0, invalid_timestamp=0, capacity_dropped=0,
                               duplicate_event_keys=0, record_capacity=4, byte_capacity=4096, bytes=80,
                               fixed_record_size=80, allocated_record_bytes=320, disk_byte_capacity=16384,
                               filtered_nonprimary=0, readiness_probe_records=1, serialization_buffer_bytes=128),
                    records=[dict(descriptor, sequence=1, event_key="0000000000000001", header_timestamp="150")])

    def companion_bytes(self, document):
        raw = json.dumps(document).encode()
        self.capture["backend"]["clock"]["raw_companion"] = dict(
            {name: copy.deepcopy(document[name]) for name in ("protocol", "session_name", "header", "stats", "ready", "stopped", "errors",
                                                             "producer_stopped", "header_sample_phase", "consumer_loss_counters_available")},
            written=True, file_bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest())
        return raw

    def test_exact_companion_key_links_original_qpc_without_mutating_primary(self):
        raw = self.companion_bytes(self.companion())
        before = copy.deepcopy(self.capture)
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw)
        self.assertEqual(result["status"], "diagnostic_ready")
        self.assertEqual(result["truth_rows"][0]["status"], "unique_interval_candidate")
        self.assertTrue(result["raw_companion"]["sha256_verified"])
        self.assertEqual(result["raw_companion"]["primary_events_without_link"], 0)
        self.assertEqual(result["raw_companion"]["event_links"][0]["original_qpc"], "150")
        self.assertEqual(self.capture, before)
        self.assertIsNone(result["release_gate"])
        self.assertFalse(result["same_machine_boot_provenance_verified"])

    def test_missing_primary_key_keeps_unlinked_count_unknown_after_partial_audit(self):
        raw = self.companion_bytes(self.companion())
        other = copy.deepcopy(self.event)
        other["id"] = "missing-primary-key"
        other["raw"]["clock"].pop("event_key")
        self.capture["events"].append(other)
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw)
        self.assertEqual(result["status"], "unavailable")
        self.assertEqual(result["raw_companion"]["status"], "unavailable")
        self.assertIsNone(result["raw_companion"]["primary_events_without_link"])
        self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_zero_realtime_start_time_retains_exact_qpc_without_inventing_an_origin(self):
        document = self.companion()
        document["header"]["start_time"] = "0"
        self.capture["backend"]["clock"]["trace_header"]["start_time"] = "0"
        raw = self.companion_bytes(document)
        original = copy.deepcopy(self.capture)
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw)
        self.assertEqual(result["status"], "diagnostic_ready")
        self.assertEqual(result["protocol_revision"], "contain-clock-observability-2")
        self.assertEqual(result["raw_companion"]["start_time_observation"], dict(
            primary="0", companion="0", status="unavailable_at_realtime_open",
            available=False, used_as_clock_origin=False))
        self.assertEqual(result["truth_rows"][0]["event_ids"], ["source"])
        self.assertEqual(result["raw_companion"]["event_links"][0]["original_qpc"], "150")
        self.assertEqual(result["tolerance_qpc"], 0)
        self.assertIsNone(result["release_gate"])
        self.assertFalse(result["same_machine_boot_provenance_verified"])
        self.assertEqual(self.capture, original)

    def test_realtime_start_time_must_be_exact_nonnegative_decimal_i64(self):
        for invalid in (True, 0, "", "-1", "00", " 0", "0.0", "\u0660", str(1 << 63)):
            for side in ("primary", "companion"):
                with self.subTest(invalid=invalid, side=side):
                    document = self.companion()
                    target = (document["header"] if side == "companion" else
                              self.capture["backend"]["clock"]["trace_header"])
                    target["start_time"] = invalid
                    result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
                    self.assertEqual(result["status"], "unavailable")
                    self.assertIn("raw_companion_header_time_malformed", result["raw_companion"]["reasons"])
        document = self.companion()
        document["header"]["start_time"] = "0"
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
        self.assertIn("raw_companion_primary_header_mismatch", result["raw_companion"]["reasons"])

    def test_zero_start_time_requires_realtime_modes_and_observed_open_phase(self):
        for field, value in (("header_sample_phase", "after_process_trace"),
                             ("header_sample_phase", None), ("consumer_loss_counters_available", True),
                             ("process_trace_mode", 0x10001000), ("primary_mode", 0x10000000)):
            with self.subTest(field=field, value=value):
                document = self.companion()
                document["header"]["start_time"] = "0"
                primary = self.capture["backend"]["clock"]["trace_header"]
                primary["start_time"] = "0"
                if field == "process_trace_mode":
                    document["header"][field] = value
                elif field == "primary_mode":
                    primary["process_trace_mode"] = value
                    self.capture["backend"]["clock"]["process_trace_mode"] = value
                else:
                    document[field] = value
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_zero_start_time_never_hides_capacity_loss_or_missing_calibration(self):
        for failure in ("capacity", "calibration"):
            with self.subTest(failure=failure):
                document = self.companion()
                document["header"]["start_time"] = "0"
                self.capture["backend"]["clock"]["trace_header"]["start_time"] = "0"
                if failure == "capacity":
                    document["stats"]["capacity_dropped"] = 1
                else:
                    self.capture["backend"]["clock"]["calibrations"] = []
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])
                self.assertIsNone(result["release_gate"])
                if failure == "capacity":
                    self.assertIn("raw_companion_loss_or_ambiguity", result["raw_companion"]["reasons"])
                else:
                    self.assertIn("trace_calibration_unavailable_or_invalid", result["source_reasons"])

    def test_invalid_companion_keeps_unlinked_count_unknown(self):
        raw = self.companion_bytes(self.companion())
        for invalid in (b"{", raw + b" "):
            with self.subTest(invalid=invalid[:1]):
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=invalid)
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["raw_companion"]["status"], "unavailable")
                self.assertIsNone(result["raw_companion"]["primary_events_without_link"])
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_companion_metadata_without_qualifying_source_links_is_unavailable(self):
        for keep_unmatched_raw_record in (False, True):
            with self.subTest(keep_unmatched_raw_record=keep_unmatched_raw_record):
                document = self.companion()
                self.capture["events"] = []
                if not keep_unmatched_raw_record:
                    document["records"] = []
                    document["stats"].update(received=0, stored=0, bytes=0)
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["raw_companion"]["status"], "unavailable")
                self.assertIn("no_original_source_clock_links", result["raw_companion"]["reasons"])
                self.assertEqual(result["raw_companion"]["linked_events"], 0)
                self.assertIsNone(result["raw_companion"]["primary_events_without_link"])
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])
                self.assertIsNone(result["release_gate"])

    def test_companion_duplicate_keys_and_sequences_cannot_be_hidden_by_counters(self):
        document = self.companion()
        document["records"].append(dict(document["records"][0], sequence=2))
        document["stats"].update(received=2, stored=2, bytes=160)
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
        self.assertEqual(result["status"], "unavailable")
        self.assertIn("raw_companion_duplicate_key_sequence_or_invalid_qpc", result["raw_companion"]["reasons"])
        document["records"][1].update(event_key="0000000000000002", sequence=1)
        self.assertEqual(CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))["status"], "unavailable")

    def test_companion_descriptor_must_match_every_field(self):
        for field in ("provider", "event_id", "version", "channel", "level", "opcode", "task", "keyword", "process_id", "thread_id"):
            with self.subTest(field=field):
                document = self.companion()
                row = document["records"][0]
                row[field] = ("22222222-2222-2222-2222-222222222222" if field == "provider" else
                              "0000000000000020" if field == "keyword" else row[field] + 1)
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_companion_hash_size_session_and_summary_must_match_primary_record(self):
        for change in ({"sha256": "0" * 64}, {"file_bytes": 1}, {"session_name": "other"},
                       {"written": False}, {"ready": False}):
            with self.subTest(change=change):
                raw = self.companion_bytes(self.companion())
                self.capture["backend"]["clock"]["raw_companion"].update(change)
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw)
                self.assertEqual(result["status"], "unavailable")

    def test_companion_loss_missing_readiness_and_overbudget_remain_unavailable(self):
        for field, value in (("capacity_dropped", 1), ("missing_event_key", 1), ("duplicate_event_keys", 1),
                             ("invalid_timestamp", 1), ("stored", 2), ("bytes", 81), ("record_capacity", 0),
                             ("allocated_record_bytes", 79), ("byte_capacity", 319), ("disk_byte_capacity", 1),
                             ("received", True), ("filtered_nonprimary", -1), ("readiness_probe_records", 0),
                             ("serialization_buffer_bytes", 4000)):
            with self.subTest(field=field):
                document = self.companion()
                document["stats"][field] = value
                self.assertEqual(CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))["status"], "unavailable")
        for field in ("ready", "stopped", "producer_stopped"):
            document = self.companion()
            document[field] = False
            self.assertEqual(CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))["status"], "unavailable")

    def test_companion_rejects_invalid_clock_header_and_raw_tick(self):
        for field, value in (("process_trace_mode", 0x10000100), ("reserved_flags", True),
                             ("perf_frequency", "0"), ("boot_time", True), ("events_lost", 1)):
            document = self.companion()
            document["header"][field] = value
            self.assertEqual(CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))["status"], "unavailable")
        for tick in ("0", "-1", True, str(1 << 63)):
            document = self.companion()
            document["records"][0]["header_timestamp"] = tick
            self.assertEqual(CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))["status"], "unavailable")
        document = self.companion()
        self.capture["backend"]["clock"]["trace_header"]["process_trace_mode"] = 0x10001100
        self.assertEqual(CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))["status"], "unavailable")

    def test_companion_metadata_filter_gaps_and_unpopulated_boot_time_are_not_invented_loss(self):
        document = self.companion()
        document["stats"].update(filtered_nonprimary=2, received=3)
        document["records"][0]["sequence"] = 3
        document["header"]["boot_time"] = "0"
        self.capture["backend"]["clock"]["trace_header"]["boot_time"] = "0"
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
        self.assertEqual(result["status"], "diagnostic_ready")
        self.assertFalse(result["raw_companion"]["consumer_loss_counters_available"])
        self.assertFalse(result["same_machine_boot_provenance_verified"])
        self.assertIsNone(result["release_gate"])
        document["header"]["boot_time"] = "123"
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
        self.assertEqual(result["status"], "diagnostic_ready")
        self.assertFalse(result["raw_companion"]["unsupported_boot_time_fields"]["equal"])
        self.assertFalse(result["raw_companion"]["unsupported_boot_time_fields"]["proves_same_boot"])

    def test_companion_never_falls_back_to_timestamp_or_payload_matching(self):
        document = self.companion()
        document["records"][0]["event_key"] = "0000000000000002"
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
        self.assertEqual(result["status"], "unavailable")
        self.assertIn("primary_event_key_missing_from_companion", result["raw_companion"]["reasons"])
        self.assertIsNone(result["raw_companion"]["primary_events_without_link"])
        document = self.companion()
        other = copy.deepcopy(self.event)
        other["id"] = "duplicate-primary-key"
        self.capture["events"].append(other)
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
        self.assertEqual(result["status"], "unavailable")
        self.assertIn("primary_event_key_ambiguous", result["raw_companion"]["reasons"])

    def test_control_markers_are_diagnostic_only_and_never_added_to_truth(self):
        marker = copy.deepcopy(self.truth)
        marker.update(schema_version=1, kind="fixture_control_marker", marker="initial_registry_open")
        result = CLOCK.marker_diagnostics(self.capture, [marker])
        self.assertEqual(result["rows"][0]["relation"], "within_ready_trace_window")
        self.assertFalse(result["attribution_scored"])
        self.assertFalse(result["same_machine_boot_provenance_verified"])
        # Move all capture boundaries beyond the already-measured open.
        for sample in self.capture["backend"]["clock"]["calibrations"]:
            for name in ("qpc_before", "qpc_after"):
                sample[name] = str(int(sample[name]) + 500)
        result = CLOCK.marker_diagnostics(self.capture, [marker])
        self.assertEqual(result["rows"][0]["relation"], "before_trace_start_request")
        marker["success"] = False
        marker["error"] = 5
        self.assertEqual(CLOCK.marker_diagnostics(self.capture, [marker])["rows"][0]["error"], 5)
        self.assertEqual(CLOCK.marker_diagnostics(self.capture, [marker, marker])["status"], "invalid")

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
