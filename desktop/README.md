# Contain desktop record viewer

The Windows viewer opens existing Contain databases. It does not start an
installer, collect events, run `doctor`, elevate, stay in the background, or
perform cleanup. The existing bounded read-only core delivery milestone remains
closed; desktop validation is a separate milestone.

## Use the Windows package

Extract the complete package and double-click `Contain.Viewer.exe`. Ordinary
viewing needs no Rust, Visual Studio, Python, PowerShell or separately installed
.NET runtime. The package contains the Windows x64 .NET runtime and the required
non-system runtime for the exact `contain.exe` sidecar.

1. Choose **打开记录数据库** and select an existing database. For an explicitly
   synthetic example, choose the packaged `samples/viewer-demo.db`. Opening a
   normal empty database never inserts demonstrations.
2. Select a record on the left. **记录概览** separates recording state from
   coverage quality and shows scope, sources, counts, missing measurements and
   limitations. All example names and warnings identify their synthetic origin.
3. Use **状态变化** for file, registry and configuration differences. The filter
   applies to this record's loaded state differences; rendering is paginated.
4. Use **事件与依据** for the timeline, including source operations, lifecycle
   and state observations. Each page displays at most 50 events. Its text filter
   applies only to the current page. Select an event to read the core's explain
   result, copy its ID, or expand the original JSON.
5. Choose **导出本地报告** and a new `.html` filename. The report contains the
   selected record's summary and state differences, the current unfiltered event
   page, and the selected event's explanation. It states that page scope and
   retains the CLI JSON. It is self-contained, has no scripts or network requests,
   and remains local. Original paths and evidence are included, so review it
   before sharing.

Unknown means insufficient attribution evidence, not danger. High source
confidence is not permission to delete. Historical names are not verified current
paths. A finished record can still have Degraded coverage. Missing measurements
are unavailable rather than zero.

## Connection and read-only contract

The native .NET 8 WinForms interface uses `CoreClient` to launch only the packaged
CLI with `ProcessStartInfo.ArgumentList`, UTF-8 pipes and no shell. It validates
CLI version 0.3.1, JSON envelope version 3, document kind, record/event identity,
exact time-string types and bounded page size. Unknown additive labels remain
visible. It never parses human-readable CLI output into business data or
recalculates attribution. Errors, invalid JSON, nonzero exits, cancellation and
output limits cannot become successful empty results.

All record queries use `--read-only`. SQLite opens the existing original with
read-only flags and holds one read transaction per CLI invocation. Missing,
unrecognized, corrupt and newer databases are rejected. Supported older schemas
use the existing migration only on an in-memory backup; the original is not
migrated. A new query can observe another process's later commit. The viewer has
no background refresh; reopen a record to refresh it. Export rechecks the summary,
state view and selected event and refuses detected changes between queries.

The additive `list --details`, `inspect --summary` and `diff --state-only`
contracts avoid loading current-schema event history for the overview. Summary
measurement availability comes from the same production helper as CLI rendering.
Old CLI commands and full JSON exports retain their behavior. For historical
records without indexed event documents, the core's compatibility path can read
the full old event set internally before slicing the requested page. State
differences are loaded per record, while controls render only one page. CLI
responses above 64 MiB or queries exceeding the configured timeout are refused
with an error; no truncated report is presented as complete.

The ordinary `doctor` command actively probes ETW providers. The desktop never
invokes it, and both the bridge and CLI reject it under the read-only contract.

## Build and verification

Development needs the existing Rust/MSVC toolchain and .NET 8 SDK. User packages
are self-contained Windows x64 publishes; UI and CLI build profiles and hashes
are recorded separately in `BUILD.json`. Native checks operate the production
form controls and call the real packaged CLI on databases generated through
production `Storage`/`RawBuffer`, never frontend objects. A second process launches
the packaged desktop executable and verifies its native window and normal exit.
The harness checks that its production UI assembly is identical to the frozen
package and records screenshots rendered by the actual Windows controls.

The existing CI uses `[desktop-viewer]` to schedule this delivery. That plan
executes no ETW capture or snapshot fixture. It explicitly skips the older CLI
integration script because that script invokes active `doctor`; applicable views
are exercised through the desktop's strict read-only path. Rust Check, Coverage,
CoverageOriginal and Dependencies retain their existing assertions and floors.
Synthetic examples do not provide new ETW or third-party installer evidence.

```powershell
cargo build --workspace --locked
dotnet publish desktop/Contain.Viewer/Contain.Viewer.csproj -c Release -r win-x64 --self-contained true -o target/viewer-publish
dotnet publish desktop/Contain.Viewer.Tests/Contain.Viewer.Tests.csproj -c Release -r win-x64 --self-contained true -o target/viewer-tests
```

The CI packaging and verification commands are in `.github/workflows/ci.yml`.
The package's `VALIDATION.json` records whether the actual desktop milestone
passed. A build or Linux cross-compile alone does not establish native operation.
