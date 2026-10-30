# Contain v0.3.1 architecture

## Capture and evidence flow

```text
CLI -> baseline registry + read-only inventories
    -> ETW session with confirmed provider configuration and consumer callback readiness
    -> bounded initial process/thread handle observations with per-query lower bounds
    -> start scoped receiver/writer -> baseline file observations
    -> launch installer through owned handle
    -> bounded callback queue -> receiver/lifetimes -> bounded writer handoff -> SQLite batches
    -> process/thread lifetimes and scoped resource operations
    -> root exit -> bounded descendant/quiet drain -> final resource observations
    -> stop ETW -> drain receiver -> close/drain/join writer -> seal/release file reads
    -> paged lifetime resolution -> raw event attribution and persistence
    -> IRP completion + stable file ID endpoint correlation
    -> state diff + confidence dimensions + quality/statistics
    -> final state/metadata transaction + continuity gate -> inspect / history / explain
```

`crates/contain-core` implements this flow; `contain-cli` renders summaries or JSON.
The test installer queries Windows directly, independently of the attribution code.

The optional [desktop record viewer](../desktop/README.md) is a native Windows
presentation layer over the versioned CLI JSON. It uses separate process arguments
and the strict `--read-only` entrypoint for list, summary, state differences,
paged history and focused explanation. It has no database query implementation,
attribution engine or capture entrypoint of its own. Historical schema conversion
uses the existing core migration on an in-memory backup only. The summary JSON
shares measurement-availability logic with the production text renderer; missing
final counters are not promoted to measured zeros. Reports are local HTML derived
from these same queries, with original JSON and explicit event-page scope.
Its syscall journal is an external oracle used only by verification scripts.

## Observation sources

- Owned root handle: Certain launch identity, creation time and observed exit.
- Kernel-Process: retained process/thread starts/stops. Polling enriches live
  metadata and supplies fallback. Child birth must match a unique parent lifetime.
- Kernel-File: raw open/create/write/rename/delete/close and IRP completion.
  Issuing TID, when resolved against a verified event-time lifetime, identifies
  the execution context; header PID may be a system worker. This does not by
  itself identify an explicit application API call or the author of final content.
- Kernel-Registry: mutation status, optional native name and object/base context.
  Missing full paths stay explicit; a selected HKCU key is never a guessed prefix.
- File snapshots: hash, size and stable single-link file ID from the same handle.
- HKCU values: before/after type and raw bytes for one selected key.
- Services, scheduled tasks and HKCU/HKLM Run: read-only configuration inventory.

## Identity, order and confidence

Process identity is `(PID, creation FILETIME)`, never PID alone. Resolve at event
time; ambiguous PID/TID reuse remains Unknown. Retain original birth ancestry after
exit. Startup thread observations bind simultaneously held thread/process handles,
exclude threads born after the trace-start boundary, and cover only events at or
after each query completes. They cannot reconstruct missing historical starts.
Retained stops only shorten candidate lifetimes; delayed starts cannot consume a
stop needed to bound another reused instance. These maps and startup queries stay
bounded, with failures and capacity loss reported. Legacy event times remain converted FILETIME ticks. The QPC-based ETW session
currently uses a ProcessTrace consumer without RAW_TIMESTAMP, so the delivered
header is not original QPC. Optional clock metadata retains that fact, the exact
signed header, and bracketed QPC/precise-FILETIME observations; it does not invert
the conversion. Independent fixture QPC bounds are separate. JSON uses decimal
strings; SQLite uses 64-bit integers. Snapshot wall time is diagnostic observation,
not a mutation timestamp or a proven common-clock ETW interval.
Local clock provenance records independently queried host-name hashes and native
boot observations. Unsupported layouts and mismatches stay unavailable; neither
those identifiers nor a numeric interval match establish a writer or final state
ownership. Full diagnostic identifiers remain private.
Stable `(ticks, sequence, id)` order does not imply causality at equal timestamps.
Deadline decisions use monotonic time.

Application confidence remains `confidence`; `dimensions` independently describe
actor, resource and operation certainty. A known unrelated actor remains application
Unknown; a registry actor need not have a known resource. A completed request does
not prove exclusive ownership or durable final state.

For file source events, resource confidence means resolution of a **recorded
name**, not a verified current path or physical file identity. Optional
`raw.file_name` persists provider/cache provenance, the original name observation
time and source-event reference during ordinary capture. A cached name keeps its
original time after another FileObject renames the file; no shared FileKey
propagates a new name. Current capture always records `current_path_verified=false`.
Older records without this metadata remain readable, with provenance unavailable
unless their retained diagnostic payload supplies it. Neither form proves an
explicit application call or final ownership. Native Write and its completion
remain Write evidence, including writes executed inside another API's path.

Loss/decode/overflow, snapshot/notification gaps, failed shutdown statistics or drain
timeout produce Incomplete. Otherwise v0.3 reports Degraded because scope/provider
coverage cannot prove a complete footprint. There is no current path to Complete.
Unverified source continuity suppresses application promotion and IRP success
inference, while preserving raw status and independently known identity.

