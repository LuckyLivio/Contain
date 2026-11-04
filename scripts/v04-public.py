"""Publish numeric matrix evidence and scoped fixture oracle rows, never captures/DBs.

Every planned attempt remains present, including missing/unreadable measurements.
The full raw exports and error strings remain private on the runner.
"""
import argparse
import hashlib
import json
import math
import ntpath
from pathlib import Path
import re


SCALAR_FIELDS = (
    "files", "workers", "noise_files", "trial", "seed_index", "expected_target_operations", "expected_noise_operations",
    "database_initial_bytes", "elapsed_seconds", "parent_cpu_seconds", "parent_cpu_percent_machine_average",
    "parent_peak_working_set_bytes", "parent_sampled_peak_private_bytes", "sampled_db_peak_bytes", "sampled_wal_peak_bytes",
    "sqlite_bytes", "capture_exit", "acceptance_pass", "timeout_terminated_capture", "capture_terminated_on_harness_exit",
    "noise_terminated_on_harness_exit",
)
LABELS = {
    "mode": {"baseline", "candidate"}, "status": {"started", "completed", "failed", "not_run", "unreadable"},
    "role": {"parent", "child", "grandchild", "unrelated", "detached", "short", "stress", "worker", "noise", "burst"},
    "group": {"target_success", "noise_success", "failed", "unsupported"},
    "operation": {"write_requested", "rename_requested", "delete_requested", "set_value", "unsupported_diagnostic"},
    "observation": {"observed", "unobserved", "unsupported_out_of_scope"},
    "attribution": {"correctly_attributed", "incorrectly_attributed", "correctly_not_assigned_to_target_app", "observed_but_unresolved"},
    "outcome": {"Correct", "Incorrect", "Unknown"},
}


def numeric(value):
    """No arbitrary strings, error messages, paths or identifier-like dictionary keys."""
    if value is None or type(value) in (bool, int):
        return value
    if type(value) is float:
        return value if math.isfinite(value) else None
    if isinstance(value, list):
        return [numeric(item) for item in value if not isinstance(item, str)]
    if isinstance(value, dict):
        return {key: numeric(item) for key, item in value.items()
                if re.fullmatch(r"[a-z][a-z0-9_]*", key) and not isinstance(item, str)}
    return None


def read(path):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig")), None
    except FileNotFoundError:
        return None, "missing"
    except (OSError, ValueError):
        return None, "unreadable"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


def fixture_path(value, root):
    if not value:
        return None
    if not root:
        return "<REDACTED_OUTSIDE_FIXTURE>"
    normalized = ntpath.normcase(ntpath.normpath(value.replace("\\\\?\\", "")))
    base = ntpath.normcase(ntpath.normpath(root.replace("\\\\?\\", "")))
    if normalized == base:
        return "<FIXTURE>"
    if normalized.startswith(base + "\\"):
        return "<FIXTURE>/" + normalized[len(base) + 1:].replace("\\", "/")
    return "<REDACTED_OUTSIDE_FIXTURE>"


def safe_row(row, root):
    result = {key: row[key] if row[key] in values else None for key, values in LABELS.items() if key in row}
    result.update({key: numeric(row[key]) for key in ("pid", "success", "observed", "raw_matches", "error") if key in row})
    for key in ("resource", "destination"):
        if key in row:
            result[key] = fixture_path(row[key], root)
    for key in ("creation_time", "start_ticks", "end_ticks"):
        if key in row:
            result[key] = row[key] if isinstance(row[key], str) and re.fullmatch(r"\d{1,20}", row[key]) else None
    if "matched_event_id" in row:
        value = row["matched_event_id"]
        result["matched_event_id"] = value if isinstance(value, str) and re.fullmatch(r"[a-fA-F0-9-]{1,40}", value) else None
    return result


