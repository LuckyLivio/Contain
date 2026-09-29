# ADR 0002: Retained process lifetimes and confidence dimensions

## Context
PID and TID are reusable. Querying an ETW writer after it exits fails; assigning a
new occupant of that PID creates false ownership. ProcessTrace orders events on a
best-effort basis and equal timestamps do not establish causality.

## Options
Polling only; a manifest process/thread ETW provider plus polling enrichment;
classic kernel logger; ProcessStartKey as the only identity; Job Object membership.

## Decision
Use `(pid, creation FILETIME)` as the minimum identity. Retain start, stop, image,
original parent PID, verified parent creation time, first/last observation and
session evidence. Kernel-Process start/stop and thread start/stop feed a bounded
lifetime cache; owned process handles and live polling remain independent sources.
Resolve delayed file events using issuing TID intervals and a unique process
lifetime at the event time. Parent lifetime must span the child's creation time.
Retain that relationship after parent exit. Overlapping intervals, gaps, missing
start events and reused identities remain Unknown. Loss disables attribution
derived from the reconstructed cache; live-handle actor evidence remains separate. Application promotion is suppressed
when source continuity is unverified.

Optional process sequence keys are retained as raw hexadecimal metadata. They are
not substituted for a writer identity: a header may describe a system worker, and
the available schemas do not establish a single cross-provider key contract.

Use UTC FILETIME ticks (100 ns since 1601) internally and SQLite INTEGER. JSON uses
decimal strings to prevent JavaScript rounding. ETW's converted timestamp and
GetProcessTimes use this epoch; snapshots carry collection-end time, not mutation
time. Stable timeline order is `(timestamp_ticks, ingestion_sequence, event_id)`.
Monotonic `Instant` alone drives deadlines.

Preserve application `confidence` and add `dimensions.actor`, `.resource`,
`.operation`. A known unrelated actor can have actor High with application Unknown.
A registry actor can be High while its resource is Unknown. Confidence is a rule
classification, not a calibrated probability. SQLite adjacency rows store the
session → process → observation → resource explanation without a graph server.

Provider setup waits for bounded synchronous enablement before launching the
installer; ferrisetw's default asynchronous enablement otherwise leaves a race.
See [EnableTraceEx2 timeout semantics](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-enabletraceex2).

## Consequences
Short-lived processes can be resolved after exit, and detached children retain
creation ancestry. Every High claim still requires a matching exact lifetime.
PID reuse, overlap, interval gaps, retained detached ancestry and dead-thread
resolution have deterministic unit tests. The fixture adds live regression gates.

## Limitations
Missing ETW starts, provider access failure, schema changes, clock adjustments,
PPID spoofing and privileged/protected brokers are not solved by this model.
Ancestry is Windows-reported creation ancestry, not proof of human intent or a
security boundary. Polling cannot recover a process that lived between samples.

## Sources
- [Process event fields](https://learn.microsoft.com/en-us/windows/win32/etw/process-v2-typegroup1)
- [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)
- [ProcessTrace ordering and timestamps](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace)
- [Extended process start keys](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-enable_trace_parameters)
