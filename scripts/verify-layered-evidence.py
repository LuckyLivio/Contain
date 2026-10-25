"""Separate API, native-request and name evidence; never replace legacy scores.

This versioned offline protocol is a bounded verifier of an existing write
diagnostic analysis and an optional, explicitly hash-pinned human review bundle.
It is not a general ETL decoder, an automatic trust authority or a release gate.
An API journal cannot independently prove a native request, and a successful
native request cannot prove a current path or final-state ownership.
"""
import argparse
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct


SPEC = importlib.util.spec_from_file_location("layered_write_audit", Path(__file__).with_name("analyze-write-diagnostic.py"))
AUDIT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(AUDIT)
PROTOCOL = "contain-layered-evidence-1"
REVIEW_PROTOCOL = "contain-layered-review-1"
CLASSIC = "90cbdc39-4a3e-11d1-84f4-0000f80464e3"
STACK = "def2fe46-7bd6-4b80-bd94-f57fe20d0ce3"
MUTATIONS = {"write_requested", "rename_requested", "delete_requested", "create_new_file"}
LIMIT = 128 * 1024 * 1024
MAX_RECORDS = 100_000
MAX_REVIEW_BYTES = 4 * 1024 * 1024


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def obj(value):
    return value if isinstance(value, dict) else {}


def integer(value):
    require(type(value) is int or isinstance(value, str) and bool(re.fullmatch(r"[0-9]+", value)), "invalid_unsigned_integer")
    result = int(value)
    require(0 <= result < 2**64, "integer_out_of_range")
    return result


def actor(event):
    evidence = obj(event.get("evidence"))
    try:
        require(type(evidence.get("pid")) is int, "actor_pid_missing")
        return evidence["pid"], integer(evidence["process_creation_time"])
    except (KeyError, TypeError, ValueError):
        return None


def unique(rows, key, label):
    require(isinstance(rows, list) and len(rows) <= MAX_RECORDS, label + "_invalid_or_over_budget")
    require(all(isinstance(row, dict) and (isinstance(row.get(key), str) or type(row.get(key)) is int) for row in rows), label + "_malformed")
    result = {row[key]: row for row in rows}
    require(len(result) == len(rows), label + "_duplicate_id")
    return result


def pointer(document, path):
    require(isinstance(path, str) and path.startswith("/") and len(path) <= 1024, "invalid_evidence_pointer")
    for token in path[1:].split("/"):
        require(not re.search(r"~(?![01])", token), "invalid_evidence_pointer_escape")
        token = token.replace("~1", "/").replace("~0", "~")
        if isinstance(document, list):
            require(bool(re.fullmatch(r"0|[1-9][0-9]*", token)), "invalid_evidence_array_index")
            index = int(token)
            require(index < len(document), "evidence_pointer_missing")
            document = document[index]
        else:
            require(isinstance(document, dict) and token in document, "evidence_pointer_missing")
            document = document[token]
    return document


def fact_consistent(primary, final):
    return (all(primary.get(key) == final.get(key) for key in ("event_type", "operation", "resource", "timestamp_ticks"))
            and all(obj(primary.get("raw")).get(key) == obj(final.get("raw")).get(key)
                    for key in ("clock", "event_id", "irp", "file_object", "file_key", "object_generation", "file_diagnostics", "file_name")))


def high_positive(event):
    return event.get("confidence") in {"High", "Certain"} and event.get("success") is True and obj(event.get("evidence")).get("source") == "EtwFile"


def raw_tick(chain):
    link = obj(chain.get("request_original_qpc"))
    require(link.get("status") == "exact_original_key_link", "original_clock_unavailable")
    return integer(link.get("original_qpc"))


