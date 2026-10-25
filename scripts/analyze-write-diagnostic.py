"""Read-only eight-file evidence audit. No scoring, clock tolerance or repair pass.

Exit zero means an exclusive-create report was written, including unavailable
reports. It never means that capture, attribution or release acceptance passed.
"""
import argparse
from collections import Counter, defaultdict
import hashlib
import importlib.util
import json
from pathlib import Path


SPEC = importlib.util.spec_from_file_location("write_clock_protocol", Path(__file__).with_name("v04-clock-protocol.py"))
CLOCK = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CLOCK)
PROTOCOL = "contain-write-diagnostic-analysis-1"
FILE_PROVIDER = "edd08927-9cc4-4e65-b970-c2560fb5c289"
PLANS = {"A": ["write"], "B": ["write", "rename"], "C": ["write", "delete"], "D": ["write", "rename", "delete"]}
API = {"write": ("write_requested", "std::fs::write"), "rename": ("rename_requested", "std::fs::rename"),
       "delete": ("delete_requested", "std::fs::remove_file")}
INPUT_LIMIT = 128 * 1024 * 1024
RECORD_LIMIT = 100_000


def obj(value):
    return value if isinstance(value, dict) else {}


def norm(value):
    return CLOCK.normalize(value, "") if isinstance(value, str) else ""


def digest(data):
    return hashlib.sha256(data).hexdigest()


def load_bytes(path, limit=INPUT_LIMIT):
    path, info = CLOCK.checked_local_file(path)
    if info.st_size > limit:
        raise ValueError("input_byte_budget_exceeded")
    with path.open("rb") as handle:
        data = handle.read(limit + 1)
    if len(data) > limit:
        raise ValueError("input_byte_budget_exceeded")
    return data


def fixture_audit(journal, summary):
    if not isinstance(journal, list) or not all(isinstance(row, dict) for row in journal):
        raise ValueError("fixture journal must contain objects")
    sessions = [row for row in journal if row.get("kind") == "write_diagnostic_session"]
    calls = [row for row in journal if row.get("kind") == "write_diagnostic_api_call"]
    issues = []
    if len(sessions) != 1:
        issues.append("fixture_session_missing_or_duplicated")
    elif any(sessions[0].get(key) != value or type(sessions[0].get(key)) is not int
             for key, value in (("schema_version", 1), ("files_per_case", 2), ("workers", 1), ("expected_api_calls", 16))):
        issues.append("fixture_session_plan_invalid")
    groups = defaultdict(list)
    for call in calls:
        key = call.get("case"), call.get("file_index")
        if key[0] not in PLANS or type(key[1]) is not int or key[1] not in (0, 1):
            issues.append("fixture_sequence_identity_invalid")
            continue
        groups[key].append(call)
        action = call.get("action")
        if (type(call.get("schema_version")) is not int or call["schema_version"] != 1
                or API.get(action) != (call.get("operation"), call.get("api"))
                or call.get("phase") != "workload" or type(call.get("success")) is not bool):
            issues.append("fixture_operation_schema_invalid")
        who = obj(call.get("actor"))
        if (who.get("pid") != call.get("pid") or who.get("creation_time") != call.get("creation_time")
                or who.get("thread_id") != obj(call.get("clock")).get("thread_id")):
            issues.append("fixture_actor_fields_disagree_or_missing")
        if call.get("success") is False:
            issues.append("fixture_api_failure")
        if (call.get("success") is True and (call.get("api_return") != "Ok(())" or
                call.get("error") is not None or call.get("error_kind") is not None)) or (
                call.get("success") is False and (call.get("api_return") != "Err" or not isinstance(call.get("error_kind"), str))):
            issues.append("fixture_api_outcome_conflict")
    orders = [row.get("order") for row in calls]
    if any(type(order) is not int for order in orders) or orders != list(range(1, len(calls) + 1)):
        issues.append("fixture_order_missing_or_duplicated")
    for case, actions in PLANS.items():
        for index in (0, 1):
            if [row.get("action") for row in groups[case, index]] != actions:
                issues.append("fixture_sequence_incomplete_or_reordered")
    if len(calls) != 16 or summary.get("recorded_api_calls") != len(calls):
        issues.append("fixture_call_count_mismatch")
    if summary.get("failures") != []:
        issues.append("fixture_summary_failure_or_missing")
    if summary.get("kind") != "write_diagnostic_summary" or any(summary.get(key) != value
            or type(summary.get(key)) is not int for key, value in
            (("schema_version", 1), ("planned_files", 8), ("files_per_case", 2), ("workers", 1), ("expected_api_calls", 16))):
        issues.append("fixture_summary_plan_invalid")
    checks = summary.get("final_checks")
    if (not isinstance(checks, list) or len(checks) != 8
            or any(not isinstance(row, dict) or not isinstance(row.get("paths"), list)
                   or len(row["paths"]) != 2
                   or any(obj(path).get("matches_expected") is not True for path in row["paths"])
                   for row in checks)):
        issues.append("fixture_final_checks_incomplete_or_failed")
    elif Counter((row.get("case"), row.get("file_index")) for row in checks) != Counter((case, index) for case in PLANS for index in (0, 1)):
        issues.append("fixture_final_check_sequence_missing_or_duplicated")
    return calls, groups, sorted(set(issues))


