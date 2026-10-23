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
