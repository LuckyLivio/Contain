param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory=$env:CONTAIN_DELIVERY_DIR }
if (!$OutputDirectory) { $OutputDirectory=Join-Path $root ('target/workflow/deliveries/'+[guid]::NewGuid().ToString('N')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {throw 'New delivery directory required; existing outputs are retained'}
$savedEnvironment=@{}
foreach ($key in @('CARGO_TARGET_DIR','RUSTFLAGS') + @([Environment]::GetEnvironmentVariables('Process').Keys)) { $savedEnvironment[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
Push-Location $root
try {
    $source=(& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or (& git status --porcelain)) {throw 'Require clean committed checkout including untracked files'}
    . (Join-Path $PSScriptRoot 'workflow-toolchain.ps1')
    $selected=Enter-WorkflowToolchain
    if ($env:CARGO_ENCODED_RUSTFLAGS) {throw 'Clear CARGO_ENCODED_RUSTFLAGS before static CRT build'}
    $out=(New-Item -ItemType Directory -Path $OutputDirectory).FullName
    $toolchain=@{status='passed';msvc=$selected.msvc;sdk=$selected.sdk}
    function Write-Json($Path,$Value) { [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding $false)) }
    Write-Json (Join-Path $out 'toolchain.json') $toolchain
    $env:CARGO_TARGET_DIR=Join-Path $out 'rust-build'
    $env:RUSTFLAGS='-C target-feature=+crt-static'
    function Run-Step([string]$Name,[string]$Program,[string[]]$Arguments) {
        $code=Invoke-WorkflowStep (Join-Path $out ($Name+'.local.log')) $Program $Arguments
        Write-Json (Join-Path $out ($Name+'.json')) @{name=$Name;exit_code=$code;status=$(if($code -eq 0){'passed'}else{'failed'})}
        if ($code -ne 0) {throw "Build/check failed: $Name (exit $code); local log retained"}
    }
    Run-Step 'toolchain-policy' 'powershell.exe' @('-NoProfile','-File','scripts/test-workflow-toolchain.ps1')
    Run-Step 'receiver-policy' 'powershell.exe' @('-NoProfile','-File','scripts/test-workflow-acceptance.ps1')
    Run-Step 'rust-build' 'cargo' @('build','--workspace','--release','--locked','--target','x86_64-pc-windows-msvc')
    Run-Step 'rust-regressions' 'cargo' @('test','--workspace','--locked','--target','x86_64-pc-windows-msvc')
    Run-Step 'model-restore' 'dotnet' @('restore','desktop/Contain.Desktop.Model.Tests','--locked-mode')
    Run-Step 'model-regressions' 'dotnet' @('run','--project','desktop/Contain.Desktop.Model.Tests','-c','Release','--no-restore')
    Run-Step 'wpf-restore' 'dotnet' @('restore','desktop/Contain.Desktop','-p:RuntimeIdentifier=win-x64','-p:RuntimeIdentifiers=win-x64','-p:SelfContained=true','--locked-mode')
    Run-Step 'wpf-publish' 'dotnet' @('publish','desktop/Contain.Desktop','-c','Release','-r','win-x64','--self-contained','true','--no-restore','-o',(Join-Path $out 'wpf'))
    Run-Step 'fixtures' (Join-Path $env:CARGO_TARGET_DIR 'x86_64-pc-windows-msvc/release/viewer-fixtures.exe') @((Join-Path $out 'fixtures'))
    . (Join-Path $PSScriptRoot 'workflow-rust-notices.ps1')
    $noticeRoot=(New-Item -ItemType Directory -Path (Join-Path $out 'rust-notices')).FullName
    Copy-WorkflowRustNotices 'contain-cli' $noticeRoot
    $core=Join-Path $env:CARGO_TARGET_DIR 'x86_64-pc-windows-msvc/release/contain.exe'
    $entry=Join-Path $out 'wpf/Contain.Viewer.exe'
    Write-Json (Join-Path $out 'build-receipt.json') @{source_sha=$source;core_sha256=(Get-FileHash $core -Algorithm SHA256).Hash.ToLowerInvariant();entry_sha256=(Get-FileHash $entry -Algorithm SHA256).Hash.ToLowerInvariant();toolchain=$toolchain;regressions='passed'}
    $nuget=$env:NUGET_PACKAGES
    if (!$nuget) {$nuget=Join-Path $env:USERPROFILE '.nuget/packages'}
    Run-Step 'desktop-freeze' 'python' @('scripts/package-source-desktop.py','--source',$source,'--viewer',(Join-Path $out 'wpf'),'--core',$core,'--fixtures',(Join-Path $out 'fixtures'),'--rust-licenses',(Join-Path $noticeRoot 'licenses'),'--nuget',$nuget,'--toolchain',(Join-Path $out 'toolchain.json'),'--receipt',(Join-Path $out 'build-receipt.json'),'--output',(Join-Path $out 'desktop-delivery'))
    Run-Step 'cli-freeze' 'powershell.exe' @('-NoLogo','-NoProfile','-NonInteractive','-File','scripts/build-workflow.ps1','-OutputDirectory',(Join-Path $out 'cli-delivery'))
    $desktopIdentity=Get-Content (Join-Path $out 'desktop-delivery/identity.json') -Raw | ConvertFrom-Json
    $cliIdentity=Get-Content (Join-Path $out 'cli-delivery/identity.json') -Raw | ConvertFrom-Json
    Write-Json (Join-Path $out 'handoff.json') @{schema_version=1;source_sha=$source;desktop=$desktopIdentity;cli=$cliIdentity;native_acceptance='not_run';clean_snapshot='not_asserted_by_build'}
    Write-Output "Delivery complete: $out (native receiver acceptance still required)"
} finally {
    foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) { if (!$savedEnvironment.ContainsKey($key)) { [Environment]::SetEnvironmentVariable($key,$null,'Process') } }
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key],'Process') }
    Pop-Location
}
