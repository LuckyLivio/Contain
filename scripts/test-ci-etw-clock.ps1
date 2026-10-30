$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null; $parseErrors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'ci-etw-clock.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Clock CI entrypoint has syntax errors' }
foreach ($name in @('Get-ClockAttemptPlan', 'Set-ClockAttemptEnvironment', 'Restore-ClockAttemptEnvironment', 'Invoke-ClockFollowups')) {
    $function = $syntax.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true) | Select-Object -First 1
    if (-not $function) { throw "Required real entrypoint function missing: $name" }
    Invoke-Expression $function.Extent.Text
}
$checks = 0
function Assert-Clock([bool]$Value, [string]$Reason) { if (-not $Value) { throw $Reason }; $script:checks++ }
function Assert-Rejected([scriptblock]$Run) { $rejected = $false; try { & $Run | Out-Null } catch { $rejected = $true }; Assert-Clock $rejected 'An invalid capture plan was accepted' }
$default = Get-ClockAttemptPlan candidate 1 default 'frozen-package'
$dual = Get-ClockAttemptPlan candidate 2 dual-clock 'frozen-package'
Assert-Clock ($default.planned_attempts -eq 2 -and -not $default.clock_requested -and $default.report_requested) 'Default plan must have no clocks and must generate a report'
Assert-Clock ($dual.planned_attempts -eq 2 -and $dual.clock_requested -and $dual.report_requested) 'Dual plan must require clocks and report'
Assert-Rejected { Get-ClockAttemptPlan candidate 1 dual-clock 'frozen-package' }
Assert-Rejected { Get-ClockAttemptPlan candidate 2 default 'frozen-package' }
Assert-Rejected { Get-ClockAttemptPlan candidate 1 default '' }
Assert-Rejected { Get-ClockAttemptPlan diagnostic 2 dual-clock '' }
Assert-Rejected { Get-ClockAttemptPlan verification 1 default '' }
Assert-Clock ((Get-ClockAttemptPlan diagnostic 1 dual-clock '').planned_attempts -eq 1) 'Existing diagnostic contract changed'
Assert-Clock ((Get-ClockAttemptPlan verification 2 dual-clock '').clock_requested) 'Existing verification contract changed'
$outer = @{}; foreach ($entry in @(Get-ChildItem Env:CONTAIN_*)) { $outer[$entry.Name] = $entry.Value }
try {
    $env:CONTAIN_FIXTURE_CLOCK = 'inherited'; $env:CONTAIN_ETW_RAW_CLOCK_PATH = 'old-private-path'
    $env:CONTAIN_PROFILE_PATH = 'profile'; $env:CONTAIN_WPR_TEST_OPTION = 'must-not-leak'
    $saved = Set-ClockAttemptEnvironment $false 'unused-path'
    Assert-Clock (@(Get-ChildItem Env:CONTAIN_*).Count -eq 0) 'Default capture inherited optional clock/experiment settings'
    Restore-ClockAttemptEnvironment $saved
    Assert-Clock ($env:CONTAIN_FIXTURE_CLOCK -eq 'inherited' -and $env:CONTAIN_PROFILE_PATH -eq 'profile' -and $env:CONTAIN_WPR_TEST_OPTION -eq 'must-not-leak') 'Caller environment was not restored'
    $saved = Set-ClockAttemptEnvironment $true 'new-private-path'
    Assert-Clock ($env:CONTAIN_FIXTURE_CLOCK -eq '1' -and $env:CONTAIN_ETW_RAW_CLOCK_PATH -eq 'new-private-path' -and @(Get-ChildItem Env:CONTAIN_*).Count -eq 2) 'Dual capture needs exactly its two clock opt-ins'
    Restore-ClockAttemptEnvironment $saved
} finally { Restore-ClockAttemptEnvironment $outer }
$script:analysisCalls = 0; $script:reportCalls = 0
$result = Invoke-ClockFollowups $true $true { $script:analysisCalls++; throw 'simulated diagnostic failure' } { $script:reportCalls++; return 0 }
Assert-Clock ($analysisCalls -eq 1 -and $reportCalls -eq 1) 'A diagnostic failure skipped the independent CLI report'
Assert-Clock ($result.analysis.status -eq 'failed' -and $result.report.status -eq 'passed' -and -not $result.passed) 'Diagnostic failure was swallowed'
$result = Invoke-ClockFollowups $true $true { return 0 } { return 2 }
Assert-Clock ($result.analysis.status -eq 'passed' -and $result.report.status -eq 'failed' -and -not $result.passed) 'Read-only report failure was swallowed'
$result = Invoke-ClockFollowups $false $true { throw 'must not run' } { return 0 }
Assert-Clock ($result.analysis.status -eq 'not_requested' -and $null -eq $result.analysis.exit_code -and $result.report.status -eq 'passed' -and $result.passed) 'Default missing clock was counted as a pass or executed'
$result = Invoke-ClockFollowups $true $false { return 0 } { throw 'must not run' }
Assert-Clock ($result.analysis.status -eq 'passed' -and $result.report.status -eq 'not_requested' -and $result.passed) 'Legacy analysis-only behavior changed'
$result = Invoke-ClockFollowups $true $true { return 0 } { return $null }
Assert-Clock ($result.report.status -eq 'failed' -and -not $result.passed) 'Missing report outcome was counted as success'
Assert-Clock ($result.report_diagnostic.status -eq 'not_run' -and $null -eq $result.report_diagnostic.exit_code) 'Unavailable offline diagnosis was counted as executed'
$script:diagnosticCalls = 0
$result = Invoke-ClockFollowups $false $true { throw 'must not run' } { return 2 } { $script:diagnosticCalls++; return 0 }
Assert-Clock ($diagnosticCalls -eq 1 -and $result.report_diagnostic.status -eq 'passed') 'Failed export did not receive exactly one offline diagnosis'
Assert-Clock ($result.report.status -eq 'failed' -and $result.report.exit_code -eq 2 -and -not $result.passed) 'Offline diagnosis replaced the original export failure'
$result = Invoke-ClockFollowups $false $true { throw 'must not run' } { return 0 } { $script:diagnosticCalls++; throw 'must not run' }
Assert-Clock ($diagnosticCalls -eq 1 -and $result.report_diagnostic.status -eq 'not_requested' -and $result.passed) 'Successful export unnecessarily ran a diagnostic'
$result = Invoke-ClockFollowups $false $true { throw 'must not run' } { throw 'private-report-error' } { $script:diagnosticCalls++; throw 'private-diagnostic-error' }
Assert-Clock ($diagnosticCalls -eq 2 -and $result.report.status -eq 'failed' -and $result.report_diagnostic.status -eq 'failed' -and -not $result.passed) 'Report/diagnostic exceptions were not preserved independently'
Assert-Clock (($result | ConvertTo-Json -Depth 5) -notmatch 'private-report-error|private-diagnostic-error') 'Private exception messages escaped into the public stage result'
$result = Invoke-ClockFollowups $false $true { throw 'must not run' } { return 2 } { $script:diagnosticCalls++; return $null }
Assert-Clock ($diagnosticCalls -eq 3 -and $result.report_diagnostic.status -eq 'failed' -and -not $result.passed) 'Missing diagnostic outcome counted as success'
$result = Invoke-ClockFollowups $true $false { return 0 } { throw 'must not run' } { $script:diagnosticCalls++; throw 'must not run' }
Assert-Clock ($diagnosticCalls -eq 3 -and $result.report_diagnostic.status -eq 'not_requested' -and $result.passed) 'Historical analysis-only plans started report diagnostics'
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-clock-boundary-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$pwsh=Join-Path $PSHOME $(if($IsWindows){'pwsh.exe'}else{'pwsh'})
try {
    # This is the real entrypoint, stopped by platform/setup validation before
    # any native capture. PowerShell must not render its private path or stack.
    $console=(& $pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot 'ci-etw-clock.ps1') `
        -Phase candidate -Configuration default -AttemptIndex 1 `
        -TrialDirectory (Join-Path $temporary 'missing-private-package') `
        -OutputDirectory (Join-Path $temporary 'private') -PublicOutputDirectory (Join-Path $temporary 'public') 2>&1) -join "`n"
    Assert-Clock ($LASTEXITCODE -eq 1) 'Entrypoint setup failure lost its nonzero exit code'
    Assert-Clock ($console.Trim() -ceq 'ETW entrypoint failed; error_code=clock_entrypoint_failed; private_error_saved=false.') 'Entrypoint setup failure leaked private context'
    Assert-Clock (-not (Test-Path -LiteralPath (Join-Path $temporary 'private'))) 'Invalid setup created an unvalidated private destination'
    $trap=@($syntax.FindAll({param($node) $node -is [Management.Automation.Language.TrapStatementAst]},$true))
    Assert-Clock ($trap.Count -eq 1) 'Entrypoint private exception boundary is missing or ambiguous'
    $childScript=Join-Path $temporary 'failure.ps1'
    @'
param([string]$PrivateRoot,[switch]$OwnDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$privateFailureRoot=$null
if($OwnDirectory){
    New-Item -ItemType Directory -Path $PrivateRoot | Out-Null
    $privateFailureRoot=$PrivateRoot
}
'@ + "`n" + $trap[0].Extent.Text + "`n" + 'throw (''private-machine-marker: '' + $PrivateRoot)' |
        Set-Content -LiteralPath $childScript -Encoding utf8
    foreach($ownDirectory in @($true,$false)) {
        $private=Join-Path $temporary $(if($ownDirectory){'owned-private'}else{'existing-private'})
        if(-not $ownDirectory){New-Item -ItemType Directory -Path $private | Out-Null}
        $childArguments=@('-NoLogo','-NoProfile','-File',$childScript,'-PrivateRoot',$private)
        if($ownDirectory){$childArguments+='-OwnDirectory'}
        $console=(& $pwsh @childArguments 2>&1) -join "`n"
        Assert-Clock ($LASTEXITCODE -eq 1) 'Entrypoint exception boundary swallowed failure'
        $saved=$ownDirectory.ToString().ToLowerInvariant()
        Assert-Clock ($console.Trim() -ceq "ETW entrypoint failed; error_code=clock_entrypoint_failed; private_error_saved=$saved.") 'Entrypoint exception or log failure escaped to the console'
        $privateError=Join-Path $private 'entrypoint.error.txt'
        if($ownDirectory) {
            $detail=Get-Content -LiteralPath $privateError -Raw
            Assert-Clock ($detail.Contains('private-machine-marker') -and $detail.Contains($private)) 'Original exception was not retained in the owned private directory'
            Remove-Item -LiteralPath $privateError
        } else {
            Assert-Clock (-not (Test-Path -LiteralPath $privateError)) 'Entrypoint wrote into a caller-owned destination'
        }
        Remove-Item -LiteralPath $private
    }
} finally {
    $childScript=Join-Path $temporary 'failure.ps1'
    if(Test-Path -LiteralPath $childScript){Remove-Item -LiteralPath $childScript}
    Remove-Item -LiteralPath $temporary
}
Write-Output "CI clock wrapper: $checks assertions passed; no native collection executed."
