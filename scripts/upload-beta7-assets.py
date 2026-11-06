"""Deliver the accepted Beta 7 artifact only; no build, overwrite or publication."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile

REPO = "LuckyLivio/Contain"
TAG = "v0.3.2-beta.7"
VERSION = "0.3.2-beta.7"
ZIP = "contain-0.3.2-beta.7-windows-x64.zip"
NAMES = ("contain-windows-x64-viewer", "windows-read-only-quality", "windows-viewer-evidence")
REQUIRED_CHECKS = frozenset({
    "frozen_package_unchanged", "quality_complete", "native_ui_cli_and_export",
    "native_personal_records_and_cold_restart", "native_persisted_overview_and_navigation",
    "native_session_event_search", "native_search_export", "native_first_use_configuration",
    "native_first_use_install_flow", "native_gui_install_etw_and_exact_result",
    "native_layout_and_simulated_scale", "native_progress_and_failure_feedback",
})


def require(value, message):
    if not value:
        raise ValueError(message)


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{REPO}/{path}"], text=True))


def digest(path):
    require(path.is_file() and not path.is_symlink(), "Expected an ordinary file")
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_artifacts(directory, source, run, zip_sha):
    """Verify local immutable artifact bytes, independent of a release/draft."""
    package = directory / NAMES[0] / ZIP
    quality_path = directory / NAMES[1] / "summary.json"
    evidence = directory / NAMES[2]
    export_path = evidence / "export-summary.json"
    install_path = evidence / "install-summary.json"
    configuration_path = evidence / "first-use-configuration-summary.json"
    require(digest(package) == zip_sha, "ZIP differs from independently accepted hash")
    quality = json.loads(quality_path.read_text())
    export = json.loads(export_path.read_text())
    installation = json.loads(install_path.read_text())
    configuration = json.loads(configuration_path.read_text())
    with zipfile.ZipFile(package) as archive:
        require(archive.testzip() is None, "ZIP integrity failure")
        build_bytes, validation_bytes = archive.read("BUILD.json"), archive.read("VALIDATION.json")
        build, validation = json.loads(build_bytes), json.loads(validation_bytes)
        contents = json.loads(archive.read("CONTENTS.json"))
        require(build["source_sha"] == validation["source_sha"] == contents["source_sha"] == source
                and build["version"] == VERSION and build["self_contained"] is True
                and build["target"] == "win-x64", "Frozen build identity differs")
        require(validation["viewer_verified"] is True and set(validation["checks"]) == REQUIRED_CHECKS
                and all(value is True for value in validation["checks"].values())
                and validation["graphical_install_verified"] is True
                and validation["graphical_install_milestone_closed"] is True
                and validation["status"] == "bounded_graphical_install_verified",
                "Actual packaged first-use capture, picker flow and all desktop regressions must be verified")
        require(hashlib.sha256(build_bytes).hexdigest() == validation["build_manifest_sha256"], "Build manifest hash differs")
        require(digest(quality_path) == validation["quality_summary_sha256"]
                and quality["source_revision"] == source and quality["current_required_gates_passed"] is True
                and quality["all_recorded_invocations_passed"] is True,
                "Quality evidence differs")
        require(export == validation["native_search_export_verification"]
                and digest(export_path) == validation["search_export_verification_summary_sha256"], "Export evidence differs")
        require(installation == validation["native_install_verification"]
                and digest(install_path) == validation["install_verification_summary_sha256"], "Actual install evidence differs")
        require(configuration == validation["native_first_use_configuration_verification"]
                and digest(configuration_path) == validation["first_use_configuration_summary_sha256"], "First-use configuration evidence differs")
        for manifest in (build, contents):
            for item in manifest["files"]:
                data = archive.read(item["path"])
                require(len(data) == item["bytes"] and hashlib.sha256(data).hexdigest() == item["sha256"], "Frozen content differs")
        require(set(archive.namelist()) == {x["path"] for x in contents["files"]} | {"CONTENTS.json"}, "Unexpected ZIP member")
        require(hashlib.sha256(archive.read("SOURCE.zip")).hexdigest() == build["source_archive"]["sha256"], "Source archive differs")
    # Re-read actual screenshot and report bytes through the mandatory package gates.
    spec = importlib.util.spec_from_file_location("beta7_package_gate", Path(__file__).with_name("package-viewer.py"))
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
    require(gate.search_export_verification_matches(export, build, hashlib.sha256(build_bytes).hexdigest(), export_path),
            "Actual HTML content or native export evidence differs")
    require(gate.first_use_configuration_verification_matches(configuration, build, hashlib.sha256(build_bytes).hexdigest()),
            "First-use configuration/failure regressions differ")
    require(gate.first_use_install_verification_matches(installation, build, hashlib.sha256(build_bytes).hexdigest(), install_path),
            "Actual first-use EXE capture, native input, saved scope or screenshots differ")
    screenshots = {name: evidence / "screenshots" / name for name in sorted(gate.FIRST_USE_SCREENSHOTS)}
    identity = dict(schema_version=1, version=VERSION, release_tag=TAG, source_sha=source,
        ci_run_url=f"https://github.com/{REPO}/actions/runs/{run}",
        package=dict(filename=ZIP, bytes=package.stat().st_size, sha256=zip_sha),
        build_manifest_sha256=hashlib.sha256(build_bytes).hexdigest(),
        validation_manifest_sha256=hashlib.sha256(validation_bytes).hexdigest(),
        source_archive=build["source_archive"],
        build={k: build[k] for k in ("viewer_configuration", "viewer_framework", "self_contained", "core_profile", "core_target", "included_frameworks", "toolchain", "build_platform", "signed")},
        frozen_binaries=[x for x in build["files"] if x["path"] in ("contain.exe", "Contain.Viewer.exe", "Contain.Viewer.dll")],
        validation=dict(status=validation["status"], checks=validation["checks"], native_install=installation,
            first_use_configuration=configuration, native_export=export, native_layout=validation["native_layout_verification"],
            quality_summary_sha256=digest(quality_path), nextest=quality["nextest_junit"],
            coverage_workspace=quality["coverage_workspace"]["export_totals"],
            coverage_original=quality["coverage_original_packages"]["export_totals"], coverage_floors=quality["coverage_floors"]),
        screenshots=[dict(filename=name, bytes=path.stat().st_size, sha256=digest(path),
            actual_device_dpi=installation["first_use"]["actual_device_dpi"],
            kind="actual_packaged_Windows_window", synthetic_fixture=True) for name, path in screenshots.items()],
        delivery="Unmodified accepted CI ZIP; no rebuild/repack. All assets read back after upload; no clobber.",
        not_executed=["Third-party installer or MSI/updater/service-agent matrix", "Windows 10/11 manual matrix, ARM64 or Parallels", "Real monitor DPI transitions", "New disk performance experiments or fuzz campaign"])
    return identity, {ZIP: package, "QUALITY-SUMMARY.json": quality_path, **screenshots}


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
        require(len(matches) == 1, "Expected exactly one existing Beta 7 draft")
        release_id = matches[0]["id"]
    value = api(f"releases/{release_id}")
    require(value["tag_name"] == TAG and value["target_commitish"] == source
            and value["draft"] is True and value["prerelease"] is True, "Only the exact existing Beta 7 draft may receive assets")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--run", required=True, type=int)
    parser.add_argument("--zip-sha256", required=True)
    parser.add_argument("--local-input", type=Path)
    parser.add_argument("--output", type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--upload", action="store_true")
    mode.add_argument("--verify-artifacts-only", action="store_true")
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{40}", args.source) and re.fullmatch(r"[0-9a-f]{64}", args.zip_sha256)
            and args.run > 0, "Invalid accepted identity inputs")
    require(not args.verify_artifacts_only or args.local_input, "Local verification needs downloaded artifacts")
    with tempfile.TemporaryDirectory(prefix="contain-beta7-") as temporary:
        root = Path(temporary)
        selected = accepted_remote(args.source, args.run) if not args.verify_artifacts_only else None
        directory = args.local_input or root / "input"
        if not args.local_input:
            for name in NAMES:
                subprocess.check_call(["gh", "run", "download", str(args.run), "--name", name, "--dir", str(directory / name), "--repo", REPO])
        identity, files = verify_artifacts(directory, args.source, args.run, args.zip_sha256)
        if selected:
            require(accepted_remote(args.source, args.run) == selected, "Artifact identities changed")
        output = args.output or root / "assets"
        output.mkdir(parents=True, exist_ok=False)
        for name, path in files.items():
            shutil.copyfile(path, output / name)
        (output / "BUILD-IDENTITY.json").write_text(json.dumps(identity, ensure_ascii=False, indent=2) + "\n")
        sums = "".join(digest(path) + "  " + path.name + "\n" for path in sorted(output.iterdir()))
        (output / "SHA256SUMS.txt").write_text(sums)
        if args.verify_artifacts_only:
            print("Local immutable artifacts, first-use capture and actual Windows screenshots verified; no upload.")
            return
        ref = api(f"git/ref/tags/{TAG}")["object"]
        for _ in range(8):
            if ref["type"] != "tag":
                break
            ref = api(f"git/tags/{ref['sha']}")["object"]
        require(ref["type"] == "commit" and ref["sha"] == args.source, "Immutable Beta 7 tag differs")
        compare = api(f"compare/{args.source}...main")
        require(compare["status"] in ("ahead", "identical") and compare["merge_base_commit"]["sha"] == args.source, "Accepted source is not merged into main")
        release_id = existing_draft(args.source)["id"]
        expected = {p.name for p in output.iterdir()}
        require(not {a["name"] for a in existing_draft(args.source, release_id)["assets"]} - expected, "Unexpected draft assets")
        if not args.upload:
            print("Exact Beta 7 tag, draft, CI and local artifact hashes verified; no upload.")
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
