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
export. No collection, installer, provider probe or cleanup is invoked.

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
