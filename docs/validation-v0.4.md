# v0.4 development validation

## Current stabilization status (2026-10-25)

The starting local and verified remote development revision was `ef52caa`, which
already included the per-operation audit and isolated matrix sidecars below.
The reviewed runtime is `cada888122dba836ea6bc22b7ff09e5eeb607ec9`; candidate runtime
and protocol together are frozen at `545ed3f91740786cf0d9df7c0efce4fe7e0e0e4a`.
Inherited, uncommitted identity/provenance work was inventoried and reviewed before
continuing it. The original workspace remains at `baf8db3` with its UI/preview
files untouched. Version remains **0.3.1**. The last real runtime is still
`86a6b59`; no new elevated capture has been executed in this development session.

The retained **21-row table below remains the evidence checklist**, including the
failed call. The 17 unique interval candidates, two missing actor intervals,
NoiseOnly's missing absolute registry context, and failed deletion observability
are different requirements. Successful-call denominators remain 17 target and
20 target-plus-noise. Neither historical Unknown nor the 1,000/10,000-file failures
are repaired by the new code or by a protocol-only reread.

### Checks on the exact candidate

All five existing quality modes were rerun in a clean detached `545ed3f` worktree,
with the source still clean at the same revision afterward. These are local
quality results, not administrator capture results.

| Check | Result and limits |
| --- | --- |
| `scripts/quality.ps1 -Mode Check` | 182 Rust and 142 Python tests passed; formatting, Clippy, PowerShell checks and one fuzz-seed aggregate passed; no retries |
| Omitted cases | Four explicit ignored experiments: three legacy storage benchmarks and the unchanged large spool experiment. Doctests contain zero tests |
| `-Mode Coverage` | 7,742/11,212 lines (69.05%), 636/869 functions (73.19%); original floors unchanged; includes inline tests |
| Affected coverage | Lifetime 96.21% lines, clock provenance 85.45%, native wrappers 51.47%, ETW wrapper 46.82%. Live provider startup/stop, several OS-query failures and enumeration-budget branches remain unexercised |
| `-Mode Dependencies` | Both committed lockfile graphs passed; shared-version drift zero; existing duplicate-version and license-exception notices remain in full logs |
| `-Mode Fuzz -FuzzSeconds 30` | Three existing targets completed 91,661 / 163,131 / 48,649 runs, each 31 elapsed seconds; no crash. The lifetime oracle covers process intervals; new thread-order behavior is covered by deterministic permutations, not claimed as fuzz coverage |

The failing startup-default, missing-thread and reordered-stop regressions are
retained alongside their successful reruns. The new six-order test failed before
the stop-retention fix because an event after the new thread's stop still resolved
an actor. Protocol malformed-object regressions similarly retained their failure
before the guarded reader fix. No assertions, workload sizes or thresholds changed.
The first inherited-tree Check stopped on formatting in a storage regression;
its failure log is retained, and formatting changed no test assertion.
Exact logs, source identity, test counts and hashes are in
`target/stabilization/evidence-load-20261025/exact-545ed3f-quality-summary.json`.

The existing release benchmark was rerun for clean `e72d88c` and `545ed3f` on
the same host/toolchain: two warmups and seven samples of 20,000 work items per
named case. All samples and p95 values remain in the private JSONs. The mixed
changes below are not evidence of improved installation or capture throughput.
These cases do not measure startup handle queries, retained thread-stop occupancy,
TDH, queues, SQLite or sidecar I/O.

| Synthetic median ns/work item | Baseline e72d88c | Candidate 545ed3f |
| --- | ---: | ---: |
| Exact-tick JSON parsing | 1,291.455 | 1,321.570 |
| Process lifetime ingest/resolve, batches of 512 | 150.375 | 140.360 |
| Completion correlation, batches of 512 | 240.810 | 246.555 |
| Registry context open/get/close | 280.700 | 285.665 |

### Reviewed runtime changes, not yet validated with live ETW

Startup identity sampling now runs with an active lifecycle provider in the
default configuration, independently of the optional registry experiment. It
queries existing process/thread handles after trace readiness and before launching
the installer. Simultaneously held handles, creation times and nonblocking exit
checks bind a preexisting thread to an exact process instance. Eligibility requires
the thread to predate the trace-start boundary; validity starts at that individual
query's completion. Missing, short-lived, inaccessible or raced identities remain
unresolved. Later starts/stops and PID/TID reuse can revoke the link. Historical
stop-only records are never backfilled.

The sampling budget remains 4,096 processes, at most 32,768 thread queries and
two seconds of work checked between queries; individual Windows calls are
synchronous, so this is not an OS-call timeout. Queues drain between queries.
Failures, budget exhaustion and an unknown unenumerated remainder are explicit.
A prior read-only local probe took 318 ms for 492 process and 8,714 thread queries,
resolving 6,006 threads with 2,708 access/invalid-handle failures. This is startup
cost evidence, not a live ETW throughput measurement or a guaranteed cost.

A new regression exposed a separate ordering bug: a reused thread's stop could
be discarded while two candidate intervals were temporarily open. Retained stop
facts now conservatively bound every earlier matching TID/owner interval and
remain available to delayed starts. Six delivery permutations verify the fix;
stop-only input cannot manufacture an actor. The existing 131,072-stop ceiling
is unchanged, but retaining matched stops increases occupancy; exhaustion remains
visible and invalidates lifecycle completeness. Reopen/full/page/explain and
late-loss regressions retain the additive identity evidence without promoting
final-state ownership.

Collector boundaries and independent fixture processes now sample local host-name
hash and native boot-environment identity separately. This is matching local
native observation, not physical-host or VM-clone attestation. Unsupported status,
layout, zero identity or inconsistent observations remain unavailable. The native
boot information class has no stable public Win32 contract; the implementation
accepts only the observed 32-byte layout and preserves errors. A measured compact
fixture row adds 514 bytes; sampling occurs once per process, outside operation
intervals. Both exploratory arms must use the same new fixture binary so this
extra journal traffic is common input. Old records remain readable and no database
migration or CLI contract version changes.

### Timing evidence and remaining formal prerequisites

