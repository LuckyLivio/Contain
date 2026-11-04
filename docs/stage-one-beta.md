# Phase-one Beta: Windows installation records and evidence viewing

The three earlier milestones remain closed. This delivery retains WinForms, the
Rust core and CLI/schema-3 contracts. The current delivery is **0.3.2-beta.1**, frozen at
`85c522839523d259c09c80a4ce4e8c78ea11472d`.
[Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.1) ·
[Windows x64 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.1/contain-0.3.2-beta.1-windows-x64.zip) ·
[SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.1/SHA256SUMS.txt).

## Scope

A user selects an executable, supplies a name, existing observation directory and
outside database, checks capabilities, then explicitly starts recording. The
installer chooses its actual installation target; Contain does not set that target
or infer it from the installer's folder. Source availability at preflight is
separate from sources actually enabled for a capture. Default recording requires
ETW readiness; snapshot mode is an explicit separate choice.

Saved records open by exact session ID. State differences, bounded source pages,
actor explanations and passive local HTML export remain available through the
read-only viewer. Event filtering and export contain the current page, not a
whole-database search or full-history export. High concerns evidence-backed actor
association, not final ownership or safe deletion. Degraded is a scope/coverage
limit, not an installation outcome.

## Changes and final validation

- Both forms have an explicit 96-DPI design baseline and PerMonitorV2 bootstrap.
  Labels, row heights and action/paging buttons use preferred content sizes;
  constrained windows scroll rather than hide controls. Explanation views occupy
  exclusive rows, and changing tabs no longer disables the whole tab tree.
  Following the retained native geometry failure, the body has an explicit
  percentage row and ordinary viewport panels own the scaled content extents;
  nested tab content also registers its actual minimum size for scrolling.
- Configuration distinguishes the executable, installer-selected target and
  observation scope. The exact watch root, registry request, database and mode
  are visible before Start. Existing-root and outside-database checks remain.
- Results separate installer raw exit code, capture completion/save, observed
  changes and coverage/error. Nonzero exit is not labelled user cancellation.
  Failed task details retain collector PID, last confirmed phase, structured
  error and bounded original stderr; the last task remains inspectable after
  closing its recording window.
- Exact IDs now take priority over names in public reads. Internal capture,
  paging and explain reads refuse name fallback when a session ID is required.
  A deterministic production-SQLite regression first failed with the old query,
  then passed after the fix. This is an independently discovered association
  defect; it is not evidence of the reported Program Files failure's cause.
- Product version derives from Cargo across Rust, desktop, reports and package
  manifests. The Windows x64 portable package remains self-contained and unsigned.

## User-reported 7-Zip trial (not independently reproduced here)

The user supplied screenshots from Windows 11 running under Parallels. Guest
architecture and actual DPI were not provided; this does **not** establish ARM64
or any particular system-DPI compatibility. No user database or exported report
was supplied to this cloud task; the user's Windows paths are not local cloud
files and were not read.

One screenshot-supported trial of **7z2605-x64.exe** used the same dedicated
existing directory as installation target and observation scope, with the database
outside it. The user reported installer exit 0 and a finished saved session,
process/file ETW active, registry not requested, 1,782 timeline observations,
244 normalized source operations and 107 file-state changes. Reported measured
loss/drop/decode counters were zero, with no drain timeout and Degraded coverage.
Two write-request explanations for `7-zip.chm` and `descript.ion` showed High actor
association to the installer lifetime and resolved operation success; names came
from file_object_cache. Those cached names do not prove the current path or final
file ownership. This is user feedback based on screenshots, not our independent
readback or a newly generated real-installer report. Personal paths, original
screenshots, session/process identifiers and user data are not republished.

An earlier desktop-only observation reported installer exit 1 and a finished,
saved capture. That proves neither cancellation nor successful installation, and
watching the Desktop does not cover other installation destinations.

## Program Files unfinished capture: unresolved Beta issue

