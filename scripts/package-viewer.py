"""Freeze the actual Windows viewer and CLI, then package their observed validation.

No capture, installer, migration or remote upload runs here. Only the explicitly
synthetic production-store sample may be copied; user databases stay local.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

SPEC = importlib.util.spec_from_file_location("viewer_trial_helpers", Path(__file__).with_name("package-trial.py"))
TRIAL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRIAL)
require, read, write, sha = TRIAL.require, TRIAL.read, TRIAL.write, TRIAL.sha
safe_file, relative_path = TRIAL.safe_file, TRIAL.relative_path
PROTOCOL = "contain-desktop-package-1"
VERIFICATION = "contain-desktop-verification-1"
SAMPLE_IDS = {"synthetic-finished", "synthetic-historical", "synthetic-interrupted"}
# Set by the native harness contract, not by whichever checks happen to finish.
REQUIRED_CHECKS = frozenset({
    "frozen_package_and_actual_ui_assembly_identity", "synthetic_inputs_and_special_character_paths",
    "real_cli_records_summary_and_exact_time_strings", "real_cli_second_page_and_explanation_identity",
    "real_cli_diff_precise_integer_and_unknown_state", "strict_protocol_rejects_invalid_schema_kind_and_shape",
    "missing_null_unknown_enums_and_json_precision", "real_cli_database_and_process_failures_are_errors",
    "doctor_rejected_without_starting_capture", "native_window_empty_state", "native_production_executable_launch",
    "ui_open_finished_record_and_overview", "ui_page_then_select_and_explain_same_event",
    "ui_filter_explicitly_applies_to_current_page", "ui_local_report_matches_current_record_page_and_event",
    "ui_historical_name_and_unknown_evidence", "ui_unfinished_and_unavailable_metrics",
    "ui_valid_empty_database_and_cancel", "ui_errors_are_visible_and_do_not_create_records",
    "all_source_and_viewed_databases_unchanged", "ui_state_changes_filter_and_reason",
    "ui_packaged_three_record_sample_switching",
})


def row(path, name):
    actual = TRIAL.identity(path)
    return {"path": name, "bytes": actual["bytes"], "sha256": actual["sha256"]}


def source_identity(repo):
    revision, core_tree = TRIAL.source_identity(repo)
    names = subprocess.check_output(["git", "-C", str(repo), "ls-files", "--", "desktop", "scripts/package-viewer.py",
        "scripts/test-package-viewer.py", ".github/workflows/ci.yml", "tools/offline", "docs/schema"], text=True,
        encoding="utf-8").splitlines()
    require(names and any(name.startswith("desktop/") for name in names), "viewer_source_missing")
    identities = [{"path": name, "sha256": sha(safe_file(repo / relative_path(name)))} for name in sorted(names)]
    tree = hashlib.sha256(json.dumps(identities, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return revision, core_tree, tree, identities


def runtime_contract(viewer):
    config = read(safe_file(viewer / "Contain.Viewer.runtimeconfig.json"))
    options = config.get("runtimeOptions", {})
    frameworks = options.get("includedFrameworks", [])
    require(options.get("tfm") == "net8.0" and "framework" not in options and "frameworks" not in options,
            "viewer_must_be_self_contained_net8")
    require(isinstance(frameworks, list) and len(frameworks) == 2 and
            {f.get("name") for f in frameworks} == {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}
            and all(re.fullmatch(r"8\.0\.[0-9]+", f.get("version", "")) for f in frameworks),
            "self_contained_framework_identity_missing")
    for name in ("Contain.Viewer.exe", "Contain.Viewer.dll", "Contain.Viewer.deps.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"):
        safe_file(viewer / name)
    return frameworks


def copy_runtime_licenses(frameworks, destination, root):
    copied = []
    for framework in frameworks:
        pack = root / (framework["name"].lower() + ".runtime.win-x64") / framework["version"]
        require(pack.is_dir(), "runtime_pack_for_license_missing")
        matches = {p.name.lower().replace("-", ""): p for p in pack.iterdir() if p.is_file()}
        # The official WindowsDesktop pack uses LICENSE (no extension) and
        # currently supplies no separate notices; Core.App supplies both files.
        license_source = matches.get("license.txt", matches.get("license"))
        require(license_source is not None, "runtime_license_missing")
        record = {"framework": framework["name"], "version": framework["version"],
                  "third_party_notices": "not_supplied_in_runtime_pack"}
        for name, source in (("license.txt", license_source), ("thirdpartynotices.txt", matches.get("thirdpartynotices.txt"))):
            if source is None:
                continue
            relative = "licenses/" + framework["name"] + "-" + name
            target = destination / relative
            target.parent.mkdir(exist_ok=True)
            shutil.copyfile(safe_file(source), target)
            record["license" if name == "license.txt" else "third_party_notices"] = relative
        copied.append(record)
    return copied


def validate_sample(contain, fixtures):
    metadata = read(safe_file(fixtures / "metadata.json"))
    require(metadata.get("synthetic") is True and metadata.get("real_etw") is False
            and metadata.get("kind") == "synthetic_desktop_viewer_fixtures"
            and metadata.get("combined_database") == "viewer-demo.db", "synthetic_sample_marker_missing")
    sample = safe_file(fixtures / "viewer-demo.db")
    expected = [entry for entry in metadata.get("databases", []) if entry.get("name") == sample.name]
    require(len(expected) == 1 and expected[0].get("sha256") == sha(sample)
            and expected[0].get("bytes") == sample.stat().st_size, "synthetic_sample_identity_changed")
    result = subprocess.run([str(contain), "--read-only", "--db", str(sample), "list", "--details", "--json"],
        capture_output=True, timeout=60, check=False)
    require(result.returncode == 0, "synthetic_sample_cli_failed")
    data = json.loads(result.stdout.decode("utf-8"))
    require(data.get("schema_version") == 3 and data.get("kind") == "list_details", "synthetic_sample_cli_contract")
    sessions = data.get("data")
    require(isinstance(sessions, list) and len(sessions) == 3
            and {s.get("id") for s in sessions} == SAMPLE_IDS
            and all(s.get("name", "").startswith("Synthetic ") for s in sessions), "synthetic_sample_sessions_unverified")
    require(sha(sample) == expected[0]["sha256"], "synthetic_sample_read_mutated_database")
    return metadata


def usage(source):
    return f"""# Contain 桌面记录查看器 0.3.1