def all_clock_links(capture, raw_bytes, raw_path, clock_report):
    """Reuse protocol-4 validation, then its exact-key helpers for other records.

    The old protocol only reports mutation-request links. Open/close/completion
    kinds remain unchanged here; no event is relabelled to enter its source set.
    A second bounded binary scan reuses the existing duplicate/hash/size checks.
    """
    detail = obj(clock_report.get("raw_companion"))
    result = {}
    if detail.get("status") != "exact_original_key_links_available":
        return result, "raw_companion_unavailable"
    try:
        document = json.loads(raw_bytes.decode("utf-8-sig"))
        if document["schema_version"] == 2:
            wanted = {int(CLOCK.event_key(obj(obj(event.get("raw")).get("clock")).get("event_key")), 16)
                      for event in capture["events"] if obj(obj(event.get("raw")).get("clock"))}
            index, _, _ = CLOCK.binary_record_index(document, raw_bytes, raw_path, wanted)
        else:
            index = {CLOCK.event_key(row["event_key"]):
                     (CLOCK.integer(row["header_timestamp"]), CLOCK.record_identity(row))
                     for row in document["records"]}
        for event in capture["events"]:
            clock = obj(obj(event.get("raw")).get("clock"))
            entry = dict(status="unavailable", original_qpc=None)
            try:
                key = CLOCK.event_key(clock["event_key"])
                if clock.get("timestamp_domain") != "filetime_100ns" or clock.get("raw_qpc") is not None:
                    raise ValueError("primary_clock_domain_unexpected")
                if key not in index:
                    raise ValueError("primary_event_key_missing_from_companion")
                tick, descriptor = index[key]
                if CLOCK.record_identity(clock["record_identity"]) != descriptor:
                    raise ValueError("raw_companion_exact_descriptor_mismatch")
                entry.update(status="exact_original_key_link", original_qpc=str(tick))
            except (KeyError, TypeError, ValueError) as error:
                entry["reason"] = str(error)
            result[event["id"]] = entry
        return result, None
    except (KeyError, TypeError, ValueError, AttributeError, OSError) as error:
        return {}, str(error)


def actor(event):
    if obj(event.get("dimensions")).get("actor") != "High":
        return None
    try:
        return CLOCK.identity(event, True)
    except (KeyError, TypeError, ValueError):
        return None


def interval_relation(event, call, link, row_clock, global_clock_ready):
    value = dict(order=call.get("order"), case=call.get("case"), file_index=call.get("file_index"),
                 actual_call_operation=call.get("operation"), actual_api=call.get("api"),
                 api_success=call.get("success"), api_error=call.get("error"), relation="unavailable",
                 same_actor=None, same_operation=event.get("operation") == call.get("operation"),
                 resource_equals_call_input=norm(event.get("resource")) == norm(call.get("resource")),
                 resource_equals_call_destination=bool(call.get("destination")) and
                 norm(event.get("resource")) == norm(call.get("destination")),
                 actor_comparison_scope="recorded High actor instance versus independent PID and creation time; lifetime resolver not rerun")
    who = actor(event)
    try:
        expected = CLOCK.identity(call)
        independent_actor = obj(call.get("actor"))
        if (who is not None and independent_actor.get("pid") == expected[0]
                and CLOCK.integer(independent_actor.get("creation_time")) == expected[1]
                and independent_actor.get("thread_id") == obj(call.get("clock")).get("thread_id")):
            value["same_actor"] = who == expected
    except (KeyError, TypeError, ValueError):
        pass
    if not global_clock_ready or link.get("status") != "exact_original_key_link":
        value["reason"] = "original_clock_or_native_provenance_unavailable"
    elif (row_clock.get("independent_clock") != "valid" or row_clock.get("status") == "invalid"
          or row_clock.get("reason") in {"trace_fixture_frequency_mismatch", "fixture_outside_recorded_trace_qpc_window"}):
        value["reason"] = row_clock.get("reason", "independent_call_clock_unavailable")
    else:
        tick = int(link["original_qpc"])
        start, end = int(row_clock["start_qpc"]), int(row_clock["end_qpc"])
        value.update(relation="before" if tick < start else "after" if tick > end else "inside",
                     start_margin_qpc=str(tick - start), end_margin_qpc=str(end - tick))
    return value