`events_received` counts decoder admission attempts (global lifecycle, applicable
registry observations and scoped files), not every kernel event. `events_retained`
is the persisted timeline including synthetic states. Filtering and synthesis mean
these counts do not form a received-minus-dropped equation. Known/unknown actor,
resource and High counters use file/registry source observations. ETW event losses
and buffer losses have different units and stay separate.

## SQLite and explanations

SQLite `user_version=4` keeps v1 tables, v2 observations/process instances, v3 raw
details, normalized operations, stats, quality and adjacency edges, and v4 durable
ingest staging. JSON stays schema 3. Migrations are
transactional and preserve old history without inventing evidence. Legacy captures
show missing v3 metadata. Newer schemas are rejected before any table changes.

The bounded clock audit also uses an optional `raw_selections` auxiliary table
in v4 databases. At the actual post-resolve, post-attribution registry selection
branch, supported SetValue records omitted from the final view receive a witness
in the same derived-evidence transaction. It binds the original raw document's
SHA-256, ID and sequence to the observed unresolved/non-High selection inputs.
It never rewrites the raw document or supplies a final actor. Existing public
CLI JSON and `user_version` stay unchanged. A historical database without a
witness cannot prove that a missing final record was legitimately filtered.
The private diagnostic export reads these witnesses; duplicates, changed facts,
conflicting verified actors and missing required final records remain failures.

Adjacency nodes identify sessions, `(PID,birth)`, events and resolved resources.
Edges persist relationship, confidence and reason. `explain` walks incoming actor
ancestry, includes the selected event's outgoing resource edges, and lists
operations referencing that event. It does not traverse from a shared resource
to other events. A visited-node set and the existing edge quota bound traversal;
outgoing and incoming reads use their respective adjacency indexes. Source
evidence and snapshot operations remain distinct.

Final resource reads occur while ETW and the receiver run. Production uses shared
file handles and retains no file or ancestor exclusion handles through stop/drain.
It records hashes, file identities and the reason stability remains unproven.
The bounded exclusive-read mechanism remains test-only; it is not the production
observation method. Shared reads preserve concurrent write/delete sharing, but
neither provide an atomic snapshot nor exclude replacement or write/restore races.

These facts still do not prove complete mutation coverage: an open can truncate
without a WriteFile record, and unresolved file contexts can be filtered. Production
`mutation_coverage_complete` remains false and final file-state ownership Unknown.
Registry unlocked double reads cannot exclude write/restore ABA, so final values
also remain Unknown. Earlier valid source actor evidence is not erased. Deleted
resources retain baseline-only/absence-unproven evidence. Inventory targets identify
configuration relationships, not writers. See [validation](validation-v0.4.md).

## Boundaries

Only selected roots and one registry snapshot key are scanned. Unresolved registry
operations persist only for verified installer actors. Lifetimes used only internally to reject reuse are discarded. Raw lifetimes whose
PIDs occur in scoped file headers are also retained to audit rejected attribution;
a file header PID alone still never identifies its actual writer. Known out-of-scope
resources are discarded. The app contains no telemetry/capture-upload code. CI
uploads synthetic fixture verification artifacts only.

Queues and caches are bounded, producer callbacks never block on a full application
channel, and persistence is batched. [Performance](performance.md) documents exact
limits. Snapshots and every working byte are not globally capped.

The callback queue and writer handoff each hold at most 4096 records (8192 queued
in total, matching the old single-queue count). The handoff additionally charges
at most 16 MiB of encoded JSON and rejects records over 256 KiB; encoding is counted
without a second payload allocation, but adds receiver CPU work. This is not a
total heap bound: in-flight receiver/writer pages are at most 256 records each,
the raw batch remains 256 records/1 MiB, and lifecycle/decoder/snapshot caches are
separate. SQLite never runs on the receiver; foreground process API queries run
outside its lifecycle lock. Initial identity setup remains synchronous before
installer launch; bounded lifetime attachment still takes that lock.

STOP completes before normal receiver termination. Empty final drain closes the
handoff; the writer consumes all pages, flushes its partial tail, then finalizes
the existing checkpoint policy. A 100-ms idle tick preserves partial-batch flushes.
Errors/unwinds close and join workers, and never publish a completed capture.
Thread-local profiles merge only after join using the same monotonic epoch.

No Job Object assignment, automated classification, driver, automatic elevation
or actual cleanup. The desktop viewer only queries existing records and exports
local reports; it does not start capture.
`remove --dry-run` preserves the prior rules. See ADRs for [identity](adr/0002-process-identity.md),
[file operations](adr/0003-file-operation-correlation.md),
[registry](adr/0004-registry-path-resolution.md) and [jobs/drain](adr/0005-job-objects.md).

## v0.3.1 streaming and read boundaries

The database/WAL/SHM must be outside watched roots. The independent oracle is also
outside business roots; capture never reads it. Additive raw journal, session-state
and document tables preserve schema-3 history. A crash leaves earlier WAL commits
and an unfinished session, without final attribution. Final continuity checks also
downgrade provisional observations stored earlier. No automatic resume is promised.
Summary, focused explain and requested pages avoid loading every event; complete
JSON export explicitly requests full history. See [ADR 0006](adr/0006-capture-pipeline.md)
and [counter definitions](capture-counters.md) for limits, units and balances.
Historic drop counters mixed context entries and raw records. v0.3.1 separates
raw record loss from context loss; each independently suppresses promotion.
