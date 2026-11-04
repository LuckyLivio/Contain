# v0.4 reproducible validation

The scripts in this document separate synthetic fixture validation, ETW acceptance,
CLI consistency and third-party installer validation. A passing fallback run does
not satisfy the ETW release gate. No vendor installer has been run in this work.

## Current diagnostic protocol 4 and exploratory boundary

All eight exploratory captures have now been audited, including the separately
recovered analysis of the interrupted fifth attempt. The executed candidate is
`545ed3f`, baseline `e72d88c`; the later report/harness revision is not a new runtime.
The [eight-arm audit](validation-v0.4.md) records every failed predicate, missing
measurement and original input identity. All eight retained legacy scores violate
the unexpected-positive requirement; candidate/on at 10,000 files also reaches
the quiet-drain deadline and companion disk quota. Raw-off clock validation remains
unavailable, with original false values preserved. The protocol below describes
the fixed exploration; it is not an instruction to rerun it. No further capture
or formal 32-attempt acceptance was started by this audit.

`contain-clock-observability-4` adds independently sampled local provenance to
protocol 3; numeric interval matching, zero tolerance, success denominators and
the legacy scorer are unchanged. It requires two collector observations bracketing
the trace and one independent initialization observation per fixture process,
consistent across that process's rows and completed before the recorded calls.
Hashing the physical DNS name limits accidental disclosure in summaries; the
identifiers and complete samples still belong only in private artifacts.

The native boot information query accepts only status zero, the observed 32-byte
layout and a nonzero identifier. [The native API may change](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation);
this is an explicitly versioned local observation rather than a stable Win32
boot-identity contract. [Physical DNS naming](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getcomputernameexw)
also does not attest physical hardware or cloned guests. Missing observations,
mismatches, failed API calls, malformed shapes or inconsistent phase bounds remain
unavailable. Old captures deserialize with absent provenance; no current query is
used to repair them retrospectively.

Common-protocol reassessment retained both historical captures and their prior
results. The capacity-failed sample remains 21 unavailable rows; the later small
clock-on capture remains 17 unique interval candidates, two actor-unavailable rows
and two without qualified in-interval sources. Both exit 2 and lack contemporary
collector provenance. The complete inputs, sidecars and prior results were hashed
before and after; no truth row changed. New outputs and the comparison manifest
are under `target/stabilization/evidence-load-20261025/`.

Numeric candidates are not operation-completion or attribution scores. The current
`provider_emission_contract` is `unverified_general` and `validation_readiness`
remains `requirements_unmet`, including when local provenance matches. A new run
cannot by itself enable a formal pass. Required follow-up is a scoped protocol
for the observed provider GUID/event/version and synchronous call type on the
recorded Windows build: relate requests to unambiguous completion/status and
independent call boundaries, retain failed-Open cases that may not emit deletion,
and leave cross-thread one-tick ordering ambiguous without extending intervals.
Control-marker provenance is still reported separately as unverified.

The authorized eight-attempt exploration uses the existing matrix runner with
frozen `e72d88c` baseline, a reviewed candidate, one common new fixture and the same
three scoring scripts. For each of 1,000 and 10,000 files, execute baseline-on,
candidate-on, candidate-off, baseline-off, with four workers and 100 noise files.
Use new output/fixture directories, keep all journals and databases, and retain
per-attempt binary/script hashes and exact configuration. Label every result
`exploratory`, never formal acceptance. The private fixed launcher refuses an
unfrozen plan or a non-administrator token and never elevates or cleans up.
Uncertain worker termination stops subsequent arms with explicit unexecuted rows.
Missing clock/identity-dependent metrics remain null; raw-off is a separate cost
control and its legacy score cannot establish original-clock validation. This
exploration may run before the small diagnostic passes; the formal 32 attempts
below still require audited prerequisites and a separate freeze.

## Current environment boundary

The read-only preflight on 2026-10-22 recorded a Windows host and a non-elevated
token. Detailed host inventory is retained locally. Hypervisor presence was true, but `Get-VM`, `Get-VMSnapshot`, `WindowsSandbox.exe`, `vmrun.exe`
and `VBoxManage.exe` were unavailable on PATH. No disposable guest, recoverable
checkpoint or independent reference trace was established. This is insufficient
for third-party installation tests; it is not a statement that virtualization is
impossible on this computer.

```powershell
./scripts/v04-preflight.ps1 -Output ./target/v04/preflight.json
# Optional existing Contain doctor probe; this never elevates or installs software:
./scripts/v04-preflight.ps1 -Contain ./target/release/contain.exe -Output ./target/v04/preflight-with-doctor.json
```

The optional doctor starts and stops its own ETW probe. Its provider activation
does not demonstrate complete delivery. The preflight deliberately does not issue
an installation permission based on a VM-looking model or a caller-supplied flag.

## Paired fixture matrix

Build the frozen baseline and candidate with the same Rust toolchain, locked
dependencies and release settings. Preserve both executable hashes. Use the same
new fixture executable for both arms: `--workers` defaults to the original four
workers; the original fixture operation definitions and `score-fixture.py` are
unchanged. File counts are divided evenly, including the remainder; deletion of
every even-numbered worker file determines the exact denominator. Thus 100 files
with four workers still means 252 target syscalls, and 1000/10000 mean 2500/25000.

