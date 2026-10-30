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
Write-Output "CI clock wrapper: $checks assertions passed; no native collection executed."
