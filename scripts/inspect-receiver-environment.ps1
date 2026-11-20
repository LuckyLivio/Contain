Set-StrictMode -Version Latest

function Get-ReceiverNetworkState {
    # The staged B gate includes virtual interfaces visible to the guest.
    try {
        $unknown = $false
        foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($adapter.NetworkInterfaceType -eq 'Loopback') { continue }
            if ($adapter.OperationalStatus -eq 'Up') { return 'online' }
            if ($adapter.OperationalStatus -notin @('Down', 'NotPresent', 'LowerLayerDown')) { $unknown = $true }
        }
        if ($unknown) { return 'unknown' }
        return 'offline'
    } catch { return 'unknown' }
}

function Get-ReceiverEnvironmentSummary {
    $osCaption = ''
    $osVersion = [Environment]::OSVersion.Version.ToString()
    $osArch = if ([Environment]::Is64BitOperatingSystem) { 'x86_64' } else { 'x86' }
    $procArch = if ([Environment]::Is64BitProcess) { 'x86_64' } else { 'x86' }

    try {
        $cimOs = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        if ($cimOs.Caption) { $osCaption = [string]$cimOs.Caption }
        if ($cimOs.Version) { $osVersion = [string]$cimOs.Version }
    } catch { }

    # Network state (boolean/count only; no MAC/IP addresses)
    $activePhysicalAdapters = 0
    try {
        $adapters = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.Status -eq 'Up' })
        $activePhysicalAdapters = $adapters.Count
    } catch {
        $activePhysicalAdapters = -1
    }
    $networkState = if ($activePhysicalAdapters -gt 0) {
        'online'
    } elseif ($activePhysicalAdapters -eq 0) {
        'offline'
    } else {
        'unknown'
    }

    # Built-in Windows .NET Framework (recorded separately from modern .NET)
    $netFxVersion = $null
    $netFxRelease = $null
    try {
        $ndp = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
        if ($ndp) {
            if ($ndp.PSObject.Properties['Version']) { $netFxVersion = [string]$ndp.Version }
            if ($ndp.PSObject.Properties['Release']) { $netFxRelease = [int]$ndp.Release }
        }
    } catch { }

    # Modern .NET runtimes & SDKs (version tokens only, never local user paths)
    $modernDotnetSdkVersions = New-Object System.Collections.Generic.List[string]
    $modernDotnetRuntimes = New-Object System.Collections.Generic.List[string]
    $dotnetCmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($dotnetCmd) {
        try {
            $sdkLines = @(& $dotnetCmd.Source --list-sdks 2>$null)
            foreach ($line in $sdkLines) {
                $tok = ($line -split '\s+')[0]
                if ($tok -match '^[0-9]+\.[0-9]+\.[0-9A-Za-z._+-]+$') {
                    $modernDotnetSdkVersions.Add($tok)
                }
            }
        } catch { }
        try {
            $rtLines = @(& $dotnetCmd.Source --list-runtimes 2>$null)
            foreach ($line in $rtLines) {
                $parts = @($line -split '\s+')
                if ($parts.Count -ge 2 -and $parts[0] -like 'Microsoft.*' -and $parts[1] -match '^[0-9]+\.[0-9]+') {
                    $modernDotnetRuntimes.Add("$($parts[0]) $($parts[1])")
                }
            }
        } catch { }
    }

    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        $sdkDir = Join-Path $base 'dotnet\sdk'
        if ((Test-Path -LiteralPath $sdkDir) -and $modernDotnetSdkVersions.Count -eq 0) {
            Get-ChildItem -LiteralPath $sdkDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                if ($_.Name -match '^[0-9]+\.[0-9]+') { $modernDotnetSdkVersions.Add($_.Name) }
            }
        }
        $sharedDir = Join-Path $base 'dotnet\shared\Microsoft.NETCore.App'
        if ((Test-Path -LiteralPath $sharedDir) -and $modernDotnetRuntimes.Count -eq 0) {
            Get-ChildItem -LiteralPath $sharedDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                if ($_.Name -match '^[0-9]+\.[0-9]+') { $modernDotnetRuntimes.Add("Microsoft.NETCore.App $($_.Name)") }
            }
        }
    }

    # Visual Studio / Build Tools
    $vsInstalled = $false
    $vsVersion = $null
    if (${env:ProgramFiles(x86)}) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            try {
                $verOut = (& $vswhere -latest -property catalog_productDisplayVersion 2>$null | Out-String).Trim()
                if ($verOut) {
                    $vsInstalled = $true
                    $vsVersion = $verOut
                }
            } catch { }
        }
    }

    # Rust
    $rustInstalled = $false
    $rustVersion = $null
    $rustcCmd = Get-Command rustc -CommandType Application -ErrorAction SilentlyContinue
    if ($rustcCmd) {
        $rustInstalled = $true
        try {
            $rv = (& $rustcCmd.Source --version 2>$null | Out-String).Trim()
            if ($rv -match '^rustc\s+([0-9]+\.[0-9]+\.[0-9A-Za-z._+-]+)') {
                $rustVersion = $Matches[1]
            }
        } catch { }
    } elseif ($env:USERPROFILE -and (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.cargo\bin\rustc.exe'))) {
        $rustInstalled = $true
    }

    # Inno Setup
    $innoInstalled = $false
    $innoVersion = $null
    foreach ($cand in @(
        $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe' }),
        $(if (${env:ProgramFiles(x86)}) { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }),
        $(if ($env:ProgramFiles) { Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe' })
    )) {
        if ($cand -and (Test-Path -LiteralPath $cand)) { $innoInstalled = $true }
    }
    foreach ($uRoot in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )) {
        $innoReg = Get-ItemProperty -Path $uRoot -ErrorAction SilentlyContinue | Where-Object {
            $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'Inno Setup*'
        } | Select-Object -First 1
        if ($innoReg) {
            $innoInstalled = $true
            if ($innoReg.PSObject.Properties['DisplayVersion'] -and $innoReg.DisplayVersion) {
                $innoVersion = [string]$innoReg.DisplayVersion
            }
            break
        }
    }

    # Clean sandbox / VM availability on current host (non-invasive read-only check)
    $wsbExe = if ($env:SystemRoot) { Join-Path $env:SystemRoot 'System32\WindowsSandbox.exe' } else { '' }
    $sandboxAvailable = ($wsbExe -and (Test-Path -LiteralPath $wsbExe))
    $hyperVService = Get-Service -Name 'vmms' -ErrorAction SilentlyContinue
    $hyperVAvailable = ($null -ne $hyperVService -and $hyperVService.Status -eq 'Running')
    $vboxAvailable = [bool](Get-Command VBoxManage.exe -CommandType Application -ErrorAction SilentlyContinue) -or ($env:ProgramFiles -and (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'Oracle\VirtualBox\VBoxManage.exe')))
    $vmwareAvailable = [bool](Get-Command vmrun.exe -CommandType Application -ErrorAction SilentlyContinue) -or (${env:ProgramFiles(x86)} -and (Test-Path -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'VMware\VMware Workstation\vmrun.exe')))
    $qemuAvailable = [bool](Get-Command qemu-system-x86_64.exe -CommandType Application -ErrorAction SilentlyContinue)

    $hasDevToolsOrModernDotnet = (
        $modernDotnetSdkVersions.Count -gt 0 -or
        $modernDotnetRuntimes.Count -gt 0 -or
        $vsInstalled -or
        $rustInstalled -or
        $innoInstalled
    )
    $anyCleanVmOrSandbox = ($sandboxAvailable -or $hyperVAvailable -or $vboxAvailable -or $vmwareAvailable -or $qemuAvailable)

    return [ordered]@{
        os_caption = $osCaption
        os_version = $osVersion
        os_architecture = $osArch
        process_architecture = $procArch
        network_state = $networkState
        active_physical_adapters = $activePhysicalAdapters
        windows_builtin_net_framework = [ordered]@{
            present = ($null -ne $netFxVersion)
            version = $netFxVersion
            release = $netFxRelease
            note = 'Windows built-in .NET Framework recorded separately from modern .NET'
        }
        preinstalled_toolchains_and_modern_dotnet = [ordered]@{
            modern_dotnet_runtimes_present = ($modernDotnetRuntimes.Count -gt 0)
            modern_dotnet_runtimes = @($modernDotnetRuntimes)
            modern_dotnet_sdk_present = ($modernDotnetSdkVersions.Count -gt 0)
            modern_dotnet_sdks = @($modernDotnetSdkVersions)
            visual_studio_present = $vsInstalled
            visual_studio_version = $vsVersion
            rust_present = $rustInstalled
            rust_version = $rustVersion
            inno_setup_present = $innoInstalled
            inno_setup_version = $innoVersion
        }
        is_clean_tool_free_receiver = (!$hasDevToolsOrModernDotnet)
        local_clean_vm_or_sandbox_available = $anyCleanVmOrSandbox
        clean_environment_probes = [ordered]@{
            windows_sandbox_enabled = [bool]$sandboxAvailable
            hyperv_vmms_running = [bool]$hyperVAvailable
            virtualbox_present = [bool]$vboxAvailable
            vmware_present = [bool]$vmwareAvailable
            qemu_present = [bool]$qemuAvailable
        }
    }
}
