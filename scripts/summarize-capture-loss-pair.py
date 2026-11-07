"""Publish only bounded counters from two retained controlled Windows captures.

The existing independent follow oracle remains the evidence gate. A baseline
failure is a measurement, not permission to weaken the candidate's acceptance.
"""
import argparse
from datetime import datetime, timezone
import importlib.util
import json
from pathlib import Path
import re

SPEC = importlib.util.spec_from_file_location("follow_oracle", Path(__file__).with_name("verify-follow-installer-experiment.py"))
ORACLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ORACLE)

FIELDS = {
    "pipeline": "callback_records deliberately_filtered unsupported_records decode_attempted decode_succeeded decode_failed failed_without_output enqueue_disconnected enqueued queue_overflow dequeued queue_pending queue_high_water queue_max_delay_ns context_evictions retention_dropped persistence_succeeded persistence_failed".split(),
    "handoff": "initial_accepted received submitted overflow oversized disconnected serialize_failed drained pending high_water_records high_water_bytes record_capacity byte_capacity receiver_finished writer_finished".split(),
    "stream": "accepted persisted failed quota_dropped committed_bytes quota_bytes batches max_batch_bytes".split(),
    "follow_installer": "direct_children_only baseline_available candidate_record_limit candidate_byte_limit resource_limit candidates admitted excluded_other unresolved excluded_self_io candidate_dropped candidate_high_water_records candidate_high_water_bytes".split(),
}


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def counters(value, fields):
    return {name: value.get(name) if isinstance(value.get(name), (int, bool)) else None for name in fields}


def iso_ticks(value):
    match = re.fullmatch(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,9}))?(Z|[+-]\d\d:\d\d)", value)
    if not match:
        raise ValueError("invalid_capture_time")
    seconds, fraction, offset = match.groups()
    instant = datetime.fromisoformat(seconds + offset.replace("Z", "+00:00"))
    delta = instant - datetime(1601, 1, 1, tzinfo=timezone.utc)
    return (delta.days * 86400 + delta.seconds) * 10_000_000 + int((fraction or "").ljust(7, "0")[:7])


def accounting(result):
    p, h, s, b = (result[key] for key in ("pipeline", "handoff", "stream", "counters"))
    def equal(left, *right):
        return all(type(n) is int and n >= 0 for n in (left,) + right) and left == sum(right)
    return dict(
        receive_queue=equal(b.get("events_received"), p.get("enqueued"), p.get("queue_overflow"), p.get("enqueue_disconnected")),
        receive_drain=equal(p.get("enqueued"), p.get("dequeued"), p.get("queue_pending")),
        handoff_input=equal(h.get("received"), *(h.get(k) for k in ("submitted", "overflow", "oversized", "disconnected", "serialize_failed"))),
        handoff_drain=equal(h.get("submitted"), h.get("drained"), h.get("pending")),
        raw_input=equal(s.get("accepted"), h.get("initial_accepted"), h.get("drained")),
        raw_persistence=equal(s.get("accepted"), s.get("persisted"), s.get("failed"), s.get("quota_dropped")),
        application_loss=equal(b.get("dropped_events"), p.get("queue_overflow"), p.get("enqueue_disconnected"),
            *(h.get(k) for k in ("overflow", "oversized", "disconnected", "serialize_failed")), s.get("failed"), s.get("quota_dropped")),
        finished_workers=p.get("queue_pending") == 0 and h.get("pending") == 0
            and h.get("receiver_finished") is True and h.get("writer_finished") is True)


