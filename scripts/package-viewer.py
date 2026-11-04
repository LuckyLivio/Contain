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
INSTALL_VERIFICATION = "contain-desktop-install-verification-1"
LAYOUT_VERIFICATION = "contain-desktop-layout-verification-1"
LAYOUT_REQUIRED_CHECKS = frozenset({
    "native_default_layout_resize_maximize", "simulated_150_percent_layout",
    "simulated_200_percent_layout", "unchanged_read_only_fixture_after_layout",
})
INSTALL_REQUIRED_CHECKS = frozenset({
    "install_protocol_and_arguments", "install_configuration_without_launch",
    "gui_real_etw_start_and_duplicate_guard", "gui_runtime_stages_and_close_guard",
    "gui_exact_session_autoopen", "gui_real_source_event_explain_and_export",
})
FIXTURE_PATH = "contain-test-installer.exe"
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


def runtime_contract(viewer, version=None):
    version = version or TRIAL.product_version()
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
    dependencies = read(viewer / "Contain.Viewer.deps.json")
    identity = "Contain.Viewer/" + version
    require(identity in dependencies.get("libraries", {}) and any(
        isinstance(target, dict) and identity in target for target in dependencies.get("targets", {}).values()),
        "viewer_product_version_mismatch")
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


def usage(source, install=False, version=None):
    version = version or TRIAL.product_version()
    recording = """
新建受控安装记录：

1. 点击“新建安装记录”，选择已知受支持的独立 EXE 安装器。
2. 填写记录名称、已存在的观察目录及观察范围之外的数据库位置。
   安装器所在目录不代表软件全部安装位置；程序不会扫描全盘或创建软件目标目录。
3. 点击“检查环境”，阅读当前权限与 ETW 能力；检查成功不能保证实际启动成功。
4. 点击“开始记录”才会启动安装器。等待采集及整理结束，再自动打开本次准确会话。
5. 浏览事件并点击查看依据，导出本地 HTML 报告。

高级参数按界面说明逐项传递，不是 shell 命令。默认 ETW 模式不可用时不会自动提权；
可明确选择快照模式，但其观察范围及覆盖能力不同。记录结束、安装器退出、数据保存
和覆盖质量分别报告；退出码 0 不证明采集成功，当前后端不承诺完整软件足迹。
活动任务期间请保持窗口打开；没有强杀安装器的取消操作。失败后的已保存记录仍可查看。
本轮仅验证受控夹具；MSI、更新器及服务代理安装未获完整支持承诺。

`contain-test-installer.exe` 仅供明确选择的受控验证，不会自动运行。
它要求 TEMP 下名称为 contain-demo-* 的已存在独立目录；普通使用不需要运行测试脚本。

无需终端的受控夹具试用：在资源管理器地址栏输入 `%TEMP%`，手动新建一个唯一的
`contain-demo-<唯一名称>` 文件夹，仅将它用于本夹具。新建记录时选择本包根目录的
`contain-test-installer.exe`，观察目录选择刚创建的现有文件夹，数据库选择观察目录
之外的新位置（例如“文档”中的 `contain-trial-<唯一名称>.db`）。展开高级参数，
每行一个参数：第一行 `--root`，第二行填写该文件夹的绝对路径（不要加引号）。
如需观察夹具的注册表状态差异，可填写 `Software\\Contain\\Demo\\<文件夹名>`。
完成环境检查后点击开始，等待整理结束，选择真实事件查看依据并导出。
以上步骤是使用说明；是否完成原生人工验证以 VALIDATION.json 为准。
""" if install else ""
    return f"""# Contain {version} · Windows 安装记录与证据查看 Beta

源码：{source}

1. 解压整个 ZIP，双击 `Contain.Viewer.exe`。不要只复制单个 EXE。
2. 点击“打开记录数据库”，选择自己的已有 Contain 数据库。
3. 在记录列表选择一条记录；查看概览、状态变化和分页事件。
4. 点击事件查看依据，可复制事件 ID；点击导出保存本地 HTML 报告。

无需 Rust、Visual Studio、Python 或另装 .NET。Windows x64 系统组件和随包
.NET 8 / MSVC 运行库用于运行程序；不自动申请管理员权限。
打开已有记录保留只读保护，不启动 ETW 或安装器。
{recording}

`samples/viewer-demo.db` 是明确标记 SYNTHETIC 的演示数据库，含正常、历史名称/
Unknown 和中断模型。它由生产 Storage/RawBuffer 写入，未发生真实 ETW 采集。
选择自己的空数据库时不会添加演示记录。保留原数据库；旧版兼容转换仅在内存副本。

事件筛选作用于当前页；状态变化和源操作是不同证据。High 不是安全删除授权；
Unknown 不是危险判定；记录结束不代表覆盖完整。报告包含本地路径与记录内容，
由你决定保存位置，不会上传。报告仅包含本条记录的当前事件页、所选事件解释与
状态变化；当前页筛选不会搜索全库。版本、脚本、界面和核心身份见 BUILD.json；
必需检查的实际结果见 VALIDATION.json。构建未签名，属于有限范围的 Beta。
整个 Program Files 目录曾有用户反馈停在整理阶段；缺少原始数据库和错误日志，
根因尚未确认。请保留未完成记录和诊断，不能将已保存证据当作完整结束。
"""


