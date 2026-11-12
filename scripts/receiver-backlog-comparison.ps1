param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$ObserverPatchSha256,
    [Parameter(Mandatory)][string]$PlanSha256,
    [Parameter(Mandatory)][string]$OutputDirectory
)
# One explicitly dispatched investigation. No automatic reruns or release action.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$script:FailedGuard = $null
function Require([bool]$Value,[string]$Reason) { if (-not $Value) { $script:FailedGuard = $Reason; throw $Reason } }
function Write-Json([object]$Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 64),[Text.UTF8Encoding]::new($false))
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Record-Test([string]$Variant,[string]$Filter,[string]$LogPath,[int]$Code) {
    $text = Get-Content -LiteralPath $LogPath -Raw
    $match = [regex]::Match($text,'test result: (ok|FAILED)\. ([0-9]+) passed; ([0-9]+) failed; ([0-9]+) ignored; ([0-9]+) measured; ([0-9]+) filtered out;')
    $row = [ordered]@{variant=$Variant;filter=$Filter;exit_code=$Code;summary_available=$match.Success;passed=$null;failed=$null;ignored=$null;measured=$null;filtered_out=$null}
    if ($match.Success) {
        $row.passed=[int]$match.Groups[2].Value; $row.failed=[int]$match.Groups[3].Value
        $row.ignored=[int]$match.Groups[4].Value; $row.measured=[int]$match.Groups[5].Value; $row.filtered_out=[int]$match.Groups[6].Value
    }
    $script:preflight.tests += $row
    Write-Json $script:preflight (Join-Path $script:public 'preflight.json')
}

