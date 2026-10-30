"""Packaging contract regressions; synthetic files do not claim native UI proof."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import zipfile


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

VIEWER = load("viewer_package", "package-viewer.py")
TRIAL_TESTS = load("viewer_trial_tests", "test-package-trial.py")


class ViewerPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"; self.repo.mkdir()
        (self.repo / "LICENSE").write_text("SYNTHETIC license fixture")
        self.publish = self.root / "publish"; self.publish.mkdir()
        for name in ("Contain.Viewer.exe", "Contain.Viewer.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"):
            (self.publish / name).write_bytes(TRIAL_TESTS.synthetic_pe())
        (self.publish / "Contain.Viewer.pdb").write_bytes(b"SYNTHETIC private symbols excluded")
        (self.publish / "Contain.Viewer.deps.json").write_text('{}')
        self.frameworks = [{"name": name, "version": "8.0.99"} for name in ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")]
        self.config = self.publish / "Contain.Viewer.runtimeconfig.json"
        self.config.write_text(json.dumps({"runtimeOptions": {"tfm": "net8.0", "includedFrameworks": self.frameworks}}))
        self.contain = self.root / "contain.exe"; self.contain.write_bytes(TRIAL_TESTS.synthetic_pe())
        self.fixtures = self.root / "fixtures"; self.fixtures.mkdir()
        (self.fixtures / "viewer-demo.db").write_bytes(b"SYNTHETIC database unit fixture; never native proof")
        self.metadata = dict(synthetic=True, real_etw=False, kind="synthetic_desktop_viewer_fixtures", combined_database="viewer-demo.db",
            databases=[dict(name="viewer-demo.db", bytes=(self.fixtures / "viewer-demo.db").stat().st_size,
                            sha256=VIEWER.sha(self.fixtures / "viewer-demo.db"))])
        (self.fixtures / "metadata.json").write_text(json.dumps(self.metadata))
        self.runtime_packs = self.root / "runtime-packs"
        for framework in self.frameworks:
            directory = self.runtime_packs / (framework['name'].lower() + '.runtime.win-x64') / framework['version']
            directory.mkdir(parents=True)
            (directory / "LICENSE.TXT").write_text("SYNTHETIC runtime license")
            (directory / "THIRD-PARTY-NOTICES.TXT").write_text("SYNTHETIC runtime notices")
        self.package = self.root / "package"
        self.source = 'a' * 40
        self.source_identity = (self.source, 'b' * 64, 'c' * 64, [{"path": "desktop/Contain.Viewer/Program.cs", "sha256": 'd' * 64}])

    def tearDown(self):
        self.temp.cleanup()

    def sample_result(self, ids=None):
        return mock.Mock(returncode=0, stdout=json.dumps({"schema_version":3,"kind":"list_details","data":[
            {"id":identity,"name":"Synthetic " + identity} for identity in (ids or sorted(VIEWER.SAMPLE_IDS))]}).encode())

    def prepare(self):
        args = argparse.Namespace(repository=self.repo, viewer=self.publish, contain=self.contain,
            fixtures=self.fixtures, runtime_directory=None, runtime_packs=self.runtime_packs, output=self.package)
        with mock.patch.object(VIEWER, 'source_identity', return_value=self.source_identity), \
             mock.patch.object(VIEWER.subprocess, 'check_output', return_value='SYNTHETIC compiler identity'), \
             mock.patch.object(VIEWER.subprocess, 'run', return_value=self.sample_result()):
            self.assertEqual(VIEWER.prepare(args), 0)
        return VIEWER.read(self.package / 'BUILD.json')

    def valid_verification(self, build):
        expected = {r['path']:r['sha256'] for r in build['files']}
        return dict(schema_version=1, protocol_revision=VIEWER.VERIFICATION, source_sha=self.source,
            native_windows=True, synthetic=True, actual_cli=True, completed=True, accepted=True,
            real_etw_executed=False, report_generated=True, report_sha256="f"*64,
            package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], planned_checks=sorted(VIEWER.REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.REQUIRED_CHECKS)],
            passed=len(VIEWER.REQUIRED_CHECKS), failed=0, skipped=0, not_run=0)

    def test_freeze_binds_all_runtime_ui_core_and_only_named_synthetic_sample(self):
        build = self.prepare()
        self.assertEqual(build, VIEWER.verify_package(self.package))
        names = {row['path'] for row in build['files']}
        self.assertIn('samples/viewer-demo.db', names)
        self.assertIn('licenses/Microsoft.NETCore.App-thirdpartynotices.txt', names)
        self.assertNotIn('Contain.Viewer.pdb', names)
        self.assertEqual(build['core_profile'], 'debug')
        self.assertEqual(build['viewer_configuration'], 'Release')

    def test_runtime_requires_self_contained_and_both_framework_identities(self):
        for options in ({'tfm':'net8.0','framework':self.frameworks[0]},
                        {'tfm':'net8.0','includedFrameworks':self.frameworks[:1]},
                        {'tfm':'net8.0','includedFrameworks':[dict(f,version='9.0.1') for f in self.frameworks]}):
            self.config.write_text(json.dumps({'runtimeOptions':options}))
            with self.assertRaises(ValueError): VIEWER.runtime_contract(self.publish)

    def test_missing_runtime_file_fails_before_freeze(self):
        (self.publish/'hostpolicy.dll').unlink()
        with self.assertRaises(ValueError): self.prepare()
        self.assertFalse(self.package.exists())

    def test_source_or_sample_marker_and_identity_mismatch_rejected(self):
        for mutate in (lambda m:m.update(synthetic=False), lambda m:m.update(real_etw=True),
                       lambda m:m['databases'][0].update(sha256='0'*64)):
            metadata=copy.deepcopy(self.metadata);mutate(metadata)
            (self.fixtures/'metadata.json').write_text(json.dumps(metadata))
            with self.assertRaises(ValueError), mock.patch.object(VIEWER.subprocess,'run',return_value=self.sample_result()):
                VIEWER.validate_sample(self.contain,self.fixtures)

    def test_sample_must_be_verified_by_actual_cli_contract_not_marker_alone(self):
        with mock.patch.object(VIEWER.subprocess,'run',return_value=self.sample_result(['not-synthetic'])):
            with self.assertRaisesRegex(ValueError,'sessions_unverified'):
                VIEWER.validate_sample(self.contain,self.fixtures)
        with mock.patch.object(VIEWER.subprocess,'run',return_value=mock.Mock(returncode=1,stdout=b'')):
            with self.assertRaisesRegex(ValueError,'cli_failed'):
                VIEWER.validate_sample(self.contain,self.fixtures)

    def test_frozen_file_changes_or_unmanifested_inputs_rejected(self):
        self.prepare(); (self.package/'contain.exe').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError,'frozen_file_changed'): VIEWER.verify_package(self.package)

    def test_unmanifested_database_cannot_be_published(self):
        self.prepare(); (self.package/'private.db').write_bytes(b'not allowed')
        with self.assertRaisesRegex(ValueError,'unmanifested_file'): VIEWER.verify_package(self.package)

    def test_duplicated_manifest_paths_are_rejected(self):
        build=self.prepare();build['files'].append(build['files'][0])
        (self.package/'BUILD.json').write_text(json.dumps(build))
        with self.assertRaisesRegex(ValueError,'duplicate_path'): VIEWER.verify_package(self.package)

    def test_native_verification_requires_every_planned_actual_check_and_identity(self):
        build=self.prepare();good=self.valid_verification(build);manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.verification_matches(good,build,manifest))
        mutations=[lambda d:d.update(actual_cli=False),lambda d:d.update(native_windows=False),
            lambda d:d.update(completed=False),lambda d:d.update(accepted=False),
            lambda d:d.update(real_etw_executed=True),lambda d:d.update(report_generated=False),
            lambda d:d.update(source_sha='0'*40),lambda d:d.update(contain_sha256='0'*64),
            lambda d:d.update(viewer_assembly_sha256='0'*64),lambda d:d.update(viewer_launcher_sha256='0'*64),
            lambda d:d.update(package_manifest_sha256='0'*64),lambda d:d.update(checks=[]),
            lambda d:d['checks'].pop(),lambda d:d['checks'].append(d['checks'][0]),
            lambda d:d['checks'][0].update(status='not_run'),lambda d:d['checks'][0].update(status='failed'),
            lambda d:d['planned_checks'].pop(),lambda d:d.update(passed=0),lambda d:d.update(skipped=1)]
        for mutate in mutations:
            bad=copy.deepcopy(good);mutate(bad)
            self.assertFalse(VIEWER.verification_matches(bad,build,manifest))

    def test_missing_native_verification_produces_diagnostic_zip_but_fails(self):
        self.prepare()
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        args=argparse.Namespace(package=self.package,quality_summary=quality,verification=self.root/'missing.json',output=self.root/'diagnostic.zip')
        self.assertEqual(VIEWER.finalize(args),2)
        with zipfile.ZipFile(args.output) as archive:
            validation=json.loads(archive.read('VALIDATION.json'))
            self.assertFalse(validation['viewer_verified'])
            self.assertFalse(validation['checks']['native_ui_cli_and_export'])
            self.assertEqual(validation['status'],'diagnostic_unverified')
            self.assertIn('samples/viewer-demo.db',archive.namelist())
            self.assertNotIn('Contain.Viewer.pdb',archive.namelist())

    def test_successful_finalization_verifies_all_manifested_zip_bytes(self):
        build=self.prepare();verification=self.root/'verification.json';verification.write_text(json.dumps(self.valid_verification(build)))
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        args=argparse.Namespace(package=self.package,quality_summary=quality,verification=verification,output=self.root/'viewer.zip')
        self.assertEqual(VIEWER.finalize(args),0)
        with zipfile.ZipFile(args.output) as archive:
            self.assertTrue(json.loads(archive.read('VALIDATION.json'))['viewer_verified'])
            contents=json.loads(archive.read('CONTENTS.json'))['files']
            self.assertEqual(set(archive.namelist()),{r['path'] for r in contents}|{'CONTENTS.json'})
            for entry in contents:
                value=archive.read(entry['path'])
                self.assertEqual(len(value),entry['bytes'])
                self.assertEqual(VIEWER.hashlib.sha256(value).hexdigest(),entry['sha256'])

    def test_native_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/Program.cs').read_text()
        section=source.split('PlannedChecks =',1)[1].split('];',1)[0]
        names=set(VIEWER.re.findall(r'"([a-z_]+)"',section))
        self.assertEqual(names,VIEWER.REQUIRED_CHECKS)


if __name__=='__main__': unittest.main()
