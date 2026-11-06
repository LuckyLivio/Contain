"""Packaging contract regressions; synthetic files do not claim native UI proof."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import struct
import zlib
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
        (self.repo / "Cargo.toml").write_text('[workspace.package]\nversion = "' + VIEWER.TRIAL.product_version() + '"\n')
        (self.repo / "LICENSE").write_text("SYNTHETIC license fixture")
        (self.repo / "desktop").mkdir()
        (self.repo / "desktop/Directory.Build.props").write_text(
            '<Project><PropertyGroup><RuntimeFrameworkVersion>' + VIEWER.runtime_patch() +
            '</RuntimeFrameworkVersion></PropertyGroup></Project>')
        self.publish = self.root / "publish"; self.publish.mkdir()
        for name in ("Contain.Viewer.exe", "Contain.Viewer.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"):
            (self.publish / name).write_bytes(TRIAL_TESTS.synthetic_pe())
        (self.publish / "Contain.Viewer.pdb").write_bytes(b"SYNTHETIC private symbols excluded")
        identity = 'Contain.Viewer/' + VIEWER.TRIAL.product_version()
        (self.publish / "Contain.Viewer.deps.json").write_text(json.dumps({
            'libraries': {identity: {'type': 'project'}}, 'targets': {'SYNTHETIC win-x64': {identity: {}}}}))
        self.frameworks = [{"name": name, "version": VIEWER.runtime_patch()} for name in ("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App")]
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

    def valid_layout_verification(self, build):
        expected = {entry['path']: entry['sha256'] for entry in build['files']}
        return dict(schema_version=1, protocol_revision=VIEWER.LAYOUT_VERIFICATION, source_sha=self.source,
            native_windows=True, synthetic=True, real_etw_executed=False, completed=True, accepted=True,
            package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], planned_checks=sorted(VIEWER.LAYOUT_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.LAYOUT_REQUIRED_CHECKS)],
            passed=4,failed=0,skipped=0,not_run=0,conditions=[
                dict(name=name,requested_scale=scale,test_method=method,device_dpi=96,
                    system_high_dpi_verified=False,working_area_width=1920,working_area_height=1080,assertions=10)
                for name,scale,method in (
                    ('native_default_layout_resize_maximize',1,'native_default'),
                    ('simulated_150_percent_layout',1.5,'simulated_native_controls'),
                    ('simulated_200_percent_layout',2,'simulated_native_controls'))])

    def prepare(self, fixture=None):
        args = argparse.Namespace(repository=self.repo, viewer=self.publish, contain=self.contain,
            fixtures=self.fixtures, fixture=fixture, runtime_directory=None, runtime_packs=self.runtime_packs, output=self.package)
        def archive_source(command):
            self.assertEqual(command[-1], self.source)
            with zipfile.ZipFile(command[command.index('--output') + 1], 'x') as archive:
                archive.writestr('Cargo.toml', 'SYNTHETIC source snapshot')
        with mock.patch.object(VIEWER, 'source_identity', return_value=self.source_identity), \
             mock.patch.object(VIEWER.subprocess, 'check_output', return_value='SYNTHETIC compiler identity'), \
             mock.patch.object(VIEWER.subprocess, 'check_call', side_effect=archive_source), \
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
        self.assertIn('SOURCE.zip', names)
        self.assertEqual(build['source_archive']['source_sha'], self.source)
        self.assertIn('licenses/Microsoft.NETCore.App-thirdpartynotices.txt', names)
        self.assertNotIn('Contain.Viewer.pdb', names)
        self.assertEqual(build['core_profile'], 'debug')
        self.assertEqual(build['viewer_configuration'], 'Release')

    def test_controlled_fixture_is_frozen_with_runtime_imports_and_explicit_usage(self):
        fixture=self.root/'fixture.exe'; fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture)
        self.assertEqual(build, VIEWER.verify_package(self.package))
        self.assertEqual(build['controlled_fixture']['path'], 'contain-test-installer.exe')
        self.assertFalse(build['controlled_fixture']['automatic_execution'])
        self.assertIn('contain-test-installer.exe', build['pe_dependencies'])
        self.assertEqual(VIEWER.sha(fixture), build['controlled_fixture']['sha256'])
        usage=(self.package/'TRY.md').read_text(encoding='utf-8')
        self.assertIn('%TEMP%', usage)
        self.assertIn('--root', usage)
        self.assertIn('不要加引号', usage)
        (self.package/'contain-test-installer.exe').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'frozen_file_changed'): VIEWER.verify_package(self.package)

    def valid_install_verification(self, build):
        expected={r['path']:r['sha256'] for r in build['files']}
        return dict(schema_version=1, protocol_revision=VIEWER.INSTALL_VERIFICATION,
            source_sha=self.source, native_windows=True, gui_initiated=True, real_etw_executed=True,
            completed=True, accepted=True, package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], fixture_sha256=expected[VIEWER.FIXTURE_PATH],
            planned_checks=sorted(VIEWER.INSTALL_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.INSTALL_REQUIRED_CHECKS)],
            passed=len(VIEWER.INSTALL_REQUIRED_CHECKS), failed=0, skipped=0, not_run=0,
            installer_started=True, installer_exit_code=0, collection_finished=True, data_saved=True,
            capture_state='finished', quality_level='Degraded', providers_active=True, real_source_event_count=1,
            independent_truth_verified=True, session_id='aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
            event_id='bbbbbbbb-cccc-dddd-eeee-ffffffffffff', report_generated=True, report_sha256='f'*64,
            actual_packaged_executable_capture=True, default_database_used_without_path_interaction=True,
            cold_restart_verified=True, working_directory_changed=True,
            first_process_id=1234, restarted_process_id=5678, first_process_exit_code=0,
            restart_session_id='aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
            restart_event_id='bbbbbbbb-cccc-dddd-eeee-ffffffffffff',
            first_use=self.valid_first_use(build),
            manual_checks=[dict(name='native_picker_navigation_and_cancel', status='passed')])

    def valid_first_use(self, build):
        directory = self.root / 'screenshots'; directory.mkdir(exist_ok=True)
        images = []
        for index, name in enumerate(sorted(VIEWER.FIRST_USE_SCREENSHOTS)):
            # Unit-test image bytes are synthetic, never claims about native UI.
            width, height = 16, 12
            def chunk(kind, data):
                return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
            data = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0))
                + chunk(b'IDAT', zlib.compress((b'\x00' + bytes([index, 0, 0]) * width) * height)) + chunk(b'IEND', b''))
            path = directory / name; path.write_bytes(data)
            images.append(dict(name=name, sha256=VIEWER.sha(path), width=width, height=height))
        return dict(protocol_revision='contain-first-install-flow-1', actual_packaged_executable=True,
            windows_version='10.0.26100.0', actual_device_dpi=96, installer_picker_cancels=1,
            installer_picker_selections=1, observation_picker_selections=1, cancel_preserved_record_selection=True,
            missing_scope_blocks_start=True, configuration_created_no_session=True, ordinary_input_count=2,
            advanced_interactions=0, environment_check_interactions=0, database_path_interactions=0,
            keyboard_flow_verified=True, default_name_matches_exe=True, default_database=True,
            selected_scope_sha256='a'*64, persisted_scope_sha256='a'*64, scope_exact_match=True,
            saved_scope_count=1, session_id='aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
            main_entry_kept_active_task=True, duplicate_start_created_one_session=True, invalid_scope_blocked=True,
            database_conflict_blocked=True, corrections_preserved_configuration=True, screenshots=images)

    def valid_configuration_verification(self, build):
        expected = {entry['path']: entry['sha256'] for entry in build['files']}
        return dict(schema_version=1, protocol_revision=VIEWER.FIRST_USE_CONFIGURATION_VERIFICATION,
            source_sha=self.source, native_windows=True, synthetic=True, real_etw_executed=False,
            real_user_records_accessed=False, completed=True, accepted=True, retries=0, assertions=15,
            package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], planned_checks=sorted(VIEWER.FIRST_USE_CONFIGURATION_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.FIRST_USE_CONFIGURATION_REQUIRED_CHECKS)],
            passed=5, failed=0, skipped=0, not_run=0)

    def configuration_verification_file(self, build):
        path = self.root / (self.package.name + '-first-use-configuration.json')
        path.write_text(json.dumps(self.valid_configuration_verification(build)))
        return path

    def test_install_acceptance_requires_exact_frozen_gui_etw_result(self):
        fixture=self.root/'fixture.exe';fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture);good=self.valid_install_verification(build);manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.install_verification_matches(good,build,manifest))
        mutations=[lambda d:d.update(gui_initiated=False),lambda d:d.update(real_etw_executed=False),
            lambda d:d.update(fixture_sha256='0'*64),lambda d:d.update(data_saved=False),
            lambda d:d.update(collection_finished=False),lambda d:d.update(capture_state='capturing'),
            lambda d:d.update(session_id='latest'),lambda d:d.update(event_id=''),lambda d:d.update(report_generated=False),
            lambda d:d.update(installer_started=False),lambda d:d.update(installer_exit_code=None),
            lambda d:d.update(independent_truth_verified=False),lambda d:d.update(real_source_event_count=0),
            lambda d:d['checks'].pop(),lambda d:d['checks'].append(d['checks'][0]),
            lambda d:d['checks'][0].update(status='not_run'),lambda d:d.update(not_run=1),
            lambda d:d.update(actual_packaged_executable_capture=False),
            lambda d:d.update(default_database_used_without_path_interaction=False),
            lambda d:d.update(cold_restart_verified=False), lambda d:d.update(working_directory_changed=False),
            lambda d:d.update(restart_session_id='another-session'), lambda d:d.update(restart_event_id='another-event'),
            lambda d:d.update(first_process_id=0), lambda d:d.update(first_process_id=False),
            lambda d:d.update(restarted_process_id=1234), lambda d:d.update(restarted_process_id=None),
            lambda d:d.update(first_process_exit_code=1), lambda d:d.update(first_process_exit_code=False),
            lambda d:d.pop('first_process_id'), lambda d:d.pop('first_process_exit_code')]
        for mutate in mutations:
            bad=copy.deepcopy(good);mutate(bad)
            self.assertFalse(VIEWER.install_verification_matches(bad,build,manifest))

    def test_fixture_package_cannot_pass_with_viewer_verification_alone(self):
        fixture=self.root/'fixture.exe';fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture)
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        verification=self.root/'verification.json';verification.write_text(json.dumps(self.valid_verification(build)))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),output=self.root/'diagnostic.zip')
        self.assertEqual(VIEWER.finalize(args),2)
        with zipfile.ZipFile(args.output) as archive:
            result=json.loads(archive.read('VALIDATION.json'))
            self.assertFalse(result['graphical_install_verified'])
            self.assertFalse(result['checks']['native_gui_install_etw_and_exact_result'])

    def test_graphical_package_finalization_keeps_manual_omissions_and_real_result(self):
        fixture=self.root/'fixture.exe';fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture)
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        verification=self.root/'verification.json';verification.write_text(json.dumps(self.valid_verification(build)))
        installation=self.valid_install_verification(build)
        installation['manual_checks']=[dict(name='native_picker_navigation_and_cancel',status='not_run')]
        install=self.root/'install.json';install.write_text(json.dumps(installation))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),
            install_verification=install,output=self.root/'install.zip')
        self.assertEqual(VIEWER.finalize(args),2)
        with zipfile.ZipFile(args.output) as archive:
            result=json.loads(archive.read('VALIDATION.json'))
            self.assertFalse(result['graphical_install_automated_verified'])
            self.assertTrue(result['checks']['native_gui_install_etw_and_exact_result'])
            self.assertFalse(result['checks']['native_first_use_install_flow'])
            self.assertFalse(result['graphical_install_verified'])
            self.assertFalse(result['graphical_install_milestone_closed'])
            self.assertEqual(result['status'],'diagnostic_unverified')
            self.assertEqual(result['native_install_verification']['manual_checks'][0]['status'],'not_run')

    def test_milestone_requires_actual_native_picker_navigation_and_cancel(self):
        good=dict(manual_checks=[dict(name='native_picker_navigation_and_cancel',status='passed')])
        self.assertTrue(VIEWER.native_picker_verified(good))
        for rows in ([],[dict(name='native_picker_navigation_and_cancel',status='not_run')],
                     [dict(name='native_picker_navigation_and_cancel',status='failed')],
                     [dict(name='injected_dialog_path',status='passed')],good['manual_checks']*2,[None]):
            self.assertFalse(VIEWER.native_picker_verified(dict(manual_checks=rows)))

    def test_graphical_milestone_closes_only_after_product_and_actual_picker_pass(self):
        fixture=self.root/'fixture.exe';fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture)
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        verification=self.root/'verification.json';verification.write_text(json.dumps(self.valid_verification(build)))
        result=self.valid_install_verification(build)
        result['manual_checks']=[dict(name='native_picker_navigation_and_cancel',status='passed')]
        install=self.root/'install.json';install.write_text(json.dumps(result))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),
            install_verification=install,output=self.root/'install.zip')
        self.assertEqual(VIEWER.finalize(args),0)
        with zipfile.ZipFile(args.output) as archive:
            result=json.loads(archive.read('VALIDATION.json'))
            self.assertTrue(result['graphical_install_milestone_closed'])
            self.assertEqual(result['status'],'bounded_graphical_install_verified')

    def test_executed_native_picker_failure_fails_finalization_and_retains_package(self):
        fixture=self.root/'fixture.exe';fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture)
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        verification=self.root/'verification.json';verification.write_text(json.dumps(self.valid_verification(build)))
        result=self.valid_install_verification(build)
        result['manual_checks']=[dict(name='native_picker_navigation_and_cancel',status='failed',reason='wrong_selection')]
        install=self.root/'install.json';install.write_text(json.dumps(result))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),
            install_verification=install,output=self.root/'install-failed.zip')
        self.assertEqual(VIEWER.finalize(args),2)
        with zipfile.ZipFile(args.output) as archive:
            result=json.loads(archive.read('VALIDATION.json'))
            self.assertFalse(result['graphical_install_automated_verified'])
            self.assertTrue(result['checks']['native_gui_install_etw_and_exact_result'])
            self.assertFalse(result['checks']['native_first_use_install_flow'])
            self.assertFalse(result['graphical_install_milestone_closed'])
            self.assertEqual(result['status'],'diagnostic_native_picker_failed')
            self.assertEqual(result['native_install_verification']['manual_checks'][0]['status'],'failed')

    def test_runtime_requires_self_contained_and_both_framework_identities(self):
        for options in ({'tfm':'net8.0','framework':self.frameworks[0]},
                        {'tfm':'net8.0','includedFrameworks':self.frameworks[:1]},
                        {'tfm':'net8.0','includedFrameworks':[dict(f,version='8.0.22') for f in self.frameworks]},
                        {'tfm':'net8.0','includedFrameworks':[dict(f,version='9.0.1') for f in self.frameworks]}):
            self.config.write_text(json.dumps({'runtimeOptions':options}))
            with self.assertRaises(ValueError): VIEWER.runtime_contract(self.publish)

    def test_viewer_publish_and_build_manifest_require_current_canonical_beta_version(self):
        version=VIEWER.TRIAL.product_version()
        build=self.prepare()
        self.assertEqual(build['version'],version)
        self.assertIn(version,(self.package/'TRY.md').read_text(encoding='utf-8'))
        build['version']='0.3.1'
        (self.package/'BUILD.json').write_text(json.dumps(build))
        with self.assertRaisesRegex(ValueError,'build_identity_invalid'): VIEWER.verify_package(self.package)
        (self.publish/'Contain.Viewer.deps.json').write_text(json.dumps({
            'libraries': {'Contain.Viewer/0.3.1': {}}, 'targets': {'SYNTHETIC win-x64': {'Contain.Viewer/0.3.1': {}}}}))
        with self.assertRaisesRegex(ValueError,'product_version_mismatch'): VIEWER.runtime_contract(self.publish)

    def test_layout_requires_exact_native_package_and_explicit_simulated_scale_evidence(self):
        build=self.prepare();good=self.valid_layout_verification(build);manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.layout_verification_matches(good,build,manifest))
        mutations=[lambda d:d.update(native_windows=False),lambda d:d.update(synthetic=False),
            lambda d:d.update(completed=False),lambda d:d.update(accepted=False),lambda d:d.update(real_etw_executed=True),
            lambda d:d.update(source_sha='0'*40),lambda d:d.update(package_manifest_sha256='0'*64),
            lambda d:d.update(viewer_assembly_sha256='0'*64),lambda d:d.update(viewer_launcher_sha256='0'*64),
            lambda d:d.update(contain_sha256='0'*64),lambda d:d.update(checks=[]),lambda d:d['checks'].pop(),
            lambda d:d['checks'].append(d['checks'][0]),lambda d:d['checks'][0].update(status='not_run'),
            lambda d:d['planned_checks'].pop(),lambda d:d.update(passed=True),lambda d:d.update(not_run=1),
            lambda d:d.update(conditions=[]),lambda d:d['conditions'].append(d['conditions'][0]),
            lambda d:d['conditions'][1].update(system_high_dpi_verified=True),
            lambda d:d['conditions'][1].update(test_method='native_default'),
            lambda d:d['conditions'][1].update(requested_scale=1),lambda d:d['conditions'][0].update(device_dpi=0),
            lambda d:d['conditions'][0].update(working_area_width=0),lambda d:d['conditions'][0].update(assertions=0),
            lambda d:d['checks'][0].update(name=[]),lambda d:d.update(checks=[None])]
        for mutate in mutations:
            bad=copy.deepcopy(good);mutate(bad)
            self.assertFalse(VIEWER.layout_verification_matches(bad,build,manifest))

    def test_beta_layout_gate_missing_summary_retains_diagnostic_zip_and_fails(self):
        build=self.prepare();verification=self.root/'verification.json';verification.write_text(json.dumps(self.valid_verification(build)))
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),
            layout_verification=self.root/'layout-missing.json',output=self.root/'layout-diagnostic.zip')
        self.assertEqual(VIEWER.finalize(args),2)
        with zipfile.ZipFile(args.output) as archive:
            validation=json.loads(archive.read('VALIDATION.json'))
            self.assertFalse(validation['viewer_verified'])
            self.assertFalse(validation['checks']['native_layout_and_simulated_scale'])
            self.assertEqual(validation['status'],'diagnostic_unverified')
            self.assertFalse(validation['system_high_dpi_verified'])

    def test_beta_layout_gate_embeds_exact_evidence_without_claiming_system_high_dpi(self):
        build=self.prepare();verification=self.root/'verification.json';verification.write_text(json.dumps(self.valid_verification(build)))
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        evidence=self.valid_layout_verification(build);layout=self.root/'layout.json';layout.write_text(json.dumps(evidence))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),
            layout_verification=layout,output=self.root/'layout-verified.zip')
        self.assertEqual(VIEWER.finalize(args),0)
        with zipfile.ZipFile(args.output) as archive:
            validation=json.loads(archive.read('VALIDATION.json'))
            self.assertTrue(validation['viewer_verified'])
            self.assertEqual(validation['native_layout_verification'],evidence)
            self.assertEqual(validation['layout_verification_summary_sha256'],VIEWER.sha(layout))
            self.assertFalse(validation['system_high_dpi_verified'])

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
        build=self.prepare()
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=self.root/'missing.json',
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),output=self.root/'diagnostic.zip')
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
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            personal_records_verification=self.personal_verification_file(build),output=self.root/'viewer.zip')
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
        # Exercise the Windows non-UTF-8 default even on the Linux preflight.
        with mock.patch('io.text_encoding', lambda encoding, *args: encoding or 'cp1252'):
            source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/Program.cs').read_text(encoding='utf-8')
        section=source.split('PlannedChecks =',1)[1].split('];',1)[0]
        names=set(VIEWER.re.findall(r'"([a-z_]+)"',section))
        self.assertEqual(names,VIEWER.REQUIRED_CHECKS)

    def test_layout_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/LayoutVerification.cs').read_text(encoding='utf-8')
        section=source.split('Plan =',1)[1].split('];',1)[0]
        names=set(VIEWER.re.findall(r'"([a-z_0-9]+)"',section))
        self.assertEqual(names,VIEWER.LAYOUT_REQUIRED_CHECKS)

    def test_progress_evidence_requires_complete_plan_and_exact_package(self):
        build = self.prepare()
        expected = {entry['path']: entry['sha256'] for entry in build['files']}
        manifest = VIEWER.sha(self.package/'BUILD.json')
        progress = dict(schema_version=1, protocol_revision=VIEWER.PROGRESS_VERIFICATION,
            source_sha=self.source, native_windows=True, synthetic=True, real_etw_executed=False,
            completed=True, accepted=True, package_manifest_sha256=manifest,
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], contain_sha256=expected['contain.exe'],
            planned_checks=sorted(VIEWER.PROGRESS_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.PROGRESS_REQUIRED_CHECKS)],
            passed=len(VIEWER.PROGRESS_REQUIRED_CHECKS), failed=0, skipped=0, not_run=0)
        self.assertTrue(VIEWER.progress_verification_matches(progress, build, manifest))
        for field, wrong in (('source_sha','f'*40), ('viewer_assembly_sha256','f'*64),
                             ('contain_sha256','f'*64), ('package_manifest_sha256','f'*64),
                             ('real_etw_executed',True), ('accepted',False)):
            changed = copy.deepcopy(progress); changed[field] = wrong
            self.assertFalse(VIEWER.progress_verification_matches(changed, build, manifest), field)
        changed = copy.deepcopy(progress); changed['checks'][0]['status'] = 'not_run'
        self.assertFalse(VIEWER.progress_verification_matches(changed, build, manifest))
        changed = copy.deepcopy(progress); changed['checks'].append(changed['checks'][0])
        self.assertFalse(VIEWER.progress_verification_matches(changed, build, manifest))
        changed = copy.deepcopy(progress); changed['planned_checks'].pop()
        self.assertFalse(VIEWER.progress_verification_matches(changed, build, manifest))

    def test_progress_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/InstallProgressChecks.cs').read_text(encoding='utf-8')
        section=source.split('Plan =',1)[1].split('];',1)[0]
        self.assertEqual(set(VIEWER.re.findall(r'"([a-z_]+)"',section)),VIEWER.PROGRESS_REQUIRED_CHECKS)


    def valid_personal_records_verification(self, build):
        expected = {entry['path']: entry['sha256'] for entry in build['files']}
        return dict(schema_version=1, protocol_revision=VIEWER.PERSONAL_RECORDS_VERIFICATION,
            source_sha=self.source, native_windows=True, synthetic=True, real_etw_executed=False,
            completed=True, accepted=True, actual_packaged_executable=True, cold_restart_verified=True,
            working_directory_changed=True, real_user_records_accessed=False, retries=0,
            initial_session_id='synthetic-finished', initial_event_id='synthetic-finished-event-0',
            restart_session_id='synthetic-finished', restart_event_id='synthetic-finished-event-0',
            first_process_id=1234, restarted_process_id=5678, first_process_exit_code=0,
            package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], planned_checks=sorted(VIEWER.PERSONAL_RECORDS_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.PERSONAL_RECORDS_REQUIRED_CHECKS)],
            passed=8, failed=0, skipped=0, not_run=0)

    def personal_verification_file(self, build):
        path=self.root/(self.package.name+'-personal-verification.json')
        path.write_text(json.dumps(self.valid_personal_records_verification(build)))
        return path

    def test_personal_records_evidence_requires_exact_native_restart_and_package(self):
        build=self.prepare();good=self.valid_personal_records_verification(build);manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.personal_records_verification_matches(good,build,manifest))
        # Offline identities are exact opaque IDs, not necessarily UUIDs.
        self.assertEqual(good['initial_session_id'],'synthetic-finished')
        mutations = [
            ('schema_version', True), ('protocol_revision', VIEWER.VERIFICATION),
            ('source_sha','0'*40), ('native_windows',False), ('synthetic',False), ('real_etw_executed',True),
            ('completed',False), ('accepted',False), ('actual_packaged_executable',False),
            ('cold_restart_verified',False), ('working_directory_changed',False),
            ('real_user_records_accessed',True), ('retries',1), ('retries',False),
            ('package_manifest_sha256','0'*64), ('viewer_assembly_sha256','0'*64),
            ('viewer_launcher_sha256','0'*64), ('contain_sha256','0'*64),
            ('initial_session_id',''), ('initial_event_id',' '), ('initial_session_id',None),
            ('restart_session_id','wrong-session'), ('restart_event_id','wrong-event'),
            ('first_process_id',0), ('first_process_id',True), ('restarted_process_id',-1),
            ('restarted_process_id',good['first_process_id']), ('first_process_exit_code',1),
            ('first_process_exit_code',False), ('passed',7), ('passed',True),
            ('failed',1), ('skipped',1), ('not_run',1), ('failed',False),
            ('checks',None), ('checks',[None]), ('checks',[dict(name=[],status='passed')]),
            ('planned_checks',None), ('planned_checks',[{}]),
        ]
        for field,wrong in mutations:
            with self.subTest(field=field,wrong=wrong):
                changed=copy.deepcopy(good);changed[field]=wrong
                self.assertFalse(VIEWER.personal_records_verification_matches(changed,build,manifest))
        for field in good:
            with self.subTest(missing=field):
                changed=copy.deepcopy(good);changed.pop(field)
                self.assertFalse(VIEWER.personal_records_verification_matches(changed,build,manifest))
        for mutate in [lambda x:x['checks'].pop(), lambda x:x['checks'].append(x['checks'][0]),
                       lambda x:x['checks'][0].update(status='failed'), lambda x:x['checks'][0].update(status='not_run'),
                       lambda x:x['planned_checks'].pop(), lambda x:x['planned_checks'].append(x['planned_checks'][0])]:
            changed=copy.deepcopy(good);mutate(changed)
            self.assertFalse(VIEWER.personal_records_verification_matches(changed,build,manifest))

    def test_personal_records_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/PersonalRecordsVerification.cs').read_text(encoding='utf-8')
        section=source.split('Plan =',1)[1].split('];',1)[0]
        names=VIEWER.re.findall(r'"([a-z_]+)"',section)
        self.assertEqual(len(names),8)
        self.assertEqual(len(names),len(set(names)))
        self.assertEqual(set(names),VIEWER.PERSONAL_RECORDS_REQUIRED_CHECKS)

    def test_personal_records_gate_requires_valid_evidence_and_retains_failure(self):
        for mode in ('passed','missing','failed'):
            with self.subTest(mode=mode):
                # Each frozen fixture gets its own directory; finalization never overwrites.
                self.package=self.root/('personal-package-'+mode)
                build=self.prepare();manifest=VIEWER.sha(self.package/'BUILD.json')
                verification=self.root/(mode+'-viewer.json');verification.write_text(json.dumps(self.valid_verification(build)))
                quality=self.root/(mode+'-quality.json');quality.write_text(json.dumps(dict(source_revision=self.source,
                    current_required_gates_passed=True,all_recorded_invocations_passed=True,
                    coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
                personal=self.root/(mode+'-personal.json')
                evidence=self.valid_personal_records_verification(build)
                if mode=='failed': evidence['cold_restart_verified']=False
                if mode!='missing': personal.write_text(json.dumps(evidence))
                args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
                    overview_verification=self.overview_verification_file(build),
                    personal_records_verification=personal,output=self.root/(mode+'-personal.zip'))
                self.assertEqual(VIEWER.finalize(args),0 if mode=='passed' else 2)
                with zipfile.ZipFile(args.output) as archive:
                    result=json.loads(archive.read('VALIDATION.json'))
                    self.assertEqual(result['checks']['native_personal_records_and_cold_restart'],mode=='passed')
                    self.assertEqual(result['native_personal_records_verification'],evidence if mode!='missing' else {})
                    self.assertEqual(result['personal_records_verification_summary_sha256'],VIEWER.sha(personal) if mode!='missing' else None)
                    self.assertEqual(result['build_manifest_sha256'],manifest)

    def test_omitted_personal_verification_argument_fails_closed_and_keeps_diagnostic_zip(self):
        build=self.prepare()
        verification=self.root/'viewer.json';verification.write_text(json.dumps(self.valid_verification(build)))
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        # Even a valid unused file cannot satisfy an omitted command-line handoff.
        personal=self.personal_verification_file(build)
        self.assertTrue(VIEWER.personal_records_verification_matches(
            VIEWER.read(personal),build,VIEWER.sha(self.package/'BUILD.json')))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            overview_verification=self.overview_verification_file(build),
            output=self.root/'personal-argument-omitted.zip')
        self.assertFalse(hasattr(args,'personal_records_verification'))
        self.assertEqual(VIEWER.finalize(args),2)
        with zipfile.ZipFile(args.output) as archive:
            result=json.loads(archive.read('VALIDATION.json'))
            self.assertTrue(result['checks']['native_ui_cli_and_export'])
            self.assertFalse(result['checks']['native_personal_records_and_cold_restart'])
            self.assertTrue(result['checks']['native_persisted_overview_and_navigation'])
            self.assertFalse(result['viewer_verified'])
            self.assertEqual(result['status'],'diagnostic_unverified')
            self.assertEqual(result['native_personal_records_verification'],{})
            self.assertIsNone(result['personal_records_verification_summary_sha256'])
            self.assertIn('samples/viewer-demo.db',archive.namelist())

    def valid_overview_verification(self, build):
        expected = {entry['path']: entry['sha256'] for entry in build['files']}
        return dict(schema_version=1, protocol_revision=VIEWER.OVERVIEW_VERIFICATION,
            source_sha=self.source, native_windows=True, synthetic=True, real_etw_executed=False,
            completed=True, accepted=True, actual_packaged_executable=True, cold_restart_verified=True,
            working_directory_changed=True, real_user_records_accessed=False, retries=0,
            initial_session_id='synthetic-finished', initial_event_id='synthetic-finished-event-0',
            restart_session_id='synthetic-finished', restart_event_id='synthetic-finished-event-0',
            first_process_id=1234, restarted_process_id=5678, first_process_exit_code=0,
            package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], planned_checks=sorted(VIEWER.OVERVIEW_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.OVERVIEW_REQUIRED_CHECKS)],
            passed=10, failed=0, skipped=0, not_run=0)

    def overview_verification_file(self, build):
        path=self.root/(self.package.name+'-overview-verification.json')
        path.write_text(json.dumps(self.valid_overview_verification(build)))
        return path

    def test_overview_evidence_requires_exact_native_restart_and_package(self):
        build=self.prepare();good=self.valid_overview_verification(build);manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.overview_verification_matches(good,build,manifest))
        mutations = [
            ('schema_version',True), ('protocol_revision',VIEWER.PERSONAL_RECORDS_VERIFICATION),
            ('source_sha','0'*40), ('native_windows',False), ('synthetic',False), ('real_etw_executed',True),
            ('completed',False), ('accepted',False), ('actual_packaged_executable',False),
            ('cold_restart_verified',False), ('working_directory_changed',False),
            ('real_user_records_accessed',True), ('retries',1), ('retries',False),
            ('package_manifest_sha256','0'*64), ('viewer_assembly_sha256','0'*64),
            ('viewer_launcher_sha256','0'*64), ('contain_sha256','0'*64),
            ('initial_session_id',''), ('initial_event_id',' '), ('initial_session_id',None),
            ('restart_session_id','wrong-session'), ('restart_event_id','wrong-event'),
            ('first_process_id',0), ('first_process_id',True), ('restarted_process_id',-1),
            ('restarted_process_id',good['first_process_id']), ('first_process_exit_code',1),
            ('first_process_exit_code',False), ('passed',9), ('passed',True),
            ('failed',1), ('skipped',1), ('not_run',1), ('failed',False),
            ('checks',None), ('checks',[None]), ('checks',[dict(name=[],status='passed')]),
            ('planned_checks',None), ('planned_checks',[{}]),
        ]
        for field,wrong in mutations:
            with self.subTest(field=field,wrong=wrong):
                changed=copy.deepcopy(good);changed[field]=wrong
                self.assertFalse(VIEWER.overview_verification_matches(changed,build,manifest))
        for field in good:
            with self.subTest(missing=field):
                changed=copy.deepcopy(good);changed.pop(field)
                self.assertFalse(VIEWER.overview_verification_matches(changed,build,manifest))
        for mutate in [lambda x:x['checks'].pop(), lambda x:x['checks'].append(x['checks'][0]),
                       lambda x:x['checks'][0].update(status='failed'), lambda x:x['checks'][0].update(status='not_run'),
                       lambda x:x['planned_checks'].pop(), lambda x:x['planned_checks'].append(x['planned_checks'][0])]:
            changed=copy.deepcopy(good);mutate(changed)
            self.assertFalse(VIEWER.overview_verification_matches(changed,build,manifest))

    def test_overview_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/OverviewVerification.cs').read_text(encoding='utf-8')
        section=source.split('Plan =',1)[1].split('];',1)[0]
        names=VIEWER.re.findall(r'"([a-z_]+)"',section)
        self.assertEqual(len(names),10)
        self.assertEqual(len(names),len(set(names)))
        self.assertEqual(set(names),VIEWER.OVERVIEW_REQUIRED_CHECKS)

    def test_overview_gate_requires_valid_evidence_and_retains_failure(self):
        for mode in ('passed','missing','failed'):
            with self.subTest(mode=mode):
                self.package=self.root/('overview-package-'+mode)
                build=self.prepare();manifest=VIEWER.sha(self.package/'BUILD.json')
                verification=self.root/(mode+'-viewer.json');verification.write_text(json.dumps(self.valid_verification(build)))
                quality=self.root/(mode+'-quality.json');quality.write_text(json.dumps(dict(source_revision=self.source,
                    current_required_gates_passed=True,all_recorded_invocations_passed=True,
                    coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
                overview=self.root/(mode+'-overview.json')
                evidence=self.valid_overview_verification(build)
                if mode=='failed': evidence['cold_restart_verified']=False
                if mode!='missing': overview.write_text(json.dumps(evidence))
                args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
                    personal_records_verification=self.personal_verification_file(build),
                    overview_verification=overview,output=self.root/(mode+'-overview.zip'))
                self.assertEqual(VIEWER.finalize(args),0 if mode=='passed' else 2)
                with zipfile.ZipFile(args.output) as archive:
                    result=json.loads(archive.read('VALIDATION.json'))
                    self.assertTrue(result['checks']['native_personal_records_and_cold_restart'])
                    self.assertEqual(result['checks']['native_persisted_overview_and_navigation'],mode=='passed')
                    self.assertEqual(result['native_overview_verification'],evidence if mode!='missing' else {})
                    self.assertEqual(result['overview_verification_summary_sha256'],VIEWER.sha(overview) if mode!='missing' else None)
                    self.assertEqual(result['build_manifest_sha256'],manifest)

    def test_omitted_overview_argument_fails_closed_and_keeps_diagnostic_zip(self):
        build=self.prepare()
        verification=self.root/'viewer.json';verification.write_text(json.dumps(self.valid_verification(build)))
        quality=self.root/'quality.json';quality.write_text(json.dumps(dict(source_revision=self.source,
            current_required_gates_passed=True,all_recorded_invocations_passed=True,
            coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
        # Unused evidence cannot satisfy an omitted command-line handoff.
        overview=self.overview_verification_file(build)
        self.assertTrue(VIEWER.overview_verification_matches(
            VIEWER.read(overview),build,VIEWER.sha(self.package/'BUILD.json')))
        args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),search_verification=self.search_verification_file(build),quality_summary=quality,verification=verification,
            personal_records_verification=self.personal_verification_file(build),
            output=self.root/'overview-argument-omitted.zip')
        self.assertFalse(hasattr(args,'overview_verification'))
        self.assertEqual(VIEWER.finalize(args),2)
        with zipfile.ZipFile(args.output) as archive:
            result=json.loads(archive.read('VALIDATION.json'))
            self.assertTrue(result['checks']['native_ui_cli_and_export'])
            self.assertTrue(result['checks']['native_personal_records_and_cold_restart'])
            self.assertFalse(result['checks']['native_persisted_overview_and_navigation'])
            self.assertFalse(result['viewer_verified'])
            self.assertEqual(result['status'],'diagnostic_unverified')
            self.assertEqual(result['native_overview_verification'],{})
            self.assertIsNone(result['overview_verification_summary_sha256'])
            self.assertIn('samples/viewer-demo.db',archive.namelist())

    def valid_search_verification(self, build):
        expected = {entry['path']: entry['sha256'] for entry in build['files']}
        ids = [f"synthetic-search-event-{index:03d}" for index in range(70, 123)]
        return dict(schema_version=1, protocol_revision=VIEWER.SEARCH_VERIFICATION,
            source_sha=self.source, native_windows=True, synthetic=True, real_etw_executed=False,
            completed=True, accepted=True, actual_packaged_executable=True,
            real_user_records_accessed=False, retries=0, actual_device_dpi=96, windows_version='10.0.26100',
            total_matches=53, first_page_count=50, second_page_count=3,
            expected_ids=ids, first_page_ids=ids[:50], second_page_ids=ids[50:],
            selected_event_id='synthetic-search-event-120', explain_event_id='synthetic-search-event-120',
            copied_event_id='synthetic-search-event-120',
            package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], planned_checks=sorted(VIEWER.SEARCH_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.SEARCH_REQUIRED_CHECKS)],
            passed=9, failed=0, skipped=0, not_run=0)

    def search_verification_file(self, build):
        path=self.root/(self.package.name+'-search-verification.json')
        path.write_text(json.dumps(self.valid_search_verification(build)))
        return path

    def test_search_evidence_requires_exact_pages_native_package_and_explain_identity(self):
        build=self.prepare();good=self.valid_search_verification(build);manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.search_verification_matches(good,build,manifest))
        mutations = [
            ('schema_version',True), ('protocol_revision',VIEWER.OVERVIEW_VERIFICATION),
            ('source_sha','0'*40), ('native_windows',False), ('synthetic',False), ('real_etw_executed',True),
            ('completed',False), ('accepted',False), ('actual_packaged_executable',False),
            ('real_user_records_accessed',True), ('retries',1), ('retries',False),
            ('actual_device_dpi',0), ('actual_device_dpi',True), ('windows_version',' '),
            ('package_manifest_sha256','0'*64), ('viewer_assembly_sha256','0'*64),
            ('viewer_launcher_sha256','0'*64), ('contain_sha256','0'*64),
            ('total_matches',130), ('first_page_count',53), ('second_page_count',0),
            ('total_matches',True), ('selected_event_id','wrong'), ('copied_event_id','wrong'),
            ('explain_event_id','synthetic-search-other-event-120'), ('expected_ids',[]),
            ('first_page_ids',good['second_page_ids']), ('second_page_ids',good['first_page_ids']),
            ('passed',8), ('passed',True), ('failed',1), ('skipped',1), ('not_run',1), ('failed',False),
            ('checks',None), ('checks',[None]), ('checks',[dict(name=[],status='passed')]),
            ('planned_checks',None), ('planned_checks',[{}]),
        ]
        for field,wrong in mutations:
            with self.subTest(field=field,wrong=wrong):
                changed=copy.deepcopy(good);changed[field]=wrong
                self.assertFalse(VIEWER.search_verification_matches(changed,build,manifest))
        for field in good:
            with self.subTest(missing=field):
                changed=copy.deepcopy(good);changed.pop(field)
                self.assertFalse(VIEWER.search_verification_matches(changed,build,manifest))
        for mutate in [lambda x:x['checks'].pop(), lambda x:x['checks'].append(x['checks'][0]),
                       lambda x:x['checks'][0].update(status='failed'), lambda x:x['checks'][0].update(status='not_run'),
                       lambda x:x['planned_checks'].pop(), lambda x:x['planned_checks'].append(x['planned_checks'][0])]:
            changed=copy.deepcopy(good);mutate(changed)
            self.assertFalse(VIEWER.search_verification_matches(changed,build,manifest))

    def test_search_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/SearchVerification.cs').read_text(encoding='utf-8')
        section=source.split('Plan =',1)[1].split('];',1)[0]
        names=VIEWER.re.findall(r'"([a-z_]+)"',section)
        self.assertEqual(len(names),9)
        self.assertEqual(len(names),len(set(names)))
        self.assertEqual(set(names),VIEWER.SEARCH_REQUIRED_CHECKS)

    def test_search_gate_requires_valid_evidence_and_retains_failure(self):
        for mode in ('passed','missing','failed','omitted'):
            with self.subTest(mode=mode):
                self.package=self.root/('search-package-'+mode)
                build=self.prepare();manifest=VIEWER.sha(self.package/'BUILD.json')
                verification=self.root/(mode+'-viewer.json');verification.write_text(json.dumps(self.valid_verification(build)))
                quality=self.root/(mode+'-quality.json');quality.write_text(json.dumps(dict(source_revision=self.source,
                    current_required_gates_passed=True,all_recorded_invocations_passed=True,
                    coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
                search=self.root/(mode+'-search.json')
                evidence=self.valid_search_verification(build)
                if mode=='failed': evidence['explain_event_id']='synthetic-search-other-event-120'
                if mode!='missing': search.write_text(json.dumps(evidence))
                args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),search_export_verification=self.export_verification_file(build),quality_summary=quality,verification=verification,
                    personal_records_verification=self.personal_verification_file(build),
                    overview_verification=self.overview_verification_file(build),output=self.root/(mode+'-search.zip'))
                if mode!='omitted': args.search_verification=search
                self.assertEqual(VIEWER.finalize(args),0 if mode=='passed' else 2)
                with zipfile.ZipFile(args.output) as archive:
                    result=json.loads(archive.read('VALIDATION.json'))
                    self.assertTrue(result['checks']['native_ui_cli_and_export'])
                    self.assertTrue(result['checks']['native_personal_records_and_cold_restart'])
                    self.assertTrue(result['checks']['native_persisted_overview_and_navigation'])
                    self.assertEqual(result['checks']['native_session_event_search'],mode=='passed')
                    self.assertEqual(result['native_search_verification'],evidence if mode not in ('missing','omitted') else {})
                    self.assertEqual(result['search_verification_summary_sha256'],VIEWER.sha(search) if mode not in ('missing','omitted') else None)
                    self.assertEqual(result['build_manifest_sha256'],manifest)
                    self.assertEqual(result['viewer_verified'],mode=='passed')
                    self.assertIn('samples/viewer-demo.db',archive.namelist())

    def valid_export_verification(self, build):
        expected = {entry['path']: entry['sha256'] for entry in build['files']}
        ids = [f"synthetic-search-event-{index:03d}" for index in range(70, 123)]
        report = self.root / (self.package.name + '-export') / 'reports/search-export-synthetic.html'
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text('<!doctype html><html><body><h1>SYNTHETIC test report</h1>'
            '<table id="matched-events"><tbody>' + ''.join('<tr data-event-id="' + identity + '"><td>' + identity + '</td><td>time</td><td>operation</td><td>resource</td><td>Unknown</td></tr>'
            for identity in ids) + '</tbody></table></body></html>', encoding='utf-8')
        return dict(schema_version=1, protocol_revision=VIEWER.SEARCH_EXPORT_VERIFICATION,
            source_sha=self.source, native_windows=True, synthetic=True, real_etw_executed=False,
            completed=True, accepted=True, actual_packaged_executable=True, native_save_dialog=True,
            real_user_records_accessed=False, retries=0, actual_device_dpi=96, windows_version='10.0.26100',
            event_count=53, report_event_ids=ids, page_before_offset=50, page_after_offset=50,
            page_after_ids=ids[50:], selected_event_id='synthetic-search-event-120',
            explain_event_id='synthetic-search-event-120', copied_event_id='synthetic-search-event-120',
            package_manifest_sha256=VIEWER.sha(self.package/'BUILD.json'),
            viewer_assembly_sha256=expected['Contain.Viewer.dll'], viewer_launcher_sha256=expected['Contain.Viewer.exe'],
            contain_sha256=expected['contain.exe'], planned_checks=sorted(VIEWER.SEARCH_EXPORT_REQUIRED_CHECKS),
            checks=[dict(name=name,status='passed') for name in sorted(VIEWER.SEARCH_EXPORT_REQUIRED_CHECKS)],
            report=dict(path='reports/search-export-synthetic.html',bytes=report.stat().st_size,sha256=VIEWER.sha(report)),
            passed=8, failed=0, skipped=0, not_run=0)

    def export_verification_file(self, build):
        evidence=self.valid_export_verification(build)
        path=self.root/(self.package.name+'-export')/'export-summary.json'
        path.write_text(json.dumps(evidence))
        return path

    def test_search_export_requires_actual_exe_native_save_dialog_and_preserved_page(self):
        build=self.prepare();path=self.export_verification_file(build);good=VIEWER.read(path);manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.search_export_verification_matches(good,build,manifest,path))
        mutations=[('schema_version',True),('protocol_revision',VIEWER.SEARCH_VERIFICATION),('source_sha','0'*40),
            ('native_windows',False),('synthetic',False),('real_etw_executed',True),('real_user_records_accessed',True),
            ('completed',False),('accepted',False),('actual_packaged_executable',False),('native_save_dialog',False),
            ('actual_device_dpi',0),('actual_device_dpi',True),('windows_version',' '),('retries',1),('retries',False),
            ('package_manifest_sha256','0'*64),('viewer_assembly_sha256','0'*64),('viewer_launcher_sha256','0'*64),('contain_sha256','0'*64),
            ('event_count',50),('event_count',True),('report_event_ids',good['report_event_ids'][::-1]),
            ('page_before_offset',0),('page_after_offset',0),('page_after_ids',[]),
            ('selected_event_id','wrong'),('copied_event_id','wrong'),('explain_event_id','synthetic-search-other-event-120'),
            ('passed',7),('passed',True),('failed',1),('skipped',1),('not_run',1),('failed',False),
            ('checks',None),('checks',[None]),('checks',[dict(name=[],status='passed')]),('planned_checks',None),('planned_checks',[{}])]
        for field,wrong in mutations:
            with self.subTest(field=field,wrong=wrong):
                changed=copy.deepcopy(good);changed[field]=wrong
                self.assertFalse(VIEWER.search_export_verification_matches(changed,build,manifest,path))
        for field in good:
            with self.subTest(missing=field):
                changed=copy.deepcopy(good);changed.pop(field)
                self.assertFalse(VIEWER.search_export_verification_matches(changed,build,manifest,path))
        for mutate in [lambda x:x['checks'].pop(),lambda x:x['checks'].append(x['checks'][0]),
                lambda x:x['checks'][0].update(status='failed'),lambda x:x['planned_checks'].pop()]:
            changed=copy.deepcopy(good);mutate(changed)
            self.assertFalse(VIEWER.search_export_verification_matches(changed,build,manifest,path))

    def test_search_export_reads_every_actual_html_id_and_binds_report_bytes(self):
        build=self.prepare();path=self.export_verification_file(build);good=VIEWER.read(path);manifest=VIEWER.sha(self.package/'BUILD.json')
        report=path.parent/good['report']['path'];original=report.read_text(encoding='utf-8')
        # Correct summary counts/IDs cannot conceal a substituted, missing or repeated middle row.
        for text in (original.replace('synthetic-search-event-090','synthetic-search-other-event-090'),
                     original.replace('synthetic-search-event-090','synthetic-search-event-091'),
                     original.replace('<tr data-event-id="synthetic-search-event-090"><td>synthetic-search-event-090</td><td>time</td><td>operation</td><td>resource</td><td>Unknown</td></tr>',''),
                     original.replace('id="matched-events"','id="ordinary-events"'),
                     original.replace('<td>synthetic-search-event-090</td>','<td>wrong-visible-id</td>'),
                     original.replace('</body>','<script>alert(1)</script></body>')):
            report.write_text(text,encoding='utf-8');changed=copy.deepcopy(good)
            changed['report'].update(bytes=report.stat().st_size,sha256=VIEWER.sha(report))
            self.assertFalse(VIEWER.search_export_verification_matches(changed,build,manifest,path))
        report.write_text(original+' ',encoding='utf-8')
        self.assertFalse(VIEWER.search_export_verification_matches(good,build,manifest,path))
        report.write_text(original,encoding='utf-8')
        for field,wrong in [('path','../outside.html'),('bytes',True),('bytes',64*1024*1024+1),('sha256','f'*64)]:
            changed=copy.deepcopy(good);changed['report'][field]=wrong
            self.assertFalse(VIEWER.search_export_verification_matches(changed,build,manifest,path))
        report.unlink()
        self.assertFalse(VIEWER.search_export_verification_matches(good,build,manifest,path))

    def test_search_export_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/SearchExportVerification.cs').read_text(encoding='utf-8')
        section=source.split('Plan =',1)[1].split('];',1)[0]
        names=VIEWER.re.findall(r'"([a-z_]+)"',section)
        self.assertEqual(len(names),8)
        self.assertEqual(len(names),len(set(names)))
        self.assertEqual(set(names),VIEWER.SEARCH_EXPORT_REQUIRED_CHECKS)

    def test_search_export_gate_requires_valid_summary_and_actual_report(self):
        for mode in ('passed','omitted','missing','failed','report_missing'):
            with self.subTest(mode=mode):
                self.package=self.root/('export-package-'+mode);build=self.prepare()
                verification=self.root/(mode+'-viewer.json');verification.write_text(json.dumps(self.valid_verification(build)))
                quality=self.root/(mode+'-quality.json');quality.write_text(json.dumps(dict(source_revision=self.source,
                    current_required_gates_passed=True,all_recorded_invocations_passed=True,
                    coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
                exported=self.export_verification_file(build);evidence=VIEWER.read(exported)
                if mode=='failed': evidence['native_save_dialog']=False;exported.write_text(json.dumps(evidence))
                if mode=='missing': exported.unlink()
                if mode=='report_missing': (exported.parent/evidence['report']['path']).unlink()
                args=argparse.Namespace(package=self.package,first_use_configuration_verification=self.configuration_verification_file(build),quality_summary=quality,verification=verification,
                    personal_records_verification=self.personal_verification_file(build),
                    overview_verification=self.overview_verification_file(build),search_verification=self.search_verification_file(build),
                    output=self.root/(mode+'-export.zip'))
                if mode!='omitted': args.search_export_verification=exported
                self.assertEqual(VIEWER.finalize(args),0 if mode=='passed' else 2)
                with zipfile.ZipFile(args.output) as archive:
                    result=json.loads(archive.read('VALIDATION.json'))
                    self.assertEqual(result['checks']['native_search_export'],mode=='passed')
                    self.assertEqual(result['native_search_export_verification'],evidence if mode not in ('omitted','missing') else {})
                    self.assertEqual(result['search_export_verification_summary_sha256'],VIEWER.sha(exported) if mode not in ('omitted','missing') else None)
                    self.assertEqual(result['viewer_verified'],mode=='passed')

    def test_first_use_configuration_requires_complete_frozen_controls_evidence(self):
        build=self.prepare(); good=self.valid_configuration_verification(build); manifest=VIEWER.sha(self.package/'BUILD.json')
        self.assertTrue(VIEWER.first_use_configuration_verification_matches(good,build,manifest))
        for field, wrong in [('source_sha','b'*40),('native_windows',False),('synthetic',False),
                ('real_etw_executed',True),('real_user_records_accessed',True),('accepted',False),('completed',False),
                ('package_manifest_sha256','0'*64),('viewer_assembly_sha256','0'*64),('contain_sha256','0'*64),
                ('viewer_launcher_sha256','0'*64),('schema_version',True),('retries',1),('retries',False),
                ('assertions',0),('assertions',True),('passed',4),('failed',1),('skipped',1),('not_run',1),
                ('checks',[None]),('checks',[]),('planned_checks',[None]),('planned_checks',[])]:
            with self.subTest(field=field):
                bad=copy.deepcopy(good);bad[field]=wrong
                self.assertFalse(VIEWER.first_use_configuration_verification_matches(bad,build,manifest))
        for field in good:
            bad=copy.deepcopy(good);bad.pop(field)
            self.assertFalse(VIEWER.first_use_configuration_verification_matches(bad,build,manifest),field)
        for mutate in [lambda x:x['checks'].append(x['checks'][0]), lambda x:x['checks'][0].update(status='failed'),
                lambda x:x['planned_checks'].append(x['planned_checks'][0])]:
            bad=copy.deepcopy(good);mutate(bad)
            self.assertFalse(VIEWER.first_use_configuration_verification_matches(bad,build,manifest))

    def test_first_use_configuration_required_plan_matches_actual_harness(self):
        source=(Path(__file__).parent.parent/'desktop/Contain.Viewer.Tests/FirstUseConfigurationChecks.cs').read_text(encoding='utf-8')
        section=source.split('Plan =',1)[1].split('];',1)[0]
        names=VIEWER.re.findall(r'"([a-z_]+)"',section)
        self.assertEqual(len(names),5)
        self.assertEqual(len(names),len(set(names)))
        self.assertEqual(set(names),VIEWER.FIRST_USE_CONFIGURATION_REQUIRED_CHECKS)

    def test_first_use_capture_requires_two_inputs_native_actions_and_exact_saved_scope(self):
        fixture=self.root/'fixture.exe'; fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture); good=self.valid_install_verification(build); manifest=VIEWER.sha(self.package/'BUILD.json')
        path=self.root/'install-summary.json'
        self.assertTrue(VIEWER.first_use_install_verification_matches(good,build,manifest,path))
        for field,wrong in [('protocol_revision','old'),('actual_packaged_executable',False),('windows_version',' '),
                ('actual_device_dpi',0),('actual_device_dpi',True),('installer_picker_cancels',0),
                ('installer_picker_selections',0),('observation_picker_selections',0),('ordinary_input_count',3),
                ('advanced_interactions',1),('environment_check_interactions',1),('database_path_interactions',1),
                ('saved_scope_count',2),('selected_scope_sha256','private path'),('persisted_scope_sha256','b'*64),
                ('session_id','another-session'),('screenshots',[]),('screenshots',[None])]:
            with self.subTest(field=field):
                bad=copy.deepcopy(good);bad['first_use'][field]=wrong
                self.assertFalse(VIEWER.first_use_install_verification_matches(bad,build,manifest,path))
        for field in good['first_use']:
            bad=copy.deepcopy(good);bad['first_use'].pop(field)
            self.assertFalse(VIEWER.first_use_install_verification_matches(bad,build,manifest,path),field)
        for field,value in good['first_use'].items():
            if value is True:
                bad=copy.deepcopy(good);bad['first_use'][field]=False
                self.assertFalse(VIEWER.first_use_install_verification_matches(bad,build,manifest,path),field)
        bad=copy.deepcopy(good);bad.pop('first_use')
        self.assertFalse(VIEWER.first_use_install_verification_matches(bad,build,manifest,path))
        self.assertFalse(VIEWER.first_use_install_verification_matches(good,build,manifest,None))

    def test_first_use_screenshots_require_actual_distinct_png_bytes(self):
        fixture=self.root/'fixture.exe'; fixture.write_bytes(TRIAL_TESTS.synthetic_pe())
        build=self.prepare(fixture); good=self.valid_install_verification(build); path=self.root/'install-summary.json'
        first=good['first_use']; image=self.root/'screenshots'/first['screenshots'][0]['name']; original=image.read_bytes()
        self.assertTrue(VIEWER.first_use_screenshots_match(first,path))
        for broken in [b'not a PNG', original+b'garbage', original[:30], original[:40]+b'X'+original[41:]]:
            image.write_bytes(broken);bad=copy.deepcopy(first);bad['screenshots'][0]['sha256']=VIEWER.sha(image)
            self.assertFalse(VIEWER.first_use_screenshots_match(bad,path))
        image.write_bytes(original)
        for field,wrong in [('name','../outside.png'),('sha256','0'*64),('width',True),('height',13)]:
            bad=copy.deepcopy(first);bad['screenshots'][0][field]=wrong
            self.assertFalse(VIEWER.first_use_screenshots_match(bad,path))
        second=self.root/'screenshots'/first['screenshots'][1]['name'];second.write_bytes(original)
        bad=copy.deepcopy(first);bad['screenshots'][1]['sha256']=VIEWER.sha(second)
        self.assertFalse(VIEWER.first_use_screenshots_match(bad,path))
        image.unlink()
        self.assertFalse(VIEWER.first_use_screenshots_match(first,path))

    def test_first_use_configuration_gate_cannot_be_omitted_from_viewer_package(self):
        for mode in ('passed','omitted','missing','failed'):
            with self.subTest(mode=mode):
                self.package=self.root/('first-use-configuration-'+mode);build=self.prepare()
                verification=self.root/(mode+'-viewer.json');verification.write_text(json.dumps(self.valid_verification(build)))
                quality=self.root/(mode+'-quality.json');quality.write_text(json.dumps(dict(source_revision=self.source,
                    current_required_gates_passed=True,all_recorded_invocations_passed=True,
                    coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
                configuration=self.configuration_verification_file(build);evidence=VIEWER.read(configuration)
                if mode=='failed': evidence['checks'][0]['status']='failed';configuration.write_text(json.dumps(evidence))
                if mode=='missing': configuration.unlink()
                args=argparse.Namespace(package=self.package,quality_summary=quality,verification=verification,
                    personal_records_verification=self.personal_verification_file(build),
                    overview_verification=self.overview_verification_file(build),search_verification=self.search_verification_file(build),
                    search_export_verification=self.export_verification_file(build),output=self.root/(mode+'-configuration.zip'))
                if mode!='omitted': args.first_use_configuration_verification=configuration
                self.assertEqual(VIEWER.finalize(args),0 if mode=='passed' else 2)
                with zipfile.ZipFile(args.output) as archive:
                    result=json.loads(archive.read('VALIDATION.json'))
                    self.assertEqual(result['checks']['native_first_use_configuration'],mode=='passed')
                    self.assertEqual(result['native_first_use_configuration_verification'],evidence if mode not in ('omitted','missing') else {})
                    self.assertEqual(result['first_use_configuration_summary_sha256'],VIEWER.sha(configuration) if mode not in ('omitted','missing') else None)
                    self.assertEqual(result['viewer_verified'],mode=='passed')

    def test_first_use_capture_gate_cannot_reuse_older_install_success(self):
        for mode in ('passed','omitted','wrong_scope','missing_screenshot'):
            with self.subTest(mode=mode):
                self.package=self.root/('first-use-install-'+mode)
                fixture=self.root/(mode+'-fixture.exe');fixture.write_bytes(TRIAL_TESTS.synthetic_pe());build=self.prepare(fixture)
                verification=self.root/(mode+'-viewer.json');verification.write_text(json.dumps(self.valid_verification(build)))
                quality=self.root/(mode+'-quality.json');quality.write_text(json.dumps(dict(source_revision=self.source,
                    current_required_gates_passed=True,all_recorded_invocations_passed=True,
                    coverage_floors=dict(lines_percent=66.5,functions_percent=70))))
                installation=self.valid_install_verification(build)
                if mode=='omitted': installation.pop('first_use')
                if mode=='wrong_scope': installation['first_use']['persisted_scope_sha256']='b'*64
                if mode=='missing_screenshot': (self.root/'screenshots'/next(iter(VIEWER.FIRST_USE_SCREENSHOTS))).unlink()
                install=self.root/(mode+'-install.json');install.write_text(json.dumps(installation))
                args=argparse.Namespace(package=self.package,quality_summary=quality,verification=verification,
                    personal_records_verification=self.personal_verification_file(build),
                    overview_verification=self.overview_verification_file(build),search_verification=self.search_verification_file(build),
                    search_export_verification=self.export_verification_file(build),
                    first_use_configuration_verification=self.configuration_verification_file(build),
                    install_verification=install,output=self.root/(mode+'-first-use.zip'))
                self.assertEqual(VIEWER.finalize(args),0 if mode=='passed' else 2)
                with zipfile.ZipFile(args.output) as archive:
                    result=json.loads(archive.read('VALIDATION.json'))
                    self.assertTrue(result['checks']['native_gui_install_etw_and_exact_result'])
                    self.assertEqual(result['checks']['native_first_use_install_flow'],mode=='passed')
                    self.assertEqual(result['viewer_verified'],mode=='passed')

    def test_try_guidance_defines_current_session_search_and_report_boundary(self):
        usage=VIEWER.usage(self.source)
        for required in ('搜索本次记录','全部事件','ASCII','% 和 _','导出全部 N 条匹配结果','同一只读数据视图','已有文件不覆盖','导出本页报告'):
            self.assertIn(required,usage)
        self.assertNotIn('搜索结果完整导出留待',usage)
        self.assertNotIn('事件筛选作用于当前页',usage)
        self.assertNotIn('当前页筛选不会搜索全库',usage)

    def test_try_guidance_uses_personal_history_and_explicit_advanced_location(self):
        usage=VIEWER.usage(self.source,install=True)
        for required in ('我的记录','无需选择数据库','重新打开程序','%LOCALAPPDATA%',
                         '使用自定义保存位置','打开外部数据库不会改变新记录的默认保存位置'):
            self.assertIn(required,usage)
        self.assertNotIn('数据库选择观察目录',usage)
        self.assertIn('普通流程只需 EXE 和已有观察文件夹',usage)
        self.assertIn('检查环境不是开始前的额外步骤',usage)
        self.assertNotIn('完成环境检查后点击开始',usage)


if __name__=='__main__': unittest.main()
