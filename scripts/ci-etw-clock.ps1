param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$PublicOutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
if (-not $IsWindows) { throw 'The fixed ETW clock diagnostic requires native Windows.' }

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

$previousFixtureClock = $env:CONTAIN_FIXTURE_CLOCK
$previousRawClock = $env:CONTAIN_ETW_RAW_CLOCK_PATH
$demoExit = 1
$analysisExit = $null
$attempt = [ordered]@{
    schema_version=1; phase='diagnostic'; planned_attempts=1
    source_sha=$null; source_after_sha=$null
    source_status_before=@(); source_status_after=@()
    source_clean_before=$false; source_clean_after=$false; source_unchanged=$false
    fixture_clock_requested='1'; raw_clock_requested=$true
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
        $attempt.scripts_before = @(Get-ClockScriptIdentity)
        $attempt.sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if (-not $attempt.source_clean_before) { throw 'The fixed diagnostic requires a clean source checkout.' }
        if ((Test-Path -LiteralPath $rawClock) -or (Test-Path -LiteralPath ($rawClock + '.rows.bin'))) {
            throw 'The raw clock destination must be unused.'
        }
        $env:CONTAIN_FIXTURE_CLOCK = '1'
        $env:CONTAIN_ETW_RAW_CLOCK_PATH = $rawClock
        $attempt.demo_started = $true
        Write-Host 'Starting the single planned ETW clock diagnostic.'
        # Keep the original score and full failure output private. The existing
        # demo remains fail-closed; its failure is rethrown after offline analysis.
        & "$PSScriptRoot/demo.ps1" -RequireEtw -KeepArtifacts -OutputDirectory $captureDirectory *> (Join-Path $privateRoot 'demo.log')
        $demoExit = 0
    } catch {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $privateRoot 'demo.error.txt') -Encoding utf8
        $demoExit = 1
        Write-Host 'The diagnostic demo failed; retained local evidence will still be analyzed.'
    } finally {
        $env:CONTAIN_FIXTURE_CLOCK = $previousFixtureClock
        $env:CONTAIN_ETW_RAW_CLOCK_PATH = $previousRawClock
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
        } catch {
            $_ | Out-String | Set-Content -LiteralPath (Join-Path $privateRoot 'identity.error.txt') -Encoding utf8
            $attempt.source_unchanged = $false
            $attempt.scripts_unchanged = $false
        }
        $attempt.finished_at_utc = [DateTime]::UtcNow.ToString('o')
        $attempt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $attemptPath -Encoding utf8
        try {
            python "$PSScriptRoot/analyze-etw-clock.py" --directory $privateRoot --public-output $publicRoot *> (Join-Path $privateRoot 'analysis.log')
            $analysisExit = $LASTEXITCODE
        } catch {
            $_ | Out-String | Set-Content -LiteralPath (Join-Path $privateRoot 'analysis.error.txt') -Encoding utf8
            $analysisExit = 1
        } finally {
            # The analyzer hashes attempt.json as an input; leave it immutable.
            [ordered]@{ schema_version=1; exit_code=$analysisExit } |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $privateRoot 'analysis-exit.json') -Encoding utf8
        }
        Write-Host "Clock diagnostic finished: demo exit $demoExit; analysis exit $analysisExit."
    }
    if ($demoExit -ne 0 -or $analysisExit -ne 0 -or -not $attempt.source_unchanged -or -not $attempt.scripts_unchanged) {
        throw 'ETW clock diagnostic did not pass; preserve the original failure and reviewed evidence summary.'
    }
} finally {
    $env:CONTAIN_FIXTURE_CLOCK = $previousFixtureClock
    $env:CONTAIN_ETW_RAW_CLOCK_PATH = $previousRawClock
    Pop-Location
}
