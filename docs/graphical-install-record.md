# Graphical installation recording MVP (0.3.1)

## Starting point and scope

Development branch: `feat/desktop-install-record`, based on
`bfd936cfc43558f65ede52927161573864276c5d` from `feat/desktop-record-viewer`.
The original clean local `work` branch at `bef6ac17cf3c816733e661fb76dc8974f5a30f12`
was preserved. The verified desktop package source remains
`4b4d38764ed417e6754b5db798488227ec147e21`. Version remains 0.3.1.
The bounded read-only candidate and desktop viewer milestones remain closed.
This milestone stays open until its own acceptance evidence is available.

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

Implementation and verification are in progress. Build success, static images,
synthetic records and zero executed tests cannot close this milestone. Exact
native outcomes and the final package link are added after Windows CI finishes.
System-picker checks are reported separately from injected-dialog UI checks.

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
