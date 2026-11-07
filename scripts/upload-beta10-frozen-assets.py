"""Deliver the original Beta 10 ZIP using explicit, separate final acceptance.

The user-approved Beta policy accepts the three documented historical unknowns
without rewriting their results. Every product, native desktop, capture, quality,
source and resource guard remains required. This path never builds, repacks,
creates a tag/release, overwrites an asset, or publishes the existing draft.
"""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile


def load(name, filename):
    import importlib.util
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


UPLOAD = load("frozen_beta10_existing_guards", "upload-beta10-assets.py")
DESKTOP = UPLOAD.DESKTOP
GATE = load("frozen_beta10_native_guards", "package-viewer.py")
require, digest, read, api = UPLOAD.require, UPLOAD.digest, UPLOAD.read, UPLOAD.api
SOURCE = "b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4"
ZIP_SHA = "a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656"
BUILD_SHA = "6801f5cffe077ff479a344805b2921c020150ab02ef240f9a16e009dfac74e23"
VALIDATION_SHA = "195c676d1f51c860ce4ea54dca3b5839480ff70f5d62608845f560b8e97472be"
QUALITY_SHA = "b41cc79e8c4a620bf0812e2284118ab0f25ed7033dae2cb656ab65bcf0d0ebee"
ORIGINAL_RUN, ORIGINAL_ZIP_ARTIFACT = 42227112910, 13506927140
ORIGINAL_QUALITY_ARTIFACT, ORIGINAL_VIEWER_ARTIFACT = 13506912182, 13506907259
MATRIX_RUN, MATRIX_ARTIFACT = 42231290678, 13509891233
MATRIX_SOURCE = "c45dab35b8ed175e9e928b53a108dafa0a7aa0f6"
EXTENDED_RUN, EXTENDED_ARTIFACT = 42227132180, 13506637116
FULL_RUN, HARNESS_SOURCE = 42325883947, "d1bca0b109d722ceaf0ab4ecf89c1d8e17d0b77b"
FULL_ARTIFACT, NATIVE_ARTIFACT = 13555580235, 13554943169
FULL_WORKFLOW = ".github/workflows/frozen-beta10-desktop.yml"
FULL_NAME, NATIVE_NAME = "frozen-beta10-desktop-evidence", "windows-viewer-evidence"
REVIEW_PATH = "docs/beta10-release-acceptance-review.json"
CONTRACT_FILES = frozenset({"desktop/Contain.Viewer.Tests/InstallVerification.cs", "desktop/Contain.Viewer.Tests/PackagedDesktop.cs"})
RISK_CODES = ("C1_ready_timeout_cause_unknown", "D5_required_control_cause_unknown", "Parallels_57767_original_breakdown_unavailable")
NATIVE = {
    "native_verification": ("summary.json", "verification_summary_sha256"),
    "native_install_verification": ("install-summary.json", "install_verification_summary_sha256"),
    "native_personal_records_verification": ("personal-records-summary.json", "personal_records_verification_summary_sha256"),
    "native_overview_verification": ("overview-summary.json", "overview_verification_summary_sha256"),
    "native_search_verification": ("search-summary.json", "search_verification_summary_sha256"),
    "native_search_export_verification": ("export-summary.json", "search_export_verification_summary_sha256"),
    "native_first_use_configuration_verification": ("first-use-configuration-summary.json", "first_use_configuration_summary_sha256"),
    "native_layout_verification": ("layout-summary.json", "layout_verification_summary_sha256"),
    "native_progress_verification": ("progress-summary.json", "progress_verification_summary_sha256"),
}
# Only test/diagnostic/delivery paths may differ from the frozen product. Existing
# native predicates, capture code, dependency manifests and packager are excluded.
ALLOWED_FILES = frozenset({
    "README.md", ".github/workflows/frozen-beta10-diagnosis.yml", FULL_WORKFLOW,
    ".github/workflows/beta10-diagnostic-bundle.yml", "scripts/bundle-beta10-diagnosis.py",
    ".github/workflows/publish-beta10-frozen-assets.yml",
    "scripts/upload-beta10-frozen-assets.py", "scripts/test-upload-beta10-frozen-assets.py",
})


def exact(value, expected, message):
    require(isinstance(value, dict) and all(key in value and type(value[key]) is type(item)
            and value[key] == item for key, item in expected.items()), message)


def verify_changed_paths(paths):
    require(isinstance(paths, list) and len(paths) < 600 and len(paths) == len(set(paths)),
            "Complete non-product source diff is required")
    require(all(isinstance(path, str) and ".." not in Path(path).parts and
        (path in ALLOWED_FILES or path.startswith("desktop/Contain.Viewer.Tests/")
         or path.startswith("desktop/Contain.Viewer.Presentation.Tests/") or path.startswith("docs/"))
        for path in paths), "Frozen runtime, manifests or acceptance predicates changed")


