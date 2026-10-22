"""Independent clock-observability diagnostic; never rescore a release gate.

Reads a complete private capture and independent fixture journal. No clock offset,
tolerance, FILETIME inversion, mutation, download or execution of either input.
"""
import argparse
from collections import Counter
from fractions import Fraction
import hashlib
import json
from pathlib import Path


RAW_TIMESTAMP = 0x1000
SUPPORTED = {"write_requested", "rename_requested", "delete_requested", "set_value"}


def integer(value):
    if type(value) is int and value >= 0:
        return value
    if isinstance(value, str) and value.isascii() and value.isdecimal():
        return int(value)
    raise ValueError("nonnegative integer required")


def bracket(value):
    lo, wall, hi = (integer(value[name]) for name in ("qpc_before", "filetime_ticks", "qpc_after"))
    if lo > hi:
        raise ValueError("QPC calibration order regressed")
    return lo, wall, hi


def calibration_diagnostic(before, after, frequency):
    """Exact rational envelopes; these test a constant-offset hypothesis only.

    Intersecting sampled envelopes cannot establish continuous clock stability.
    A disjoint envelope is retained as a diagnostic, never turned into tolerance.
    """
    left, right = bracket(before), bracket(after)
    scale = Fraction(10_000_000, frequency)
    offsets = [(wall - hi * scale, wall - lo * scale) for lo, wall, hi in (left, right)]
    compatible = max(pair[0] for pair in offsets) <= min(pair[1] for pair in offsets)
    rational = lambda value: {"numerator": str(value.numerator), "denominator": str(value.denominator)}
    return dict(before_width_qpc=str(left[2] - left[0]), after_width_qpc=str(right[2] - right[0]),
                wall_delta_100ns=str(right[1] - left[1]),
                sampled_constant_offset_compatible=compatible,
                constant_offset_proven=False,
                offset_envelopes_100ns=[list(map(rational, pair)) for pair in offsets])


def normalize(path, sid):
    value = (path or "").replace("/", "\\").replace("\\\\?\\", "").lower()
    if value.startswith("hkcu\\"):
        value = "\\registry\\user\\" + sid.lower() + "\\" + value[5:]
    return value


def identity(value, event=False):
    value = value.get("evidence", {}) if event else value
    pid, birth = integer(value["pid"]), integer(value["process_creation_time" if event else "creation_time"])
    if not 0 < pid <= 0xFFFFFFFF or birth == 0:
        raise ValueError("unknown process identity")
    return pid, birth


