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
coverage denominators and list aliases without filtering production code.

The native CLI validator runs 66 commands across the three synthetic databases,
including list, inspect, verbose inspect, diff, paged history, every explanation,
and a final reopen. CI checks all 33 JSON documents against the existing schema.
Public artifacts contain only text exactly matching the reviewed examples and
fixed-field summaries; databases and raw CLI/quality logs stay local. Every quality
invocation records its SHA, tools, commands, exit statuses and archived logs under
`target/quality/runs`; failed attempts are retained. The summary reports test
execution groups and unavailable counts explicitly. These new Windows checks are
pending until an exact CI run is recorded below.

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