def verify_review(value):
    expected = dict(schema_version=1, protocol="contain-beta10-release-policy-review-1",
        policy_id="2026-11-07-known-beta-risks-accepted", product_source_sha=SOURCE, zip_sha256=ZIP_SHA,
        reviewed_harness_source_sha=HARNESS_SOURCE, accepted_full_desktop_run=FULL_RUN,
        test_contract="unchanged_d6_reused", historical_failures_retained=True,
        historical_unknowns_are_release_blockers=False, product_binaries_changed=False,
        resource_limits_changed=False, timeout_limits_changed=False, assertions_weakened=False,
        cleanup_weakened=False, repeat_until_green=False, unresolved_new_acceptance_failure=False,
        known_reproducible_integrity_defect=False)
    exact(value, expected, "Explicit unchanged-contract Beta release policy review differs")
    require(set(value) == set(expected) | {"contract_review", "known_risk_tracking"}, "Unreviewed release-policy fields")
    contract = dict(control_id="SessionTitle", absence_is_legal_transition=False,
        stale_description_waited=True, provisional_guid_is_final_acceptance=False,
        exact_task_session_checked_before_readback=True, historical_cause_claimed=False)
    exact(value["contract_review"], contract, "Result waiting contract review differs")
    require(set(value["contract_review"]) == set(contract), "Unreviewed contract metadata")
    risks = value["known_risk_tracking"]
    require(isinstance(risks, dict) and set(risks) == set(RISK_CODES)
            and all(isinstance(url, str) and re.fullmatch(r"https://github\.com/LuckyLivio/Contain/issues/[1-9][0-9]*", url)
                for url in risks.values()) and len(set(risks.values())) == 3,
            "Each retained historical unknown needs a distinct repository issue")
    return value


def verify_original(directory):
    package = directory / "original" / "contain-windows-x64-viewer" / UPLOAD.ZIP
    quality_path = directory / "original" / "windows-read-only-quality" / "summary.json"
    require(digest(package) == ZIP_SHA and digest(quality_path) == QUALITY_SHA,
            "Original ZIP or original quality bytes differ")
    quality = read(quality_path)
    exact(quality, dict(source_revision=SOURCE, current_required_gates_passed=True,
        all_recorded_invocations_passed=True, coverage_floors=dict(lines_percent=66.5, functions_percent=70)),
        "Original source quality gates differ")
    with zipfile.ZipFile(package) as archive:
        require(archive.testzip() is None and len(archive.namelist()) == len(set(archive.namelist())),
                "ZIP integrity or duplicate-member failure")
        build_bytes, validation_bytes = archive.read("BUILD.json"), archive.read("VALIDATION.json")
        require(hashlib.sha256(build_bytes).hexdigest() == BUILD_SHA
                and hashlib.sha256(validation_bytes).hexdigest() == VALIDATION_SHA,
                "Original build or failed validation manifest changed")
        build, validation, contents = json.loads(build_bytes), json.loads(validation_bytes), json.loads(archive.read("CONTENTS.json"))
        exact(build, dict(source_sha=SOURCE, version=UPLOAD.VERSION, self_contained=True, target="win-x64"),
              "Frozen product build identity differs")
        exact(validation, dict(source_sha=SOURCE, status="diagnostic_unverified", viewer_verified=False,
            build_manifest_sha256=BUILD_SHA, quality_summary_sha256=QUALITY_SHA), "Original failure was not retained")
        require(contents.get("source_sha") == SOURCE and set(validation["checks"]) == DESKTOP.REQUIRED_CHECKS,
                "Frozen content source or historical check set differs")
        for key, (filename, hash_key) in NATIVE.items():
            path = directory / "original" / NATIVE_NAME / filename
            require(read(path) == validation[key] and digest(path) == validation[hash_key],
                    "Retained original native evidence differs: " + filename)
        for manifest in (build, contents):
            require(len(manifest["files"]) == len({item["path"] for item in manifest["files"]}), "Duplicate manifest member")
            for item in manifest["files"]:
                data = archive.read(item["path"])
                require(len(data) == item["bytes"] and hashlib.sha256(data).hexdigest() == item["sha256"], "Frozen content differs")
        require(set(archive.namelist()) == {item["path"] for item in contents["files"]} | {"CONTENTS.json"}, "Unexpected ZIP member")
        require(build["source_archive"]["source_sha"] == SOURCE
                and hashlib.sha256(archive.read("SOURCE.zip")).hexdigest() == build["source_archive"]["sha256"], "Frozen source differs")
        with zipfile.ZipFile(io.BytesIO(archive.read("SOURCE.zip"))) as source_archive:
            require(hashlib.sha256(source_archive.read("docs/examples/follow-installer-before.png")).hexdigest() == DESKTOP.BEFORE_SHA256
                    and digest(DESKTOP.BEFORE_PATH) == DESKTOP.BEFORE_SHA256, "Historical screenshot changed")
    return package, quality_path, build, validation, validation_bytes