def analyze(capture, truth, sid=""):
    if not isinstance(truth, list) or not truth:
        raise ValueError("nonempty independent truth list required")
    if not isinstance(capture, dict) or not isinstance(capture.get("backend", {}), dict):
        raise ValueError("capture and backend must be objects")
    if not isinstance(capture.get("events", []), list) or not all(isinstance(row, dict) for row in truth + capture.get("events", [])):
        raise ValueError("source and independent rows must be objects")
    report = dict(schema_version=1, purpose="independent_clock_observability", release_gate=None,
                  same_machine_boot_provenance_verified=False,
                  attribution_scored=False, legacy_score_recomputed=False, tolerance_qpc=0,
                  source_reasons=[], truth_rows=[], source_event_count=0)
    metadata = capture.get("backend", {}).get("clock")
    source_ready = isinstance(metadata, dict)
    if not source_ready:
        report["source_reasons"].append("trace_clock_metadata_missing")
        metadata = {}
    else:
        mode = metadata.get("process_trace_mode")
        for valid, reason in (
            (type(metadata.get("schema_version")) is int and metadata["schema_version"] == 1, "trace_clock_schema_unsupported"),
            (type(metadata.get("client_context")) is int and metadata["client_context"] == 1 and metadata.get("clock_type") == "qpc", "trace_not_qpc"),
            (metadata.get("raw_qpc_available") is True, "original_qpc_unavailable"),
            (metadata.get("delivered_timestamp_domain") == "qpc", "delivered_header_is_not_raw_qpc"),
            (metadata.get("trace_header_available") is True, "trace_header_unavailable"),
            (type(mode) is int and 0 <= mode <= 0xFFFFFFFF and mode & 0x10001100 == 0x10001100, "consumer_did_not_request_raw_timestamp"),
            (metadata.get("errors") == [], "trace_clock_measurement_errors_or_missing"),
        ):
            if not valid:
                source_ready = False
                report["source_reasons"].append(reason)
    try:
        frequency = integer(metadata["qpc_frequency"])
        if frequency == 0:
            raise ValueError("zero frequency")
    except (KeyError, TypeError, ValueError):
        frequency = None
        source_ready = False
        report["source_reasons"].append("trace_qpc_frequency_unavailable")
    report["trace_metadata"] = metadata
    boundaries = None
    try:
        samples = metadata["calibrations"]
        if [item["phase"] for item in samples] != ["trace_start_requested", "consumer_ready",
                                                     "trace_stop_requested", "consumer_stopped"]:
            raise ValueError("trace phase calibration missing or duplicated")
        measured = [bracket(item) for item in samples]
        if any(left[2] > right[0] for left, right in zip(measured, measured[1:])):
            raise ValueError("trace QPC calibration phases regressed")
        boundaries = (measured[1][2], measured[2][0])
        if frequency:
            report["trace_calibration_diagnostic"] = calibration_diagnostic(samples[0], samples[-1], frequency)
        report["trace_calibration_status"] = "valid"
    except (KeyError, TypeError, ValueError):
        report["trace_calibration_status"] = "unavailable_or_invalid"
        source_ready = False
        report["source_reasons"].append("trace_calibration_unavailable_or_invalid")
    events = [event for event in capture.get("events", [])
              if event.get("event_type") in {"file", "registry"} and event.get("operation") in SUPPORTED]
    report["source_event_count"] = len(events)
    valid_events = []
    for event in events:
        try:
            raw = event["raw"]["clock"]
            qpc = integer(raw["raw_qpc"])
            if raw["timestamp_domain"] != "qpc" or integer(raw["header_timestamp"]) != qpc:
                raise ValueError("not original QPC")
            actor = identity(event, True)
            valid_events.append((event, qpc, actor))
        except (KeyError, TypeError, ValueError):
            source_ready = False
    if len(valid_events) != len(events):
        report["source_reasons"].append("source_original_qpc_or_actor_missing")
    report["source_events_with_raw_qpc_and_identity"] = len(valid_events)
    used_sequences = set()
    row_candidates = []
    invalid = 0
    for row_id, row in enumerate(truth):
        diagnostic = dict(row=row_id, operation=row.get("operation"), role=row.get("role"),
                          success=row.get("success"), status="unavailable", event_ids=[])
        candidates = []
        try:
            value = row["clock"]
            if not isinstance(value, dict):
                raise ValueError("independent clock must be an object")
            if type(value.get("schema_version")) is not int or value["schema_version"] != 1:
                raise ValueError("independent clock schema unsupported")
            rate = integer(value["qpc_frequency"])
            start, end = integer(value["start_qpc"]), integer(value["end_qpc"])
            if rate == 0 or start > end or integer(value["thread_id"]) == 0:
                raise ValueError("invalid independent operation clock")
            before, after = bracket(value["before"]), bracket(value["after"])
            if before[2] > start or end > after[0]:
                raise ValueError("operation escaped independently sampled brackets")
            sequence = (*identity(row), integer(value["sequence"]))
            if sequence[-1] == 0 or sequence in used_sequences:
                raise ValueError("duplicate or zero independent sequence")
            used_sequences.add(sequence)
            diagnostic.update(independent_clock="valid", start_qpc=str(start), end_qpc=str(end),
                              qpc_frequency=str(rate), thread_id=value["thread_id"],
                              calibration=calibration_diagnostic(value["before"], value["after"], rate))
            if frequency is not None and frequency != rate:
                diagnostic["reason"] = "trace_fixture_frequency_mismatch"
            elif boundaries is not None and not (boundaries[0] <= before[0] and after[2] <= boundaries[1]):
                diagnostic["reason"] = "fixture_outside_recorded_trace_qpc_window"
            elif not source_ready:
                diagnostic["reason"] = "source_original_clock_unavailable"
            elif row.get("operation") not in SUPPORTED:
                diagnostic["status"] = "unsupported"
            else:
                paths = {normalize(row.get(name), sid) for name in ("resource", "destination")} - {""}
                candidates = [event["id"] for event, tick, actor in valid_events
                              if start <= tick <= end and actor == identity(row)
                              and event.get("operation") == row["operation"]
                              and normalize(event.get("resource"), sid) in paths]
                diagnostic["status"] = "no_in_interval_source" if not candidates else "interval_candidates"
                diagnostic["event_ids"] = candidates
        except KeyError:
            diagnostic["reason"] = "independent_clock_or_identity_missing"
        except (TypeError, ValueError) as error:
            invalid += 1
            diagnostic.update(status="invalid", reason=str(error))
        report["truth_rows"].append(diagnostic)
        row_candidates.append(candidates)
    uses = Counter(event for candidates in row_candidates for event in candidates)
    for diagnostic in report["truth_rows"]:
        if diagnostic["status"] == "interval_candidates":
            values = diagnostic["event_ids"]
            diagnostic["status"] = ("unique_interval_candidate" if len(values) == 1 and uses[values[0]] == 1
                                    else "ambiguous_interval_candidates")
    counts = Counter(row["status"] for row in report["truth_rows"])
    report["counts"] = dict(counts)
    report["status"] = "invalid" if invalid else "unavailable" if not source_ready or counts["unavailable"] else "diagnostic_ready"
    report["definition"] = ("Timing compatibility is not causal attribution or a release pass. Missing original QPC stays unavailable. "
                            "Same-machine, same-boot capture and fixture provenance must be independently established. "
                            "FILETIME samples diagnose clocks only; no inverse conversion or tolerance is used.")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", type=Path, required=True)
    inputs = parser.add_mutually_exclusive_group(required=True)
    inputs.add_argument("--truth", type=Path)
    inputs.add_argument("--truth-root", type=Path)
    parser.add_argument("--sid", default="")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    capture = json.loads(args.capture.read_text(encoding="utf-8-sig"))
    if "data" in capture:
        capture = capture["data"]
    sources = [args.truth] if args.truth else sorted(args.truth_root.glob("ground-truth-*.jsonl"))
    if args.truth:
        truth = json.loads(args.truth.read_text(encoding="utf-8-sig"))
    else:
        truth = [json.loads(line) for source in sources
                 for line in source.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    result = analyze(capture, truth, args.sid)
    result["input_sha256"] = dict(capture=hashlib.sha256(args.capture.read_bytes()).hexdigest(),
                                 truth=[hashlib.sha256(source.read_bytes()).hexdigest() for source in sources])
    result["script_lf_sha256"] = hashlib.sha256(Path(__file__).read_bytes().replace(b"\r\n", b"\n")).hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Refuse to overwrite any historical diagnostic, score or other evidence.
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2, allow_nan=False)
        output.write("\n")
    print(json.dumps({"status": result["status"], "counts": result["counts"], "release_gate": None}))
    return 0 if result["status"] == "diagnostic_ready" else 2


if __name__ == "__main__":
    raise SystemExit(main())
