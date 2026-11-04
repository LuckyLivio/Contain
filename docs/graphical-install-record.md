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
from the retained desktop branch; none had been merged into `main`. Cleanup can
remove redundant topic refs after verifying the new development branch remotely,
while retaining `main`, `feat/desktop-record-viewer` and this delivery branch.
No history rewrite, automatic merge, default-branch push, tag or Release is part
of this task. The local original `work` branch is retained.
