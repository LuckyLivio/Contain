param([switch]$Release, [switch]$KeepArtifacts, [switch]$RequireEtw, [switch]$SnapshotOnly, [string]$OutputDirectory)
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
    $buildArgs=@("build","--workspace","--locked");if($Release){$buildArgs+="--release"}
    & $cargo @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
    $name = 'contain-demo-' + [guid]::NewGuid().ToString('N')
    $root = Join-Path $env:TEMP $name
    $db = Join-Path $env:TEMP ($name + '.db')
    $truthRoot=Join-Path $env:TEMP ($name+'-truth')
    New-Item -ItemType Directory -Path $truthRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $truthRoot '.contain-demo-marker'),'Contain test fixture')
    $previousTruth=$env:CONTAIN_FIXTURE_TRUTH
    $env:CONTAIN_FIXTURE_TRUTH=$truthRoot
    $key = 'Software\Contain\Demo\' + $name
    $registryPath = 'Registry::HKEY_CURRENT_USER\' + $key
    $profile=if($Release){"release"}else{"debug"}
    $contain = (Resolve-Path "./target/$profile/contain.exe").Path
    $fixture = (Resolve-Path "./target/$profile/contain-test-installer.exe").Path
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
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath (Join-Path $root ".detached-done"))) { if ([DateTime]::UtcNow -gt $deadline) { throw "detached fixture did not finish" }; Start-Sleep -Milliseconds 50 }
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
        if (-not (Test-Path -LiteralPath (Join-Path $root 'locked.txt'))) { throw 'failed delete was treated as success' }
        if (@($manifest.operations | Where-Object { $_.operation -eq 'Renamed' -and $_.resource -like '*\renamed.txt' }).Count -ne 1) { throw 'stable file identity rename was not recovered' }
        $noiseRegistry=@($manifest.registry | Where-Object name -eq 'NoiseOnly')
        if ($noiseRegistry.Count -ne 1 -or $noiseRegistry[0].confidence -ne 'Unknown') { throw 'independent registry state was misattributed' }
        $score = & "$PSScriptRoot/score-fixture.ps1" -Capture $manifest -Root $truthRoot
        if ($OutputDirectory) {
            New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
            $document | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'capture.json') -Encoding utf8
            $score | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'reliability.json') -Encoding utf8
            Copy-Item -LiteralPath (Join-Path $truthRoot 'ground-truth.json') -Destination $OutputDirectory
        }
        if ($score.expected -ne 21) { throw "ground truth incomplete: expected 21 instrumented operations" }
        Write-Output ("RELIABILITY: " + (($score | Select-Object -Property * -ExcludeProperty rows) | ConvertTo-Json -Compress))
        if (@($manifest.events | Where-Object { $_.evidence.pid -eq $unrelated.Id -and $_.confidence -in @('High','Certain') }).Count -gt 0) { throw 'independent source actor received application attribution' }
        if ($score.incorrect_attribution -ne 0 -or $score.false_positive_target_events -ne 0) { throw 'false attribution detected' }
        if ($RequireEtw) {
            Write-Output ("PROCESSES: " + ($manifest.processes | ConvertTo-Json -Depth 8 -Compress))
            $shortPid=@($score.rows | Where-Object role -eq "short")[0].pid
            $rootPid=@($manifest.processes | Where-Object confidence -eq "Certain")[0].pid
            Write-Output ("LIFECYCLES: " + (@($manifest.events | Where-Object { $_.event_type -eq "lifecycle" -and $_.evidence.pid -in @($shortPid,$rootPid) }) | ConvertTo-Json -Depth 8 -Compress))
            Write-Output ("SHORT: " + (@($manifest.events | Where-Object { $_.resource -like '*\short.txt' }) | ConvertTo-Json -Depth 8 -Compress))
            Write-Output ("REGISTRY_EXAMPLE: " + (@($manifest.events | Where-Object { $_.event_type -eq "registry" -and $_.operation -eq "set_value" -and $_.confidence -eq "High" -and $_.raw.resource_resolved } | Select-Object -First 1) | ConvertTo-Json -Depth 8 -Compress))
            Write-Output ("SCORED: " + ($score.rows | ConvertTo-Json -Depth 8 -Compress))
            foreach ($role in @('short','detached')) {
                if (@($score.rows | Where-Object { $_.role -eq $role -and $_.outcome -eq 'Correct' }).Count -eq 0) { throw "$role actor was not correctly attributed" }
            }
            $rootExit=@($manifest.events | Where-Object operation -eq 'installer_exited')[0]
            $detached=@($manifest.events | Where-Object { $_.event_type -eq 'file' -and $_.resource -like '*\detached.txt' -and $_.confidence -eq 'High' })
            if (@($detached | Where-Object { [uint64]$_.timestamp_ticks -gt [uint64]$rootExit.timestamp_ticks }).Count -eq 0) { throw 'detached write after root exit not captured' }
        }
        $firstHistory=(& $contain --db $db history TestFixture --json) -join "`n"
        $secondHistory=(& $contain --db $db history TestFixture --json) -join "`n"
        if ($firstHistory -cne $secondHistory) { throw 'history ordering was not stable across readback' }
        $eventId=$manifest.events[0].id
        $explanation=(& $contain --db $db explain $eventId --json) -join "`n"
        Assert-JsonContract $explanation
        if ($LASTEXITCODE -ne 0) { throw 'explain failed' }
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
            & $fixture --root $truthRoot --cleanup
            if ($LASTEXITCODE -ne 0) { throw 'truth cleanup failed' }
            foreach ($suffix in @('','-wal','-shm')) { $target=$db+$suffix; if(Test-Path -LiteralPath $target){ Remove-Item -LiteralPath $target -Force } }
        } else { Write-Output "Fixture: $root`nDatabase: $db`nTruth: $truthRoot" }
    }
} finally { $env:CONTAIN_FIXTURE_TRUTH=$previousTruth; Pop-Location }