def verify_native(evidence, build):
    """Apply every unchanged Beta 9 native predicate to the new complete run."""
    values = {key: read(evidence / filename) for key, (filename, _) in NATIVE.items()}
    validators = {
        "native_verification": GATE.verification_matches,
        "native_personal_records_verification": GATE.personal_records_verification_matches,
        "native_overview_verification": GATE.overview_verification_matches,
        "native_search_verification": GATE.search_verification_matches,
        "native_first_use_configuration_verification": GATE.first_use_configuration_verification_matches,
        "native_layout_verification": GATE.layout_verification_matches,
        "native_progress_verification": GATE.progress_verification_matches,
    }
    for key, validate in validators.items():
        require(validate(values[key], build, BUILD_SHA), "Native contract differs: " + key)
    require(GATE.search_export_verification_matches(values["native_search_export_verification"], build, BUILD_SHA,
        evidence / "export-summary.json"), "Actual native export/HTML content differs")
    progress, configuration = values["native_progress_verification"], values["native_first_use_configuration_verification"]
    require(DESKTOP.guidance_verification_matches(progress, configuration), "Existing guidance cases differ")
    installation = values["native_install_verification"]
    require(installation.get("first_use", {}).get("protocol_revision") == "contain-follow-install-flow-1"
            and GATE.first_use_install_verification_matches(installation, build, BUILD_SHA, evidence / "install-summary.json"),
            "Full actual install/save/explain/cold-restart evidence differs")
    exact(configuration.get("follow_configuration_verification"), DESKTOP.FOLLOW_CONFIGURATION,
          "Default follow/manual configuration differs")
    layout = values["native_layout_verification"].get("follow_page_verification", {})
    expected = {"native_default_layout_resize_maximize": 1, "simulated_150_percent_layout": 1.5, "simulated_200_percent_layout": 2}
    conditions = layout.get("conditions", [])
    exact(layout, dict(protocol_revision="contain-follow-layout-verification-1", accepted=True), "Follow layout protocol differs")
    require(isinstance(conditions, list) and len(conditions) == 3 and all(isinstance(row, dict) for row in conditions)
            and {row.get("name") for row in conditions} == set(expected), "Follow layout conditions differ")
    for row in conditions:
        require(type(row.get("requested_scale")) in (int, float) and row["requested_scale"] == expected[row["name"]]
                and type(row.get("actual_device_dpi")) is int and row["actual_device_dpi"] > 0
                and all(type(row.get(key)) is int and 0 < row[key] <= 16384 for key in ("window_width", "window_height"))
                and all(row.get(key) is True for key in ("default_follow", "no_scope_required", "start_near_mode",
                    "primary_before_advanced", "manual_available", "path_copy_exact", "keyboard_start_reachable")),
                "Existing follow layout assertion differs")
    after = evidence / "screenshots/install-follow-configured.png"
    shot = installation["first_use"]["screenshots"][0]
    require((shot["width"], shot["height"]) == (740, 620)
            and installation["first_use"]["actual_device_dpi"] == 96 and digest(after) != DESKTOP.BEFORE_SHA256,
            "Native before/after screenshot evidence differs")
    return values, after


