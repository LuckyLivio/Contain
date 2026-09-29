param([switch]$KeepArtifacts)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) { $cargo = 'cargo' }
& $cargo build --workspace
if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }

$name = 'contain-demo-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$root = Join-Path $env:TEMP $name
$db = Join-Path $env:TEMP ($name + '.db')
$key = 'Software\Contain\Demo\' + $name
$contain = Join-Path $PSScriptRoot '..\target\debug\contain.exe'
$fixture = Join-Path $PSScriptRoot '..\target\debug\contain-test-installer.exe'
New-Item -ItemType Directory -Path $root | Out-Null

try {
    & $contain --db $db install $fixture --name TestFixture --watch $root --registry-key $key -- --root $root
    if ($LASTEXITCODE -ne 0) { throw 'capture failed' }
    & $contain --db $db inspect TestFixture
    if ($LASTEXITCODE -ne 0) { throw 'inspect failed' }
    & $contain --db $db diff TestFixture
    if ($LASTEXITCODE -ne 0) { throw 'diff failed' }
    $beforeDryRun = (Get-ChildItem -LiteralPath $root -File -Recurse | Get-FileHash -Algorithm SHA256 | Sort-Object Path | ConvertTo-Json -Compress)
    $registryBeforeDryRun = @(
        (Get-ItemPropertyValue -LiteralPath ('Registry::HKEY_CURRENT_USER\' + $key) -Name Installed),
        (Get-ItemPropertyValue -LiteralPath ('Registry::HKEY_CURRENT_USER\' + $key) -Name ChildObserved)
    ) -join '|'
    & $contain --db $db remove TestFixture --dry-run
    if ($LASTEXITCODE -ne 0) { throw 'dry-run failed' }
    $afterDryRun = (Get-ChildItem -LiteralPath $root -File -Recurse | Get-FileHash -Algorithm SHA256 | Sort-Object Path | ConvertTo-Json -Compress)
    $registryAfterDryRun = @(
        (Get-ItemPropertyValue -LiteralPath ('Registry::HKEY_CURRENT_USER\' + $key) -Name Installed),
        (Get-ItemPropertyValue -LiteralPath ('Registry::HKEY_CURRENT_USER\' + $key) -Name ChildObserved)
    ) -join '|'
    if ($beforeDryRun -ne $afterDryRun -or $registryBeforeDryRun -ne $registryAfterDryRun) { throw 'dry-run modified fixture state' }

    $manifest = (& $contain --db $db inspect TestFixture --json | ConvertFrom-Json)
    if ($manifest.processes.Count -lt 2) { throw 'child process was not observed' }
    if ($manifest.files.Count -lt 4) { throw 'expected file changes were not observed' }
    if ($manifest.registry.Count -lt 2) { throw 'expected registry changes were not observed' }
    if ($manifest.files | Where-Object { $_.confidence -ne 'Unknown' }) { throw 'file ownership was overstated' }
    Write-Output 'DEMO VERIFIED: process ancestry, real file changes, scoped registry changes, SQLite readback, and dry-run.'
}
finally {
    if (-not $KeepArtifacts) {
        if (Test-Path -LiteralPath (Join-Path $root '.contain-demo-marker')) {
            & $fixture --root $root --cleanup
            if ($LASTEXITCODE -ne 0) { Write-Warning 'fixture cleanup failed; inspect demo root manually' }
            if (Test-Path -LiteralPath $root) { Write-Warning 'fixture root still exists after cleanup' }
            if (Test-Path -LiteralPath ('Registry::HKEY_CURRENT_USER\' + $key)) { Write-Warning 'fixture registry key still exists after cleanup' }
        }
        if (Test-Path -LiteralPath $root) {
            $resolved = (Resolve-Path -LiteralPath $root).Path
            $tempResolved = (Resolve-Path -LiteralPath $env:TEMP).Path
            if ((Split-Path $resolved -Parent) -eq $tempResolved -and (Split-Path $resolved -Leaf) -like 'contain-demo-*') {
                Remove-Item -LiteralPath $root -Force
            }
        }
        foreach ($suffix in @('', '-shm', '-wal')) {
            $target = $db + $suffix
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
        }
    } else {
        Write-Output "Demo root: $root"
        Write-Output "Demo database: $db"
    }
}
