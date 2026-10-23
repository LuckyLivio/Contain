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
import uuid


RAW_TIMESTAMP = 0x1000
SUPPORTED = {"write_requested", "rename_requested", "delete_requested", "set_value"}
COMPANION_PROTOCOL = "contain-raw-clock-companion-1"
PROTOCOL_REVISION = "contain-clock-observability-2"
IDENTITY_WIDTHS = dict(event_id=16, version=8, channel=8, level=8, opcode=8,
                       task=16, process_id=32, thread_id=32)


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


def record_identity(value):
    result = {}
    provider = value["provider"]
    if not isinstance(provider, str) or str(uuid.UUID(provider)) != provider:
        raise ValueError("noncanonical provider GUID")
    result["provider"] = provider
    for name, width in IDENTITY_WIDTHS.items():
        item = value[name]
        if type(item) is not int or not 0 <= item < 1 << width:
            raise ValueError("invalid record descriptor or header identity")
        result[name] = item
    keyword = value["keyword"]
    if not isinstance(keyword, str) or len(keyword) != 16 or any(c not in "0123456789abcdef" for c in keyword):
        raise ValueError("invalid keyword")
    result["keyword"] = keyword
    return result


def event_key(value):
    if not isinstance(value, str) or len(value) != 16 or any(c not in "0123456789abcdef" for c in value) or int(value, 16) == 0:
        raise ValueError("missing or invalid original event key")
    return value


