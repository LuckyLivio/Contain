param(
    [Parameter(Mandatory)][string]$ContainExecutable,
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Source,
    [ValidateRange(0,30000)][int]$BackgroundFiles = 0,
    [string]$IdentityDiagnosticExe,
    [switch]$Profile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count) { throw 'Experiment output must be new; retained attempts are never overwritten' }
$private = Join-Path $OutputDirectory 'private'
$public = Join-Path $OutputDirectory 'public'
New-Item -ItemType Directory -Path $private,$public | Out-Null
$ContainExecutable = [IO.Path]::GetFullPath($ContainExecutable)
$FixtureExecutable = [IO.Path]::GetFullPath($FixtureExecutable)
$summary = Join-Path $public 'follow-experiment-summary.json'
$phase = 'preflight'
$captureExit = $null
$lastStatus = $null
$previousTruth = $env:CONTAIN_FIXTURE_TRUTH
$previousProfile = $env:CONTAIN_PROFILE_PATH
$previousProfileCpu = $env:CONTAIN_PROFILE_THREAD_CPU
$previousBackgroundWait = $env:CONTAIN_FIXTURE_BACKGROUND_WAIT
$unrelated = $null
$background = $null
$backgroundSignal = $null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
function Write-Json([object]$Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))
}
function Start-Owned([string]$Exe,[string[]]$Arguments,[string]$Name,[int]$Timeout) {
    # All paths/arguments are owned experiment values, never a shell command.
    $quoted = $Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }
    $process = Start-Process -FilePath $Exe -ArgumentList $quoted -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $private ($Name+'.stdout')) -RedirectStandardError (Join-Path $private ($Name+'.stderr'))
    $null = $process.Handle
    $ownedProcesses.Add($process)
    if (-not $process.WaitForExit($Timeout)) { throw 'Owned experiment command exceeded its declared deadline; no success is inferred' }
    $process.Refresh()
    if ($null -eq $process.ExitCode) { throw 'Owned command exit unavailable' }
    return $process.ExitCode
}
try {
    if ($Source -notmatch '^[0-9a-f]{40}$' -or -not (Test-Path -LiteralPath $ContainExecutable -PathType Leaf) -or -not (Test-Path -LiteralPath $FixtureExecutable -PathType Leaf)) { throw 'Invalid experiment identity/executables' }
    $name = 'contain-demo-follow-' + [guid]::NewGuid().ToString('N')
    $root = Join-Path ([IO.Path]::GetTempPath()) $name
    $truth = Join-Path ([IO.Path]::GetTempPath()) ($name+'-truth')
    New-Item -ItemType Directory -Path $root,$truth,(Join-Path $root 'A') | Out-Null
    foreach ($directory in @($root,$truth)) { [IO.File]::WriteAllText((Join-Path $directory '.contain-demo-marker'),'Contain test fixture') }
    $env:CONTAIN_FIXTURE_TRUTH = $truth
    $database = Join-Path $private 'follow-record.db'
    $targets = @(
        @{label='parent';role='followparent';path=(Join-Path $root 'A/parent-target.txt');content="Contain follow experiment: exact parent write`n"},
        @{label='child';role='followchild';path=(Join-Path $root 'B/child-target.txt');content="Contain follow experiment: exact direct child write`n"},
        @{label='unrelated';role='followunrelated';path=(Join-Path $root 'A/unrelated-target.txt');content="Contain follow experiment: independent writer, never installer`n"}
    )
    foreach ($target in $targets) {
        if (Test-Path -LiteralPath $target.path) { throw 'Answer file already existed before the experiment' }
        if ($target.path.StartsWith((Split-Path $FixtureExecutable)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Answers must be outside the package' }
        $target.content_sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($target.content))).ToLowerInvariant()
        $target.Remove('content')
    }
    if (Test-Path -LiteralPath (Join-Path $root 'B')) { throw 'Directory B must be created by the direct child at runtime' }
    $expected = @{schema_version=1;targets=$targets;capture_exit_code=$null;directory_b_existed_before=$false;product_watch_arguments=@();source_sha=$Source;
        excluded_io_directories=@($private);excluded_io_paths=@($database,(Join-Path $private 'capture.stdout'),(Join-Path $private 'capture.stderr'));
        background_files=$BackgroundFiles;truth_directory=$truth}
    $expectedPath = Join-Path $private 'expected.json'
    Write-Json $expected $expectedPath
    $phase = 'independent_writer'
    $unrelated = Start-Process -FilePath $FixtureExecutable -ArgumentList @('--role','follow-unrelated','--root',('"'+$root+'"')) -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $private 'unrelated.stdout') -RedirectStandardError (Join-Path $private 'unrelated.stderr')
    $null = $unrelated.Handle
    $ownedProcesses.Add($unrelated)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $root '.follow-unrelated-ready'))) {
        if ($deadline.ElapsedMilliseconds -gt 10000 -or $unrelated.HasExited) { throw 'Independent writer readiness failed' }
        Start-Sleep -Milliseconds 20
    }
    if ($BackgroundFiles -gt 0) {
        $env:CONTAIN_FIXTURE_BACKGROUND_WAIT = '1'
        # Existing fixture writes, renames and deletes each file, with independent
        # syscall truth. The unrelated fixture is a sibling, never a descendant
        # of the installer. A separate bounded helper bridges the fixture signals.
        $background = Start-Process -FilePath $FixtureExecutable -ArgumentList @('--role','noise','--files',"$BackgroundFiles",'--root',('"'+$root+'"')) -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $private 'background.stdout') -RedirectStandardError (Join-Path $private 'background.stderr')
        $null = $background.Handle
        $ownedProcesses.Add($background)
        $deadline.Restart()
        while (-not (Test-Path -LiteralPath (Join-Path $root '.noise-ready'))) {
            if ($deadline.ElapsedMilliseconds -gt 10000 -or $background.HasExited) { throw 'Background fixture readiness failed' }
            Start-Sleep -Milliseconds 20
        }
        $signalCode = '$r=' + "'" + $root.Replace("'","''") + "';" + '$n=[Diagnostics.Process]::GetProcessById(' + $background.Id + ');$null=$n.Handle;$d=[Diagnostics.Stopwatch]::StartNew(); while(-not [IO.File]::Exists([IO.Path]::Combine($r,".follow-go"))){if($d.ElapsedMilliseconds -gt 45000){exit 2};[Threading.Thread]::Sleep(10)};[IO.File]::WriteAllText([IO.Path]::Combine($r,".fixture-go"),"begin");if(-not $n.WaitForExit(45000)){exit 3};$n.Refresh();if($n.ExitCode -ne 0){exit 4};[IO.File]::WriteAllText([IO.Path]::Combine($r,".pair-background-finished"),"background complete")'
        $encodedSignal = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($signalCode))
        $backgroundSignal = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',$encodedSignal) -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $private 'signal.stdout') -RedirectStandardError (Join-Path $private 'signal.stderr')
        $null = $backgroundSignal.Handle
        $ownedProcesses.Add($backgroundSignal)
    }
    if ($IdentityDiagnosticExe -and -not (Test-Path -LiteralPath $IdentityDiagnosticExe -PathType Leaf)) { throw 'Identity diagnostic executable missing' }
    # Explicitly keep all acceptance runs unprofiled. The two diagnostic runs
    # use the same binary; only this opt-in instrumentation changes.
    $env:CONTAIN_PROFILE_PATH = if ($Profile) { Join-Path $private 'receiver-profile.json' } else { $null }
    $env:CONTAIN_PROFILE_THREAD_CPU = if ($Profile) { '1' } else { $null }
    $phase = 'one_follow_capture'
    # Answer paths are fixture arguments only. There is deliberately no --watch,
    # root inference or broad snapshot path in this product invocation.
    $captureArgs = @('--db',$database,'install','--status-json','--follow-installer','--require-etw','--name','Follow installer controlled experiment','--settle-ms','1500','--max-drain-ms','10000','--evidence-quota-mib','64',$FixtureExecutable,'--','--role','follow-parent','--root',$root)
    Write-Json $captureArgs (Join-Path $private 'capture-command.json')
    $captureExit = Start-Owned $ContainExecutable $captureArgs 'capture' 120000
    $expected.capture_exit_code = $captureExit
    $statuses = @(Get-Content -LiteralPath (Join-Path $private 'capture.stdout') | ForEach-Object { try { $_ | ConvertFrom-Json } catch {} } | Where-Object { $_.kind -eq 'install_status' })
    if ($statuses.Count) { $lastStatus = $statuses[-1].data }
    Write-Json $expected $expectedPath
    if ($captureExit -ne 0 -and ($null -eq $lastStatus -or -not $lastStatus.installer_started -or -not (Test-Path -LiteralPath $database -PathType Leaf))) {
        $phase = 'capture_failed_before_readable_evidence'
        throw 'Collector failed before readable launched evidence; its final stage and provider availability are retained without exposing original private errors'
    }
    if (-not $unrelated.WaitForExit(10000)) { throw 'Independent writer failed to finish' }
    $unrelated.Refresh()
    $expected.independent_exit_code = $unrelated.ExitCode
    if ($null -ne $background) {
        if (-not $background.WaitForExit(30000) -or -not $backgroundSignal.WaitForExit(1000)) { throw 'Background fixture or signal exceeded its deadline' }
        $background.Refresh(); $backgroundSignal.Refresh()
        $expected.background_exit_code = $background.ExitCode
        $expected.background_signal_exit_code = $backgroundSignal.ExitCode
    }
    foreach ($target in $targets) {
        $target.actual_content_sha256 = if (Test-Path -LiteralPath $target.path -PathType Leaf) { (Get-FileHash -LiteralPath $target.path -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
        $target.content_verified = $target.actual_content_sha256 -eq $target.content_sha256
    }
    Write-Json $expected $expectedPath
    $phase = 'fresh_process_readback'
    if (-not $statuses.Count) { throw 'Capture produced no structured session identity' }
    $session = $statuses[0].data.session_id
    if ($session -notmatch '^[0-9a-fA-F-]{36}$') { throw 'Invalid returned session UUID' }
    $inspectExit = Start-Owned $ContainExecutable @('--db',$database,'--read-only','inspect',$session,'--json') 'inspect' 30000
    if ($inspectExit -ne 0 -or (Get-Item -LiteralPath (Join-Path $private 'inspect.stdout')).Length -gt 67108864) { throw 'Saved-session readback failed or exceeded its output bound' }
    $captureJson = Join-Path $private 'inspect.stdout'
    $selectionPath = Join-Path $private 'selection.json'
    python "$PSScriptRoot/verify-follow-installer-experiment.py" --capture $captureJson --truth $truth --expected $expectedPath --select-only --output $selectionPath --source $Source
    if ($LASTEXITCODE -ne 0) { throw 'Candidate selection failed' }
    $selection = Get-Content -LiteralPath $selectionPath -Raw | ConvertFrom-Json
    if ($IdentityDiagnosticExe) {
        $identityExit = Start-Owned ([IO.Path]::GetFullPath($IdentityDiagnosticExe)) @('--db',$database,'--session',$session,'--capture',$captureJson,'--expected',$expectedPath,'--truth',$truth,'--selection',$selectionPath) 'identity-diagnostic' 30000
        Copy-Item -LiteralPath (Join-Path $private 'identity-diagnostic.stdout') -Destination (Join-Path $public 'follow-identity-diagnostic.json')
        if ($identityExit -ne 0) { throw 'Read-only identity replay failed; fixed diagnostic reason retained' }
    }
    if ($Profile) {
        python "$PSScriptRoot/summarize-receiver-profile.py" --profile (Join-Path $private 'receiver-profile.json') --capture $captureJson --output (Join-Path $public 'receiver-profile-summary.json')
        if ($LASTEXITCODE -ne 0) { throw 'Bounded receiver profile summary failed' }
    }
    $explanations = Join-Path $private 'explanations'
    New-Item -ItemType Directory -Path $explanations | Out-Null
    foreach ($eventId in $selection.candidate_event_ids) {
        if ($eventId -notmatch '^[A-Za-z0-9._-]{1,160}$') { throw 'Invalid selected event identity' }
        $exit = Start-Owned $ContainExecutable @('--db',$database,'--read-only','explain',$eventId,'--json') ('explain-'+$eventId) 30000
        if ($exit -ne 0) { throw 'Fresh-process explanation failed' }
        Copy-Item -LiteralPath (Join-Path $private ('explain-'+$eventId+'.stdout')) -Destination (Join-Path $explanations ($eventId+'.json'))
    }
    $phase = 'independent_evidence_verification'
    python "$PSScriptRoot/verify-follow-installer-experiment.py" --capture $captureJson --truth $truth --expected $expectedPath --explanations $explanations --output $summary --source $Source
    if ($LASTEXITCODE -ne 0) { throw 'Independent follow-installer evidence gate failed; see retained aggregate' }
    Write-Output 'Controlled follow-installer experiment passed; safe aggregate retained.'
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $private 'failure.txt') -Encoding utf8
    if (-not (Test-Path -LiteralPath $summary)) {
        $safeStatus = if ($null -ne $lastStatus) { @{stage=$lastStatus.stage;step=$lastStatus.step;etw_process=$lastStatus.etw_process;etw_file=$lastStatus.etw_file;etw_registry=$lastStatus.etw_registry;installer_started=$lastStatus.installer_started;installer_exited=$lastStatus.installer_exited;capture_finished=$lastStatus.capture_finished;data_saved=$lastStatus.data_saved} } else { $null }
        Write-Json @{schema_version=1;protocol='contain-follow-experiment-1';source_sha=$Source;accepted=$false;phase=$phase;error_type=$_.Exception.GetType().Name;capture_exit_code=$captureExit;last_status=$safeStatus;maximum_native_captures=1;retries=0;not_run=@('source_writer_identity','direct_child_identity','unrelated_not_promoted','saved_session_restart','missing_before_baseline')} $summary
    }
    throw "Follow-installer experiment failed at $phase; safe evidence retained; no looser attribution accepted."
} finally {
    $env:CONTAIN_FIXTURE_TRUTH = $previousTruth
    $env:CONTAIN_PROFILE_PATH = $previousProfile
    $env:CONTAIN_PROFILE_THREAD_CPU = $previousProfileCpu
    $env:CONTAIN_FIXTURE_BACKGROUND_WAIT = $previousBackgroundWait
    if ($BackgroundFiles -gt 0) {
        # Never begin the paired candidate while a prior owned collector or
        # fixture can still generate activity. Keep exact handles; no PID kills.
        $quiescence = [Diagnostics.Stopwatch]::StartNew()
        do {
            $remaining = @($ownedProcesses | Where-Object { -not $_.HasExited }).Count
            if ($remaining -eq 0 -or $quiescence.ElapsedMilliseconds -ge 45000) { break }
            Start-Sleep -Milliseconds 20
        } while ($true)
        Write-Json @{all_owned_processes_exited=($remaining -eq 0);owned_processes=$ownedProcesses.Count;remaining=$remaining;maximum_wait_ms=45000} (Join-Path $private 'worker-termination.json')
    }
}
