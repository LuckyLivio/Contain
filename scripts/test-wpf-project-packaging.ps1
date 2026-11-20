[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$Iscc,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitProcess) {
    throw 'Run this acceptance test in a native 64-bit Windows PowerShell session.'
}
$Cli = (Get-Item -LiteralPath $Cli).FullName
$Iscc = (Get-Item -LiteralPath $Iscc).FullName
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a fresh output directory.' }
$out = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$projectName = 'WpfSmoke' + [guid]::NewGuid().ToString('N').Substring(0,12)
$assemblyName = "${projectName}Desktop"
$project = Join-Path ([IO.Path]::GetTempPath()) $projectName
$appDir = Join-Path $out 'installed fixture'
$entry = Join-Path $appDir "$assemblyName.exe"
$app = $null
$setupStarted = $false
$summary = [ordered]@{ status = 'failed'; stage = 'create-template'; fixture = $projectName; assembly_name = $assemblyName }

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with code $LASTEXITCODE at $($summary.stage)." }
}
function Invoke-Setup([string]$Program, [string]$Arguments) {
    $process = Start-Process -FilePath $Program -ArgumentList $Arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "$Program exited with code $($process.ExitCode)." }
}
function Read-Json([string]$Path) {
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

try {
    # Use a new project outside the repository so no repository MSBuild files affect it.
    $null = New-Item -ItemType Directory -Path $project
    Push-Location $project
    try {
        Invoke-Checked 'dotnet' @('new','wpf','--name',$projectName,'--output','.','--framework','net10.0','--no-restore')
    } finally { Pop-Location }
    $csproj = Join-Path $project "$projectName.csproj"
    [xml]$projectXml = Get-Content -LiteralPath $csproj -Raw
    $wpfGroup = $projectXml.SelectSingleNode('/Project/PropertyGroup[UseWPF]')
    $assembly = $projectXml.CreateElement('AssemblyName')
    $assembly.InnerText = $assemblyName
    [void]$wpfGroup.AppendChild($assembly)
    $projectXml.Save($csproj)
    $originalProjectHash = (Get-FileHash -LiteralPath $csproj).Hash
    [xml]$window = Get-Content -LiteralPath (Join-Path $project 'MainWindow.xaml') -Raw
    $expectedTitle = $window.DocumentElement.GetAttribute('Title')
    if (!$expectedTitle) { throw 'The WPF template must declare a window title.' }

    $summary.stage = 'preview-and-confirm'
    $inspect = Join-Path $out 'preview.json'
    Invoke-Checked $Cli @('onboard-dotnet','--project',$project,'--output-json',$inspect)
    $preview = Read-Json $inspect
    $type = @($preview.evidence_fields | Where-Object label -CEQ '应用类型')
    if ($preview.status -ne 'ready_to_confirm' -or $preview.csproj_file -cne "$projectName.csproj" -or
        $type.Count -ne 1 -or $type[0].value -cne 'WPF 桌面应用' -or
        $preview.assembly_name -cne $assemblyName -or $preview.entry_executable -cne "$assemblyName.exe" -or
        (Test-Path -LiteralPath (Join-Path $project 'contain.environment.json'))) { throw 'Unexpected read-only preview.' }
    Invoke-Checked $Cli @('onboard-dotnet','--project',$project,'--apply','--expected-fingerprint',$preview.source_fingerprint_sha256,'--output-json',(Join-Path $out 'apply.json'))
    if ((Get-FileHash -LiteralPath $csproj).Hash -ne $originalProjectHash) { throw 'Project file changed during onboarding.' }

    $summary.stage = 'check-and-build'
    Invoke-Checked $Cli @('check','--project',$project,'--safe-probes','--report',(Join-Path $out 'check'))
    Invoke-Checked $Cli @('build','--project',$project,'--safe-probes','--execute','--log-dir',(Join-Path $out 'logs'),'--report',(Join-Path $out 'build'))
    $attestationPath = Join-Path $out 'build/build-attestation.json'
    $attestation = Read-Json $attestationPath
    $builtEntry = @($attestation.files | Where-Object path -CEQ "$assemblyName.exe")
    if (!$attestation.build_passed -or $builtEntry.Count -ne 1) { throw 'Build did not attest the custom AssemblyName executable.' }
    $summary.stage = 'package'
    $setupFolder = Join-Path $out 'package'
    Invoke-Checked $Cli @('package-installer','--project',$project,'--attestation',$attestationPath,'--output-dir',$setupFolder,'--iscc',$Iscc,'--report',(Join-Path $out 'packaging'))
    $receipt = Read-Json (Join-Path $setupFolder 'installer-receipt.json')
    $setup = Join-Path $setupFolder "$assemblyName-Setup-x64.exe"
    if ($receipt.entry -cne "$assemblyName.exe" -or $receipt.entry_sha256 -ne $builtEntry[0].sha256 -or $receipt.installer_file -ne "$assemblyName-Setup-x64.exe" -or
        (Get-FileHash -LiteralPath $setup).Hash -ne $receipt.installer_sha256) { throw 'Generated installer identity mismatch.' }
    $appId = [guid]::Parse($receipt.app_id)
    $shortId = $appId.ToString('N').Substring(0,8)
    $link = Join-Path ([Environment]::GetFolderPath('Programs')) "$assemblyName ($shortId).lnk"
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $appId.ToString('B') + '_is1'
    if ((Test-Path -LiteralPath $link) -or (Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $appDir)) {
        throw 'Fixture identity already in use.'
    }

    $summary.stage = 'install-and-launch'
    $setupStarted = $true
    Invoke-Setup $setup ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="{0}" /LOG="{1}"' -f $appDir,(Join-Path $out 'install.log'))
    if (!(Test-Path -LiteralPath $key) -or !(Test-Path -LiteralPath $link)) { throw 'Expected per-user registration and shortcut.' }
    if (!(Test-Path -LiteralPath (Join-Path $appDir 'PresentationFramework.dll') -PathType Leaf)) { throw 'Installed fixture lacks its WPF runtime.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    if ($shortcut.TargetPath -ne $entry -or (Get-FileHash -LiteralPath $entry).Hash -ne $receipt.entry_sha256) {
        throw 'The Start menu shortcut or installed binary differs from the generated application.'
    }
    $app = Start-Process -FilePath $shortcut.TargetPath -WorkingDirectory $shortcut.WorkingDirectory -PassThru
    $null = $app.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $app.Refresh()
        if ($app.HasExited) { throw "Installed WPF app exited before displaying its window ($($app.ExitCode))." }
        if ($app.MainWindowHandle -ne [IntPtr]::Zero -and $app.MainWindowTitle -ceq $expectedTitle -and $app.Responding) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Installed WPF process did not expose a responsive window with the expected title.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    if ($app.MainModule.FileName -ne $entry) { throw 'Window process did not load the installed custom entry.' }
    $summary.window_pid = $app.Id
    $summary.window_title = $app.MainWindowTitle
    $summary.window_responding = $true
    $summary.project_type = $type[0].value
    if (!$app.CloseMainWindow() -or !$app.WaitForExit(10000) -or $app.ExitCode -ne 0) { throw 'The WPF application did not close normally.' }

    $summary.stage = 'uninstall'
    Invoke-Setup (Join-Path $appDir 'unins000.exe') ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="{0}"' -f (Join-Path $out 'uninstall.log'))
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-Path -LiteralPath $appDir) -or (Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $link)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Uninstall left a fixture directory, registration or shortcut.' }
        Start-Sleep -Milliseconds 150
    }
    $setupStarted = $false
    $summary.status = 'passed'
    $summary.stage = 'finished'
    $summary.installer_sha256 = $receipt.installer_sha256
} catch {
    $summary.error = $_.Exception.Message
    throw
} finally {
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'acceptance.json') -Encoding UTF8
    if ($app -and !$app.HasExited) {
        [void]$app.CloseMainWindow()
        [void]$app.WaitForExit(5000)
    }
    if ($setupStarted -and (Test-Path -LiteralPath (Join-Path $appDir 'unins000.exe')) -and (!$app -or $app.HasExited)) {
        Invoke-Setup (Join-Path $appDir 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    }
    if ($summary.status -eq 'passed') {
        # Only this newly generated fixture has disposable build output. Keep the package and receipts in $out.
        foreach ($child in @('bin','obj','target/workflow/publish/win-x64')) {
            $path = Join-Path $project $child
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
        }
    }
}
