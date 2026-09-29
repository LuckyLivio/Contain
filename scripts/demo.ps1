param([switch]$KeepArtifacts, [switch]$RequireEtw, [switch]$SnapshotOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($RequireEtw -and $SnapshotOnly) { throw 'Choose RequireEtw or SnapshotOnly, not both.' }
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) { $cargo = 'cargo' }
Push-Location (Join-Path $PSScriptRoot '..')
function Assert-JsonContract([string]$text) {
    if (Get-Command Test-Json -ErrorAction SilentlyContinue) {
        if (-not (Test-Json -Json $text -SchemaFile './docs/schema/cli-v3.schema.json')) { throw 'JSON schema validation failed' }
    }
}
try {
    & $cargo build --workspace --locked
    if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
    $name = 'contain-demo-' + [guid]::NewGuid().ToString('N')
    $root = Join-Path $env:TEMP $name
    $db = Join-Path $env:TEMP ($name + '.db')
    $key = 'Software\Contain\Demo\' + $name
    $registryPath = 'Registry::HKEY_CURRENT_USER\' + $key
    $contain = (Resolve-Path './target/debug/contain.exe').Path
    $fixture = (Resolve-Path './target/debug/contain-test-installer.exe').Path
    New-Item -ItemType Directory -Path $root | Out-Null
    [IO.File]::WriteAllText((Join-Path $root '.contain-demo-marker'), 'Contain test fixture')
    foreach ($file in @('settings.txt','rename-me.txt','delete-me.txt')) { [IO.File]::WriteAllText((Join-Path $root $file), 'baseline') }
    New-Item -Path $registryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $registryPath -Name Installed -Value 'baseline' -PropertyType String | Out-Null
    $unrelated = Start-Process -FilePath $fixture -ArgumentList @('--role','unrelated','--root',('"' + $root + '"')) -PassThru -WindowStyle Hidden
    try {
        $arguments = @('--db',$db,'install',$fixture,'--name','TestFixture','--watch',$root,'--registry-key',$key)
        if ($SnapshotOnly) { $arguments += '--no-etw' }
        $arguments += @('--','--root',$root)
        & $contain @arguments
        if ($LASTEXITCODE -ne 0) { throw 'capture failed' }
        if (-not $unrelated.WaitForExit(10000)) { throw 'independent fixture timed out' }
        if ($unrelated.ExitCode -ne 0) { throw 'independent fixture failed' }
        $raw = (& $contain --db $db inspect TestFixture --json) -join "`n"
        Assert-JsonContract $raw
        $document = $raw | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $document.schema_version -ne 3) { throw 'inspect JSON contract failed' }
        $manifest = $document.data
        Write-Output ("BACKEND: " + ($manifest.backend | ConvertTo-Json -Compress))
        if ($manifest.processes.Count -lt 3) { throw 'parent/child/grandchild were not all observed' }
        if (@($manifest.processes | Where-Object confidence -eq 'Certain').Count -ne 1) { throw 'installer must be Certain' }
        if (@($manifest.processes | Where-Object confidence -eq 'High').Count -lt 2) { throw 'child and grandchild must have verified ancestry' }
        if (@($manifest.processes | Where-Object pid -eq $unrelated.Id).Count -ne 0) { throw 'independent process was attached to installer' }
        if (@($manifest.files | Where-Object operation -eq 'modified').Count -lt 1) { throw 'file modification not captured' }
        if (@($manifest.files | Where-Object operation -eq 'deleted').Count -lt 1) { throw 'file deletion not captured' }
        if (@($manifest.registry | Where-Object operation -eq 'modified').Count -lt 1) { throw 'registry update not captured' }
        $unrelatedChanges = @($manifest.files | Where-Object { $_.path -like '*\unrelated.txt' -or $_.path -like '*\shared.txt' })
        if ($unrelatedChanges.Count -lt 2 -or @($unrelatedChanges | Where-Object confidence -ne 'Unknown').Count -gt 0) { throw 'unrelated/mixed writer state was misattributed' }
        if ($RequireEtw -and $manifest.backend.etw_file -ne 'active') { throw 'ETW was required but did not start' }
        if ($manifest.backend.etw_file -eq 'active') {
            $highFiles = @($manifest.events | Where-Object { $_.event_type -eq 'file' -and $_.confidence -eq 'High' })
            $unknownFiles = @($manifest.events | Where-Object { $_.resource -like '*\unrelated.txt' -and $_.event_type -eq 'file' -and $_.confidence -eq 'Unknown' })
            if ($highFiles.Count -eq 0) { throw 'no real High-confidence file source event captured' }
            if ($unknownFiles.Count -eq 0) { throw 'independent ETW writer was not observed as Unknown' }
            Write-Output "ETW VERIFIED: $($highFiles.Count) High file events; $($unknownFiles.Count) independent writer events remain Unknown."
            foreach ($event in @($manifest.events | Where-Object { $_.event_type -in @('file','registry') } | Select-Object -First 6)) {
                Write-Output ("SOURCE: " + ($event | ConvertTo-Json -Compress -Depth 6))
            }
        } elseif (@($manifest.files | Where-Object confidence -ne 'Unknown').Count -ne 0) { throw 'snapshot fallback overstated attribution' }
        foreach ($command in @('diff','history')) {
            $raw = (& $contain --db $db $command TestFixture --json) -join "`n"
            Assert-JsonContract $raw
            $json = $raw | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0 -or $json.schema_version -ne 3 -or $json.kind -ne $command) { throw "$command JSON contract failed" }
        }
        $raw = (& $contain doctor --json) -join "`n"
        Assert-JsonContract $raw
        $doctor = $raw | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $doctor.kind -ne 'doctor' -or -not $doctor.data.process_observation) { throw 'doctor probe failed' }
        $before = (Get-ChildItem -LiteralPath $root -File -Recurse | Get-FileHash | Sort-Object Path | ConvertTo-Json -Compress)
        $registryBefore = Get-ItemPropertyValue -LiteralPath $registryPath -Name Installed
        & $contain --db $db remove TestFixture --dry-run
        if ($LASTEXITCODE -ne 0) { throw 'dry-run failed' }
        $after = (Get-ChildItem -LiteralPath $root -File -Recurse | Get-FileHash | Sort-Object Path | ConvertTo-Json -Compress)
        if ($before -ne $after -or $registryBefore -ne (Get-ItemPropertyValue -LiteralPath $registryPath -Name Installed)) { throw 'dry-run changed fixture data' }
        Write-Output "DEMO VERIFIED: $($manifest.processes.Count) processes, $($manifest.files.Count) file states, $($manifest.registry.Count) registry states, $($manifest.events.Count) timeline events; readback, JSON and dry-run passed."
    } finally {
        if (-not $unrelated.HasExited) { $unrelated.Kill(); $unrelated.WaitForExit() }
        if (-not $KeepArtifacts) {
            & $fixture --root $root --cleanup
            if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $registryPath)) { throw 'fixture cleanup failed' }
            foreach ($suffix in @('','-wal','-shm')) { $target=$db+$suffix; if(Test-Path -LiteralPath $target){ Remove-Item -LiteralPath $target -Force } }
        } else { Write-Output "Fixture: $root`nDatabase: $db" }
    }
} finally { Pop-Location }
