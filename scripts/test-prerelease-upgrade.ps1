[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$HarnessPath,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+-beta\.[0-9]+$')][string]$BaselineVersion,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+-beta\.[0-9]+$')][string]$CandidateVersion,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{64}$')][string]$BaselineSetupSha256
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Choose a new evidence directory' }
$out = (New-Item -ItemType Directory -Path $EvidenceDirectory).FullName
$expectedFileVersion = $CandidateVersion.Replace('-beta.', '.')
$expectedProductVersion = '^' + [regex]::Escape($CandidateVersion) + '(?:\+[0-9A-Za-z.-]+)?$'
$recordIds = @('synthetic-finished', 'synthetic-historical', 'synthetic-interrupted')
$eventId = 'synthetic-finished-event-0'
$checks = [ordered]@{runner='not_run'; identity='not_run'; baseline_install='not_run'; baseline_records='not_run'
    in_place_upgrade='not_run'; default_library_preserved='not_run'; upgraded_records='not_run'; uninstall='not_run'}
$receipt = [ordered]@{
    protocol='contain-prerelease-upgrade-1'; schema_version=1; status='failed'
    source_sha=$env:CANDIDATE_SOURCE_SHA; workflow_sha=($env:GITHUB_WORKFLOW_SHA ?? $env:ACCEPTANCE_WORKFLOW_SHA)
    run_id=$env:GITHUB_RUN_ID; attempt=$env:GITHUB_RUN_ATTEMPT; baseline_version=$BaselineVersion; version=$CandidateVersion
    baseline_setup_sha256=$BaselineSetupSha256; setup_sha256=$null; checksums_sha256=$null; records_sha256=$null
    record_ids=$recordIds; event_id=$eventId; installed_versions=[ordered]@{}; checks=$checks
}