def verify_full(directory, build, harness_source):
    proof = directory / "full" / FULL_NAME
    plan, provenance, execution, summary = [read(proof / name) for name in
        ("plan.json", "provenance.json", "gui-execution.json", "frozen-desktop-summary.json")]
    exact(plan, dict(schema_version=1, protocol="contain-frozen-beta10-desktop-plan-1",
        product_source_sha=SOURCE, harness_source_sha=harness_source, script_source_sha=harness_source,
        fixture_generator_source_sha=SOURCE, native_oracle_source_sha=SOURCE, full_entrypoint=True,
        personal_subset_only=False, harness_runs=1, maximum_native_captures=1, retries=0,
        ready_discovery_deadline_ms=40000, ready_enabled_deadline_ms=40000, uia_call_deadline_ms=20000,
        harness_deadline_ms=600000, owned_process_quiescence_deadline_ms=45000,
        capture_code_changed=False, original_zip_must_remain_unchanged=True, repack_allowed=False,
        native_gate_relaxation_allowed=False, release_accepted=False), "Predeclared complete desktop plan differs")
    exact(plan.get("candidate"), dict(source_sha=SOURCE, run_id=ORIGINAL_RUN, artifact_id=ORIGINAL_ZIP_ARTIFACT,
        zip_sha256=ZIP_SHA), "Full desktop used a different original ZIP")
    exact(plan.get("original_full_gui"), dict(run_id=ORIGINAL_RUN, accepted=False,
        failed_check="packaged_executable_default_history", error_type="TimeoutException", retained=True),
        "Original desktop failure was not retained")
    exact(provenance, dict(schema_version=1, protocol="contain-frozen-beta10-desktop-provenance-1",
        product_source_sha=SOURCE, harness_source_sha=harness_source, script_source_sha=harness_source,
        fixture_generator_source_sha=SOURCE, native_oracle_source_sha=SOURCE, original_zip_sha256=ZIP_SHA,
        full_report_source_sha=SOURCE, full_report_source_is_product_revision=True, release_accepted=False),
        "Full desktop split provenance differs")
    expected = {item["path"]: item["sha256"] for item in build["files"]}
    for key, filename in (("core_sha256", "contain.exe"), ("installer_fixture_sha256", "contain-test-installer.exe"),
            ("viewer_exe_sha256", "Contain.Viewer.exe"), ("production_viewer_dll_sha256", "Contain.Viewer.dll"),
            ("harness_production_reference_sha256", "Contain.Viewer.dll")):
        require(provenance.get(key) == expected[filename], "Full desktop production binary differs: " + key)
    for key in ("harness_exe_sha256", "harness_dll_sha256", "viewer_fixture_generator_sha256"):
        require(re.fullmatch(r"[0-9a-f]{64}", provenance.get(key, "")), "Diagnostic tool hash missing")
    exact(execution, dict(harness_started=True, harness_exited=True, exit_code=0, timed_out=False,
        driver_error_type=None, viewer_count=0, core_count=0, unknown_count=0, all_owned_viewer_core_exited=True,
        full_desktop_invoked=True, personal_subset_only=False, harness_deadline_ms=600000,
        maximum_quiescence_wait_ms=45000, retries=0),
        "Complete desktop did not exit cleanly")
    exact(summary, dict(schema_version=1, protocol="contain-frozen-beta10-desktop-1",
        product_source_sha=SOURCE, harness_source_sha=harness_source, original_zip_unchanged=True,
        full_desktop_invoked=True, original_full_gui_failure_retained=True, all_extracted_package_members_unchanged=True,
        original_full_gui_run=ORIGINAL_RUN, original_full_gui_failed_check="packaged_executable_default_history",
        original_full_gui_error_type="TimeoutException", complete_native_gates_passed=True, release_accepted=False,
        retries=0, real_user_records_accessed=False), "Complete desktop gates were not verified")
    require(summary.get("execution") == execution and summary.get("native_check_error_types") == {},
            "Desktop execution or native validator errors differ")
    checks = summary.get("native_checks", {})
    expected_checks = (DESKTOP.REQUIRED_CHECKS - {"quality_complete", "frozen_package_unchanged"}) | {"preserved_beta9_guidance_cases"}
    require(set(checks) == expected_checks and all(item is True for item in checks.values()),
            "Complete desktop validator result set differs")
    integrity = read(proof / "original-package-integrity.json")
    exact(integrity, dict(schema_version=1, source_sha=SOURCE, zip_sha256=ZIP_SHA, archive_entries=477,
        verified_contents_members=476, original_validation_sha256=VALIDATION_SHA,
        original_validation_status="diagnostic_unverified", original_failure_preserved=True, all_members_verified=True),
        "Original archive member verification differs")
    return verify_native(directory / "full" / NATIVE_NAME, build), {name: digest(proof / name)
        for name in ("plan.json", "provenance.json", "gui-execution.json", "frozen-desktop-summary.json", "original-package-integrity.json")}



REVIEWED_TEST_TREE_SOURCE = "6f96beb84cd2c6e9c5b3e23271e2b5c7779f5116"
DELIVERY_FILES = frozenset({"README.md", "scripts/upload-beta10-frozen-assets.py",
    "scripts/test-upload-beta10-frozen-assets.py", ".github/workflows/publish-beta10-frozen-assets.yml"})


def verify_delivery_paths(paths):
    require(all(path in DELIVERY_FILES or path.startswith("docs/") for path in paths),
            "Unvalidated changes after the reviewed desktop test tree require new applicable acceptance")


