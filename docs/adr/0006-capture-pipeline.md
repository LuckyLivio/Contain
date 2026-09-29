# ADR 0006: measured capture pipeline hardening

Status: measurement protocol frozen before optimization (2026-09-29).

## Starting point and acceptance contract

The repository and remote start at `f79e4351d3e447a75dd9b7f98b645df01ffd78aa`.
The old `a8d58f8` debug run is diagnostic only, not a release comparison.
Code proves a 100000 raw-record retention cap, synchronous per-event live identity
queries, final-only SQLite persistence, and stop/join before application drain.
The relative cost of TDH, identity queries, serialization, hashing and SQLite must
be measured. Lock contention and provider filtering benefits are hypotheses.
Ferrisetw 1.2 already caches TDH schemas by provider/id/opcode/version/level.

Freeze the following oracle before changing the pipeline:

* Successful target writes, renames and deletes are supported instrumented file
  operations. Four workers with 10000 files yield exactly 25000 target operations.
  The small fixture additionally supports instrumented registry value writes.
* Independent noise uses the same operation semantics but is never target family.
  Failed syscalls are a separate denominator. An absent failed-operation event is
  unobserved, never proof of successful mutation. Uninstrumented opens/closes,
  create notifications, journals, coordination and state snapshots do not stand
  in for instrumented operations. Unsupported operations are declared by name,
  never decided from whether a candidate observed them.
* Each raw event can satisfy at most one truth row. Exact resource/operation,
  syscall time interval and PID + creation time are checked independently.
  Final state is a separate observation and cannot satisfy a transient syscall.
* Observation axis: observed, unobserved, unsupported_out_of_scope. Attribution
  axis on observed rows: correctly_attributed, incorrectly_attributed,
  observed_but_unresolved, correctly_not_assigned_to_target_app. Unobserved rows
  have null attribution. Target, noise, failed and unsupported totals stay apart.
* Audit all positive target predictions for supported mutation kinds in the tested
  roots, including unmatched positives. Extra records inside the same instrumented
  syscall are reported separately and cannot increase recall. Precision is null
  without positive predictions. Zero false attribution is a bounded fixture result.
* 10000 files / 4 workers release acceptance: three consecutive candidate trials
  with zero application queue/retention loss, zero ETW event/realtime-buffer loss,
  >=95% observation and >=95% correct attribution of the SAME 25000 target-success
  denominator, zero false target attribution. Failed gates remain failures.

## Comparison protocol

Build baseline and candidate with `cargo build --release --workspace --locked` on
the same Windows runner, token, filesystem and compiler. Record both lock hashes,
revisions, OS, CPU, profile and workload/scorer hashes. Use one frozen independent
fixture/scorer for both binaries. Move journals outside watched business roots for
BOTH sides. Alternate baseline/candidate order over at least three paired trials.
Retain every trial and medians. This is not a randomized controlled OS experiment:
background activity, caches and runner variability remain limitations. Never disable
Defender. The historical debug result is never an optimization percentage baseline.

Small fixture, 1000 files, 10000 files / 4 workers, independent noise and an immediate
exit burst are required. A deliberately tiny evidence quota tests overload separately.
Record phase wall times (including overlapping scopes), callback CPU-style elapsed
totals, queue delay/high-water, process peak working set (parent only), kernel buffer
configuration/allocated size, SQLite/WAL/spool bytes, layer losses and useful results.
Rate divided by capture wall time is admitted records per capture second, not machine
ETW capacity. Instrumentation overhead is paid by both instrumented comparisons.

## Design decision

Pending measurements. No GUI, cleanup executor, driver, sandbox or provider-mask
changes are authorized by this ADR. Keep the confirmed consumer-readiness handshake.
Do not optimize away lifecycle, object-close or completion evidence.