源码：{source}

1. 解压整个 ZIP，双击 `Contain.Viewer.exe`。不要只复制单个 EXE。
2. 点击“打开记录数据库”，选择自己的已有 Contain 数据库。
3. 在记录列表选择一条记录；查看概览、状态变化和分页事件。
4. 点击事件查看依据，可复制事件 ID；点击导出保存本地 HTML 报告。

无需 Rust、Visual Studio、Python 或另装 .NET。Windows x64 系统组件和随包
.NET 8 / MSVC 运行库用于普通查看；不申请管理员权限，不启动 ETW 或安装器。

`samples/viewer-demo.db` 是明确标记 SYNTHETIC 的演示数据库，含正常、历史名称/
Unknown 和中断模型。它由生产 Storage/RawBuffer 写入，未发生真实 ETW 采集。
选择自己的空数据库时不会添加演示记录。保留原数据库；旧版兼容转换仅在内存副本。

事件筛选作用于当前页；状态变化和源操作是不同证据。High 不是安全删除授权；
Unknown 不是危险判定；记录结束不代表覆盖完整。报告包含本地路径与记录内容，
由你决定保存位置，不会上传。版本、脚本、界面和核心身份见 BUILD.json；
必需检查的实际结果见 VALIDATION.json。构建未签名，不是正式 Release。
"""


def prepare(args):
    repo = args.repository.resolve()
    identity = source_identity(repo)
    revision, core_tree, viewer_tree, source_files = identity
    frameworks = runtime_contract(args.viewer)
    validate_sample(args.contain.resolve(), args.fixtures)
    output = TRIAL.new_directory(args.output)
    for source in sorted(args.viewer.rglob("*")):
        if not source.is_file():
            require(not TRIAL.redirected(source), "redirected_publish_directory")
            continue
        if source.suffix.lower() == ".pdb":
            continue
        name = source.relative_to(args.viewer).as_posix()
        relative_path(name)
        require(source.suffix.lower() in {".dll", ".exe", ".json"}, "unexpected_publish_file")
        target = output / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(safe_file(source), target)
    require(not (output / "contain.exe").exists(), "viewer_publish_contains_core_binary")
    shutil.copyfile(safe_file(args.contain), output / "contain.exe")
    imports = {}
    pending = ["contain.exe", "Contain.Viewer.exe", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"]
    while pending:
        name = pending.pop(0)
        if name.lower() in {n.lower() for n in imports}:
            continue
        destination = output / name
        if not destination.is_file():
            require(TRIAL.RUNTIME_DLL.fullmatch(name) and args.runtime_directory is not None,
                    "desktop_runtime_dependency_missing")
            candidates = [p for p in args.runtime_directory.iterdir() if p.name.lower() == name.lower()]
            require(len(candidates) == 1, "desktop_runtime_dependency_ambiguous")
            shutil.copyfile(safe_file(candidates[0]), destination)
        imports[name] = TRIAL.pe_imports(destination)
        pending.extend(n for n in imports[name]["imports"] + imports[name]["delay_imports"] if not TRIAL.system_dll(n))
    samples = output / "samples"
    samples.mkdir()
    for name in ("viewer-demo.db", "metadata.json"):
        shutil.copyfile(safe_file(args.fixtures / name), samples / name)
    shutil.copyfile(safe_file(repo / "LICENSE"), output / "LICENSE")
    (output / "TRY.md").write_text(usage(revision), encoding="utf-8", newline="\n")
    license_root = args.runtime_packs or Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    runtime_licenses = copy_runtime_licenses(frameworks, output, license_root)
    files = [row(path, path.relative_to(output).as_posix()) for path in sorted(output.rglob("*")) if path.is_file()]
    build = dict(schema_version=1, protocol_revision=PROTOCOL, version="0.3.1", source_sha=revision,
        target="win-x64", viewer_configuration="Release", viewer_framework="net8.0-windows", self_contained=True,
        core_profile="debug", core_target="x86_64-pc-windows-msvc", core_source_tree_sha256=core_tree,
        viewer_source_tree_sha256=viewer_tree, source_files=source_files,
        packager_sha256=sha(Path(__file__)),
        toolchain={p: subprocess.check_output([p, "--version"], text=True, encoding="utf-8").strip()
                   for p in ("dotnet", "rustc", "cargo")},
        build_commands=["cargo build --workspace --locked",
            "dotnet publish desktop/Contain.Viewer/Contain.Viewer.csproj -c Release -r win-x64 --self-contained true -o target/viewer-publish"],
        included_frameworks=frameworks, runtime_licenses=runtime_licenses, pe_dependencies=imports, files=files,
        sample_scope="SYNTHETIC production Storage/RawBuffer; no ETW, installer or private user database",
        build_platform=TRIAL.platform_summary(), validation_at_freeze="not_run")
    require(source_identity(repo) == identity, "source_changed_during_desktop_freeze")
    write(output / "BUILD.json", build)
    print(json.dumps({"phase": "frozen", "source_sha": revision, "files": len(files), "viewer_verified": False}))
    return 0


def verify_package(package):
    build = read(safe_file(package / "BUILD.json"))
    require(build.get("protocol_revision") == PROTOCOL and build.get("version") == "0.3.1"
            and build.get("target") == "win-x64" and build.get("self_contained") is True
            and re.fullmatch(r"[0-9a-f]{40}", build.get("source_sha", "")), "desktop_build_identity_invalid")
    require(build.get("packager_sha256") == sha(Path(__file__)), "desktop_packager_changed_after_freeze")
    names = [entry["path"] for entry in build["files"]]
    require(len(set(name.lower() for name in names)) == len(names), "desktop_manifest_duplicate_path")
    require({"contain.exe", "Contain.Viewer.exe", "Contain.Viewer.dll", "samples/viewer-demo.db",
             "Contain.Viewer.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"} <= set(names),
            "desktop_required_file_missing")
    for entry in build["files"]:
        path = package / relative_path(entry["path"])
        require(row(path, entry["path"]) == entry, "desktop_frozen_file_changed")
    actual = {path.relative_to(package).as_posix() for path in package.rglob("*") if path.is_file()}
    require(actual == set(names) | {"BUILD.json"}, "desktop_unmanifested_file")
    runtime_contract(package)
    return build


def verification_matches(verification, build, manifest_sha):
    if not isinstance(verification, dict):
        return False
    checks = verification.get("checks", [])
    planned = verification.get("planned_checks", [])
    if not isinstance(checks, list) or not isinstance(planned, list) or not all(isinstance(name, str) for name in planned):
        return False
    names = [check.get("name") for check in checks if isinstance(check, dict)]
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    return bool(REQUIRED_CHECKS) and (
        verification.get("schema_version") == 1 and verification.get("protocol_revision") == VERIFICATION
        and verification.get("source_sha") == build["source_sha"] and verification.get("native_windows") is True
        and verification.get("synthetic") is True and verification.get("actual_cli") is True
        and verification.get("real_etw_executed") is False
        and verification.get("report_generated") is True
        and re.fullmatch(r"[0-9a-f]{64}", verification.get("report_sha256", "")) is not None
        and verification.get("completed") is True and verification.get("accepted") is True
        and planned == list(dict.fromkeys(planned))
        and set(planned) == REQUIRED_CHECKS
        and verification.get("package_manifest_sha256") == manifest_sha
        and verification.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and verification.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and verification.get("contain_sha256") == expected["contain.exe"]
        and len(names) == len(set(names)) == len(checks)
        and set(names) == REQUIRED_CHECKS and all(check.get("status") == "passed" for check in checks)
        and type(verification.get("passed")) is int and verification["passed"] == len(REQUIRED_CHECKS)
        and all(type(verification.get(key)) is int and verification[key] == 0 for key in ("failed", "skipped", "not_run")))


def finalize(args):
    package = args.package.resolve()
    build = verify_package(package)
    manifest_sha = sha(package / "BUILD.json")
    quality = read(args.quality_summary) if args.quality_summary.is_file() else {}
    verification = read(args.verification) if args.verification.is_file() else {}
    checks = {
        "frozen_package_unchanged": True,
        "quality_complete": quality.get("source_revision") == build["source_sha"]
            and quality.get("current_required_gates_passed") is True
            and quality.get("all_recorded_invocations_passed") is True
            and quality.get("coverage_floors") == {"lines_percent": 66.5, "functions_percent": 70},
        "native_ui_cli_and_export": verification_matches(verification, build, manifest_sha),
    }
    passed = all(checks.values())
    validation = dict(schema_version=1, protocol_revision=PROTOCOL, source_sha=build["source_sha"],
        build_manifest_sha256=manifest_sha, viewer_verified=passed,
        status="bounded_desktop_viewer_verified" if passed else "diagnostic_unverified", checks=checks,
        quality_summary_sha256=sha(args.quality_summary) if args.quality_summary.is_file() else None,
        verification_summary_sha256=sha(args.verification) if args.verification.is_file() else None,
        native_verification=verification,
        scope="Existing-record desktop viewing only, synthetic production databases and real packaged CLI. No ETW or installer capture.",
        prior_read_only_candidate_milestone="remains_closed_separately", formal_acceptance="not_run")
    write(package / "VALIDATION.json", validation)
    contents = [row(path, path.relative_to(package).as_posix()) for path in sorted(package.rglob("*")) if path.is_file()]
    write(package / "CONTENTS.json", dict(schema_version=1, source_sha=build["source_sha"], files=contents))
    require(not args.output.exists(), "desktop_zip_already_exists")
    with zipfile.ZipFile(args.output, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(package.rglob("*")):
            if path.is_file():
                archive.write(safe_file(path), path.relative_to(package).as_posix())
    print(json.dumps(dict(phase="packaged", source_sha=build["source_sha"], viewer_verified=passed,
                         zip_sha256=sha(args.output), checks=checks)))
    return 0 if passed else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    freeze = sub.add_parser("prepare")
    freeze.add_argument("--repository", type=Path, default=TRIAL.REPO)
    freeze.add_argument("--viewer", type=Path, required=True)
    freeze.add_argument("--contain", type=Path, required=True)
    freeze.add_argument("--fixtures", type=Path, required=True)
    freeze.add_argument("--runtime-directory", type=Path)
    freeze.add_argument("--runtime-packs", type=Path)
    freeze.add_argument("--output", type=Path, required=True)
    finish = sub.add_parser("finalize")
    finish.add_argument("--package", type=Path, required=True)
    finish.add_argument("--quality-summary", type=Path, required=True)
    finish.add_argument("--verification", type=Path, required=True)
    finish.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        return prepare(args) if args.command == "prepare" else finalize(args)
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError) as error:
        # Full Python/OS paths belong in private build logs, not safe summaries.
        print(json.dumps({"status": "failed", "error_type": type(error).__name__,
                          "reason": str(error) if isinstance(error, ValueError) else "desktop_package_input_or_execution_failed"}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
