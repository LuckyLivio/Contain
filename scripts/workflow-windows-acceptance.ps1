param(
    [Parameter(Mandatory)][ValidateSet('SourceMissing','Source','Package')][string]$Stage,
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$SnapshotLabel,
    [string]$Project='.', [string]$Zip, [string]$Spec, [string]$CliZip, [string]$CliSpec, [string]$Handoff
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$Cli=(Resolve-Path -LiteralPath $Cli).Path
if (Test-Path -LiteralPath $OutputDirectory) {throw 'New output directory required; prior evidence retained'}
$out=(New-Item -ItemType Directory -Path $OutputDirectory).FullName
$results=New-Object Collections.ArrayList
function Save-Json($Path,$Value) {[IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 30),(New-Object Text.UTF8Encoding $false))}
function Record-CLI([string]$Name,[string[]]$Arguments,[int]$Expected) {
    & $Cli @Arguments | Out-Host
    $actual=$LASTEXITCODE
    [void]$results.Add(@{name=$Name;expected_exit=$Expected;actual_exit=$actual;status=$(if($actual -eq $Expected){'passed'}else{'failed'})})
    return $actual
}
try {
    $platformCode=Record-CLI 'native-platform' @('platform','--report',(Join-Path $out 'platform')) 0
    if ($platformCode -ne 0) {throw 'Native Windows x64 required; architecture evidence retained'}
    if ($Stage -eq 'SourceMissing') {
        $code=Record-CLI 'actual-source-before-install' @('check','--project',$Project,'--report',(Join-Path $out 'before-install')) 1
        if ($code -eq 1) {
            $checks=(Get-Content (Join-Path $out 'before-install/report.json') -Raw | ConvertFrom-Json).checks
            if (!($checks | Where-Object {$_.status -eq 'failed' -and ($_.name -like 'tool:*' -or $_.name -eq 'selected_windows_toolchain')})) {throw 'No actual missing toolchain evidence; an unrelated error cannot satisfy this gate'}
        }
    } elseif ($Stage -eq 'Source') {
        $code=Record-CLI 'restored-source' @('check','--project',$Project,'--report',(Join-Path $out 'restored')) 0
        if ($code -ne 0) {throw 'Prerequisites not restored; build not started'}
        $savedMsvc=$env:VCToolsVersion
        $savedSdk=$env:WindowsSDKVersion
        try {
            $env:VCToolsVersion='14.99.99999'
            [void](Record-CLI 'active-msvc-conflict' @('check','--project',$Project,'--report',(Join-Path $out 'msvc-conflict')) 1)
            $conflict=Get-Content (Join-Path $out 'msvc-conflict/report.json') -Raw | ConvertFrom-Json
            if (!($conflict.checks | Where-Object {$_.name -eq 'selected_windows_toolchain' -and $_.status -eq 'failed' -and $_.detail -like '*active_msvc_conflict*'})) {throw 'Active MSVC conflict not identified'}
            $env:VCToolsVersion=$savedMsvc
            $env:WindowsSDKVersion='10.0.22621.0'
            [void](Record-CLI 'active-sdk-conflict' @('check','--project',$Project,'--report',(Join-Path $out 'sdk-conflict')) 1)
            $conflict=Get-Content (Join-Path $out 'sdk-conflict/report.json') -Raw | ConvertFrom-Json
            if (!($conflict.checks | Where-Object {$_.name -eq 'selected_windows_toolchain' -and $_.status -eq 'failed' -and $_.detail -like '*active_sdk_conflict*'})) {throw 'Active SDK conflict not identified'}
        } finally {$env:VCToolsVersion=$savedMsvc;$env:WindowsSDKVersion=$savedSdk}
        # Real build, regressions, freeze desktop + independent CLI in the same selected environment.
        [void](Record-CLI 'build-and-freeze' @('build','--project',$Project,'--execute','--delivery-dir',(Join-Path $out 'delivery'),'--report',(Join-Path $out 'build')) 0)
    } else {
        if (!$Zip -or !$Spec -or !$CliZip -or !$CliSpec -or !$Handoff) {throw 'Package stage requires desktop ZIP/spec, CLI ZIP/spec, and A handoff.json'}
        . (Join-Path $PSScriptRoot 'workflow-receiver-evidence.ps1')
        $absence=Get-WorkflowReceiverEvidence
        Save-Json (Join-Path $out 'development-tools.json') $absence
        [void]$results.Add(@{name='development-tools-absent';status=$absence.status})
        $handoffData=Get-Content -LiteralPath $Handoff -Raw | ConvertFrom-Json
        foreach ($pair in @(@($Zip,$handoffData.desktop.zip_sha256),@($Spec,$handoffData.desktop.spec_sha256),@($CliZip,$handoffData.cli.zip_sha256),@($CliSpec,$handoffData.cli.spec_sha256),@($Cli,$handoffData.cli.entry_sha256))) {
            if ((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pair[1]) {throw 'A/B transfer identity mismatch; refusing execution'}
        }
        Save-Json (Join-Path $out 'received-identity.json') $handoffData
        [void]$results.Add(@{name='A-B-same-artifacts';status='passed'})
        [void](Record-CLI 'independent-cli' @('verify','--zip',$CliZip,'--spec',$CliSpec,'--run','--work-dir',(Join-Path $out 'cli-package'),'--report',(Join-Path $out 'cli')) 0)
        [void](Record-CLI 'desktop-launch-and-tests' @('verify','--zip',$Zip,'--spec',$Spec,'--run','--work-dir',(Join-Path $out 'desktop-package'),'--report',(Join-Path $out 'desktop')) 0)
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $broken=Join-Path $out 'missing-coreclr.zip'
        Copy-Item -LiteralPath $Zip -Destination $broken
        (Get-Item -LiteralPath $broken).IsReadOnly=$false
        $archive=[IO.Compression.ZipFile]::Open($broken,[IO.Compression.ZipArchiveMode]::Update)
        try { $entry=$archive.GetEntry('coreclr.dll');if (!$entry) {throw 'Original WPF package lacks coreclr.dll'};$entry.Delete() } finally {$archive.Dispose()}
        $s=Get-Content -LiteralPath $Spec -Raw -Encoding UTF8 | ConvertFrom-Json
        $s.sha256=(Get-FileHash $broken -Algorithm SHA256).Hash.ToLowerInvariant()
        $brokenSpec=Join-Path $out 'broken-spec.json';Save-Json $brokenSpec $s
        [void](Record-CLI 'missing-coreclr-rejected' @('verify','--zip',$broken,'--spec',$brokenSpec,'--run','--work-dir',(Join-Path $out 'must-not-launch'),'--report',(Join-Path $out 'broken')) 1)
        $r=Get-Content (Join-Path $out 'broken/report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if (!($r.checks | Where-Object {$_.name -eq 'static_file:coreclr.dll' -and $_.status -eq 'failed'}) -or ($r.checks | Where-Object {$_.name -eq 'launch'}).status -ne 'not_run' -or (Test-Path (Join-Path $out 'must-not-launch'))) {throw 'Broken package did not fail before execution'}
    }
} catch {
    [void]$results.Add(@{name='orchestration';status='failed';detail='Cannot complete acceptance; local failure retained; no environment values serialized'})
    Write-Warning $_.Exception.Message
} finally {
    $expected=@('native-platform')
    if ($Stage -eq 'Package') {$expected+=@('development-tools-absent','A-B-same-artifacts','independent-cli','desktop-launch-and-tests','missing-coreclr-rejected')} elseif ($Stage -eq 'Source') {$expected+=@('restored-source','active-msvc-conflict','active-sdk-conflict','build-and-freeze')} else {$expected+=@('actual-source-before-install')}
    foreach ($name in $expected) {if (!($results | Where-Object {$_.name -eq $name})) {[void]$results.Add(@{name=$name;status='not_run'})}}
    $failed=[bool]($results | Where-Object {$_.status -ne 'passed'} | Select-Object -First 1)
    Save-Json (Join-Path $out 'acceptance.json') @{schema_version=1;stage=$Stage;snapshot_label=$SnapshotLabel;snapshot_provenance='operator-supplied; tool cannot prove image cleanliness';status=$(if($failed){'failed'}else{'passed'});checks=@($results)}
}
if ($failed) {exit 1}
