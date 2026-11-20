param(
    [string]$PackageDirectory,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Local development launcher for the verified current-source WPF desktop package.
# Note: Opens the existing WPF installation-record viewer; contain-workflow is still CLI-only and not integrated into the GUI.
$repoRoot = Split-Path $PSScriptRoot -Parent
$parentRoot = Split-Path $repoRoot -Parent

$candidates = @()
if ($PackageDirectory) {
    $candidates += [pscustomobject]@{ Source = 'parameter -PackageDirectory'; Path = $PackageDirectory }
} elseif ($env:CONTAIN_LOCAL_DESKTOP_DIR) {
    $candidates += [pscustomobject]@{ Source = 'environment CONTAIN_LOCAL_DESKTOP_DIR'; Path = $env:CONTAIN_LOCAL_DESKTOP_DIR }
} else {
    $candidates += [pscustomobject]@{ Source = 'repo target/local-desktop'; Path = (Join-Path $repoRoot 'target\local-desktop') }
    if ($parentRoot) {
        $candidates += [pscustomobject]@{ Source = 'sibling Contain-runs/local-desktop'; Path = (Join-Path $parentRoot 'Contain-runs\local-desktop') }
    }
}

$selected = $null
foreach ($candidate in $candidates) {
    if ($candidate.Path -and (Test-Path -LiteralPath $candidate.Path -PathType Container)) {
        $selected = [pscustomobject]@{ Source = $candidate.Source; Path = (Resolve-Path -LiteralPath $candidate.Path).Path }
        break
    }
}

if (!$selected) {
    $checked = ($candidates | ForEach-Object { "  - [$($_.Source)] $($_.Path)" }) -join [Environment]::NewLine
    Write-Error ("Local development desktop package was not found. Checked locations:`n" + $checked + "`n`n" +
        "Build and extract a current-source desktop package first (no historical Beta binary is downloaded automatically):`n" +
        "  powershell.exe -NoProfile -File `"$repoRoot\scripts\build-source-delivery.ps1`" -OutputDirectory `"$repoRoot\target\workflow\delivery-local`"`n" +
        "  Expand-Archive -LiteralPath `"$repoRoot\target\workflow\delivery-local\desktop-delivery\contain-source-windows-x64.zip`" -DestinationPath `"$repoRoot\target\local-desktop`"")
}

$requiredMembers = @('Contain.Viewer.exe', 'Contain.Viewer.dll', 'contain.exe', 'contain-workflow.exe', 'workflow\select-workflow-toolchain.ps1', 'coreclr.dll', 'BUILD.json', 'samples\viewer-demo.db')
$missing = @($requiredMembers | Where-Object { !(Test-Path -LiteralPath (Join-Path $selected.Path $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    Write-Error ("Local development desktop directory '$($selected.Path)' is incomplete (missing: $($missing -join ', ')).`n" +
        "Extract the complete contain-source-windows-x64.zip archive rather than copying a standalone EXE.")
}

$buildPath = Join-Path $selected.Path 'BUILD.json'
$build = Get-Content -LiteralPath $buildPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($build.protocol -ne 'contain-source-desktop-package-1' -or $build.reused_core_binary -ne $false) {
    Write-Error ("Refusing to launch '$($selected.Path)': BUILD.json protocol is '$($build.protocol)' (expected current-source 'contain-source-desktop-package-1', not a historical Beta package).")
}

$viewerExe = Join-Path $selected.Path 'Contain.Viewer.exe'
Write-Host "[Contain Local Dev Entry] WPF desktop (project environment check + installation record viewer)"
Write-Host "  Package : $($selected.Path) ($($selected.Source))"
Write-Host "  Source  : $($build.source_sha)"
Write-Host "  Sample  : $(Join-Path $selected.Path 'samples\viewer-demo.db') (SYNTHETIC; open read-only via sidebar external-library action)"

if ($CheckOnly) {
    exit 0
}

$proc = Start-Process -FilePath $viewerExe -WorkingDirectory $selected.Path -PassThru
Write-Host "  Started : Contain.Viewer.exe (PID $($proc.Id))"
