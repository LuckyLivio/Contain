# Graphical installation recording MVP (0.3.1)

## Starting point and scope

Development branch: `feat/desktop-install-record`, based on
`bfd936cfc43558f65ede52927161573864276c5d` from `feat/desktop-record-viewer`.
The original clean local `work` branch at `bef6ac17cf3c816733e661fb76dc8974f5a30f12`
was preserved. The verified desktop package source remains
`4b4d38764ed417e6754b5db798488227ec147e21`. Version remains 0.3.1.
The bounded read-only candidate and desktop viewer milestones remain closed.
The graphical installation recording MVP is **closed** for frozen source
`15f214442d69baa0ebd91f5ad12edc5661281779` and the bounded Windows result below.

The implementation retains WinForms, Rust capture, the existing CLI, evidence
model, read-only viewer, fixture, quality entrypoints and Windows CI. No service,
framework replacement, automatic elevation, cleanup or third-party installation
is included. Native executable (`.exe`) launch is the current product boundary;
the controlled packaged fixture is the acceptance target. MSI, service-agent and
updater completeness are not promised.

## Bounded validation plan, declared before native execution

Linux performs editing, runnable Python/offline regressions and cross-build
checks. These cannot establish Windows GUI or ETW operation. A `[desktop-install]`
push selects the existing Windows CI with zero retries and at most **one** real
controlled capture after applicable offline checks. Normal documentation pushes
do not start capture. There is no formal 32-attempt or scale plan in this task.

1. Preserve all existing read-only viewer, CLI, schema, migration, scorer and
   quality/coverage assertions. Exercise structured argument and lifecycle
   validation before launching any installer. Invalid JSON, missing final result,
   unknown state, mismatched identity and nonzero CLI exits must fail visibly.
2. Freeze the actual desktop, CLI, fixture and runtimes with their source,
   configuration and hashes. Validate the production form assembly against it.
3. Through the production new-record controls configure an existing disposable
   fixture scope, separate database, record name and literal arguments. Selecting
   or checking the environment must not launch the installer. Start from the
   product button, refuse a second start, and keep the window responsive.
4. Observe real backend phases, including root exit before final completion,
   conservative window-close behavior, actual ETW providers and retained source
   records. Capture degradation remains explicit and never becomes Complete.
5. Auto-open the exact returned session, select a real source event, run production
   explain and export its local HTML report. Check identity, actual source and
   persisted state. An old or same-name record cannot substitute for this result.
6. Exercise actual system picker navigation/cancel where the Windows CI desktop
   permits it; injected dialog answers only establish configuration/start logic.
   Any unavailable system interaction stays explicitly not run.

Native tests use only isolated, marked controlled fixture paths and matching HKCU
keys. They neither install third-party software nor mutate services/tasks/startup.
Full databases and traces remain private. Only reviewed fixture aggregates,
source/event identifiers, screenshots and local fixture report are published.
Failed attempts are retained and only affected checks rerun after a concrete fix.

## Acceptance status

