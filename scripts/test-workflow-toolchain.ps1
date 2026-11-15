$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'workflow-toolchain.ps1')
$candidates=@(@{Version='14.45.1'},@{Version='14.44.1'},@{Version='14.44.9'})
if ((Select-WorkflowToolset $candidates '14.44' '').Version -ne '14.44.9') {throw 'wrong multi-version selection'}
if ((Select-WorkflowToolset $candidates '14.44' '14.44.1').Version -ne '14.44.1') {throw 'active compatible version was not retained'}
$cases=@(
    { Select-WorkflowToolset $candidates '14.44' '14.45.1' },
    { Select-WorkflowToolset @(@{Version='14.45.1'}) '14.44' '' },
    { Assert-WorkflowSelected @{VCToolsVersion='14.45.1';WindowsSDKVersion='10.0.26100.0\'} '14.44.9' '10.0.26100.0' },
    { Assert-WorkflowSelected @{VCToolsVersion='14.44.9';WindowsSDKVersion='10.0.22621.0\'} '14.44.9' '10.0.26100.0' }
)
foreach ($case in $cases) { $rejected=$false;try { & $case | Out-Null } catch {$rejected=$true};if (!$rejected) {throw 'wrong selection accepted'} }
Write-Output 'Toolchain policy fixtures: 6 passed (synthetic; no installed Windows toolchain claim)'
