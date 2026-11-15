# Contain package

Platform: linux / x86_64

- package_identity — 通过: actual ZIP SHA-256 02f8bf5aee0198c83ddb5cd9504a57e56173c41ea2c3735a1e754a950aeaadd4; expected 02f8bf5aee0198c83ddb5cd9504a57e56173c41ea2c3735a1e754a950aeaadd4
- static_entry — 通过: Contain.Viewer.exe; EXE SHA-256 2d0c0af61f2fcda3bdb599e9d94cb84da143c9133f9a32922f9c6f7443fe0eb2; Windows x64 PE header checked (not a loader test)
- static_file:Contain.Viewer.dll — 通过: required package member
- static_file:Contain.Desktop.Model.dll — 通过: required package member
- static_file:contain.exe — 通过: required package member
- static_file:Wpf.Ui.dll — 通过: required package member
- static_file:Wpf.Ui.Abstractions.dll — 通过: required package member
- static_file:CommunityToolkit.Mvvm.dll — 通过: required package member
- static_file:coreclr.dll — 失败: required package member
- static_file:hostfxr.dll — 通过: required package member
- static_file:hostpolicy.dll — 通过: required package member
- static_file:PresentationFramework.dll — 通过: required package member
- static_file:Contain.Viewer.deps.json — 通过: required package member
- static_file:Contain.Viewer.runtimeconfig.json — 通过: required package member
- static_file:BUILD.json — 通过: required package member
- static_file:CONTENTS.json — 通过: required package member
- static_test:Core --version exits 0 (no output assertion) — 通过: test executable must be present in package
- static_contents — 失败: existing Contain CONTENTS.json membership, size and SHA-256 contract
- launch — 未实测: static/identity gate failed; execution blocked
- Core --version exits 0 (no output assertion) — 未实测: static/identity gate failed; execution blocked
