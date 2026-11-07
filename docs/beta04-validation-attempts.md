# v0.4.0-beta.1 native validation attempts

These are ordered attempts, not a selection of green reruns. The fixed plan is
[the migration and design document](beta04-design-and-migration.md). Every product
ZIP is frozen before its native checks; each failed artifact remains in Actions.
No previous WinForms report establishes acceptance of this WPF desktop.

Reviewed public aggregates are also archived under
[`examples/beta04-attempts`](examples/beta04-attempts/index.json), preserving
their original bytes and SHA-256. This archive contains no database or full log.

## M1 — first native viewing preview

- Source: `e308844e3c1f6be5f530b248d3de488d9dec4be8`.
- [Run 42337219333](https://github.com/LuckyLivio/Contain/actions/runs/42337219333):
  **failed**, 1 fixed check passed, 1 failed, 12 not run; zero retries and zero
  requested or executed installations. Windows Server 2025, build 10.0.26100,
  x64, actual screenshot DPI 96, window 1024 × 720 pixels.
- [Evidence artifact 13559798511](https://github.com/LuckyLivio/Contain/actions/runs/42337219333/artifacts/13559798511),
  [frozen package artifact 13559664672](https://github.com/LuckyLivio/Contain/actions/runs/42337219333/artifacts/13559664672).
- ZIP: 70,076,752 bytes; SHA-256
  `4869f7d03e4f158a25e522159e9e0b06dc3a8fe4bae8de6f7e27ceed25c1a3f5`.
  All frozen package members remained unchanged after the failed attempt.
- Ready completed in 1,660 ms. The real empty-library page was visible; no
  personal database was created. The following `NewInstall` automation action
  raised `InvalidOperationException`. It was not a startup timeout.

The pinned WPF UI 4.3.0 source exposes a reproducible action-contract defect: its
`NavigationViewItemAutomationPeer` exposes `SelectionItem`, whose `Select` calls
`NavigationView.NavigateInternal`. That path requires a `TargetPageType`; our
action navigation instead used `Click` handlers and `ReplaceContent`. The
component's ordinary `OnClick` supports this action-only case, but its selection
peer does not. Contain's action-navigation peer corrects that contract while
preserving the actual component, keyboard behavior, and click handler. It must
not be hidden by weakening the native test to mouse fallback. **M1 did not log
which external pattern was available or selected.** The later direct-control
reproduction proves this contract defect, but does not by itself confirm the
original external M1 exception's precise cause. M2/M3 retain that distinction.

Pinned upstream evidence:
[peer](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/NavigationView/NavigationViewItemAutomationPeer.cs),
[navigation](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/NavigationView/NavigationView.Navigation.cs),
[ordinary click](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/NavigationView/NavigationViewItem.cs).

The actual screenshot also exposed dark text on teal primary buttons. Contain's
global `TextBlock.Foreground` overrode the component's foreground inheritance.
Removing that global setter restores WPF UI's normal/pressed/disabled primary
button colors while body text inherits the window foreground. This is a product
style correction, not an edited screenshot; a subsequent actual screenshot must
confirm it.

Before M1, 34 deterministic model checks, 22 package guard checks, locked Windows
target publishes, and the NuGet advisory query passed. Those remain build/logic
evidence, not completion of native viewing or installation acceptance. M1 supplies
no search, export, cold-start, or ETW success claim. Historical Beta.10 issues
#16, #17, and #18 are neither explained nor reclassified by this result.

## M2 — action-navigation and button contrast correction

Predeclared before running: one more isolated native viewing preview, still zero
installations. The product adds a small action-only automation peer to the real
WPF UI navigation control and restores foreground inheritance. The native test
now requires `Invoke` for these five action entries; it does not fall back to
mouse selection. A separate Windows STA control regression uses the actual
production control source, first reproduces the pinned library's null-target
selection failure, then checks invocation, names, disabled and queued-disable
behavior. The actual packaged executable must also pass the navigation action.

Boolean visibility probes may report a temporarily absent element during a
bounded transition. Required actions/text remain strict; unexpected UIA errors,
permanent absence, wrong session and early process exit still fail. New action
diagnostics retain the exact invoked pattern with a fixed 128-row bound and no
extra UIA queries. M1 is retained regardless of M2's outcome.

M2 source `08827096f893ad271bba0593e954329eae039343`,
[run 42338017143](https://github.com/LuckyLivio/Contain/actions/runs/42338017143),
also **failed**: 1 fixed check passed, 1 failed, 12 not run, zero installations.
The six Windows in-process control contracts passed, including reproduction of
the original null-target selection exception. The actual executable reached
Ready in 1,838 ms, but the separate UIA client reported
`RequiredInvokePatternMissingException` for `NewInstall`. This is not repaired
merely because the direct control peer test passes. The failure screenshot
confirms white, readable primary-button text at actual 96 DPI / 1024 × 720.
[Evidence artifact 13560701084](https://github.com/LuckyLivio/Contain/actions/runs/42338017143/artifacts/13560701084)
and [unchanged frozen package artifact 13560825660](https://github.com/LuckyLivio/Contain/actions/runs/42338017143/artifacts/13560825660)
remain available. Full viewing and installation acceptance remain incomplete.

## M3 — one bounded external control diagnosis

Predeclared before execution: one isolated `FluentWindow` host with the actual
M2 action-navigation source, the same WPF UI version and a standard button for
comparison. A separate process probes only those two fixed IDs, records their
class/role/patterns, duplicate count and up to three ancestor classes, and invokes
each supported action once. A normal-close deadline bounds cleanup. There is no
personal database, core launch, packaged desktop suite, installer or ETW capture.
`completed` means the diagnostic ran; it cannot mean product `accepted`. The
original failing code is unchanged during this observation. Another product
patch and full preview require evidence from this discrepancy.

M3 source `431fd92ac44c10bbc61cc50c51c6781077835fde`,
[run 42338832102](https://github.com/LuckyLivio/Contain/actions/runs/42338832102),
completed the diagnostic with `accepted=false`, one host, normal exit 0, no
forced exit, no retries and no ETW. The unique, enabled, onscreen `NewInstall`
match was **`ItemsControlItem` / `ControlType.DataItem`**, under the component's
`NavigationItems` ItemsControl. It exposed only SynchronizedInput. The standard
button in that same process exposed Invoke and recorded exactly one click.
This directly identifies the external item-wrapper path masking the custom
element peer; it is not a general failure of UIA or the .NET 10 button provider.
It establishes the current M2 discrepancy; the unlogged original M1 pattern
selection remains separate.

## M4 — real button inside component navigation, then viewing preview

Predeclared before execution: retain WPF UI NavigationView and its responsive
pane, move the fixed action identity and operation onto an actual WPF UI button
inside each navigation item, and preserve selected, focus and disabled states.
The Windows gate combines the six deterministic local control cases with six
external-process cases, including unique button identity, exactly-once invoke,
disabled/refused action, reenabled action, keyboard activation and normal exit.
The queued-disable boundary remains covered by the deterministic local cases.
Only if this gate passes may one new frozen packaged viewing preview proceed;
it still requests zero installations. All control failure summaries are retained
even when they stop packaging. M1, M2, and M3 evidence remain unchanged.

M4 source `63f264a0b397e7cf582b25505e1ba095e0d1b6fd`,
[run 42339828453](https://github.com/LuckyLivio/Contain/actions/runs/42339828453),
**failed** after its 12 control regressions passed. The actual package reached
Ready in 1,513 ms; Invoke, native EXE-picker cancellation, return navigation and
keyboard navigation all completed. The first product close then exceeded its
unchanged 20-second deadline. The failure screenshot shows WPF's explicit
`InvalidOperationException` rejecting `Close` while the window is already
closing. `Records.CloseAsync()` can complete synchronously for the empty store;
the handler then calls `Close()` before the original Closing event has returned.
This is a new product lifecycle error, not a navigation failure or an explanation
of historical C1/D5. The summary's last stage still says keyboard navigation;
its call site identifies `CloseAsync`, and that phase label will be corrected.

The run passed 1 complete fixed group, failed 1 and left 12 unexecuted. There
were zero installations. Its screenshot also exposes a long configuration form
with the primary action below the visible content. Evidence
[13561162276](https://github.com/LuckyLivio/Contain/actions/runs/42339828453/artifacts/13561162276)
and the unchanged frozen package
[13560724531](https://github.com/LuckyLivio/Contain/actions/runs/42339828453/artifacts/13560724531)
remain retained.

## M5 — defer final close and keep the primary action visible

Predeclared before execution: defer the final close to the dispatcher after the
cancelled Closing event returns, retaining the active-task guard, read/export
cleanup and existing deadlines. Keep Start in a fixed task footer while the
configuration and advanced options scroll. The next single frozen-package
viewing preview performs the same checks and zero installations. Label the
normal-close phase accurately. Use the existing exact-record bounded helper at
the one same-name transition that previously made an immediate required lookup;
this preserves its exact identity assertion and deadline, without retrying a
failed action or accepting an old session. Full Windows controls and the actual
empty-store normal close remain mandatory before downstream viewing checks.

M5 source `94ec2a274b47616b3dba645e15a43704c57f488d`,
[run 42340263767](https://github.com/LuckyLivio/Contain/actions/runs/42340263767),
**failed**: 4 complete fixed groups passed, 1 failed, 9 not run, zero installations.
The actual empty and loaded windows now exited normally with code 0 in 83 ms and
71 ms, within the original 20-second bound. Finished, nonzero, incomplete and
missing-value presentation passed. The 130-event fixture returned the exact
53 matching IDs in order across 50 + 3 pages; selected explanation and copied
ID were also correct. The first full-match export then timed out discovering
a native picker (15 seconds).

The screenshot shows that selecting/copying the explanation scrolled the whole
events page, moving its search/pagination/export controls behind the fixed page
header. The test's click check only compared bounds to the main window and
process, so it did not reject a location covered by another part of the same
window. This is evidence of a layout and hit-target verification defect; it does
not establish a broken SaveFileDialog or exporter. The frozen package remained
unchanged. All original reports and screenshots are retained by that run.

## M6 — fixed event operations and verified click targets

Predeclared before execution: keep the event search, pagination and export
controls outside the scrollable event/explanation body. Preserve all query,
selection and export contracts. Before a native chooser click, require that UIA
hit-testing identifies that exact action or one of its descendants, with bounded
ancestor traversal. Do not fall back to a blind coordinate or another control.
Use the same single viewing preview and zero installations, retaining existing
deadlines and all M5 results. Inspect actual screenshots before final capture.

The M5 repository CI [42340263909](https://github.com/LuckyLivio/Contain/actions/runs/42340263909)
(and M4 CI 42339828397) found a separate test-tool portability error: the new publisher schema
regression read UTF-8 C# source using Windows' default CP1252 codec. The test now
requests UTF-8 explicitly for its source/report reads. Its 26 checks pass locally
both normally and with Python UTF-8 mode and locale coercion disabled under the
C locale; the next Windows CI must verify the actual platform. No product or
core behavior, assertion or acceptance threshold changes for this correction.

M6 source `37b6d1dbb3a384817c77f70b5318e4b16321f2c6`,
[run 42340821695](https://github.com/LuckyLivio/Contain/actions/runs/42340821695),
**failed** in the new click guard before the first EXE chooser: 1 fixed group
passed, 1 failed, 12 not run, zero installations. The screenshot shows the
chooser button and fixed Start footer visibly present. Native ownership passed;
UIA hit-testing returned Text, then Button, then its ancestors in the product,
but `Automation.Compare` did not match the previously found element. The guard
refused to click before reaching the foreign desktop ancestor. This report does
not yet show the hit button's required-ID or rectangle match, so it cannot prove
the exact provider-identity reason. It does not establish a product chooser
failure. The package remained unchanged; the result and screenshots are retained.

## M7 — unique stable action identity plus actual hit geometry

Predeclared before execution: no product change. .NET WPF's element overload of
`Automation.Compare` compares runtime-ID arrays; it is not a CLR reference test.
For the four fixed native-chooser actions, require one unique target in the
owned main window, Button role, visible/enabled state and matching current
bounds containing the click point. Require that exact stable ID, role and bounds
on the actual hit element or within its bounded 16-node ancestry. Runtime-ID
comparison remains separately recorded rather than serving as the only action
identity. Reject duplicate IDs, siblings/overlays, wrong role or bounds, foreign
ancestry and exhausted traversal. Preserve unexpected UIA exceptions.

Deterministic regressions exercise the actual identity/ancestry guard, including
distinct runtime identities with the same unique stable target, and all rejection
cases above. One new viewing preview with zero installations records only fixed
IDs and per-node match/ownership/geometry booleans. No arbitrary desktop names,
paths or content IDs are collected. M6 remains failed; its unrecorded hit-ID and
geometry cannot be inferred retroactively. If current identity is ambiguous,
the next attempt must fail before clicking rather than fall back or retry.

M7 source `eb512ce59ab7758f9941f7fe66c4f721beba6121`,
[run 42341344681](https://github.com/LuckyLivio/Contain/actions/runs/42341344681),
**failed** before the first chooser click: 1 fixed group passed, 1 failed,
12 not run, zero installations. The target fixed ID was unique and native
ownership passed. The FromPoint Text/Button/Group ancestors had neither the
required ID nor matching bounds, and their rectangles did not contain the queried
point. This rules out treating the result as only a runtime-ID alias. It does
not establish why the provider returned that branch or whether it persists:
both M6 and M7 took one immediate probe. Product bytes were unchanged during
execution. The original public summary is archived as `M7.json`.

## M8 — bounded actionability under the original chooser deadline

Predeclared before execution: no product change and no relaxation of the strict
unique-ID/role/geometry/owned-hit predicate. Wait for that read-only actionability
state before clicking, sharing the existing 15-second chooser budget between
readiness and discovery of the owned native dialog. Cap each probe by remaining
time; click once only after a match. Record counts plus bounded first/last
mismatches. Duplicate IDs, unexpected UIA errors and early process exit fail;
persistent geometric mismatch times out. The modal must also have the exact
fixed title for its requested action, not merely belong to the process.

Controlled sequences exercise the actual waiting path, including mismatch then
match, persistent mismatch/foreign hit, original exception and single-click
behavior. One viewing preview, zero installations, no increased timeout and no
repeated click. This tests a possible transition without claiming animation or
a provider bug as the cause of M6/M7.

M8 source `9356cde03697091b690709c9904cd90de59d6040`,
[run 42341966951](https://github.com/LuckyLivio/Contain/actions/runs/42341966951),
**failed** before input: 1 fixed group passed, 1 failed, 12 not run, zero
installations. Across 101 read-only probes and exactly 15,000 ms, the unique
owned target remained present but the FromPoint branch did not contain the point
or match its ID/bounds. First and last observations agree; no click was sent.
This establishes persistence for this run and rejects simply waiting longer.
The provider/coordinate implementation cause is not established. The original
summary is archived as `M8.json`; its failure is not converted to a pass.

## M9 — verify physical operation independently of contradictory point queries

Predeclared before execution: test-only adaptation; product code remains
identical to M6. The workflow still freezes a new build tied to this test-source
SHA; this does not claim equal ZIP/binary hashes across those runs.
The required user behavior is a visible, correct action, not equality with a
provider's contradictory FromPoint branch. Require one fixed-ID enabled Button
under the exact owned main HWND, finite current geometry inside the window and
its actual ScrollViewer/scroll-content viewports, bounded target ancestry reaching
that exact window, no preexisting native modal, and native point ownership.
Recheck current state/geometry immediately before one physical click. Before any
filename entry or cancellation, require the newly opened owned native dialog's
exact fixed title for that action. Existing exact-path, cancel, protected-file,
report-content and session assertions remain mandatory.

Retain FromPoint observations as diagnostics, including mismatches; unexpected
provider exceptions still fail. Do not call the physical input a confirmed action
until its expected dialog/result is observed. The original shared 15-second
budget remains. Controlled geometry regressions must reject the M5 clipped
same-window toolbar case, wrong viewport, missing/duplicate ID, foreign window,
invalid/moved bounds and exhausted ancestry. One viewing preview, zero
installations, no repeated click, no product change or data-correctness relaxation.

M9 source `c5e4b856b1edf2643bc6caa3b4ae8cd5687dabb5`,
[run 42342627378](https://github.com/LuckyLivio/Contain/actions/runs/42342627378),
**passed its viewing preview**: 10 fixed groups, 933 assertions, zero failures;
4 installation groups deliberately not run. `accepted=false` remains correct.
The frozen ZIP SHA-256 is
`52718ccba4aa78fbd224b9b4c97bb1f5c4b0d26656e1ad613ed6ed4d1ec8a790`.
All seven chooser actions used one eligible physical click and the exact new
modal title. All 53 exported IDs match `synthetic-search-event-070` through
`-122` in order. Cancel/existing-target protection, external read-only bytes,
same-name identity, compact controls and different-directory cold startup passed.
Four viewers exited normally with code 0 in 94/83/98/87 ms. The contradictory
FromPoint observations remain in the report; the new action evidence is not
reported as a repair of that provider behavior. Original aggregate: `M9.json`.

Actual 96-DPI screenshots at 1024 × 720 and 820 × 640 were reviewed. The fixed
event toolbar, list, explanation and copy controls are readable and usable.
The compact new-record screenshot caught the component's default page fade,
making text temporarily pale. Disable that optional page transition for this
first release; preserve all button states and other component behavior. This
visual decision does not explain M8's persistent point-query inconsistency.

## M10 — final frozen package and one real GUI installation

Predeclared before execution: the only product change after M9 is the explicit
`Navigation.Transition="None"` setting. Build and freeze one new package, then
run all 14 native groups against that same package: the complete M9 viewing
sequence, one explicit GUI fixture installation using default personal/follow
settings, active-task close/duplicate-start protection, exact saved UUID, parent
and verified direct-child writes/completion/identity, no unrelated attribution,
original strict loss predicates, event explanation/export, normal exit and cold
restart from another working directory. Maximum installations: one; retries:
zero; all original deadlines and failure retention remain.

Use the unchanged Beta.10 core's D4 evidence for its bounded heavy-load capture
behavior; do not rerun a broad historical matrix. The new WPF/CLI live integration
and all 13 actual screenshots remain required. Review the final screenshots,
source identities and ZIP hash before making PR #19 mergeable. Release must reuse
that exact accepted ZIP, without rebuilding or repackaging it.

M10 source `90ba93199044aea3dcdf69c1b4bb4a1f3b125e05`,
[run 42343076345](https://github.com/LuckyLivio/Contain/actions/runs/42343076345),
**failed overall**: 12 fixed groups passed, 1 failed, 1 not run, 535 assertions;
one real GUI installation, zero retries. Original aggregate: `M10.json`.
Frozen ZIP SHA-256:
`71d7413b8e05f0a1060e62ab5738c13a5d6e9566f5656545f6c93a9cb90be14a`.
The GUI selected the fixture, started once, guarded an active close, saved and
opened exact session `a795a31b-b29f-412f-a806-22e7c3c97497`. All 24 independent
live predicates passed: receiver/candidate/handoff/storage and ETW loss were zero;
parent write `3b8679d7-ac39-4a58-ab52-361e310639a3` and child write
`8564e966-b888-44b3-af5a-a67be781b66d` had instance identity, High association and
successful completion. Unrelated activity was not attributed. These facts do
not convert the incomplete desktop run into acceptance.

The child event's search explanation and copied ID passed before the later
`real_timeline_page` selection raised `RequiredControlMissingException`.
The harness had enumerated and realized all 50 rows, then used generic FindFirst
for an earlier row recycled by WPF virtualization. The screenshot shows the page
scrolled to its final rows. This is a test selection defect, not evidence of a
missing stored event; exact selection must use the owned list's bounded item
container and realize the matching ID. The real cold restart and final unchanged
source/package check were not reached. Repository CI
[42343076381](https://github.com/LuckyLivio/Contain/actions/runs/42343076381) passed.

Actual screenshots also exposed two presentation issues: a long English raw
coverage diagnostic occupied the normal overview banner, and the running task
was partly below the configuration viewport. Preserve raw diagnostics in technical
details, summarize the normal banner in Chinese, and bring the task area into
view when it starts. These are new UI changes, not capture or attribution changes.

## M11 — exact virtualized-row selection and readable live task

Predeclared before execution: fix only the confirmed test lookup contract and the
two observed UI presentation issues. The dependency audit also explicitly selects
the official NuGet feed: M10 listed the runner's additional offline feed, which
the release verifier correctly rejects. A focused regression retains that strict
source check; no dependency versions change. Keep event virtualization and the 50-row
page boundary. Exercise actual selection/explanation/copy for the first and middle
rows after enumerating to the end, then retain all existing 53 ordered match,
export and final selected-ID assertions. Never substitute a different event or
session. Unexpected UIA exceptions and original deadlines remain failures.

Freeze a new product ZIP because the UI presentation changes. Run the complete
14-group desktop plan once, with at most one explicit GUI fixture installation
and zero retries, then review all actual screenshots. Preserve M10's failed result
and its separate successful live predicates. The unchanged core still reuses
Beta.10 D4; no new heavy-load matrix is needed. Only the exact fully accepted ZIP
can be published, with no rebuilding or repackaging afterward.

M11 source `28fc2fc4ee7edd1bec3f525f4f5a6d98a233f80d`,
[push run 42343947561](https://github.com/LuckyLivio/Contain/actions/runs/42343947561),
**failed before product freeze**. Model34 and the shared presentation checks
passed. The independent navigation regression had 10 passed, 1 failed, 1 not run:
`keyboard_navigation_invokes_once` raised TimeoutException. The exact synthetic
host exited normally with code0. No packaged desktop test or installation ran,
and no product ZIP was produced. `M11.json` preserves the original combined
control result. The same-source PR check 42343950976 passed, but is not substituted
for this failed push attempt. Repository CI
[42343947449](https://github.com/LuckyLivio/Contain/actions/runs/42343947449) passed.

The code already waits for HasKeyboardFocus and checks foreground ownership before
sending one Space key. The old aggregate does not identify whether the timeout
occurred while acquiring focus or while observing the resulting click. Neither
cause is established; do not claim a product keyboard defect or missing focus wait.

## M12 — identify the keyboard prerequisite stage without an installation

Predeclared before execution: add bounded, redacted stage observations to the
existing one-host external navigation check, retaining its original discovery,
UIA, focus/click and cleanup deadlines. Record only the known host, fixed control
ID/focus, foreground ownership, whether the one Space input was sent and the
expected counter observation. Do not log unrelated window names, paths or typed
content. Keep all 12 checks and fail on unexpected UIA errors, persistent state
mismatch or early exit. Ordinary push/PR checks will run; no native acceptance
marker, no product freeze and no installation. Use the result to choose any
necessary test or product correction before the final complete desktop run.

M12 source `c9942c3eb0a19d7e782a4648c50abab56add1637`,
[push run 42344334095](https://github.com/LuckyLivio/Contain/actions/runs/42344334095),
**passed the control-only diagnosis**: model34/control12 passed, no product
package, no installation. `M12.json` and `M12-keyboard.json` preserve original
aggregate bytes. The exact host owned focus at 172 ms; one Space down/up reached
the target, and the expected 3/2 click counters appeared at 1019 ms. The host
exited normally. M11 was not reproduced and its cause remains unconfirmed; this
is not a claim that a product keyboard defect was repaired.

Review of the added diagnostics found a separate late-input hazard: a synchronous
UIA sample on the STA worker could finish after its outer dispatch timeout, then
continue to send the key. Guard the existing five-second dispatch deadline and
freeze bounded diagnostic snapshots before the next run. This review finding
is not evidence that it caused M11, whose original code did not contain that
additional UIA sample.

## M13 — final complete package after bounded diagnostic hardening

Predeclared before execution: only harden the new test diagnostics against a late
Space dispatch and concurrent snapshot mutation; keep the M11 product changes,
the exact virtualized-event selection regression, all12 control checks and all14
packaged-desktop groups. Run one full acceptance on a newly frozen ZIP, at most
one explicit GUI installation, zero retries and unchanged deadlines. No additional
random control runs or capture-pressure research. Retain M11's unresolved isolated
test timeout and M12's limited diagnostic scope; the final package must independently
pass actual keyboard navigation, installation, exact readback, event explanation,
export and cold restart. No prior PR pass substitutes for this run.
