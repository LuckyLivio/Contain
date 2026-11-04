"""Independent clock-observability diagnostic; never rescore a release gate.

Reads a complete private capture and independent fixture journal. No clock offset,
tolerance, FILETIME inversion, mutation, download or execution of either input.
"""
import argparse
from collections import Counter
from fractions import Fraction
import hashlib
import json
import os
from pathlib import Path
import stat
import struct
import uuid


RAW_TIMESTAMP = 0x1000
SUPPORTED = {"write_requested", "rename_requested", "delete_requested", "set_value"}
COMPANION_PROTOCOL = "contain-raw-clock-companion-1"
COMPANION_BINARY_PROTOCOL = "contain-raw-clock-companion-2"
PROTOCOL_REVISION = "contain-clock-observability-5"
RESULT_CONTRACT = "contain-clock-result-1"
BINARY_ROW = struct.Struct("<QQq16sHBBBBHQII")
BINARY_DISK_LIMIT = 128 * 1024 * 1024
BINARY_METADATA_LIMIT = 64 * 1024
BINARY_RECORD_LIMIT = (BINARY_DISK_LIMIT - BINARY_METADATA_LIMIT) // BINARY_ROW.size
BINARY_PROVIDERS = {0xedd089279cc44e65b970c2560fb5c289, 0x22fb2cd60e7b422ba0c72fad1fd0e716,
                    0x70eb4f03c1de4f73a05133d13d5413bd}
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


def native_provenance_sample(value, phase):
    if not isinstance(value, dict):
        raise ValueError("native_provenance_sample_missing")
    if (type(value.get("schema_version")) is not int or value["schema_version"] != 1
            or value.get("phase") != phase or value.get("status") != "observed"
            or value.get("machine_source") != "GetComputerNameExW_PhysicalDnsFullyQualified_lower_utf16le_sha256"
            or value.get("boot_source") != "NtQuerySystemInformation_SystemBootEnvironmentInformation_90_32"
            or value.get("nt_status") != "00000000" or type(value.get("returned_bytes")) is not int
            or value["returned_bytes"] != 32 or value.get("errors") != []):
        raise ValueError("native_provenance_measurement_unavailable_or_invalid")
    for name, width in (("machine_name_sha256", 64), ("boot_identifier_le_hex", 32)):
        token = value.get(name)
        if (not isinstance(token, str) or len(token) != width or any(c not in "0123456789abcdef" for c in token)
                or int(token, 16) == 0):
            raise ValueError("native_provenance_identifier_invalid")
    times = []
    for name in ("qpc_before", "qpc_after"):
        tick = value.get(name)
        if (not isinstance(tick, str) or not tick.isascii() or not tick.isdecimal()
                or str(int(tick)) != tick or not 0 <= int(tick) < 1 << 63):
            raise ValueError("native_provenance_qpc_invalid")
        times.append(int(tick))
    if times[0] > times[1]:
        raise ValueError("native_provenance_qpc_regressed")
    return (value["machine_name_sha256"], value["boot_identifier_le_hex"]), tuple(times)


def provenance_diagnostic(metadata, truth):
    result = dict(status="unavailable", verified=False, collector_samples=0, fixture_rows=0,
                  scope="matching_independent_local_native_observations_not_tamper_or_vm_clone_attestation")
    try:
        samples = metadata.get("provenance")
        if not isinstance(samples, list) or len(samples) != 2:
            raise ValueError("collector_native_provenance_missing")
        first, first_bounds = native_provenance_sample(samples[0], "trace_start_requested")
        last, last_bounds = native_provenance_sample(samples[1], "consumer_stopped")
        if first != last:
            raise ValueError("collector_native_provenance_changed")
        calibrations = metadata["calibrations"]
        if (first_bounds[1] > bracket(calibrations[0])[0]
                or last_bounds[0] < bracket(calibrations[2])[2]
                or last_bounds[1] > bracket(calibrations[3])[0]):
            raise ValueError("collector_native_provenance_phase_bounds_invalid")
        result["collector_samples"] = 2
        processes = {}
        for row in truth:
            clock = row["clock"]
            if not isinstance(clock, dict):
                raise ValueError("fixture_clock_not_an_object")
            measured, bounds = native_provenance_sample(clock.get("provenance"), "fixture_clock_initialized")
            if measured != first:
                raise ValueError("fixture_collector_machine_or_boot_mismatch")
            if bounds[1] > bracket(clock["before"])[0]:
                raise ValueError("fixture_native_provenance_after_operation_boundary")
            process = identity(row)
            observation = (measured, bounds)
            if process in processes and processes[process] != observation:
                raise ValueError("fixture_native_provenance_changed_within_process")
            processes[process] = observation
            result["fixture_rows"] += 1
        result.update(status="matched_native_observations", verified=True, fixture_processes=len(processes))
    except (KeyError, TypeError, ValueError, IndexError) as error:
        result["reason"] = str(error)
    return result


