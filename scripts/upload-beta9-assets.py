"""Deliver the accepted Beta 9 artifact only; no build, overwrite or publication."""
import argparse
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile

REPO = "LuckyLivio/Contain"
TAG = "v0.3.2-beta.9"
VERSION = "0.3.2-beta.9"
ZIP = "contain-0.3.2-beta.9-windows-x64.zip"
NAMES = ("contain-windows-x64-viewer", "windows-read-only-quality", "windows-viewer-evidence")
REQUIRED_CHECKS = frozenset({
    "frozen_package_unchanged", "quality_complete", "native_ui_cli_and_export",
    "native_personal_records_and_cold_restart", "native_persisted_overview_and_navigation",
    "native_session_event_search", "native_search_export", "native_first_use_configuration",
    "native_layout_and_simulated_scale", "native_progress_and_failure_feedback",
    "native_gui_install_etw_and_exact_result", "native_first_use_install_flow",
})
GUIDANCE_PROTOCOL = "contain-install-guidance-verification-1"
FOLDER_PROTOCOL = "contain-folder-guidance-verification-1"
# Fixed expectations from the independently exercised native protocol fixture.
# This table validates evidence; it does not generate user-facing task guidance.
_GUIDANCE_FIELDS = ("stage", "step", "installer_started", "installer_exited", "installer_exit_code",
                    "capture_finished", "data_saved", "quality_level", "result_succeeded", "next_step")
GUIDANCE_CASES = {row[0]: dict(zip(_GUIDANCE_FIELDS, row[1:])) for row in (
    ("installer_still_running", "capturing", "installer_running", True, False, None, False, True, None, None,
        "安装器仍在运行，可能需要您在安装器窗口继续操作；无法判断它是否正在等待输入。"),
    ("exited_still_collecting", "capturing", "descendant_drain", True, True, 23, False, True, None, None,
        "无需继续操作安装器；请等待整理和保存。"),
    ("stopped_partial_finalizing", "finalizing", "evidence_replay", True, True, 23, True, True, None, None,
        "采集已停止，已有证据保存；请等待整理和最终结果确认。"),
    ("saved_result_readback", "finalizing", "result_readback", True, True, 23, True, True, None, None,
        "记录已保存，正在读回并确认本次结果，请稍候。"),
    ("terminal_waiting_for_collector", "finished", "completed", True, True, 23, True, True, "Degraded", None,
        "已收到最终状态，正在确认采集进程退出和本次结果，请稍候。"),
    ("confirmed_nonzero_installer_degraded", "finished", "completed", True, True, 23, True, True, "Degraded", True,
        "记录已保存，可以查看变化。"),
    ("failed_saved_evidence", "failed", "sqlite_save", True, True, 0, True, True, None, False,
        "本次记录失败或未确认完整结束，请查看任务详情。可打开已有记录查看已保存的证据。"),
    ("prelaunch_failed_no_evidence", "failed", "database_open", False, False, None, False, False, None, False,
        "本次记录失败或未确认完整结束，请查看任务详情。"),
    ("missing_terminal_result", "finalizing", "evidence_replay", True, True, 0, True, True, None, False,
        "本次记录失败或未确认完整结束，请查看任务详情。可打开已有记录查看已保存的证据。"),
    ("nonzero_collector_exit", "finished", "completed", True, True, 0, True, True, "Degraded", False,
        "本次记录失败或未确认完整结束，请查看任务详情。可打开已有记录查看已保存的证据。"),
)}
FOLDER_GUIDANCE = dict(
    protocol_revision=FOLDER_PROTOCOL,
    title="要记录哪个文件夹里的变化？",
    note="通常选软件准备安装到的已有文件夹。安装包的下载位置不代表安装目标；这里仅决定观察范围。",
    missing_hint="请选择一个已有文件夹，再开始记录。",
    visible_before_picker=True, explicit_selection_preserved=True,
)



FOLLOW_CONFIGURATION = dict(protocol_revision="contain-follow-configuration-verification-1", accepted=True,
    default_follow_no_scope=True, start_enabled=True, no_watch_argument=True, manual_folder_required=True,
    manual_selection_preserved=True, snapshot_conflict_explicit=True)