def api_layers(analysis, primary, finals, chains, calls):
    rows, correspondence = [], {}
    clock_ready = (obj(analysis.get("clock")).get("same_machine_boot_provenance_verified") is True
                   and analysis.get("original_clock_link_error") is None
                   and not [reason for reason in obj(analysis.get("clock")).get("source_reasons", [])
                            if reason != "source_original_qpc_or_actor_missing"])
    clock_rows = obj(analysis.get("clock")).get("truth_rows", [])
    for index, call in enumerate(calls.values()):
        observed, matched = [], []
        reason = None
        evaluable = False
        try:
            require(clock_ready, "clock_or_boot_provenance_unavailable")
            require(type(call.get("success")) is bool, "independent_outcome_missing")
            require((call["success"] and call.get("api_return") == "Ok(())" and call.get("error") is None
                     and call.get("error_kind") is None) or
                    (not call["success"] and call.get("api_return") == "Err" and isinstance(call.get("error_kind"), str)),
                    "independent_outcome_conflict")
            clock = call["clock"]
            start, end = integer(clock["start_qpc"]), integer(clock["end_qpc"])
            require(start <= end and integer(clock["qpc_frequency"]) > 0, "invalid_call_interval")
            require(len(clock_rows) == len(calls), "independent_clock_validation_missing")
            checked = clock_rows[index]
            require(checked.get("independent_clock") == "valid" and checked.get("status") != "invalid"
                    and checked.get("reason") not in {"trace_fixture_frequency_mismatch", "fixture_outside_recorded_trace_qpc_window"}
                    and (integer(checked.get("start_qpc")), integer(checked.get("end_qpc")), integer(checked.get("qpc_frequency")))
                    == (start, end, integer(clock["qpc_frequency"])), "independent_call_clock_unavailable")
            who = (call["pid"], integer(call["creation_time"]))
            require((obj(call.get("actor")).get("pid"), integer(obj(call.get("actor")).get("creation_time"))) == who,
                    "independent_actor_conflict")
            require(AUDIT.API.get(call.get("action")) == (call.get("operation"), call.get("api")), "independent_operation_schema_conflict")
            evaluable = True
            for event_id, event in primary.items():
                if event.get("event_type") != "file" or event.get("operation") != call.get("operation"):
                    continue
                paths = {AUDIT.norm(call.get("resource")), AUDIT.norm(call.get("destination"))} - {""}
                if AUDIT.norm(event.get("resource")) not in paths:
                    continue
                chain = chains.get(event_id, {})
                try:
                    tick = raw_tick(chain)
                except ValueError:
                    continue
                if not start <= tick <= end:
                    continue
                observed.append(event_id)
                final = finals.get(event_id, event)
                if (fact_consistent(event, final) and high_positive(final) and actor(final) == who
                        and obj(final.get("dimensions")).get("actor") in {"High", "Certain"}
                        and chain.get("completion_status") == "successful_recorded_chain"):
                    ends = chain.get("completions", [])
                    if len(ends) == 1:
                        link = obj(ends[0].get("original_qpc"))
                        if link.get("status") == "exact_original_key_link" and start <= integer(link.get("original_qpc")) <= end:
                            matched.append(event_id)
                            if call.get("success") is True:
                                correspondence.setdefault(event_id, []).append(call["order"])
            if not observed:
                reason = "no_same_operation_resource_source_in_exact_call_interval"
            elif not matched:
                reason = "source_observed_but_actor_completion_or_final_facts_not_supported"
        except (KeyError, TypeError, ValueError) as error:
            evaluable = False
            reason = str(error)
        rows.append(dict(order=call["order"], operation=call.get("operation"), success=call.get("success"),
                         error=call.get("error"), observed_event_ids=observed, matched_event_ids=matched,
                         evaluable=evaluable,
                         failed_call_successful_claims=matched if call.get("success") is False else [], reason=reason))
    successful = [row for row in rows if row["success"] is True]
    observed = sum(bool(row["observed_event_ids"]) for row in successful)
    matched = sum(bool(row["matched_event_ids"]) for row in successful)
    evaluated = sum(row["evaluable"] for row in successful)
    complete = evaluated == len(successful)
    return dict(total_calls=len(rows), successful_calls=len(successful), failed_calls=sum(row["success"] is False for row in rows),
                evaluable_successful_calls=evaluated, unavailable_successful_calls=len(successful)-evaluated,
                observed_successful_calls=observed if complete else None, matched_successful_calls=matched if complete else None,
                observed_successful_calls_lower_bound=observed, matched_successful_calls_lower_bound=matched,
                observation_coverage=observed / len(successful) if successful and complete else None,
                effective_attribution_coverage=matched / len(successful) if successful and complete else None,
                denominator="All independently logged successful API calls; failed calls retained separately",
                rows=rows), correspondence


def binary_record(record, provider, opcode, version, length=None):
    require(isinstance(record, dict), "independent_record_missing")
    require(all(type(record.get(key)) is int for key in ("id", "opcode", "version", "task")), "independent_descriptor_type_invalid")
    require((record.get("provider"), record.get("id"), record.get("opcode"), record.get("version"), record.get("task"))
            == (provider, 0, opcode, version, 0), "independent_descriptor_mismatch")
    text = record.get("payload")
    require(isinstance(text, str) and len(text) <= 4096 and bool(re.fullmatch(r"(?:[0-9a-fA-F]{2})*", text)), "independent_payload_invalid")
    payload = bytes.fromhex(text)
    require(type(record.get("length")) is int and record["length"] == len(payload), "independent_payload_length_mismatch")
    require(length is None or len(payload) == length, "independent_schema_length_mismatch")
    return payload


