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


UPLOAD = load('beta9_upload_tests', 'upload-beta9-assets.py')
FIXTURES = load('beta9_upload_package_fixtures', 'test-package-viewer.py')


class Beta9DeliveryTests(unittest.TestCase):
    def setUp(self):
        # Keep the delivery fixture bound to Beta 9 as later product versions advance.
        version = mock.patch.object(FIXTURES.VIEWER.TRIAL, 'product_version', return_value=UPLOAD.VERSION)
        version.start(); self.addCleanup(version.stop)
        self.fixture = FIXTURES.ViewerPackageTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        f, gate = self.fixture, FIXTURES.VIEWER
        f.source_extra_files = {'docs/examples/follow-installer-before.png': UPLOAD.BEFORE_PATH.read_bytes()}
        executable = f.root / 'installer.exe'; executable.write_bytes(FIXTURES.TRIAL_TESTS.synthetic_pe())
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
        configuration_value = f.valid_configuration_verification(build)
        configuration_value['follow_configuration_verification'] = dict(UPLOAD.FOLLOW_CONFIGURATION)
        configuration_value['folder_guidance_verification'] = dict(
            protocol_revision=UPLOAD.FOLDER_PROTOCOL, title='要记录哪个文件夹里的变化？',
            note='通常选软件准备安装到的已有文件夹。安装包的下载位置不代表安装目标；这里仅决定观察范围。',
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
        install_value = f.valid_follow_install_verification(build)
        installation = save('install-summary.json', install_value)
        shutil.copytree(f.root / 'screenshots', self.evidence / 'screenshots')
        layout_value = f.valid_layout_verification(build)
        layout_value['follow_page_verification'] = dict(protocol_revision='contain-follow-layout-verification-1',accepted=True,
            conditions=[dict(name=name,requested_scale=scale,actual_device_dpi=96,window_width=740,window_height=620,
                default_follow=True,no_scope_required=True,start_near_mode=True,primary_before_advanced=True,
                manual_available=True,path_copy_exact=True,keyboard_start_reachable=True) for name,scale in
                [('native_default_layout_resize_maximize',1),('simulated_150_percent_layout',1.5),('simulated_200_percent_layout',2)]])
        self.extended = self.inputs / UPLOAD.EXTENDED_NAME; self.extended.mkdir()
        for name,source in [('baseline-benchmark.json',UPLOAD.BASELINE_SOURCE),('candidate-benchmark.json',f.source)]:
            value=dict(schema_version=1,measurement_kind='synthetic_in_memory',etw_validation='not_performed',
                etw_providers_started=False,sqlite_measured=False,profile='release',source_commit=source,
                working_tree_clean=True,fixture_generation_timed=False,assertions_timed=True,results=[dict(name=case,
                    work_items_per_sample=20000,input_records_per_sample=0 if case=='registry_open_get_close' else 20000,
                    measured_samples=7,warmup_samples=2,validated_checksum=20000,sample_ns_per_work_item=[1,2,3,4,5,6,7])
                    for case in UPLOAD.BENCH_CASES])
            (self.extended/name).write_text(json.dumps(value))
        (self.extended/'benchmark-identity.json').write_text(json.dumps(dict(schema_version=1,source_sha=f.source,
            baseline_sha=UPLOAD.BASELINE_SOURCE,accepted=True,same_runner=True)))
        (self.extended/'fuzz-identity.json').write_text(json.dumps(dict(schema_version=1,source_sha=f.source,accepted=True,
            toolchain='nightly-2026-11-05',seconds_per_target=30,targets=['event_json','lifetime_sequences','completion_correlation'])))
        self.package = self.inputs / UPLOAD.NAMES[0] / UPLOAD.ZIP
        self.package.parent.mkdir(parents=True)
        args = argparse.Namespace(package=f.package, quality_summary=quality,
            verification=save('summary.json', f.valid_verification(build)),
            personal_records_verification=save('personal-records-summary.json', f.valid_personal_records_verification(build)),
            overview_verification=save('overview-summary.json', f.valid_overview_verification(build)),
            search_verification=save('search-summary.json', f.valid_search_verification(build)),
            search_export_verification=export, first_use_configuration_verification=configuration,
            install_verification=installation, layout_verification=save('layout-summary.json', layout_value),
            progress_verification=save('progress-summary.json', progress), output=self.package)
        self.assertEqual(gate.finalize(args), 0)

    def verify(self, source=None, digest=None):
        return UPLOAD.verify_artifacts(self.inputs, source or self.fixture.source, 12345,
            digest or UPLOAD.digest(self.package), 12346)

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

    def test_exact_artifacts_bind_actual_follow_capture_guidance_and_before_after(self):
        identity, files = self.verify()
        self.assertEqual(identity['source_sha'], self.fixture.source)
        self.assertEqual(identity['release_tag'], 'v0.3.2-beta.9')
        self.assertEqual(set(files), {UPLOAD.ZIP, 'QUALITY-SUMMARY.json', 'install-record-before.png', 'install-record-after.png'})
        self.assertEqual(identity['validation']['status'], 'bounded_graphical_install_verified')
        self.assertEqual(len(identity['validation']['native_progress']['guidance_verification']['cases']), 10)
        self.assertEqual(identity['validation']['native_install']['first_use']['ordinary_input_count'],1)
        self.assertEqual(len(identity['screenshots']),2)

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
        with self.assertRaisesRegex(ValueError, 'Beta 9 guidance'):
            self.verify()

    def test_saved_completed_case_cannot_still_say_wait(self):
        def stale(guidance):
            case = next(row for row in guidance['cases'] if row['name'] == 'confirmed_nonzero_installer_degraded')
            case['next_step'] = '无需继续操作安装器；请等待整理和保存。'
        self.change_guidance(stale)
        with self.assertRaisesRegex(ValueError, 'Beta 9 guidance'):
            self.verify()

    def test_completed_case_must_retain_nonzero_installer_and_degraded_quality(self):
        def changed(guidance):
            case = next(row for row in guidance['cases'] if row['name'] == 'confirmed_nonzero_installer_degraded')
            case['installer_exit_code'] = 0
            case['quality_level'] = 'Complete'
        self.change_guidance(changed)
        with self.assertRaisesRegex(ValueError, 'Beta 9 guidance'):
            self.verify()

    def test_missing_or_duplicate_guidance_cases_are_rejected(self):
        original = self.package.read_bytes()
        path = self.evidence / 'progress-summary.json'; original_summary = path.read_bytes()
        for change in (lambda d: d['cases'].pop(), lambda d: d['cases'].append(d['cases'][0])):
            self.package.write_bytes(original); path.write_bytes(original_summary)
            self.change_guidance(change)
            with self.assertRaisesRegex(ValueError, 'Beta 9 guidance'):
                self.verify()

    def test_copy_or_details_mismatch_is_rejected(self):
        self.change_guidance(lambda d: d['cases'][0].update(ui_clipboard_details_match=False))
        with self.assertRaisesRegex(ValueError, 'Beta 9 guidance'):
            self.verify()

    def test_old_folder_explanation_is_rejected(self):
        self.rewrite_summary('native_first_use_configuration_verification', 'first-use-configuration-summary.json',
            'first_use_configuration_summary_sha256', lambda d: d['folder_guidance_verification'].update(
                note='Contain 会记录这个文件夹里的变化。'))
        with self.assertRaisesRegex(ValueError, 'Beta 9 guidance'):
            self.verify()

    def test_partial_failure_cannot_claim_complete_success(self):
        def stale(guidance):
            case = next(row for row in guidance['cases'] if row['name'] == 'failed_saved_evidence')
            case['result_succeeded'] = True
        self.change_guidance(stale)
        with self.assertRaisesRegex(ValueError, 'Beta 9 guidance'):
            self.verify()

    def test_missing_actual_capture_is_rejected(self):
        self.rewrite_validation(lambda d: d.update(graphical_install_verified=False))
        with self.assertRaisesRegex(ValueError, 'actual packaged follow capture'):
            self.verify()

    def test_changed_native_evidence_is_rejected(self):
        path = self.evidence / 'progress-summary.json'
        value = json.loads(path.read_text()); value['source_sha'] = 'f'*40
        path.write_text(json.dumps(value))
        with self.assertRaisesRegex(ValueError, 'Native summary evidence'):
            self.verify()


    def test_manual_only_old_capture_cannot_substitute_for_follow_flow(self):
        self.rewrite_summary('native_install_verification','install-summary.json','install_verification_summary_sha256',
            lambda d: d['first_use'].update(protocol_revision='contain-first-install-flow-1',ordinary_input_count=2))
        with self.assertRaisesRegex(ValueError,'Actual single-input follow'):
            self.verify()

    def test_follow_ui_explain_and_copied_event_must_be_same_actual_child_event(self):
        self.rewrite_summary('native_install_verification','install-summary.json','install_verification_summary_sha256',
            lambda d: d['follow_verification'].update(copied_event_id='different-event'))
        with self.assertRaisesRegex(ValueError,'Actual single-input follow'):
            self.verify()

    def test_default_follow_manual_and_snapshot_configuration_cannot_be_omitted(self):
        self.rewrite_summary('native_first_use_configuration_verification','first-use-configuration-summary.json',
            'first_use_configuration_summary_sha256', lambda d: d['follow_configuration_verification'].update(manual_folder_required=False))
        with self.assertRaisesRegex(ValueError,'configuration regression'):
            self.verify()

    def test_modern_layout_requires_nearby_reachable_primary_action_in_every_condition(self):
        self.rewrite_summary('native_layout_verification','layout-summary.json','layout_verification_summary_sha256',
            lambda d: d['follow_page_verification']['conditions'][2].update(start_near_mode=False))
        with self.assertRaisesRegex(ValueError,'Modern follow page'):
            self.verify()

    def test_actual_after_screenshot_bytes_cannot_be_replaced(self):
        path=self.evidence/'screenshots/install-follow-configured.png';path.write_bytes(b'not actual accepted image')
        with self.assertRaisesRegex(ValueError,'Actual single-input follow'):
            self.verify()

    def test_before_after_dimensions_and_dpi_must_match_measured_native_baseline(self):
        self.rewrite_summary('native_install_verification','install-summary.json','install_verification_summary_sha256',
            lambda d:d['first_use'].update(actual_device_dpi=144))
        with self.assertRaisesRegex(ValueError,'Before/after'):
            self.verify()


    def change_extended(self, name, change):
        path=self.extended/name; value=json.loads(path.read_text()); change(value); path.write_text(json.dumps(value))

    def test_extended_quality_must_use_same_source_accepted_fuzz_and_same_runner(self):
        self.change_extended('fuzz-identity.json',lambda d:d.update(accepted=False))
        with self.assertRaisesRegex(ValueError,'fuzz smoke'):
            self.verify()

    def test_required_seven_benchmark_samples_cannot_be_omitted(self):
        self.change_extended('candidate-benchmark.json',lambda d:d['results'][0]['sample_ns_per_work_item'].pop())
        with self.assertRaisesRegex(ValueError,'Seven validated'):
            self.verify()

    def test_benchmark_case_denominators_and_checksums_must_match_baseline(self):
        self.change_extended('candidate-benchmark.json',lambda d:d['results'][0].update(validated_checksum=123))
        with self.assertRaisesRegex(ValueError,'denominators or checksums'):
            self.verify()

    def test_raw_host_inventory_cannot_be_embedded_in_build_identity(self):
        self.change_extended('baseline-benchmark.json',lambda d:d.update(processor_identifier='SYNTHETIC host inventory'))
        with self.assertRaisesRegex(ValueError,'host metadata'):
            self.verify()

    def test_extended_quality_remote_identity_is_separate_exact_successful_first_push(self):
        run=dict(head_sha=self.fixture.source,event='push',conclusion='success',status='completed',run_attempt=1,
            path='.github/workflows/follow-installer-quality.yml',head_repository=dict(full_name=UPLOAD.REPO))
        artifact=dict(id=7,name=UPLOAD.EXTENDED_NAME,expired=False)
        with mock.patch.object(UPLOAD,'api',side_effect=[run,dict(artifacts=[artifact])]):
            self.assertEqual(UPLOAD.accepted_extended_remote(self.fixture.source,12346),7)
        for key,wrong in [('head_sha','f'*40),('event','workflow_dispatch'),('run_attempt',2),('conclusion','failure'),('path','.github/workflows/ci.yml')]:
            changed=dict(run);changed[key]=wrong
            with self.subTest(key=key),mock.patch.object(UPLOAD,'api',return_value=changed),self.assertRaises(ValueError):
                UPLOAD.accepted_extended_remote(self.fixture.source,12346)

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
