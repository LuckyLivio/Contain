param([ValidateSet('light','final')][string]$Phase='light', [string]$OutputDirectory='./target/hotpath-public')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
$priorVariant=$env:CONTAIN_HOTPATH_VARIANT
$priorProfile=$env:CONTAIN_PROFILE_PATH
try {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $output=(Resolve-Path $OutputDirectory).Path
    $records=[Collections.Generic.List[object]]::new()
    $rounds=if($Phase -eq 'light'){2}else{5}
    foreach($round in 1..$rounds) {
        $variants=if($Phase -eq 'light'){if($round%2){@('D0','D1','D2')}else{@('D2','D1','D0')}}else{@('D3')}
        foreach($variant in $variants) {
            foreach($noise in @($false,$true)) {
                $label="$variant-$round-$(if($noise){'noise'}else{'ordinary'})"
                $private=Join-Path $repo "target/hotpath-private/$Phase-$label"
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
                $json=& python -X utf8 "$PSScriptRoot/hotpath-artifact.py" $private (Join-Path $output "$Phase-$label") $variant $failure
                if($LASTEXITCODE -ne 0){throw "artifact extraction failed: $label"}
                $row=$json | ConvertFrom-Json
                $row | Add-Member round $round
                $row | Add-Member registry_noise $noise
                $row | Add-Member harness_wall_seconds $clock.Elapsed.TotalSeconds
                $records.Add($row)
                Write-Output ("HOTPATH: "+($row|ConvertTo-Json -Depth 30 -Compress))
            }
        }
    }
    $report=[ordered]@{phase=$Phase;commit=(& git rev-parse HEAD);expected_attempts=($rounds*2*$(if($Phase -eq 'light'){3}else{1}));profile='release --locked';fixture_source_sha256=(Get-FileHash examples/test-installer/src/main.rs).Hash;scorer_sha256=(Get-FileHash scripts/score-fixture.py).Hash;trials=$records}
    $report | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $output "$Phase-summary.json") -Encoding utf8
} finally { $env:CONTAIN_HOTPATH_VARIANT=$priorVariant;$env:CONTAIN_PROFILE_PATH=$priorProfile;Pop-Location }
