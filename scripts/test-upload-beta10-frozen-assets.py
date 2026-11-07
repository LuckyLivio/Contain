"""Deterministic delivery guard regressions; no Windows acceptance is simulated."""
import contextlib
import copy
import io
import json
from pathlib import Path
import unittest
from unittest import mock
import zipfile


def load(name, filename):
    import importlib.util
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


FROZEN = load("frozen_delivery_guards", "upload-beta10-frozen-assets.py")
FIXTURE = load("frozen_delivery_native_fixtures", "test-upload-beta9-assets.py")
MAIN, TAG_OBJECT = "a" * 40, "c" * 40


def review():
    return dict(schema_version=1, protocol="contain-beta10-release-policy-review-1",
        policy_id="2026-11-07-known-beta-risks-accepted", product_source_sha=FROZEN.SOURCE,
        zip_sha256=FROZEN.ZIP_SHA, reviewed_harness_source_sha=FROZEN.HARNESS_SOURCE,
        accepted_full_desktop_run=FROZEN.FULL_RUN, test_contract="unchanged_d6_reused",
        historical_failures_retained=True, historical_unknowns_are_release_blockers=False,
        product_binaries_changed=False, resource_limits_changed=False, timeout_limits_changed=False,
        assertions_weakened=False, cleanup_weakened=False, repeat_until_green=False,
        unresolved_new_acceptance_failure=False, known_reproducible_integrity_defect=False,
        contract_review=dict(control_id="SessionTitle", absence_is_legal_transition=False,
            stale_description_waited=True, provisional_guid_is_final_acceptance=False,
            exact_task_session_checked_before_readback=True, historical_cause_claimed=False),
        known_risk_tracking={key: f"https://github.com/LuckyLivio/Contain/issues/{16 + index}"
            for index, key in enumerate(FROZEN.RISK_CODES)})


