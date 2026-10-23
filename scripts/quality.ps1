param(
    [ValidateSet('Setup', 'Check', 'Coverage', 'Dependencies', 'Bench', 'Fuzz')]
    [string]$Mode = 'Check',
    [ValidateRange(1, 3600)][int]$FuzzSeconds = 30
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PSNativeCommandUseErrorActionPreference = $false
$previousPath = $env:PATH
$cargoDirectory = if ($env:CARGO_HOME) { Join-Path $env:CARGO_HOME 'bin' } else { Join-Path $env:USERPROFILE '.cargo/bin' }
$env:PATH = $cargoDirectory + [IO.Path]::PathSeparator + $env:PATH
Push-Location (Join-Path $PSScriptRoot '..')
try {
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw 'Rust is missing. Inspect the existing installation before installing rustup.' }
    New-Item -ItemType Directory -Force target/quality | Out-Null
    # Preserve previous attempts, including failures, before refreshing convenient paths.
    $archive = Join-Path 'target/quality/runs' ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $archive | Out-Null
    function Invoke-Gate([string]$Label, [string]$Program, [string[]]$Arguments) {
        Write-Host "Quality gate: $Label"
        $log = "target/quality/$Label.log"
        if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination $archive }
        if ($Label -like 'metadata*') {
            & $Program @Arguments *> $log
        } else {
            & $Program @Arguments 2>&1 | Tee-Object -FilePath $log
        }
        if ($LASTEXITCODE -ne 0) { throw "$Label failed (exit $LASTEXITCODE). See target/quality/$Label.log" }
    }
    if ($Mode -eq 'Setup') {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        $userPathParts = @($userPath -split ';' | Where-Object { $_ })
        if (-not ($userPathParts | Where-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\', '/') -ieq $cargoDirectory.TrimEnd('\', '/') })) {
            [Environment]::SetEnvironmentVariable('Path', (($userPathParts + $cargoDirectory) -join ';'), 'User')
            Write-Host 'Added existing Cargo bin directory to user PATH for future terminals.'
        }
        $installed = @(& rustup component list --installed)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect rustup components' }
        foreach ($component in @('rustfmt', 'clippy', 'rust-analyzer', 'llvm-tools-preview')) {
            $componentName = if ($component -eq 'llvm-tools-preview') { 'llvm-tools' } else { $component }
            if (-not ($installed -match ('^' + [regex]::Escape($componentName) + '-'))) {
                Invoke-Gate "install-$component" rustup @('component', 'add', $component)
            }
        }
        $versions = [ordered]@{ 'nextest'='0.9.149'; 'llvm-cov'='0.9.1'; 'deny'='0.20.2'; 'fuzz'='0.13.2' }
        foreach ($tool in $versions.Keys) {
            & cargo $tool --version *> $null
            if ($LASTEXITCODE -ne 0) {
                Invoke-Gate "install-$tool" cargo @('install', "cargo-$tool", '--version', $versions[$tool], '--locked')
            } else { Write-Host "Reusing installed cargo-$tool" }
        }
        return
    }
    switch ($Mode) {
        'Check' {
            Invoke-Gate fmt cargo @('fmt', '--all', '--', '--check')
            Invoke-Gate clippy cargo @('clippy', '--workspace', '--all-targets', '--all-features', '--locked', '--', '-D', 'warnings')
            Invoke-Gate nextest cargo @('nextest', 'run', '--workspace', '--all-features', '--locked', '--profile', 'ci', '--retries', '0')
            Invoke-Gate doctests cargo @('test', '--workspace', '--all-features', '--locked', '--doc')
            Invoke-Gate fuzz-fmt cargo @('fmt', '--manifest-path', 'fuzz/Cargo.toml', '--all', '--', '--check')
            Invoke-Gate fuzz-clippy cargo @('clippy', '--manifest-path', 'fuzz/Cargo.toml', '--all-targets', '--locked', '--', '-D', 'warnings')
            Invoke-Gate fuzz-seeds cargo @('test', '--manifest-path', 'fuzz/Cargo.toml', '--lib', '--locked')
            Invoke-Gate scorer-pwsh pwsh @('-NoProfile', '-File', 'scripts/test-score.ps1')
            Invoke-Gate provenance-pwsh pwsh @('-NoProfile', '-File', 'scripts/test-demo-provenance.ps1')
            foreach ($script in Get-ChildItem -LiteralPath scripts -Filter 'test-*.py' | Sort-Object Name) {
                Invoke-Gate $script.BaseName python @($script.FullName)
            }
        }
        'Coverage' {
            foreach ($artifact in @('coverage.json', 'lcov.info', 'coverage-html')) {
                $artifactPath = Join-Path 'target/quality' $artifact
                if (Test-Path -LiteralPath $artifactPath) {
                    $resolvedArtifact = (Resolve-Path -LiteralPath $artifactPath).Path
                    $resolvedQuality = (Resolve-Path -LiteralPath target/quality).Path
                    if (-not $resolvedArtifact.StartsWith($resolvedQuality + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact archive path escaped target/quality' }
                    Move-Item -LiteralPath $resolvedArtifact -Destination $archive
                }
            }
            Invoke-Gate coverage cargo @('llvm-cov', '--workspace', '--all-features', '--locked', '--json', '--output-path', 'target/quality/coverage.json')
            Invoke-Gate coverage-html cargo @('llvm-cov', 'report', '--html', '--output-dir', 'target/quality/coverage-html')
            Invoke-Gate coverage-lcov cargo @('llvm-cov', 'report', '--lcov', '--output-path', 'target/quality/lcov.info')
            Invoke-Gate coverage-floor cargo @('llvm-cov', 'report', '--summary-only', '--fail-under-lines', '66.5', '--fail-under-functions', '70')
        }
        'Dependencies' {
            Invoke-Gate metadata cargo @('metadata', '--locked', '--all-features', '--filter-platform', 'x86_64-pc-windows-msvc', '--format-version', '1')
            Invoke-Gate deny cargo @('deny', '--locked', 'check')
            if (Test-Path fuzz/Cargo.toml) {
                if (-not (Test-Path fuzz/Cargo.lock)) { throw 'Fuzz workspace requires its committed Cargo.lock' }
                Invoke-Gate metadata-fuzz cargo @('metadata', '--manifest-path', 'fuzz/Cargo.toml', '--locked', '--format-version', '1')
                Invoke-Gate fuzz-lock python @('scripts/check-fuzz-lock.py')
                Invoke-Gate deny-fuzz cargo @('deny', '--manifest-path', 'fuzz/Cargo.toml', '--config', 'deny.toml', '--locked', 'check')
            }
        }
        'Bench' {
            if (Test-Path target/quality/event-pipeline.json) { Move-Item -LiteralPath target/quality/event-pipeline.json -Destination $archive }
            Invoke-Gate bench cargo @('bench', '-p', 'contain-core', '--bench', 'event_pipeline', '--locked', '--', '--events', '20000', '--samples', '7')
        }
        'Fuzz' {
            $fuzzToolchain = if ($env:CONTAIN_FUZZ_TOOLCHAIN) { $env:CONTAIN_FUZZ_TOOLCHAIN } else { 'nightly' }
            $toolchains = @(& rustup toolchain list)
            if (-not ($toolchains -match ('^' + [regex]::Escape($fuzzToolchain) + '-'))) {
                throw "Missing isolated fuzz toolchain $fuzzToolchain. See fuzz/README.md; install only if absent. Stable production default is unchanged."
            }
            $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
            if (-not (Test-Path $vswhere)) { throw 'Fuzzing needs the MSVC developer environment and ASan. See fuzz/README.md.' }
            $vsInstall = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.ASAN -property installationPath
            if (-not $vsInstall) { throw 'MSVC ASan not detected. See fuzz/README.md.' }
            $msvc = Get-ChildItem -LiteralPath (Join-Path $vsInstall 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
            $asanBin = Join-Path $msvc.FullName 'bin/Hostx64/x64'
            $env:PATH = $asanBin + [IO.Path]::PathSeparator + $env:PATH
            Invoke-Gate metadata-fuzz cargo @("+$fuzzToolchain", 'metadata', '--manifest-path', 'fuzz/Cargo.toml', '--locked', '--format-version', '1')
            Invoke-Gate fuzz-lock python @('scripts/check-fuzz-lock.py')
            $fuzzLockHash = (Get-FileHash -LiteralPath fuzz/Cargo.lock).Hash
            foreach ($target in @('event_json', 'lifetime_sequences', 'completion_correlation')) {
                Invoke-Gate "fuzz-$target" cargo @("+$fuzzToolchain", 'fuzz', 'run', $target, '--', "-max_total_time=$FuzzSeconds", '-max_len=4096', '-timeout=10', '-rss_limit_mb=2048')
                if ((Get-FileHash -LiteralPath fuzz/Cargo.lock).Hash -ne $fuzzLockHash) { throw 'Fuzzing changed its lockfile; results are not reproducible' }
            }
        }
    }
} finally {
    Pop-Location
    $env:PATH = $previousPath
}