def companion_links(capture, raw_bytes):
    """Exact original-key links only. No payload/timestamp similarity matching."""
    diagnostic = dict(status="unavailable", reasons=[], sha256_verified=False,
                      linked_events=0, primary_events_without_link=None)
    links = {}
    try:
        document = json.loads(raw_bytes.decode("utf-8-sig"))
        metadata = capture["backend"]["clock"]
        summary = metadata["raw_companion"]
        primary_header = metadata["trace_header"]
        header, stats, records = (document[name] for name in ("header", "stats", "records"))
        if type(document.get("schema_version")) is not int or document["schema_version"] != 1 or document.get("protocol") != COMPANION_PROTOCOL:
            raise ValueError("raw_companion_schema_unsupported")
        if summary.get("protocol") != COMPANION_PROTOCOL or summary.get("written") is not True:
            raise ValueError("raw_companion_not_committed")
        digest = hashlib.sha256(raw_bytes).hexdigest()
        if summary.get("sha256") != digest or type(summary.get("file_bytes")) is not int or summary["file_bytes"] != len(raw_bytes):
            raise ValueError("raw_companion_digest_or_size_mismatch")
        diagnostic.update(sha256_verified=True, input_sha256=digest)
        if not isinstance(document.get("session_name"), str) or not document["session_name"] or document["session_name"] != summary.get("session_name"):
            raise ValueError("raw_companion_session_mismatch")
        if any(document.get(name) != summary.get(name) for name in ("header", "stats", "errors", "ready", "stopped",
                                                                   "producer_stopped", "header_sample_phase", "consumer_loss_counters_available")):
            raise ValueError("raw_companion_summary_mismatch")
        if (document.get("ready") is not True or document.get("stopped") is not True
                or document.get("producer_stopped") is not True or document.get("errors") != []):
            raise ValueError("raw_companion_incomplete")
        if document.get("header_sample_phase") != "open_before_process_trace" or document.get("consumer_loss_counters_available") is not False:
            raise ValueError("raw_companion_header_observation_contract_unsupported")
        mode = header["process_trace_mode"]
        if type(mode) is not int or not 0 <= mode <= 0xFFFFFFFF or mode & 0x10001100 != 0x10001100:
            raise ValueError("raw_companion_consumer_mode_invalid")
        if type(header.get("reserved_flags")) is not int or header["reserved_flags"] != 1:
            raise ValueError("raw_companion_clock_type_not_qpc")
        primary_mode = primary_header.get("process_trace_mode")
        if (type(primary_mode) is not int or primary_mode != metadata.get("process_trace_mode")
                or not 0 <= primary_mode <= 0xFFFFFFFF
                or primary_mode & 0x10001100 != 0x10000100):
            raise ValueError("primary_actual_consumer_mode_mismatch")
        frequency = integer(header["perf_frequency"])
        if not 0 < frequency < 1 << 63 or frequency != integer(metadata["qpc_frequency"]):
            raise ValueError("raw_companion_frequency_mismatch")
        for sample in (header, primary_header):
            start = sample["start_time"]
            if (not isinstance(start, str) or not start.isascii() or not start.isdecimal()
                    or str(int(start)) != start or not 0 <= int(start) < 1 << 63):
                raise ValueError("raw_companion_header_time_malformed")
        for name in ("reserved_flags", "perf_frequency", "start_time"):
            if header[name] != primary_header[name]:
                raise ValueError("raw_companion_primary_header_mismatch")
        # Real-time OpenTrace can leave StartTime unpopulated. These headers were
        # sampled before ProcessTrace, not at finalization. Exact event-key QPC
        # links and independently sampled QPC boundaries do not use this FILETIME
        # as an origin. Retain zero as unavailable; never synthesize a wall time.
        diagnostic["start_time_observation"] = dict(
            primary=primary_header["start_time"], companion=header["start_time"],
            status="unavailable_at_realtime_open" if header["start_time"] == "0" else "reported_at_realtime_open",
            available=header["start_time"] != "0", used_as_clock_origin=False)
        # BootTime is only supported for Global Logger traces. Its value/equality
        # is not a prerequisite for an original-event-key link in this UUID session.
        for sample in (header, primary_header):
            boot = sample["boot_time"]
            if (not isinstance(boot, str) or not boot.isascii()
                    or not boot.removeprefix("-").isdecimal() or not -(1 << 63) <= int(boot) < 1 << 63):
                raise ValueError("raw_companion_header_boot_time_malformed")
        for name in ("events_lost", "buffers_lost"):
            if type(header.get(name)) is not int or header[name] != 0:
                raise ValueError("raw_companion_header_reports_loss")
        required_stats = ("received", "stored", "missing_event_key", "invalid_timestamp", "capacity_dropped",
                          "duplicate_event_keys", "record_capacity", "byte_capacity", "bytes", "fixed_record_size",
                          "allocated_record_bytes", "disk_byte_capacity", "filtered_nonprimary",
                          "readiness_probe_records", "serialization_buffer_bytes")
        if any(type(stats.get(name)) is not int or stats[name] < 0 for name in required_stats):
            raise ValueError("raw_companion_invalid_counters")
        if any(stats[name] != 0 for name in ("missing_event_key", "invalid_timestamp", "capacity_dropped", "duplicate_event_keys")):
            raise ValueError("raw_companion_loss_or_ambiguity")
        if (not isinstance(records, list) or stats["received"] != stats["stored"] + stats["filtered_nonprimary"]
                or stats["stored"] != len(records) or stats["readiness_probe_records"] == 0):
            raise ValueError("raw_companion_record_count_mismatch")
        if not (0 < stats["record_capacity"] and 0 < stats["fixed_record_size"] and len(records) <= stats["record_capacity"]
                and stats["bytes"] == len(records) * stats["fixed_record_size"]
                and stats["bytes"] <= stats["allocated_record_bytes"] <= stats["byte_capacity"]
                and stats["record_capacity"] * stats["fixed_record_size"] <= stats["allocated_record_bytes"]
                and stats["allocated_record_bytes"] + stats["serialization_buffer_bytes"] <= stats["byte_capacity"]
                and len(raw_bytes) <= stats["disk_byte_capacity"]):
            raise ValueError("raw_companion_budget_inconsistent")
        index, sequences = {}, set()
        for row in records:
            key = event_key(row["event_key"])
            sequence, tick = integer(row["sequence"]), integer(row["header_timestamp"])
            if key in index or sequence in sequences or not 0 < sequence <= stats["received"] or not 0 < tick < 1 << 63:
                raise ValueError("raw_companion_duplicate_key_sequence_or_invalid_qpc")
            sequences.add(sequence)
            index[key] = (tick, record_identity(row))
        primary_keys = Counter(event.get("raw", {}).get("clock", {}).get("event_key")
                               for event in capture.get("events", []) if event.get("raw", {}).get("clock"))
        if any(count != 1 for key, count in primary_keys.items() if key is not None):
            raise ValueError("primary_event_key_ambiguous")
        for event in capture.get("events", []):
            if event.get("event_type") not in {"file", "registry"} or event.get("operation") not in SUPPORTED:
                continue
            primary = event["raw"]["clock"]
            key = event_key(primary["event_key"])
            if primary.get("timestamp_domain") != "filetime_100ns" or primary.get("raw_qpc") is not None:
                raise ValueError("primary_clock_domain_unexpected")
            if key not in index:
                raise ValueError("primary_event_key_missing_from_companion")
            tick, descriptor = index[key]
            if record_identity(primary["record_identity"]) != descriptor:
                raise ValueError("raw_companion_exact_descriptor_mismatch")
            links[event["id"]] = tick
        if not links:
            raise ValueError("no_original_source_clock_links")
        diagnostic.update(status="exact_original_key_links_available", linked_events=len(links),
                          primary_events_without_link=0,
                          raw_record_count=len(records), qpc_frequency=str(frequency),
                          consumer_loss_counters_available=False,
                          unsupported_boot_time_fields=dict(primary=primary_header["boot_time"], companion=header["boot_time"],
                                                            equal=primary_header["boot_time"] == header["boot_time"], proves_same_boot=False),
                          coverage="Exact original-key coverage of retained primary source events only; initial header loss counters do not establish final consumer loss.",
                          event_links=[dict(event_id=event["id"], event_key=event["raw"]["clock"]["event_key"],
                                            original_qpc=str(links[event["id"]]),
                                            delivered_filetime=event["raw"]["clock"]["header_timestamp"])
                                       for event in capture.get("events", []) if event["id"] in links])
    except (KeyError, TypeError, ValueError, AttributeError) as error:
        diagnostic["reasons"].append(str(error))
        links = {}
    return links, diagnostic


