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
personal library is consulted. The unchanged fixture permits only a direct TEMP
child named `contain-demo-*`. Private outputs retain database copies, raw process
errors, independent truth and full capture data. They are not release assets.

`modern-desktop-plan.json` declares the ordered checks, deadlines and zero retries
before launch. `modern-desktop-progress.json` preserves completed checks during
execution. `modern-desktop-summary.json` records failed/unexecuted checks,
process-owned stage probes, final binary hashes and screenshots with actual
window pixel dimensions and native DPI. The first failed dependent stage stops
later stages. A deadline is never extended by an inner UIA call; transient absence
is accepted only inside the relevant bounded state wait. Unexpected UIA errors
and premature process exits retain their types.

The actual wait engine also runs controlled sequences for temporary absence plus
an old session followed by the correct session, permanent absence, a permanent
wrong session, process exit and preservation of an unexpected COM exception.
These verify the new test contract, not a historical C1/D5 root-cause claim.

The synthetic search oracle is independent of the UI: 130 events per same-name
session, exactly positions 70 through 122 matching `needle`, 50 + 3 results and
selected event 120. UI row order, copy, raw explanation, all-match HTML and cold
restart are checked against those fixed IDs. External legacy data and every
frozen package member are hashed before and after.

The single real install uses the new GUI's default follow path and unchanged
`verify-follow-installer-experiment.py` oracle. Its parent, direct child and
independent unrelated writer supply independent syscall truth. No installer is
started by file selection. Receiver, handoff, candidate and raw-save loss gates
remain strict; bounded storage and identity/completion rules are not replaced by
an exit-code assertion. GUI-returned exact UUID, recorded collector exit,
persisted readback, original event explanation, report and a new process from a
different working directory must agree.

Omitting `--install-fixture` runs the fixed viewer preview checks and exits zero
only when they all pass. The four live checks remain explicitly `not_run`, and
`accepted` remains false. A viewer preview is never full native release evidence.
