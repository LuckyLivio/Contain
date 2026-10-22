"""Windows fixture matrix. No build, download, elevation, vendor install or uninstall.

Both executables use the same fixture and frozen score-fixture.py. All attempts
are journaled before execution. Raw exports, profiles and databases stay local.
"""
import argparse
from contextlib import closing
import ctypes
from ctypes import wintypes
import hashlib
import importlib.util
import itertools
import json
import os
from pathlib import Path
import platform
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import uuid


SCRIPT = Path(__file__).resolve()
SPEC = importlib.util.spec_from_file_location("v04_frozen_oracle", SCRIPT.with_name("score-fixture.py"))
ORACLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ORACLE)


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def target_operations(files, workers):
    counts = [files // workers + int(worker < files % workers) for worker in range(workers)]
    return sum(2 * count + (count + 1) // 2 for count in counts)


def quality_gate(capture, score, files, workers, noise, snapshot):
    """Retain the historical strict 1000 and 95% other-scale ETW gates."""
    backend = capture["backend"]
    pipeline = backend.get("pipeline") or {}
    stream = backend.get("stream") or {}
    target = score["groups"]["target_success"]
    checks = {
        "target_denominator": target["expected"] == target_operations(files, workers),
        "noise_denominator": score["groups"]["noise_success"]["expected"] == noise * 3,
        "no_false_attribution": score["incorrect_attribution"] == score["false_positive_target_events"] == 0,
        "no_drain_timeout": capture["stats"]["drain_timed_out"] is False,
    }
    if snapshot:
        checks.update(etw_disabled=backend["etw_file"] == "disabled",
                      no_source_promotion=not any(event["event_type"] in ("file", "registry", "file_state", "registry_state")
                          and event["confidence"] != "Unknown" for event in capture.get("events", [])),
                      no_state_promotion=all(item["confidence"] == "Unknown"
                          for group in ("files", "registry") for item in capture.get(group, [])))
    else:
        balanced = all(type(pipeline.get(key)) is int for key in
                       ("callback_records", "decode_attempted", "unsupported_records", "decode_succeeded", "decode_failed", "enqueued", "dequeued", "queue_pending"))
        balanced = balanced and (
            pipeline["callback_records"] == pipeline["decode_attempted"] + pipeline["unsupported_records"]
            and pipeline["decode_attempted"] == pipeline["decode_succeeded"] + pipeline["decode_failed"]
            and pipeline["enqueued"] == pipeline["dequeued"] + pipeline["queue_pending"])
        # Older baseline releases have no stream journal. Check the additive
        # contract when present without silently treating missing counters as 0.
        if stream:
            balanced = balanced and all(type(stream.get(key)) is int for key in
                                       ("accepted", "persisted", "failed", "quota_dropped"))
            balanced = balanced and all(type(pipeline.get(key)) is int for key in
                                       ("deliberately_filtered", "failed_without_output", "queue_overflow", "enqueue_disconnected"))
            balanced = balanced and type(backend.get("events_received")) is int
            balanced = balanced and pipeline["callback_records"] == pipeline["deliberately_filtered"] + backend["events_received"] + pipeline["failed_without_output"]
            balanced = balanced and backend["events_received"] == pipeline["enqueued"] + pipeline["queue_overflow"] + pipeline["enqueue_disconnected"]
            balanced = balanced and pipeline["dequeued"] == stream.get("accepted")
            balanced = balanced and stream["accepted"] == stream["persisted"] + stream["failed"] + stream["quota_dropped"]
        minimum = 1.0 if files == 1000 else 0.95
        checks.update(etw_active=backend["etw_file"] == "active", pipeline_balanced=balanced,
                      final_queue_empty=pipeline.get("queue_pending") == 0,
                      context_loss_zero=pipeline.get("context_evictions") == 0,
                      application_loss_zero=backend.get("dropped_events") == 0,
                      decode_loss_zero=backend.get("decode_errors") == 0,
                      etw_event_loss_zero=backend.get("etw_events_lost") == 0,
                      etw_buffer_loss_zero=backend.get("etw_buffers_lost") == 0,
                      target_observation=target["observation_rate"] is not None and target["observation_rate"] >= minimum,
                      target_attribution=target["correct_attribution_rate"] is not None and target["correct_attribution_rate"] >= minimum)
    return {"mode": "fallback_conservatism" if snapshot else "useful_etw_attribution",
            "checks": checks, "pass": all(checks.values()), "etw_release_gate": None if snapshot else all(checks.values())}


class ProcessMetrics:
    """A retained process handle allows a final CPU/I/O sample after exit."""
    def __init__(self, pid):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        self.kernel.GetProcessTimes.argtypes = (wintypes.HANDLE,) + (ctypes.POINTER(wintypes.FILETIME),) * 4
        self.kernel.GetProcessIoCounters.argtypes = (wintypes.HANDLE, ctypes.c_void_p)
        self.psapi.GetProcessMemoryInfo.argtypes = (wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD)
        self.handle = self.kernel.OpenProcess(0x0400 | 0x0010, False, pid)
        self.values = {}
        self.errors = set()
        if not self.handle:
            self.errors.add("OpenProcess:" + str(ctypes.get_last_error()))

    def sample(self):
        if not self.handle:
            return

        class Memory(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("page_faults", wintypes.DWORD)] + [(name, ctypes.c_size_t) for name in
                ("peak_working_set", "working_set", "peak_paged_pool", "paged_pool", "peak_nonpaged_pool", "nonpaged_pool", "pagefile", "peak_pagefile", "private_usage")]

        class IO(ctypes.Structure):
            _fields_ = [(name, ctypes.c_ulonglong) for name in
                        ("read_operations", "write_operations", "other_operations", "read_bytes", "write_bytes", "other_bytes")]

        memory = Memory()
        memory.cb = ctypes.sizeof(memory)
        if self.psapi.GetProcessMemoryInfo(self.handle, ctypes.byref(memory), memory.cb):
            self.values["parent_peak_working_set_bytes"] = int(memory.peak_working_set)
            self.values["parent_sampled_peak_private_bytes"] = max(self.values.get("parent_sampled_peak_private_bytes", 0), int(memory.private_usage))
        else:
            self.errors.add("GetProcessMemoryInfo:" + str(ctypes.get_last_error()))
        counters = IO()
        if self.kernel.GetProcessIoCounters(self.handle, ctypes.byref(counters)):
            self.values["parent_io"] = {name: int(getattr(counters, name)) for name, _ in IO._fields_}
        else:
            self.errors.add("GetProcessIoCounters:" + str(ctypes.get_last_error()))
        creation, end, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if self.kernel.GetProcessTimes(self.handle, ctypes.byref(creation), ctypes.byref(end), ctypes.byref(kernel), ctypes.byref(user)):
            ticks = lambda value: (value.dwHighDateTime << 32) | value.dwLowDateTime
            self.values["parent_cpu_seconds"] = (ticks(kernel) + ticks(user)) / 10_000_000
        else:
            self.errors.add("GetProcessTimes:" + str(ctypes.get_last_error()))

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def sampled_size(path):
    try:
        return path.stat().st_size
    except FileNotFoundError:
        return 0


def backup_database(source, destination):
    # Include committed WAL state. A byte-copy of only the .db would lose it.
    with closing(sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True)) as original:
        with closing(sqlite3.connect(destination)) as copied:
            original.backup(copied)


def run_trial(args, directory, mode, files, workers, noise_count, trial, seed, seed_index=0):
    directory.mkdir()
    executable = args.baseline if mode == "baseline" else args.candidate
    root = Path(tempfile.gettempdir()) / ("contain-demo-v04-" + uuid.uuid4().hex)
    truth_root = root.with_name(root.name + "-truth")
    database = directory / "capture.db"
    name = "V04-" + uuid.uuid4().hex
    record = dict(mode=mode, files=files, workers=workers, noise_files=noise_count, trial=trial, seed_index=seed_index,
                  seed_database=str(seed) if seed else None, status="started", acceptance_pass=False,
                  fixture_root=str(root), truth_root=str(truth_root), app=name,
                  expected_target_operations=target_operations(files, workers), expected_noise_operations=noise_count * 3)
    write_json(directory / "measurement.json", record)
    noise = process = metrics = start = None
    db_peak = wal_peak = 0
    environment = os.environ.copy()
    # Freeze experimental settings explicitly, so inherited developer state does
    # not silently change either arm. The report records these choices.
    for key in ("CONTAIN_PROFILE_PATH", "CONTAIN_HOTPATH_VARIANT", "CONTAIN_CHECKPOINT_VARIANT", "CONTAIN_WAL_BUDGET_BYTES", "CONTAIN_ETW_EVENT_ID_FILTER"):
        environment.pop(key, None)
    environment["CONTAIN_PROFILE_PATH"] = str(directory / "profile.json")
    environment["CONTAIN_FIXTURE_TRUTH"] = str(truth_root)
    variant = args.baseline_variant if mode == "baseline" else args.candidate_variant
    if variant:
        environment["CONTAIN_HOTPATH_VARIANT"] = variant
    try:
        for path in (root, truth_root):
            path.mkdir()
            (path / ".contain-demo-marker").write_text("Contain test fixture", encoding="utf-8")
        if seed:
            backup_database(seed, database)
        record["database_initial_bytes"] = sampled_size(database)
        if noise_count:
            with (directory / "noise.stdout.txt").open("wb") as stdout, (directory / "noise.stderr.txt").open("wb") as stderr:
                noise = subprocess.Popen([str(args.fixture), "--root", str(root), "--role", "noise", "--files", str(noise_count)],
                                         stdout=stdout, stderr=stderr, env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
            deadline = time.monotonic() + 30
            while not (root / ".noise-ready").exists():
                if noise.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError("noise readiness failed")
                time.sleep(0.01)
        command = [str(executable), "--db", str(database), "install", str(args.fixture), "--name", name, "--watch", str(root)]
        if args.snapshot_only:
            command.append("--no-etw")
        command += ["--", "--root", str(root), "--role", "stress", "--files", str(files), "--workers", str(workers)]
        record["command"] = command
        write_json(directory / "measurement.json", record)
        start = time.perf_counter()
        with (directory / "capture.stdout.txt").open("wb") as stdout, (directory / "capture.stderr.txt").open("wb") as stderr:
            process = subprocess.Popen(command, stdout=stdout, stderr=stderr, env=environment, creationflags=subprocess.CREATE_NO_WINDOW)
        metrics = ProcessMetrics(process.pid)
        db_peak = wal_peak = 0
        while process.poll() is None:
            metrics.sample()
            db_peak = max(db_peak, sampled_size(database))
            wal_peak = max(wal_peak, sampled_size(Path(str(database) + "-wal")))
            if time.perf_counter() - start > args.timeout:
                process.kill()
                process.wait()
                record["timeout_terminated_capture"] = True
                raise RuntimeError("capture exceeded configured timeout; fixture retained")
            time.sleep(0.02)
        record["elapsed_seconds"] = time.perf_counter() - start
        metrics.sample()
        record.update(metrics.values)
        record["metric_errors"] = sorted(metrics.errors)
        record["parent_cpu_percent_machine_average"] = (metrics.values["parent_cpu_seconds"] / record["elapsed_seconds"] / (os.cpu_count() or 1) * 100
                                                        if "parent_cpu_seconds" in metrics.values else None)
        record.update(capture_exit=process.returncode, sampled_db_peak_bytes=db_peak, sampled_wal_peak_bytes=wal_peak,
                      sqlite_bytes=sampled_size(database))
        if process.returncode:
            raise RuntimeError(f"capture exit {process.returncode}")
        if noise and noise.wait(timeout=30):
            raise RuntimeError("independent noise failed")
        with (directory / "capture.json").open("wb") as stdout, (directory / "export.stderr.txt").open("wb") as stderr:
            subprocess.run([str(executable), "--db", str(database), "inspect", name, "--json"],
                           stdout=stdout, stderr=stderr, check=True, timeout=args.timeout, creationflags=subprocess.CREATE_NO_WINDOW)
        capture = json.loads((directory / "capture.json").read_text(encoding="utf-8-sig"))["data"]
        truth = [json.loads(line) for source in sorted(truth_root.glob("ground-truth-*.jsonl"))
                 for line in source.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        write_json(directory / "ground-truth.json", truth)
        score = ORACLE.score(capture, truth, args.sid)
        write_json(directory / "score.json", score)
        record.update(stats=capture["stats"], backend=capture["backend"], quality=capture["quality"],
                      score={key: value for key, value in score.items() if key != "rows"})
        record["gate"] = quality_gate(capture, score, files, workers, noise_count, args.snapshot_only)
        record["acceptance_pass"] = record["gate"]["pass"]
        record["status"] = "completed"
    except (OSError, ValueError, KeyError, RuntimeError, sqlite3.Error, subprocess.SubprocessError) as error:
        record["status"] = "failed"
        record["harness_error"] = str(error)
    finally:
        if process and process.poll() is None:
            process.kill()
            process.wait()
            record["capture_terminated_on_harness_exit"] = True
        if metrics:
            metrics.sample()
            record.update(metrics.values)
            record["metric_errors"] = sorted(metrics.errors)
            if "elapsed_seconds" not in record and start is not None:
                record["elapsed_seconds"] = time.perf_counter() - start
            if "parent_cpu_seconds" in record and record.get("elapsed_seconds"):
                record["parent_cpu_percent_machine_average"] = record["parent_cpu_seconds"] / record["elapsed_seconds"] / (os.cpu_count() or 1) * 100
            record.update(sampled_db_peak_bytes=db_peak, sampled_wal_peak_bytes=wal_peak)
            metrics.close()
        if noise and noise.poll() is None:
            noise.kill()
            noise.wait()
            record["noise_terminated_on_harness_exit"] = True
        # Preserve raw independent logs even when capture/scoring failed.
        if truth_root.exists():
            journal_directory = directory / "truth-journals"
            journal_directory.mkdir(exist_ok=True)
            for journal in truth_root.glob("ground-truth-*.jsonl"):
                shutil.copy2(journal, journal_directory / journal.name)
        if record["status"] == "completed" and not args.keep_fixtures:
            # The fixture verifies direct TEMP child, exact marker and no reparse
            # points before it deletes its own tree and matching HKCU fixture key.
            for path in (root, truth_root):
                try:
                    cleanup = subprocess.run([str(args.fixture), "--root", str(path), "--cleanup"], capture_output=True,
                                             timeout=30, creationflags=subprocess.CREATE_NO_WINDOW)
                    if cleanup.returncode:
                        raise RuntimeError(cleanup.stderr.decode("utf-8", errors="replace"))
                except (OSError, RuntimeError, subprocess.SubprocessError) as error:
                    record["cleanup_error"] = str(error)
                    record["acceptance_pass"] = False
                    record["status"] = "failed"
        write_json(directory / "measurement.json", record)
    return record


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    for name in ("baseline", "candidate", "fixture", "output"):
        result.add_argument("--" + name, required=True, type=Path)
    result.add_argument("--files", nargs="+", type=int, default=[100, 500, 1000, 3000, 10000])
    result.add_argument("--workers", nargs="+", type=int, default=[1, 4, 8])
    result.add_argument("--noise-files", nargs="+", type=int, default=[100])
    result.add_argument("--trials", type=int, default=3)
    result.add_argument("--seed-database", action="append", type=Path, default=[])
    result.add_argument("--baseline-variant", choices=["D0", "D1", "D2", "D3"])
    result.add_argument("--candidate-variant", choices=["D0", "D1", "D2", "D3"])
    result.add_argument("--snapshot-only", action="store_true")
    result.add_argument("--keep-fixtures", action="store_true")
    result.add_argument("--timeout", type=int, default=600)
    return result


def main():
    cli = parser()
    args = cli.parse_args()
    if sys.platform != "win32":
        cli.error("Windows required; this script never runs vendor installers")
    if not (1 <= args.trials <= 5 and 10 <= args.timeout <= 3600
            and all(4 <= count <= 100000 for count in args.files)
            and all(1 <= count <= 64 for count in args.workers)
            and all(0 <= count <= 10000 for count in args.noise_files)):
        cli.error("bounds: trials 1..5, timeout 10..3600, files 4..100000, workers 1..64, noise 0..10000")
    for name in ("baseline", "candidate", "fixture"):
        path = getattr(args, name).resolve(strict=True)
        if not path.is_file():
            cli.error(f"--{name} must name an existing executable")
        setattr(args, name, path)
    args.seed_database = [path.resolve(strict=True) for path in args.seed_database]
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    sid_command = ["powershell", "-NoProfile", "-Command", "[Security.Principal.WindowsIdentity]::GetCurrent().User.Value"]
    args.sid = subprocess.check_output(sid_command, text=True, creationflags=subprocess.CREATE_NO_WINDOW).strip()
    records = []
    planned = []
    for files, workers, noise_count, seed_index, trial in itertools.product(args.files, args.workers, args.noise_files,
                                                                          range(len(args.seed_database) + 1), range(1, args.trials + 1)):
        for mode in (["baseline", "candidate"] if trial % 2 else ["candidate", "baseline"]):
            planned.append(dict(mode=mode, files=files, workers=workers, noise_files=noise_count, seed_index=seed_index, trial=trial))
    environment = dict(schema_version=1, platform=platform.platform(), logical_processors=os.cpu_count(), python=sys.version,
                       snapshot_only=args.snapshot_only, baseline_variant=args.baseline_variant, candidate_variant=args.candidate_variant,
                       executables={name: dict(path=str(getattr(args, name)), sha256=sha(getattr(args, name))) for name in ("baseline", "candidate", "fixture")},
                       scorer_sha256=sha(SCRIPT.with_name("score-fixture.py")), script_sha256=sha(SCRIPT),
                       seeds=[dict(path=str(path), sha256=sha(path), bytes=path.stat().st_size,
                                   wal_bytes=sampled_size(Path(str(path) + "-wal"))) for path in args.seed_database],
                       planned_attempts=planned,
                       measurement="Capture wall through process exit; export/scoring excluded. Parent CPU and I/O are cumulative handle counters. I/O transfer bytes are logical process I/O, not physical disk traffic. Parent memory excludes descendants and ETW kernel buffers; private memory and DB/WAL peaks sampled every 20ms. Background load/caches uncontrolled. No actual ETW loss rate: enabled-provider total is unavailable. Snapshot passes never establish ETW attribution.")
    write_json(args.output / "environment.json", environment)
    try:
        for index, plan in enumerate(planned, 1):
            directory = args.output / f"{index:04}-{plan['mode']}-{plan['files']}-w{plan['workers']}-n{plan['noise_files']}-db{plan['seed_index']}-t{plan['trial']}"
            seed = args.seed_database[plan["seed_index"] - 1] if plan["seed_index"] else None
            record = run_trial(args, directory, plan["mode"], plan["files"], plan["workers"], plan["noise_files"], plan["trial"], seed, plan["seed_index"])
            records.append(record)
            write_json(args.output / "comparison.json", dict(environment=environment, trials=records, completed_attempts=len(records), planned_attempts=len(planned)))
            print(json.dumps({key: record.get(key) for key in ("mode", "files", "workers", "noise_files", "trial", "status", "elapsed_seconds", "acceptance_pass", "harness_error")}), flush=True)
    finally:
        write_json(args.output / "comparison.json", dict(environment=environment, trials=records, completed_attempts=len(records),
                                                        planned_attempts=len(planned), interrupted=len(records) != len(planned)))
    return 0 if all(row["acceptance_pass"] for row in records) else 2


if __name__ == "__main__":
    raise SystemExit(main())
