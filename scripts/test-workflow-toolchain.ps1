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
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ('contain-step-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $probeScript = Join-Path $tempDir 'probe.ps1'
    [IO.File]::WriteAllText($probeScript, '[Console]::Out.WriteLine("arg:" + $args[0]); [Console]::Error.WriteLine("progress on stderr"); exit ([int]$args[1])', (New-Object Text.UTF8Encoding $false))
    $okLog = Join-Path $tempDir 'stderr-ok.log'
    $code = Invoke-WorkflowStep $okLog 'powershell.exe' @('-NoProfile','-File',$probeScript,'path with spaces\dir','0')
    $text = [IO.File]::ReadAllText($okLog, [Text.Encoding]::UTF8)
    if ($code -ne 0 -or $text -notlike '*arg:path with spaces\dir*' -or $text -notlike '*progress on stderr*') { throw 'native stderr or spaced argument mishandled' }
    $failLog = Join-Path $tempDir 'stderr-fail.log'
    $failCode = Invoke-WorkflowStep $failLog 'powershell.exe' @('-NoProfile','-File',$probeScript,'second arg','7')
    $failText = [IO.File]::ReadAllText($failLog, [Text.Encoding]::UTF8)
    if ($failCode -ne 7 -or $failText -notlike '*progress on stderr*') { throw 'nonzero exit or stderr log lost' }
} finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output 'Toolchain policy fixtures: 8 passed (synthetic; native stderr/exit/argument checks included)'
