# Read-only workflow continuation

The task branch `fix/read-only-workflow` starts at
`600151d10d404f84eb9312784c280def1aeaf0ee`, including the `956febc` name-provenance
repair. Version remains 0.3.1. No private capture/database was available in this
Linux x86_64 environment; examples are deterministic **synthetic** inputs.

Default text inspection now identifies the session, installer, watched scope,
recording state, process birth identities, source/state counting domains and
limitations. It no longer calls the start time an installation success. Missing
measurements stay unavailable; unfinished evidence stays provisional. History and
verbose inspection expose event IDs for the existing `explain` command. Provider
names, historical cached names, execution actors and final ownership stay separate.
JSON fields/envelopes and database versions are unchanged. Text is streamed by
the CLI; examples use the same formatting functions.

`tools/offline` executes the production SQLite, model and renderer modules without
starting a provider or installer. Small shared path/counter modules preserve the
Windows APIs through re-exports. The optional Win32 disk-reserve experiment errors
explicitly on Linux; its five Windows tests are marked ignored there. The normal
storage, migration, rollback, child-process recovery, pagination, legacy-name and
explanation tests execute. This is not a Linux port of capture or a replacement
implementation of persistence.

## Reproduce synthetic examples on Linux

From the repository root with the existing toolchain activated:

```bash
cargo test -p contain-offline --locked --offline --target x86_64-unknown-linux-gnu
cargo clippy -p contain-offline --all-targets --locked --offline --target x86_64-unknown-linux-gnu -- -D warnings
cargo run -p contain-offline --bin read-only-examples --locked --offline --target x86_64-unknown-linux-gnu -- target/read-only-examples-new
```

The output directory must be new. It retains three databases, JSON inspect
documents and text from actual save/reopen/summary/full/history/explain paths:
finished, historical-name/unknown-actor, and interrupted-before-finalization.
Text explicitly labels provider states as synthetic inputs. Generated source
records are not a reconstruction of any historic ETW run. The published text
examples contain only these synthetic paths and identities; databases remain
ignored local artifacts.

Generated text: [finished](examples/read-only-finished.txt),
[historical/unknown](examples/read-only-historical.txt), and
[interrupted](examples/read-only-interrupted.txt). Each includes default and
verbose inspect, history and focused explain from the production formatter.

## Development checks

The integrated first-stage tree was tested with Rust 1.99.1 on Linux x86_64:
61 native Rust tests passed, 0 failed, 8 ignored. Five ignores require Windows
disk-reserve APIs and three are explicit existing performance experiments; binary
targets and doctests each ran zero tests and are not counted as validation.
All 18 Python regression scripts total 243 passing tests, with zero skips. The
first run had three errors in the diagnostic test's Windows API mocks; the test
fixture was corrected and all 17 tests in that script rerun successfully. The
production diagnostic was not changed. A new workflow test initially expected
`capturing` after raw drain; the actual phase is `finalizing`, and its exact
phase assertion was corrected before the passing run. All attempts remain local.

Formatting, host Clippy, Windows-target workspace Clippy and compilation of the
Windows test executables are checked separately. Four existing warnings from
the vendored ferrisetw dependency remain. The Cargo lock change adds only the
local offline package; no external dependency version changed. The shared fuzz
dependency lock comparison passes. Full `quality.ps1` Check/Coverage/Dependencies,
nextest, Windows doctests/fuzz seeds and the real ETW fixtures are not claimed
executed: this machine lacks Windows execution and PowerShell. Native scoped
coverage is supplementary and does not replace the unchanged Windows floors.

Compiler-instrumented scoped coverage ran the same 61 tests with source hashes
unchanged during measurement. Including inline test code: renderer 696/751 lines
(92.68%) and 54/66 functions (81.82%); storage.rs 438/456 lines (96.05%) and
26/30 functions (86.67%); shared path helpers 79/79 lines and 8/8 functions.
Production formatters ran; the actual stdout adapter/wrappers and legacy
diagnostic-provenance fallback were uncovered. These figures are not whole-project,
production-only or branch coverage. Raw coverage and all failed attempts remain
under `target/quality/read-only-coverage`; no floor or assertion was relaxed.

## Minimal Windows handoff

Use the exact task-branch commit listed with the validation results below. Required
tools: stable Rust with Visual Studio C++ tools, PowerShell 7, Python, and the
already documented quality tools. These synthetic checks require ordinary user
permissions, no administrator token or ETW trace session. From the repository root:

```powershell
./scripts/quality.ps1 -Mode Check
./scripts/quality.ps1 -Mode Coverage
./scripts/quality.ps1 -Mode CoverageOriginal
./scripts/quality.ps1 -Mode Dependencies
./scripts/quality.ps1 -Mode Companion
cargo build --workspace --locked
$out = 'target/read-only-windows-new'
cargo run -p contain-offline --bin read-only-examples --locked -- $out
if ($LASTEXITCODE -ne 0) { throw 'Synthetic generation failed' }
python scripts/verify-read-only-cli.py --contain target/debug/contain.exe --fixtures $out --output target/quality/read-only-cli-new --public-output target/quality-public/read-only-cli-new
if ($LASTEXITCODE -ne 0) { throw 'Synthetic CLI validation failed; preserve all local output' }
$documents = @(Get-ChildItem target/quality/read-only-cli-new/json -Filter '*.json')
if ($documents.Count -ne 33) { throw 'Expected 33 CLI JSON documents' }
foreach ($document in $documents) {
    if (-not (Get-Content -Raw $document.FullName | Test-Json -SchemaFile docs/schema/cli-v3.schema.json)) {
        throw 'CLI JSON schema validation failed'
    }
}
python scripts/quality-summary.py --root . --quality target/quality --output target/quality-public/summary.json
```

