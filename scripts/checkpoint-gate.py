"""Add storage-experiment checks to an existing, unchanged fixture quality gate."""
import argparse
import json
from pathlib import Path


WAL_BUDGET_BYTES = 512 * 1024 * 1024


def validate(record, variant):
    stream = (record.get("backend") or {}).get("stream") or {}
    checkpoint = stream.get("checkpoint") or {}
    checks = {
        "variant": stream.get("checkpoint_variant") == variant,
        "wal_journal": stream.get("sqlite_journal_mode") == "wal",
        "normal_synchronous": stream.get("sqlite_synchronous") == 1,
        "capture_autocheckpoint": stream.get("wal_autocheckpoint_pages") == (0 if variant == "E1" else 4096),
        "wal_budget": stream.get("wal_budget_bytes") == WAL_BUDGET_BYTES,
        "wal_peak_measured": isinstance(stream.get("wal_peak_bytes"), int) and not isinstance(stream.get("wal_peak_bytes"), bool) and stream["wal_peak_bytes"] >= 0,
        "wal_peak_below_budget": type(stream.get("wal_peak_bytes")) is int and 0 <= stream["wal_peak_bytes"] < WAL_BUDGET_BYTES,
        "wal_sampled": type(stream.get("wal_samples")) is int and stream["wal_samples"] > 0,
        "wal_budget_not_exceeded": stream.get("wal_budget_exceeded") is False,
        "disk_reserve": stream.get("disk_reserve_bytes") == 64 * 1024 * 1024,
        "disk_free_above_reserve": type(stream.get("disk_free_min_bytes")) is int and stream["disk_free_min_bytes"] > 64 * 1024 * 1024,
        "stream_error_absent": "error" in stream and stream["error"] is None,
    }
    if variant == "E1":
        log_frames = checkpoint.get("log_frames")
        checkpointed_frames = checkpoint.get("checkpointed_frames")
        checks.update(
            passive_checkpoint=checkpoint.get("mode") == "PASSIVE",
            checkpoint_not_busy=checkpoint.get("busy") == 0,
            checkpoint_complete=stream.get("checkpoint_completed") is True,
            checkpoint_all_frames=(
                type(log_frames) is int and type(checkpointed_frames) is int
                and log_frames >= 0 and log_frames == checkpointed_frames
            ),
            checkpoint_error_absent="error" in checkpoint and checkpoint["error"] is None,
            autocheckpoint_restored=stream.get("restored_wal_autocheckpoint_pages") == 4096,
        )
    else:
        checks.update(
            no_explicit_checkpoint=stream.get("checkpoint") is None,
            no_explicit_checkpoint_completion=stream.get("checkpoint_completed") is False,
        )
    original_pass = record.get("acceptance_pass") is True
    record["checkpoint_validation"] = dict(
        expected_variant=variant,
        original_quality_pass=original_pass,
        checks=checks,
        pass_all=all(checks.values()),
    )
    record["acceptance_pass"] = original_pass and all(checks.values())
    return record


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("record", type=Path)
    parser.add_argument("variant", choices=("E0", "E1"))
    args = parser.parse_args()
    record = json.loads(args.record.read_text(encoding="utf-8-sig"))
    args.record.write_text(json.dumps(validate(record, args.variant), indent=2), encoding="utf-8")