def verify_artifacts(directory, review):
    verify_review(review)
    package, quality, build, old_validation, old_bytes = verify_original(directory)
    (native, after), proof_hashes = verify_full(directory, build, HARNESS_SOURCE)
    matrix = UPLOAD.verify_loss_matrix(directory / "matrix" / "matrix", SOURCE, ZIP_SHA, build["controlled_fixture"]["sha256"])
    extended = UPLOAD.verify_extended(directory / "extended", SOURCE, EXTENDED_RUN)
    identity = dict(schema_version=1, protocol="contain-beta10-final-acceptance-1", version=UPLOAD.VERSION,
        release_tag=UPLOAD.TAG, product_source_sha=SOURCE, accepted=False,
        acceptance_status="artifact_gates_verified_pending_remote_main_and_tag",
        package=dict(filename=UPLOAD.ZIP, bytes=package.stat().st_size, sha256=ZIP_SHA),
        build_manifest_sha256=BUILD_SHA, source_archive=build["source_archive"],
        original_build=dict(run_id=ORIGINAL_RUN, artifact_id=ORIGINAL_ZIP_ARTIFACT,
            validation_manifest_sha256=VALIDATION_SHA, validation_status=old_validation["status"],
            original_checks=old_validation["checks"], original_failure_retained=True),
        complete_desktop=dict(run_id=FULL_RUN, harness_source_sha=HARNESS_SOURCE, product_source_sha=SOURCE,
            integration_test_tree_reviewed_at=REVIEWED_TEST_TREE_SOURCE, provenance_files=proof_hashes,
            native_summaries=native),
        release_policy_review=review,
        historical_results=[
            dict(run_id=ORIGINAL_RUN, label="C1", result="failed", root_cause_confirmed=False, preserved=True),
            dict(run_id=42249868473, label="D5", result="failed", root_cause_confirmed=False, preserved=True),
            dict(run_id=42327363195, label="D7", result="passed_targeted_diagnosis_only",
                 root_cause_confirmed=False, substitute_for_complete_desktop=False, preserved=True)],
        capture_loss_matrix=dict(run_id=MATRIX_RUN, artifact_id=MATRIX_ARTIFACT, evidence=matrix),
        extended_quality=extended, quality_summary_sha256=QUALITY_SHA,
        quality_scope="Original product Check/Coverage/CoverageOriginal/Dependencies/Companion and same-source bounded ASan/Bench; current merged main quality is independently required.",
        delivery="Original product ZIP bytes and its failed historical VALIDATION.json are unchanged. This independent final acceptance applies the explicit Beta risk policy and retained same-ZIP desktop/capture evidence; it does not claim a new native run or a historical root-cause fix.")
    files = {UPLOAD.ZIP: package, "QUALITY-SUMMARY.json": quality,
        "install-record-before.png": DESKTOP.BEFORE_PATH, "install-record-after.png": after,
        "CAPTURE-LOSS-MATRIX.json": directory / "matrix/matrix/public/capture-loss-matrix.json",
        "CAPTURE-LOSS-PLAN.json": directory / "matrix/matrix/public/run-plan.json",
        "CAPTURE-LOSS-EXECUTION.json": directory / "matrix/matrix/public/execution.json"}
    build_identity = dict(schema_version=1, version=UPLOAD.VERSION, release_tag=UPLOAD.TAG,
        source_sha=SOURCE, product_source_sha=SOURCE, test_source_sha=HARNESS_SOURCE,
        reviewed_integration_test_tree_sha=REVIEWED_TEST_TREE_SOURCE,
        package=identity["package"], build_manifest_sha256=BUILD_SHA, source_archive=build["source_archive"],
        build={key: build[key] for key in ("viewer_configuration", "viewer_framework", "self_contained", "core_profile",
            "core_target", "included_frameworks", "toolchain", "build_platform", "signed")},
        frozen_binaries=[row for row in build["files"] if row["path"] in ("contain.exe", "Contain.Viewer.exe", "Contain.Viewer.dll")],
        original_validation_sha256=VALIDATION_SHA, original_validation_status=old_validation["status"],
        final_acceptance_file="FINAL-ACCEPTANCE.json", product_rebuilt=False, product_repacked=False,
        source_relationship="Product and embedded source archive are b0d2510; actual complete desktop harness is d1bca0b. Reviewed integration test tree is 6f96beb. Tag/main contain the reviewed test/delivery documentation and have separate identities.")
    return identity, build_identity, files, old_bytes


def remote_run(run, source, workflow, conclusion, *, branch="fix/beta10-capture-loss"):
    require(type(run) is int and run > 0, "Exact run ID is required")
    value = api(f"actions/runs/{run}")
    exact(value, dict(id=run, head_sha=source, path=workflow, status="completed", conclusion=conclusion,
        run_attempt=1, head_branch=branch), "Remote run identity/outcome differs")
    require(value.get("event") == "push" and value.get("head_repository", {}).get("full_name") == UPLOAD.REPO,
            "Remote run origin differs")
    return value


def remote_artifact(run, name, source, pinned=None):
    artifacts = api(f"actions/runs/{run}/artifacts?per_page=100")
    require(artifacts.get("total_count", 0) < 100, "Complete artifact set unavailable")
    matches = [item for item in artifacts["artifacts"] if item["name"] == name and item["expired"] is False]
    require(len(matches) == 1 and (pinned is None or matches[0]["id"] == pinned), "Remote artifact missing, ambiguous or replaced")
    exact(matches[0].get("workflow_run"), dict(id=run, head_sha=source), "Artifact does not belong to its exact source/run")
    return matches[0]["id"]


def comparison_paths(base, head):
    compare = api(f"compare/{base}...{head}")
    require(compare.get("status") in {"ahead", "identical"}
            and compare.get("merge_base_commit", {}).get("sha") == base,
            "Required source is not an ancestor of the accepted revision")
    rows = compare.get("files")
    require(isinstance(rows, list) and len(rows) < 300, "Complete source diff is unavailable or truncated")
    paths = []
    for row in rows:
        require(isinstance(row, dict) and isinstance(row.get("filename"), str), "Invalid comparison file")
        paths.append(row["filename"])
        if "previous_filename" in row:
            require(isinstance(row["previous_filename"], str), "Invalid renamed source path")
            paths.append(row["previous_filename"])
    return sorted(set(paths))


