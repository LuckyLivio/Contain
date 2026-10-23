"""Run already-built direct read API fixtures; never starts ETW or an installer.

The same workload uses a process/pipe barrier, not sleeps, to attempt operations
after the read API returns and before retained candidate handles are released.
Every error and final byte/name state is preserved; failed mutations are data.
"""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import sys


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def summarize(attempts):
    rows = {}
    for attempt in attempts:
        if attempt.get("result") is None:
            continue
        for case in attempt["result"]["cases"]:
            key = (attempt["arm"], case["case"])
            row = rows.setdefault(key, {"arm": key[0], "case": key[1], "attempts": 0,
                "success": 0, "error_codes": {}, "after_release_success": 0, "final_state_matches_control": 0})
            row["attempts"] += 1
            operation = case["during"]["operation"]
            row["success"] += int(operation["success"])
            if not operation["success"]:
                error = str(operation["error_code"])
                row["error_codes"][error] = row["error_codes"].get(error, 0) + 1
            row["after_release_success"] += int(case["after_release_control"]["operation"]["success"])
            row["final_state_matches_control"] += int(case["during"]["final_state"] == case["after_release_control"]["final_state"])
    return list(rows.values())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--baseline-revision", required=True)
    parser.add_argument("--candidate-revision", required=True)
    parser.add_argument("--candidate-mode", choices=["leased", "shared"], default="leased")
    parser.add_argument("--trials", type=int, default=3)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists() or args.trials < 1:
        parser.error("output must be new and trials positive")
    args.output.mkdir(parents=True)
    baseline, candidate = args.baseline.resolve(), args.candidate.resolve()
    manifest = {"schema": 1, "scope": "direct_final_read_api_post_hash_retention_window",
        "etw_started": False, "live_cli_experiment": False,
        "interpretation": "Barrier-controlled interference, not natural overlap probability or attribution accuracy",
        "platform": platform.platform(), "python": sys.version, "trials": args.trials,
        "baseline": {"revision": args.baseline_revision, "binary_sha256": sha(baseline)},
        "candidate": {"revision": args.candidate_revision, "binary_sha256": sha(candidate)},
        "runner_sha256": sha(Path(__file__)), "attempts": []}
    for trial in range(args.trials):
        arms = [("no_monitor", candidate, "none"), ("baseline", baseline, "shared"), ("candidate", candidate, args.candidate_mode)]
        arms = arms[trial % len(arms):] + arms[:trial % len(arms)]
        for arm, binary, mode in arms:
            stem = f"trial-{trial + 1:02}-{arm}"
            command = [str(binary), "run", str((args.output / (stem + "-files")).resolve()), mode]
            attempt = {"trial": trial + 1, "arm": arm, "mode": mode, "command": command, "result": None}
            manifest["attempts"].append(attempt)
            write(args.output / "results.json", manifest)
            try:
                result = subprocess.run(command, capture_output=True, timeout=120)
                (args.output / (stem + ".stdout")).write_bytes(result.stdout)
                (args.output / (stem + ".stderr")).write_bytes(result.stderr)
                attempt["exit_code"] = result.returncode
                if result.returncode == 0:
                    attempt["result"] = json.loads(result.stdout)
            except (OSError, subprocess.TimeoutExpired, ValueError) as error:
                attempt["harness_error"] = repr(error)
            write(args.output / "results.json", manifest)
    manifest["binaries_unchanged"] = (sha(baseline) == manifest["baseline"]["binary_sha256"] and sha(candidate) == manifest["candidate"]["binary_sha256"])
    manifest["all_attempts_collected"] = all(attempt["result"] is not None for attempt in manifest["attempts"])
    manifest["summary"] = summarize(manifest["attempts"])
    write(args.output / "results.json", manifest)
    print(json.dumps({key: manifest[key] for key in ("all_attempts_collected", "binaries_unchanged", "summary")}, indent=2))
    return 0 if manifest["all_attempts_collected"] and manifest["binaries_unchanged"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