def same_run_bridge(bundle, documents, document, analysis):
    binding = obj(bundle.get("run_binding"))
    artifacts = unique(bundle.get("artifacts"), "id", "review_artifacts")
    outer_id, child_id = binding.get("observer_attempt_artifact"), binding.get("child_attempt_artifact")
    require(outer_id in documents and child_id in documents and outer_id in artifacts and child_id in artifacts,
            "same_execution_artifacts_missing")
    outer, child = documents[outer_id], documents[child_id]
    child_path = artifacts[child_id].get("path")
    require(isinstance(child_path, str) and AUDIT.norm(obj(outer.get("child_state")).get("attempt_path")) == AUDIT.norm(child_path),
            "observer_child_execution_binding_mismatch")
    analysis_path = binding.get("analysis_path")
    require(isinstance(analysis_path, str) and AUDIT.norm(str(Path(child_path).parent)) == AUDIT.norm(str(Path(analysis_path).parent)),
            "child_analysis_execution_binding_mismatch")
    require(child.get("capture_exit") == 0 and outer.get("child_exit") == 0, "child_execution_not_completed")
    collector = outer.get("expected_collector")
    require(isinstance(collector, str) and collector, "observer_collector_identity_missing")
    source_paths = {AUDIT.norm(row.get("path")) for row in outer.get("retained_observer_files", [])
                    if isinstance(row, dict) and isinstance(row.get("path"), str)
                    and Path(row["path"]).name == collector + ".etl"}
    snapshots = document.get("source_snapshots_after", [])
    require(len(source_paths) == 1 and any(isinstance(row, dict) and AUDIT.norm(row.get("path")) in source_paths
        and row.get("unchanged") is True and bool(re.fullmatch(r"[a-fA-F0-9]{64}", str(row.get("sha256")))) for row in snapshots),
        "independent_source_same_execution_binding_missing")
    metadata = obj(obj(analysis.get("capture_backend")).get("clock"))
    source = obj(document.get("source_header"))
    headers = (obj(metadata.get("trace_header")), obj(obj(metadata.get("raw_companion")).get("header")))
    require(integer(source.get("boot_time")) > 0 and all(integer(row.get("boot_time")) == integer(source["boot_time"])
        and integer(row.get("perf_frequency")) == integer(source.get("perf_freq")) for row in headers),
        "independent_boot_or_frequency_bridge_mismatch")
    require(obj(analysis.get("clock")).get("same_machine_boot_provenance_verified") is True, "primary_fixture_provenance_unavailable")


