# Dot-sourceable helpers. No environment values or paths are written to public receipts.
function Select-WorkflowToolset($Candidates, [string]$Family, [string]$ActiveVersion) {
    if ($ActiveVersion -and $ActiveVersion.TrimEnd('\') -notlike "$Family.*") { throw 'active_msvc_conflict' }
    $matches = @($Candidates | Where-Object { $_.Version -like "$Family.*" } | Sort-Object { [version]$_.Version } -Descending)
    if (!$matches.Count) { throw 'required_msvc_missing' }
    if ($ActiveVersion) {
        $matches = @($matches | Where-Object { $_.Version -eq $ActiveVersion.TrimEnd('\') })
        if (!$matches.Count) { throw 'active_msvc_not_found' }
    }
    return $matches[0]
}
function Assert-WorkflowSelected($Environment, [string]$Msvc, [string]$Sdk) {
    if ($Environment['VCToolsVersion'].TrimEnd('\') -ne $Msvc) { throw 'selected_msvc_conflict' }
    if ($Environment['WindowsSDKVersion'].TrimEnd('\') -ne $Sdk) { throw 'selected_sdk_conflict' }
}
function Get-WorkflowToolchain([string]$Family = '14.44', [string]$Sdk = '10.0.26100.0') {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'windows_required' }
    if ($env:WindowsSDKVersion -and $env:WindowsSDKVersion.TrimEnd('\') -ne $Sdk) { throw 'active_sdk_conflict' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'vswhere_missing' }
    $installations = & $vswhere -all -products '*' -version '[17,18)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'vswhere_failed' }
    $candidates = @()
    foreach ($installation in $installations) {
        $tools = Join-Path $installation.installationPath 'VC/Tools/MSVC'
        if (!(Test-Path -LiteralPath $tools)) { continue }
        foreach ($dir in Get-ChildItem -LiteralPath $tools -Directory) {
            if ((Test-Path (Join-Path $dir.FullName 'bin/Hostx64/x64/cl.exe')) -and (Test-Path (Join-Path $dir.FullName 'bin/Hostx64/x64/link.exe'))) {
                $candidates += [pscustomobject]@{ Version=$dir.Name; Root=$dir.FullName; VsRoot=$installation.installationPath }
            }
        }
    }
    $selected = Select-WorkflowToolset $candidates $Family $env:VCToolsVersion
    # Enter the requested toolset/SDK in a child shell, capture only build variables.
    $vcvars = Join-Path $selected.VsRoot 'VC/Auxiliary/Build/vcvarsall.bat'
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'cmd.exe'
    $start.Arguments = '/D /U /S /C ""' + $vcvars + '" amd64 ' + $Sdk + ' -vcvars_ver=' + $selected.Version + ' >nul && set"'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::Unicode
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'vcvars_timeout' }
    if ($process.ExitCode -ne 0) { throw 'sdk_or_vcvars_failed' }
    $captured = @{}
    $allowed = @('PATH','INCLUDE','LIB','LIBPATH','VCToolsVersion','VCToolsInstallDir','VSINSTALLDIR','VCINSTALLDIR','VisualStudioVersion','WindowsSdkDir','WindowsSDKVersion','WindowsSDKLibVersion','UniversalCRTSdkDir','UCRTVersion','FrameworkDir','FrameworkVersion','FrameworkDIR64','FrameworkVersion64','VSCMD_ARG_HOST_ARCH','VSCMD_ARG_TGT_ARCH')
    foreach ($line in $stdout.Result -split "`r?`n") {
        $pair = $line -split '=',2
        if ($pair.Count -eq 2 -and $pair[0] -in $allowed) { $captured[$pair[0]]=$pair[1] }
    }
    [void]$stderr.Result
    if (!$captured.ContainsKey('VCToolsVersion') -or !$captured.ContainsKey('WindowsSDKVersion')) { throw 'vcvars_versions_unknown' }
    Assert-WorkflowSelected $captured $selected.Version $Sdk
    # Verify actual executable resolution in this exact environment, not mere installation presence.
    $savedPath = $env:PATH
    try {
        $env:PATH = $captured['PATH']
        foreach ($tool in @('cl.exe','link.exe')) {
            $actual = (Get-Command $tool -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
            $expected = Join-Path $selected.Root ('bin/Hostx64/x64/' + $tool)
            if ([IO.Path]::GetFullPath($actual) -ine [IO.Path]::GetFullPath($expected)) { throw 'resolved_compiler_conflict' }
        }
        $rc = (Get-Command rc.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $expectedRc = Join-Path $captured['WindowsSdkDir'] ('bin/' + $Sdk + '/x64/rc.exe')
        if ([IO.Path]::GetFullPath($rc) -ine [IO.Path]::GetFullPath($expectedRc)) { throw 'resolved_sdk_conflict' }
    } finally { $env:PATH=$savedPath }
    if (!(Test-Path (Join-Path $captured['WindowsSdkDir'] ('Lib/' + $Sdk + '/um/x64/kernel32.lib')))) { throw 'sdk_library_missing' }
    $captured['CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER']=Join-Path $selected.Root 'bin/Hostx64/x64/link.exe'
    $captured['CC_x86_64_pc_windows_msvc']=Join-Path $selected.Root 'bin/Hostx64/x64/cl.exe'
    $captured['CXX_x86_64_pc_windows_msvc']=Join-Path $selected.Root 'bin/Hostx64/x64/cl.exe'
    return [pscustomobject]@{ status='passed'; msvc=$selected.Version; sdk=$Sdk; environment=$captured }
}
function Enter-WorkflowToolchain {
    $selection = Get-WorkflowToolchain
    foreach ($key in $selection.environment.Keys) { [Environment]::SetEnvironmentVariable($key,$selection.environment[$key],'Process') }
    return $selection
}
