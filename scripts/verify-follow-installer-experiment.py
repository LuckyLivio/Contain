"""Independent, fail-closed oracle for the bounded installer-following experiment.

Fixture syscall intervals and owned-process creation times are the answers. This
module never supplies paths or identities to Contain, and never recalculates its
attribution. Private captures/journals stay local; the output is a safe aggregate.
"""
import argparse
import hashlib
import json
import ntpath
from pathlib import Path
import re

LABELS = ("parent", "child", "unrelated")
ROLES = dict(parent="followparent", child="followchild", unrelated="followunrelated")
FILES = dict(parent="parent-target.txt", child="child-target.txt", unrelated="unrelated-target.txt")
PROMOTED = {"Certain", "High"}
PLAN = ["independent_fixed_answers", "capture_and_fixture_finished", "no_predetermined_scope_and_runtime_directory", "bounded_follow_admission", "bounded_storage", "self_io_excluded", "finished_saved_session", "no_preset_watch_roots", "actual_sources_and_loss_accounting"]
for _label in LABELS:
    PLAN += [_label + "_independent_write", _label + "_content"]
PLAN += ["distinct_process_instances"]
for _label in ("parent", "child"):
    PLAN += [_label + "_verified_source_write", _label + "_reopened_explanation", _label + "_missing_baseline_explicit"]
PLAN += ["direct_child_parent_instance", "unrelated_never_attributed"]


def normalized(value):
    value = str(value).replace("/", "\\")
    if value.startswith("\\\\?\\"):
        value = value[4:]
    return ntpath.normcase(ntpath.normpath(value))


def document(path, kind):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if data.get("schema_version") != 3 or data.get("kind") != kind or not isinstance(data.get("data"), dict):
        raise ValueError("invalid_document")
    return data["data"]


def decimal(value):
    if not isinstance(value, str) or not value.isdecimal():
        raise ValueError("exact_decimal_string_required")
    return int(value)


def safe_id(value):
    return value if isinstance(value, str) and re.fullmatch(r"[a-zA-Z0-9_-]{1,100}", value) else None


def expected_targets(expected):
    rows = expected.get("targets")
    if not isinstance(rows, list) or [row.get("label") for row in rows] != list(LABELS):
        raise ValueError("fixed_three_answers_required")
    if len({normalized(row.get("path", "")) for row in rows}) != 3:
        raise ValueError("distinct_answers_required")
    for row in rows:
        label = row["label"]
        if row.get("role") != ROLES[label] or ntpath.basename(normalized(row.get("path", ""))) != FILES[label]:
            raise ValueError("fixed_answer_role_and_filename_required")
    return {row["label"]: row for row in rows}


def load_truth(directory):
    rows = []
    for path in sorted(Path(directory).glob("ground-truth-*.jsonl")):
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if line.strip():
                value = json.loads(line)
                if isinstance(value, dict):
                    rows.append(value)
    return rows


def candidates(capture, target):
    return [event for event in capture.get("events", [])
            if isinstance(event, dict) and event.get("event_type") == "file"
            and event.get("evidence", {}).get("source") == "EtwFile"
            and normalized(event.get("resource", "")) == normalized(target["path"])]


def select(capture, expected):
    targets = expected_targets(expected)
    events = []
    for target in targets.values():
        found = candidates(capture, target)
        if len(found) > 32:
            raise ValueError("candidate_bound_exceeded")
        events.extend(found)
    ids = list(dict.fromkeys(event.get("id") for event in events))
    if not all(safe_id(value) for value in ids):
        raise ValueError("invalid_candidate_id")
    return dict(schema_version=1, session_id=capture["id"], candidate_event_ids=ids)


