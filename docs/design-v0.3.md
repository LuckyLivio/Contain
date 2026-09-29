# v0.3 implementation plan: attribution reliability

Baseline: a85e17e; working tree clean and existing Windows CI successful on 2026-09-29.

## Decisions before implementation

1. Preserve `(PID, creation FILETIME)` as process identity. Add a bounded lifetime cache populated by Kernel-Process start/stop plus thread lifecycle events; polling enriches or provides fallback. Resolve after delivery using event time, not current PID. Ambiguous overlapping intervals never resolve. Preserve original birth ancestry across exits/detachment.
2. Retain raw event metadata (provider event ID, sequence, object/key/IRP, thread, status) separately from normalized operations. A correlator links requests/completions by bounded IRP lifetime and file-object generation. Rename endpoints must be proven by event/object identity or an independently verified stable file identity; same path/timing/hash alone is insufficient.
3. Separate actor/resource/operation confidence while preserving the existing application confidence. Add explicit quality/reasons, event counters, and SQLite adjacency edges with reasons. No Complete claim for a source with known coverage gaps.
4. Registry context is keyed by object plus lifetime and verified absolute path. Missing open/base events remain unresolved. Do not inject a guessed HKCU prefix or enable undocumented filters. Record the outcome of real experiments.
5. Root exit begins a bounded drain: tracked descendants and recent scoped activity postpone completion, with configurable quiet period and hard timeout. No job assignment by default.
6. JSON/SQLite version 3; v1/v2 migrate without inventing new evidence. Stable `(timestamp, sequence, source, id)` history ordering survives save/load. Add explain and concise inspect.
7. Fixture writes per-process ground truth, including short-lived/detached children, rename chains, failure and unrelated file/registry noise. Compare observation/actor attribution with zero false claims; Unknown is allowed and counted separately from missed observations.
8. Reproducible small CI and configurable 10k-file stress workflow measure timing, peak process memory, database bytes and event throughput. Only measured environment results enter compatibility/performance docs.

Safety remains: no GUI, AI, driver, job restrictions, elevation, general deletion executor, telemetry or cloud capture storage. Fixture cleanup remains limited to its marked direct TEMP child and matching test key.