def marker_diagnostics(capture, markers):
    """Control operations have their own journal and never enter oracle scoring."""
    result = dict(rows=[], attribution_scored=False, same_machine_boot_provenance_verified=False,
                  definition="QPC ordering is conditional on same-machine/boot provenance; a pre-trace open does not identify any unresolved ETW resource.")
    try:
        metadata = capture["backend"]["clock"]
        rate = integer(metadata["qpc_frequency"])
        phases = metadata["calibrations"]
        if rate == 0 or [row["phase"] for row in phases] != ["trace_start_requested", "consumer_ready", "trace_stop_requested", "consumer_stopped"]:
            raise ValueError("trace calibrations missing")
        bounds = [bracket(row) for row in phases]
        if any(a[2] > b[0] for a, b in zip(bounds, bounds[1:])):
            raise ValueError("trace calibrations regressed")
    except (KeyError, TypeError, ValueError):
        return dict(result, status="unavailable", reason="trace_clock_boundaries_missing_or_invalid")
    seen = set()
    for row in markers:
        diagnostic = dict(marker=row.get("marker"), role=row.get("role"), status="invalid")
        try:
            value = row["clock"]
            if type(row.get("schema_version")) is not int or row["schema_version"] != 1 or row.get("kind") != "fixture_control_marker":
                raise ValueError("control marker schema unsupported")
            if row.get("marker") != "initial_registry_open" or type(row.get("success")) is not bool:
                raise ValueError("control marker kind or outcome invalid")
            if type(value.get("schema_version")) is not int or value["schema_version"] != 1:
                raise ValueError("control marker clock schema unsupported")
            actor = identity(row)
            sequence = integer(value["sequence"])
            if sequence == 0 or (*actor, sequence) in seen or integer(value["thread_id"]) == 0:
                raise ValueError("control marker identity or sequence invalid")
            seen.add((*actor, sequence))
            before, after = bracket(value["before"]), bracket(value["after"])
            start, end = integer(value["start_qpc"]), integer(value["end_qpc"])
            if integer(value["qpc_frequency"]) != rate or not before[2] <= start <= end <= after[0]:
                raise ValueError("control marker frequency or bracketing invalid")
            relation = ("before_trace_start_request" if after[2] < bounds[0][0] else
                        "within_ready_trace_window" if bounds[1][2] <= before[0] and after[2] <= bounds[2][0] else
                        "after_consumer_stopped" if before[0] > bounds[3][2] else
                        "overlaps_trace_boundary_or_startup")
            diagnostic.update(status="measured", pid=actor[0], creation_time=str(actor[1]),
                              success=row["success"], error=row.get("error"), sequence=sequence,
                              start_qpc=str(start), end_qpc=str(end), relation=relation)
        except (KeyError, TypeError, ValueError) as error:
            diagnostic["reason"] = str(error)
        result["rows"].append(diagnostic)
    result["status"] = ("invalid" if any(row["status"] == "invalid" for row in result["rows"])
                        else "measured" if result["rows"] else "unavailable")
    return result