def actor_record(capture, event, oracle):
    evidence = event.get("evidence", {})
    if evidence.get("pid") != oracle["pid"] or decimal(evidence.get("process_creation_time")) != decimal(oracle["creation_time"]):
        return None
    records = [p for p in capture.get("processes", []) if p.get("pid") == oracle["pid"]
               and p.get("creation_time") == oracle["creation_time"]]
    if len(records) != 1:
        return None
    process = records[0]
    at = decimal(event.get("timestamp_ticks"))
    if at < decimal(process["creation_time"]) or process.get("ended_at") is not None and at > decimal(process["ended_at"]):
        return None
    if process.get("confidence") not in PROMOTED or not process.get("image") or not evidence.get("process_image") \
        or normalized(process["image"]) != normalized(evidence["process_image"]):
        return None
    return process


BASELINE_WARNING = "Follow-installer mode records verified source operations only; no installation-before baseline or final-file ownership is established."


def baseline_missing(capture, target):
    # Events-only mode performs no dynamic path reads. The explicit persisted
    # warning must say why no before/after or final ownership is available.
    return capture.get("files") == [] and BASELINE_WARNING in capture.get("warnings", [])


def verify(capture, truth, expected, explanations, source):
    checks = [dict(name=name, status="not_run", reason="not_reached") for name in PLAN]
    observations = []
    session = capture.get("id")
    summary = dict(schema_version=1, protocol="contain-follow-installer-experiment-1", source_sha=source,
                   synthetic=False, controlled_fixture=True, maximum_native_captures=1, retries=0,
                   session_id=safe_id(session), checks=checks, observations=observations,
                   ownership_claim=False, current_path_claim=False, installation_before_baseline=False,
                   post_install_file_reads=False, accepted=False)

    def check(name, predicate, reason):
        try:
            passed = bool(predicate())
        except (ValueError, TypeError, KeyError, OSError, json.JSONDecodeError):
            passed = False
        row = next(row for row in checks if row["name"] == name)
        row.update(status="passed" if passed else "failed", reason=None if passed else reason)
        return passed

    try:
        targets = expected_targets(expected)
    except (ValueError, TypeError, KeyError):
        check("independent_fixed_answers", lambda: False, "invalid_fixed_answers")
        summary.update(passed=0, failed=1, not_run=len(checks)-1, unresolved=list(PLAN))
        return summary
    check("independent_fixed_answers", lambda: True, "invalid_fixed_answers")
    check("capture_and_fixture_finished", lambda: type(expected.get("capture_exit_code")) is int and expected["capture_exit_code"] == 0
          and type(expected.get("independent_exit_code")) is int and expected["independent_exit_code"] == 0 and expected.get("source_sha") == source,
          "capture_or_independent_fixture_not_finished")
    check("no_predetermined_scope_and_runtime_directory", lambda: expected.get("directory_b_existed_before") is False
          and expected.get("product_watch_arguments") == [], "directory_preexisted_or_scope_was_supplied")
    check("finished_saved_session", lambda: capture.get("capture_state") == "finished" and bool(safe_id(session)), "session_not_confirmed_finished")
    check("no_preset_watch_roots", lambda: capture.get("watch_roots") == [], "unexpected_observation_roots")
    backend = capture.get("backend", {})
    follow = backend.get("follow_installer", {})
    summary["follow_counters"] = {key: follow.get(key) for key in (
        "direct_children_only", "baseline_available", "candidate_record_limit", "candidate_byte_limit", "resource_limit",
        "candidates", "admitted", "excluded_other", "unresolved", "excluded_self_io", "candidate_dropped",
        "candidate_high_water_records", "candidate_high_water_bytes")}
    check("bounded_follow_admission", lambda: follow.get("direct_children_only") is True
          and follow.get("baseline_available") is False and follow.get("candidate_record_limit") == 2048
          and follow.get("candidate_byte_limit") == 8388608 and follow.get("resource_limit") == 4096
          and follow.get("candidate_dropped") == 0
          and 0 <= follow.get("candidate_high_water_records", -1) <= 2048
          and 0 <= follow.get("candidate_high_water_bytes", -1) <= 8388608,
          "follow_admission_unbounded_or_dropped")
    stream = backend.get("stream", {})
    summary["storage_counters"] = {key: stream.get(key) for key in ("quota_bytes", "committed_bytes", "persisted", "failed", "quota_dropped")}
    check("bounded_storage", lambda: 0 < stream.get("quota_bytes", 0) <= 67108864
          and 0 <= stream.get("committed_bytes", -1) <= stream["quota_bytes"]
          and stream.get("failed") == 0 and stream.get("quota_dropped") == 0 and stream.get("error") is None,
          "storage_unbounded_dropped_or_failed")
    excluded_paths = expected.get("excluded_io_paths", [])
    excluded_directories = expected.get("excluded_io_directories", [])
    def private_io(event):
        path = normalized(event.get("resource", ""))
        return any(path == normalized(p) + suffix for p in excluded_paths for suffix in ("", "-wal", "-shm", "-journal")) \
            or any(path.startswith(normalized(p).rstrip("\\") + "\\") for p in excluded_directories)
    check("self_io_excluded", lambda: bool(excluded_paths) and bool(excluded_directories)
          and not any(private_io(event) for event in capture.get("events", []) if event.get("event_type") == "file"),
          "collector_database_or_diagnostic_io_retained")
    check("actual_sources_and_loss_accounting", lambda: backend.get("etw_process") == "active"
          and backend.get("etw_file") == "active" and backend.get("etw_events_lost") == 0
          and backend.get("etw_buffers_lost") == 0
          and all(backend.get(k, 0) == 0 for k in ("dropped_events", "decode_errors", "context_losses"))
          and capture.get("stats", {}).get("drain_timed_out") is False,
          "source_unavailable_lost_or_unconfirmed")
    oracles = {}
    for label in LABELS:
        target = targets[label]
        oracle = [row for row in truth if row.get("role") == ROLES[label]
                  and normalized(row.get("resource", "")) == normalized(target["path"])
                  and row.get("operation") == "write_requested" and row.get("success") is True]
        if check(label + "_independent_write", lambda: len(oracle) == 1 and type(oracle[0].get("pid")) is int
                 and decimal(oracle[0]["creation_time"]) > 0
                 and decimal(oracle[0]["start_ticks"]) <= decimal(oracle[0]["end_ticks"]), "oracle_write_missing_or_ambiguous"):
            oracles[label] = oracle[0]
        check(label + "_content", lambda: re.fullmatch(r"[a-f0-9]{64}", target.get("content_sha256", ""))
              and target.get("actual_content_sha256") == target["content_sha256"], "independent_content_check_failed")
    check("distinct_process_instances", lambda: len(oracles) == 3
          and len({(o["pid"], o["creation_time"]) for o in oracles.values()}) == 3, "independent_process_instances_not_distinct")
    selected = {}
    for label in ("parent", "child"):
        target = targets[label]
        oracle = oracles.get(label)
        eligible = []
        for event in candidates(capture, target):
            try:
                evidence = event.get("evidence", {})
                if oracle and event.get("operation") == "write_requested" and event.get("confidence") in PROMOTED \
                    and event.get("dimensions", {}).get("actor") in PROMOTED \
                    and evidence.get("session_id") == session and evidence.get("rule") == ("InstallerPid" if label == "parent" else "DescendantProcess") \
                    and decimal(oracle["start_ticks"]) <= decimal(event["timestamp_ticks"]) <= decimal(oracle["end_ticks"]) \
                    and event.get("raw", {}).get("resource_resolved") is True and event.get("state_validated") is False \
                    and event.get("success") is not False and actor_record(capture, event, oracle):
                    eligible.append(event)
            except (ValueError, TypeError, KeyError):
                continue
        summary.setdefault("candidate_evidence", []).append(dict(label=label, events=[dict(
            event_id=safe_id(event.get("id")), operation=event.get("operation"), confidence=event.get("confidence"),
            pid=event.get("evidence", {}).get("pid"), creation_time=event.get("evidence", {}).get("process_creation_time"),
            rule=event.get("evidence", {}).get("rule"), source=event.get("evidence", {}).get("source"))
            for event in candidates(capture, target)[:32]]))
        eligible.sort(key=lambda event: (decimal(event["timestamp_ticks"]), event["id"]))
        found = check(label + "_verified_source_write", lambda: bool(eligible), "no_source_write_with_exact_oracle_actor_and_interval")
        if found:
            event = selected[label] = eligible[0]
            e = event["evidence"]
            observation = dict(label=label, event_id=safe_id(event["id"]), operation=event["operation"],
                timestamp_ticks=event["timestamp_ticks"], pid=e["pid"], creation_time=e["process_creation_time"],
                rule=e["rule"], confidence=event["confidence"], actor_confidence=event["dimensions"]["actor"],
                source=e["source"], completion=event.get("success"), state_validated=event["state_validated"],
                recorded_name_sha256=hashlib.sha256(normalized(event["resource"]).encode()).hexdigest(),
                current_path_verified=event.get("raw", {}).get("file_name", {}).get("current_path_verified"),
                historical_name_source=event.get("raw", {}).get("file_name", {}).get("path_source"))
            observations.append(observation)
            check(label + "_reopened_explanation", lambda: document(Path(explanations) / (event["id"] + ".json"), "explain").get("app_id") == session
                and document(Path(explanations) / (event["id"] + ".json"), "explain").get("event") == event,
                "reopened_explanation_identity_or_event_differs")
        check(label + "_missing_baseline_explicit", lambda: baseline_missing(capture, target), "post_install_state_without_explicit_missing_baseline")
    check("direct_child_parent_instance", lambda: "parent" in selected and "child" in selected
          and actor_record(capture, selected["child"], oracles["child"]).get("parent_pid") == oracles["parent"]["pid"]
          and actor_record(capture, selected["child"], oracles["child"]).get("parent_creation_time") == oracles["parent"]["creation_time"],
          "direct_parent_process_instance_not_verified")
    unrelated_context = candidates(capture, targets["unrelated"])
    unrelated = [e for e in unrelated_context if e.get("operation") == "write_requested"]
    check("unrelated_never_attributed", lambda: all(e.get("confidence") not in PROMOTED
          and e.get("evidence", {}).get("rule") not in ("InstallerPid", "DescendantProcess")
          and e.get("evidence", {}).get("session_id") is None for e in unrelated_context), "unrelated_writer_was_promoted")
    observations.append(dict(label="unrelated", observed_event_count=len(unrelated), contextual_event_count=len(unrelated_context),
        event_ids=[safe_id(e.get("id")) for e in unrelated[:32]],
        outcome="not_retained_by_follow_scope; no_claim_no_system_event" if not unrelated else "retained_without_installer_attribution",
        event_id=None if not unrelated else safe_id(unrelated[0].get("id")),
        classification="unobserved" if not unrelated else "Unknown"))
    summary["accepted"] = all(row["status"] == "passed" for row in checks)
    summary["passed"] = sum(row["status"] == "passed" for row in checks)
    summary["failed"] = sum(row["status"] == "failed" for row in checks)
    summary["not_run"] = sum(row["status"] == "not_run" for row in checks)
    summary["unresolved"] = [row["name"] for row in checks if row["status"] != "passed"]
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", type=Path, required=True)
    parser.add_argument("--truth", type=Path, required=True)
    parser.add_argument("--explanations", type=Path)
    parser.add_argument("--expected", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--select-only", action="store_true")
    args = parser.parse_args()
    try:
        if not re.fullmatch(r"[a-f0-9]{40}", args.source):
            raise ValueError("invalid_source_sha")
        capture = document(args.capture, "inspect")
        expected = json.loads(args.expected.read_text(encoding="utf-8-sig"))
        if args.select_only:
            result = select(capture, expected)
        else:
            if args.explanations is None:
                raise ValueError("explanations_required")
            result = verify(capture, load_truth(args.truth), expected, args.explanations, args.source)
    except (ValueError, OSError, TypeError, KeyError, json.JSONDecodeError) as error:
        result = dict(schema_version=1, protocol="contain-follow-installer-experiment-1", source_sha=args.source,
                      accepted=False, failure_stage="oracle_input", error_type=type(error).__name__)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 0 if args.select_only and "candidate_event_ids" in result or result.get("accepted") else 1


if __name__ == "__main__":
    raise SystemExit(main())
