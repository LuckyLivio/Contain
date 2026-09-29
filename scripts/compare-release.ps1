param([Parameter(Mandatory)][string]$BaselineDirectory,[int]$Files=10000,[int]$Trials=3,[string]$OutputDirectory='./target/release-comparison',[switch]$SnapshotOnly)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($Files -lt 4 -or $Files -gt 10000 -or $Trials -lt 1 -or $Trials -gt 5) {throw 'Bounded comparison requires 4..10000 files and 1..5 trials'}
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$BaselineDirectory=(Resolve-Path -LiteralPath $BaselineDirectory).Path
$cargo=Join-Path $env:USERPROFILE '.cargo/bin/cargo.exe'
foreach($directory in @($BaselineDirectory,$repo)) {
    Push-Location $directory
    try { & $cargo build --release --workspace --locked; if($LASTEXITCODE -ne 0){throw 'release build failed'} } finally {Pop-Location}
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$OutputDirectory=(Resolve-Path $OutputDirectory).Path
$OutputDirectory=Join-Path $OutputDirectory ('run-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$fixture=Join-Path $repo 'target/release/contain-test-installer.exe'
$bins=@{baseline=(Join-Path $BaselineDirectory 'target/release/contain.exe');candidate=(Join-Path $repo 'target/release/contain.exe')}
$records=[Collections.Generic.List[object]]::new()
$previousTruth=$env:CONTAIN_FIXTURE_TRUTH
function Run-Trial([string]$Mode,[int]$Count,[int]$Trial,[string]$Role='stress',[switch]$Overload,[switch]$FilterOff) {
    $label="$Mode-$Role-$Count-$Trial";if($Overload){$label+='-overload'}
    if($FilterOff){$label+='-filter-off'}
    $priorFilter=$env:CONTAIN_ETW_EVENT_ID_FILTER
    $env:CONTAIN_ETW_EVENT_ID_FILTER=if($FilterOff){'0'}else{'1'}
    $dest=Join-Path $OutputDirectory $label;New-Item -ItemType Directory -Path $dest | Out-Null
    $name='contain-demo-'+[guid]::NewGuid().ToString('N')
    $root=Join-Path $env:TEMP $name;$truthRoot=Join-Path $env:TEMP ($name+'-truth');$db=Join-Path $env:TEMP ($name+'.db')
    foreach($path in @($root,$truthRoot)){New-Item -ItemType Directory -Path $path | Out-Null;[IO.File]::WriteAllText((Join-Path $path '.contain-demo-marker'),'Contain test fixture')}
    $env:CONTAIN_FIXTURE_TRUTH=$truthRoot;$noise=$null
    try {
        if($Role -eq 'stress') {
            $noise=Start-Process -WindowStyle Hidden -FilePath $fixture -ArgumentList @('--root',('"'+$root+'"'),'--role','noise','--files','100') -PassThru -RedirectStandardOutput (Join-Path $dest 'noise.stdout.txt') -RedirectStandardError (Join-Path $dest 'noise.stderr.txt')
            $deadline=[DateTime]::UtcNow.AddSeconds(30)
            while(-not(Test-Path -LiteralPath (Join-Path $root '.noise-ready'))){if([DateTime]::UtcNow -gt $deadline){throw 'noise readiness timeout'};Start-Sleep -Milliseconds 10}
        }
        $arguments=@('--db',('"'+$db+'"'),'install',('"'+$fixture+'"'),'--name','ReleaseFixture','--watch',('"'+$root+'"'))
        if($SnapshotOnly){$arguments+='--no-etw'}
        if($Overload){$arguments+=@('--evidence-quota-mib','0')}
        $arguments+=@('--','--root',('"'+$root+'"'),'--role',$Role,'--files',"$Count")
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $process=Start-Process -WindowStyle Hidden -FilePath $bins[$Mode] -ArgumentList $arguments -PassThru -RedirectStandardOutput (Join-Path $dest 'capture.stdout.txt') -RedirectStandardError (Join-Path $dest 'capture.stderr.txt')
        $peak=0L;$dbPeak=0L;$walPeak=0L
        while(-not $process.HasExited){
            $process.Refresh();$peak=[Math]::Max($peak,$process.PeakWorkingSet64)
            if(Test-Path -LiteralPath $db){$dbPeak=[Math]::Max($dbPeak,(Get-Item -LiteralPath $db).Length)}
            if(Test-Path -LiteralPath ($db+'-wal')){$walPeak=[Math]::Max($walPeak,(Get-Item -LiteralPath ($db+'-wal')).Length)}
            Start-Sleep -Milliseconds 20
        }
        $process.WaitForExit();$watch.Stop()
        if($process.ExitCode -ne 0){throw "Capture failed: $label; see preserved stderr and database $db"}
        if($noise -and (-not $noise.WaitForExit(30000) -or $noise.ExitCode -ne 0)){throw 'noise fixture failed'}
        $export=Start-Process -WindowStyle Hidden -FilePath $bins[$Mode] -ArgumentList @('--db',('"'+$db+'"'),'inspect','ReleaseFixture','--json') -PassThru -Wait -RedirectStandardOutput (Join-Path $dest 'capture.json') -RedirectStandardError (Join-Path $dest 'export.stderr.txt')
        if($export.ExitCode -ne 0){throw 'database export failed'}
        $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        & python -X utf8 "$PSScriptRoot/score-release.py" $dest $truthRoot $sid
        if($LASTEXITCODE -ne 0){throw 'release scoring failed'}
        $capture=Get-Content -Raw -LiteralPath (Join-Path $dest 'metadata.json') | ConvertFrom-Json
        $score=$capture.score
        if(-not $SnapshotOnly -and $capture.backend.etw_file -ne 'active'){throw 'real ETW is required'}
        $expected=if($Role -eq 'burst'){$Count}else{$total=0;foreach($w in 0..3){$n=[Math]::Floor($Count/4)+[int]($w -lt ($Count%4));$total+=2*$n+[Math]::Ceiling($n/2)};$total}
        if($score.groups.target_success.expected -ne $expected){throw 'fixed target-success denominator incomplete'}
        if($Role -eq 'stress' -and $score.groups.noise_success.expected -ne 300){throw 'independent noise denominator incomplete'}
        $p=$capture.backend.pipeline
        $balances=($null -ne $p -and $p.callback_records -eq $p.decode_attempted+$p.unsupported_records -and $p.decode_attempted -eq $p.decode_succeeded+$p.decode_failed -and $p.enqueued -eq $p.dequeued+$p.queue_pending -and $p.queue_pending -eq 0)
        if($Mode -eq 'candidate' -and $null -ne $p) {
            $s=$capture.backend.stream
            $balances=$balances -and $p.callback_records -eq $p.deliberately_filtered+$capture.backend.events_received+$p.failed_without_output -and $capture.backend.events_received -eq $p.enqueued+$p.queue_overflow+$p.enqueue_disconnected -and $p.dequeued -eq $s.accepted -and $s.accepted -eq $s.persisted+$s.failed+$s.quota_dropped
        }
        if($Mode -eq 'baseline' -and $null -ne $p){$p.persistence_succeeded=$null;$p.persistence_failed=$null}
        $pass=($balances -and $p.context_evictions -eq 0 -and $capture.backend.dropped_events -eq 0 -and $capture.backend.decode_errors -eq 0 -and $capture.backend.etw_events_lost -eq 0 -and $capture.backend.etw_buffers_lost -eq 0 -and $score.groups.target_success.observation_rate -ge 0.95 -and $score.groups.target_success.correct_attribution_rate -ge 0.95 -and $score.false_positive_target_events -eq 0 -and $score.incorrect_attribution -eq 0)
        $record=[ordered]@{mode=$Mode;files=$Count;workers=$(if($Role -eq 'stress'){4}else{1});trial=$Trial;role=$Role;intentional_overload=[bool]$Overload;elapsed_seconds=$watch.Elapsed.TotalSeconds;parent_peak_working_set_bytes=$peak;sqlite_bytes=(Get-Item $db).Length;sampled_db_peak_bytes=$dbPeak;sampled_wal_peak_bytes=$walPeak;stats=$capture.stats;backend=$capture.backend;quality=$capture.quality;score=($score|Select-Object * -ExcludeProperty rows);counter_balances=$balances;acceptance_pass=$pass}
        $record.file_filter_experiment=[bool]$FilterOff
        $records.Add($record)
        $record | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $dest 'measurement.json') -Encoding utf8
        Write-Output ('TRIAL: '+($record|ConvertTo-Json -Depth 30 -Compress))
        if($Overload -and ($p.retention_dropped -eq 0 -or $capture.stats.high_confidence_events -ne 0 -or $capture.quality.level -ne 'Incomplete')){throw 'intentional overload failed to suppress attribution'}
    } finally {
        $env:CONTAIN_ETW_EVENT_ID_FILTER=$priorFilter
        if($noise -and -not $noise.HasExited){$noise.Kill();$noise.WaitForExit()}
        & $fixture --root $root --cleanup; if($LASTEXITCODE -ne 0){throw 'fixture cleanup failed'}
        & $fixture --root $truthRoot --cleanup; if($LASTEXITCODE -ne 0){throw 'truth cleanup failed'}
        foreach($suffix in @('','-wal','-shm')){$path=$db+$suffix;if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
    }
}
try {
    $counts=@(1000,$Files)|Select-Object -Unique
    foreach($count in $counts){foreach($trial in 1..$Trials){$order=if($trial%2 -eq 1){@('baseline','candidate')}else{@('candidate','baseline')};foreach($mode in $order){Run-Trial $mode $count $trial}}}
    Run-Trial candidate 1000 1 burst
    if(-not $SnapshotOnly){Run-Trial candidate 1000 1 stress -FilterOff}
    if(-not $SnapshotOnly){Run-Trial candidate 1000 1 stress -Overload}
    $medians=@(foreach($count in $counts){foreach($mode in @('baseline','candidate')){$r=@($records|Where-Object {$_.mode -eq $mode -and $_.files -eq $count -and $_.role -eq 'stress' -and -not $_.intentional_overload -and -not $_.file_filter_experiment});$sorted=@($r.elapsed_seconds|Sort-Object);[ordered]@{mode=$mode;files=$count;trials=$r.Count;median_seconds=$sorted[[int][Math]::Floor($sorted.Count/2)];passes=@($r|Where-Object acceptance_pass).Count}}})
    $result=[ordered]@{schema_version=2;candidate_commit=(& git -C $repo rev-parse HEAD);baseline_commit=(& git -C $BaselineDirectory rev-parse HEAD);profile='release --locked';rustc=((& (Join-Path $env:USERPROFILE '.cargo/bin/rustc.exe') -Vv)-join "`n");os=[Environment]::OSVersion.VersionString;elevated=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);filesystem=(Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($env:TEMP).Substring(0,1))).FileSystem;logical_processors=[Environment]::ProcessorCount;candidate_lock_sha256=(Get-FileHash (Join-Path $repo 'Cargo.lock')).Hash;baseline_lock_sha256=(Get-FileHash (Join-Path $BaselineDirectory 'Cargo.lock')).Hash;fixture_sha256=(Get-FileHash $fixture).Hash;scorer_sha256=(Get-FileHash (Join-Path $PSScriptRoot 'score-fixture.py')).Hash;measurement='Same runner, compiler, release profile and frozen common fixture. Alternating baseline/candidate pairs; independent noise. Parent PeakWorkingSet64 and DB/WAL sampled every20ms, excludes child memory/kernel buffers. Capture wall includes final commit; export/scoring outside timing. Callback timers overlap wall phases; no CPU utilization claim. Background/caches uncontrolled, Defender unchanged.';medians=$medians;trials=$records}
    $result|ConvertTo-Json -Depth 40|Set-Content (Join-Path $OutputDirectory 'comparison.json') -Encoding utf8
    Write-Output ('COMPARISON: '+($result|ConvertTo-Json -Depth 40 -Compress))
} finally {$env:CONTAIN_FIXTURE_TRUTH=$previousTruth}
