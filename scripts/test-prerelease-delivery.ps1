[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Choose a new evidence directory' }
$out = (New-Item -Path $EvidenceDirectory -ItemType Directory).FullName
function File-Sha($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Json($path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Require($condition, $message) {
    if (!$condition) {
        # Only these fixed gate messages are safe to include in public diagnostics.
        $errorInfo = [InvalidOperationException]::new($message)
        $errorInfo.Data['gate_line'] = (Get-PSCallStack)[1].ScriptLineNumber
        throw $errorInfo
    }
}
function Run-Installer($path) {
    $proc = Start-Process -FilePath $path -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-' -PassThru
    try {
        Require ($proc.WaitForExit(180000)) 'Installer did not exit in time'
        Require ($proc.ExitCode -eq 0) 'Installer returned a nonzero exit code'
    } finally { $proc.Dispose() }
}

$checks = [ordered]@{runner='not_run'; identity='not_run'; cli_zip='not_run'; desktop_zip='not_run'
    install='not_run'; installed_window='not_run'; installed_sample='not_run'; uninstall='not_run'}
$receipt = [ordered]@{
    protocol='contain-prerelease-smoke-1'; schema_version=1; status='failed'
    source_sha=$env:CANDIDATE_SOURCE_SHA; workflow_sha=($env:GITHUB_WORKFLOW_SHA ? $env:GITHUB_WORKFLOW_SHA : $env:ACCEPTANCE_WORKFLOW_SHA)
    run_id=$env:GITHUB_RUN_ID; attempt=$env:GITHUB_RUN_ATTEMPT
    windows_version=[Environment]::OSVersion.Version.ToString(); image_os=$env:ImageOS; image_version=$env:ImageVersion
    runner_environment=$env:RUNNER_ENVIRONMENT; architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    version=$null; packaged_cli_probes=[ordered]@{}; checksums_sha256=$null; artifacts=[ordered]@{}; zip_checks=[ordered]@{}; checks=$checks
}
$gate = 'runner'
try {
    Require ($IsWindows -and $env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_OS -ceq 'Windows' -and
        $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and $receipt.architecture -eq 'X64' -and [Environment]::Is64BitProcess -and
        $env:ImageOS -and $env:ImageVersion -and $receipt.source_sha -cmatch '^[0-9a-f]{40}$' -and
        $receipt.workflow_sha -cmatch '^[0-9a-f]{40}$') 'A GitHub-hosted Windows x64 runner and immutable revisions are required'
    $scratch = (New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP ("contain-smoke-" + [guid]::NewGuid().ToString('N')))).FullName
    $checks[$gate] = 'passed'

    $gate = 'identity'
    $bundle = (Resolve-Path -LiteralPath $CandidateDirectory).Path
    $manifest = Join-Path $bundle 'SHA256SUMS.txt'
    $receipt.checksums_sha256 = File-Sha $manifest
    $hashes = @{}
    foreach ($line in Get-Content -LiteralPath $manifest -Encoding UTF8) {
        Require ($line -cmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]+)$') 'Invalid checksum entry'
        $name = $Matches[2]; $sha = $Matches[1]
        Require (!$hashes.ContainsKey($name) -and (File-Sha (Join-Path $bundle $name)) -ceq $sha) 'Duplicate or mismatched checksum'
        $hashes[$name] = $sha
    }
    $meta = Read-Json (Join-Path $bundle 'candidate-identity.json')
    Require ($meta.source_sha -ceq $receipt.source_sha -and $meta.version -cmatch '^[0-9]+\.[0-9]+\.[0-9]+-[0-9A-Za-z.-]+$') 'Unexpected candidate revision or version'
    $version = $meta.version
    $receipt.version = $version
    $zipNames = @{desktop="contain-$version-windows-x64.zip"; cli="contain-workflow-$version-windows-x64.zip"}
    $expected = @($zipNames.desktop,$zipNames.cli,'desktop-package-spec.json','cli-package-spec.json','desktop-identity.json','cli-identity.json',
        'Contain-Setup-x64.exe','installer-receipt.json','candidate-identity.json','handoff.json','receive-release-candidate.ps1',
        'workflow-windows-acceptance.ps1','workflow-receiver-evidence.ps1','inspect-receiver-environment.ps1')
    Require (!(Compare-Object $expected @($hashes.Keys) -CaseSensitive)) 'Candidate checksum inventory is incomplete or unexpected'
    $entries = @(Get-ChildItem -LiteralPath $bundle -Force)
    Require (!@($entries | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count -and
        !(Compare-Object ($expected + 'SHA256SUMS.txt') @($entries.Name) -CaseSensitive)) 'Candidate directory contains unexpected entries'
    $handoff = Read-Json (Join-Path $bundle 'handoff.json')
    Require ($handoff.source_sha -ceq $receipt.source_sha -and $hashes['handoff.json'] -ceq $meta.a_handoff_sha256) 'Incorrect source handoff'
    $setup = Join-Path $bundle 'Contain-Setup-x64.exe'
    $installer = Read-Json (Join-Path $bundle 'installer-receipt.json')
    Require ($installer.app_version -ceq $version -and $installer.source_sha -ceq $receipt.source_sha -and
        $installer.installer_sha256 -ceq $hashes['Contain-Setup-x64.exe'] -and
        $installer.source_spec_sha256 -ceq $hashes['desktop-package-spec.json'] -and
        $installer.source_zip_sha256 -ceq $hashes[$zipNames.desktop]) 'Installer provenance mismatch'
    foreach ($kind in 'cli','desktop') {
        $id = Read-Json (Join-Path $bundle "$kind-identity.json")
        $spec = Read-Json (Join-Path $bundle "$kind-package-spec.json")
        $bound = $handoff.$kind
        $probe = $meta.packaged_cli_probes.$kind
        Require ($probe.default -ceq 'passed' -and $probe.safe_probes -ceq 'passed' -and
            $probe.git_version_observed -cmatch '^[0-9][0-9A-Za-z._+-]+$') 'Packaged read-only probe checks did not pass'
        $receipt.packaged_cli_probes[$kind] = @{default='passed'; safe_probes='passed'; git_version_observed=$probe.git_version_observed}
        Require ($id.source_sha -ceq $receipt.source_sha -and $bound.source_sha -ceq $receipt.source_sha -and
            $id.zip_sha256 -ceq $bound.zip_sha256 -and $bound.zip_sha256 -ceq $hashes[$zipNames[$kind]] -and
            $spec.sha256 -ceq $id.zip_sha256 -and $id.spec_sha256 -ceq $bound.spec_sha256 -and
            $id.spec_sha256 -ceq $hashes["$kind-package-spec.json"] -and $id.entry_sha256 -ceq $bound.entry_sha256) 'Package identity mismatch'
    }
    foreach ($name in $expected) { $receipt.artifacts[$name] = $hashes[$name] }
    $verifier = Join-Path $scratch 'contain-workflow.exe'
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $bundle $zipNames.cli))
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('contain-workflow.exe'), $verifier) } finally { $archive.Dispose() }
    Require ((File-Sha $verifier) -ceq $handoff.cli.entry_sha256) 'Verifier digest mismatch'
    $checks[$gate] = 'passed'

    foreach ($kind in 'cli','desktop') {
        $gate = "${kind}_zip"
        $reportDir = Join-Path $scratch "$kind-report"
        $specPath = Join-Path $bundle "$kind-package-spec.json"
        $spec = Read-Json $specPath
        if ($kind -eq 'desktop') {
            Require ($spec.entry -ceq 'Contain.Viewer.exe' -and $spec.launch.window_title -ceq "Contain · $version") 'Missing desktop window contract'
            $requiredChecks = @('launch','static_contents','Core exact version','Bundled workflow CLI exact version','Synthetic sample read-only list')
        } else {
            Require ($spec.entry -ceq 'contain-workflow.exe') 'Unexpected standalone entry'
            $requiredChecks = @('launch','CLI exact version output','CLI --help exits 0')
        }
        & $verifier verify --zip (Join-Path $bundle $zipNames[$kind]) --spec $specPath --run --work-dir (Join-Path $scratch $kind) --report $reportDir | Out-Null
        Require ($LASTEXITCODE -eq 0) 'Native package verification failed'
        $report = Read-Json (Join-Path $reportDir 'report.json')
        Require ($report.workflow -ceq 'package' -and !@($report.checks | Where-Object status -ne 'passed').Count) 'Incomplete package execution'
        foreach ($name in $requiredChecks) { Require (@($report.checks | Where-Object name -CEQ $name).Count -eq 1) 'Missing required package check' }
        $receipt.zip_checks[$kind] = $requiredChecks
        $checks[$gate] = 'passed'
    }

    $gate = 'install'
    $install = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/Contain'))
    $reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}_is1'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Contain.lnk'
    Require (!(Test-Path $install) -and !(Test-Path $reg) -and !(Test-Path $shortcut)) 'Runner already contains this installation'
    Run-Installer $setup
    $install = (Get-Item -LiteralPath $install).FullName
    $registration = Get-ItemProperty -LiteralPath $reg
    Require ($registration.DisplayName -ceq 'Contain' -and $registration.DisplayVersion -ceq $version -and
        $registration.InstallLocation.TrimEnd('\') -eq $install -and (Test-Path -LiteralPath $shortcut)) 'Current-user installation or version missing'
    $viewer = Join-Path $install 'Contain.Viewer.exe'
    $core = Join-Path $install 'contain.exe'
    $workflow = Join-Path $install 'contain-workflow.exe'
    $desktopId = Read-Json (Join-Path $bundle 'desktop-identity.json')
    Require ((File-Sha $viewer) -ceq $desktopId.entry_sha256 -and (File-Sha $core) -ceq $desktopId.core_sha256 -and
        (File-Sha $workflow) -ceq $desktopId.workflow_cli_sha256) 'Installed binaries differ from the frozen desktop package'
    $versionOutput = & $workflow --version
    Require ($LASTEXITCODE -eq 0 -and $versionOutput -ceq "contain-workflow $version") 'Installed workflow version mismatch'
    $checks[$gate] = 'passed'

    $gate = 'installed_window'
    $ui = Start-Process -FilePath $viewer -WorkingDirectory $install -PassThru
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $ui.Refresh()
            Require (!$ui.HasExited) 'Installed viewer exited before readiness'
            if ($ui.MainWindowHandle -ne [IntPtr]::Zero -and $ui.MainWindowTitle -ceq "Contain · $version" -and $ui.Responding) { break }
            Require ($timer.Elapsed.TotalSeconds -lt 45) 'Installed viewer did not expose the expected responsive window'
            Start-Sleep -Milliseconds 200
        } while ($true)
        Require ($ui.CloseMainWindow() -and $ui.WaitForExit(15000) -and $ui.ExitCode -eq 0) 'Installed viewer did not close normally'
    } finally { $ui.Dispose() }
    $checks[$gate] = 'passed'

    $gate = 'installed_sample'
    $sample = Join-Path $scratch 'viewer-demo.db'
    Copy-Item -LiteralPath (Join-Path $install 'samples/viewer-demo.db') -Destination $sample
    $sampleSha = File-Sha $sample
    Require ($sampleSha -ceq (File-Sha (Join-Path $scratch 'desktop/samples/viewer-demo.db'))) 'Installed sample differs from the ZIP'
    $rows = & $core --read-only --db $sample list --details --json
    Require ($LASTEXITCODE -eq 0) 'Installed core could not read synthetic sample'
    $list = ($rows -join "`n") | ConvertFrom-Json
    Require ($list.kind -ceq 'list_details' -and $list.schema_version -eq 3 -and
        !(Compare-Object @('synthetic-finished','synthetic-historical','synthetic-interrupted') @($list.data.id) -CaseSensitive)) 'Unexpected synthetic sessions'
    $eventId = 'synthetic-finished-event-0'
    $rows = & $core --read-only --db $sample explain $eventId --json
    Require ($LASTEXITCODE -eq 0) 'Synthetic event explain failed'
    $event = ($rows -join "`n") | ConvertFrom-Json
    Require ($event.kind -ceq 'explain' -and $event.data.app_id -ceq 'synthetic-finished' -and
        $event.data.event.id -ceq $eventId -and (File-Sha $sample) -ceq $sampleSha) 'Synthetic event verification failed'
    $checks[$gate] = 'passed'

    $gate = 'uninstall'
    $uninstaller = Join-Path $install 'unins000.exe'
    Require ($registration.UninstallString.Trim('"') -eq $uninstaller) 'Unexpected registered uninstall command'
    Run-Installer $uninstaller
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath $install) -or (Test-Path -LiteralPath $reg) -or (Test-Path -LiteralPath $shortcut)) {
        Require ($timer.Elapsed.TotalSeconds -lt 30) 'Installation files, shortcut or registration remain'
        Start-Sleep -Milliseconds 200
    }
    $checks[$gate] = 'passed'
    $receipt.status = 'passed'
} catch {
    $checks[$gate] = 'failed'
    $failure = $_
    $reason = $failure.Exception.GetType().Name
    $line = $failure.InvocationInfo.ScriptLineNumber
    if ($failure.Exception.Data.Contains('gate_line')) {
        $reason = $failure.Exception.Message
        $line = $failure.Exception.Data['gate_line']
    }
    $receipt.failure = @{gate=$gate; line=$line; reason=$reason}
    Write-Warning "Prerelease smoke failed at $gate, line $line. $reason"
} finally {
    [IO.File]::WriteAllText((Join-Path $out 'prerelease-smoke.json'), ($receipt | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
}
if ($receipt.status -ne 'passed') { exit 1 }
