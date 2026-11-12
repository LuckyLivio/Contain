# WPF packaged desktop acceptance

This harness starts the frozen `Contain.Viewer.exe` as a separate process. It has
no reference to the product assembly and cannot call viewmodels to make a GUI
check pass. WPF UI Automation is scoped to that retained process. The harness's
WinForms dependency supplies only native keyboard input and GDI screenshots; the
product remains WPF.

```powershell
dotnet publish desktop/Contain.Desktop.Tests -c Release -r win-x64 --self-contained true -o <harness>
<harness>/Contain.Desktop.Tests.exe --package <extracted-frozen-zip> --fixtures <viewer-fixtures-output> --output <public-output> --private-output <new-private-output> --source <ui-source-sha> --install-fixture <controlled-fixture-exe>
```

The caller sets a new isolated `TEMP`/`TMP` directory before starting the harness.
Every personal library has the existing production isolation marker; no real
personal library is consulted. The controlled fixture permits only a direct TEMP
child named `contain-demo-*`. Its opt-in native installer window starts with a blank
destination; the harness enters a new marked destination and invokes Install once.
Contain receives neither a watch root nor the destination environment setting.
Independent truth goes to a separately named temporary directory. Private outputs retain database copies, raw process
errors, independent truth and full capture data. They are not release assets.

`modern-desktop-plan.json` declares the ordered checks, deadlines and zero retries
before launch. `modern-desktop-progress.json` preserves completed checks during
execution. `modern-desktop-summary.json` records failed/unexecuted checks,
process-owned stage probes, final binary hashes and screenshots with actual
window pixel dimensions and native DPI. The first failed dependent stage stops
later stages. A deadline is never extended by an inner UIA call; transient absence
is accepted only inside the relevant bounded state wait. Unexpected UIA errors
and premature process exits retain their types.

Action-only sidebar entries must expose `InvokePattern`; the harness does not
substitute a mouse click for a broken automation action. The empty-library check
also reaches New record by actual focused Space input. M1 (run 42337219333)
retains its original InvalidOperationException at NewInstall. Source review found
that WPF UI 4.3.0's default selection peer routes through target-page navigation,
while these entries are configured as actions. The separate Windows STA control
regression verifies that library route and the corrected inner-button composition
through both in-process and external UI Automation. Bounded
action diagnostics distinguish lookup, missing pattern, selection and invocation
without collecting unrelated desktop elements or exception text.

Native picker actions use a physical click to avoid synchronously blocking in a
modal `InvokePattern` call. Before sending input, the driver requires an enabled,
visible button, a unique fixed AutomationId in the owned main window, native
window ownership, and finite positive target bounds fully inside the main window
and every exposed ScrollViewer/ScrollContentPresenter or scrolling ancestor.
Target ancestry must reach the exact owned main HWND within 16 inspected nodes.
This rejects M5's toolbar clipped above its event viewport even though it remains
inside the window. Eligibility is polled without input; fresh unique identity,
state, bounds and clipping are rechecked immediately before one physical click.
An existing owned native modal is rejected. Readiness and the exact
action-specific native dialog title share the original 15-second discovery
budget. Duplicate IDs and unexpected provider errors fail immediately; persistent
mismatches time out without clicking. There is no fallback or repeated click.
Cached per-launch diagnostics retain the attempt count and first/last observations
with only the fixed action ID, roles, identity/geometry comparison booleans,
bounded ancestry, match outcome and original exception type. The actual geometry
guard rejects a clipped toolbar, wrong viewport, foreign or missing main window,
nonfinite/outside bounds and overlong ancestry. Duplicate IDs and unexpected
provider exceptions retain their failures. M6 did not capture the hit ancestor's
ID/bounds; it does not
establish that the observed Button was the required action or a provider bug.
M7 found mismatching hit geometry in one immediate probe; it did not establish
persistence or an animation cause. M8 retained 101 contradictory read-only
FromPoint observations over 15 seconds without clicking. FromPoint's first
observation is now retained as diagnostic evidence, including a false match;
it is not substituted for the geometry gate or exact native-modal postcondition.
Physical input sent and expected dialog confirmed are reported separately.
No product/provider root cause is claimed. The actual action-wait engine additionally
tests transient mismatch before an exact hit, persistent wrong/foreign hits,
one input only, unchanged exception types and the original deadline.

