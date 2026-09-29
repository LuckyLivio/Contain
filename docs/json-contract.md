# CLI JSON contract v2

`inspect <app> --json`, `diff <app> --json`, `history <app> --json`, `list --json`, and `doctor --json` produce one UTF-8 JSON document on stdout. Logs/errors use stderr. `install --manifest <path>` writes the same envelope as inspect. See [JSON Schema](schema/cli-v2.schema.json).

```json
{"schema_version":2,"kind":"history","data":{"app_id":"session UUID","events":[]}}
```

- `schema_version` on the envelope versions the API. Inspect's `data.schema_version` versions the stored capture: 1 for migrated legacy captures, 2 for new captures.
- `kind` identifies the command. Clients should allow new fields, event operations and backend states. Breaking changes require a new envelope version.
- Confidence values are `Certain`, `High`, `Medium`, `Low`, `Unknown`. All claims include a human reason and structured evidence. Arrays are empty when there are no recorded changes; inventory failures are explicit warnings, never fabricated removals.
- FILETIME fields (`timestamp_ticks`, `creation_time`, `parent_creation_time`, `ended_at`, `process_creation_time`) are **decimal strings**, 100 ns ticks since 1601 UTC. Use BigInt/u64; JavaScript numbers lose precision. `timestamp`, `first_seen`, `last_seen`, `started_at`, `finished_at` use RFC3339. Legacy records may have empty missing fields.
- `success: null` means no completion status. `state_validated` links a source observation to a separate before/after state difference; it does not turn an operation request into a successful completion.
- History is sorted by numeric ticks. Snapshot and inventory entries use observation time at session end, never an invented mutation timestamp. Legacy captures have no fabricated history.
- `backend.etw_file`/`etw_registry`: `active`, `disabled`, `unavailable`, or `not_requested`. Active means provider enabled, not guaranteed delivery. `etw_events_lost: null` means statistics unavailable. Application capacity drops and decode errors are separate counters. Loss blocks promotion of final state attribution.
- `files`/`registry` are state differences; `events` contains source observations and explicitly labelled state observations. Do not sum these as unique changes.
- Registry snapshot values are lossless `REG_TYPE:hex-bytes`. Raw ETW registry records store operations and value names, not value data. Task triggers contain XML strings. Inventory target evidence describes a configured executable association, not a known writer.

The SQLite schema is independently versioned by `PRAGMA user_version = 2`. Migration adds typed observation/process-instance/inventory tables in a transaction and preserves v0.1 tables. Newer databases are rejected before schema changes. The old PID-only process table is compatibility data; `process_instances` is authoritative for v2.

`backend.registry_path_gaps` counts registry provider records without absolute hive/key names, across the enabled provider, because these cannot be safely scoped. Any such gap prevents promotion of registry final-state attribution. These records are not assigned a guessed HKCU path.

`backend.etw_buffers_lost` separately reports lost ETW log/realtime buffers. A buffer may contain multiple events, so its count is never added to `etw_events_lost`. Missing statistics are null, not zero.
