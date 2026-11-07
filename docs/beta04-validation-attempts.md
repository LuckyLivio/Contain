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