The **graphical installation recording MVP is closed** for version 0.3.1,
frozen source `15f214442d69baa0ebd91f5ad12edc5661281779`.
[Download the self-contained Windows x64 package](https://github.com/LuckyLivio/Contain/actions/runs/41805319059/artifacts/13311396065).
[Windows CI 41805319059](https://github.com/LuckyLivio/Contain/actions/runs/41805319059)
passed on Windows Server 2025 x64, build 10.0.26100, image
`win25-vs2026/20261030.294.1`. The prior two milestones remain closed separately.

The same frozen production UI assembly, actual CLI and packaged runtimes passed
native verification. The packaged desktop EXE separately passed launch/normal-close;
the integration harness operates the byte-identical production WinForms assembly
and real packaged CLI. Actual native picker keyboard navigation/cancel passed in
that Windows desktop. No script recorded the successful database before the UI.

| Final executed check | Passed | Failed | Ignored / not run |
| --- | ---: | ---: | ---: |
| Existing native viewer plan | 22 | 0 | 0 |
| New recording product plan | 6 | 0 | 0 |
| Actual native installer picker navigation and cancel | 1 | 0 | 0 |
| Explicit synthetic failed-start / read-only DB preservation plan | 1 | 0 | 0 |
| Required Check / Coverage / CoverageOriginal / Dependencies / Companion gates | 49 | 0 | 0 |
| Rust nextest | 328 | 0 | 7 ignored |
| Python unittest suites (23 suites) | 360 | 0 | 0 |
| Windows directory identity, including real 8.3 alias | 7 | 0 | 0 |

The viewer plan passed 230 assertions; the new product/interaction plan passed 156.
These overlap broader unit executions and are not unique test or completion
percentages. Workspace coverage lines/functions: **75.731560% / 76.933333%**;
original packages: **72.941091% / 75.422139%**. Floors remain 66.5% / 70%.
Workspace coverage executed 328 passing tests / 7 ignored; original-package
coverage executed 241 / 4 ignored. The existing separate companion regression
passed once. Doctests executed zero tests and provide no feature evidence.
Unchanged fuzz seed checks passed; no fuzz campaign or performance run was added.

Affected lifecycle helpers and uncovered capture/CLI regions were inspected.
The live happy capture path is not covered by instrumented unit coverage; its
separate native execution supplies runtime evidence. Source aliases and zero
instantiations remain in the artifact. No .NET or LLVM branch percentage is claimed.
[Reviewed exact results, versions, counts, coverage scope and failures](examples/windows-graphical-install-41805319059.json).

### Actual GUI-started session

- Session: `b3c37c62-b384-4c78-863b-77981d2b1050`.
- Managed installer PID **5516**, creation FILETIME **134382584023751165**,
  exited **0**; collection ended and data saved, persisted state `finished`.
- Actual phases: preparing → capturing → finalizing → finished. Closing the active
  window kept the task running; duplicate Start and task-state clicks did not
  create another installer or session.
- **219** real file/registry source events, **314** retained timeline observations.
  Admission count **337** has a different scope and is not an accuracy denominator.
  Measured ETW event/buffer loss, application drops, context loss, decode errors,
  snapshot/notification gaps: **0**; drain timeout: **false**.
- Event `afee040b-5667-4580-8fa6-32c6f4458470`: `write_requested`, `EtwFile`,
  High, writer PID **8700**, creation **134382584023940759**, event FILETIME
  **134382584024071100**. PID/birth/path/operation/time match the independent
  fixture syscall journal. Production explain matched the same event/session.
- Coverage is **Degraded**, not Complete: scope is bounded and **591** global
  registry-provider records lacked verified absolute paths. No resource prefix
  or actor was invented to hide this gap.
- The exact auto-selected record coexists with three earlier records, including
  its deliberately reused name **Synthetic interrupted**. That name tests
  same-name isolation; the new UUID and source events are real ETW, not a model.
- [Actual exported local HTML](examples/install-41805319059-report.html), SHA-256
  `c1fc99bc4982ebaf05f52569f9d1cc3706e002c6f3d2b5076b833dbc4807caf8`.
  Export includes this record, the current unfiltered event page and selected
  explain, not all event history. The public copy is byte-identical and passive.

[New record configuration](examples/install-41805319059-new-record.png),
[recording](examples/install-41805319059-recording.png),
[result](examples/install-41805319059-result.png),
[automatic session selection](examples/install-41805319059-autoopened-session.png),
and [real event explain](examples/install-41805319059-real-event-explain.png)
are unmodified images of actual Windows production controls. Advanced settings
were expanded for fixture arguments; their default-collapsed state was checked.

### Package audit and use

The ZIP contains 477 members / 76,491,689 bytes. All 476 CONTENTS entries and
474 frozen-file hashes were independently checked after download. Every one of
342 SOURCE.zip files was compared byte-for-byte with the exact Git tree above.
The viewer is Release, core/fixture are MSVC debug, and included .NETCore /
WindowsDesktop runtimes are **8.0.33**; development tools are not user prerequisites.

ZIP SHA-256: `11cda20dcea4e8d338da940fc645f8ff24328ecc62c83c871852262f3d6d596e`.
SOURCE.zip SHA-256: `d1054185d419dce86d723b2068f686f92e3b0e3902903dc77a9253667f8cba8b`.
`BUILD.json`, `CONTENTS.json`, `VALIDATION.json`, licenses and `TRY.md` travel with
this unsigned trial. Validation says `bounded_graphical_install_verified` and
`graphical_install_milestone_closed=true`; this is not a tagged Release.

Extract the complete contained ZIP, double-click Contain.Viewer.exe, choose
新建安装记录, select the supported EXE, name, existing observation directory and
outside database, check the environment, then click 开始记录. Wait for finalization
and exact auto-open, select an event and export. Package TRY.md includes an
Explorer-only controlled fixture walkthrough and its two literal --root arguments.
Do not use demo.ps1 as the product launcher.

### Limits and separate next work

Only the controlled standalone EXE is validated in this round. Required ETW
availability is rechecked before launch; explicit snapshot mode is different.
Live nonadministrator trace-denial UI, clean Windows 10/11 manual operation,
native watch-folder/report-save dialog navigation and .NET code coverage were
not executed. Unsupported providers/resources, service agents/updaters/MSI,
complete footprints, final ownership, cancellation/resume and safe cleanup are
not promised. Collector crashes can leave unfinished journals; reopen the bound
DB rather than assuming completion or deleting evidence.

Next work is a separate small trial plan: choose 2–3 approved real standalone EXE
installers on a disposable Windows VM, declare permissions and existing scopes,
then inspect each saved record and failures. No third-party installation, formal
32-attempt protocol or scale run was started in this round.

## Branch cleanup policy

All existing remote topic tips were inspected before changes. Each was reachable
from the retained desktop branch; none had been merged into `main`. After pushing and verifying `80342317422499da6d755f7e03f378adb22da44e`,
11 redundant remote topic refs were removed atomically. Every removed exact tip
was rechecked as reachable from both retained desktop branches. Remote now retains
`main`, `feat/desktop-record-viewer` and this delivery branch. Local stale remote
refs were pruned; the original local `work` branch remains. The
[exact-tip audit](examples/desktop-install-branch-cleanup.json) preserves the list.
No history rewrite, automatic merge, default-branch push, tag or Release is part
of this task. The local original `work` branch is retained.

## Retained validation attempts

- Linux integrated preflight: 83 passed / 0 failed / 8 ignored in the actual
  production-module offline tests; zero-test binaries/doctests are not feature
  evidence. Windows-target Clippy and test executable compilation passed;
  neither executes Windows behavior. Production desktop and harness Release
  compilation passed with zero warnings/errors. Python packaging regressions:
  19 passed. Linux native-directory identity checks: 7 skipped, not validated.
- A direct `cargo test -p ferrisetw` attempt failed because the patched dependency
  is excluded from the workspace. Its exact production ownership helper was
  imported by the existing offline package instead; all four ownership tests
  passed. No provider is started by these synthetic tests.
- [CI 41795312477](https://github.com/LuckyLivio/Contain/actions/runs/41795312477),
  source `80342317422499da6d755f7e03f378adb22da44e`, was deliberately canceled
  during quality-tool installation, before quality/native/ETW execution. Review
  found the existing numeric coverage exporter did not yet list the newly changed
  lifecycle functions. The following source adds their region evidence without
  changing coverage floors, exclusions or tests. This is not a successful native
  attempt and supplies no capture evidence.

- [CI 41795659625](https://github.com/LuckyLivio/Contain/actions/runs/41795659625),
  source `3f81c7f56ba6e3c103e9dd6eab8671d0664a3dd5`, passed all quality gates
  and all 22 native existing-record viewer checks. The install protocol check
  passed; configuration failed with `ArgumentOutOfRangeException` before any
  installer or real ETW capture. Four dependent product checks were not run.
  [Original failed install summary](examples/desktop-install-attempt-41795659625.json).
  The new form initialized NumericUpDown.Value (1500/10000) before raising its
  default Maximum of 100. The fix sets Maximum first; code review checked the
  remaining new constructor properties for the same error. The affected
  new-form/synthetic-failure checks and their previously unexecuted recording
  dependents need native execution on the corrected frozen package. The failed
  [diagnostic package](https://github.com/LuckyLivio/Contain/actions/runs/41795659625/artifacts/13306645145)
  remains available and does not establish graphical acceptance.

- [CI 41798143791](https://github.com/LuckyLivio/Contain/actions/runs/41798143791),
  source `72e0226e20a3afcb0f92aad42954340682c4e724`, fixed construction and passed
  all quality/viewer checks, synthetic failure preservation, and actual native
  picker navigation/cancel. It executed **one** genuine GUI-started ETW capture:
  session `3c6f1516-3177-4cdb-911b-d711e237b583`, 219 file/registry source events,
  High file event `b5757ee6-044f-4870-aaf9-5f35b90a11d8` matched independent truth.
  Installer exited 0; capture finished/saved with Degraded quality. Measured ETW
  events/buffers lost, application drops, context losses, decode and snapshot/
  notification gaps were zero; registry path gaps remain explicit. Automatic
  exact-session opening, explain and actual HTML export passed. Five of six
  product checks passed; the duplicate/task-state check failed with
  InvalidOperationException: the existing visible task form was passed to Show
  again. The product fix calls Show only for a hidden form, then Activate.
  [Unmodified failed summary](examples/desktop-install-attempt-41798143791.json)
  and [diagnostic ZIP](https://github.com/LuckyLivio/Contain/actions/runs/41798143791/artifacts/13307757308)
  remain available. No result was discarded or promoted to accepted.
  The changed final desktop assembly requires one bounded repeat of the affected
  duplicate/task-state behavior and the same-package GUI capture chain; earlier
  source/package evidence cannot validate replacement bytes. No scale,
  third-party installer or formal 32-attempt plan is added.

- [CI 41801560771](https://github.com/LuckyLivio/Contain/actions/runs/41801560771),
  source `65739c3fc99f5d5de610db3e553927a3ba3a5c5e`, passed Check/workspace
  coverage/dependencies/companion but failed CoverageOriginal before packaging
  or another native capture. The existing write-diagnostic test helper used
  PID+QPC alone for temporary names; concurrent calls can share a QPC tick.
  `failed_api_is_logged_with_error_and_independent_clock_brackets` hit Win32 183
  AlreadyExists at its helper's create_dir, not its API-evidence assertions.
  [Retained failure](examples/desktop-install-attempt-41801560771.json).
  The correction changes only test naming: PID8+QPC16+checked atomic sequence8,
  preserving the exact 32-hex name constraint. A deterministic 512-name concurrent
  same-clock regression and overflow regression are added. No filesystem retry,
  deletion, assertion reduction, floor change or application code change is used.
  The failed quality gate and previously blocked same-package native plan must
  run on the corrected source; no additional scale or third-party plan is added.
