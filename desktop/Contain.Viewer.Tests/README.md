# Native desktop verification

This is a Windows integration executable, not a mock frontend. It references the
production viewer, verifies its assembly is byte-identical to the frozen package,
uses the packaged `contain.exe`, and launches the packaged desktop executable.
The normal workflow uses actual WinForms buttons, grid selection, paging, filters,
clipboard and report export. Injected file-dialog answers only replace the human's
path selection; they do not replace CLI calls, database reads, rendering or export.

From the repository root on Windows, after preparing the desktop package and
production-generated synthetic databases:

```powershell
dotnet publish desktop/Contain.Viewer.Tests/Contain.Viewer.Tests.csproj -c Release -r win-x64 --self-contained true -o target/viewer-tests
./target/viewer-tests/Contain.Viewer.Tests.exe --package target/windows-viewer --fixtures target/viewer-fixtures --output target/viewer-public --private-output target/viewer-private --source (git rev-parse HEAD)
```

The fixture directory must contain the production-generated `finished.db`,
`historical.db`, `interrupted.db` and `empty.db`, plus explicitly adverse
`corrupt.db`, `future.db` and `unrelated.db` inputs. The frozen package supplies
`samples/viewer-demo.db`. Tests copy inputs into a new, clearly named synthetic
Windows directory with spaces, Chinese and parameter-special characters. Existing
files are never overwritten. Database byte hashes are checked after viewing and
export. The default read-only plan invokes no collection, installer, provider
probe or cleanup. The explicitly selected install plan below is separate.

`Program.cs` declares the complete required check list before execution. Failed
checks remain failed; unavailable prerequisites are `not_run`. Assertion failures
have safe fixed reasons in `summary.json`; full errors stay in the private output.
The standalone harness's `--version` mode deliberately reports an incompatible
version solely to test rejection by the real process bridge. It never supplies
successful query results.

Screenshots come from the actual production WinForms controls rendered on Windows
with `DrawToBitmap` after the tested operations. They contain only synthetic model
data and the fixed reviewed synthetic path. They are not browser previews or
real-ETW captures. The actual local synthetic HTML export is copied byte-for-byte
to `reports/` only after checking its public content and identity. Linux cross-build
success cannot satisfy any native execution check.

The optional `--install-fixture <frozen-package>/contain-test-installer.exe` adds
`InstallVerification.cs`'s six predeclared checks after every original read-only
check passes. It first checks the production argument builder and status parser,
including literal quotes/Chinese/special characters, missing results, unknown
states, changed identities, nonzero collector exits and installer-exit versus
final-save boundaries. A snapshot-only invalid-EXE fault injection and read-only
database attempt then exercise the actual Start handler. They cannot receive
real ETW credit. The invalid-EXE attempt's saved unfinished session is opened by
its exact backend ID and earlier finished/unfinished records are checked unchanged.

Only then does the harness click the real production **开始记录** button for one
controlled fixture installation. It verifies duplicate and window-close guards,
actual running state, source stop/final persistence, exact auto-selected session
among older and same-name records, a real High file event matched against the
fixture's independent PID/creation-time/path/operation/time journal, production
explain, and actual local report export. No script collects the successful record
before the UI runs. `CONTAIN_FIXTURE_TRUTH` points outside the selected root;
both are new direct TEMP children named `contain-demo-*`. CI uses a fixed reviewed
TEMP and package path, with spaces, Chinese and special characters. The complete
database, oracle and full CLI exports stay private. A real export containing a
host registry SID stays local; its hash still proves the product export result.

Actual OS picker cancel and navigation have a separate bounded keyboard scenario:
the harness waits for the real native dialog, sends Escape, then reopens it,
navigates its address bar to the package directory and selects the EXE. A missing
interactive desktop is `not_run`; an attempted interaction failure is preserved
as `failed`, with no retry. Directly filling configuration fields is never counted
as native picker navigation. If unavailable, the minimum manual handoff is to open
**新建安装记录**, open the installer picker, cancel once, reopen it, navigate to a
folder containing spaces/Chinese/special characters, select the controlled EXE,
then confirm the exact displayed path. This manual handoff is not a passed test.

`install-summary.json` separates the six product checks, synthetic fault injection,
OS picker result, exact source/binary hashes, task/session/process identity,
source event and measured loss counters. `install-*.png` are the actual production
form's configuration, running, result and explain states for this UI-started
capture. No automatic retry, third-party installer, service mutation, scale test
or 32-run protocol is part of this plan. The package can identify verified product
automation while leaving the graphical milestone open for an unexecuted or failed
native picker scenario.
