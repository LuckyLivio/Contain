$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/demo-checks.ps1"
class DemoModelRegistryKey {
    [hashtable]$Values=@{Installed='baseline';NoiseOnly='observed noise'}
    [hashtable]$Kinds=@{Installed='String';NoiseOnly='String'}
    [string[]] GetValueNames() { return [string[]]$this.Values.Keys }
    [object] GetValue([string]$Name,[object]$Default,[Microsoft.Win32.RegistryValueOptions]$Options) { return $this.Values[$Name] }
    [string] GetValueKind([string]$Name) { return $this.Kinds[$Name] }
    [void] Close() { }
}
$passed = 0
$failed = 0
function Regression([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:passed++; Write-Output "PASS: $Name" }
    catch { $script:failed++; Write-Output "FAIL: $Name — $($_.Exception.Message)" }
}
Regression 'diagnostic failure does not skip independent product checks' {
    $checks = [Collections.Generic.List[object]]::new()
    $observed = @{ independent=$false }
    Invoke-DemoCheck -Checks $checks -Name clock -Domain diagnostic -Action { throw 'missing clock input' }
    Invoke-DemoCheck -Checks $checks -Name reopen -Action { $observed.independent=$true }
    if (-not $observed.independent -or $checks.Count -ne 2 -or $checks[0].status -ne 'failed' -or $checks[1].status -ne 'passed') {
        throw 'Independent product check did not execute after diagnostic failure'
    }
}
Regression 'action output stays in a redirected private log without changing check results' {
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-redirection-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $childScript=Join-Path $temporary 'redirect.ps1'
    $privateLog=Join-Path $temporary 'private.log'
    $statePath=Join-Path $temporary 'checks.json'
    @'
param([string]$Helper,[string]$PrivateLog,[string]$StatePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. $Helper
$checks=[Collections.Generic.List[object]]::new()
& {
    Invoke-DemoCheck -Checks $checks -Name first -Action {
        Write-Output 'private-success-marker'
        Write-Host 'private-host-marker'
        Write-Warning 'private-warning-marker'
    }
    Invoke-DemoCheck -Checks $checks -Name failed -Action {
        Write-Output 'private-before-failure-marker'
        throw 'expected-action-failure'
    }
    Invoke-DemoCheck -Checks $checks -Name independent -Action { Write-Output 'private-independent-marker' }
} *> $PrivateLog
$checks.ToArray() | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $StatePath
Write-Output 'public-complete'
'@ | Set-Content -LiteralPath $childScript -Encoding utf8
    try {
        # A child console reproduces CI: Out-Host bypasses the inner *> redirect
        # and reaches the parent's captured stdout instead of the private log.
        $pwsh=Join-Path $PSHOME $(if($IsWindows){'pwsh.exe'}else{'pwsh'})
        $consoleOutput=(& $pwsh -NoLogo -NoProfile -File $childScript -Helper (Join-Path $PSScriptRoot 'demo-checks.ps1') -PrivateLog $privateLog -StatePath $statePath 2>&1) -join "`n"
        if($LASTEXITCODE -ne 0){throw 'Redirected check subprocess failed'}
        $privateOutput=Get-Content -LiteralPath $privateLog -Raw
        foreach($marker in @('private-success-marker','private-host-marker','private-warning-marker','private-before-failure-marker','private-independent-marker')) {
            if($consoleOutput.Contains($marker) -or -not $privateOutput.Contains($marker)){throw 'Action output escaped the private log'}
        }
        if($consoleOutput.Trim() -ne 'public-complete'){throw 'Unexpected output escaped the private log'}
        $rows=@(Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json)
        if($rows.Count -ne 3 -or $rows[0].status -ne 'passed' -or $rows[1].status -ne 'failed' -or
            $rows[1].reason -ne 'expected-action-failure' -or $rows[2].status -ne 'passed') {
            throw 'Redirection changed failure recording or independent check execution'
        }
    } finally {
        foreach($path in @($childScript,$privateLog,$statePath)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}
        Remove-Item -LiteralPath $temporary
    }
}
function Complete-ClockResult {
    @{ result_contract=@{ revision='contain-clock-result-1'; execution=@{status='completed'};
        evidence=@{status='complete';scope='supplied_event_view_clock_inputs';raw_final_join='not_evaluated'};
        formal=@{status='requirements_unmet'} } } | ConvertTo-Json -Depth 6 | ConvertFrom-Json
}
Regression 'diagnostic completion and formal requirements unmet remain distinct' {
    $checks = [Collections.Generic.List[object]]::new()
    Invoke-DemoCheck -Checks $checks -Name clock -Domain diagnostic -Action { }
    Invoke-DemoCheck -Checks $checks -Name reopen -Action { }
    $result=Get-DemoCheckSummary -Checks $checks -ClockRequested $true -ClockResult (Complete-ClockResult)
    if (-not $result.bounded_pass -or $result.diagnostic_execution.status -ne 'completed' -or $result.formal.status -ne 'requirements_unmet' -or $result.evidence.raw_final_join -ne 'not_evaluated') {
        throw 'Diagnostic, bounded product and formal scope were conflated'
    }
}
Regression 'product failure fails bounded result even with complete clocks' {
    $checks = [Collections.Generic.List[object]]::new()
    Invoke-DemoCheck -Checks $checks -Name clock -Domain diagnostic -Action { }
    Invoke-DemoCheck -Checks $checks -Name unrelated_actor -Action { throw 'wrong attribution' }
    $result=Get-DemoCheckSummary -Checks $checks -ClockRequested $true -ClockResult (Complete-ClockResult)
    if ($result.bounded_pass -or $result.product.status -ne 'failed' -or $result.product.required_failed -ne 1) { throw 'Product failure was ignored' }
}
Regression 'missing evidence and dependent not-run never become passing checks' {
    $checks = [Collections.Generic.List[object]]::new()
    Invoke-DemoCheck -Checks $checks -Name reopen -Ready $false -UnavailableReason 'capture_unavailable' -Action { throw 'must not run' }
    Invoke-DemoCheck -Checks $checks -Name independent -Action { }
    $result=Get-DemoCheckSummary -Checks $checks -ClockRequested $true
    if ($result.bounded_pass -or $result.product.status -ne 'incomplete' -or $result.product.required_not_run -ne 1 -or $result.diagnostic_execution.status -ne 'failed') { throw 'Missing work counted as success' }
    $empty=Get-DemoCheckSummary -Checks ([Collections.Generic.List[object]]::new())
    if ($empty.bounded_pass -or $empty.product.status -ne 'incomplete') { throw 'Empty check set passed' }
}
Regression 'default product path has no mandatory clock dependency' {
    $checks = [Collections.Generic.List[object]]::new()
    Invoke-DemoCheck -Checks $checks -Name clock -Domain diagnostic -Required $false -Ready $false -UnavailableReason 'not_requested' -Action { throw 'must not run' }
    Invoke-DemoCheck -Checks $checks -Name reopen -Action { }
    $result=Get-DemoCheckSummary -Checks $checks
    if (-not $result.bounded_pass -or $result.diagnostic_execution.status -ne 'not_requested' -or $result.formal.status -ne 'requirements_unmet') { throw 'Default product path required optional diagnostic' }
}
Regression 'predeclared unreachable work stays not-run and a check cannot retry to green' {
    $checks=[Collections.Generic.List[object]]::new()
    $checks.Add([ordered]@{name='reopen';domain='product';required=$true;status='not_run';reason='not_reached'})
    $before=Get-DemoCheckSummary -Checks $checks
    if ($before.product.required_not_run -ne 1 -or $before.bounded_pass) { throw 'Unreached planned check was counted as passing' }
    Invoke-DemoCheck -Checks $checks -Name reopen -Action { throw 'first failure' }
    $rejected=$false
    try { Invoke-DemoCheck -Checks $checks -Name reopen -Action { } } catch { $rejected=$true }
    if (-not $rejected -or $checks.Count -ne 1 -or $checks[0].status -ne 'failed') { throw 'Retry overwrote original failed check' }
    $missing=Get-DemoCheckSummary -Checks ([Collections.Generic.List[object]]::new()) -PlannedCheckNames @('reopen')
    if ($missing.bounded_pass -or $missing.missing_planned_checks[0] -ne 'reopen') { throw 'Missing planned row was silently discarded' }
}
Regression 'malformed diagnostic result remains unavailable' {
    $checks=[Collections.Generic.List[object]]::new()
    Invoke-DemoCheck -Checks $checks -Name product -Action { }
    Invoke-DemoCheck -Checks $checks -Name clock -Domain diagnostic -Action { }
    $malformed='{"result_contract":{"revision":"contain-clock-result-1"}}' | ConvertFrom-Json
    $result=Get-DemoCheckSummary -Checks $checks -ClockRequested $true -ClockResult $malformed
    if ($result.bounded_pass -or $result.diagnostic_execution.status -ne 'failed' -or $result.evidence.status -ne 'not_evaluated') { throw 'Malformed diagnostic counted as valid' }
}
Regression 'actual demo blocks continue readback and dry-run after diagnostic error' {
    $errors=$null; $tokens=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'demo.ps1'),[ref]$tokens,[ref]$errors)
    if ($errors.Count -gt 0) { throw 'Demo script has parse errors' }
    $commands=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-DemoCheck'},$true))
    $selected=@{}
    foreach($name in @('clock_diagnostic','view_consistency','dry_run_unchanged')) {
        $matches=@($commands | Where-Object { $_.Extent.Text -match ('-Name '+$name+'\s') })
        if($matches.Count -ne 1){throw "Missing/duplicate production check: $name"}
        $selected[$name]=$matches[0]
    }
    $first=$selected.clock_diagnostic.Extent.StartOffset; $last=$selected.dry_run_unchanged.Extent.EndOffset
    foreach($statement in @($ast.FindAll({param($node) $node -is [Management.Automation.Language.ThrowStatementAst]},$true))) {
        if($statement.Extent.StartOffset -ge $first -and $statement.Extent.StartOffset -lt $last) {
            $parent=$statement.Parent; $insideCheck=$false
            while($null -ne $parent) {
                if($parent -is [Management.Automation.Language.CommandAst] -and $parent.GetCommandName() -eq 'Invoke-DemoCheck'){$insideCheck=$true;break}
                $parent=$parent.Parent
            }
            if(-not $insideCheck){throw 'A fail-fast throw can bypass independent product checks'}
        }
    }
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-flow-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $priorClock=$env:CONTAIN_ETW_RAW_CLOCK_PATH
    try {
        $checks=[Collections.Generic.List[object]]::new()
        $clockRequested=$true; $manifestReady=$true; $scoreReady=$true; $runIdentity=$null
        $OutputDirectory=$temporary; $truthRoot=$temporary; $root=$temporary; $db=Join-Path $temporary 'model.db'
        $registryPath='synthetic-registry'; $contain='Invoke-DemoTestContain'; $observation=@{clock=$null}
        $env:CONTAIN_ETW_RAW_CLOCK_PATH=Join-Path $temporary 'raw.json'
        $invocations=@{readback=$false;dry_run=$false}
        function python {
            # No capture, SQLite or fake success claim: these mocks exercise the
            # real demo's continuation and aggregation, not its CLI semantics.
            if($args[0] -like '*v04-clock-protocol.py'){$global:LASTEXITCODE=2}
            else{$invocations.readback=$true;$global:LASTEXITCODE=0}
        }
        function Invoke-DemoTestContain {$invocations.dry_run=$true;$global:LASTEXITCODE=0}
        $modelRegistry=[DemoModelRegistryKey]::new()
        function Get-Item {
            param([string]$LiteralPath)
            if($LiteralPath -eq $registryPath){return $modelRegistry}
            Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath
        }
        foreach($name in @('clock_diagnostic','view_consistency','dry_run_unchanged')) { Invoke-Expression $selected[$name].Extent.Text }
        $result=Get-DemoCheckSummary -Checks $checks -ClockRequested $true
        if(-not $invocations.readback -or -not $invocations.dry_run -or $checks[0].status -ne 'failed' -or
            $result.product.required_passed -ne 2 -or -not $result.product.passed -or $result.bounded_pass) {
            throw 'Production check blocks skipped independent work or swallowed the diagnostic failure'
        }
    } finally {
        $env:CONTAIN_ETW_RAW_CLOCK_PATH=$priorClock
        Remove-Item -LiteralPath $temporary
    }
}
Regression 'dry-run state covers every fixture registry value/type and empty directories' {
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-state-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $file=Join-Path $temporary 'settings.txt'; $emptyDirectory=Join-Path $temporary 'empty'
    $registry=[DemoModelRegistryKey]::new()
    try {
        [IO.File]::WriteAllText($file,'same files')
        $before=Get-DemoObservedState -Root $temporary -RegistryKey $registry
        if($before -cne (Get-DemoObservedState -Root $temporary -RegistryKey $registry)){throw 'Unchanged state rejected'}
        $registry.Values.NoiseOnly='changed without touching Installed'
        if($before -ceq (Get-DemoObservedState -Root $temporary -RegistryKey $registry)){throw 'Non-Installed registry mutation escaped check'}
        $registry.Values.NoiseOnly='observed noise';$registry.Kinds.NoiseOnly='ExpandString'
        if($before -ceq (Get-DemoObservedState -Root $temporary -RegistryKey $registry)){throw 'Registry type-only change escaped check'}
        $registry.Kinds.NoiseOnly='String';$registry.Values.NewValue=7;$registry.Kinds.NewValue='DWord'
        if($before -ceq (Get-DemoObservedState -Root $temporary -RegistryKey $registry)){throw 'New registry value escaped check'}
        $registry.Values.Remove('NewValue');$registry.Kinds.Remove('NewValue')
        New-Item -ItemType Directory -Path $emptyDirectory | Out-Null
        if($before -ceq (Get-DemoObservedState -Root $temporary -RegistryKey $registry)){throw 'Empty directory creation escaped check'}
        Remove-Item -LiteralPath $emptyDirectory
        [IO.File]::WriteAllText($file,'different files')
        if($before -ceq (Get-DemoObservedState -Root $temporary -RegistryKey $registry)){throw 'File content mutation escaped check'}
    } finally {
        if(Test-Path -LiteralPath $emptyDirectory){Remove-Item -LiteralPath $emptyDirectory}
        Remove-Item -LiteralPath $file
        Remove-Item -LiteralPath $temporary
    }
}
Regression 'actual dry-run check rejects changed observed registry data' {
    $errors=$null;$tokens=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'demo.ps1'),[ref]$tokens,[ref]$errors)
    $command=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Invoke-DemoCheck' -and $node.Extent.Text -match '-Name dry_run_unchanged\s'},$true))[0]
    $root=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-mutation-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    try {
        $checks=[Collections.Generic.List[object]]::new();$manifestReady=$true
        $db=Join-Path $root 'model.db';$registryPath='synthetic-registry';$modelRegistry=[DemoModelRegistryKey]::new()
        $contain='Invoke-DemoMutatingTestContain'
        function Invoke-DemoMutatingTestContain { $modelRegistry.Values.NoiseOnly='changed';$global:LASTEXITCODE=0 }
        function Get-Item {
            param([string]$LiteralPath)
            if($LiteralPath -eq $registryPath){return $modelRegistry}
            Microsoft.PowerShell.Management\Get-Item -LiteralPath $LiteralPath
        }
        Invoke-Expression $command.Extent.Text
        $result=Get-DemoCheckSummary -Checks $checks
        if($result.bounded_pass -or $result.product.required_failed -ne 1 -or $checks[0].status -ne 'failed'){throw 'Actual dry-run assertion ignored scoped registry mutation'}
    } finally { Remove-Item -LiteralPath $root }
}
Write-Output "Demo check regressions: $passed passed, $failed failed, 0 ignored"
if ($failed -ne 0) { throw "$failed demo check regressions failed" }
