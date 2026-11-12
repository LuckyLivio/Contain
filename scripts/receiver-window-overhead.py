"""Retain one fixed alternating offline profile-cost experiment, without retries.

The binary links actual production profile code through contain-offline. Numeric
queue snapshots and SHA256 work are synthetic, not Windows ingest or ETW truth.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess


MODES = ("off", "existing", "window")
SAMPLE_ORDER = (MODES + tuple(reversed(MODES))) * 3
PROFILE_ENV = ("CONTAIN_PROFILE_PATH", "CONTAIN_PROFILE_BACKLOG", "CONTAIN_PROFILE_THREAD_CPU")
INPUTS = ("Cargo.toml", "Cargo.lock", "tools/offline/Cargo.toml", "tools/offline/src/lib.rs",
          "tools/offline/src/bin/receiver-window-check.rs", "crates/contain-core/src/profile.rs",
          "crates/contain-core/src/profile/backlog.rs", "scripts/receiver-window-overhead.py")


def file_hash(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def summarize(rows, spans):
    measured = [row for row in rows if not row["warmup"]]
    identity = {(row["result"]["output_sha256"], json.dumps(row["result"]["snapshot_input"], sort_keys=True)) for row in rows}
    if len(identity) != 1 or any(row["result"]["spans"] != spans for row in rows):
        raise ValueError("fixed work or numeric inputs changed between profile modes")
    medians = {}
    for mode in MODES:
        values = [row["result"]["elapsed_ns"] / spans for row in measured if row["mode"] == mode]
        if len(values) != 6:
            raise ValueError("each fixed mode must retain all six samples")
        medians[mode] = statistics.median(values)
    return {"median_ns_per_span": medians,
            "existing_profile_increment_ns_per_span": medians["existing"] - medians["off"],
            "window_increment_ns_per_span": medians["window"] - medians["existing"],
            "fixed_work_and_snapshot_inputs_equal": True}


def validate_profile(data, mode, spans):
    windows = data.get("backlog_windows")
    if mode == "existing":
        if windows is not None:
            raise ValueError("existing mode unexpectedly enabled receiver windows")
        return None
    if not isinstance(windows, dict) or windows.get("receiver_observations") != spans:
        raise ValueError("window mode did not record every fixed receiver span")
    if (windows.get("backlog_threshold_records"), windows.get("pre_capacity"), windows.get("post_capacity"),
            windows.get("maximum_records_per_window")) != (1024, 64, 64, 129):
        raise ValueError("receiver window bounds differ from the frozen plan")
    sizes = {}
    for key in ("first_backlog", "first_overflow", "maximum_backlog"):
        window = windows.get(key)
        if not isinstance(window, dict) or not 1 <= len(window.get("records", [])) <= 129:
            raise ValueError("fixed anomaly window is missing or unbounded")
        if not window.get("post_capacity_full") or window.get("post_records") != 64:
            raise ValueError("fixed anomaly lacks its available post-window context")
        if window["pre_records"] + window["post_records"] + 1 != len(window["records"]):
            raise ValueError("window context accounting is inconsistent")
        sizes[key] = len(window["records"])
    return sizes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--spans", type=int, default=200000)
    args = parser.parse_args()
    if not 9000 <= args.spans <= 1000000:
        parser.error("--spans must be 9000..1000000 so late anomaly windows can complete")
    binary = args.binary.resolve(strict=True)
    binary_sha256 = file_hash(binary)
    root = Path(__file__).resolve().parents[1]
    inputs = {name: file_hash(root / name) for name in INPUTS}
    source_revision = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"],
                                     capture_output=True, text=True, check=True).stdout.strip()
    patch = subprocess.run(["git", "-C", str(root), "diff", "--no-ext-diff", "--binary", "HEAD"],
                           capture_output=True, check=True).stdout
    args.output.mkdir(parents=True, exist_ok=False)
    rows = []
    report = {"protocol": "contain-receiver-window-offline-overhead-1", "accepted": False,
              "synthetic": True, "real_etw": False, "native_ingest": False,
              "binary_sha256": binary_sha256, "spans_per_sample": args.spans,
              "source_revision": source_revision, "tracked_patch_at_freeze_sha256": hashlib.sha256(patch).hexdigest(),
              "input_sha256": inputs,
              "warmup_order": list(MODES), "sample_order": list(SAMPLE_ORDER), "retries": 0,
              "samples": rows,
              "scope": "Same immutable release binary and fixed SHA256 work. Off, existing profile, and existing profile plus window. No ETW or Windows ingest overhead claim; profile JSON serialization is outside the binary's timed loop. CPU profiling is disabled in all modes; OS scheduling is not controlled."}
    if hasattr(os, "getloadavg"):
        report["host_load_average_before"] = os.getloadavg()
    try:
        for index, (warmup, mode) in enumerate([(True, mode) for mode in MODES] + [(False, mode) for mode in SAMPLE_ORDER]):
            if file_hash(binary) != binary_sha256:
                raise ValueError("binary changed after experiment freeze")
            if any(file_hash(root / name) != digest for name, digest in inputs.items()):
                raise ValueError("offline measurement inputs changed after experiment freeze")
            label = f"{index + 1:02d}-{mode}"
            profile_path = args.output.resolve() / (label + "-profile.json")
            env = dict(os.environ)
            for name in PROFILE_ENV:
                env.pop(name, None)
            if mode != "off":
                env["CONTAIN_PROFILE_PATH"] = str(profile_path)
            if mode == "window":
                env["CONTAIN_PROFILE_BACKLOG"] = "1"
            row = {"number": index + 1, "warmup": warmup, "mode": mode}
            rows.append(row)
            try:
                completed = subprocess.run([str(binary), "--spans", str(args.spans)], env=env,
                                           text=True, capture_output=True, timeout=60, check=False)
            except subprocess.TimeoutExpired as error:
                row["timed_out"] = True
                for name, data in (("stdout", error.stdout), ("stderr", error.stderr)):
                    (args.output / (label + f"-{name}.txt")).write_text(
                        data.decode("utf-8", errors="replace") if isinstance(data, bytes) else data or "", encoding="utf-8")
                raise RuntimeError(f"sample {index + 1} timed out; partial output retained without retry") from error
            row["exit_code"] = completed.returncode
            (args.output / (label + "-stdout.json")).write_text(completed.stdout, encoding="utf-8")
            (args.output / (label + "-stderr.txt")).write_text(completed.stderr, encoding="utf-8")
            if completed.returncode != 0:
                raise RuntimeError(f"sample {index + 1} failed; retained without retry")
            row["result"] = json.loads(completed.stdout)
            if row["result"]["profile_enabled"] != (mode != "off"):
                raise ValueError("profile activation differs from fixed mode")
            if row["result"]["debug_assertions_disabled"] is not True:
                raise ValueError("use the frozen release binary for offline cost samples")
            if (mode != "off") != profile_path.is_file():
                raise ValueError("profile output presence differs from fixed mode")
            if mode != "off":
                row["window_records"] = validate_profile(json.loads(profile_path.read_text(encoding="utf-8")), mode, args.spans)
        report.update(summarize(rows, args.spans))
        report["accepted"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        if hasattr(os, "getloadavg"):
            report["host_load_average_after"] = os.getloadavg()
        (args.output / "summary.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "samples"}, indent=2))


if __name__ == "__main__":
    main()
