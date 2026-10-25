# v0.4 development validation

## Current result: request-linked stacks recovered; observer incomplete (2026-10-25)

The owner's administrator run **actually captured independent request stacks**.
Both extra D Writes still reproduce with the observer enabled. Retained positive
evidence now supports branch **B for these two requests**: they occur on a
`DeleteFileW` execution path through filesystem filters, flush and cache-coherency
functions. Branch **C still applies to current-name/file-identity meaning**.
The observer attempt failed: its source reached the configured size limit with
loss, and its stop/merge controller was terminated by the entry's wait timeout.
Neither this attempt nor the earlier scores are reclassified as passing.

Runtime remains **d80b4903c237f892e98a913c566a3f1b51e4e349**, observer source
**6f4d8fa6ccac07a13571b59728dd4849edc93335**, version **0.3.1**. All seven wrapper
and eight child input hashes agree before/after; the child's eight inputs also
match the prior observer-off run. This audit began at report HEAD **8396bca**.
Wrapper-only repair **df1e6ef4aaedb5eace0e75b526078aba3f06501f** is tested locally,
but has not been run with an administrator token. Original frozen inputs and
artifacts were not replaced. No new capture, retry, identity arm or acceptance
run was launched during this audit; the original workspace/UI/preview is intact.

### What failed, and current session state

