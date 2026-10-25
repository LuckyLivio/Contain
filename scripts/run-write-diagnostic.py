"""One frozen, eight-file A/B/C/D diagnostic; never a release acceptance run.

No build, elevation, downloads, retries, cleanup, workload expansion or rescoring.
The fixture may delete only its own fresh C/D test files. All evidence stays local.
"""
import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sqlite3
import sys
import tempfile
import time
import traceback
import uuid


def sha(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def save(path, value):
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".new")
    with temporary.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2, allow_nan=False)
        stream.write("\n")
    temporary.replace(path)


def fixture_variant(plan):
    if plan.get("protocol") == "contain-write-diagnostic-entry-1":
        if plan.get("fixture_variant", "original") != "original":
            raise ValueError("The original protocol cannot enable a fixture variant")
        return "original"
    if plan.get("protocol") == "contain-write-diagnostic-entry-2":
        if plan.get("fixture_variant") != "transient_metadata_handles_v1":
            raise ValueError("The identity protocol requires its explicit fixture variant")
        return "transient_metadata_handles_v1"
    raise ValueError("Unsupported diagnostic plan")


def verified_plan(path, expected):
    if sha(path) != expected:
        raise ValueError("Frozen plan SHA mismatch; nothing launched")
    plan = read(path)
    variant = fixture_variant(plan)
    if plan.get("sequences") != ["A", "B", "C", "D"] or plan.get("files_per_sequence") != 2 or plan.get("workers") != 1:
        raise ValueError("This entry permits only the fixed eight-file single-worker diagnostic")
    if plan.get("formal_acceptance") is not False:
        raise ValueError("Formal acceptance is not authorized by this entry")
    names = {entry["name"] for entry in plan["inputs"]}
    required = {"contain", "fixture", "runner", "analyzer", "clock", "matrix", "scorer", "build"}
    if variant != "original":
        required.add("python")
    if names != required or len(plan["inputs"]) != len(required):
        raise ValueError("Missing or duplicate frozen input")
    observed = []
    for entry in plan["inputs"]:
        actual = sha(entry["path"])
        if actual != entry["sha256"]:
            raise ValueError(f"Frozen input mismatch: {entry['name']}; nothing launched")
        observed.append(dict(name=entry["name"], path=entry["path"], sha256=actual))
    inputs = {entry["name"]: Path(entry["path"]) for entry in observed}
    if inputs["runner"].resolve() != Path(__file__).resolve():
        raise ValueError("Running script is not the frozen entry")
    if "python" in inputs and inputs["python"].resolve() != Path(sys.executable).resolve():
        raise ValueError("Running interpreter is not the frozen input")
    for owner, name, filename in (("matrix", "clock", "v04-clock-protocol.py"),
                                  ("matrix", "scorer", "score-fixture.py"),
                                  ("analyzer", "clock", "v04-clock-protocol.py")):
        if inputs[owner].with_name(filename).resolve() != inputs[name].resolve():
            raise ValueError(f"Frozen {name} is not the actual sibling imported by {owner}")
    return plan, inputs, observed


def identity_observer_artifact(truth, enabled, producer_finished):
    """Inventory only: the layered verifier validates this independent journal."""
    path = truth / "identity-observations.jsonl"
    result = dict(path=str(path), enabled=enabled, producer_finished=producer_finished,
                  bytes=None, sha256=None, observed_samples=None,
                  semantic_validation="unavailable_not_performed_by_entry",
                  availability="not_enabled" if not enabled else "unavailable_missing")
    if not enabled:
        return result
    try:
        before = path.stat()
        result["bytes"] = before.st_size
        if not producer_finished:
            result["availability"] = "unavailable_producer_may_be_running"
        elif before.st_size > 4 * 1024 * 1024:
            result["availability"] = "unavailable_size_limit"
        else:
            digest = sha(path)
            after = path.stat()
            if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
                result["availability"] = "unavailable_changed_during_inventory"
            else:
                result.update(sha256=digest, availability="present_unvalidated")
    except OSError as error:
        result["read_error"] = f"{type(error).__name__}: {error}"
    return result


def capture_environment(directory):
    environment = {key: value for key, value in os.environ.items() if not key.upper().startswith("CONTAIN_")}
    environment.update(CONTAIN_FILE_DIAGNOSTICS="1", CONTAIN_ETW_RAW_CLOCK_PATH=str(directory / "raw-clock.json"),
                       CONTAIN_PROFILE_PATH=str(directory / "profile.json"))
    return environment