BEFORE_SHA256 = "7ef700437b685550fae05f5a4ea1aa58f874adb443dcd7caab2f44850330e2c7"
BEFORE_PATH = Path(__file__).resolve().parent.parent / "docs/examples/follow-installer-before.png"

EXTENDED_NAME = "follow-installer-extended-quality"
BASELINE_SOURCE = "a1f9876f4976c2e35b443d9634bbb06fa19f1927"
BENCH_CASES = ("event_json_parse_exact_ticks", "lifetime_ingest_resolve_batched_512",
    "completion_correlation_batched_512", "registry_open_get_close")


def require(value, message):
    if not value:
        raise ValueError(message)


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{REPO}/{path}"], text=True))


def digest(path):
    require(path.is_file() and not path.is_symlink(), "Expected an ordinary file")
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def guidance_verification_matches(progress, configuration):
    """Require all exercised UI/clipboard/detail states, not just a passed count."""
    guidance = progress.get("guidance_verification")
    folder = configuration.get("folder_guidance_verification")
    if not (isinstance(guidance, dict) and guidance.get("protocol_revision") == GUIDANCE_PROTOCOL
            and guidance.get("accepted") is True and guidance.get("late_callback_preserved") is True
            and isinstance(guidance.get("cases"), list) and isinstance(folder, dict)
            and all(type(folder.get(key)) is type(expected) and folder[key] == expected
                    for key, expected in FOLDER_GUIDANCE.items())):
        return False
    cases = guidance["cases"]
    if not (all(isinstance(case, dict) for case in cases)
            and [case.get("name") for case in cases] == list(GUIDANCE_CASES)):
        return False
    return all(case.get("ui_clipboard_details_match") is True
        and all(key in case and type(case[key]) is type(expected) and case[key] == expected
                for key, expected in GUIDANCE_CASES[case["name"]].items()) for case in cases)


