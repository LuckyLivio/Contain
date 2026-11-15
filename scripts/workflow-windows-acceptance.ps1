param(
    [Parameter(Mandatory)][ValidateSet('Source','Package')][string]$Stage,
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Project = '.',
    [string]$Zip,
    [string]$Spec
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Cli = (Resolve-Path -LiteralPath $Cli).Path
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or ![Environment]::Is64BitOperatingSystem -or ![Environment]::Is64BitProcess) { throw 'Requires Windows x64 process and OS' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; previous evidence is retained' }
$out = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
function Invoke-Recorded([string[]]$Arguments, [int]$Expected) {
    & $Cli @Arguments
    if ($LASTEXITCODE -ne $Expected) { throw "Unexpected CLI exit $LASTEXITCODE (expected $Expected)" }
}
if ($Stage -eq 'Source') {
    $Project = (Resolve-Path -LiteralPath $Project).Path
    $manifest = Get-Content -LiteralPath (Join-Path $Project 'contain.environment.json') -Raw | ConvertFrom-Json
    # Deliberately absent tool, then an installed tool with an impossible required version.
    $manifest.tools = @([pscustomobject]@{ program='contain-intentionally-missing-tool'; version='1.0.0'; version_args=@('--version'); remedy='negative fixture only; do not install' })
    $missing = Join-Path $out 'missing.json'
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $missing -Encoding UTF8
    # PowerShell 5 emits a BOM. Re-encode as UTF-8 without BOM for the JSON contract.
    [IO.File]::WriteAllText($missing, ($manifest | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    Invoke-Recorded @('check','--project',$Project,'--manifest',$missing,'--report',(Join-Path $out 'missing')) 1
    $missingReport = Get-Content -LiteralPath (Join-Path $out 'missing/report.json') -Raw | ConvertFrom-Json
    if (!( $missingReport.checks | Where-Object { $_.name -eq 'tool:contain-intentionally-missing-tool' -and $_.status -eq 'failed' })) { throw 'Missing tool was not identified' }
    $manifest.tools = @([pscustomobject]@{ program='rustc'; version='999.0.0'; version_args=@('--version'); remedy='negative mismatch fixture; install project pinned Rust' })
    $mismatch = Join-Path $out 'mismatch.json'
    [IO.File]::WriteAllText($mismatch, ($manifest | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    Invoke-Recorded @('check','--project',$Project,'--manifest',$mismatch,'--report',(Join-Path $out 'mismatch')) 1
    $mismatchReport = Get-Content -LiteralPath (Join-Path $out 'mismatch/report.json') -Raw | ConvertFrom-Json
    if (!( $mismatchReport.checks | Where-Object { $_.name -eq 'tool:rustc' -and $_.status -eq 'failed' -and $_.detail -like '*version mismatch*' })) { throw 'Installed Rust did not produce a version mismatch; install project prerequisites first' }
    # Run after prerequisites in contain.environment.json have been installed. No auto elevation.
    Invoke-Recorded @('check','--project',$Project,'--report',(Join-Path $out 'restored')) 0
    Invoke-Recorded @('build','--project',$Project,'--execute','--report',(Join-Path $out 'build')) 0
} else {
    if (!$Zip -or !$Spec) { throw 'Package stage requires -Zip and -Spec with independently frozen SHA-256 and expectations' }
    $Zip = (Resolve-Path -LiteralPath $Zip).Path
    $Spec = (Resolve-Path -LiteralPath $Spec).Path
    # No Rust, .NET SDK, Python or MSVC command is used in this stage.
    Invoke-Recorded @('verify','--zip',$Zip,'--spec',$Spec,'--run','--work-dir',(Join-Path $out 'actual-package'),'--report',(Join-Path $out 'actual')) 0
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $broken = Join-Path $out 'missing-key-file.zip'
    Copy-Item -LiteralPath $Zip -Destination $broken
    $archive = [IO.Compression.ZipFile]::Open($broken, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('coreclr.dll')
        if (!$entry) { throw 'Contain negative fixture requires coreclr.dll in original ZIP' }
        $entry.Delete()
    } finally { $archive.Dispose() }
    # Pin the broken ZIP's OWN identity, so failure must be caused by missing content, not a stale ZIP hash.
    $s = Get-Content -LiteralPath $Spec -Raw | ConvertFrom-Json
    $s.sha256 = (Get-FileHash -LiteralPath $broken -Algorithm SHA256).Hash.ToLowerInvariant()
    $brokenSpec = Join-Path $out 'broken-spec.json'
    [IO.File]::WriteAllText($brokenSpec, ($s | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    Invoke-Recorded @('verify','--zip',$broken,'--spec',$brokenSpec,'--run','--work-dir',(Join-Path $out 'must-not-launch'),'--report',(Join-Path $out 'broken')) 1
    $failure = Get-Content -LiteralPath (Join-Path $out 'broken/report.json') -Raw | ConvertFrom-Json
    if (!($failure.checks | Where-Object { $_.name -eq 'static_file:coreclr.dll' -and $_.status -eq 'failed' })) { throw 'Missing file was not detected' }
    if (($failure.checks | Where-Object { $_.name -eq 'launch' }).status -ne 'not_run') { throw 'Broken package was executed' }
}