Expect finished/Degraded sessions to retain source actor evidence with Unknown
state ownership; historical names keep their original provenance; the interrupted
session stays finalizing/Incomplete with provisional source evidence. Each view
checker compares all three events including two-record pagination and focused
explanations. Its `acceptance_pass` is **view agreement only**, not ETW acceptance.
Use a new output name for retries and preserve failed JSON/logs/databases locally.
The quality script already archives prior attempts. Do not upload private data.

Windows-native execution, its full quality/coverage gates and real ETW remain
unexecuted here. Cross-target compilation and Linux model tests do not establish
those results. No 32-run acceptance, third-party installer, elevated diagnostic,
real cleanup, uninstall or rollback is part of this handoff.

The existing Windows CI now invokes these quality modes and the same native CLI
validator, followed by the two existing ETW fixture runs and snapshot fallback.
`Coverage` retains the workspace scope and the 66.5% line / 70% function floors.
`CoverageOriginal` independently applies those same floors to the original three
packages, with a separate instrumentation directory. The offline package imports
production source and repeats shared tests; neither execution totals nor LLVM
source aliases represent additional unique tests. Reports retain both original
coverage denominators and list aliases without adding any production-code
exclusions. The LLVM tool's existing defaults omit tests/examples/benches paths;
inline tests remain included. Selecting the installer package for execution does
not put its examples/ source into the coverage denominator, and workspace coverage
does not establish coverage of vendored ferrisetw internals.

The native CLI validator runs 66 commands across the three synthetic databases,
including list, inspect, verbose inspect, diff, paged history, every explanation,
and a final reopen. CI checks all 33 JSON documents against the existing schema.
Public artifacts contain only text exactly matching the reviewed examples and
fixed-field summaries; databases and raw CLI/quality logs stay local. Every quality
invocation records its SHA, tools, commands, exit statuses and archived logs under
`target/quality/runs`; failed attempts are retained. The summary reports test
execution groups and unavailable counts explicitly. Exact Windows results, including failures, are recorded below.

## Offline persistence measurement (synthetic)

Baseline `8f3ab7c7818e3607279cc276290c72c85027034a` and candidate
`0b97805ccac20f0524a0471f39f9a5fb9dfc9a12` were built with the same Rust 1.99.1,
locked dependencies, Linux x86_64 release profile and bundled SQLite 3.50.2.
Five state/process INSERT statements now reuse their prepared forms within the
existing transaction. SQL, row associations, evidence copies, quota and durability
policy are unchanged. A late registry-detail failure regression checks complete
rollback, retry with cached statements, reopened values and foreign-key integrity.

The fixed-seed paired workload has 10,000 source events, 10,000 file states,
10,000 registry states, 10,000 operations, four process instances and 18,575 edges.
Seven pairs alternate baseline/candidate order. Every full input, summary, page,
list and reopened view is checked; three explanations per sample also compare
the exact event, operation and ancestry/resource graph. Input/semantic hashes and
counts match across every pair. All 14 paired samples and six preliminary samples
passed. Every sample, including the slower preliminary run, is retained in the
[reviewed aggregate](examples/read-only-storage-comparison.json).

| Pair | Order | Baseline save (ms) | Candidate save (ms) |
|---|---|---:|---:|
| 1 | baseline / candidate | 596.355 | 463.169 |
| 2 | candidate / baseline | 552.928 | 512.488 |
| 3 | baseline / candidate | 569.062 | 453.917 |
| 4 | candidate / baseline | 558.110 | 468.519 |
| 5 | baseline / candidate | 564.532 | 439.797 |
| 6 | candidate / baseline | 543.874 | 458.024 |
| 7 | baseline / candidate | 598.132 | 441.060 |

Save median: **564.532 → 458.024 ms (18.87% lower)**. This is an offline
synthetic save measurement, not live Windows ETW throughput or installer speed.
Full-query medians were 129.289 / 126.113 ms and summary medians 59.647 / 58.726 ms;
no query optimization is claimed. The same input produced 56,926,208-byte DBs and
57,523,472-byte WAL files immediately after save in every paired sample. These are
file-size observations, not sampled peaks. CPU, peak RSS and I/O counters were
unavailable and remain null. Shared-host scheduling and caches remain limitations.

Reproduce at each exact source revision, retaining both built executables before
alternating runs; do not run builds or coverage concurrently with measurement:

```bash
cargo build -p contain-offline --bin storage-bench --release --locked --offline --target x86_64-unknown-linux-gnu
# Run a frozen candidate binary built from the exact revision below:
./target/x86_64-unknown-linux-gnu/release/storage-bench --revision 0b97805ccac20f0524a0471f39f9a5fb9dfc9a12 --records 10000 --samples 1 --output target/storage-candidate-new
```

