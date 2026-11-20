param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedChecksumsSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$SnapshotLabel,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bundle = $PSScriptRoot
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory to preserve earlier receipts' }
$out = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
function Read-Json($Path) { Get-Content -LiteralPath $Path -Encoding UTF8 -Raw | ConvertFrom-Json }
function File-Sha($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$checks = [ordered]@{}
foreach ($name in @('native-platform','development-tools-absent','A-B-same-artifacts','independent-cli',
                     'desktop-launch-and-tests','missing-coreclr-rejected')) { $checks[$name] = 'not_run' }
$receipt = [ordered]@{
    schema_version=1; protocol='contain-candidate-receiver-1'; snapshot_label=$SnapshotLabel
    snapshot_provenance='operator-supplied'; status='failed'; checksums_sha256=$ExpectedChecksumsSha256.ToLowerInvariant()
    source_sha=$null; artifacts=$null; network_before='not_run'; network_after='not_run'; checks=$checks
}
try {
    # Authenticate the manifest using the digest received independently from A.
    $manifest = Join-Path $bundle 'SHA256SUMS.txt'
    if ((File-Sha $manifest) -ne $ExpectedChecksumsSha256) { throw 'SHA256SUMS differs from A' }
    $hashes = @{}
    foreach ($line in (Get-Content -LiteralPath $manifest -Encoding UTF8)) {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]+)$') { throw 'Invalid SHA256SUMS entry' }
        $file = $Matches[2]; $sha = $Matches[1]
        if ($hashes.ContainsKey($file) -or (File-Sha (Join-Path $bundle $file)) -ne $sha) { throw 'Candidate file hash mismatch or duplicate' }
        $hashes[$file] = $sha
    }
    $meta = Read-Json (Join-Path $bundle 'candidate-identity.json')
    if ($meta.version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$' -or $meta.source_sha -cnotmatch '^[0-9a-f]{40}$') {
        throw 'Invalid candidate version or source SHA'
    }
    $desktopName = "contain-$($meta.version)-windows-x64.zip"
    $cliName = "contain-workflow-$($meta.version)-windows-x64.zip"
    $expected = @($desktopName, $cliName, 'desktop-package-spec.json', 'cli-package-spec.json',
        'desktop-identity.json', 'cli-identity.json', 'Contain-Setup-x64.exe', 'installer-receipt.json',
        'candidate-identity.json', 'handoff.json', 'receive-release-candidate.ps1',
        'workflow-windows-acceptance.ps1', 'workflow-receiver-evidence.ps1', 'inspect-receiver-environment.ps1')
    if (Compare-Object ($hashes.Keys | Sort-Object) ($expected | Sort-Object) -CaseSensitive) { throw 'Incomplete or unexpected candidate manifest' }
    $handoffPath = Join-Path $bundle 'handoff.json'
    $handoff = Read-Json $handoffPath
    if ($meta.a_handoff_sha256 -ne $hashes['handoff.json'] -or $handoff.source_sha -ne $meta.source_sha) { throw 'A handoff identity mismatch' }
    foreach ($kind in @('desktop','cli')) {
        $archiveName = if ($kind -eq 'desktop') { $desktopName } else { $cliName }
        $identity = Read-Json (Join-Path $bundle "$kind-identity.json")
        $spec = Read-Json (Join-Path $bundle "$kind-package-spec.json")
        $bound = $handoff.$kind
        if ($identity.source_sha -ne $meta.source_sha -or $bound.source_sha -ne $meta.source_sha -or
            $identity.zip_sha256 -ne $bound.zip_sha256 -or $bound.zip_sha256 -ne $hashes[$archiveName] -or
            $spec.sha256 -ne $bound.zip_sha256 -or $identity.spec_sha256 -ne $bound.spec_sha256 -or
            $bound.spec_sha256 -ne $hashes["$kind-package-spec.json"] -or $identity.entry_sha256 -ne $bound.entry_sha256) {
            throw 'Candidate ZIP/spec identity mismatch'
        }
    }
    $receipt.source_sha = $meta.source_sha
    $receipt.artifacts = [ordered]@{handoff=$hashes['handoff.json']; desktop_zip=$hashes[$desktopName]; cli_zip=$hashes[$cliName]
        desktop_spec=$handoff.desktop.spec_sha256; cli_spec=$handoff.cli.spec_sha256; setup=$hashes['Contain-Setup-x64.exe']}
    . (Join-Path $PSScriptRoot 'inspect-receiver-environment.ps1')
    $receipt.network_before = Get-ReceiverNetworkState
    if ($receipt.network_before -ne 'offline') { throw 'Receiver interfaces must be disconnected before verification' }

    $cliZip = Join-Path $bundle $cliName
    $cli = Join-Path $out 'contain-workflow.exe'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($cliZip)
    try {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry('contain-workflow.exe'), $cli)
    } finally { $archive.Dispose() }
    if ((File-Sha $cli) -ne $handoff.cli.entry_sha256) { throw 'Standalone verifier hash mismatch' }
    $stage = Join-Path $out 'package'
    $shell = (Get-Process -Id $PID).Path
    & $shell -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'workflow-windows-acceptance.ps1') -Stage Package `
        -SnapshotLabel $SnapshotLabel -Cli $cli -Zip (Join-Path $bundle $desktopName) -Spec (Join-Path $bundle 'desktop-package-spec.json') `
        -CliZip $cliZip -CliSpec (Join-Path $bundle 'cli-package-spec.json') -Handoff $handoffPath -OutputDirectory $stage
    $exitCode = $LASTEXITCODE
    $receipt.network_after = Get-ReceiverNetworkState
    $result = Read-Json (Join-Path $stage 'acceptance.json')
    if ($result.stage -ne 'Package' -or $result.snapshot_label -cne $SnapshotLabel) { throw 'Unexpected acceptance receipt' }
    foreach ($name in @($checks.Keys)) {
        $row = @($result.checks | Where-Object { $_.name -eq $name })
        if ($row.Count -ne 1 -or $row[0].status -notin @('passed','failed','not_run')) { throw 'Invalid package gate in receipt' }
        $checks[$name] = $row[0].status
    }
    if ($exitCode -ne 0 -or $result.status -ne 'passed' -or ($checks.Values -ne 'passed') -or $receipt.network_after -ne 'offline') {
        throw 'B verification did not pass all package and offline checks'
    }
    $receipt.status = 'passed'
} catch {
    Write-Warning $_.Exception.Message
} finally {
    $path = Join-Path $out 'B-receipt.json'
    [IO.File]::WriteAllText($path, ($receipt | ConvertTo-Json -Depth 8) + "`n", (New-Object Text.UTF8Encoding $false))
    Write-Output $path
}
if ($receipt.status -ne 'passed') { exit 1 }
