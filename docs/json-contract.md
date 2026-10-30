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

SQLite versioning is independent (`PRAGMA user_version=4`, including durable ingest
staging); the JSON envelope remains 3. Migration tests from v1/v2/v3 preserve
historical observations. Additive session read indexes also install on existing
schema-4 databases. Legacy captures explicitly identify
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

In v0.4 development, inventory executable matches use `inventory_target` edges;
they do not identify an observed writer. Conflicting completions remain unresolved,
and a delete without completion is `DeleteRequested`. File/registry final-state
ownership stays Unknown when the snapshot lies outside the trace interval. A High
source event and an Unknown state diff are different evidence domains, not a
contradiction. `state_validated` is cleared on unfinished or continuity-lost source
evidence; a snapshot observation can still establish that a state difference exists.

## Desktop viewer read-only additions (0.3.1)

These are opt-in additions; existing command output and envelope version 3 remain
unchanged. `--read-only` requires an explicit `--db` path and permits only list,
inspect, diff, history and explain. It rejects install, remove and doctor before
opening the database or probing providers: the existing doctor performs active
ETW probes and is not a viewer operation.

Read-only opening requires an existing recognized Contain database. Missing,
non-database, malformed header and newer-schema inputs fail instead of creating
an empty database. Current schema-4 input is opened with SQLite read-only flags
and query-only mode; no migrations, indexes or journal settings are applied.
Each CLI invocation holds a read transaction so its statements share one snapshot;
separate invocations can observe later commits if another process is recording.
Supported older schemas use SQLite's online backup into memory and the existing
production migrations on that private copy. The original is never migrated.
No synthetic rows are inserted when a valid empty database is selected.

- `list --details --json` uses kind `list_details`. Entries include `id`, `name`,
  `started_at`, `finished_at` and nullable `capture_state`. It reads only session
  headers and recording state, ordered by descending start and then ID. A missing
  legacy state is unknown, not proof of completion.
- `inspect <id> --summary --json` uses kind `inspect_summary`. Its data is the
  existing capture shape with `view_scope:"summary"` and `history_included:false`.
  Events, normalized operations and edges are not loaded; their arrays are empty
  because they are omitted from this view, not because their counts are zero.
  Persisted statistics, process evidence, sources, state changes and quality
  remain available. Use paged history and explain for event evidence.
  `summary_counts` is the same availability-aware accounting used by the text
  summary: nullable `events_retained`, `events_normalized`, `known_actor_events`,
  `unknown_actor_events`, `unknown_resource_events`, `application_records_dropped`,
  `decode_errors`, `snapshot_gaps`, `notification_gaps`, and nullable boolean
  `drain_timed_out`. These distinguish unavailable final counters from measured
  zero and retained nonzero observations. Unfinished normalized operations are
  null; committed raw-view actor/timeline counts may still be available. Clients
  use these summary counts rather than interpreting raw default statistics.
- `diff <id> --state-only --json` retains kind `diff` and adds
  `view_scope:"state_only"`. File, registry and inventory state changes and quality
  are retained; normalized operations are excluded. The default diff is unchanged.
- Read-only `history --limit N --offset M` keeps the existing history document
  and stable `(ticks, sequence, id)` ordering. Older records without a document
  index use the existing production full-read mapping internally before returning
  a bounded page. This compatibility path can read the whole old session; modern
  document-indexed records use SQL pagination. The ordinary non-read-only legacy
  pagination error is unchanged.

These views do not reinterpret confidence, unknown metrics, historical names,
process identities or ownership. Clients still preserve exact time strings and
integer values and distinguish unavailable/null from zero. The viewer does not
start collection or active provider probes.