def supported_witness(review, document, event, final, chain, calls):
    """Version1 deliberately supports only the actually audited native schema.

    Payload equality here is explicitly limited to manifest id16/v1 and classic
    FileIo opcode68/v3, each independently decoded as eight recorded fields. It
    does not apply another provider's generic schema or reinterpret operation.
    """
    require(review.get("name_scope") == "historical_open_name", "name_scope_not_proven_by_native_stack")
    require(event.get("operation") == "write_requested", "independent_schema_only_proves_write")
    require(chain.get("completion_status") == "successful_recorded_chain", "primary_completion_not_verified")
    ends = chain.get("completions", [])
    require(len(ends) == 1 and ends[0].get("event_id") == review["claim"].get("completion_id"), "completion_reference_mismatch")
    witness = pointer(document, review["witness_ref"]["pointer"])
    request, completion, stack = (witness.get(name) for name in ("classic_request", "classic_completion", "stack_record"))
    native = struct.unpack("<QQQQIIII", binary_record(request, CLASSIC, 68, 3, 48))
    end_irp, extra, status = struct.unpack("<QQI", binary_record(completion, CLASSIC, 76, 3, 20))
    stack_payload = binary_record(stack, STACK, 32, 2)
    require(len(stack_payload) >= 24 and (len(stack_payload) - 16) % 8 == 0, "stack_payload_incomplete")
    trigger, pid, tid = struct.unpack_from("<QII", stack_payload)
    addresses = struct.unpack_from("<" + "Q" * ((len(stack_payload)-16)//8), stack_payload, 16)
    require(any(addresses), "independent_stack_has_no_recorded_address")
    require((trigger, pid, tid) == (integer(request["qpc"]), request.get("pid"), request.get("tid")), "stack_exact_request_link_missing")
    raw = obj(event.get("raw"))
    descriptor = obj(obj(raw.get("clock")).get("record_identity"))
    require((descriptor.get("provider"), descriptor.get("event_id"), descriptor.get("version"), descriptor.get("opcode"), descriptor.get("task"))
            == (AUDIT.FILE_PROVIDER, 16, 1, 0, 16), "primary_write_descriptor_mismatch")
    diagnostic = obj(raw.get("file_diagnostics"))
    require(diagnostic.get("payload_truncated") is False and diagnostic.get("payload_bytes") == 48
            and obj(diagnostic.get("schema")).get("truncated") is False
            and obj(diagnostic.get("schema")).get("invalid_layout") is False, "primary_payload_schema_incomplete")
    payload = bytes.fromhex(diagnostic.get("payload_prefix_hex", ""))
    require(len(payload) == 48 and struct.unpack("<QQQQIIII", payload) == native, "independent_native_fields_disagree")
    fields = obj(diagnostic.get("fields"))
    for name, expected in zip(("ByteOffset", "Irp", "FileObject", "FileKey", "IssuingThreadId", "IOSize", "IOFlags", "ExtraFlags"), native):
        require(obj(fields.get(name)).get("status") == "parsed" and integer(fields[name].get("value")) == expected,
                "primary_decoded_field_disagrees_" + name)
    for key, expected in (("irp", native[1]), ("file_object", native[2]), ("file_key", native[3])):
        require(isinstance(raw.get(key), str) and re.fullmatch(r"(?:0x)?[0-9a-fA-F]+", raw[key])
                and int(raw[key], 16) == expected, "primary_pointer_field_disagrees_" + key)
    require(end_irp == native[1] and status == 0 and completion.get("pid") == pid and completion.get("tid") == tid,
            "independent_completion_identity_or_outcome_conflict")
    require(request.get("tid") == native[4] == review["claim"]["thread_id"] and request.get("pid") == review["claim"]["pid"],
            "independent_actor_fields_disagree")
    require(type(review.get("actor_call_order")) is int and review["actor_call_order"] in calls, "independent_actor_call_missing")
    call = calls[review["actor_call_order"]]
    require((call.get("pid"), integer(call.get("creation_time"))) == actor(final)
            and obj(call.get("actor")).get("thread_id") == tid, "independent_actor_instance_mismatch")
    clock = call["clock"]
    start, end = integer(clock["start_qpc"]), integer(clock["end_qpc"])
    require(start <= integer(request["qpc"]) <= integer(completion["qpc"]) <= end, "independent_request_outside_reviewed_call")
    require(start <= raw_tick(chain) <= integer(ends[0]["original_qpc"]["original_qpc"]) <= end, "primary_request_outside_reviewed_call")
    header = obj(document.get("source_header"))
    require(header.get("reserved_flags_clock") == 1 and integer(header.get("perf_freq")) == integer(clock["qpc_frequency"]),
            "independent_clock_basis_unavailable")
    return dict(events_lost=header.get("events_lost"), buffers_lost=header.get("buffers_lost")), dict(
        witness_ref=review["witness_ref"], stack_address_slots=len(addresses), nonzero_stack_addresses=sum(bool(v) for v in addresses),
        exact_stack_link=True, current_path_proven=False, actual_bytes_changed=None,
        symbols_resolved_by_verifier=False, stack_mechanism_interpretation="Separate prior review; this verifier checks recorded address linkage only",
        completion_extra_information=str(extra), actor_call_order=review["actor_call_order"])


def name_row(event, primary):
    raw = obj(event.get("raw"))
    diagnostic = obj(raw.get("file_diagnostics"))
    compact = obj(raw.get("file_name"))
    origin = obj(diagnostic.get("cache_origin"))
    source = compact.get("path_source", diagnostic.get("path_source"))
    source_id = compact.get("source_event_id", origin.get("source_event_id"))
    ticks = compact.get("observed_ticks", origin.get("event_timestamp"))
    valid_origin = False
    if source_id in primary:
        observed = primary[source_id]
        try:
            valid_origin = (integer(ticks) == integer(observed.get("timestamp_ticks")) <= integer(event.get("timestamp_ticks"))
                and obj(observed.get("raw")).get("file_object") == raw.get("file_object")
                and obj(observed.get("raw")).get("object_generation") == raw.get("object_generation")
                and AUDIT.norm(observed.get("resource")) == AUDIT.norm(event.get("resource"))
                and (source != "file_object_cache" or obj(observed.get("raw")).get("event_id") in (12, 30)))
        except ValueError:
            pass
    return dict(event_id=event["id"], recorded_resource=event.get("resource"), name_source=source,
                source_observation_id=source_id, source_observed_ticks=ticks, origin_reference_verified=valid_origin,
                claim_scope="historical_open_name" if source == "file_object_cache" else
                    "provider_name_at_observation" if source in {"supplied_FileName", "supplied_FilePath"} else "unavailable",
                verified_current_path=None, continuous_file_identity_proven=False, final_ownership_proven=False,
                reason="Source names and sampled observer-handle names do not establish a unique current path or final ownership")


def identity_observations(records, analysis):
    """Validate bounded transient samples, without joining identity across gaps."""
    require(isinstance(records, list) and 1 <= len(records) <= 29
            and all(isinstance(row, dict) for row in records), "identity_sidecar_invalid_or_over_budget")
    session, samples = records[0], records[1:]
    calls = unique([row for row in analysis["fixture_journal"] if row.get("kind") == "write_diagnostic_api_call"], "order", "calls")
    expected = {}
    for call in calls.values():
        key = (call["case"], call["file_index"])
        later = next((row for row in calls.values() if row["order"] > call["order"]
                      and (row["case"], row["file_index"]) == key), None)
        stages = [("after_write", call["resource"])] if call["action"] == "write" else (
            [("after_rename_old_path", call["resource"]), ("after_rename_new_path", call["destination"])]
            if call["action"] == "rename" else [("after_delete", call["resource"])])
        if later and later["action"] in {"rename", "delete"}:
            stages.append(("before_" + later["action"], later["resource"]))
        for stage, path in stages:
            expected[(call["order"], stage)] = path
    session_error = None
    try:
        require(session.get("kind") == "write_identity_observer_session" and session.get("schema_version") == 1
                and session.get("variant") == "transient_metadata_handles_v1" and session.get("max_observations") == 28
                and session.get("path_buffer_chars") == 4096, "identity_session_schema_invalid")
        require(integer(session.get("qpc_frequency")) > 0, "identity_session_frequency_invalid")
        provenance, _ = AUDIT.CLOCK.native_provenance_sample(session.get("provenance"), "fixture_clock_initialized")
    except (KeyError, TypeError, ValueError) as error:
        session_error = str(error)
        provenance = None
    rows, seen, previous_end = [], set(), None
    for position, sample in enumerate(samples, 1):
        error = None
        try:
            require(session_error is None, session_error)
            require(sample.get("kind") == "write_identity_observation" and sample.get("schema_version") == 1
                    and sample.get("variant") == "transient_metadata_handles_v1" and type(sample.get("order")) is int
                    and sample["order"] == position, "identity_observation_schema_or_order_invalid")
            slot = (sample.get("after_api_order"), sample.get("stage"))
            require(slot in expected and slot not in seen, "identity_stage_missing_or_duplicated")
            seen.add(slot)
            call = calls[slot[0]]
            require((sample.get("case"), sample.get("file_index")) == (call["case"], call["file_index"])
                    and sample.get("requested_path_kind") == "observer_open_argument"
                    and AUDIT.norm(sample.get("requested_path")) == AUDIT.norm(expected[slot]), "identity_call_or_path_binding_mismatch")
            expected_actor = obj(call.get("actor"))
            for who in (obj(sample.get("actor")), obj(session.get("actor"))):
                require(type(who.get("pid")) is int and who["pid"] == expected_actor.get("pid")
                        and integer(who.get("creation_time")) == integer(expected_actor.get("creation_time"))
                        and type(who.get("thread_id")) is int and who["thread_id"] == expected_actor.get("thread_id"),
                        "identity_actor_instance_mismatch")
            native, _ = AUDIT.CLOCK.native_provenance_sample(call["clock"].get("provenance"), "fixture_clock_initialized")
            require(native == provenance and integer(sample.get("qpc_frequency")) == integer(session["qpc_frequency"])
                    == integer(call["clock"]["qpc_frequency"]), "identity_clock_provenance_mismatch")
            opened = obj(sample.get("open"))
            require(opened.get("method") == "OpenOptions access FILE_READ_ATTRIBUTES, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT"
                    and opened.get("access") == 128 and opened.get("share_mode") == 7, "identity_open_semantics_invalid")
            start, cursor = integer(opened.get("start_qpc")), integer(opened.get("end_qpc"))
            end = integer(sample.get("end_qpc"))
            require(integer(call["clock"]["end_qpc"]) <= start <= cursor <= end
                    and (previous_end is None or previous_end <= start), "identity_observation_interval_invalid")
            next_call = next((row for row in calls.values() if row["order"] > call["order"]), None)
            require(next_call is None or end <= integer(next_call["clock"]["start_qpc"]), "identity_handle_crosses_workload_boundary")
            previous_end = end
            require(type(opened.get("success")) is bool, "identity_open_outcome_missing")
            fields = ("file_identity", "volume", "observer_opened_name", "observer_normalized_name")
            if opened["success"]:
                require(opened.get("error") is None, "identity_open_outcome_conflict")
                for name, method in zip(fields, ("GetFileInformationByHandleEx(FileIdInfo)", "GetVolumeInformationByHandleW",
                                                "GetFinalPathNameByHandleW", "GetFinalPathNameByHandleW")):
                    query = obj(sample.get(name))
                    require(query.get("method") == method and type(query.get("success")) is bool, "identity_query_schema_invalid")
                    lo, hi = integer(query.get("start_qpc")), integer(query.get("end_qpc"))
                    require(cursor <= lo <= hi <= end, "identity_query_outside_open_handle_interval")
                    cursor = hi
                    if query["success"]:
                        require(query.get("error") is None, "identity_query_outcome_conflict")
                        if name == "file_identity":
                            integer(query.get("volume_serial_number"))
                            require(bool(re.fullmatch(r"[0-9a-f]{32}", str(query.get("file_id_128_hex")))), "identity_file_id_invalid")
                        elif name == "volume":
                            require(isinstance(query.get("filesystem"), str) and query["filesystem"]
                                    and isinstance(query.get("label"), str), "identity_volume_fields_invalid")
                            require(integer(query.get("format_serial_number_32")) < 2**32, "identity_volume_serial_invalid")
                        else:
                            require(isinstance(query.get("name"), str) and query["name"], "identity_queried_name_missing")
                    else:
                        require(type(query.get("error")) is int or query.get("buffer_limit_exceeded") is True,
                                "identity_query_failure_reason_missing")
                    if name.startswith("observer_"):
                        count, capacity = integer(query.get("returned_chars")), integer(query.get("capacity_chars"))
                        require(query.get("flags") == (9 if name == "observer_opened_name" else 1) and capacity == 4096
                                and query.get("buffer_limit_exceeded") is (count >= capacity)
                                and query["success"] is (0 < count < capacity)
                                and (query["success"] or query.get("name") is None), "identity_name_query_flags_or_buffer_invalid")
                closed = obj(sample.get("close"))
                require(closed.get("method") == "CloseHandle" and closed.get("success") is True and closed.get("error") is None,
                        "identity_handle_close_not_verified")
                require(cursor <= integer(closed.get("start_qpc")) <= integer(closed.get("end_qpc")) <= end,
                        "identity_close_interval_invalid")
            else:
                require(type(opened.get("error")) is int and all(sample.get(name) is None for name in fields + ("close",)),
                        "identity_failed_open_has_queries_or_missing_error")
            require(sample.get("handle_retained_across_workload_call") is False, "identity_handle_retention_not_excluded")
        except (KeyError, TypeError, ValueError) as failure:
            error = str(failure)
        rows.append(dict(order=sample.get("order"), valid=error is None, reason=error, recorded=sample))
    valid = sum(row["valid"] for row in rows)
    complete = valid == len(rows) == len(expected) == 28 and seen == set(expected)
    return dict(status="complete_consistent_observations" if complete else "incomplete_or_inconsistent_observations",
                session_error=session_error, recorded_observations=len(rows), valid_observations=valid,
                expected_observations=28, complete_expected_plan=complete,
                open_failures=sum(obj(row.get("open")).get("success") is False for row in samples),
                observations=rows, session=session, continuous_file_identity_proven=False,
                current_path_proven=False, final_ownership_proven=False,
                scope="Each sample describes only this transient observer handle at its query intervals; equal sampled IDs do not bridge closed-handle gaps")


def recompute_sources(analysis, capture, journal, summary, raw_bytes, raw_path):
    recomputed = AUDIT.analyze(capture, journal, summary, raw_bytes, raw_path)
    fields = ("clock", "original_clock_link_error", "all_file_request_chains", "capture_backend", "observation_integrity")
    if raw_bytes is not None:
        for key in fields:
            require(recomputed.get(key) == analysis.get(key), "derived_analysis_mismatch_" + key)
        return analysis
    # Missing evidence does not erase recorded positives or manufacture a zero
    # coverage result. Recompute unavailable links, preserving historical scores.
    current = dict(analysis)
    current.update({key: recomputed.get(key) for key in fields})
    current["source_revalidation"] = "raw_clock_source_unavailable; historical derived links not reused"
    return current


def verify(analysis, review=None, documents=None, analysis_bytes=None):
    require(isinstance(analysis, dict) and analysis.get("protocol") == AUDIT.PROTOCOL and analysis.get("schema_version") == 1,
            "unsupported_analysis_protocol")
    primary = unique(analysis.get("primary_records"), "id", "primary")
    finals = unique(analysis.get("final_view_events"), "id", "final")
    chains = unique(analysis.get("all_file_request_chains"), "request_id", "chains")
    journal = analysis.get("fixture_journal")
    require(isinstance(journal, list) and all(isinstance(row, dict) for row in journal), "journal_invalid")
    calls = unique([row for row in journal if row.get("kind") == "write_diagnostic_api_call"], "order", "calls")
    api, correspondence = api_layers(analysis, primary, finals, chains, calls)
    positives, others = [], []
    retained = dict(primary)
    retained.update({event_id: event for event_id, event in finals.items() if event_id not in primary})
    for event_id, event in retained.items():
        final = finals.get(event_id, event)
        if (event.get("event_type") != "file" and final.get("event_type") != "file") or (event.get("operation") not in MUTATIONS and not high_positive(final)):
            continue
        row = dict(event_id=event_id, operation=event.get("operation"), recorded_confidence=final.get("confidence"),
                   recorded_success=final.get("success"), api_correspondence_orders=correspondence.get(event_id, []),
                   status="unresolved", reason="no_reviewed_independent_native_request_witness", independent_loss=None,
                   fact_consistent=event_id in primary and fact_consistent(event, final), evidence_ref=None,
                   primary_record_available=event_id in primary)
        if event_id not in primary:
            row["reason"] = "primary_record_unavailable_final_claim_retained"
        (positives if high_positive(final) else others).append(row)
    claimed = {row["event_id"]: row for row in positives}
    rejected = []
    if review is not None:
        require(isinstance(review, dict) and review.get("protocol") == REVIEW_PROTOCOL and review.get("reviewed") is True,
                "review_protocol_or_review_state_invalid")
        require(isinstance(review.get("review_id"), str) and bool(review["review_id"].strip()), "review_identity_missing")
        require(isinstance(analysis_bytes, bytes) and review.get("analysis_sha256") == hashlib.sha256(analysis_bytes).hexdigest(), "review_analysis_hash_mismatch")
        reviews = unique(review.get("claims"), "event_id", "review_claims")
        for event_id, item in reviews.items():
            try:
                require(event_id in claimed, "review_targets_missing_or_nonpositive_request")
                event, final, row = primary[event_id], finals.get(event_id, primary[event_id]), claimed[event_id]
                require(row["fact_consistent"], "raw_final_fact_conflict")
                fact = item.get("claim")
                require(isinstance(fact, dict), "review_claim_facts_missing")
                who = actor(final)
                require(who is not None and (fact.get("pid"), integer(fact.get("creation_time"))) == who, "review_actor_instance_mismatch")
                require(fact.get("operation") == event.get("operation") and fact.get("resource") == event.get("resource"), "review_operation_or_resource_mismatch")
                descriptor = obj(obj(obj(event.get("raw")).get("clock")).get("record_identity"))
                require(type(fact.get("thread_id")) is int and fact["thread_id"] == descriptor.get("thread_id"), "review_thread_mismatch")
                ref = item.get("witness_ref")
                require(isinstance(ref, dict) and ref.get("artifact") in (documents or {}), "review_artifact_missing")
                document = documents[ref["artifact"]]
                require(isinstance(item.get("reason"), str) and bool(item["reason"].strip()), "review_reason_missing")
                verdict = item.get("verdict")
                if verdict == "independent_supported":
                    same_run_bridge(review, documents, document, analysis)
                    loss, details = supported_witness(item, document, event, final, chains.get(event_id, {}), calls)
                    details["clock_provenance_scope"] = "Pinned same-execution observer/child bindings and equal boot/frequency headers; observer has no separate machine identifier"
                    row.update(status=verdict, reason=item["reason"], evidence_ref=details, independent_loss=loss)
                elif verdict == "proven_wrong":
                    # Version1 only proves operation-label contradictions through
                    # the same independently decoded native request. Actor or
                    # current-vs-historical name differences need other evidence.
                    require(item.get("contradiction_dimension") == "operation"
                            and event.get("operation") != "write_requested", "contradiction_not_proven_at_same_layer")
                    same_run_bridge(review, documents, document, analysis)
                    loss, details = supported_witness(item, document, dict(event, operation="write_requested"), final,
                                                      chains.get(event_id, {}), calls)
                    details.update(claimed_operation=event.get("operation"), independently_observed_operation="write_requested")
                    row.update(status=verdict, reason=item["reason"], evidence_ref=details, independent_loss=loss)
                else:
                    raise ValueError("review_verdict_invalid")
            except (KeyError, TypeError, ValueError, struct.error) as error:
                rejected.append(dict(event_id=event_id, reason=str(error)))
                if event_id in claimed:
                    claimed[event_id]["reason"] = "review_rejected: " + str(error)
    counts = Counter(row["status"] for row in positives)
    total = len(positives)
    return dict(protocol=PROTOCOL, schema_version=1, formal_acceptance=None, legacy_scores_modified=False,
                status="layered_partial_evidence" if not rejected else "layered_partial_evidence_with_rejected_reviews",
                api_layer=api, native_layer=dict(positive_requests=total, independent_supported=counts["independent_supported"],
                    proven_wrong=counts["proven_wrong"], unresolved=counts["unresolved"], all_positive_requests_accounted=sum(counts.values()) == total,
                    independent_supported_rate=counts["independent_supported"] / total if total else None,
                    unresolved_rate=counts["unresolved"] / total if total else None,
                    proven_wrong_fraction=counts["proven_wrong"] / total if total else None,
                    precision=None, false_attribution_rate=None,
                    denominator="Every retained High or Certain successful EtwFile file request, regardless of case, API relation, size or flags; orphan final claims retained",
                    precision_unavailable_reason="Independent native ground truth is non-exhaustive; resolved-only precision is not an overall metric",
                    requests=positives, retained_nonpositive_requests=others,
                    high_or_certain_without_success_requests=sum(row["recorded_confidence"] in {"High", "Certain"}
                                                                  and row["recorded_success"] is not True for row in others),
                    unknown_confidence_requests=sum(row["recorded_confidence"] == "Unknown" for row in others)),
                name_resource_layer=dict(requests=[name_row(retained[row["event_id"]], primary) for row in positives + others],
                                         transient_observations=None, current_path_proofs=0),
                rejected_reviews=rejected, historical_analysis_issues=analysis.get("issues"),
                historical_observation_integrity=analysis.get("observation_integrity"),
                trust="Review assertions are separately hash-pinned and source-bound, not independently authenticated signatures")


def load_json(path, limit=LIMIT):
    data = AUDIT.load_bytes(path, limit)
    return data, json.loads(data.decode("utf-8-sig"))


def file_digest(path):
    path, info = AUDIT.CLOCK.checked_local_file(path)
    require(info.st_size <= LIMIT, "input_byte_budget_exceeded")
    digest, total = hashlib.sha256(), 0
    with path.open("rb") as handle:
        while chunk := handle.read(256 * 1024):
            total += len(chunk)
            require(total <= LIMIT, "input_byte_budget_exceeded")
            digest.update(chunk)
    require(total == info.st_size, "input_size_changed_during_read")
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("analysis", "capture", "operations", "summary", "output"):
        parser.add_argument("--" + key, type=Path, required=True)
    parser.add_argument("--raw-clock", type=Path)
    parser.add_argument("--review", type=Path)
    parser.add_argument("--review-sha256")
    parser.add_argument("--identity-observations", type=Path)
    parser.add_argument("--identity-sha256")
    args = parser.parse_args()
    require(not args.output.exists(), "output_exists_choose_new_private_path")
    analysis_bytes, analysis = load_json(args.analysis)
    tracked = {args.analysis: hashlib.sha256(analysis_bytes).hexdigest()}
    inputs = {}
    for key in ("capture", "operations", "summary", "raw_clock"):
        path = getattr(args, key)
        if key == "raw_clock" and (path is None or not path.exists()):
            inputs[key] = None
            continue
        data = AUDIT.load_bytes(path)
        digest = hashlib.sha256(data).hexdigest()
        require(obj(analysis.get("inputs_sha256")).get(key) == digest, "source_input_hash_mismatch_" + key)
        tracked[path] = digest
        inputs[key] = data
    capture = json.loads(inputs["capture"].decode("utf-8-sig"))
    capture = capture.get("data", capture)
    journal = [json.loads(line) for line in inputs["operations"].decode("utf-8-sig").splitlines() if line.strip()]
    require(capture.get("raw_events", capture.get("events")) == analysis.get("primary_records")
            and capture.get("events") == analysis.get("final_view_events") and journal == analysis.get("fixture_journal")
            and json.loads(inputs["summary"].decode("utf-8-sig")) == analysis.get("fixture_summary"), "analysis_embedded_source_mismatch")
    raw_bytes = inputs["raw_clock"]
    raw_unavailable = "raw_clock_source_missing" if raw_bytes is None else None
    if raw_bytes is not None:
        manifest = json.loads(raw_bytes.decode("utf-8-sig"))
        if manifest.get("schema_version") == 2:
            name = obj(manifest.get("binary")).get("file_name")
            require(name == args.raw_clock.name + ".rows.bin", "raw_companion_binary_file_name_invalid")
            binary_path = args.raw_clock.parent / name
            try:
                digest = file_digest(binary_path)
            except OSError:
                raw_bytes = None
                raw_unavailable = "raw_clock_binary_source_unavailable"
            else:
                require(digest == manifest["binary"].get("sha256"), "raw_clock_binary_hash_mismatch")
                tracked[binary_path] = digest
    analysis = recompute_sources(analysis, capture, journal, json.loads(inputs["summary"].decode("utf-8-sig")), raw_bytes, args.raw_clock)
    review, documents = None, {}
    require(bool(args.review) == bool(args.review_sha256), "review_and_explicit_hash_pin_required_together")
    if args.review:
        data, review = load_json(args.review, MAX_REVIEW_BYTES)
        require(hashlib.sha256(data).hexdigest() == args.review_sha256, "review_hash_pin_mismatch")
        tracked[args.review] = args.review_sha256
        require(AUDIT.norm(obj(review.get("run_binding")).get("analysis_path")) == AUDIT.norm(str(args.analysis.absolute())),
                "review_analysis_path_binding_mismatch")
        artifacts = unique(review.get("artifacts"), "id", "review_artifacts")
        require(len(artifacts) <= 16, "review_artifact_budget_exceeded")
        for artifact_id, artifact in artifacts.items():
            path = Path(artifact["path"])
            path = path if path.is_absolute() else args.review.parent / path
            data, document = load_json(path)
            digest = hashlib.sha256(data).hexdigest()
            require(digest == artifact.get("sha256"), "review_artifact_hash_mismatch")
            tracked[path] = digest
            documents[artifact_id] = document
    result = verify(analysis, review, documents, analysis_bytes)
    require(bool(args.identity_observations) == bool(args.identity_sha256), "identity_sidecar_and_explicit_hash_pin_required_together")
    if args.identity_observations:
        data = AUDIT.load_bytes(args.identity_observations, MAX_REVIEW_BYTES)
        digest = hashlib.sha256(data).hexdigest()
        require(digest == args.identity_sha256, "identity_sidecar_hash_pin_mismatch")
        tracked[args.identity_observations] = digest
        observations = [json.loads(line) for line in data.decode("utf-8-sig").splitlines() if line.strip()]
        result["name_resource_layer"]["transient_observations"] = identity_observations(observations, analysis)
    for path, digest in tracked.items():
        require(file_digest(path) == digest, "input_changed_during_analysis")
    result.update(inputs_sha256={str(path): digest for path, digest in tracked.items()}, input_hashes_unchanged=True,
                  raw_clock_source_unavailable_reason=raw_unavailable,
                  derived_clock_and_completion_recomputed=True,
                  verifier_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    output = json.dumps(result, indent=2, allow_nan=False).encode() + b"\n"
    require(len(output) <= LIMIT, "output_byte_budget_exceeded")
    with args.output.open("xb") as handle:
        handle.write(output)
    print(json.dumps(dict(protocol=PROTOCOL, status=result["status"], formal_acceptance=None, output=str(args.output))))


if __name__ == "__main__":
    main()
