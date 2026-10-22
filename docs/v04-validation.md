# v0.4 reproducible validation

The scripts in this document separate synthetic fixture validation, ETW acceptance,
CLI consistency and third-party installer validation. A passing fallback run does
not satisfy the ETW release gate. No vendor installer has been run in this work.

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
```

These tests cover workload denominators and unchanged thresholds, missing loss
counters, fallback versus ETW acceptance, failed-attempt retention, committed-WAL
seed copying, scoped publication, view conflicts/missing ancestry and null metric
semantics. They do not replace live Windows ETW, real installer or power-failure
validation. Do not bump the release version while those required gates are unmet.
