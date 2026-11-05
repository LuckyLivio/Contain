# Windows Beta 4: a readable saved-record overview

Version **0.3.2-beta.4**, frozen source
`681aad7cfb938d6d6204b2c1cc9cbd7431f73674`.
[Windows x64 ZIP](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.4/contain-0.3.2-beta.4-windows-x64.zip) ·
[Release](https://github.com/LuckyLivio/Contain/releases/tag/v0.3.2-beta.4) ·
[SHA-256](https://github.com/LuckyLivio/Contain/releases/download/v0.3.2-beta.4/SHA256SUMS.txt).

This iteration changes the saved-record overview, exact-session binding and
detail navigation. WinForms, the CLI contracts, database schema and capture
protocol remain in use. Personal history and Beta 2 progress/failure feedback
retain their existing behavior.

## Reading a record

The default overview separates four results:

- Installer: the persisted exit code, including a nonzero code without guessing
  its cause; missing exit evidence stays unconfirmed.
- Record: saved session end information separately from successful database
  readback. A partial readable record is not a completed session, and a historic
  capturing/finalizing phase does not prove a live collector process.
- Scope and coverage: saved file roots and registry scope, with a short Chinese
  explanation of limited coverage (Degraded) and incomplete evidence (Incomplete).
  Paths remain available in full and can be copied.
- Changes: file, registry-value and configuration entries from the same backend
  diff as the state-change page. Event, source-operation and state-change counts
  remain separate; missing measurements do not become zero.

**查看变化** and **查看事件与依据** navigate to the selected session's existing
views. **技术详情** expands source status, statistics, gaps, warnings and process
evidence. Important limitations remain visible in the summary. Loading or failure
clears the previous record rather than showing it under a newly selected title.
Live-task information is only associated with a matching database and session.

The overview does not establish final ownership or safe deletion. Old databases
remain read-only. Missing legacy quality metadata retains the backend's explicit
qualification; a default Degraded label is not a recovered historical measurement.

## Acceptance plan

The existing exact-source quality gates and 22 viewer, four layout, eight progress
and eight personal-history native checks remain. Ten focused overview checks add
saved exit 0 / finished / Degraded, nonzero installer exit with readable records,
and unfinished records with partial evidence; zero versus absent statistics;
same-name UUID selection; default and external old databases; query failure;
actual packaged EXE navigation and cold restart; long text and expanded layout.

Only guarded isolated databases and explicitly synthetic fixtures are used.
This result-display iteration requests zero new captures using the existing
desktop-only acceptance path. Previous real-ETW acceptance remains historical;
it is not counted as a new Beta 4 measurement. Native DPI and simulated 150%/200%
control scaling are reported separately.

## Actual Windows acceptance

[Push CI 42063050676](https://github.com/LuckyLivio/Contain/actions/runs/42063050676)
and [PR CI 42063061641](https://github.com/LuckyLivio/Contain/actions/runs/42063061641)
passed on the final runtime source / identical PR merge tree. All **50 quality
gates** and **52 native checks** passed: viewer 22, layout 4, progress/failure 8,
personal history 8 and overview 10. Failed, skipped and unexecuted native checks
were zero. CI used Windows Server 2025 x64 build 26100, actual DeviceDpi **96**.
Check, Coverage, CoverageOriginal, Dependencies and Companion retained their gates.
nextest passed **338**, with seven existing skipped executions and zero retries;
Companion separately ran its ignored spool regression. The other six skipped
executions are three storage-performance experiments imported twice. The 24 Python
suites passed **383 tests**, including 35 packaging-contract tests.

Workspace Rust line/function coverage was **14836/19598 (75.701602%)** /
**1207/1565 (77.124601%)**; original-package coverage was
**10348/14208 (72.832207%)** / **845/1117 (75.649060%)**. Floors remain 66.5% / 70%.
These denominators include inline tests and shared aliases; they do not measure
managed code, branches or production-only coverage. No Rust production behavior,
parser or hot path changed, so no new fuzz campaign or throughput claim is made.

The same-source local candidate passed all **52 native checks** on Windows 11 Pro
x64 build 26300 at actual DeviceDpi **144**. The **unmodified downloaded final CI
package** then passed its ten focused overview checks on that machine, with 121
assertions and no failures, skips or unexecuted checks. Its exact UI DLL, launcher,
CLI and manifest hashes matched the accepted package. The local harness was built
from the same source and loaded the delivered UI DLL. This final-package run
reused isolated data and performed no installation capture.

The packaged EXE fully exited (process 41592, exit 0), restarted from another
working directory (process 51928), and opened the same explicitly synthetic
session `synthetic-finished` and event `synthetic-finished-event-0`; summary and
production explanation identities remained consistent. CI independently recorded
process 8860, exit 0, followed by process 4188. These are synthetic persisted-record
tests, not new real-ETW acceptance.

| Persisted fixture | Default result | Verified detail behavior |
| --- | --- | --- |
| Exit 0, finished, Degraded | Exit code 0; session ended; saved record readable; coverage limited | Counts match diff; current-session changes/events and full diagnostic open |
| Exit 1603, finished, Degraded | Actual nonzero exit, without inventing a cause; saved record still readable | Same-name UUID selection and external/default databases with different exit values stay separate |
| No exit/end confirmation, Incomplete | Exit unrecorded; session incomplete; partial saved evidence readable | No claim of a currently running collector; full long source error retained |

Missing versus zero, failed-query clearing, cancelled late errors, the first
non-first-row accessibility selection, an independent mouse selection, scope
copy, cold restart, paging, explanation and report behavior passed. External-view
tests use an existing-format historical-name fixture; schema migration/read
compatibility remains covered by the existing core regressions rather than a new
claim of native old-schema GUI acceptance. Simulated 150% and 200% control/font
scaling both passed; they are separate from actual 96/144 DPI and do not certify
real monitor transitions.

Actual final-package screenshots on Windows 11 / 144 DPI use reviewed synthetic
data: [exit 0](delivery/v0.3.2-beta.4/overview-success.png),
[nonzero exit](delivery/v0.3.2-beta.4/overview-nonzero.png),
[unfinished](delivery/v0.3.2-beta.4/overview-unfinished.png).

## Immutable artifact

The unmodified accepted ZIP is **76,522,212 bytes**, SHA-256
`0afacc6cbf163ce336484d7256a32b9c47d018960f7c52fd8e392b2c4064460f`.
All **476 members**, **475 content-manifest entries** and **390 tracked source
files** inside `SOURCE.zip` were checked against exact Git source. Release
WinForms and the MSVC debug CLI are version **0.3.2-beta.4**; the bundled
self-contained .NET runtime is **8.0.33**. CI used SDK 10.0.403 and Rust/Cargo
1.99.1. The package is unsigned and carries no release-performance claim.

[PR #7](https://github.com/LuckyLivio/Contain/pull/7) adds delivery-only documentation
after the frozen runtime source. The tag and ZIP retain that exact tested revision;
no rebuild or repack follows acceptance. Beta 3 and earlier tags/assets, local
backups and history remain preserved.
[Build identity and reviewed aggregates](delivery/v0.3.2-beta.4/BUILD-IDENTITY.json).

## Known limits

The first local candidate `5ac26283d3ddb04d856a0d3803dd97592a9a7a7d`
passed the 36 Check gates and nine of ten focused overview checks. Actual Windows
144-DPI inspection found an unnecessary horizontal scrollbar and a limitation
notice below the viewport; the corrective layout reserves scrollbar space and
keeps the notice with the actions. An initial direct selection of a non-first
session exposed a native focus/selection ordering defect: transient row-zero
selection started a query before the intended selection completed. The corrected
handler clears old results immediately. Candidate `0247e8b` showed that merely
deferring the query was insufficient: accessibility selection can cross another
native boundary while the first read is pending. Native selected/focused state
confirmed the intended row, while a separate mouse probe distinguished its event
sequence. The final handler keeps selection available during record reads and
immediately cancels superseded reads; other operations retain their existing
guards. Both failures and the explicit non-first-selection regression are
retained; no retry or alternate-row workaround replaces acceptance.

Candidate `9aae663` passed all ten overview checks, the original viewer/progress/
personal-history checks, native 144-DPI layout and simulated 150% scaling.
Simulated 200% exposed clipped note text; the final layout measures each actual
font, padding and margin rather than fixed logical-pixel allowances. An additional
regression rejects late generic errors from cancelled reads. A bounded actual EXE
probe selected a non-first event through the overview action and explained that
exact event; no event-handler change or new capture was required.

The original whole-Program-Files / Parallels finalizing incident remains
unconfirmed and tracked independently in [Issue #5](https://github.com/LuckyLivio/Contain/issues/5).
No new full-history search, complete export, automatic install-directory discovery,
uninstall or cleanup capability is included. Existing releases, backups and local
history are retained. Windows 10, ARM64, Parallels, third-party installer matrices
and real monitor DPI transitions are not established by these bounded checks.