The actual wait engine also runs controlled sequences for temporary absence plus
an old session followed by the correct session, permanent absence, a permanent
wrong session, process exit and preservation of an unexpected COM exception.
These verify the new test contract, not a historical C1/D5 root-cause claim.

The synthetic search oracle is independent of the UI: 130 events per same-name
session, exactly positions 70 through 122 matching `needle`, 50 + 3 results and
selected event 120. UI row order, copy, raw explanation, all-match HTML and cold
restart are checked against those fixed IDs. External legacy data and every
frozen package member are hashed before and after.

Event selection resolves an exact ID through the owned EventList's ItemContainer
and VirtualizedItem patterns within its 50-row page. After checking all page IDs
for duplicates, it re-realizes the retained exact item, scrolls and selects that
item. It does not assume that every enumerated row remains in the rendered tree.
The synthetic first page deliberately scrolls to its last row before selecting,
explaining and copying the offscreen first and middle rows. M10 retained the real
child event in the independent oracle and already explained/copied it through
search; its later full-timeline failure occurred in the old rendered-tree lookup.
The native regression verifies the corrected selection contract, with product
virtualization, exact UUID/event checks and page limits unchanged.

The single real install uses the new GUI's default follow path and unchanged
`verify-follow-installer-experiment.py` oracle. The GUI directory choice occurs in
the fixture installer process itself, preserving root and direct-child identity.
A screenshot records the actual custom destination before Install. This is an
independent controlled installer, not a third-party 7-Zip installation. Its parent, direct child and
independent unrelated writer supply independent syscall truth. No installer is
started by file selection. Receiver, handoff, candidate and raw-save loss gates
remain strict; bounded storage and identity/completion rules are not replaced by
an exit-code assertion. GUI-returned exact UUID, recorded collector exit,
persisted readback, original event explanation, report and a new process from a
different working directory must agree.

Omitting `--install-fixture` runs the fixed viewer preview checks and exits zero
only when they all pass. The four live checks remain explicitly `not_run`, and
`accepted` remains false. A viewer preview is never full native release evidence.

Beta.2 additionally reads both actual Save dialogs before replacing the suggested
filename, checks session/time/type components, verifies real process start/stop
selection, explanation and copies, and checks the finished task hides runtime
step/measurement noise while retaining elapsed time and its record entry.

Beta.2 default-name observation reads the unique owned filename Edit through UIA
ValuePattern and retains the WM_GETTEXT comparison. It validates the complete
expected record name, exact UTC start, short session identity and export type
while the dialog is still open. A displayed stem is accepted only with the HTML
file-type contract and a successful no-extension input that produces the exact
`.html` output. Every dialog observation and a native dialog screenshot survive
failure; the original discovery deadline is unchanged. The first Beta.2 native
attempt did not retain its observed default value, so its filename assertion
failure alone does not establish a product naming defect or an extension-hiding
cause. This targeted attempt tests those hypotheses without changing product code.

B2 retained both UIA and WM_GETTEXT showing the correct default stem. Its actual
native Save dialog screenshot shows the selected label `本地 HTML 报告`, without
`*.html`; the old guessed native ComboBox ID returned no type text. The next
observation reads current SelectionPattern/ValuePattern values from at most eight
owned dialog ComboBoxes, requires a unique visible enabled HTML type control and
its exact selected label, and preserves bounded provider diagnostics. This fixes
the test's type-reading assumption; B2 remains a failed attempt. The actual
no-extension save must still produce the exact `.html` file before acceptance.
