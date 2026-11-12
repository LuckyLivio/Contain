param(
    [Parameter(Mandatory)][string]$BaselineZip,
    [Parameter(Mandatory)][string]$CandidateZip,
    [Parameter(Mandatory)][string]$CandidateSource,
    [Parameter(Mandatory)][string]$FixtureExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory
)
# Fixed ABBA on one disposable runner. No profiling or repeat-to-green.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
function Require([bool]$Value,[string]$Reason) { if (-not $Value) { throw $Reason } }
function Write-Json([object]$Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))
}
Require ($env:OS -eq 'Windows_NT') 'Real ETW comparison requires Windows'
Require ($CandidateSource -cmatch '^[0-9a-f]{40}$') 'Invalid source'
Require ((Get-FileHash -LiteralPath $BaselineZip -Algorithm SHA256).Hash.ToLowerInvariant() -eq '953b243bb3dd8ea32e61dcf5fdeaf143b9e697ea4a57021fb85ffd961ef907e3') 'Immutable Beta.1 ZIP differs'
$FixtureExecutable = [IO.Path]::GetFullPath($FixtureExecutable)
Require (Test-Path -LiteralPath $FixtureExecutable -PathType Leaf) 'Independent fixture missing'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Require (-not (Test-Path -LiteralPath $OutputDirectory)) 'Previous attempts must be retained'
$public = Join-Path $OutputDirectory 'public'
$private = Join-Path $OutputDirectory 'private-packages'
New-Item -ItemType Directory -Path $OutputDirectory,$public,$private | Out-Null
$directories = @{baseline=(Join-Path $private 'baseline');candidate=(Join-Path $private 'candidate')}
Expand-Archive -LiteralPath $BaselineZip -DestinationPath $directories.baseline
Expand-Archive -LiteralPath $CandidateZip -DestinationPath $directories.candidate
python "$PSScriptRoot/package-modern-desktop.py" verify --zip $BaselineZip --package $directories.baseline > (Join-Path $private 'baseline-verification.json')
Require ($LASTEXITCODE -eq 0) 'Baseline archive or extracted files differ'
python "$PSScriptRoot/package-beta2-desktop.py" verify --zip $CandidateZip --package $directories.candidate > (Join-Path $private 'candidate-verification.json')
Require ($LASTEXITCODE -eq 0) 'Candidate archive or extracted files differ'
$baseline = Get-Content -LiteralPath (Join-Path $directories.baseline 'BUILD.json') -Raw | ConvertFrom-Json
$candidate = Get-Content -LiteralPath (Join-Path $directories.candidate 'BUILD.json') -Raw | ConvertFrom-Json
Require ($baseline.version -eq '0.4.0-beta.1' -and $baseline.source_sha -eq '5a6ce89867e4c8c9d428de0a80b67b421bbd9717') 'Baseline source differs'
Require ($candidate.version -eq '0.4.0-beta.2' -and $candidate.source_sha -eq $CandidateSource) 'Candidate source differs'
$order = @('r1/baseline','r1/candidate','r2/candidate','r2/baseline')
$identities = @{
    baseline=@{version=$baseline.version;source_sha=$baseline.source_sha;core_source_sha=$baseline.core_source_sha;core_sha256=$baseline.core_sha256;zip_sha256='953b243bb3dd8ea32e61dcf5fdeaf143b9e697ea4a57021fb85ffd961ef907e3'}
    candidate=@{version=$candidate.version;source_sha=$CandidateSource;core_source_sha=$candidate.core_source_sha;core_sha256=$candidate.core_sha256;zip_sha256=(Get-FileHash -LiteralPath $CandidateZip -Algorithm SHA256).Hash.ToLowerInvariant()}
}
Write-Json @{schema_version=1;protocol='contain-beta2-comparison-1';maximum_native_captures=4;retries=0;order=$order;package_identity=$identities;fixture_sha256=(Get-FileHash -LiteralPath $FixtureExecutable -Algorithm SHA256).Hash.ToLowerInvariant();fixture_source=$CandidateSource;background_files=12000;profile=$false;success='Both candidate samples pass unchanged independent oracle with zero source/application/candidate loss, correct saved session, parent and direct-child writes verified, unrelated never promoted; all four workloads complete and comparable';resource_policy='unchanged queue limits; 64 MiB journal; settle 1500 ms; maximum drain 10000 ms'} (Join-Path $public 'run-plan.json')
$execution = @{attempted=@();not_run=@($order);stop_reason=$null}
Write-Json $execution (Join-Path $public 'execution.json')
foreach ($name in $order) {
    $package = $name.Split('/')[1]
    $execution.attempted += $name
    $execution.not_run = @($order | Where-Object { $_ -notin $execution.attempted })
    Write-Json $execution (Join-Path $public 'execution.json')
    try {
        & "$PSScriptRoot/follow-installer-experiment.ps1" -ContainExecutable (Join-Path $directories[$package] 'contain.exe') -FixtureExecutable $FixtureExecutable -OutputDirectory (Join-Path $OutputDirectory $name) -Source $identities[$package].source_sha -BackgroundFiles 12000
    } catch {
        Write-Host ('Predeclared capture failed: ' + $name + '; retained without retry')
    }
    $terminationPath = Join-Path $OutputDirectory ($name + '/private/worker-termination.json')
    $termination = if (Test-Path -LiteralPath $terminationPath) { Get-Content -LiteralPath $terminationPath -Raw | ConvertFrom-Json } else { $null }
    if ($null -eq $termination -or $termination.all_owned_processes_exited -ne $true) {
        $execution.stop_reason = 'prior_owned_process_termination_unverified'
        Write-Json $execution (Join-Path $public 'execution.json')
        break
    }
}
python "$PSScriptRoot/summarize-beta2-capture.py" --directory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Fixed comparison failed; candidate remains diagnostic only' }
