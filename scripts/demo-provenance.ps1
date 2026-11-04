# Helpers used by demo.ps1. Hash observations are not a loaded-image attestation.
function Get-DemoTextHash([string]$Text) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
function Get-DemoBinaryIdentity([string]$Path) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    [ordered]@{
        path = $item.FullName
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $item.Length
        last_write_utc = $item.LastWriteTimeUtc.ToString('o')
        observed_at_utc = [DateTime]::UtcNow.ToString('o')
    }
}

function Complete-DemoBinaryIdentity($Before) {
    $rows = @(
        foreach ($binary in $Before) {
            try {
                $after = Get-DemoBinaryIdentity $binary.path
                [ordered]@{ before = $binary; after = $after; unchanged = $binary.sha256 -ceq $after.sha256; error = $null }
            } catch {
                [ordered]@{ before = $binary; after = $null; unchanged = $false; error = $_.Exception.Message }
            }
        }
    )
    [ordered]@{ binaries = $rows; unchanged = @($rows | Where-Object { -not $_.unchanged }).Count -eq 0 }
}

function Get-DemoSourceIdentity {
    # Preserve observed tracked/untracked status; no private source contents in JSON.
    $head = (& git rev-parse HEAD) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record demo source revision' }
    $status = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record demo source status' }
    $diff = (& git diff --binary HEAD) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot record demo tracked source changes' }
    $inputs = @(& git ls-files --cached --others --exclude-standard -- Cargo.toml Cargo.lock rust-toolchain.toml crates examples/test-installer vendor | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate demo build inputs' }
    $files = @($inputs | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $tree = ($files | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
    [ordered]@{ head = $head; status = $status; tracked_diff_utf8_lf_sha256 = (Get-DemoTextHash $diff); runtime_tree_sha256 = (Get-DemoTextHash $tree); runtime_inputs = $files; observed_at_utc = [DateTime]::UtcNow.ToString('o') }
}


function Get-DemoBuildExecutables([object[]]$BuildOutput, [switch]$RegistryNoise) {
    # Cargo reports absolute executable paths even for custom target directories
    # and target triples. Never infer a path from an older target/ checkout.
    $required = @('contain', 'contain-test-installer')
    if ($RegistryNoise) { $required += 'contain-registry-noise' }
    $paths = @{}
    foreach ($line in $BuildOutput) {
        $text = [string]$line
        if (-not $text.TrimStart().StartsWith('{')) { continue }
        try { $message = $text | ConvertFrom-Json -ErrorAction Stop } catch { continue }
        if ($null -eq $message.PSObject.Properties['reason'] -or $message.reason -ne 'compiler-artifact') { continue }
        if ($null -eq $message.PSObject.Properties['target'] -or $null -eq $message.target) { continue }
        $target = $message.target
        if ($null -eq $target.PSObject.Properties['name'] -or $target.name -notin $required) { continue }
        if ($null -eq $target.PSObject.Properties['kind'] -or 'bin' -notin @($target.kind)) { continue }
        if ($null -eq $message.PSObject.Properties['executable'] -or $message.executable -isnot [string] -or [string]::IsNullOrWhiteSpace($message.executable)) { continue }
        if (-not [IO.Path]::IsPathRooted($message.executable)) { throw "Cargo reported a non-absolute executable for $($target.name)" }
        $path = [IO.Path]::GetFullPath($message.executable)
        if ($paths.ContainsKey($target.name) -and $paths[$target.name] -cne $path) { throw "Cargo reported ambiguous executables for $($target.name)" }
        $paths[$target.name] = $path
    }
    foreach ($name in $required) {
        if (-not $paths.ContainsKey($name)) { throw "This Cargo build did not report the required executable: $name" }
    }
    return $paths
}

function Get-DemoPrebuiltExecutables {
    param([Parameter(Mandatory)][string]$ContainExecutable,
          [Parameter(Mandatory)][string]$FixtureExecutable,
          [Parameter(Mandatory)][string]$BuildManifest)
    $manifestPath = (Get-Item -LiteralPath $BuildManifest -ErrorAction Stop).FullName
    $root = Split-Path $manifestPath
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schema_version -ne 1 -or $manifest.source_sha -cnotmatch '^[0-9a-f]{40}$' -or
        $manifest.runtime_tree_sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $manifest.target -ne 'x86_64-pc-windows-msvc' -or $manifest.profile -notin @('debug','release')) {
        throw 'Unsupported or incomplete prebuilt build identity'
    }
    $seen = @{}
    foreach ($item in @($manifest.binaries) + @($manifest.runtime_files) + @($manifest.scripts) + @($manifest.resources)) {
        $relative = if ($item.PSObject.Properties['name']) { [string]$item.name } else { [string]$item.path }
        $normalized = $relative.Replace('\','/')
        if (-not $relative -or [IO.Path]::IsPathRooted($relative) -or $relative -match '^[A-Za-z]:' -or
            $normalized.StartsWith('/') -or @($normalized.Split('/') | Where-Object { $_ -in @('..','.','') }).Count -gt 0 -or
            $seen.ContainsKey($normalized) -or $item.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'Invalid or duplicate package manifest path/hash'
        }
        $seen[$normalized] = $true
        $path = Join-Path $root $normalized
        $actual = Get-DemoBinaryIdentity $path
        if ($actual.sha256 -cne $item.sha256 -or ($item.PSObject.Properties['bytes'] -and $actual.bytes -ne $item.bytes)) {
            throw "Packaged file does not match build manifest: $normalized"
        }
    }
    foreach ($required in @('contain.exe','contain-test-installer.exe','scripts/demo.ps1','scripts/demo-provenance.ps1',
        'scripts/demo-checks.ps1','scripts/demo-save-capture.ps1','scripts/score-fixture.ps1','scripts/score-fixture.py','scripts/demo-measure.py',
        'scripts/v04-verify-views.py','scripts/v04-clock-protocol.py','scripts/v04-matrix.py','scripts/v04-metrics.py',
        'docs/schema/cli-v3.schema.json')) {
        if (-not $seen.ContainsKey($required)) { throw "Required package file lacks a manifest hash: $required" }
    }
    $paths = @{}
    foreach ($pair in @(@('contain',$ContainExecutable), @('contain-test-installer',$FixtureExecutable))) {
        $actual = Get-DemoBinaryIdentity $pair[1]
        $expected = [IO.Path]::GetFullPath((Join-Path $root ($pair[0]+'.exe')))
        if (-not $actual.path.Equals($expected,[StringComparison]::OrdinalIgnoreCase)) { throw 'Selected executable is outside the verified package location' }
        $paths[$pair[0]] = $actual.path
    }
    [ordered]@{ executables=$paths; manifest=$manifest; manifest_path=$manifestPath;
        manifest_sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() }
}
