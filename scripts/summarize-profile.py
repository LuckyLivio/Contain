"""Bounded capture diagnostics, separate from the frozen fixture scorer."""
import json
import sys
from pathlib import Path


def quantile_upper(d, percentile):
    cumulative = 0
    for bucket, count in enumerate(d["buckets"]):
        cumulative += count
        if cumulative >= d["count"] * percentile / 100:
            return (1 << bucket) / 1e6
    return None


def summarize(path):
    p = json.loads(path.read_text(encoding="utf-8-sig"))
    stages = {
        name: dict(count=d["count"], total_ms=d["total_ns"] / 1e6,
                   max_ms=d["max_ns"] / 1e6,
                   **{f"p{x}_upper_ms": quantile_upper(d, x) for x in (50, 95, 99)})
        for name, d in p["stages"].items()
    }
    # Exact percentiles for the retained bounded batch samples, not log bins.
    batches = p["batches"]
    durations = sorted(row[3] / 1e6 for row in batches)
    import math
    batch_summary = {f"p{x}_ms": durations[math.ceil(len(durations)*x/100)-1]
                     for x in (50, 95, 99)} if durations else {}
    batch_summary.update(max_ms=max(durations, default=None),
                         retained=len(batches), omitted=p["omitted_batches"],
                         records=sum(b[1] for b in batches),
                         payload_bytes=sum(b[2] for b in batches))
    return dict(path=str(path), stages=stages, batch_summary=batch_summary,
                checkpoint_timing=p["checkpoint_timing"])


if __name__ == "__main__":
    for arg in sys.argv[1:]:
        path = Path(arg)
        paths = path.rglob("*profile*.json") if path.is_dir() else [path]
        for item in paths:
            print(json.dumps(summarize(item), ensure_ascii=False))