```powershell
python scripts/v04-matrix.py --baseline target/v04/baseline-bin/contain.exe --candidate target/release/contain.exe --fixture target/release/contain-test-installer.exe --output target/v04/paired-etw --files 100 500 1000 3000 10000 --workers 1 4 8 --trials 3 --noise-files 100
# Non-admin fallback is a different experiment:
python scripts/v04-matrix.py --baseline target/v04/baseline-bin/contain.exe --candidate target/release/contain.exe --fixture target/release/contain-test-installer.exe --output target/v04/paired-fallback --files 100 500 1000 3000 10000 --workers 1 4 8 --trials 3 --noise-files 100 --snapshot-only
```

Output directories must be new. Every attempted capture gets a measurement before
launch. The full attempt plan, executable/scorer hashes, raw stdout/stderr, complete
capture export, independent syscall journal, score, profile and database remain
local. Failed or missing attempts remain in the denominator. Exit code 2 reports
at least one failed gate or harness failure. No test threshold is reduced: 1000
files requires every target syscall observed and correctly attributed; other ETW
scales require at least 95%, zero false attribution, zero measured loss, balanced
counters, an empty queue and no drain timeout. Fallback checks state conservatism
and returns `etw_release_gate:null` even when those checks pass.

Pairs alternate baseline/candidate order between trials. `--noise-files 0 100
1000` adds independent writer loads; each noise file produces three syscalls.
`--seed-database PATH` adds a database-size condition alongside the empty database.
Repeat the option for multiple seeds. Each condition uses a SQLite backup of
committed source state, including WAL; the seed is never opened by Contain. Freeze
seed writers for the experiment and retain seed hashes. This measures existing
database size without inventing artificial session rows. Use `--baseline-variant
D0 --candidate-variant D3` only for an explicitly labelled experiment; inherited
profiling/hotpath/checkpoint/filter environment settings are cleared.

Parent CPU time and logical I/O counters use a retained Windows process handle
through exit. Mean CPU percent divides CPU seconds by capture wall and logical CPU
count. I/O transfer bytes are process I/O, not physical disk traffic. Peak working
set excludes fixture descendants and kernel ETW buffers. Private-memory and DB/WAL
peaks are sampled every 20 ms; separate peaks must not be summed as simultaneous
usage. Capture wall includes process exit/DB close; export and scoring are outside
timing. Instrumentation and background activity still affect measurements.

The dedicated [v0.4 workflow](../.github/workflows/v04.yml) compares frozen baseline
`7c5a5b2b534d235f4db6c472c2c34a60e8be66ee` against the checked-out candidate on one
hosted Windows runner: 100/1000/10000 files, four workers, three alternating pairs,
300 independent noise syscalls per attempt. It runs all 18 attempts and uploads
evidence before enforcing a failing result. It executes only the bounded synthetic
fixture. A red result at 1000 or 10000 is a failed release gate, even if every
measurement completed. These samples cannot be pooled with local fallback runs.

The following **stabilization protocol is preregistered and has not been executed**.
Use `--stabilization` to keep ordinary matrix defaults compatible while requiring
the original three scales, at least three pairs at 100/10000, and ten pairs at
1000 by default (`--stability-trials`, range 10..30). Each worker/noise/database
condition gets its own alternating baseline/candidate pairs; no failed attempt
is discarded. `--snapshot-only`, fewer trials, omitted scales, duplicate
conditions and abbreviated source revisions are rejected. The three-scale,
four-worker, single-noise/empty-database command below plans **32 attempts**:

```powershell
# Preregistration example, not an executed validation result. Set these paths
# and full 40-character revisions to the prebuilt, frozen experiment inputs.
python scripts/v04-matrix.py --stabilization --baseline $baselineExe --candidate $candidateExe --fixture $commonFixtureExe --baseline-revision $baselineRevision --candidate-revision $candidateRevision --output target/v04/stabilization-etw --files 100 1000 10000 --workers 4 --trials 3 --stability-trials 10 --noise-files 100
```

The manifest retains protocol/version, parameters, planned attempts, source/script
hashes and supplied revisions. Revisions are operator declarations, not executable
build attestations. All three executable hashes are checked before and after every
attempt; replacement fails that attempt and retains its fixture. The same opt-in
QPC fixture is used for both arms. Ordinary ETW captures keep their original
`score.json` and strict `gate`, with `clock-diagnostic.json` written separately.
Stabilization additionally records `legacy_acceptance_pass` and an explicit
`stabilization_gate`. Its status is currently **blocked**. The administrator
diagnostic now supplies original ETW QPC for its retained source events, but still
has unresolved actor rows. The formal paired inputs, same-machine/same-boot
provenance and a validated provider emission-time contract remain unverified.
Even `diagnostic_ready` cannot satisfy that new gate. There is
no override or automatic fallback that can turn it green.

When a new capture supplies `backend.handoff`, its counters must be nonnegative
integers and satisfy all of the following in addition to the frozen original
pipeline/storage/loss gates:

```text
pipeline.dequeued = handoff.initial_accepted + handoff.received
handoff.received = handoff.submitted + overflow + oversized + disconnected + serialize_failed
handoff.submitted = handoff.drained + handoff.pending
stream.accepted = handoff.initial_accepted + handoff.submitted
handoff.pending = overflow = oversized = disconnected = serialize_failed = 0
```

Missing or malformed handoff fields cannot pass. A balanced queue with recorded
loss still fails. Both receiver/writer completion flags must be exactly true;
record and byte high-water marks must fit their positive recorded capacities.
Captures without handoff retain the previous direct
`pipeline.dequeued = stream.accepted` contract. This protocol has no remote
dispatch or elevation capability. The current public exporter predates these
additional diagnostic fields; retain complete private records and review its
whitelist before publishing a future stabilization run.

