# Contain

**Record installation changes and inspect the evidence behind them.**

> Unknown is safer than a wrong answer.

## Which app changed this?

Contain v0.3.1 observes Windows installation sessions, links real events to verified process instances, and explains the evidence. **Contain distinguishes observed changes from attributed changes.**

Contain records evidence and offers a cleanup **dry run**. It does not guarantee zero leftovers or isolate an installer.

## Current read-only trial status

The bounded trial is record controlled activity → save → reopen → query changes
→ explain an event ID. Version remains **0.3.1**. Implementation, measured results
and outstanding acceptance are separate; test counts are not feature progress.
**The bounded read-only candidate delivery milestone is closed** for frozen
source `2ba98b6cbb99e2cf622ad45713fd8d38d9b76c15` and the package linked below.

| User task | Implemented | Verified platform / scenario | Status and specific limit | Build / evidence |
| --- | --- | --- | --- | --- |
| Record controlled activity and save it | Native process/file/registry capture, SQLite persistence and validated original JSON text saving | Windows Server 2025 x64; one default and one dual-clock fixture, each 35 required product checks passed | Verified in these bounded cases. Both recordings finished with quality Degraded; completion does not imply full coverage | [CI 41102818151](https://github.com/LuckyLivio/Contain/actions/runs/41102818151), frozen build `2ba98b6` |
| Reopen the database and query paged changes | Existing inspect/diff/history and strict complete-envelope comparison | Both real reports: 7/7 commands, zero field differences and byte-identical saved/readback JSON. Windows synthetic chain: 24 CLI calls, including expected mismatch rejection | Verified. A mismatch still fails acceptance while independent safe commands produce diagnostics | [Reviewed results](docs/examples/windows-read-only-trial-41102818151.json) |
| Explain event IDs without promoting Unknown or historical names | Existing explain and cross-view identity checks | Both real reports trace the same event in history and explain; all three original synthetic scenarios retained | Verified for the controlled fixture. Execution evidence does not establish application API intent, a current path or ownership | [Default real CLI report](docs/examples/windows-trial-default-41102818151.txt), [dual-clock report](docs/examples/windows-trial-dual-clock-41102818151.txt) |
| Distinguish diagnostic, evidence, product and formal acceptance | Versioned comparison, field-level diagnostics and independent acceptance checks | Dual-clock: 91 original-QPC links, complete evidence in the supported clock/connection scope; all required product checks passed | Historical six remain D; formal acceptance remains unmet. Earlier report failures cannot be retrospectively assigned a field-level cause | [Evidence and limits](docs/read-only-workflow.md#verified-bounded-delivery) |
| Download and run without Rust or Visual Studio | MSVC debug binaries, runtime, prebuilt demo including `149a5e3` and shared save helper, hashes and validation manifest | Identical frozen package in preflights and both native scenarios; 18/18 required candidate checks, ZIP contents/hashes verified | **Bounded read-only candidate verified; delivery milestone closed.** No release or general installer/stability claim | [Download Windows ZIP artifact](https://github.com/LuckyLivio/Contain/actions/runs/41102818151/artifacts/13000967787) |

Separate follow-up scopes remain: formal 32-attempt acceptance; 1,000/10,000-file
scale validation; third-party installer trials; final-state ownership; and safe
cleanup. None is implied by this read-only trial, and none starts in this round.

## Try it

The Windows x64 CI trial package contains `contain.exe`, the controlled fixture,
required non-system runtime files, `BUILD.json`, `VALIDATION.json` and `TRY.md`.
Read its actual validation status before use: a diagnostic package is not a
verified candidate. Ordinary CLI use needs neither Rust nor Visual Studio.
The controlled demo additionally needs PowerShell 7 and Python 3.
From the extracted package, use a new output directory:

```powershell
pwsh -NoProfile -File .\scripts\demo.ps1 -RequireEtw -KeepArtifacts -OutputDirectory "$PWD\demo-result-new"
```

The prebuilt demo preserves its database and fixture and prints their locations.
It records a parent → child → grandchild fixture with an independent writer,
short-lived and detached helpers, rename/failure cases and registry noise;
then checks attribution, database reopening, JSON, paged views, event explanation
and unchanged data after dry-run. Files and registry changes stay within the
marked temporary fixture and its matching `HKCU\Software\Contain\Demo\*` key.
It creates no services, tasks or startup registrations.

For development from source, install Rust stable and Visual Studio C++ build
tools in addition to those demo tools. The original build entrypoint remains:

```powershell
./scripts/demo.ps1 -RequireEtw -KeepArtifacts
./scripts/demo.ps1 -SnapshotOnly -KeepArtifacts # explicit, separate fallback
```

ETW needs suitable Windows trace/provider permissions. Contain never opens UAC. With insufficient rights it continues with process observation and snapshots, and file/registry attribution remains `Unknown`. `-RequireEtw` is a test assertion, not an elevation request.

## Attribution reliability

v0.3 retains ETW process/thread lifetimes, distinguishes PID reuse, joins file
requests to completion where available, and explains actor, resource, operation
and application confidence separately. A SQLite evidence graph connects the
session, process instances, observations and resources.

The [verified fixture run](https://github.com/LuckyLivio/Contain/actions/runs/36542048919)
observed 20 of 21 instrumented operations: **17 correctly attributed, 4 Unknown,
0 incorrectly attributed, 0 application drops**. Unknown includes the unrelated
writer and a blocked deletion with no matching mutation event. This is fixture
coverage, not general accuracy. Both short-lived and post-installer-exit writes
were attributed. Registry context recovered some full paths; unresolved objects
still remain explicit. A later provider-readiness fix addresses asynchronous
startup. See [verification](docs/verification-v0.3.md) for the final tested revisions.

- [Actual terminal demo](docs/demo-output.txt)
- [Compatibility matrix](docs/windows-compatibility.md)
- [Reproducible 10,000-file benchmark](docs/performance.md)

## Use the CLI

```powershell
cargo build --workspace --locked
./target/debug/contain.exe doctor
./target/debug/contain.exe install .\SomeAppSetup.exe --name SomeApp --watch "$env:LOCALAPPDATA\SomeApp" --registry-key Software\Vendor\SomeApp
./target/debug/contain.exe inspect SomeApp
./target/debug/contain.exe inspect SomeApp --verbose
./target/debug/contain.exe explain <event-id> --json
./target/debug/contain.exe diff SomeApp
./target/debug/contain.exe history SomeApp --json
./target/debug/contain.exe remove SomeApp --dry-run
```

Watch directories must already exist. Repeat `--watch` for each scope. Without it only the installer's directory is watched. `--registry-key` selects one HKCU Software key: values are compared at that key; ETW may also observe operations beneath it. Installer arguments follow `--`. Use `--no-etw` to disable tracing. After root exit, `--settle-ms` (default 1500) sets the quiet period and `--max-drain-ms` (default 10000) bounds waiting for descendants/activity. A timeout marks the session Incomplete.

`--db <path>` selects a database; default is `%LOCALAPPDATA%\Contain\contain.db`. `list --json`, `inspect --json`, `diff --json`, `history --json`, and `doctor --json` use a [versioned JSON contract](docs/json-contract.md). `install --manifest <path>` exports an inspect document. FILETIME identity fields use decimal strings to preserve full precision.

v0.3.1 continuously commits a bounded raw evidence journal, then finalizes evidence
in pages. Keep the database outside watched roots. `--evidence-quota-mib` defaults
to 256; quota/write failures remain visible. Interrupted sessions are explicitly
unfinished and expose no final attribution. `history <app> --json --limit 256
--offset 0` reads a page; default inspect reads a summary. See the
[pipeline ADR](docs/adr/0006-capture-pipeline.md) and [counter definitions](docs/capture-counters.md).

The current release comparison passes 100 files / four workers in three trials.
1000 and 10000 files fail the useful-attribution gate; 1000 is a regression against
the baseline. This remains experimental. See the [measured limits](docs/performance.md#final-measured-decision-2026-09-29).

Opt-in development experiments are tracked separately in the
[commit-window diagnostics](docs/capture-commit-tail.md) and
[deferred checkpoint report](docs/capture-deferred-checkpoint.md).

The [v0.4 development report](docs/validation-v0.4.md) records measured readback
optimizations and attribution fixes. Release gates remain unmet; the package is
still 0.3.1. The stabilization candidate separates receiving from SQLite and
observes final resources while tracing. The administrator-run small ETW demo is
now archived; it does not replace the 1,000/10,000-file acceptance runs. Controlled
tests found that retained file read leases blocked legitimate operations, so the
current candidate uses shared reads without retaining those restrictive handles.
These reads are not a global atomic snapshot. Uncovered mutations and registry read races
still prevent final-state ownership claims. Valid source actor evidence remains
separate. The stabilization milestone is not yet accepted by live ETW validation.

## What v0.3 records

| Source | Recorded evidence | Attribution boundary |
| --- | --- | --- |
| Owned handle, ETW process/thread lifetimes and polling | PID, creation time, executable, verified parent chain | Installer Certain; observed descendants High |
| ETW Kernel-File | Scoped create/write/rename/delete observations, timestamp, issuing thread → retained or live process identity, object generations and IRP completion | High only for a matching installer-family instance |
| ETW Kernel-Registry | Scoped key/value operations, completion status when available, writer identity when queryable | Unknown unless the same lifetime/ancestry checks pass |
| File hash and HKCU snapshots | Before/after state, including modified/deleted entries | Unknown alone; correlated event evidence is separate |
| Service, scheduled task, startup inventories | Created/changed/removed configuration with before/after fields | Exact verified executable target can be Medium; timing alone stays Unknown |

Inspect summarizes capture quality and counters; `--verbose` and `explain` show evidence details. Diff groups attributed and unattributed changes. History distinguishes source event times from end-of-session state observations. A successful operation, an attributed process, and a surviving state change are separate facts.

## Safety and privacy

- `remove` requires `--dry-run`; there is no deletion executor, uninstaller execution, or rollback.
- User data stays `USER_DATA`. A High-attributed cache may be `REVIEW`; unknown or changed files remain `UNKNOWN`. Nothing is marked `SAFE` for deletion.
- No automatic elevation, driver, service installation, Defender changes, automated classification, or GUI.
- No account, telemetry, or uploads in the application. Databases/manifests contain local paths and possibly registry/configuration data; review them before sharing.
- ETW providers produce system-wide activity; scoped records and verified installer lifecycle/unresolved-object evidence are retained; other resources are discarded. The scanner skips reparse points. Contain does not sandbox the installer you launch.

## Limitations

- File source confidence identifies an execution actor and recorded name, not an
  explicit application API call, verified current path or final content owner.
  Ordinary capture retains provider/cache name provenance and its observation
  time; a historical cached name is labelled as such. Older records without this
  evidence remain readable with provenance unavailable.

- Missing lifecycle events, ambiguous lifetimes, provider access failure or unsupported schemas reduce coverage. Captures are Degraded or Incomplete; v0.3 never claims a complete footprint.
- Normalized operations preserve raw references. IRP completion is partial; stable file IDs prove surviving rename/replacement endpoints, not an entire transient chain or exclusive ownership.
- Registry context requires a verified absolute base and the same process lifetime. Missing paths stay `<unresolved registry object>`. Registry snapshots remain Unknown when complete scoped writer evidence is unavailable.
- Drain is finite. Broker/service-manager activity and changes after the session are not automatically descendants.
- Services, tasks and Run entries are read-only inventories. Their actual writer and mutation time are unknown. Protected inventories produce warnings, not inferred removals.
- Captures have bounded queues/caches, visible drops and separate ETW event/buffer loss. Reported loss suppresses application promotion; no-loss counters do not prove provider coverage.

## Development

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo build --workspace --locked
./scripts/demo.ps1
```

Windows CI requires both real ETW evidence and fallback integration. Read the [architecture](docs/architecture.md), [backend ADR](docs/adr/0001-event-attribution-backend.md), [contribution guide](CONTRIBUTING.md), [security policy](SECURITY.md), and [code of conduct](CODE_OF_CONDUCT.md).

The next stabilization matrix is **preregistered, not executed**. With frozen
baseline/candidate executables, one common fixture and their full source revisions,
this command plans 32 real-ETW attempts (three pairs at 100/10000, ten at 1000):

```powershell
python scripts/v04-matrix.py --stabilization --baseline $baselineExe --candidate $candidateExe --fixture $commonFixtureExe --baseline-revision $baselineRevision --candidate-revision $candidateRevision --output target/v04/stabilization-etw --files 100 1000 10000 --workers 4 --trials 3 --stability-trials 10 --noise-files 100
```

The [protocol](docs/v04-validation.md) retains the original scores separately.
Its new gate remains blocked until original ETW QPC and provider timing rules are
independently validated; fallback or a clock diagnostic cannot count as a pass.

Next: broader Windows validation, more registry context coverage and stronger multi-step operation evidence. GUI and real cleanup remain deferred.

## License

Apache-2.0. See [LICENSE](LICENSE).
