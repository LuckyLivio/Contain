"""Exercise quality output isolation without compiling or touching real evidence."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


DRIVER = r"""
param([string]$Mode, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
function global:git { $global:LASTEXITCODE = 0; 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' }
function global:rustc { $global:LASTEXITCODE = 0; 'rustc 1.98.1' }
function global:python { $global:LASTEXITCODE = 0; 'Python 3.12.7' }
function global:pwsh { $global:LASTEXITCODE = 0; 'PowerShell 7.6.5' }
function global:cargo {
    $arguments = @($args)
    @{ arguments=$arguments; coverage_target=$env:CARGO_LLVM_COV_TARGET_DIR } |
        ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $env:TEST_REPO 'commands.jsonl')
    if ($arguments.Count -gt 1 -and $arguments[0] -eq 'nextest' -and $arguments[1] -eq 'run') {
        # Match real nextest: Cargo's target directory does not redirect its store.
        $store = Join-Path $env:TEST_REPO 'target/nextest'
        $configIndex = [Array]::IndexOf($arguments, '--tool-config-file')
        if ($configIndex -ge 0) {
            $config = $arguments[$configIndex + 1].Substring('contain-output:'.Length)
            $setting = Get-Content -LiteralPath $config | Where-Object { $_ -match '^dir = ' }
            $store = $setting.Substring('dir = '.Length) | ConvertFrom-Json
        }
        $junit = Join-Path $store 'ci/junit.xml'
        New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($junit)) | Out-Null
        '<testsuites><testsuite><testcase name="isolated"/></testsuite></testsuites>' |
            Set-Content -LiteralPath $junit
    }
    foreach ($option in @('--output-path', '--output-dir', '--output')) {
        $index = [Array]::IndexOf($arguments, $option)
        if ($index -ge 0) {
            $output = $arguments[$index + 1]
            if ($option -eq '--output-dir') { New-Item -ItemType Directory -Force $output | Out-Null }
            else {
                New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($output)) | Out-Null
                '{}' | Set-Content -LiteralPath $output
            }
        }
    }
    $global:LASTEXITCODE = 0
    'cargo 1.98.1'
}
$options = @{ Mode=$Mode }
if ($OutputDirectory) { $options.OutputDirectory = $OutputDirectory }
& (Join-Path $env:TEST_REPO 'scripts/quality.ps1') @options
if ($env:CARGO_LLVM_COV_TARGET_DIR -ne 'previous-coverage-target') { throw 'Coverage environment leaked' }
"""


@unittest.skipUnless(shutil.which("pwsh"), "PowerShell unavailable; output isolation unverified")
class QualityOutputTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="contain-quality-output-")
        self.addCleanup(self.temporary.cleanup)
        # Windows runner TEMP may use an 8.3 alias. PowerShell resolves its
        # working directory to the long spelling; compare the actual directory.
        self.root = Path(self.temporary.name).resolve()
        (self.root / "scripts").mkdir()
        shutil.copyfile(Path(__file__).with_name("quality.ps1"), self.root / "scripts/quality.ps1")
        (self.root / "driver.ps1").write_text(DRIVER, encoding="utf-8")
        self.history = self.root / "target/quality"
        self.history.mkdir(parents=True)
        self.historical_log = self.history / "fmt.log"
        self.historical_log.write_bytes(b"preserve historical failed check\n")
        self.historical_junit = self.root / "target/nextest/ci/junit.xml"
        self.historical_junit.parent.mkdir(parents=True)
        self.historical_junit.write_bytes(b"preserve historical junit\n")
        self.cargo_target = self.root / "target/beta2/cargo"
        self.output = self.root / "target/beta2/quality"
        self.environment = dict(os.environ, TEST_REPO=str(self.root), CARGO_TARGET_DIR=str(self.cargo_target),
                                CARGO_LLVM_COV_TARGET_DIR="previous-coverage-target")

    def run_mode(self, mode, custom=True):
        command = [shutil.which("pwsh"), "-NoProfile", "-File", str(self.root / "driver.ps1"), "-Mode", mode]
        if custom:
            command.extend(["-OutputDirectory", "target/beta2/quality"])
        result = subprocess.run(command, cwd=self.root, env=self.environment, capture_output=True,
                                text=True, encoding="utf-8", timeout=60)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def commands(self):
        return [json.loads(line) for line in (self.root / "commands.jsonl").read_text(encoding="utf-8-sig").splitlines()]

    def test_check_keeps_historical_outputs_and_uses_actual_cargo_junit(self):
        self.run_mode("Check")
        self.assertEqual(self.historical_log.read_bytes(), b"preserve historical failed check\n")
        self.assertEqual(self.historical_junit.read_bytes(), b"preserve historical junit\n")
        self.assertFalse((self.history / "gates.json").exists())
        manifest = json.loads((self.output / "gates.json").read_text(encoding="utf-8-sig"))
        self.assertTrue(manifest["invocations"][0]["passed"])
        nextest = next(gate for gate in manifest["gates"] if gate["label"] == "nextest")
        self.assertIn('name="isolated"', (self.output / nextest["log"]).with_name("junit.xml").read_text())
        self.run_mode("Check")
        self.assertEqual(len(list((self.output / "runs").glob("*/previous-junit.xml"))), 1)
        self.assertEqual(self.historical_junit.read_bytes(), b"preserve historical junit\n")

    def test_custom_coverage_outputs_and_original_build_stay_isolated(self):
        self.run_mode("Coverage")
        self.run_mode("CoverageOriginal")
        for name in ("coverage.json", "coverage-html", "lcov.info", "coverage-original.json",
                     "coverage-original-html", "coverage-original-lcov.info"):
            self.assertTrue((self.output / name).exists(), name)
            self.assertFalse((self.history / name).exists(), name)
        original = [call for call in self.commands() if "contain-core" in call["arguments"]]
        self.assertTrue(original)
        self.assertTrue(all(Path(call["coverage_target"]) == self.output / "coverage-original-build" for call in original))
        floors = [call["arguments"] for call in self.commands() if "--fail-under-lines" in call["arguments"]]
        self.assertEqual(len(floors), 2)
        self.assertTrue(all(args[args.index("--fail-under-lines") + 1] == "66.5" and
                            args[args.index("--fail-under-functions") + 1] == "70" for args in floors))

    def test_default_check_retains_nextest_workspace_store(self):
        self.run_mode("Check", custom=False)
        manifest = json.loads((self.history / "gates.json").read_text(encoding="utf-8-sig"))
        gate = next(gate for gate in manifest["gates"] if gate["label"] == "nextest")
        archive = (self.history / gate["log"]).parent
        self.assertEqual((archive / "previous-junit.xml").read_bytes(), b"preserve historical junit\n")
        self.assertIn('name="isolated"', (archive / "junit.xml").read_text())
        self.assertNotIn("--tool-config-file", gate["arguments"])

    def test_default_original_coverage_build_keeps_existing_location(self):
        self.run_mode("CoverageOriginal", custom=False)
        original = [call for call in self.commands() if "contain-core" in call["arguments"]]
        self.assertTrue(all(Path(call["coverage_target"]) == self.root / "target/coverage-original-build" for call in original))
        self.assertTrue((self.history / "coverage-original.json").exists())

    @unittest.skipUnless(os.name == "nt", "Windows 8.3 alias regression")
    def test_original_coverage_destinations_survive_short_directory_alias(self):
        import ctypes
        buffer = ctypes.create_unicode_buffer(32768)
        length = ctypes.windll.kernel32.GetShortPathNameW(str(self.root), buffer, len(buffer))
        if not length or Path(buffer.value) == self.root:
            self.skipTest("8.3 aliases unavailable on the temporary volume")
        canonical = self.root
        alias = Path(buffer.value)
        self.assertEqual(alias.resolve(), canonical)
        self.environment.update(TEST_REPO=str(alias), CARGO_TARGET_DIR=str(alias / "target/beta2/cargo"))
        try:
            # Invoke through the alias while keeping expected destinations bound
            # to the canonical fixture, with the same strict equality checks.
            self.root = alias
            for custom in (True, False):
                with self.subTest(custom=custom):
                    previous = len(self.commands()) if (alias / "commands.jsonl").exists() else 0
                    self.run_mode("CoverageOriginal", custom=custom)
                    original = [call for call in self.commands()[previous:] if "contain-core" in call["arguments"]]
                    expected = (self.output if custom else canonical / "target") / "coverage-original-build"
                    self.assertTrue(original)
                    self.assertTrue(all(Path(call["coverage_target"]) == expected for call in original))
        finally:
            self.root = canonical

    def test_benchmark_explicit_output_preserves_historical_sample(self):
        historical = self.history / "event-pipeline.json"
        historical.write_bytes(b"historical sample\n")
        self.run_mode("Bench")
        self.assertEqual(historical.read_bytes(), b"historical sample\n")
        self.assertTrue((self.output / "event-pipeline.json").exists())


if __name__ == "__main__":
    unittest.main()