The child capture/export/analysis completed with exit0. The outer entry's
`subprocess.run(timeout=20)` then killed the WPR stop controller while it was
merging. Python [documents this timeout behavior](https://docs.python.org/3.11/library/subprocess.html#subprocess.run).
The partial merged ETL is 12,582,912 bytes but contains **only one header event**;
it supplies none of the request/stack evidence below. A successful child does
not turn this failed outer attempt into a completed observation.

Read-only `ControlTraceW(EVENT_TRACE_CONTROL_QUERY)` calls for both exact owned
collector names subsequently returned **4201**, not access-denied5. Microsoft
[defines this as the named session not running](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-controltracew).
No corresponding controller/capture process remained. No recovery STOP is
needed; the old stored command must not be retried against its existing output.
This later absence does not establish that the historical merge succeeded.

The retained source ETL has **1,726,945 readable records**, including 805,098
StackWalk records. Its header reports **80,719 events and 129 buffers lost**.
`MaximumFileSize=256 MiB`, `BufferSize=262,144` bytes and `BuffersWritten=1,024`
agree with its 268,435,456-byte length. The source ends about 2.03 seconds before
the stop request. The source reached its configured sequential capacity and is
incomplete. No explicit contemporaneous capacity-error status was retained;
the exact cause of all reported losses is not established. These are independent
observer losses, not Contain main-queue drops. No loss rate is inferred by
subtracting counts from different collectors.

Offline analysis used verified copies of all three ETLs; original SHA-256 values
remain unchanged. xperf's default loss rejection was preserved, then documented
`-tle` was used only to inspect retained positive evidence. An independent native
SDK `OpenTraceW/ProcessTrace` reader with `RAW_TIMESTAMP` verified the selected
payloads. ProcessTrace exit0 means the readable source was processed, not that
the capture was loss-free or complete.

### One auditable extra-Write chain, confirmed twice

1. The independent journal records a successful D0 delete after the original
   write/close/rename sequence. Contain records a 48-byte manifest Write
   (provider `edd08927-9cc4-4e65-b970-c2560fb5c289`, ID16/v1), followed by its
   unique successful completion. Request/completion fall 2,078/2,229 QPC ticks
   after the delete call begins; no time tolerance was added.
2. Independent classic FileIo provider `90cbdc39-4a3e-11d1-84f4-0000f80464e3`
   has an ID0/opcode68/v3 Write with the same IRP, FileObject, FileKey, thread,
   offset0, size4096 and flags `0x60043`. This multi-field match is unique within
   the inspected 20ms window; its ID0/opcode76/v3 completion has the same IRP,
   status0 and ExtraInformation42. The request timestamp is two ticks later
   than the manifest event, not equal; the second D request differs by one tick.
   Shared EventKeys, schemas or exact cross-provider emission times were never
   assumed. The actual source header is QPC clock1 with 10MHz frequency.
3. Stack provider `def2fe46-7bd6-4b80-bd94-f57fe20d0ce3`, ID0/opcode32/v2,
   contains one 440-byte payload for that request: its **original event QPC,
   PID and TID match exactly**, followed by 53 addresses (38 kernel, 15 user).
   Each selected request and stack is unique in the complete readable-source
   scan. These are actual [StackWalk association fields](https://learn.microsoft.com/en-us/windows/win32/etw/stackwalk-event),
   not adjacent-output or nearest-time matching. Native payload and xperf frames
   agree address for address; D0 and D1 stacks also agree.
4. Selected frames, from outer to inner, are `DeleteFileW` -> private helper ->
   `ntdll!NtSetInformationFile` -> ... -> kernel `NtSetInformationFile` -> ... ->
   filesystem filtering -> `FltFlushBuffers` -> `FltFlushBuffers2` -> ... -> NTFS
   -> `CcCoherencyFlushAndPurgeCache` -> ... -> Write event recording. Ellipses
   are unresolved/intervening frames, not claims of direct adjacent calls.
   Named functions are confirmed by matching local PE export entry points and
   unwind-function bounds. Eight module timestamp/checksum/image-size tuples
   match trace image records. Current module hashes are **not** runtime hashes.
   Full PDB symbol resolution was not obtained; a mismatched fixture PDB was rejected.
   No symbol download occurred.

Contain has verified process/thread birth identities for these requests. The
independent trace's rendered lifetime model also has one covering process and
thread interval, without an observed reuse/conflict; lifecycle raw payloads were
not separately fully decoded. Known observer loss prevents a global no-reuse or
no-other-request claim. The request tuple is unique in the inspected window;
pointer uniqueness is not asserted across lifetimes or missing events.

This proves real lower-layer Write on the observed delete execution path, not an
additional explicit application write call. The stack includes an additional
installed filesystem filter whose internal policy/parameters remain unresolved;
do not generalize this to every Windows delete or a Windows-only mechanism.
Request size4096 and success do not prove 4096 new bytes, final content ownership,
or permission to delete anything.

Name evidence remains separate: Contain's old `.tmp` name comes from its original
FileObject's ID30 mapping, with `current_path_verified=false`; xperf's `.dat` is
its own resolved name, **not a file-ID or handle-name query**. This run has no
same-run independent before/after rename volume/file-ID observations. It cannot
choose between historical-open-name and incorrect-current-name interpretations.
No production cache rewrite, Write-to-Delete conversion or blanket downgrade was
made. The prepared transient identity variant is still unexecuted with ETW.

### Comparable child outcome and remaining limits

| Measure and scope | Prior observer off | This observer on |
|---|---:|---:|
| Successful API calls / complete file sequences | 16/16; 8/8 | 16/16; 8/8 |
| Successful calls with strict actor/op/path/QPC source match | 16/16 | 16/16 |
| Recorded Write / High / Unknown, Write subset | 14 / 10 / 4 | 14 / 10 / 4 |
| Extra D High Writes / D files | 2/2 | 2/2 |
| Main received / persisted records | 271 / 271 | 258 / 258 |
| File source / lifecycle / completion records | 206 / 43 / 22 | 206 / 30 / 22 |
| Main reported ETW, queue, handoff, write and quota loss | 0 | 0 |
| Companion received / saved | 276,056 / 276,056 | 218,596 / 218,596 |
| Capture seconds; export / analysis seconds | 26.323921; .205575 / .727662 | 7.981442; .142561 / .632373 |
| Final inventory milliseconds | 20,015 | 2,314 |
| Parent CPU seconds; peak working set bytes | 1.359375; 33,013,760 | 1.328125; 31,965,184 |
| Parent sampled private bytes | 21,921,792 | 20,480,000 |
| Parent read / write bytes | 24,909,652 / 29,423,328 | 11,694,597 / 25,196,890 |
| Final DB / sampled WAL peak bytes | 4,325,376 / 6,278,912 | 4,300,800 / 6,208,872 |
| Companion file bytes | 17,667,584 | 13,990,144 |

All 14 current Write payloads are complete and independently decoded; 258/258
main records link to raw QPC by full descriptor/EventKey. Handoff received,
submitted and drained253 plus five initial records equals258; pending0,
receiver/writer finished, main/handoff peaks41/34. Immutable DB `quick_check` is
ok with no remaining WAL/SHM. The child diagnostic remains incomplete because
131 diagnostic payload prefixes were truncated (127 admitted records and four
sampled-context records) and 217,906 context records sampled out; neither count
is a main-pipeline loss. Companion independent terminal
consumer-loss counters remain unavailable. The parent metrics exclude WPR's
whole cost, which is unavailable. Different inventory delays/system activity and
one sample per arm preclude a speedup or stability claim.

Proposed protocol handling is **same-layer comparison**, not an implemented gate
change: API-journal recall keeps all successful calls; native request claims
need independent request-layer evidence, separately from explicit API intent,
current name and final ownership. These two independently supported native
requests cannot justify dropping other unmatched positives. Old extra-High
failures and denominators remain; a revised protocol would need its own version
and equal evaluation of comparison samples with adequate evidence.

### Confirmed entry repair and verification

`df1e6ef` changes only the wrapper and its regressions. STOP now uses
`Popen.wait(timeout=20)` without terminating the merge on timeout/interruption.
It records controller PID/state before waiting, leaves live output hashes null,
keeps unfinished merge as failed/unconfirmed even after collector disappearance,
refuses an existing ETL destination, and never retries STOP. The wait budget was
not increased. A surviving merge can continue after the entry returns; this is
explicitly recorded, not an OS-stop or bounded whole-directory guarantee.

The regression failed against the old terminating path, then all **25 wrapper
tests passed**, covering timeout, interruption, checkpoint failure, existing-file
protection and successful completion. `./scripts/quality.ps1 -Mode Check` at
committed **df1e6ef** passed formatting, Clippy, **199 nextest tests**, **205 Python
tests in 17 suites**, PowerShell scorer/provenance checks and **1 fuzz seed replay**.
Four pre-existing performance/volume tests were skipped; doctests matched0.
Four existing dependency warnings remain. Current aggregate logs and matching
before/after input hashes are retained under private `quality-df1e6ef4aaed`.
Core coverage, fuzz campaigns, dependency gates and performance benchmarks were
not rerun for this wrapper-only change. Real validation of the repaired entry,
the identity variant, and formal 32-run acceptance remain **unexecuted**.

Private evidence is retained under
`io-stack-20261025/stack-20261025-124840-06b9a8b0789f`: `capture-audit`,
`stop-state-audit`, and `etl-offline-audit-6450945f` contain original-hash manifests,
exact offline commands/readers, selected request chains and frame provenance.
Only these reviewed aggregates are published; raw ETLs, addresses, module
inventories, clocks and databases stay local. Next missing evidence is a labelled
transient volume/file-ID/name observation across the rename, with unchanged
workload API results and extra-Write reproduction checked. It must not reuse
this incomplete observer as negative proof or substitute for formal acceptance.
Inventory timeout, companion quota and derived-write performance remain separate.

## Historical preparation: independent request stacks and sampled file identity (2026-10-25)

At preparation HEAD **8396bca**, the last actual minimal capture was **d80b490**,
reviewed at **3b89307**, version **0.3.1**. The following records preparation before
the administrator run reviewed above. It did not yet establish stacks or a new
reproduction outcome, production fix, changed scorer or formal acceptance.

### Observer selection and execution boundary

Process Monitor was not found in bounded checks of PATH, common tool directories,
running processes or installation/EULA records. That does not prove it is absent
from every directory. Its installed version/source and licence state are therefore
unavailable; no executable was downloaded, started or accepted on the owner's
behalf. Microsoft's [Process Monitor documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon)
describes per-operation stacks and native PML records, but no local PML has been
obtained.

The existing Microsoft-signed WPR **10.0.26100.9711** and xperf
**10.0.26100.3916** were inspected instead. `scripts/write-stack.wprp` requests
FileWrite, FileCreate, FileCleanup, FileClose, FileSetInformation, FileDelete,
FileRename and FileOpEnd stacks, together with process/thread/image and filename
context. The installed WPR profile parser and installed XSD accept this exact
profile. These are request-event stack settings, not CPU sampling; successful
configuration is not evidence that a captured request has usable frames.

An unrelated persistent kernel logger is already active. The observer must use a
fresh WPR instance, verify its exact owned collector, and stop only that instance.
It must never call a default/global stop or cancel to make room. Microsoft
[documents that instance name, not SystemCollector Name, determines the session](https://devblogs.microsoft.com/performance-diagnostics/controlling-the-event-session-name-with-the-instance-name/).
Read-only queries can return exit0 for an absent instance, so exit0 alone cannot
authorize a child capture or establish collector ownership.

The profile requests 256 KiB x128 buffers (32 MiB buffer budget) and a **256 MB
sequential source ETL limit**. [MaximumFileSize](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/maximumfilesize)
stops sequential recording at the cap; it does not overwrite old evidence. Hitting
the cap is incomplete evidence. This is not a 32 MiB whole-process memory cap or
a 256 MB total-directory cap: recorder overhead, merge output, Contain's existing
bounded artifacts and later exports are separate costs. No PID-only filter is
used; old/new names and other execution contexts remain eligible for collection.
No symbol download, service setting change, new driver, automatic elevation or
foreign-session stop is part of the entry.

### Fixed comparison and interpretation rules

The first observer-on run reuses the original frozen d80b490 executables and
original A-D APIs, eight files, one worker. It compares against the already
retained observer-off capture; there is no new observer-off rerun or automatic
second arm. The stack entry changes only the child's private output parent and
records the derived plan. Every input is hashed before and after. A fresh output
directory, bounded wait and staged failure record preserve interrupted attempts;
an uncertain child is not killed or retried. Actual stack inspection is required
before any later metadata-variant capture.

Matching will not assume shared EventKeys, timestamp epochs or provider schemas.
First inspect the actual classic kernel event layout and trace clock metadata.
Where available, require a unique request-generation match using IRP, object,
operation, length/offset and verified actor lifetime, with order and measured
clock bounds as additional checks. A pointer or nearby timestamp alone is not
enough. Missing fields or multiple candidates leave cross-collector correspondence
unavailable. Link stack fragments to their triggering request using retained trace
metadata and the existing decoder, not a nearest-time search. Preserve unresolved
addresses, image identity and symbol provenance; a missing/truncated stack or a
different observation layer cannot establish an application call chain.

The opt-in `write-diagnostic --observe-file-identity` variant keeps `std::fs::write`,
`rename` and `remove_file` unchanged; no flush or caching policy is added. A separate
`identity-observations.jsonl` contains at most 28 metadata observations, each with
open/query/close QPC boundaries and actual native errors. Transient attribute
handles share READ/WRITE/DELETE and close before the next workload call. FileIdInfo
records a 128-bit file ID and 64-bit volume serial; volume information separately
records filesystem type, label and formatting serial. Handle-name queries record
both OPENED and NORMALIZED flags and bounded-buffer failures.

These are names of **observer handles**; the original write handle is inaccessible
inside the unchanged `std::fs::write` API and stays unavailable. ETW-provided names,
historical cache origins and queried names remain separate. Equal volume/file IDs
on either side of a handle gap are sampled identity evidence, not proof of no
replacement, a unique pathname or ownership. Microsoft documents both the
[identity pair](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_id_info)
and [handle-name query modes](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew).
The variant's extra opens, queries, closes and logging can perturb I/O; compare
API success/error, final contents and the D extra-Write reproduction separately.

The preparation-time branch conclusion was **C: request mechanism and current-name
meaning remained insufficiently established**. A positive stack chain may support the
lower-level-activity/API-journal distinction, but will not retroactively pass old
scores. A demonstrated cache-semantic error needs a failing regression before a
minimal production fix. Thread execution, request success, current name, explicit
application call and final-state ownership must remain distinct judgments.
Scheduled-task inventory timeout, companion quota and derived-write performance
remain separate follow-ups; this stage does not refactor them.

### Frozen preparation and executed checks

Observer/optional-fixture source: **6f4d8fa6ccac07a13571b59728dd4849edc93335**.
The first stack entry still runs the original **d80b490** Contain and fixture
executables; it does not rebuild or substitute them. Its seven wrapper inputs
and eight existing child inputs pass the read-only hash preflight. Private plan
SHA-256: `edc35bde2f4d3fcade14a52aa4077a6e6f6019bb6f9eded88abf21dcc0ffd322`.
The entry's 60-second observation limit initiates only an owned-instance stop;
the status/counter/stop/post-status control budget is separately 35 seconds.
This is a bounded control attempt, not an operating-system stop guarantee. A
forced window close can leave a child or observer running; retain the new output
directory and use its exact-instance recovery record, never a generic stop.

`./scripts/quality.ps1 -Mode Check` was rerun on committed **6f4d8fa**: formatting,
workspace Clippy with warnings denied, **199 nextest tests passed**, **200 Python
tests passed across 17 suites**, the existing PowerShell scorer/provenance checks
passed, and **1 fuzz seed replay passed**. Four existing tests remain skipped
(three disk-backed storage experiments and one companion spool-volume experiment).
Doctests matched **0**, which is not acceptance coverage. The existing four
dependency warnings remain visible; no dependency or lint policy was changed.
Current aggregate logs are retained privately under `quality-6f4d8fa6ccac`;
an older zero-output formatter's stale individual log is explicitly labelled
historical, not used as current gate evidence.

The identity fixture's nine tests include default/observer call-result comparison,
exclusive-open proof that observer handles were released, and a failed-close plus
failed-clock regression preventing later workload operations. Local NTFS fixture
observations exercised 20 successful metadata opens/queries/closes, eight real
missing-path errors and four sampled rename identity pairs with changed queried
names. These are fixture/API checks, not a new ETW reproduction. The initial
private sidecar came from the uncommitted preparation tree; it was not relabelled
as a release capture. The full test suite was subsequently rerun at 6f4d8fa.

The optional fixture release build also succeeds at 6f4d8fa, with source hashes
before/after and the actual compiler executable hash retained. Its executable
SHA-256 is `38410eec4d767e3620f4cb5631e04a2feaa79ec4c3d5669c777ca98c2212d1b5`;
it is **not** selected by the first stack entry. No second administrator entry is
enabled until the first trace's request-linked stacks have been reviewed. Core
coverage, fuzz campaigns, throughput benchmarks and dependency checks were not
rerun: this patch changes diagnostic fixtures/orchestration, not production core,
parser, storage, dependencies or the capture hot path. WPR frames, symbols and
observer-on D reproduction were **unexecuted at preparation time**; the actual
run and remaining limitations are reviewed above. Formal acceptance is unexecuted.

## Prior result: extra Write reproduced in the minimal diagnostic (2026-10-25)

The frozen diagnostic source is **`d80b4903c237f892e98a913c566a3f1b51e4e349`**,
built from the reviewed `0ac483e` development history. The previous large-load
candidate was `545ed3f`, baseline `e72d88c`; version remains **0.3.1**. The owner has now run the
frozen **d80b490** diagnostic in the existing administrator window. Both D files
reproduced an additional High Write during the independent delete-call interval,
with successful completion records. The native source records are real; why this
I/O is issued and whether its historical name is its current path remain unproven.
No production naming/operation fix or scoring-protocol correction is claimed.
The development report started this audit at `c0792f8`; the original workspace
and UI/preview remain untouched. No new capture or formal acceptance was launched
during this read-only audit.

### Actual run and A/B/C/D outcome

Private attempt: `write-diagnostic-20261025/admin-20261025-120717-3042a933`.
The plan, all eight inputs and the short entry match the frozen identities below;
the eight runtime input hashes match before/after and at audit time. The build
manifest maps 263 tracked files to d80b490 (one report has only CRLF differences).
Both release executables were built in that recorded build. Compiler version is
recorded, but its executable hash was not captured then; that identity field is
missing and has not been filled using a later hash.

All **16/16 API calls succeeded**: 8 writes, 4 renames and 4 deletes, with null
errors. All 8 sequences and 16 final path checks match the fixture expectations;
4 files survive. Each of the 16 calls has exactly one retained successful source
operation matching operation, path/input-or-destination, PID plus birth and strict
original-QPC interval. This is positive call-observation coverage **16/16**, not
precision over every low-level I/O. The original scorer was not rerun or changed.

| Case, two files each | Explicit call-matching High Write | Extra High Write inside delete interval | Other retained Write |
| --- | ---: | --- | --- |
| A: write/close | 2 | Not applicable | 2 later Unknown, outside all calls |
| B: write/rename | 2 | Not applicable | 2 later Unknown, outside all calls |
| C: write/delete | 2 | None recorded; complete negative conclusion unavailable | 0 |
| D: write/rename/delete | 2 | **2, both with successful completion** | 0 |

All 14 retained Write records use the actual file provider GUID
`edd08927-9cc4-4e65-b970-c2560fb5c289`, id16/v1, opcode0/task16. Their actual TDH
schema has eight fields and their 48-byte payloads are complete. Independent
little-endian decoding agrees with all eight retained values for all 14 records.
The 8 explicit writes request IOSize42/offset0/IOFlags`0x60a00`; D's 2 extras
request IOSize4096/offset0/IOFlags`0x60043`; the 4 later A/B writes request
IOSize4096/offset0/IOFlags`0x60003`. ExtraFlags is 0 throughout. These numeric
flags are not assigned undocumented meanings or borrowed from another provider.
The 4 later records have header PID4 and remain Unknown; their completion does
not manufacture an actor. For this **14-record Write subset**, 10 are recorded
High and 4 Unknown (28.57%); this is not the Unknown rate of all captured events.
False-attribution rate and precision over all low-level writes remain **null**:
the API journal is not exhaustive independent ground truth for that layer.

### One auditable extra-Write chain

The complete local `clock-chain-audit/audit.json` preserves exact event IDs,
EventKeys, raw QPC, payload, pointers and actor identity. The following is a
redacted view of **D0, delete call 13**; offsets are QPC ticks from that call's
independently recorded start, at 10 MHz. No tolerance or clock inversion is used.

1. The API call runs from tick 0 to **28,294**, returns Ok and names `D-0.dat`.
2. The extra Write appears at **1,645**; its unique same-IRP id24/v0 completion
   appears at **1,710**, status0. Both have exact EventKey/descriptor links to the
   original-clock sidecar and lie strictly inside the call interval.
3. The recorded process PID/birth agrees with the independent process identity
   and owned process handle. Real process/thread start and stop records enclose
   the event. This does not rely on header PID or a later stop alone.
4. Write's FileObject and local cache generation lead to a retained id30 origin
   named `D-0.tmp`. Write's schema has **no FileName/FilePath**. An earlier successful
   Rename on a different FileObject supplies `D-0.dat` and the same FileKey; it
   invalidates that rename object's cache, not the original object's history.
5. Request IOSize is **4096**, but completion's complete 20-byte payload contains
   ExtraInformation **42** and Status0. ExtraInformation's interpretation has not
   been established for this provider. Neither value proves that 4096 bytes of
   new application content were written. Final state/ownership remains separate.

This rules out a Rename event being decoded as Write in these two samples. It
does **not** prove flush/writeback, a duplicate of the earlier write, deletion as
the cause, or a second explicit application write. The old path is demonstrably
cached history; same FileKey and a local generation do not independently establish
an immutable file identity, its unique current name, or safe deletion ownership.

### Collection, clock and diagnostic limits

| Measurement | Actual value and scope |
| --- | --- |
| Main receive / persisted | 271 / 271; 206 file, 43 lifecycle and 22 completion raw records |
| Main queue / handoff / write / quota loss | All reported counters 0; pending0; receiver and writer finished; no quiet-drain timeout |
| Upstream ETW loss | Reported events0/buffers0; raw consumer's final independent loss counters unavailable |
| Raw-clock companion | 276,056 received/accepted/stored; 17,667,584 bytes; queue/capacity/quota/not-persisted counters0; producer stopped, writer drained, binary complete and hash verified |
| Clock correspondence | Independent scan finds no duplicate EventKeys; 271/271 main descriptors link exactly; 16/16 call clock envelopes valid with matching native machine/boot observations |
| Attached diagnostics | 228/228, 598,863 encoded JSON bytes; no budget rejection, oversize or field parse errors |
| Bounded context | 48 first-N samples (16 per reason), 112,605 bytes; **275,347 omitted**, not main-queue drops |
| Truncation | **132 payload prefixes** (127 admitted + 5 context); no schema/text-field truncation. All 14 Write payloads complete. This still blocks complete negative coverage. |
| Capture / export / offline analysis wall time | 26.323921 / 0.205575 / 0.727662 seconds |
| Main process CPU / memory | CPU1.359375s; peak working set33,013,760 B; sampled private peak21,921,792 B; no sampling errors; not the whole process tree |
| Main process I/O | Read24,909,652 B; write29,423,328 B; includes companion activity, not an on/off overhead comparison |
| Database / WAL | Final DB4,325,376 B; sampled WAL peak6,278,912 B. Sampled DB peak4,096 B missed final growth and is not the true peak. |

The database is finished, has no WAL/SHM/journal sidecars and passes read-only
`quick_check`. Its hash matches the export-time hash. The selected 27 original
artifacts retain their hashes, sizes and mtimes across the audit. Full evidence
stays local in `clock-chain-audit`, `identity-integrity-audit` and
`resource-semantics-audit` alongside the untouched run files.

The saved analyzer still says **unavailable_or_incomplete**, fix/release gates
null. Its two failing negative-coverage checks are truncated_records132 and
context_samples_omitted275347, each requiring0. This is not a source-loss finding.
The nested old clock report's 16 actor-unavailable rows use the pre-attribution
raw view. The new per-sequence analysis separately verifies the unchanged facts
against final same-ID actor evidence; this is not 16 missing production actors.
The general provider emission/causality contract remains unverified. StartTime0
is retained as unavailable at realtime OpenTrace, not used as an origin. All four
final file ownership judgments remain Unknown.

The 26-second wall time includes a reported 20-second scheduled-task inventory
timeout. It does not establish a slow disk or callback bottleneck. This run has
no diagnostic-off pair, so added diagnostic CPU/I/O cost remains unavailable.

### Decision after this run

Keep the production decoder, cache and attribution rules unchanged for now. The
event really is a Write; renaming it Delete or removing the extra positive would
erase evidence. The API journal's same-operation matching does not cover every
lower-level I/O produced during other calls. That coverage gap is now explicit,
but it is not permission to reclassify historical false positives or change gates.

The next unresolved mechanism question is which filesystem/cache path issued the
4096-byte request during D's delete. These artifacts contain no call stack or
independent native-I/O trace to answer it. A future targeted D request/completion
stack observation could distinguish the competing explanations; it has not been
configured or run. A current-name fix additionally needs independent per-object
file identity and name observations across rename/replacement, not FileKey-only
propagation or blanket invalidation. No larger workload is justified by this run.

This audit adds no production or protocol change. The existing analyzer's 25
regressions and clock protocol's 55 regressions were rerun; independent read-only
checks also verified payload/layout, all main-to-companion links and database
integrity. Full Rust quality checks below belong to d80b490 preparation and were
not rerun for this report-only update. Formal 32-run acceptance remains unexecuted.

The dedicated `write-diagnostic` fixture keeps the original Rust file APIs, with
two files each in A (write/close), B (write/rename), C (write/delete), D
(write/rename/delete), in one thread. It is separate from the frozen stress/demo
workload. A fresh marked TEMP container and fresh payload/truth directories are
mandatory. Only this invocation's C/D disposable files are removed as part of
the requested diagnostic; no cleanup, uninstall or user-data deletion exists.
The journal is outside the watched directory. Each of the 16 API calls records
independent QPC/calibration bounds, actual result/error, order, process/thread
creation times, local native provenance and requested payload bytes. The internal
File Drop in `std::fs::write` has no independently observable close result; this
is recorded as null, not success. Failed calls and stopped dependent sequences
remain in the journal. Final content/path observations are separate from calls.

One unmonitored eight-file self-test completed all 16 APIs successfully, with four
expected surviving files and matching contents. Its executable hash was unchanged
before/after. This validates the fixture, not ETW or the extra-Write hypothesis.
Private artifacts are under `target/stabilization/write-diagnostic-20261025/`.

### Evidence additions, without changing attribution

`CONTAIN_FILE_DIAGNOSTICS=1` enables diagnostic copies on existing admitted file
records and completions. Default behavior is off. Actual descriptors, TDH property
names/raw types, payload prefixes, field parse states, name selection, cache
establishment/invalidation and pending-IRP links are retained. Missing fields,
schema errors, type mismatches and truncation remain distinct. A historical cache
name is explicitly not a proven current path. No filename propagation by FileKey,
operation relabelling, actor promotion, new SystemEvent or scoring change occurs.

Copies travel through the existing bounded queue and persistence path; callbacks
perform no synchronous disk writes. Limits are 16 KiB per diagnostic record and
8 MiB total attached diagnostic JSON; names are capped at 1 KiB, raw payload at
256 bytes, and actual schema at 24 fields with 64-byte names. Non-admitted context
has a separate 128-sample/512-KiB ceiling and 16 samples per fixed reason. All
omissions and budget failures are visible. This first-N context is not a complete
system trace. Cache-origin metadata uses the existing 16,384-object ceiling.
Diagnostic JSON bytes are not a measurement of allocator overhead. The real run
above measured enabled totals; incremental CPU and SQLite/I/O cost still lacks
a matched disabled run. These limits do
not replace the existing queue, raw-evidence or raw-clock companion quotas.

The read-only analyzer joins original QPC through the unchanged protocol-4
EventKey/descriptor checks, preserves raw and final views separately, and records
request/completion identity and interval relationships without turning Write into
Delete. Its output is an evidence review, never an acceptance score. Missing
clocks, loss, actors or diagnostic fields remain explicit. The original scores,
thresholds and formal workload are unchanged. The entry makes a separate bounded
export of all committed raw records, including lifecycle/open/completion, using
a read-only database connection; it does not overwrite the CLI export or database.

### Source review retained from preparation

The retained provider export and the current local manifest identify GUID
`edd08927-9cc4-4e65-b970-c2560fb5c289`, id16/version1, task Write/opcode Info. Its
fields are ByteOffset, Irp, FileObject, FileKey, IssuingThreadId, IOSize, IOFlags
and ExtraFlags; FileName/FilePath are absent. This supports investigating cached
names but is not a replacement for each historical callback's actual TDH schema.
The matching Rust 1.98.1 local standard-library source calls DeleteFileW, with an
access-denied disposition fallback; it contains no explicit write in remove_file.
Neither observation proves why Windows emits the extra Write during deletion.

The completed run above used this single eight-file fixture, frozen inputs,
real file/process providers and original-clock companion. No fallback was used.
The entry retains unique output directories and stage checkpoints, with no retry,
cleanup, automatic expansion or formal acceptance. Companion quota, quiet-timeout
and storage optimization remain separate follow-up items.

### Frozen inputs and executed checks

The release build used Rust **1.98.1 (48a229cea)**, Windows x64 MSVC and the
committed lockfile. All tracked source hashes matched before/after the build.
The only untracked development-tree item is local `vendor/ferrisetw/target/`
build output; it is neither staged nor a source input. The earlier unmonitored
fixture used an earlier executable hash: its fixture/provenance source and lockfile
match the frozen sources, but it is not a run of the newly frozen binary.

| Frozen artifact | SHA-256 |
| --- | --- |
| `contain.exe` | `66fdbea18a0fcca82d5a48a21a619d615e29783937088d6f91056f942189a577` |
| `write-diagnostic.exe` | `5039423469e3367e56ade0f04107cc09916054a897ad324d832f4d7899da9f4b` |
| Private plan (all eight inputs pinned) | `b6fb0098a422cd2c927fdfb8aeb95b5392b08abd29d5c96fcc626bc7b474ba76` |

The plan pins both executables, runner, analyzer, original clock protocol, matrix
metrics helper, original scorer import and private build manifest. The entry checks
them before and after execution. A read-only preflight passed; it starts no ETW.
Private build commands, source/script hashes, logs and run entry are retained under
`target/stabilization/write-diagnostic-20261025/frozen-d80b490/` and its parent.

Checks below ran on committed **d80b490**, not the historical runtime or a later
report-only HEAD. Existing assertions, coverage floors and ignored cases remain.

| Command/check | Actual result and scope |
| --- | --- |
| `./scripts/quality.ps1 -Mode Check` | Pass: fmt, workspace Clippy, 196 nextest passes / 0 failures / 4 skipped, doctests 0 cases, one fuzz-seed replay, PowerShell scorer/provenance checks, 180 Python regressions. Zero doctests is not additional validation. |
| `./scripts/quality.ps1 -Mode Coverage` | Pass at unchanged floors: lines 8,244/12,047 = 68.43%; functions 673/934 = 72.06%. Includes inline test code; not production-only or branch coverage. |
| `./scripts/quality.ps1 -Mode Dependencies` | Main/fuzz advisories, bans, licenses, sources and shared lock check pass; existing duplicate-version warnings remain. No dependency version/lockfile change. |
| `./scripts/quality.ps1 -Mode Fuzz -FuzzSeconds 30` | Existing targets finish without failure: JSON 84,792, lifetime 54,563, completion 44,536 iterations (31 seconds each). These are model smoke checks, not a native TDH fuzz campaign or throughput measurement. |
| `./scripts/quality.ps1 -Mode Bench` | Four synthetic cases, 20,000 work items per sample, seven measured samples each; all assertions pass. No paired baseline, live ETW or enabled-diagnostic cost claim. |
| `cargo test --manifest-path vendor/ferrisetw/Cargo.toml --lib diagnostic --locked` | 3 bounded payload/schema tests pass; 17 unrelated cases filtered. The vendored crate is outside the workspace coverage report. |

The four skipped tests remain three disk-backed storage experiments and the
historical-volume companion spool experiment, neither being modified here.
The vendored dependency still emits four existing compiler warnings; the workspace
Clippy result does not mean those warnings were eliminated. New core diagnostics
have eight regressions, including real database close/reopen/full/page/summary
readback without attribution promotion. The ThreadId width regression first failed
for a valid UInt32 declaration, then passed after diagnostic-only width selection.
Its red/green logs remain local. This is a diagnostic parsing fix, not a root-cause
fix for the extra Write. New analyzer/entry checks contribute 25/10 Python cases.

Affected coverage: `file_diagnostics.rs` 493/685 lines (71.97%), 36/59 functions
(61.02%); `decode.rs` 159/741 lines (21.46%). The actual callback-to-TDH `build`,
`admit` and `ignored` paths are not covered by these model tests. The real run now
provides actual schema/scalar evidence for all 14 complete Write payloads and
228 attached diagnostics; it does not cover every schema, error path or missing
model-test branch. No exclusion was added to hide that gap. The synthetic medians are 1,305.610 ns
per JSON event, 153.695 ns per lifecycle record, 253.285 ns per completion source
record, and 284.095 ns per registry context cycle; all samples stay in the private
quality archive. They do not measure this diagnostic's callback, queue or disk cost.

The frozen entry is one A/B/C/D capture, followed by bounded raw export
and offline analysis; its completed result is above. No full eight-arm rerun or formal 32-run protocol is started.
The runner records stages and prints a heartbeat during capture. On Ctrl+C,
timeout or forced window closure, preserve the whole new directory and fixture:
the capture child can remain running, and a forced close may leave only the last
checkpoint. Do not relaunch automatically. Missing context prevents a negative
"not reproduced" conclusion; an observed Write and its successful completion
remain visible separately, even when the overall evidence review is incomplete.

## Eight exploratory captures audited (2026-10-25)

**The main capture queues are not the observed failure point in these samples.**
All eight databases contain completed captures and balanced main persistence.
All eight retained scores still fail the unchanged unexpected-positive requirement;
candidate/on at 10,000 files additionally hits the quiet-drain deadline and the
raw companion's disk quota. These are three separate failures. There is one sample
per condition, no stability claim, and no formal 32-attempt acceptance execution.
Version remains **0.3.1**. This update changes the report only: no production,
scorer, protocol or workload changes, no new capture and no cleanup.

### Runtime identity and completeness

The report checkout and verified remote started at `6218ece1c9d5e4a5008a1ca6017fd82c21f39b98`.
The executed candidate remains **`545ed3f91740786cf0d9df7c0efce4fe7e0e0e4a`**, baseline
`e72d88c410f8ec69b8e17f7ccc193a6cb9c972d4`. The original workspace remains `baf8db3`
with its UI/preview files untouched. Report and harness commits are not runtime builds.

| Frozen input | SHA-256 |
| --- | --- |
| Baseline executable | `1f8a874cc14030d0afe0051f9acf462b961c309fb1e4370bf6ec139356b90ce2` |
| Candidate executable | `15c13e21747be8a6bc384aa6745de880f6e2f3c39205c7c6e0ab3b5ebf9fe296` |
| Common candidate fixture | `25200a95051e046f5a776b330d69a0365aabe71a14585ee24d6337ae9716ba0a` |
| Original eight-attempt plan | `516e8a74bebfb3ecec94d54ecf7a38dbc3ceaef625ed7c7ff309ae5c2b1e4d39` |
| Continuation plan, original indices 6–8 only | `dadd19e43126e7dd86d3ffd237ff9bf54f98c91e60b586ef3670ea65cb7e3cdc` |
| Unchanged syscall scorer | `aa20dc9b8103c6e79ed3d96b00f6b0219e76d9771f2749a58ffee79a56689ab7` |
| Unchanged clock protocol 4 | `57a8d30d1afdf56220f64a521d58faaed45b6edee09a69830fb3f4ff75d15f3f` |

Arms 1–5 used the `545ed3f` matrix (SHA `983e40d700b7b6fb33e790d7c59bd4d37a5020bc39934422c78ba2a723e18a99`).
Arms 6–8 used checkpoint/progress harness `bdce931` (SHA
`f86a2c2d307c1bf84e05a6475a2b9f3a5df0baf9787ba7a8a562b20bbf599d3c`).
Both use the same scorer/protocol bytes. Each build's 108 recorded runtime inputs
and all six script snapshots match their declared Git source blobs. Plan, entry,
launcher and parent-copy hashes match. Seven arms have matching recorded before/after
identities; arm 5 has only its recorded executable precheck, not a complete before/after
identity record. Current hashes are separate observations and do not fill that gap.

| Arm | Files | Runtime / companion | Original run status |
| --- | ---: | --- | --- |
| 1 | 1,000 | baseline / on | Capture and analysis completed; legacy and clock gates failed |
| 2 | 1,000 | candidate / on | Capture and analysis completed; legacy and clock gates failed |
| 3 | 1,000 | candidate / off | Capture and analysis completed; legacy failed, raw-clock validation unavailable |
| 4 | 1,000 | baseline / off | Capture and analysis completed; legacy failed, raw-clock validation unavailable |
| 5 | 10,000 | baseline / on | Capture/export/score completed; owner interrupted analysis; original final measurement and post-identity absent |
| 6 | 10,000 | candidate / on | Completed; legacy failed, quiet timeout and companion quota loss |
| 7 | 10,000 | candidate / off | Completed; legacy failed, raw-clock validation unavailable |
| 8 | 10,000 | baseline / off | Completed; legacy failed, raw-clock validation unavailable |

All databases have `capture_runs=finished`, session exit zero, matching committed
raw row counts and persistence counters. No WAL/SHM/journal sidecars were present;
read-only immutable `PRAGMA quick_check` returned `ok` for all eight. This checks
SQLite structure, not attribution correctness. Seven exported measurements agree
with stored stats/backend/quality. Arm 5's missing final measurement remains missing;
its separate offline clock replay and journal archive do not make it an uninterrupted run.
The original wrapper's incorrect registry-active check for file-only arms 1–4 and
the later output-directory collision are retained as harness failures, not ETW fallback.

### Every unmet gate, with denominators

Four workers and 100 independent noise files were fixed in every arm. The workload
is files only: file/process providers are active; registry is `not_requested`.
No failed/unsupported calls occur in this stress truth set. The small demo's failed
delete and NoiseOnly registry gap remain separate, unresolved requirements.

| Arm | Target success / observed / correct | Noise observed / expected | Wrong actor rows / all successful calls | Legacy Unknown / all successful calls | Unexpected positives / positive predictions |
| --- | --- | --- | --- | --- | --- |
| 1 | 2,500 / 2,500 / 2,500 | 300 / 300 | 0 / 2,800 | 300 / 2,800 | 500 / 3,000 |
| 2 | 2,500 / 2,500 / 2,500 | 300 / 300 | 0 / 2,800 | 300 / 2,800 | 500 / 3,000 |
| 3 | 2,500 / 2,500 / 2,500 | 300 / 300 | 0 / 2,800 | 300 / 2,800 | 500 / 3,000 |
| 4 | 2,500 / 2,500 / 2,500 | 300 / 300 | 0 / 2,800 | 300 / 2,800 | 500 / 3,000 |
| 5 | 25,000 / 25,000 / 25,000 | 300 / 300 | 0 / 25,300 | 300 / 25,300 | 5,000 / 30,000 |
| 6 | 25,000 / 25,000 / 25,000 | 300 / 300 | 0 / 25,300 | 300 / 25,300 | 5,000 / 30,000 |
| 7 | 25,000 / 25,000 / 25,000 | 300 / 300 | 0 / 25,300 | 300 / 25,300 | 5,000 / 30,000 |
| 8 | 25,000 / 25,000 / 25,000 | 300 / 300 | 0 / 25,300 | 300 / 25,300 | 5,000 / 30,000 |

These are **unchanged legacy scorer facts**, not raw-QPC-validated metrics. Target
observation and effective target attribution are 100% of the stated target-call
denominators. Noise identity is different: all 300 noise calls in each baseline
are `observed_but_unresolved`; each candidate identifies all 300 as
`correctly_not_assigned_to_target_app`. The top-level legacy Unknown bucket includes
both categories: 10.7143% at 1k and 1.1858% at 10k. Target Unknown is 0; final-state
ownership remains Unknown and is outside these syscall denominators.

| Requirement | Actual | Required | Result / scope |
| --- | --- | --- | --- |
| `no_false_attribution`, every arm | Wrong matched rows 0, but 500 (1k) / 5,000 (10k) unmatched supported High positives | Both counts 0 | Failed; precision 83.3333% of 3,000 / 30,000 positive predictions. Extras are not demonstrated wrong writers |
| `no_drain_timeout`, arm 6 | `drain_timed_out=true`, quiet wait 10,015 ms | false; stop at the unchanged 10,000-ms maximum | Failed; different from queue drain completion |
| Same requirement, other seven | false | false | Met, including arm 5's stored capture facts |
| Target observation / attribution | 1.0 / 1.0 | 1.0 at 1k; at least 0.95 at 10k | Met under legacy protocol |
| Target/noise denominator, active file ETW, pipeline/handoff balance, empty bounded queues, worker completion, zero context/decode/application/upstream loss | Recorded values satisfy each existing predicate | Unchanged `quality_gate` predicates in `scripts/v04-matrix.py` | Met; arm 5 assessment is an offline check of stored facts, not a fabricated original gate |

The recorded false-check sets are exactly `no_false_attribution` for 1–4, 7, 8;
arm 6 additionally has `no_drain_timeout`. Arm 5 never wrote its final gate: its
retained score independently violates the zero-unexpected-positive requirement.
No denominator or threshold has been changed and no sample has been discarded.

| Arm | Detailed clock result | Evidence requirement still unmet |
| --- | --- | --- |
| 1 | 2,500 unique interval candidates, 300 unavailable actor intervals | Initial noise identity and collector native provenance missing; provider timing unverified |
| 2 | `diagnostic_ready`, 2,800 unique strict interval candidates; native provenance matched | General provider timing contract unverified; readiness still `requirements_unmet` |
| 3 | 2,800 unavailable rows | Raw companion intentionally off; native provenance matched. Not a companion runtime failure |
| 4 | 2,800 unavailable rows | Raw companion off; collector provenance absent |
| 5 | Separate unchanged-protocol replay: 25,000 unique + 300 unavailable | Missing actor intervals/provenance; original diagnostic absent; provider timing unverified |
| 6 | 25,300 unavailable rows | Incomplete companion: 821,039 quota rejections; native provenance matched but cannot repair missing raw-clock proof |
| 7 | 25,300 unavailable rows | Raw companion intentionally off; native provenance matched |
| 8 | 25,300 unavailable rows | Raw companion off; collector provenance absent |

All original `clock_diagnostic_pass=false` values remain false. In unavailable
arms, a temporal match failure rate is **null**, not 100%; those comparisons could
not be validated. All arms still lack the general provider emission prerequisite.
Strictly validated operation metrics remain null. Numeric interval candidates,
verified writer instances, resource naming and final state are separate conclusions.

### Main pipeline versus raw companion

| Arm | Main received = persisted | Main / handoff queue record peaks | STOP + queue drain ms | Raw received / stored / quota rejected | Raw bytes |
| --- | ---: | --- | ---: | --- | ---: |
| 1 | 19,322 | 346 / 1,896 | 28 | 462,849 / 462,849 / 0 | 29,622,336 |
| 2 | 18,568 | 134 / 1,426 | 68 | 519,591 / 519,591 / 0 | 33,253,824 |
| 3 | 18,463 | 297 / 2,138 | 4 | null / null / null (off) | null |
| 4 | 18,437 | 219 / 1,492 | 5 | null / null / null (off) | null |
| 5 | 166,971 | 405 / 2,964 | 59 | 1,779,485 / 1,779,485 / 0 | 113,887,040 |
| 6 | 174,495 | 485 / 3,986 | 54 | 2,917,167 / 2,096,128 / 821,039 | 134,152,192 |
| 7 | 167,111 | 282 / 3,228 | 20 | null / null / null (off) | null |
| 8 | 167,215 | 259 / 3,755 | 43 | null / null / null (off) | null |

For **each of eight main captures**, upstream reported ETW events/buffers lost,
callback queue overflow/disconnection, handoff overflow/oversized/disconnection/
serialization failure, persistence failure and raw-evidence quota refusal are all
0. Both pending queues are 0; consumer stop is recorded; receiver and writer finished.
Queue record limits are 4,096 each. Arm 6's handoff reaches 97.31% of that limit
without dropping, so sustained-load headroom is not established. The provider-total
loss denominator is unavailable; no main/companion count difference is a loss rate.

For the four enabled companions, queue/writer-closed/not-persisted rejection
counters are 0, writer queue peak one chunk, producer stopped and writer drained.
Arm 6 reaches the fixed 128-MiB disk budget (64 KiB reserved for metadata), hence
2,096,128 accepted 64-byte rows and 821,039 quota rejections: **28.1451% of this
companion's 2,917,167 received callbacks**. `capacity_dropped` and `quota_dropped`
describe the same refusals, not two losses to add. Its binary SHA matches and
`complete=true` means the accepted file is complete, not that all received events
were saved. Reported writer memory is 9 MiB. Companion final consumer-loss counters
are unavailable; zero counters in its OpenTrace header are startup values only.

### Costs and what was not timed

Times below are seconds. Capture-parent wall includes its post-STOP processing;
the derived-evidence column is part of that time, not additional. The wrapper
remainder is its recorded UTC span minus capture-parent monotonic duration; it
covers setup, hash checks, export, scoring, clock analysis and archival, and could
also reflect wall-clock adjustment. It is **not** an isolated analysis measurement.
Offline export/score/clock durations
for every original arm are **null**, because those stages have no duration timer.

| Arm | Capture-parent wall | Stored internal capture | Derived evidence/paged persistence | Wrapper remainder | Parent CPU | Peak WS / sampled private bytes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 11.985 | 11.723 | 3.083 | 2.398 | 5.156 | 35,545,088 / 25,096,192 |
| 2 | 14.581 | 14.248 | 4.129 | 2.472 | 6.719 | 37,203,968 / 26,083,328 |
| 3 | 18.573 | 18.329 | 4.677 | 1.631 | 6.922 | 29,589,504 / 19,169,280 |
| 4 | 12.286 | 12.108 | 2.717 | 1.253 | 4.156 | 28,823,552 / 16,322,560 |
| 5 | null | 94.115 | 81.483 | null | null | null / null |
| 6 | 122.909 | 120.670 | 98.263 | 18.926 | 100.781 | 78,278,656 / 66,818,048 |
| 7 | 93.426 | 92.031 | 71.201 | 14.199 | 70.859 | 78,213,120 / 70,373,376 |
| 8 | 89.456 | 88.003 | 68.588 | 13.696 | 66.172 | 77,373,440 / 69,300,224 |

| Arm | DB bytes | Sampled WAL peak bytes | Parent logical read / write bytes |
| --- | ---: | ---: | --- |
| 1 | 134,426,624 | 43,330,072 | 947,282,055 / 1,135,887,109 |
| 2 | 134,987,776 | 41,752,112 | 960,117,759 / 1,152,892,148 |
| 3 | 125,820,928 | 61,087,272 | 914,963,399 / 1,095,802,649 |
| 4 | 119,267,328 | 61,008,992 | 867,145,693 / 1,033,023,397 |
| 5 | 1,220,132,864 | null | null / null |
| 6 | 1,274,957,824 | 375,748,152 | 19,744,039,076 / 21,112,769,184 |
| 7 | 1,153,626,112 | 416,054,112 | 18,683,696,223 / 20,082,363,751 |
| 8 | 1,106,087,936 | 416,396,072 | 17,690,160,029 / 19,143,482,767 |

CPU/memory/I/O are capture-parent only; physical disk I/O and descendant totals
remain null. The main serialized-raw quota does not bound all derived tables/indexes.
Separate companion CPU/callback duration is unavailable; its write timer covers
sink writes only and excludes hashing, so it cannot measure total diagnostic overhead.
For arms 5/6/7/8, grouped derived COMMIT (including automatic checkpoint) takes
38.663/43.460/32.761/32.632 seconds; derived write takes
32.892/37.475/26.730/25.240 seconds. Nested detail/edge timings must not be added
again. Post-STOP SQL, duplicate representations and transaction/checkpoint work
are the measured major cost; the checkpoint/fsync/index shares are not isolated.

The separately profiled recovery of arm 5 took 64.193 seconds, including 55.326
in clock analysis; it is a later offline replay, not the missing original duration.
Its profiling wrapper catches `SystemExit`, so exit zero does not override the
diagnostic's unavailable/unmet result. The continuation Python result was written
at **02:46:55 Asia/Shanghai**, but PowerShell finished at **03:07:11**. The
1,215.427-second recorded UTC gap follows final result creation. Missing monotonic
process-exit/output-drain timers prevent diagnosing the actual outer wait or excluding
a clock adjustment; it is not part of the recorded arm-8 cost.
All sample timings and different ambient volumes are retained. Candidate/off at
10k is 93.426 s versus baseline/off 89.456 s; candidate/on is 122.909 s with the
additional failures. One observation cannot isolate companion overhead or prove
a regression/improvement in stable throughput. Harness revisions also differ.

### Main failure, evidence and smallest next action

| Priority / issue | What existing evidence establishes | Minimal repair or validation action; not executed here |
| --- | --- | --- |
| 1. Candidate/off still fails: unmatched High writes and unresolved name provenance | Every one of 5,000 extra Write id16 events in arms 6–8 has the original write's FileObject, generation and FileKey. Rename/delete use different FileObjects of the same FileKey. Extra requests and unique successful same-IRP completions are inside the independent delete call by converted FILETIME, still named `.tmp`. Actor, resource and operation dimensions are all High | First isolate cross-object rename/name validity. Use open/write through A, rename through B, later event through A as the focused regression sequence; retain actor/request/completion and distinguish historical handle names from verified current paths. Do not predetermine that all extras should be renamed, downgraded or discarded |
| Evidence missing before selecting the implementation fix | `windows/etw/decode.rs` caches by FileObject and invalidates only the rename object's entry, leaving a possible cross-object stale-name path. Retained evidence does not establish whether these extra events used that cache: supplied/cached origin, original payload, I/O size, offset and flags were not retained. Neither a cache cause nor flush semantics is proven | One small controlled write-close/rename/delete fixture sequence retaining those fields and name provenance can distinguish provider semantics from stale context before changing production rules. Use original QPC and exact object/lifetime evidence; do not propagate names by FileKey alone. This is a proposed focused diagnostic, not another 1k/10k rerun or formal sample; no entry was launched or scheduled |
| 2. Companion disk quota | Arm 6 quota loss is balanced and explicit; queue peak one excludes observed writer-queue saturation. Background volume and capture duration differ | Audit a bounded original-event-key retention policy with both consumer arrival orders and required unrelated/context evidence before implementation. Preserve unmatched/over-limit counts. A larger quota alone is not a fix; no truth-based filtering |
| 3. Quiet wait hits deadline | All five final installer-family instances have end records. There are 4,573 later scoped writes with Unknown writer and header PID 4; receiver activity refreshes for file events. The exact decision-time quiet/descendant inputs were not logged | Add bounded stop-decision diagnostics to the focused investigation. Do not identify the writer from PID 4, claim OS writeback, ignore late writes, or lengthen the timeout to pass |
| 4. Derived persistence cost | Existing profiles locate most 10k wall time in paged derived writes/commits, not callback loss | Profile the already captured evidence on a separate database copy, isolating SQL, indexes and COMMIT/checkpoint with identical records. No production refactor justified by the aggregate timer alone |
| 5. Outer terminal delay | Final result predates wrapper completion by 20 minutes | Measure child exit and output-pipeline completion separately in the harness; no evidence presently proves console selection, ETW blocking or a deadlock |

The candidate's verified noise identity and native provenance are real improvements
over baseline in these samples. They do not repair resource-name semantics, make an
incomplete sidecar usable or satisfy the general provider timing contract. Existing
sources cannot settle the omitted payload/name-origin question offline. No old
score is reclassified and no final-state ownership or safe deletion is inferred.

### Audit reproduction and remaining gates

Private evidence root: `target/stabilization/evidence-load-20261025/`.
Original arms are in `exploratory-545ed3f-verified-eight`; continuation in
`continuation-6-8/results-original-6-7-8`; arm-5 recovery in
`interrupted-fifth-audit`. New read-only audit scripts, exact command lines, input
hashes, complete counters and supplements are in `eight-arm-audit-20261025/`:
`identity-completeness.json`, `database-quick-check.json`, `performance-pipeline.json`,
`clock-continuation-summary.json`, `clock-context-supplement.json` and
`eight-arm-aggregate.json`. The local `summarize-eight.py` cross-checks all eight
existing denominators and persistence/companion accounting without invoking the scorer.
Its output is exclusive-create; use a new audit destination for another run.

233 selected evidence files (8,255,310,639 bytes) had unchanged before/after SHA,
size and mtime. Additional read-only SQLite checks created no sidecars. All seven
completed measurement files, old scores, raw streams and interrupted records remain
unchanged. Full logs, host/process identities, paths and databases remain private;
only reviewed aggregate facts appear here.

Executed this update: read-only identity/blob/hash comparisons, eight SQLite
structural checks, existing-score/accounting invariants, and offline event-context
audits. No runtime build, Rust/Python product test suite, coverage, benchmark or
fuzz campaign was rerun for this documentation-only update. Historical checks:
exact candidate `545ed3f` passed 182 Rust tests (four skipped), 142 Python tests,
69.05% line / 73.19% function coverage, locked dependency checks and three short
fuzz runs; checkpoint harness `bdce931` passed Check with 182 Rust / 145 Python
tests. These are quality checks, not live acceptance or tests of a new fix.

Formal inputs are not frozen. The unexpected-positive/resource-context requirement,
quiet-timeout cause, bounded companion retention, provider emission contract and
small-demo registry/failed-call gaps still need resolution. The eight explorations
cannot replace the unchanged 32-attempt paired acceptance. No new capture is
scheduled by this audit, and the foundation for a safe removal loop is not established.


## Historical evidence gaps and exploratory scope (2026-10-24)


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