def remote_inputs():
    remote_run(ORIGINAL_RUN, SOURCE, ".github/workflows/ci.yml", "failure")
    remote_run(MATRIX_RUN, MATRIX_SOURCE, ".github/workflows/frozen-beta10-diagnosis.yml", "success")
    remote_run(FULL_RUN, HARNESS_SOURCE, FULL_WORKFLOW, "success")
    verify_changed_paths(comparison_paths(SOURCE, HARNESS_SOURCE))
    extended = UPLOAD.accepted_extended_remote(SOURCE, EXTENDED_RUN)
    require(extended == EXTENDED_ARTIFACT, "Original extended-quality artifact identity changed")
    return {
        "original/contain-windows-x64-viewer": remote_artifact(ORIGINAL_RUN, "contain-windows-x64-viewer", SOURCE, ORIGINAL_ZIP_ARTIFACT),
        "original/windows-read-only-quality": remote_artifact(ORIGINAL_RUN, "windows-read-only-quality", SOURCE, ORIGINAL_QUALITY_ARTIFACT),
        "original/windows-viewer-evidence": remote_artifact(ORIGINAL_RUN, NATIVE_NAME, SOURCE, ORIGINAL_VIEWER_ARTIFACT),
        "matrix": remote_artifact(MATRIX_RUN, "frozen-beta10-diagnosis", MATRIX_SOURCE, MATRIX_ARTIFACT),
        "extended": extended,
        "full/" + FULL_NAME: remote_artifact(FULL_RUN, FULL_NAME, HARNESS_SOURCE, FULL_ARTIFACT),
        "full/" + NATIVE_NAME: remote_artifact(FULL_RUN, NATIVE_NAME, HARNESS_SOURCE, NATIVE_ARTIFACT),
    }


def download_artifact(artifact_id, destination, temporary):
    archive_path = temporary / f"artifact-{artifact_id}.zip"
    with archive_path.open("wb") as stream:
        subprocess.check_call(["gh", "api", f"repos/{UPLOAD.REPO}/actions/artifacts/{artifact_id}/zip"], stdout=stream)
    with zipfile.ZipFile(archive_path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)) and len(names) <= 10000
                and sum(item.file_size for item in archive.infolist()) <= 1024 * 1024 * 1024
                and archive.testzip() is None,
                "Artifact exceeds its bounded extraction or has duplicate/corrupt files")
        for item in archive.infolist():
            path = Path(item.filename)
            require(not path.is_absolute() and ".." not in path.parts and "\\" not in item.filename
                    and ":" not in item.filename and (item.external_attr >> 16) & 0o170000 != 0o120000,
                    "Artifact has an unsafe member")
        archive.extractall(destination)


def verify_main_and_tag(main_run, tag_source):
    main = api("git/ref/heads/main")["object"]["sha"]
    require(main == tag_source, "Tag must identify the exact reviewed current main, separately from product source")
    remote_run(main_run, main, ".github/workflows/ci.yml", "success", branch="main")
    verify_changed_paths(comparison_paths(SOURCE, main))
    require(not CONTRACT_FILES.intersection(comparison_paths(HARNESS_SOURCE, main)),
            "D6 result waiting code changed and cannot be accepted as unchanged")
    verify_delivery_paths(comparison_paths(REVIEWED_TEST_TREE_SOURCE, main))
    ref = api(f"git/ref/tags/{UPLOAD.TAG}")["object"]
    tag_ref = dict(ref)
    for _ in range(8):
        if ref["type"] != "tag":
            break
        ref = api(f"git/tags/{ref['sha']}")["object"]
    require(ref["type"] == "commit" and ref["sha"] == tag_source, "Immutable Beta 10 tag differs from reviewed main")
    return dict(main_sha=main, tag_ref_sha=tag_ref["sha"], tag_ref_type=tag_ref["type"], tag_commit_sha=ref["sha"])


def verify_risk_tracking(review):
    for url in review["known_risk_tracking"].values():
        number = int(url.rsplit("/", 1)[1])
        issue = api(f"issues/{number}")
        require(issue.get("number") == number and issue.get("html_url") == url
                and issue.get("state") == "open" and "pull_request" not in issue,
                "Known Beta risk must be retained in an open issue, not asserted repaired")


def verify_main_quality(path, source):
    value = read(path)
    exact(value, dict(source_revision=source, current_required_gates_passed=True,
        all_recorded_invocations_passed=True, coverage_floors=dict(lines_percent=66.5, functions_percent=70)),
        "Merged main required quality gates did not pass")
    return dict(source_sha=source, sha256=digest(path), current_required_gates_passed=True,
        all_recorded_invocations_passed=True)


