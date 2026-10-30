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
The harness supplies file-selection paths through the dialog interface, then
operates the production buttons, tables, tabs and clipboard. It does not automate
the operating system's file-picker navigation or establish manual Windows 10/11
compatibility. The packaged executable also has a separate native launch/close
check; screenshots are not browser previews.

The existing CI uses `[desktop-viewer]` to schedule this delivery. That plan
executes no ETW capture or snapshot fixture. It explicitly skips the older CLI
integration script because that script invokes active `doctor`; applicable views
are exercised through the desktop's strict read-only path. Rust Check, Coverage,
CoverageOriginal and Dependencies retain their existing assertions and floors.
Synthetic examples do not provide new ETW or third-party installer evidence.
Rust coverage retains inline tests and the shared `contain-offline` executions;
`CoverageOriginal` separately reports the original package denominator. Neither
percentage is .NET UI coverage. Native desktop behavior is reported separately
as the predeclared checks, with failures and unexecuted checks preserved.

```powershell
cargo build --workspace --locked
dotnet publish desktop/Contain.Viewer/Contain.Viewer.csproj -c Release -r win-x64 --self-contained true -o target/viewer-publish
dotnet publish desktop/Contain.Viewer.Tests/Contain.Viewer.Tests.csproj -c Release -r win-x64 --self-contained true -o target/viewer-tests
```

The CI packaging and verification commands are in `.github/workflows/ci.yml`.
The package's `VALIDATION.json` records whether the actual desktop milestone
passed. A build or Linux cross-compile alone does not establish native operation.

## Verified Windows desktop delivery

The **desktop record viewer MVP is closed** for frozen source
`4b4d38764ed417e6754b5db798488227ec147e21`, version 0.3.1.
[Download the desktop ZIP artifact](https://github.com/LuckyLivio/Contain/actions/runs/41122708670/artifacts/13010587633).
[CI 41122708670](https://github.com/LuckyLivio/Contain/actions/runs/41122708670)
completed successfully on Windows Server 2025 x64, build 10.0.26100,
runner image `win25-vs2026/20261030.294.1`.

| Executed check | Passed | Failed | Skipped / ignored | Not run |
| --- | ---: | ---: | ---: | ---: |
| Native desktop plan, including packaged EXE, actual CLI, controls and report | 22 | 0 | 0 | 0 |
| Required quality gates: Check / Coverage / CoverageOriginal / Dependencies / Companion | 49 | 0 | 0 | 0 |
| Rust nextest | 314 | 0 | 7 | 0 |
| Python regressions, 23 suites | 353 | 0 | 0 | 0 |
| Workspace coverage test executions | 314 | 0 | 7 | 0 |
| Original-package coverage test executions | 234 | 0 | 4 | 0 |
| Existing Windows directory-identity checks, including actual 8.3 alias | 7 | 0 | 0 | 0 |

The native plan passed 230 assertions. Coverage lines/functions were
**75.380070% / 76.308540%** for the workspace and
**72.641020% / 74.880153%** for the original packages. Floors remain
66.5% lines / 70% functions. These are overlapping executions, not unique test
counts or product completion percentages. The existing companion regression ran
separately (1 passed); the ignored disk experiments were not started. Doctests
executed zero tests and supply no feature evidence. The fuzz seed test passed;
no new fuzz campaign or performance experiment was started.

`Storage::open_read_only`, `events_page_compatible`, `list_details` and
`summary_counts` coverage regions were inspected. Positive current/legacy reads,
database refusal, paging and summary availability ran; SQLite open/backup/query
error-propagation regions remain partly uncovered. Shared aliases and zero-count
instantiations remain in the CI artifact. LLVM branch coverage and .NET coverage
were not measured. Exact commands, versions, counts and denominators are in the
[reviewed result](../docs/examples/windows-desktop-viewer-41122708670.json).
Actual tools were Rust/Cargo 1.99.1, PowerShell 7.6.9, Python 3.12.10,
nextest 0.9.149, llvm-cov 0.9.1, deny 0.20.2 and .NET SDK 10.0.403.

The core is the tested MSVC **debug** build and the viewer is **Release**;
the package includes .NETCore/WindowsDesktop 8.0.33 and the needed non-system
`vcruntime140.dll`. All 472 frozen files and 474 content-manifest entries matched
the 475-member final ZIP. `BUILD.json` and `VALIDATION.json` bind the scripts,
source, binary identities, actual native checks and unchanged files. The ZIP is
74,077,890 bytes, SHA-256:

```text
ff59a15260e49283bfc6e63c9780f22930e9767a1c058ce6daa9a65c198ed5ff
```

The [actual exported HTML](../docs/examples/viewer-41122708670-report.html)
is explicitly synthetic and byte-identical to the native viewer's local export.
It contains `synthetic-finished`, event-page offset 50 with 10 events, and
`synthetic-finished-event-50`; the history event and explain event match in every
field. The exact timestamp is `2026-10-01T00:00:01.123400+00:00`, FILETIME
`134350000000000061`. Its SHA-256 is
`5cd59752f74b5ac3b5848a6ad575bb9c8b824f269613afd1f097f1acff93ebf0`.
This is a real CLI/desktop export of model data, not real installation evidence.

[Overview](../docs/examples/viewer-41122708670-finished-overview.png),
[paged event and explanation](../docs/examples/viewer-41122708670-event-explanation.png),
[Unknown](../docs/examples/viewer-41122708670-unknown-evidence.png), and
[unfinished record](../docs/examples/viewer-41122708670-interrupted-record.png)
are unchanged screenshots of actual Windows controls. The complete ten screenshots
and report are in the [evidence artifact](https://github.com/LuckyLivio/Contain/actions/runs/41122708670/artifacts/13009798835).

Both earlier failures remain in the [attempt history](../docs/examples/windows-desktop-viewer-attempts.json).
The first stopped at a Windows-default-encoding test reader. The second had 18
native passes, two failures and two dependent checks not run: the harness expected
`capturing` instead of the fixture's actual `finalizing`, and checked navigation
before waiting for a tab-triggered query. The final run measured busy/disabled
immediately after activating the event tab and idle/enabled after the real
operation completed. Original assertions were retained; product states were not
changed to satisfy the harness.

No ETW capture, active `doctor`, old 66-command active-probe integration or snapshot
fallback ran in this desktop plan. Clean Windows 10/11 manual testing and native
file-picker navigation remain unexecuted. The unsigned ZIP has no installer;
keep the complete extracted directory. The existing core-delivery milestone stays
closed. Graphical recording, real-installer trials, formal 32-attempt acceptance,
scale validation, ownership and cleanup remain separate, unstarted scopes.