def event_paths(event):
    raw = obj(event.get("raw"))
    diagnostic = obj(raw.get("file_diagnostics"))
    fields = obj(diagnostic.get("fields"))
    return {norm(path) for path in (event.get("resource"), diagnostic.get("cache_name"),
            obj(fields.get("FileName")).get("value"), obj(fields.get("FilePath")).get("value")) if norm(path)}


def capture_integrity(capture, files):
    backend, stats = obj(capture.get("backend")), obj(capture.get("stats"))
    checks = {}
    for group_name, group, fields in (
        ("backend", backend, ("dropped_events", "context_losses", "decode_errors", "etw_events_lost", "etw_buffers_lost")),
        ("stats", stats, ("events_dropped",)),
        ("pipeline", obj(backend.get("pipeline")), ("queue_overflow", "enqueue_disconnected", "queue_pending", "context_evictions", "retention_dropped", "failed_without_output")),
        ("handoff", obj(backend.get("handoff")), ("overflow", "oversized", "disconnected", "serialize_failed", "pending")),
        ("stream", obj(backend.get("stream")), ("failed", "quota_dropped")),
        ("file_diagnostics", obj(backend.get("file_diagnostics")), ("parse_errors", "truncated_records", "budget_dropped", "oversized_records", "context_samples_omitted")),
    ):
        for key in fields:
            value = group.get(key)
            checks[group_name + "." + key] = dict(actual=value, required=0, satisfied=type(value) is int and value == 0)
    for name, actual, required in (
        ("file_provider", backend.get("etw_file"), "active"),
        ("process_provider", backend.get("etw_process"), "active"),
        ("drain_timed_out", stats.get("drain_timed_out"), False),
        ("receiver_finished", obj(backend.get("handoff")).get("receiver_finished"), True),
        ("writer_finished", obj(backend.get("handoff")).get("writer_finished"), True),
        ("stream_error", obj(backend.get("stream")).get("error", "missing"), None),
        ("file_diagnostics_enabled", obj(backend.get("file_diagnostics")).get("enabled"), True),
    ):
        checks[name] = dict(actual=actual, required=required, satisfied=type(actual) is type(required) and actual == required)
    missing = [event["id"] for event in files if not isinstance(obj(event.get("raw")).get("file_diagnostics"), dict)]
    checks["file_diagnostic_records_missing"] = dict(actual=len(missing), required=0, satisfied=not missing)
    checks["source_file_records_present"] = dict(actual=len(files), required="greater_than_zero", satisfied=bool(files))
    return dict(negative_observation_complete=all(row["satisfied"] for row in checks.values()), checks=checks,
                file_diagnostic_record_ids_missing=missing, counters=backend.get("file_diagnostics"),
                scope="Prerequisites for describing absence as not-reproduced only; known positive records remain visible. First-N omitted context samples do not establish complete negative coverage.")


def write_classification(event, view):
    raw = obj(event.get("raw"))
    descriptor = obj(obj(raw.get("clock")).get("record_identity"))
    verified = (descriptor.get("provider") == FILE_PROVIDER and descriptor.get("version") in (0, 1)
                and type(descriptor.get("version")) is int and
                tuple(descriptor.get(key) for key in ("event_id", "opcode", "task")) == (16, 0, 16)
                and raw.get("event_id") == 16 and event.get("event_type") == "file"
                and event.get("operation") == "write_requested" and obj(event.get("evidence")).get("source") == "EtwFile")
    high = (verified and obj(view.get("evidence")).get("source") == "EtwFile"
            and view.get("confidence") == "High" and view.get("success") is True
            and all(obj(view.get("dimensions")).get(key) == "High" for key in ("actor", "resource", "operation")))
    return dict(event_id=event["id"], raw_id16=raw.get("event_id") == 16,
                descriptor_verified_write=verified, recorded_high_write=high, descriptor=descriptor,
                recorded_source=obj(event.get("evidence")).get("source"), recorded_confidence=view.get("confidence"))


