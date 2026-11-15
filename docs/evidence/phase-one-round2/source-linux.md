# Contain source

Platform: linux / x86_64

Process architecture: x86_64

Architecture probe: non_windows_host; emulated: Some(false)

- platform — 失败: requires Windows x64
- tool:rustc — 通过: version matched; required 1.100.0; remedy: rustup toolchain install 1.100.0 --component rustfmt --component clippy
- tool:rustup — 通过: version matched; required x86_64-pc-windows-msvc; remedy: rustup target add --toolchain 1.100.0 x86_64-pc-windows-msvc
- tool:dotnet — 通过: version matched; required 10.0.403; remedy: Install .NET SDK 10.0.403 x64 from https://dotnet.microsoft.com/download; global.json disables roll-forward
- tool:python — 失败: version mismatch; required 3.12.10; remedy: Install Python 3.12.10 x64 from python.org and enable its executable on PATH (build machine only)
- environment_manifest — 通过: canonical JSON SHA-256 f3d083606db8b62ad84234004959350af1a72d91743c3425e49b7db23a76b02c
- source_revision — 通过: Git HEAD 51172ff2c1f93c11e5d95151e55508c0a9a2e8cb; worktree clean Some(false); HEAD does not identify uncommitted content
- selected_windows_toolchain — 失败: Windows toolchain selector unavailable; install required MSVC/SDK or use a fresh shell to resolve active-version conflicts
