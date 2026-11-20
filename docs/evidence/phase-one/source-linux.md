# Contain source

Platform: linux / x86_64

- platform — 失败: requires Windows x64
- tool:rustc — 失败: tool unavailable or version probe failed; required 1.100.0; remedy: rustup toolchain install 1.100.0 --component rustfmt --component clippy
- tool:rustup — 失败: tool unavailable or version probe failed; required x86_64-pc-windows-msvc; remedy: rustup target add --toolchain 1.100.0 x86_64-pc-windows-msvc
- tool:dotnet — 失败: tool unavailable or version probe failed; required 10.0.403; remedy: Install .NET SDK 10.0.403 x64 from https://dotnet.microsoft.com/download; global.json disables roll-forward
- tool:powershell.exe — 失败: tool unavailable or version probe failed; required 14.44; remedy: Install Visual Studio 2022 Build Tools with Desktop development with C++, MSVC v143 14.44 x64/x86 (individual components); this requirement pins the minor toolset family, not its patch
- tool:powershell.exe — 失败: tool unavailable or version probe failed; required 10.0.26100.0; remedy: Install Windows SDK 10.0.26100.0 x64 using Visual Studio Installer individual components
- environment_manifest — 通过: canonical JSON SHA-256 2af1562c7a49285e630fe548524f4976f680a56958520329ab232cbac768efce
- source_revision — 通过: Git HEAD 5c6e4027b2fa6afe8c5a9fe3835880b4f7252b0b; worktree clean Some(false); HEAD does not identify uncommitted content
