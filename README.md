# Contain

**Install anything. Leave nothing behind.**

## Which app changed this?

Contain v0.2 observes Windows installation sessions, links real events to verified process instances, and explains the evidence. **Contain distinguishes observed changes from attributed changes.**

The tagline is the long-term direction. Today Contain records evidence and offers a cleanup **dry run**. It does not guarantee zero leftovers or isolate an installer.

## Try it

Requirements: Windows x64, [Rust stable](https://rust-lang.org/tools/install/), Visual Studio C++ build tools, and PowerShell. From the repository:

```powershell
./scripts/demo.ps1
```

This builds the CLI, runs a safe parent → child → grandchild installer alongside an **independent process writing the same directory**, checks attribution, JSON/SQLite readback and unchanged data after dry-run, then removes only the marked fixture. Files stay within `%TEMP%\contain-demo-*`; registry mutations stay within its matching `HKCU\Software\Contain\Demo\*` key. It creates no real services, tasks, or startup registrations.

```powershell
./scripts/demo.ps1 -SnapshotOnly  # explicit fallback
./scripts/demo.ps1 -RequireEtw    # fails unless real PID-aware events are captured
./scripts/demo.ps1 -KeepArtifacts # prints database and fixture paths for exploration
```

ETW needs suitable Windows trace/provider permissions. Contain never opens UAC. With insufficient rights it continues with process observation and snapshots, and file/registry attribution remains `Unknown`. `-RequireEtw` is a test assertion, not an elevation request.

## Real verification

The initial v0.2 [Windows CI run](https://github.com/LuckyLivio/Contain/actions/runs/36536794866) captured **9 High file events**, while **3 independent writer events remained Unknown**. Both ETW and snapshot fallback integration steps passed on Windows Server 2025. This verifies the bounded fixture, not arbitrary installers. See [verification notes](docs/verification-v0.2.md) for current evidence and limitations.

One real event from the [second run](https://github.com/LuckyLivio/Contain/actions/runs/36537468528), with its temporary root abbreviated for display:

```text
File:       %TEMP%\contain-demo-b238...\cache\index.bin
Operation:  write_requested (completion status not supplied)
Written by: contain-test-installer.exe, PID 4276
Belongs to: TestFixture
Confidence: High
Evidence:   child PID 4276 → installer PID 1720
Identity:   process creation FILETIME 134351410641704877
```

The [original event JSON](docs/examples/file-event-v2.json) preserves the actual evidence. The final file state stayed Unknown because an additional write had no queryable writer. Attribution of this one event does not imply exclusive ownership of the file.

## Use the CLI

```powershell
cargo build --workspace --locked
./target/debug/contain.exe doctor
./target/debug/contain.exe install .\SomeAppSetup.exe --name SomeApp --watch "$env:LOCALAPPDATA\SomeApp" --registry-key Software\Vendor\SomeApp
./target/debug/contain.exe inspect SomeApp
./target/debug/contain.exe diff SomeApp
./target/debug/contain.exe history SomeApp --json
./target/debug/contain.exe remove SomeApp --dry-run
```

Watch directories must already exist. Repeat `--watch` for each scope. Without it only the installer's directory is watched. `--registry-key` selects one HKCU Software key: values are compared at that key; ETW may also observe operations beneath it. Installer arguments follow `--`. Use `--no-etw` to disable tracing and `--settle-ms` to adjust the observation window after the root exits.

`--db <path>` selects a database; default is `%LOCALAPPDATA%\Contain\contain.db`. `list --json`, `inspect --json`, `diff --json`, `history --json`, and `doctor --json` use a [versioned JSON contract](docs/json-contract.md). `install --manifest <path>` exports an inspect document. FILETIME identity fields use decimal strings to preserve full precision.

## What v0.2 records

| Source | Recorded evidence | Attribution boundary |
| --- | --- | --- |
| Owned process handle and process polling | PID, creation time, executable, verified parent chain | Installer Certain; observed descendants High |
| ETW Kernel-File | Scoped create/write/rename/delete observations, timestamp, issuing thread → live process identity when queryable | High only for a matching installer-family instance |
| ETW Kernel-Registry | Scoped key/value operations, completion status when available, writer identity when queryable | Unknown unless the same lifetime/ancestry checks pass |
| File hash and HKCU snapshots | Before/after state, including modified/deleted entries | Unknown alone; correlated event evidence is separate |
| Service, scheduled task, startup inventories | Created/changed/removed configuration with before/after fields | Exact verified executable target can be Medium; timing alone stays Unknown |

Inspect shows evidence quality, reasons, writer identity and source details. Diff groups attributed and unattributed changes. History distinguishes source event times from end-of-session state observations. A successful operation, an attributed process, and a surviving state change are separate facts.

## Safety and privacy

- `remove` requires `--dry-run`; there is no deletion executor, uninstaller execution, or rollback.
- User data stays `USER_DATA`. A High-attributed cache may be `REVIEW`; unknown or changed files remain `UNKNOWN`. Nothing is marked `SAFE` for deletion.
- No automatic elevation, driver, service installation, Defender changes, AI classification, or GUI.
- No account, telemetry, or uploads in the application. Databases/manifests contain local paths and possibly registry/configuration data; review them before sharing.
- ETW providers produce system-wide activity; scoped records are retained, other paths are discarded. The scanner skips reparse points. Contain does not sandbox the installer you launch.

## Limitations

- Process polling and delayed ETW delivery can miss short-lived or detached writers. Missing creation time, path, or ancestry stays Unknown. Identical executable names do not establish ownership.
- File events describe operation requests; v0.2 does not correlate IRP completion or both rename endpoints. Snapshot rename appears as deleted/created paths. Unsupported provider schemas and unresolvable paths reduce coverage.
- On the tested runner, registry ETW omitted absolute hive/key paths. Those records are counted and discarded; scoped HKCU snapshots remain Unknown. Provider activation alone is not a claim of working registry attribution.
- Services, tasks and Run entries are read-only snapshots. Their writer and exact mutation time are unknown. Protected inventories can be unavailable; this produces warnings, not removals.
- No whole-machine footprint guarantee. Only selected file roots and one HKCU key are state-scanned; HKCU/HKLM Run (32/64-bit views) are inventoried. No startup folders, arbitrary registry hive scan, or virtualization.
- Capture has bounded buffers and records drops/ETW losses/decode errors. No-loss counters do not prove complete provider coverage or exclusive ownership. System-wide overhead has not been benchmarked on large installs.

## Development

```powershell
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo build --workspace --locked
./scripts/demo.ps1
```

Windows CI requires both real ETW evidence and fallback integration. Read the [architecture](docs/architecture.md), [backend ADR](docs/adr/0001-event-attribution-backend.md), [contribution guide](CONTRIBUTING.md), [security policy](SECURITY.md), and [code of conduct](CODE_OF_CONDUCT.md).

Next: reliable short-lived process lifetimes, stronger operation completion/path correlation, broader Windows validation, then reviewed uninstaller/cleanup planning. No release date or completeness guarantee is implied.

## License

Apache-2.0. See [LICENSE](LICENSE).