def summarize(directory):
    directory = Path(directory)
    result = dict(experiment_accepted=False, available=False)
    try:
        existing = read(directory / "public/follow-experiment-summary.json")
        result["experiment_accepted"] = existing.get("accepted") is True
        termination = read(directory / "private/worker-termination.json")
        result["all_owned_processes_exited"] = termination.get("all_owned_processes_exited") is True
        expected = read(directory / "private/expected.json")
        capture = ORACLE.document(directory / "private/inspect.stdout", "inspect")
        truth = ORACLE.load_truth(expected["truth_directory"])
        backend = capture.get("backend", {})
        statuses = []
        for line in (directory / "private/capture.stdout").read_text(encoding="utf-8-sig").splitlines():
            try:
                document = json.loads(line)
                if document.get("kind") == "install_status":
                    statuses.append(document["data"])
            except (ValueError, KeyError, TypeError):
                pass
        terminal = statuses[-1] if statuses else {}
        result["terminal"] = {key: terminal.get(key) for key in
            ("stage", "installer_exited", "installer_exit_code", "capture_finished", "data_saved")}
        result["cold_query_exact_session"] = terminal.get("session_id") == capture.get("id")
        result.update(available=True, capture_finished=capture.get("capture_state") == "finished",
                      capture_exit_code=expected.get("capture_exit_code"),
                      independent_exit_code=expected.get("independent_exit_code"),
                      quality_level=capture.get("quality", {}).get("level"),
                      counters=counters(backend, "events_received dropped_events etw_events_lost etw_buffers_lost decode_errors context_losses".split()),
                      stats=counters(capture.get("stats", {}), "events_received events_retained events_normalized known_actor_events unknown_actor_events drain_timed_out".split()))
        result["providers_active"] = {name: backend.get(name) == "active" for name in ("etw_process", "etw_file")}
        for layer, fields in FIELDS.items():
            result[layer] = counters(backend.get(layer) or {}, fields)
        result["stream"]["error_present"] = (backend.get("stream") or {}).get("error") is not None
        result["targets"] = []
        for target in expected["targets"]:
            events = [event for event in ORACLE.candidates(capture, target) if event.get("operation") == "write_requested"]
            matching_truth = [row for row in truth if row.get("role") == target["role"] and row.get("success") is True
                              and row.get("operation") == "write_requested"
                              and ORACLE.normalized(row.get("resource", "")) == ORACLE.normalized(target["path"])]
            promoted = [event for event in events if event.get("confidence") in ORACLE.PROMOTED
                        or event.get("evidence", {}).get("rule") in ("InstallerPid", "DescendantProcess")
                        or event.get("evidence", {}).get("session_id") is not None]
            gate = next((row.get("status") == "passed" for row in existing.get("checks", [])
                         if row.get("name") == target["label"] + "_verified_source_write"), None)
            result["targets"].append(dict(label=target["label"], independent_successful_writes=len(matching_truth),
                content_verified=target.get("content_verified") is True, retained_write_requests=len(events),
                promoted_write_requests=len(promoted), verified_actor_and_interval=gate,
                event_ids=[ORACLE.safe_id(event.get("id")) for event in events[:32]]))
        noise = [row for row in truth if row.get("role") == "noise"]
        counts = {operation: sum(row.get("operation") == operation and row.get("success") is True for row in noise)
                  for operation in ("write_requested", "rename_requested", "delete_requested")}
        # Paths are Windows strings even in offline regression on Linux.
        prefix = ORACLE.normalized(expected["targets"][0]["path"]).rsplit("\\", 2)[0] + "\\noise\\"
        observed = [event for event in capture.get("events", []) if event.get("event_type") == "file"
                    and ORACLE.normalized(event.get("resource", "")).startswith(prefix)]
        wrongly_promoted = [event for event in observed if event.get("confidence") in ORACLE.PROMOTED
                            or event.get("evidence", {}).get("rule") in ("InstallerPid", "DescendantProcess")
                            or event.get("evidence", {}).get("session_id") is not None]
        files = expected.get("background_files", 0)
        result["background"] = dict(files=files, successful_operations=counts,
            exit_code=expected.get("background_exit_code"), signal_exit_code=expected.get("background_signal_exit_code"),
            observed_events=len(observed), falsely_attributed_events=len(wrongly_promoted),
            complete=type(files) is int and files > 0 and all(count == files for count in counts.values())
                     and expected.get("background_exit_code") == 0 and expected.get("background_signal_exit_code") == 0)
        parent_truth = [row for row in truth if row.get("role") == "followparent" and row.get("operation") == "write_requested" and row.get("success") is True]
        child_truth = [row for row in truth if row.get("role") == "followchild" and row.get("operation") == "write_requested" and row.get("success") is True]
        root = [process for process in capture.get("processes", []) if len(parent_truth) == 1
                and process.get("pid") == parent_truth[0].get("pid") and process.get("creation_time") == parent_truth[0].get("creation_time")]
        window = dict(all_noise_within_root_lifetime=False, all_noise_within_capture=False, spans_parent_write=False, spans_child_write=False)
        if noise and len(root) == 1 and root[0].get("ended_at") is not None:
            start = min(ORACLE.decimal(row["start_ticks"]) for row in noise)
            end = max(ORACLE.decimal(row["end_ticks"]) for row in noise)
            window["all_noise_within_root_lifetime"] = ORACLE.decimal(root[0]["creation_time"]) <= start <= end <= ORACLE.decimal(root[0]["ended_at"])
            window["all_noise_within_capture"] = iso_ticks(capture["started_at"]) <= start <= end <= iso_ticks(capture["finished_at"])
            for label, rows in (("parent", parent_truth), ("child", child_truth)):
                window["spans_" + label + "_write"] = len(rows) == 1 and start <= ORACLE.decimal(rows[0]["start_ticks"]) <= ORACLE.decimal(rows[0]["end_ticks"]) <= end
        result["background"]["window"] = window
        result["accounting"] = accounting(result)
        finished = terminal.get("stage") == "finished" and terminal.get("installer_exited") is True \
            and terminal.get("installer_exit_code") == 0 and terminal.get("capture_finished") is True \
            and terminal.get("data_saved") is True and result["cold_query_exact_session"]
        result["accepted"] = result["experiment_accepted"] and result["background"]["complete"] and not wrongly_promoted and finished \
            and all(window.values()) and all(result["accounting"].values()) and result["all_owned_processes_exited"]
        result["workload_complete"] = finished and result["capture_finished"] and expected.get("capture_exit_code") == 0 \
            and expected.get("independent_exit_code") == 0 and all(result["providers_active"].values()) \
            and result["background"]["complete"] and all(target["independent_successful_writes"] == 1
                and target["content_verified"] for target in result["targets"]) and all(window.values()) \
            and all(result["accounting"].values()) and result["all_owned_processes_exited"]
    except (OSError, ValueError, KeyError, TypeError):
        result["accepted"] = False
        result["blocked"] = "saved_capture_or_independent_truth_unavailable"
    return result


