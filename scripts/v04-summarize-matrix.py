"""Compact already-published matrix evidence without rescoring or dropping attempts.

Example: --input core=target/v04/public-local-core/comparison.json. Inputs must be
trusted v04-public.py projections; this summarizer does not establish that trust
or sanitize arbitrary private documents. Oracle rows are counted, not copied.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import statistics


FIELDS = (
    "attempt", "mode", "files", "workers", "noise_files", "seed_index", "trial", "status", "measurement_sha256",
    "acceptance_pass", "expected_target_operations", "expected_noise_operations", "database_initial_bytes", "elapsed_seconds",
    "parent_cpu_seconds", "parent_cpu_percent_machine_average", "parent_peak_working_set_bytes", "parent_sampled_peak_private_bytes",
    "sampled_db_peak_bytes", "sampled_wal_peak_bytes", "sqlite_bytes", "capture_exit", "label_mismatch", "seed_label_verified",
    "harness_error_present", "cleanup_error_present", "metric_error_count", "stats", "backend", "score", "gate", "parent_io",
    "quality_level", "stream_error_present", "score.json_status", "ground-truth.json_status", "artifact_complete", "profile_status",
    "timeout_terminated_capture", "capture_terminated_on_harness_exit", "noise_terminated_on_harness_exit",
)
METRICS = ("elapsed_seconds", "parent_cpu_seconds", "parent_peak_working_set_bytes", "parent_sampled_peak_private_bytes",
           "sampled_db_peak_bytes", "sampled_wal_peak_bytes", "sqlite_bytes")
IO_FIELDS = ("read_operations", "write_operations", "other_operations", "read_bytes", "write_bytes", "other_bytes")


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def summarize(inputs, baseline_commit, candidate_commit, baseline_environment=None):
    sources, trials, groups = [], [], {}
    labels = set()
    for label, path in inputs:
        if not re.fullmatch(r"[a-z][a-z0-9-]*", label) or label in labels:
            raise ValueError("input labels must be unique simple names")
        labels.add(label)
        document = json.loads(path.read_text(encoding="utf-8-sig"))
        if document.get("environment", {}).get("snapshot_only") is not True:
            raise ValueError("local fallback summarizer requires explicit snapshot_only=true")
        planned = document["planned_attempts"]
        source_trials = document["trials"]
        if type(planned) is not int or planned != len(source_trials):
            raise ValueError("public projection omitted planned attempts")
        if {row["attempt"] for row in source_trials} != set(range(1, planned + 1)):
            raise ValueError("public projection has missing or duplicate attempt IDs")
        sources.append(dict(label=label, public_projection_sha256=sha(path), planned_attempts=planned,
                            completed_attempts=document["completed_attempts"], source_acceptance_pass=document["acceptance_pass"],
                            environment=document["environment"], provenance=document["provenance"]))
        for row in source_trials:
            clean = {field: row[field] for field in FIELDS if field in row}
            clean.update(run=label, scored_row_count=len(row.get("scored_rows", [])), truth_row_count=len(row.get("truth_rows", [])))
            stages = row.get("profile", {}).get("stages", {})
            clean["profile_stages"] = {name: {key: value[key] for key in ("count", "total_ns", "max_ns") if key in value}
                                       for name, value in stages.items() if isinstance(value, dict)}
            trials.append(clean)
            key = (label, row["files"], row["workers"], row["noise_files"], row["seed_index"], row["mode"])
            groups.setdefault(key, []).append(clean)
    aggregates = []
    for (label, files, workers, noise, seed, mode), rows in sorted(groups.items()):
        aggregate = dict(run=label, files=files, workers=workers, noise_files=noise, seed_index=seed, mode=mode,
                         attempts=len(rows), completed=sum(row["status"] == "completed" for row in rows),
                         passed=sum(row.get("acceptance_pass") is True for row in rows), medians={}, measured_samples={})
        for metric in METRICS:
            values = [row[metric] for row in rows if type(row.get(metric)) in (int, float)]
            aggregate["medians"][metric] = statistics.median(values) if values else None
            aggregate["measured_samples"][metric] = len(values)
        aggregate["parent_io_medians"] = {}
        aggregate["parent_io_measured_samples"] = {}
        for metric in IO_FIELDS:
            values = [row["parent_io"][metric] for row in rows if type(row.get("parent_io", {}).get(metric)) in (int, float)]
            aggregate["parent_io_medians"][metric] = statistics.median(values) if values else None
            aggregate["parent_io_measured_samples"][metric] = len(values)
        aggregates.append(aggregate)
    environment = None
    if baseline_environment:
        private = json.loads(baseline_environment.read_text(encoding="utf-8-sig"))
        rust = private.get("rust")
        rust = rust[0] if isinstance(rust, list) and rust else rust
        environment = dict(source_sha256=sha(baseline_environment), observed_at=private.get("timestamp", private.get("time")),
                           windows_version=private.get("os", {}).get("Version"), windows_build=int(private.get("os", {}).get("BuildNumber")),
                           logical_processors=private.get("computer", {}).get("NumberOfLogicalProcessors"),
                           physical_memory_bytes=private.get("computer", {}).get("TotalPhysicalMemory"),
                           elevated=private.get("admin"), filesystem=private.get("disk", {}).get("FileSystem"),
                           rustc_version=rust if isinstance(rust, str) else None, python_version=private.get("python"), powershell_version=private.get("powershell"))
    return dict(schema_version=1, experiment="local_snapshot_fallback", baseline_commit=baseline_commit, candidate_commit=candidate_commit,
                summary_script_lf_sha256=hashlib.sha256(Path(__file__).read_bytes().replace(b"\r\n", b"\n")).hexdigest(),
                source_association="Commit identities supplied by the experiment operator; exact measured executable/scorer/script SHA-256 are retained per run. This archive does not represent later candidate revisions.",
                baseline_environment=environment, sources=sources, planned_attempts=sum(source["planned_attempts"] for source in sources),
                recorded_attempts=len(trials), completed_attempts=sum(row["status"] == "completed" for row in trials),
                fallback_passes=sum(row.get("acceptance_pass") is True for row in trials), etw_release_gate=None,
                measurement="Medians include every available sample in its declared group, including failed samples; measured_samples exposes missing metrics. Groups/runs are not pooled. No ETW attribution or physical-disk throughput claim. Parent CPU/memory/I/O scope and sampling follow v04-matrix.py.",
                publication="Numeric public projection only. All attempts retained. Oracle row counts and hashes are retained; individual fixture paths/rows, process identities, global inventory, databases and logs are omitted. Full public projections and private evidence remain separate.",
                groups=aggregates, trials=trials)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", action="append", required=True, metavar="LABEL=PUBLIC_JSON")
    parser.add_argument("--baseline-commit", required=True)
    parser.add_argument("--candidate-commit", required=True)
    parser.add_argument("--baseline-environment", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if any(not re.fullmatch(r"[a-fA-F0-9]{40}", value) for value in (args.baseline_commit, args.candidate_commit)):
        parser.error("full Git commit SHAs required")
    inputs = [(label, Path(path)) for label, path in (value.split("=", 1) for value in args.input)]
    report = summarize(inputs, args.baseline_commit, args.candidate_commit, args.baseline_environment)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("recorded_attempts", "completed_attempts", "fallback_passes")}))


if __name__ == "__main__":
    main()