def provider_emission_diagnostic(events):
    observations = []
    for event in events:
        raw = event.get("raw")
        clock = raw.get("clock") if isinstance(raw, dict) else None
        descriptor = clock.get("record_identity") if isinstance(clock, dict) else None
        if not isinstance(descriptor, dict):
            descriptor = {}
        provider, number, version = (descriptor.get(name) for name in ("provider", "event_id", "version"))
        semantics = "unverified_descriptor"
        if type(number) is int and type(version) is int:
            if (provider == "edd08927-9cc4-4e65-b970-c2560fb5c289" and version in (0, 1)
                    and {16: "write_requested", 26: "delete_requested", 27: "rename_requested"}.get(number) == event.get("operation")):
                semantics = "file_operation_request"
            elif (provider == "70eb4f03-c1de-4f73-a051-33d13d5413bd" and number == 5 and version == 0
                    and event.get("operation") == "set_value"):
                semantics = "registry_operation_status"
        observations.append(dict(event_id=event.get("id"), provider=provider, provider_event_id=number,
                                 provider_version=version, semantics=semantics, completion_proven_by_timestamp=False))
    return dict(status="unverified_general", source_events=observations,
                definition="An exact interval candidate is a timestamp observation, not proof of operation completion. File requests need separate unambiguous OperationEnd evidence; registry result needs the recorded status. Provider schemas alone do not guarantee emission within every API-call interval.")


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


def checked_local_file(path):
    """Reject redirection before opening a private diagnostic artifact."""
    path = Path(path).absolute()
    for item in (path, *path.parents):
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("raw_companion_binary_redirected_path")
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise ValueError("raw_companion_binary_not_single_regular_file")
    return path, info


