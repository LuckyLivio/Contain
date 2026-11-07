"""Bundle the unchanged C1 Windows ZIP and retained D6/D7 diagnosis; never build or release."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import subprocess
import zipfile

REPO = "LuckyLivio/Contain"
PRODUCT = "b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4"
HARNESS = "d1bca0b109d722ceaf0ab4ecf89c1d8e17d0b77b"
ORIGINAL_RUN, ORIGINAL_ARTIFACT = 42227112910, 13506927140
DIAGNOSTIC_RUN = 42325883947
DIAGNOSTIC_PUBLIC_ARTIFACT = 13555580235
DIAGNOSTIC_VIEWER_ARTIFACT = 13554943169
TRANSITION_HARNESS = "6cc91f3e5f72cd12efb5bf6660088fea33e00187"
TRANSITION_RUN = 42327363195
TRANSITION_PUBLIC_ARTIFACT, TRANSITION_VIEWER_ARTIFACT = 13555578090, 13556340703
INNER = "contain-0.3.2-beta.10-windows-x64.zip"
INNER_SHA = "a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656"
ARTIFACT_NAME = "contain-beta10-diagnostic-preview-D6-D7"
PUBLIC = ("plan.json", "provenance.json", "gui-execution.json", "original-package-integrity.json", "frozen-desktop-summary.json")
VIEWER = ("packaged-ready-diagnostics.json", "packaged-lookup-diagnostics.json", "install-summary.json", "install-progress.json")
TRANSITION_PUBLIC = PUBLIC[:-1] + ("frozen-transition-summary.json",)
TRANSITION_VIEWER = VIEWER[:2] + ("lookup-transition-plan.json", "lookup-transition-summary.json")


def require(value, message):
    if not value:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", f"repos/{REPO}/{path}"]))


def artifact_bytes(artifact, run, source, name, workflow, conclusion=None):
    require(artifact > 0, "Artifact identity has not been pinned")
    record = api(f"actions/runs/{run}")
    require(record["id"] == run and record["head_sha"] == source and record["path"] == workflow
            and record["head_repository"]["full_name"] == REPO and record["run_attempt"] == 1
            and record["status"] == "completed" and record["conclusion"] in ("success", "failure")
            and (conclusion is None or record["conclusion"] == conclusion), "Run identity or completion differs")
    metadata = api(f"actions/artifacts/{artifact}")
    require(metadata["id"] == artifact and metadata["name"] == name and metadata["expired"] is False
            and metadata["workflow_run"]["id"] == run and metadata["workflow_run"]["head_sha"] == source,
            "Artifact identity differs or has expired")
    return subprocess.check_output(["gh", "api", f"repos/{REPO}/actions/artifacts/{artifact}/zip"])


def selected(archive_bytes, names):
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        require(len(archive.namelist()) == len(set(archive.namelist())) and archive.testzip() is None,
                "Artifact ZIP integrity or duplicate-member failure")
        result = {}
        for name in names:
            matches = [entry for entry in archive.namelist() if entry == name or entry.endswith("/" + name)]
            require(len(matches) == 1, "Required artifact member missing or ambiguous: " + name)
            result[name] = archive.read(matches[0])
        return result


def verified_members(original, public, viewer, transition_public, transition_viewer):
    require(digest(original) == INNER_SHA, "Original Windows ZIP differs; no rebuild or repack is accepted")
    with zipfile.ZipFile(io.BytesIO(original)) as archive:
        require(archive.testzip() is None and len(archive.namelist()) == len(set(archive.namelist())), "Original ZIP integrity failure")
        build = json.loads(archive.read("BUILD.json"))
        contents = json.loads(archive.read("CONTENTS.json"))
        validation = json.loads(archive.read("VALIDATION.json"))
        require(build["source_sha"] == contents["source_sha"] == validation["source_sha"] == PRODUCT
                and validation["status"] == "diagnostic_unverified" and validation["viewer_verified"] is False,
                "Original product identity or retained failure differs")
        require(len(contents["files"]) == 476 and len({row["path"] for row in contents["files"]}) == 476
                and set(archive.namelist()) == {row["path"] for row in contents["files"]} | {"CONTENTS.json"},
                "Original package membership differs")
        for row in contents["files"]:
            data = archive.read(row["path"])
            require(len(data) == row["bytes"] and digest(data) == row["sha256"], "Original package content differs")
    facts = {name: json.loads(data.decode("utf-8-sig")) for name, data in public.items()}
    plan, provenance, result = (facts[name] for name in ("plan.json", "provenance.json", "frozen-desktop-summary.json"))
    require(plan["diagnostic_stage"] == "D6" and plan["harness_source_sha"] == plan["script_source_sha"] == HARNESS
            and plan["product_source_sha"] == PRODUCT and plan["harness_runs"] == 1 and plan["retries"] == 0
            and plan["candidate"] == dict(source_sha=PRODUCT, run_id=ORIGINAL_RUN, artifact_id=ORIGINAL_ARTIFACT, zip_sha256=INNER_SHA),
            "Predeclared D6 source/package/attempt identity differs")
    require(provenance["harness_source_sha"] == provenance["script_source_sha"] == HARNESS
            and provenance["product_source_sha"] == PRODUCT and provenance["original_zip_sha256"] == INNER_SHA,
            "D6 provenance differs")
    require(result["product_source_sha"] == PRODUCT and result["harness_source_sha"] == HARNESS
            and result["original_zip_unchanged"] is True and result["all_extracted_package_members_unchanged"] is True
            and result["original_full_gui_failure_retained"] is True and result["release_accepted"] is False
            and result["complete_native_gates_passed"] is True and len(result["native_checks"]) == 11
            and all(value is True for value in result["native_checks"].values()),
            "D6 package integrity or preserved release boundary differs")
    diagnostics = {name: json.loads(viewer[name].decode("utf-8-sig")) for name in VIEWER[:2]}
    require(diagnostics[VIEWER[0]]["protocol_revision"] == "contain-packaged-startup-diagnostics-1"
            and diagnostics[VIEWER[1]]["protocol_revision"] == "contain-required-lookup-diagnostics-1"
            and all(value["diagnostic_only"] is True for value in diagnostics.values()), "D6 diagnostic protocols differ")
    require(all(json.loads(viewer[name].decode("utf-8-sig"))["source_sha"] == PRODUCT for name in VIEWER[2:]),
            "D6 installation evidence source differs")
    install = json.loads(viewer["install-summary.json"].decode("utf-8-sig"))
    require((install["passed"], install["failed"], install["not_run"]) == (6, 0, 0)
            and install["cold_restart_verified"] is True, "Reviewed D6 installation result differs")
    facts = {name: json.loads(data.decode("utf-8-sig")) for name, data in transition_public.items()}
    plan, provenance, result = (facts[name] for name in ("plan.json", "provenance.json", "frozen-transition-summary.json"))
    require(plan["diagnostic_stage"] == "D7"
            and plan["harness_source_sha"] == plan["script_source_sha"] == TRANSITION_HARNESS
            and plan["product_source_sha"] == PRODUCT and plan["harness_runs"] == 1 and plan["retries"] == 0
            and plan["maximum_native_captures"] == 0 and plan["transition_subset_only"] is True
            and plan["candidate"] == dict(source_sha=PRODUCT, run_id=ORIGINAL_RUN, artifact_id=ORIGINAL_ARTIFACT, zip_sha256=INNER_SHA),
            "Predeclared D7 source/package/attempt identity differs")
    require(provenance["harness_source_sha"] == provenance["script_source_sha"] == TRANSITION_HARNESS
            and provenance["product_source_sha"] == PRODUCT and provenance["original_zip_sha256"] == INNER_SHA,
            "D7 provenance differs")
    require(result["product_source_sha"] == PRODUCT and result["harness_source_sha"] == TRANSITION_HARNESS
            and result["original_zip_unchanged"] is True and result["all_extracted_package_members_unchanged"] is True
            and result["complete_native_gates_passed"] is False and result["real_etw_executed"] is False
            and result["accepted"] is False and result["release_accepted"] is False
            and result["case_counts"] == dict(passed=100, failed=0, not_run=0)
            and result["retained_previous_runs"] == [ORIGINAL_RUN, 42231290678, 42249868473, DIAGNOSTIC_RUN],
            "D7 result, integrity or retained historical boundary differs")
    transition = {name: json.loads(data.decode("utf-8-sig")) for name, data in transition_viewer.items()}
    require(transition[VIEWER[0]]["protocol_revision"] == "contain-packaged-startup-diagnostics-1"
            and transition[VIEWER[1]]["protocol_revision"] == "contain-required-lookup-diagnostics-1"
            and all(transition[name]["diagnostic_only"] is True for name in VIEWER[:2]), "D7 diagnostic protocols differ")
    tplan, summary = (transition[name] for name in TRANSITION_VIEWER[2:])
    require(tplan["product_source_sha"] == summary["source_sha"] == summary["product_source_sha"] == PRODUCT
            and tplan["harness_source_sha"] == summary["harness_source_sha"] == TRANSITION_HARNESS
            and tplan["targets"] == ["synthetic-finished", "synthetic-interrupted"] * 50
            and tplan["transition_count"] == 100 and tplan["maximum_selection_requests"] == 101
            and tplan["transition_loop_deadline_ms"] == 180000 and tplan["transition_deadline_ms"] == 30000
            and tplan["retries"] == summary["retries"] == 0
            and summary["real_etw_executed"] is False and summary["database_unchanged"] is True
            and summary["package_sample_unchanged"] is True and summary["normal_exit"] is True
            and summary["accepted"] is False and summary["release_accepted"] is False
            and (summary["passed"], summary["failed"], summary["not_run"]) == (100, 0, 0)
            and summary["transitions_with_non_target_samples"] == 1 and summary["reproduced_required_miss"] is False,
            "Reviewed D7 fixed transition result or acceptance boundary differs")
    members = {INNER: original}
    members.update({"D6/public/" + name: public[name] for name in PUBLIC})
    members.update({"D6/viewer/" + name: viewer[name] for name in VIEWER})
    members.update({"D7/public/" + name: transition_public[name] for name in TRANSITION_PUBLIC})
    members.update({"D7/viewer/" + name: transition_viewer[name] for name in TRANSITION_VIEWER})
    return members


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--local-input", type=Path, help="Contains original ZIP plus D6/public, D6/viewer, D7/public and D7/viewer")
    args = parser.parse_args()
    if args.local_input:
        original = (args.local_input / INNER).read_bytes()
        public = {name: (args.local_input / "D6/public" / name).read_bytes() for name in PUBLIC}
        viewer = {name: (args.local_input / "D6/viewer" / name).read_bytes() for name in VIEWER}
        transition_public = {name: (args.local_input / "D7/public" / name).read_bytes() for name in TRANSITION_PUBLIC}
        transition_viewer = {name: (args.local_input / "D7/viewer" / name).read_bytes() for name in TRANSITION_VIEWER}
    else:
        original = selected(artifact_bytes(ORIGINAL_ARTIFACT, ORIGINAL_RUN, PRODUCT, "contain-windows-x64-viewer",
            ".github/workflows/ci.yml", "failure"), (INNER,))[INNER]
        public = selected(artifact_bytes(DIAGNOSTIC_PUBLIC_ARTIFACT, DIAGNOSTIC_RUN, HARNESS,
            "frozen-beta10-desktop-evidence", ".github/workflows/frozen-beta10-desktop.yml", "success"), PUBLIC)
        viewer = selected(artifact_bytes(DIAGNOSTIC_VIEWER_ARTIFACT, DIAGNOSTIC_RUN, HARNESS,
            "windows-viewer-evidence", ".github/workflows/frozen-beta10-desktop.yml", "success"), VIEWER)
        transition_public = selected(artifact_bytes(TRANSITION_PUBLIC_ARTIFACT, TRANSITION_RUN, TRANSITION_HARNESS,
            "frozen-beta10-transition-evidence", ".github/workflows/frozen-beta10-desktop.yml", "success"), TRANSITION_PUBLIC)
        transition_viewer = selected(artifact_bytes(TRANSITION_VIEWER_ARTIFACT, TRANSITION_RUN, TRANSITION_HARNESS,
            "windows-lookup-transition-evidence", ".github/workflows/frozen-beta10-desktop.yml", "success"), TRANSITION_VIEWER)
    members = verified_members(original, public, viewer, transition_public, transition_viewer)
    native_result = json.loads(public["frozen-desktop-summary.json"].decode("utf-8-sig"))
    lookup_result = json.loads(viewer["packaged-lookup-diagnostics.json"].decode("utf-8-sig"))
    installation = json.loads(viewer["install-summary.json"].decode("utf-8-sig"))
    transition_result = json.loads(transition_viewer["lookup-transition-summary.json"].decode("utf-8-sig"))
    ready = json.loads(transition_viewer[VIEWER[0]].decode("utf-8-sig"))["launches"][0]
    members["README.txt"] = (f"Contain Beta 10 — D6/D7 诊断证据包，不是正式发布\n\n"
        f"本外层包新增 D6/D7 脱敏证据；内层 {INNER} 是字节未改的原 Windows 预览。\n"
        f"产品源码：{PRODUCT}\n产品 ZIP SHA-256：{INNER_SHA}\n"
        f"D6 测试源码：{HARNESS}\nD6 运行：https://github.com/{REPO}/actions/runs/{DIAGNOSTIC_RUN}\n\n"
        f"D7 测试源码：{TRANSITION_HARNESS}\nD7 运行：https://github.com/{REPO}/actions/runs/{TRANSITION_RUN}\n\n"
        "解开内层 ZIP 后运行 Contain.Viewer.exe。此交付没有重新编译产品，不能视为新增二进制修复。\n"
        f"D6 完整桌面门槛通过：{native_result['complete_native_gates_passed']}；记录到的必需控件查找失败：{len(lookup_result['failures'])}。\n"
        "D6 本次完整套件 80 项通过、0 失败、0 未运行；实际走过安装、保存、源事件解释、退出重开。\n"
        f"实际安装检查：通过 {installation['passed']}，失败 {installation['failed']}，未运行 {installation['not_run']}。\n"
        "本次查找诊断见 D6/viewer/packaged-lookup-diagnostics.json；各项安装检查状态见 install-summary.json。\n"
        "启动阶段见 packaged-ready-diagnostics.json；运行安排及同包身份见 D6/public。\n"
        f"D7 是一个 Viewer、无安装器／ETW 的合成历史记录切换诊断：{transition_result['passed']}/100 次通过，\n"
        f"循环 {transition_result['loop_elapsed_ms']} ms；仅一次轮询观察到尚未显示目标记录，下次轮询正确。\n"
        "没有捕获必需控件缺失；不能排除未采样时段，也没有覆盖安装完成回调与 InstallForm.Hide 的组合路径。\n"
        f"D7 进程启动返回 {ready['process_start_returned_ms']} ms，首次观察到可见窗口 {ready['first_main_window_observed_ms']} ms，\n"
        f"NewInstall 找到 {ready['first_control_found_ms']} ms、启用 {ready['first_control_enabled_ms']} ms，Ready {ready['elapsed_ms']} ms。\n"
        "该窗口可能是错误对话框；控件找到／启用才证明就绪。未发生截止超时或提前退出。\n"
        "D7 使用三条合成记录、不同 runner 实例；本次 9.141 秒启动不能解释空库 C1 超时。\n"
        "全部安排均预先冻结，D6/D7 各执行一次、无重试；没有提高原超时、放宽断言或新增产品行为修复。\n"
        "C1/D5 的历史失败未复现、根因仍未确认；D7 不算完整桌面或真实采集验收，不能据此宣布故障已修复。\n"
        "原包内失败 VALIDATION.json 保留，PR 仍须完成全部正式门槛。没有新标签或 prerelease。\n"
        f"保留 C1 失败：https://github.com/{REPO}/actions/runs/{ORIGINAL_RUN}\n"
        f"保留 D5 失败：https://github.com/{REPO}/actions/runs/42249868473\n"
        f"历史采集对照：https://github.com/{REPO}/actions/runs/42231290678\n\n"
        "Parallels 最短复测：解压内层 ZIP，按原方式跟随 7-Zip 安装；结束保存后分别查看接收队列、\n"
        "跟随候选、交付和原始保存丢失。搜索实际安装的 7z.exe 与 write_requested，复制一条带\n"
        "PID＋创建时间和成功完成依据的源写入事件 ID；直接子进程还须有父实例。退出重开同一会话，\n"
        "核对同一事件解释。其他 Unknown 保留，不要求所有重复请求 High。无需上传数据库、私人路径或完整日志。\n"
        "用户原会话 57,767 次应用丢失仍缺原机分项，根因未确认；受控 Windows 结果不能代替原机证据。\n").encode("utf-8")
    members["BUNDLE-IDENTITY.json"] = (json.dumps(dict(schema_version=1, diagnostic_only=True, release_accepted=False,
        product_source_sha=PRODUCT, unchanged_product_zip_sha256=INNER_SHA, original_run=ORIGINAL_RUN,
        original_artifact=ORIGINAL_ARTIFACT, diagnostic_source_sha=HARNESS, diagnostic_run=DIAGNOSTIC_RUN,
        diagnostic_artifacts=[DIAGNOSTIC_PUBLIC_ARTIFACT, DIAGNOSTIC_VIEWER_ARTIFACT],
        transition_source_sha=TRANSITION_HARNESS, transition_run=TRANSITION_RUN,
        transition_artifacts=[TRANSITION_PUBLIC_ARTIFACT, TRANSITION_VIEWER_ARTIFACT],
        files=[dict(path=name, bytes=len(data), sha256=digest(data)) for name, data in sorted(members.items())]),
        ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    members["SHA256SUMS.txt"] = "".join(digest(data) + "  " + name + "\n" for name, data in sorted(members.items())).encode("utf-8")
    args.output.mkdir(parents=True, exist_ok=False)
    for name, data in sorted(members.items()):
        destination = args.output / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
        require(destination.read_bytes() == data, "Diagnostic artifact readback differs")
    require(digest((args.output / INNER).read_bytes()) == INNER_SHA, "Copied original product ZIP differs")
    print(json.dumps(dict(artifact=ARTIFACT_NAME, files=len(members), unchanged_product_zip_sha256=INNER_SHA,
        diagnostic_run=DIAGNOSTIC_RUN, transition_run=TRANSITION_RUN, diagnostic_only=True, release_accepted=False)))


if __name__ == "__main__":
    main()
