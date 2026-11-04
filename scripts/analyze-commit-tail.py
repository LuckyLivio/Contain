"""Bounded timing/counter diagnostics; separate from the frozen acceptance scorer.

Usage: python scripts/analyze-commit-tail.py PROFILE.json [RESULT_OR_METADATA.json]
Only timing and queue counters are emitted. No event documents or paths from the
capture are copied. Legacy batch windows are estimates, not COMMIT spans.
"""
import argparse
import json
from pathlib import Path
import sys


STAGES = ("raw_commit_including_autocheckpoint", "raw_batch_flush")


def natural(value, label):
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ValueError(f"{label} must be a nonnegative integer")
    return value


def pipeline_from(record):
    """Recognize common report wrappers without traversing event payloads."""
    if not isinstance(record, dict):
        raise ValueError("metadata must be an object")
    candidates = [record]
    for name in ("measurement", "metadata", "result"):
        if isinstance(record.get(name), dict):
            candidates.append(record[name])
    for candidate in candidates:
        backend = candidate.get("backend", {})
        if isinstance(backend, dict) and "pipeline" in backend:
            # Snapshot fallback explicitly has no ETW pipeline. This differs
            # from a wrong input file with no recognized pipeline field.
            if backend["pipeline"] is None:
                return {}
            if isinstance(backend["pipeline"], dict):
                return backend["pipeline"]
        if isinstance(candidate.get("pipeline"), dict):
            return candidate["pipeline"]
        if any(key in candidate for key in ("queue_overflow", "overflow_first_ns")):
            return candidate
    raise ValueError("metadata has no recognized queue pipeline counters")


def union_width(intervals):
    total = 0
    end = None
    for left, right in sorted(intervals):
        if end is None or left > end:
            total += right - left
        else:
            total += max(0, right - end)
        end = max(end if end is not None else right, right)
    return total


def selected(windows, limit, count_key):
    largest = sorted(windows, key=lambda w: (-w[count_key], -w["duration_ns"], w["start_ns"]))[:limit]
    return sorted(largest, key=lambda w: w["start_ns"])


