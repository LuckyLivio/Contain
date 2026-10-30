$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/demo-checks.ps1"
if (Test-Path -LiteralPath "$PSScriptRoot/demo-save-capture.ps1") { . "$PSScriptRoot/demo-save-capture.ps1" }
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
$PSNativeCommandUseErrorActionPreference=$false
. $Helper
$checks=[Collections.Generic.List[object]]::new()
$pwsh=Join-Path $PSHOME $(if($IsWindows){'pwsh.exe'}else{'pwsh'})
& {
    Invoke-DemoCheck -Checks $checks -Name first -Action {
        Write-Output 'private-success-marker'
        Write-Host 'private-host-marker'
        Write-Warning 'private-warning-marker'
        & $pwsh -NoLogo -NoProfile -Command "[Console]::Out.WriteLine('private-native-success-stdout'); [Console]::Error.WriteLine('private-native-success-stderr'); exit 0"
        if($LASTEXITCODE -ne 0){throw 'unexpected-native-success-exit'}
    }
    Invoke-DemoCheck -Checks $checks -Name failed -Action {
        Write-Output 'private-before-failure-marker'
        Write-Host 'private-failure-host-marker'
        Write-Warning 'private-failure-warning-marker'
        & $pwsh -NoLogo -NoProfile -Command "[Console]::Out.WriteLine('private-native-failure-stdout'); [Console]::Error.WriteLine('private-native-failure-stderr'); exit 7"
        if($LASTEXITCODE -ne 7){throw 'unexpected-native-failure-exit'}
        throw 'expected-native-exit-7'
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
        foreach($marker in @('private-success-marker','private-host-marker','private-warning-marker',
            'private-native-success-stdout','private-native-success-stderr','private-before-failure-marker',
            'private-failure-host-marker','private-failure-warning-marker','private-native-failure-stdout',
            'private-native-failure-stderr','private-independent-marker')) {
            if($consoleOutput.Contains($marker) -or -not $privateOutput.Contains($marker)){throw 'Action output escaped the private log'}
        }
        if($consoleOutput.Trim() -ne 'public-complete'){throw 'Unexpected output escaped the private log'}
        $rows=@(Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json)
        if($rows.Count -ne 3 -or $rows[0].status -ne 'passed' -or $rows[1].status -ne 'failed' -or
            $rows[1].reason -ne 'expected-native-exit-7' -or $rows[2].status -ne 'passed') {
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
# Explicitly synthetic input for the real demo save block. This is a text/save
# regression; the Windows preflight separately exercises the real CLI + database.
$syntheticCaptureJson=@'
{
  "schema_version": 3, "kind": "inspect", "synthetic": true,
  "data": {
    "schema_version": 3, "id": "synthetic-save", "name": "SyntheticSave",
    "installer": "synthetic-fixture.exe",
    "started_at": "2026-07-16T09:10:20.120+00:00",
    "finished_at": "2026-07-16T09:10:20.123400+00:00", "exit_code": null,
    "watch_roots": [], "registry_key": null, "processes": [], "files": [],
    "registry": [], "warnings": [], "events": [], "inventory": [],
    "backend": { "etw_file": "not_requested", "etw_registry": "not_requested",
      "etw_process": "not_requested", "dropped_events": 0, "etw_events_lost": null,
      "decode_errors": 0, "warnings": [], "registry_path_gaps": 0,
      "etw_buffers_lost": null, "events_received": 0 },
    "stats": { "events_received": 0, "events_retained": 0, "events_normalized": 0,
      "events_dropped": 0, "known_actor_events": 0, "unknown_actor_events": 0,
      "known_resource_events": 0, "unknown_resource_events": 0,
      "high_confidence_events": 0, "capture_elapsed_ms": 9007199254740993,
      "drain_timed_out": false },
    "quality": { "level": "Degraded", "reasons": ["synthetic save regression"] },
    "operations": [], "edges": [],
    "synthetic_nested": { "exact_integer": 18446744073709551615,
      "array": [null, 7, {"timestamp": "2026-07-16T09:10:20.123450+00:00",
        "escaped": "literal\\r\\n", "unicode": "合成"}] }
  }
}
'@
Regression 'actual demo inspect save preserves validated CLI field representations' {
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-save-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    try {
        $errors=$null;$tokens=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'demo.ps1'),[ref]$tokens,[ref]$errors)
        $commands=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq 'Invoke-DemoCheck' -and $node.Extent.Text -match '-Name inspect_json\s'},$true))
        if($errors.Count -ne 0 -or $commands.Count -ne 1){throw 'Production inspect check is missing or malformed'}
        $checks=[Collections.Generic.List[object]]::new();$OutputDirectory=$temporary
        $db='synthetic.db';$contain='Invoke-SyntheticInspectForSave';$observation=@{document=$null;manifest=$null}
        function Invoke-SyntheticInspectForSave { $syntheticCaptureJson; $global:LASTEXITCODE=0 }
        function Assert-JsonContract([string]$text) {
            if(-not (Test-Json -Json $text -SchemaFile (Join-Path $PSScriptRoot '../docs/schema/cli-v3.schema.json'))){throw 'Synthetic contract failed'}
        }
        Invoke-Expression $commands[0].Extent.Text
        if($checks[0].status -ne 'passed'){throw ('Production inspect check did not pass: '+$checks[0].reason)}
        $saved=[IO.File]::ReadAllText((Join-Path $temporary 'capture.json'))
        if($saved -cne ($syntheticCaptureJson+"`n")){throw 'Production save changed validated JSON text or field representations'}
        if($null -eq $observation.manifest){throw 'Parsed object is unavailable to product checks'}
    } finally {
        if(Test-Path -LiteralPath (Join-Path $temporary 'capture.json')){Remove-Item -LiteralPath (Join-Path $temporary 'capture.json')}
        Remove-Item -LiteralPath $temporary
    }
}
Regression 'shared capture save uses UTF8 without BOM and only documented physical newlines' {
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-save-bytes-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $path=Join-Path $temporary 'capture.json'
    try {
        $crlf=$syntheticCaptureJson.Replace("`n","`r`n")+"`r`n`r`n "
        $result=@(Save-DemoCaptureJson -Json $crlf -Path $path -SchemaPath (Join-Path $PSScriptRoot '../docs/schema/cli-v3.schema.json'))
        if($result.Count -ne 0){throw 'Save emitted data into stdout'}
        $expected=$syntheticCaptureJson+"`n`n `n"
        $bytes=[IO.File]::ReadAllBytes($path)
        $wanted=[Text.UTF8Encoding]::new($false).GetBytes($expected)
        if([Convert]::ToBase64String($bytes) -cne [Convert]::ToBase64String($wanted)){throw 'Encoding, physical newline or field representation changed'}
    } finally {
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
        Remove-Item -LiteralPath $temporary
    }
}
Regression 'shared capture save rejects invalid contract before creating an output' {
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-save-invalid-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $path=Join-Path $temporary 'capture.json'
    try {
        if(-not (Get-Command Save-DemoCaptureJson -ErrorAction SilentlyContinue)){throw 'Shared save function unavailable'}
        $rejected=$false
        try { Save-DemoCaptureJson -Json '{"schema_version":3,"kind":"inspect","data":{}}' -Path $path -SchemaPath (Join-Path $PSScriptRoot '../docs/schema/cli-v3.schema.json') } catch { $rejected=$true }
        if(-not $rejected -or (Test-Path -LiteralPath $path)){throw 'Invalid input created saved evidence'}
    } finally {
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}
        Remove-Item -LiteralPath $temporary
    }
}
Regression 'shared save CLI entrypoint preserves the same synthetic stdout text' {
    $temporary=Join-Path ([IO.Path]::GetTempPath()) ('contain-demo-save-entry-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    $inputPath=Join-Path $temporary 'inspect.stdout.txt';$outputPath=Join-Path $temporary 'capture.json'
    try {
        [IO.File]::WriteAllText($inputPath,$syntheticCaptureJson,[Text.UTF8Encoding]::new($false))
        $pwsh=Join-Path $PSHOME $(if($IsWindows){'pwsh.exe'}else{'pwsh'})
        $output=@(& $pwsh -NoProfile -File (Join-Path $PSScriptRoot 'demo-save-capture.ps1') -InputPath $inputPath -OutputPath $outputPath -SchemaPath (Join-Path $PSScriptRoot '../docs/schema/cli-v3.schema.json') 2>&1)
        if($LASTEXITCODE -ne 0 -or $output.Count -ne 0){throw 'Shared save entrypoint failed or leaked output'}
        if([IO.File]::ReadAllText($outputPath) -cne ($syntheticCaptureJson+"`n")){throw 'Shared entrypoint changed input field representation'}
    } finally {
        foreach($path in @($inputPath,$outputPath)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}
        Remove-Item -LiteralPath $temporary
    }
}
Write-Output "Demo check regressions: $passed passed, $failed failed, 0 ignored"
if ($failed -ne 0) { throw "$failed demo check regressions failed" }