`v04-public.py PRIVATE NEW_PUBLIC_DIRECTORY` exports a numeric/scoped projection
after original scoring. It preserves all planned attempts and scored rows, replaces
validated fixture roots with `<FIXTURE>`, and redacts outside paths. Global process
or inventory records, complete captures, databases, raw logs and arbitrary error
strings are not published. Hashes identify the private measurements. The original
private records are not rewritten; a public projection is not a complete raw trace.

## Attribution and CLI consistency

```powershell
python scripts/v04-verify-views.py --contain target/release/contain.exe --database target/v04/paired-fallback/TRIAL/capture.db --app APP_ID_FROM_MEASUREMENT --output target/v04/views.json
python scripts/v04-metrics.py target/v04/paired-fallback/TRIAL/score.json --output target/v04/metrics.json
```

The view checker makes a SQLite backup before invoking the CLI because existing
read commands may migrate a database. It compares every inspect/history/paged
history event and every diff state/operation/quality field, then compares every
explain result and checks a positive file/registry source event's chain to the
installer process instance. `--max-explain 64` is an optional diagnostic sample:
it explicitly reports incomplete and cannot pass exhaustive acceptance. Source
event attribution and surviving snapshot ownership are separate domains; a High
request and an Unknown state can be consistent. A failed request can still have
a verified actor; actor attribution does not establish a successful mutation.

The metrics wrapper reads the unchanged fixture score; it is not a vendor oracle:

| Metric | Fixed domain |
|---|---|
| Precision | Correct audited raw positive predictions / all audited raw positive predictions; no predictions means null |
| Recall | Correctly attributed successful target syscall rows / independent successful target syscall rows |
| Unknown rate | Unobserved or observed-but-unresolved successful target/noise rows / all supported successful target/noise rows |
| False attribution rate | Incorrectly attributed successful target/noise rows / all supported successful target/noise rows |
| Raw false discovery rate | Unexpected/incorrect audited raw positives / all audited raw positives, including unmatched positives |
| Event loss rate | Null: the available ETW counters do not establish the enabled-provider event denominator |

Verified unrelated actors do not inflate Unknown. Failed and unsupported calls
retain separate denominators. ETW event loss, ETW buffer loss and application drop
counts remain separate, and missing counters remain null. Positive raw duplicates
do not increase syscall recall. Consistent CLI output is not independent truth.

## Raw-clock diagnostic entry

The user-run administrator demo `etw-small-20261023-202449` is valid real ETW
evidence under the original scorer; see [its archive and limitations](validation-v0.4.md).
It predates the following code and has no original-QPC sidecar. Its old score is
not changed. Its clock diagnostic still returns unavailable for all 21 truth rows.

The local ferrisetw 1.2.0 patch exposes the numeric header returned by successful
OpenTraceW and a consumer-only subscription API. The default consumer remains
Windows-converted FILETIME. The actual primary header is saved additively in
`backend.clock.trace_header`; old DB/JSON remain readable. No boot-time formula,
calibration fit or FILETIME-to-QPC inversion is used. See the patch's
[upstream identity and interface notes](../vendor/ferrisetw/CONTAIN-PATCH.md).

Only an explicit absolute `CONTAIN_ETW_RAW_CLOCK_PATH` enables a second consumer
of the **same session**, with `RAW_TIMESTAMP`. Both consumers must receive the
readiness probe before the installer launches. The companion reserves a new file;
it refuses overwrite, watched roots and the capture DB/WAL/SHM. Startup errors
abort launch; STOP failure closes the raw consumer before joining and marks the
tail incomplete. Missing keys, duplicates, capacity refusal, persistence errors
and partial artifacts stay visible. Default capture queues are not enlarged.

The original protocol-1 sidecar retained at most 262,144 rows until STOP and then
serialized verbose JSON. The administrator run delivered 896,972 callbacks, so
634,828 capacity refusals are retained as a failed diagnostic; they are not
primary queue loss. Raising that lifetime cap alone would also exceed its
128-MiB JSON allowance for this workload.

Protocol 2 keeps all records from the same three providers, including unrelated
noise and context. A preallocated pool of 32 chunks, each 4,096 fixed 64-byte rows,
feeds a separate writer using nonblocking submission. The callback never waits
for disk and never allocates another chunk when the pool is full. Row storage is
8 MiB, with a separate 1-MiB writer stack, within the existing 32-MiB payload
budget. These bounds do not include the primary collector or OS buffers.

The JSON manifest now references its sibling `<manifest>.rows.bin`. The binary
wire layout is explicitly little-endian: sequence u64, original event key u64,
signed QPC i64, provider u128, id u16, version/channel/level/opcode u8 each, task
u16, keyword u64, PID u32, TID u32. The unchanged 128-MiB combined disk budget
reserves 64 KiB for metadata and permits 2,096,128 rows. Thus 896,972 callbacks
require 57,406,208 payload bytes, without a lifetime-sized in-memory collection.
This capacity calculation is not proof of sustained live write throughput.