The local provider-manifest export and retained raw evidence distinguish file
requests from OperationEnd status. In the small capture, 17 verified-actor sources
are numerically inside their independent call intervals; all 13 associated file
requests have matching IRP completions, request-before-completion order, and
in-interval completion records. Other events and the failed Open remain retained.
At the recorded 10-MHz QPC frequency, the smallest source-to-start/end margins
are 1,291/8 ticks; completion margins are 1,440/237 ticks, and the smallest
request-to-completion interval is 32 ticks. These positives do not need a widened
one-tick boundary. The private arithmetic recheck preserves original input hashes.
This validates those sample relations, not a general emission contract for every
provider descriptor or API path. [FileIo OperationEnd documentation](https://learn.microsoft.com/en-us/windows/win32/etw/fileio-opend)
describes the request/IRP/status distinction; the current manifest-based provider
still needs a scoped timing validation by Windows build, GUID/event/version and
synchronous operation type. [QPC documentation](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)
also identifies cross-thread ordering ambiguity at one tick. No matching window
is widened to conceal it.

Protocol 4 checks independent provenance and keeps numeric interval candidates
separate from validation readiness. Its provider-emission prerequisite remains
explicitly unmet: another administrator run alone cannot make formal readiness
pass. Malformed optional clock/provenance objects now produce unavailable results
instead of aborting the audit. The protocol is committed separately from runtime
changes; old protocol outputs and raw inputs are retained.

Exploration is fixed at **eight planned attempts**: 1,000 and 10,000 files, four
workers, 100 noise files, fresh database, baseline/candidate with raw diagnostics
on/off separately. It is not the formal 32-attempt protocol. Capture parent
CPU/memory/logical I/O, DB/WAL and distinct primary/companion counters are retained;
physical-disk and total-provider loss-rate denominators remain unavailable.
Clock/identity-dependent validated operation metrics stay unavailable when their
prerequisites fail; legacy scores remain separate. A timeout with uncertain worker
termination stops further execution and retains remaining planned rows as not
executed, instead of contaminating the next arm. No automatic cleanup occurs.

Both clean revisions were rebuilt with the same release/locked command, recorded
compiler versions, environment and Cargo configuration. Startup shims and actual
`rustup which` toolchain executables are recorded separately, with unchanged
before/after hashes; these are file observations, not loaded-image or complete
SDK/linker attestation. The earlier, superseded build records remain local and
are not substituted into any historical run. The active frozen inputs are:

| Input | SHA-256 |
| --- | --- |
| Baseline e72d88c executable | `1f8a874cc14030d0afe0051f9acf462b961c309fb1e4370bf6ec139356b90ce2` |
| Candidate 545ed3f executable | `15c13e21747be8a6bc384aa6745de880f6e2f3c39205c7c6e0ab3b5ebf9fe296` |
| Common candidate fixture | `25200a95051e046f5a776b330d69a0365aabe71a14585ee24d6337ae9716ba0a` |
| Private fixed plan | `516e8a74bebfb3ecec94d54ecf7a38dbc3ceaef625ed7c7ff309ae5c2b1e4d39` |

The fixed entry's validate-only run passed with **no capture launched**. Its nine
private harness regressions do not count as ETW samples. The developer token is
still non-administrator; actual execution requires the existing administrator
window, using `target/stabilization/evidence-load-20261025/run-exploratory-admin.cmd`.
The expected new output is `exploratory-545ed3f-verified-eight` in that directory.
All eight live attempts and the formal 32 attempts remain **unexecuted** at this
report update. There is therefore no measured new large-load bottleneck or capture
speedup to report yet.

Formal acceptance still requires audited contemporary identity/provenance evidence,
a scoped provider-timing protocol with explicit unsupported/failed-call behavior,
an acceptable small diagnostic and immutable paired inputs. No formal inputs are
frozen, no new scale pass is claimed, and final file/registry ownership remains
Unknown. Private evidence is under `target/stabilization/evidence-load-20261025/`;
earlier failures remain under `evidence-load-20261024/` and `raw-clock-stabilization/`.

## Current evidence gaps and exploratory scope (2026-10-24)

Starting source and verified remote development HEAD: `e72d88c410f8ec69b8e17f7ccc193a6cb9c972d4`.
The most recent live runtime remains clean `86a6b59`, version **0.3.1**.
The development token is not elevated. Existing administrator captures are valid
real ETW observations; the new work below is not another capture or formal acceptance.
The original workspace and UI/preview files are preserved. A fresh clean build
of `e72d88c` and its complete input/executable identities are retained locally.
Its freshly executed Check passed 161 Rust tests (4 explicit skips), 131 Python
tests, formatting, Clippy, PowerShell regressions and the deterministic fuzz seed.
Doctests contain zero tests. These checks do not establish live capture coverage.

### Per-operation audit of the retained clock-on diagnostic

The table retains all 21 independent rows from
`admin-pair-20261023-231230-468d9d12/clock-on`. Fixture-relative names replace
private paths. **B** means independently established same-machine/same-boot
provenance is absent; **E** means provider emission timing has not been validated
as a general syscall/completion contract. Both are global prerequisites, not
evidence that the recorded QPC numbers themselves are corrupt. A unique interval
candidate is not proof of final-state ownership or successful operation completion.

| Row / role | Operation and expected result | Source observed | Resource resolved | Actor instance | Exact raw-clock link | Remaining requirement |
| --- | --- | --- | --- | --- | --- | --- |
| 1 short | write short.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 2 grandchild | write user-notes.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 3 grandchild | write transient.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 4 grandchild | delete transient.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 5 grandchild | set GrandchildObserved; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 6 parent | write locked.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 7 parent | delete locked.txt; failure, error 32 | Open request; no qualified delete request/completion | Open path resolved | Open actor verified | Open links within call; no qualified delete match | Failed deletion request/outcome not fully observed; B, E |
| 8 parent | write app.bin; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 9 parent | write settings.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 10 parent | write shared.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 11 parent | rename rename-me.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 12 parent | rename intermediate.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 13 parent | delete delete-me.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 14 parent | set Installed; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 15 detached | write detached.txt; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 16 unrelated | write unrelated.txt; success | Request/completion retained | Yes | Missing event-time process/thread interval | Yes; interval evidence retained | Actor identity; B, E |
| 17 unrelated | write shared.txt; success | Requests/completions retained | Yes | Missing event-time process/thread interval | Yes; interval evidence retained | Actor identity; B, E |
| 18 unrelated | set NoiseOnly; success | Successful set-value persisted | No absolute key/object context | Process birth verified | Yes; within call, not a full-resource match | Resource identity; B, E |
| 19 child | write index.bin; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 20 child | set Installed; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |
| 21 child | set ChildObserved; success | Yes | Yes | Verified | Yes; unique interval candidate | B, E |

The extended audit links all **460 persisted primary records** to the raw archive
by exact EventKey and complete descriptor, including the NoiseOnly set-value
record. This is broader than the earlier 35 eligible final-view source records;
the denominators must not be combined. NoiseOnly has status 0 and a ValueName,
but `nonabsolute_key_name_and_no_object_context` prevents an absolute-key match.
It is retained in raw storage and excluded from the resolved final scope. Actor
initialization cannot recover that missing resource identity. The earlier 1,427
global registry path gaps retain their historical scoped analysis below; they
are not 1,427 target misses or a count from this newer capture.

The failed delete has an in-call open request with unresolved success, but no
qualified deletion evidence. No deletion was promoted, and locked.txt remains
in the final observation with state ownership Unknown. **No false success** is
verified here; **complete failed-request observability** is not. The failed call
stays outside the 20-success denominator. Legacy results remain 17/17 successful
target calls observed/correct, 19/20 supported successful calls observed, 17/20
valid attribution, 3/20 Unknown and 0/20 wrong attribution. No sample or threshold
changed. Protocol 3 remains 17 candidates, 2 actor-unavailable rows and 2 without
a qualified in-interval source, overall unavailable. The older capacity-failed
sample remains unavailable; no historical identity is reconstructed.

Read-only audit inputs, hashes, exact event IDs, every row and original outcomes
are retained in `target/stabilization/evidence-load-20261024/per-operation-gaps-private.json`.
The same directory holds the fresh baseline build, Check and seven-sample synthetic
benchmark records. Full paths, raw clocks, host data and databases remain private.

Next scope: bounded startup identity handoff and independently measured clock
provenance, followed by **exploratory**, separately labelled 1,000/10,000-file
baseline/candidate and clock-on/off attempts. These do not require declaring the
small diagnostic passed and do not count toward the formal 32 attempts. Formal
inputs are not frozen, historical large-scale failures remain, and no release or
real deletion capability is authorized by this work.

The existing matrix entry now clears inherited raw paths, assigns each enabled
attempt its own sidecar, passes that exact file and independent control markers
to the clock diagnostic, and archives both journals on failure. Missing process
measurements remain null; a diagnostic exception cannot leave acceptance true.
All 20 matrix regressions passed, including four new failure-path cases. This
repairs the test entry only; no new live load result is claimed.

## Integrated candidate and bounded companion (2026-10-23)

The clean runtime candidate tested live is `86a6b596aea28f30ea3afca4c4a65465172431b6`,
version **0.3.1**, from the `fix/raw-clock-stabilization` development line and
captured from a clean detached validation worktree. This is development progress,
not a completed stabilization milestone or release. The public base `a0fb81f`
and local quality commit `baf8db3` share ancestor `a072072`; neither was assumed
to contain the other. Necessary quality deltas were reviewed file by file and
reapplied in `32494f6`, without merging unpublished ancestry. The original
workspace and its untracked UI/preview files remain untouched.

`d422027` separately corrects the real-time StartTime protocol assumption.
`69c9cb0` closes a native callback/CloseTrace ownership race: opaque registration
tokens and reference acquisition under the removal lock prevent freed-context
access and allocation-address reuse. `86a6b59` replaces the lifetime-sized raw
vector with a finite asynchronous chunk writer, keeping all three providers and
their unrelated context. The [protocol and bounds](v04-validation.md#raw-clock-diagnostic-entry)
describe the fixed 8-MiB row pool, 1-MiB writer stack, unchanged 128-MiB disk
budget, 64-byte wire format and explicit queue/quota/write/drain failures.
No callback performs synchronous file writes; metadata waits also release the
callback lock. Neither product commands nor attribution rules were broadened.

### Fresh checks of the integrated source

The following ran against clean `86a6b59`, with exact source identity checked
before and after. Results from the original quality branch are not reused.

| Check | Actual result and scope |
| --- | --- |
| Full quality Check | 161 Rust tests passed; 4 explicitly skipped; 125 Python tests in 14 suites passed; PowerShell scorer/provenance, formatting, Clippy and deterministic fuzz seeds passed |
| Skipped tests | Three existing storage performance experiments; one large spool experiment executed separately below. Doctests contain zero tests |
| Native dependency regressions | 17 passed, including dispatch/close, address reuse, exhausted token space and failed OpenTrace; tested at `69c9cb0`, identical vendor source at `86a6b59` |
| Dependencies | Both locked graphs passed, shared-version drift 0; existing duplicate-version warnings and four vendor compiler warnings retained |
| Coverage | 7,116/10,552 lines (67.44%), 597/831 functions (71.84%); original 66.5%/70% floors unchanged |
| Affected coverage | companion 58.94% lines; spool 81.70%. Includes inline test code; actual Companion open/ready/finish/Drop and several exceptional OS branches remain unexercised by this suite |
| Bounded guided fuzz | Existing three targets completed 30-second requested campaigns: 90,052 / 146,853 / 50,959 runs; no crash found. Normalized model inputs, not raw ETW |
| Existing policy benchmark | Four cases, seven samples each, 20,000 inputs/sample. Median ns/item: JSON 1,319.640; lifetime 142.700; completion 263.920; registry context 290.755. No before/after speedup or live throughput claim |

An explicit **paced synthetic** Rust writer test persisted and read back all
896,972 rows in 57,406,208 bytes, with 219 writes, an 8-MiB row allocation and no
discard or incomplete drain. Every sequence and the full SHA-256 were checked.
The Python reader independently scanned the actual Rust payload and all keys,
including unmatched context; no duplicate was found. One synthetic primary key
matched its full descriptor. Producer pacing is deliberate in this storage test;
it is not evidence that callbacks can sustain that rate. Saturation, partial
writes, quota exhaustion, metadata failures and timeouts have separate regressions.

Private logs and retained payloads are under
`target/stabilization/raw-clock-stabilization/`; clean-worktree Check, Dependencies
and Coverage artifacts are under that worktree's `target/quality/`, including
`candidate-86a6-check-results.json`. Reproduce with `scripts/quality.ps1 -Mode`
`Check`, `Dependencies`, `Coverage`, `Fuzz -FuzzSeconds 30`, and `Bench`. Run the
large writer case explicitly with
`cargo test -p contain-core paced_historical_callback_volume_fits_disk_quota_and_drains_every_record --release --locked -- --ignored --nocapture`.
`CONTAIN_CLOCK_SPOOL_TEST_OUTPUT` optionally retains a new absolute output path.

### New administrator pair: real collection, overall diagnostic still failed

The user executed `admin-pair-20261023-231230-468d9d12` in the existing elevated
window. Both arms recorded clean `86a6b59`, matching build-input inventories and
unchanged pre/post executable hashes. The development session itself remains
non-elevated. These are real file/registry/process ETW captures, not fallback.
The contemporaneously recorded SHA-256 values are:
`contain.exe` `44f5efd63bff1c0e4b05fff3f9cb44c1faae86d5e690616c506125d3b8f55f5e`;
fixture `3aff34193d338b10282eda9693a667e9e0bd38f4f051203218176256b4a2b1eb`.
Current-file hashes corroborate those records; they do not replace them or attest
to the loaded image. The private identity audit verified each arm's 106 runtime
inputs and 42 scripts and preserved all original pair artifacts and journals.
Database backups were produced from private copies, passed SQLite quick_check and
retained table counts; all original file hashes remained unchanged.

| Same fixture, one attempt per arm | Clock off | Clock on |
| --- | ---: | ---: |
| Original demo result | Passed | Failed at the additional clock diagnostic |
| Primary received / persisted | 1,147 / 1,147 | 460 / 460 |
| Reported primary queue, handoff, write and ETW loss | 0 | 0 |
| Successful target observed / correctly attributed | 17/17 / 17/17 | 17/17 / 17/17 |
| Noise observed / independent successful noise | 2/3 | 2/3 |
| Audited wrong attribution / supported successful calls | 0/20 | 0/20 |
| Views and explanations checked | 363, consistent | 370, consistent |
| Capture wall seconds | 13.251779 | 15.754381 |
| Descendant quiet-drain milliseconds | 2,496 | 5,233 |
| Parent CPU seconds | 3.078125 | 3.406250 |
| Parent peak working set bytes | 22,806,528 | 27,435,008 |
| Parent sampled private-memory peak bytes | 12,279,808 | 19,066,880 |
| Parent logical read / write bytes | 14,056,748 / 13,584,378 | 7,294,252 / 15,063,004 |
| Sampled DB / WAL peaks bytes | 3,997,696 / 7,539,632 | 1,568,768 / 5,623,832 |
| Final database bytes | 3,997,696 | 2,777,088 |

For each arm, overall observation is 19/20 supported successful calls (95%);
valid attribution is 17/17 target calls, or 17/20 over target plus noise (85%).
All three successful noise calls remain unresolved/unobserved, so Unknown is
3/20 (15%). Supported raw positive precision is 17/17; this is a small fixture
domain, not general installation accuracy. One intentionally failed delete remains
separate. Event loss **rate** is unavailable: reported zero counts do not supply
a complete enabled-provider denominator. Final state ownership remains limited.

Clock-on retained **96,655/96,655** raw rows, 6,185,920 payload bytes, 24 write
calls, peak one queued chunk, no queue/quota/closed-writer drops and no missing
keys, partial records, timeout or write failure. Data write time was 2.4931 ms,
final drain 2.8754 ms; the 1,958-byte metadata commit also completed. Independent
SHA-256, full sequence and all-key duplicate checks passed. All 35 eligible
retained source events linked by original key and full descriptor, with no missing
link. Both actual OpenTrace StartTime fields were zero; revision 2 correctly
retained them as unavailable and did not invent a clock origin.

This live run is **below the old 262,144-row cap**, not a reproduction of the
896,972-row historical load. The synthetic full-volume test removes the former
inevitable lifetime-cap failure for that count, but live sustained load still
needs measurement. The two arms differ in ambient traffic, main event count and
active trace duration; their single wall/CPU/I/O differences do not isolate the
companion's overhead. The quiet-drain stage alone differs by 2.737 seconds,
exceeding the total wall-time difference. Parent counters exclude descendants/kernel buffers and are
logical I/O, not physical disk traffic. Sampling reported no errors.

The original clock report failed all 21 rows with
`source_original_qpc_or_actor_missing`, despite complete raw links. Fourteen extra
write records lacked verified actor instances: three have the unrelated fixture's
header PID but no trusted birth identity; eleven have header PID 4, mostly outside
the corresponding operation intervals. Neither observation proves their writer
or a particular kernel write mechanism. All remain Unknown. Independent examination
finds one exact verified-actor/QPC/path candidate for each of 17 successful target
calls; the global actor-presence prerequisite incorrectly hides these usable facts.
The three unrelated file records have no retained process/thread start intervals;
only later stops are available. A later registry process query cannot prove those
earlier file-thread identities. This is an evidence boundary, not a reason to
backfill actors from the oracle, header PID or stop events.

Protocol-only commit `6e3162b` introduces revision 3 with 48 focused clock tests
passing. It retains all 14 unknown-actor records, localizes their impact to matching
operation/resource/QPC intervals, preserves overlapping and cross-row ambiguity,
and leaves original-QPC/metadata/calibration failures global. Applying the same
revision to both preserved samples gives: historical sample 21 unavailable; new
clock-on 17 unique interval candidates, two unavailable actor rows and two with
no in-interval source (failed delete and NoiseOnly). **Both still exit 2**. No
original report, input hash, legacy score or threshold changed. Production source
is identical to `86a6b59`; this is reassessment of the same run, not another run.
See [the protocol correction](v04-validation.md#unknown-actor-clock-protocol-correction).

All failed attempts remain: two shell invocations failed before any capture, and
`admin-pair-20261023-230813-87d8adaa` contains two configuration failures before
ETW. A local launcher used a null environment assignment that became an empty
checkpoint setting under the installed PowerShell. Explicit removal fixed the
launcher; the original script and both failures were retained. Production code
and acceptance thresholds were not changed to accommodate those failures.

The new clock diagnostic has **not passed**. Formal acceptance inputs are not
frozen and the 32-attempt protocol has not run. Historical 1,000/10,000-file
failures remain failures. No third-party installer, deletion, elevated agent
process, remote test, default-branch merge or release was added.

The next decision needs an audited evidence policy for pre-existing thread/process
coverage, plus verified same-boot provenance and provider emission semantics; it
cannot be replaced by a larger queue or PID-only matching. The current evidence
does not justify entering a real deletion loop. Full raw files and databases remain
private; only the reviewed scoped aggregates above are published.

## Raw-clock administrator diagnostic audited (2026-10-23)

`etw-raw-20261023-212829-782` is a second, actually executed administrator-window
ETW run. **Its raw-clock diagnostic failed**; this is not a permission-blocked or
snapshot-only attempt. The original score and clock report reproduce exactly
under the recorded scripts. No score, timestamp, tolerance or gate was changed.

The execution identity records source HEAD `239e841` **plus uncommitted runtime
changes**. It is not an execution of that clean commit or the retained development
checkpoint. Recorded input inventories agree before/after build, executable hashes
agree before/after execution, and Cargo artifact paths match. Current bytes are
corroboration, not a replacement for those contemporaneous observations. Full
identity records, database, journals and raw sidecar remain local.

| Scope and denominator | Result |
| --- | --- |
| Primary receive / persist | 3,819 / 3,819; all 15 audited accounting, drain and database checks pass |
| Reported primary loss | ETW event/buffer loss, queue overflow/drop and write failures are 0; this is not a raw-consumer loss-rate claim |
| Raw companion | 896,972 callbacks; 262,144 stored; **634,828 refused at the fixed capacity** |
| Other raw counters | Missing key, invalid timestamp and duplicate key counters 0; no unbounded buffer or new threshold introduced |
| Exact original-key plus full-descriptor links | 22/24 eligible retained file/registry source events; 2 missing, 0 descriptor mismatches |
| Successful target operations, unchanged scorer | 17/17 observed and correctly attributed; audited wrong attribution 0 |
| Target calls with exact original QPC evidence | 16/17; all 16 lie within the independent recorded interval; detached operation lacks retained raw evidence |
| Full clock protocol | All 21 truth rows unavailable; exit 2; diagnostic is **failed** |
| View consistency | 382 events/explanations, 14 file states, 4 registry states agree |
| Fixture failure | Only the intended locked-file delete fails, with error 32 |
| Measured capture wall / final database | 14.8218355 seconds / 12,738,560 bytes |

Partial exact links demonstrate that original QPC can reach the diagnostic, but
do not establish complete clock coverage or acceptance. The finite companion
retains system-wide enabled-provider callbacks while the primary stream persists
scope-filtered evidence; these denominators must not be compared as loss. The
capacity refusal is a confirmed limitation of the diagnostic path. No throughput
improvement is claimed from comparing this dirty-tree sample with older runs.

Both actual OpenTrace headers contain `StartTime="0"`. That field is unavailable
in this sample and did not satisfy the original protocol's positive-start check;
the capacity failure currently short-circuits the later check. The Windows
[header contract](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-trace_logfile_header)
defines it as the session start time, but this audit does not infer an absent
timestamp or relax the check. Original QPC is distinct from the default
[ProcessTrace-converted timestamp](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew).
Initial header counters are not final raw-consumer loss statistics.

NoiseOnly now has a retained `ValueName` and an exact original-key/QPC link, but
`KeyName` is empty, base-name properties are absent and object context cannot
resolve its absolute parent key. The independent initial-open marker establishes
an open before trace startup; it does not prove the full resource for this object.
The row remains unmatched/Unknown in the unchanged path-based scorer. Of 21,420
global registry path gaps, 336 unresolved records are retained (302 without a
verified absolute base, 17 without process identity and 17 without object context).
The other 21,084 have no retained per-record identity/resource, so neither target
loss nor unrelated-background scope can be assigned to all of them.

All nine run artifacts and the database remain unchanged under SHA-256 checks.
Read-only audit code and the complete private findings are under
`target/sync-audit/raw-212829-audit.py` and `raw-212829-audit.json`; location/identity
details stay in the separate private report. No new live capture or remote test
was started for this audit. Capacity handling and unavailable header semantics
need a scoped repair and another real diagnostic before formal acceptance inputs
can be frozen. The 32-attempt protocol, historical scale failures, and version
0.3.1 remain unchanged.

Checks executed for this audit/publication stage: all 12 `scripts/test-*.py`
modules passed (99 tests), `scripts/test-demo-provenance.ps1` and
`scripts/test-score.ps1` passed, and `cargo fmt --all -- --check` passed. Runtime,
test and script blobs are unchanged by publication preparation. The Rust test,
Clippy, coverage and live ETW suites were not rerun in this documentation stage;
earlier results are not relabeled as new executions.

### Publication scope

The unpublished development sequence was reviewed at every historical blob and
prepared with host inventory withheld, historical branch labels redacted and
project-focused wording. Runtime code, tests, scripts, measured outcomes and
failed samples are unchanged by that preparation. The original local commits,
source artifacts and a private old-to-new commit map remain available; recorded
source IDs still identify the original measured inputs. Withheld JSON values are
null, not zero; generalized platform labels use `Windows`. Both carry an explicit
redaction note. Public development uses
`livio/deferred-checkpoint`; see [synchronization rules](../CONTRIBUTING.md#development-synchronization).
Previously published ancestry is not rewritten by this cleanup. Historical text
and other branches/default-branch contents therefore remain a separate boundary;
this report does not claim complete historical removal.

## Administrator-run ETW diagnostic received and audited (2026-10-23)

**A real small ETW diagnostic has now run successfully in the user's administrator
window.** This supersedes any blanket statement that current stabilization has
only snapshot evidence. It does not grant the current development process an elevated
token (checked again: false), validate large workloads, or satisfy the preregistered
32-attempt acceptance protocol. Version remains 0.3.1; no deletion was performed.

The run is `etw-small-20261023-202449`, session
`ea42cc18-f36a-4ab1-b286-1f28fb4769e5`, 12:24:55–12:25:03 UTC. Original exports,
SQLite database, six per-process independent JSONL journals and aggregate truth
are copied without modification to
`target/stabilization/archive/etw-small-20261023-202449/`.
`archive-manifest.json` records original paths, sizes and SHA-256; DB integrity is
`ok`, SQLite schema is 4. Private raw logs and system inventory are not committed.
The reproducible read-only audit is `analysis/forensic-audit-v2.py`; its v2 report
separates the empty WAL/SHM generated by post-run SQLite readers from original
run artifacts. All original-file hashes remain unchanged. The initial audit is
also retained, including its superseded sidecar provenance.

| Check and denominator | Observed result |
| --- | --- |
| ETW providers | File, registry and process all active |
| Queue / journal / SQLite rows | 509 received, 509 drained, 509 committed; sequences 1–509 contiguous |
| Reported source/application losses | ETW event/buffer loss, application overflow/drop, write failure and quota drops all 0 |
| Completion | Receiver/writer finished; both pending counts 0; 12 accounting checks pass |
| Successful target calls | 17/17 observed and correctly attributed under the unchanged FILETIME scorer |
| Successful noise calls | 2/3 observed, all 3 remain Unknown; NoiseOnly is unmatched |
| All supported successful calls | Observation 19/20; Unknown 3/20; incorrect attribution 0/20 |
| Audited positive predictions | 17/17 correct; this finite fixture precision is not general accuracy |
| Failed calls (separate denominator) | One intentional locked-file delete failed and was not scored as a successful mutation |
| Persisted CLI evidence | All 370 events/explanations, 14 file states and 4 registry states agree |
| Wall time / final DB | 15.2616765 seconds / 2,486,272 bytes |
| CPU / peak memory / physical I/O / true event-loss rate | Unavailable; not replaced by zero |

The 509 raw records are 243 file, 31 completion, 208 lifecycle and 27 registry
records. Post-processing excludes 149 lifecycle and 14 unresolved non-High registry
records and adds 6 process, 14 file-state and 4 registry-state records:
`509 - 163 + 24 = 370`. This difference is not transport loss. Raw evidence remains
in SQLite. Final-state ownership is still Unknown and `remove --dry-run` remains
read-only. The original score (`unknown=4/21`, including the failed call) is retained
unchanged; the successful-call denominator above is explicitly separate.

### Execution identity: evidence and missing facts

The capture records the fixture image as `target/release/contain-test-installer.exe`
and its process birth; the supplied console excerpt confirms the retained fixture,
database and truth paths. The run did not record a pre-launch executable hash,
source revision or build attestation. The CLI path is suggested by the demo script,
but is not an executed-image identity in this capture. These missing fields remain
null in the archive; current hashes are **post-run observations**, not replacements:

| Archived post-run file | SHA-256 |
| --- | --- |
| contain.exe | `0a7b92ffda0edb1f939c0c02970c580233f189eebf7a4b0e5853d3931a7e7c0d` |
| contain-test-installer.exe | `b5466749a84dd5034ee9aaf9db8d3f9945177bfc7e713f6e6aa5b1f9104a6540` |

They match the previously frozen 076fe76 binaries, and current runtime sources
were unchanged from that commit before this follow-up. This supports consistency,
but does not retroactively establish which bytes the administrator process loaded.
The database SHA-256 is
`a8f735a954c430769197ed15a3672edf302eb85bfe9d261ce7f2ba36a614b5bb`.

### NoiseOnly and the 1,427 registry path gaps

Raw sequence 200 (event `bf74d6d1-97ae-4979-8272-95b9a9d506b4`) has the independent
noise process's PID and birth, `set_value`, successful status, and delivered
FILETIME `134372318955732472` within the exact independent call interval
`[134372318955729656, 134372318955732505]`. Its resource is unresolved. It is removed
from the derived view by the existing non-High/unresolved rule, so the frozen
path-based scorer finds no NoiseOnly match. This is a resource-evidence gap, not
an identified queue loss or a reason to expand the time interval. Identity and
timing compatibility alone do not prove that raw event names NoiseOnly.

The legacy raw record omitted the original registry names and their property parse
outcomes. The unrelated fixture opens a key before waiting for the launch marker,
and its process predates trace startup by about 3.9 seconds. A pre-trace open is a
plausible cause of missing handle context; the open itself was not independently
timed, so that exact cause cannot be recovered from this run.

The counter covers all 55,461 registry callbacks before scope filtering. Of 27 raw
registry records, 12 resolve and 15 do not. One of those 15 belongs to a verified
short-lived target child; one is the compatible noise candidate above; the other
13 are not verified target actors (9 have other process births, 4 lack birth).
Their resource scope is still unknown. Given the inspected non-deferred decoder
and zero reported transport/decode losses, the remaining `1427 - 15 = 1412` gaps
are filtered open/close context callbacks (IDs 2/13). This last classification is
a source-based inference, not a missing executable attestation: there is no retained
per-record PID/path/payload to reconstruct them. Neither “1,427 target misses” nor
“all unrelated background events” is supported. Zero `decode_errors` also did not
count property getters whose failures became empty strings.

### Targeted follow-up: confirmed interference fixed; new clock path awaits live audit

The follow-up started from `fd25e8d7b614f73659760b807bf85dc5cc43325d` on
the development branch (historical name withheld). The untracked UI/preview work remains untouched.
The administrator-run archive above is unchanged and does not describe the new
code's execution. There is no new 32-attempt result, no release, and no version bump.

The independent process/pipe-barrier experiment reproduced a real read-induced
workload failure. Eight operations were run three times per arm in the same
post-hash/pre-release window, with a fresh after-release control for each:

| Arm, same workload | Operation success | Win32 sharing violations | Final results matching control |
| --- | --- | --- | --- |
| No monitor | 24/24 | 0 | 24/24 |
| Original baseline a072072 | 24/24 | 0 | 24/24 |
| Prior candidate fd25e8d | 6/24 | 18 (error 32) | 9/24 |
| Candidate with shared-read repair only | 24/24 | 0 | 24/24 |

Retained restrictive file/ancestor handles caused the failures. Production reads
now share read/write/delete and release handles after same-handle hashing and
identity checks. The code no longer claims a retained stability lease or pinned
namespace. Final ownership remains Unknown; earlier valid source-event evidence
is preserved. Brief reads still have ordinary Windows sharing interactions, and
this experiment does not establish natural-overlap probability or whole-CLI/ETW
noninterference. All 288 operation records, including all 18 failures and controls,
remain under `target/interference/{before,after}`. Exact source/harness/binary hashes,
error cases and commands are in [the existing protocol](v04-validation.md#read-interference-controls)
and [numeric evidence](examples/read-interference.json). This is a correctness
repair, not a capture-throughput improvement.

The raw-clock implementation leaves the primary FILETIME stream and frozen scorer
unchanged. A small ferrisetw patch exposes actual OpenTrace headers and an opt-in
same-session RAW_TIMESTAMP consumer. Both consumers must pass readiness before
launch; only unique EventKey plus complete descriptor/PID/TID pairs join. It uses
16,842,752 bytes of record/serialization backing storage and a 128-MiB sidecar
limit; this excludes total process/OS memory. Missing keys, saturation, startup,
STOP, write and partial-artifact failures remain explicit. STOP failure now closes
the companion before joining; failed OpenTrace unregisters callback state.
The initial header is not a final consumer loss sample, and BootTime is not used
as proof of same-boot provenance. Successful live EventKey/header/QPC matching is
**not yet verified** by the current session's restricted token.

Registry diagnostics retain bounded raw names and parse/context failure reasons
after existing filters. Independent initial-key-open markers are separate from
the original 21 oracle rows. These support the next NoiseOnly investigation;
they do not retrospectively identify the missing historical payloads or turn an
unresolved record into a resolved resource. The demo now retains build/source and
pre/post executable observations, refuses nonempty result directories, and checks
actual file and registry source events. It selects executable paths from this
Cargo invocation's compiler artifacts, avoiding stale default-directory binaries
when an administrator shell has custom Cargo output/target settings. Missing or
ambiguous artifacts abort launch. Existing historical identity gaps stay null.

Executed follow-up validation:

- Workspace Rust: **144 passed** (136 core, 3 noise fixture, 5 installer fixture),
  3 explicitly ignored. Zero-test CLI/doc targets do not add passes.
- All 12 `scripts/test-*.py` files invoked explicitly: **99 passed**, including 25
  clock protocol cases. The earlier discovery invocation matched zero tests and
  remains logged; it is not a successful validation run.
- Local ferrisetw unit tests: **12 passed**. Failure-first OpenTrace/context and
  interference evidence is retained; no live trace claim follows from unit tests.
- Workspace/vendor formatting, workspace Clippy `-D warnings`, and locked release
  build passed. The dependency still emits four inherited ferrisetw warnings;
  dependency lint caps mean this is not a warning-free dependency-tree claim.
- PowerShell parsing, scorer regressions and provenance-helper checks passed.
  A restricted-token CLI probe exited 1 without launching the installer or leaving
  its UUID session running. This proves refusal on failure, not successful ETW.

Raw validation logs are under `target/stabilization/raw/` (`followup-rust-final.log`,
`followup-python-reviewed.log`, `followup-clippy-final.log`, `followup-release-final.log`,
and `clock/`). The measured interference overlay has its own immutable source
hashes; do not relabel it as the integrated candidate's full workload result.

Reproduce the integrated checks from the repository (the release build alone is
not an acceptance test):

```powershell
cargo fmt --all -- --check
cargo test --workspace --locked --target-dir target/stabilization/clock-build
cargo clippy --workspace --all-targets --locked --target-dir target/stabilization/clock-build -- -D warnings
Get-ChildItem scripts/test-*.py | Sort-Object Name | ForEach-Object { python $_.FullName; if ($LASTEXITCODE) { throw $_.Name } }
./scripts/test-demo-provenance.ps1
cargo build --workspace --release --locked --target-dir target/stabilization/clock-build
```

The remaining minimal step is the documented [administrator raw-clock small
diagnostic](v04-validation.md#current-raw-clock-diagnostic-entry-still-requires-elevated-live-validation)
using the existing harmless fixture and keeping all artifacts. It must establish
actual header/key availability, independent timing and post-repair fixture behavior.
Only then can baseline/candidate inputs and the unchanged 32-attempt paired
protocol be frozen for acceptance. Historical 1,000/10,000 failures remain failures.
Final-state ownership and safe deletion are still unproven: this work does not
establish a basis for a safe removal executor.

Local commits: `6df0de7` archives the administrator evidence; `3e4d9dc` repairs
read interference; `38261b5efd808a440bf553c4eee71a88b853e2b5` implements the clock,
registry diagnostics and execution-identity repairs. A retained development build
is at `target/stabilization/followup-38261b5-bin/`, including a source archive,
pre/post-build input inventory, actual Cargo artifact paths and SHA-256 manifest.
Its `contain.exe` SHA-256 is
`3a708479ad606141f6b55b57639e6c6b0fd4b80ff621e10d0c2b4b747b97199e`.
The final artifact-selection build log and identity are
`target/stabilization/raw/followup-build-artifacts-reviewed.log` and
`followup-build-identity-reviewed.json`. This checkpoint explicitly has
`acceptance_frozen=false` and `live_raw_clock_verified=false`; it must not replace
the historical demo's missing executed-image identity. No push was performed.

The following sections retain the pre-diagnostic measurements and their original
conditions. They must not be relabelled as results of subsequent clock or file-read
changes.

## Main-flow stabilization: verified starting point (2026-10-23)

This section records the earlier stabilization entry point;
historical measurements below remain unchanged. Starting branch was
the development branch (historical name withheld), HEAD `a072072ad805880290fd1f47f67fba6828d2c24e`,
package 0.3.1. The only existing changes were untracked `docs/preview.md`,
`scripts/build-preview.py`, and `ui/`; they remain user-owned and are excluded.
There was no repository AGENTS.md. This task keeps the version and does not push,
merge, tag, elevate, change security settings, or run third-party installers.

The local Windows host used PowerShell 7.6.5, Rust/Cargo 1.98.1 and Python 3.12.7
with a non-administrator token. Detailed host inventory is retained locally.
`doctor` again reported ETW access denied (0x80070005). A recoverable isolated
Windows guest has not been established. Existing prior hosted ETW results are
evidence to audit, not a new baseline/candidate run for this task.

Before edits, `cargo fmt --all -- --check`, `cargo test --workspace --locked`,
`cargo clippy --workspace --all-targets --locked -- -D warnings`, and
`cargo build --workspace --release --locked` passed. Tests: 93 core + 2 fixture,
3 opt-in experiments ignored. The shell required prepending the already installed
`$env:USERPROFILE\.cargo\bin` to PATH; no tools or dependencies were upgraded.
Exact environment/status, logs, executable hashes and the renewed doctor result
are retained privately in `target/stabilization/raw/baseline/`. Unmodified release
executables were copied to `target/stabilization/baseline-bin/` before edits.

The final historical ETW matrix (run 39812655516, production 880b450) confirms
100-file 3/3 passes per arm, 1,000-file baseline 2/3 versus candidate 1/3, and
10,000-file 0/3 per arm. All six 10,000-file attempts overflowed the application
queue; four also lost upstream ETW events. The 1,000-file failures lost 221/241
candidate and 805 baseline application records, with zero reported upstream loss.
These failures are not being rescored or relabelled as fixed.

The same raw profiles show two receiver blockers: process polling and raw SQLite
transactions. Failed 1,000-file candidate poll maxima were 280.541/206.968 ms,
versus commit maxima 42.703/40.618 ms. Candidate 10,000-file poll totals were
5.731/4.436/4.218 s and commit totals 1.670/1.142/0.999 s. These are elapsed spans,
including scheduler delay, not isolated CPU or physical I/O. The retained public
projection strips queue-span stage labels; unioned overflow-counter intervals
cover 89.14%, 1.86%, and 4.56% of the three 1,000-file losses. They cannot be
relabeled as checkpoint-only losses. The reproducible all-18-attempt numeric
audit is `target/stabilization/consumer-stall-audit.json`, with its source hash.

Clock mismatches and state-coverage gaps are separate correctness/protocol issues.
The historical samples lack original ETW QPC, so their sub-millisecond boundary
failures cannot be repaired by fitted offsets. The old ~86x result measures
synthetic evidence readback only; it does not measure capture throughput.

## Initial stabilization implementation (076fe76; before the administrator demo)

**The main-flow stabilization milestone is not accepted yet.** Runtime candidate
`076fe7634ab4644fa0bce123ac798c4a3dea6601` is still 0.3.1. The following fixes have
local regression evidence; at this stage their real-time ETW throughput and useful
attribution coverage had not been validated. No new product command or deletion executor was
added. CLI JSON remains schema 3 and SQLite user_version remains 4.

| Area | Implemented and tested | Remaining boundary |
| --- | --- | --- |
| Clock | Exact delivered signed event header, dependency clock configuration, bracketed QPC/precise FILETIME samples; independent fixture QPC/TID/sequence/bounds; strict diagnostic with no offsets/tolerance | ferrisetw 1.2.0 still delivers converted FILETIME, not original QPC, and does not expose the actual trace header. Provider emission outside syscall bounds remains unresolved. Old missing-clock samples remain unavailable, never rescored as passing |
| Capture blocking | Scoped receiver owns lifecycle ingestion; one SQLite writer owns the existing journal. Foreground polling/Win32 queries do not run on the receiver. Bounded nonblocking handoff, distinct refusals, idle partial flush, STOP/drain/join protocol, worker profile merge | Initial identity setup remains synchronous before launch; bounded lifetime attachment takes a shared lock. Callback decoding/identity queries and upstream losses still need real ETW measurement |
| State observations | Baseline files read after trace readiness; final files/registry read while receiving. Same-handle hashes/identities and bounded file read leases seal only after STOP/drain. Failed leases, deleted baseline-only evidence, registry ABA and SID failure remain explicit | An open can truncate without a write event; unresolved file contexts can be filtered. Production mutation coverage is false, so final state stays Unknown. Earlier verified source actors remain usable independently |
| Storage/views | Late losses atomically revoke event, summary, operation, graph and embedded observation claims; unfinished full/page/explain use committed raw evidence and consistent Unknown dimensions/counts | WAL/NORMAL process-crash recovery is tested, not hardware power-loss durability. Live interrupted ETW sessions still require validation |

The callback queue was split from 8192 records into 4096 callback records and 4096
handoff records; capacity was not enlarged to hide loss. The handoff additionally
charges 16 MiB encoded JSON and rejects individual records over 256 KiB. Rejected
records have overflow/oversized/disconnected/serialization counters and suppress
promotion. This adds a second serialization walk for size accounting and two scoped
threads. In-flight pages (256 each), the existing 1-MiB journal batch, and caches
are additional memory. See [exact balances](capture-counters.md). SQLite batching,
WAL/NORMAL, final FULL commit, busy timeout and opt-in E0/E1 policy are unchanged.
The writer's 100-ms idle tick prevents an idle partial batch waiting until STOP;
this is a flush mechanism, not a hard durability-latency guarantee.

Read leases are active measurement: they can reject concurrent write/delete opens
and ancestor renames. The 256-handle budget includes ancestors; it is not a maximum
hold duration. Baseline directory pins last through the baseline scan, and final
pins through scan/registry read/STOP/drain. Windows share modes do not establish an
atomic namespace or exclude every metadata/alias mutation. The actual
[CreateFile sharing and truncate semantics](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
support this narrower boundary. A future oracle must retain instrumentation-induced
sharing failures instead of interpreting prevented operations as absent behavior.

### Regression evidence

Failing tests were retained before fixes: stale late-loss summary metadata;
unfinished explanation selecting derived evidence; missing/unsealed/read-conflicted
snapshot proofs; a real unrelated `open(truncate)` with no write call; stale embedded
`final_source_complete`; stale unfinished counts/High dimensions; and the report
reader truncating a scalar compiler-version string to its first character.
Baseline a072072 already kept production final-state attribution Unknown because
its snapshot followed STOP. The truncate counterexample rejects an unsafe new
promotion path after moving observation earlier; it is not evidence of a measured
false attribution in that baseline's production captures.
Raw logs are under `target/stabilization/raw/{storage,ingest}` and
`target/v04/raw/reliability/p0c-*.log`. The intermediate integrated run with one
remaining observation-flag failure is preserved as `candidate-first-full.log`.

Final checks: **124 core + 6 fixture = 130 Rust tests passed**, 3 existing optional
performance experiments ignored; fmt, Clippy `-D warnings`, locked release build
passed. **84 Python tests** passed in the final complete run
(`target/stabilization/raw/final-python-all.log`). The unchanged
PowerShell scorer rejection suite passed. Original baseline Python scripts were
extracted from a072072 into `target/stabilization/baseline-scripts`; all 62 passed.
These are actual matching tests, not zero-test filters or build-only acceptance.

New worker tests use actual SQLite files and deterministic queue bounds: saturation,
byte/record refusals, partial tails, idle commits before STOP, pre-initialization
accounting, insert-trigger failure, a real held write lock/busy failure, operation
error/unwind, and release after STOP panic. Existing PID reuse, unordered/duplicate
lifetimes, conflicting completions, abrupt-process-exit, FULL/busy/checkpoint and
migration tests remain intact. Synthetic injection does not establish ETW capture.

`scripts/demo.ps1 -Release -SnapshotOnly` passed: 3 processes, 12 file states,
4 registry states, all 20 timeline events compared through full/paged history,
diff and every explain. JSON contracts and repeated ordering passed. File hashes
and the registry value were unchanged across `remove --dry-run`. Its independent
journal contained 21 operations; **zero were observed as source operations**.
Evidence is in `target/stabilization/integration/` and `raw/integration.log`.
Two additional finished database backups (one written by each version) were read
by both executables: their 52-event views matched after excluding only additive
clock/handoff/observation fields. Results are in `target/stabilization/compatibility/`.

### Paired local measurements: snapshot mode only

The frozen baseline a072072 and runtime candidate 076fe76 used the same new fixture,
with independent clock logging enabled in both arms. Each attempt checks all three
binary hashes before and after, retains its command/profile/DB/score/truth, and
alternates arm order. All 36 planned attempts completed and passed the existing
**fallback conservatism** checks; none was a real ETW acceptance attempt.

The [complete numeric archive](examples/stabilization-local.json) keeps every
attempt, group, metric denominator and executable/source/script hash. Private raw
captures and personal/system inventory are not committed. Median values below use
all three repetitions per arm, four fixture workers, 32 noise files (96 independent
noise syscalls), and initially empty databases:

| Files | Fallback passes baseline / candidate | Wall seconds | Parent CPU seconds | Parent peak working set MiB | Final DB MiB | Sampled WAL peak MiB |
| --- | --- | --- | --- | --- | --- | --- |
| 100 | 3/3 / 3/3 | 8.070 / 7.996 | 0.594 / 0.625 | 17.80 / 17.11 | 0.410 / 0.453 | 0.358 / 0.358 |
| 1,000 | 3/3 / 3/3 | 8.252 / 8.431 | 0.812 / 0.844 | 21.57 / 20.01 | 2.363 / 2.844 | 2.648 / 3.120 |
| 10,000 | 3/3 / 3/3 | 12.450 / 12.810 | 3.547 / 3.531 | 41.07 / 59.34 | 21.945 / 27.293 | 22.337 / 27.716 |

At 10,000, median parent logical read/write transfer increased from 64.259/44.341
MiB to 86.809/55.058 MiB. These are process logical I/O counters, not physical disk
traffic. The retained observation JSON adds storage and memory; this is a measured
cost, not a performance win. Working set includes the parent only; private memory
and DB/WAL peaks use 20-ms sampling. Caches/background load are uncontrolled, so
small wall differences are not significant-speedup claims.

Additional 1,000-file groups, three repetitions per arm, all fallback checks passed:

| Condition | Baseline / candidate median wall seconds | Purpose |
| --- | --- | --- |
| 1 worker, 32 noise files, empty DB | 9.271 / 8.957 | Compare worker count with the four-worker core group |
| 4 workers, zero noise, empty DB | 8.558 / 8.424 | Compare scoped background workload |
| 4 workers, zero noise, 27.293-MiB seed DB | 8.325 / 8.543 | Same committed SQLite backup in both arms |

These groups are separate; their repetitions cannot be pooled to satisfy the
required ten-run 1,000-file stability test under one real ETW condition.
Across the local matrix the independent journal has 211,512 successful target
operations and 2,304 successful noise operations. For each applicable group,
target source observation coverage = 0%, effective target attribution coverage =
0%, Unknown rate = 100%, and false-attribution rows = 0. Precision/raw false-discovery
rate are null because there are no positive source predictions. ETW loss counts
and rate are null because ETW was disabled; zero application drops with no source
traffic cannot establish capture reliability. These results do not meet useful
attribution acceptance despite all fallback checks passing.

Candidate observations retained at most 62/256/256 handles in the 100/1,000/10,000
groups. Median per-attempt maximum file-handle hold was 9.831/73.009/694.874 ms;
these do not bound ancestor hold time or future stalls. Stable read metadata covered
49/245/246 changed files; 0/256/4,755 changed files had read-lease refusal diagnostics,
principally the fixed handle quota at larger scales. Their readable states remain
present and Unknown. The full derived count/hash records are retained at
`target/stabilization/observation-metrics.json`.

### Reproduction and unresolved acceptance

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo build --workspace --release --locked
$env:CONTAIN_FIXTURE_CLOCK = '1'
python scripts/v04-matrix.py --baseline target/stabilization/baseline-bin/contain.exe --candidate target/stabilization/candidate-bin/contain.exe --fixture target/stabilization/candidate-bin/contain-test-installer.exe --baseline-revision a072072ad805880290fd1f47f67fba6828d2c24e --candidate-revision 076fe7634ab4644fa0bce123ac798c4a3dea6601 --output target/stabilization/local-paired-new --files 100 1000 10000 --workers 4 --noise-files 32 --trials 3 --snapshot-only
Remove-Item Env:CONTAIN_FIXTURE_CLOCK
./scripts/demo.ps1 -Release -SnapshotOnly -OutputDirectory target/stabilization/integration-new
```

Original raw directories are `local-paired`, `local-worker`, and `local-database`
under `target/stabilization`. The latter two use the same command with files=1000,
respectively workers=1/noise=32, and workers=4/noise=0 plus `--seed-database` pointing
to `local-paired/0014-candidate-10000-w4-n32-db0-t1/capture.db`. Each has three trials;
the database command includes both the empty and seeded condition. Output directories
must be new. Use `v04-public.py` then `v04-summarize-matrix.py` for the numeric archive;
never commit the complete capture or DB. The historical all-18-attempt stall audit
is also retained as [numeric evidence](examples/stabilization-consumer-audit.json).

All historical ETW failures remain failures, including 1,000-file candidate 1/3 and
10,000-file 0/3. There is **no current paired ETW result** for 076fe76. The new
[pre-registered protocol](v04-validation.md) plans 32 attempts per condition,
including ten pairs at 1,000, with the old score separate from the new blocked
clock/attribution gate. It has not run. No arbitrary tolerance, lower threshold,
reduced workload, deleted sample or all-Unknown usability pass is introduced.

Minimum remaining conditions: an already authorized isolated/recoverable Windows
environment with suitable ETW permissions; frozen binaries built from both exact
revisions and the common fixture; actual raw ETW QPC/trace-header capture with
validated provider timing/provenance; independent operation/collector-loss evidence.
Then run the fixed paired protocol, including snapshot interference and real STOP
failures. A verified disposable guest and independently adjudicated reference ledger
are additionally needed for third-party installers. No automatic elevation, remote
dispatch or vendor installation was attempted here.

**Do not start a safe-uninstall execution loop on this evidence.** Outstanding work
is original-clock validation, complete file mutation/context accounting, measured
live loss/coverage under 1,000 and 10,000 workloads, bounded observation interference,
and real installer/reference validation. Source request attribution and stable read
facts alone do not authorize deletion. Local development commits are kept on
the development branch (historical name withheld); this task does not push, merge or tag.

The remaining sections retain the earlier 2026-10-22 report. Their historical
branch, baseline, validation and GitHub synchronization statements do not describe
the new local-only stabilization commits above.

Status: **development; v0.4.0 release gates are not satisfied**. Package version
remains 0.3.1. This report separates historical elevated ETW measurements, local
fallback measurements, synthetic storage experiments, and unexecuted installer
validation. No third-party installer or real uninstaller runs on the host.

## Baseline and environment

Baseline: the development branch (historical name withheld),
`7c5a5b2b534d235f4db6c472c2c34a60e8be66ee`. On 2026-10-22 the working tree had
untracked `docs/preview.md`, `scripts/build-preview.py` and `ui/`; these are
pre-existing work and are excluded from this change's commits.

Local environment: Windows x64, NTFS; ordinary non-elevated token. Detailed
host inventory is retained locally. Rust/Cargo 1.98.1,
MSVC target, PowerShell 7.6.5, Python 3.12.7. No CONTAIN experiment overrides were
set. Cargo was installed but absent from the terminal PATH; commands below add
the existing installation to the process PATH. This is a tooling issue, not a
Contain failure. No toolchain or dependency upgrade was performed.

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo test --workspace --locked
cargo build --workspace --release --locked
./target/v04/baseline-bin/contain.exe doctor --json
python scripts/test-score.py
python scripts/test-commit-tail.py
python scripts/test-checkpoint-gate.py
python scripts/test-hotpath-artifact.py
python scripts/test-summarize-checkpoint.py
```

Original tests: 64 core + 1 fixture passed, 1 optional storage replay ignored;
release build passed. Python suites passed 7 / 9 / 6 / 3 / 14 tests respectively.
Baseline executables were preserved before production changes in
`target/v04/baseline-bin`; raw environment, hashes, commands and output are in
`target/v04/raw/baseline`. Public reproducible numeric metadata is in
[v04-baseline.json](examples/v04-baseline.json). Machine captures remain local
because they can contain unrelated service/task/process metadata.

`doctor` and an actual marked 100-file fixture capture both attempted ETW and
received access denied (`0x80070005`). Fallback completed, with zero source events,
49 file-state differences and no promoted file attribution. One service inventory
timed out at its existing 20-second bound; that warning and trial remain in the
raw result. These facts do not reproduce the elevated ETW throughput failures.
There is no verified disposable VM/checkpoint on this host; HypervisorPresent
alone is insufficient evidence of isolation or recoverability.

## Historical failures retained

| Experiment | 100 files | 1,000 files | 10,000 files | Classification |
| --- | --- | --- | --- | --- |
| Final v0.3.1 streaming comparison | 3/3 pass | 0/3 candidate vs 3/3 original baseline | 0/3 both | Queue stalls/loss; upstream ETW loss at large scale |
| Later ingest/query repair | small fixture passes | repaired candidate 3/3 pass | still fails | Throughput coverage restored at 1,000, not proof of end-to-end speedup |
| Deferred checkpoint E0/E1 | E1 small attempts 10/10 | E1 3/3; wall median 44.862 s vs E0 25.721 s | 0/3 both | Shorter live commit tails, remaining queue/ETW losses; E1 not promoted |

Sources: [performance](performance.md), [ingest repair](capture-ingest-regression.md),
[hotpath](capture-hotpath-queries.md), [commit tails](capture-commit-tail.md),
[checkpoint experiment](capture-deferred-checkpoint.md) and their complete JSON
trial archives. Results from different runners must not be combined into a speedup.
Default hotpath remains D0, and E1 remains explicitly opt-in.

## Audit boundaries

The source, tests, experiment scripts and historical reports were reviewed before
selecting changes. These are the relevant production paths and their remaining
limits; a bounded queue alone is not a claim of bounded total memory.

| Path | Finding and disposition |
| --- | --- |
| ETW callback / decoder | Existing nonblocking bounded channel and explicit overflow/disconnect/decode counters retained. Added fail-closed handling of stale file context; no provider filtering or loss denominator invented. |
| Process / thread lifetimes | Birth identity and temporal intervals are required. Duplicate/conflicting starts, late stops, ancestor ambiguity and reused parent PID now have regression coverage. |
| Filesystem snapshots | Existing scans hash every readable file with BLAKE3; reparse paths are excluded and unreadable subtrees are not called deletions. The 100,000-entry quota remains. No unsafe metadata-only hash cache or narrowed scan was introduced. |
| Registry snapshots | Values of one selected HKCU Software key, not a recursive machine registry snapshot. Value bytes retain their type and lossless encoding. Enumeration size is not globally bounded. |
| SQLite | Existing WAL/NORMAL, 256-record raw batches, atomic finalization, paged raw readers, quotas and checkpoint experiments retained. Measured readback joins/indexes changed; no claim that live writer stalls are solved. |
| Evidence / views | Operation result, actor identity and final-state ownership remain different claims. Exact event-ID updates and full/paged/explain tests prevent inconsistent stored flags. |
| Synchronization / memory | Callback mutex work and process-query costs remain instrumented. Full exports, snapshot maps and registry value buffers still materialize data; the new lookup maps add temporary linear memory. |
| Recovery | Existing subprocess abrupt-exit, busy-writer, full-disk, checkpoint and rollback tests run with the new code. These establish tested process-crash behavior, not power-loss durability under NORMAL. |

The final snapshot guard intentionally leaves runtime file/registry state
validation unpromoted in the current stop-then-snapshot pipeline. The new exact-ID
registry persistence path is unit-tested infrastructure; it is **not** evidence
that real ETW registry final-state validation has passed.

## Correctness fixes

Eight new minimal regression cases fail against the baseline: duplicated thread
start reopens an ended interval; conflicting thread start overwrites its owner;
process stop preceding start is lost; conflicting later stops extend lifetime;
file and registry operation direction conflicts promote snapshot changes;
an executable-name suffix is mistaken for an exact inventory target; and an
inventory target match is labeled as an observed writer. Red-test output is
retained at `target/v04/raw/reliability/red-tests.txt`. These are correctness
failures, distinct from queue loss, evidence gaps, and environment restrictions.

Storage audit identified and measured a quadratic evidence readback loop. The
live queue (8192), raw batch (256), evidence quota,
provider masks, drain limits, and frozen attribution gates are not relaxed.

The fixes now retain pending stop boundaries, make duplicate starts idempotent,
reject conflicting identities, shrink rather than extend lifetime endpoints,
and revoke descendant claims when an ancestor becomes ambiguous. The owned
installer handle remains independent Certain evidence. File/registry composition
checks mutation direction and ordering; unrelated/mixed writers remain Unknown.
Inventory parsing requires an exact executable token, and an `inventory_target`
Medium edge replaces the incorrect `observed_actor` edge.

Further regression tests reject duplicate/conflicting completions (including the
paged storage lookup), preserve the original records in the raw journal, and preserve an uncompleted
delete as `DeleteRequested`. File records arriving before the decoder's watermark
are rejected before object/IRP context can be reused. This increments decode
ambiguity, clears unsafe context, and suppresses session promotion. It is deliberately
conservative across objects; real ETW testing must quantify resulting coverage loss.

State validation now persists only selected event IDs in one post-stop transaction,
including registry values, updating typed and JSON stores together. Tests compare
full/history/page/explain events and inject rollback, foreign-session, unfinished
capture and late-loss cases. A same-path unselected event is never marked validated.

The audit also found a fundamental sampling gap: final snapshots run **after**
ETW stop and paged evidence postprocessing. An unrelated writer can change the resource
in that interval. The capture path now checks the monotonic trace/snapshot boundary
and keeps these final-state ownership claims **Unknown**, even with zero reported
loss and an earlier High source event. Source-event actor attribution is preserved;
the snapshot does not prove who wrote its final bytes. This intentionally reduces
state attribution coverage and does not change the frozen syscall oracle.

Validation at this stage: formatting, Clippy with `-D warnings`, locked release
build and workspace tests pass at `880b450`: **93 core + 2 fixture tests**, with three opt-in
disk experiments excluded from the ordinary suite. Original assertions and
acceptance thresholds are unchanged. Thirteen identity/attribution/completion
counterexamples were first observed failing before their fixes; all pass now.
Raw logs: `target/v04/raw/reliability` and `target/v04/raw/final-*.log`.

A final targeted audit found one additional parent-PID reuse case: revalidation
could revoke a child and then reattach it to a different birth of the same parent
PID. Promotion now respects an already-recorded parent creation time, leaving a
conflict Unknown and preserving its original evidence. Its red test and 16 passing
lifetime tests are retained as `parent-birth-{red,green}.txt`. Measurements of
candidate `07e2e86` predate this extra guard and must keep that revision label.

## Measured storage optimization

`storage/evidence.rs` used a linear search for every evidence row and repeatedly
allocated registry resource names. Exact-resource lookup maps replace that join;
they preserve the existing first-match behavior and add O(N) temporary memory.
Release disk-backed fixtures contain N files **and** N registry values, with
distinct evidence per row. All three reads per API and all verification assertions
are retained. Warm-cache timings exclude fixture writes, verification and close.

| N files + N values | Summary before/after median ms | Full read before/after median ms |
| ---: | ---: | ---: |
| 100 | 0.743 / 0.370 | 0.777 / 0.460 |
| 1,000 | 37.616 / 2.707 | 37.769 / 2.656 |
| 5,000 | 904.521 / 20.587 | 901.002 / 21.456 |
| 10,000 | 3655.371 / 42.452 | 3633.803 / 42.319 |

Database sizes are identical for this isolated map change. This is approximately
86x faster summary readback at the largest synthetic scale, **not live ETW
throughput or installer speedup**.

A separate experiment keeps a 100-file/100-value target fixed and adds unrelated
sessions, each with 1,000 files, 1,000 values and 1,000 normalized operations.
Query plans confirmed missing session indexes on `system_events` and
`normalized_operations`. Additive indexes now install on both new and existing
schema-4 databases, without changing table contracts or JSON schema 3.

| History sessions | Summary before/after ms | Full before/after ms | DB before/after bytes |
| ---: | ---: | ---: | ---: |
| 0 | 0.341 / 0.403 | 0.376 / 0.441 | 286720 / 311296 |
| 10 | 3.399 / 0.383 | 6.648 / 0.443 | 14131200 / 16932864 |
| 50 | 25.524 / 0.398 | 52.303 / 0.473 | 70021120 / 83988480 |

The tradeoff is explicit: total elapsed for the 50 history writes increased
1095.353 to 1287.679 ms (+17.56%), median write increased 16.082 to 18.983 ms,
and database size increased 19.95%. Index creation on an old database also needs
one-time time and disk space. No raw-journal index or live commit policy changed.

[Every raw timing sample](examples/v04-storage.json) includes the earlier
diagnostic without write timings; it was not discarded. Tests verify exact
evidence mapping after SQL ordering and existing schema-4 index installation
without row changes. Existing recovery, rollback, full-disk and checkpoint tests
also passed. Three explicitly opt-in experiments are ignored by the ordinary
suite; both new benchmarks were separately executed.

```powershell
$env:CONTAIN_STORAGE_BENCHMARK_OUTPUT = (Join-Path $PWD 'target/v04/readback.json')
cargo test -p contain-core --release --locked storage::perf::state_evidence_readback_scaling -- --ignored --exact --nocapture
$env:CONTAIN_STORAGE_BENCHMARK_OUTPUT = (Join-Path $PWD 'target/v04/history.json')
cargo test -p contain-core --release --locked storage::perf::history_database_readback_scaling -- --ignored --exact --nocapture
```

For the first before arm, add only `storage/perf.rs` and its test module declaration
to baseline `7c5a5b2`; the corresponding after arm adds only the evidence lookup
map change, without the later indexes. For the index before arm, keep the map fix but omit the two
new indexes in `storage/migration.rs`. Run only the named ignored test in each
command; do not change its workload, assertions, or run two disk experiments
concurrently. Full raw stdout is retained locally in `target/v04/raw/storage`.

## Local paired workload regression

All **60** planned captures completed with zero harness/metric errors. These are
explicit `--snapshot-only` experiments on the non-admin host, not ETW throughput
measurements. Each arm uses the same fixture binary and independent operation
journal; the original scorer and thresholds remain unchanged.

The initial 42 attempts compare `7c5a5b2` with `07e2e86`: five file scales with
four workers and three pairs (30 attempts), 1/8-worker sensitivity at 1000/10000
(8 attempts), and 0/1000 background files at scale 1000 (4 attempts). The later
18 attempts independently compare the same baseline with final production code
`880b450` at 100/1000/10000 files, four workers, three pairs, and 100 background
files. No earlier sample is discarded or pooled with the later revision.

| Initial candidate 07e2e86, 4 workers | Baseline median wall s | Candidate median wall s |
| ---: | ---: | ---: |
| 100 | 7.821 | 8.181 |
| 500 | 8.306 | 7.985 |
| 1,000 | 8.145 | 8.233 |
| 3,000 | 9.406 | 9.531 |
| 10,000 | 12.225 | 12.861 |

| Final candidate 880b450 | Wall s before/after | Parent CPU s before/after | Peak working set MiB before/after | Closed DB MiB before/after |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 8.380 / 7.778 | 0.609 / 0.578 | 17.87 / 17.81 | 0.39 / 0.41 |
| 1,000 | 8.198 / 8.422 | 0.766 / 0.859 | 21.58 / 21.55 | 2.26 / 2.37 |
| 10,000 | 12.713 / 12.160 | 3.156 / 3.094 | 41.69 / 41.11 | 20.89 / 21.92 |

The mixed wall-time differences do not establish an end-to-end speedup. Memory
and CPU cover the Contain parent process, excluding descendants and kernel ETW
buffers. At final scale 10000, median logical read/write transfer was baseline
71.11/42.26 MiB and candidate 64.24/44.29 MiB; these are not physical disk usage.
Every per-trial resource measurement, WAL/private-memory sample peak and group
denominator is retained in [initial 42 trials](examples/v04-local-matrix.json)
and [final 18 trials](examples/v04-local-final.json).

All 60 fallback conservatism gates pass. Their target syscall recall is **0**,
successful target/noise Unknown rate is **1**, audited-positive precision is
**null**, and wrong-attribution rate is **0**. ETW loss counters/rate are **null**,
not zero. Final snapshot differences remain Unknown. These outcomes demonstrate
conservative fallback, not useful source attribution or a satisfied release gate.

Raw records remain in `target/v04/matrix-{core,workers,load,final}`, with safe full
projections under `target/v04/public-local-*`. The numeric archives reference
projection and measurement SHA-256 values and include every attempt. Reproduction
uses `scripts/v04-matrix.py` with the parameters above, then `v04-public.py` and
`v04-summarize-matrix.py`; [the protocol](v04-validation.md) defines all boundaries.

## Elevated ETW workload regression

The first paired workflow, [39811449654](https://github.com/LuckyLivio/Contain/actions/runs/39811449654),
compared `7c5a5b2` with `07e2e86` on one elevated Windows Server 2025 runner
(Rust 1.99.1, matching locked dependencies; detailed host inventory retained locally).
File/process ETW were active; registry was not requested for this file stress
experiment. All 18 attempts completed and published complete evidence before the
unchanged gate failed. There were no harness or measurement errors.

| Files | Gate baseline/candidate | Median wall s baseline/candidate | Parent CPU s baseline/candidate | Peak working set MiB baseline/candidate |
| ---: | --- | ---: | ---: | ---: |
| 100 | 3/3 / 3/3 pass | 6.166 / 5.388 | 0.828 / 0.984 | 19.86 / 19.76 |
| 1,000 | 3/3 / 3/3 pass | 8.967 / 8.526 | 3.875 / 3.719 | 25.06 / 26.38 |
| 10,000 | **0/3 / 0/3 FAIL** | 33.156 / 28.323 | 22.094 / 19.156 | 45.60 / 45.75 |

At 100/1000 the strict target recall and observation are 100%, precision is 1,
wrong attribution is zero, and all reported loss counters are zero. All 300 noise
operations remain observed-but-unresolved; combined target/noise Unknown rates
are therefore 54.35% and 10.71%, respectively. This is scoped synthetic evidence,
not a general precision estimate.

All six 10000-file attempts reach queue capacity 8192 and fail the application
loss, ETW event loss, observation and attribution gates. Queue overflow ranges
32,483–52,449 records; ETW event loss ranges 113,481–174,176. Reported buffer loss
is zero, which does not cancel event loss. These are demonstrated collection
failures, separate from the small-CI clock issue below. Each large capture
conservatively downgrades source attribution: recall 0, Unknown 100%, precision
null, zero asserted wrong positives. The event-loss *percentage* stays null
because the counters lack a verified common denominator.

The 10000 candidate's shorter median time **is not a performance win**: median
persisted raw events fell from 74,019 to 59,822, median ETW event loss increased
from 124,448 to 165,383, and matched target observation fell from 52.388% to
39.788%. Its wall maximum was 45.147 s versus baseline 39.125 s. At 1000 the
overlapping ranges and three samples do not establish a general speedup either.
Candidate logical writes were 824.05 MiB versus 808.74 MiB; parent working set
also increased. No failed or slow sample is removed from these comparisons.

The [complete numeric trial archive](examples/v04-etw-07.json) retains CPU,
memory, logical I/O, DB/WAL, profile phases, all counters/gates, hashes and exact
build provenance. The 154 MB public projection and workflow log are preserved
locally under `target/v04/remote/39811449654`, and the workflow artifact retains
the scoped oracle rows. This public artifact is not a complete machine trace;
private hosted raw captures were deliberately not published and are unavailable
after runner teardown. Every unmatched syscall cannot be diagnosed further from
that projection alone.

The final production revision `880b450` was then tested independently in
[run 39812655516](https://github.com/LuckyLivio/Contain/actions/runs/39812655516)
with the same 18-attempt protocol. All attempts completed with complete artifacts
and zero harness/metric errors; **9/18 pass**. The completed ETW job fails its
unchanged acceptance step. These samples are kept separate from the earlier run.

| Final 880b450, files | Baseline gate | Candidate gate | Median wall s baseline/candidate |
| ---: | --- | --- | ---: |
| 100 | 3/3 pass | 3/3 pass | 5.201 / 5.557 |
| 1,000 | **2/3 FAIL** | **1/3 FAIL** | 8.692 / 8.135 |
| 10,000 | **0/3 FAIL** | **0/3 FAIL** | 42.141 / 36.413 |

At 1000, baseline trial 3 dropped 805 queue records; candidate trials 1 and 3
dropped 221 and 241. Their ETW event-loss counters were zero, so these are
application-queue losses, not proof of upstream ETW loss. Source confidence is
conservatively revoked, giving those attempts recall 0 and Unknown 100%. This
demonstrates that even the 1000-file gate is **not stable**; the previous run's
3/3 result does not override it. At 10000 all six attempts have queue overflow;
four also have nonzero ETW event loss. All six fail and downgrade to Unknown.

All 18 final attempts retain zero strict false-positive and matched-row
incorrect-attribution counts. Positive precision is null on loss-degraded runs,
not perfect. The final candidate's median 10000 persisted count is 74,787 versus
baseline 94,261. At 1000, two candidate loss downgrades also reduce final evidence
and database work. Lower wall time, write traffic or database size under these
conditions cannot establish a performance improvement. The final
[18-trial archive](examples/v04-etw-final.json) preserves every metric, gate,
profile phase and provenance; raw public evidence is in
`target/v04/remote/39812655516`. Across the two elevated matrices, **all 36 planned
attempts** remain available. Required 1000- and 10000-file release gates fail.

## Strict clock-domain validation failure

Ordinary hosted Windows CI was inconsistent: `07e2e86` push run
[39811449454](https://github.com/LuckyLivio/Contain/actions/runs/39811449454)
passed, while its PR run
[39811456338](https://github.com/LuckyLivio/Contain/actions/runs/39811456338)
failed; `880b450` push run
[39812014521](https://github.com/LuckyLivio/Contain/actions/runs/39812014521)
failed, while its PR run
[39812022615](https://github.com/LuckyLivio/Contain/actions/runs/39812022615)
passed. Both failing fixtures had zero reported queue/ETW/decode loss, and their
exhaustive CLI view checks passed. Their original strict scores remain failed.

Inspection of all seven unexpected positive records found matching PID, process
creation time, resource and operation in the independent journal, but ETW time
was 0.1–79.3 microseconds after that journal row's syscall end. The failing target
recalls were 12/17 and 15/17, with five and two unmatched positive events. The
matched-row wrong-actor counts were zero. This is a demonstrated interval-matching
failure, not proof that the unmatched records are correct or that no attribution
bug exists. [Exact ticks and original hashes](examples/v04-clock-diagnostics.json)
preserve the diagnostic without rewriting the original score.

The locked ferrisetw 1.2.0 trace uses QPC (`ClientContext=1`) but its consumer does
not request RAW_TIMESTAMP. Despite its getter name, `raw_timestamp()` therefore
receives the ProcessTrace conversion to FILETIME; the independent fixture uses
`SystemTime::now()`. Microsoft's [consumer documentation](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/ns-evntrace-event_trace_logfilew)
describes this conversion. Existing artifacts lack original QPC and calibration
data, so conversion uncertainty, wall-clock adjustment and provider emission
timing cannot be separated. No offset, tolerance, assertion or acceptance score
was changed to hide these failures.

## Changed files

| Files / groups | Technical change |
| --- | --- |
| `crates/contain-core/src/{lifetime,attribution,correlation,reliability}.rs` | Conservative lifetime, completion, state-composition and inventory evidence rules; regression cases alongside implementation. |
| `crates/contain-core/src/capture.rs`, `windows/etw.rs`, `windows/etw/decode.rs` | Trace/snapshot coverage guard, exact validation integration and stale file-context rejection. |
| `crates/contain-core/src/storage.rs`, `storage/{evidence,migration,stream}.rs` | Indexed readback, additive schema-4 indexes, atomic exact-ID persistence and completion tests. |
| `storage/perf.rs`, `storage/state_validation_tests.rs` | Disk-backed measurements, schema compatibility and rollback/view/loss regressions. |
| `examples/test-installer/src/main.rs`, `scripts/v04-*`, `scripts/test-v04-*`, `scripts/demo.ps1` | Configurable bounded worker fixture, paired measurements, independent scores, publication, strict local summarization and exhaustive CLI consistency checks. |
| `.github/workflows/{ci,v04}.yml` | Regression suites and dedicated all-attempt elevated ETW gate with evidence publication before failure. |
| `README.md`, `docs/{architecture,json-contract,v04-validation,validation-v0.4}.md`, `docs/examples/v04-*.json` | Evidence semantics, reproducible protocol and complete numeric trial archives, including failures. |

## Verification, release decision and next work

Final production code `880b450` passes 95 ordinary Rust tests, formatting, Clippy
with warnings denied, and the locked release build. All 62 Python harness/scorer
tests pass, as does the existing PowerShell scorer rejection suite. The original
opt-in disk replay was additionally executed: 16,384/16,384 records persisted,
zero failed/quota-dropped records, 64 batches, 1.616 s total on this machine.
Its raw log is `target/v04/raw/final-replay-executed.log`; an earlier incorrectly
qualified test filter ran zero tests and is retained separately, not counted.
This synthetic replay does not replace a live ETW stress run.

No third-party MSI, NSIS, Inno Setup or self-extracting installer was executed.
The local token cannot activate ETW, and a disposable recoverable guest was not
established. The hosted runner provides real ETW for the bounded fixture, but no
vendor experiment or blinded independent reference ledger was established there.
Thus real-installer precision/recall/Unknown and physical-disk metrics are
**unavailable**. The [validation protocol](v04-validation.md) supplies preflight,
Windows commands, official candidate sources, independent Process Monitor ledger
requirements and an explicit list of unverifiable/out-of-scope cases.

Release remains **blocked**, package version **0.3.1**, with no v0.4.0 tag. CLI
commands, JSON schema 3 and existing database schema 4 remain compatible. Local
development commits are synchronized to the development branch (historical name withheld); no merge or
release is implied by that synchronization.

Priority work toward v0.5 (or before any v0.4 release) is:

1. Establish independently recorded QPC syscall bounds, trace clock metadata and
   bracketed FILETIME calibration; validate provider timing before changing any
   production timestamp mapping or strict oracle contract.
2. Profile the remaining 10000-file live persistence/queue stalls and ETW loss;
   measure bounded consumer/write isolation against the existing D0/E1 controls.
3. Align per-resource final snapshot coverage with the live trace before allowing
   final-state promotion. Add generation-aware bounded reordering only with
   evidence that it preserves object/IRP ownership; the global watermark is
   deliberately conservative and may reduce coverage.
4. Run the vendor matrix in a verified disposable guest with a blinded independent
   oracle. Resolve service/broker ownership only with a causal link; otherwise
   retain Unknown. Bound registry/export memory and test power-failure durability
   separately from the existing process-crash recovery tests.
