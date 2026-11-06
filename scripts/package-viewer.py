"""Freeze the actual Windows viewer and CLI, then package their observed validation.

No capture, installer, migration or remote upload runs here. Only the explicitly
synthetic production-store sample may be copied; user databases stay local.
"""
import argparse
import hashlib
from html.parser import HTMLParser
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import zipfile
import zlib
import xml.etree.ElementTree as ET

SPEC = importlib.util.spec_from_file_location("viewer_trial_helpers", Path(__file__).with_name("package-trial.py"))
TRIAL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRIAL)
require, read, write, sha = TRIAL.require, TRIAL.read, TRIAL.write, TRIAL.sha
safe_file, relative_path = TRIAL.safe_file, TRIAL.relative_path
PROTOCOL = "contain-desktop-package-1"
VERIFICATION = "contain-desktop-verification-1"
INSTALL_VERIFICATION = "contain-desktop-install-verification-1"
LAYOUT_VERIFICATION = "contain-desktop-layout-verification-1"
PROGRESS_VERIFICATION = "contain-desktop-progress-verification-1"
PERSONAL_RECORDS_VERIFICATION = "contain-desktop-personal-records-verification-1"
FIRST_USE_CONFIGURATION_VERIFICATION = "contain-desktop-first-use-configuration-verification-1"
FIRST_USE_CONFIGURATION_REQUIRED_CHECKS = frozenset({
    "picker_cancel_and_existing_selection", "missing_and_invalidated_observation_directory",
    "personal_library_conflict_and_correction", "advanced_options_retained_when_collapsed",
    "synthetic_permission_refusal_explicit_retry_and_single_start",
})
FIRST_USE_SCREENSHOTS = frozenset({"install-first-use-configured.png", "install-first-use-missing-folder.png"})
FOLLOW_SCREENSHOTS = frozenset({"install-follow-configured.png"})
SEARCH_EXPORT_VERIFICATION = "contain-desktop-search-export-verification-1"
SEARCH_EXPORT_REQUIRED_CHECKS = frozenset({
    "frozen_export_package_identity", "actual_packaged_search_export_all_ids",
    "exported_page_selection_preserved", "ordinary_page_report_after_clear",
    "no_matches_cancel_and_failure_state", "safe_targets_and_cancelled_write",
    "literal_text_html_escaping", "unchanged_readonly_databases",
})
SEARCH_VERIFICATION = "contain-desktop-session-search-verification-1"
SEARCH_REQUIRED_CHECKS = frozenset({
    "frozen_search_package_identity", "full_session_search_fixed_oracle",
    "second_page_copy_explain_identity", "literal_text_operation_confidence",
    "empty_clear_failure_and_export_boundary", "superseded_queries_and_same_name_records",
    "default_library_and_legacy_readonly", "packaged_exe_cross_page_search",
    "search_databases_unchanged",
})
OVERVIEW_VERIFICATION = "contain-desktop-overview-verification-1"
OVERVIEW_REQUIRED_CHECKS = frozenset({
    "frozen_overview_package_identity", "persisted_three_outcomes",
    "missing_statistics_distinct_from_zero", "same_name_uuid_switch_and_details_navigation",
    "external_legacy_and_default_library", "query_failure_clears_previous_summary",
    "packaged_overview_three_outcomes", "packaged_overview_cold_restart_exact_session_event",
    "overview_expand_resize_actions_and_long_text", "overview_databases_unchanged",
})
PERSONAL_RECORDS_REQUIRED_CHECKS = frozenset({
    "guarded_test_root_and_missing_library", "default_path_and_name_suggestion",
    "manual_name_and_advanced_custom_location", "external_database_read_only_new_record_default",
    "scope_conflict_before_library_creation", "future_corrupt_unwritable_records_preserved",
    "packaged_executable_default_history", "packaged_executable_cold_restart_exact_session_event",
})
PROGRESS_REQUIRED_CHECKS = frozenset({
    "additive_measurements_and_missing_values", "repeated_finalizing_and_stale_identity",
    "slow_finalizing_real_pipes_ui_timer_and_close_guard", "structured_failure_details_and_clipboard",
    "missing_terminal_result", "nonzero_collector_exit", "absent_progress_measurement",
    "disposed_form_callback_guard",
})
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
    "ui_search_empty_and_clear_restores_first_page", "ui_local_report_matches_current_record_page_and_event",
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


def runtime_patch(repository=TRIAL.REPO):
    patch = ET.parse(repository / "desktop/Directory.Build.props").findtext("./PropertyGroup/RuntimeFrameworkVersion")
    require(isinstance(patch, str) and re.fullmatch(r"8\.0\.[0-9]+", patch), "runtime_patch_not_pinned")
    return patch


