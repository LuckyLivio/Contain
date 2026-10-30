$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/demo-provenance.ps1"
$directory = Join-Path $env:TEMP ('contain-provenance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$file = Join-Path $directory 'sample.bin'
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

} finally {
    # Only the exact file and directory created above are touched; no recursive cleanup.
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    Remove-Item -LiteralPath $directory
}
