"""Print existing loss counters for one exact session; never capture or migrate.

Run after closing Contain normally (Python 3, standard library only):
  py -3 scripts/session-loss-counts.py --database <database> --session-id <exact-id>

Only whitelisted numbers, booleans and fixed status labels leave the database.
Missing historical fields are "未提供", never assumed to be zero. Errors omit
SQLite exception text, which can contain private database paths or documents.
Nonempty WAL files are refused: immutable read-only SQLite must not silently
ignore uncheckpointed commits. Do not delete WAL/SHM files to bypass this check.
"""
import argparse
import contextlib
import json
from pathlib import Path
import sqlite3
import sys


MISSING = "未提供"


class UncheckpointedWal(ValueError):
    pass


class DatabaseChanged(ValueError):
    pass


NUMERIC_FIELDS = {
    "backend": ("events_received", "dropped_events", "etw_events_lost",
                "etw_buffers_lost", "decode_errors", "context_losses"),
    "pipeline": ("callback_records", "deliberately_filtered", "unsupported_records",
                 "queue_overflow", "enqueue_disconnected", "enqueued", "dequeued",
                 "queue_pending", "queue_high_water", "context_evictions",
                 "retention_dropped", "persistence_succeeded", "persistence_failed"),
    "handoff": ("initial_accepted", "received", "submitted", "overflow", "oversized",
                "disconnected", "serialize_failed", "drained", "pending",
                "high_water_records", "high_water_bytes", "record_capacity", "byte_capacity"),
    "stream": ("accepted", "persisted", "failed", "quota_dropped", "committed_bytes", "quota_bytes"),
    "follow_installer": ("candidates", "admitted", "excluded_other", "unresolved",
                         "excluded_self_io", "candidate_dropped", "candidate_record_limit",
                         "candidate_byte_limit", "candidate_high_water_records",
                         "candidate_high_water_bytes"),
    "stats": ("events_retained", "events_normalized", "known_actor_events", "unknown_actor_events"),
}


def number(value):
    return value if type(value) is int and value >= 0 else MISSING


def boolean(value):
    return value if type(value) is bool else MISSING


def status(value, allowed):
    return value if isinstance(value, str) and value in allowed else MISSING


def object_json(value):
    if value is None:
        return {}
    result = json.loads(value)
    if not isinstance(result, dict):
        raise ValueError("invalid metadata object")
    return result


def object_field(value, field):
    result = value.get(field)
    return result if isinstance(result, dict) else {}


def optional_row(db, tables, table, columns, session):
    if table not in tables:
        return None
    # Identifiers are internal constants; user input is always a bound value.
    return db.execute("SELECT " + columns + " FROM " + table + " WHERE session_id=?", (session,)).fetchone()


def known_sum(*values):
    return sum(values) if all(type(value) is int for value in values) else MISSING


