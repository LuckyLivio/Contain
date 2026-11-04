# Phase-one Beta: Windows installation records and evidence viewing

The three earlier milestones remain closed. This delivery retains WinForms, the
Rust core and CLI/schema-3 contracts. The current candidate is **0.3.2-beta.1**;
publication and final Windows evidence are pending the checks below.

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

## Changes prepared for validation

- Both forms have an explicit 96-DPI design baseline and PerMonitorV2 bootstrap.
  Labels, row heights and action/paging buttons use preferred content sizes;
  constrained windows scroll rather than hide controls. Explanation views occupy
  exclusive rows, and changing tabs no longer disables the whole tab tree.
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

## Release and repository closure

After exact-source validation, a new immutable `v0.3.2-beta.1` tag and GitHub
Prerelease will carry the same verified x64 ZIP, SHA-256 and build identity.
User authorization includes a normal PR merge into main after checks and branch
rules. The tag/assets will not move when subsequent documentation is merged.
Old delivery links and failed attempts remain in [the history index](delivery-history.md).
Only redundant task files/refs whose commits are safely retained may be cleaned;
user records, logs, evidence and useful build caches stay intact.