def analyze(profile, metadata, max_windows=20):
    if not isinstance(profile, dict):
        raise ValueError("profile must be an object")
    natural(max_windows, "max_windows")
    if not 1 <= max_windows <= 4096:
        raise ValueError("max_windows must be between 1 and 4096")
    pipeline = pipeline_from(metadata)
    total = pipeline.get("queue_overflow")
    if total is not None:
        natural(total, "queue_overflow")
    recorded = pipeline.get("overflow_first_ns", [])
    if not isinstance(recorded, list):
        raise ValueError("overflow_first_ns must be a list")
    for at in recorded:
        natural(at, "overflow_first_ns timestamp")
    if total is not None and len(recorded) > total:
        raise ValueError("recorded overflow timestamps exceed total queue overflow")
    last = pipeline.get("overflow_last_ns")
    if last is not None:
        natural(last, "overflow_last_ns")

    windows = []
    for row in profile.get("batches", []):
        if not isinstance(row, list) or len(row) != 4:
            raise ValueError("legacy batches must contain [end_ns, records, bytes, duration_ns]")
        end, records, size, duration = [natural(v, "batch field") for v in row]
        if duration > end:
            raise ValueError("batch duration exceeds elapsed end time")
        start = end - duration
        windows.append(dict(start_ns=start, end_ns=end, duration_ns=duration,
                            records=records, payload_bytes=size,
                            recorded_overflows=sum(start <= at < end for at in recorded)))
    intervals = [(w["start_ns"], w["end_ns"]) for w in windows]
    matched = sum(any(start <= at < end for start, end in intervals) for at in recorded)
    omitted_batches = natural(profile.get("omitted_batches", 0), "omitted_batches")
    legacy = dict(
        window_kind="estimated flush intervals: [batch_end - duration, batch_end)",
        retained_batches=len(windows), omitted_batches=omitted_batches,
        retained_interval_union_ns=union_width(intervals),
        retained_first_start_ns=min((w["start_ns"] for w in windows), default=None),
        retained_last_end_ns=max((w["end_ns"] for w in windows), default=None),
        recorded_overflows_in_flush_windows=matched,
        recorded_overflows_outside_flush_windows=len(recorded) - matched,
        displayed_windows=selected(windows, max_windows, "recorded_overflows"),
        undisplayed_windows=max(0, len(windows) - max_windows),
    )

    grouped = {stage: [] for stage in STAGES}
    unknown_stages = 0
    for span in profile.get("queue_spans", []):
        if not isinstance(span, dict):
            raise ValueError("queue span must be an object")
        stage = span.get("stage")
        if stage not in grouped:
            unknown_stages += 1
            continue
        start = natural(span.get("start_ns"), "queue span start_ns")
        end = natural(span.get("end_ns"), "queue span end_ns")
        if end < start:
            raise ValueError("queue span ends before it starts")
        snapshots = []
        for side in ("before", "after"):
            snapshot = span.get(side)
            if not isinstance(snapshot, dict):
                raise ValueError(f"queue span {side} must be an object")
            snapshots.append({key: natural(snapshot.get(key), f"{side}.{key}")
                              for key in ("enqueued", "overflow", "dequeued", "pending")})
        before, after = snapshots
        # Each atomic counter is sampled separately. In particular, pending is
        # neither a transaction snapshot nor an exact queue-balance assertion.
        for key in ("enqueued", "overflow", "dequeued"):
            if after[key] < before[key]:
                raise ValueError(f"queue span cumulative {key} counter regressed")
        if total is not None and after["overflow"] > total:
            raise ValueError("queue span overflow exceeds metadata total")
        grouped[stage].append(dict(
            start_ns=start, end_ns=end, duration_ns=end - start,
            overflow_delta=after["overflow"] - before["overflow"],
            overflow_counter_before=before["overflow"],
            overflow_counter_after=after["overflow"],
            enqueued_delta=after["enqueued"] - before["enqueued"],
            dequeued_delta=after["dequeued"] - before["dequeued"],
            pending_before=before["pending"], pending_after=after["pending"],
        ))
    span_summary = {}
    for stage, spans in grouped.items():
        observed_sum = sum(s["overflow_delta"] for s in spans)
        distinct = union_width([(s["overflow_counter_before"], s["overflow_counter_after"])
                                for s in spans])
        span_summary[stage] = dict(
            measured=bool(spans),
            retained_spans=len(spans),
            retained_interval_union_ns=union_width([(s["start_ns"], s["end_ns"]) for s in spans]),
            overflow_delta_sum=observed_sum if spans else None,
            distinct_overflow_increments=distinct if spans else None,
            overlapping_counter_increments=observed_sum - distinct if spans else None,
            fraction_of_total_overflow=(distinct / total if spans and total else None),
            total_overflows_not_covered=(total - distinct if spans and total is not None else None),
            displayed_windows=selected(spans, max_windows, "overflow_delta"),
            undisplayed_windows=max(0, len(spans) - max_windows),
        )

    return dict(
        schema_version=1, diagnostic_only=True,
        overflow=dict(total=total, recorded_timestamps=len(recorded),
                      unrecorded_timestamps=(total - len(recorded) if total is not None else None),
                      first_recorded_ns=min(recorded, default=None),
                      last_recorded_ns=max(recorded, default=None),
                      last_overflow_ns=last if total != 0 else None),
        legacy_batches=legacy, queue_spans=span_summary,
        omitted_queue_spans=natural(profile.get("omitted_queue_spans", 0), "omitted_queue_spans"),
        ignored_queue_span_stages=unknown_stages,
        limitations=[
            "Legacy timestamps describe only the recorded prefix (normally first 64 losses); overlap does not measure all loss or prove causality.",
            "Legacy batch endpoints are recorded just after timing completes, so estimated flush windows are approximate and do not isolate COMMIT.",
            "Queue snapshots read separate atomics; overflow deltas are evaluated independently. Pending is not used for exact balance or occupancy reconstruction.",
            "Queue-span endpoints bracket the counter snapshots as well as the stage; their duration is not exact SQL execution time.",
            "Commit and flush summaries are separate, nested measurements and must not be added together.",
            "A stage with no retained spans is unmeasured, not a measured zero. Fractions cover retained spans only; missing spans prevent estimating the complete stage share.",
            "Omitted spans/batches and intervals outside retained windows are unmeasured; unrecorded timestamps have unknown timing.",
            "Elapsed timings include scheduling. Automatic checkpoint time is included in COMMIT and no checkpoint causality is established.",
        ],
    )


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", type=Path)
    parser.add_argument("metadata", type=Path, nargs="?")
    parser.add_argument("--max-windows", type=int, default=20)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    metadata = args.metadata
    if metadata is None:
        metadata = next((args.profile.with_name(name) for name in
                         ("measurement.json", "result.json", "metadata.json")
                         if args.profile.with_name(name).is_file()), None)
        if metadata is None:
            parser.error("provide matching metadata, result or measurement JSON")
    try:
        profile = json.loads(args.profile.read_text(encoding="utf-8-sig"))
        record = json.loads(metadata.read_text(encoding="utf-8-sig"))
        report = analyze(profile, record, args.max_windows)
    except (OSError, ValueError, TypeError) as error:
        parser.error(str(error))
    rendered = json.dumps(report, indent=2) + "\n"
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    else:
        sys.stdout.write(rendered)


if __name__ == "__main__":
    main()