def binary_record_index(document, raw_bytes, raw_clock_path, wanted):
    """Scan all records; only retained primary keys get descriptor objects.

    At most 2,096,128 integer keys are held for an in-place duplicate sort, plus
    the primary-key index and one 256-KiB read block. No truth rows select input.
    """
    if raw_clock_path is None:
        raise ValueError("raw_companion_binary_path_required")
    stats, binary, writer = (document[name] for name in ("stats", "binary", "writer"))
    required = ("received", "stored", "missing_event_key", "invalid_timestamp", "capacity_dropped",
                "record_capacity", "byte_capacity", "bytes", "fixed_record_size", "allocated_record_bytes",
                "disk_byte_capacity", "filtered_nonprimary", "readiness_probe_records", "serialization_buffer_bytes",
                "accepted", "queued", "queue_dropped", "quota_dropped", "writer_closed_dropped", "not_persisted",
                "writer_memory_bytes")
    if any(type(stats.get(name)) is not int or not 0 <= stats[name] < 1 << 64 for name in required):
        raise ValueError("raw_companion_invalid_counters")
    if stats.get("duplicate_event_keys", "missing") is not None:
        raise ValueError("raw_companion_binary_duplicate_check_not_deferred")
    if any(stats[name] != 0 for name in ("missing_event_key", "invalid_timestamp", "capacity_dropped",
                                       "queue_dropped", "quota_dropped", "writer_closed_dropped", "not_persisted")):
        raise ValueError("raw_companion_loss_or_ambiguity")
    if (stats["received"] != sum(stats[name] for name in ("filtered_nonprimary", "missing_event_key", "accepted",
                                                       "queue_dropped", "quota_dropped", "writer_closed_dropped"))
            or stats["accepted"] != stats["stored"] + stats["not_persisted"]
            or stats["queued"] != stats["accepted"] or stats["readiness_probe_records"] == 0
            or stats["capacity_dropped"] != stats["queue_dropped"] + stats["quota_dropped"]):
        raise ValueError("raw_companion_record_count_mismatch")
    if (not isinstance(binary, dict) or binary.get("format") != "contain-raw-clock-le64-1"
            or any(type(binary.get(name)) is not int or binary[name] < 0
                   for name in ("record_size", "records", "bytes", "metadata_byte_capacity", "byte_capacity"))):
        raise ValueError("raw_companion_binary_format_invalid")
    if (not isinstance(writer, dict) or any(type(writer.get(name)) is not int or not 0 <= writer[name] < 1 << 64
            for name in ("bytes", "records", "partial_record_bytes", "write_calls", "written_chunks",
                         "queue_high_water_chunks", "write_elapsed_ns", "drain_elapsed_ns"))):
        raise ValueError("raw_companion_binary_writer_counters_invalid")
    if (binary.get("complete") is not True or writer.get("drained") is not True or writer.get("finished") is not True
            or writer.get("timed_out") is not False or writer.get("cancel_requested") is not False
            or writer.get("cancel_error", "missing") is not None or writer.get("error", "missing") is not None
            or writer["partial_record_bytes"] != 0):
        raise ValueError("raw_companion_binary_writer_incomplete")
    size, count = binary["bytes"], binary["records"]
    if (binary["record_size"] != BINARY_ROW.size or stats["fixed_record_size"] != BINARY_ROW.size
            or count != stats["stored"] or count != writer["records"]
            or size != count * BINARY_ROW.size or size != stats["bytes"] or size != writer["bytes"]
            or not 0 < stats["record_capacity"] <= BINARY_RECORD_LIMIT or count > stats["record_capacity"]
            or binary["byte_capacity"] != stats["record_capacity"] * BINARY_ROW.size
            or not 0 < len(raw_bytes) <= binary["metadata_byte_capacity"] <= BINARY_METADATA_LIMIT
            or binary["byte_capacity"] + binary["metadata_byte_capacity"] > stats["disk_byte_capacity"]
            or not size + len(raw_bytes) <= stats["disk_byte_capacity"] <= BINARY_DISK_LIMIT
            or not 0 < stats["allocated_record_bytes"] <= stats["writer_memory_bytes"]
            or not 0 < stats["serialization_buffer_bytes"]
            or stats["writer_memory_bytes"] + stats["serialization_buffer_bytes"] > stats["byte_capacity"]
            or stats["byte_capacity"] > 32 * 1024 * 1024 or writer["queue_high_water_chunks"] > 32
            or (count > 0 and (writer["write_calls"] == 0 or writer["written_chunks"] == 0))):
        raise ValueError("raw_companion_budget_inconsistent")
    digest = binary.get("sha256")
    if (not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest)
            or writer.get("sha256") != digest or writer.get("blake3") != binary.get("blake3")):
        raise ValueError("raw_companion_binary_digest_invalid")
    metadata_path, metadata_info = checked_local_file(raw_clock_path)
    name = binary.get("file_name")
    if (not isinstance(name, str) or name != metadata_path.name + ".rows.bin"
            or any(c in name for c in '/\\:\x00<>"|?*') or name.endswith((".", " "))
            or metadata_path.name.endswith((".", " "))):
        raise ValueError("raw_companion_binary_file_name_invalid")
    with metadata_path.open("rb") as metadata_file:
        if (not os.path.samestat(metadata_info, os.fstat(metadata_file.fileno()))
                or metadata_file.read(BINARY_METADATA_LIMIT + 1) != raw_bytes):
            raise ValueError("raw_companion_binary_manifest_changed")
    binary_path, binary_info = checked_local_file(metadata_path.parent / name)
    if binary_info.st_size != size:
        raise ValueError("raw_companion_binary_size_mismatch")
    all_keys, index, previous, processed = [], {}, 0, 0
    hasher = hashlib.sha256()
    with binary_path.open("rb") as source:
        if not os.path.samestat(binary_info, os.fstat(source.fileno())):
            raise ValueError("raw_companion_binary_changed_during_open")
        while chunk := source.read(BINARY_ROW.size * 4096):
            if len(chunk) % BINARY_ROW.size or processed + len(chunk) > size:
                raise ValueError("raw_companion_binary_truncated_or_overbudget")
            processed += len(chunk)
            hasher.update(chunk)
            for sequence, key, tick, provider_bytes, event_id, version, channel, level, opcode, task, keyword, pid, tid in BINARY_ROW.iter_unpack(chunk):
                provider = int.from_bytes(provider_bytes, "little")
                if not previous < sequence <= stats["received"] or key == 0 or not 0 < tick < 1 << 63:
                    raise ValueError("raw_companion_duplicate_key_sequence_or_invalid_qpc")
                if provider not in BINARY_PROVIDERS:
                    raise ValueError("raw_companion_binary_unexpected_provider")
                previous = sequence
                all_keys.append(key)
                if key in wanted:
                    index[f"{key:016x}"] = (tick, dict(provider=str(uuid.UUID(int=provider)), event_id=event_id,
                        version=version, channel=channel, level=level, opcode=opcode, task=task,
                        keyword=f"{keyword:016x}", process_id=pid, thread_id=tid))
        final_info = os.fstat(source.fileno())
    if (processed != size or len(all_keys) != count or hasher.hexdigest() != digest
            or final_info.st_size != binary_info.st_size or final_info.st_mtime_ns != binary_info.st_mtime_ns):
        raise ValueError("raw_companion_binary_digest_size_or_file_changed")
    _, closed_info = checked_local_file(binary_path)
    if not os.path.samestat(binary_info, closed_info):
        raise ValueError("raw_companion_binary_changed_during_read")
    all_keys.sort()
    if any(all_keys[i - 1] == all_keys[i] for i in range(1, len(all_keys))):
        raise ValueError("raw_companion_duplicate_key_sequence_or_invalid_qpc")
    return index, count, dict(sha256_verified=True, record_count=count, bytes=processed,
                             duplicate_event_keys=0, duplicate_check_scope="all_binary_records",
                             indexed_primary_keys=len(index), primary_key_candidates=len(wanted),
                             global_key_count=len(all_keys), maximum_record_count=BINARY_RECORD_LIMIT)