def existing_draft(tag_source, release_id=None):
    if release_id is None:
        values = api("releases?per_page=100")
        require(len(values) < 100, "Complete release list unavailable")
        matches = [value for value in values if value["tag_name"] == UPLOAD.TAG]
        require(len(matches) == 1, "Expected exactly one existing Beta 10 draft")
        release_id = matches[0]["id"]
    value = api(f"releases/{release_id}")
    require(value["tag_name"] == UPLOAD.TAG and value["target_commitish"] == tag_source
            and value["draft"] is True and value["prerelease"] is True,
            "Only the exact existing Beta 10 prerelease draft may receive assets")
    return value


def upload_assets(output, temporary, tag_source, release_id):
    expected = {path.name for path in output.iterdir()}
    require(not existing_draft(tag_source, release_id)["assets"], "Existing assets must not be overwritten or reused")
    readback = temporary / "readback"
    readback.mkdir()
    for path in sorted(output.iterdir()):
        current = existing_draft(tag_source, release_id)
        require(path.name not in {asset["name"] for asset in current["assets"]}, "Asset appeared before upload; refusing overwrite")
        subprocess.check_call(["gh", "release", "upload", UPLOAD.TAG, str(path), "--repo", UPLOAD.REPO])
        current = existing_draft(tag_source, release_id)
        matches = [asset for asset in current["assets"] if asset["name"] == path.name]
        require(len(matches) == 1 and matches[0].get("state") == "uploaded"
                and matches[0].get("size") == path.stat().st_size, "Uploaded asset identity/state/length differs")
        asset_id = matches[0]["id"]
        destination = readback / path.name
        with destination.open("wb") as stream:
            subprocess.check_call(["gh", "api", f"repos/{UPLOAD.REPO}/releases/assets/{asset_id}",
                "--header", "Accept: application/octet-stream"], stdout=stream)
        require(digest(destination) == digest(path), "Uploaded asset readback differs")
        after = existing_draft(tag_source, release_id)
        require(any(asset["id"] == asset_id and asset["name"] == path.name for asset in after["assets"]),
                "Asset ID changed during readback")
    require({asset["name"] for asset in existing_draft(tag_source, release_id)["assets"]} == expected,
            "Final asset set differs")


