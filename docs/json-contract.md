# CLI JSON contract v3

`inspect`, `diff`, `history`, `list`, `doctor`, and `explain` support `--json` and
produce one UTF-8 JSON document on stdout. Logs/errors go to stderr. The install
`--manifest` option writes an inspect document. [Schema](schema/cli-v3.schema.json).

```json
{"schema_version":3,"kind":"history","data":{"app_id":"session UUID","events":[]}}
```

## Changes from v2

- Envelope version is 3. Stored `data.schema_version` can remain 1 or 2 for migrated
  history; migration never fabricates evidence from a newer capture.
- Capture adds `stats`, `quality`, `operations` and `edges`.
- Events add `sequence`, `raw` and `dimensions` (actor/resource/operation). Existing
  `confidence` still means application attribution.
- Raw metadata includes event ID, TID, header PID, FileObject, FileKey, registry
  object, object generation, IRP, related request ID, NTSTATUS, optional process keys
  and whether the resource was resolved. Hex object/key values are strings.
- `EtwProcess` is a new evidence source. History includes retained lifecycle and
  completion observations alongside explicit state observations.
- Diff adds normalized operations/quality. Explain returns the event, incoming
  ancestry, applicable operations, application identity and quality.
- Backend/doctor add process ETW status; backend adds decoder admission count.

Clients should allow new fields and operation/backend labels. Confidence is Certain,
High, Medium, Low or Unknown; it is not an accuracy probability. `success:null`
means no resolved completion. `state_validated` means correlation with a separate
state diff, not that a request completed successfully.

FILETIME fields (`timestamp_ticks`, creation/parent creation/process creation and
`ended_at`) are decimal strings in 100 ns units since 1601 UTC. Use BigInt/u64.
Readable timestamps are RFC3339. Legacy missing fields may be empty/null. SQLite
stores ticks as 64-bit integers. History sorts `(ticks,sequence,id)` deterministically
across readback. Snapshot time is collection time, never an invented mutation time.

`events` and `operations` have different cardinalities. Operations can reference
several raw events. Snapshot Renamed/Replaced use stable identity but never invent
actors or intermediate chains. Do not add events and state changes as unique mutations.

Backend statuses include active, disabled, unavailable and not_requested. Active
means configuration succeeded, not full delivery. ETW event and buffer loss have
different units; null means unavailable, never zero. Application drops and decode
errors are separate. Registry path gaps count missing names across the provider,
not just the watched key. No guessed hive name is inserted.

Quality includes reasons. Current captures are Degraded or Incomplete, never Complete.
Stats count admitted/retained/normalized observations, drops, actor/resource resolution,
High application events, elapsed time, snapshot/notification gaps and drain timeout.
[Architecture](architecture.md) defines the counting domains.

SQLite versioning is independent (`PRAGMA user_version=3`). Real v1 -> v3 and v2 -> v3
migration tests preserve historical observations. Legacy captures explicitly identify
missing metadata. Newer databases are rejected before schema mutation. The authoritative
lifetime table is `process_instances`; PID-only v1 rows are compatibility data.

## v0.3.1 additive capture metadata

The envelope remains schema version 3. `capture_state` is null for legacy captures
or `capturing`, `finalizing`, `failed`, `finished` for new sessions. Non-finished
sessions are Incomplete and public evidence is provisional Unknown.
`backend.pipeline` and `backend.stream` add stage counters; missing measurements
are null, including ETL-log buffer loss in realtime-only capture. See
[counter definitions](capture-counters.md). Historic v1/v2/v3 full readback remains.

Core `capture::install(options, storage)` returns a summary with persisted counters;
use `Storage::load` for full history, `load_summary` for summary, `events_page` for
1–4096 records, and `for_event` for focused explanation. CLI `inspect --json` and
unpaged history retain complete export semantics. CLI history accepts `--limit`
and `--offset`; legacy databases without a document index explicitly ask for
unpaged history. Requested pagination never claims to contain every event.
