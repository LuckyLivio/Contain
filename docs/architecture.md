# Contain v0.3 architecture

## Capture and evidence flow

```text
CLI -> baseline snapshots + read-only inventories
    -> ETW session with confirmed provider configuration and consumer callback readiness
    -> launch installer through owned handle
    -> process/thread lifetimes and scoped resource operations
    -> root exit -> bounded descendant/quiet drain -> stop/drain ETW
    -> retained lifetime resolution -> raw event attribution
    -> IRP completion + stable file ID endpoint correlation
    -> state diff + confidence dimensions + quality/statistics
    -> one SQLite transaction -> inspect / history / explain
```

`crates/contain-core` implements this flow; `contain-cli` renders summaries or JSON.
The test installer queries Windows directly, independently of the attribution code.
Its syscall journal is an external oracle used only by verification scripts.

## Observation sources

- Owned root handle: Certain launch identity, creation time and observed exit.
- Kernel-Process: retained process/thread starts/stops. Polling enriches live
  metadata and supplies fallback. Child birth must match a unique parent lifetime.
- Kernel-File: raw open/create/write/rename/delete/close and IRP completion.
  Issuing TID identifies the writer; header PID may be a system worker.
- Kernel-Registry: mutation status, optional native name and object/base context.
  Missing full paths stay explicit; a selected HKCU key is never a guessed prefix.
- File snapshots: hash, size and stable single-link file ID from the same handle.
- HKCU values: before/after type and raw bytes for one selected key.
- Services, scheduled tasks and HKCU/HKLM Run: read-only configuration inventory.

## Identity, order and confidence

Process identity is `(PID, creation FILETIME)`, never PID alone. Resolve at event
time; ambiguous PID/TID reuse remains Unknown. Retain original birth ancestry after
exit. All source and snapshot times use UTC FILETIME ticks. JSON uses decimal
strings; SQLite uses 64-bit integers. Snapshot time means observation, not mutation.
Stable `(ticks, sequence, id)` order does not imply causality at equal timestamps.
Deadline decisions use monotonic time.

Application confidence remains `confidence`; `dimensions` independently describe
actor, resource and operation certainty. A known unrelated actor remains application
Unknown; a registry actor need not have a known resource. A completed request does
not prove exclusive ownership or durable final state.

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

SQLite `user_version=3` keeps v1 tables, v2 observations/process instances, and v3 raw
details, normalized operations, stats, quality and adjacency edges. Migrations are
transactional and preserve old history without inventing evidence. Legacy captures
show missing v3 metadata. Newer schemas are rejected before any table changes.

Adjacency nodes identify sessions, `(PID,birth)`, events and resolved resources.
Edges persist relationship, confidence and reason. `explain` walks incoming actor
ancestry and lists operations referencing the selected event. A visited-node set
bounds graph traversal. Source evidence and snapshot operations remain distinct.

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

No Job Object assignment, GUI, AI, driver, automatic elevation or actual cleanup.
`remove --dry-run` preserves the prior rules. See ADRs for [identity](adr/0002-process-identity.md),
[file operations](adr/0003-file-operation-correlation.md),
[registry](adr/0004-registry-path-resolution.md) and [jobs/drain](adr/0005-job-objects.md).
