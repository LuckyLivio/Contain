# Called after the installed Contain UI generates an installer for the disposable WPF project.
function Test-GeneratedWpfSetup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectDirectory,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z][A-Za-z0-9]{1,39}$')][string]$AssemblyName,
        [Parameter(Mandatory)][string]$ReceiptPath,
        [Parameter(Mandatory)][string]$WorkDirectory,
        [Parameter(Mandatory)][string]$ExpectedWindowTitle
    )

    $ErrorActionPreference = 'Stop'
    $stage = 'identity'
    $app = $null
    $setupStarted = $false
    function Assert-ProjectSetup($condition, [string]$reason) {
        if (!$condition) {
            $exception = [InvalidOperationException]::new($reason)
            $exception.Data['fixture_reason'] = $reason
            throw $exception
        }
    }
    function Get-FixtureHash([string]$path) {
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    function Invoke-FixtureSetup([string]$program, [string]$arguments) {
        $proc = Start-Process -FilePath $program -ArgumentList $arguments -PassThru
        try {
            Assert-ProjectSetup ($proc.WaitForExit(180000)) 'installer_timeout'
            Assert-ProjectSetup ($proc.ExitCode -eq 0) 'installer_exit'
        } finally { $proc.Dispose() }
    }
    try {
        Assert-ProjectSetup ($env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and
            $env:RUNNER_OS -ceq 'Windows' -and $env:OS -eq 'Windows_NT' -and [Environment]::Is64BitProcess) 'runner'
        $project = (Get-Item -LiteralPath $ProjectDirectory).FullName
        $work = (Get-Item -LiteralPath $WorkDirectory).FullName
        foreach ($path in $project,$work) {
            $tempRoots = @([IO.Path]::GetTempPath(),$env:RUNNER_TEMP)
            Assert-ProjectSetup (@($tempRoots | Where-Object {
                $_ -and $path.StartsWith(([IO.Path]::GetFullPath($_).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)
            }).Count -gt 0) 'fixture_outside_temporary_directory'
        }
        $receiptFile = Get-Item -LiteralPath $ReceiptPath
        $outputs = [IO.Path]::GetFullPath((Join-Path $project 'target/workflow/installers'))
        Assert-ProjectSetup ($receiptFile.Name -ceq 'installer-receipt.json' -and $receiptFile.Directory.Parent.FullName -eq $outputs -and
            $receiptFile.Directory.Name -cmatch '^installer-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$') 'receipt_location'
        $receipt = Get-Content -LiteralPath $receiptFile.FullName -Encoding UTF8 -Raw | ConvertFrom-Json
        $identity = Get-Content -LiteralPath (Join-Path $project 'contain.installer.json') -Encoding UTF8 -Raw | ConvertFrom-Json
        $appId = [guid]::Parse($identity.app_id)
        $shortId = $appId.ToString('N').Substring(0,8)
        $shortcutName = "$AssemblyName ($shortId)"
        Assert-ProjectSetup ($receipt.protocol -ceq 'contain-project-installer-receipt-1' -and $receipt.schema_version -eq 1 -and
            $appId -ne [guid]::Empty -and [guid]::Parse($receipt.app_id) -eq $appId -and $receipt.short_id -ceq $shortId -and
            $receipt.display_name -ceq $AssemblyName -and $receipt.entry -ceq "$AssemblyName.exe" -and
            $receipt.installer_file -ceq "$AssemblyName-Setup-x64.exe" -and $receipt.start_menu_shortcut -ceq "$shortcutName.lnk" -and
            $receipt.publish_rel_dir -ceq 'target/workflow/publish/win-x64' -and $receipt.publish_kind -ceq 'dotnet-self-contained-win-x64' -and
            $receipt.install_scope -ceq 'current_user_lowest_privilege') 'receipt_contract'

        $setup = Join-Path $receiptFile.Directory.FullName $receipt.installer_file
        $setupHash = Get-FixtureHash $setup
        $publish = Join-Path $project 'target/workflow/publish/win-x64'
        $entryHash = Get-FixtureHash (Join-Path $publish "$AssemblyName.exe")
        $runtimeHash = Get-FixtureHash (Join-Path $publish 'PresentationFramework.dll')
        Assert-ProjectSetup ($setupHash -ceq $receipt.installer_sha256 -and
            (Get-Item -LiteralPath $setup).Length -eq $receipt.installer_bytes -and $entryHash -ceq $receipt.entry_sha256) 'receipt_digest'
        $dir = Join-Path $work 'installed-wpf-fixture'
        $link = Join-Path ([Environment]::GetFolderPath('Programs')) "$shortcutName.lnk"
        $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $appId.ToString('B') + '_is1'
        $entry = Join-Path $dir "$AssemblyName.exe"
        $uninstaller = Join-Path $dir 'unins000.exe'
        Assert-ProjectSetup (!(Test-Path -LiteralPath $dir) -and !(Test-Path -LiteralPath $link) -and !(Test-Path -LiteralPath $key)) 'fixture_already_exists'

        $stage = 'install'
        $setupStarted = $true
        Invoke-FixtureSetup $setup ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="{0}"' -f $dir)
        $reg = Get-ItemProperty -LiteralPath $key
        Assert-ProjectSetup ($reg.DisplayName -ceq $shortcutName -and $reg.DisplayVersion -ceq $receipt.app_version -and
            $reg.InstallLocation.TrimEnd('\') -eq $dir -and $reg.UninstallString.Trim('"') -eq $uninstaller) 'registration'
        Assert-ProjectSetup ((Get-FixtureHash $entry) -ceq $entryHash -and
            (Get-FixtureHash (Join-Path $dir 'PresentationFramework.dll')) -ceq $runtimeHash) 'installed_payload'
        Assert-ProjectSetup (Test-Path -LiteralPath $link -PathType Leaf) 'shortcut_missing'
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($link)
        Assert-ProjectSetup ($shortcut.TargetPath -eq $entry -and $shortcut.WorkingDirectory -eq $dir -and !$shortcut.Arguments) 'shortcut_target'

        $stage = 'launch'
        Start-Process -FilePath $link
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            if (!$app) {
                $matching = @(Get-Process -Name $AssemblyName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $entry })
                Assert-ProjectSetup ($matching.Count -le 1) 'multiple_window_processes'
                if ($matching.Count -eq 1) { $app = $matching[0]; $null = $app.Handle }
            }
            if ($app) {
                $app.Refresh()
                Assert-ProjectSetup (!$app.HasExited) 'window_process_exited'
                if ($app.MainWindowHandle -ne [IntPtr]::Zero -and $app.MainWindowTitle -ceq $ExpectedWindowTitle -and $app.Responding) { break }
            }
            Assert-ProjectSetup ($timer.Elapsed.TotalSeconds -lt 45) 'window_not_ready'
            Start-Sleep -Milliseconds 150
        } while ($true)
        $appPid = $app.Id
        Assert-ProjectSetup ($app.CloseMainWindow() -and $app.WaitForExit(15000) -and $app.ExitCode -eq 0) 'window_close'

        $stage = 'uninstall'
        Invoke-FixtureSetup $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
        $timer.Restart()
        while ((Test-Path -LiteralPath $dir) -or (Test-Path -LiteralPath $key) -or (Test-Path -LiteralPath $link)) {
            Assert-ProjectSetup ($timer.Elapsed.TotalSeconds -lt 30) 'uninstall_residue'
            Start-Sleep -Milliseconds 150
        }
        $setupStarted = $false
        return [ordered]@{status='passed'; app_id=$appId.ToString('D'); assembly_name=$AssemblyName; app_version=$receipt.app_version
            entry=$receipt.entry; installer_sha256=$setupHash; entry_sha256=$entryHash; runtime_sha256=$runtimeHash
            window_pid=$appPid; window_title=$ExpectedWindowTitle; window_responding=$true; shortcut_launch='passed'; uninstall='passed'}
    } catch {
        $reason = if ($_.Exception.Data.Contains('fixture_reason')) { $_.Exception.Data['fixture_reason'] } else { $_.Exception.GetType().Name }
        throw "generated_wpf_${stage}_$reason"
    } finally {
        if ($app) {
            try { if (!$app.HasExited) { [void]$app.CloseMainWindow(); [void]$app.WaitForExit(5000) } } catch { }
            $app.Dispose()
        }
        if ($setupStarted -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
            try { Invoke-FixtureSetup $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' } catch { }
        }
    }
}
