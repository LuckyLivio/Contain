param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$PackageSha256
)
# Supplementary third-party trial only. The accepted desktop/fixture gates are
# separate. Run only on the disposable Windows CI VM, never a personal machine.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$installerUrl = 'https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe'
$installerSha = 'fd99e86f57d8f429ef5ed045bf98315af962e2309b0f1ba87a147a44c71647d0'
$installerSource = 'b88c6bc09339de32f7d18bbab900686b244f42ba'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Trial output must be new; previous attempts are retained' }
$private = Join-Path $OutputDirectory 'private'
$public = Join-Path $OutputDirectory 'public'
New-Item -ItemType Directory -Path $private,$public | Out-Null
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$core = Join-Path $PackageDirectory 'contain.exe'
$phase = 'accepted_package'
$lastStatus = $null
$captureExit = $null
$summary = [ordered]@{
    schema_version=1; protocol='contain-7zip-follow-trial-1'; source_sha=$Source
    package_sha256=$PackageSha256; supplementary_third_party=$true
    controlled_fixture=$false; synthetic=$false; accepted=$false
    maximum_native_captures=1; retries=0; windows_version=[Environment]::OSVersion.Version.ToString()
    installer=@{name='7-Zip';version='26.05';url=$installerUrl;sha256=$installerSha;source_commit=$installerSource;
        license_url=('https://github.com/ip7z/7zip/blob/'+$installerSource+'/DOC/License.txt');redistributed=$false}
    installer_arguments=@('/S'); installation_target_override=$false
    installation_before_baseline=$false; ownership_claim=$false; current_path_claim=$false
}
function Write-Json([object]$Value,[string]$Path) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 60),[Text.UTF8Encoding]::new($false))
}
function Require([bool]$Condition,[string]$Reason) { if (-not $Condition) { throw $Reason } }
function Normalized([string]$Path) { return $Path.Replace('/','\').Replace('\\?\','').ToLowerInvariant() }
function Ticks([object]$Value) {
    Require ($Value -is [string] -and $Value -match '^[0-9]+$') 'Exact decimal time string required'
    return [UInt64]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture)
}
function Run-Owned([string[]]$Arguments,[string]$Name,[int]$Timeout) {
    $quoted = $Arguments | ForEach-Object { '"' + $_.Replace('"','\"') + '"' }
    $process = Start-Process -FilePath $core -ArgumentList $quoted -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $private ($Name+'.stdout')) -RedirectStandardError (Join-Path $private ($Name+'.stderr'))
    $null = $process.Handle
    if (-not $process.WaitForExit($Timeout)) { throw 'Owned collector/query exceeded its deadline; no success inferred' }
    $process.Refresh()
    Require ($null -ne $process.ExitCode) 'Owned command exit code unavailable'
    return $process.ExitCode
}
function Read-Envelope([string]$Name,[string]$Kind) {
    $path = Join-Path $private ($Name+'.stdout')
    Require ((Get-Item -LiteralPath $path).Length -le 67108864) 'Read-only response exceeded 64 MiB'
    $doc = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Require ($doc.schema_version -eq 3 -and $doc.kind -eq $Kind) 'Unexpected read-only JSON contract'
    return $doc.data
}
try {
    Require ($env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_OS -eq 'Windows') 'This optional trial requires the disposable Windows GitHub runner'
    Require ($Source -match '^[0-9a-f]{40}$' -and $PackageSha256 -match '^[0-9a-f]{64}$') 'Invalid accepted identity'
    $build = Get-Content -LiteralPath (Join-Path $PackageDirectory 'BUILD.json') -Raw | ConvertFrom-Json
    $validation = Get-Content -LiteralPath (Join-Path $PackageDirectory 'VALIDATION.json') -Raw | ConvertFrom-Json
    Require ($build.source_sha -eq $Source -and $validation.source_sha -eq $Source -and $build.version -eq '0.3.2-beta.9') 'Accepted source/version differs'
    Require ($validation.viewer_verified -eq $true -and $validation.graphical_install_verified -eq $true) 'Actual packaged desktop installation acceptance is required before the supplementary trial'
    Require (@($validation.checks.PSObject.Properties | Where-Object { $_.Value -ne $true }).Count -eq 0) 'A mandatory package gate did not pass'
    $binary = @($build.files | Where-Object { $_.path -eq 'contain.exe' })
    Require ($binary.Count -eq 1 -and (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash.ToLowerInvariant() -eq $binary[0].sha256) 'Frozen core differs'
    $summary.core_sha256 = $binary[0].sha256
    $target = Join-Path $env:ProgramFiles '7-Zip/7z.exe'
    $summary.default_target = 'ProgramFiles/7-Zip/7z.exe'
    $summary.preexisting_target = Test-Path -LiteralPath $target -PathType Leaf
    if ($summary.preexisting_target) { $summary.preexisting_version = [Diagnostics.FileVersionInfo]::GetVersionInfo($target).FileVersion }

    $phase = 'fixed_official_installer'
    $installer = Join-Path $private '7z2605-x64.exe'
    Invoke-WebRequest -Uri $installerUrl -OutFile $installer -MaximumRedirection 5
    Require ((Get-Item -LiteralPath $installer).Length -eq 1663506 -and (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -eq $installerSha) 'Official installer bytes differ from the pinned release'
    $database = Join-Path $private 'third-party-record.db'
    $phase = 'one_default_target_installation'
    # /S preserves the installer's default target. No /D, --watch, product path
    # inference or target directory is supplied to Contain.
    $arguments = @('--db',$database,'install','--status-json','--follow-installer','--require-etw','--name','7-Zip 26.05 supplementary default-target trial','--settle-ms','1500','--max-drain-ms','10000','--evidence-quota-mib','64',$installer,'--','/S')
    $captureExit = Run-Owned $arguments 'capture' 120000
    $statuses = @(Get-Content -LiteralPath (Join-Path $private 'capture.stdout') | ForEach-Object { try { $_ | ConvertFrom-Json } catch {} } | Where-Object { $_.kind -eq 'install_status' })
    Require ($statuses.Count -gt 0) 'No structured session identity received'
    $lastStatus = $statuses[-1].data
    $summary.capture_exit_code = $captureExit
    $summary.installer_exit_code = $lastStatus.installer_exit_code
    $summary.session_id = $lastStatus.session_id
    Require ($captureExit -eq 0 -and $lastStatus.stage -eq 'finished' -and $lastStatus.capture_finished -and $lastStatus.data_saved -and $lastStatus.installer_exited) 'Third-party record did not confirm a completed saved result'
    Require ($lastStatus.installer_exit_code -eq 0) 'The pinned installer returned a nonzero result'
    Require (Test-Path -LiteralPath $target -PathType Leaf) 'Default installed executable was not readable after installation'
    $summary.post_install_version = [Diagnostics.FileVersionInfo]::GetVersionInfo($target).FileVersion
    $summary.post_install_sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    Require ($summary.post_install_version -match '^26\.0?5(?:\.| |$)') 'Default executable version was not the pinned installer version'

    $phase = 'fresh_readonly_session'
    Require ((Run-Owned @('--db',$database,'--read-only','inspect',$lastStatus.session_id,'--json') 'inspect' 30000) -eq 0) 'Fresh saved-session query failed'
    $capture = Read-Envelope 'inspect' 'inspect'
    Require ($capture.id -eq $lastStatus.session_id -and $capture.capture_state -eq 'finished' -and @($capture.watch_roots).Count -eq 0 -and @($capture.files).Count -eq 0) 'Saved follow-session identity/scope differs'
    $follow = $capture.backend.follow_installer
    Require ($follow.direct_children_only -and -not $follow.baseline_available -and $follow.candidate_dropped -eq 0) 'Follow admission mode/limits failed'
    Require ($capture.backend.etw_process -eq 'active' -and $capture.backend.etw_file -eq 'active') 'Actual source providers were unavailable'
    foreach ($field in @('etw_events_lost','etw_buffers_lost','dropped_events','decode_errors','context_losses')) { Require ($capture.backend.$field -eq 0) 'Source loss prevents third-party evidence acceptance' }
    Require ($capture.warnings -contains 'Follow-installer mode records verified source operations only; no installation-before baseline or final-file ownership is established.') 'Missing-before-baseline boundary was absent'
    $summary.follow_counters = $follow
    # Raw warning/error text can contain host paths; publish only this bounded
    # quality aggregate, leaving the original evidence in the private directory.
    $summary.quality_level = $capture.quality.level
    $summary.quality_reason_count = @($capture.quality.reasons).Count
    $rootIdentity = @($capture.processes | Where-Object { $_.pid -eq $lastStatus.installer_pid -and $_.creation_time -eq $lastStatus.installer_creation_time })
    Require ($rootIdentity.Count -eq 1 -and $rootIdentity[0].confidence -eq 'Certain' -and (Normalized $rootIdentity[0].image) -eq (Normalized $installer)) 'Owned installer process instance was not retained'
    $eligible = @()
    foreach ($event in @($capture.events)) {
        if ($event.event_type -ne 'file' -or $event.operation -ne 'write_requested' -or (Normalized $event.resource) -ne (Normalized $target)) { continue }
        if ($event.confidence -ne 'High' -or $event.dimensions.actor -ne 'High' -or $event.evidence.source -ne 'EtwFile' -or $event.success -ne $true -or $event.state_validated -ne $false -or $event.evidence.session_id -ne $capture.id -or $event.raw.resource_resolved -ne $true) { continue }
        $birth = Ticks $event.evidence.process_creation_time
        $at = Ticks $event.timestamp_ticks
        $process = @($capture.processes | Where-Object { $_.pid -eq $event.evidence.pid -and $_.creation_time -eq $event.evidence.process_creation_time })
        if ($process.Count -ne 1 -or $at -lt $birth -or ($null -ne $process[0].ended_at -and $at -gt (Ticks $process[0].ended_at)) -or (Normalized $event.evidence.process_image) -ne (Normalized $process[0].image)) { continue }
        $isRoot = $process[0].pid -eq $rootIdentity[0].pid -and $process[0].creation_time -eq $rootIdentity[0].creation_time -and $process[0].confidence -eq 'Certain' -and $event.evidence.rule -eq 'InstallerPid'
        $isDirect = $process[0].parent_pid -eq $rootIdentity[0].pid -and $process[0].parent_creation_time -eq $rootIdentity[0].creation_time -and $process[0].confidence -eq 'High' -and $event.evidence.rule -eq 'DescendantProcess'
        if ($isRoot -or $isDirect) { $eligible += $event }
    }
    Require ($eligible.Count -gt 0) 'No completed source write to default 7z.exe with verified installer/direct-child instance'
    $event = @($eligible | Sort-Object timestamp_ticks,id)[0]
    Require ($event.id -match '^[A-Za-z0-9_-]{1,100}$') 'Unexpected selected event ID'
    Require ($event.raw.file_name.current_path_verified -eq $false) 'Followed historical file names must not become current-path verification'
    $phase = 'fresh_readonly_explanation'
    Require ((Run-Owned @('--db',$database,'--read-only','explain',$event.id,'--json') 'explain' 30000) -eq 0) 'Fresh selected-event explanation failed'
    $explain = Read-Envelope 'explain' 'explain'
    Require ($explain.app_id -eq $capture.id -and ($explain.event | ConvertTo-Json -Depth 60 -Compress) -eq ($event | ConvertTo-Json -Depth 60 -Compress)) 'Reopened explanation differs from the original selected source event'
    $summary.observation = @{event_id=$event.id;operation=$event.operation;source=$event.evidence.source;confidence=$event.confidence;
        actor_confidence=$event.dimensions.actor;pid=$event.evidence.pid;creation_time=$event.evidence.process_creation_time;
        rule=$event.evidence.rule;timestamp_ticks=$event.timestamp_ticks;completion=$event.success;state_validated=$event.state_validated;
        current_path_verified=$event.raw.file_name.current_path_verified;historical_name_source=$event.raw.file_name.path_source;reopened_explanation_matches=$true}
    $summary.accepted = $true
    $summary.phase = 'verified'
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $private 'failure.txt') -Encoding utf8
    $summary.phase = $phase
    $summary.error_type = $_.Exception.GetType().Name
    $summary.capture_exit_code = $captureExit
    if ($null -ne $lastStatus) { $summary.last_status = @{stage=$lastStatus.stage;step=$lastStatus.step;installer_started=$lastStatus.installer_started;installer_exited=$lastStatus.installer_exited;capture_finished=$lastStatus.capture_finished;data_saved=$lastStatus.data_saved} }
    throw 'Supplementary 7-Zip trial did not pass; safe aggregate retained, no weaker attribution accepted'
} finally {
    Write-Json $summary (Join-Path $public '7zip-follow-trial-summary.json')
}
