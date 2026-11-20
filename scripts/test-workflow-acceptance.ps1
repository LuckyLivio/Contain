$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
# Import only the function definition, never run the native orchestration on this host.
$t=$null;$e=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'workflow-windows-acceptance.ps1'),[ref]$t,[ref]$e)
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Record-CLI'},$true)
Invoke-Expression $definition.Extent.Text
$results=New-Object Collections.ArrayList
function Fake-CLI { Write-Output 'Report written: synthetic fixture';$global:LASTEXITCODE=0 }
$Cli='Fake-CLI'
$code=Record-CLI 'fixture' @('platform') 0
if ($code -isnot [int] -or $code -ne 0 -or $results[0].status -ne 'passed') {throw 'Native stdout contaminated exit-code return'}
. (Join-Path $PSScriptRoot 'workflow-receiver-evidence.ps1')
function Test-Path {param($LiteralPath,$Path) return $false}
function Get-ItemProperty {param($Path,$ErrorAction) return $null}
function Get-Command {param($Name,$CommandType,$ErrorAction) return $null}
$absent=Get-WorkflowReceiverEvidence
if ($absent.status -ne 'passed') {throw 'Controlled absent-tool fixture rejected'}
function Fake-SDKList { Write-Output '10.0.403 [PRIVATE_PATH_NOT_FOR_REPORT]';$global:LASTEXITCODE=0 }
function Get-Command {param($Name,$CommandType,$ErrorAction) if ($Name -eq 'dotnet') {return @{Source='Fake-SDKList'}} }
$present=Get-WorkflowReceiverEvidence
if ($present.status -ne 'failed' -or !$present.probes.dotnet_sdk -or ($present | ConvertTo-Json -Depth 10) -like '*PRIVATE_PATH*') {throw 'Installed SDK evidence incorrect or leaked'}
Write-Output 'Acceptance policy fixtures: 3 passed (synthetic; no clean snapshot claim)'