Queue exhaustion, disk quota exhaustion, writer closure, partial writes, pending
records and failed or timed-out drains remain separate visible failures. Data
and metadata writers each wait at most 30 seconds plus a one-second cancellation
grace; an unresponsive writer is marked incomplete and cannot supply a committed
hash. Metadata serialization is capped at 64 KiB; its seek, truncate, write and
sync run in the cancellable worker, including startup failure records. Final
callback counters are copied after draining; `stats_final_available=false` cannot
pass the audit. Metadata commit failure cannot be reported as `written=true`.
Startup and OS filesystem calls are not a hard real-time guarantee. Longer
system-wide workloads can still exceed the finite quota and must fail visibly.
Extra EventKey data and the writer's own I/O create overhead, including possible
system-wide callbacks even though output is outside watched roots.

Do not pool diagnostic-on and diagnostic-off results. `demo.ps1 -OutputDirectory`
records parent CPU, memory and logical I/O alongside wall time and DB/WAL samples;
missing measurements are null. I/O transfer counters are not physical disk usage.
The sidecar, binary payload, database and console logs remain private.

`v04-clock-protocol.py --raw-clock SIDECAR` validates its SHA-256, byte count,
session, actual headers, bounds, counter partitions and termination. Links require
the unique ETW EventKey **and** identical provider, descriptor, PID and TID.
Missing, duplicated, inconsistent or zero qualifying links are unavailable.
Original QPC and primary FILETIME remain distinct in the diagnostic report.
The protocol-2 reader streams the entire bounded payload and checks every key
for duplicates, including unmatched noise, before linking primary records. Its
offline key sort has a finite 2,096,128-key bound; it is outside the capture's
32-MiB budget. A producer-side duplicate count of null is never treated as zero.
Protocol-1 evidence remains readable under the same revised clock protocol.
[Microsoft documents EventKey as an event-instance identifier](https://learn.microsoft.com/windows/win32/api/evntrace/ns-evntrace-enable_trace_parameters).
Synthetic non-provider metadata is counted separately, not relabelled as lost
target operations. The OpenTrace header is an initial sample, not final loss
statistics: `consumer_loss_counters_available=false` remains explicit. Exact links
establish clock coverage only for retained primary events; they do not establish
consumer-wide completeness or a loss rate. BootTime is retained without using it
as boot provenance; [it is only supported for Global Logger](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-trace_logfile_header).
Same-machine/same-boot provenance and provider emission semantics still need live
verification. `diagnostic_ready` never means attribution or release acceptance.

### Real-time StartTime protocol correction

Clock diagnostic revision `contain-clock-observability-2` corrects the former
requirement that both real-time OpenTrace headers contain a positive StartTime.
The native wrapper copies `EVENT_TRACE_LOGFILEW.LogfileHeader.StartTime` immediately
after successful OpenTraceW; it does not calculate or reinterpret the value.
The user-run `etw-raw-20261023-212829-782` recorded zero in both headers at this
`open_before_process_trace` phase. The [Windows header contract](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-trace_logfile_header)
defines StartTime but does not promise a populated real-time value. Microsoft's
[reference consumer](https://github.com/microsoft/perfview/blob/9cf2c8f74696e9f518b709e889c1a9a7d6423e27/src/TraceEvent/ETWTraceEventSource.cs#L470-L519)
explicitly avoids the header-derived session times for real-time sessions. Its
approximate wall-time fallback is **not** used here.

The revised diagnostic retains exact zero and reports
`start_time_observation.status=unavailable_at_realtime_open`, with
`available=false` and `used_as_clock_origin=false`. Both fields must still be
canonical decimal nonnegative i64 strings and equal. This applies only after the
observed real-time/raw and primary consumer modes and header phase are checked.
Original event keys plus every recorded descriptor field establish the links;
QPC frequency and independently sampled ready/stop and operation brackets establish
the measured interval relation. StartTime establishes neither. No clock origin,
offset, tolerance or same-boot proof is manufactured. Existing loss, ambiguity,
hash, session, budget and termination checks remain required; malformed time
fields and incomplete calibration remain failures.

The original diagnostic and artifacts remain unchanged. Re-evaluation of that
same administrator sample with revision 2 still returns `unavailable` for all
21 truth rows: the companion is incomplete after 634,828 capacity refusals.
Its 3,819/3,819 main-chain accounting is a separate result. The old report is not
replaced or converted into a pass. Local re-evaluation and before/after regression
logs are retained in `target/stabilization/raw-clock-stabilization/`; the formal
32-attempt protocol remains pending.

### Unknown-actor clock protocol correction

Revision `contain-clock-observability-3` separates original-clock completeness from
actor completeness. The new administrator clock-on sample has exact original-QPC
links for all 35 eligible file/registry records, but 14 lack a verified actor
instance. Revision 2 made any such record invalidate every independent operation,
including operations with a complete identity and an exact interval match. Eleven
of the 14 records have system-process header PIDs outside the corresponding
operation intervals or on control files; three have an unrelated fixture's header
PID inside two unrelated write intervals. A header PID does not supply the missing
thread/process lifetime, and these records remain Unknown. Their timing alone
does not prove deferred writeback or an actor.

Revision 3 retains each identity-deficient event's ID, operation, resource and
original QPC as Unknown. It blocks a truth row whenever such a record has the same
operation and resource within the exact independently measured QPC interval,
including both endpoints. That row has `reason=actor_identity_unavailable` and no
positive `event_ids`, even when an otherwise valid candidate overlaps it. Records
outside those three matching dimensions cannot invalidate an unrelated row.
Blocked rows still participate in cross-row candidate ambiguity checks.
Missing original QPC, header metadata, calibration, hash integrity, loss accounting
or companion completion still invalidates the full diagnostic. No PID-only identity,
truth-derived birth time, offset or tolerance is introduced. Any unavailable row
keeps the overall result unavailable; attribution and release gates are unchanged.

The same revision was applied to both preserved input sets. The historical capacity
failure still has 21/21 unavailable rows. The new sample has 17 unique interval
candidates, two rows unavailable due to actor identity, and two with no in-interval
source (the intentionally failed delete and the NoiseOnly registry write). Its
overall result remains `unavailable`, exit 2. All 14 unknown-actor source records
remain reported. Input hashes match the original diagnostics; new reports are
`previous-run-protocol-3-final.json` and `admin-pair-clock-on-protocol-3-final.json` under the
same local artifact directory. Neither original report was overwritten. This is
a protocol-only reassessment of existing evidence, not a new administrator run,
an attribution pass, or formal 32-attempt acceptance.

The independent fixture additionally writes `clock-marker-PID.jsonl` when clock
logging is enabled. It times the initial registry open, with success/error and QPC
brackets, before waiting for the launch marker. `--marker-root` reports its relation
to trace readiness separately; the 21 original scoring rows are unchanged. Default
registry source records now retain four bounded raw-name fields (4,096 UTF-8 bytes
each), parse failures and context-resolution reasons **after existing filtering**.
These diagnostics do not resolve paths, promote Unknown, or recover the historical
1,412 filtered context records.

For the next administrator-window diagnostic, start in the repository and use a
new output directory. Clear any old companion setting before probing. After all
three providers are active, run only the existing harmless fixture:

```powershell
$oldClock=$env:CONTAIN_FIXTURE_CLOCK
$oldRawClock=$env:CONTAIN_ETW_RAW_CLOCK_PATH
try {
    Remove-Item Env:CONTAIN_ETW_RAW_CLOCK_PATH -ErrorAction SilentlyContinue
    $d=(& ./target/stabilization/candidate-bin/contain.exe doctor --json | ConvertFrom-Json).data
    if ($d.etw_file -ne 'active' -or $d.etw_process -ne 'active' -or $d.etw_registry -ne 'active') { throw 'ETW is not ready' }
    $out=Join-Path (Get-Location) ('target/stabilization/etw-raw-' + (Get-Date -Format yyyyMMdd-HHmmss))
    New-Item -ItemType Directory -Path $out | Out-Null
    $env:CONTAIN_FIXTURE_CLOCK='1'
    $env:CONTAIN_ETW_RAW_CLOCK_PATH=Join-Path $out 'raw-clock.json'
    ./scripts/demo.ps1 -Release -RequireEtw -KeepArtifacts -OutputDirectory $out
} finally {
    $env:CONTAIN_FIXTURE_CLOCK=$oldClock
    $env:CONTAIN_ETW_RAW_CLOCK_PATH=$oldRawClock
}
```

This performs no elevation, remote dispatch or third-party install. The updated
demo records source inputs before/after build, compiler/build command and log,
executable paths reported by that Cargo build (including custom target directories),
hashes before/after execution, fixture/data paths,
and failure state in `run-identity.json`. These observations are not cryptographic
loaded-image attestation. Nonempty output directories are refused. It requires
all three providers plus actual file and registry source events, retains the old
score, checks views, and writes a separate clock diagnostic. `-KeepArtifacts` is
essential to retain the DB and raw journals on failure. Neither this run nor the
read-interference experiment below replaces the 32-attempt protocol above. Do not
start those paired trials until this new diagnostic has been audited and exact
baseline/candidate inputs frozen; no acceptance thresholds have been lowered.
Capture stdout/stderr still goes to the terminal; save a terminal transcript
alongside the result directory if a complete console record is needed. Keep any
transcript outside the initially empty output directory.

## Original converted-only clock protocol, version 1 (historical)

This is an observability protocol, **not a replacement score or a release gate**.
The seven historical interval mismatches and their original strict scores remain
unchanged. The locked ferrisetw 1.2.0 controller sets `ClientContext=1` (QPC), but
its consumer sets `REAL_TIME|EVENT_RECORD` without `RAW_TIMESTAMP`. The name
`record.raw_timestamp()` therefore does not mean original QPC. By default,
[ProcessTrace converts the header to FILETIME](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew).
Converting those delivered integers back to QPC would invent missing evidence.

Production observations are additive and keep JSON schema 3 / SQLite schema 4:

- `backend.clock` identifies the locked dependency configuration, clock type,
  consumer flags, measured QPC frequency, and explicit `raw_qpc_available:false`
  and `trace_header_available:false`. The dependency does not expose OpenTrace's
  `TRACE_LOGFILE_HEADER`; configuration is not presented as an observed header.
- Four calibration phases bracket trace start request, consumer readiness, stop
  request and consumer completion. Each records QPC → precise UTC FILETIME → QPC
  in call order. Missing phases and API errors remain visible. They sample the
  host clock; they do not recover ETW's original timestamp or prove continuous
  clock stability between samples.
- Every retained source record has `raw.clock.header_timestamp` with the exact
  signed delivered header, `timestamp_domain:"filetime_100ns"`, and `raw_qpc:null`.
  Lifecycle `ExitTime` can still supply `timestamp_ticks`; its original header is
  separately retained. Existing timestamp mapping, ordering and attribution are
  unchanged. Legacy records omit this optional metadata.

Set `CONTAIN_FIXTURE_CLOCK=1` only for a newly labelled experiment. The independent
fixture calls Windows directly and shares no clock implementation with Contain.
Each syscall row gains a QPC start/end, frequency, thread ID, per-process sequence,
and a separately bracketed `GetSystemTimePreciseAsFileTime` sample before and
after. Failed syscalls remain in the journal. Serialization/journal writes occur
outside the recorded operation interval. The original `SystemTime` fields and
`score-fixture.py` remain intact; without the environment variable the old row
format is preserved and no extra Windows clock API calls are made. QPC measures intervals; precise
FILETIME observes UTC, as described by
[Microsoft's QPC documentation](https://learn.microsoft.com/en-us/windows/win32/api/profileapi/nf-profileapi-queryperformancecounter)
and [precise time API documentation](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getsystemtimepreciseasfiletime).

Use the same newly built fixture for both baseline and candidate and a new output
directory. Preserve the executable hashes, command, environment switch and raw
journals. Added timing calls have overhead, so do not pool these timings with
earlier fixtures. This example continues to use the original matrix scorer; its
acceptance result is separate from the new diagnostic:

```powershell
$previousFixtureClock = $env:CONTAIN_FIXTURE_CLOCK
try {
    $env:CONTAIN_FIXTURE_CLOCK = '1'
    python scripts/v04-matrix.py --baseline BASELINE.exe --candidate CANDIDATE.exe --fixture target/release/contain-test-installer.exe --output target/v04/clock-paired --files 100 --workers 4 --trials 3 --noise-files 100 --keep-fixtures
} finally { $env:CONTAIN_FIXTURE_CLOCK = $previousFixtureClock }
python scripts/v04-clock-protocol.py --capture target/v04/clock-paired/TRIAL/capture.json --truth target/v04/clock-paired/TRIAL/ground-truth.json --output target/v04/clock-paired/TRIAL/clock-diagnostic.json
```

Run the diagnostic identically for every baseline and candidate attempt, including
failed attempts with readable inputs. It never reads or rewrites a score, refuses
to overwrite an output, records input hashes, and preserves every truth row.
Historical journals, fallback captures, converted source headers without exact
companion links, missing raw QPC, frequency mismatch, or an operation outside the
measured trace window cannot satisfy the original-QPC check. A missing actor birth
blocks matching operation/resource intervals as described above. Earlier baseline
and candidate samples without original QPC remain `unavailable`, exit 2; the newer
companion evidence does not repair those historical inputs.

Future raw timestamp capture must first retain actual trace-header metadata and
establish same-machine/same-boot provenance independently. Merely setting JSON
flags cannot establish that provenance; the diagnostic reports it as unverified.
Its raw-QPC branch uses exact integer intervals with zero added tolerance and
marks overlapping candidates ambiguous. `diagnostic_ready` means data can be
inspected, not that attribution passed. `release_gate` is always null. Calibration
offset envelopes use exact rational arithmetic and only test compatibility at
sampled points; wall-clock steps or disjoint envelopes are never repaired by a
fitted offset. Provider emission outside syscall bounds remains a separate
unresolved observation, even when PID, birth, path and operation agree.

Pending validation: capture original ETW QPC without changing production evidence
semantics; retain actual OpenTrace header values; run the same new fixture on both
arms in an already authorized elevated environment; verify provider emission
boundaries and independent trace loss. No vendored consumer, elevation, remote
dispatch, installer download or historical rescoring is performed by this protocol.

## Vendor workloads to validate in a disposable guest

Only official project/documentation sources were consulted (2026-10-22). These
are candidate technologies, not downloaded or tested packages. Freeze an exact
version, URL, SHA-256, signature result and licence before each experiment.

| Technology | Candidate and official evidence | Status |
|---|---|---|
| MSI | [7-Zip download page](https://www.7-zip.org/download.html) lists MSI packages; use the x64 MSI specifically | Not run |
| NSIS | VLC Windows installer; [VideoLAN's installer source change](https://mailman.videolan.org/pipermail/vlc-commits/2026-January/072500.html) identifies NSIS, and the [official Windows download page](https://images.videolan.org/vlc/download-windows.html) lists the installer | Not run |
| Inno Setup | [WinSCP installation documentation](https://winscp.net/eng/docs/installation) identifies Inno Setup and its command-line options | Not run |
| Self-extracting | [Portable Git extraction documentation](https://gitforwindows.org/zip-archives-extracting-the-released-archives.html) identifies the released 7z self-extractor and unattended extraction options | Not run |

Do not infer installer technology from `.exe` alone. Select the specific published
artifact and retain the corresponding source/build metadata. The Portable Git
case measures extraction/deployment and must not be described as an MSI-equivalent
system installation.

Before running any vendor binary, verify a disposable Windows guest with no user
data or shared writable host drives, an isolated test network policy, and a clean
checkpoint whose restoration was tested. Record guest/image/checkpoint identity,
Windows build, architecture, filesystem, token, compiler, binary hashes, disk free
space and ETW probe results. Export evidence before a human restores the guest;
these scripts do not restore snapshots or run uninstallers. Each vendor and trial
starts from the same clean state, including a no-installer negative control.

Use [Microsoft Process Monitor](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon)
as a separate reference collector: its native trace preserves filesystem,
registry and process details and process-tree relationships. Prepare it inside
the disposable guest; starting its driver on this host is outside this work.
Capture reference logs before the installer, retain the complete native log, and
verify reference capture loss/readability independently. Preserve a separate
before/after file hash manifest, registry export, native installer log and process
identity record. Collect CPU, memory and logical/physical disk measurements with
their respective process/system boundaries declared.

An operator reviews reference evidence without viewing Contain attribution first.
Build a versioned oracle ledger with operation, resource/endpoints, result, exact
process instance, reference record IDs, clock mapping/uncertainty, target/other
classification and adjudication rationale. Hash and freeze that ledger before
joining Contain predictions. Match only predeclared compatible operation classes
and time intervals; do not add timing tolerance after inspecting misses. Keep
duplicate source events separate from unique reference operations.

Mark the following `unverifiable` in a separate ledger, excluded from accuracy
denominators but included in reported oracle-coverage counts: unavailable process
birth, ambiguous clock mapping, collector loss, overlapping indistinguishable
operations, incomplete resource paths and unproven broker/service ownership.
Neither a changed file nor an MSI action log alone proves its writer. Windows
Installer service activity may not descend from the launched `msiexec`; Unknown
is expected without an independent causal link. Do not relabel it as target merely
because it occurs during installation.

Contain currently snapshots one HKCU Software key and watches explicitly selected
existing roots. MSI/HKLM/system-wide changes beyond those scopes must be labelled
out of scope before scoring; they are not silently removed after a miss. Report
scoped precision/recall, Unknown, false attribution, reference coverage, excluded
counts, event-loss counters, resource use and capture wall per trial. General
accuracy is unavailable until this independent ledger and real captures exist.

## Regression commands

```powershell
python scripts/test-v04-matrix.py
python scripts/test-v04-public.py
python scripts/test-v04-verify-views.py
python scripts/test-v04-metrics.py
python scripts/test-v04-summarize-matrix.py
python scripts/test-v04-clock-protocol.py
```

These tests cover workload denominators and unchanged thresholds, missing loss
counters, fallback versus ETW acceptance, failed-attempt retention, committed-WAL
seed copying, scoped publication, view conflicts/missing ancestry and null metric
semantics. They do not replace live Windows ETW, real installer or power-failure
validation. Do not bump the release version while those required gates are unmet.


## Read interference controls

The retained final-read handles in `fd25e8d` changed the observed workload:
18 of 24 independent worker operations failed with Win32 error **32** while the
same operations succeeded without a reader and with the `a072072` shared reader.
The failures were write-open, overwrite, truncate-on-open, file rename,
replace-existing and parent-directory rename, each in all three trials. Fifteen
failures also changed final bytes or namespace relative to their controls;
write-open does not itself change bytes. Read-open and an already-open writer
succeeded. All 24 separately initialized controls after releasing the candidate
reader succeeded. This is a production interference defect, even though the
candidate already kept final-state attribution Unknown.

The repair uses shared reads for both production baseline and final snapshots.
Hash and physical identity still come from the same open file where available;
content stability, path stability, writer exclusion and retention through source
stop are explicitly false, and no file or ancestor-directory handles survive the
read. Mutation coverage remains incomplete, so this change promotes no final
state. The old exclusive-handle mechanism remains only in controlled unit tests.

| Direct API arm | Before: operation success | After: operation success | Before / after matching final states | After-release controls, before / after |
|---|---:|---:|---:|---:|
| No reader | 24/24 | 24/24 | 24/24 / 24/24 | 24/24 / 24/24 |
| Baseline `a072072` shared read | 24/24 | 24/24 | 24/24 / 24/24 | 24/24 / 24/24 |
| Candidate final-read API | 6/24 | 24/24 | 9/24 / 24/24 | 24/24 / 24/24 |

These are local, non-elevated Windows **snapshot API controls**, with no ETW
session or full `contain install`. Each phase recorded 144 operations including
all controls; the original 18 failures remain in the 288-operation record. This
is not a performance benchmark or an installation-accuracy result.

The identical `crates/contain-core/examples/read_interference.rs` harness was
built against baseline `a072072ad805880290fd1f47f67fba6828d2c24e`, candidate
`fd25e8d7b614f73659760b807bf85dc5cc43325d`, and the latter archive with **only**
this report's `filesystem.rs` shared-read change. Each operation runs in an
independent worker process. A pipe barrier orders worker-ready, read-API-return,
exactly one operation, worker-exit and reader release. An existing writer is
opened before worker-ready in its dedicated case. No sleeps select successful
overlap. Arm order rotates across three trials. The historical `leased` harness
mode selects `observed_snapshot`; after the repair that API retains zero handles.

The barrier deliberately tests the finite post-read, pre-release interval that
production previously extended until trace stop/drain. It does not measure the
probability of natural overlap, concurrent access during hashing, or every
possible installer operation. A shared `File::open` can still affect requests for
exclusive access during its brief read, as the baseline did. After-release
controls use a fresh equivalent tree, so a successful rename/replacement is not
retried against a different namespace. Every operation's numeric error, exact
final bytes and relative namespace are retained independently of Contain's
attribution.

[The numeric archive](examples/read-interference.json) records source, harness,
runner, binary and raw-result SHA-256 hashes, system/compiler, every arm/case
count, all 18 failed operations and their exact final-state variants. The local
raw records are `target/interference/{before,after}/results.json`, with per-trial
stdout/stderr and untouched fixture trees. These local records are ignored by
Git; the compact archive excludes personal paths. Isolated core regression was
125 passed / 3 ignored, including all 9 filesystem tests; the harness and runner
had 2 passing tests each. The new production regression first failed with error
32 and then passed. Logs are `target/interference/production-isolated-{red,green}.log`,
`isolated-core-final.log`, `harness-tests.log` and `runner-tests.log`. The earlier
`production-interference-red.log` contains a concurrent integration compilation
failure and is not the fail-first evidence.

To reproduce, use an ordinary Windows terminal and a new output directory. The
following builds isolated debug binaries; it does not overwrite `target/release`.
Verify the harness and overlaid `filesystem.rs` SHA-256 against the numeric
archive before interpreting results. Later code changes require a new experiment
identity and cannot be substituted for this recorded patch.

```powershell
$cargoExe = "$env:USERPROFILE\.cargo\bin\cargo.exe"
$experimentRoot = Join-Path (Get-Location) 'target\interference-reproduce'
if (Test-Path -LiteralPath $experimentRoot) { throw 'Choose a new experiment directory' }
New-Item -ItemType Directory -Path $experimentRoot | Out-Null
git archive a072072ad805880290fd1f47f67fba6828d2c24e --output "$experimentRoot\baseline.tar"
git archive fd25e8d7b614f73659760b807bf85dc5cc43325d --output "$experimentRoot\candidate.tar"
git archive 3e4d9dce40121035d61687ce26f4875182740279 --output "$experimentRoot\measured-inputs.tar" crates/contain-core/examples/read_interference.rs crates/contain-core/src/filesystem.rs
New-Item -ItemType Directory -Path "$experimentRoot\measured-inputs" | Out-Null
tar -xf "$experimentRoot\measured-inputs.tar" -C "$experimentRoot\measured-inputs"
foreach ($name in @('baseline', 'before', 'after')) {
    $sourceDir = Join-Path $experimentRoot $name
    New-Item -ItemType Directory -Path $sourceDir | Out-Null
    $archive = if ($name -eq 'baseline') { 'baseline.tar' } else { 'candidate.tar' }
    tar -xf (Join-Path $experimentRoot $archive) -C $sourceDir
    New-Item -ItemType Directory -Path "$sourceDir\crates\contain-core\examples" -Force | Out-Null
    Copy-Item "$experimentRoot\measured-inputs\crates\contain-core\examples\read_interference.rs" "$sourceDir\crates\contain-core\examples\read_interference.rs"
}
Copy-Item "$experimentRoot\measured-inputs\crates\contain-core\src\filesystem.rs" "$experimentRoot\after\crates\contain-core\src\filesystem.rs"
Get-FileHash "$experimentRoot\measured-inputs\crates\contain-core\examples\read_interference.rs", "$experimentRoot\after\crates\contain-core\src\filesystem.rs" -Algorithm SHA256
& $cargoExe build --locked --manifest-path "$experimentRoot\baseline\Cargo.toml" -p contain-core --example read_interference --target-dir "$experimentRoot\build"
if ($LASTEXITCODE -ne 0) { throw 'Baseline build failed' }
Copy-Item "$experimentRoot\build\debug\examples\read_interference.exe" "$experimentRoot\baseline.exe"
foreach ($name in @('before', 'after')) {
    & $cargoExe rustc --locked --manifest-path "$experimentRoot\$name\Cargo.toml" -p contain-core --example read_interference --target-dir "$experimentRoot\build" -- --cfg contain_candidate_observer
    if ($LASTEXITCODE -ne 0) { throw "Candidate build failed: $name" }
    Copy-Item "$experimentRoot\build\debug\examples\read_interference.exe" "$experimentRoot\$name.exe"
}
python scripts/read-interference.py --baseline "$experimentRoot\baseline.exe" --candidate "$experimentRoot\before.exe" --baseline-revision a072072ad805880290fd1f47f67fba6828d2c24e --candidate-revision fd25e8d7b614f73659760b807bf85dc5cc43325d --trials 3 --output "$experimentRoot\results-before"
python scripts/read-interference.py --baseline "$experimentRoot\baseline.exe" --candidate "$experimentRoot\after.exe" --baseline-revision a072072ad805880290fd1f47f67fba6828d2c24e --candidate-revision fd25e8d7b614f73659760b807bf85dc5cc43325d-plus-filesystem-shared-read-fix --trials 3 --output "$experimentRoot\results-after"
```

The next validation must run the full CLI with ETW in an isolated, recoverable
administrator Windows guest, retaining no-monitor/baseline/candidate controls
and an independent worker oracle. Record actual read/operation overlap,
operation errors and final bytes, event loss, complete drain and clock evidence.
Retain failed operations and unchanged scoring rules. Until then this experiment
establishes the retained-handle defect and its narrow repair only; live ETW
interference, universal noninterference and vendor-installer validation remain
unvalidated.
## Bounded diagnostic result contract

`contain-clock-observability-5` adds `contain-clock-result-1` without changing
timestamp comparisons or the formal `requirements_unmet` gate. The old default
CLI exit remains tied to formal readiness. Callers doing the bounded read-only
fixture may explicitly select `--exit-contract diagnostic-v1` and must read four
separate results: execution, supplied-view clock evidence, product checks, and
formal acceptance. This tool leaves raw/final joining and product checks
`not_evaluated`; their actual callers must supply those results. Diagnostic exit
zero means execution completed and the supplied-view clock inputs are complete,
not that the product or provider contract passed. Missing clocks/provenance,
Unknown competing records, invalid inputs and empty source evidence still fail.

Input or analysis failures now retain an explicit failed result (without private
error text) so independent product checks can continue. Later optional-input
failures preserve already measured clock facts. This correction is separately
versioned because the prior formal-only exit made `demo.ps1` stop before product
assertions even when the diagnostic itself completed. The historical six time
failures remain D; this change does not adjudicate or rescore them. Focused
regressions first failed, then passed with the unchanged rejection tests.