A separate user attempt watching the whole Program Files directory remained
`finalizing` / Incomplete, without a confirmed installer exit or end timestamp.
No specific error log or reviewable database was supplied. Size, permissions,
compatibility, collector interruption and other causes cannot currently be
separated. A successful narrower trial is not proof this problem was fixed.
Program Files is not blocked to conceal it; no all-drive experiment was started.

Code review traced the normal source-stop, drain/join, association, save and
readback paths. It found the independent ID/name collision above and diagnostic
prefix loss on oversized/invalid stderr, which are fixed with focused regressions.
Neither is attributed to this user incident without evidence. If a capture fails,
retain the bound DB/WAL/SHM and inspect local task details/unfinished records.
There is no automatic resume or installer-tree kill/cancel operation.

## Preregistered bounded verification

Linux runs production-module SQLite regressions, Python packaging tests, formatting,
Windows-target Clippy/test compilation and .NET compilation. None substitutes for
native WinForms/ETW. Existing Windows CI retains Check, both coverage denominators,
dependency review, companion regression and zero retries with unchanged floors.

The existing desktop harness first runs all read-only checks. Four additional
layout checks use the actual production assembly/CLI: default reported DeviceDpi,
resize/maximize/tabs/paging/explain; explicit 1.5x and 2x control/font stress;
and unchanged fixture bytes. Simulated stress is not a changed system/monitor DPI
and not Windows 11/Parallels validation. Only after these pass may one controlled
GUI-started ETW fixture run through Start, save, exact auto-open, explain and export.
Native installer-picker navigation/cancel and failure preservation remain required.
No third-party installer, formal 32-attempt plan, scale trial, ownership study or
cleanup research is added. Actual unavailable conditions will remain unverified.

## Retained failures and diagnostic boundary

[Attempt history](examples/stage-one-beta-attempts.json) keeps the earlier
cancelled preflights, the actual Windows UTF-8 test-reader failure and the first
native scaled-layout failure. The latter passed all 49 quality gates and all 22
existing viewer checks, plus default 96-DPI layout; the 1.5x and 2x simulated
conditions could not bring the complete NextEvents button into the viewport.
The install protocol check passed, but the five dependent product/capture checks
were not run and no real ETW installer capture occurred.

A single follow-up used the existing desktop-viewer plan with zero native
captures. Numerical scroll/pager geometry and failure screenshots confirmed the
implicit AutoSize body row extended beyond its parent while the outer virtual
scroll size stayed unscaled. The pager fit horizontally. This is diagnostic
evidence, not a repaired acceptance result; the complete reachability assertions
remain required. Source tags cannot replay
capture markers, snapshot fixtures require explicit opt-in, and historical scale
workflows now require a separate manual dispatch. A normal main merge without
capture markers runs ordinary quality/read-only checks, not those experiments.

The diagnostic source's PR also exposed an existing CI cache collision: a
restored synthetic fixture directory was correctly refused instead of overwritten.
Non-Cargo test outputs now use a run-and-attempt-specific root; existing evidence
and useful build caches remain intact. This infrastructure failure supplies no
installer or ETW evidence.

## Actual Windows package result