def prepare(args):
    repo = args.repository.resolve()
    identity = source_identity(repo)
    revision, core_tree, viewer_tree, source_files = identity
    version = TRIAL.product_version(repo)
    frameworks = runtime_contract(args.viewer, version)
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
    fixture = getattr(args, "fixture", None)
    if fixture is not None:
        target = output / FIXTURE_PATH
        shutil.copyfile(safe_file(fixture), target)
    imports = {}
    pending = ["contain.exe", "Contain.Viewer.exe", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"]
    if fixture is not None:
        pending.append(FIXTURE_PATH)
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
    subprocess.check_call(["git", "-C", str(repo), "archive", "--format=zip",
        "--output", str(output / "SOURCE.zip"), revision])
    with zipfile.ZipFile(safe_file(output / "SOURCE.zip")) as archive:
        require(archive.testzip() is None and bool(archive.namelist()), "desktop_source_archive_invalid")
        require(all(not name.startswith(".git/") and not name.endswith((".db", ".db-wal", ".db-shm"))
                    for name in archive.namelist()), "desktop_source_archive_private_data")
    (output / "TRY.md").write_text(usage(revision, fixture is not None, version), encoding="utf-8", newline="\n")
    license_root = args.runtime_packs or Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
    runtime_licenses = copy_runtime_licenses(frameworks, output, license_root)
    files = [row(path, path.relative_to(output).as_posix()) for path in sorted(output.rglob("*")) if path.is_file()]
    build = dict(schema_version=1, protocol_revision=PROTOCOL, version=version, source_sha=revision,
        target="win-x64", viewer_configuration="Release", viewer_framework="net8.0-windows", self_contained=True,
        core_profile="debug", core_target="x86_64-pc-windows-msvc", core_source_tree_sha256=core_tree,
        viewer_source_tree_sha256=viewer_tree, source_files=source_files,
        packager_sha256=sha(Path(__file__)),
        toolchain={p: subprocess.check_output([p, "--version"], text=True, encoding="utf-8").strip()
                   for p in ("dotnet", "rustc", "cargo")},
        build_commands=["cargo build --workspace --locked",
            "dotnet publish desktop/Contain.Viewer/Contain.Viewer.csproj -c Release -r win-x64 --self-contained true -o <publish-directory>"],
        build_command_scope="Reproducible configuration templates; <publish-directory> is the chosen output directory. CI uses a per-run temporary directory; machine-specific paths are not published.",
        included_frameworks=frameworks, runtime_licenses=runtime_licenses, pe_dependencies=imports, files=files,
        source_archive=dict(path="SOURCE.zip", source_sha=revision, sha256=sha(output / "SOURCE.zip"),
                            format="git archive zip; exact tracked HEAD snapshot; no repository history"),
        controlled_fixture=(dict(path=FIXTURE_PATH, profile="debug", target="x86_64-pc-windows-msvc",
            sha256=sha(output / FIXTURE_PATH), automatic_execution=False,
            scope="Explicit controlled TEMP fixture only; no third-party installer support claim")
            if fixture is not None else None),
        sample_scope="SYNTHETIC production Storage/RawBuffer; no ETW, installer or private user database",
        build_platform=TRIAL.platform_summary(), signed=False, validation_at_freeze="not_run")
    require(source_identity(repo) == identity, "source_changed_during_desktop_freeze")
    write(output / "BUILD.json", build)
    print(json.dumps({"phase": "frozen", "source_sha": revision, "files": len(files), "viewer_verified": False}))
    return 0


def verify_package(package):
    build = read(safe_file(package / "BUILD.json"))
    require(build.get("protocol_revision") == PROTOCOL and build.get("version") == TRIAL.product_version()
            and build.get("target") == "win-x64" and build.get("self_contained") is True
            and re.fullmatch(r"[0-9a-f]{40}", build.get("source_sha", "")), "desktop_build_identity_invalid")
    require(build.get("packager_sha256") == sha(Path(__file__)), "desktop_packager_changed_after_freeze")
    names = [entry["path"] for entry in build["files"]]
    require(len(set(name.lower() for name in names)) == len(names), "desktop_manifest_duplicate_path")
    require({"contain.exe", "Contain.Viewer.exe", "Contain.Viewer.dll", "samples/viewer-demo.db",
             "Contain.Viewer.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "SOURCE.zip"} <= set(names),
            "desktop_required_file_missing")
    for entry in build["files"]:
        path = package / relative_path(entry["path"])
        require(row(path, entry["path"]) == entry, "desktop_frozen_file_changed")
    actual = {path.relative_to(package).as_posix() for path in package.rglob("*") if path.is_file()}
    require(actual == set(names) | {"BUILD.json"}, "desktop_unmanifested_file")
    runtime_contract(package)
    fixture = build.get("controlled_fixture")
    if fixture is not None:
        require(isinstance(fixture, dict) and fixture.get("path") == FIXTURE_PATH
                and fixture.get("sha256") == sha(safe_file(package / FIXTURE_PATH))
                and fixture.get("automatic_execution") is False, "desktop_fixture_identity_invalid")
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


def install_verification_matches(value, build, manifest_sha):
    """Accept only the declared GUI-started capture, never the synthetic viewer run."""
    if not isinstance(value, dict) or not isinstance(build.get("controlled_fixture"), dict):
        return False
    checks, planned = value.get("checks", []), value.get("planned_checks", [])
    if not isinstance(checks, list) or not isinstance(planned, list) or not all(isinstance(x, str) for x in planned):
        return False
    names = [x.get("name") for x in checks if isinstance(x, dict)]
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    uuid = r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}"
    return (
        value.get("schema_version") == 1 and value.get("protocol_revision") == INSTALL_VERIFICATION
        and value.get("source_sha") == build["source_sha"] and value.get("native_windows") is True
        and value.get("gui_initiated") is True and value.get("real_etw_executed") is True
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and value.get("fixture_sha256") == expected.get(FIXTURE_PATH)
        and planned == list(dict.fromkeys(planned)) and set(planned) == INSTALL_REQUIRED_CHECKS
        and len(names) == len(set(names)) == len(checks) and set(names) == INSTALL_REQUIRED_CHECKS
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("passed")) is int and value["passed"] == len(INSTALL_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run"))
        and value.get("installer_started") is True and type(value.get("installer_exit_code")) is int
        and value.get("collection_finished") is True and value.get("data_saved") is True
        and value.get("capture_state") == "finished" and value.get("quality_level") in {"Degraded", "Incomplete"}
        and value.get("providers_active") is True and type(value.get("real_source_event_count")) is int
        and value["real_source_event_count"] > 0 and value.get("independent_truth_verified") is True
        and re.fullmatch(uuid, value.get("session_id", "")) is not None
        and re.fullmatch(uuid, value.get("event_id", "")) is not None
        and value.get("report_generated") is True
        and re.fullmatch(r"[0-9a-f]{64}", value.get("report_sha256", "")) is not None)


def native_picker_verified(value):
    """System dialog interaction is separate from injected control configuration."""
    rows = value.get("manual_checks", []) if isinstance(value, dict) else []
    if not isinstance(rows, list) or not all(isinstance(row, dict) for row in rows):
        return False
    required = [row for row in rows if row.get("name") == "native_picker_navigation_and_cancel"]
    return len(required) == 1 and required[0].get("status") == "passed"


def native_picker_failed(value):
    rows = value.get("manual_checks", []) if isinstance(value, dict) else []
    return isinstance(rows, list) and any(isinstance(row, dict)
        and row.get("name") == "native_picker_navigation_and_cancel" and row.get("status") == "failed" for row in rows)


def layout_verification_matches(value, build, manifest_sha):
    """Native controls at default DPI and explicit simulated scale stress are distinct."""
    if not isinstance(value, dict):
        return False
    checks, planned, conditions = value.get("checks"), value.get("planned_checks"), value.get("conditions")
    if not (isinstance(checks, list) and all(isinstance(check, dict) for check in checks)
            and isinstance(planned, list) and all(isinstance(name, str) for name in planned)
            and isinstance(conditions, list) and all(isinstance(condition, dict) for condition in conditions)):
        return False
    names = [check.get("name") for check in checks]
    condition_names = [condition.get("name") for condition in conditions]
    expected_conditions = {
        "native_default_layout_resize_maximize": (1, "native_default"),
        "simulated_150_percent_layout": (1.5, "simulated_native_controls"),
        "simulated_200_percent_layout": (2, "simulated_native_controls"),
    }
    if (not all(isinstance(name, str) for name in names + condition_names)
            or len(names) != len(set(names)) or set(names) != LAYOUT_REQUIRED_CHECKS
            or len(planned) != len(set(planned)) or set(planned) != LAYOUT_REQUIRED_CHECKS
            or len(condition_names) != len(set(condition_names)) or set(condition_names) != set(expected_conditions)):
        return False
    for condition in conditions:
        scale, method = expected_conditions[condition["name"]]
        if not (type(condition.get("requested_scale")) in {int, float} and condition["requested_scale"] == scale
                and condition.get("test_method") == method and condition.get("system_high_dpi_verified") is False
                and all(type(condition.get(key)) is int and condition[key] > 0
                        for key in ("device_dpi", "working_area_width", "working_area_height", "assertions"))):
            return False
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    return (
        value.get("schema_version") == 1 and value.get("protocol_revision") == LAYOUT_VERIFICATION
        and value.get("source_sha") == build["source_sha"] and value.get("native_windows") is True
        and value.get("synthetic") is True and value.get("real_etw_executed") is False
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("passed")) is int and value["passed"] == len(LAYOUT_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run")))


def finalize(args):
    package = args.package.resolve()
    build = verify_package(package)
    manifest_sha = sha(package / "BUILD.json")
    quality = read(args.quality_summary) if args.quality_summary.is_file() else {}
    verification = read(args.verification) if args.verification.is_file() else {}
    install_path = getattr(args, "install_verification", None)
    installation = read(install_path) if install_path and install_path.is_file() else {}
    layout_path = getattr(args, "layout_verification", None)
    layout = read(layout_path) if layout_path and layout_path.is_file() else {}
    install_requested = build.get("controlled_fixture") is not None or install_path is not None
    checks = {
        "frozen_package_unchanged": True,
        "quality_complete": quality.get("source_revision") == build["source_sha"]
            and quality.get("current_required_gates_passed") is True
            and quality.get("all_recorded_invocations_passed") is True
            and quality.get("coverage_floors") == {"lines_percent": 66.5, "functions_percent": 70},
        "native_ui_cli_and_export": verification_matches(verification, build, manifest_sha),
    }
    if install_requested:
        checks["native_gui_install_etw_and_exact_result"] = install_verification_matches(installation, build, manifest_sha)
    if layout_path is not None:
        checks["native_layout_and_simulated_scale"] = layout_verification_matches(layout, build, manifest_sha)
    passed = all(checks.values())
    milestone_checks = dict(automated_product_and_quality=passed,
                           native_picker_navigation_and_cancel=native_picker_verified(installation)) if install_requested else None
    milestone_closed = all(milestone_checks.values()) if milestone_checks else None
    interaction_failed = install_requested and native_picker_failed(installation)
    status = "diagnostic_unverified"
    if passed:
        status = ("bounded_graphical_install_verified" if milestone_closed else "graphical_install_automated_verified_manual_pending") if install_requested else "bounded_desktop_viewer_verified"
    if interaction_failed:
        status = "diagnostic_native_picker_failed"
    validation = dict(schema_version=1, protocol_revision=PROTOCOL, source_sha=build["source_sha"],
        build_manifest_sha256=manifest_sha, viewer_verified=passed,
        graphical_install_automated_verified=passed if install_requested else None,
        graphical_install_verified=milestone_closed, graphical_install_milestone_closed=milestone_closed,
        milestone_checks=milestone_checks, status=status, checks=checks,
        quality_summary_sha256=sha(args.quality_summary) if args.quality_summary.is_file() else None,
        verification_summary_sha256=sha(args.verification) if args.verification.is_file() else None,
        native_verification=verification,
        native_install_verification=installation,
        install_verification_summary_sha256=sha(install_path) if install_path and install_path.is_file() else None,
        native_layout_verification=layout,
        layout_verification_summary_sha256=sha(layout_path) if layout_path and layout_path.is_file() else None,
        system_high_dpi_verified=False if layout_path is not None else None,
        scope=("One GUI-initiated controlled real-ETW fixture using the frozen desktop, CLI and runtime; no third-party installer or scale trial. Manual dialog navigation remains separately reported."
               if install_requested else "Existing-record desktop viewing only, synthetic production databases and real packaged CLI. No ETW or installer capture."),
        prior_read_only_candidate_milestone="remains_closed_separately", formal_acceptance="not_run")
    write(package / "VALIDATION.json", validation)
    contents = [row(path, path.relative_to(package).as_posix()) for path in sorted(package.rglob("*")) if path.is_file()]
    write(package / "CONTENTS.json", dict(schema_version=1, source_sha=build["source_sha"], files=contents))
    require(not args.output.exists(), "desktop_zip_already_exists")
    with zipfile.ZipFile(args.output, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(package.rglob("*")):
            if path.is_file():
                archive.write(safe_file(path), path.relative_to(package).as_posix())
    with zipfile.ZipFile(args.output) as archive:
        require(archive.testzip() is None, "desktop_zip_readback_failed")
        actual = {path.relative_to(package).as_posix(): path for path in package.rglob("*") if path.is_file()}
        require(set(archive.namelist()) == set(actual), "desktop_zip_files_disagree")
        require(all(hashlib.sha256(archive.read(name)).hexdigest() == sha(path) for name, path in actual.items()),
                "desktop_zip_bytes_disagree")
    print(json.dumps(dict(phase="packaged", source_sha=build["source_sha"], viewer_verified=passed,
                         graphical_install_automated_verified=passed if install_requested else None,
                         graphical_install_milestone_closed=milestone_closed, status=status,
                         zip_sha256=sha(args.output), checks=checks)))
    return 0 if passed and not interaction_failed else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    freeze = sub.add_parser("prepare")
    freeze.add_argument("--repository", type=Path, default=TRIAL.REPO)
    freeze.add_argument("--viewer", type=Path, required=True)
    freeze.add_argument("--contain", type=Path, required=True)
    freeze.add_argument("--fixtures", type=Path, required=True)
    freeze.add_argument("--fixture", type=Path, help="Optional safe controlled installer; copied but never run by packaging")
    freeze.add_argument("--runtime-directory", type=Path)
    freeze.add_argument("--runtime-packs", type=Path)
    freeze.add_argument("--output", type=Path, required=True)
    finish = sub.add_parser("finalize")
    finish.add_argument("--package", type=Path, required=True)
    finish.add_argument("--quality-summary", type=Path, required=True)
    finish.add_argument("--verification", type=Path, required=True)
    finish.add_argument("--install-verification", type=Path)
    finish.add_argument("--layout-verification", type=Path,
        help="Require default native layout and explicitly simulated 150/200%% scale checks")
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
