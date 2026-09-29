"""Publish safe fixture evidence after unmodified scoring, never machine inventory."""
import json
import sys
from collections import Counter
from pathlib import Path


def write_trial(source, destination, variant, error):
    destination.mkdir(parents=True, exist_ok=True)
    result = dict(variant=variant, harness_error=error or None, acceptance_pass=False)
    capture_path = source / "capture.json"
    if not capture_path.exists():
        result["missing_capture"] = True
    else:
        envelope = json.loads(capture_path.read_text(encoding="utf-8-sig"))
        c = envelope["data"]
        score = json.loads((source / "reliability.json").read_text(encoding="utf-8-sig"))
        truth = json.loads((source / "ground-truth.json").read_text(encoding="utf-8-sig"))
        pids = {t["pid"] for t in truth}
        result.update(stats=c["stats"], backend=c["backend"], quality=c["quality"],
                      score={k: v for k, v in score.items() if k != "rows"})
        categories = {}
        for row in score["rows"]:
            if row["group"] == "target_success":
                category = "target_registry" if row["operation"] == "set_value" else "target_file"
            elif row["group"] == "noise_success":
                category = "noise_pre" if "\\NoisePre\\" in row["resource"] else "noise_post" if "\\NoisePost\\" in row["resource"] else "original_noise"
            else:
                category = row["group"]
            group = categories.setdefault(category, Counter())
            group["expected"] += 1
            group[row["observation"]] += 1
            if row["attribution"]:
                group[row["attribution"]] += 1
        result["categories"] = categories
        target = score["groups"]["target_success"]
        b = c["backend"]
        p = b.get("pipeline") or {}
        result["acceptance_pass"] = bool(
            not error and target["expected"] == target["observed"] == target["correctly_attributed"] == 17
            and score["false_positive_target_events"] == score["incorrect_attribution"] == 0
            and b["dropped_events"] == b["decode_errors"] == b["context_losses"] == 0
            and b["etw_events_lost"] == b["etw_buffers_lost"] == 0
            and p.get("queue_pending") == 0 and not c["stats"]["drain_timed_out"])
        # Scoring was completed before this publication boundary. Keep ALL scored rows,
        # including misses/false positives. Only raw fixture paths/lifecycles are public.
        def scoped(e):
            value = e["resource"].lower().replace("\\\\?\\", "")
            return any(value.startswith(r.lower().replace("\\\\?\\", "")) for r in c["watch_roots"]) or (
                c.get("registry_key", "").lower() in value and "\\contain\\demo\\contain-demo-" in value)
        safe_events = []
        for e in c["events"]:
            if scoped(e) or (e["event_type"] == "lifecycle" and e["evidence"].get("pid") in pids):
                if e["evidence"].get("pid") not in pids:
                    # Preserve observation/confidence; redact non-fixture process details.
                    e["evidence"]["process_image"] = None
                    e["evidence"]["pid"] = None
                    e["evidence"]["process_creation_time"] = None
                    e["raw"]["header_pid"] = None
                safe_events.append(e)
        safe = {k: c[k] for k in ("id", "schema_version", "watch_roots", "registry_key", "stats", "backend", "quality", "capture_state")}
        safe["events"] = safe_events
        safe["processes"] = [p for p in c["processes"] if p["pid"] in pids]
        safe["publication_note"] = "Fixture-only export after scoring; global inventory/registry/process records omitted. Not the complete raw database. Scores are original and include every failure."
        for name, value in [("capture-fixture.json", safe), ("score.json", score), ("ground-truth.json", truth)]:
            (destination / name).write_text(json.dumps(value, indent=2), encoding="utf-8")
    measurement = source / "measurement.json"
    if measurement.exists():
        result["measurement"] = json.loads(measurement.read_text(encoding="utf-8-sig"))
    profile = source / "profile.json"
    if profile.exists():
        (destination / "profile.json").write_bytes(profile.read_bytes())
    (destination / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


if __name__ == "__main__":
    r = write_trial(Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else "")
    print(json.dumps(r))