def write_assets(output, files, identity, build_identity, original_validation):
    output.mkdir(parents=True, exist_ok=False)
    for name, path in files.items():
        shutil.copyfile(path, output / name)
    (output / "ORIGINAL-FAILED-VALIDATION.json").write_bytes(original_validation)
    (output / "FINAL-ACCEPTANCE.json").write_text(json.dumps(identity, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    build_identity["final_acceptance_sha256"] = digest(output / "FINAL-ACCEPTANCE.json")
    (output / "BUILD-IDENTITY.json").write_text(json.dumps(build_identity, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    risks = identity["release_policy_review"]["known_risk_tracking"]
    start = ("Contain 0.3.2-beta.10 — Windows x64\n\n"
        "包内 VALIDATION.json 保留首次失败的历史结果，没有改成通过。当前发布依据见外部 FINAL-ACCEPTANCE.json。\n"
        "下载 contain-0.3.2-beta.10-windows-x64.zip，完整解压后运行 Contain.Viewer.exe。\n"
        "这是自包含应用，无需单独安装 .NET。包未签名；ETW 需要当前令牌已有相应权限，程序不会自动提权。\n"
        "选择安装器，默认跟随安装器记录；结束后保存并打开准确会话。搜索源写入，查看依据，复制事件 ID，退出重开核对。\n"
        "接收、候选、交付和保存丢失分别报告；历史缺字段不是零。Unknown 和丢失后抑制升级仍保留。\n\n"
        f"产品源码：{SOURCE}\n产品 ZIP SHA-256：{ZIP_SHA}\n"
        f"实际完整桌面测试源码：{HARNESS_SOURCE}\n"
        "原产品 ZIP 未重编译或重打包；包内 VALIDATION.json 是首次失败的历史结果，保持原样。\n"
        "请同时阅读独立 FINAL-ACCEPTANCE.json：D4 同包两轮采集无接收／候选丢失，D6 完整桌面检查通过，\n"
        "包括真实安装、准确保存读回、父子来源、事件解释、正常退出和冷启动。这不是一次新的原生运行。\n"
        "本次 Beta 政策允许以下历史未知风险继续独立跟踪，未声称已修复：\n"
        f"C1 首次空记录库 Ready 超时原因未确认：{risks[RISK_CODES[0]]}\n"
        f"D5 安装结果观察阶段必需控件缺失原因未确认：{risks[RISK_CODES[1]]}\n"
        f"Parallels 原会话 57,767 条丢失缺原机分项：{risks[RISK_CODES[2]]}\n\n"
        "已验证 Windows x64 runner 的有界场景；未覆盖完整第三方安装器、Parallels、ARM64、真实显示器 DPI 切换或任意负载矩阵。\n"
        "有限范围且没有基线时 Degraded 不自动表示失败。High 是有依据的执行进程关联，不是最终所有权。\n"
        "不承诺完整软件足迹、最终归属或安全删除。请保留历史失败；不要上传私人数据库或完整日志。\n")
    (output / "START-HERE.txt").write_text(start, encoding="utf-8")
    (output / "SHA256SUMS.txt").write_text("".join(digest(path) + "  " + path.name + "\n"
        for path in sorted(output.iterdir())), encoding="utf-8")
    require(digest(output / UPLOAD.ZIP) == ZIP_SHA, "Output changed the original accepted product ZIP")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--main-run", type=int)
    parser.add_argument("--tag-source")
    parser.add_argument("--local-input", type=Path)
    parser.add_argument("--output", type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--upload", action="store_true")
    mode.add_argument("--verify-artifacts-only", action="store_true")
    args = parser.parse_args()
    require(not args.verify_artifacts_only or args.local_input, "Local verification requires downloaded immutable artifacts")
    require(args.local_input is None or args.verify_artifacts_only,
            "Local inputs are diagnostic verification only; remote delivery must download the bound artifacts")
    review_path = Path(__file__).resolve().parent.parent / REVIEW_PATH
    require(review_path.is_file(), "Explicit reviewed release policy is required")
    review = verify_review(read(review_path))
    if not args.verify_artifacts_only:
        require(type(args.main_run) is int and args.main_run > 0
                and isinstance(args.tag_source, str) and re.fullmatch(r"[0-9a-f]{40}", args.tag_source),
                "Exact successful main CI and tag/main source are required")
        require(subprocess.check_output(["git", "show", f"HEAD:{REVIEW_PATH}"]) == review_path.read_bytes(),
                "Release policy review must be committed without local edits")
        require(not subprocess.check_output(["git", "status", "--porcelain"]),
                "Remote publication must use a clean reviewed checkout")
    with tempfile.TemporaryDirectory(prefix="contain-beta10-frozen-") as temporary:
        root = Path(temporary)
        selected = None
        main_source = None
        tag_identity = None
        if not args.verify_artifacts_only:
            tag_identity = verify_main_and_tag(args.main_run, args.tag_source)
            main_source = tag_identity["main_sha"]
            require(subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip() == main_source,
                    "Delivery tools must execute from exact current reviewed main")
            verify_risk_tracking(review)
            selected = remote_inputs()
            selected["current-quality"] = remote_artifact(args.main_run, "windows-read-only-quality", main_source)
        directory = args.local_input or root / "input"
        if not args.local_input:
            for relative, artifact_id in selected.items():
                download_artifact(artifact_id, directory / relative, root)
        identity, build_identity, files, original_validation = verify_artifacts(directory, review)
        identity["release_policy_review_sha256"] = digest(review_path)
        if selected:
            require(remote_inputs() == {key: value for key, value in selected.items() if key != "current-quality"},
                    "Immutable remote artifact identities changed during verification")
            require(remote_artifact(args.main_run, "windows-read-only-quality", main_source) == selected["current-quality"],
                    "Current main quality artifact identity changed")
            require(verify_main_and_tag(args.main_run, args.tag_source) == tag_identity,
                    "Main/tag changed during verification")
            current_quality = directory / "current-quality/summary.json"
            identity.update(accepted=True, acceptance_status="all_release_gates_verified_with_documented_beta_risks",
                main_sha=main_source, tag_sha=tag_identity["tag_ref_sha"], tag_ref_type=tag_identity["tag_ref_type"],
                tag_commit_sha=tag_identity["tag_commit_sha"], main_ci_run=args.main_run, artifact_ids=selected,
                current_quality=verify_main_quality(current_quality, main_source))
            build_identity.update(main_sha=main_source, tag_sha=tag_identity["tag_ref_sha"],
                tag_ref_type=tag_identity["tag_ref_type"], tag_commit_sha=tag_identity["tag_commit_sha"],
                delivery_source_sha=main_source, main_ci_run=args.main_run)
            files["CURRENT-QUALITY-SUMMARY.json"] = current_quality
        output = args.output or root / "assets"
        write_assets(output, files, identity, build_identity, original_validation)
        if args.verify_artifacts_only:
            print("Original ZIP, policy and local immutable desktop/capture/quality artifacts verified; remote release acceptance remains pending.")
            return
        release_id = existing_draft(args.tag_source)["id"]
        if not args.upload:
            print("All same-ZIP evidence, unchanged contract, explicit Beta policy, current main CI, tag and draft verified; no upload.")
            return
        upload_assets(output, root, args.tag_source, release_id)
        print("Original product ZIP and separate final acceptance uploaded and read back; release remains a draft.")


if __name__ == "__main__":
    main()
