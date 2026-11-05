# Windows Beta 4: a readable saved-record overview

Version **0.3.2-beta.4**.
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
control scaling are reported separately. Actual results and immutable delivery
identity are recorded after the final candidate passes.

## Known limits

The first local candidate `5ac26283d3ddb04d856a0d3803dd97592a9a7a7d`
passed the 36 Check gates and nine of ten focused overview checks. Actual Windows
144-DPI inspection found an unnecessary horizontal scrollbar and a limitation
notice below the viewport; the corrective layout reserves scrollbar space and
keeps the notice with the actions. An initial direct selection of a non-first
session exposed a native focus/selection ordering defect: transient row-zero
selection started a query before the intended selection completed. The corrected
handler clears old results immediately and queries the final row after that native
event sequence. The original failure and explicit non-first-selection regression
are retained; no retry or alternate-row workaround replaces acceptance.

The original whole-Program-Files / Parallels finalizing incident remains
unconfirmed and tracked independently in [Issue #5](https://github.com/LuckyLivio/Contain/issues/5).
No new full-history search, complete export, automatic install-directory discovery,
uninstall or cleanup capability is included. Existing releases, backups and local
history are retained. Windows 10, ARM64, Parallels, third-party installer matrices
and real monitor DPI transitions are not established by these bounded checks.
