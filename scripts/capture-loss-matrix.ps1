param(
    [Parameter(Mandatory)][string]$BaselineZip,
    [Parameter(Mandatory)][string]$PreviousZip,
    [Parameter(Mandatory)][string]$CandidateZip,
    [Parameter(Mandatory)][string]$CandidateSha256,
    [Parameter(Mandatory)][string]$CandidateSource,
    [Parameter(Mandatory)][string]$IdentityDiagnosticExe,
    [Parameter(Mandatory)][string]$OutputDirectory
)
# The order is declared before any capture. Every result is kept, with no retry
# and no selection of a passing sample. The same frozen candidate ZIP is used
# for both candidate captures and by the separate packaged desktop verification.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
function Require([bool]$Value,[string]$Reason) { if (-not $Value) { throw $Reason } }
function Write-Json([object]$Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))
}
function Open-Package([string]$Zip,[object]$Identity,[string]$Directory) {
    Require ((Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Identity.zip_sha256) 'Frozen ZIP hash differs'
    Expand-Archive -LiteralPath $Zip -DestinationPath $Directory
    $build = Get-Content -LiteralPath (Join-Path $Directory 'BUILD.json') -Raw | ConvertFrom-Json
    Require ($build.source_sha -eq $Identity.source_sha -and $build.target -eq 'win-x64' -and $build.version -eq $Identity.version) 'Frozen package source, target or version differs'
    foreach ($name in @('contain.exe','Contain.Viewer.exe','contain-test-installer.exe')) {
        $row = @($build.files | Where-Object { $_.path -eq $name })
        Require ($row.Count -eq 1) 'Frozen executable identity is absent or ambiguous'
        Require ((Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash.ToLowerInvariant() -eq $row[0].sha256) 'Frozen executable differs'
    }
}
Require ($env:OS -eq 'Windows_NT') 'Live ETW comparison requires Windows'
Require ($CandidateSource -cmatch '^[0-9a-f]{40}$' -and $CandidateSha256 -cmatch '^[0-9a-f]{64}$') 'Invalid candidate identity'
$IdentityDiagnosticExe = [IO.Path]::GetFullPath($IdentityDiagnosticExe)
Require (Test-Path -LiteralPath $IdentityDiagnosticExe -PathType Leaf) 'Current-source identity diagnostic is absent'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Require (-not (Test-Path -LiteralPath $OutputDirectory)) 'Matrix output must be new; earlier attempts are retained'
$public = Join-Path $OutputDirectory 'public'
$private = Join-Path $OutputDirectory 'private-packages'
New-Item -ItemType Directory -Path $OutputDirectory,$public,$private | Out-Null
$identities = @{
    baseline=@{version='0.3.2-beta.9';source_sha='8e68ccfe63389e1533235e253eb59329f15be898';zip_sha256='dee47046f01c6a2c31c5bda3a3a3bcf3fc2a1603eb18b35464f68cde04ec7612'}
    previous=@{version='0.3.2-beta.10';source_sha='3c2e06871d28acd199ba1813605afecc9074738d';zip_sha256='41a3e4e3e8dc0b75e245eb3306ed8e5c6294d04c5fc72d4cc9110d3512ef99e7'}
    candidate=@{version='0.3.2-beta.10';source_sha=$CandidateSource;zip_sha256=$CandidateSha256}
}
$directories = @{beta9=(Join-Path $private 'beta9');previous=(Join-Path $private 'previous');candidate=(Join-Path $private 'candidate')}
Open-Package ([IO.Path]::GetFullPath($BaselineZip)) $identities.baseline $directories.beta9
Open-Package ([IO.Path]::GetFullPath($PreviousZip)) $identities.previous $directories.previous
Open-Package ([IO.Path]::GetFullPath($CandidateZip)) $identities.candidate $directories.candidate
$fixture = Join-Path $directories.candidate 'contain-test-installer.exe'
$identities.same_fixture_sha256 = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant()
$identities.fixture_source = $CandidateSource
$identities.identity_diagnostic = @{source_sha=$CandidateSource;sha256=(Get-FileHash -LiteralPath $IdentityDiagnosticExe -Algorithm SHA256).Hash.ToLowerInvariant()}
$identities.background_files = 12000
$identities.resource_policy = 'unchanged existing queue limits; evidence quota 64 MiB; settle 1500 ms; maximum descendant drain 10000 ms'
$order = @('r1/beta9','r1/previous','r1/candidate','r2/candidate','r2/previous','r2/beta9')
Write-Json @{schema_version=1;protocol='contain-capture-loss-matrix-1';maximum_native_captures=6;retries=0;order=$order;package_identity=$identities} (Join-Path $public 'run-plan.json')
$execution = @{attempted=@();not_run=@($order);stop_reason=$null}
Write-Json $execution (Join-Path $public 'execution.json')
foreach ($name in $order) {
    $package = $name.Split('/')[1]
    $identity = $identities[$(if($package -eq 'beta9'){'baseline'}else{$package})]
    $execution.attempted += $name
    $execution.not_run = @($order | Where-Object { $_ -notin $execution.attempted })
    Write-Json $execution (Join-Path $public 'execution.json')
    try {
        & "$PSScriptRoot/follow-installer-experiment.ps1" -ContainExecutable (Join-Path $directories[$package] 'contain.exe') -FixtureExecutable $fixture -OutputDirectory (Join-Path $OutputDirectory $name) -Source $identity.source_sha -BackgroundFiles 12000 -IdentityDiagnosticExe $IdentityDiagnosticExe
    } catch {
        Write-Host ('Predeclared capture/oracle did not pass: '+$name+'; original aggregate retained')
    }
    $terminationPath = Join-Path $OutputDirectory ($name+'/private/worker-termination.json')
    $termination = if(Test-Path -LiteralPath $terminationPath){Get-Content -LiteralPath $terminationPath -Raw | ConvertFrom-Json}else{$null}
    if ($null -eq $termination -or $termination.all_owned_processes_exited -ne $true) {
        $execution.stop_reason = 'owned_process_termination_unverified'
        Write-Json $execution (Join-Path $public 'execution.json')
        Write-Host 'No subsequent capture will start: prior owned process termination is unverified'
        break
    }
}
python "$PSScriptRoot/summarize-capture-loss-matrix.py" --directory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Fixed matrix acceptance failed; all safe aggregates retained without a retry or weaker evidence gate' }
