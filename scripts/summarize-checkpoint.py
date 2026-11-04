"""Reproduce a compact checkpoint experiment report from downloaded safe artifacts.

Original quality/storage gates are retained; the frozen scorer is never rerun.
Queue-window diagnostics are recomputed with the pinned, unchanged analyzer.
"""
import argparse
from collections import Counter
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import statistics


ANALYZER = Path(__file__).with_name("analyze-commit-tail.py")
ANALYZER_LF_SHA256 = "6607be474bbd6c0b61f59a1c3c42971c8de283c0aea3067cebe58c65575b804b"
STAGES = (
    "raw_commit_including_autocheckpoint", "raw_batch_flush", "raw_insert",
    "raw_wal_budget_sample", "post_raw_checkpoint", "capture_receive_wall",
    "post_wall", "post_raw_index_materialization", "post_derived_group_commit",
    "post_final_downgrade_full_commit",
)
ENVIRONMENT_KEYS = (
    "phase", "commit", "baseline_commit", "candidate_commit", "baseline_variant",
    "candidate_variant", "checkpoint_variant", "expected_attempts", "completed_attempts",
    "harness_aborted", "profile", "rustc", "os", "elevated", "filesystem",
    "logical_processors", "fixture_source_sha256", "fixture_sha256", "scorer_sha256",
    "candidate_lock_sha256", "baseline_lock_sha256", "wal_budget_bytes", "run_id",
)
STREAM_KEYS = (
    "accepted", "persisted", "failed", "quota_dropped", "committed_bytes", "quota_bytes",
    "batches", "persistence_ns", "sqlite_synchronous", "wal_autocheckpoint_pages",
    "sqlite_page_size_bytes", "wal_budget_bytes", "wal_initial_bytes", "wal_peak_bytes",
    "wal_growth_bytes", "wal_budget_overshoot_bytes", "wal_samples", "wal_budget_exceeded",
    "disk_reserve_bytes", "disk_free_min_bytes", "checkpoint_completed",
    "restored_wal_autocheckpoint_pages",
)
PIPELINE_KEYS = (
    "queue_overflow", "queue_pending", "queue_high_water", "queue_max_delay_ns",
    "arrival_peak_per_100ms", "callback_records", "decode_attempted", "decode_succeeded",
    "decode_failed", "enqueued", "dequeued", "enqueue_disconnected", "context_evictions",
    "persistence_failed", "persistence_succeeded", "retention_dropped", "failed_without_output",
)


def object_or_empty(value):
    return value if isinstance(value, dict) else {}


def numeric(value):
    """No strings, paths, event rows, machine inventory, or array payloads."""
    if value is None or isinstance(value, (int, float, bool)):
        return value
    if isinstance(value, dict):
        return {key: numeric(item) for key, item in value.items()
                if item is None or isinstance(item, (int, float, bool, dict))}
    raise TypeError("non-numeric field")


def fields(value, names):
    obj = object_or_empty(value)
    return {name: obj.get(name) for name in names}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def reference(path, root):
    return {"path": path.relative_to(root).as_posix(), "sha256": sha(path.read_bytes())} if path.is_file() else None


def read_object(path):
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("JSON root is not an object")
    return value


def read_optional(path, issues):
    if not path.is_file():
        return {}
    try:
        return read_object(path)
    except (OSError, ValueError) as error:
        # Error text can contain source payloads, so only publish its type.
        issues.append({"kind": "unreadable_json", "file": path.name, "error_type": type(error).__name__})
        return {}