Require ($env:OS -eq 'Windows_NT') 'Investigation requires native Windows'
Require ($env:GITHUB_RUN_ATTEMPT -eq '1') 'Retain the original attempt; retries are forbidden'
Require ($Source -cmatch '^[0-9a-f]{40}$' -and $ObserverPatchSha256 -cmatch '^[0-9a-f]{64}$' -and $PlanSha256 -cmatch '^[0-9a-f]{64}$') 'Invalid frozen identity'
Require ((git rev-parse HEAD).Trim() -ceq $Source) 'Frozen source differs'
Require ($env:GITHUB_SHA -ceq $Source) 'Dispatch source differs'
$planPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../docs/beta2-validation-plan.md'))
Require ((Hash $planPath) -ceq $PlanSha256) 'Frozen written plan differs'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Require (-not (Test-Path -LiteralPath $OutputDirectory)) 'Previous experiment outputs must be retained'
$public = Join-Path $OutputDirectory 'public'
$private = Join-Path $OutputDirectory 'private'
$frozen = Join-Path $OutputDirectory 'frozen-binaries'
New-Item -ItemType Directory -Path $OutputDirectory,$public,$private,$frozen | Out-Null
$order = @('r1/baseline','r1/candidate','r2/candidate','r2/baseline')
$plan = [ordered]@{schema_version=1;protocol='contain-receiver-backlog-plan-1';source_sha=$Source;written_plan_sha256=$PlanSha256;common_observer_patch_sha256=$ObserverPatchSha256;baseline_source='f76a8618426945093eacdd5751d49d2633635e78';candidate_behavior_base='347a86ee730c4fd2d9ee775add60a6c5deddf7fa';maximum_native_captures=4;retries=0;order=$order;background_files=12000;independent_background_operations=36000;profile=$true;backlog_window=$true;thread_cpu=$true;threshold_records=1024;pre_records=64;post_records=64;maximum_window_records=129;build_profile='debug';target='x86_64-pc-windows-msvc';queue_capacity=4096;candidate_records=2048;candidate_bytes=8388608;settle_ms=1500;maximum_drain_ms=10000;evidence_quota_bytes=67108864;is_release_acceptance=$false;is_original_release_zip=$false;build_identity=$null}
$execution = [ordered]@{attempted=@();not_run=@($order);stop_reason=$null}
Write-Json $plan (Join-Path $public 'run-plan.json')
Write-Json $execution (Join-Path $public 'execution.json')
$phase = 'prepare_common_observer_patch'
$preflight = [ordered]@{accepted=$false;phase=$phase;native_captures_started=0;error_type=$null;failed_guard=$null;checks=@();tests=@();builds=@();bench=$null}
$previousTarget = $env:CARGO_TARGET_DIR
$previousBacklog = $env:CONTAIN_PROFILE_BACKLOG
$previousCpu = $env:CONTAIN_PROFILE_THREAD_CPU
$previousInteractive = $env:CONTAIN_FIXTURE_INTERACTIVE
try {
    python "$PSScriptRoot/prepare-backlog-comparison.py" --repo $repo --source $Source --output (Join-Path $OutputDirectory 'inputs') --observer-patch-sha256 $ObserverPatchSha256 *> (Join-Path $private 'prepare.log')
    Require ($LASTEXITCODE -eq 0) 'Common observation patch could not be safely applied before live work'
    $inputs = Join-Path $OutputDirectory 'inputs'
    $paths = Get-Content -LiteralPath (Join-Path $inputs 'private/source-paths.json') -Raw | ConvertFrom-Json -AsHashtable
    $identity = Get-Content -LiteralPath (Join-Path $inputs 'public/build-inputs.json') -Raw | ConvertFrom-Json -AsHashtable
    Copy-Item -LiteralPath (Join-Path $inputs 'public/build-inputs.json') -Destination (Join-Path $public 'build-inputs.json')
    Copy-Item -LiteralPath (Join-Path $inputs 'private/common-observer.patch') -Destination (Join-Path $frozen 'common-observer.patch')
    $compiler = (& rustc -vV | Out-String).Trim()
    Require ($compiler -match 'host: x86_64-pc-windows-msvc') 'Expected the same native MSVC x64 compiler for both variants'
    $toolchain = [ordered]@{rustc=$compiler;cargo=((& cargo -V | Out-String).Trim());git=((& git --version | Out-String).Trim());python=((& python --version | Out-String).Trim());windows=[Environment]::OSVersion.Version.ToString();os_architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString();process_architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString();runner_architecture=$env:RUNNER_ARCH;emulation_verified=$false}
    Write-Json $toolchain (Join-Path $public 'toolchain.json')
    $toolchainSha = Hash (Join-Path $public 'toolchain.json')
    $env:CARGO_TARGET_DIR = Join-Path $repo 'target/receiver-backlog-build'
    Copy-Item -LiteralPath (Join-Path $repo 'docs/examples/beta2/receiver-window-offline-summary.json') -Destination (Join-Path $public 'offline-cost.json')
    $producerTest = 'windows::etw::metrics::tests::producer_milestones_use_successful_admission_and_keep_first_time'
    $invariance = 'ingest::integration_tests::profiling_switch_preserves_actual_follow_receiver_evidence_and_boundaries'
    foreach ($variant in @('baseline','candidate')) {
        $phase = 'offline_observation_gates_' + $variant
        Push-Location $paths[$variant]
        try {
            cargo test -p contain-core --lib --locked -- --list *> (Join-Path $private ($variant+'-test-list.log'))
            Require ($LASTEXITCODE -eq 0) 'Actual production test enumeration failed'
            $listing = Get-Content -LiteralPath (Join-Path $private ($variant+'-test-list.log')) -Raw
            Require ($listing.Contains($invariance + ': test')) 'Actual profile-invariance test is absent'
            Require ($listing.Contains($producerTest + ': test')) 'Actual producer milestone test is absent'
            Require (([regex]::Matches($listing,'profile::backlog::tests::[^\r\n]+: test')).Count -ge 3) 'Actual bounded-window reliability tests are absent'
            cargo test -p contain-core --lib --locked $invariance -- --exact *> (Join-Path $private ($variant+'-invariance.log'))
            $testExit=$LASTEXITCODE
            Record-Test $variant $invariance (Join-Path $private ($variant+'-invariance.log')) $testExit
            Require ($testExit -eq 0) 'Actual receiver profiling changed evidence or boundaries'
            Require ((Get-Content -LiteralPath (Join-Path $private ($variant+'-invariance.log')) -Raw) -match 'test result: ok\. 1 passed; 0 failed; 0 ignored;') 'Invariance test was skipped or did not execute'
            cargo test -p contain-core --lib --locked profile::backlog::tests *> (Join-Path $private ($variant+'-windows.log'))
            $testExit=$LASTEXITCODE
            Record-Test $variant 'profile::backlog::tests' (Join-Path $private ($variant+'-windows.log')) $testExit
            Require ($testExit -eq 0) 'Actual bounded receiver-window tests failed'
            $windowResult = [regex]::Match((Get-Content -LiteralPath (Join-Path $private ($variant+'-windows.log')) -Raw),'test result: ok\. ([0-9]+) passed; 0 failed; 0 ignored;')
            Require ($windowResult.Success -and [int]$windowResult.Groups[1].Value -ge 3) 'Window reliability tests were skipped or not executed'
            cargo test -p contain-core --lib --locked $producerTest -- --exact *> (Join-Path $private ($variant+'-producer.log'))
            $testExit=$LASTEXITCODE
            Record-Test $variant $producerTest (Join-Path $private ($variant+'-producer.log')) $testExit
            Require ($testExit -eq 0) 'Actual producer first-milestone reliability test failed'
            Require ((Get-Content -LiteralPath (Join-Path $private ($variant+'-producer.log')) -Raw) -match 'test result: ok\. 1 passed; 0 failed; 0 ignored;') 'Producer milestone test was skipped or not executed'
            $followTests = @('follow::tests::profiling_switch_preserves_saturated_follow_boundaries_and_conservative_evidence','follow::tests::final_flush_checkpoint_preserves_empty_admission_and_discarded_pending_state')
            $followIndex = 0
            foreach ($followTest in $followTests) {
                Require ($listing.Contains($followTest + ': test')) 'Actual follow capacity or final-checkpoint regression is absent'
                $followLog = Join-Path $private ($variant+'-follow-'+$followIndex+'.log')
                cargo test -p contain-core --lib --locked $followTest -- --exact *> $followLog
                $testExit=$LASTEXITCODE
                Record-Test $variant $followTest $followLog $testExit
                Require ($testExit -eq 0) 'Actual follow capacity or final-checkpoint regression failed'
                Require ((Get-Content -LiteralPath $followLog -Raw) -match 'test result: ok\. 1 passed; 0 failed; 0 ignored;') 'Follow capacity or final-checkpoint test was skipped or did not execute'
                $followIndex += 1
            }
            cargo build -p contain-cli -p contain-test-installer --locked *> (Join-Path $private ($variant+'-build.log'))
            $buildExit=$LASTEXITCODE
            $preflight.builds += [ordered]@{variant=$variant;profile='debug';packages=@('contain-cli','contain-test-installer');exit_code=$buildExit}
            Require ($buildExit -eq 0) 'Native debug product or independent fixture build failed'
            Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'debug/contain.exe') -Destination (Join-Path $frozen ($variant+'-contain.exe'))
            if ($variant -eq 'candidate') {
                Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'debug/contain-test-installer.exe') -Destination (Join-Path $frozen 'contain-test-installer.exe')
            }
            $preflight.checks += [ordered]@{variant=$variant;invariance_passed=$true;bounded_window_tests_passed=$true;producer_milestone_test_passed=$true;follow_capacity_and_flush_tests_passed=$true;debug_build_passed=$true}
        } finally { Pop-Location }
    }
    $phase = 'existing_candidate_synthetic_bench'
    $benchDirectory = Join-Path $private 'candidate-bench'
    $benchPassed = $false; $benchErrorType = $null
    Push-Location $paths.candidate
    try {
        try {
            ./scripts/quality.ps1 -Mode Bench -OutputDirectory $benchDirectory *> (Join-Path $private 'candidate-bench.log')
            $benchPassed = $LASTEXITCODE -eq 0
        } catch { $benchErrorType = $_.Exception.GetType().Name }
    } finally { Pop-Location }
    python "$PSScriptRoot/summarize-backlog-comparison.py" --benchmark-directory $benchDirectory --source $Source --output (Join-Path $public 'existing-bench.json') *> (Join-Path $private 'bench-summary.log')
    $benchSummaryExit=$LASTEXITCODE
    $preflight.bench = [ordered]@{mode='Bench';source_sha=$Source;fixed_events=20000;fixed_samples=7;passed=$benchPassed;error_type=$benchErrorType;safe_summary_exit_code=$benchSummaryExit;synthetic=$true;real_etw=$false}
    Write-Json $preflight (Join-Path $public 'preflight.json')
    Require $benchPassed 'Existing candidate synthetic Bench quality gate failed'
    Require ($benchSummaryExit -eq 0) 'Existing candidate Bench numeric summary is unavailable or failed'
    $phase = 'freeze_native_identities'
    $binaries = [ordered]@{}
    foreach ($name in @('baseline-contain.exe','candidate-contain.exe','contain-test-installer.exe')) {
        $binaries[$name] = [ordered]@{sha256=(Hash (Join-Path $frozen $name));bytes=(Get-Item -LiteralPath (Join-Path $frozen $name)).Length}
    }
    $buildIdentity = [ordered]@{schema_version=1;source_inputs=$identity;configuration='debug';target='x86_64-pc-windows-msvc';toolchain_sha256=$toolchainSha;offline_summary_sha256=$identity.reviewed_offline_summary_sha256;existing_bench_summary_sha256=(Hash (Join-Path $public 'existing-bench.json'));binaries=$binaries;fixture_source=$Source;written_plan_sha256=$PlanSha256;common_observer_patch_sha256=$ObserverPatchSha256;profile=$true;backlog_window=$true;thread_cpu=$true;rebuilt_diagnostic=$true;original_release_zip=$false}
    Write-Json $buildIdentity (Join-Path $public 'build-identity.json')
    Write-Json $buildIdentity (Join-Path $frozen 'BUILD.json')
    $plan.build_identity = $buildIdentity
    Write-Json $plan (Join-Path $public 'run-plan.json')
    $preflight.accepted = $true
    $preflight.phase = 'all_offline_observation_gates_passed'
    Write-Json $preflight (Join-Path $public 'preflight.json')
    $env:CONTAIN_PROFILE_BACKLOG = '1'
    $env:CONTAIN_PROFILE_THREAD_CPU = '1'
    $env:CONTAIN_FIXTURE_INTERACTIVE = $null
    foreach ($name in $order) {
        $phase = 'one_predeclared_capture'
        foreach ($binary in $binaries.Keys) { Require ((Hash (Join-Path $frozen $binary)) -ceq $binaries[$binary].sha256) 'Frozen binary changed before capture' }
        $variant = $name.Split('/')[1]
        $execution.attempted += $name
        $execution.not_run = @($order | Where-Object { $_ -notin $execution.attempted })
        $preflight.native_captures_started = $execution.attempted.Count
        Write-Json $execution (Join-Path $public 'execution.json')
        Write-Json $preflight (Join-Path $public 'preflight.json')
        try {
            $variantSource = if ($variant -eq 'baseline') { $plan.baseline_source } else { $Source }
            & "$PSScriptRoot/follow-installer-experiment.ps1" -ContainExecutable (Join-Path $frozen ($variant+'-contain.exe')) -FixtureExecutable (Join-Path $frozen 'contain-test-installer.exe') -OutputDirectory (Join-Path $OutputDirectory $name) -Source $variantSource -BackgroundFiles 12000 -Profile *> (Join-Path $private ($name.Replace('/','-')+'-experiment.log'))
        } catch {
            # The unchanged strict no-loss oracle can reject EventLoss/Unknown.
            # Keep that failure and ask the independent measurement gate below.
            Write-Host ('Retained strict experiment result: '+$name)
        }
        foreach ($binary in $binaries.Keys) { Require ((Hash (Join-Path $frozen $binary)) -ceq $binaries[$binary].sha256) 'Frozen binary changed during capture' }
        $samplePath = Join-Path $public ($name.Replace('/','-')+'.json')
        python "$PSScriptRoot/summarize-backlog-comparison.py" --sample-directory (Join-Path $OutputDirectory $name) --output $samplePath *> (Join-Path $private ($name.Replace('/','-')+'-summary.log'))
        Require ($LASTEXITCODE -eq 0) 'Safe measurement summary failed; no next capture'
        $sample = Get-Content -LiteralPath $samplePath -Raw | ConvertFrom-Json -AsHashtable
        $execution.stop_reason = $sample.continuation_stop_reason
        Write-Json $execution (Join-Path $public 'execution.json')
        if ($null -ne $execution.stop_reason) { break }
    }
} catch {
    $preflight.phase = $phase
    $preflight.error_type = $_.Exception.GetType().Name
    $preflight.failed_guard = if ($null -ne $script:FailedGuard) { $script:FailedGuard } else { 'unexpected_exception_in_fixed_phase' }
    $execution.stop_reason = if ($null -eq $execution.stop_reason) { 'preflight_or_measurement_step_failed' } else { $execution.stop_reason }
    Write-Json $preflight (Join-Path $public 'preflight.json')
    Write-Json $execution (Join-Path $public 'execution.json')
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $private 'failure.log') -Encoding utf8
} finally {
    $env:CARGO_TARGET_DIR = $previousTarget
    $env:CONTAIN_PROFILE_BACKLOG = $previousBacklog
    $env:CONTAIN_PROFILE_THREAD_CPU = $previousCpu
    $env:CONTAIN_FIXTURE_INTERACTIVE = $previousInteractive
}
python "$PSScriptRoot/summarize-backlog-comparison.py" --directory $OutputDirectory *> (Join-Path $private 'final-summary.log')
Require ($LASTEXITCODE -eq 0) 'Measurement or test failed; retained once without retry, no product fix or release claim'