def verify_artifacts(directory, source, run, zip_sha, extended_run):
    """Verify local immutable artifact bytes, independent of a release/draft."""
    package = directory / NAMES[0] / ZIP
    quality_path = directory / NAMES[1] / "summary.json"
    evidence = directory / NAMES[2]
    paths = {
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
    require(digest(package) == zip_sha, "ZIP differs from independently accepted hash")
    quality = json.loads(quality_path.read_text(encoding="utf-8"))
    summaries = {key: json.loads((evidence / filename).read_text(encoding="utf-8"))
                 for key, (filename, _) in paths.items()}
    with zipfile.ZipFile(package) as archive:
        require(archive.testzip() is None and len(archive.namelist()) == len(set(archive.namelist())), "ZIP integrity or duplicate-member failure")
        build_bytes, validation_bytes = archive.read("BUILD.json"), archive.read("VALIDATION.json")
        build, validation = json.loads(build_bytes), json.loads(validation_bytes)
        contents = json.loads(archive.read("CONTENTS.json"))
        require(build["source_sha"] == validation["source_sha"] == contents["source_sha"] == source
                and build["version"] == VERSION and build["self_contained"] is True
                and build["target"] == "win-x64", "Frozen build identity differs")
        require(validation["viewer_verified"] is True and set(validation["checks"]) == REQUIRED_CHECKS
                and all(value is True for value in validation["checks"].values())
                and isinstance(build.get("controlled_fixture"), dict)
                and all(validation.get(key) is True for key in ("graphical_install_verified", "graphical_install_automated_verified", "graphical_install_milestone_closed"))
                and validation["status"] == "bounded_graphical_install_verified",
                "All existing desktop gates and actual packaged follow capture must be verified")
        require(archive.read("SOURCE.zip"), "Missing frozen source archive")
        import io
        with zipfile.ZipFile(io.BytesIO(archive.read("SOURCE.zip"))) as source_archive:
            before = source_archive.read("docs/examples/follow-installer-before.png")
            require(hashlib.sha256(before).hexdigest() == BEFORE_SHA256 and digest(BEFORE_PATH) == BEFORE_SHA256,
                    "Retained native before screenshot differs from the frozen source")
        manifest_sha = hashlib.sha256(build_bytes).hexdigest()
        require(manifest_sha == validation["build_manifest_sha256"], "Build manifest hash differs")
        require(digest(quality_path) == validation["quality_summary_sha256"]
                and quality["source_revision"] == source and quality["current_required_gates_passed"] is True
                and quality["all_recorded_invocations_passed"] is True
                and quality["coverage_floors"] == dict(lines_percent=66.5, functions_percent=70), "Quality evidence differs")
        for key, (filename, hash_key) in paths.items():
            require(summaries[key] == validation[key] and digest(evidence / filename) == validation[hash_key],
                    "Native summary evidence differs: " + filename)
        for manifest in (build, contents):
            require(len(manifest["files"]) == len({item["path"] for item in manifest["files"]}), "Duplicate manifest member")
            for item in manifest["files"]:
                data = archive.read(item["path"])
                require(len(data) == item["bytes"] and hashlib.sha256(data).hexdigest() == item["sha256"], "Frozen content differs")
        require(set(archive.namelist()) == {x["path"] for x in contents["files"]} | {"CONTENTS.json"}, "Unexpected ZIP member")
        require(build["source_archive"]["source_sha"] == source
                and hashlib.sha256(archive.read("SOURCE.zip")).hexdigest() == build["source_archive"]["sha256"], "Source archive differs")
    # Reuse every existing native gate, including the independent ordered HTML
    # parser, then require the new guidance evidence from these exact binaries.
    spec = importlib.util.spec_from_file_location("beta9_package_gate", Path(__file__).with_name("package-viewer.py"))
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
    validators = {
        "native_verification": gate.verification_matches,
        "native_personal_records_verification": gate.personal_records_verification_matches,
        "native_overview_verification": gate.overview_verification_matches,
        "native_search_verification": gate.search_verification_matches,
        "native_first_use_configuration_verification": gate.first_use_configuration_verification_matches,
        "native_layout_verification": gate.layout_verification_matches,
        "native_progress_verification": gate.progress_verification_matches,
    }
    for key, validate in validators.items():
        require(validate(summaries[key], build, manifest_sha), "Native contract differs: " + key)
    require(gate.search_export_verification_matches(summaries["native_search_export_verification"], build, manifest_sha, evidence / "export-summary.json"),
            "Actual HTML content or native export evidence differs")
    progress = summaries["native_progress_verification"]
    configuration = summaries["native_first_use_configuration_verification"]
    require(guidance_verification_matches(progress, configuration), "Beta 9 guidance cases or folder explanation were not verified")
    installation = summaries["native_install_verification"]
    require(installation.get("first_use", {}).get("protocol_revision") == "contain-follow-install-flow-1"
            and gate.first_use_install_verification_matches(installation, build, manifest_sha, evidence / "install-summary.json"),
            "Actual single-input follow capture, independent source identities or screenshots differ")
    follow_config = configuration.get("follow_configuration_verification", {})
    require(all(type(follow_config.get(key)) is type(expected) and follow_config[key] == expected
                for key, expected in FOLLOW_CONFIGURATION.items()), "Default follow/manual configuration regression differs")
    layout = summaries["native_layout_verification"].get("follow_page_verification", {})
    conditions = layout.get("conditions", [])
    expected_layout = {"native_default_layout_resize_maximize": 1,
        "simulated_150_percent_layout": 1.5, "simulated_200_percent_layout": 2}
    require(layout.get("protocol_revision") == "contain-follow-layout-verification-1" and layout.get("accepted") is True
            and isinstance(conditions, list) and len(conditions) == 3 and all(isinstance(c, dict) for c in conditions)
            and {c.get("name") for c in conditions} == set(expected_layout), "Modern follow page layout conditions missing")
    for condition in conditions:
        require(type(condition.get("requested_scale")) in (int, float)
                and condition["requested_scale"] == expected_layout[condition["name"]]
                and type(condition.get("actual_device_dpi")) is int and condition["actual_device_dpi"] > 0
                and all(type(condition.get(key)) is int and 0 < condition[key] <= 16384 for key in ("window_width", "window_height"))
                and all(condition.get(key) is True for key in ("default_follow", "no_scope_required", "start_near_mode",
                    "primary_before_advanced", "manual_available", "path_copy_exact", "keyboard_start_reachable")),
                "Modern follow page text, input or action reachability differs")
    after = evidence / "screenshots/install-follow-configured.png"
    shot = installation["first_use"]["screenshots"][0]
    require((shot["width"], shot["height"]) == (740, 620)
            and installation["first_use"]["actual_device_dpi"] == 96
            and digest(after) != BEFORE_SHA256, "Before/after window dimensions or actual DPI differ")
    extended = verify_extended(directory / EXTENDED_NAME, source, extended_run)
    identity = dict(schema_version=1, version=VERSION, release_tag=TAG, source_sha=source,
        extended_quality=extended,
        ci_run_url=f"https://github.com/{REPO}/actions/runs/{run}",
        package=dict(filename=ZIP, bytes=package.stat().st_size, sha256=zip_sha),
        build_manifest_sha256=manifest_sha, validation_manifest_sha256=hashlib.sha256(validation_bytes).hexdigest(),
        source_archive=build["source_archive"],
        build={k: build[k] for k in ("viewer_configuration", "viewer_framework", "self_contained", "core_profile", "core_target", "included_frameworks", "toolchain", "build_platform", "signed")},
        frozen_binaries=[x for x in build["files"] if x["path"] in ("contain.exe", "Contain.Viewer.exe", "Contain.Viewer.dll")],
        validation=dict(status=validation["status"], checks=validation["checks"],
            native_install=installation, native_progress=progress, first_use_configuration=configuration,
            native_export=summaries["native_search_export_verification"], native_layout=summaries["native_layout_verification"],
            quality_summary_sha256=digest(quality_path), nextest=quality["nextest_junit"],
            coverage_workspace=quality["coverage_workspace"]["export_totals"],
            coverage_original=quality["coverage_original_packages"]["export_totals"], coverage_floors=quality["coverage_floors"]),
        screenshots=[dict(filename="install-record-before.png", sha256=BEFORE_SHA256, width=740, height=620,
            actual_device_dpi=96, source_sha="ec8d1134ead49adaaed60ee6847c4ea6664fbd75", kind="retained_native_production_form"),
            dict(filename="install-record-after.png", sha256=digest(after), width=740, height=620,
            actual_device_dpi=96, source_sha=source, kind="actual_packaged_executable_window")],
        scope="One actual packaged EXE follow-installer recording with independent process/write truth; saved records and synthetic protocol regressions remain separate.",
        delivery="Unmodified accepted CI ZIP; no rebuild/repack. All assets read back after upload; no clobber.",
        not_executed=[ "Third-party installer or MSI/updater/service-agent matrix", "Windows 10/11 manual matrix, ARM64 or Parallels", "Real monitor DPI transitions", "New disk performance experiments or extended fuzz campaign"])
    return identity, {ZIP: package, "QUALITY-SUMMARY.json": quality_path,
        "install-record-before.png": BEFORE_PATH, "install-record-after.png": after}


def verify_extended(directory, source, run):
    require(type(run) is int and run > 0, "Required extended-quality run identity is missing")
    names = ("baseline-benchmark.json", "candidate-benchmark.json", "benchmark-identity.json", "fuzz-identity.json")
    values = {name: json.loads((directory / name).read_text(encoding="utf-8-sig")) for name in names}
    identity, fuzz = values["benchmark-identity.json"], values["fuzz-identity.json"]
    require(identity.get("source_sha") == source and identity.get("baseline_sha") == BASELINE_SOURCE
            and identity.get("schema_version") == 1 and identity.get("accepted") is True
            and identity.get("same_runner") is True, "Extended benchmark source/baseline was not accepted")
    require(fuzz.get("schema_version") == 1 and fuzz.get("source_sha") == source and fuzz.get("accepted") is True
            and fuzz.get("toolchain") == "nightly-2026-11-05"
            and type(fuzz.get("seconds_per_target")) is int and fuzz["seconds_per_target"] == 30
            and fuzz.get("targets") == ["event_json", "lifetime_sequences", "completion_correlation"],
            "Required actual Windows fuzz smoke did not pass")
    definitions = []
    for name, revision in (("baseline-benchmark.json", BASELINE_SOURCE), ("candidate-benchmark.json", source)):
        value = values[name]
        allowed = {"schema_version", "measurement_kind", "etw_validation", "etw_providers_started", "sqlite_measured",
            "profile", "source_commit", "results", "compiler", "os", "os_version", "arch", "fixture_generation_timed",
            "assertions_timed", "notes", "working_tree_clean"}
        require(set(value) <= allowed, "Unreviewed benchmark host metadata must remain private")
        require(value.get("schema_version") == 1 and value.get("measurement_kind") == "synthetic_in_memory"
                and value.get("etw_validation") == "not_performed" and value.get("etw_providers_started") is False
                and value.get("sqlite_measured") is False and value.get("profile") == "release"
                and value.get("source_commit") == revision and value.get("working_tree_clean") is True
                and value.get("fixture_generation_timed") is False and value.get("assertions_timed") is True,
                "Benchmark scope/profile/source differs")
        rows = value.get("results")
        require(isinstance(rows, list) and len(rows) == 4 and all(isinstance(row, dict) for row in rows)
                and [row.get("name") for row in rows] == list(BENCH_CASES), "Required benchmark case definitions differ")
        case_definitions = []
        for row in rows:
            samples = row.get("sample_ns_per_work_item")
            require(type(row.get("measured_samples")) is int and row["measured_samples"] == 7
                    and type(row.get("warmup_samples")) is int and row["warmup_samples"] == 2
                    and type(row.get("work_items_per_sample")) is int and row["work_items_per_sample"] == 20000
                    and type(row.get("input_records_per_sample")) is int and row["input_records_per_sample"] in (0,20000)
                    and type(row.get("validated_checksum")) is int and row["validated_checksum"] > 0
                    and isinstance(samples, list) and len(samples) == 7
                    and all(type(x) in (int,float) and math.isfinite(x) and x > 0 for x in samples),
                    "Seven validated benchmark samples are required")
            case_definitions.append({key: row[key] for key in ("name", "work_items_per_sample", "input_records_per_sample",
                "measured_samples", "warmup_samples", "validated_checksum")})
        definitions.append(case_definitions)
    require(definitions[0] == definitions[1], "Baseline and candidate benchmark denominators or checksums differ")
    return dict(ci_run_url=f"https://github.com/{REPO}/actions/runs/{run}", source_sha=source,
        files=[dict(filename=name,sha256=digest(directory / name)) for name in names],
        benchmark_identity=identity, baseline_benchmark=values["baseline-benchmark.json"],
        candidate_benchmark=values["candidate-benchmark.json"], fuzz_identity=fuzz)


def accepted_extended_remote(source, run):
    value = api(f"actions/runs/{run}")
    require(value["head_sha"] == source and value["event"] == "push" and value["conclusion"] == "success"
            and value["status"] == "completed" and value["run_attempt"] == 1
            and value["path"] == ".github/workflows/follow-installer-quality.yml"
            and value["head_repository"]["full_name"] == REPO, "Extended-quality CI identity differs")
    values = api(f"actions/runs/{run}/artifacts?per_page=100")["artifacts"]
    selected = [item for item in values if item["name"] == EXTENDED_NAME and item["expired"] is False]
    require(len(selected) == 1, "Extended-quality artifact missing or ambiguous")
    return selected[0]["id"]


def accepted_remote(source, run):
    value = api(f"actions/runs/{run}")
    require(value["head_sha"] == source and value["event"] == "push" and value["conclusion"] == "success"
            and value["status"] == "completed" and value["run_attempt"] == 1
            and value["path"] == ".github/workflows/ci.yml"
            and value["head_repository"]["full_name"] == REPO, "Accepted CI identity differs")
    artifacts = api(f"actions/runs/{run}/artifacts?per_page=100")["artifacts"]
    selected = {}
    for name in NAMES:
        candidates = [x for x in artifacts if x["name"] == name and x["expired"] is False]
        require(len(candidates) == 1, "Accepted artifact missing or ambiguous")
        selected[name] = candidates[0]["id"]
    return selected


def existing_draft(source, release_id=None):
    if release_id is None:
        # The by-tag endpoint may omit drafts; authenticated listing exposes
        # them. Bind one exact ID before any later upload or readback operation.
        matches = [x for x in api("releases?per_page=100") if x["tag_name"] == TAG]
        require(len(matches) == 1, "Expected exactly one existing Beta 9 draft")
        release_id = matches[0]["id"]
    value = api(f"releases/{release_id}")
    require(value["tag_name"] == TAG and value["target_commitish"] == source
            and value["draft"] is True and value["prerelease"] is True, "Only the exact existing Beta 9 draft may receive assets")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--run", required=True, type=int)
    parser.add_argument("--extended-run", required=True, type=int)
    parser.add_argument("--zip-sha256", required=True)
    parser.add_argument("--local-input", type=Path)
    parser.add_argument("--output", type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--upload", action="store_true")
    mode.add_argument("--verify-artifacts-only", action="store_true")
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{40}", args.source) and re.fullmatch(r"[0-9a-f]{64}", args.zip_sha256)
            and args.run > 0 and args.extended_run > 0, "Invalid accepted identity inputs")
    require(not args.verify_artifacts_only or args.local_input, "Local verification needs downloaded artifacts")
    with tempfile.TemporaryDirectory(prefix="contain-beta9-") as temporary:
        root = Path(temporary)
        selected = accepted_remote(args.source, args.run) if not args.verify_artifacts_only else None
        extended_id = accepted_extended_remote(args.source, args.extended_run) if not args.verify_artifacts_only else None
        directory = args.local_input or root / "input"
        if not args.local_input:
            for name in NAMES:
                subprocess.check_call(["gh", "run", "download", str(args.run), "--name", name, "--dir", str(directory / name), "--repo", REPO])
            subprocess.check_call(["gh", "run", "download", str(args.extended_run), "--name", EXTENDED_NAME,
                "--dir", str(directory / EXTENDED_NAME), "--repo", REPO])
        identity, files = verify_artifacts(directory, args.source, args.run, args.zip_sha256, args.extended_run)
        if selected:
            require(accepted_remote(args.source, args.run) == selected
                    and accepted_extended_remote(args.source, args.extended_run) == extended_id, "Artifact identities changed")
        output = args.output or root / "assets"
        output.mkdir(parents=True, exist_ok=False)
        for name, path in files.items():
            shutil.copyfile(path, output / name)
        (output / "BUILD-IDENTITY.json").write_text(json.dumps(identity, ensure_ascii=False, indent=2) + "\n")
        sums = "".join(digest(path) + "  " + path.name + "\n" for path in sorted(output.iterdir()))
        (output / "SHA256SUMS.txt").write_text(sums)
        if args.verify_artifacts_only:
            print("Local immutable artifacts and native guidance regressions verified; no upload.")
            return
        ref = api(f"git/ref/tags/{TAG}")["object"]
        for _ in range(8):
            if ref["type"] != "tag":
                break
            ref = api(f"git/tags/{ref['sha']}")["object"]
        require(ref["type"] == "commit" and ref["sha"] == args.source, "Immutable Beta 9 tag differs")
        compare = api(f"compare/{args.source}...main")
        require(compare["status"] in ("ahead", "identical") and compare["merge_base_commit"]["sha"] == args.source, "Accepted source is not merged into main")
        release_id = existing_draft(args.source)["id"]
        expected = {p.name for p in output.iterdir()}
        require(not {a["name"] for a in existing_draft(args.source, release_id)["assets"]} - expected, "Unexpected draft assets")
        if not args.upload:
            print("Exact Beta 9 tag, draft, CI and local artifact hashes verified; no upload.")
            return
        readback = root / "readback"
        for path in sorted(output.iterdir()):
            current = existing_draft(args.source, release_id)
            if path.name not in {a["name"] for a in current["assets"]}:
                subprocess.check_call(["gh", "release", "upload", TAG, str(path), "--repo", REPO])
            subprocess.check_call(["gh", "release", "download", TAG, "--pattern", path.name, "--dir", str(readback), "--repo", REPO])
            require(digest(readback / path.name) == digest(path), "Uploaded asset readback differs")
        require({a["name"] for a in existing_draft(args.source, release_id)["assets"]} == expected, "Final asset set differs")
        print("All six accepted assets uploaded and read back; release remains a draft.")


if __name__ == "__main__":
    main()
