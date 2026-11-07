# v0.4.0-beta.1 native validation attempts

These are ordered attempts, not a selection of green reruns. The fixed plan is
[the migration and design document](beta04-design-and-migration.md). Every product
ZIP is frozen before its native checks; each failed artifact remains in Actions.
No previous WinForms report establishes acceptance of this WPF desktop.

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

The pinned WPF UI 4.3.0 source explains the action mismatch: its
`NavigationViewItemAutomationPeer` exposes `SelectionItem`, whose `Select` calls
`NavigationView.NavigateInternal`. That path requires a `TargetPageType`; our
action navigation instead used `Click` handlers and `ReplaceContent`. The
component's ordinary `OnClick` supports this action-only case, but its selection
peer does not. The correction therefore belongs in Contain's action-navigation
automation peer, preserving the actual component, keyboard behavior, and click
handler. It must not be hidden by weakening the native test to mouse fallback.

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