def companion_links(capture, raw_bytes, raw_clock_path=None):
    """Exact original-key links only. No payload/timestamp similarity matching."""
    diagnostic = dict(status="unavailable", reasons=[], sha256_verified=False,
                      linked_events=0, primary_events_without_link=None)
    links = {}
    try:
        if len(raw_bytes) > BINARY_DISK_LIMIT:
            raise ValueError("raw_companion_document_overbudget")
        document = json.loads(raw_bytes.decode("utf-8-sig"))
        metadata = capture["backend"]["clock"]
        summary = metadata["raw_companion"]
        primary_header = metadata["trace_header"]
        header, stats = (document[name] for name in ("header", "stats"))
        version = document.get("schema_version")
        protocols = {1: COMPANION_PROTOCOL, 2: COMPANION_BINARY_PROTOCOL}
        if type(version) is not int or version not in protocols or document.get("protocol") != protocols[version]:
            raise ValueError("raw_companion_schema_unsupported")
        if version == 2 and len(raw_bytes) > BINARY_METADATA_LIMIT:
            raise ValueError("raw_companion_binary_manifest_overbudget")
        if summary.get("protocol") != protocols[version] or summary.get("written") is not True:
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
        if version == 2:
            # JSON booleans and integers are distinct evidence, even though
            # Python's equality considers True == 1 and False == 0.
            summary_fields = ("header", "stats", "errors", "ready", "stopped", "producer_stopped",
                              "header_sample_phase", "consumer_loss_counters_available",
                              "binary", "writer", "stats_final_available")
            if any(json.dumps(document.get(name), sort_keys=True, allow_nan=False) !=
                   json.dumps(summary.get(name), sort_keys=True, allow_nan=False) for name in summary_fields):
                raise ValueError("raw_companion_binary_summary_mismatch")
        if version == 2 and document.get("stats_final_available") is not True:
            raise ValueError("raw_companion_binary_final_stats_unavailable")
        if version == 2:
            persistence = summary.get("metadata_writer")
            if (not isinstance(persistence, dict)
                    or any(type(persistence.get(name)) is not int or not 0 <= persistence[name] < 1 << 64
                           for name in ("bytes", "write_calls", "write_elapsed_ns", "drain_elapsed_ns"))
                    or persistence["bytes"] != len(raw_bytes) or persistence["write_calls"] == 0
                    or persistence.get("finished") is not True or persistence.get("drained") is not True
                    or persistence.get("timed_out") is not False or persistence.get("cancel_requested") is not False
                    or persistence.get("error", "missing") is not None or persistence.get("cancel_error", "missing") is not None):
                raise ValueError("raw_companion_metadata_writer_incomplete")
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
        if type(primary_header.get("reserved_flags")) is not int or primary_header["reserved_flags"] != 1:
            raise ValueError("primary_actual_clock_type_not_qpc")
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
        primary_keys = Counter(event.get("raw", {}).get("clock", {}).get("event_key")
                               for event in capture.get("events", []) if event.get("raw", {}).get("clock"))
        if any(count != 1 for key, count in primary_keys.items() if key is not None):
            raise ValueError("primary_event_key_ambiguous")
        if version == 2:
            wanted = {int(event_key(key), 16) for key in primary_keys if key is not None}
            if len(wanted) > BINARY_RECORD_LIMIT:
                raise ValueError("raw_companion_primary_key_budget_exceeded")
            index, record_count, binary_diagnostic = binary_record_index(document, raw_bytes, raw_clock_path, wanted)
            diagnostic["binary"] = binary_diagnostic
        else:
            records = document["records"]
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
            record_count = len(records)
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
                          raw_record_count=record_count, qpc_frequency=str(frequency),
                          consumer_loss_counters_available=False,
                          unsupported_boot_time_fields=dict(primary=primary_header["boot_time"], companion=header["boot_time"],
                                                            equal=primary_header["boot_time"] == header["boot_time"], proves_same_boot=False),
                          coverage="Exact original-key coverage of retained primary source events only; initial header loss counters do not establish final consumer loss.",
                          event_links=[dict(event_id=event["id"], event_key=event["raw"]["clock"]["event_key"],
                                            original_qpc=str(links[event["id"]]),
                                            delivered_filetime=event["raw"]["clock"]["header_timestamp"])
                                       for event in capture.get("events", []) if event["id"] in links])
    except (KeyError, TypeError, ValueError, AttributeError, OSError) as error:
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