def runtime_contract(viewer, version=None, patch=None):
    version = version or TRIAL.product_version()
    patch = patch or runtime_patch()
    config = read(safe_file(viewer / "Contain.Viewer.runtimeconfig.json"))
    options = config.get("runtimeOptions", {})
    frameworks = options.get("includedFrameworks", [])
    require(options.get("tfm") == "net8.0" and "framework" not in options and "frameworks" not in options,
            "viewer_must_be_self_contained_net8")
    require(isinstance(frameworks, list) and len(frameworks) == 2 and
            {f.get("name") for f in frameworks} == {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}
            and all(f.get("version") == patch for f in frameworks),
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
2. 默认选择“跟随安装器记录”，无需提前选择安装目录。记录由本次 EXE 及身份可验证的直接子进程发出的文件操作。
   名称默认来自 EXE 文件名，记录保存到“我的记录”，无需选择数据库。
3. 点击“开始记录”才会启动安装器。等待记录及整理结束，再自动打开本次准确会话。
4. 浏览事件并点击查看依据，导出本地 HTML 报告。关闭后重新打开程序，“我的记录”会显示已保存的历史记录。

普通流程只需选择 EXE，再明确点击“开始记录”。取消 EXE 选择器会回到原界面。
跟随模式没有安装前基线，不提供文件前后状态对比；记录的历史名称不一定是当前路径，执行进程关联不代表最终归属。
需要明确范围和前后状态对比时，改选“指定文件夹观察”，再选择“要记录哪个文件夹里的变化？”。
通常选软件准备安装到的已有文件夹：安装包在“下载”里、软件准备装到 D 盘，就选 D 盘上对应的已有文件夹。这里的选择只决定 Contain 观察哪里。
名称、自定义保存位置、参数、注册表、等待时间、快照模式和可选“检查环境”在默认折叠的高级设置中。
修改高级选项后，主操作旁会显示实际生效设置。检查环境不是开始前的额外步骤。
安装器所在目录不代表软件安装目标；程序不会替你选择范围、扫描全盘或创建软件目标目录。

如默认保存位置不可写，或手动目录模式的保存位置与观察范围冲突，使用“更改保存位置”；高级参数中可明确启用
“使用自定义保存位置”，选择观察范围之外的数据库。打开外部数据库不会改变新记录的默认保存位置。

高级参数按界面说明逐项传递，不是 shell 命令。默认 ETW 模式不可用时不会自动提权；
需要快照模式时，应先明确选择“指定文件夹观察”并选择已有文件夹；不会将跟随模式静默切换为快照。记录结束、安装器退出、数据保存
和覆盖质量分别报告；退出码 0 不证明采集成功，当前后端不承诺完整软件足迹。
活动任务期间请保持窗口打开；没有强杀安装器的取消操作。失败后的已保存记录仍可查看。
本轮仅验证受控夹具；MSI、更新器及服务代理安装未获完整支持承诺。

`contain-test-installer.exe` 仅供明确选择的受控验证，不会自动运行。
它要求 TEMP 下名称为 contain-demo-* 的已存在独立目录；普通使用不需要运行测试脚本。

无需终端的受控夹具试用：在资源管理器地址栏输入 `%TEMP%`，手动新建一个唯一的
`contain-demo-<唯一名称>` 文件夹，仅将它用于本夹具。新建记录时选择本包根目录的
`contain-test-installer.exe`，改选“指定文件夹观察”，观察目录选择刚创建的现有文件夹，保持“保存到我的记录”。展开高级参数，
每行一个参数：第一行 `--root`，第二行填写该文件夹的绝对路径（不要加引号）。
如需观察夹具的注册表状态差异，可填写 `Software\\Contain\\Demo\\<文件夹名>`。
点击开始，等待整理结束，选择真实事件查看依据并导出。“检查环境”仅在需要诊断时主动使用。
以上步骤是使用说明；是否完成原生人工验证以 VALIDATION.json 为准。
""" if install else ""
    return f"""# Contain {version} · Windows 安装记录与证据查看 Beta

源码：{source}

1. 解压整个 ZIP，双击 `Contain.Viewer.exe`。不要只复制单个 EXE。
2. 默认打开“我的记录”：没有历史时显示空状态，不会创建数据库或填入演示记录。
3. 在记录列表选择一条记录；概览分别显示安装器结果、记录状态、观察范围与覆盖、主要变化。
   “查看变化”和“查看事件与依据”打开当前会话；展开“技术详情”查看来源、统计、缺口与进程证据。
   重启后仍从数据库读取同一会话；未结束的历史记录不代表采集进程当前仍在运行。
4. 在“事件与依据”的“搜索本次记录”输入文件名、完整路径、操作或置信标签，
   搜索当前数据库中本次会话的全部事件。每页最多 50 条，显示匹配总数与当前范围；翻页保留条件。
5. 点击事件查看同一原始事件的依据，可复制其 ID。搜索成功后点击“导出全部 N 条匹配结果”，
   选择新的 .html 文件；无论当前页码或是否选中事件，报告都包含全部匹配事件。
   导出后保留搜索条件、当前页和所选事件。清除搜索后仍可“导出本页报告”。

无需 Rust、Visual Studio、Python 或另装 .NET。Windows x64 系统组件和随包
.NET 8 / MSVC 运行库用于运行程序；不自动申请管理员权限。
打开已有记录保留只读保护，不启动 ETW 或安装器。
个人记录使用 Windows 当前用户的数据目录，保存在 `%LOCALAPPDATA%\\Contain\\Records\\contain.db`；
不随解压目录或工作目录改变。只有明确点击“开始记录”才会创建缺失的个人库目录。
“打开记录数据库”仍可只读查看外部记录；“我的记录”返回个人历史，不迁移外部数据库。
{recording}

`samples/viewer-demo.db` 是明确标记 SYNTHETIC 的演示数据库，含正常、历史名称/
Unknown 和中断模型。它由生产 Storage/RawBuffer 写入，未发生真实 ETW 采集。
选择自己的空数据库时不会添加演示记录。保留原数据库；旧版兼容转换仅在内存副本。

事件搜索按普通文本包含匹配，ASCII 英文字母不区分大小写，其他字符精确匹配；
中文、空格、% 和 _ 按字面处理。搜索仅限当前会话，不混入同名记录。
状态变化和源操作是不同证据。High 不是安全删除授权；Unknown 不是危险判定；
记录结束不代表覆盖完整。报告包含本地路径与记录内容，由你决定保存位置，不会上传。
普通报告仅包含本条记录的当前未筛选事件页、所选事件解释与状态变化。
搜索报告只包含本次会话的全部匹配事件及必要上下文，顺序与搜索一致。
一次导出使用同一只读数据视图；发现匹配内容已变化会提示刷新后重试。
查询中、查询失败或无匹配时不能导出旧结果。取消保存不改变当前视图；已有文件不覆盖，
写入失败或取消后不会留下半份报告。超过输出限额会明确失败，不会截断后声称全部导出成功。
版本、脚本、界面和核心身份见 BUILD.json；
必需检查的实际结果见 VALIDATION.json。构建未签名，属于有限范围的 Beta。
整个 Program Files 目录曾有用户反馈停在整理阶段；缺少原始数据库和错误日志，
根因尚未确认。请保留未完成记录和诊断，不能将已保存证据当作完整结束。
"""


def prepare(args):
    repo = args.repository.resolve()
    identity = source_identity(repo)
    revision, core_tree, viewer_tree, source_files = identity
    version = TRIAL.product_version(repo)
    frameworks = runtime_contract(args.viewer, version, runtime_patch(repo))
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
            "dotnet publish desktop/Contain.Viewer/Contain.Viewer.csproj -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=" + frameworks[0]["version"] + " -o <publish-directory>"],
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
        and value.get("actual_packaged_executable_capture") is True
        and value.get("default_database_used_without_path_interaction") is True
        and value.get("cold_restart_verified") is True and value.get("working_directory_changed") is True
        and all(type(value.get(key)) is int and value[key] > 0
                for key in ("first_process_id", "restarted_process_id"))
        and value["first_process_id"] != value["restarted_process_id"]
        and type(value.get("first_process_exit_code")) is int and value["first_process_exit_code"] == 0
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
        and isinstance(value.get("session_id"), str) and re.fullmatch(uuid, value["session_id"]) is not None
        and isinstance(value.get("event_id"), str) and re.fullmatch(uuid, value["event_id"]) is not None
        and value.get("restart_session_id") == value["session_id"]
        and value.get("restart_event_id") == value["event_id"]
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


def progress_verification_matches(value, build, manifest_sha):
    """Synthetic process/WinForms checks are separate from real ETW evidence."""
    if not isinstance(value, dict):
        return False
    checks, planned = value.get("checks", []), value.get("planned_checks", [])
    if not isinstance(checks, list) or not isinstance(planned, list) or not all(isinstance(x, str) for x in planned):
        return False
    names = [x.get("name") for x in checks if isinstance(x, dict)]
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    return (
        value.get("schema_version") == 1 and value.get("protocol_revision") == PROGRESS_VERIFICATION
        and value.get("source_sha") == build["source_sha"] and value.get("native_windows") is True
        and value.get("synthetic") is True and value.get("real_etw_executed") is False
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and len(planned) == len(set(planned)) and set(planned) == PROGRESS_REQUIRED_CHECKS
        and len(names) == len(set(names)) == len(checks) and set(names) == PROGRESS_REQUIRED_CHECKS
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("passed")) is int and value["passed"] == len(PROGRESS_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run")))


def personal_records_verification_matches(value, build, manifest_sha):
    """Require the actual packaged EXE's isolated read-only cold-restart evidence."""
    if not isinstance(value, dict):
        return False
    checks, planned = value.get("checks"), value.get("planned_checks")
    if not (isinstance(checks, list) and all(isinstance(check, dict) for check in checks)
            and isinstance(planned, list) and all(isinstance(name, str) for name in planned)):
        return False
    names = [check.get("name") for check in checks]
    if (not all(isinstance(name, str) for name in names)
            or len(names) != len(set(names)) or set(names) != PERSONAL_RECORDS_REQUIRED_CHECKS
            or len(planned) != len(set(planned)) or set(planned) != PERSONAL_RECORDS_REQUIRED_CHECKS):
        return False
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    session, event = value.get("initial_session_id"), value.get("initial_event_id")
    return (
        type(value.get("schema_version")) is int and value["schema_version"] == 1
        and value.get("protocol_revision") == PERSONAL_RECORDS_VERIFICATION
        and value.get("source_sha") == build["source_sha"]
        and value.get("native_windows") is True and value.get("synthetic") is True
        and value.get("real_etw_executed") is False
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("actual_packaged_executable") is True and value.get("cold_restart_verified") is True
        and value.get("working_directory_changed") is True
        and value.get("real_user_records_accessed") is False
        and type(value.get("retries")) is int and value["retries"] == 0
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and isinstance(session, str) and bool(session.strip())
        and isinstance(event, str) and bool(event.strip())
        and value.get("restart_session_id") == session and value.get("restart_event_id") == event
        and all(type(value.get(key)) is int and value[key] > 0
                for key in ("first_process_id", "restarted_process_id"))
        and value["first_process_id"] != value["restarted_process_id"]
        and type(value.get("first_process_exit_code")) is int and value["first_process_exit_code"] == 0
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("passed")) is int and value["passed"] == len(PERSONAL_RECORDS_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run")))

def overview_verification_matches(value, build, manifest_sha):
    """Require the actual packaged EXE's persisted summary and exact-navigation evidence."""
    if not isinstance(value, dict):
        return False
    checks, planned = value.get("checks"), value.get("planned_checks")
    if not (isinstance(checks, list) and all(isinstance(check, dict) for check in checks)
            and isinstance(planned, list) and all(isinstance(name, str) for name in planned)):
        return False
    names = [check.get("name") for check in checks]
    if (not all(isinstance(name, str) for name in names)
            or len(names) != len(set(names)) or set(names) != OVERVIEW_REQUIRED_CHECKS
            or len(planned) != len(set(planned)) or set(planned) != OVERVIEW_REQUIRED_CHECKS):
        return False
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    session, event = value.get("initial_session_id"), value.get("initial_event_id")
    return (
        type(value.get("schema_version")) is int and value["schema_version"] == 1
        and value.get("protocol_revision") == OVERVIEW_VERIFICATION
        and value.get("source_sha") == build["source_sha"]
        and value.get("native_windows") is True and value.get("synthetic") is True
        and value.get("real_etw_executed") is False
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("actual_packaged_executable") is True and value.get("cold_restart_verified") is True
        and value.get("working_directory_changed") is True
        and value.get("real_user_records_accessed") is False
        and type(value.get("retries")) is int and value["retries"] == 0
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and isinstance(session, str) and bool(session.strip())
        and isinstance(event, str) and bool(event.strip())
        and value.get("restart_session_id") == session and value.get("restart_event_id") == event
        and all(type(value.get(key)) is int and value[key] > 0
                for key in ("first_process_id", "restarted_process_id"))
        and value["first_process_id"] != value["restarted_process_id"]
        and type(value.get("first_process_exit_code")) is int and value["first_process_exit_code"] == 0
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("passed")) is int and value["passed"] == len(OVERVIEW_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run")))


def search_verification_matches(value, build, manifest_sha):
    """Require the frozen executable's complete-session search and identity evidence."""
    if not isinstance(value, dict):
        return False
    checks, planned = value.get("checks"), value.get("planned_checks")
    if not (isinstance(checks, list) and all(isinstance(check, dict) for check in checks)
            and isinstance(planned, list) and all(isinstance(name, str) for name in planned)):
        return False
    names = [check.get("name") for check in checks]
    if (not all(isinstance(name, str) for name in names)
            or len(names) != len(set(names)) or set(names) != SEARCH_REQUIRED_CHECKS
            or len(planned) != len(set(planned)) or set(planned) != SEARCH_REQUIRED_CHECKS):
        return False
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    return (
        type(value.get("schema_version")) is int and value["schema_version"] == 1
        and value.get("protocol_revision") == SEARCH_VERIFICATION
        and value.get("source_sha") == build["source_sha"]
        and value.get("native_windows") is True and value.get("synthetic") is True
        and value.get("real_etw_executed") is False
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("actual_packaged_executable") is True
        and value.get("real_user_records_accessed") is False
        and type(value.get("actual_device_dpi")) is int and value["actual_device_dpi"] > 0
        and isinstance(value.get("windows_version"), str) and bool(value["windows_version"].strip())
        and type(value.get("retries")) is int and value["retries"] == 0
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and all(type(value.get(key)) is int and value[key] == expected_count
                for key, expected_count in (("total_matches", 53), ("first_page_count", 50), ("second_page_count", 3)))
        and value.get("expected_ids") == [f"synthetic-search-event-{index:03d}" for index in range(70, 123)]
        and value.get("first_page_ids") == value["expected_ids"][:50]
        and value.get("second_page_ids") == value["expected_ids"][50:]
        and all(value.get(key) == "synthetic-search-event-120"
                for key in ("selected_event_id", "explain_event_id", "copied_event_id"))
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("passed")) is int and value["passed"] == len(SEARCH_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run")))


class SearchReportIds(HTMLParser):
    """Read actual report rows, independently of native summary/count assertions."""
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.ids = []
        self.matched_tables = 0
        self.in_matches = False
        self.valid = True
        self.row_id = None
        self.cells = 0
        self.first_cell = None
        self.reading_first_cell = False

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag in {"script", "iframe", "object", "embed", "link", "img"}:
            self.valid = False
        if any(name.startswith("on") for name in attrs):
            self.valid = False
        if tag == "table":
            if self.in_matches:
                self.valid = False
            self.in_matches = attrs.get("id") == "matched-events"
            self.matched_tables += int(self.in_matches)
        if "data-event-id" in attrs:
            if tag != "tr" or not self.in_matches:
                self.valid = False
            self.ids.append(attrs["data-event-id"])
            if self.row_id is not None:
                self.valid = False
            self.row_id = attrs["data-event-id"]
            self.cells = 0
            self.first_cell = ""
        if tag == "td" and self.row_id is not None:
            self.cells += 1
            self.reading_first_cell = self.cells == 1

    def handle_data(self, data):
        if self.reading_first_cell:
            self.first_cell += data

    def handle_endtag(self, tag):
        if tag == "td":
            self.reading_first_cell = False
        if tag == "tr" and self.row_id is not None:
            if self.cells < 5 or self.first_cell != self.row_id:
                self.valid = False
            self.row_id = None
        if tag == "table":
            self.in_matches = False


def search_export_report_matches(value, summary_path):
    if not isinstance(value, dict) or summary_path is None:
        return False
    report = value.get("report")
    if not isinstance(report, dict) or report.get("path") != "reports/search-export-synthetic.html":
        return False
    if type(report.get("bytes")) is not int or not 0 < report["bytes"] <= 64 * 1024 * 1024:
        return False
    if not isinstance(report.get("sha256"), str) or not re.fullmatch(r"[0-9a-f]{64}", report["sha256"]):
        return False
    try:
        path = safe_file(summary_path.parent / "reports" / "search-export-synthetic.html")
        if path.stat().st_size != report["bytes"] or sha(path) != report["sha256"]:
            return False
        parser = SearchReportIds()
        parser.feed(path.read_text(encoding="utf-8"))
        parser.close()
        return (parser.valid and parser.matched_tables == 1 and not parser.in_matches and parser.row_id is None
                and parser.ids == [f"synthetic-search-event-{index:03d}" for index in range(70, 123)])
    except (OSError, ValueError, UnicodeError):
        return False


def search_export_verification_matches(value, build, manifest_sha, summary_path):
    if not isinstance(value, dict):
        return False
    checks, planned = value.get("checks"), value.get("planned_checks")
    if not (isinstance(checks, list) and all(isinstance(check, dict) for check in checks)
            and isinstance(planned, list) and all(isinstance(name, str) for name in planned)):
        return False
    names = [check.get("name") for check in checks]
    if (not all(isinstance(name, str) for name in names)
            or len(names) != len(set(names)) or set(names) != SEARCH_EXPORT_REQUIRED_CHECKS
            or len(planned) != len(set(planned)) or set(planned) != SEARCH_EXPORT_REQUIRED_CHECKS):
        return False
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    ids = [f"synthetic-search-event-{index:03d}" for index in range(70, 123)]
    return (
        type(value.get("schema_version")) is int and value["schema_version"] == 1
        and value.get("protocol_revision") == SEARCH_EXPORT_VERIFICATION
        and value.get("source_sha") == build["source_sha"]
        and value.get("native_windows") is True and value.get("synthetic") is True
        and value.get("real_etw_executed") is False and value.get("real_user_records_accessed") is False
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("actual_packaged_executable") is True and value.get("native_save_dialog") is True
        and type(value.get("actual_device_dpi")) is int and value["actual_device_dpi"] > 0
        and isinstance(value.get("windows_version"), str) and bool(value["windows_version"].strip())
        and type(value.get("retries")) is int and value["retries"] == 0
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and type(value.get("event_count")) is int and value["event_count"] == 53
        and value.get("report_event_ids") == ids
        and all(type(value.get(key)) is int and value[key] == 50 for key in ("page_before_offset", "page_after_offset"))
        and value.get("page_after_ids") == ids[50:]
        and all(value.get(key) == "synthetic-search-event-120"
                for key in ("selected_event_id", "copied_event_id", "explain_event_id"))
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("passed")) is int and value["passed"] == len(SEARCH_EXPORT_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run"))
        and search_export_report_matches(value, summary_path))


def first_use_configuration_verification_matches(value, build, manifest_sha):
    """Configuration/failure regressions use real controls, with explicit synthetic failures."""
    if not isinstance(value, dict):
        return False
    checks, planned = value.get("checks"), value.get("planned_checks")
    if not (isinstance(checks, list) and all(isinstance(check, dict) for check in checks)
            and isinstance(planned, list) and all(isinstance(name, str) for name in planned)):
        return False
    names = [check.get("name") for check in checks]
    if (not all(isinstance(name, str) for name in names)
            or len(names) != len(set(names)) or set(names) != FIRST_USE_CONFIGURATION_REQUIRED_CHECKS
            or len(planned) != len(set(planned)) or set(planned) != FIRST_USE_CONFIGURATION_REQUIRED_CHECKS):
        return False
    expected = {entry["path"]: entry["sha256"] for entry in build["files"]}
    return (
        type(value.get("schema_version")) is int and value["schema_version"] == 1
        and value.get("protocol_revision") == FIRST_USE_CONFIGURATION_VERIFICATION
        and value.get("source_sha") == build["source_sha"]
        and value.get("native_windows") is True and value.get("synthetic") is True
        and value.get("real_etw_executed") is False and value.get("real_user_records_accessed") is False
        and value.get("completed") is True and value.get("accepted") is True
        and value.get("package_manifest_sha256") == manifest_sha
        and value.get("viewer_assembly_sha256") == expected["Contain.Viewer.dll"]
        and value.get("viewer_launcher_sha256") == expected["Contain.Viewer.exe"]
        and value.get("contain_sha256") == expected["contain.exe"]
        and all(check.get("status") == "passed" for check in checks)
        and type(value.get("assertions")) is int and value["assertions"] > 0
        and type(value.get("passed")) is int and value["passed"] == len(FIRST_USE_CONFIGURATION_REQUIRED_CHECKS)
        and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "skipped", "not_run", "retries")))


def first_use_screenshots_match(value, summary_path):
    return native_png_screenshots_match(value, summary_path, FIRST_USE_SCREENSHOTS)


def native_png_screenshots_match(value, summary_path, required_names):
    """Read the exact native PNG bytes; metadata alone cannot supply the release examples."""
    if not isinstance(value, dict) or summary_path is None:
        return False
    screenshots = value.get("screenshots")
    if (not isinstance(screenshots, list) or len(screenshots) != len(required_names)
            or not all(isinstance(item, dict) and isinstance(item.get("name"), str) for item in screenshots)
            or {item["name"] for item in screenshots} != required_names):
        return False
    try:
        digests = set()
        for item in screenshots:
            if (not isinstance(item.get("sha256"), str) or not re.fullmatch(r"[0-9a-f]{64}", item["sha256"])
                    or any(type(item.get(key)) is not int or not 0 < item[key] <= 16384 for key in ("width", "height"))):
                return False
            path = safe_file(summary_path.parent / "screenshots" / item["name"])
            if not 0 < path.stat().st_size <= 16 * 1024 * 1024 or sha(path) != item["sha256"]:
                return False
            data = path.read_bytes()
            if (data[:8] != b"\x89PNG\r\n\x1a\n" or data[8:16] != b"\x00\x00\x00\rIHDR"
                    or len(data) < 33 or struct.unpack(">II", data[16:24]) != (item["width"], item["height"])):
                return False
            offset, has_image, ended = 8, False, False
            while offset + 12 <= len(data):
                size = struct.unpack(">I", data[offset:offset + 4])[0]
                end = offset + size + 12
                if end > len(data):
                    return False
                kind = data[offset + 4:offset + 8]
                if zlib.crc32(data[offset + 4:end - 4]) != struct.unpack(">I", data[end - 4:end])[0]:
                    return False
                has_image |= kind == b"IDAT"
                offset = end
                if kind == b"IEND":
                    ended = size == 0 and offset == len(data)
                    break
            if not has_image or not ended:
                return False
            digests.add(item["sha256"])
        return len(digests) == len(required_names)
    except (OSError, ValueError, struct.error):
        return False


def first_use_install_verification_matches(value, build, manifest_sha, summary_path):
    """One ordinary two-input flow must be the existing, exact GUI-started capture."""
    if not install_verification_matches(value, build, manifest_sha) or not native_picker_verified(value):
        return False
    first = value.get("first_use")
    if not isinstance(first, dict):
        return False
    if first.get("protocol_revision") == "contain-follow-install-flow-1":
        return follow_first_use_matches(value, summary_path)
    positive = ("actual_packaged_executable", "cancel_preserved_record_selection", "missing_scope_blocks_start",
        "configuration_created_no_session", "keyboard_flow_verified", "default_name_matches_exe", "default_database",
        "scope_exact_match", "main_entry_kept_active_task", "duplicate_start_created_one_session",
        "invalid_scope_blocked", "database_conflict_blocked", "corrections_preserved_configuration")
    return (
        first.get("protocol_revision") == "contain-first-install-flow-1"
        and all(first.get(key) is True for key in positive)
        and isinstance(first.get("windows_version"), str) and bool(first["windows_version"].strip())
        and type(first.get("actual_device_dpi")) is int and first["actual_device_dpi"] > 0
        and all(type(first.get(key)) is int and first[key] == 1 for key in
            ("installer_picker_cancels", "installer_picker_selections", "saved_scope_count"))
        and type(first.get("observation_picker_selections")) is int and first["observation_picker_selections"] >= 1
        and type(first.get("ordinary_input_count")) is int and first["ordinary_input_count"] == 2
        and all(type(first.get(key)) is int and first[key] == 0 for key in
            ("advanced_interactions", "environment_check_interactions", "database_path_interactions"))
        and isinstance(first.get("selected_scope_sha256"), str)
        and re.fullmatch(r"[0-9a-f]{64}", first["selected_scope_sha256"]) is not None
        and first.get("persisted_scope_sha256") == first["selected_scope_sha256"]
        and first.get("session_id") == value["session_id"]
        and first_use_screenshots_match(first, summary_path))


def follow_first_use_matches(value, summary_path):
    """The single-input path is a distinct protocol; legacy manual checks stay intact."""
    first = value.get("first_use", {})
    positive = ("actual_packaged_executable", "cancel_preserved_record_selection", "configuration_created_no_session",
        "keyboard_flow_verified", "default_name_matches_exe", "default_database", "default_follow", "no_watch_argument",
        "follow_mode_persisted", "main_entry_kept_active_task", "duplicate_start_created_one_session")
    return (first.get("protocol_revision") == "contain-follow-install-flow-1"
        and all(first.get(key) is True for key in positive)
        and isinstance(first.get("windows_version"), str) and bool(first["windows_version"].strip())
        and type(first.get("actual_device_dpi")) is int and first["actual_device_dpi"] > 0
        and all(type(first.get(key)) is int and first[key] == 1 for key in
            ("installer_picker_cancels", "installer_picker_selections", "ordinary_input_count"))
        and all(type(first.get(key)) is int and first[key] == 0 for key in
            ("saved_scope_count", "observation_picker_selections", "advanced_interactions", "environment_check_interactions", "database_path_interactions"))
        and first.get("session_id") == value.get("session_id")
        and native_png_screenshots_match(first, summary_path, FOLLOW_SCREENSHOTS)
        and follow_evidence_matches(value.get("follow_verification"), value.get("source_sha"), value.get("session_id"))
        and value.get("event_id") == value["follow_verification"].get("selected_event_id"))


def follow_evidence_matches(value, source, session):
    """Require observed actor proofs and UI identity, not just a successful count."""
    if not isinstance(value, dict):
        return False
    spec = importlib.util.spec_from_file_location("follow_oracle_plan", Path(__file__).with_name("verify-follow-installer-experiment.py"))
    oracle = importlib.util.module_from_spec(spec); spec.loader.exec_module(oracle)
    checks, observations = value.get("checks"), value.get("observations")
    if not (value.get("protocol") == "contain-follow-installer-experiment-1" and value.get("source_sha") == source
            and value.get("session_id") == session and value.get("accepted") is True
            and value.get("synthetic") is False and value.get("controlled_fixture") is True
            and type(value.get("maximum_native_captures")) is int and value["maximum_native_captures"] == 1
            and type(value.get("retries")) is int and value["retries"] == 0
            and all(value.get(key) is False for key in ("ownership_claim", "current_path_claim", "installation_before_baseline", "post_install_file_reads"))
            and isinstance(checks, list) and len(checks) == len(oracle.PLAN) and all(isinstance(row, dict) for row in checks)
            and [row.get("name") for row in checks] == oracle.PLAN and all(row.get("status") == "passed" for row in checks)
            and type(value.get("passed")) is int and value["passed"] == len(oracle.PLAN)
            and all(type(value.get(key)) is int and value[key] == 0 for key in ("failed", "not_run"))
            and value.get("unresolved") == [] and isinstance(observations, list) and len(observations) == 3
            and all(isinstance(row, dict) for row in observations) and [row.get("label") for row in observations] == ["parent", "child", "unrelated"]):
        return False
    uuid = r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}"
    parent, child, other = observations
    for row, rule in ((parent, "InstallerPid"), (child, "DescendantProcess")):
        if not (isinstance(row.get("event_id"), str) and re.fullmatch(uuid, row["event_id"])
                and row.get("operation") == "write_requested" and row.get("source") == "EtwFile"
                and row.get("rule") == rule and row.get("confidence") == row.get("actor_confidence") == "High"
                and row.get("completion") is True and row.get("state_validated") is False
                and row.get("current_path_verified") is False and row.get("historical_name_source") == "file_object_cache"
                and type(row.get("pid")) is int and row["pid"] > 0
                and isinstance(row.get("creation_time"), str) and row["creation_time"].isdecimal() and int(row["creation_time"]) > 0
                and isinstance(row.get("timestamp_ticks"), str) and row["timestamp_ticks"].isdecimal()
                and int(row["timestamp_ticks"]) >= int(row["creation_time"]) and isinstance(row.get("recorded_name_sha256"), str)
                and re.fullmatch(r"[0-9a-f]{64}", row["recorded_name_sha256"])):
            return False
    if parent["event_id"] == child["event_id"] or (parent["pid"], parent["creation_time"]) == (child["pid"], child["creation_time"]):
        return False
    if other.get("classification") == "unobserved":
        if not (other.get("observed_event_count") == 0 and other.get("contextual_event_count") == 0
                and other.get("event_id") is None and other.get("event_ids") == []
                and other.get("outcome") == "not_retained_by_follow_scope; no_claim_no_system_event"):
            return False
    elif other.get("classification") != "Unknown" or other.get("outcome") != "retained_without_installer_attribution":
        return False
    counters, storage = value.get("follow_counters", {}), value.get("storage_counters", {})
    fixed = {"direct_children_only": True, "baseline_available": False, "candidate_record_limit": 2048,
        "candidate_byte_limit": 8388608, "resource_limit": 4096, "candidate_dropped": 0}
    if not all(type(counters.get(key)) is type(expected) and counters[key] == expected for key, expected in fixed.items()):
        return False
    if not (type(counters.get("candidate_high_water_records")) is int and 0 <= counters["candidate_high_water_records"] <= 2048
            and type(counters.get("candidate_high_water_bytes")) is int and 0 <= counters["candidate_high_water_bytes"] <= 8388608
            and type(storage.get("quota_bytes")) is int and 0 < storage["quota_bytes"] <= 67108864
            and type(storage.get("committed_bytes")) is int and 0 < storage["committed_bytes"] <= storage["quota_bytes"]
            and type(storage.get("persisted")) is int and storage["persisted"] > 0
            and all(type(storage.get(key)) is int and storage[key] == 0 for key in ("failed", "quota_dropped"))):
        return False
    return (all(value.get(key) == child["event_id"] for key in
            ("selected_event_id", "copied_event_id", "explain_event_id", "restart_event_id"))
        and all(value.get(key) == session for key in ("returned_session_id", "autoopened_session_id", "restart_session_id")))


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
    progress_path = getattr(args, "progress_verification", None)
    progress = read(progress_path) if progress_path and progress_path.is_file() else {}
    personal_path = getattr(args, "personal_records_verification", None)
    personal = read(personal_path) if personal_path and personal_path.is_file() else {}
    overview_path = getattr(args, "overview_verification", None)
    overview = read(overview_path) if overview_path and overview_path.is_file() else {}
    search_path = getattr(args, "search_verification", None)
    search = read(search_path) if search_path and search_path.is_file() else {}
    export_path = getattr(args, "search_export_verification", None)
    exported = read(export_path) if export_path and export_path.is_file() else {}
    configuration_path = getattr(args, "first_use_configuration_verification", None)
    configuration = read(configuration_path) if configuration_path and configuration_path.is_file() else {}
    install_requested = build.get("controlled_fixture") is not None or install_path is not None
    checks = {
        "frozen_package_unchanged": True,
        "quality_complete": quality.get("source_revision") == build["source_sha"]
            and quality.get("current_required_gates_passed") is True
            and quality.get("all_recorded_invocations_passed") is True
            and quality.get("coverage_floors") == {"lines_percent": 66.5, "functions_percent": 70},
        "native_ui_cli_and_export": verification_matches(verification, build, manifest_sha),
        # Beta 3 cannot be called verified without its personal-library/cold-
        # restart evidence, even when a caller omits the diagnostic-friendly flag.
        "native_personal_records_and_cold_restart": personal_records_verification_matches(personal, build, manifest_sha),
        # Beta 4's result page is mandatory even if the optional diagnostic flag
        # is omitted. A passed older UI suite cannot stand in for these checks.
        "native_persisted_overview_and_navigation": overview_verification_matches(overview, build, manifest_sha),
        # A successful older viewer suite cannot substitute for Beta 5 search.
        "native_session_event_search": search_verification_matches(search, build, manifest_sha),
        # Beta 6 requires actual EXE export plus independently parsed report bytes.
        "native_search_export": search_export_verification_matches(exported, build, manifest_sha, export_path),
        # Beta 7's offline controls/errors checks run before any bounded install.
        "native_first_use_configuration": first_use_configuration_verification_matches(configuration, build, manifest_sha),
    }
    if install_requested:
        checks["native_gui_install_etw_and_exact_result"] = install_verification_matches(installation, build, manifest_sha)
        checks["native_first_use_install_flow"] = first_use_install_verification_matches(installation, build, manifest_sha, install_path)
    if layout_path is not None:
        checks["native_layout_and_simulated_scale"] = layout_verification_matches(layout, build, manifest_sha)
    if progress_path is not None:
        checks["native_progress_and_failure_feedback"] = progress_verification_matches(progress, build, manifest_sha)
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
        native_progress_verification=progress,
        native_personal_records_verification=personal,
        native_overview_verification=overview,
        native_search_verification=search,
        native_search_export_verification=exported,
        native_first_use_configuration_verification=configuration,
        first_use_configuration_summary_sha256=sha(configuration_path) if configuration_path and configuration_path.is_file() else None,
        search_export_verification_summary_sha256=sha(export_path) if export_path and export_path.is_file() else None,
        search_verification_summary_sha256=sha(search_path) if search_path and search_path.is_file() else None,
        overview_verification_summary_sha256=sha(overview_path) if overview_path and overview_path.is_file() else None,
        personal_records_verification_summary_sha256=sha(personal_path) if personal_path and personal_path.is_file() else None,
        progress_verification_summary_sha256=sha(progress_path) if progress_path and progress_path.is_file() else None,
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
    finish.add_argument("--progress-verification", type=Path,
        help="Require native synthetic process/progress/failure checks; these do not establish real ETW")
    finish.add_argument("--personal-records-verification", type=Path,
        help="Require the isolated personal-library and actual packaged EXE cold-restart checks")
    finish.add_argument("--overview-verification", type=Path,
        help="Require persisted overview outcomes, exact current-session navigation and cold restart")
    finish.add_argument("--search-verification", type=Path,
        help="Require current-session cross-page search and original event identity in the actual packaged EXE")
    finish.add_argument("--search-export-verification", type=Path,
        help="Require actual packaged EXE full-search export, fixed ordered HTML IDs and retained current page")
    finish.add_argument("--first-use-configuration-verification", type=Path,
        help="Require native two-input configuration, cancellation, correctable errors and explicit advanced choices")
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
