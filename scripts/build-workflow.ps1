param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or ![Environment]::Is64BitProcess) { throw 'Build on Windows x64 with the manifest prerequisites' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('target/workflow-cli/packages/' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory; previous deliveries are preserved' }
$savedEnvironment=@{}
foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) { $savedEnvironment[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
Push-Location $root
try {
    $source=(& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or (& git status --porcelain)) {throw 'Require clean committed checkout before CLI packaging'}
    . (Join-Path $PSScriptRoot 'workflow-toolchain.ps1')
    $selection=Enter-WorkflowToolchain
    if ($env:CARGO_ENCODED_RUSTFLAGS) { throw 'Clear CARGO_ENCODED_RUSTFLAGS before requesting static CRT' }

    # A native CLI handed to a machine without MSVC must not require its redistributable DLLs.
    $out = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
    $env:RUSTFLAGS = '-C target-feature=+crt-static'
    $env:CARGO_TARGET_DIR = Join-Path $out 'rust-build'
    & cargo build -p contain-workflow --release --locked --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw 'Workflow CLI build failed' }
    $files = (New-Item -ItemType Directory -Path (Join-Path $out 'files')).FullName
    Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'x86_64-pc-windows-msvc/release/contain-workflow.exe') -Destination $files
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $files
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $files
    . (Join-Path $PSScriptRoot 'workflow-rust-notices.ps1')
    Copy-WorkflowRustNotices 'contain-workflow' $files
    $zip = Join-Path $out 'contain-workflow-windows-x64.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($files,$zip)
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $exeSha = (Get-FileHash -LiteralPath (Join-Path $files 'contain-workflow.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $versionProcess=New-Object Diagnostics.ProcessStartInfo
    $versionProcess.FileName=Join-Path $files 'contain-workflow.exe'
    $versionProcess.Arguments='--version'
    $versionProcess.UseShellExecute=$false
    $versionProcess.RedirectStandardOutput=$true
    $versionProcess.RedirectStandardError=$true
    $probe=[Diagnostics.Process]::Start($versionProcess)
    if (!$probe.WaitForExit(10000)) {$probe.Kill();throw 'CLI version probe timed out'}
    if ($probe.ExitCode -ne 0) {throw 'Built CLI did not run on builder'}
    $bytes=New-Object IO.MemoryStream
    $probe.StandardOutput.BaseStream.CopyTo($bytes)
    $versionSha=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes.ToArray())).Replace('-','').ToLowerInvariant()
    $spec = [ordered]@{
        schema_version=1; target='windows-x64'; sha256=$sha; entry='contain-workflow.exe'; required_files=@('LICENSE','THIRD-PARTY-NOTICES.md')
        launch=@{args=@('--version');timeout_ms=10000;expected_exit=0;window_title=$null}
        tests=@(@{name='CLI exact version output';program='contain-workflow.exe';args=@('--version');expected_exit=0;timeout_ms=10000;stdout_sha256=$versionSha},@{name='CLI --help exits 0';program='contain-workflow.exe';args=@('--help');expected_exit=0;timeout_ms=10000})
    }
    [IO.File]::WriteAllText((Join-Path $out 'package-spec.json'),($spec | ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding $false))
    [IO.File]::WriteAllText((Join-Path $out 'identity.json'),([ordered]@{schema_version=1;zip_sha256=$sha;spec_sha256=(Get-FileHash (Join-Path $out 'package-spec.json') -Algorithm SHA256).Hash.ToLowerInvariant();entry_sha256=$exeSha;crt='static requested; clean-machine execution still requires validation';acceptance='not_run';source_sha=$source;toolchain=@{msvc=$selection.msvc;sdk=$selection.sdk}} | ConvertTo-Json),(New-Object Text.UTF8Encoding $false))
    Write-Output "Package and frozen test specification: $out"
} finally {
    foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) { if (!$savedEnvironment.ContainsKey($key)) { [Environment]::SetEnvironmentVariable($key,$null,'Process') } }
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key],'Process') }
    Pop-Location
}
