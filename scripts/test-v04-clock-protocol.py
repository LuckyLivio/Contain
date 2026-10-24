import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import uuid


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

    def native_provenance(self):
        sample = dict(schema_version=1, status="observed",
            machine_source="GetComputerNameExW_PhysicalDnsFullyQualified_lower_utf16le_sha256",
            machine_name_sha256="1" * 64,
            boot_source="NtQuerySystemInformation_SystemBootEnvironmentInformation_90_32",
            boot_identifier_le_hex="2" * 32, nt_status="00000000", returned_bytes=32,
            errors=[])
        self.capture["backend"]["clock"]["provenance"] = [
            dict(sample, phase="trace_start_requested", qpc_before="0", qpc_after="1"),
            dict(sample, phase="consumer_stopped", qpc_before="430", qpc_after="440")]
        self.truth["clock"]["provenance"] = dict(sample, phase="fixture_clock_initialized",
                                                  qpc_before="20", qpc_after="21")

    def test_missing_provenance_preserves_exact_interval_fact_but_not_readiness(self):
        result = self.analyze()
        self.assertEqual(result["truth_rows"][0]["status"], "unique_interval_candidate")
        self.assertEqual(result["truth_rows"][0]["event_ids"], ["source"])
        self.assertFalse(result["same_machine_boot_provenance_verified"])
        self.assertEqual(result["validation_readiness"]["status"], "requirements_unmet")
        self.assertEqual(result["clock_provenance"]["reason"], "collector_native_provenance_missing")

    def test_independently_matching_native_provenance_is_not_provider_completion(self):
        self.native_provenance()
        self.event["raw"]["clock"]["record_identity"] = dict(
            provider="edd08927-9cc4-4e65-b970-c2560fb5c289", event_id=16, version=1)
        result = self.analyze()
        self.assertTrue(result["same_machine_boot_provenance_verified"])
        self.assertEqual(result["validation_readiness"]["status"], "requirements_unmet")
        self.assertEqual(result["provider_emission_contract"]["status"], "unverified_general")
        observed = result["provider_emission_contract"]["source_events"][0]
        self.assertEqual(observed["semantics"], "file_operation_request")
        self.assertFalse(observed["completion_proven_by_timestamp"])

    def test_native_provenance_rejects_mismatch_failure_truncation_or_fabrication(self):
        for field, invalid in (("schema_version", True), ("status", "unavailable"),
                ("returned_bytes", True), ("returned_bytes", 31), ("returned_bytes", 33),
                ("nt_status", "c0000001"), ("boot_identifier_le_hex", "0" * 32),
                ("boot_identifier_le_hex", "3" * 32), ("machine_name_sha256", "3" * 64),
                ("boot_source", "trace_header_BootTime"), ("errors", ["failed"]),
                ("qpc_before", "22"), ("qpc_after", "100"), ("qpc_before", True)):
            with self.subTest(field=field, invalid=invalid):
                self.native_provenance()
                self.truth["clock"]["provenance"][field] = invalid
                result = self.analyze()
                self.assertFalse(result["same_machine_boot_provenance_verified"])
                self.assertEqual(result["truth_rows"][0]["event_ids"], ["source"])
                self.assertEqual(result["validation_readiness"]["status"], "requirements_unmet")

    def test_native_provenance_requires_both_collector_phases_and_every_fixture_row(self):
        for defect in ("missing_stop", "reversed", "different_stop_boot", "fixture_missing"):
            with self.subTest(defect=defect):
                self.native_provenance()
                samples = self.capture["backend"]["clock"]["provenance"]
                if defect == "missing_stop": samples.pop()
                elif defect == "reversed": samples.reverse()
                elif defect == "different_stop_boot": samples[-1]["boot_identifier_le_hex"] = "3" * 32
                else: self.truth["clock"].pop("provenance")
                self.assertFalse(self.analyze()["same_machine_boot_provenance_verified"])

    def test_malformed_source_clock_shapes_remain_unavailable_without_crashing(self):
        for raw in (None, [], {"clock": None}, {"clock": []}):
            with self.subTest(raw=raw):
                self.event["raw"] = raw
                result = self.analyze()
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])
                self.assertEqual(result["provider_emission_contract"]["source_events"][0]["semantics"],
                                 "unverified_descriptor")
                self.assertEqual(result["validation_readiness"]["status"], "requirements_unmet")

    def test_malformed_provider_descriptor_never_claims_known_semantics(self):
        for descriptor in (None, [], "descriptor", 5):
            with self.subTest(descriptor=descriptor):
                self.event["raw"]["clock"]["record_identity"] = descriptor
                result = self.analyze()
                self.assertEqual(result["provider_emission_contract"]["source_events"][0]["semantics"],
                                 "unverified_descriptor")
                self.assertEqual(result["validation_readiness"]["status"], "requirements_unmet")

    def test_malformed_independent_clock_cannot_verify_native_provenance(self):
        self.native_provenance()
        for clock in (None, [], "clock", 5):
            with self.subTest(clock=clock):
                self.truth["clock"] = clock
                result = self.analyze()
                self.assertEqual(result["status"], "invalid")
                self.assertFalse(result["same_machine_boot_provenance_verified"])
                self.assertEqual(result["clock_provenance"]["reason"], "fixture_clock_not_an_object")
                self.assertEqual(result["validation_readiness"]["status"], "requirements_unmet")

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
        self.assertEqual(result["protocol_revision"], "contain-clock-observability-4")
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

    def test_primary_header_clock_selector_preserves_native_integer_type(self):
        for invalid in (True, 1.0, "1", None, 0, -1):
            with self.subTest(invalid=invalid):
                document = self.companion()
                self.capture["backend"]["clock"]["trace_header"]["reserved_flags"] = invalid
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.companion_bytes(document))
                self.assertEqual(result["status"], "unavailable")
                self.assertIn("primary_actual_clock_type_not_qpc", result["raw_companion"]["reasons"])
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

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

    def test_unknown_actor_context_does_not_hide_unaffected_exact_candidate(self):
        for changes in ({"tick": 99}, {"tick": 201}, {"resource": "C:\\fixture\\other"},
                        {"operation": "delete_requested"}):
            with self.subTest(changes=changes):
                context = copy.deepcopy(self.event)
                context.update(id="unknown-context", confidence="Unknown", evidence={})
                context.update({name: value for name, value in changes.items() if name != "tick"})
                tick = changes.get("tick", 150)
                context["raw"]["clock"].update(raw_qpc=str(tick), header_timestamp=str(tick))
                self.capture["events"] = [self.event, context]
                before = copy.deepcopy(self.capture)
                result = self.analyze()
                self.assertEqual(result["status"], "diagnostic_ready")
                self.assertEqual(result["truth_rows"][0]["event_ids"], ["source"])
                self.assertEqual(result["source_events_with_raw_qpc"], 2)
                self.assertEqual(result["source_events_with_raw_qpc_and_identity"], 1)
                self.assertEqual(result["source_actor_unknown_events"], [dict(
                    event_id="unknown-context", event_type="file", operation=context["operation"],
                    resource=context["resource"], original_qpc=str(tick), status="Unknown",
                    reason="actor_identity_unavailable")])
                self.assertEqual(self.capture, before)
                self.assertIsNone(result["release_gate"])

    def test_overlapping_unknown_actor_blocks_even_a_verified_candidate(self):
        for tick in (100, 150, 200):
            with self.subTest(tick=tick):
                context = copy.deepcopy(self.event)
                context.update(id="unknown-context", evidence={})
                context["raw"]["clock"].update(raw_qpc=str(tick), header_timestamp=str(tick))
                self.capture["events"] = [self.event, context]
                result = self.analyze()
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["reason"], "actor_identity_unavailable")
                self.assertEqual(result["truth_rows"][0]["actor_unknown_event_ids"], ["unknown-context"])
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_unknown_actor_row_is_localized_without_guessing_from_header_pid(self):
        other_truth = copy.deepcopy(self.truth)
        other_truth.update(resource="C:\\fixture\\other", role="unrelated")
        other_truth["clock"]["sequence"] = 2
        other_event = copy.deepcopy(self.event)
        other_event.update(id="unknown-other", resource=other_truth["resource"], evidence={})
        other_event["raw"]["clock"]["record_identity"] = dict(process_id=self.truth["pid"])
        self.capture["events"].append(other_event)
        result = self.analyze([self.truth, other_truth])
        self.assertEqual(result["status"], "unavailable")
        self.assertEqual(result["counts"], {"unique_interval_candidate": 1, "unavailable": 1})
        self.assertEqual(result["truth_rows"][0]["event_ids"], ["source"])
        self.assertEqual(result["truth_rows"][1]["reason"], "actor_identity_unavailable")
        self.assertEqual(result["truth_rows"][1]["actor_unknown_event_ids"], ["unknown-other"])
        self.assertEqual(result["truth_rows"][1]["event_ids"], [])

    def test_missing_original_qpc_on_unrelated_unknown_actor_remains_global(self):
        context = copy.deepcopy(self.event)
        context.update(id="unknown-context", resource="C:\\fixture\\other", evidence={})
        context["raw"]["clock"].pop("raw_qpc")
        self.capture["events"].append(context)
        result = self.analyze()
        self.assertEqual(result["status"], "unavailable")
        self.assertEqual(result["truth_rows"][0]["reason"], "source_original_clock_unavailable")
        self.assertEqual(result["truth_rows"][0]["event_ids"], [])
        self.assertEqual(result["source_events_without_raw_qpc"], 1)
        self.assertEqual(result["source_events_with_raw_qpc"], 1)

    def test_exact_companion_link_keeps_missing_actor_unknown(self):
        raw = self.companion_bytes(self.companion())
        self.event["evidence"].pop("process_creation_time")
        before = copy.deepcopy(self.capture)
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw)
        self.assertEqual(result["raw_companion"]["status"], "exact_original_key_links_available")
        self.assertEqual(result["raw_companion"]["event_links"][0]["original_qpc"], "150")
        self.assertEqual(result["source_actor_unknown_events"][0]["original_qpc"], "150")
        self.assertEqual(result["source_actor_unknown_events"][0]["status"], "Unknown")
        self.assertEqual(result["status"], "unavailable")
        self.assertEqual(result["truth_rows"][0]["reason"], "actor_identity_unavailable")
        self.assertEqual(result["truth_rows"][0]["event_ids"], [])
        self.assertEqual(self.capture, before)

    def test_actor_blocked_row_still_counts_in_cross_row_candidate_ambiguity(self):
        other_truth = copy.deepcopy(self.truth)
        other_truth["clock"].update(sequence=2, start_qpc="140")
        other_truth["clock"]["before"].update(qpc_before="130", qpc_after="139")
        context = copy.deepcopy(self.event)
        context.update(id="unknown-context", evidence={})
        context["raw"]["clock"].update(raw_qpc="120", header_timestamp="120")
        self.capture["events"].append(context)
        result = self.analyze([self.truth, other_truth])
        self.assertEqual(result["status"], "unavailable")
        self.assertEqual(result["truth_rows"][0]["reason"], "actor_identity_unavailable")
        self.assertEqual(result["truth_rows"][0]["event_ids"], [])
        self.assertEqual(result["truth_rows"][1]["status"], "ambiguous_interval_candidates")

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