def analyze(capture, truth, sid="", raw_clock_bytes=None):
    if not isinstance(truth, list) or not truth:
        raise ValueError("nonempty independent truth list required")
    if not isinstance(capture, dict) or not isinstance(capture.get("backend", {}), dict):
        raise ValueError("capture and backend must be objects")
    if not isinstance(capture.get("events", []), list) or not all(isinstance(row, dict) for row in truth + capture.get("events", [])):
        raise ValueError("source and independent rows must be objects")
    report = dict(schema_version=1, protocol_revision=PROTOCOL_REVISION,
                  purpose="independent_clock_observability", release_gate=None,
                  same_machine_boot_provenance_verified=False,
                  attribution_scored=False, legacy_score_recomputed=False, tolerance_qpc=0,
                  source_reasons=[], truth_rows=[], source_event_count=0)
    metadata = capture.get("backend", {}).get("clock")
    linked_qpc = {}
    companion_ready = False
    if raw_clock_bytes is not None:
        linked_qpc, report["raw_companion"] = companion_links(capture, raw_clock_bytes)
        companion_ready = report["raw_companion"]["status"] == "exact_original_key_links_available"
    source_ready = isinstance(metadata, dict)
    if not source_ready:
        report["source_reasons"].append("trace_clock_metadata_missing")
        metadata = {}
    else:
        mode = metadata.get("process_trace_mode")
        for valid, reason in (
            (type(metadata.get("schema_version")) is int and metadata["schema_version"] == 1, "trace_clock_schema_unsupported"),
            (type(metadata.get("client_context")) is int and metadata["client_context"] == 1 and metadata.get("clock_type") == "qpc", "trace_not_qpc"),
            (metadata.get("raw_qpc_available") is (False if companion_ready else True), "original_qpc_unavailable"),
            (metadata.get("delivered_timestamp_domain") == ("filetime_100ns" if companion_ready else "qpc"), "delivered_header_is_not_raw_qpc"),
            (metadata.get("trace_header_available") is True, "trace_header_unavailable"),
            (type(mode) is int and 0 <= mode <= 0xFFFFFFFF and mode & 0x10000100 == 0x10000100
             and (mode & RAW_TIMESTAMP == 0 if companion_ready else mode & RAW_TIMESTAMP != 0), "consumer_clock_mode_invalid" if companion_ready else "consumer_did_not_request_raw_timestamp"),
            (metadata.get("errors") == [], "trace_clock_measurement_errors_or_missing"),
        ):
            if not valid:
                source_ready = False
                report["source_reasons"].append(reason)
        if raw_clock_bytes is not None and not companion_ready:
            source_ready = False
            report["source_reasons"].append("raw_companion_link_unavailable")
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
            if companion_ready:
                qpc = linked_qpc[event["id"]]
            else:
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
    parser.add_argument("--raw-clock", type=Path, help="Private same-session original-QPC companion; exact-key links only")
    parser.add_argument("--marker-root", type=Path, help="Independent clock-marker-*.jsonl files; never included in scored truth rows")
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
    raw_clock_bytes = args.raw_clock.read_bytes() if args.raw_clock else None
    result = analyze(capture, truth, args.sid, raw_clock_bytes)
    result["input_sha256"] = dict(capture=hashlib.sha256(args.capture.read_bytes()).hexdigest(),
                                 truth=[hashlib.sha256(source.read_bytes()).hexdigest() for source in sources])
    if raw_clock_bytes is not None:
        result["input_sha256"]["raw_clock"] = hashlib.sha256(raw_clock_bytes).hexdigest()
    if args.marker_root:
        marker_files = sorted(args.marker_root.glob("clock-marker-*.jsonl"))
        markers = [json.loads(line) for source in marker_files
                   for line in source.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        result["control_markers"] = marker_diagnostics(capture, markers)
        result["input_sha256"]["control_markers"] = [hashlib.sha256(source.read_bytes()).hexdigest() for source in marker_files]
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
