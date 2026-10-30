$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/demo-provenance.ps1"
$directory = Join-Path $env:TEMP ('contain-provenance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$file = Join-Path $directory 'sample.bin'
$package = Join-Path $directory 'package'
try {
    [IO.File]::WriteAllText($file, 'first executable bytes')
    $before = Get-DemoBinaryIdentity $file
    if (-not (Complete-DemoBinaryIdentity @($before)).unchanged) { throw 'Unchanged bytes rejected' }
    [IO.File]::WriteAllText($file, 'different executable bytes')
    if ((Complete-DemoBinaryIdentity @($before)).unchanged) { throw 'Replacement was not detected' }
    Remove-Item -LiteralPath $file
    $missing = Complete-DemoBinaryIdentity @($before)
    if ($missing.unchanged -or $null -ne $missing.binaries[0].after -or -not $missing.binaries[0].error) { throw 'Missing executable incorrectly accepted' }
    Write-Output 'PASS: unchanged, replaced, and missing executable observations'

    function Artifact([string]$Name, [string]$Executable, [string]$Kind = 'bin') {
        [ordered]@{ reason='compiler-artifact'; target=@{ name=$Name; kind=@($Kind) }; executable=$Executable; fresh=$true } | ConvertTo-Json -Depth 5 -Compress
    }
    function Assert-Rejected([object[]]$Output, [switch]$Noise) {
        $rejected=$false
        try { $null=Get-DemoBuildExecutables -BuildOutput $Output -RegistryNoise:$Noise } catch { $rejected=$true }
        if (-not $rejected) { throw 'Missing or ambiguous Cargo artifacts were accepted' }
    }
    $custom = Join-Path $directory 'custom target\x86_64-pc-windows-msvc\release'
    $containPath = Join-Path $custom 'contain.exe'
    $fixturePath = Join-Path $custom 'contain-test-installer.exe'
    $noisePath = Join-Path $custom 'contain-registry-noise.exe'
    $containArtifact = Artifact 'contain' $containPath
    $fixtureArtifact = Artifact 'contain-test-installer' $fixturePath
    $noiseArtifact = Artifact 'contain-registry-noise' $noisePath
    $output = @('Compiling diagnostic on stderr', '{"reason":"compiler-message","message":{"rendered":"warning"}}', $containArtifact, $fixtureArtifact, $noiseArtifact)
    $selected = Get-DemoBuildExecutables -BuildOutput $output -RegistryNoise
    # Production returns full paths; Windows may expand a TEMP short name such
    # as RUNNER~1 even when the synthetic child directories do not exist.
    if ($selected['contain'] -cne [IO.Path]::GetFullPath($containPath) -or $selected['contain-test-installer'] -cne [IO.Path]::GetFullPath($fixturePath) -or $selected['contain-registry-noise'] -cne [IO.Path]::GetFullPath($noisePath)) { throw 'Custom output/target paths were not selected from this build' }
    $noncanonical = Join-Path $custom 'unused\..\contain.exe'
    $selected = Get-DemoBuildExecutables -BuildOutput @((Artifact 'contain' $noncanonical), $fixtureArtifact)
    if ($selected['contain'] -cne [IO.Path]::GetFullPath($containPath)) { throw 'Absolute artifact normalization changed the selected executable' }
    $selected = Get-DemoBuildExecutables -BuildOutput @($containArtifact, $fixtureArtifact)
    if ($selected.Count -ne 2) { throw 'Optional noise unexpectedly required' }
    Assert-Rejected @($containArtifact)
    Assert-Rejected @($containArtifact, $fixtureArtifact) -Noise
    Assert-Rejected @((Artifact 'contain' $containPath 'lib'), $fixtureArtifact)
    Assert-Rejected @($containArtifact, (Artifact 'contain' (Join-Path $custom 'other.exe')), $fixtureArtifact)
    Assert-Rejected @((Artifact 'contain' 'target\release\contain.exe'), $fixtureArtifact)
    Write-Output 'PASS: custom target paths, optional noise, missing fixture/noise, library-only, ambiguous, and relative artifacts'

    New-Item -ItemType Directory -Path $package | Out-Null
    $packageNames=@('contain.exe','contain-test-installer.exe','scripts/demo.ps1','scripts/demo-provenance.ps1',
        'scripts/demo-checks.ps1','scripts/demo-save-capture.ps1','scripts/score-fixture.ps1','scripts/score-fixture.py','scripts/demo-measure.py',
        'scripts/v04-verify-views.py','scripts/v04-clock-protocol.py','scripts/v04-matrix.py','scripts/v04-metrics.py',
        'docs/schema/cli-v3.schema.json','example-runtime.dll')
    foreach($relative in $packageNames) {
        $destination=Join-Path $package $relative
        New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
        [IO.File]::WriteAllText($destination,('synthetic package bytes: '+$relative))
    }
    $build=[ordered]@{schema_version=1;source_sha=('a'*40);runtime_tree_sha256=('b'*64);
        target='x86_64-pc-windows-msvc';profile='release';binaries=@();runtime_files=@();scripts=@();resources=@()}
    foreach($relative in $packageNames) {
        $item=Get-DemoBinaryIdentity (Join-Path $package $relative)
        if ($relative.EndsWith('.exe')) { $build.binaries+=@{name=$relative;sha256=$item.sha256;bytes=$item.bytes} }
        elseif($relative.EndsWith('.dll')) { $build.runtime_files+=@{name=$relative;sha256=$item.sha256;bytes=$item.bytes} }
        elseif($relative.StartsWith('scripts/')) { $build.scripts+=@{path=$relative;sha256=$item.sha256;bytes=$item.bytes} }
        else { $build.resources+=@{path=$relative;sha256=$item.sha256;bytes=$item.bytes} }
    }
    $buildPath=Join-Path $package 'BUILD.json'
    $goodManifest=$build | ConvertTo-Json -Depth 8
    $goodManifest | Set-Content -LiteralPath $buildPath -Encoding utf8
    function Select-Package {
        Get-DemoPrebuiltExecutables -ContainExecutable (Join-Path $package 'contain.exe') -FixtureExecutable (Join-Path $package 'contain-test-installer.exe') -BuildManifest $buildPath
    }
    function Reject-Package {
        $rejected=$false
        try { $null=Select-Package } catch { $rejected=$true }
        if(-not $rejected){throw 'Invalid prebuilt package was accepted'}
    }
    $selection=Select-Package
    if ($selection.manifest.source_sha -ne ('a'*40) -or $selection.executables.Count -ne 2 -or $selection.manifest_sha256.Length -ne 64) { throw 'Prebuilt manifest selection lost source/binary identity' }
    # Pure package selection must never require a source checkout or a Cargo build.
    function Get-DemoSourceIdentity { throw 'Unexpected Git source lookup for a prebuilt package' }
    $null=Select-Package
    foreach($relative in @('contain.exe','scripts/demo-checks.ps1','scripts/demo-save-capture.ps1','example-runtime.dll','docs/schema/cli-v3.schema.json')) {
        $path=Join-Path $package $relative
        $bytes=[IO.File]::ReadAllBytes($path)
        [IO.File]::WriteAllText($path,'changed')
        Reject-Package
        [IO.File]::WriteAllBytes($path,$bytes)
    }
    foreach($kind in @('missing-script','missing-save','duplicate','escape','wrong-target','invalid-source')) {
        $bad=$goodManifest | ConvertFrom-Json
        switch($kind) {
            'missing-script' { $bad.scripts=@($bad.scripts | Where-Object path -ne 'scripts/demo-checks.ps1') }
            'missing-save' { $bad.scripts=@($bad.scripts | Where-Object path -ne 'scripts/demo-save-capture.ps1') }
            'duplicate' { $bad.binaries=@($bad.binaries)+@($bad.binaries[0]) }
            'escape' { $bad.scripts[0].path='../outside.py' }
            'wrong-target' { $bad.target='x86_64-unknown-linux-gnu' }
            'invalid-source' { $bad.source_sha='unknown' }
        }
        $bad | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $buildPath -Encoding utf8
        Reject-Package
    }
    $goodManifest | Set-Content -LiteralPath $buildPath -Encoding utf8
    $null=Select-Package
    Write-Output 'PASS: prebuilt package identity; changed executable/script/runtime/schema, missing check/save script, duplicate, escape, wrong target and invalid source rejected'

} finally {
    # Only the exact file and directory created above are touched; no recursive cleanup.
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    # This nested package contains only files created by this synthetic test.
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Recurse }
    Remove-Item -LiteralPath $directory
}
