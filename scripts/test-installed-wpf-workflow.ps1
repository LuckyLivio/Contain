[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$HarnessPath,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$ExpectedVersion='0.5.0-beta.2',
    [switch]$MeasureFirstPackage
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Choose a fresh evidence directory' }
$out = (New-Item -ItemType Directory -Path $EvidenceDirectory).FullName

function Sha($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Json($path) { Get-Content -LiteralPath $path -Encoding UTF8 -Raw | ConvertFrom-Json }
function Require($ok, [string]$reason) {
    if (!$ok) {
        $failure = [InvalidOperationException]::new($reason)
        $failure.Data['reason'] = $reason
        throw $failure
    }
}
function Run-Setup($file) {
    $p = Start-Process -FilePath $file -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-' -PassThru
    $null = $p.Handle
    try { Require ($p.WaitForExit(180000) -and $p.ExitCode -eq 0) 'setup_exit' } finally { $p.Dispose() }
}

$checks = [ordered]@{authenticate_candidate='not_run'; install_product='not_run'; launch_product='not_run'; select_project='not_run'
    confirm_configuration='not_run'; check_environment='not_run'; build_project='not_run'; package_project='not_run'
    install_launch_uninstall_project='not_run'; uninstall_product='not_run'}
$r = [ordered]@{schema_version=1; protocol='contain-installed-wpf-workflow-1'; status='failed'; version=$null
    source_sha=$env:CANDIDATE_SOURCE_SHA; workflow_sha=($env:GITHUB_WORKFLOW_SHA ? $env:GITHUB_WORKFLOW_SHA : $env:ACCEPTANCE_WORKFLOW_SHA)
    run_id=$env:GITHUB_RUN_ID; attempt=$env:GITHUB_RUN_ATTEMPT; windows_version=[Environment]::OSVersion.Version.ToString()
    image_os=$env:ImageOS; image_version=$env:ImageVersion; setup_sha256=$null; viewer_sha256=$null; workflow_cli_sha256=$null
    project_name=$null; assembly_name=$null; product_pid=$null; window_title=$null; chooser_count=0; generated_project=$null; checks=$checks}
$gate = 'authenticate_candidate'
try {
    Require ($IsWindows -and $env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64' -and [Environment]::Is64BitProcess -and
        $r.source_sha -cmatch '^[0-9a-f]{40}$' -and $r.workflow_sha -cmatch '^[0-9a-f]{40}$') 'hosted_windows_identity'
    . (Join-Path $PSScriptRoot 'test-installed-wpf-project-setup.ps1')
    $harness = (Get-Item -LiteralPath $HarnessPath).FullName
    $bundle = (Get-Item -LiteralPath $CandidateDirectory).FullName
    $setup = Join-Path $bundle 'Contain-Setup-x64.exe'
    $sums = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $bundle 'SHA256SUMS.txt') -Encoding utf8) {
        Require ($line -cmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]+)$') 'checksum_syntax'
        $name = $Matches[2]; $sha = $Matches[1]
        Require (!$sums.ContainsKey($name) -and (Sha (Join-Path $bundle $name)) -ceq $sha) 'checksum_mismatch'
        $sums[$name] = $sha
    }
    foreach ($name in 'Contain-Setup-x64.exe','candidate-identity.json','desktop-identity.json','installer-receipt.json') {
        Require ($sums.ContainsKey($name)) 'missing_identity'
    }
    $meta = Read-Json (Join-Path $bundle 'candidate-identity.json')
    $desktop = Read-Json (Join-Path $bundle 'desktop-identity.json')
    $installer = Read-Json (Join-Path $bundle 'installer-receipt.json')
    Require ($meta.version -ceq $ExpectedVersion -and $installer.app_version -ceq $meta.version -and
        $meta.source_sha -ceq $r.source_sha -and $installer.source_sha -ceq $r.source_sha -and $desktop.source_sha -ceq $r.source_sha -and
        $installer.installer_sha256 -ceq $sums['Contain-Setup-x64.exe'] -and
        $installer.source_zip_sha256 -ceq $desktop.zip_sha256 -and $desktop.zip_sha256 -ceq $sums["contain-$($meta.version)-windows-x64.zip"]) 'candidate_binding'
    $r.version = $meta.version
    $r.setup_sha256 = $installer.installer_sha256
    $r.viewer_sha256 = $desktop.entry_sha256
    $r.workflow_cli_sha256 = $desktop.workflow_cli_sha256
    $checks[$gate] = 'passed'

    $gate = 'install_product'
    $scratch = (New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP ('contain-installed-ui-' + [guid]::NewGuid().ToString('N')))).FullName
    $projectName = if ($MeasureFirstPackage) { 'HelloWpf' } else { 'WpfUi' + [guid]::NewGuid().ToString('N').Substring(0,10) }
    $assemblyName = "${projectName}Desktop"
    $r.project_name = $projectName; $r.assembly_name = $assemblyName
    $project = if ($MeasureFirstPackage) { 'C:\ContainDemo\HelloWpf' } else { Join-Path $scratch $projectName }
    Require (!(Test-Path -LiteralPath $project)) 'fixture_already_exists'
    & dotnet new wpf --name $projectName --output $project --framework net10.0 --no-restore > (Join-Path $scratch 'template.log') 2>&1
    Require ($LASTEXITCODE -eq 0) 'wpf_template'
    $csproj = Join-Path $project "$projectName.csproj"
    [xml]$xml = Get-Content -LiteralPath $csproj -Raw
    $assembly = $xml.CreateElement('AssemblyName')
    $assembly.InnerText = $assemblyName
    [void]$xml.SelectSingleNode('/Project/PropertyGroup[UseWPF]').AppendChild($assembly)
    $xml.Save($csproj)
    $csprojHash = Sha $csproj
    [xml]$xaml = Get-Content -LiteralPath (Join-Path $project 'MainWindow.xaml') -Raw
    $expectedTitle = $xaml.DocumentElement.GetAttribute('Title')
    Require ($expectedTitle -ceq 'MainWindow') 'fixture_window_title'
    $install = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/Contain'))
    $reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}_is1'
    $link = Join-Path ([Environment]::GetFolderPath('Programs')) 'Contain.lnk'
    Require (!(Test-Path -LiteralPath $install) -and !(Test-Path -LiteralPath $reg) -and !(Test-Path -LiteralPath $link)) 'previous_product_present'
    Run-Setup $setup
    $registration = Get-ItemProperty -LiteralPath $reg
    $viewer = Join-Path $install 'Contain.Viewer.exe'
    $uninstaller = Join-Path $install 'unins000.exe'
    Require ($registration.DisplayVersion -ceq $r.version -and $registration.InstallLocation.TrimEnd('\') -eq $install -and
        $registration.UninstallString.Trim('"') -eq $uninstaller -and (Sha $viewer) -ceq $r.viewer_sha256 -and
        (Sha (Join-Path $install 'contain-workflow.exe')) -ceq $r.workflow_cli_sha256) 'installed_identity'
    $checks[$gate] = 'passed'

    $gate = 'launch_product'
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    Require ((Test-Path -LiteralPath $link -PathType Leaf) -and $shortcut.TargetPath -eq $viewer -and !$shortcut.Arguments) 'product_shortcut'
    Require (!@(Get-Process -Name 'Contain.Viewer' -ErrorAction SilentlyContinue).Count) 'unexpected_running_viewer'
    $priorModulePath = $env:PSModulePath
    try {
        # The packaged workflow invokes Windows PowerShell 5.1 for signing/toolchain metadata.
        $env:PSModulePath = $env:CONTAIN_WINDOWS_POWERSHELL_MODULE_PATH
        Start-Process -FilePath $link
    } finally { $env:PSModulePath = $priorModulePath }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $owned = @(Get-Process -Name 'Contain.Viewer' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $viewer })
        Require ($owned.Count -le 1) 'ambiguous_viewer'
        if ($owned.Count -eq 1 -and $owned[0].MainWindowHandle -ne [IntPtr]::Zero -and
            $owned[0].MainWindowTitle -ceq "Contain · $($r.version)" -and $owned[0].Responding) { break }
        Require ($timer.Elapsed.TotalSeconds -lt 45) 'viewer_not_ready'
        Start-Sleep -Milliseconds 200
    } while ($true)
    $r.product_pid = $owned[0].Id; $r.window_title = $owned[0].MainWindowTitle
    $uiFile = Join-Path $scratch 'ui.json'
    $measurementArgs = @()
    if ($MeasureFirstPackage) { $measurementArgs += $out }
    & $harness --installed-wpf $install $project $assemblyName $r.product_pid $uiFile @measurementArgs > (Join-Path $scratch 'ui.log') 2>&1
    $uiCode = $LASTEXITCODE
    Require (Test-Path -LiteralPath $uiFile -PathType Leaf) 'ui_receipt_missing'
    $ui = Read-Json $uiFile
    foreach ($key in 'launch_product','select_project','confirm_configuration','check_environment','build_project','package_project') {
        $gate = $key
        $value = $ui.checks.$key
        Require ($value -ceq 'passed') "ui_$key"
        $checks[$key] = 'passed'
    }
    Require ($uiCode -eq 0 -and $ui.status -ceq 'passed' -and $ui.product_pid -eq $r.product_pid -and $ui.chooser_count -eq 1 -and
        (Sha $csproj) -ceq $csprojHash) 'ui_identity_or_source_changed'
    $r.chooser_count = $ui.chooser_count

    $gate = 'install_launch_uninstall_project'
    $generated = Join-Path $project $ui.installer_relative
    Require ((Get-Item -LiteralPath $generated).Name -ceq "$assemblyName-Setup-x64.exe") 'displayed_setup_name'
    $generatedReceipt = Join-Path (Split-Path $generated) 'installer-receipt.json'
    $r.generated_project = Test-GeneratedWpfSetup -ProjectDirectory $project -AssemblyName $assemblyName -ReceiptPath $generatedReceipt -WorkDirectory $scratch -ExpectedWindowTitle $expectedTitle
    Require ($r.generated_project.status -ceq 'passed') 'generated_setup_acceptance'
    $checks[$gate] = 'passed'

    $gate = 'uninstall_product'
    Run-Setup $uninstaller
    $timer.Restart()
    while ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $reg) -or (Test-Path -LiteralPath $link)) {
        Require ($timer.Elapsed.TotalSeconds -lt 30) 'product_uninstall_residue'
        Start-Sleep -Milliseconds 200
    }
    $checks[$gate] = 'passed'
    $r.status = 'passed'
} catch {
    $checks[$gate] = 'failed'
    $why = $_.Exception.GetType().Name
    if ($_.Exception.Data.Contains('reason')) { $why = $_.Exception.Data['reason'] }
    elseif ($_.Exception.Message -cmatch '^generated_wpf_[A-Za-z0-9_]+$') { $why = $_.Exception.Message }
    $r.failure = @{stage=$gate; reason=$why; line=$_.InvocationInfo.ScriptLineNumber}
    if ((Get-Variable -Name ui -ErrorAction SilentlyContinue) -and $ui.status -ceq 'failed') {
        $r.failure.control = $ui.control; $r.failure.ui_stage = $ui.driver_stage; $r.failure.ui_reason = $ui.reason
    }
    Write-Warning ("Installed UI test failed: " + ($r.failure | ConvertTo-Json -Compress))
} finally {
    [IO.File]::WriteAllText((Join-Path $out 'installed-wpf-workflow.json'), ($r | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
}
if ($r.status -ne 'passed') { exit 1 }
