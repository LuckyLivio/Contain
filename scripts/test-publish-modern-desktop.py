"""Fail-closed modern release gates using isolated, labelled synthetic reports."""
import copy
import argparse
import hashlib
import importlib.util
import json
import io
from pathlib import Path
import struct
import shutil
import tempfile
import unittest
from unittest.mock import patch
import zipfile


SPEC = importlib.util.spec_from_file_location("modern_delivery", Path(__file__).with_name("publish-modern-desktop.py"))
DELIVERY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DELIVERY)
SOURCE = "1" * 40
SESSION = "22222222-2222-4222-8222-222222222222"
EVENT = "33333333-3333-4333-8333-333333333333"


def model():
    return dict(protocol="contain-modern-model-checks-1", accepted=True, synthetic=True, native_windows_ui=False,
                real_etw=False, checks=list(DELIVERY.MODEL_CHECKS), passed=len(DELIVERY.MODEL_CHECKS), failure=None, message=None)


def checks(names):
    return dict(checks=[dict(name=name, status="passed") for name in names], passed=len(names), failed=0, not_run=0)


def native_inputs(archive):
    storage = dict(failed=0, quota_dropped=0, quota_bytes=67108864, persisted=100, committed_bytes=10000)
    oracle = dict(schema_version=1, protocol="contain-follow-installer-experiment-1", source_sha=SOURCE,
        synthetic=False, controlled_fixture=True, maximum_native_captures=1, retries=0, accepted=True, session_id=SESSION,
        ownership_claim=False, current_path_claim=False, installation_before_baseline=False, post_install_file_reads=False,
        unresolved=[], **checks(DELIVERY.ORACLE_CHECKS),
        follow_counters=dict(direct_children_only=True, baseline_available=False, candidate_record_limit=2048,
            candidate_byte_limit=8388608, resource_limit=4096, candidate_dropped=0, candidate_high_water_records=12, candidate_high_water_bytes=1000),
        storage_counters=copy.deepcopy(storage), observations=[
            dict(label="parent", event_id="44444444-4444-4444-8444-444444444444", operation="write_requested", rule="InstallerPid",
                 completion=True, state_validated=False, confidence="High", actor_confidence="High", pid=100, creation_time="133000000000000000"),
            dict(label="child", event_id=EVENT, operation="write_requested", rule="DescendantProcess", completion=True,
                 state_validated=False, confidence="High", actor_confidence="High", pid=101, creation_time="133000000000000001"),
            dict(label="unrelated", classification="unobserved")])
    expected = [f"synthetic-search-event-{i:03}" for i in range(70, 123)]
    native = dict(schema_version=1, protocol="contain-wpf-native-verification-1", source_sha=SOURCE, native_windows=True,
        framework="WPF + WPF UI", architecture="X64", windows_version="10.0.26100.0", product_process_only=True,
        real_user_records_accessed=False, accepted=True, preview_checks_passed=True, native_install_requested=True,
        actual_etw=True, cold_restart_verified=True, retries=0, planned_checks=list(DELIVERY.NATIVE_CHECKS),
        **checks(DELIVERY.NATIVE_CHECKS), assertions=100, launches=[dict(process_exited=True) for _ in range(4)],
        search=dict(expected_ids=expected, first_page_ids=expected[:50], second_page_ids=expected[50:], exported_ids=expected,
                    selected_event_id="synthetic-search-event-120"),
        live=dict(session_id=SESSION, selected_event_id=EVENT, follow_oracle=oracle, accounting=dict(
            events_received=1000, events_retained=100, etw_events_lost=0, etw_buffers_lost=0, dropped_events=0,
            context_losses=0, decode_errors=0, drain_timed_out=False,
            receiver=dict(queue_overflow=0, enqueue_disconnected=0, queue_pending=0),
            handoff=dict(overflow=0, oversized=0, disconnected=0, serialize_failed=0, pending=0), raw_storage=copy.deepcopy(storage))))
    for key, filename in {"viewer_exe_sha256": "Contain.Viewer.exe", "viewer_dll_sha256": "Contain.Viewer.dll",
                          "core_sha256": "contain.exe", "package_manifest_sha256": "BUILD.json"}.items():
        native[key] = hashlib.sha256(archive.read(filename)).hexdigest()
    plan = dict(schema_version=1, protocol="contain-wpf-native-plan-1", source_sha=SOURCE, retries=0, maximum_native_captures=1,
                native_install_requested=True, product_process_only=True, real_user_records_accessed=False,
                planned_checks=list(DELIVERY.NATIVE_CHECKS),
                deadlines_ms=dict(ready=40000, uia=20000, transition=30000, installation=180000, close=20000),
                fixture=dict(event_count=130, matches=53, first_page=50, second_page=3, selected_event="synthetic-search-event-120"))
    build = dict(source_sha=SOURCE, core_sha256=DELIVERY.PACKAGE.CORE_SHA)
    return native, plan, build