class BinaryClockProtocolTests(unittest.TestCase):
    def setUp(self):
        self.fixture = ClockProtocolTests()
        self.fixture.setUp()
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "raw-clock.json"
        self.capture = self.fixture.capture
        self.truth = self.fixture.truth

    def document(self):
        document = self.fixture.companion()
        document.update(schema_version=2, protocol=CLOCK.COMPANION_BINARY_PROTOCOL, stats_final_available=True)
        document.pop("records")
        self.descriptor = self.fixture.event["raw"]["clock"]["record_identity"]
        self.descriptor["provider"] = "edd08927-9cc4-4e65-b970-c2560fb5c289"
        document["header"]["start_time"] = "0"
        self.capture["backend"]["clock"]["trace_header"]["start_time"] = "0"
        return document

    def row(self, sequence=1, key=1, tick=150, **changes):
        descriptor = dict(self.descriptor, **changes)
        return (struct.pack("<QQq", sequence, key, tick)
                + uuid.UUID(descriptor["provider"]).int.to_bytes(16, "little")
                + struct.pack("<HBBBBHQII", *(descriptor[name] for name in
                    ("event_id", "version", "channel", "level", "opcode", "task")),
                    int(descriptor["keyword"], 16), descriptor["process_id"], descriptor["thread_id"]))

    def prepare(self, document, data, received=None):
        count = len(data) // 64
        received = count if received is None else received
        document["stats"].update(received=received, stored=count, duplicate_event_keys=None,
            record_capacity=2096128, byte_capacity=33554432, bytes=len(data), fixed_record_size=64,
            allocated_record_bytes=8388608, disk_byte_capacity=134217728, serialization_buffer_bytes=65536,
            filtered_nonprimary=received-count, accepted=count, queued=count, queue_dropped=0,
            quota_dropped=0, writer_closed_dropped=0, not_persisted=0, writer_memory_bytes=9437184)
        digest = hashlib.sha256(data).hexdigest()
        document["binary"] = dict(file_name=self.path.name + ".rows.bin", format="contain-raw-clock-le64-1",
            record_size=64, records=count, bytes=len(data), sha256=digest, blake3=None, complete=True,
            metadata_byte_capacity=65536, byte_capacity=134152192)
        document["writer"] = dict(bytes=len(data), records=count, partial_record_bytes=0, write_calls=1,
            written_chunks=1, queue_high_water_chunks=1, write_elapsed_ns=1, drain_elapsed_ns=1,
            drained=True, finished=True, timed_out=False, cancel_requested=False, cancel_error=None, error=None,
            sha256=digest, blake3=None)
        self.path.with_name(document["binary"]["file_name"]).write_bytes(data)

    def manifest(self, document):
        raw = json.dumps(document).encode()
        self.path.write_bytes(raw)
        self.capture["backend"]["clock"]["raw_companion"] = dict(copy.deepcopy(document),
            written=True, file_bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest(),
            metadata_writer=dict(bytes=len(raw), write_calls=1, write_elapsed_ns=1, drain_elapsed_ns=1,
                finished=True, drained=True, timed_out=False, cancel_requested=False, cancel_error=None, error=None))
        return raw

    def analyze(self, document):
        return CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=self.manifest(document), raw_clock_path=self.path)

    def test_binary_exact_descriptor_links_and_all_noise_records_are_checked(self):
        document = self.document()
        self.prepare(document, self.row(key=2) + self.row(sequence=2, key=1) + self.row(sequence=4, key=3), received=4)
        result = self.analyze(document)
        self.assertEqual(result["status"], "diagnostic_ready")
        self.assertEqual(result["truth_rows"][0]["event_ids"], ["source"])
        binary = result["raw_companion"]["binary"]
        self.assertEqual(binary["record_count"], 3)
        self.assertEqual(binary["global_key_count"], 3)
        self.assertEqual(binary["indexed_primary_keys"], 1)
        self.assertEqual(binary["duplicate_event_keys"], 0)
        self.assertEqual(binary["duplicate_check_scope"], "all_binary_records")
        self.assertTrue(binary["sha256_verified"])
        self.assertIsNone(document["stats"]["duplicate_event_keys"])
        self.assertFalse(result["raw_companion"]["start_time_observation"]["available"])

    def test_binary_duplicate_noise_keys_fail_even_when_primary_is_unique(self):
        document = self.document()
        self.prepare(document, self.row() + self.row(sequence=2, key=2) + self.row(sequence=3, key=2))
        result = self.analyze(document)
        self.assertEqual(result["status"], "unavailable")
        self.assertIn("raw_companion_duplicate_key_sequence_or_invalid_qpc", result["raw_companion"]["reasons"])
        self.assertIsNone(result["raw_companion"]["primary_events_without_link"])

    def test_binary_invalid_noise_wire_fields_are_not_ignored(self):
        cases = [dict(sequence=2, key=0), dict(sequence=1, key=2), dict(sequence=2, key=2, tick=0),
                 dict(sequence=2, key=2, tick=-1), dict(sequence=2, key=2, provider="11111111-1111-1111-1111-111111111111")]
        for changes in cases:
            with self.subTest(changes=changes):
                document = self.document()
                self.prepare(document, self.row() + self.row(**changes))
                result = self.analyze(document)
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_binary_missing_or_wrong_primary_descriptor_never_uses_payload_similarity(self):
        for changes in (dict(key=2), dict(process_id=8), dict(event_id=17), dict(keyword="0000000000000020")):
            with self.subTest(changes=changes):
                document = self.document()
                self.prepare(document, self.row(**changes))
                result = self.analyze(document)
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_binary_hash_length_and_partial_record_cannot_be_hidden(self):
        for corrupt in ("hash", "truncated", "extra", "partial"):
            with self.subTest(corrupt=corrupt):
                document = self.document()
                data = self.row()
                self.prepare(document, data)
                if corrupt == "partial":
                    self.prepare(document, data + b"x")
                else:
                    changed = (data[:-1] if corrupt == "truncated" else data + b"x" if corrupt == "extra" else data[:-1] + b"x")
                    self.path.with_name(document["binary"]["file_name"]).write_bytes(changed)
                result = self.analyze(document)
                self.assertEqual(result["status"], "unavailable")
                self.assertEqual(result["truth_rows"][0]["event_ids"], [])

    def test_binary_quota_pending_failure_and_unmeasured_duplicate_claim_remain_fatal(self):
        cases = [("stats", "quota_dropped", 1), ("stats", "queue_dropped", 1),
                 ("stats", "writer_closed_dropped", 1), ("stats", "not_persisted", 1),
                 ("stats", "queued", 0), ("stats", "received", True), ("stats", "duplicate_event_keys", 0),
                 ("writer", "drained", False), ("writer", "timed_out", True),
                 ("writer", "finished", False),
                 ("writer", "cancel_requested", True), ("writer", "cancel_error", 5),
                 ("writer", "error", "disk failure"), ("writer", "partial_record_bytes", 1),
                 ("binary", "complete", False), ("binary", "record_size", True),
                 ("binary", "byte_capacity", 134217728), ("binary", "metadata_byte_capacity", 65537)]
        for section, field, value in cases:
            with self.subTest(section=section, field=field):
                document = self.document()
                self.prepare(document, self.row())
                document[section][field] = value
                self.assertEqual(self.analyze(document)["status"], "unavailable")

    def test_binary_final_stats_cannot_be_assumed_from_zero_counters(self):
        for flag in (None, False, 1, "true"):
            with self.subTest(flag=flag):
                document = self.document()
                self.prepare(document, self.row())
                if flag is None:
                    document.pop("stats_final_available")
                else:
                    document["stats_final_available"] = flag
                result = self.analyze(document)
                self.assertEqual(result["status"], "unavailable")
                self.assertIn("raw_companion_binary_final_stats_unavailable", result["raw_companion"]["reasons"])

    def test_binary_summary_must_preserve_boolean_and_integer_types(self):
        for section, field, value in (("stats", "received", True), ("writer", "finished", 1),
                                      (None, "stats_final_available", 1), (None, "ready", 1)):
            with self.subTest(section=section, field=field):
                document = self.document()
                self.prepare(document, self.row())
                raw = self.manifest(document)
                summary = self.capture["backend"]["clock"]["raw_companion"]
                target = summary[section] if section else summary
                target[field] = value
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw, raw_clock_path=self.path)
                self.assertEqual(result["status"], "unavailable")
                self.assertIn("raw_companion_binary_summary_mismatch", result["raw_companion"]["reasons"])

    def test_binary_written_claim_cannot_hide_unfinished_metadata_persistence(self):
        for field, value in (("bytes", 0), ("write_calls", 0), ("finished", False), ("drained", False),
                             ("timed_out", True), ("cancel_requested", True), ("cancel_error", 5),
                             ("error", "sync failed"), ("write_elapsed_ns", True), ("finished", 1)):
            with self.subTest(field=field, value=value):
                document = self.document()
                self.prepare(document, self.row())
                raw = self.manifest(document)
                self.capture["backend"]["clock"]["raw_companion"]["metadata_writer"][field] = value
                result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw, raw_clock_path=self.path)
                self.assertEqual(result["status"], "unavailable")
                self.assertIn("raw_companion_metadata_writer_incomplete", result["raw_companion"]["reasons"])

    def test_binary_requires_manifest_path_and_exact_safe_sibling_name(self):
        document = self.document()
        self.prepare(document, self.row())
        raw = self.manifest(document)
        result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw)
        self.assertIn("raw_companion_binary_path_required", result["raw_companion"]["reasons"])
        for name in ("../other.bin", r"C:\other.bin", self.path.name + ".rows.bin.",
                     self.path.name + ".rows.bin:stream", "other.rows.bin"):
            with self.subTest(name=name):
                document["binary"]["file_name"] = name
                self.assertEqual(self.analyze(document)["status"], "unavailable")

    def test_binary_reparse_and_hardlink_paths_are_rejected(self):
        document = self.document()
        self.prepare(document, self.row())
        raw = self.manifest(document)
        native_lstat = Path.lstat
        def redirected(path):
            info = native_lstat(path)
            result = mock.Mock(st_mode=info.st_mode, st_file_attributes=0x400)
            return result
        with mock.patch.object(Path, "lstat", redirected):
            result = CLOCK.analyze(self.capture, [self.truth], raw_clock_bytes=raw, raw_clock_path=self.path)
        self.assertIn("raw_companion_binary_redirected_path", result["raw_companion"]["reasons"])
        binary = self.path.with_name(document["binary"]["file_name"])
        os.link(binary, binary.with_name("alias.bin"))
        self.assertEqual(self.analyze(document)["status"], "unavailable")

    def test_binary_cli_resolves_only_committed_sibling_artifact(self):
        document = self.document()
        self.prepare(document, self.row())
        self.manifest(document)
        capture, truth, output = (self.path.with_name(name) for name in ("capture.json", "truth.json", "diagnostic.json"))
        capture.write_text(json.dumps(self.capture))
        truth.write_text(json.dumps([self.truth]))
        process = subprocess.run([sys.executable, str(Path(CLOCK.__file__)), "--capture", str(capture),
            "--truth", str(truth), "--raw-clock", str(self.path), "--output", str(output)], capture_output=True)
        # The exact sibling link is still valid, but missing independent native
        # provenance/general emission validation now prevents readiness.
        self.assertEqual(process.returncode, 2, process.stderr)
        result = json.loads(output.read_text())
        self.assertEqual(result["status"], "diagnostic_ready")
        self.assertEqual(result["validation_readiness"]["status"], "requirements_unmet")
        self.assertEqual(result["raw_companion"]["status"], "exact_original_key_links_available")
        self.assertIsNone(result["release_gate"])


if __name__ == "__main__":
    unittest.main()
