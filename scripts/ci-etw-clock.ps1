param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$PublicOutputDirectory,
    [ValidateSet('diagnostic', 'verification', 'candidate')][string]$Phase = 'diagnostic',
    [ValidateRange(1, 2)][int]$AttemptIndex = 1,
    [ValidateSet('default', 'dual-clock')][string]$Configuration = 'dual-clock',
    [string]$TrialDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) { throw 'The fixed ETW clock diagnostic requires native Windows.' }

function Get-ClockAttemptPlan([string]$Phase, [int]$Index, [string]$Configuration, [string]$Trial) {
    if ($Phase -eq 'candidate') {
        if (-not $Trial -or -not (($Index -eq 1 -and $Configuration -eq 'default') -or
            ($Index -eq 2 -and $Configuration -eq 'dual-clock'))) {
            throw 'The candidate plan is one default attempt followed by one dual-clock attempt using the frozen package.'
        }
    } elseif ($Phase -notin @('diagnostic', 'verification') -or $Configuration -ne 'dual-clock' -or $Trial -or
        $Index -notin @(1, 2) -or ($Phase -eq 'diagnostic' -and $Index -ne 1)) {
        throw 'The historical diagnostic/verification plan only permits its fixed clock attempts.'
    }
    [ordered]@{ planned_attempts=$(if ($Phase -eq 'diagnostic') { 1 } else { 2 });
        clock_requested=($Configuration -eq 'dual-clock'); report_requested=($Phase -eq 'candidate') }
}

function Set-ClockAttemptEnvironment([bool]$ClockRequested, [string]$RawClockPath) {
    $previous = @{}
    # Ordinary product configuration must not inherit experimental clocks,
    # profiling, stack/WPR hints or storage variants. Never start/stop WPR here.
    foreach ($entry in @(Get-ChildItem Env:CONTAIN_*)) {
        $previous[$entry.Name] = $entry.Value
        Remove-Item -LiteralPath ('Env:' + $entry.Name)
    }
    if ($ClockRequested) {
        $env:CONTAIN_FIXTURE_CLOCK = '1'
        $env:CONTAIN_ETW_RAW_CLOCK_PATH = $RawClockPath
    }
    return $previous
}

function Restore-ClockAttemptEnvironment([Collections.IDictionary]$Previous) {
    foreach ($entry in @(Get-ChildItem Env:CONTAIN_*)) { Remove-Item -LiteralPath ('Env:' + $entry.Name) }
    foreach ($name in $Previous.Keys) { Set-Item -LiteralPath ('Env:' + $name) -Value $Previous[$name] }
}

function Invoke-ClockFollowups([bool]$ClockRequested, [bool]$ReportRequested, [scriptblock]$Analyze, [scriptblock]$Report) {
    $result = [ordered]@{ passed=$true }
    foreach ($stage in @(
        @{ name='analysis'; requested=$ClockRequested; run=$Analyze },
        @{ name='report'; requested=$ReportRequested; run=$Report })) {
        $outcome = [ordered]@{ status='not_requested'; exit_code=$null; error_type=$null }
        if ($stage.requested) {
            try {
                $code = & $stage.run
                if ($code -isnot [int]) { throw 'The stage did not return an exit code.' }
                $outcome.exit_code = $code
                $outcome.status = if ($code -eq 0) { 'passed' } else { 'failed' }
            } catch {
                $outcome.status = 'failed'
                $outcome.error_type = $_.Exception.GetType().Name
            }
            if ($outcome.status -ne 'passed') { $result.passed = $false }
        }
        $result[$stage.name] = $outcome
    }
    return $result
}

$plan = Get-ClockAttemptPlan $Phase $AttemptIndex $Configuration $TrialDirectory

function Get-NewClockDestination([string]$Value) {
    try {
        if (-not [IO.Path]::IsPathFullyQualified($Value)) { throw 'relative path' }
        $full = [IO.Path]::GetFullPath($Value).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $leaf = [IO.Path]::GetFileName($full)
        if (-not $leaf -or $leaf.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $leaf.TrimEnd(' ', '.') -cne $leaf) { throw 'invalid destination name' }
        if (Test-Path -LiteralPath $full) { throw 'destination already exists' }
        $parent = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($full))
        if ($parent -isnot [IO.DirectoryInfo]) { throw 'parent is not a directory' }
        # Do not let a junction/symlink alias defeat the private/public boundary.
        for ($ancestor = $parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
            if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'redirected parent' }
        }
        return $full
    } catch { throw 'Clock output requires a new absolute directory with an existing, non-redirected parent.' }
}