After comparison, all three pre-existing ignored storage experiments were run
individually in release mode: state readback scaling, multi-session readback and
16,384-record synthetic raw replay; each passed one test with zero failures.
The replay retained all 16,384 records, zero write/quota drops and 64 batches.
Its single write/postprocessing timings (78.631 / 889.094 ms) are diagnostics, not
a before/after comparison. The normal final native suite has 62 passed, 0 failed
and 8 ignored; the three separately executed experiments are not silently counted
as ordinary suite passes. Five Win32-specific checkpoint experiments remain
unexecuted locally. Commands and all local logs are under target/quality/storage-comparison.

Final scoped instrumented run at `0b97805` also passed 62 tests with eight ignores;
source hashes stayed unchanged. Storage coverage, including inline tests, is
562/580 lines (96.90%) and 30/34 functions (88.24%); `Storage::save` ran 48 times.
Other begin/prepare/commit/metadata-update error returns remain unexercised.
Renderer/path coverage is unchanged. The unrelated companion path repair was not
compiled into either offline benchmark executable or counted as host coverage.

## Windows CI follow-up

The ordinary push of `8f3ab7c` triggered [CI run 41023074320](https://github.com/LuckyLivio/Contain/actions/runs/41023074320).
Formatting and Clippy passed; 11 CLI renderer tests passed, then core reported
186 passed, one failed, four ignored. The existing sidecar same-directory rejection
assertion failed; remaining workspace targets and ETW/fallback steps were not run.
Its destination parent was canonicalized while watched roots were only normalized
lexically, so different aliases could miss the same directory. This asymmetry
already exists at `600151d`; the log does not establish which alias occurred.
The follow-up resolves both sides identically, fails closed on an unresolved root,
keeps the old assertion and adds an existing child/.. alias plus noncreation checks.
It does not change event-name normalization or attribution. This Windows-only
function cannot execute in the Linux harness; its native result is tracked below.

### Verified code and handoff

The code commit is **`be3b385149eaaed106b04965abf8f218dc8d9423`**, on
**`fix/read-only-workflow`**, synchronized and verified against origin. Subsequent
handoff-only commits do not change that code. The final local run at this commit
passed 62 host tests (0 failed, 8 ignored), Windows-target workspace build,
test-executable compilation, Clippy and formatting.

The ordinary push triggered [Windows CI 41024587029](https://github.com/LuckyLivio/Contain/actions/runs/41024587029),
which completed successfully at that exact code commit with Rust 1.99.1 MSVC.
`cargo test --workspace` actually executed:

| Target | Passed | Failed | Ignored |
|---|---:|---:|---:|
| CLI renderer | 11 | 0 | 0 |
| contain-core | 188 | 0 | 4 |
| contain-offline | 66 | 0 | 3 |
| registry-noise fixture | 5 | 0 | 0 |
| test-installer fixture | 7 | 0 | 0 |
| write-diagnostic fixture | 9 | 0 | 0 |

The offline package re-executes shared production tests; these rows are execution
counts, not independent or unique coverage. Two helper binaries and both doctest
targets had zero tests and are not evidence. The sidecar original/alias rejection
test and all five platform-specific checkpoint tests passed natively. Existing
explicit performance/spool experiments remain ignored by the normal Windows suite.
The ten Python scripts selected by this CI ran 79 tests successfully, a subset of
the 18-script/243-test local run. The PowerShell scorer gate also passed; it does
not emit a unit-test count.

Both existing `demo.ps1 -RequireEtw` runs and the separate `-SnapshotOnly` run
passed, as did fixture-only artifact publication. This is native small-fixture
validation; no 32-run acceptance was started, and no release or safe-removal claim
follows. CI is not the complete `quality.ps1` nextest/coverage/dependency/fuzz
workflow; those remaining Windows gates and coverage floors are still unverified.

Continue from this branch, preserving the same evidence boundaries. The minimal
Windows commands above exercise the new synthetic records through the complete
CLI and the existing view verifier. No private historical database is required.

## Windows closeout evidence

The closeout branch `fix/windows-read-only-validation` starts from the actual
`fa8ed67229c0ed55a9d60b8d865d0a86baf3fee9` tip. The new quality/CLI gates were
first pushed as `448331bf983957155915d08cbeac0c74a0273cc3` for
[Windows CI 41029089684](https://github.com/LuckyLivio/Contain/actions/runs/41029089684).
Linux script regressions on that tree passed: CLI validator 15, quality summary
14, existing view verifier 6; zero failures/skips. These are script checks, not
Windows CLI results.

Checking the actual later CI also found a failure that the earlier `be3b385`
success does not supersede.
[CI 41025410010](https://github.com/LuckyLivio/Contain/actions/runs/41025410010)
on `fa8ed67` failed its first ETW fixture with five unmatched positive events.
The [reviewed aggregate](examples/windows-etw-failed-41025410010.json) preserves
the original failure and its limits. Four registry events were 8.5–10.3 µs and
one file write was 232.1 µs after the end of a same-actor/resource/category truth
candidate. Such a candidate does not establish the same syscall or explicit
application operation. The unchanged scorer reproduces the original result from
the published fixture projection. The event timestamps equal their raw FILETIME
headers; original QPC and independent truth clocks are absent. No safe product
fix can be inferred from that projection, and its failure remains unresolved.
There was no production/fixture/scorer change between `be3b385` and `fa8ed67`.
A later green run cannot erase this failure; the exact-interval scorer is unchanged.

No formal 32-run, scaled-load or real third-party installer acceptance has been
performed in this closeout. Version remains 0.3.1; these checks do not establish
overall stabilization, safe uninstall or release acceptance.

The first closeout CI completed with failures, retained without retry. It ran on
Windows Server 2025 build 26100 (runner image 20261021.278.1), Rust/Cargo 1.99.1,
nextest 0.9.149, llvm-cov 0.9.1, deny 0.20.2, Python 3.12.10 and PowerShell 7.6.7.
Its quality summary records the complete commands and source-bound results.

| Check at `448331b` | Actual result |
|---|---|
| Check: fmt/Clippy/nextest | Passed; nextest 286 passed, 0 failed, 7 skipped |
| Doctests | 0 executed; not counted as test coverage |
| Fuzz fmt/Clippy and seeds | Passed; 1 seed test, 0 failed/ignored |
| PowerShell scorer / provenance | Scorer passed; provenance failed; Python gates not reached |
| Workspace Coverage | 286 passed, 0 failed, 7 ignored; lines 13033/17567 (74.1902%), functions 1045/1387 (75.3425%) |
| Original-package Coverage | 220 passed, 0 failed, 4 ignored; lines 9460/13112 (72.1477%), functions 755/1016 (74.3110%) |
| Dependencies (main and fuzz) | All five gates passed |
| Bounded companion spool | 1 passed, 0 failed/ignored; 191 filtered |
| Actual synthetic Windows CLI | 3 scenarios, 66 commands and 33 schema checks passed; 0 failed/skipped |
| Two ETW fixture runs | Both failed: each 5 unmatched positive events; target observation/attribution 12/17 |
| Snapshot fallback | Passed, independently of the ETW failures |

The provenance failure compared a raw custom artifact path containing a Windows
TEMP short name against the production function's `GetFullPath` result. .NET
expands that short name, so the test incorrectly rejected the same executable.
The follow-up changes only the test's expected paths to canonical full paths,
retains case-sensitive equality and all rejection checks, and adds an explicit
absolute path with `..` regression. Production artifact selection is unchanged.
The original failure remains above; the follow-up Check is tracked separately
on this branch, with its exact SHA in the CI quality artifact.

Windows-specific coverage was exercised, not skipped: core and offline each ran
the four checkpoint behavior regressions (plus their child-process helper), and
core ran native thread query, exit-race and thread snapshot checks. The original
package report records `available_disk_bytes` 18 executions and 21/22 code regions;
its API-error return remains uncovered. Shared tests are duplicate executions,
not new unique cases. Original-package affected-file line coverage: renderer
678/751, storage 562/580, path helpers 79/79, checkpoint 186/207, companion 416/687.
The stdout adapter was not covered by instrumented unit tests; the later actual
CLI commands execute it without instrumentation and do not increase these figures.

The remaining ETW blocker cannot safely be fixed by changing the interval scorer
or deleting unmatched records. If a local diagnostic is needed, the smallest
existing entrypoint is one fixture run with both clocks, using native Windows and
already available ETW permissions; this command was **not executed** in closeout:

```powershell
$out = Join-Path 'target/quality' ('etw-window-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
$oldFixtureClock = $env:CONTAIN_FIXTURE_CLOCK
$oldRawClock = $env:CONTAIN_ETW_RAW_CLOCK_PATH
try {
    $env:CONTAIN_FIXTURE_CLOCK = '1'
    $env:CONTAIN_ETW_RAW_CLOCK_PATH = [IO.Path]::GetFullPath((Join-Path $out 'raw-clock.jsonl'))
    ./scripts/demo.ps1 -RequireEtw -KeepArtifacts -OutputDirectory $out
} finally {
    $env:CONTAIN_FIXTURE_CLOCK = $oldFixtureClock
    $env:CONTAIN_ETW_RAW_CLOCK_PATH = $oldRawClock
}
```

Preserve the failed original score, `clock-diagnostic.json`, `run-identity.json`
and the local database/truth locations reported by `-KeepArtifacts`. Original QPC
and independent truth-clock coverage are prerequisites for distinguishing the
remaining causes; a missing clock makes that comparison unavailable. Keep all raw
clocks, logs and databases local. This is not a formal acceptance or a request to
repeat a full system diagnostic, elevate privileges or stop another trace session.


## Bounded prebuilt trial contract

The `fix/windows-read-only-candidate` branch retains 0.3.1 and packages the exact
MSVC debug binaries tested by CI; debug is a functional diagnostic build, not a
release performance result. `BUILD.json` fixes source, build configuration, PE
imports, dependencies and file hashes. Ordinary CLI use needs Windows x64;
the existing controlled demo needs PowerShell 7 and Python 3, but neither Rust
nor Visual Studio. `scripts/demo.ps1` accepts `-ContainExecutable`,
`-FixtureExecutable`, `-BuildManifest`, or automatically uses the extracted
package's `BUILD.json`. It preserves prebuilt fixture/database output.

Only a push explicitly marked `[trial-pair]` schedules native candidate work.
Check, Coverage, original-package Coverage, Dependencies, companion regression,
workspace build and all three existing synthetic CLI scenarios must pass first.
One frozen package then runs once with optional instrumentation disabled and
once with both independent fixture and raw ETW clocks enabled, in separate new
absolute output directories outside the watched scope/database. These are two
configurations, not repeated stability evidence. The existing fallback remains
available on non-pair CI; it is not an extra capture in this two-attempt trial.

The result contract separates diagnostic execution, supplied-view clock inputs
(and a separate raw/final join check), bounded product checks and formal
`requirements_unmet`. A diagnostic failure no longer bypasses independent
product assertions or offline CLI readback. Missing inputs are `not_run` or
incomplete, never passed. Raw-only registry source records need the production
selection witness described in the architecture; absent final rows alone are
not evidence of legitimate filtering. No actor is inferred from fixture truth.

The trial ZIP includes actual CLI excerpts when export succeeds, and otherwise
retains the export failure alongside per-check validation status;
failed required checks produce an explicitly unverified diagnostic package and
a failing CI, not a verified candidate. Full SQLite databases, ETW sidecars and
machine inventories are excluded. Formal 32-run, scale, third-party installers,
final ownership and safe cleanup remain separate unexecuted follow-up scopes.
Historical six unmatched events without original QPC remain D.


The first bounded trial CI, [41061301488](https://github.com/LuckyLivio/Contain/actions/runs/41061301488)
at `f8e7e84ce1e280deb66366ff75fb49eb5bc95f08`, is retained as a failed attempt.
`Quality Check` reached the package regression suite: 18 ran, 16 passed and two
failed. Windows drive-less rooted paths were not rejected by `Path.is_absolute()`;
the report test also compared a TEMP short path with the selected executable's
resolved long path. The fix rejects rooted paths explicitly and normalizes only
the test's expected selected path, preserving strict executable equality.
Fourteen subsequent Python suites were not executed after this gate failure.

On that same SHA, nextest passed 290 with zero failures and seven skips; all four
PowerShell regression gates passed. Coverage, original-package Coverage,
Dependencies, Companion, workspace build and the existing three synthetic CLI
scenarios passed. Workspace line/function coverage was 13296/17833 (74.5584%) /
1069/1411 (75.7619%); original packages were 9590/13244 (72.4101%) /
767/1028 (74.6109%). These include inline tests; shared offline test executions
are duplicated. The unchanged floors remain 66.5% lines and 70% functions.
The package freeze and both native scenarios were **not executed**, so this
failure consumed zero of the two planned captures. There was no candidate ZIP
from this attempt. The original CI and its quality artifact preserve the failure;
a follow-up source revision must pass its own checks before any native trial.

The affected `select_raw_only_registry` function's executed original-package
instance ran seven times and covered 44/47 code regions; its row-decoding
closure ran three times (2/2 regions). Workspace adds the same shared test via
the offline package, not independent native coverage. Uncovered regions are the
non-SetValue registry early return, missing-raw query error propagation and
witness serialization error propagation. Zero-execution duplicate instances
remain in the artifact. Region coverage is not branch coverage, and these tests
do not establish every real SQLite I/O failure behavior.


Before any collection, a static dependency review found two Windows system DLLs
missing from the packaging allowlist: `tdh.dll` (locked ferrisetw/windows TDH
bindings) and `pdh.dll` (locked sysinfo/windows performance bindings). Both also
appear in the existing GNU Windows PE imports; that binary is supporting static
evidence, not the delivered MSVC build or a native test. A new synthetic PE
regression first failed, then passed after adding exactly those system DLLs.
Unknown DLLs and Debug VC runtime imports remain rejected; system DLLs are not
copied into the package. Package regressions now contain 20 tests.
The preparation-only CI [41065051721](https://github.com/LuckyLivio/Contain/actions/runs/41065051721)
at `e14ac36594fcf9fc6876077b8b0b0ebb337c24d5` was cancelled during quality-tool
preparation for this concrete fix, before tests or native captures. Its cancelled
result remains recorded; it is not a pass or an ETW attempt. The final MSVC
imports and runtime loading still require the frozen candidate CI.


A final public-artifact review found that default-mode scoring was private-only:
its check name survived, but its original counts and extra-positive event IDs
would disappear with the runner. CI [41066223519](https://github.com/LuckyLivio/Contain/actions/runs/41066223519)
at `818d6a548096708aad84e68087dddec92bcc65fa` was therefore cancelled during
quality-tool preparation, again before tests or collection. The correction
preserves each configuration's original score as a reviewed fixed-field
projection, independent of CLI report success, without publishing paths, process
identifiers or arbitrary error text. The score is not recomputed or relaxed;
missing original scoring remains unavailable and cannot satisfy the candidate.
Both preparation-only cancellations remain distinct from the initial quality
failure and from the still-unconsumed pair of native attempts.


## Bounded trial result

The actual deliverable is a **diagnostic build, not a verified trial candidate**:
[download the CI artifact](https://github.com/LuckyLivio/Contain/actions/runs/41067439514/artifacts/12984204005)
and extract its `contain-0.3.1-windows-x64.zip`. Source is
`e84f9b9d5c35ed62a0b2ecf06aee7addc9a31fc7`, MSVC x64 debug, version 0.3.1.
`contain.exe` SHA-256 is
`fb1a5aebce7402114b15af342800d1721666a3540f1db00dde7d47e1a95823cf`;
ZIP SHA-256 is `c4e70f07042780f6daf1fac8e7264dd703172ddb02e35e7278e2f8a19705b1f8`.
The downloaded ZIP was read back and all 23 manifest-listed file hashes/lengths
verified (24 archive members including CONTENTS.json). Actual PE dependencies
were inspected; the required `vcruntime140.dll` is included. Both native runs used
these exact binaries/scripts. No Release, tag or version change was made.

Windows Server 2025 build 26100, AMD64, image `win25-vs2026` / 20261021.278.1;
Rust/Cargo 1.99.1, nextest 0.9.149, llvm-cov 0.9.1, deny 0.20.2,
PowerShell 7.6.7 and Python 3.12.10 produced the following results in
[CI 41067439514](https://github.com/LuckyLivio/Contain/actions/runs/41067439514).
The [reviewed machine-readable summary](examples/windows-read-only-trial-41067439514.json)
retains each product check, original scores, the raw-only selection and one event
comparison. It is a diagnostic projection, **not fabricated CLI explain output**.

| Gate / exact entrypoint | Result |
| --- | --- |
| `./scripts/quality.ps1 -Mode Check` | Passed: nextest 290 passed / 0 failed / 7 skipped; 22 Python suites, 330 passed / 0 failed / 0 skipped; four PowerShell gates passed (demo 10 tests, wrapper 18 assertions). Doctests executed 0 and are not counted as coverage. |
| `-Mode Coverage` | 290 passed / 0 failed / 7 ignored; lines 13297/17833 (74.5640%), functions 1069/1411 (75.7619%). |
| `-Mode CoverageOriginal` | 222 passed / 0 failed / 4 ignored; lines 9590/13244 (72.4101%), functions 767/1028 (74.6109%). Shared offline tests remain duplicate executions, with inline tests included. Floors remain 66.5% / 70%. |
| `-Mode Dependencies`, `-Mode Companion` | Passed; main/fuzz dependency gates retained; companion 1 passed / 0 failed / 0 ignored, 193 filtered. |
| `cargo build --workspace --locked`; synthetic CLI + schema | Passed; three existing synthetic scenarios, 66 commands and 33 schema checks passed, 0 failed/skipped. |
| `ci-etw-clock.ps1 -Phase candidate -Configuration default -AttemptIndex 1` with frozen package and new absolute directories | Real ETW; 35 required product checks passed / 0 failed / 0 not-run. Clocks not requested. |
| Same entrypoint, `-Configuration dual-clock -AttemptIndex 2` | Real ETW; 35 required product checks and one diagnostic passed. Original clocks and raw/final connection evidence complete in the supported source-operation scope. |
| Actual CLI text report export, each configuration | **Blocked**: `not_fixed_fixture_scope`; 0 report CLI commands executed. Existing demo readback/explain checks above did execute. |
| Package finalization / overall CI | ZIP generated as `diagnostic_build_unverified`; finalization exit 2 and overall CI failed. No additional capture or retry. Snapshot fallback was not run in this pair. |

Each original scorer observed 19/21 instrumented operations: all 17 target
successes attributed correctly, two independent-writer operations observed and
not attributed to the target, one independent operation unobserved, and one
failed deletion unobserved. Both runs retained four Unknown outcomes, zero wrong
attributions and zero extra positive source events. Provider and application
loss counters were measured as zero; that does not establish complete coverage.
The dual-clock audit linked 32 original QPC records at 10,000,000 Hz. One registry
raw-only Unknown has its positive production selection witness; the unresolved
source remains explicit. No truth was used to fill its actor.

The blocker is the **report validator**, not the passed product checks. The demo
stores a TEMP-derived fixture root, while capture stores its canonical spelling.
The exporter only removes Windows prefixes and folds case; it cannot recognize
valid 8.3 short-name/long-name aliases. Both runs failed that precondition before
snapshot/CLI export. The private input spellings and databases were not published
and the runner has ended, so the alias defect is a source-confirmed limitation
and likely trigger, not a reconstructed comparison of this run's private paths.
The subsequent event/diff selection also uses the original spelling. The next
minimal fix is a real Windows same-directory identity check, then using the
canonical captured root for selection/redaction while retaining its observed
aliases. Regressions must reject different/missing directories and different
fixture GUIDs. Do not guess 8.3 mappings or weaken source attribution. The missing
real CLI text report cannot be recreated from the safe aggregates; the synthetic
examples remain explicitly synthetic.

The public console also exposed runner boot identity/clock metadata because
`Out-Host` bypassed the wrapper's private redirection. A post-capture fix removes
that bypass and adds a real PowerShell child-process redirection regression:
first 10 passed / 1 failed, then 11 passed / 0 failed / 0 ignored on Linux
PowerShell 7.6.9. Failure recording and independent checks remain intact. This
fix is **not in the frozen downloadable package and has not run native Windows
capture**. The redirect fix is commit `149a5e3f4224ff5eedb947acb11cedd83be13d23`.
Raw console logs were saved privately and removed from GitHub after the reviewed
replacement summary was pushed; the log API now returns 404. The failed run
status and all three artifacts remain available. This removes exposed machine
identifiers while preserving the original failures and measured results.
Package/local demo output should not be uploaded unreviewed.

From the extracted package, with Windows x64, PowerShell 7 and Python 3, use:

```powershell
pwsh -NoProfile -File .\scripts\demo.ps1 -RequireEtw -KeepArtifacts -OutputDirectory "$PWD\demo-result-new"
.\contain.exe --db <printed-database-path> inspect TestFixture
.\contain.exe --db <printed-database-path> history TestFixture --limit 20 --offset 0
.\contain.exe --db <printed-database-path> explain <event-id-from-history> --json
```

The ordinary CLI needs neither Rust nor Visual Studio; the demo needs existing
ETW permissions and never elevates. Keep the printed database and outputs after
failure. Event IDs from CI belong to its private session and are not IDs in a new
user capture. Formal 32-run, scale, third-party installers, final ownership and
safe cleanup remain separate unexecuted scopes. Historical six timing failures
remain D. No further collection is authorized or scheduled by this closeout.

## Report export update

This bounded delivery used branch `fix/windows-trial-report`, frozen source
`08604b476dfcdb5bfd7fa93d157bc11467bb858a`. Report export is versioned as
`contain-trial-report-2`; `BUILD.json` binds the exporter hash as well as the
packaged binaries and scripts. The existing `149a5e3` stream-redirection fix is
included. This update does not change capture, attribution, storage optimization,
the CLI/JSON contracts, version 0.3.1, or the formal acceptance requirements.

The exporter checks the live directory's filesystem identity, canonical fixture
GUID, marker and recorded run identity, then shares the verified root spellings
across event selection, diff filtering and redaction. Observed aliases must have
the same directory identity. Missing/different directories, wrong GUID/marker,
unverifiable identity, neighbouring prefixes and ambiguous dot paths are rejected.
Historical or deleted child files need not exist today; directory identity does
not establish event-resource identity or final ownership.

Windows CI requires both native filesystem regressions and a complete synthetic
report preflight before either native capture. The latter writes through the
existing production RawBuffer/Storage path and executes the frozen `contain.exe`:
inspect JSON, one-event history JSON, explain JSON, inspect text, diff text,
the same history page and the same event's explain text. All seven commands,
four report sections, Unknown/historical-name boundaries, path redaction and
the production package collector must pass. Normal and extended spellings are
mandatory; 8.3 coverage uses only an actual `GetShortPathNameW` result and is
explicitly unavailable when the filesystem supplies none. Synthetic results
cannot satisfy either real-report acceptance gate.

The fixed plan remains one default capture and one dual-clock capture, using the
same frozen package and separate new directories, without retries. Report failure
retains its original result and performs one independent read-only investigation
of that same database before the runner ends. Raw CLI/diagnostic output stays
private; public evidence contains approved excerpts, status codes and hashes.
An independent diagnosis cannot promote a failed report or candidate.

Local regression evidence is separate from Windows execution. The old exporter
at `0df6612` failed a new real-Linux-directory alias test with
`not_fixed_fixture_scope`, before any report CLI command. Its command responses
were synthetic mocks: this is neither a Windows filesystem result nor a replay
of either unavailable historical private database. During the new synthetic
generator extraction, two tests detected duplicate final-state rows; the
intermediate change was corrected and exact row-count assertions retained.
Final Linux results were 338 Python tests passed, 65 offline Rust tests passed
with 8 ignored, 11 demo regression tests and 36 wrapper assertions passed.
Windows filesystem tests were 0 passed / 7 skipped on Linux, and the full report
preflight refused execution with 0 CLI commands / 14 not run. These omissions
are not Windows passes. Original local failure logs remain under `target/quality`;
no private path or database is published.

### Frozen Windows result: report still blocked

[CI 41078101988](https://github.com/LuckyLivio/Contain/actions/runs/41078101988)
completed with **failure**. Both preflights passed before the two planned captures;
no capture was retried. The updated [Windows ZIP](https://github.com/LuckyLivio/Contain/actions/runs/41078101988/artifacts/12989213594)
is correctly marked `diagnostic_build_unverified`. The bounded read-only delivery
milestone remains open. The [reviewed aggregate](examples/windows-read-only-trial-41078101988.json)
preserves original scores, individual product outcomes, report failures, diagnostic
results, exact commands and identities. No real fixture text report was generated.

| Check on frozen `08604b476dfcdb5bfd7fa93d157bc11467bb858a` | Actual result |
| --- | --- |
| `./scripts/quality.ps1 -Mode Check` | Passed: nextest 291 passed / 0 failed / 7 skipped; 338 Python unittest cases passed; native filesystem 7/7; demo 11/11 and wrapper 36 assertions passed. Doctests executed 0 and do not count as validation coverage. |
| `-Mode Coverage` / `-Mode CoverageOriginal` | Passed: workspace lines 13450/17995 (74.7430%), functions 1077/1419 (75.8985%); original-package lines 9590/13244 (72.4101%), functions 767/1028 (74.6109%). Workspace test executions 291/0/7 ignored; original packages 222/0/4 ignored. |
| `-Mode Dependencies` / `-Mode Companion` | Passed; companion 1 passed / 0 failed, 193 filtered. All 48 recorded quality gates passed. No new fuzz campaign, performance experiment or formal acceptance run. |
| `cargo build --workspace --locked`; existing synthetic CLI + schema | Passed: 66 actual CLI commands, 33 schema checks, three original scenarios. |
| `test-report-paths.py --summary … --private-log …` | 7 passed / 0 failed / 0 skipped on native Windows; actual 8.3 alias obtained from the OS. The old string comparison disagreed while filesystem identity matched. This is a new synthetic directory, not a historical private-path reconstruction. |
| `verify-trial-report.py --package … --generator … --output … --public-output …` | Normal and extended paths: 14/14 actual packaged CLI commands, all sections, same-event JSON/text, redaction and collection passed. This volume supplied no distinct 8.3 alias: that third full-export case is explicitly not covered. |
| Default native fixture, attempt 1 | 35 required product checks passed / 0 failed / 0 not-run; demo exit 0; clocks not requested. Report failed, wrapper exit 1. |
| Dual-clock native fixture, attempt 2 | 35 required product checks plus one diagnostic passed; 33 original QPC links, supported clock/connection evidence complete. Report failed, wrapper exit 1. |
| Real report, each configuration | Directory identity passed. `inspect-json` returned 0, then strict comparison failed with `report_reopened_capture_changed`. **1/7 report commands executed, 6/7 not run**. No inspect/diff/history/explain text report or selected report event was published. |
| Same-capture offline diagnosis, each configuration | 4 commands executed and returned 0: inspect JSON, diff text, history JSON, explain JSON. Its inspect stdout hash exactly matched that configuration's failed report readback. These are independent diagnostics, not completion of the remaining six report commands. |
| Finalize / overall CI | Diagnostic ZIP produced; finalize exit 2; CI failed. Snapshot fallback was not run in this fixed pair. |

The filesystem tests ran in Check and the explicit preflight; those are repeated
executions, not fourteen independent cases. Coverage repeats shared offline tests
and includes inline tests; original-package coverage remains separately gated.
No denominator or 66.5% line / 70% function floor was changed. Windows Server 2025
AMD64 build `10.0.26100`, image `win25-vs2026/20261021.278.1`, used Rust/Cargo
1.99.1, PowerShell 7.6.7, Python 3.12.10, nextest 0.9.149, llvm-cov 0.9.1 and
deny 0.20.2. Both original scorers retained 19/21 observed operations, 17 correctly
attributed, four Unknown, zero wrong attributions and zero extra positives.
Measured provider/application losses were zero; unavailable companion consumer
loss counters remain unavailable. Finished recording is separate from coverage.

The new blocker is at the **report's whole-document comparison**. The preserved
diagnosis demonstrates repeatable later readback, but does not contain the
field-level difference against the original `capture.json`. Therefore it cannot
establish a validator-only false failure or rule out a product data difference.
The runner has ended; missing private evidence cannot be reconstructed from the
aggregate. This limitation is retained rather than marking the report passed.

One independent serialization defect was reproduced offline after the captures:
demo saves the parsed CLI document through PowerShell `ConvertTo-Json`, whereas
the report compares it with directly parsed CLI stdout. Using the same operations
on explicitly synthetic input in existing Linux PowerShell 7.6.9 changed
`2026-07-16T09:10:20.120+00:00` to `2026-07-16T09:10:20.12+00:00`.
Three of four trailing-zero samples changed strings; integer ticks were unchanged.
An earlier six-sample experiment was entirely unchanged and is also retained.
This proves a possible strict-comparison failure, **not the cause of either
capture failure using PowerShell 7.6.7 on Windows**. The full synthetic preflight wrote its saved JSON
directly from production Storage and did not exercise the demo's reserialization.

The next minimal work is an offline regression through that actual save path,
preserving already-validated CLI stdout verbatim while keeping strict comparison,
and retaining safe field-category/count/hash differences on failure. Do not relax
equality or time tolerances, reconstruct a database, or start new capture research.
Frozen scripts and binaries were not changed after these attempts. This round is
closed with a diagnostic build; formal 32-attempt, scale, third-party installer,
final ownership and safe cleanup validation remain separate and unexecuted.
Historical six timing doubts remain D.

Package identity and public-content review:

- Build: `x86_64-pc-windows-msvc`, **debug**, `cargo build --workspace --locked`.
- ZIP SHA-256: `591212f952b6ea4df000d88b4834d5a86fdf7a15569024ed176e69e182b3383d`.
- `contain.exe` SHA-256: `163b493ff0561ef6a2dd67af5613c3cdc6cd26891572e5560a8a7f812753cc3b`.
- Exporter SHA-256: `54982ea0255cdb1427ef01d1d6246d08f929dc50fecab66e3fc41599f5b4d58d`; all packaged script hashes are in `BUILD.json` and the aggregate.
- All 24 ZIP members match the allowlist; all 23 `CONTENTS.json` entries match hashes and sizes. `demo-checks.ps1` is byte-identical to the `149a5e3` redirect fix. No database, ETL, PDB, raw clock, full log or run-identity document is packaged.
- Capture stdout/stderr/host output and original exceptions remained private. Public native-step logs contained only safe status/error codes. Standard Actions build/toolchain paths remain in CI logs; these are not claimed to be path-free logs.

The [complete report actually produced by Windows CLI](examples/windows-report-synthetic-41078101988.txt)
is explicitly **synthetic**, including its event `synthetic-report-event-0`; it
contains inspect, diff, history and the same event's explain. It is not substituted
for the missing real ETW report. The updated diagnostic package includes the
prebuilt demo, required `vcruntime140.dll`, both failed report summaries and usage
instructions. Ordinary CLI use still needs no Rust or Visual Studio; the controlled
demo needs PowerShell 7, Python 3 and existing ETW permissions, with no elevation.
