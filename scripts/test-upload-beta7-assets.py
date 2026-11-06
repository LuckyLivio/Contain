"""Release guards over synthetic artifacts; no network, upload or native claims."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import unittest
from unittest import mock
import zipfile


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


UPLOAD = load('beta7_upload_tests', 'upload-beta7-assets.py')
FIXTURES = load('beta7_upload_package_fixtures', 'test-package-viewer.py')


class Beta7DeliveryTests(unittest.TestCase):
    def setUp(self):
        self.fixture = FIXTURES.ViewerPackageTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        f, gate = self.fixture, FIXTURES.VIEWER
        executable = f.root / 'installer.exe'
        executable.write_bytes(FIXTURES.TRIAL_TESTS.synthetic_pe())
        build = f.prepare(executable)
        self.inputs = f.root / 'input'
        self.evidence = self.inputs / UPLOAD.NAMES[2]
        self.evidence.mkdir(parents=True)
        quality = self.inputs / UPLOAD.NAMES[1] / 'summary.json'
        quality.parent.mkdir(parents=True)
        quality.write_text(json.dumps(dict(source_revision=f.source, current_required_gates_passed=True,
            all_recorded_invocations_passed=True, coverage_floors=dict(lines_percent=66.5, functions_percent=70),
            nextest_junit=dict(passed=1), coverage_workspace=dict(export_totals={}),
            coverage_original_packages=dict(export_totals={}))))
        def save(name, value):
            path = self.evidence / name
            path.write_text(json.dumps(value))
            return path
        install = save('install-summary.json', f.valid_install_verification(build))
        shutil.copytree(f.root / 'screenshots', self.evidence / 'screenshots')
        configuration = save('first-use-configuration-summary.json', f.valid_configuration_verification(build))
        exported = f.export_verification_file(build)
        shutil.copytree(exported.parent / 'reports', self.evidence / 'reports')
        export = save('export-summary.json', gate.read(exported))
        expected = {item['path']: item['sha256'] for item in build['files']}
        progress = dict(schema_version=1, protocol_revision=gate.PROGRESS_VERIFICATION,
            source_sha=f.source, native_windows=True, synthetic=True, real_etw_executed=False,
            completed=True, accepted=True, package_manifest_sha256=gate.sha(f.package / 'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], contain_sha256=expected['contain.exe'],
            planned_checks=sorted(gate.PROGRESS_REQUIRED_CHECKS),
            checks=[dict(name=name, status='passed') for name in sorted(gate.PROGRESS_REQUIRED_CHECKS)],
            passed=len(gate.PROGRESS_REQUIRED_CHECKS), failed=0, skipped=0, not_run=0)
        self.package = self.inputs / UPLOAD.NAMES[0] / UPLOAD.ZIP
        self.package.parent.mkdir(parents=True)
        args = argparse.Namespace(package=f.package, quality_summary=quality,
            verification=save('summary.json', f.valid_verification(build)),
            personal_records_verification=save('personal-records-summary.json', f.valid_personal_records_verification(build)),
            overview_verification=save('overview-summary.json', f.valid_overview_verification(build)),
            search_verification=save('search-summary.json', f.valid_search_verification(build)),
            search_export_verification=export, first_use_configuration_verification=configuration,
            install_verification=install, layout_verification=save('layout-summary.json', f.valid_layout_verification(build)),
            progress_verification=save('progress-summary.json', progress), output=self.package)
        self.assertEqual(gate.finalize(args), 0)

    def verify(self, source=None, digest=None):
        return UPLOAD.verify_artifacts(self.inputs, source or self.fixture.source, 12345,
            digest or UPLOAD.digest(self.package))

    def rewrite_validation(self, change):
        # Build a self-consistent, but semantically invalid candidate. The
        # release guard must reject it even when its supplied ZIP hash agrees.
        with zipfile.ZipFile(self.package) as archive:
            contents = {name: archive.read(name) for name in archive.namelist()}
        validation = json.loads(contents['VALIDATION.json'])
        change(validation)
        contents['VALIDATION.json'] = json.dumps(validation).encode()
        manifest = json.loads(contents['CONTENTS.json'])
        for item in manifest['files']:
            if item['path'] == 'VALIDATION.json':
                item['bytes'] = len(contents['VALIDATION.json'])
                item['sha256'] = hashlib.sha256(contents['VALIDATION.json']).hexdigest()
        contents['CONTENTS.json'] = json.dumps(manifest).encode()
        with zipfile.ZipFile(self.package, 'w') as archive:
            for name, data in contents.items():
                archive.writestr(name, data)

    def test_exact_artifacts_produce_first_use_identity_and_two_unmodified_screenshots(self):
        identity, files = self.verify()
        self.assertEqual(identity['source_sha'], self.fixture.source)
        self.assertEqual(identity['release_tag'], 'v0.3.2-beta.7')
        self.assertEqual(set(files), {UPLOAD.ZIP, 'QUALITY-SUMMARY.json'} | FIXTURES.VIEWER.FIRST_USE_SCREENSHOTS)
        self.assertEqual(identity['validation']['native_install']['first_use']['ordinary_input_count'], 2)
        self.assertTrue(all(row['actual_device_dpi'] == 96 for row in identity['screenshots']))
        for row in identity['screenshots']:
            self.assertEqual(row['sha256'], UPLOAD.digest(files[row['filename']]))

    def test_wrong_source_or_zip_hash_is_rejected(self):
        for arguments in ({'source': 'f'*40}, {'digest': 'f'*64}):
            with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                self.verify(**arguments)

    def test_old_install_success_cannot_omit_first_use_gate(self):
        self.rewrite_validation(lambda d: d['checks'].pop('native_first_use_install_flow'))
        with self.assertRaisesRegex(ValueError, 'first-use capture'):
            self.verify()

    def test_older_desktop_regression_cannot_be_omitted(self):
        self.rewrite_validation(lambda d: d['checks'].pop('native_session_event_search'))
        with self.assertRaisesRegex(ValueError, 'all desktop regressions'):
            self.verify()

    def test_gate_values_require_true_booleans(self):
        self.rewrite_validation(lambda d: d['checks'].update(native_first_use_configuration=1))
        with self.assertRaises(ValueError):
            self.verify()

    def test_changed_screenshot_is_rejected_even_if_zip_stays_accepted(self):
        screenshot = self.evidence / 'screenshots' / 'install-first-use-configured.png'
        screenshot.write_bytes(screenshot.read_bytes() + b'changed')
        with self.assertRaisesRegex(ValueError, 'screenshots differ'):
            self.verify()

    def test_changed_install_evidence_is_rejected(self):
        path = self.evidence / 'install-summary.json'
        value = json.loads(path.read_text())
        value['first_use']['persisted_scope_sha256'] = 'b'*64
        path.write_text(json.dumps(value))
        with self.assertRaisesRegex(ValueError, 'install evidence differs'):
            self.verify()

    def test_remote_run_must_be_exact_successful_first_push_and_unique_artifacts(self):
        run = dict(head_sha=self.fixture.source, event='push', conclusion='success', status='completed',
            run_attempt=1, path='.github/workflows/ci.yml', head_repository=dict(full_name=UPLOAD.REPO))
        artifacts = [dict(id=index+1, name=name, expired=False) for index, name in enumerate(UPLOAD.NAMES)]
        with mock.patch.object(UPLOAD, 'api', side_effect=[run, dict(artifacts=artifacts)]):
            self.assertEqual(set(UPLOAD.accepted_remote(self.fixture.source, 12345)), set(UPLOAD.NAMES))
        for key, wrong in [('head_sha', 'f'*40), ('event', 'pull_request'), ('conclusion', 'failure'),
                ('run_attempt', 2), ('path', 'another.yml'), ('head_repository', dict(full_name='another/repository'))]:
            invalid=copy.deepcopy(run); invalid[key]=wrong
            with self.subTest(key=key), mock.patch.object(UPLOAD, 'api', return_value=invalid), self.assertRaises(ValueError):
                UPLOAD.accepted_remote(self.fixture.source, 12345)
        for rows in [artifacts[:-1], artifacts + artifacts[:1], [dict(row, expired=True) for row in artifacts]]:
            with mock.patch.object(UPLOAD, 'api', side_effect=[run, dict(artifacts=rows)]), self.assertRaises(ValueError):
                UPLOAD.accepted_remote(self.fixture.source, 12345)

    def test_only_one_exact_existing_draft_can_be_selected(self):
        draft=dict(id=77, tag_name=UPLOAD.TAG, target_commitish=self.fixture.source, draft=True, prerelease=True)
        with mock.patch.object(UPLOAD, 'api', side_effect=[[draft], draft]) as api:
            self.assertEqual(UPLOAD.existing_draft(self.fixture.source)['id'],77)
            self.assertEqual(api.call_args_list, [mock.call('releases?per_page=100'), mock.call('releases/77')])
        for rows in [[], [draft, draft]]:
            with mock.patch.object(UPLOAD, 'api', return_value=rows), self.assertRaises(ValueError):
                UPLOAD.existing_draft(self.fixture.source)
        for key,wrong in [('tag_name','v0.3.2-beta.6'),('target_commitish','f'*40),('draft',False),('prerelease',False)]:
            invalid=dict(draft);invalid[key]=wrong
            with self.subTest(key=key), mock.patch.object(UPLOAD, 'api', return_value=invalid), self.assertRaises(ValueError):
                UPLOAD.existing_draft(self.fixture.source, 77)


if __name__ == '__main__':
    unittest.main()