def analyze(capture, truth, sid="", raw_clock_bytes=None, raw_clock_path=None):
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
        linked_qpc, report["raw_companion"] = companion_links(capture, raw_clock_bytes, raw_clock_path)
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
    report["provider_emission_contract"] = provider_emission_diagnostic(events)
    valid_events = []
    unknown_actor_events = []
    missing_qpc = 0
    report["source_actor_unknown_events"] = []
    for event in events:
        try:
            raw = event["raw"]["clock"]
            if companion_ready:
                qpc = linked_qpc[event["id"]]
            else:
                qpc = integer(raw["raw_qpc"])
                if raw["timestamp_domain"] != "qpc" or integer(raw["header_timestamp"]) != qpc:
                    raise ValueError("not original QPC")
        except (KeyError, TypeError, ValueError):
            source_ready = False
            missing_qpc += 1
            continue
        try:
            actor = identity(event, True)
        except (KeyError, TypeError, ValueError):
            unknown_actor_events.append((event, qpc))
            report["source_actor_unknown_events"].append(dict(
                event_id=event.get("id"), event_type=event.get("event_type"),
                operation=event.get("operation"), resource=event.get("resource"),
                original_qpc=str(qpc), status="Unknown", reason="actor_identity_unavailable"))
        else:
            valid_events.append((event, qpc, actor))
    if len(valid_events) != len(events):
        report["source_reasons"].append("source_original_qpc_or_actor_missing")
    report["source_events_with_raw_qpc"] = len(events) - missing_qpc
    report["source_events_without_raw_qpc"] = missing_qpc
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
                unknown = [event["id"] for event, tick in unknown_actor_events
                           if start <= tick <= end and event.get("operation") == row["operation"]
                           and normalize(event.get("resource"), sid) in paths]
                # Blocked rows still count when checking cross-row event ambiguity.
                candidates = [event["id"] for event, tick, actor in valid_events
                              if start <= tick <= end and actor == identity(row)
                              and event.get("operation") == row["operation"]
                              and normalize(event.get("resource"), sid) in paths]
                if unknown:
                    diagnostic.update(reason="actor_identity_unavailable", actor_unknown_event_ids=unknown)
                else:
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
    report["clock_provenance"] = provenance_diagnostic(metadata, truth)
    report["same_machine_boot_provenance_verified"] = report["clock_provenance"]["verified"]
    requirements = dict(original_clock_and_intervals=report["status"],
                        same_machine_boot=report["clock_provenance"]["status"],
                        provider_emission_contract=report["provider_emission_contract"]["status"])
    report["validation_readiness"] = dict(status="requirements_unmet", requirements=requirements,
        definition="Clock/interval facts remain visible when provenance or the provider emission contract is unverified; diagnostic_ready alone is not readiness or acceptance.")
    report["definition"] = ("Timing compatibility is not causal attribution or a release pass. Missing original QPC stays unavailable. "
                            "Missing actor identity is retained as Unknown and blocks every matching operation/resource/QPC interval. "
                            "Native provenance comparisons are limited to independently sampled local observations, not tamper or cloned-VM attestation. "
                            "FILETIME samples diagnose clocks only; no inverse conversion or tolerance is used.")
    report["result_contract"] = result_contract(report)
    return report


