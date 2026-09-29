param([ValidateRange(4,100000)][int]$Files=10000, [switch]$SnapshotOnly, [switch]$RequireEtw, [string]$OutputDirectory='./target/stress')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($SnapshotOnly -and $RequireEtw) { throw 'Choose one backend mode' }
Push-Location (Join-Path $PSScriptRoot '..')
function Measure-Run([string]$Exe, [string[]]$Arguments, [string]$Label) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $out=Join-Path $OutputDirectory ($Label+'.stdout.txt'); $err=Join-Path $OutputDirectory ($Label+'.stderr.txt')
    $p=Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err
    $peak=0L
    while (-not $p.HasExited) { $p.Refresh(); $peak=[Math]::Max($peak,$p.PeakWorkingSet64); Start-Sleep -Milliseconds 20 }
    $p.WaitForExit(); $clock.Stop()
    if ($p.ExitCode -ne 0) { throw "$Label failed; see $err" }
    return @{elapsed_seconds=$clock.Elapsed.TotalSeconds;sampled_peak_working_set_bytes=$peak}
}
try {
    $cargo=Join-Path $env:USERPROFILE '.cargo/bin/cargo.exe'
    & $cargo build --workspace --locked
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
    $fixture=(Resolve-Path './target/debug/contain-test-installer.exe').Path
    $contain=(Resolve-Path './target/debug/contain.exe').Path
    $results=@{}
    foreach ($mode in @('baseline','capture')) {
        $name='contain-demo-'+[guid]::NewGuid().ToString('N')
        $root=Join-Path $env:TEMP $name; $db=Join-Path $env:TEMP ($name+'.db')
        New-Item -ItemType Directory -Path $root | Out-Null
        [IO.File]::WriteAllText((Join-Path $root '.contain-demo-marker'),'Contain test fixture')
        try {
            $fixtureArgs=@('--root',('"'+$root+'"'),'--role','stress','--files',"$Files")
            if ($mode -eq 'baseline') { $results.baseline=Measure-Run $fixture $fixtureArgs $mode }
            else {
                $captureArgs=@('--db',('"'+$db+'"'),'install',('"'+$fixture+'"'),'--name','StressFixture','--watch',('"'+$root+'"'))
                if ($SnapshotOnly) { $captureArgs+='--no-etw' }
                $captureArgs+=@('--')+$fixtureArgs
                $results.capture=Measure-Run $contain $captureArgs $mode
                $capture=((& $contain --db $db inspect StressFixture --json) -join "`n" | ConvertFrom-Json).data
                if ($LASTEXITCODE -ne 0) { throw 'stress database readback failed' }
                if ($RequireEtw -and $capture.backend.etw_file -ne 'active') { throw 'stress ETW unavailable' }
                $score=& "$PSScriptRoot/score-fixture.ps1" -Capture $capture -Root $root
                if ($score.expected -ne ($Files*2+[Math]::Ceiling($Files/4.0/2)*4)) {
                    # Work is divided over four processes; odd division uses each worker's ceiling.
                    $expected=0
                    foreach ($worker in 0..3) { $n=[Math]::Floor($Files/4)+[int]($worker -lt ($Files%4)); $expected+=2*$n+[Math]::Ceiling($n/2) }
                    if ($score.expected -ne $expected) { throw 'stress ground truth incomplete' }
                }
                if ($score.incorrect_attribution -ne 0) { throw 'stress false attribution detected' }
                $results.capture.sqlite_bytes=(Get-Item -LiteralPath $db).Length
                $results.capture.stats=$capture.stats; $results.capture.backend=$capture.backend
                $results.capture.quality=$capture.quality
                $results.capture.admitted_events_per_second=$capture.stats.events_received/$results.capture.elapsed_seconds
                $results.reliability=$score | Select-Object -Property * -ExcludeProperty rows
                $score | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'reliability.json') -Encoding utf8
            }
        } finally {
            & $fixture --root $root --cleanup
            if ($LASTEXITCODE -ne 0) { throw 'stress cleanup failed' }
            foreach ($suffix in @('','-wal','-shm')) { $target=$db+$suffix; if(Test-Path -LiteralPath $target){ Remove-Item -LiteralPath $target -Force } }
        }
    }
    $results.schema_version=1; $results.files=$Files; $results.workers=4
    $results.os=[Environment]::OSVersion.VersionString; $results.commit=(& git rev-parse HEAD)
    $results.elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $results.end_to_end_overhead_seconds=$results.capture.elapsed_seconds-$results.baseline.elapsed_seconds
    $results.end_to_end_overhead_ratio=$results.capture.elapsed_seconds/$results.baseline.elapsed_seconds
    $results.measurement='Debug build; one sequential baseline/capture pair, four fixture workers; process wall time includes inventory, drain, hashing and SQLite commit. Memory is the monitored parent process PeakWorkingSet sampled every 20ms, excluding descendants and kernel ETW buffers. Throughput is admitted source records per entire capture wall time, not raw system event rate. Scoring is outside the timed region.'
    $results | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'benchmark.json') -Encoding utf8
    Write-Output ('BENCHMARK: '+($results | ConvertTo-Json -Depth 15 -Compress))
} finally { Pop-Location }
