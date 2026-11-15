function Copy-WorkflowRustNotices([string]$PackageName,[string]$Files) {
    $notices = (New-Item -ItemType Directory -Path (Join-Path $files 'licenses')).FullName
    # Collect notices from the actual locked CLI dependency closure, not the old ETW graph.
    $metadataText = & cargo metadata --locked --format-version 1 --filter-platform x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve locked license graph' }
    $metadata = $metadataText | ConvertFrom-Json
    $queue = New-Object Collections.Queue
    $queue.Enqueue(($metadata.packages | Where-Object { $_.name -eq $PackageName }).id)
    $seen = @{}
    $rows = @()
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if ($seen.ContainsKey($id)) { continue }
        $seen[$id] = $true
        $node = $metadata.resolve.nodes | Where-Object { $_.id -eq $id }
        foreach ($dep in $node.deps) { $queue.Enqueue($dep.pkg) }
        $package = $metadata.packages | Where-Object { $_.id -eq $id }
        if ($package.name -eq $PackageName) { continue }
        $packageRoot = Split-Path $package.manifest_path -Parent
        $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|UNLICENSE)' })
        if (!$package.source) { $licenseFiles += Get-Item -LiteralPath (Join-Path $root 'LICENSE') }
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
}
