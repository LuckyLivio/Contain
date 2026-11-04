import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(file))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


AUDIT = module("write_audit", "analyze-write-diagnostic.py")
FIXTURES = module("existing_clock_fixtures", "test-v04-clock-protocol.py")


class WriteDiagnosticTests(unittest.TestCase):
    def setUp(self):
        self.base = FIXTURES.ClockProtocolTests()
        self.base.setUp()
        self.base.native_provenance()
        self.document = self.base.companion()
        self.capture = self.base.capture
        self.capture["events"] = []
        self.capture["raw_events"] = []
        self.capture["stats"] = dict(events_dropped=0, drain_timed_out=False)
        self.capture["backend"].update(etw_file="active", etw_process="active", dropped_events=0,
            context_losses=0, decode_errors=0, etw_events_lost=0, etw_buffers_lost=0,
            pipeline=dict(queue_overflow=0, enqueue_disconnected=0, queue_pending=0,
                          context_evictions=0, retention_dropped=0, failed_without_output=0),
            handoff=dict(overflow=0, oversized=0, disconnected=0, serialize_failed=0, pending=0,
                         receiver_finished=True, writer_finished=True),
            stream=dict(failed=0, quota_dropped=0, error=None),
            file_diagnostics=dict(enabled=True, parse_errors=0, truncated_records=0, budget_dropped=0,
                                  oversized_records=0, context_samples_omitted=0, context_samples=[]))
        self.journal = [dict(kind="write_diagnostic_session", schema_version=1, files_per_case=2, workers=1, expected_api_calls=16)]
        self.summary = dict(kind="write_diagnostic_summary", schema_version=1, planned_files=8, files_per_case=2,
                            workers=1, expected_api_calls=16, recorded_api_calls=16, failures=[], final_checks=[])
        self.document["records"] = []
        metadata = self.capture["backend"]["clock"]
        metadata["calibrations"][2].update(qpc_before="1000", qpc_after="1001")
        metadata["calibrations"][3].update(qpc_before="1100", qpc_after="1101")
        metadata["provenance"][1].update(qpc_before="1080", qpc_after="1090")
        self.next_key = 1
        for case, actions in AUDIT.PLANS.items():
            for index in (0, 1):
                old, new = f"C:\\fixture\\{case}-{index}.tmp", f"C:\\fixture\\{case}-{index}.dat"
                self.summary["final_checks"].append(dict(case=case, file_index=index,
                    paths=[dict(path=old, matches_expected=True), dict(path=new, matches_expected=True)]))
                for action in actions:
                    order = len(self.journal)
                    start, end = 100 + order * 20, 110 + order * 20
                    call = copy.deepcopy(self.base.truth)
                    call.update(kind="write_diagnostic_api_call", schema_version=1, case=case, file_index=index, phase="workload",
                        order=order, action=action, operation=AUDIT.API[action][0], api=AUDIT.API[action][1],
                        resource=new if action == "delete" and case == "D" else old,
                        destination=new if action == "rename" else None, success=True, error=None,
                        close_result=None, api_return="Ok(())", actor=dict(pid=7, creation_time="1000", thread_id=9))
                    call["clock"].update(sequence=order, start_qpc=str(start), end_qpc=str(end),
                        before=dict(qpc_before=str(start - 2), qpc_after=str(start - 1), filetime_ticks=str(100000 + start)),
                        after=dict(qpc_before=str(end + 1), qpc_after=str(end + 2), filetime_ticks=str(100000 + end)))
                    self.journal.append(call)
                    event = self.add_event(call["operation"], call["resource"], start + 3,
                                           {"write": 16, "rename": 27, "delete": 26}[action])
                    self.add_completion(event, start + 4)

    def add_event(self, operation, resource, tick, number, event_type="file", related=None):
        key = self.next_key
        self.next_key += 1
        descriptor = dict(provider=AUDIT.FILE_PROVIDER, event_id=number, version=0 if number == 24 else 1,
                          channel=16, level=4, opcode=0, task=number, keyword="8000000000000220",
                          process_id=7, thread_id=9)
        event = dict(id=f"event-{key}", event_type=event_type, operation=operation, resource=resource,
            timestamp_ticks=str(100000 + tick), success=True if number == 24 else None,
            confidence="Unknown", dimensions=dict(actor="Unknown", resource="High", operation="High"),
            evidence=dict(source="EtwFile", pid=None, process_creation_time=None),
            raw=dict(event_id=number, irp=f"irp-{key}", related_event=related,
                file_object="object-1", object_generation="generation-1", file_key="key-1",
                clock=dict(timestamp_domain="filetime_100ns", raw_qpc=None, header_timestamp=str(100000 + tick),
                           event_key=f"{key:016x}", record_identity=descriptor)))
        if number == 24:
            event["raw"]["status"] = 0
        else:
            event["raw"]["file_diagnostics"] = dict(schema_version=1, path_source="file_object_cache",
                cache_name=resource, current_path_verified=False, fields={"IOSize": dict(status="parsed", value="42")})
        self.capture["raw_events"].append(event)
        if event_type == "file":
            final = copy.deepcopy(event)
            final.update(confidence="High", success=True)
            final["evidence"].update(pid=7, process_creation_time="1000")
            final["dimensions"]["actor"] = "High"
            self.capture["events"].append(final)
        self.document["records"].append(dict(descriptor, sequence=key, event_key=f"{key:016x}", header_timestamp=str(tick)))
        return event

    def add_completion(self, event, tick):
        completion = self.add_event("operation_end", "", tick, 24, "completion", event["id"])
        completion["raw"]["irp"] = event["raw"]["irp"]
        return completion

    def raw_bytes(self):
        count = len(self.document["records"])
        self.document["stats"].update(received=count, stored=count, bytes=count * 80,
            record_capacity=128, allocated_record_bytes=10240, byte_capacity=16384, disk_byte_capacity=131072)
        return self.base.companion_bytes(self.document)

    def analyze(self, raw=True):
        return AUDIT.analyze(self.capture, self.journal, self.summary, self.raw_bytes() if raw else None)

    def sequence(self, result, case="D", index=0):
        return next(row for row in result["sequences"] if (row["case"], row["file_index"]) == (case, index))

    def extra_write(self):
        call = next(row for row in self.journal if row.get("case") == "D" and row["file_index"] == 0 and row["action"] == "delete")
        event = self.add_event("write_requested", call["resource"].replace(".dat", ".tmp"), int(call["clock"]["start_qpc"]) + 5, 16)
        self.add_completion(event, int(call["clock"]["start_qpc"]) + 6)
        return event

    def test_original_records_final_actor_and_completion_remain_separate(self):
        before = copy.deepcopy((self.capture, self.journal, self.summary))
        result = self.analyze()
        self.assertEqual(result["status"], "evidence_available_for_review")
        self.assertEqual(result["counts"]["fixture_calls"], 16)
        self.assertEqual(result["primary_records"], before[0]["raw_events"])
        self.assertEqual(result["final_view_events"], before[0]["events"])
        self.assertTrue(all(row["completion_status"] == "successful_recorded_chain" for row in result["all_file_request_chains"]))
        self.assertIsNone(result["release_gate"])
        self.assertIsNone(result["fix_pass"])
        self.assertFalse(result["attribution_scored"])
        self.assertFalse(result["legacy_score_recomputed"])
        self.assertEqual(result["tolerance_qpc"], 0)
        self.assertEqual(self.journal, before[1])
        self.assertEqual(self.summary, before[2])

    def test_zero_extra_candidate_is_not_a_fix_pass(self):
        result = self.analyze()
        self.assertEqual(self.sequence(result)["delete_window_write"]["status"], "not_reproduced_not_a_fix_pass")
        self.assertIsNone(self.sequence(result)["delete_window_write"]["fix_pass"])

    def test_extra_write_reports_delete_api_old_name_and_cache_without_reclassification(self):
        extra = self.extra_write()
        result = self.analyze()
        sequence = self.sequence(result)
        self.assertEqual(sequence["delete_window_write"]["status"], "observed_delete_window_write")
        self.assertEqual(sequence["delete_window_write"]["event_ids"], [extra["id"]])
        inside = [row for row in sequence["event_call_relations"][extra["id"]] if row["relation"] == "inside"]
        self.assertEqual(len(inside), 1)
        self.assertEqual(inside[0]["actual_api"], "std::fs::remove_file")
        self.assertTrue(inside[0]["same_actor"])
        self.assertFalse(inside[0]["same_operation"])
        name = next(row for row in sequence["name_observations"] if row["event_id"] == extra["id"])
        self.assertTrue(name["equals_rename_old"])
        self.assertFalse(name["equals_rename_new"])
        self.assertFalse(name["current_path_inferred"])
        self.assertEqual(name["file_diagnostics"], extra["raw"]["file_diagnostics"])

    def test_missing_raw_clock_never_inverts_filetime(self):
        result = self.analyze(raw=False)
        self.assertEqual(result["status"], "unavailable_or_incomplete")
        self.assertEqual(self.sequence(result)["delete_window_write"]["status"], "unavailable")
        for chain in result["all_file_request_chains"]:
            self.assertIsNone(chain["request_original_qpc"]["original_qpc"])

    def test_missing_or_duplicate_original_key_never_uses_nearby_clock(self):
        for kind in ("missing", "duplicate", "descriptor"):
            with self.subTest(kind=kind):
                self.setUp()
                if kind == "missing": self.capture["raw_events"][0]["raw"]["clock"].pop("event_key")
                if kind == "duplicate": self.document["records"][1]["event_key"] = self.document["records"][0]["event_key"]
                if kind == "descriptor": self.document["records"][0]["version"] += 1
                result = self.analyze()
                self.assertEqual(result["status"], "unavailable_or_incomplete")
                self.assertTrue(all(row["request_original_qpc"]["original_qpc"] is None for row in result["all_file_request_chains"]))

    def test_missing_completion_key_is_visible_without_fabricating_request_time(self):
        completion = self.capture["raw_events"][1]
        self.document["records"][1]["event_key"] = "000000000000ffff"
        result = self.analyze()
        chain = result["all_file_request_chains"][0]
        self.assertEqual(chain["completion_status"], "unavailable")
        self.assertEqual(chain["completions"][0]["event_id"], completion["id"])
        self.assertIsNone(chain["completions"][0]["original_qpc"]["original_qpc"])

    def test_completion_duplicate_wrong_irp_reversed_and_status_conflict(self):
        for defect, expected in (("duplicate", "ambiguous"), ("irp", "conflict"), ("reverse", "conflict"), ("status", "conflict")):
            with self.subTest(defect=defect):
                self.setUp()
                request, completion = self.capture["raw_events"][:2]
                if defect == "duplicate": self.add_completion(request, 125)
                if defect == "irp": completion["raw"]["irp"] = "other"
                if defect == "reverse": self.document["records"][1]["header_timestamp"] = "122"
                if defect == "status": completion["success"] = False
                self.assertEqual(self.analyze()["all_file_request_chains"][0]["completion_status"], expected)

    def test_out_of_order_delivery_preserves_exact_request_reference(self):
        self.capture["raw_events"].reverse()
        result = self.analyze()
        self.assertTrue(all(row["completion_status"] == "successful_recorded_chain" for row in result["all_file_request_chains"]))

    def test_actor_unknown_never_filled_from_header_pid(self):
        extra = self.extra_write()
        final = next(row for row in self.capture["events"] if row["id"] == extra["id"])
        final["dimensions"]["actor"] = "Unknown"
        result = self.analyze()
        outcome = self.sequence(result)["delete_window_write"]
        self.assertEqual(outcome["status"], "unavailable")
        self.assertEqual(outcome["unavailable_event_ids"], [extra["id"]])

    def test_pid_reuse_with_other_birth_is_not_same_actor(self):
        extra = self.extra_write()
        next(row for row in self.capture["events"] if row["id"] == extra["id"])["evidence"]["process_creation_time"] = "2000"
        result = self.analyze()
        inside = [row for row in self.sequence(result)["event_call_relations"][extra["id"]] if row["relation"] == "inside"]
        self.assertFalse(inside[0]["same_actor"])
        self.assertEqual(self.sequence(result)["delete_window_write"]["event_ids"], [])

    def test_before_and_after_call_are_retained_not_ignored(self):
        extra = self.extra_write()
        row = next(row for row in self.document["records"] if row["event_key"] == extra["raw"]["clock"]["event_key"])
        row["header_timestamp"] = "99"
        result = self.analyze()
        self.assertTrue(all(item["relation"] == "before" for item in self.sequence(result)["event_call_relations"][extra["id"]]))
        self.assertIn(extra, result["primary_records"])
        row["header_timestamp"] = "999"
        result = self.analyze()
        self.assertTrue(all(item["relation"] == "after" for item in self.sequence(result)["event_call_relations"][extra["id"]]))

    def test_call_outside_trace_and_missing_provenance_keep_relations_unavailable(self):
        for defect in ("window", "provenance"):
            with self.subTest(defect=defect):
                self.setUp()
                if defect == "window": self.capture["backend"]["clock"]["calibrations"][1].update(qpc_after="120")
                else: self.capture["backend"]["clock"].pop("provenance")
                result = self.analyze()
                self.assertEqual(result["status"], "unavailable_or_incomplete")
                first = self.sequence(result, "A")["event_call_relations"]["event-1"][0]
                self.assertEqual(first["relation"], "unavailable")

    def test_api_error_retained_success_denominator_unchanged(self):
        self.journal[1].update(success=False, api_return="Err", error=32, error_kind="PermissionDenied")
        self.summary["failures"] = [dict(order=1, error=32)]
        result = self.analyze()
        self.assertEqual(result["counts"]["fixture_calls"], 16)
        self.assertEqual(result["counts"]["successful_fixture_calls"], 15)
        self.assertIn("fixture_api_failure", result["issues"])
        self.assertEqual(result["fixture_journal"][1]["error"], 32)
        self.assertEqual(result["status"], "unavailable_or_incomplete")

    def test_raw_final_conflict_and_duplicate_ids_do_not_choose_one(self):
        for defect in ("duplicate", "conflict"):
            with self.subTest(defect=defect):
                self.setUp()
                if defect == "duplicate": self.capture["raw_events"].append(copy.deepcopy(self.capture["raw_events"][0]))
                else: self.capture["events"][0]["resource"] = "C:\\other"
                result = self.analyze()
                self.assertEqual(result["status"], "unavailable_or_incomplete")
                self.assertIn("duplicate_primary_event_id" if defect == "duplicate" else "raw_final_fact_conflict", result["issues"])

    def test_orphan_completion_retained(self):
        completion = self.add_event("operation_end", "", 130, 24, "completion", "absent-request")
        result = self.analyze()
        self.assertEqual(result["orphan_completion_ids"], [completion["id"]])

    def test_final_event_clock_cannot_replace_original_record_identity(self):
        self.capture["events"][0]["raw"]["clock"]["event_key"] = "000000000000aaaa"
        result = self.analyze()
        self.assertIn("raw_final_fact_conflict", result["issues"])
        self.assertEqual(result["status"], "unavailable_or_incomplete")

    def test_missing_or_malformed_diagnostic_fields_do_not_invent_path_provenance(self):
        for value in (None, {}, {"schema": {"truncated": True}, "fields": {"IOSize": {"status": "schema_type_mismatch", "value": None}}}):
            with self.subTest(value=value):
                self.capture["raw_events"][0]["raw"]["file_diagnostics"] = value
                result = self.analyze()
                observation = self.sequence(result, "A")["name_observations"][0]
                self.assertEqual(observation["file_diagnostics"], value)
                self.assertFalse(observation["current_path_inferred"])

    def test_loss_missing_counters_and_diagnostic_budget_prevent_negative_conclusion(self):
        for group, key, value in (("backend", "dropped_events", 1), ("backend", "etw_events_lost", None),
                                  ("file_diagnostics", "budget_dropped", 1), ("file_diagnostics", "parse_errors", 1),
                                  ("file_diagnostics", "context_samples_omitted", 1)):
            with self.subTest(group=group, key=key):
                self.setUp()
                target = self.capture["backend"] if group == "backend" else self.capture["backend"][group]
                target[key] = value
                result = self.analyze()
                self.assertEqual(self.sequence(result)["delete_window_write"]["status"], "unavailable")
                self.assertFalse(result["observation_integrity"]["negative_observation_complete"])
                first = self.sequence(result, "A")["event_call_relations"]["event-1"][0]
                self.assertEqual(first["relation"], "inside")

    def test_bogus_provider_or_nonhigh_write_is_not_qualified_reproduction(self):
        for defect in ("provider", "source", "final_source", "confidence", "operation"):
            with self.subTest(defect=defect):
                self.setUp()
                extra = self.extra_write()
                final = next(row for row in self.capture["events"] if row["id"] == extra["id"])
                if defect == "provider":
                    extra["raw"]["clock"]["record_identity"]["provider"] = "11111111-1111-1111-1111-111111111111"
                    final["raw"]["clock"]["record_identity"]["provider"] = "11111111-1111-1111-1111-111111111111"
                    self.document["records"][-2]["provider"] = "11111111-1111-1111-1111-111111111111"
                if defect == "source": extra["evidence"]["source"] = "Snapshot"
                if defect == "final_source": final["evidence"]["source"] = "Snapshot"
                if defect == "confidence": final["confidence"] = "Unknown"
                if defect == "operation":
                    extra["operation"] = final["operation"] = "other"
                result = self.analyze()
                outcome = self.sequence(result)["delete_window_write"]
                self.assertEqual(outcome["event_ids"], [])
                self.assertEqual(outcome["status"], "unavailable")

    def test_zero_file_records_is_unavailable_not_not_reproduced(self):
        self.capture["raw_events"] = []
        self.capture["events"] = []
        result = self.analyze()
        self.assertEqual(self.sequence(result)["delete_window_write"]["status"], "unavailable")

    def test_binary_clock_extension_links_completion_without_changing_its_kind(self):
        helper = FIXTURES.BinaryClockProtocolTests()
        helper.setUp()
        self.addCleanup(helper.temporary.cleanup)
        helper.capture = self.capture
        helper.descriptor = self.document["records"][0]
        document = copy.deepcopy(self.document)
        records = document.pop("records")
        document.update(schema_version=2, protocol=AUDIT.CLOCK.COMPANION_BINARY_PROTOCOL, stats_final_available=True)
        data = b"".join(helper.row(sequence=row["sequence"], key=int(row["event_key"], 16), tick=int(row["header_timestamp"]),
            **{name: row[name] for name in ("provider", "event_id", "version", "channel", "level", "opcode", "task", "keyword", "process_id", "thread_id")})
            for row in records)
        helper.prepare(document, data)
        raw = helper.manifest(document)
        result = AUDIT.analyze(self.capture, self.journal, self.summary, raw, helper.path)
        self.assertEqual(result["status"], "evidence_available_for_review")
        self.assertTrue(all(row["completion_status"] == "successful_recorded_chain" for row in result["all_file_request_chains"]))
        self.assertEqual(result["primary_records"][1]["event_type"], "completion")

    def test_missing_duplicate_sequence_and_mismatched_fixture_actor_are_visible(self):
        for defect, issue in (("sequence", "fixture_final_check_sequence_missing_or_duplicated"),
                              ("actor", "fixture_actor_fields_disagree_or_missing"),
                              ("call", "fixture_sequence_incomplete_or_reordered")):
            with self.subTest(defect=defect):
                self.setUp()
                if defect == "sequence": self.summary["final_checks"][1] = copy.deepcopy(self.summary["final_checks"][0])
                if defect == "actor": self.journal[1]["actor"]["creation_time"] = "2000"
                if defect == "call": self.journal.pop()
                self.assertIn(issue, self.analyze()["issues"])

    def test_contradictory_fixture_actor_cannot_be_same_actor_or_negative_conclusion(self):
        extra = self.extra_write()
        call = next(row for row in self.journal if row.get("case") == "D" and row["file_index"] == 0 and row["action"] == "delete")
        call["actor"]["creation_time"] = "2000"
        result = self.analyze()
        inside = [row for row in self.sequence(result)["event_call_relations"][extra["id"]] if row["relation"] == "inside"]
        self.assertIsNone(inside[0]["same_actor"])
        self.assertEqual(self.sequence(result)["delete_window_write"]["status"], "unavailable")

    def test_success_flag_with_error_is_not_an_independent_success_claim(self):
        self.journal[1]["error"] = 32
        result = self.analyze()
        self.assertIn("fixture_api_outcome_conflict", result["issues"])
        self.assertEqual(self.sequence(result)["delete_window_write"]["status"], "unavailable")

    def test_cli_exclusive_output_and_input_hashes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            raw = self.raw_bytes()
            paths = {"capture": root / "capture.json", "operations": root / "operations.jsonl",
                     "summary": root / "summary.json", "raw-clock": root / "raw-clock.json"}
            paths["capture"].write_text(json.dumps(dict(schema_version=3, kind="capture", data=self.capture,
                private_diagnostic_export=dict(rows=len(self.capture["raw_events"])))), encoding="utf-8")
            paths["operations"].write_text("\n".join(json.dumps(row) for row in self.journal), encoding="utf-8")
            paths["summary"].write_text(json.dumps(self.summary), encoding="utf-8")
            paths["raw-clock"].write_bytes(raw)
            before = {key: hashlib.sha256(path.read_bytes()).hexdigest() for key, path in paths.items()}
            output = root / "analysis.json"
            command = [sys.executable, str(Path(AUDIT.__file__))]
            for key, path in paths.items(): command += ["--" + key, str(path)]
            command += ["--output", str(output)]
            first = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(first.returncode, 0, first.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertTrue(report["input_hashes_unchanged"])
            self.assertEqual(report["capture_envelope"]["schema_version"], 3)
            self.assertEqual(report["capture_backend"]["file_diagnostics"]["context_samples"], [])
            original = output.read_bytes()
            self.assertNotEqual(subprocess.run(command, capture_output=True).returncode, 0)
            self.assertEqual(output.read_bytes(), original)
            self.assertEqual(before, {key: hashlib.sha256(path.read_bytes()).hexdigest() for key, path in paths.items()})


if __name__ == "__main__":
    unittest.main()