def export_raw(database, exported, destination):
    """Keep the CLI export unchanged; make a bounded, separate private evidence view."""
    if any(Path(str(database) + suffix).exists() for suffix in ("-wal", "-shm", "-journal")):
        raise RuntimeError("Unexpected database sidecars; preserve them for a separate recovery audit")
    document = read(exported)
    session = document["data"]["id"]
    before = sha(database)
    connection = sqlite3.connect(database.resolve().as_uri() + "?mode=ro&immutable=1", uri=True)
    rows, byte_count = [], 0
    try:
        state = connection.execute("SELECT state FROM capture_runs WHERE session_id=?", (session,)).fetchone()
        if state != ("finished",):
            raise RuntimeError("Capture database is not finished")
        for (row,) in connection.execute("SELECT document FROM raw_stream WHERE session_id=? ORDER BY sequence", (session,)):
            byte_count += len(row.encode("utf-8"))
            if len(rows) >= 100_000 or byte_count > 128 * 1024 * 1024:
                raise RuntimeError("Small-diagnostic raw export limit exceeded; no partial chain claimed")
            rows.append(json.loads(row))
    finally:
        connection.close()
    after = sha(database)
    if before != after:
        raise RuntimeError("Database changed during read-only export")
    document["data"]["raw_events"] = rows
    document["private_diagnostic_export"] = dict(database_sha256=before, rows=len(rows), raw_json_bytes=byte_count,
                                                 source_file_records=sum(row.get("event_type") == "file" and row.get("evidence", {}).get("source") == "EtwFile" for row in rows),
                                                 file_diagnostic_records=sum(row.get("raw", {}).get("file_diagnostics") is not None for row in rows),
                                                 scope="all_committed_raw_stream_rows_not_truth_filtered")
    with destination.open("x", encoding="utf-8") as output:
        json.dump(document, output, allow_nan=False)
        output.write("\n")
    return document["private_diagnostic_export"]


