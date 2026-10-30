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
./scripts/quality.ps1 -Mode Dependencies
cargo build --workspace --locked
$out = 'target/read-only-windows-new'
cargo run -p contain-offline --bin read-only-examples --locked -- $out
foreach ($case in @('finished', 'historical', 'interrupted')) {
    ./target/debug/contain.exe --db "$out/$case.db" inspect "synthetic-$case" |
        Out-File -Encoding utf8 "$out/$case-cli.txt"
    if ($LASTEXITCODE -ne 0) { throw "CLI inspection failed: $case" }
    python scripts/v04-verify-views.py --contain target/debug/contain.exe --database "$out/$case.db" --app "synthetic-$case" --page-size 2 --output "$out/$case-views.json"
    if ($LASTEXITCODE -ne 0) { throw "View agreement failed: $case; keep all artifacts" }
}
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
