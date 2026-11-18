param(
    [Parameter(Mandatory = $true)]
    [string]$Zip,
    [string]$Spec,
    [string]$Identity,
    [string]$ExpectedZipSha256,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$OutputBaseFilename = 'Contain-Setup-x64',
    [string]$AppVersionOverride,
    [string]$VersionInfoOverride,
    [switch]$UpgradeRehearsal,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path $PSScriptRoot -Parent
$issSource = Join-Path $repoRoot 'installer\contain.iss'
$islSource = Join-Path $repoRoot 'installer\ChineseSimplified.isl'

if (!(Test-Path -LiteralPath $issSource -PathType Leaf) -or !(Test-Path -LiteralPath $islSource -PathType Leaf)) {
    throw "Missing installer definition files under '$repoRoot\installer'."
}

if ([string]::IsNullOrWhiteSpace($Zip) -or !(Test-Path -LiteralPath $Zip -PathType Leaf)) {
    throw "Input candidate ZIP was not found: '$Zip'. Existing files were left untouched."
}
$fullZip = (Resolve-Path -LiteralPath $Zip).Path
$zipDir = Split-Path $fullZip -Parent

if ([string]::IsNullOrWhiteSpace($Spec)) {
    $Spec = Join-Path $zipDir 'package-spec.json'
}
if (!(Test-Path -LiteralPath $Spec -PathType Leaf)) {
    throw "Companion package-spec.json was not found: '$Spec'. Input ZIP '$fullZip' was left untouched."
}
$fullSpec = (Resolve-Path -LiteralPath $Spec).Path

$fullIdentity = $null
if (![string]::IsNullOrWhiteSpace($Identity)) {
    if (!(Test-Path -LiteralPath $Identity -PathType Leaf)) {
        throw "Specified identity.json was not found: '$Identity'. Existing files were left untouched."
    }
    $fullIdentity = (Resolve-Path -LiteralPath $Identity).Path
} else {
    $defaultIdentity = Join-Path $zipDir 'identity.json'
    if (Test-Path -LiteralPath $defaultIdentity -PathType Leaf) {
        $fullIdentity = (Resolve-Path -LiteralPath $defaultIdentity).Path
    }
}

$fullOut = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $fullOut) {
    throw "OutputDirectory already exists: '$fullOut'. Choose a new directory so previous outputs are preserved."
}

