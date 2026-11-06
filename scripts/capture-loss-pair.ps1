param(
    [Parameter(Mandatory)][string]$BaselineZip,
    [Parameter(Mandatory)][string]$BaselineSha256,
    [Parameter(Mandatory)][string]$CandidateZip,
    [Parameter(Mandatory)][string]$CandidateSha256,
    [Parameter(Mandatory)][string]$CandidateSource,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,30000)][int]$BackgroundFiles = 12000
)
# Exactly two controlled captures, no retries. Failed baseline evidence is kept.
# Only the integrated candidate must pass the unchanged independent follow gate.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$baselineSource = '8e68ccfe63389e1533235e253eb59329f15be898'
function Require([bool]$Value,[string]$Reason) { if (-not $Value) { throw $Reason } }
function Write-Json([object]$Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
}
function Open-Package([string]$Zip,[string]$Hash,[string]$Source,[string]$Directory) {
    Require ($Hash -cmatch '^[0-9a-f]{64}$') 'Package SHA256 must be lowercase hexadecimal'
    Require ((Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Hash) 'Frozen ZIP hash differs'
    Expand-Archive -LiteralPath $Zip -DestinationPath $Directory
    $build = Get-Content -LiteralPath (Join-Path $Directory 'BUILD.json') -Raw | ConvertFrom-Json
    Require ($build.source_sha -eq $Source -and $build.target -eq 'win-x64') 'Frozen package source/target differs'
    foreach ($name in @('contain.exe','Contain.Viewer.exe','contain-test-installer.exe')) {
        $row = @($build.files | Where-Object { $_.path -eq $name })
        Require ($row.Count -eq 1) 'Frozen package executable identity is absent or ambiguous'
        Require ((Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash.ToLowerInvariant() -eq $row[0].sha256) 'Frozen package executable differs'
    }
    return $build
}
Require ($env:OS -eq 'Windows_NT') 'Live ETW comparison requires Windows'
Require ($CandidateSource -cmatch '^[0-9a-f]{40}$') 'Invalid candidate source'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Require (-not (Test-Path -LiteralPath $OutputDirectory)) 'Pair output must be new; previous attempts are retained'
$private = Join-Path $OutputDirectory 'private-packages'
New-Item -ItemType Directory -Path $OutputDirectory,$private | Out-Null
$baselineDirectory = Join-Path $private 'beta9'
$candidateDirectory = Join-Path $private 'candidate'
$baseline = Open-Package ([IO.Path]::GetFullPath($BaselineZip)) $BaselineSha256 $baselineSource $baselineDirectory
$candidate = Open-Package ([IO.Path]::GetFullPath($CandidateZip)) $CandidateSha256 $CandidateSource $candidateDirectory
Require ($baseline.version -eq '0.3.2-beta.9') 'Baseline is not the immutable Beta9 release'
$fixture = Join-Path $candidateDirectory 'contain-test-installer.exe'
$fixtureHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Json @{baseline=@{source_sha=$baselineSource;zip_sha256=$BaselineSha256;version=$baseline.version};
    candidate=@{source_sha=$CandidateSource;zip_sha256=$CandidateSha256;version=$candidate.version};
    same_fixture_sha256=$fixtureHash;fixture_source=$CandidateSource;background_files=$BackgroundFiles;
    workload='existing fixed follow parent/direct-child/unrelated targets, plus bounded independent noise create/rename/delete';
    resource_policy='unchanged existing queue limits; evidence quota 64 MiB; settle 1500 ms; maximum descendant drain 10000 ms';
    maximum_native_captures=2;retries=0} (Join-Path $OutputDirectory 'package-identity.json')
foreach ($case in @(@{name='beta9';directory=$baselineDirectory;source=$baselineSource},@{name='candidate';directory=$candidateDirectory;source=$CandidateSource})) {
    # A failing oracle is retained by the called script. Continue to candidate;
    # neither the baseline loss nor the candidate's failure is turned into a pass.
    try {
        & "$PSScriptRoot/follow-installer-experiment.ps1" -ContainExecutable (Join-Path $case.directory 'contain.exe') -FixtureExecutable $fixture -OutputDirectory (Join-Path $OutputDirectory $case.name) -Source $case.source -BackgroundFiles $BackgroundFiles
    } catch {
        Write-Host ('Controlled capture/oracle did not pass: '+$case.name+'; original aggregate retained')
    }
    $terminationPath = Join-Path $OutputDirectory ($case.name+'/private/worker-termination.json')
    $termination = if (Test-Path -LiteralPath $terminationPath) { Get-Content -LiteralPath $terminationPath -Raw | ConvertFrom-Json } else { $null }
    if ($null -eq $termination -or $termination.all_owned_processes_exited -ne $true) {
        Write-Host 'No subsequent capture will start: prior owned process termination is unverified'
        break
    }
}
python "$PSScriptRoot/summarize-capture-loss-pair.py" --directory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Candidate pair acceptance failed; safe aggregate retained without relaxing evidence requirements' }