function Get-ClockSourceRevision {
    $value = (& git rev-parse HEAD 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $value -cnotmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the diagnostic source revision.' }
    return $value
}

function Get-ClockSourceStatus {
    $rows = @(& git status --porcelain=v1 --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify diagnostic source changes.' }
    return $rows
}

function Get-ClockScriptIdentity {
    $paths = @(& git ls-files --cached -- 'scripts/*' 2>$null | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Cannot enumerate tracked diagnostic scripts.' }
    foreach ($path in $paths) {
        [ordered]@{ path=$path; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}

$privateRoot = Get-NewClockDestination $OutputDirectory
$publicRoot = Get-NewClockDestination $PublicOutputDirectory
$separator = [IO.Path]::DirectorySeparatorChar
if ($privateRoot.Equals($publicRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $privateRoot.StartsWith($publicRoot + $separator, [StringComparison]::OrdinalIgnoreCase) -or
    $publicRoot.StartsWith($privateRoot + $separator, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Private and public clock output directories must be separate and non-nested.'
}

$packageRoot = $null
$entryScripts = $PSScriptRoot
if ($TrialDirectory) {
    $packageRoot = (Get-Item -LiteralPath $TrialDirectory -ErrorAction Stop).FullName.TrimEnd($separator)
    foreach ($path in @($privateRoot, $publicRoot)) {
        if ($path.Equals($packageRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($packageRoot + $separator, [StringComparison]::OrdinalIgnoreCase) -or
            $packageRoot.StartsWith($path + $separator, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The frozen package and evidence output directories must be separate and non-nested.'
        }
    }
    $entryScripts = Join-Path $packageRoot 'scripts'
}
$previousEnvironment = $null
$demoExit = 1
$followups = $null
$attempt = [ordered]@{
    schema_version=1; phase=$Phase; planned_attempts=$plan.planned_attempts; attempt_index=$AttemptIndex; configuration=$Configuration
    source_sha=$null; source_after_sha=$null
    source_status_before=@(); source_status_after=@()
    source_clean_before=$false; source_clean_after=$false; source_unchanged=$false
    fixture_clock_requested=$(if ($plan.clock_requested) { '1' } else { $null }); raw_clock_requested=$plan.clock_requested
    build_manifest_sha256=$null; build_manifest_unchanged=$null; package_files_unchanged=$null
    optional_environment_isolated=$false; wpr_started=$false
    sid=$null; scripts_before=@(); scripts_after=@(); scripts_unchanged=$false
    demo_started=$false; demo_exit_code=$null
    started_at_utc=[DateTime]::UtcNow.ToString('o'); finished_at_utc=$null
}
Push-Location (Join-Path $PSScriptRoot '..')
try {
    # No Force: refuse an existing directory rather than overwriting an attempt.
    New-Item -ItemType Directory -Path $privateRoot | Out-Null
    $attemptPath = Join-Path $privateRoot 'attempt.json'
    $rawClock = Join-Path $privateRoot 'raw-clock.json'
    $captureDirectory = Join-Path $privateRoot 'capture'
    try {
        $attempt.source_sha = Get-ClockSourceRevision
        $attempt.source_status_before = @(Get-ClockSourceStatus)
        $attempt.source_clean_before = $attempt.source_status_before.Count -eq 0
        # The analyzer is a development verifier and remains in the source
        # checkout. Package script identities are separately bound by BUILD.json.
        $attempt.scripts_before = @(Get-ClockScriptIdentity)
        $attempt.sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if (-not $attempt.source_clean_before) { throw 'The fixed diagnostic requires a clean source checkout.' }
        if ($packageRoot) {
            $buildManifest = Join-Path $packageRoot 'BUILD.json'
            . (Join-Path $PSScriptRoot 'demo-provenance.ps1')
            $frozen = Get-DemoPrebuiltExecutables -ContainExecutable (Join-Path $packageRoot 'contain.exe') -FixtureExecutable (Join-Path $packageRoot 'contain-test-installer.exe') -BuildManifest $buildManifest
            if ($frozen.manifest.source_sha -cne $attempt.source_sha) { throw 'The frozen package does not match this source revision.' }
            $attempt.build_manifest_sha256 = $frozen.manifest_sha256
        }
        if ((Test-Path -LiteralPath $rawClock) -or (Test-Path -LiteralPath ($rawClock + '.rows.bin'))) {
            throw 'The raw clock destination must be unused.'
        }
        $previousEnvironment = Set-ClockAttemptEnvironment $plan.clock_requested $rawClock
        $attempt.optional_environment_isolated = $true
        $attempt.demo_started = $true
        Write-Host "Starting planned $Configuration ETW $Phase attempt $AttemptIndex of $($plan.planned_attempts)."
        # Run the frozen package scripts and executables themselves. The old
        # development path remains available for the historical clock plans.
        $demoArguments = @{ RequireEtw=$true; KeepArtifacts=$true; OutputDirectory=$captureDirectory }
        if ($packageRoot) {
            $demoArguments.ContainExecutable = Join-Path $packageRoot 'contain.exe'
            $demoArguments.FixtureExecutable = Join-Path $packageRoot 'contain-test-installer.exe'
            $demoArguments.BuildManifest = $buildManifest
        }
        & (Join-Path $entryScripts 'demo.ps1') @demoArguments *> (Join-Path $privateRoot 'demo.log')
        $demoExit = 0
    } catch {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $privateRoot 'demo.error.txt') -Encoding utf8
        $demoExit = 1
        Write-Host 'The diagnostic demo failed; retained local evidence will still be analyzed.'
    } finally {
        if ($null -ne $previousEnvironment) {
            Restore-ClockAttemptEnvironment $previousEnvironment
            $previousEnvironment = $null
        }
        $attempt.demo_exit_code = $demoExit
        try {
            $attempt.source_after_sha = Get-ClockSourceRevision
            $attempt.source_status_after = @(Get-ClockSourceStatus)
            $attempt.source_clean_after = $attempt.source_status_after.Count -eq 0
            $attempt.source_unchanged = $attempt.source_clean_before -and $attempt.source_clean_after -and
                $attempt.source_sha -ceq $attempt.source_after_sha
            $attempt.scripts_after = @(Get-ClockScriptIdentity)
            $before = ($attempt.scripts_before | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
            $after = ($attempt.scripts_after | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
            $attempt.scripts_unchanged = $attempt.scripts_before.Count -gt 0 -and $before -ceq $after
            if ($packageRoot) {
                $checked = Get-DemoPrebuiltExecutables -ContainExecutable (Join-Path $packageRoot 'contain.exe') -FixtureExecutable (Join-Path $packageRoot 'contain-test-installer.exe') -BuildManifest (Join-Path $packageRoot 'BUILD.json')
                $attempt.build_manifest_unchanged = $attempt.build_manifest_sha256 -ceq $checked.manifest_sha256
                $attempt.package_files_unchanged = $attempt.build_manifest_unchanged
                if (-not $attempt.package_files_unchanged) { throw 'The frozen package manifest changed during capture.' }
            }
        } catch {
            $_ | Out-String | Set-Content -LiteralPath (Join-Path $privateRoot 'identity.error.txt') -Encoding utf8
            $attempt.source_unchanged = $false
            $attempt.scripts_unchanged = $false
            if ($packageRoot) { $attempt.package_files_unchanged = $false }
        }
        $attempt.finished_at_utc = [DateTime]::UtcNow.ToString('o')
        $attempt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attemptPath -Encoding utf8
        $followups = Invoke-ClockFollowups $plan.clock_requested $plan.report_requested {
            python (Join-Path $PSScriptRoot 'analyze-etw-clock.py') --directory $privateRoot --public-output $publicRoot *> (Join-Path $privateRoot 'analysis.log')
            return $LASTEXITCODE
        } {
            if (-not (Test-Path -LiteralPath $publicRoot)) { New-Item -ItemType Directory -Path $publicRoot | Out-Null }
            python (Join-Path $PSScriptRoot 'package-trial.py') report --package $packageRoot --capture-directory $captureDirectory --private-output (Join-Path $privateRoot 'report') --public-output (Join-Path $publicRoot 'report') --scenario $Configuration *> (Join-Path $privateRoot 'report.log')
            return $LASTEXITCODE
        }
        # The analyzer hashes attempt.json as an input; leave it immutable.
        [ordered]@{ schema_version=1; status=$followups.analysis.status; exit_code=$followups.analysis.exit_code } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $privateRoot 'analysis-exit.json') -Encoding utf8
        $followups | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $privateRoot 'followups.json') -Encoding utf8
        if (-not (Test-Path -LiteralPath $publicRoot)) { New-Item -ItemType Directory -Path $publicRoot | Out-Null }
        [ordered]@{ schema_version=1; phase=$Phase; configuration=$Configuration; attempt_index=$AttemptIndex;
            planned_attempts=$plan.planned_attempts; source_sha=$attempt.source_sha; demo_exit_code=$demoExit;
            analysis=$followups.analysis; report=$followups.report; source_unchanged=$attempt.source_unchanged;
            scripts_unchanged=$attempt.scripts_unchanged; build_manifest_sha256=$attempt.build_manifest_sha256;
            build_manifest_unchanged=$attempt.build_manifest_unchanged; package_files_unchanged=$attempt.package_files_unchanged;
            optional_environment_isolated=$attempt.optional_environment_isolated; wpr_started=$false;
            formal_acceptance='requirements_unmet' } | ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath (Join-Path $publicRoot 'execution.json') -Encoding utf8
        Write-Host "ETW attempt finished: demo exit $demoExit; analysis $($followups.analysis.status); CLI report $($followups.report.status)."
    }
    if ($demoExit -ne 0 -or -not $followups.passed -or -not $attempt.source_unchanged -or -not $attempt.scripts_unchanged -or
        ($packageRoot -and -not $attempt.package_files_unchanged)) {
        throw 'ETW clock diagnostic did not pass; preserve the original failure and reviewed evidence summary.'
    }
} finally {
    if ($null -ne $previousEnvironment) { Restore-ClockAttemptEnvironment $previousEnvironment }
    Pop-Location
}
