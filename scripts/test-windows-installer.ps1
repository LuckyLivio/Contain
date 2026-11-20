$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path $PSScriptRoot -Parent
$buildScript = Join-Path $PSScriptRoot 'build-windows-installer.ps1'

# 1. Parse-check installer PowerShell script
$tokens = $null
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) {
    throw "PowerShell syntax errors in build-windows-installer.ps1: $($errors | Out-String)"
}

# 2. Verify missing ZIP and hash-mismatch rejections preserve input files and never create OutputDirectory
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('contain-installer-policy-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $missingOut = Join-Path $tempRoot 'out-missing'
    $failedMissing = $false
    try {
        & $buildScript -Zip (Join-Path $tempRoot 'absent.zip') -OutputDirectory $missingOut
    } catch {
        $failedMissing = $_.Exception.Message -match 'was not found'
    }
    if (!$failedMissing -or (Test-Path -LiteralPath $missingOut)) {
        throw 'Missing ZIP check did not fail cleanly before creating OutputDirectory!'
    }

    $fakeZip = Join-Path $tempRoot 'sample.zip'
    $fakeSpec = Join-Path $tempRoot 'package-spec.json'
    [IO.File]::WriteAllText($fakeZip, 'original-zip-bytes', (New-Object System.Text.UTF8Encoding($false)))
    $origBytes = [IO.File]::ReadAllBytes($fakeZip)
    $wrongSpec = @{
        schema_version = 1
        target = 'windows-x64'
        sha256 = ('0' * 64)
        entry = 'Contain.Viewer.exe'
        required_files = @()
        launch = $null
        tests = @()
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText($fakeSpec, $wrongSpec, (New-Object System.Text.UTF8Encoding($false)))

    $mismatchOut = Join-Path $tempRoot 'out-mismatch'
    $failedMismatch = $false
    try {
        & $buildScript -Zip $fakeZip -Spec $fakeSpec -OutputDirectory $mismatchOut
    } catch {
        $failedMismatch = ($_.Exception.Message -match 'SHA-256 mismatch') -and ($_.Exception.Message -match ('0' * 64))
    }
    $afterBytes = [IO.File]::ReadAllBytes($fakeZip)
    if (!$failedMismatch -or (Test-Path -LiteralPath $mismatchOut) -or ([Convert]::ToBase64String($origBytes) -ne [Convert]::ToBase64String($afterBytes))) {
        throw 'SHA-256 mismatch check did not report discrepancy or modified original files!'
    }

    Write-Output 'Windows installer script policy checks passed (missing-input + hash-mismatch + preservation).'
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
