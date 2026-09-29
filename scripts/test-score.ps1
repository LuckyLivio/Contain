$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=Join-Path $env:TEMP ('contain-demo-score-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $truth=@{role='child';pid=1;creation_time='10';operation='write_requested';resource='C:\fixture\data';destination=$null;start_ticks='100';end_ticks='200';success=$true}
    $journal=Join-Path $root 'ground-truth-1.jsonl'
    $truth | ConvertTo-Json -Compress | Set-Content -LiteralPath $journal -Encoding utf8
    $event=[pscustomobject]@{event_type='file';operation='write_requested';resource='C:\fixture\data';timestamp_ticks='150';confidence='High';evidence=[pscustomobject]@{pid=2;process_creation_time='20'}}
    $capture=[pscustomobject]@{events=@($event);stats=@{events_dropped=0};backend=@{etw_events_lost=0;etw_buffers_lost=0};quality=@{level='Degraded'}}
    $r=& "$PSScriptRoot/score-fixture.ps1" -Capture $capture -Root $root
    if ($r.incorrect_attribution -ne 1) {throw 'oracle failed to detect wrong actor'}
    $event.evidence.pid=1; $event.evidence.process_creation_time='10'
    $r=& "$PSScriptRoot/score-fixture.ps1" -Capture $capture -Root $root
    if ($r.correctly_attributed -ne 1) {throw 'oracle rejected exact actor'}
    $truth.role='unrelated'
    $truth | ConvertTo-Json -Compress | Set-Content -LiteralPath $journal -Encoding utf8
    $r=& "$PSScriptRoot/score-fixture.ps1" -Capture $capture -Root $root
    if ($r.incorrect_attribution -ne 1) {throw 'oracle failed to reject noise ownership'}
    $event.confidence='Unknown'
    $r=& "$PSScriptRoot/score-fixture.ps1" -Capture $capture -Root $root
    if ($r.unknown -ne 1 -or $r.observed -ne 1) {throw 'oracle confused observation and attribution'}
    $event.timestamp_ticks='201'
    $r=& "$PSScriptRoot/score-fixture.ps1" -Capture $capture -Root $root
    if ($r.observed -ne 0 -or $r.unknown -ne 1) {throw 'oracle ignored syscall interval'}
    Write-Output 'SCORER VERIFIED: wrong actor, noise ownership, exact identity, Unknown and timestamp boundary.'
} finally {
    $resolved=(Resolve-Path -LiteralPath $root).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -or [IO.Path]::GetFileName($resolved) -notlike 'contain-demo-score-*') {throw 'unexpected score test cleanup path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