def module(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--plan-sha256", required=True)
    parser.add_argument("--preflight", action="store_true", help="Read/hash inputs only; never start a capture")
    args = parser.parse_args()
    plan, inputs, before = verified_plan(args.plan, args.plan_sha256)
    variant = fixture_variant(plan)
    if args.preflight:
        print(json.dumps(dict(status="frozen_inputs_verified", runtime_commit=plan["runtime_commit"],
                              fixture_variant=variant, capture_executed=False, formal_acceptance=False, inputs=len(before))))
        return 0
    if os.name != "nt" or not ctypes.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Use the existing administrator window; this entry cannot elevate or fall back")
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    directory = Path(plan["output_parent"]) / f"admin-{stamp}-{uuid.uuid4().hex[:8]}"
    directory.mkdir(parents=False, exist_ok=False)
    container = Path(tempfile.gettempdir()) / f"contain-write-diagnostic-{uuid.uuid4().hex}"
    truth = container.with_name(container.name + "-truth")
    database = directory / "capture.db"
    record = dict(protocol=plan["protocol"], runtime_commit=plan["runtime_commit"], formal_acceptance=False,
                  plan_identity=dict(path=str(args.plan.resolve()), sha256=args.plan_sha256),
                  fixture_variant=variant, workload_expected_api_calls=16,
                  observer_expected_samples=28 if variant != "original" else None,
                  workload_journal=str(truth / "operations.jsonl"),
                  status="running", stage="prepare", started_utc=datetime.now(timezone.utc).isoformat(),
                  artifact_directory=str(directory), fixture_container=str(container), truth_directory=str(truth),
                  input_identity_before=before, input_identity_after=None, capture_exit=None, capture_pid=None,
                  capture_elapsed_seconds=None, export_elapsed_seconds=None, analysis_elapsed_seconds=None, parent_metrics=None,
                  sampled_db_peak_bytes=None, sampled_wal_peak_bytes=None)
    process = None
    meter = None
    start = None
    def checkpoint(stage):
        record["stage"] = stage
        save(directory / "attempt.json", record)
        print(f"Stage: {stage}; private artifacts: {directory}", flush=True)
    checkpoint("prepare")
    try:
        container.mkdir(exist_ok=False)
        with (container / ".contain-write-diagnostic-marker").open("xb") as marker:
            marker.write(b"Contain write diagnostic container v1")
        command = [str(inputs["contain"]), "--db", str(database), "install", str(inputs["fixture"]),
                   "--name", "write-diagnostic", "--watch", str(container), "--", "--container", str(container)]
        if variant != "original":
            command.append("--observe-file-identity")
        record["command"] = command
        record["capture_environment"] = {key: value for key, value in capture_environment(directory).items() if key.startswith("CONTAIN_")}
        metrics = module(inputs["matrix"], "write_diagnostic_metrics")
        checkpoint("capture")
        start = time.perf_counter()
        with (directory / "capture.stdout.log").open("xb") as stdout, (directory / "capture.stderr.log").open("xb") as stderr:
            process = subprocess.Popen(command, stdout=stdout, stderr=stderr, env=capture_environment(directory),
                                       creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
            record["capture_pid"] = process.pid
            checkpoint("capture_running")
            meter = metrics.ProcessMetrics(process.pid)
            pulse = start
            while process.poll() is None:
                meter.sample()
                for key, path in (("sampled_db_peak_bytes", database), ("sampled_wal_peak_bytes", Path(str(database) + "-wal"))):
                    if path.exists():
                        record[key] = max(record[key] or 0, path.stat().st_size)
                now = time.perf_counter()
                if now - pulse >= 2:
                    print(f"Capture running: {now - start:.1f}s, PID {process.pid}; output retained in files", flush=True)
                    pulse = now
                if now - start > 180:
                    # Do not kill an uncertain process tree or start another arm.
                    # Existing capture queues/disk quotas still bound its data.
                    raise RuntimeError("Capture exceeded 180s observation limit; child state retained, no further launch")
                time.sleep(0.05)
        record["capture_elapsed_seconds"] = time.perf_counter() - start
        meter.sample()
        record["parent_metrics"] = dict(meter.values)
        record["sampling_errors"] = sorted(meter.errors)
        record["capture_exit"] = process.returncode
        checkpoint("capture_finished")
        if process.returncode:
            raise RuntimeError(f"Capture exited {process.returncode}; preserve all evidence")
        checkpoint("export")
        started_export = time.perf_counter()
        with (directory / "capture.json").open("xb") as stdout, (directory / "export.stderr.log").open("xb") as stderr:
            subprocess.run([str(inputs["contain"]), "--db", str(database), "inspect", "write-diagnostic", "--json"],
                           stdout=stdout, stderr=stderr, check=True, timeout=60)
        record["raw_export"] = export_raw(database, directory / "capture.json", directory / "capture-evidence.json")
        record["export_elapsed_seconds"] = time.perf_counter() - started_export
        capture = read(directory / "capture.json")["data"]
        record["backend"] = capture["backend"]
        record["capture_stats"] = capture["stats"]
        if capture["backend"]["etw_file"] != "active" or capture["backend"]["etw_process"] != "active":
            raise RuntimeError("Real file/process ETW unavailable; snapshot fallback is not a diagnostic result")
        if not record["raw_export"]["source_file_records"]:
            raise RuntimeError("No actual file source records; capture is not a successful diagnostic")
        if not record["raw_export"]["file_diagnostic_records"]:
            raise RuntimeError("No attached file diagnostics; retained capture cannot explain the missing fields")
        checkpoint("analysis")
        started_analysis = time.perf_counter()
        with (directory / "analysis.stdout.log").open("xb") as stdout, (directory / "analysis.stderr.log").open("xb") as stderr:
            result = subprocess.run([sys.executable, str(inputs["analyzer"]), "--capture", str(directory / "capture-evidence.json"),
                                     "--operations", str(truth / "operations.jsonl"), "--summary", str(truth / "summary.json"),
                                     "--raw-clock", str(directory / "raw-clock.json"), "--output", str(directory / "analysis.json")],
                                    stdout=stdout, stderr=stderr, timeout=120)
        record["analysis_elapsed_seconds"] = time.perf_counter() - started_analysis
        record["analysis_exit"] = result.returncode
        if result.returncode:
            raise RuntimeError(f"Analysis exited {result.returncode}; original capture remains retained")
        record["status"] = "completed_pending_evidence_review"
        checkpoint("completed")
    except KeyboardInterrupt:
        record["status"] = "interrupted"
        record["failed_stage"] = record["stage"]
        record["error"] = "KeyboardInterrupt; child may still be running. No retry, cleanup or follow-on capture."
        raise
    except Exception as error:
        record["status"] = "failed"
        record["failed_stage"] = record["stage"]
        record["error"] = f"{type(error).__name__}: {error}"
        record["traceback"] = traceback.format_exc()
    finally:
        if meter:
            record["parent_metrics"] = dict(meter.values)
            record["sampling_errors"] = sorted(meter.errors)
            record["parent_metrics_complete"] = record["capture_elapsed_seconds"] is not None
            meter.close()
        if process:
            record["child_exit_at_entry_finish"] = process.poll()
            record["child_still_running_at_entry_finish"] = process.returncode is None
            if record["capture_elapsed_seconds"] is None and start is not None:
                record["partial_capture_observation_seconds"] = time.perf_counter() - start
        record["identity_observer_artifact"] = identity_observer_artifact(
            truth, variant != "original", record.get("child_still_running_at_entry_finish") is False)
        try:
            _, _, record["input_identity_after"] = verified_plan(args.plan, args.plan_sha256)
        except (OSError, ValueError) as error:
            record["post_identity_error"] = str(error)
            record["status"] = "failed"
        record["finished_utc"] = datetime.now(timezone.utc).isoformat()
        checkpoint(record["stage"])
        print(f"Private diagnostic artifacts: {directory}\nFixture and independent log retained: {container}; {truth}\nFormal acceptance: not executed", flush=True)
    return 0 if record["status"] == "completed_pending_evidence_review" else 1


if __name__ == "__main__":
    raise SystemExit(main())
