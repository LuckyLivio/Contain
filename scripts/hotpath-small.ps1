param([ValidateSet('light','final')][string]$Phase='light', [string]$OutputDirectory='./target/hotpath-public', [ValidateSet('E1')][string]$CheckpointVariant)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($CheckpointVariant -and $Phase -ne 'final'){throw 'Checkpoint small trials require final D3 phase'}
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
$priorVariant=$env:CONTAIN_HOTPATH_VARIANT
$priorProfile=$env:CONTAIN_PROFILE_PATH
$priorCheckpoint=$env:CONTAIN_CHECKPOINT_VARIANT
$priorWalBudget=$env:CONTAIN_WAL_BUDGET_BYTES
$checkpointRun=if($CheckpointVariant){'checkpoint-'+[guid]::NewGuid().ToString('N')}else{''}
$output=$null;$report=$null
$records=[Collections.Generic.List[object]]::new()
try {
    if($CheckpointVariant){$env:CONTAIN_CHECKPOINT_VARIANT=$CheckpointVariant;$env:CONTAIN_WAL_BUDGET_BYTES='536870912'}
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $output=(Resolve-Path $OutputDirectory).Path
    $rounds=if($Phase -eq 'light'){2}else{5}
    foreach($round in 1..$rounds) {
        $variants=if($Phase -eq 'light'){if($round%2){@('D0','D1','D2')}else{@('D2','D1','D0')}}else{@('D3')}
        foreach($variant in $variants) {
            foreach($noise in @($false,$true)) {
                $label="$variant-$round-$(if($noise){'noise'}else{'ordinary'})"
                $artifactLabel=if($CheckpointVariant){"$checkpointRun-$Phase-$label"}else{"$Phase-$label"}
                $private=Join-Path $repo "target/hotpath-private/$artifactLabel"
                New-Item -ItemType Directory -Path $private -Force | Out-Null
                $env:CONTAIN_HOTPATH_VARIANT=$variant
                $env:CONTAIN_PROFILE_PATH=Join-Path $private 'profile.json'
                $failure=''
                $clock=[Diagnostics.Stopwatch]::StartNew()
                try {
                    & "$PSScriptRoot/demo.ps1" -Release -RequireEtw -RegistryNoise:$noise -OutputDirectory $private *> (Join-Path $private 'demo.log')
                } catch { $failure=$_.Exception.Message }
                $clock.Stop()
                # Failures remain in the fixed denominator; the caller enforces the aggregate gate.
                $json=& python -X utf8 "$PSScriptRoot/hotpath-artifact.py" $private (Join-Path $output $artifactLabel) $variant $failure
                if($LASTEXITCODE -ne 0){throw "artifact extraction failed: $label"}
                $row=$json | ConvertFrom-Json
                $row | Add-Member round $round
                $row | Add-Member registry_noise $noise
                $row | Add-Member harness_wall_seconds $clock.Elapsed.TotalSeconds
                if($CheckpointVariant){
                    $row | Add-Member checkpoint_variant $CheckpointVariant
                    $resultPath=Join-Path (Join-Path $output $artifactLabel) 'result.json'
                    $row | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $resultPath -Encoding utf8
                    & python -X utf8 "$PSScriptRoot/checkpoint-gate.py" $resultPath $CheckpointVariant
                    if($LASTEXITCODE -ne 0){throw "Checkpoint gate validation failed: $label"}
                    $row=Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
                }
                $records.Add($row)
                Write-Output ("HOTPATH: "+($row|ConvertTo-Json -Depth 30 -Compress))
            }
        }
    }
    $report=[ordered]@{phase=$Phase;commit=(& git rev-parse HEAD);expected_attempts=($rounds*2*$(if($Phase -eq 'light'){3}else{1}));profile='release --locked';fixture_source_sha256=(Get-FileHash examples/test-installer/src/main.rs).Hash;scorer_sha256=(Get-FileHash scripts/score-fixture.py).Hash;trials=$records}
    if($CheckpointVariant){$report.checkpoint_variant=$CheckpointVariant;$report.wal_budget_bytes=536870912;$report.run_id=$checkpointRun}
    $report | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $output "$Phase-summary.json") -Encoding utf8
} finally {
    $env:CONTAIN_HOTPATH_VARIANT=$priorVariant;$env:CONTAIN_PROFILE_PATH=$priorProfile;$env:CONTAIN_CHECKPOINT_VARIANT=$priorCheckpoint;$env:CONTAIN_WAL_BUDGET_BYTES=$priorWalBudget
    if($CheckpointVariant -and $output){
        if(-not $report){$report=[ordered]@{phase=$Phase;checkpoint_variant=$CheckpointVariant;run_id=$checkpointRun;expected_attempts=10;harness_aborted=$true;trials=$records}}
        $report | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $output "$Phase-summary.json") -Encoding utf8
        $report | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $output "$checkpointRun-summary.json") -Encoding utf8
    }
    Pop-Location
}
