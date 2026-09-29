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

The [instrumented release diagnostic](../examples/profile-server2025-release-v031-before.json)
at f8037e9 ran on Server 2025 / NTFS / elevated, 1000 files. Identity queries took
1.002 s versus 1.707 s callback elapsed; properties 0.232 s, event construction
0.151 s, schema 0.036 s, mutex wait 0.019 s. Final persistence took 6.979 s.
It received 177894 callbacks, admitted 152417 records, lost 35876 at the channel
and 16541 at the vector, and reported 442524 ETW events lost. Mutex contention is
not established by this result. Timing scopes overlap and are not CPU utilization.
The fixture's per-operation JSON Display writes fragmented journal data inside the
watched root. The common comparison fixture writes journals through one handle per
process outside the root. Both compared binaries use that identical fixture.

Selected changes (three closely scoped mechanisms):

1. Replace final-only raw retention with a quota-limited SQLite journal and paged
   normalization/persistence. Keep existing evidence policy and SQLite tables;
   cache prepared statements instead of reparsing the same INSERT for every row.
2. Remove the measured per-file live thread/process queries from callbacks.
   Owned decoded records carry issuing TID/time/sequence; retained process/thread
   intervals resolve them after drain. PID-only/TID-only live identity caches are
   not introduced. Registry live queries still protect registry object lifetimes.
3. Drain bounded pages continuously, including while ControlTrace STOP and
   ProcessTrace are ending. Polling remains every 30 ms, independently of queue
   consumption; a full page is followed immediately by another page. Do not
   join the producer while leaving a full queue unattended.

Rejected: duplicate TDH schema cache (ferrisetw already has it), replacing ferrisetw,
lock-free/async rewrites without measured need, raw EVENT_RECORD pointer deferral,
raising the Vec record cap, PID whitelisting at provider startup, broad exclusion
of a directory or of other writers, and tuning ETW buffers before comparable data.
No new unsafe payload handling. Keep the confirmed readiness probe. Keep file
mask 0x1ef0 / level 4, process 0x30 / level 5, registry 0x7301 / level 4 and
64 KiB buffers (minimum 8, maximum 64). Final allocated counts come from Windows.

## Bounds, persistence and recovery

The application queue has 8192 owned decoded records; each receive takes at most
256. Raw writes commit at 256 records, 1 MiB serialized payload or 100 ms of
consumer service. One record is limited to 256 KiB; the default session payload
quota is 256 MiB (`--evidence-quota-mib`, zero allowed for an overload test). This
is a raw payload quota, not a promise that the complete SQLite file is 256 MiB:
indexes, JSON/typed evidence copies, graph and WAL consume additional bounded
storage proportional to retained evidence. No callback executes SQLite.

WAL + synchronous FULL, 256-page autocheckpoint, 250 ms busy timeout. A failed
batch rolls back; earlier commits remain. A write failure stops accepting further
raw records and counts each unavailable record; errors and unfinished state are
visible. A crash leaves `capturing` or `finalizing`, never `finished`. Full reads
and raw pages of unfinished sessions suppress promotion. Finalization applies a
session-wide downgrade to previously persisted provisional observations when
continuity is lost. It is not an automatic resume mechanism.

Existing process/thread bounds remain 32768/131072; file objects, IRPs and registry
contexts remain 16384 each. Snapshot collection explicitly rejects 100000 entries
instead of returning a partial diff. Per-resource state association rejects more
than 512 observations and marks the capture incomplete. Explanations cap ancestry
and operation results at 4096. Complete JSON export explicitly materializes the
requested history; summary and paged history do not. Large configuration inventories
remain a separate cost; no claim of an absolute whole-process memory budget.

The database/WAL/SHM must be outside watched roots, checked before launch. Manifest
export occurs after tracing; hashing the after-snapshot also occurs after stop.
No whole-process exclusion suppresses genuine writes by other actors. The raw
journal includes global lifecycle/context evidence while a session is unfinished;
like the existing database it contains potentially sensitive local paths.

SQLite user_version and JSON envelope remain 3: new tables/nullable metadata are
additive, old tables and historic readback are preserved, newer unknown SQLite
versions are still rejected. The core `install` API now receives Storage and returns
a summary; CLI full inspect JSON remains the full contract. `history --limit N
--offset N` and focused explain use bounded reads for new captures.

## Provider selection rationale

Process/thread start and stop (1–4) establish lifetime and ancestry, including
unknown-at-start descendants. File open/create (12/30), close (14), write (16),
operation end (24), delete/rename paths (26/27) maintain object generations and
completion joins. Registry open/context/close (1/2/13), delete key (3), set/delete
value (5/6) maintain proven paths and user-visible operations. Unselected IDs and
out-of-scope resources are deliberately counted. Mask/level filtering is unchanged;
there is no measured claim that a narrower provider mask is safe or faster.

Windows documents buffer reservation/limits and separate loss units in
[EVENT_TRACE_PROPERTIES](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_properties).
Its [ProcessTrace](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-processtrace)
contract explains buffer processing and why callback storage must be owned before
return. [EnableTraceEx2](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-enabletraceex2)
documents provider enable semantics; the readiness acknowledgement is preserved.

Comparable repeated results will be appended after measurement. No GUI, cleanup,
driver, sandbox or broader application-ownership claims are introduced.