def load_analyzer():
    source = ANALYZER.read_bytes()
    if sha(source.replace(b"\r\n", b"\n")) != ANALYZER_LF_SHA256:
        raise ValueError("review analyzer changes before updating its pinned LF hash")
    spec = importlib.util.spec_from_file_location("checkpoint_commit_tail", ANALYZER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def checkpoint_metadata(value):
    return fields(value, ("baseline_variant", "candidate_variant", "wal_budget_bytes",
                          "budget_semantics", "fixed_hotpath", "capture_wall", "expected_attempts"))


def manifests(root):
    result, contexts = [], {}
    names = {"environment.json", "comparison.json", "final-summary.json"}
    paths = sorted(path for path in root.rglob("*.json")
                   if path.name in names or re.fullmatch(r"checkpoint-[a-f0-9]+-summary\.json", path.name))
    for path in paths:
        issues = []
        obj = read_optional(path, issues)
        metadata = {key: obj[key] for key in ENVIRONMENT_KEYS if key in obj}
        if "checkpoint_experiment" in obj:
            metadata["checkpoint_experiment"] = checkpoint_metadata(obj["checkpoint_experiment"])
        result.append({"source": reference(path, root), "metadata": metadata,
                       "original_trial_count": len(obj["trials"]) if isinstance(obj.get("trials"), list) else None,
                       "issues": issues})
        contexts[path] = obj
        # A comparison can contain aborted-attempt metadata missing from environment.
        key = path.parent
        if path.name == "comparison.json" or key not in contexts:
            contexts[key] = obj
        elif path.name == "final-summary.json" and "checkpoint_experiment" not in contexts[key]:
            contexts[key] = obj
    return result, contexts


def context_for(path, root, contexts):
    small = re.fullmatch(r"(checkpoint-[a-f0-9]+)-final-D[0-3]-\d+-(?:ordinary|noise)", path.parent.name)
    if small:
        specific = contexts.get(path.parent.parent / f"{small.group(1)}-summary.json")
        if specific is not None:
            return specific
        fallback = contexts.get(path.parent.parent / "final-summary.json", {})
        return fallback if fallback.get("run_id") == small.group(1) else {}
    current = path.parent
    while True:
        if current in contexts:
            return contexts[current]
        if current == root:
            return {}
        current = current.parent


def describe_trial(path, root, context, commit, analyzer):
    issues = []
    record = read_optional(path, issues)
    directory = path.parent
    profile_path = directory / "profile.json"
    profile = read_optional(profile_path, issues)
    backend = object_or_empty(record.get("backend"))
    stream = object_or_empty(backend.get("stream"))
    stats = object_or_empty(record.get("stats"))
    validation = object_or_empty(record.get("checkpoint_validation"))
    source_gates = [validation.get("original_quality_pass"), validation.get("pass_all"), record.get("acceptance_pass")]
    if not all(type(value) is bool for value in source_gates):
        issues.append({"kind": "source_gate_booleans_unavailable"})
    elif source_gates[2] != (source_gates[0] and source_gates[1]):
        issues.append({"kind": "source_gate_conjunction_mismatch"})
    stress = re.fullmatch(r"(baseline|candidate)-stress-(\d+)-(\d+)", directory.name)
    small = re.search(r"(?:^|-)final-(D[0-3])-(\d+)-(ordinary|noise)$", directory.name)
    mode = record.get("mode", stress.group(1) if stress else None)
    variant = record.get("checkpoint_variant")
    if variant not in ("E0", "E1"):
        issues.append({"kind": "missing_or_invalid_checkpoint_label"})
        variant = stream.get("checkpoint_variant", validation.get("expected_variant"))
    if variant not in ("E0", "E1"):
        variant = None
    if stream.get("checkpoint_variant") is not None and stream["checkpoint_variant"] != variant:
        issues.append({"kind": "checkpoint_label_mismatch"})
    if mode in ("baseline", "candidate") and variant != {"baseline": "E0", "candidate": "E1"}[mode]:
        issues.append({"kind": "checkpoint_role_mismatch"})
    scale = "stress" if stress else "small" if small else "unknown"
    files = record.get("files", int(stress.group(2)) if stress else None)
    trial = record.get("trial", int(stress.group(3)) if stress else record.get("round", int(small.group(2)) if small else None))
    condition = small.group(3) if small else record.get("role")
    if small and "registry_noise" in record and record["registry_noise"] is not (condition == "noise"):
        issues.append({"kind": "noise_label_mismatch"})
    if stress and (record.get("mode", mode) != stress.group(1) or files != int(stress.group(2)) or trial != int(stress.group(3))):
        issues.append({"kind": "stress_path_label_mismatch"})
    if small and (trial != int(small.group(2)) or record.get("round") != trial or variant != "E1"):
        issues.append({"kind": "small_path_label_mismatch"})
    if stress and (record.get("role") != "stress" or files not in (1000, 10000)):
        issues.append({"kind": "invalid_stress_role_or_scale"})
    if validation.get("expected_variant") is not None and validation["expected_variant"] != variant:
        issues.append({"kind": "validation_variant_mismatch"})
    hotpath = record.get("variant") if small else context.get(f"{mode}_variant")
    if hotpath != "D3":
        issues.append({"kind": "fixed_d3_unverified"})
    source_commit = context.get(f"{mode}_commit") if stress else context.get("commit")
    commit_matches = source_commit.lower().startswith(commit.lower()) if isinstance(source_commit, str) else None
    if commit_matches is not True:
        issues.append({"kind": "source_commit_mismatch" if commit_matches is False else "source_commit_unavailable"})
    if scale == "unknown":
        issues.append({"kind": "unrecognized_attempt_directory"})
    label = f"small:{variant}:{condition}:{trial}" if scale == "small" else f"stress:{variant}:{files}:{trial}" if scale == "stress" else None
    measurement = object_or_empty(record.get("measurement")) if "measurement" in record else record
    checkpoint = object_or_empty(stream.get("checkpoint"))
    score = object_or_empty(record.get("score"))
    if not score:
        score = read_optional(directory / "score.json", issues)
    expected_denominator = 17 if scale == "small" else {1000: 2500, 10000: 25000}.get(files)
    observed_denominator = object_or_empty(object_or_empty(score.get("groups")).get("target_success")).get("expected")
    if expected_denominator is None or type(observed_denominator) is not int or observed_denominator != expected_denominator:
        issues.append({"kind": "fixed_target_denominator_unverified", "expected": expected_denominator,
                       "observed": observed_denominator if type(observed_denominator) is int else None})
    result = {
        "attempt": directory.relative_to(root).as_posix(), "label": label,
        "scale": scale, "checkpoint_variant": variant, "hotpath_variant": hotpath,
        "mode": mode, "files": files, "trial": trial, "condition": condition,
        "source_commit": source_commit, "source_commit_matches_requested": commit_matches,
        "sources": {name: reference(directory / filename, root) for name, filename in (
            ("result", "result.json"), ("profile", "profile.json"),
            ("artifact_commit_tail", "commit-tail.json"), ("score", "score.json"))},
        "gates": {"original_quality_pass": validation.get("original_quality_pass"),
                  "experiment_pass": validation.get("pass_all"),
                  "combined_pass": record.get("acceptance_pass"),
                  "experiment_checks": numeric(validation.get("checks", {}))},
        "expected_target_denominator": expected_denominator,
        "harness_error_present": bool(record.get("harness_error")),
        "missing_capture": record.get("missing_capture"),
        "incomplete_scoring": record.get("incomplete_scoring"),
        "artifact_export": numeric(object_or_empty(record.get("artifact_export"))),
        "counter_balances": record.get("counter_balances"),
        "quality_level": object_or_empty(record.get("quality")).get("level"),
        "score": numeric(score),
        "categories": numeric(record.get("categories", {})),
        "capture_wall_seconds": measurement.get("elapsed_seconds"),
        "capture_wall_scope": "Contain process through database close; export/scoring excluded",
        "harness_wall_seconds": record.get("harness_wall_seconds"),
        "capture_stats": fields(stats, ("drain_timed_out", "capture_elapsed_ms", "notification_gaps", "snapshot_gaps")),
        "phase_ms": numeric(stats.get("phase_ms", {})),
        "backend": fields(backend, ("dropped_events", "etw_events_lost", "etw_buffers_lost", "decode_errors", "context_losses", "events_received")),
        "pipeline": fields(backend.get("pipeline"), PIPELINE_KEYS),
        "stream": fields(stream, STREAM_KEYS),
        "stream_error_present": bool(stream.get("error")),
        "checkpoint": {**fields(checkpoint, ("mode", "busy", "log_frames", "checkpointed_frames", "duration_ns")),
                       "error_present": bool(checkpoint.get("error")) if stream.get("checkpoint") is not None else None},
        "resources": fields(measurement, ("parent_peak_working_set_bytes", "sqlite_bytes", "sampled_db_peak_bytes", "sampled_wal_peak_bytes")),
        "profile_stages": {stage: fields(object_or_empty(profile.get("stages")).get(stage), ("count", "total_ns", "max_ns")) for stage in STAGES},
        "queue_window_diagnostics": None,
        "issues": issues,
    }
    if not profile_path.is_file():
        issues.append({"kind": "missing_profile"})
    else:
        try:
            analysis = analyzer.analyze(profile, record, max_windows=1)
            result["queue_window_diagnostics"] = {
                "origin": "recomputed from hashed profile and result using pinned analyzer; artifact commit-tail JSON is hashed for audit only",
                "overflow": analysis["overflow"],
                "omitted_queue_spans": analysis["omitted_queue_spans"],
                "queue_spans": {stage: {key: value for key, value in row.items() if key not in ("displayed_windows", "undisplayed_windows")}
                                for stage, row in analysis["queue_spans"].items()},
            }
        except (ValueError, TypeError, KeyError) as error:
            issues.append({"kind": "queue_analysis_unavailable", "error_type": type(error).__name__})
    return result


def audit_manifests(root, manifest, contexts, commit):
    """Reconcile declared rows, including rows whose per-attempt artifact is absent."""
    for item in manifest:
        path = root / item["source"]["path"]
        obj = contexts.get(path, {})
        issues = item["issues"]
        for key in ("commit", "baseline_commit", "candidate_commit"):
            if key in obj and (not isinstance(obj[key], str) or not obj[key].lower().startswith(commit.lower())):
                issues.append({"kind": "manifest_source_commit_mismatch", "field": key})
        if "checkpoint_experiment" in obj:
            if obj.get("baseline_commit") != obj.get("candidate_commit"):
                issues.append({"kind": "same_source_commit_mismatch"})
            hashes = [obj.get(f"{arm}_lock_sha256") for arm in ("baseline", "candidate")]
            if any(not isinstance(value, str) or not re.fullmatch(r"[a-fA-F0-9]{64}", value) for value in hashes):
                issues.append({"kind": "same_lock_provenance_unavailable"})
            elif hashes[0].lower() != hashes[1].lower():
                issues.append({"kind": "same_lock_provenance_mismatch"})
            if object_or_empty(obj.get("checkpoint_experiment")).get("fixed_hotpath") != "D3":
                issues.append({"kind": "manifest_fixed_d3_unverified"})
            if obj.get("baseline_variant") != "D3" or obj.get("candidate_variant") != "D3":
                issues.append({"kind": "manifest_arm_d3_unverified"})
        if path.name == "comparison.json":
            environment = contexts.get(path.parent / "environment.json", {})
            for key in (*ENVIRONMENT_KEYS, "checkpoint_experiment"):
                if key in environment and key in obj and environment[key] != obj[key]:
                    issues.append({"kind": "comparison_environment_disagreement", "field": key})
        if path.name == "final-summary.json" and isinstance(obj.get("run_id"), str):
            other = contexts.get(path.parent / f"{obj['run_id']}-summary.json")
            if other is not None and other != obj:
                issues.append({"kind": "matching_small_summaries_disagree"})
        rows = obj.get("trials")
        if not isinstance(rows, list):
            continue
        matches, missing, mismatches, unrecognized = 0, [], [], 0
        expected_paths = []
        for row in rows:
            row = object_or_empty(row)
            if path.name == "comparison.json" and row.get("mode") in ("baseline", "candidate") and type(row.get("files")) is int and type(row.get("trial")) is int:
                expected_path = path.parent / f"{row['mode']}-stress-{row['files']}-{row['trial']}" / "result.json"
            elif isinstance(obj.get("run_id"), str) and type(row.get("round")) is int and type(row.get("registry_noise")) is bool and row.get("variant") == "D3":
                expected_path = path.parent / f"{obj['run_id']}-final-D3-{row['round']}-{'noise' if row['registry_noise'] else 'ordinary'}" / "result.json"
            else:
                unrecognized += 1
                continue
            relative = expected_path.relative_to(root).as_posix()
            expected_paths.append(relative)
            if not expected_path.is_file():
                missing.append(relative)
                continue
            original = read_optional(expected_path, [])
            if any(key not in original or original[key] != value for key, value in row.items()):
                mismatches.append(relative)
            else:
                matches += 1
        duplicate_paths = sorted(path for path, count in Counter(expected_paths).items() if count > 1)
        declared = obj.get("expected_attempts", object_or_empty(obj.get("checkpoint_experiment")).get("expected_attempts"))
        completed = obj.get("completed_attempts")
        count_mismatch = (type(declared) is int and declared != len(rows)) or (type(completed) is int and completed != len(rows))
        item["trial_reconciliation"] = {"matched_results": matches, "missing_result_paths": missing,
                                         "mismatched_result_paths": mismatches, "duplicate_result_paths": duplicate_paths,
                                         "unrecognized_manifest_rows": unrecognized,
                                         "declared_attempt_count_mismatch": count_mismatch}
        if missing or mismatches or unrecognized or duplicate_paths or count_mismatch:
            issues.append({"kind": "manifest_trial_reconciliation_failed"})


def expected_labels(scale, files=None, variant=None):
    if scale == "small":
        return {f"small:E1:{condition}:{trial}" for condition in ("ordinary", "noise") for trial in range(1, 6)}
    return {f"stress:{arm}:{files}:{trial}" for arm in ([variant] if variant else ("E0", "E1")) for trial in range(1, 4)}


def stage_gate(rows, expected, candidate_only=False):
    labels = Counter(row["label"] for row in rows)
    if set(labels) != expected or any(count != 1 for count in labels.values()):
        return False
    gates = [row["gates"]["experiment_pass"] for row in rows]
    gates += [row["gates"]["combined_pass"] for row in rows if not candidate_only or row["checkpoint_variant"] == "E1"]
    if any(value is False for value in gates):
        return False
    return True if all(value is True for value in gates) else None


def complete_evidence(rows, expected):
    labels = Counter(row["label"] for row in rows)
    return (set(labels) == expected and len(rows) == len(expected)
            and not any(row["issues"] for row in rows))


def summarize(root, run_url, commit):
    root = root.resolve()
    if not root.is_dir():
        raise ValueError("artifact root must be a directory")
    analyzer = load_analyzer()
    manifest, contexts = manifests(root)
    audit_manifests(root, manifest, contexts, commit)
    paths = sorted(root.rglob("result.json"))
    if not paths:
        raise ValueError("no trial result.json files found")
    trials = [describe_trial(path, root, context_for(path, root, contexts), commit, analyzer) for path in paths]
    small = [row for row in trials if row["scale"] == "small"]
    thousand = [row for row in trials if row["scale"] == "stress" and row["files"] == 1000]
    small_gate = stage_gate(small, expected_labels("small"))
    thousand_gate = stage_gate(thousand, expected_labels("stress", 1000), candidate_only=True)
    eligible = False if small_gate is False or thousand_gate is False else True if small_gate is True and thousand_gate is True else None
    small_manifests_ok = not any(item["issues"] for item in manifest if "summary.json" in item["source"]["path"])
    thousand_parents = {Path(row["attempt"]).parent.as_posix() for row in thousand}
    stress_manifests_ok = not any(item["issues"] for item in manifest
                                  if Path(item["source"]["path"]).parent.as_posix() in thousand_parents)
    small_integrity = complete_evidence(small, expected_labels("small")) and small_manifests_ok
    thousand_integrity = complete_evidence(thousand, expected_labels("stress", 1000)) and stress_manifests_ok
    verified_small = small_gate is True and small_integrity
    verified_thousand = thousand_gate is True and thousand_integrity
    groups = []
    for scale, files, variant in [("small", None, "E1"), ("stress", 1000, "E0"), ("stress", 1000, "E1"), ("stress", 10000, "E0"), ("stress", 10000, "E1")]:
        rows = [row for row in trials if (row["scale"], row["files"], row["checkpoint_variant"]) == (scale, files, variant)]
        expected = expected_labels(scale, files, variant)
        labels = Counter(row["label"] for row in rows)
        full_wall = [row["capture_wall_seconds"] for row in rows if type(row["capture_wall_seconds"]) in (int, float)]
        groups.append({"scale": scale, "files": files, "checkpoint_variant": variant,
                       "status": "not_run" if not rows else "complete" if set(labels) == expected and len(rows) == len(expected) else "partial_or_mislabeled",
                       "required_by_reported_preceding_gates": eligible if files == 10000 else True,
                       "planned_attempts_if_run": len(expected), "attempts": len(rows),
                       "missing_labels": sorted(expected - set(labels)),
                       "duplicate_labels": sorted(label for label, count in labels.items() if count > 1),
                       "unexpected_labels": sorted(str(label) for label in set(labels) - expected),
                       "gate_counts": {gate: {"pass": sum(row["gates"][gate] is True for row in rows),
                                               "fail": sum(row["gates"][gate] is False for row in rows),
                                               "unknown": sum(row["gates"][gate] is not True and row["gates"][gate] is not False for row in rows)}
                                       for gate in ("original_quality_pass", "experiment_pass", "combined_pass")},
                       "capture_wall_samples": len(full_wall),
                       "capture_wall_median_seconds": statistics.median(full_wall) if full_wall else None})
    orphan_profiles = [reference(path, root) for path in sorted(root.rglob("profile.json")) if not path.with_name("result.json").is_file()]
    integrity_issues = []
    if not small_integrity:
        integrity_issues.append("small_stage_provenance_labels_or_completeness_unverified")
    if not thousand_integrity:
        integrity_issues.append("thousand_stage_provenance_labels_or_completeness_unverified")
    if any(row["issues"] for row in trials):
        integrity_issues.append("trial_evidence_issues_present")
    if any(item["issues"] for item in manifest):
        integrity_issues.append("manifest_evidence_issues_present")
    if orphan_profiles:
        integrity_issues.append("orphan_profiles_without_result_records")
    ten_thousand = [row for row in trials if row["scale"] == "stress" and row["files"] == 10000]
    if (ten_thousand or eligible is True) and not complete_evidence(ten_thousand, expected_labels("stress", 10000)):
        integrity_issues.append("ten_thousand_stage_provenance_labels_or_completeness_unverified")
    if ten_thousand and not (verified_small and verified_thousand):
        integrity_issues.append("ten_thousand_attempts_present_without_verified_preceding_gates")
    script = Path(__file__)
    return {
        "schema_version": 1, "diagnostic_only": True,
        "source": {"run_url": run_url, "requested_commit": commit, "provenance": "run URL and commit supplied on CLI; commits compared with artifact manifests"},
        "reproduction": {"script": "scripts/summarize-checkpoint.py", "script_lf_sha256": sha(script.read_bytes().replace(b"\r\n", b"\n")),
                         "analyzer": "scripts/analyze-commit-tail.py", "analyzer_lf_sha256": ANALYZER_LF_SHA256,
                         "scorer_rerun": False},
        "limitations": [
            "Only unique trial result.json paths enumerate attempts; summary/comparison trial arrays are metadata, not extra attempts. Failures and unreadable/missing fields remain visible.",
            "Quality, experiment integrity, and combined gates retain original booleans. Missing booleans are unknown, never a pass.",
            "Reported gate aggregates reproduce source booleans; verified eligibility additionally requires matching source commits, fixed D3, path/role/variant labels, fixed target denominators, complete preceding attempt sets, and reconciled manifests. Commit/lock hashes establish source-build provenance, not an executable binary hash.",
            "10000-file pairs are conditional on all ten small attempts, three E1 1000-file passes, and all six 1000-file storage checks. Not-run groups are not fabricated failures or successful trials.",
            "Capture process wall includes checkpoint/postprocessing/final commit/database close. Small harness wall also includes fixture/build/export/scoring and must not substitute for capture wall.",
            "Internal WAL peak is sampled around raw batches; external stress WAL peak is sampled every 20ms through process close and can include postprocessing. Neither establishes an exact physical maximum or hard cap.",
            "Commit and flush overflow windows are nested and must not be added. Queue losses and ETW losses are separate; temporal overlap does not establish checkpoint causality.",
            "Process-crash tests do not establish power-loss durability. No source payloads, machine event rows, body paths, or error strings are copied.",
        ],
        "manifests": manifest, "attempt_count": len(trials),
        "reported_preceding_gates": {"small": small_gate, "thousand": thousand_gate, "ten_thousand_eligible": eligible},
        "evidence_integrity": {"pass": not integrity_issues, "issues": integrity_issues,
                               "small_stage_verified": small_integrity, "thousand_stage_verified": thousand_integrity},
        "verified_eligibility": {"small": verified_small, "thousand": verified_thousand,
                                 "ten_thousand_eligible": verified_small and verified_thousand},
        "groups": groups, "orphan_profiles_without_results": orphan_profiles,
        "trials": trials,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact_root", type=Path, help="downloaded and extracted safe artifact directory")
    parser.add_argument("--run-url", required=True)
    parser.add_argument("--commit", required=True, help="binary Git commit; full SHA preferred")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[0-9a-fA-F]{7,40}", args.commit):
        parser.error("--commit must be a Git SHA (7-40 hex characters)")
    try:
        report = summarize(args.artifact_root, args.run_url, args.commit)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({"attempts": report["attempt_count"], "reported_preceding_gates": report["reported_preceding_gates"],
                      "evidence_integrity": report["evidence_integrity"], "verified_eligibility": report["verified_eligibility"],
                      "trials_with_issues": sum(bool(row["issues"]) for row in report["trials"]),
                      "groups": report["groups"]}))


if __name__ == "__main__":
    main()
