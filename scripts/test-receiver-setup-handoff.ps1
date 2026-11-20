$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$inspectScript = Join-Path $PSScriptRoot 'inspect-receiver-environment.ps1'
$prepareScript = Join-Path $PSScriptRoot 'prepare-receiver-setup-handoff.ps1'

foreach ($s in @($inspectScript, $prepareScript)) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($s, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        throw "PowerShell syntax error in '$s': $($errors | Out-String)"
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('contain-rx-handoff-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $fakeHello = Join-Path $tempRoot 'HelloContain-Setup-x64.exe'
    $fakeContain = Join-Path $tempRoot 'Contain-Setup-x64.exe'
    [IO.File]::WriteAllText($fakeHello, 'synthetic-hello-setup-bytes', $utf8NoBom)
    [IO.File]::WriteAllText($fakeContain, 'synthetic-contain-setup-bytes', $utf8NoBom)

    $helloSha = (Get-FileHash -LiteralPath $fakeHello -Algorithm SHA256).Hash.ToLowerInvariant()
    $containSha = (Get-FileHash -LiteralPath $fakeContain -Algorithm SHA256).Hash.ToLowerInvariant()

    # 1. Pre-copy hash mismatch must fail before creating OutputDirectory and leave inputs intact
    $badOut = Join-Path $tempRoot 'out-mismatch'
    $rejectedMismatch = $false
    try {
        & $prepareScript `
            -HelloContainSetup $fakeHello `
            -ExpectedHelloContainSha256 ('0' * 64) `
            -ContainSetup $fakeContain `
            -ExpectedContainSha256 $containSha `
            -OutputDirectory $badOut | Out-Null
    } catch {
        $rejectedMismatch = $_.Exception.Message -match 'Pre-copy SHA-256 mismatch'
    }
    if (!$rejectedMismatch -or (Test-Path -LiteralPath $badOut)) {
        throw 'Pre-copy SHA-256 mismatch did not abort cleanly before creating OutputDirectory!'
    }

    # 2. Valid handoff directory generation verifies pre/post copy hashes and writes SHA256SUMS + Chinese walkthrough
    $okOut = Join-Path $tempRoot 'out-ok'
    $json = & $prepareScript `
        -HelloContainSetup $fakeHello `
        -ExpectedHelloContainSha256 $helloSha `
        -ContainSetup $fakeContain `
        -ExpectedContainSha256 $containSha `
        -OutputDirectory $okOut | Out-String
    $parsed = $json | ConvertFrom-Json
    if ($parsed.setup_clean_receiver_status -ne '接收机待实测') {
        throw "Unexpected status: $($parsed.setup_clean_receiver_status)"
    }
    foreach ($req in @('HelloContain-Setup-x64.exe', 'Contain-Setup-x64.exe', 'SHA256SUMS.txt', '接收机离线验收走查单.md', 'handoff-manifest.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $okOut $req) -PathType Leaf)) {
            throw "Missing expected handoff file: $req"
        }
    }
    $sums = Get-Content -LiteralPath (Join-Path $okOut 'SHA256SUMS.txt') -Raw -Encoding UTF8
    if ($sums -notlike "*$helloSha *HelloContain-Setup-x64.exe*" -or $sums -notlike "*$containSha *Contain-Setup-x64.exe*") {
        throw 'SHA256SUMS.txt missing expected lines!'
    }
    $walkthrough = Get-Content -LiteralPath (Join-Path $okOut '接收机离线验收走查单.md') -Raw -Encoding UTF8
    if ($walkthrough -notlike '*CONTAIN_DEMO_BUILD_OK_V1*' -or $walkthrough -notlike '*viewer-demo.db*') {
        throw 'Walkthrough checklist missing required verification steps!'
    }

    Write-Output 'Receiver Setup handoff script checks passed (pre/post-copy SHA-256 + walkthrough + preservation).'
} finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