function Resolve-IsccExecutable([string]$ExplicitPath) {
    if (![string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (!(Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "Specified ISCC.exe does not exist: '$ExplicitPath'"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $candidates = @()
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { $candidates += $cmd.Source }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe') }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $c).Path
        }
    }

    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install JRSoftware.InnoSetup (6.x) or pass -IsccPath.'
}

function Convert-ToFourPartVersion([string]$SemVer) {
    $core = ($SemVer -split '[-+]')[0]
    $parts = @($core -split '\.')
    $nums = @(0, 0, 0, 0)
    for ($i = 0; $i -lt [Math]::Min(4, $parts.Count); $i++) {
        $parsed = 0
        if ([int]::TryParse($parts[$i], [ref]$parsed) -and $parsed -ge 0 -and $parsed -le 65535) {
            $nums[$i] = $parsed
        }
    }
    if ($parts.Count -lt 4 -and $SemVer -match '-[A-Za-z]+\.([0-9]+)') {
        $preNum = 0
        if ([int]::TryParse($Matches[1], [ref]$preNum) -and $preNum -ge 0 -and $preNum -le 65535) {
            $nums[3] = $preNum
        }
    }
    return ($nums -join '.')
}

function Resolve-InnoSetupVersion([string]$IsccExePath) {
    $innoDir = Split-Path $IsccExePath -Parent
    $roots = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($r in $roots) {
        $entry = Get-ItemProperty -Path $r -ErrorAction SilentlyContinue | Where-Object {
            $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'Inno Setup*' -and
            $_.PSObject.Properties['DisplayVersion'] -and ![string]::IsNullOrWhiteSpace($_.DisplayVersion)
        } | Select-Object -First 1
        if ($entry) { return [string]$entry.DisplayVersion }
    }
    $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($IsccExePath).ProductVersion
    if (![string]::IsNullOrWhiteSpace($fvi) -and $fvi -ne '0.0.0.0') { return $fvi.Trim() }
    return '6.x'
}

$isccExe = Resolve-IsccExecutable $IsccPath
$isccVersion = Resolve-InnoSetupVersion $isccExe

# 1. Recompute input hashes before creating output directory
$actualZipSha = (Get-FileHash -LiteralPath $fullZip -Algorithm SHA256).Hash.ToLowerInvariant()
$actualSpecSha = (Get-FileHash -LiteralPath $fullSpec -Algorithm SHA256).Hash.ToLowerInvariant()

$specObj = Get-Content -LiteralPath $fullSpec -Raw -Encoding UTF8 | ConvertFrom-Json
if ($specObj.schema_version -ne 1 -or $specObj.target -ne 'windows-x64') {
    throw "Unsupported package-spec.json schema/target ($($specObj.schema_version) / $($specObj.target)). Input files retained."
}

$specDeclaredZipSha = ([string]$specObj.sha256).ToLowerInvariant()
if ($actualZipSha -ne $specDeclaredZipSha) {
    throw "ZIP SHA-256 mismatch against package-spec.json! Actual ZIP: $actualZipSha ; package-spec.json expected: $specDeclaredZipSha . Original files retained untouched."
}

if (![string]::IsNullOrWhiteSpace($ExpectedZipSha256)) {
    $normExpected = $ExpectedZipSha256.Trim().ToLowerInvariant()
    if ($actualZipSha -ne $normExpected) {
        throw "ZIP SHA-256 mismatch against -ExpectedZipSha256! Actual ZIP: $actualZipSha ; Expected parameter: $normExpected . Original files retained untouched."
    }
}

if ($fullIdentity) {
    $idObj = Get-Content -LiteralPath $fullIdentity -Raw -Encoding UTF8 | ConvertFrom-Json
    $idZipSha = ([string]$idObj.zip_sha256).ToLowerInvariant()
    if ($idZipSha -and $actualZipSha -ne $idZipSha) {
        throw "ZIP SHA-256 mismatch against identity.json! Actual ZIP: $actualZipSha ; identity.json zip_sha256: $idZipSha . Original files retained untouched."
    }
}

# 2. Extract into a brand-new staging directory and verify every member against CONTENTS.json & contain-workflow verify
New-Item -ItemType Directory -Path $fullOut | Out-Null
$stagingRoot = Join-Path $fullOut '_staging'
$payloadDir = Join-Path $stagingRoot 'payload'
$issStageDir = Join-Path $stagingRoot 'iss'
New-Item -ItemType Directory -Path $issStageDir | Out-Null

try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($fullZip, $payloadDir)

    $buildJsonPath = Join-Path $payloadDir 'BUILD.json'
    $contentsJsonPath = Join-Path $payloadDir 'CONTENTS.json'
    $bundledWorkflowCli = Join-Path $payloadDir 'contain-workflow.exe'
    foreach ($reqPath in @($buildJsonPath, $contentsJsonPath, $bundledWorkflowCli, (Join-Path $payloadDir 'Contain.Viewer.exe'), (Join-Path $payloadDir 'workflow\select-workflow-toolchain.ps1'), (Join-Path $payloadDir 'samples\viewer-demo.db'), (Join-Path $payloadDir 'LICENSE'), (Join-Path $payloadDir 'THIRD-PARTY-NOTICES.md'))) {
        if (!(Test-Path -LiteralPath $reqPath -PathType Leaf)) {
            throw "Extracted payload is missing required member: '$reqPath'"
        }
    }

    $buildMeta = Get-Content -LiteralPath $buildJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($buildMeta.protocol -ne 'contain-source-desktop-package-1') {
        throw "Unexpected BUILD.json protocol '$($buildMeta.protocol)' (expected 'contain-source-desktop-package-1')."
    }

    $contentsMeta = Get-Content -LiteralPath $contentsJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $extractedFiles = @(Get-ChildItem -LiteralPath $payloadDir -Recurse -File)
    if (($contentsMeta.files.Count + 1) -ne $extractedFiles.Count) {
        throw "Extracted file count ($($extractedFiles.Count)) does not match CONTENTS.json ($($contentsMeta.files.Count) + CONTENTS.json)."
    }
    foreach ($member in $contentsMeta.files) {
        $relWin = ([string]$member.path).Replace('/', '\')
        $memberPath = Join-Path $payloadDir $relWin
        if (!(Test-Path -LiteralPath $memberPath -PathType Leaf)) {
            throw "Missing CONTENTS.json member after extraction: $($member.path)"
        }
        $fi = Get-Item -LiteralPath $memberPath
        if ([int64]$fi.Length -ne [int64]$member.bytes) {
            throw "Byte size mismatch for $($member.path): expected $($member.bytes), actual $($fi.Length)"
        }
        $hash = (Get-FileHash -LiteralPath $memberPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne ([string]$member.sha256).ToLowerInvariant()) {
            throw "SHA-256 mismatch for extracted member $($member.path)"
        }
    }

    # Static verify via bundled contain-workflow.exe (exit code 2 = static checks passed, launch/tests not_run)
    $staticVerifyDir = Join-Path $stagingRoot 'static-verify-report'
    & $bundledWorkflowCli verify --zip $fullZip --spec $fullSpec --report $staticVerifyDir | Out-Null
    $verifyExit = $LASTEXITCODE
    if ($verifyExit -ne 2 -and $verifyExit -ne 0) {
        throw "Bundled contain-workflow static verify rejected input ZIP/spec (exit code $verifyExit)."
    }

    $appVersion = if (![string]::IsNullOrWhiteSpace($AppVersionOverride)) { $AppVersionOverride.Trim() } else { [string]$buildMeta.version }
    $verInfo = if (![string]::IsNullOrWhiteSpace($VersionInfoOverride)) { $VersionInfoOverride.Trim() } else { Convert-ToFourPartVersion $appVersion }

    # Stage .iss and .isl with explicit UTF-8 BOM so Inno Setup 6 parses Unicode Chinese deterministically
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    $stagedIss = Join-Path $issStageDir 'contain.iss'
    $stagedIsl = Join-Path $issStageDir 'ChineseSimplified.isl'
    [IO.File]::WriteAllText($stagedIss, [IO.File]::ReadAllText($issSource, [Text.Encoding]::UTF8), $utf8Bom)
    [IO.File]::WriteAllText($stagedIsl, [IO.File]::ReadAllText($islSource, [Text.Encoding]::UTF8), $utf8Bom)

    $compileLog = Join-Path $fullOut 'iscc-compile.local.log'
    $isccArgs = @(
        '/Qp',
        "/DSourcePayloadDir=$payloadDir",
        "/DAppVersion=$appVersion",
        "/DVersionInfoVersion=$verInfo",
        "/DOutputDir=$fullOut",
        "/DOutputBaseFilename=$OutputBaseFilename",
        $stagedIss
    )
    function Quote-WinArg([string]$Value) {
        if ($Value.Length -eq 0) { return '""' }
        if ($Value -match '[\s"]') {
            return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
        }
        return $Value
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo($isccExe)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.Arguments = (($isccArgs | ForEach-Object { Quote-WinArg $_ }) -join ' ')
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdoutTask = $proc.StandardOutput.ReadToEndAsync()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText($compileLog, ($stdout + "`n" + $stderr).Trim() + "`n", (New-Object System.Text.UTF8Encoding($false)))
    if ($proc.ExitCode -ne 0) {
        throw "ISCC.exe failed with exit code $($proc.ExitCode). See '$compileLog'."
    }

    $setupExe = Join-Path $fullOut ($OutputBaseFilename + '.exe')
    if (!(Test-Path -LiteralPath $setupExe -PathType Leaf)) {
        throw "Expected installer output was not created: '$setupExe'"
    }

    $setupFileInfo = Get-Item -LiteralPath $setupExe
    $setupSha256 = (Get-FileHash -LiteralPath $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $sig = Get-AuthenticodeSignature -LiteralPath $setupExe
    $sigStatus = [string]$sig.Status

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText(
        (Join-Path $fullOut ($OutputBaseFilename + '.sha256')),
        "$setupSha256 *$($setupFileInfo.Name)`n",
        $utf8NoBom)

    $receipt = [ordered]@{
        schema_version = 1
        protocol = 'contain-windows-installer-receipt-1'
        installer_file = $setupFileInfo.Name
        installer_bytes = [int64]$setupFileInfo.Length
        installer_sha256 = $setupSha256
        authenticode_status = $sigStatus
        signed = ($sig.Status -eq [System.Management.Automation.SignatureStatus]::Valid)
        upgrade_rehearsal_only = [bool]$UpgradeRehearsal
        app_id = '{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}'
        app_name = 'Contain'
        app_version = $appVersion
        version_info_version = $verInfo
        install_scope = 'current_user_lowest_privilege'
        default_install_dir = '%LOCALAPPDATA%\Programs\Contain'
        source_sha = [string]$buildMeta.source_sha
        source_zip_sha256 = $actualZipSha
        source_spec_sha256 = $actualSpecSha
        payload_file_count = $extractedFiles.Count
        inno_setup_version = $isccVersion.Trim()
    }
    [IO.File]::WriteAllText(
        (Join-Path $fullOut 'installer-receipt.json'),
        ($receipt | ConvertTo-Json -Depth 6) + "`n",
        $utf8NoBom)

    $summaryLines = @(
        "# Contain Windows Installer Build Summary",
        "",
        "- Installer file: ``$($setupFileInfo.Name)``",
        "- Size (bytes): ``$($setupFileInfo.Length)``",
        "- SHA-256: ``$setupSha256``",
        "- Authenticode status: ``$sigStatus`` (unsigned candidate)" ,
        "- AppId: ``{8F4E2D91-5C3A-4B7E-9A16-2D8C4E7F1035}``",
        "- AppVersion: ``$appVersion`` (PE VersionInfo: ``$verInfo``)",
        "- Source revision: ``$($buildMeta.source_sha)``",
        "- Input desktop ZIP SHA-256: ``$actualZipSha`` ($($extractedFiles.Count) files verified)",
        "- Input package-spec.json SHA-256: ``$actualSpecSha``",
        "- Inno Setup compiler version: ``$($isccVersion.Trim())``",
        "- Upgrade rehearsal build: ``$([bool]$UpgradeRehearsal)``"
    )
    [IO.File]::WriteAllText(
        (Join-Path $fullOut 'installer-summary.md'),
        ($summaryLines -join "`n") + "`n",
        $utf8NoBom)

    Write-Output ($receipt | ConvertTo-Json -Depth 6)
} finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
