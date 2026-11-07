"""Verify and upload one accepted WPF package to an existing empty draft.

Main-only delivery: no build, repack, tag creation, asset replacement or release
publication. The caller reviews authenticated draft readback, publishes, then
verifies the public download separately.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import uuid
import zipfile


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PACKAGE = load("modern_frozen_package", "package-modern-desktop.py")
require, digest = PACKAGE.require, PACKAGE.sha
REPO = "LuckyLivio/Contain"
TAG = "v0.4.0-beta.1"
ZIP = "contain-0.4.0-beta.1-windows-x64.zip"
WORKFLOW = ".github/workflows/modern-desktop.yml"
ROOT = Path(__file__).resolve().parents[1]
CORE_ACCEPTANCE_SHA = "f7f5cf09d809a50834998bbc454b4ff8fffadd2fd4c6a79524eedecbf6cffdad"
NATIVE_CHECKS = (
    "frozen_package_identity", "empty_personal_library_and_native_picker_cancel", "saved_records_exact_identity_and_outcomes",
    "search_130_events_53_matches_exact_order", "search_copy_explain_and_export_all", "external_legacy_readonly_and_return",
    "same_name_session_switch_and_clear", "compact_visible_keyboard_actions", "saved_records_cold_restart",
    "gui_default_configuration_no_implicit_install", "gui_single_start_close_guard_and_saved_uuid",
    "real_etw_parent_child_independent_oracle", "real_event_explain_export_and_cold_restart", "source_databases_and_package_unchanged")
MODEL_CHECKS = (
    'installer selection and cancellation never start a process',
    'start is exclusive and binds literal arguments and configuration',
    'progress dispatch is bounded and cannot replace a terminal result',
    'close is refused while recording without cancelling its process',
    'pre-installer failure permits correction and retains the error',
    'nonzero installer exit and incomplete coverage remain separate from saving',
    'unfinished saved evidence opens its exact session only on request',
    'missing measurements and real zero stay distinct',
    'environment probe locks start and close but never starts an installer',
    'advanced options are retained in the immutable request',
    'automatic readback errors do not erase a saved result',
    'empty personal library performs no CLI read or creation',
    'same-name same-time rows have distinct visible labels and exact identities',
    'same-name late summary cannot replace exact newer session',
    'cancelled query late error cannot replace newer success',
    'late search cannot mix events into another same-name session',
    'editing search draft invalidates pending results before explicit search',
    'late explanation cannot change copy identity',
    'mismatched event payload refuses explain and copy',
    '130 event and 53 match pagination uses exact ordered IDs',
    'zero search results preserve measured zero and disable match export',
    'export snapshot survives search and session changes inside save dialog',
    'page export freezes selected event and offset',
    'export cancellation preserves selection and does not become error',
    'export existing target and IO errors keep view and error kind',
    'changed search snapshot invalidates only current export authorization',
    'accurate captured session cannot fall back to same name',
    'wrong session summary and CLI failure are not successful empty pages',
    'finished Incomplete keeps saved loss message and missing counts',
    'unfinished missing exit and no-baseline follow state stay distinct',
    'external library remains distinct from personal destination',
    'disposed model rejects late query and leaves no active selection',
    'close cancels pending query and export then awaits both cleanup paths',
    'close also awaits superseded query that ignores cancellation',
)
ORACLE_CHECKS = (
    "independent_fixed_answers", "capture_and_fixture_finished", "no_predetermined_scope_and_runtime_directory",
    "bounded_follow_admission", "bounded_storage", "self_io_excluded", "finished_saved_session", "no_preset_watch_roots",
    "actual_sources_and_loss_accounting", "parent_independent_write", "parent_content", "child_independent_write",
    "child_content", "unrelated_independent_write", "unrelated_content", "distinct_process_instances",
    "parent_verified_source_write", "parent_reopened_explanation", "parent_missing_baseline_explicit",
    "child_verified_source_write", "child_reopened_explanation", "child_missing_baseline_explicit",
    "direct_child_parent_instance", "unrelated_never_attributed")
SHOTS = {
    "records-empty.png": "isolated-empty", "records-sample.png": "synthetic-viewer-fixtures",
    "overview-finished.png": "synthetic-persisted-outcome", "overview-historical.png": "synthetic-persisted-outcome",
    "overview-interrupted.png": "synthetic-persisted-outcome", "events-search-explain.png": "synthetic-130-events-53-matches",
    "compact-events.png": "synthetic-viewer-fixtures", "compact-new-record.png": "isolated-configuration-no-install",
    "new-record-configured.png": "real-controlled-fixture-before-explicit-start", "task-real-recording.png": "real-controlled-etw-capture",
    "real-saved-overview.png": "real-controlled-etw-saved", "real-event-explanation.png": "real-controlled-etw-saved",
    "real-cold-restart.png": "real-controlled-etw-saved-cold-restart"}
CONTROL_CHECKS = (
    "library_selection_route_reproduces_null_target_failure", "action_peer_has_name_id_button_and_invoke",
    "action_invoke_routes_exactly_one_existing_click", "disabled_action_rejects_invoke",
    "queued_action_disabled_before_dispatch_does_not_click", "reenabled_action_remains_usable",
    "external_unique_button_identity", "uia_navigation_invokes_exactly_once", "disabled_navigation_refuses_invoke",
    "reenabled_navigation_invokes_once", "keyboard_navigation_invokes_once", "owned_host_normal_exit")
PROTECTED_PREFIXES = ("desktop/Contain.Desktop/", "desktop/Contain.Desktop.Model/", "desktop/Contain.Desktop.Tests/",
                      "desktop/Contain.Desktop.Model.Tests/", "desktop/Contain.Desktop.Controls.Tests/", "desktop/Contain.Viewer/",
                      "desktop/Contain.Viewer.Presentation.Tests/", "crates/", "tools/offline/", "licenses/")
PROTECTED_FILES = {"global.json", "Cargo.toml", "Cargo.lock", "LICENSE", "THIRD-PARTY-NOTICES.md", WORKFLOW,
                   "scripts/package-modern-desktop.py", "scripts/test-package-modern-desktop.py",
                   "scripts/verify-follow-installer-experiment.py", "scripts/test-verify-follow-installer-experiment.py"}
DELIVERY_FILES = {"scripts/publish-modern-desktop.py", "scripts/test-publish-modern-desktop.py",
                  ".github/workflows/publish-modern-desktop.yml"}


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def exact(value, fields, reason):
    require(isinstance(value, dict) and all(type(value.get(k)) is type(v) and value.get(k) == v for k, v in fields.items()), reason)


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{REPO}/{path}"], text=True))


def sha_shape(value, size=40):
    return isinstance(value, str) and re.fullmatch(r"[a-f0-9]{" + str(size) + "}", value) is not None


def successful_checks(value, names):
    rows = value.get("checks")
    require(isinstance(rows, list) and len(rows) == len(names)
            and {row.get("name") for row in rows} == set(names)
            and all(row.get("status") == "passed" for row in rows), "fixed native check set did not pass")
    exact(value, dict(passed=len(names), failed=0, not_run=0), "native totals differ")


def verify_model(value):
    exact(value, dict(protocol="contain-modern-model-checks-1", accepted=True, synthetic=True,
                      native_windows_ui=False, real_etw=False, failure=None, message=None), "model regression was not accepted")
    names = value.get("checks")
    require(isinstance(names, list) and all(isinstance(name, str) for name in names)
            and len(names) == len(set(names)) and set(MODEL_CHECKS) <= set(names)
            and type(value.get("passed")) is int and value["passed"] == len(names), "required model contracts missing")
    return value


def verify_controls(value, source):
    exact(value, dict(schema_version=1, protocol="contain-wpf-action-navigation-regression-2", source_sha=source,
          native_windows=True, synthetic=True, real_etw_executed=False, actual_packaged_executable=False,
          external_uia=True, host_count=1, normal_exit=True, forced_exit=False,
          production_source_link="desktop/Contain.Desktop/Controls/ActionNavigationItem.cs", wpf_ui_version="4.3.0",
          planned_checks=list(CONTROL_CHECKS), accepted=True, retries=0), "native production navigation-control regression missing")
    successful_checks(value, CONTROL_CHECKS)
    return value


def verify_dependencies(value):
    exact(value, dict(version=1, parameters="--vulnerable --include-transitive"), "dependency audit kind differs")
    require(value.get("sources") == ["https://api.nuget.org/v3/index.json"] and not value.get("problems"), "dependency audit source or problem")
    projects = value.get("projects")
    require(isinstance(projects, list) and len(projects) == 1
            and projects[0].get("path", "").replace("\\", "/").endswith("/desktop/Contain.Desktop/Contain.Desktop.csproj"),
            "dependency audit project differs")
    require(all(not framework.get("topLevelPackages") and not framework.get("transitivePackages")
                for framework in projects[0].get("frameworks", [])), "vulnerable dependency requires review")
    # Keep the original artifact hash in acceptance, but do not publish runner paths.
    return dict(protocol="contain-modern-dependency-review-1", source="https://api.nuget.org/v3/index.json",
                project="desktop/Contain.Desktop/Contain.Desktop.csproj", vulnerable_packages=[],
                pinned_dependencies=PACKAGE.DEPENDENCIES, sdk=PACKAGE.SDK, runtime=PACKAGE.RUNTIME)


def verify_native(value, plan, build, archive, source):
    public_keys = {"schema_version", "protocol", "source_sha", "native_windows", "windows_version", "architecture", "framework",
                  "product_process_only", "real_user_records_accessed", "accepted", "preview_checks_passed", "native_install_requested",
                  "actual_etw", "cold_restart_verified", "viewer_exe_sha256", "viewer_dll_sha256", "core_sha256", "package_manifest_sha256",
                  "planned_checks", "checks", "assertions", "passed", "failed", "not_run", "retries", "screenshots", "launches",
                  "failure_screenshots", "navigation_diagnostics", "search", "live", "scope"}
    require(set(value) <= public_keys and not value.get("failure_screenshots"), "unreviewed native fields or failure screenshot")
    require(value.get("navigation_diagnostics") == [], "accepted native report must have an empty navigation diagnostic list")
    exact(value, dict(schema_version=1, protocol="contain-wpf-native-verification-1", source_sha=source,
                      native_windows=True, framework="WPF + WPF UI", product_process_only=True,
                      real_user_records_accessed=False, accepted=True, preview_checks_passed=True,
                      native_install_requested=True, actual_etw=True, cold_restart_verified=True, retries=0),
          "full native WPF acceptance required; a preview or old WinForms report is insufficient")
    require(value.get("architecture") == "X64" and isinstance(value.get("windows_version"), str)
            and re.fullmatch(r"10\.0\.[0-9]+(?:\.[0-9]+)?", value["windows_version"]), "native Windows x64 platform missing")
    require(value.get("planned_checks") == list(NATIVE_CHECKS), "native planned contract differs")
    successful_checks(value, NATIVE_CHECKS)
    require(type(value.get("assertions")) is int and value["assertions"] >= len(NATIVE_CHECKS)
            and len(value.get("launches", [])) >= 4
            and all(row.get("process_exited") is True for row in value["launches"]), "native assertions or normal process exits missing")
    exact(plan, dict(schema_version=1, protocol="contain-wpf-native-plan-1", source_sha=source, retries=0,
                     maximum_native_captures=1, native_install_requested=True, product_process_only=True,
                     real_user_records_accessed=False, planned_checks=list(NATIVE_CHECKS),
                     deadlines_ms=dict(ready=40000, uia=20000, transition=30000, installation=180000, close=20000),
                     fixture=dict(event_count=130, matches=53, first_page=50, second_page=3, selected_event="synthetic-search-event-120")),
          "predeclared native plan or deadlines changed")
    for field, filename in {"viewer_exe_sha256": "Contain.Viewer.exe", "viewer_dll_sha256": "Contain.Viewer.dll",
                            "core_sha256": "contain.exe", "package_manifest_sha256": "BUILD.json"}.items():
        require(value.get(field) == hashlib.sha256(archive.read(filename)).hexdigest(), "native binary/manifest does not match original ZIP")
    require(build["source_sha"] == source and build["core_sha256"] == PACKAGE.CORE_SHA, "native package source differs")
    expected_ids = [f"synthetic-search-event-{n:03}" for n in range(70, 123)]
    exact(value.get("search"), dict(expected_ids=expected_ids, first_page_ids=expected_ids[:50],
          second_page_ids=expected_ids[50:], exported_ids=expected_ids, selected_event_id="synthetic-search-event-120"),
          "130/53 search, selection or export identity differs")
    live = value.get("live", {})
    for field in ["session_id", "selected_event_id"]:
        require(isinstance(live.get(field), str) and str(uuid.UUID(live[field])) == live[field], "exact real UUID is missing")
    oracle = live.get("follow_oracle", {})
    exact(oracle, dict(schema_version=1, protocol="contain-follow-installer-experiment-1", source_sha=source,
          synthetic=False, controlled_fixture=True, maximum_native_captures=1, retries=0, accepted=True,
          session_id=live["session_id"], ownership_claim=False, current_path_claim=False, installation_before_baseline=False,
          post_install_file_reads=False, unresolved=[]), "independent follow oracle differs")
    successful_checks(oracle, ORACLE_CHECKS)
    follow = oracle.get("follow_counters", {})
    exact(follow, dict(direct_children_only=True, baseline_available=False, candidate_record_limit=2048,
          candidate_byte_limit=8388608, resource_limit=4096, candidate_dropped=0), "follow loss or resource boundary differs")
    require(all(type(follow.get(key)) is int and 0 <= follow[key] <= limit for key, limit in
                [("candidate_high_water_records", 2048), ("candidate_high_water_bytes", 8388608)]), "candidate high water invalid")
    accounting = live.get("accounting", {})
    exact(accounting, dict(etw_events_lost=0, etw_buffers_lost=0, dropped_events=0, context_losses=0,
                          decode_errors=0, drain_timed_out=False), "source loss or drain failure")
    require(all(type(accounting.get(key)) is int and accounting[key] > 0 for key in ["events_received", "events_retained"]), "actual event denominator missing")
    exact(accounting.get("receiver"), dict(queue_overflow=0, enqueue_disconnected=0, queue_pending=0), "receiver loss")
    exact(accounting.get("handoff"), dict(overflow=0, oversized=0, disconnected=0, serialize_failed=0, pending=0), "handoff loss")
    for storage in [accounting.get("raw_storage"), oracle.get("storage_counters")]:
        exact(storage, dict(failed=0, quota_dropped=0, quota_bytes=67108864), "raw storage loss or bound differs")
        require(type(storage.get("persisted")) is int and storage["persisted"] > 0
                and type(storage.get("committed_bytes")) is int and 0 < storage["committed_bytes"] <= 67108864, "saved raw evidence missing")
    observations = oracle.get("observations", [])
    require(len(observations) == 3 and {row.get("label") for row in observations} == {"parent", "child", "unrelated"}, "target evidence missing")
    for label, rule in [("parent", "InstallerPid"), ("child", "DescendantProcess")]:
        row = next(item for item in observations if item["label"] == label)
        exact(row, dict(operation="write_requested", rule=rule, completion=True, state_validated=False), "target completion/association differs")
        require(row.get("confidence") in {"High", "Certain"} and row.get("actor_confidence") in {"High", "Certain"}
                and type(row.get("pid")) is int and row["pid"] > 0
                and isinstance(row.get("creation_time"), str) and row["creation_time"].isdigit() and int(row["creation_time"]) > 0,
                "target PID/birth evidence missing")
        if label == "child": require(row.get("event_id") == live["selected_event_id"], "cold-reopened child event differs")
    require(next(row for row in observations if row["label"] == "unrelated").get("classification") in {"Unknown", "unobserved"}, "unrelated write misattributed")
    return value


def verify_screenshots(rows, directory):
    require(isinstance(rows, list) and len(rows) == len(SHOTS) and {row.get("name") for row in rows} == set(SHOTS), "required screenshot set differs")
    result = {}
    for row in rows:
        name = row["name"]
        path = directory / name
        require(path.is_file() and not path.is_symlink() and digest(path) == row.get("sha256"), "screenshot bytes differ")
        data = path.read_bytes()
        require(len(data) >= 33 and data[:8] == b"\x89PNG\r\n\x1a\n" and data[12:16] == b"IHDR", "screenshot is not PNG")
        width, height = struct.unpack(">II", data[16:24])
        require((width, height) == (row.get("width"), row.get("height"))
                and 400 <= width <= 8192 and 300 <= height <= 8192
                and type(row.get("dpi")) is int and 96 <= row["dpi"] <= 768
                and row.get("data_kind") == SHOTS[name], "native screenshot dimensions/DPI/data label differs")
        if name.startswith("compact-"): require(width <= 900 and height <= 700, "compact window screenshot is not compact")
        result[name] = path
    require(any(row["dpi"] == 96 for row in rows), "actual 96 DPI baseline not demonstrated")
    return result


def verify_quality(value, source):
    exact(value, dict(source_revision=source, current_required_gates_passed=True, all_recorded_invocations_passed=True,
                      all_recorded_gates_passed=True, required_gates_complete=True,
                      coverage_floors=dict(lines_percent=66.5, functions_percent=70)), "current main quality did not pass")
    required = {"Check", "Coverage", "CoverageOriginal", "Dependencies", "Companion"}
    require(value.get("missing_gates") == {mode: [] for mode in required}, "main quality gate missing")
    require({row.get("mode") for row in value.get("invocations", [])} == required
            and all(row.get("passed") is True for row in value["invocations"]), "main quality invocation missing or failed")
    return value


def unchanged_paths(paths):
    require(all(isinstance(path, str) for path in paths), "invalid source comparison")
    for path in paths:
        protected = path in PROTECTED_FILES or (path.startswith(PROTECTED_PREFIXES) and not path.endswith(".md"))
        require(not protected and (path.endswith(".md") or path.startswith("docs/") or path in DELIVERY_FILES),
                "accepted product/test contract or unreviewed build input changed: " + path)


def remote_run(run, source, workflow, branch=None):
    value = api(f"actions/runs/{run}")
    exact(value, dict(id=run, head_sha=source, path=workflow, status="completed", conclusion="success", event="push", run_attempt=1), "run identity/outcome differs")
    require(value.get("head_repository", {}).get("full_name") == REPO and (branch is None or value.get("head_branch") == branch), "run repository or branch differs")
    return value


def artifact(run, source, name):
    value = api(f"actions/runs/{run}/artifacts?per_page=100")
    rows = value.get("artifacts", [])
    require(value.get("total_count") == len(rows) and len(rows) < 100, "artifact listing incomplete")
    matches = [row for row in rows if row.get("name") == name and row.get("expired") is False]
    require(len(matches) == 1, "required artifact missing or ambiguous")
    selected = matches[0]
    exact(selected.get("workflow_run"), dict(id=run, head_sha=source), "artifact belongs to another run/source")
    require(type(selected.get("id")) is int and selected["id"] > 0
            and re.fullmatch(r"sha256:[a-f0-9]{64}", selected.get("digest", "")), "artifact immutable digest missing")
    return {key: selected[key] for key in ["id", "name", "digest"]}


def download_artifact(value, directory, temporary):
    path = temporary / (str(value["id"]) + ".zip")
    if not path.exists():
        with path.open("xb") as stream:
            subprocess.check_call(["gh", "api", f"repos/{REPO}/actions/artifacts/{value['id']}/zip"], stdout=stream)
    require("sha256:" + digest(path) == value["digest"], "downloaded artifact digest differs")
    with zipfile.ZipFile(path) as archive:
        require(len(archive.infolist()) <= 10000 and sum(row.file_size for row in archive.infolist()) <= 1024 ** 3
                and archive.testzip() is None, "artifact extraction bound or CRC differs")
        PACKAGE.safe_names([row.filename.rstrip("/") for row in archive.infolist()])
        require(all((row.external_attr >> 16) & 0o170000 != 0o120000 for row in archive.infolist()), "artifact contains link")
        archive.extractall(directory)


def main_tag(source, product):
    require(api("git/ref/heads/main")["object"]["sha"] == source, "current main differs from reviewed tag source")
    ref = api(f"git/ref/tags/{TAG}")["object"]
    original = dict(ref)
    for _ in range(8):
        if ref.get("type") != "tag": break
        ref = api(f"git/tags/{ref['sha']}")["object"]
    require(ref.get("type") == "commit" and ref.get("sha") == source, "existing tag does not identify reviewed main")
    require(subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip() == source
            and not subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT), "publisher must use clean current main")
    subprocess.check_call(["git", "merge-base", "--is-ancestor", product, source], cwd=ROOT)
    paths = subprocess.check_output(["git", "diff", "--name-only", "--no-renames", product, source], cwd=ROOT, text=True).splitlines()
    unchanged_paths(paths)
    return dict(main_sha=source, tag_object_sha=original["sha"], tag_object_type=original["type"], tag_commit_sha=source)


def existing_draft(source, release_id=None):
    if release_id is None:
        rows = api("releases?per_page=100")
        require(len(rows) < 100, "release list incomplete")
        matches = [row for row in rows if row.get("tag_name") == TAG]
        require(len(matches) == 1, "exactly one existing Beta.1 draft required")
        release_id = matches[0]["id"]
    value = api(f"releases/{release_id}")
    exact(value, dict(tag_name=TAG, target_commitish=source, draft=True, prerelease=True), "only the exact existing prerelease draft may receive assets")
    return value


def core_evidence(temporary):
    release = api("releases/tags/v0.3.2-beta.10")
    exact(release, dict(tag_name="v0.3.2-beta.10", draft=False, prerelease=True), "immutable Beta.10 release unavailable")
    assets = [row for row in release["assets"] if row.get("name") == "FINAL-ACCEPTANCE.json"]
    require(len(assets) == 1 and assets[0].get("state") == "uploaded", "published Beta.10 acceptance unavailable")
    path = temporary / "beta10-acceptance.json"
    with path.open("xb") as stream:
        subprocess.check_call(["gh", "api", f"repos/{REPO}/releases/assets/{assets[0]['id']}", "--header", "Accept: application/octet-stream"], stdout=stream)
    require(digest(path) == CORE_ACCEPTANCE_SHA, "published Beta.10 acceptance bytes changed")
    value = read(path)
    exact(value, dict(accepted=True, product_source_sha=PACKAGE.CORE_SOURCE), "original core acceptance differs")
    require(value["package"]["sha256"] == PACKAGE.BASELINE_SHA
            and value["capture_loss_matrix"]["run_id"] == 42231290678
            and value["capture_loss_matrix"]["evidence"]["summary"]["accepted"] is True, "unchanged-core matrix evidence missing")
    return dict(version="0.3.2-beta.10", source_sha=PACKAGE.CORE_SOURCE, binary_sha256=PACKAGE.CORE_SHA,
                baseline_zip_sha256=PACKAGE.BASELINE_SHA, acceptance_sha256=CORE_ACCEPTANCE_SHA,
                acceptance_url=f"https://github.com/{REPO}/releases/download/v0.3.2-beta.10/FINAL-ACCEPTANCE.json",
                capture_matrix_run=42231290678, reused_for_identical_core_only=True, old_winforms_not_used_as_wpf_acceptance=True)


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def prepare_assets(output, archive_path, evidence, main_evidence, quality_dir, args, refs, artifacts, core):
    require(not output.exists() and digest(archive_path) == args.zip_sha256, "output exists or frozen ZIP hash differs")
    build = PACKAGE.verify(archive_path)
    require(build["source_sha"] == args.product_source_sha, "frozen product source differs")
    with zipfile.ZipFile(archive_path) as archive:
        require((evidence / "BUILD-IDENTITY.json").read_bytes() == archive.read("BUILD.json"), "artifact build identity differs")
    native, native_plan, model = read(evidence / "modern-desktop-summary.json"), read(evidence / "modern-desktop-plan.json"), verify_model(read(evidence / "model-checks.json"))
    controls = verify_controls(read(evidence / "navigation-control-summary.json"), args.product_source_sha)
    plan = read(evidence / "plan.json")
    exact(plan, dict(schema_version=1, protocol="contain-modern-validation-plan-1", source_sha=args.product_source_sha,
          native_windows=True, full_acceptance=True, maximum_gui_installations=1, retries=0, capture_core_changed=False,
          core_source_sha=PACKAGE.CORE_SOURCE, baseline_zip_sha256=PACKAGE.BASELINE_SHA,
          previous_failures_retained=True, old_winforms_acceptance_is_not_new_desktop_evidence=True, zip_frozen_before_native=True), "accepted run was not planned as full acceptance")
    with zipfile.ZipFile(archive_path) as archive:
        verify_native(native, native_plan, build, archive, args.product_source_sha)
        start = archive.read("START-HERE.txt")
    shots = verify_screenshots(native["screenshots"], evidence / "screenshots")
    dependencies = verify_dependencies(read(evidence / "dotnet-dependencies.json"))
    main_model = verify_model(read(main_evidence / "model-checks.json"))
    main_controls = verify_controls(read(main_evidence / "navigation-control-summary.json"), args.tag_source_sha)
    verify_dependencies(read(main_evidence / "dotnet-dependencies.json"))
    quality = verify_quality(read(quality_dir / "summary.json"), args.tag_source_sha)
    # Publish reviewed aggregates and named screenshots only, never glob the
    # evidence artifact: it can also contain HTML or unrelated diagnostic files.
    output.mkdir()
    shutil.copyfile(archive_path, output / ZIP)
    (output / "START-HERE.txt").write_bytes(start)
    for name in ["BUILD-IDENTITY.json", "modern-desktop-summary.json", "modern-desktop-plan.json", "model-checks.json", "navigation-control-summary.json", "plan.json"]:
        shutil.copyfile(evidence / name, output / name)
    for name, path in shots.items(): shutil.copyfile(path, output / name)
    dependencies["original_audit_sha256"] = digest(evidence / "dotnet-dependencies.json")
    write_json(output / "DEPENDENCY-REVIEW.json", dependencies)
    write_json(output / "MAIN-QUALITY.json", dict(source_sha=args.tag_source_sha, original_summary_sha256=digest(quality_dir / "summary.json"),
               current_required_gates_passed=True, modes=[row["mode"] for row in quality["invocations"]],
               model_passed=main_model["passed"], model_summary_sha256=digest(main_evidence / "model-checks.json"),
               native_control_passed=main_controls["passed"], native_control_summary_sha256=digest(main_evidence / "navigation-control-summary.json")))
    acceptance = dict(schema_version=1, protocol="contain-modern-final-acceptance-1", version=PACKAGE.VERSION, release_tag=TAG,
        accepted=True, product_source_sha=args.product_source_sha, native_test_source_sha=args.product_source_sha,
        delivery_source_sha=args.tag_source_sha, **refs, package=dict(filename=ZIP, sha256=args.zip_sha256, bytes=archive_path.stat().st_size),
        product_rebuilt_for_release=False, product_repacked_for_release=False,
        source_relationship="The original accepted ZIP and its native harness use product_source_sha. Tag/main may add reviewed documentation and delivery tools; protected product and test files must be identical.",
        accepted_native_run=args.accepted_run_id, main_quality_run=args.main_quality_run_id, main_desktop_run=args.main_desktop_run_id,
        native_passed=len(NATIVE_CHECKS), model_passed=model["passed"], native_control_passed=controls["passed"], source_core_evidence=core, artifact_identities=artifacts,
        native_summary_sha256=digest(evidence / "modern-desktop-summary.json"), build_manifest_sha256=native["package_manifest_sha256"],
        windows=dict(version=native["windows_version"], architecture=native["architecture"], measured_dpi=sorted({row["dpi"] for row in native["screenshots"]})),
        screenshots=native["screenshots"], history_preserved=True,
        known_risks=[dict(issue=f"https://github.com/{REPO}/issues/{number}", status="cause_not_confirmed") for number in [16, 17, 18]],
        limitations=["One controlled Windows x64 runner installation and isolated synthetic records; not a Parallels or ARM64 validation.",
                     "No claim of full installer coverage, final file ownership, safe deletion or monitor-DPI switching.",
                     "The unchanged core reuses Beta.10 bounded capture evidence; no new broad pressure matrix was run.",
                     "M11 had one isolated synthetic navigation keyboard timeout before packaging. M12 staged diagnosis did not reproduce it; no historical cause or product repair is claimed. Final package acceptance remains separate.",
                     "C1 first-empty-library Ready timeout, D5 missing result control, and the original Parallels 57,767 losses remain historically unexplained."])
    write_json(output / "FINAL-ACCEPTANCE.json", acceptance)
    (output / "SHA256SUMS.txt").write_text("".join(digest(path) + "  " + path.name + "\n" for path in sorted(output.iterdir())), encoding="utf-8")
    require(digest(output / ZIP) == args.zip_sha256, "original accepted ZIP was changed")
    return acceptance


def upload_assets(directory, source, release_id, temporary):
    require(not existing_draft(source, release_id)["assets"], "refuse nonempty draft; never overwrite or resume assets")
    expected = {path.name for path in directory.iterdir()}
    readback = temporary / "readback"
    readback.mkdir()
    uploaded = {}
    def asset_map(rows):
        require(len(rows) == len({row["name"] for row in rows}), "duplicate release asset name")
        return {row["name"]: {key: row.get(key) for key in ["id", "digest", "size"]} for row in rows}
    for path in sorted(directory.iterdir()):
        current = existing_draft(source, release_id)
        require(asset_map(current["assets"]) == uploaded, "draft assets changed concurrently")
        subprocess.check_call(["gh", "release", "upload", TAG, str(path), "--repo", REPO])
        current = existing_draft(source, release_id)
        matches = [row for row in current["assets"] if row["name"] == path.name]
        require(len(matches) == 1 and matches[0].get("state") == "uploaded" and matches[0].get("size") == path.stat().st_size
                and matches[0].get("digest") == "sha256:" + digest(path), "uploaded asset metadata differs")
        destination = readback / path.name
        with destination.open("xb") as stream:
            subprocess.check_call(["gh", "api", f"repos/{REPO}/releases/assets/{matches[0]['id']}", "--header", "Accept: application/octet-stream"], stdout=stream)
        require(digest(destination) == digest(path), "asset readback hash differs")
        uploaded[path.name] = {key: matches[0][key] for key in ["id", "digest", "size"]}
    require(set(uploaded) == expected and asset_map(existing_draft(source, release_id)["assets"]) == uploaded,
            "final asset set or identities differ")


def workflow_context(environment):
    require(environment.get("GITHUB_REPOSITORY") == REPO and environment.get("GITHUB_REF") == "refs/heads/main"
            and environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch" and environment.get("GITHUB_RUN_ATTEMPT") == "1",
            "delivery requires first-attempt main workflow_dispatch in the original repository")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ["accepted-run-id", "main-quality-run-id", "main-desktop-run-id"]: parser.add_argument("--" + key, type=int, required=True)
    for key in ["product-source-sha", "tag-source-sha", "zip-sha256"]: parser.add_argument("--" + key, required=True)
    args = parser.parse_args()
    workflow_context(os.environ)
    require(all(sha_shape(value, size) for value, size in [(args.product_source_sha, 40), (args.tag_source_sha, 40), (args.zip_sha256, 64)])
            and all(value > 0 for value in [args.accepted_run_id, args.main_quality_run_id, args.main_desktop_run_id]), "exact run/source/hash inputs required")
    refs = main_tag(args.tag_source_sha, args.product_source_sha)
    remote_run(args.accepted_run_id, args.product_source_sha, WORKFLOW)
    remote_run(args.main_quality_run_id, args.tag_source_sha, ".github/workflows/ci.yml", "main")
    remote_run(args.main_desktop_run_id, args.tag_source_sha, WORKFLOW, "main")
    selected = {
        "package": artifact(args.accepted_run_id, args.product_source_sha, "modern-desktop-frozen-package"),
        "native": artifact(args.accepted_run_id, args.product_source_sha, "modern-desktop-evidence"),
        "main": artifact(args.main_desktop_run_id, args.tag_source_sha, "modern-desktop-evidence"),
        "quality": artifact(args.main_quality_run_id, args.tag_source_sha, "windows-read-only-quality")}
    draft = existing_draft(args.tag_source_sha)
    require(not draft["assets"], "refuse nonempty draft")
    with tempfile.TemporaryDirectory(prefix="contain-modern-release-") as folder:
        temporary = Path(folder)
        for name, value in selected.items(): download_artifact(value, temporary / name, temporary)
        output = temporary / "assets"
        acceptance = prepare_assets(output, temporary / "package" / ZIP, temporary / "native", temporary / "main", temporary / "quality",
                                    args, refs, selected, core_evidence(temporary))
        require(main_tag(args.tag_source_sha, args.product_source_sha) == refs, "main or tag changed during verification")
        for key, run, source, name, workflow, branch in [
            ("package", args.accepted_run_id, args.product_source_sha, "modern-desktop-frozen-package", WORKFLOW, None),
            ("native", args.accepted_run_id, args.product_source_sha, "modern-desktop-evidence", WORKFLOW, None),
            ("main", args.main_desktop_run_id, args.tag_source_sha, "modern-desktop-evidence", WORKFLOW, "main"),
            ("quality", args.main_quality_run_id, args.tag_source_sha, "windows-read-only-quality", ".github/workflows/ci.yml", "main")]:
            remote_run(run, source, workflow, branch)
            require(artifact(run, source, name) == selected[key], "accepted remote artifact changed during verification")
        upload_assets(output, args.tag_source_sha, draft["id"], temporary)
        require(main_tag(args.tag_source_sha, args.product_source_sha) == refs, "main or tag changed during upload; leave draft for review")
        print(json.dumps(dict(uploaded_to_existing_draft=True, published=False, release_id=draft["id"],
                              zip_sha256=acceptance["package"]["sha256"], assets=len(list(output.iterdir())))))


if __name__ == "__main__":
    main()
