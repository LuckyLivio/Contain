[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$HarnessPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidateSet('Wpf','MissingInno')][string]$Scenario
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'test-installed-wpf-workflow.ps1') -CandidateDirectory $CandidateDirectory `
    -HarnessPath $HarnessPath -EvidenceDirectory $EvidenceDirectory -ExpectedVersion '0.8.0' `
    -MeasureFirstPackage -FirstPackageScenario $Scenario
exit $LASTEXITCODE