class ModernReleaseGates(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="contain-release-contract-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = self.root / "synthetic.zip"
        with zipfile.ZipFile(self.path, "w") as archive:
            for name in ["Contain.Viewer.exe", "Contain.Viewer.dll", "contain.exe", "BUILD.json"]:
                archive.writestr(name, b"isolated synthetic identity bytes")
        self.archive = zipfile.ZipFile(self.path)
        self.addCleanup(self.archive.close)

    def verify(self, value=None, plan=None):
        native, declared, build = native_inputs(self.archive)
        return DELIVERY.verify_native(value or native, plan or declared, build, self.archive, SOURCE)

    def test_complete_fixed_native_contract_passes(self):
        self.verify()
        self.assertEqual(len(DELIVERY.NATIVE_CHECKS), 14)
        self.assertEqual(len(DELIVERY.MODEL_CHECKS), 34)

    def test_preview_linux_missing_etw_and_incomplete_checks_fail(self):
        for key, val in [("accepted", False), ("native_windows", False), ("actual_etw", False),
                         ("cold_restart_verified", False), ("native_install_requested", False),
                         ("protocol", "contain-old-winforms-verification"), ("source_sha", "2" * 40), ("retries", 1),
                         ("assertions", 0), ("launches", [dict(process_exited=False)]), ("checks", []),
                         ("failure_screenshots", ["old-failure"]), ("private_database", "personal.db")]:
            with self.subTest(key=key):
                native, _, _ = native_inputs(self.archive)
                native[key] = val
                with self.assertRaises(ValueError): self.verify(native)

    def test_duplicate_native_case_does_not_replace_required_case(self):
        native, _, _ = native_inputs(self.archive)
        native["checks"][-1] = native["checks"][0]
        with self.assertRaises(ValueError): self.verify(native)

    def test_extended_wait_or_altered_predeclared_fixture_fails(self):
        for key in ["deadlines_ms", "fixture"]:
            native, plan, _ = native_inputs(self.archive)
            if key == "deadlines_ms": plan[key]["ready"] += 1
            else: plan[key]["matches"] = 52
            with self.assertRaises(ValueError): self.verify(native, plan)

    def test_native_report_must_identify_all_exact_binary_and_manifest_bytes(self):
        for key in ["viewer_exe_sha256", "viewer_dll_sha256", "core_sha256", "package_manifest_sha256"]:
            native, _, _ = native_inputs(self.archive)
            native[key] = "0" * 64
            with self.subTest(key=key), self.assertRaises(ValueError): self.verify(native)

    def test_search_ids_order_duplicates_and_selected_event_are_not_counts_only(self):
        for key in ["first_page_ids", "second_page_ids", "exported_ids", "selected_event_id"]:
            native, _, _ = native_inputs(self.archive)
            if key == "selected_event_id": native["search"][key] = "synthetic-search-event-121"
            else: native["search"][key] = list(reversed(native["search"][key]))
            with self.subTest(key=key), self.assertRaises(ValueError): self.verify(native)

    def test_exact_live_session_and_explained_child_event_are_required(self):
        for field in ["session_id", "selected_event_id"]:
            native, _, _ = native_inputs(self.archive)
            native["live"][field] = "55555555-5555-4555-8555-555555555555"
            with self.subTest(field=field), self.assertRaises(ValueError): self.verify(native)

    def test_oracle_losses_resource_limits_and_nonzero_storage_fail(self):
        paths = [("follow_oracle", "follow_counters", "candidate_dropped"), ("follow_oracle", "follow_counters", "candidate_record_limit"),
                 ("follow_oracle", "storage_counters", "failed"), ("accounting", "receiver", "queue_overflow"),
                 ("accounting", "handoff", "pending"), ("accounting", "raw_storage", "quota_dropped")]
        for path in paths:
            native, _, _ = native_inputs(self.archive)
            parent = native["live"]
            for name in path[:-1]: parent = parent[name]
            parent[path[-1]] += 1
            with self.subTest(path=path), self.assertRaises(ValueError): self.verify(native)

    def test_target_pid_birth_completion_and_unrelated_attribution_fail_closed(self):
        for index, key, value in [(0, "creation_time", None), (0, "pid", 0), (0, "completion", None),
                                  (1, "rule", "InstallerPid"), (1, "confidence", "Unknown"), (2, "classification", "High")]:
            native, _, _ = native_inputs(self.archive)
            native["live"]["follow_oracle"]["observations"][index][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError): self.verify(native)

    def test_model_required_cases_cannot_be_replaced_by_pass_count(self):
        DELIVERY.verify_model(model())
        for mode in ["missing", "duplicate", "failure", "native"]:
            value = model()
            if mode == "missing": value["checks"][0] = "different assertion"
            if mode == "duplicate": value["checks"][0] = value["checks"][1]
            if mode == "failure": value["failure"] = "IOException"
            if mode == "native": value["real_etw"] = True
            with self.subTest(mode=mode), self.assertRaises(ValueError): DELIVERY.verify_model(value)

    def test_control_fixture_is_native_source_bound_and_not_package_acceptance(self):
        good = dict(schema_version=1, protocol="contain-wpf-action-navigation-regression-1", source_sha=SOURCE,
            native_windows=True, synthetic=True, real_etw_executed=False, actual_packaged_executable=False,
            production_source_link="desktop/Contain.Desktop/Controls/ActionNavigationItem.cs", wpf_ui_version="4.3.0",
            planned_checks=list(DELIVERY.CONTROL_CHECKS), accepted=True, retries=0, **checks(DELIVERY.CONTROL_CHECKS))
        DELIVERY.verify_controls(good, SOURCE)
        for key, value in [("source_sha", "2" * 40), ("native_windows", False), ("checks", []),
                           ("actual_packaged_executable", True), ("real_etw_executed", True)]:
            with self.subTest(key=key), self.assertRaises(ValueError): DELIVERY.verify_controls({**good, key: value}, SOURCE)

    def test_dependency_audit_removes_runner_path_and_rejects_vulnerability(self):
        value = dict(version=1, parameters="--vulnerable --include-transitive", sources=["https://api.nuget.org/v3/index.json"],
                     projects=[dict(path="D:/a/Contain/Contain/desktop/Contain.Desktop/Contain.Desktop.csproj")])
        result = DELIVERY.verify_dependencies(value)
        self.assertNotIn("D:/a", json.dumps(result))
        value["projects"][0]["frameworks"] = [dict(transitivePackages=[dict(id="unsafe", vulnerabilities=[{}])])]
        with self.assertRaises(ValueError): DELIVERY.verify_dependencies(value)

    def screenshot_rows(self):
        directory = self.root / "screenshots"
        directory.mkdir(exist_ok=True)
        rows = []
        for name, kind in DELIVERY.SHOTS.items():
            data = b"\x89PNG\r\n\x1a\n" + struct.pack(">I", 13) + b"IHDR" + struct.pack(">II", 820, 640) + b"\x08\x06\x00\x00\x00" + b"\x00" * 4
            (directory / name).write_bytes(data)
            rows.append(dict(name=name, data_kind=kind, width=820, height=640, dpi=96, sha256=hashlib.sha256(data).hexdigest()))
        return rows, directory

    def test_all_required_screenshot_bytes_dimensions_dpi_and_data_labels(self):
        rows, directory = self.screenshot_rows()
        self.assertEqual(len(DELIVERY.verify_screenshots(rows, directory)), 13)
        for key, val in [("width", 821), ("sha256", "0" * 64), ("dpi", 0), ("data_kind", "fabricated-preview")]:
            mutated = copy.deepcopy(rows)
            mutated[0][key] = val
            with self.subTest(key=key), self.assertRaises(ValueError): DELIVERY.verify_screenshots(mutated, directory)
        for row in rows: row["dpi"] = 144
        with self.assertRaisesRegex(ValueError, "96 DPI"): DELIVERY.verify_screenshots(rows, directory)

    def test_protected_product_and_test_sources_cannot_change_after_acceptance(self):
        DELIVERY.unchanged_paths(["README.md", "docs/beta04-release.md", "scripts/publish-modern-desktop.py"])
        for path in ["desktop/Contain.Desktop/App.xaml", "desktop/Contain.Desktop.Model/RecordsModel.cs", "desktop/Contain.Viewer/CoreClient.cs",
                     "desktop/Contain.Desktop.Tests/Program.cs", "desktop/Contain.Desktop.Model.Tests/RecordChecks.cs", "global.json",
                     "desktop/Contain.Desktop.Controls.Tests/Program.cs",
                     "crates/contain-core/src/lib.rs", "scripts/package-modern-desktop.py", "Directory.Build.props", DELIVERY.WORKFLOW]:
            with self.subTest(path=path), self.assertRaises(ValueError): DELIVERY.unchanged_paths([path])

    def test_only_first_attempt_manual_main_context_can_upload(self):
        value = dict(GITHUB_REPOSITORY=DELIVERY.REPO, GITHUB_REF="refs/heads/main", GITHUB_EVENT_NAME="workflow_dispatch", GITHUB_RUN_ATTEMPT="1")
        DELIVERY.workflow_context(value)
        for key, val in [("GITHUB_REPOSITORY", "fork/Contain"), ("GITHUB_REF", "refs/heads/feature"),
                         ("GITHUB_EVENT_NAME", "pull_request"), ("GITHUB_RUN_ATTEMPT", "2")]:
            with self.subTest(key=key), self.assertRaises(ValueError): DELIVERY.workflow_context({**value, key: val})

    def test_remote_source_workflow_event_branch_and_attempt_are_bound(self):
        good = dict(id=7, head_sha=SOURCE, path=DELIVERY.WORKFLOW, status="completed", conclusion="success", event="push", run_attempt=1,
                    head_repository=dict(full_name=DELIVERY.REPO), head_branch="main")
        with patch.object(DELIVERY, "api", return_value=good): DELIVERY.remote_run(7, SOURCE, DELIVERY.WORKFLOW, "main")
        for key, val in [("head_sha", "2" * 40), ("path", ".github/workflows/ci.yml"), ("conclusion", "failure"),
                         ("event", "pull_request"), ("run_attempt", 2), ("head_branch", "feature")]:
            with self.subTest(key=key), patch.object(DELIVERY, "api", return_value={**good, key: val}), self.assertRaises(ValueError):
                DELIVERY.remote_run(7, SOURCE, DELIVERY.WORKFLOW, "main")

    def test_artifact_digest_origin_expiration_and_listing_completeness(self):
        item = dict(id=11, name="modern-desktop-evidence", expired=False, digest="sha256:" + "a" * 64,
                    workflow_run=dict(id=7, head_sha=SOURCE))
        good = dict(total_count=1, artifacts=[item])
        with patch.object(DELIVERY, "api", return_value=good): DELIVERY.artifact(7, SOURCE, item["name"])
        bad = [{**good, "total_count": 100}, dict(total_count=2, artifacts=[item, item])]
        bad += [dict(total_count=1, artifacts=[{**item, key: val}]) for key, val in
                [("expired", True), ("digest", None), ("workflow_run", dict(id=8, head_sha=SOURCE))]]
        for index, value in enumerate(bad):
            with self.subTest(case=index), patch.object(DELIVERY, "api", return_value=value), self.assertRaises((ValueError, TypeError)):
                DELIVERY.artifact(7, SOURCE, item["name"])

    def test_unsafe_artifact_is_rejected_before_extraction(self):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as archive: archive.writestr("../private.txt", b"unsafe synthetic path")
        contents = data.getvalue()
        item = dict(id=99, digest="sha256:" + hashlib.sha256(contents).hexdigest())
        with patch.object(DELIVERY.subprocess, "check_call", side_effect=lambda command, **kwargs: kwargs["stdout"].write(contents)), self.assertRaises(ValueError):
            DELIVERY.download_artifact(item, self.root / "extracted", self.root)
        self.assertFalse((self.root / "extracted").exists())

    def test_asset_preparation_keeps_original_zip_and_excludes_unreviewed_files(self):
        fixture_spec = importlib.util.spec_from_file_location("modern_package_test_fixture", Path(__file__).with_name("test-package-modern-desktop.py"))
        fixture = importlib.util.module_from_spec(fixture_spec)
        fixture_spec.loader.exec_module(fixture)
        members = fixture.published_members()
        members.update({"contain.exe": fixture.CORE, "START-HERE.txt": b"SYNTHETIC delivery contract fixture"})
        build = fixture.build_metadata()
        build["files"] = [dict(path=n, bytes=len(b), sha256=hashlib.sha256(b).hexdigest()) for n, b in members.items()]
        members["BUILD.json"] = json.dumps(build).encode()
        package = self.root / "original.zip"
        fixture.zip_bytes(package, members)
        evidence, main, quality = [self.root / name for name in ["native", "main", "quality"]]
        for directory in [evidence, main, quality]: directory.mkdir()
        sha = DELIVERY.digest(package)
        rows, images = self.screenshot_rows()
        shutil.copytree(images, evidence / "screenshots")
        args = argparse.Namespace(product_source_sha=SOURCE, tag_source_sha="2" * 40, zip_sha256=sha,
                                  accepted_run_id=7, main_quality_run_id=8, main_desktop_run_id=9)
        with patch.object(DELIVERY.PACKAGE, "CORE_SHA", fixture.CORE_HASH), zipfile.ZipFile(package) as archive:
            native, native_plan, _ = native_inputs(archive)
            native["screenshots"] = rows
            plan = dict(schema_version=1, protocol="contain-modern-validation-plan-1", source_sha=SOURCE,
                native_windows=True, full_acceptance=True, maximum_gui_installations=1, retries=0, capture_core_changed=False,
                core_source_sha=DELIVERY.PACKAGE.CORE_SOURCE, baseline_zip_sha256=DELIVERY.PACKAGE.BASELINE_SHA,
                previous_failures_retained=True, old_winforms_acceptance_is_not_new_desktop_evidence=True, zip_frozen_before_native=True)
            dependencies = dict(version=1, parameters="--vulnerable --include-transitive", sources=["https://api.nuget.org/v3/index.json"],
                projects=[dict(path="D:/SYNTHETIC/desktop/Contain.Desktop/Contain.Desktop.csproj")])
            def controls(source):
                return dict(schema_version=1, protocol="contain-wpf-action-navigation-regression-1", source_sha=source,
                    native_windows=True, synthetic=True, real_etw_executed=False, actual_packaged_executable=False,
                    production_source_link="desktop/Contain.Desktop/Controls/ActionNavigationItem.cs", wpf_ui_version="4.3.0",
                    planned_checks=list(DELIVERY.CONTROL_CHECKS), accepted=True, retries=0, **checks(DELIVERY.CONTROL_CHECKS))
            for name, value in {"modern-desktop-summary.json": native, "modern-desktop-plan.json": native_plan,
                                "plan.json": plan, "model-checks.json": model(), "navigation-control-summary.json": controls(SOURCE),
                                "dotnet-dependencies.json": dependencies}.items(): DELIVERY.write_json(evidence / name, value)
            (evidence / "BUILD-IDENTITY.json").write_bytes(archive.read("BUILD.json"))
            for name, value in {"model-checks.json": model(), "navigation-control-summary.json": controls(args.tag_source_sha),
                                "dotnet-dependencies.json": dependencies}.items(): DELIVERY.write_json(main / name, value)
            modes = ["Check", "Coverage", "CoverageOriginal", "Dependencies", "Companion"]
            DELIVERY.write_json(quality / "summary.json", dict(source_revision=args.tag_source_sha,
                current_required_gates_passed=True, all_recorded_invocations_passed=True, all_recorded_gates_passed=True,
                required_gates_complete=True, coverage_floors=dict(lines_percent=66.5, functions_percent=70),
                missing_gates={mode: [] for mode in modes}, invocations=[dict(mode=mode, passed=True) for mode in modes]))
            for name in ["private.db", "raw.log", "unreviewed.html"]: (evidence / name).write_bytes(b"must not ship")
            output = self.root / "delivery"
            result = DELIVERY.prepare_assets(output, package, evidence, main, quality, args,
                dict(main_sha=args.tag_source_sha, tag_commit_sha=args.tag_source_sha), {}, {"synthetic_test_fixture": True})
        self.assertEqual(DELIVERY.digest(output / DELIVERY.ZIP), sha)
        self.assertEqual(DELIVERY.digest(package), sha)
        self.assertEqual(result["product_source_sha"], SOURCE)
        self.assertEqual(result["delivery_source_sha"], args.tag_source_sha)
        self.assertEqual(result["native_control_passed"], 6)
        self.assertFalse(any((output / name).exists() for name in ["private.db", "raw.log", "unreviewed.html"]))
        self.assertNotIn("D:/SYNTHETIC", (output / "DEPENDENCY-REVIEW.json").read_text())
        sums = (output / "SHA256SUMS.txt").read_text().splitlines()
        self.assertEqual(len(sums), len(list(output.iterdir())) - 1)
        for line in sums:
            expected, name = line.split("  ", 1)
            self.assertEqual(DELIVERY.digest(output / name), expected)

    def test_existing_published_release_or_wrong_target_is_never_mutated(self):
        good = dict(id=9, tag_name=DELIVERY.TAG, target_commitish=SOURCE, draft=True, prerelease=True, assets=[])
        with patch.object(DELIVERY, "api", return_value=good): DELIVERY.existing_draft(SOURCE, 9)
        for key, value in [("draft", False), ("prerelease", False), ("tag_name", "v0.3.2-beta.10"), ("target_commitish", "main")]:
            with self.subTest(key=key), patch.object(DELIVERY, "api", return_value={**good, key: value}), self.assertRaises(ValueError):
                DELIVERY.existing_draft(SOURCE, 9)

    def test_nonempty_draft_is_refused_before_any_upload(self):
        with patch.object(DELIVERY, "existing_draft", return_value=dict(assets=[dict(name="original.zip")])), \
                patch.object(DELIVERY.subprocess, "check_call") as process, self.assertRaises(ValueError):
            DELIVERY.upload_assets(self.root, SOURCE, 9, self.root)
        process.assert_not_called()

    def test_upload_reads_every_original_byte_without_publish_or_overwrite(self):
        folder = self.root / "assets"
        folder.mkdir()
        (folder / "test.txt").write_bytes(b"accepted immutable bytes")
        state, calls = [], []
        def execute(command, **kwargs):
            calls.append(command)
            if command[:3] == ["gh", "release", "upload"]:
                self.assertNotIn("--clobber", command)
                state.append(dict(id=1, name="test.txt", state="uploaded", size=24, digest="sha256:" + DELIVERY.digest(folder / "test.txt")))
            elif command[:2] == ["gh", "api"]:
                kwargs["stdout"].write((folder / "test.txt").read_bytes())
            else: self.fail("unexpected mutation")
        with patch.object(DELIVERY, "existing_draft", side_effect=lambda *args: dict(assets=copy.deepcopy(state))), \
                patch.object(DELIVERY.subprocess, "check_call", side_effect=execute):
            DELIVERY.upload_assets(folder, SOURCE, 9, self.root)
        self.assertEqual(len(calls), 2)
        self.assertEqual((self.root / "readback/test.txt").read_bytes(), b"accepted immutable bytes")
        self.assertFalse(any("edit" in command or "create" in command or "--clobber" in command for command in calls))


if __name__ == "__main__":
    unittest.main()
