param([string]$Family='14.44', [string]$Sdk='10.0.26100.0')
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding $false
$OutputEncoding=[Console]::OutputEncoding
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'workflow-toolchain.ps1')
try { Get-WorkflowToolchain $Family $Sdk | ConvertTo-Json -Depth 5 -Compress }
catch {
    # Do not serialize provider error text which can contain local environment values.
    $reason=$_.Exception.Message
    if ($reason -notmatch '^[a-z_]+$') { $reason='toolchain_selection_failed' }
    @{status='failed';reason=$reason;remedy='Install MSVC 14.44 x64 and Windows SDK 10.0.26100.0; use a fresh shell if active versions conflict'} | ConvertTo-Json -Compress
}
