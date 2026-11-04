# Shared by the existing demo and its control-flow regressions.
function Invoke-DemoCheck {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[object]]$Checks,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action,
        [ValidateSet('product', 'diagnostic')][string]$Domain = 'product',
        [bool]$Required = $true,
        [bool]$Ready = $true,
        [string]$UnavailableReason = 'required_input_unavailable'
    )
    $existing = @($Checks | Where-Object { $_.name -eq $Name -and $_.domain -eq $Domain })
    if ($existing.Count -gt 1 -or ($existing.Count -eq 1 -and $existing[0].reason -ne 'not_reached')) {
        throw "Refusing to overwrite or retry a demo check: $Name"
    }
    $row = if ($existing.Count -eq 1) { $existing[0] } else {
        $new = [ordered]@{ name=$Name; domain=$Domain; required=$Required; status='not_run'; reason='not_reached' }
        $Checks.Add($new)
        $new
    }
    $row.required = $Required
    if (-not $Ready) {
        $row.status = 'not_run'; $row.reason = $UnavailableReason
        return
    }
    try {
        # Preserve the caller's stream redirection; Out-Host bypasses private logs.
        & $Action
        $row.status = 'passed'; $row.reason = $null
    } catch {
        $row.status = 'failed'; $row.reason = $_.Exception.Message
        Write-Warning "Demo check failed: $Name. Other independent checks will continue."
    }
}

function Get-DemoCheckSummary {
    param([Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[object]]$Checks,
          [bool]$ClockRequested = $false, $ClockResult = $null,
          [string[]]$PlannedCheckNames = @())
    $products = @($Checks | Where-Object domain -eq 'product')
    $required = @($products | Where-Object required)
    $failed = @($required | Where-Object status -eq 'failed').Count
    $notRun = @($required | Where-Object status -eq 'not_run').Count
    $passed = @($required | Where-Object status -eq 'passed').Count
    $missingPlanned = @($PlannedCheckNames | Where-Object { $_ -notin @($Checks | ForEach-Object name) })
    $productStatus = if ($failed -gt 0) { 'failed' } elseif ($notRun -gt 0 -or $required.Count -eq 0 -or $missingPlanned.Count -gt 0) { 'incomplete' } else { 'passed' }
    $execution = [ordered]@{ status=($(if($ClockRequested){'failed'}else{'not_requested'})) }
    $evidence = [ordered]@{ status='not_evaluated'; scope='supplied_event_view_clock_inputs'; raw_final_join='not_evaluated' }
    $formal = [ordered]@{ status='requirements_unmet'; scope='general_provider_and_stabilization_acceptance' }
    if ($ClockResult -and $ClockResult.PSObject.Properties['result_contract']) {
        $contract = $ClockResult.result_contract
        if ($contract -and $contract.PSObject.Properties['revision'] -and
            $contract.revision -eq 'contain-clock-result-1' -and
            $contract.PSObject.Properties['execution'] -and $contract.PSObject.Properties['evidence'] -and
            $contract.PSObject.Properties['formal'] -and
            $contract.execution.PSObject.Properties['status'] -and $contract.evidence.PSObject.Properties['status'] -and
            $contract.formal.PSObject.Properties['status']) {
            $execution = $contract.execution
            $evidence = $contract.evidence
            $formal = $contract.formal
        }
    }
    $diagnosticChecks = @($Checks | Where-Object { $_.domain -eq 'diagnostic' -and $_.required })
    $diagnosticPassed = (-not $ClockRequested) -or ($execution.status -eq 'completed' -and $evidence.status -eq 'complete' -and
        $diagnosticChecks.Count -gt 0 -and @($diagnosticChecks | Where-Object status -ne 'passed').Count -eq 0)
    [ordered]@{
        schema_version=1; protocol_revision='contain-demo-result-1'; clock_requested=$ClockRequested
        diagnostic_execution=$execution; evidence=$evidence; formal=$formal
        product=[ordered]@{
            status=$productStatus; passed=($productStatus -eq 'passed')
            required_passed=$passed; required_failed=$failed; required_not_run=$notRun
            passed_count=@($products | Where-Object status -eq 'passed').Count
            failed_count=@($products | Where-Object status -eq 'failed').Count
            not_run_count=@($products | Where-Object status -eq 'not_run').Count
        }
        bounded_pass=($productStatus -eq 'passed' -and $diagnosticPassed)
        checks=$Checks.ToArray(); planned_check_names=$PlannedCheckNames; missing_planned_checks=$missingPlanned
        interpretation='Product checks, supplied-view clock diagnostics and formal acceptance are independent. Raw/final join completeness is evaluated separately; formal unmet never becomes a pass.'
    }
}

function Get-DemoObservedState {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)]$RegistryKey)
    $rootItem=Get-Item -LiteralPath $Root
    if ($rootItem -isnot [IO.DirectoryInfo] -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Fixture root is unavailable or redirected' }
    $entries=@(foreach($item in @(Get-ChildItem -LiteralPath $rootItem.FullName -Recurse -Force | Sort-Object FullName)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected fixture entry cannot establish unchanged data' }
        $relative=[IO.Path]::GetRelativePath($rootItem.FullName,$item.FullName).Replace('\','/')
        if ($item -is [IO.DirectoryInfo]) { [ordered]@{path=$relative;kind='directory'} }
        else { [ordered]@{path=$relative;kind='file';bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} }
    })
    $values=@(foreach($name in @($RegistryKey.GetValueNames() | Sort-Object)) {
        [ordered]@{name=$name;kind=$RegistryKey.GetValueKind($name).ToString();value=$RegistryKey.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
    })
    [ordered]@{entries=$entries;registry_values=$values} | ConvertTo-Json -Depth 12 -Compress
}