def result_contract(report, failure=None):
    """Separate execution, supplied-view evidence, product checks and formal gates.

    This tool neither performs the raw/final selection join nor runs product
    assertions. Unknown actors remain in the original interval analysis; an
    Unknown competitor still makes its row unavailable. Noncompeting Unknowns
    do not turn otherwise measured clocks into missing data.
    """
    reasons = []
    if report.get("status") != "diagnostic_ready":
        reasons.append("clock_or_interval_inputs_incomplete")
    if report.get("same_machine_boot_provenance_verified") is not True:
        reasons.append("native_clock_provenance_unverified")
    if type(report.get("source_event_count")) is not int or report["source_event_count"] <= 0:
        reasons.append("supported_source_events_unavailable")
    if "control_markers" in report and report["control_markers"].get("status") != "measured":
        reasons.append("control_marker_inputs_unavailable_or_invalid")
    return dict(revision=RESULT_CONTRACT,
        execution=dict(status="failed", **failure) if failure else dict(status="completed"),
        evidence=dict(scope="supplied_event_view_clock_inputs",
            status="not_evaluated" if failure else "incomplete" if reasons else "complete",
            raw_final_join="not_evaluated", reasons=["diagnostic_execution_failed"] if failure else reasons),
        product=dict(status="not_evaluated", reason="product_assertions_are_owned_by_the_caller"),
        formal=dict(status=report.get("validation_readiness", {}).get("status", "requirements_unmet"),
            requirements=report.get("validation_readiness", {}).get("requirements", {})),
        definition="A completed diagnostic and complete supplied-view clock inputs do not establish raw/final selection agreement, product correctness, provider semantics or formal acceptance. Each scope needs its own actual result.")