def analyze(capture, journal, summary, raw_bytes=None, raw_path=None):
    if not isinstance(capture, dict) or not isinstance(summary, dict):
        raise ValueError("capture and summary must be objects")
    envelope = None
    if "data" in capture:
        envelope = {key: value for key, value in capture.items() if key != "data"}
        capture = capture["data"]
        if not isinstance(capture, dict):
            raise ValueError("CLI capture data must be an object")
    primary = capture.get("raw_events", capture.get("events"))
    if (not isinstance(primary, list) or len(primary) > RECORD_LIMIT
            or not all(isinstance(row, dict) and isinstance(row.get("id"), str) for row in primary)):
        raise ValueError("primary records missing, malformed or over budget")
    calls, groups, fixture_issues = fixture_audit(journal, summary)
    if not calls:
        raise ValueError("no independent API calls")
    final_events = capture.get("events", [])
    if not isinstance(final_events, list) or not all(isinstance(event, dict) for event in final_events):
        raise ValueError("final view must be a list of objects")
    issues = list(fixture_issues)
    if len({event["id"] for event in primary}) != len(primary):
        issues.append("duplicate_primary_event_id")
    finals = defaultdict(list)
    for event in final_events:
        finals[event.get("id")].append(event)
    views = {}
    for event in primary:
        matches = finals[event["id"]]
        if len(matches) > 1:
            issues.append("duplicate_final_event_id")
        final = matches[0] if len(matches) == 1 else None
        if final and (any(final.get(key) != event.get(key) for key in ("event_type", "operation", "resource", "timestamp_ticks"))
                      or obj(final.get("raw")).get("clock") != obj(event.get("raw")).get("clock")):
            issues.append("raw_final_fact_conflict")
        views[event["id"]] = final or event
    projected = dict(capture, events=primary)
    try:
        clock_report = CLOCK.analyze(projected, calls, raw_clock_bytes=raw_bytes, raw_clock_path=raw_path)
    except (KeyError, TypeError, ValueError, AttributeError) as error:
        clock_report = dict(status="unavailable", source_reasons=[str(error)], truth_rows=[],
                            same_machine_boot_provenance_verified=False)
    links, link_error = all_clock_links(projected, raw_bytes, raw_path, clock_report)
    ready = (not link_error and clock_report.get("same_machine_boot_provenance_verified") is True
             and not [reason for reason in clock_report.get("source_reasons", [])
                      if reason != "source_original_qpc_or_actor_missing"]
             and not any(reason in issues for reason in ("duplicate_primary_event_id", "duplicate_final_event_id", "raw_final_fact_conflict")))
    row_clocks = clock_report.get("truth_rows", [])
    if len(row_clocks) != len(calls) or any(row.get("independent_clock") != "valid" or
            row.get("status") == "invalid" or row.get("reason") in
            {"trace_fixture_frequency_mismatch", "fixture_outside_recorded_trace_qpc_window"} for row in row_clocks):
        issues.append("independent_call_clock_invalid_or_outside_trace")
    relations = {}
    files = [event for event in primary if event.get("event_type") == "file"]
    integrity = capture_integrity(capture, files)
    if not integrity["negative_observation_complete"]:
        issues.append("source_or_diagnostic_coverage_incomplete")
    completions = defaultdict(list)
    for event in primary:
        if event.get("event_type") == "completion":
            completions[obj(event.get("raw")).get("related_event")].append(event)
    chains = []
    for event in files:
        view = views[event["id"]]
        link = links.get(event["id"], dict(status="unavailable", original_qpc=None))
        relations[event["id"]] = [interval_relation(view, call, link,
            row_clocks[index] if index < len(row_clocks) else {}, ready) for index, call in enumerate(calls)]
        attached = completions[event["id"]]
        chain = dict(request_id=event["id"], source_operation=event.get("operation"), request_original_qpc=link, completions=[],
                     completion_status="missing" if not attached else "ambiguous" if len(attached) != 1 else "unavailable")
        for completion in attached:
            completion_link = links.get(completion["id"], dict(status="unavailable", original_qpc=None))
            relation = [interval_relation(completion, call, completion_link,
                row_clocks[index] if index < len(row_clocks) else {}, ready) for index, call in enumerate(calls)]
            descriptor = obj(obj(obj(completion.get("raw")).get("clock")).get("record_identity"))
            raw, end = obj(event.get("raw")), obj(completion.get("raw"))
            same_irp = isinstance(raw.get("irp"), str) and raw.get("irp") == end.get("irp")
            exact_end = (descriptor.get("provider") == FILE_PROVIDER and
                         tuple(descriptor.get(key) for key in ("event_id", "version", "opcode", "task")) == (24, 0, 0, 24))
            ordered = (link.get("original_qpc") is not None and completion_link.get("original_qpc") is not None
                       and int(link["original_qpc"]) <= int(completion_link["original_qpc"]))
            status = end.get("status")
            valid_status = type(status) is int and 0 <= status <= 0xFFFFFFFF
            succeeded = type(status) is int and 0 <= status < 0x80000000 and completion.get("success") is True
            outcome_conflict = valid_status and type(completion.get("success")) is bool and (
                (status < 0x80000000) != completion["success"])
            chain["completions"].append(dict(event_id=completion["id"], original_qpc=completion_link,
                same_irp=same_irp, operation_end_descriptor=exact_end, request_before_completion=ordered,
                status=status, recorded_success=completion.get("success"), call_relations=relation,
                actor_inherited_from_request=False))
            if len(attached) == 1:
                chain["completion_status"] = ("conflict" if not same_irp or not exact_end or outcome_conflict or (ordered is False and
                    link.get("original_qpc") is not None and completion_link.get("original_qpc") is not None) else
                    "successful_recorded_chain" if ready and ordered and succeeded else
                    "failed_recorded_chain" if ready and ordered and valid_status and status >= 0x80000000
                    and completion.get("success") is False else "unavailable")
        chains.append(chain)
    sequence_results = []
    associated = set()
    for case in PLANS:
        for file_index in (0, 1):
            group = groups[case, file_index]
            paths = {norm(row.get(name)) for row in group for name in ("resource", "destination")} - {""}
            seeds = [event for event in files if event_paths(event) & paths]
            objects = {(obj(event.get("raw")).get("file_object"), obj(event.get("raw")).get("object_generation"))
                       for event in seeds if obj(event.get("raw")).get("file_object") and obj(event.get("raw")).get("object_generation")}
            selected = [event for event in files if event_paths(event) & paths or
                        (obj(event.get("raw")).get("file_object"), obj(event.get("raw")).get("object_generation")) in objects]
            ids = {event["id"] for event in selected}
            associated.update(ids)
            writes = [event for event in selected if obj(event.get("raw")).get("event_id") == 16]
            classifications = [write_classification(event, views[event["id"]]) for event in writes]
            qualified = {row["event_id"] for row in classifications if row["recorded_high_write"]}
            candidates = []
            unknown = []
            for event in writes:
                for relation in relations[event["id"]]:
                    if (relation["case"], relation["file_index"]) == (case, file_index) and relation["actual_call_operation"] == "delete_requested":
                        if relation["relation"] == "inside" and relation["same_actor"] is True and event["id"] in qualified:
                            candidates.append(event["id"])
                        elif relation["relation"] == "unavailable" or (relation["relation"] == "inside" and
                                (relation["same_actor"] is None or event["id"] not in qualified)):
                            unknown.append(event["id"])
            has_delete = any(row.get("action") == "delete" for row in group)
            write_call_observed = any(classification["recorded_high_write"] and any(
                relation["relation"] == "inside" and relation["same_actor"] is True and relation["same_operation"]
                and relation["resource_equals_call_input"] and (relation["case"], relation["file_index"]) == (case, file_index)
                for relation in relations[classification["event_id"]]) for classification in classifications)
            outcome = ("not_applicable_no_delete_call" if not has_delete else
                       "unavailable" if not ready else "observed_delete_window_write" if candidates else
                       "unavailable" if unknown or fixture_issues or not write_call_observed or not integrity["negative_observation_complete"] else
                       "not_reproduced_not_a_fix_pass")
            sequence_results.append(dict(case=case, file_index=file_index, independent_calls=group,
                resource_paths=sorted(paths), association_rule="exact recorded name or same FileObject AND generation; not ownership",
                request_event_ids=[event["id"] for event in selected], write_event_ids=[event["id"] for event in writes],
                write_classifications=classifications, original_write_call_observed=write_call_observed,
                chains=[chain for chain in chains if chain["request_id"] in ids],
                name_observations=[dict(event_id=event["id"], recorded_resource=event.get("resource"),
                    equals_rename_old=any(row.get("action") == "rename" and norm(event.get("resource")) == norm(row.get("resource")) for row in group),
                    equals_rename_new=any(row.get("action") == "rename" and norm(event.get("resource")) == norm(row.get("destination")) for row in group),
                    file_diagnostics=obj(event.get("raw")).get("file_diagnostics"),
                    current_path_inferred=False) for event in selected],
                event_call_relations={event["id"]: relations[event["id"]] for event in selected},
                delete_window_write=dict(status=outcome, event_ids=sorted(set(candidates)),
                                         unavailable_event_ids=sorted(set(unknown)), fix_pass=None)))
    return dict(schema_version=1, protocol=PROTOCOL, clock_protocol=CLOCK.PROTOCOL_REVISION,
        status="evidence_available_for_review" if ready and not issues else "unavailable_or_incomplete",
        release_gate=None, fix_pass=None, attribution_scored=False, legacy_score_recomputed=False, tolerance_qpc=0,
        issues=sorted(set(issues)), original_clock_link_error=link_error, clock=clock_report,
        primary_records_source="raw_events" if "raw_events" in capture else "events_only_no_raw_stream_export",
        capture_envelope=envelope, observation_integrity=integrity,
        primary_records=primary, final_view_events=final_events, process_lifetimes=capture.get("processes"),
        capture_backend=capture.get("backend"), capture_stats=capture.get("stats"), capture_quality=capture.get("quality"),
        fixture_journal=journal, fixture_summary=summary, sequences=sequence_results,
        all_file_request_chains=chains, unassigned_file_request_ids=[event["id"] for event in files if event["id"] not in associated],
        orphan_completion_ids=[event["id"] for key, values in completions.items() if key not in {e["id"] for e in files} for event in values],
        counts=dict(primary_records=len(primary), file_records_including_open_close_context=len(files), fixture_calls=len(calls),
                    successful_fixture_calls=sum(call.get("success") is True for call in calls)),
        definition="Exact raw-key/QPC interval observations do not establish a general emission contract, causality, current resource name, final ownership or safe deletion. A recorded old name, payload name and cache provenance remain separate fields. Missing clocks and actors remain unavailable; no FILETIME inversion or tolerance. All original records and failed calls remain included.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("capture", "operations", "summary", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--raw-clock", type=Path)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError("output already exists; choose a new private report path")
    paths = {name: getattr(args, name) for name in ("capture", "operations", "summary", "raw_clock") if getattr(args, name)}
    data = {name: load_bytes(path) for name, path in paths.items()}
    journal = [json.loads(line) for line in data["operations"].decode("utf-8-sig").splitlines() if line.strip()]
    result = analyze(json.loads(data["capture"].decode("utf-8-sig")), journal,
        json.loads(data["summary"].decode("utf-8-sig")), data.get("raw_clock"), args.raw_clock)
    hashes = {name: digest(value) for name, value in data.items()}
    if any(digest(load_bytes(paths[name])) != value for name, value in hashes.items()):
        raise ValueError("input changed during read-only analysis")
    result["inputs_sha256"] = hashes
    result["input_hashes_unchanged"] = True
    result["analyzer_sha256"] = digest(Path(__file__).read_bytes())
    result["clock_protocol_sha256"] = digest(Path(__file__).with_name("v04-clock-protocol.py").read_bytes())
    output = json.dumps(result, indent=2, allow_nan=False).encode("utf-8") + b"\n"
    if len(output) > INPUT_LIMIT:
        raise ValueError("report byte budget exceeded; no partial report written")
    with args.output.open("xb") as handle:
        handle.write(output)
    print(json.dumps(dict(status=result["status"], output=str(args.output), release_gate=None, fix_pass=None)))


if __name__ == "__main__":
    main()