function Require($condition, $message) {
    if (!$condition) {
        $problem = [InvalidOperationException]::new($message)
        $problem.Data['gate_line'] = (Get-PSCallStack)[1].ScriptLineNumber
        throw $problem
    }
}
function Sha($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Install-OrUninstall($exe) {
    $process = Start-Process -FilePath $exe -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-' -PassThru
    try {
        $null = $process.Handle
        Require ($process.WaitForExit(180000) -and $process.ExitCode -eq 0) 'Installer did not complete normally'
    } finally { $process.Dispose() }
}
function Find-Registrations {
    foreach ($scope in 'HKCU:', 'HKLM:') {
        foreach ($software in 'Software', 'Software\WOW6432Node') {
            $uninstall = "$scope\$software\Microsoft\Windows\CurrentVersion\Uninstall"
            if (Test-Path -LiteralPath $uninstall) {
                Get-ChildItem -LiteralPath $uninstall | Where-Object { $_.GetValue('DisplayName') -ceq 'Contain' }
            }
        }
    }
}
function Check-Installed($version) {
    $registration = Get-ItemProperty -LiteralPath $registry
    Require ($registration.DisplayName -ceq 'Contain' -and $registration.DisplayVersion -ceq $version -and
        $registration.InstallLocation.TrimEnd('\') -eq $install -and $registration.UninstallString.Trim('"') -eq $uninstaller -and
        @(Find-Registrations).Count -eq 1) 'Expected exactly one current-user Contain installation at the original location'
    $reply = & (Join-Path $install 'contain-workflow.exe') --version
    Require ($LASTEXITCODE -eq 0 -and $reply -ceq "contain-workflow $version" -and (Test-Path -LiteralPath $shortcut)) 'Installed product version or shortcut mismatch'
}
function Read-OldRecords($database) {
    $core = Join-Path $install 'contain.exe'
    $lines = & $core --read-only --db $database list --details --json
    Require ($LASTEXITCODE -eq 0) 'Installed core failed reading the existing library'
    $list = ($lines -join "`n") | ConvertFrom-Json
    Require ($list.kind -ceq 'list_details' -and $list.schema_version -eq 3 -and
        !(Compare-Object $recordIds @($list.data.id) -CaseSensitive) -and
        @($list.data | Where-Object name -cne 'SYNTHETIC 同名安装记录').Count -eq 0) 'Persisted records or fixture-specific names differ'
    $lines = & $core --read-only --db $database explain $eventId --json
    Require ($LASTEXITCODE -eq 0) 'Installed core failed reading the existing event'
    $explanation = ($lines -join "`n") | ConvertFrom-Json
    Require ($explanation.kind -ceq 'explain' -and $explanation.data.app_id -ceq 'synthetic-finished' -and
        $explanation.data.event.id -ceq $eventId) 'Existing event evidence differs'
}

$gate = 'runner'
try {
    Require ($IsWindows -and $env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and
        [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64' -and [Environment]::Is64BitProcess -and
        $receipt.source_sha -cmatch '^[0-9a-f]{40}$' -and $receipt.workflow_sha -cmatch '^[0-9a-f]{40}$') 'Expected a GitHub-hosted Windows x64 acceptance job'
    $scratch = (New-Item -ItemType Directory -Path (Join-Path $env:RUNNER_TEMP ("contain-upgrade-" + [guid]::NewGuid().ToString('N')))).FullName
    $personal = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Contain'
    $defaultDb = Join-Path $personal 'Records/contain.db'
    Require (!(Test-Path -LiteralPath $personal)) 'Hosted runner must have an unused personal-library location'
    $harness = (Get-Item -LiteralPath $HarnessPath).FullName
    $install = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/Contain'))
    $uninstaller = Join-Path $install 'unins000.exe'
    $registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}_is1'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Contain.lnk'
    Require (!(Test-Path $install) -and !(Test-Path $registry) -and !(Test-Path $shortcut) -and
        @(Find-Registrations).Count -eq 0) 'A clean current-user Contain installation slot is required'
    $checks[$gate] = 'passed'

    $gate = 'identity'
    $oldSetup = Join-Path $scratch 'baseline-setup.exe'
    Invoke-WebRequest "https://github.com/LuckyLivio/Contain/releases/download/v$BaselineVersion/Contain-Setup-x64.exe" -OutFile $oldSetup
    Require ((Sha $oldSetup) -ceq $BaselineSetupSha256) 'Public baseline Setup digest mismatch'
    $newSetup = Join-Path $CandidateDirectory 'Contain-Setup-x64.exe'
    $manifest = Join-Path $CandidateDirectory 'SHA256SUMS.txt'
    $setupSha = Sha $newSetup
    $identity = Get-Content -LiteralPath (Join-Path $CandidateDirectory 'installer-receipt.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $desktop = Get-Content -LiteralPath (Join-Path $CandidateDirectory 'desktop-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Require ($identity.source_sha -ceq $receipt.source_sha -and $desktop.source_sha -ceq $receipt.source_sha -and
        $identity.app_version -ceq $CandidateVersion -and $identity.installer_sha256 -ceq $setupSha -and
        @(Get-Content -LiteralPath $manifest | Where-Object { $_ -ceq "$setupSha  Contain-Setup-x64.exe" }).Count -eq 1) 'Candidate Setup identity mismatch'
    $receipt.setup_sha256 = $setupSha
    $receipt.checksums_sha256 = Sha $manifest
    $checks[$gate] = 'passed'

    $gate = 'baseline_install'
    Install-OrUninstall $oldSetup
    Check-Installed $BaselineVersion
    $checks[$gate] = 'passed'

    $gate = 'baseline_records'
    [IO.File]::WriteAllText((Join-Path $scratch '.contain-records-test-root'), 'Contain isolated record library test data')
    $database = Join-Path $scratch 'Contain/Records/contain.db'
    & $harness --upgrade-records $install $scratch seed
    Require ($LASTEXITCODE -eq 0) 'Baseline desktop could not open the marked personal records'
    $receipt.records_sha256 = Sha $database
    Require ($receipt.records_sha256 -cne (Sha (Join-Path $install 'samples/viewer-demo.db'))) 'Expected a customized personal library'
    Read-OldRecords $database
    Require ((Sha $database) -ceq $receipt.records_sha256) 'Baseline read changed the database'
    Require (!(Test-Path -LiteralPath $personal)) 'Refusing to replace existing personal records'
    New-Item -ItemType Directory -Path (Split-Path $defaultDb -Parent) | Out-Null
    [IO.File]::Copy($database, $defaultDb, $false)
    Read-OldRecords $defaultDb
    Require ((Sha $defaultDb) -ceq $receipt.records_sha256) 'Pre-upgrade personal library differs from the synthetic fixture'
    $checks[$gate] = 'passed'

    $gate = 'in_place_upgrade'
    Install-OrUninstall $newSetup
    Check-Installed $CandidateVersion
    foreach ($name in 'Contain.Viewer.exe','Contain.Viewer.dll','Contain.Desktop.Model.dll') {
        $binary = Join-Path $install $name
        $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
        $fileVersion = '{0}.{1}.{2}.{3}' -f $metadata.FileMajorPart,$metadata.FileMinorPart,$metadata.FileBuildPart,$metadata.FilePrivatePart
        Require ($fileVersion -ceq $expectedFileVersion -and $metadata.ProductVersion -cmatch $expectedProductVersion) 'Installed file or informational product version mismatch'
        $versionRow = [ordered]@{file_version=$fileVersion; product_version=$metadata.ProductVersion}
        if ($name.EndsWith('.dll')) {
            $versionRow.assembly_version = [Reflection.AssemblyName]::GetAssemblyName($binary).Version.ToString()
            Require ($versionRow.assembly_version -ceq $expectedFileVersion) 'Installed managed assembly version mismatch'
        }
        $receipt.installed_versions[$name] = $versionRow
    }
    Require ((Sha (Join-Path $install 'Contain.Viewer.exe')) -ceq $desktop.entry_sha256 -and
        (Sha (Join-Path $install 'contain-workflow.exe')) -ceq $desktop.workflow_cli_sha256 -and
        (Sha (Join-Path $install 'contain.exe')) -ceq $desktop.core_sha256 -and
        (Sha $database) -ceq $receipt.records_sha256) 'Upgrade payload differs or existing library changed'
    $checks[$gate] = 'passed'

    $gate = 'default_library_preserved'
    Require ((Sha $defaultDb) -ceq $receipt.records_sha256) 'Installer changed the default personal library'
    $checks[$gate] = 'passed'
    $gate = 'upgraded_records'
    & $harness --upgrade-records $install $scratch read-default
    Require ($LASTEXITCODE -eq 0) 'Upgraded desktop could not open existing personal records'
    Read-OldRecords $defaultDb
    Require ((Sha $defaultDb) -ceq $receipt.records_sha256) 'Readback changed the existing database'
    $checks[$gate] = 'passed'

    $gate = 'uninstall'
    Install-OrUninstall $uninstaller
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path $install) -or (Test-Path $registry) -or (Test-Path $shortcut)) {
        Require ($wait.Elapsed.TotalSeconds -lt 30) 'Uninstall left program files, registration or shortcut installed'
        Start-Sleep -Milliseconds 200
    }
    Require (@(Find-Registrations).Count -eq 0 -and (Sha $defaultDb) -ceq $receipt.records_sha256) 'Uninstall left registration or changed the personal library'
    $checks[$gate] = 'passed'
    $receipt.status = 'passed'
} catch {
    $checks[$gate] = 'failed'
    $reason = $_.Exception.GetType().Name
    $line = $_.InvocationInfo.ScriptLineNumber
    if ($_.Exception.Data.Contains('gate_line')) {
        $reason = $_.Exception.Message
        $line = $_.Exception.Data['gate_line']
    }
    $receipt.failure = @{gate=$gate; reason=$reason; line=$line}
    Write-Warning "Installed upgrade failed at $gate, line $line. $reason"
} finally {
    [IO.File]::WriteAllText((Join-Path $out 'prerelease-upgrade.json'), ($receipt | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
}
if ($receipt.status -ne 'passed') { exit 1 }