def publish(source, destination):
    environment, error = read(source / "environment.json")
    if error or not isinstance(environment, dict) or not isinstance(environment.get("planned_attempts"), list):
        raise ValueError("matrix environment/attempt plan is missing or unreadable")
    destination.mkdir(parents=True, exist_ok=False)
    public_environment = {key: numeric(environment.get(key)) for key in ("schema_version", "logical_processors", "snapshot_only")}
    for key in ("baseline_variant", "candidate_variant"):
        public_environment[key] = environment.get(key) if environment.get(key) in ("D0", "D1", "D2", "D3") else None
    public_environment["executables"] = {name: dict(sha256=(value.get("sha256") if re.fullmatch(r"[a-fA-F0-9]{64}", value.get("sha256", "")) else None))
                                          for name, value in environment.get("executables", {}).items() if name in ("baseline", "candidate", "fixture")}
    for key in ("scorer_sha256", "script_sha256"):
        value = environment.get(key)
        public_environment[key] = value if isinstance(value, str) and re.fullmatch(r"[a-fA-F0-9]{64}", value) else None
    # OS/compiler/commit provenance is written by the workflow as a fixed-field
    # record, not taken from arbitrary runtime environment or process logs.
    provenance, _ = read(source / "build-provenance.json")
    public_provenance = {}
    if isinstance(provenance, dict):
        for key in ("baseline_commit", "candidate_commit", "baseline_lock_sha256", "candidate_lock_sha256"):
            value = provenance.get(key)
            if isinstance(value, str) and re.fullmatch(r"[a-fA-F0-9]{40}|[a-fA-F0-9]{64}", value):
                public_provenance[key] = value
        for key in ("windows_build", "logical_processors", "elevated"):
            public_provenance[key] = numeric(provenance.get(key))
        value = provenance.get("rustc_version")
        if isinstance(value, str) and re.fullmatch(r"rustc [0-9.]+ \([a-f0-9]+ [0-9-]+\)", value):
            public_provenance["rustc_version"] = value
    trials = []
    for index, plan in enumerate(environment["planned_attempts"], 1):
        if (plan.get("mode") not in LABELS["mode"] or any(type(plan.get(key)) is not int for key in ("files", "workers", "noise_files", "seed_index", "trial"))):
            raise ValueError("invalid planned attempt label")
        label = f"{index:04}-{plan['mode']}-{plan['files']}-w{plan['workers']}-n{plan['noise_files']}-db{plan['seed_index']}-t{plan['trial']}"
        directory = source / label
        record, record_error = read(directory / "measurement.json")
        record = record if isinstance(record, dict) else {}
        result = dict(attempt=index, mode=plan["mode"], **{key: plan[key] for key in ("files", "workers", "noise_files", "seed_index", "trial")},
                      status="not_run" if record_error == "missing" else "unreadable" if record_error else record.get("status"),
                      measurement_sha256=digest(directory / "measurement.json"), acceptance_pass=False)
        plan_fields = ("mode", "files", "workers", "noise_files", "seed_index", "trial")
        result.update({key: numeric(record[key]) for key in SCALAR_FIELDS if key in record and key not in plan_fields})
        result["label_mismatch"] = any(record.get(key, plan[key]) != plan[key] for key in plan_fields)
        result["seed_label_verified"] = record.get("seed_index") == plan["seed_index"]
        if result["status"] not in LABELS["status"]:
            result["status"] = "unreadable"
        if result["label_mismatch"] or result["status"] != "completed":
            result["acceptance_pass"] = False
        result.update(harness_error_present=bool(record.get("harness_error")), cleanup_error_present=bool(record.get("cleanup_error")),
                      metric_error_count=len(record.get("metric_errors", [])))
        for field in ("stats", "backend", "score", "gate", "parent_io"):
            if field in record:
                result[field] = numeric(record[field])
        backend = record.get("backend") or {}
        for field in ("etw_file", "etw_registry", "etw_process"):
            value = backend.get(field)
            result.setdefault("backend", {})[field] = value if value in ("active", "disabled", "unavailable", "not_requested") else None
        result["quality_level"] = (record.get("quality") or {}).get("level")
        if result["quality_level"] not in ("Complete", "Degraded", "Incomplete"):
            result["quality_level"] = None
        for field in ("error",):
            result["stream_error_present"] = bool((backend.get("stream") or {}).get(field))
        for filename in ("score.json", "ground-truth.json"):
            document, issue = read(directory / filename)
            result[filename + "_status"] = issue or "available"
            if document is not None:
                rows = document.get("rows", []) if filename == "score.json" and isinstance(document, dict) else document
                if isinstance(rows, list):
                    result["scored_rows" if filename == "score.json" else "truth_rows"] = [safe_row(row, record.get("fixture_root")) for row in rows]
        expected_target = record.get("expected_target_operations")
        expected_noise = record.get("expected_noise_operations")
        expected_rows = expected_target + expected_noise if type(expected_target) is int and type(expected_noise) is int else None
        result["artifact_complete"] = (expected_rows is not None
            and all(result.get(filename + "_status") == "available" for filename in ("score.json", "ground-truth.json"))
            and len(result.get("scored_rows", [])) == len(result.get("truth_rows", [])) == expected_rows)
        if not result["artifact_complete"] or not result["seed_label_verified"]:
            result["acceptance_pass"] = False
        profile, profile_error = read(directory / "profile.json")
        result["profile_status"] = profile_error or "available"
        if isinstance(profile, dict):
            result["profile"] = numeric(profile)
        trials.append(result)
    report = dict(schema_version=1, environment=public_environment, provenance=public_provenance,
                  planned_attempts=len(trials), completed_attempts=sum(row["status"] == "completed" for row in trials),
                  acceptance_pass=bool(trials) and all(row["acceptance_pass"] is True for row in trials), trials=trials,
                  publication="Projection after original scoring. All planned attempts and original scored rows retained; arbitrary strings, source events, inventory, absolute paths, logs and databases omitted. Fixture paths are templated; out-of-scope paths redacted. Full raw evidence remains private. Null means unavailable.")
    (destination / "comparison.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    report = publish(args.source, args.destination)
    print(json.dumps({key: report[key] for key in ("planned_attempts", "completed_attempts", "acceptance_pass")}))


if __name__ == "__main__":
    main()