[CI 41834806392](https://github.com/LuckyLivio/Contain/actions/runs/41834806392)
passed on Windows Server 2025 AMD64, build 10.0.26100, image
`win25-vs2026/20261030.294.1`. Actual DeviceDpi was 96 and work area 1024 × 720.
The same frozen UI, CLI and runtime passed all 22 read-only checks, four layout
checks and six GUI record checks. Native installer-picker navigation/cancel,
duplicate Start, conservative window closing, failure details after closing the
task window, exact-session auto-open, explain and export passed. The 1.5x/2x
conditions remain simulated control/font stress; virtual-canvas screenshots show
the current scroll position, not all content at once.

The GUI started the controlled installer once, after configuration and preflight.
It observed preparing → capturing → finalizing → finished, installer exit 0,
collection completion and saved data separately. The exact session is
`dbf73c05-1220-46c1-a5c4-ad31104bec1c`, with 13 file and three registry-value
state changes and 219 real source events. The deliberately reused record name
matches an older synthetic fixture; session identity, not its name or latest-row
position, selected the real record.

The selected `write_requested` EtwFile event is
`e059368a-a322-40e4-972e-90f2a455a943`, ticks `134382718063537053`.
Its High writer is the verified child instance PID 7396 / creation time
`134382718063413983`, linked to the managed installer PID 1436 / creation time
`134382718063178942`. The existing harness matched the selected operation against
the independent fixture oracle. Local review rechecked the published summaries,
actual report, IDs/times and hashes; it did not execute another ETW capture or
read the private raw database/oracle. High remains actor association, not final
ownership.

Reported ETW event/buffer loss, dropped events, context loss, decode errors,
snapshot gaps and notification gaps were 0; drain did not time out. Coverage
still is **Degraded**: scope cannot prove a machine footprint, and 364 registry
provider records lacked a verified absolute path. Different received/retained
counters have different denominators and are not an accuracy percentage.

The 49 required quality gates passed with unchanged 66.5% line / 70% function
floors. nextest ran 331 passing tests and skipped seven named experiments;
Companion separately ran its one required regression. Zero doctests are not
feature validation. Workspace line/function coverage was 75.760745% / 77.025232%;
the original-package denominator was 72.972387% / 75.514019%. These overlapping
executions include inline tests and shared source aliases. Changed ID-read and
explain regions were inspected; SQLite error propagation and zero-count
instantiations remain partly uncovered. No .NET or LLVM branch coverage was
measured. No new fuzz campaign, benchmark, third-party install, formal 32-attempt
or scale plan ran.

[Reviewed result and actual summaries](examples/windows-stage-one-beta-41834806392.json)
include exact identities, source accounting and unexecuted conditions.
[The actual controlled-fixture report](examples/stage-one-41834806392-controlled-report.html)
is byte-identical to the GUI export and is not the user's 7-Zip report. It was
reviewed for personal paths, SIDs and network/script content before copying.
No user database, original user screenshot or private raw log was published.

Actual application screenshots, copied without image modification:

- [New record](examples/stage-one-41834806392-new-record.png)
- [Running](examples/stage-one-41834806392-recording.png)
- [Automatically opened result](examples/stage-one-41834806392-result.png)
- [Selected real source event and explanation](examples/stage-one-41834806392-event-explain.png)
- [Simulated 200% stress, scrolled viewport](examples/stage-one-41834806392-simulated-200-percent.png)

## Release and repository closure

The immutable `v0.3.2-beta.1` tag points to the validated source above. The ZIP is
76,802,668 bytes, SHA-256
`4b5c533b3e45e65fcd579ca45475c92e6e027144d0c73ba3ee6a97bf8a46192f`.
All 477 package members and 476 content-manifest entries matched; `SOURCE.zip`
matched all 357 tracked files at the tagged revision. Desktop is Release /
self-contained .NET 8.0.33, built with SDK 10.0.403; core and controlled fixture
are MSVC debug. The package is unsigned and carries no release-performance claim.
[Build identity](delivery/v0.3.2-beta.1/BUILD-IDENTITY.json) records the configuration.

Cloud API/Git authentication worked, but `uploads.github.com` returned 401 when
uploading the draft's first asset. The retained draft has no replacement asset.
A separately invoked repository workflow uses its standard GITHUB_TOKEN to
upload the exact verified artifact and reviewed hash/identity files. It never
rebuilds, captures, moves the tag, clobbers an asset or edits a published release.
The upload and publication result is authoritative at the Release link above.

The authorized normal [PR #3](https://github.com/LuckyLivio/Contain/pull/3) brings
this exact Beta source plus current documentation/release entry into main.
Documentation and publication tooling do not alter the tagged runtime files.
Old delivery links and failed attempts remain in [the history index](delivery-history.md).
After merge only redundant branches whose commits are retained in main and the
Beta ancestry may be removed. User records, logs, historical evidence and useful
build caches remain. Windows 11/Parallels actual system DPI, guest architecture
and a clean Windows 10/11 manual trial remain unverified; the Program Files
incident above remains unresolved.
