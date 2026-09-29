# Contain

**Contain doesn't just see changes. It explains why they belong to an app.**

> Unknown is safer than a wrong answer.

## Which app changed this?

Contain v0.3 observes Windows installation sessions, links real events to verified process instances, and explains the evidence. **Contain distinguishes observed changes from attributed changes.**

Contain records evidence and offers a cleanup **dry run**. It does not guarantee zero leftovers or isolate an installer.

## Try it

Requirements: Windows x64, [Rust stable](https://rust-lang.org/tools/install/), Visual Studio C++ build tools, and PowerShell. From the repository:

```powershell
./scripts/demo.ps1
```

This builds the CLI, runs a safe parent → child → grandchild installer alongside an **independent process writing the same directory**, records independent ground truth, short-lived and detached helpers, rename/failure cases, and registry noise; checks attribution, JSON/SQLite readback and unchanged data after dry-run, then removes only the marked fixture. Files stay within `%TEMP%\contain-demo-*`; registry mutations stay within its matching `HKCU\Software\Contain\Demo\*` key. It creates no real services, tasks, or startup registrations.

```powershell
./scripts/demo.ps1 -SnapshotOnly  # explicit fallback
./scripts/demo.ps1 -RequireEtw    # fails unless real PID-aware events are captured
./scripts/demo.ps1 -KeepArtifacts # prints database and fixture paths for exploration
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
- No automatic elevation, driver, service installation, Defender changes, AI classification, or GUI.
- No account, telemetry, or uploads in the application. Databases/manifests contain local paths and possibly registry/configuration data; review them before sharing.
- ETW providers produce system-wide activity; scoped records and verified installer lifecycle/unresolved-object evidence are retained; other resources are discarded. The scanner skips reparse points. Contain does not sandbox the installer you launch.

## Limitations

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

Next: broader Windows validation, more registry context coverage and stronger multi-step operation evidence. GUI and real cleanup remain deferred.

## License

Apache-2.0. See [LICENSE](LICENSE).
