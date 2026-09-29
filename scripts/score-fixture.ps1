param([Parameter(Mandatory)]$Capture, [Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Normalize([string]$path) {
    $path = $path.Replace('\\?\','').ToLowerInvariant()
    if ($path.StartsWith('hkcu\')) { $path = '\registry\user\' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value.ToLowerInvariant() + '\' + $path.Substring(5) }
    return $path
}
$truth = @(Get-ChildItem -LiteralPath $Root -Filter 'ground-truth-*.jsonl' | ForEach-Object { Get-Content -LiteralPath $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } })
if ($truth.Count -eq 0) { throw 'Fixture produced no independent ground truth' }
$truth | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root 'ground-truth.json') -Encoding utf8
$index = @{}
foreach ($e in $Capture.events) {
    if ($e.event_type -notin @('file','registry')) { continue }
    $k = (Normalize $e.resource) + '|' + $e.operation
    if (-not $index.ContainsKey($k)) { $index[$k] = [Collections.Generic.List[object]]::new() }
    $index[$k].Add($e)
}
$states = @{}
foreach ($e in $Capture.events) { if ($e.event_type -in @('file_state','registry_state')) { $states[(Normalize $e.resource)] = $e.operation } }
$observed=0; $correct=0; $incorrect=0; $rows=[Collections.Generic.List[object]]::new()
foreach ($t in $truth) {
    $paths=@((Normalize $t.resource))
    if ($null -ne $t.destination) { $paths += (Normalize $t.destination) }
    $candidates=@(foreach ($path in $paths) {
        $k=$path+'|'+$t.operation
        if ($index.ContainsKey($k)) { $index[$k] | Where-Object { [uint64]$_.timestamp_ticks -ge [uint64]$t.start_ticks -and [uint64]$_.timestamp_ticks -le [uint64]$t.end_ticks } }
    })
    $high=@($candidates | Where-Object confidence -eq High)
    $wrong=@($high | Where-Object { $_.evidence.pid -ne $t.pid -or $_.evidence.process_creation_time -ne $t.creation_time -or $t.role -eq 'unrelated' })
    $hasState=$false
    if ($t.success) {
        foreach ($path in $paths) {
            if ($states.ContainsKey($path)) {
                $hasState = $hasState -or ($t.operation -ne 'delete_requested' -or $states[$path] -eq 'deleted')
            }
        }
    }
    $seen=$candidates.Count -gt 0 -or $hasState
    if ($seen) { $observed++ }
    $outcome='Unknown'
    if ($wrong.Count -gt 0) { $incorrect++; $outcome='Incorrect' }
    elseif ($high.Count -gt 0) { $correct++; $outcome='Correct' }
    $rows.Add([ordered]@{role=$t.role;operation=$t.operation;resource=$t.resource;pid=$t.pid;observed=$seen;outcome=$outcome;raw_matches=$candidates.Count})
}
$report=[ordered]@{
    expected=$truth.Count; observed=$observed; correctly_attributed=$correct
    unknown=$truth.Count-$correct-$incorrect; incorrect_attribution=$incorrect
    dropped=$Capture.stats.events_dropped; etw_events_lost=$Capture.backend.etw_events_lost
    etw_buffers_lost=$Capture.backend.etw_buffers_lost; quality=$Capture.quality.level
    definition='One row per instrumented syscall. Observed includes matching raw operation in its exact time interval or compatible final state; Correct requires exact PID+birth and application High. Noise remains Unknown. Missing observations are included in Unknown. This is fixture coverage, not global accuracy.'
    rows=$rows
}
return $report
