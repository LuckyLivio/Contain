param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or ![Environment]::Is64BitProcess) { throw 'Build on Windows x64 with the manifest prerequisites' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('target/workflow-cli/packages/' + [guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory; previous deliveries are preserved' }
$oldFlags = $env:RUSTFLAGS
$oldTarget = $env:CARGO_TARGET_DIR
Push-Location $root
try {
    # A native CLI handed to a machine without MSVC must not require its redistributable DLLs.
    $env:RUSTFLAGS = (($oldFlags + ' -C target-feature=+crt-static').Trim())
    $env:CARGO_TARGET_DIR = Join-Path $root 'target/workflow-cli/build'
    & cargo build -p contain-workflow --release --locked --target x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw 'Workflow CLI build failed' }
    $out = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
    $files = (New-Item -ItemType Directory -Path (Join-Path $out 'files')).FullName
    Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'x86_64-pc-windows-msvc/release/contain-workflow.exe') -Destination $files
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $files
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $files
    $notices = (New-Item -ItemType Directory -Path (Join-Path $files 'licenses')).FullName
    # Collect notices from the actual locked CLI dependency closure, not the old ETW graph.
    $metadataText = & cargo metadata --locked --format-version 1 --filter-platform x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve locked license graph' }
    $metadata = $metadataText | ConvertFrom-Json
    $queue = New-Object Collections.Queue
    $queue.Enqueue(($metadata.packages | Where-Object { $_.name -eq 'contain-workflow' }).id)
    $seen = @{}
    $rows = @()
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if ($seen.ContainsKey($id)) { continue }
        $seen[$id] = $true
        $node = $metadata.resolve.nodes | Where-Object { $_.id -eq $id }
        foreach ($dep in $node.deps) { $queue.Enqueue($dep.pkg) }
        $package = $metadata.packages | Where-Object { $_.id -eq $id }
        if ($package.name -eq 'contain-workflow') { continue }
        $packageRoot = Split-Path $package.manifest_path -Parent
        $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|UNLICENSE)' })
        if ($package.license_file) { $licenseFiles += Get-Item -LiteralPath (Join-Path $packageRoot $package.license_file) }
        if ($licenseFiles.Count -eq 0) { throw "No redistribution notice found for $($package.name)" }
        $destination = (New-Item -ItemType Directory -Path (Join-Path $notices ($package.name + '-' + $package.version))).FullName
        foreach ($licenseFile in $licenseFiles) { Copy-Item -LiteralPath $licenseFile.FullName -Destination $destination -Force }
        $rows += [ordered]@{ name=$package.name; version=$package.version; license=$package.license }
    }
    [IO.File]::WriteAllText((Join-Path $notices 'dependencies.json'), (ConvertTo-Json -InputObject $rows -Depth 10), (New-Object Text.UTF8Encoding $false))
    $sysroot = (& rustc --print sysroot).Trim()
    $rustNotices = Join-Path $sysroot 'share/doc/rust'
    Copy-Item -LiteralPath (Join-Path $rustNotices 'COPYRIGHT.html') -Destination $notices
    Copy-Item -LiteralPath (Join-Path $rustNotices 'licenses') -Destination (Join-Path $notices 'rust-stdlib') -Recurse
    $zip = Join-Path $out 'contain-workflow-windows-x64.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($files,$zip)
    $sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $exeSha = (Get-FileHash -LiteralPath (Join-Path $files 'contain-workflow.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    $spec = [ordered]@{
        schema_version=1; target='windows-x64'; sha256=$sha; entry='contain-workflow.exe'; required_files=@('LICENSE','THIRD-PARTY-NOTICES.md')
        launch=@{args=@('--version');timeout_ms=10000;expected_exit=0;window_title=$null}
        tests=@(@{name='CLI --help exits 0';program='contain-workflow.exe';args=@('--help');expected_exit=0;timeout_ms=10000})
    }
    [IO.File]::WriteAllText((Join-Path $out 'package-spec.json'),($spec | ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding $false))
    [IO.File]::WriteAllText((Join-Path $out 'identity.json'),([ordered]@{schema_version=1;zip_sha256=$sha;entry_sha256=$exeSha;crt='static requested; clean-machine execution still requires validation';acceptance='not_run'} | ConvertTo-Json),(New-Object Text.UTF8Encoding $false))
    Write-Output "Package and frozen test specification: $out"
} finally {
    $env:RUSTFLAGS = $oldFlags
    $env:CARGO_TARGET_DIR = $oldTarget
    Pop-Location
}
