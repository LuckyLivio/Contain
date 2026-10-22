param(
    [string]$Output = './target/v04/preflight.json',
    [string]$Contain
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$problems = [Collections.Generic.List[string]]::new()
$computer = $null
try { $computer = Get-CimInstance Win32_ComputerSystem } catch { $problems.Add('Computer model query failed: ' + $_.Exception.GetType().Name) }
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$tools = @{}
foreach ($name in @('Get-VM', 'Get-VMSnapshot', 'WindowsSandbox.exe', 'vmrun.exe', 'VBoxManage.exe')) {
    $tools[$name] = [bool](Get-Command $name -ErrorAction SilentlyContinue)
}
$doctor = $null
if ($Contain) {
    $executable = (Resolve-Path -LiteralPath $Contain).Path
    $raw = (& $executable doctor --json) -join "`n"
    if ($LASTEXITCODE -eq 0) { $doctor = ($raw | ConvertFrom-Json).data }
    else { $problems.Add('Contain doctor failed; ETW remains unverified') }
}
if (-not $elevated) { $problems.Add('Current token is not elevated') }
if (-not $doctor -or $doctor.etw_file -ne 'active' -or $doctor.etw_process -ne 'active') {
    $problems.Add('Required ETW provider activation is not verified')
}
$problems.Add('Disposable guest identity, clean checkpoint and successful restore test have not been independently verified')
$problems.Add('Independent Procmon/native log and target process-instance ground truth have not been collected')
$report = [ordered]@{
    schema_version = 1
    observed_at_utc = [DateTime]::UtcNow.ToString('o')
    os = [Environment]::OSVersion.VersionString
    architecture = $env:PROCESSOR_ARCHITECTURE
    elevated = $elevated
    manufacturer = $(if ($computer) { $computer.Manufacturer } else { $null })
    model = $(if ($computer) { $computer.Model } else { $null })
    hypervisor_present = $(if ($computer) { $computer.HypervisorPresent } else { $null })
    available_commands = $tools
    doctor = $doctor
    safe_recoverable_environment_verified = $false
    vendor_installers_executed = 0
    blockers = $problems
    interpretation = 'Read-only capability inventory; optional doctor starts/stops its own ETW probe. A hypervisor flag, installed VM command, admin token or provider activation does not prove disposable isolation or a recoverable checkpoint. This script never runs an installer, accepts a safe=true override, creates a VM, restores a checkpoint, installs a driver, or changes privileges.'
}
$destination = [IO.Path]::GetFullPath($Output)
$parent = [IO.Path]::GetDirectoryName($destination)
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding utf8
$report | ConvertTo-Json -Depth 12