def report(db, session):
    """Read one consistent snapshot. Does not read paths or event documents."""
    tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if "installation_sessions" not in tables:
        raise ValueError("unsupported database")
    header = db.execute("SELECT finished_at,exit_code FROM installation_sessions WHERE id=? COLLATE BINARY", (session,)).fetchone()
    if header is None:
        raise LookupError("exact session ID not found")
    metadata = optional_row(db, tables, "capture_metadata", "backend_json", session)
    backend = object_json(metadata[0] if metadata else None)
    reliability = optional_row(db, tables, "reliability_metadata", "stats_json,quality_json", session)
    stats = object_json(reliability[0] if reliability else None)
    quality = object_json(reliability[1] if reliability else None)
    run = optional_row(db, tables, "capture_runs", "state,stats_json,error", session)
    # Match production readback: unfinished runs use committed journal stats.
    stream = object_field(backend, "stream")
    if run and run[0] != "finished":
        stream = object_json(run[1])
    groups = {"backend": backend, "stats": stats, "stream": stream}
    groups.update({key: object_field(backend, key) for key in ("pipeline", "handoff", "follow_installer")})
    result = {key: {field: number(groups[key].get(field)) for field in fields}
              for key, fields in NUMERIC_FIELDS.items()}
    result["session"] = {
        "exact_id_found": True,
        "capture_state": status(run[0] if run else None, {"capturing", "finalizing", "failed", "finished"}),
        "finished_at_recorded": bool(header[0]),
        "installer_exit_code": header[1] if type(header[1]) is int else MISSING,
        "coverage_quality": status(quality.get("level"), {"Complete", "Degraded", "Incomplete"}),
    }
    result["stats"]["drain_timed_out"] = boolean(stats.get("drain_timed_out"))
    for field in ("receiver_finished", "writer_finished"):
        result["handoff"][field] = boolean(groups["handoff"].get(field))
    # Never expose error text, including private paths embedded by SQLite.
    result["stream"]["error_present"] = (
        stream.get("error") is not None if "error" in stream else MISSING)
    if run and run[2] is not None:
        result["stream"]["error_present"] = True
    for field in ("etw_process", "etw_file"):
        result["backend"][field] = status(backend.get(field), {"active", "not_requested", "unavailable", "failed", "disabled"})
    p, h, s = (result[key] for key in ("pipeline", "handoff", "stream"))
    receive_loss = known_sum(p["queue_overflow"], p["enqueue_disconnected"])
    handoff_loss = known_sum(*(h[field] for field in ("overflow", "oversized", "disconnected", "serialize_failed")))
    persistence_loss = known_sum(s["quota_dropped"], s["failed"])
    raw_loss = known_sum(receive_loss, handoff_loss, persistence_loss)
    recorded_loss = result["backend"]["dropped_events"]
    result["application_loss_accounting"] = {
        "receive_queue_loss": receive_loss,
        "handoff_loss": handoff_loss,
        "raw_persistence_loss": persistence_loss,
        "sum": raw_loss,
        "matches_recorded_total": raw_loss == recorded_loss
        if type(raw_loss) is int and type(recorded_loss) is int else MISSING,
    }
    # Scope exclusions and follow-candidate/context losses are deliberately not
    # added to application raw-record loss. They have independent meanings.
    result["stored_rows"] = {}
    for table in ("raw_ingest", "raw_stream", "observations", "normalized_operations"):
        result["stored_rows"][table] = (db.execute(
            "SELECT COUNT(*) FROM " + table + " WHERE session_id=?", (session,)).fetchone()[0]
            if table in tables else MISSING)
    return result


def read_report(path, session):
    path = Path(path)
    if not path.is_file():
        raise FileNotFoundError("database missing")
    path = path.resolve()
    wal = Path(str(path) + "-wal")
    if wal.exists() and wal.stat().st_size:
        raise UncheckpointedWal("normal application close required")
    before = path.stat()
    # Plain mode=ro may write WAL read marks to a source SHM sidecar. Immutable
    # mode avoids all source writes but ignores WAL, hence the explicit guard.
    with contextlib.closing(sqlite3.connect(path.as_uri() + "?mode=ro&immutable=1", uri=True, timeout=1)) as db:
        db.execute("PRAGMA query_only=ON")
        db.execute("BEGIN DEFERRED")
        result = report(db, session)
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns) or (wal.exists() and wal.stat().st_size):
        raise DatabaseChanged("database changed while reading")
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", required=True)
    parser.add_argument("--session-id", required=True)
    args = parser.parse_args(argv)
    try:
        result = read_report(args.database, args.session_id)
    except UncheckpointedWal:
        result, code = {"error": "nonempty_wal_close_contain_normally_do_not_delete_sidecars"}, 1
    except DatabaseChanged:
        result, code = {"error": "database_changed_close_contain_and_retry"}, 1
    except LookupError:
        result, code = {"error": "exact_session_id_not_found"}, 2
    except (OSError, sqlite3.Error, ValueError, TypeError):
        result, code = {"error": "read_only_diagnostic_failed"}, 1
    else:
        code = 0
    # ASCII escapes keep output portable even in legacy Windows console codepages.
    print(json.dumps(result, ensure_ascii=True, sort_keys=True, indent=2))
    return code


if __name__ == "__main__":
    sys.exit(main())
