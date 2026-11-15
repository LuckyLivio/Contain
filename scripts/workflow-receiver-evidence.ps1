# Presence evidence only, not proof that the machine came from a clean snapshot.
function Get-WorkflowReceiverEvidence {
    $probes=@{}
    foreach ($name in @('rustc','cargo','cl','msbuild','python','git')) {
        $found=Get-Command $name -CommandType Application -ErrorAction SilentlyContinue
        # Windows Store's uninstalled Python alias is not an installed interpreter.
        $probes[$name]=[bool]$found
        if ($name -eq 'python' -and $found -and $found.Source -like '*\WindowsApps\python*.exe') {$probes[$name]=$false}
    }
    $sdk=$false
    foreach ($base in @($env:ProgramFiles,${env:ProgramFiles(x86)},$env:LOCALAPPDATA)) {
        if (!$base) {continue}
        foreach ($relative in @('dotnet/sdk','Microsoft/dotnet/sdk','.cargo/bin/rustc.exe','Microsoft Visual Studio/Installer/vswhere.exe')) {
            $path=Join-Path $base $relative
            if (Test-Path -LiteralPath $path) {
                if ($relative -like '*sdk') { $sdk=$sdk -or [bool](Get-ChildItem -LiteralPath $path -Directory -ErrorAction Stop | Select-Object -First 1) }
                else { $probes['registered_development_tool']=$true }
            }
        }
    }
    if ($env:USERPROFILE -and (Test-Path (Join-Path $env:USERPROFILE '.cargo/bin/rustc.exe'))) {$probes['rust_user_install']=$true}
    if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'sdk'))) {$sdk=$sdk -or [bool](Get-ChildItem (Join-Path $env:DOTNET_ROOT 'sdk') -Directory -ErrorAction Stop | Select-Object -First 1)}
    if ($env:CARGO_HOME -and (Test-Path (Join-Path $env:CARGO_HOME 'bin/rustc.exe'))) {$probes['rust_custom_install']=$true}
    $dotnet=Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($dotnet) {
        $sdks=& $dotnet.Source --list-sdks
        if ($LASTEXITCODE -ne 0) {throw 'dotnet SDK enumeration failed; absence unknown'}
        $sdk=$sdk -or [bool]$sdks
    }
    $probes['dotnet_sdk']=$sdk
    foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots','HKLM:\SOFTWARE\Microsoft\VisualStudio\SxS\VS7','HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\SxS\VS7')) {
        if (Test-Path $key) {$probes['registered_sdk_or_vs']=$true}
    }
    # Scan uninstall display names locally, serialize only a presence boolean.
    foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        $rows=@()
        if (Test-Path ($key.TrimEnd('*').TrimEnd('\'))) {
            try {$rows=Get-ItemProperty $key -ErrorAction Stop} catch {throw 'Registration probe unavailable; absence unknown'}
        }
        foreach ($row in $rows) {
            $name=$row.PSObject.Properties['DisplayName']
            if ($name -and $name.Value -match '(Visual Studio|Build Tools|Windows Software Development Kit|\.NET SDK|Rustup|Python 3)') {$probes['registered_development_tool']=$true}
        }
    }
    $present=[bool]($probes.Values | Where-Object {$_} | Select-Object -First 1)
    return @{status=$(if($present){'failed'}else{'passed'});probes=$probes;scope='PATH, standard install directories, SDK enumeration, registration; clean snapshot provenance must be supplied independently'}
}
