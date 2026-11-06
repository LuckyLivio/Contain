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


UPLOAD = load('beta8_upload_tests', 'upload-beta8-assets.py')
FIXTURES = load('beta8_upload_package_fixtures', 'test-package-viewer.py')


class Beta8DeliveryTests(unittest.TestCase):
    def setUp(self):
        # Keep the delivery fixture bound to Beta 8 as later product versions advance.
        version = mock.patch.object(FIXTURES.VIEWER.TRIAL, 'product_version', return_value=UPLOAD.VERSION)
        version.start(); self.addCleanup(version.stop)
        self.fixture = FIXTURES.ViewerPackageTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        f, gate = self.fixture, FIXTURES.VIEWER
        build = f.prepare()
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
        configuration_value = f.valid_configuration_verification(build)
        configuration_value['folder_guidance_verification'] = dict(
            protocol_revision=UPLOAD.FOLDER_PROTOCOL, title='要记录哪个文件夹里的变化？',
            note='通常选软件准备安装到的文件夹。例如，安装包在“下载”里、软件准备装到 D 盘，就选 D 盘上对应的已有文件夹。这里的选择只决定 Contain 观察哪里。',
            missing_hint='请选择一个已有文件夹，再开始记录。',
            visible_before_picker=True, explicit_selection_preserved=True)
        configuration = save('first-use-configuration-summary.json', configuration_value)
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
            passed=len(gate.PROGRESS_REQUIRED_CHECKS), failed=0, skipped=0, not_run=0,
            native_device_dpi=96, guidance_verification=dict(protocol_revision=UPLOAD.GUIDANCE_PROTOCOL,
                accepted=True, late_callback_preserved=True, cases=[dict(name=name, **values, ui_clipboard_details_match=True)
                    for name, values in UPLOAD.GUIDANCE_CASES.items()]))
        self.package = self.inputs / UPLOAD.NAMES[0] / UPLOAD.ZIP
        self.package.parent.mkdir(parents=True)
        args = argparse.Namespace(package=f.package, quality_summary=quality,
            verification=save('summary.json', f.valid_verification(build)),
            personal_records_verification=save('personal-records-summary.json', f.valid_personal_records_verification(build)),
            overview_verification=save('overview-summary.json', f.valid_overview_verification(build)),
            search_verification=save('search-summary.json', f.valid_search_verification(build)),
            search_export_verification=export, first_use_configuration_verification=configuration,
            install_verification=None, layout_verification=save('layout-summary.json', f.valid_layout_verification(build)),
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

    def rewrite_summary(self, key, filename, hash_key, change):
        path = self.evidence / filename
        value = json.loads(path.read_text())
        change(value)
        path.write_text(json.dumps(value))
        self.rewrite_validation(lambda d: d.update({key: value, hash_key: UPLOAD.digest(path)}))

    def change_guidance(self, change):
        self.rewrite_summary('native_progress_verification', 'progress-summary.json',
            'progress_verification_summary_sha256', lambda d: change(d['guidance_verification']))

    def test_exact_artifacts_bind_native_guidance_and_no_new_capture(self):
        identity, files = self.verify()
        self.assertEqual(identity['source_sha'], self.fixture.source)
        self.assertEqual(identity['release_tag'], 'v0.3.2-beta.8')
        self.assertEqual(set(files), {UPLOAD.ZIP, 'QUALITY-SUMMARY.json'})
        self.assertEqual(identity['validation']['status'], 'bounded_desktop_viewer_verified')
        self.assertEqual(len(identity['validation']['native_progress']['guidance_verification']['cases']), 10)
        self.assertIn('New installer or ETW capture', identity['not_executed'])

    def test_wrong_source_or_zip_hash_is_rejected(self):
        for arguments in ({'source': 'f'*40}, {'digest': 'f'*64}):
            with self.subTest(arguments=arguments), self.assertRaises(ValueError):
                self.verify(**arguments)

    def test_existing_desktop_gate_cannot_be_omitted(self):
        self.rewrite_validation(lambda d: d['checks'].pop('native_session_event_search'))
        with self.assertRaisesRegex(ValueError, 'existing desktop gates'):
            self.verify()

    def test_gate_values_require_true_booleans(self):
        self.rewrite_validation(lambda d: d['checks'].update(native_first_use_configuration=1))
        with self.assertRaises(ValueError):
            self.verify()

    def test_old_successful_progress_without_guidance_is_rejected(self):
        self.rewrite_summary('native_progress_verification', 'progress-summary.json',
            'progress_verification_summary_sha256', lambda d: d.pop('guidance_verification'))
        with self.assertRaisesRegex(ValueError, 'Beta 8 guidance'):
            self.verify()

    def test_saved_completed_case_cannot_still_say_wait(self):
        def stale(guidance):
            case = next(row for row in guidance['cases'] if row['name'] == 'confirmed_nonzero_installer_degraded')
            case['next_step'] = '无需继续操作安装器；请等待整理和保存。'
        self.change_guidance(stale)
        with self.assertRaisesRegex(ValueError, 'Beta 8 guidance'):
            self.verify()

    def test_completed_case_must_retain_nonzero_installer_and_degraded_quality(self):
        def changed(guidance):
            case = next(row for row in guidance['cases'] if row['name'] == 'confirmed_nonzero_installer_degraded')
            case['installer_exit_code'] = 0
            case['quality_level'] = 'Complete'
        self.change_guidance(changed)
        with self.assertRaisesRegex(ValueError, 'Beta 8 guidance'):
            self.verify()

    def test_missing_or_duplicate_guidance_cases_are_rejected(self):
        original = self.package.read_bytes()
        path = self.evidence / 'progress-summary.json'; original_summary = path.read_bytes()
        for change in (lambda d: d['cases'].pop(), lambda d: d['cases'].append(d['cases'][0])):
            self.package.write_bytes(original); path.write_bytes(original_summary)
            self.change_guidance(change)
            with self.assertRaisesRegex(ValueError, 'Beta 8 guidance'):
                self.verify()

    def test_copy_or_details_mismatch_is_rejected(self):
        self.change_guidance(lambda d: d['cases'][0].update(ui_clipboard_details_match=False))
        with self.assertRaisesRegex(ValueError, 'Beta 8 guidance'):
            self.verify()

    def test_old_folder_explanation_is_rejected(self):
        self.rewrite_summary('native_first_use_configuration_verification', 'first-use-configuration-summary.json',
            'first_use_configuration_summary_sha256', lambda d: d['folder_guidance_verification'].update(
                note='Contain 会记录这个文件夹里的变化。'))
        with self.assertRaisesRegex(ValueError, 'Beta 8 guidance'):
            self.verify()

    def test_partial_failure_cannot_claim_complete_success(self):
        def stale(guidance):
            case = next(row for row in guidance['cases'] if row['name'] == 'failed_saved_evidence')
            case['result_succeeded'] = True
        self.change_guidance(stale)
        with self.assertRaisesRegex(ValueError, 'Beta 8 guidance'):
            self.verify()

    def test_claiming_new_capture_is_rejected(self):
        self.rewrite_validation(lambda d: d.update(graphical_install_verified=True))
        with self.assertRaisesRegex(ValueError, 'no-new-capture'):
            self.verify()

    def test_changed_native_evidence_is_rejected(self):
        path = self.evidence / 'progress-summary.json'
        value = json.loads(path.read_text()); value['source_sha'] = 'f'*40
        path.write_text(json.dumps(value))
        with self.assertRaisesRegex(ValueError, 'Native summary evidence'):
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