def exit_status(report, contract):
    if contract == "diagnostic-v1":
        stages = report["result_contract"]
        return 0 if stages["execution"]["status"] == "completed" and stages["evidence"]["status"] == "complete" else 2
    # Preserve the historical CLI gate unless the caller explicitly selects the
    # versioned diagnostic contract. Formal readiness is never relaxed here.
    return 0 if report["status"] == "diagnostic_ready" and report["validation_readiness"]["status"] == "ready" else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", type=Path, required=True)
    inputs = parser.add_mutually_exclusive_group(required=True)
    inputs.add_argument("--truth", type=Path)
    inputs.add_argument("--truth-root", type=Path)
    parser.add_argument("--sid", default="")
    parser.add_argument("--raw-clock", type=Path, help="Private same-session original-QPC companion; exact-key links only")
    parser.add_argument("--marker-root", type=Path, help="Independent clock-marker-*.jsonl files; never included in scored truth rows")
    parser.add_argument("--exit-contract", choices=("legacy-formal", "diagnostic-v1"), default="legacy-formal",
                        help="Explicit diagnostic-v1 exit checks execution and supplied-view clock inputs; product and formal results stay separate")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    input_hashes = {}
    result = None
    stage = "capture_input"
    try:
        capture_bytes = args.capture.read_bytes()
        input_hashes["capture"] = hashlib.sha256(capture_bytes).hexdigest()
        capture = json.loads(capture_bytes.decode("utf-8-sig"))
        if isinstance(capture, dict) and "data" in capture:
            capture = capture["data"]
        stage = "truth_input"
        sources = [args.truth] if args.truth else sorted(args.truth_root.glob("ground-truth-*.jsonl"))
        truth_bytes = [source.read_bytes() for source in sources]
        input_hashes["truth"] = [hashlib.sha256(value).hexdigest() for value in truth_bytes]
        truth = (json.loads(truth_bytes[0].decode("utf-8-sig")) if args.truth else
                 [json.loads(line) for value in truth_bytes for line in value.decode("utf-8-sig").splitlines() if line.strip()])
        raw_clock_bytes = None
        stage = "raw_clock_input"
        if args.raw_clock:
            with args.raw_clock.open("rb") as raw_source:
                raw_clock_bytes = raw_source.read(BINARY_DISK_LIMIT + 1)
            input_hashes["raw_clock"] = hashlib.sha256(raw_clock_bytes).hexdigest()
        stage = "clock_analysis"
        result = analyze(capture, truth, args.sid, raw_clock_bytes, args.raw_clock)
        if args.marker_root:
            stage = "control_marker_input"
            marker_bytes = [source.read_bytes() for source in sorted(args.marker_root.glob("clock-marker-*.jsonl"))]
            input_hashes["control_markers"] = [hashlib.sha256(value).hexdigest() for value in marker_bytes]
            markers = [json.loads(line) for value in marker_bytes for line in value.decode("utf-8-sig").splitlines() if line.strip()]
            result["control_markers"] = marker_diagnostics(capture, markers)
        result["result_contract"] = result_contract(result)
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, AttributeError) as error:
        # Record a fixed failure stage/type, not a private path or input value.
        if result is None:
            result = dict(schema_version=1, protocol_revision=PROTOCOL_REVISION,
                purpose="independent_clock_observability", release_gate=None,
                source_event_count=None, truth_rows=[], counts={},
                validation_readiness=dict(status="requirements_unmet", requirements={}))
        # A later optional-input failure must retain already computed clock facts.
        result["status"] = "execution_failed"
        result["result_contract"] = result_contract(result, dict(stage=stage, error_type=type(error).__name__))
    result["input_sha256"] = input_hashes
    result["script_lf_sha256"] = hashlib.sha256(Path(__file__).read_bytes().replace(b"\r\n", b"\n")).hexdigest()
    result["exit_contract"] = args.exit_contract
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Refuse to overwrite any historical diagnostic, score or other evidence.
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2, allow_nan=False)
        output.write("\n")
    print(json.dumps({"status": result["status"], "validation_readiness": result["validation_readiness"]["status"],
                      "counts": result["counts"], "release_gate": None, "result_contract": result["result_contract"],
                      "exit_contract": args.exit_contract}))
    return exit_status(result, args.exit_contract)


if __name__ == "__main__":
    raise SystemExit(main())