class ReviewAndSourceGuards(unittest.TestCase):
    def test_explicit_policy_accepts_historical_unknowns_without_rewriting_them(self):
        self.assertEqual(FROZEN.verify_review(review()), review())
        self.assertFalse(review()["contract_review"]["historical_cause_claimed"])

    def test_new_failure_product_change_or_weakened_contract_is_rejected(self):
        changes = dict(product_source_sha="f" * 40, zip_sha256="e" * 64,
            reviewed_harness_source_sha="f" * 40, accepted_full_desktop_run=FROZEN.FULL_RUN + 1,
            test_contract="scoped_result_wait", historical_failures_retained=False,
            historical_unknowns_are_release_blockers=True, product_binaries_changed=True,
            resource_limits_changed=True, timeout_limits_changed=True, assertions_weakened=True,
            cleanup_weakened=True, repeat_until_green=True, unresolved_new_acceptance_failure=True,
            known_reproducible_integrity_defect=True, private_notes="unreviewed metadata")
        for key, value in changes.items():
            with self.subTest(key=key), self.assertRaises(ValueError):
                FROZEN.verify_review(dict(review(), **{key: value}))

    def test_policy_boolean_types_and_required_exact_session_contract(self):
        for key in ("historical_failures_retained", "product_binaries_changed", "unresolved_new_acceptance_failure"):
            with self.subTest(key=key), self.assertRaises(ValueError):
                FROZEN.verify_review(dict(review(), **{key: int(review()[key])}))
        for key, value in dict(absence_is_legal_transition=True, stale_description_waited=False,
            provisional_guid_is_final_acceptance=True, exact_task_session_checked_before_readback=False,
            historical_cause_claimed=True).items():
            changed = review(); changed["contract_review"][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                FROZEN.verify_review(changed)

    def test_known_risks_cannot_be_omitted_or_point_to_other_repository(self):
        for replacement in ({}, {key: "https://github.com/other/repo/issues/1" for key in FROZEN.RISK_CODES},
                            {key: "https://github.com/LuckyLivio/Contain/issues/16" for key in FROZEN.RISK_CODES}):
            with self.subTest(replacement=replacement), self.assertRaises(ValueError):
                FROZEN.verify_review(dict(review(), known_risk_tracking=replacement))

    def test_frozen_product_and_native_predicates_may_not_change(self):
        FROZEN.verify_changed_paths(["desktop/Contain.Viewer.Tests/Program.cs", "docs/review.json"])
        for paths in (["Cargo.toml"], ["scripts/package-viewer.py"], ["scripts/upload-beta9-assets.py"],
                      ["desktop/Contain.Viewer/Program.cs"], ["crates/contain-core/src/capture.rs"],
                      ["docs/../Cargo.toml"], ["README.md", "README.md"]):
            with self.subTest(paths=paths), self.assertRaises(ValueError):
                FROZEN.verify_changed_paths(paths)

    def test_d6_reuse_cannot_cover_new_desktop_changes_after_reviewed_tree(self):
        FROZEN.verify_delivery_paths(["docs/review.json", "scripts/test-upload-beta10-frozen-assets.py"])
        for path in ("desktop/Contain.Viewer.Tests/Program.cs", "desktop/Contain.Viewer.Tests/RequiredLookupDiagnostics.cs",
                     ".github/workflows/frozen-beta10-desktop.yml", "scripts/package-viewer.py"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                FROZEN.verify_delivery_paths([path])

    def test_comparison_checks_rename_origin_and_rejects_api_truncation(self):
        good = dict(status="ahead", merge_base_commit=dict(sha=FROZEN.SOURCE),
            files=[dict(filename="desktop/Contain.Viewer.Tests/moved.cs", previous_filename="desktop/Contain.Viewer/Program.cs")])
        with mock.patch.object(FROZEN, "api", return_value=good):
            paths = FROZEN.comparison_paths(FROZEN.SOURCE, MAIN)
            self.assertIn("desktop/Contain.Viewer/Program.cs", paths)
            with self.assertRaises(ValueError): FROZEN.verify_changed_paths(paths)
        for change in (dict(files=[dict(filename=f"docs/{index}.md") for index in range(300)]),
                       dict(status="diverged"), dict(merge_base_commit=dict(sha=MAIN))):
            with self.subTest(change=next(iter(change))), mock.patch.object(FROZEN, "api", return_value=dict(good, **change)), self.assertRaises(ValueError):
                FROZEN.comparison_paths(FROZEN.SOURCE, MAIN)

    def test_local_artifacts_cannot_claim_remote_upload_provenance(self):
        for mode in ([], ["--upload"]):
            with self.subTest(mode=mode), mock.patch("sys.argv", ["uploader", "--local-input", "untrusted"] + mode), mock.patch.object(FROZEN, "api") as api:
                with self.assertRaisesRegex(ValueError, "Local inputs are diagnostic verification only"):
                    FROZEN.main()
                api.assert_not_called()


class RemoteIdentityGuards(unittest.TestCase):
    def test_remote_run_must_bind_success_source_origin_and_attempt(self):
        correct = dict(id=FROZEN.FULL_RUN, head_sha=FROZEN.HARNESS_SOURCE, path=FROZEN.FULL_WORKFLOW,
            status="completed", conclusion="success", run_attempt=1, head_branch="fix/beta10-capture-loss",
            event="push", head_repository=dict(full_name=FROZEN.UPLOAD.REPO))
        with mock.patch.object(FROZEN, "api", return_value=correct):
            FROZEN.remote_run(FROZEN.FULL_RUN, FROZEN.HARNESS_SOURCE, FROZEN.FULL_WORKFLOW, "success")
        for key, value in dict(id=1, head_sha=MAIN, conclusion="failure", run_attempt=2, status="in_progress",
            path="wrong", head_branch="main", event="pull_request").items():
            with self.subTest(key=key), mock.patch.object(FROZEN, "api", return_value=dict(correct, **{key: value})), self.assertRaises(ValueError):
                FROZEN.remote_run(FROZEN.FULL_RUN, FROZEN.HARNESS_SOURCE, FROZEN.FULL_WORKFLOW, "success")

    def test_artifact_is_pinned_to_run_source_name_and_nonexpired_id(self):
        item = dict(id=FROZEN.NATIVE_ARTIFACT, name=FROZEN.NATIVE_NAME, expired=False,
            workflow_run=dict(id=FROZEN.FULL_RUN, head_sha=FROZEN.HARNESS_SOURCE))
        def verify(value):
            with mock.patch.object(FROZEN, "api", return_value=dict(total_count=1, artifacts=[value])):
                return FROZEN.remote_artifact(FROZEN.FULL_RUN, FROZEN.NATIVE_NAME, FROZEN.HARNESS_SOURCE, FROZEN.NATIVE_ARTIFACT)
        self.assertEqual(verify(item), FROZEN.NATIVE_ARTIFACT)
        for key, value in dict(id=1, expired=True, name="other", workflow_run=dict(id=1, head_sha=MAIN)).items():
            with self.subTest(key=key), self.assertRaises(ValueError): verify(dict(item, **{key: value}))

    def test_annotated_tag_object_and_commit_are_recorded_separately(self):
        def api(path):
            if path == "git/ref/heads/main": return dict(object=dict(sha=MAIN))
            if path == "git/ref/tags/" + FROZEN.UPLOAD.TAG: return dict(object=dict(type="tag", sha=TAG_OBJECT))
            if path == "git/tags/" + TAG_OBJECT: return dict(object=dict(type="commit", sha=MAIN))
            raise AssertionError(path)
        with mock.patch.object(FROZEN, "api", side_effect=api), mock.patch.object(FROZEN, "remote_run"), mock.patch.object(FROZEN, "comparison_paths", return_value=[]):
            self.assertEqual(FROZEN.verify_main_and_tag(123, MAIN),
                dict(main_sha=MAIN, tag_ref_sha=TAG_OBJECT, tag_ref_type="tag", tag_commit_sha=MAIN))
            with self.assertRaises(ValueError): FROZEN.verify_main_and_tag(123, FROZEN.SOURCE)

    def test_existing_published_release_or_wrong_target_cannot_receive_assets(self):
        value = dict(id=10, tag_name=FROZEN.UPLOAD.TAG, target_commitish=MAIN, draft=True, prerelease=True, assets=[])
        with mock.patch.object(FROZEN, "api", return_value=value):
            self.assertEqual(FROZEN.existing_draft(MAIN, 10), value)
        for key, replacement in dict(draft=False, prerelease=False, target_commitish=FROZEN.SOURCE, tag_name="v0.3.2-beta.9").items():
            with self.subTest(key=key), mock.patch.object(FROZEN, "api", return_value=dict(value, **{key: replacement})), self.assertRaises(ValueError):
                FROZEN.existing_draft(MAIN, 10)

    def test_existing_asset_prevents_any_upload(self):
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); output = root / "assets"; output.mkdir(); (output / "file").write_bytes(b"reviewed")
            with mock.patch.object(FROZEN, "existing_draft", return_value=dict(assets=[dict(name="file")])), mock.patch.object(FROZEN.subprocess, "check_call") as command:
                with self.assertRaises(ValueError): FROZEN.upload_assets(output, root, MAIN, 10)
                command.assert_not_called()


class NativePredicateGuards(unittest.TestCase):
    def setUp(self):
        patch = mock.patch.object(FIXTURE, "UPLOAD", FROZEN.DESKTOP)
        patch.start(); self.addCleanup(patch.stop)
        self.legacy = FIXTURE.Beta9DeliveryTests("test_exact_artifacts_bind_actual_follow_capture_guidance_and_before_after")
        self.addCleanup(self.legacy.doCleanups)
        with contextlib.redirect_stdout(io.StringIO()):
            self.legacy.setUp()
        with zipfile.ZipFile(self.legacy.package) as archive:
            self.build = json.loads(archive.read("BUILD.json"))
        patch = mock.patch.object(FROZEN, "BUILD_SHA", FROZEN.digest(self.legacy.fixture.package / "BUILD.json"))
        patch.start(); self.addCleanup(patch.stop)

    def verify(self):
        return FROZEN.verify_native(self.legacy.evidence, self.build)

    def test_complete_original_native_fixture_passes_unchanged_predicates(self):
        values, _ = self.verify()
        self.assertEqual(len(values), 9)

    def test_each_native_summary_is_required_and_failed_assertion_rejects(self):
        for filename, _ in FROZEN.NATIVE.values():
            path = self.legacy.evidence / filename
            original = path.read_bytes(); value = json.loads(original)
            value["checks"][0]["status"] = "failed"
            try:
                path.write_text(json.dumps(value))
                with self.subTest(filename=filename), self.assertRaises(ValueError):
                    self.verify()
            finally:
                path.write_bytes(original)

    def test_product_hash_substitution_fails(self):
        path = self.legacy.evidence / "summary.json"
        value = json.loads(path.read_text()); value["viewer_assembly_sha256"] = "a" * 64
        path.write_text(json.dumps(value))
        with self.assertRaises(ValueError):
            self.verify()

    def test_cold_restart_identity_and_real_install_evidence_cannot_be_omitted(self):
        path = self.legacy.evidence / "install-summary.json"; original = path.read_bytes()
        for field, value in dict(cold_restart_verified=False, actual_packaged_executable_capture=False,
            data_saved=False, restart_event_id="wrong", real_etw_executed=False).items():
            changed = json.loads(original); changed[field] = value
            path.write_text(json.dumps(changed))
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.verify()
        path.write_bytes(original)

    def test_native_export_html_and_follow_screenshot_bytes_are_required(self):
        for relative in ("reports/search-export-synthetic.html", "screenshots/install-follow-configured.png"):
            path = self.legacy.evidence / relative; original = path.read_bytes()
            try:
                path.write_bytes(original + b"changed")
                with self.subTest(relative=relative), self.assertRaises(ValueError):
                    self.verify()
            finally:
                path.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
