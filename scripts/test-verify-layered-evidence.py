"""Synthetic regressions for the separate layered evidence protocol."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


FIXTURE = module("layered_fixture", "test-analyze-write-diagnostic.py")
VERIFY = module("layered_verify", "verify-layered-evidence.py")


class LayeredTests(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURE.WriteDiagnosticTests()
        self.fixture.setUp()
        self.extra = self.fixture.extra_write()
        self.extra_id = self.extra["id"]
        raw = self.extra["raw"]
        payload = struct.pack("<QQQQIIII", 0, 4096, 8192, 12288, 9, 4096, 0x60043, 0)
        raw.update(irp="1000", file_object="2000", file_key="3000", thread_id=9)
        raw["file_diagnostics"].update(payload_bytes=48, payload_prefix_hex=payload.hex(),
            payload_truncated=False, schema=dict(truncated=False, invalid_layout=False),
            fields={name: dict(status="parsed", value=str(value)) for name, value in zip(
                ("ByteOffset", "Irp", "FileObject", "FileKey", "IssuingThreadId", "IOSize", "IOFlags", "ExtraFlags"),
                struct.unpack("<QQQQIIII", payload))})
        final = next(e for e in self.fixture.capture["events"] if e["id"] == self.extra_id)
        final["raw"] = copy.deepcopy(raw)
        completion = next(e for e in self.fixture.capture["raw_events"] if e["raw"].get("related_event") == self.extra_id)
        completion["raw"]["irp"] = "1000"
        self.analysis = self.fixture.analyze()
        self.analysis_bytes = json.dumps(self.analysis).encode()
        call = next(c for c in self.fixture.journal if c.get("case") == "D" and c.get("file_index") == 0 and c.get("action") == "delete")
        qpc = int(call["clock"]["start_qpc"]) + 5
        self.native = dict(source_header=dict(reserved_flags_clock=1, perf_freq=10000000,
            boot_time=12345, events_lost=3, buffers_lost=1), chains=[dict(
                classic_request=dict(provider=VERIFY.CLASSIC, id=0, opcode=68, version=3, task=0,
                                     qpc=qpc, pid=7, tid=9, length=48, payload=payload.hex()),
                classic_completion=dict(provider=VERIFY.CLASSIC, id=0, opcode=76, version=3, task=0,
                    qpc=qpc+1, pid=7, tid=9, length=20, payload=struct.pack("<QQI",4096,42,0).hex()),
                stack_record=dict(provider=VERIFY.STACK, id=0, opcode=32, version=2, task=0,
                    qpc=qpc+1, length=32, payload=struct.pack("<QIIQQ",qpc,7,9,0xffff000000001234,0x1234).hex()))])
        self.review = dict(protocol=VERIFY.REVIEW_PROTOCOL, review_id="synthetic-review", reviewed=True,
            analysis_sha256=hashlib.sha256(self.analysis_bytes).hexdigest(), artifacts=[dict(
                id="native", path="synthetic-native.json", sha256="0"*64)], claims=[dict(
                    event_id=self.extra_id, verdict="independent_supported", reason="Synthetic exact request and stack audit",
                    claim=dict(operation="write_requested", resource=final["resource"], pid=7,
                               creation_time="1000", thread_id=9, completion_id=completion["id"]),
                    actor_call_order=call["order"], witness_ref=dict(artifact="native",pointer="/chains/0"),
                    name_scope="historical_open_name")])
        self.analysis["capture_backend"]["clock"]["trace_header"]=dict(boot_time="12345",perf_frequency="10000000")
        self.analysis["capture_backend"]["clock"].setdefault("raw_companion",{})["header"]=dict(boot_time="12345",perf_frequency="10000000")
        self.analysis_bytes=json.dumps(self.analysis).encode()
        self.review["analysis_sha256"]=hashlib.sha256(self.analysis_bytes).hexdigest()
        self.review["run_binding"]=dict(observer_attempt_artifact="outer",child_attempt_artifact="child",analysis_path="C:/fixture/run/capture/analysis.json")
        self.review["artifacts"].extend([dict(id="outer",path="C:/fixture/run/attempt.json",sha256="0"*64),
                                       dict(id="child",path="C:/fixture/run/capture/attempt.json",sha256="0"*64)])
        self.outer=dict(child_state=dict(attempt_path="C:/fixture/run/capture/attempt.json"),child_exit=0,
                        expected_collector="observer",retained_observer_files=[dict(path="C:/fixture/run/observer.etl")])
        self.child=dict(capture_exit=0)
        self.native["source_snapshots_after"]=[dict(path="C:/fixture/run/observer.etl",unchanged=True,sha256="2"*64)]

    def verify(self, review=True):
        return VERIFY.verify(self.analysis, self.review if review else None,
                             {"native": self.native,"outer":self.outer,"child":self.child}, self.analysis_bytes)

    def test_api_and_native_denominators_are_separate(self):
        result = self.verify()
        self.assertEqual(result["api_layer"]["successful_calls"],16)
        self.assertEqual(result["api_layer"]["matched_successful_calls"],16)
        self.assertEqual(result["native_layer"]["positive_requests"],17)
        self.assertEqual(result["native_layer"]["independent_supported"],1)
        self.assertEqual(result["native_layer"]["unresolved"],16)
        self.assertIsNone(result["native_layer"]["precision"])
        self.assertIsNone(result["formal_acceptance"])

    def test_unreviewed_extra_positive_is_retained_unresolved(self):
        result = self.verify(False)
        self.assertEqual(result["native_layer"]["positive_requests"],17)
        self.assertEqual(result["native_layer"]["unresolved"],17)
        self.assertIn(self.extra_id,[r["event_id"] for r in result["native_layer"]["requests"]])

    def test_delete_window_size_and_flags_never_exempt_extra(self):
        result = self.verify(False)
        row = next(r for r in result["native_layer"]["requests"] if r["event_id"] == self.extra_id)
        self.assertEqual(row["status"],"unresolved")
        self.assertEqual(row["api_correspondence_orders"],[])

    def test_wrong_review_operation_actor_or_resource_rejected(self):
        for field,value in (("operation","delete_requested"),("pid",8),("creation_time","1001"),
                            ("resource","C:\\fixture\\unrelated.tmp"),("thread_id",10)):
            with self.subTest(field=field):
                before=copy.deepcopy(self.review)
                self.review["claims"][0]["claim"][field]=value
                result=self.verify()
                self.assertEqual(result["native_layer"]["independent_supported"],0)
                self.assertEqual(result["native_layer"]["proven_wrong"],0)
                self.assertEqual(len(result["rejected_reviews"]),1)
                self.review=before

    def test_wrong_native_pointer_thread_size_or_completion_cannot_support(self):
        changes=[("classic_request","payload",struct.pack("<QQQQIIII",0,4096,8193,12288,9,4096,0x60043,0).hex()),
                 ("classic_request","tid",10),("classic_request","opcode",70),
                 ("classic_completion","payload",struct.pack("<QQI",4096,42,0xC0000001).hex())]
        for kind,field,value in changes:
            with self.subTest(kind=kind,field=field):
                old=copy.deepcopy(self.native)
                self.native["chains"][0][kind][field]=value
                self.assertEqual(self.verify()["native_layer"]["independent_supported"],0)
                self.native=old

    def test_stack_exact_trigger_required_not_nearby(self):
        row=self.native["chains"][0]["stack_record"]
        data=bytearray.fromhex(row["payload"])
        struct.pack_into("<Q",data,0,struct.unpack_from("<Q",data)[0]+1)
        row["payload"]=data.hex()
        self.assertEqual(self.verify()["native_layer"]["independent_supported"],0)

    def test_same_frequency_without_boot_or_execution_bridge_is_insufficient(self):
        for mutate in (lambda:self.native["source_header"].update(boot_time=999),
                       lambda:self.outer["child_state"].update(attempt_path="C:/fixture/other/attempt.json"),
                       lambda:self.native.update(source_snapshots_after=[])):
            before=copy.deepcopy((self.native,self.outer));mutate()
            self.assertEqual(self.verify()["native_layer"]["independent_supported"],0)
            self.native,self.outer=before

    def test_bad_review_hash_or_duplicate_event_review_fails_closed(self):
        self.review["analysis_sha256"]="1"*64
        with self.assertRaises(ValueError):self.verify()
        self.review["analysis_sha256"]=hashlib.sha256(self.analysis_bytes).hexdigest()
        self.review["claims"].append(copy.deepcopy(self.review["claims"][0]))
        with self.assertRaises(ValueError):self.verify()

    def test_missing_or_malformed_reference_stays_unresolved(self):
        for pointer in ("/chains/99","/chains/no","not-a-pointer"):
            self.review["claims"][0]["witness_ref"]["pointer"]=pointer
            self.assertEqual(self.verify()["native_layer"]["independent_supported"],0)

    def test_partial_observer_does_not_erase_exact_positive_or_enable_gate(self):
        result=self.verify()
        self.assertEqual(result["native_layer"]["independent_supported"],1)
        self.assertEqual(result["native_layer"]["requests"][-1]["independent_loss"]["events_lost"],3)
        self.assertIsNone(result["native_layer"]["precision"])

    def test_wrong_api_actor_operation_or_resource_loses_api_match(self):
        first=self.analysis["final_view_events"][0]
        for update in (dict(operation="rename_requested"),dict(resource="C:\\fixture\\wrong.tmp"),
                       dict(evidence=dict(source="EtwFile",pid=8,process_creation_time="1000"))):
            original=copy.deepcopy(first);first.update(update)
            result=self.verify(False)
            self.assertLess(result["api_layer"]["matched_successful_calls"],16)
            first.clear();first.update(original)

    def test_current_name_and_ownership_not_promoted_by_request_stack(self):
        result=self.verify()
        row=next(r for r in result["name_resource_layer"]["requests"] if r["event_id"]==self.extra_id)
        self.assertIsNone(row["verified_current_path"])
        self.assertFalse(row["final_ownership_proven"])

    def test_reason_or_cross_layer_name_difference_never_proves_wrong(self):
        self.review["claims"][0].update(verdict="proven_wrong",contradiction_dimension="resource")
        result=self.verify()
        self.assertEqual(result["native_layer"]["proven_wrong"],0)
        self.assertEqual(result["native_layer"]["unresolved"],17)

    def test_verified_native_write_can_demonstrate_wrong_operation_label(self):
        for collection in ("primary_records","final_view_events"):
            next(e for e in self.analysis[collection] if e["id"]==self.extra_id)["operation"]="rename_requested"
        self.review["claims"][0].update(verdict="proven_wrong",contradiction_dimension="operation")
        self.review["claims"][0]["claim"]["operation"]="rename_requested"
        self.analysis_bytes=json.dumps(self.analysis).encode()
        self.review["analysis_sha256"]=hashlib.sha256(self.analysis_bytes).hexdigest()
        result=self.verify()
        self.assertEqual(result["native_layer"]["proven_wrong"],1)
        self.assertEqual(result["native_layer"]["independent_supported"],0)
        self.assertIsNone(result["native_layer"]["precision"])

    def test_cache_origin_must_also_name_the_same_resource(self):
        primary={e["id"]:e for e in self.analysis["primary_records"]}
        event=primary[self.extra_id]
        origin=next(e for e in primary.values() if e["id"]!=self.extra_id and e["event_type"]=="file")
        origin["raw"].update(file_object=event["raw"]["file_object"],object_generation=event["raw"]["object_generation"],event_id=30)
        event["raw"]["file_diagnostics"]["cache_origin"]=dict(source_event_id=origin["id"],event_timestamp=origin["timestamp_ticks"])
        self.assertFalse(VERIFY.name_row(event,primary)["origin_reference_verified"])

    def test_supplied_provider_name_uses_this_event_not_old_cache_context(self):
        primary={e["id"]:e for e in self.analysis["primary_records"]}
        event=next(e for e in primary.values() if e["operation"]=="rename_requested")
        old=next(e for e in primary.values() if e["operation"]=="write_requested")
        event["resource"]="C:\\fixture\\renamed.dat"
        diagnostic=event["raw"]["file_diagnostics"]
        diagnostic.update(path_source="supplied_FilePath",truncated_fields=[],
            cache_origin=dict(source_event_id=old["id"],event_timestamp=old["timestamp_ticks"]),
            fields=dict(FilePath=dict(status="parsed",value="\\Device\\SyntheticVolume\\fixture\\renamed.dat")))
        row=VERIFY.name_row(event,primary)
        self.assertEqual(row["source_observation_id"],event["id"])
        self.assertEqual(row["source_observed_ticks"],event["timestamp_ticks"])
        self.assertTrue(row["origin_reference_verified"])
        self.assertEqual(row["claim_scope"],"provider_name_at_observation")
        self.assertEqual(row["cache_context_origin"]["source_event_id"],old["id"])
        self.assertIsNone(row["verified_current_path"])
        self.assertFalse(row["continuous_file_identity_proven"])

    def test_supplied_name_cannot_hide_invalid_compact_reference_or_missing_field(self):
        primary={e["id"]:e for e in self.analysis["primary_records"]}
        event=primary[self.extra_id]
        event["raw"]["file_diagnostics"].update(path_source="supplied_FilePath",fields={})
        self.assertFalse(VERIFY.name_row(event,primary)["origin_reference_verified"])
        event["raw"]["file_name"]=dict(path_source="supplied_FilePath",source_event_id="missing",observed_ticks=event["timestamp_ticks"])
        self.assertFalse(VERIFY.name_row(event,primary)["origin_reference_verified"])

    def test_failed_calls_remain_separate_and_do_not_change_success_denominator(self):
        call=self.analysis["fixture_journal"][1]
        call.update(success=False,api_return="Err",error=5,error_kind="PermissionDenied")
        result=self.verify(False)
        self.assertEqual(result["api_layer"]["total_calls"],16)
        self.assertEqual(result["api_layer"]["successful_calls"],15)
        self.assertEqual(result["api_layer"]["failed_calls"],1)

    def test_empty_positives_never_reports_perfect_precision(self):
        for e in self.analysis["final_view_events"]:e["confidence"]="Unknown"
        result=self.verify(False)
        self.assertEqual(result["native_layer"]["positive_requests"],0)
        self.assertIsNone(result["native_layer"]["precision"])
        self.assertIsNone(result["native_layer"]["independent_supported_rate"])

    def test_orphan_final_positive_and_certain_are_not_dropped(self):
        orphan=copy.deepcopy(self.analysis["final_view_events"][0])
        orphan.update(id="missing-primary-request",confidence="Certain")
        self.analysis["final_view_events"].append(orphan)
        result=self.verify(False)
        self.assertEqual(result["native_layer"]["positive_requests"],18)
        self.assertEqual(result["native_layer"]["unresolved"],18)
        self.assertIn(orphan["id"],[r["event_id"] for r in result["native_layer"]["requests"]])

    def test_unavailable_clock_does_not_become_zero_coverage(self):
        self.analysis["clock"]["same_machine_boot_provenance_verified"]=False
        result=self.verify(False)["api_layer"]
        self.assertEqual(result["successful_calls"],16)
        self.assertIsNone(result["observation_coverage"])
        self.assertIsNone(result["matched_successful_calls"])

    def test_invalid_independent_call_clock_is_not_evaluable(self):
        self.analysis["clock"]["truth_rows"][0].update(independent_clock="invalid",reason="trace_fixture_frequency_mismatch")
        result=self.verify(False)["api_layer"]
        self.assertIsNone(result["observation_coverage"])
        self.assertEqual(result["evaluable_successful_calls"],15)

    def test_conflicting_api_outcome_is_unavailable_without_changing_denominator(self):
        self.analysis["fixture_journal"][1].update(api_return="Err",error=5)
        result=self.verify(False)["api_layer"]
        self.assertEqual(result["successful_calls"],16)
        self.assertIsNone(result["effective_attribution_coverage"])

    def identity_rows(self):
        call=self.analysis["fixture_journal"][1]
        session=dict(kind="write_identity_observer_session",schema_version=1,
            variant="transient_metadata_handles_v1",actor=copy.deepcopy(call["actor"]),
            qpc_frequency=call["clock"]["qpc_frequency"],provenance=copy.deepcopy(call["clock"]["provenance"]),
            max_observations=28,path_buffer_chars=4096)
        tick=int(call["clock"]["end_qpc"])+1
        def sample(method,**values):return dict(method=method,start_qpc=str(tick),end_qpc=str(tick),success=True,error=None,**values)
        row=dict(kind="write_identity_observation",schema_version=1,variant=session["variant"],order=1,
            case="A",file_index=0,stage="after_write",after_api_order=call["order"],actor=call["actor"],
            qpc_frequency=session["qpc_frequency"],requested_path=call["resource"],requested_path_kind="observer_open_argument",
            open=sample("OpenOptions access FILE_READ_ATTRIBUTES, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT",access=128,share_mode=7),
            file_identity=sample("GetFileInformationByHandleEx(FileIdInfo)",volume_serial_number="12",file_id_128_hex="12"*16),
            volume=sample("GetVolumeInformationByHandleW",filesystem="NTFS",label="synthetic",format_serial_number_32="12",
                          maximum_component_length=255,filesystem_flags=1),
            observer_opened_name=sample("GetFinalPathNameByHandleW",flags=9,name=call["resource"],returned_chars=len(call["resource"]),
                capacity_chars=4096,buffer_limit_exceeded=False,name_source="this transient observer handle; not an ETW name or original write-handle name"),
            close=sample("CloseHandle"),handle_retained_across_workload_call=False,end_qpc=str(tick))
        row["observer_normalized_name"]=dict(row["observer_opened_name"],flags=1)
        return [session,row]

    def test_transient_identity_keeps_sampled_names_separate_without_continuity(self):
        result=VERIFY.identity_observations(self.identity_rows(),self.analysis)
        self.assertEqual(result["valid_observations"],1)
        self.assertFalse(result["complete_expected_plan"])
        self.assertFalse(result["continuous_file_identity_proven"])
        self.assertEqual(result["observations"][0]["recorded"]["volume"]["filesystem"],"NTFS")

    def test_identity_open_failure_and_real_error_are_retained(self):
        rows=self.identity_rows()
        rows[1]["open"].update(success=False,error=2)
        for name in ("file_identity","volume","observer_opened_name","observer_normalized_name","close"):rows[1][name]=None
        result=VERIFY.identity_observations(rows,self.analysis)
        self.assertEqual(result["valid_observations"],1)
        self.assertEqual(result["open_failures"],1)
        self.assertEqual(result["observations"][0]["recorded"]["open"]["error"],2)

    def test_identity_hold_actor_stage_and_clock_provenance_fail_closed(self):
        for change in (lambda rows:rows[1].update(handle_retained_across_workload_call=True),
                       lambda rows:rows[1]["close"].update(end_qpc="99999"),
                       lambda rows:rows[1]["actor"].update(creation_time="1001"),
                       lambda rows:rows[1].update(stage="after_delete"),
                       lambda rows:rows[0]["provenance"].update(boot_identifier_le_hex="f"*32)):
            rows=copy.deepcopy(self.identity_rows());change(rows)
            result=VERIFY.identity_observations(rows,self.analysis)
            self.assertEqual(result["valid_observations"],0)
            self.assertFalse(result["continuous_file_identity_proven"])

    def test_source_recomputation_rejects_fabricated_derived_clock(self):
        raw=self.fixture.raw_bytes()
        analysis=self.fixture.analyze()
        analysis["all_file_request_chains"][0]["request_original_qpc"]["original_qpc"]="999"
        with self.assertRaisesRegex(ValueError,"derived_analysis_mismatch"):
            VERIFY.recompute_sources(analysis,self.fixture.capture,self.fixture.journal,self.fixture.summary,raw,None)

    def test_missing_raw_source_preserves_denominators_and_unavailable_clock(self):
        analysis=VERIFY.recompute_sources(self.analysis,self.fixture.capture,self.fixture.journal,self.fixture.summary,None,None)
        result=VERIFY.verify(analysis)
        self.assertEqual(result["native_layer"]["positive_requests"],17)
        self.assertEqual(result["api_layer"]["successful_calls"],16)
        self.assertIsNone(result["api_layer"]["effective_attribution_coverage"])

    def test_primary_final_request_identity_conflict_cannot_support(self):
        final=next(row for row in self.analysis["final_view_events"] if row["id"]==self.extra_id)
        for field in ("irp","file_object","file_key","object_generation"):
            original=final["raw"][field];final["raw"][field]="other"
            result=self.verify()
            self.assertEqual(result["native_layer"]["independent_supported"],0)
            self.assertEqual(result["native_layer"]["positive_requests"],17)
            final["raw"][field]=original
        final["raw"]["file_name"]=dict(path_source="supplied_FileName",source_event_id="invented-origin")
        self.assertEqual(self.verify()["native_layer"]["independent_supported"],0)

    def test_zero_stack_slots_do_not_claim_usable_stack(self):
        record=self.native["chains"][0]["stack_record"]
        record["payload"]=record["payload"][:32]+"00"*16
        self.assertEqual(self.verify()["native_layer"]["independent_supported"],0)

    def test_cli_source_hash_recompute_and_exclusive_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            raw=self.fixture.raw_bytes()
            sources=dict(capture=json.dumps(self.fixture.capture).encode(),
                         operations=b"\n".join(json.dumps(row).encode() for row in self.fixture.journal),
                         summary=json.dumps(self.fixture.summary).encode(),raw_clock=raw)
            analysis=self.fixture.analyze()
            analysis["inputs_sha256"]={key:hashlib.sha256(data).hexdigest() for key,data in sources.items()}
            paths={key:root/(key+".json") for key in sources}
            for key,data in sources.items():paths[key].write_bytes(data)
            (root/"analysis.json").write_text(json.dumps(analysis))
            command=[sys.executable,str(Path(VERIFY.__file__)),"--analysis",str(root/"analysis.json"),"--output",str(root/"result.json")]
            for key,path in paths.items():command.extend(["--"+key.replace("_","-"),str(path)])
            result=subprocess.run(command,capture_output=True,text=True)
            self.assertEqual(result.returncode,0,result.stderr)
            produced=(root/"result.json").read_bytes()
            self.assertTrue(json.loads(produced)["derived_clock_and_completion_recomputed"])
            repeated=subprocess.run(command,capture_output=True,text=True)
            self.assertNotEqual(repeated.returncode,0)
            self.assertEqual((root/"result.json").read_bytes(),produced)
            sources["capture"]+=b" "
            paths["capture"].write_bytes(sources["capture"])
            command[command.index("--output")+1]=str(root/"other.json")
            changed=subprocess.run(command,capture_output=True,text=True)
            self.assertNotEqual(changed.returncode,0)
            self.assertFalse((root/"other.json").exists())


if __name__=="__main__":unittest.main()