def compare(baseline, candidate, identity):
    result = dict(schema_version=1, protocol="contain-capture-loss-pair-1", measurement_kind="real_etw",
                  maximum_native_captures=2, retries=0, same_runner=True, order=["beta9", "candidate"],
                  package_identity=identity, baseline=baseline, candidate=candidate,
                  comparable=False, accepted=False, improvement_claim=False,
                  limitations=["Sequential single samples on a disposable Windows runner; not the user's Parallels VM.",
                               "Background syscall counts are equal; incidental OS activity and ETW arrival timing can differ.",
                               "This does not identify the cause of the user's private historical session."])
    if baseline.get("available") and candidate.get("available"):
        limits = [("stream", "quota_bytes"), ("handoff", "record_capacity"), ("handoff", "byte_capacity"),
                  ("follow_installer", "candidate_record_limit"), ("follow_installer", "candidate_byte_limit"),
                  ("follow_installer", "resource_limit")]
        comparable = all(baseline[layer].get(key) is not None and baseline[layer][key] == candidate[layer].get(key)
                         for layer, key in limits)
        comparable = comparable and baseline["background"]["files"] == candidate["background"]["files"] \
            and baseline.get("workload_complete") is True and candidate.get("workload_complete") is True
        result["comparable"] = comparable
        before = baseline["counters"].get("dropped_events")
        after = candidate["counters"].get("dropped_events")
        if type(before) is int and type(after) is int:
            result["application_loss_change"] = dict(before=before, after=after, reduction=before-after,
                reduction_percent=100 * (before-after) / before if before else None)
            result["improvement_claim"] = comparable and candidate.get("accepted") is True and before > after
        result["accepted"] = comparable and candidate.get("accepted") is True
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True, type=Path)
    args = parser.parse_args()
    result = compare(summarize(args.directory / "beta9"), summarize(args.directory / "candidate"),
                     read(args.directory / "package-identity.json"))
    destination = args.directory / "public/capture-loss-pair.json"
    destination.parent.mkdir(exist_ok=True)
    destination.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return 0 if result["accepted"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
