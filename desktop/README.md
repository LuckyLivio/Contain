# Contain desktop records

The Windows WinForms desktop opens existing Contain databases read-only and
provides an explicit **新建安装记录** flow through the packaged Rust CLI.
The bounded read-only core and desktop record viewer milestones remain closed.
Graphical recording is a separate [implementation and validation milestone](../docs/graphical-install-record.md).

## Current download

Use the [Beta 3 release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.3).
Its [personal library, validation scope and known issues](../docs/personal-records-beta3.md) distinguish
native Windows results, simulated layout stress and user-reported trials.
The entries below retain historical evidence rather than additional current downloads.

## Historical graphical installation recording delivery

The graphical recording MVP is **closed** for version 0.3.1, frozen source
`15f214442d69baa0ebd91f5ad12edc5661281779`.
[Windows CI 41805319059](https://github.com/LuckyLivio/Contain/actions/runs/41805319059)
passed all 49 quality gates, 22 existing viewer checks, six new product checks,
actual installer-picker navigation/cancel and explicit synthetic failure preservation.
The actual GUI-started ETW session saved 219 source events with Degraded coverage,
auto-opened its exact UUID among older/same-name rows, and explained/exported a
High file event verified against the independent fixture oracle.
[Exact evidence, screenshots, hashes, limits and retained failures](../docs/graphical-install-record.md).
The previous viewer/core milestones remain closed separately; no third-party,
formal 32-attempt or scale trial was added.

## Use the Windows package

Extract the complete package and double-click `Contain.Viewer.exe`. Ordinary
viewing needs no Rust, Visual Studio, Python, PowerShell or separately installed
.NET runtime. The package contains the Windows x64 .NET runtime and the required
non-system runtime for the exact `contain.exe` sidecar.

1. Startup loads **我的记录** from the current user's Windows LocalApplicationData
   folder, `Contain\Records\contain.db`. It is independent of the ZIP location
   and working directory. A first empty startup creates neither directories nor
   a database; only explicit recording Start creates the default library.
   **打开记录数据库** still opens an existing external database read-only; for
   an explicitly synthetic example choose `samples/viewer-demo.db`. **我的记录**
   returns to the personal library. External selection never changes the next
   new record's default destination.
2. Select a record on the left. **记录概览** separates recording state from
   coverage quality and shows scope, sources, counts, missing measurements and
   limitations. All example names and warnings identify their synthetic origin.
3. Use **状态变化** for file, registry and configuration differences. The filter
   applies to this record's loaded state differences; rendering is paginated.
4. Use **事件与依据** for the timeline, including source operations, lifecycle
   and state observations. Each page displays at most 50 events. Its text filter
   applies only to the current page. Select an event to read the core's explain
   result, copy its ID, or expand the original JSON.
5. Choose **导出本页报告** and a new `.html` filename. The report contains the
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
the canonical Cargo product version, JSON envelope version 3, document kind, record/event identity,
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

The ordinary `doctor` command actively probes ETW providers. The read-only
`CoreClient` and CLI read-only mode reject it. The new-record page invokes it only
when the user explicitly chooses **检查环境**, through a separate install client.
It does not launch the installer, elevate or alter system security settings.
A successful check does not guarantee later startup.

## New installation record

1. Choose **新建安装记录**, then select an existing native `.exe` installer.
   Current acceptance uses the controlled `contain-test-installer.exe` fixture.
   MSI, updater and service-agent installation coverage is not promised.
2. The EXE filename suggests a record name; an edited name is preserved, and
   duplicate names are allowed. Choose an **existing** observation directory.
   **保存到我的记录** uses the stable personal library without asking for a
   database path. Restarting the application loads its saved sessions again.
   The application does not infer all software
   destinations from the installer directory, scan all drives or create the
   software destination to make validation pass. For the packaged fixture,
   a developer/tester prepares a marked isolated fixture directory first.
3. Open advanced settings only if needed. A custom database location is optional
   and does not replace the next new record's personal default. Database and
   WAL/SHM must remain outside the observation scope; unavailable, corrupt or
   incompatible files are reported and preserved. Prelaunch errors offer
   **更改保存位置**. Arguments are one literal argument per
   line, without shell quoting or expansion. Spaces, Chinese and special characters
   remain part of their argument. The registry option is a selected HKCU key;
   defaults preserve the existing backend limits.
4. Click **检查环境**. ETW availability, current permissions, warnings and the
   explicitly selected snapshot mode are reported separately. The default recording
   path requires ETW readiness at actual launch. A user can select snapshot mode
   knowing that its observations and attribution are limited.
5. Click **开始记录** once. Configuration and checking never start the installer.
   The interface remains responsive, accepts only one active task and reports
   actual preparing/capturing/finalizing/finished/failed backend stages without
   a fabricated percentage. Installer exit, capture completion, saved data and
   coverage quality are separate facts.
6. After finalization choose the exact returned session automatically in the existing
   viewer. Select a source event to read explain, then export a local report.
   Failed or interrupted saved sessions can also be inspected; no evidence is
   deleted and the latest database row is never used to identify the task.

There is no force-cancel button. Closing an active recording window is refused
with a visible explanation that the task continues; leave it open until the
backend finishes. Abnormal desktop/core termination is not a successful capture;
reopen the bound database to inspect retained unfinished evidence. Contain does
not automatically elevate or stop somebody else's trace session.

### Task progress and failure details

The recording window shows the actual backend step, task/stage/step durations,
available completed-work counts and age of the last measured work. Finalization
distinguishes file enumeration/read/hash, source stop and queue drain, raw replay,
operation normalization, evidence association, final inventory, SQLite save and
exact-session readback. The snapshot entry count includes completed skipped or
unreadable attempts; source-record counts do not assert persistence or attribution.
There is no percentage or remaining-time estimate when the total is unknown.

The local display timer continues elapsed/age estimates and labels them as local;
it never changes a backend work timestamp or count. Receipt of another message is
separate from actual work. Silence alone is not a hang diagnosis, and a single
large file read may have no intermediate count. Missing measurements remain
unavailable. A running installer may need input; Contain cannot determine whether
its window is actually waiting for the user.

**查看任务详情** opens scrollable, selectable original errors, source availability,
exact session/database and collector information. **复制任务摘要** copies a concise
local diagnostic snapshot. Neither action uploads anything. Review local paths
before sharing. Saved evidence, source stop and installer exit remain separate
from a confirmed complete session and from installation success.

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

The existing CI uses `[stage1-beta]` for this bounded Beta validation,
`[desktop-viewer]` for viewing regressions and
`[desktop-install]` for the bounded graphical recording plan. The viewing plan
executes no ETW capture or snapshot fixture. The install plan declares one controlled
GUI-started ETW capture after offline regressions; it does not invoke demo.ps1 to
start the product or precapture a database. It explicitly skips the older CLI
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
closed. This historical viewing result supplies no graphical recording evidence.
The new [graphical recording milestone](../docs/graphical-install-record.md) is
tracked separately. Real-installer trials, formal 32-attempt acceptance, scale
validation, ownership and cleanup remain outside this round.
