param([switch]$Release, [switch]$KeepArtifacts, [switch]$RequireEtw, [switch]$SnapshotOnly, [string]$OutputDirectory, [switch]$RegistryNoise)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($RequireEtw -and $SnapshotOnly) { throw 'Choose RequireEtw or SnapshotOnly, not both.' }
$cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
if (-not (Test-Path -LiteralPath $cargo)) { $cargo = (Get-Command cargo -ErrorAction Stop).Source }
Push-Location (Join-Path $PSScriptRoot '..')
. "$PSScriptRoot/demo-provenance.ps1"
$previousTruth=$env:CONTAIN_FIXTURE_TRUTH
$runIdentity=$null
$identityPath=$null
function Assert-JsonContract([string]$text) {
    if (Get-Command Test-Json -ErrorAction SilentlyContinue) {
        if (-not (Test-Json -Json $text -SchemaFile './docs/schema/cli-v3.schema.json')) { throw 'JSON schema validation failed' }
    }
}
try {
    $buildArgs=@("build","--workspace","--locked","--message-format=json-render-diagnostics");if($Release){$buildArgs+="--release"}
    if ($OutputDirectory) {
        $OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
        if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) { throw 'OutputDirectory must be empty; historical evidence will not be overwritten' }
        New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
        $identityPath=Join-Path $OutputDirectory 'run-identity.json'
        $rustc=Join-Path (Split-Path $cargo) 'rustc.exe'
        if (-not (Test-Path -LiteralPath $rustc)) { $rustc='rustc' }
        $runIdentity=[ordered]@{
            schema_version=1; status='building'; started_at_utc=[DateTime]::UtcNow.ToString('o')
            source_before_build=(Get-DemoSourceIdentity); source_after_build=$null
            build_command=@($cargo)+$buildArgs; build_exit_code=$null
            cargo_version=((& $cargo --version) -join "`n"); rustc_version=((& $rustc --version --verbose) -join "`n")
            source_stable_during_build=$null; binary_before=@(); binary_after=$null
            fixture_clock=$env:CONTAIN_FIXTURE_CLOCK; raw_clock_path=$env:CONTAIN_ETW_RAW_CLOCK_PATH
            elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            interpretation='Contemporaneous build/source and pre/post executable observations, not a cryptographic loaded-image attestation. Existing historical runs without this record remain unidentified.'
        }
        $runIdentity | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $identityPath -Encoding utf8
    }
    $buildOutput = [Collections.Generic.List[string]]::new()
    $buildErrorPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 5 converts redirected native stderr into error
        # records. Keep diagnostics, then judge Cargo by its actual exit code.
        $ErrorActionPreference = 'Continue'
        if ($OutputDirectory) {
            & $cargo @buildArgs 2>&1 | ForEach-Object -ErrorAction Stop {
                $line = [string]$_; $buildOutput.Add($line); $line
            } | Tee-Object -FilePath (Join-Path $OutputDirectory 'build.log') -ErrorAction Stop
        } else {
            & $cargo @buildArgs 2>&1 | ForEach-Object -ErrorAction Stop {
                $line = [string]$_; $buildOutput.Add($line); $line
            }
        }
        $buildExit=$LASTEXITCODE
    } finally { $ErrorActionPreference = $buildErrorPreference }
    if ($runIdentity) { $runIdentity.build_exit_code=$buildExit }
    if ($buildExit -ne 0) { throw 'cargo build failed' }
    $buildExecutables = Get-DemoBuildExecutables -BuildOutput $buildOutput.ToArray() -RegistryNoise:$RegistryNoise
    $name = 'contain-demo-' + [guid]::NewGuid().ToString('N')
    $root = Join-Path $env:TEMP $name
    $db = Join-Path $env:TEMP ($name + '.db')
    $truthRoot=Join-Path $env:TEMP ($name+'-truth')
    New-Item -ItemType Directory -Path $truthRoot | Out-Null
    [IO.File]::WriteAllText((Join-Path $truthRoot '.contain-demo-marker'),'Contain test fixture')
    $env:CONTAIN_FIXTURE_TRUTH=$truthRoot
    $key = 'Software\Contain\Demo\' + $name
    $registryPath = 'Registry::HKEY_CURRENT_USER\' + $key
    $contain = $buildExecutables['contain']
    $fixture = $buildExecutables['contain-test-installer']
    if ($runIdentity) {
        $runIdentity.source_after_build=Get-DemoSourceIdentity
        $runIdentity.source_stable_during_build=$runIdentity.source_before_build.runtime_tree_sha256 -ceq $runIdentity.source_after_build.runtime_tree_sha256
        if (-not $runIdentity.source_stable_during_build) { throw 'Build inputs changed during build; refuse to launch fixture' }
        $runIdentity.binary_before=@(Get-DemoBinaryIdentity $contain; Get-DemoBinaryIdentity $fixture)
        if ($RegistryNoise) { $runIdentity.binary_before+=Get-DemoBinaryIdentity $buildExecutables['contain-registry-noise'] }
        $runIdentity.status='running'
        $runIdentity['fixture_root']=$root; $runIdentity['database']=$db; $runIdentity['truth_root']=$truthRoot
        $runIdentity | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $identityPath -Encoding utf8
    }
    New-Item -ItemType Directory -Path $root | Out-Null
    [IO.File]::WriteAllText((Join-Path $root '.contain-demo-marker'), 'Contain test fixture')
    foreach ($file in @('settings.txt','rename-me.txt','delete-me.txt')) { [IO.File]::WriteAllText((Join-Path $root $file), 'baseline') }
    New-Item -Path $registryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $registryPath -Name Installed -Value 'baseline' -PropertyType String | Out-Null
    $unrelated = Start-Process -FilePath $fixture -ArgumentList @('--role','unrelated','--root',('"' + $root + '"')) -PassThru -WindowStyle Hidden
    $registryNoiseProcesses=@()
    try {
        if($RegistryNoise) {
            $noiseBin=$buildExecutables['contain-registry-noise']
            foreach($phase in @('pre','launcher')) {
                $registryNoiseProcesses += Start-Process -WindowStyle Hidden -FilePath $noiseBin -ArgumentList @('--root',('"'+$root+'"'),'--phase',$phase) -PassThru
            }
            $noiseDeadline=[DateTime]::UtcNow.AddSeconds(10)
            while(-not ((Test-Path -LiteralPath (Join-Path $root '.registry-pre-ready')) -and (Test-Path -LiteralPath (Join-Path $root '.registry-launcher-ready')))) {
                if([DateTime]::UtcNow -gt $noiseDeadline){throw 'registry noise readiness failed'}
                Start-Sleep -Milliseconds 10
            }
        }
        $arguments = @('--db',$db,'install',$fixture,'--name','TestFixture','--watch',$root,'--registry-key',$key)
        if ($SnapshotOnly) { $arguments += '--no-etw' }
        $arguments += @('--','--root',$root)
        if ($runIdentity) {
            $runIdentity['capture_command']=@($contain)+$arguments
            $runIdentity | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $identityPath -Encoding utf8
        }
        $captureClock=[Diagnostics.Stopwatch]::StartNew()
        if ($OutputDirectory) {
            $captureCommandPath=Join-Path $OutputDirectory 'capture-command.json'
            ConvertTo-Json -InputObject (@($contain)+$arguments) | Set-Content -LiteralPath $captureCommandPath -Encoding utf8
            python "$PSScriptRoot/demo-measure.py" --command $captureCommandPath --output $OutputDirectory --database $db
            $captureExit=$LASTEXITCODE
            Get-Content -LiteralPath (Join-Path $OutputDirectory 'capture.stdout.log')
            Get-Content -LiteralPath (Join-Path $OutputDirectory 'capture.stderr.log') | Write-Output
        } else {
            & $contain @arguments
            $captureExit=$LASTEXITCODE
        }
        $captureClock.Stop()
        if ($captureExit -ne 0) { throw 'capture failed; any process measurements and logs were retained' }
        if (-not $unrelated.WaitForExit(10000)) { throw 'independent fixture timed out' }
        if ($unrelated.ExitCode -ne 0) { throw 'independent fixture failed' }
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath (Join-Path $root ".detached-done"))) { if ([DateTime]::UtcNow -gt $deadline) { throw "detached fixture did not finish" }; Start-Sleep -Milliseconds 50 }
        foreach($noiseProcess in $registryNoiseProcesses) {
            if(-not $noiseProcess.WaitForExit(21000) -or $noiseProcess.ExitCode -ne 0){throw 'bounded registry noise failed'}
        }
        $raw = (& $contain --db $db inspect TestFixture --json) -join "`n"
        Assert-JsonContract $raw
        $document = $raw | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $document.schema_version -ne 3) { throw 'inspect JSON contract failed' }
        $manifest = $document.data
        Write-Output ("BACKEND: " + ($manifest.backend | ConvertTo-Json -Compress))
        # Capture has stopped. Preserve the independent score and export before any
        # quality assertion can fail, so a red CI sample remains inspectable.
        $score = & "$PSScriptRoot/score-fixture.ps1" -Capture $manifest -Root $truthRoot
        if ($OutputDirectory) {
            New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
            $document | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'capture.json') -Encoding utf8
            $score | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'reliability.json') -Encoding utf8
            Copy-Item -LiteralPath (Join-Path $truthRoot 'ground-truth.json') -Destination $OutputDirectory
        }
        $viewReport=if($OutputDirectory){Join-Path $OutputDirectory 'view-consistency.json'}else{$db+'.views.json'}
        python "$PSScriptRoot/v04-verify-views.py" --contain $contain --database $db --app TestFixture --output $viewReport
        if($LASTEXITCODE -ne 0){throw 'Cross-CLI evidence consistency failed; see view-consistency report'}
        if ($OutputDirectory -and $env:CONTAIN_ETW_RAW_CLOCK_PATH) {
            $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            python "$PSScriptRoot/v04-clock-protocol.py" --capture (Join-Path $OutputDirectory 'capture.json') --truth (Join-Path $OutputDirectory 'ground-truth.json') --marker-root $truthRoot --raw-clock $env:CONTAIN_ETW_RAW_CLOCK_PATH --sid $sid --output (Join-Path $OutputDirectory 'clock-diagnostic.json')
            $clockExit=$LASTEXITCODE
            if ($runIdentity) { $runIdentity['clock_diagnostic_exit_code']=$clockExit }
            if ($clockExit -ne 0) { throw 'Original QPC diagnostic is unavailable or invalid; original score and artifacts were retained' }
        }
        if ($manifest.processes.Count -lt 3) { throw 'parent/child/grandchild were not all observed' }
        if (@($manifest.processes | Where-Object confidence -eq 'Certain').Count -ne 1) { throw 'installer must be Certain' }
        if (@($manifest.processes | Where-Object confidence -eq 'High').Count -lt 2) { throw 'child and grandchild must have verified ancestry' }
        if (@($manifest.processes | Where-Object pid -eq $unrelated.Id).Count -ne 0) { throw 'independent process was attached to installer' }
        if (@($manifest.files | Where-Object operation -eq 'modified').Count -lt 1) { throw 'file modification not captured' }
        if (@($manifest.files | Where-Object operation -eq 'deleted').Count -lt 1) { throw 'file deletion not captured' }
        if (@($manifest.registry | Where-Object operation -eq 'modified').Count -lt 1) { throw 'registry update not captured' }
        $unrelatedChanges = @($manifest.files | Where-Object { $_.path -like '*\unrelated.txt' -or $_.path -like '*\shared.txt' })
        if ($unrelatedChanges.Count -lt 2 -or @($unrelatedChanges | Where-Object confidence -ne 'Unknown').Count -gt 0) { throw 'unrelated/mixed writer state was misattributed' }
        if ($RequireEtw) {
            if ($manifest.backend.etw_file -ne 'active' -or $manifest.backend.etw_registry -ne 'active' -or $manifest.backend.etw_process -ne 'active') { throw 'ETW file, registry and process providers were required but not all active' }
            if (@($manifest.events | Where-Object event_type -eq 'file').Count -eq 0 -or @($manifest.events | Where-Object event_type -eq 'registry').Count -eq 0) { throw 'ETW requires actual file and registry source events; provider activation alone is insufficient' }
        }
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
        $expected=if($RegistryNoise){2021}else{21}
        if ($score.expected -ne $expected) { throw "ground truth incomplete: expected $expected instrumented operations" }
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
        # The companion destination belongs to this capture, not a later probe.
        # A second doctor must not attempt to reuse or overwrite that artifact.
        $captureRawClock=$env:CONTAIN_ETW_RAW_CLOCK_PATH
        try {
            Remove-Item Env:CONTAIN_ETW_RAW_CLOCK_PATH -ErrorAction SilentlyContinue
            $raw = (& $contain doctor --json) -join "`n"
        } finally { $env:CONTAIN_ETW_RAW_CLOCK_PATH=$captureRawClock }
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
        if ($runIdentity) { $runIdentity.status='completed' }
    } finally {
        foreach($noiseProcess in $registryNoiseProcesses) {
            # Bounded fixture exits itself. Do not terminate it to manufacture a passing test.
            if(-not $noiseProcess.HasExited){$null=$noiseProcess.WaitForExit(21000)}
            if(-not $noiseProcess.HasExited){throw 'noise did not honor deadline; retain fixture for investigation'}
        }
        if (-not $unrelated.HasExited) { $unrelated.Kill(); $unrelated.WaitForExit() }
        if (-not $KeepArtifacts) {
            & $fixture --root $root --cleanup
            if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $root) -or (Test-Path -LiteralPath $registryPath)) { throw 'fixture cleanup failed' }
            & $fixture --root $truthRoot --cleanup
            if ($LASTEXITCODE -ne 0) { throw 'truth cleanup failed' }
            foreach ($suffix in @('','-wal','-shm','.views.json')) { $target=$db+$suffix; if(Test-Path -LiteralPath $target){ Remove-Item -LiteralPath $target -Force } }
        } else { Write-Output "Fixture: $root`nDatabase: $db`nTruth: $truthRoot" }
    }
} catch {
    if ($runIdentity) { $runIdentity.status='failed'; $runIdentity['error']=$_.Exception.Message }
    throw
} finally {
    try {
        if ($runIdentity) {
            $runIdentity['finished_at_utc']=[DateTime]::UtcNow.ToString('o')
            if ($runIdentity.binary_before.Count -gt 0) {
                $runIdentity.binary_after=Complete-DemoBinaryIdentity $runIdentity.binary_before
                if (-not $runIdentity.binary_after.unchanged) { $runIdentity.status='binary_changed' }
            }
            $runIdentity | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $identityPath -Encoding utf8
            if ($runIdentity.status -eq 'binary_changed') { throw 'Executable changed or disappeared during demo; retained evidence cannot pass identity checks' }
        }
    } finally { $env:CONTAIN_FIXTURE_TRUTH=$previousTruth; Pop-Location }
}
