"""Freeze Beta.2 WPF and its newly built core before native verification.

Reuses the reviewed archive/runtime contracts, never the historical core identity
or acceptance. Only the labelled synthetic sample database enters the ZIP.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

REPO = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("modern_archive_contracts", REPO / "scripts/package-modern-desktop.py")
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)
VERSION = "0.4.0-beta.2"
BASE.VERSION = VERSION
PROTOCOL = "contain-beta2-desktop-package-1"


def require(value, message):
    BASE.require(value, message)


def core_contract(build, manifest):
    source = manifest["source_sha"]
    require(build.get("protocol") == PROTOCOL and build.get("version") == VERSION,
            "Beta.2 package identity differs")
    expected = dict(schema_version=1, source_sha=source, viewer_configuration="Release", target="win-x64",
                    framework="net10.0-windows", ui_framework="WPF + WPF UI", sdk=BASE.SDK, runtime=BASE.RUNTIME,
                    self_contained=True, trimming=False, aot=False, core_version=VERSION,
                    core_source_sha=source, core_profile="debug", reused_core_binary=False,
                    dependencies=BASE.DEPENDENCIES, native_validation=BASE.NATIVE_VALIDATION)
    require(all(type(build.get(key)) is type(value) and build.get(key) == value for key, value in expected.items()),
            "new core or desktop build identity differs")
    require(re.fullmatch(r"[0-9a-f]{64}", build.get("core_sha256", "")) is not None,
            "core binary identity missing")
    rows = build.get("files", [])
    require(len(rows) == len({row["path"] for row in rows}) and {row["path"]: row for row in rows}
            == {row["path"]: row for row in manifest["files"] if row["path"] != "BUILD.json"},
            "build file identities differ from complete manifest")


def verify(archive_path, extracted=None):
    with zipfile.ZipFile(archive_path) as archive:
        manifest = BASE.verify_manifest(archive)
        build = json.loads(archive.read("BUILD.json"))
        core_contract(build, manifest)
        require(hashlib.sha256(archive.read("contain.exe")).hexdigest() == build["core_sha256"],
                "core bytes differ from frozen build identity")
        names = archive.namelist()
        def prefix(name):
            with archive.open(name) as stream:
                return stream.read(16)
        BASE.member_policy(names, prefix)
        BASE.runtime_data_contract(json.loads(archive.read("Contain.Viewer.runtimeconfig.json")),
                                   json.loads(archive.read("Contain.Viewer.deps.json")), names)
        if extracted:
            files = BASE.tree_files(extracted)
            require(set(files) == set(names), "extracted membership differs")
            for name in names:
                require(BASE.sha(files[name]) == hashlib.sha256(archive.read(name)).hexdigest(),
                        "frozen extracted product changed")
    return build


def prepare(args):
    outputs = [args.package, args.zip, args.fixture]
    require(all(not path.exists() and not path.is_symlink() for path in outputs),
            "refuse replacing frozen output")
    resolved = [path.resolve() for path in outputs]
    inputs = [path.resolve() for path in [args.viewer, args.core, args.fixture_source, args.fixtures, args.nuget, args.runtime_directory]]
    require(all(left != right and left not in right.parents and right not in left.parents
                for i, left in enumerate(resolved) for right in resolved[i + 1:])
            and all(output != source and output not in source.parents and source not in output.parents
                    for output in resolved for source in inputs), "freeze inputs and outputs overlap")
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip()
    require(source == args.source and re.fullmatch(r"[0-9a-f]{40}", source), "source identity differs")
    require(not subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=REPO),
            "tracked source is dirty")
    require(subprocess.check_output(["dotnet", "--version"], cwd=REPO, text=True).strip() == BASE.SDK, "SDK differs")
    require(subprocess.check_output([str(args.core.resolve()), "--version"], text=True).strip()
            == "contain " + VERSION, "core strict version differs")
    for path in [args.core, args.fixture_source, args.runtime_directory / "vcruntime140.dll"]:
        require(path.is_file() and not path.is_symlink(), "native input must be a regular file")
    frameworks = BASE.runtime_contract(args.viewer)
    files = BASE.tree_files(args.viewer)
    def prefix(name):
        with files[name].open("rb") as stream:
            return stream.read(16)
    BASE.member_policy(files, prefix, published=True)
    shutil.copytree(args.viewer, args.package)
    for path, name in [(args.core, "contain.exe"), (args.runtime_directory / "vcruntime140.dll", "vcruntime140.dll")]:
        require(not (args.package / name).exists(), "core member already present")
        shutil.copyfile(path, args.package / name)
    args.fixture.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(args.fixture_source, args.fixture)
    spec = importlib.util.spec_from_file_location("sample_runtime_contracts", REPO / "scripts/package-viewer.py")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    helper.validate_sample(args.package / "contain.exe", args.fixtures)
    samples = args.package / "samples"
    samples.mkdir()
    shutil.copyfile(args.fixtures / "viewer-demo.db", samples / "viewer-demo.db")
    (samples / "README.txt").write_text("合成演示数据，不是真实安装记录。通过“打开外部库”只读查看。\n", encoding="utf-8")
    (args.package / "licenses").mkdir()
    for name in BASE.LICENSE_FILES:
        path = REPO / "licenses" / name
        require(path.is_file() and not path.is_symlink(), "redistribution license is not a regular file")
        shutil.copyfile(path, args.package / "licenses" / name)
    licenses = helper.copy_runtime_licenses(frameworks, args.package, args.nuget)
    for name in ["LICENSE", "THIRD-PARTY-NOTICES.md"]:
        shutil.copyfile(REPO / name, args.package / name)
    (args.package / "START-HERE.txt").write_text(
        "Contain 0.4.0-beta.2 · Windows x64\n\n"
        "完整解压，然后运行 Contain.Viewer.exe。无需另装 .NET 或开发工具。\n"
        "新建记录 → 选择 EXE → 开始记录。默认跟随安装器，保存到我的记录。\n"
        "在安装器自己的窗口选择目标目录；记录期间保持 Contain 打开。\n"
        "完成后查看本次准确会话，在“事件与依据”中搜索、解释或导出。\n"
        "个人库沿用 LocalApplicationData/Contain/Records/contain.db；外部库仅只读。\n"
        "UI 与核心均为 0.4.0-beta.2。WPF UI 是第三方 MIT 组件。\n"
        "退出、保存、覆盖质量分别表达；High 不证明文件所有权或可安全删除。\n"
        "采集受当前令牌、支持来源及负载限制；不承诺完整足迹、卸载或回滚。\n"
        "此包先冻结再验证，构建本身不证明验收通过。请核对独立验收结果与剩余限制。\n", encoding="utf-8")
    files = [BASE.identity(p, args.package) for p in sorted(args.package.rglob("*")) if p.is_file()]
    build = dict(schema_version=1, protocol=PROTOCOL, version=VERSION, source_sha=source,
                 viewer_configuration="Release", target="win-x64", framework="net10.0-windows", ui_framework="WPF + WPF UI",
                 sdk=BASE.SDK, runtime=BASE.RUNTIME, self_contained=True, trimming=False, aot=False,
                 core_version=VERSION, core_source_sha=source, core_sha256=BASE.sha(args.core), core_profile="debug",
                 reused_core_binary=False, dependencies=BASE.DEPENDENCIES, runtime_licenses=licenses,
                 native_validation=BASE.NATIVE_VALIDATION, files=files)
    BASE.write(args.package / "BUILD.json", build)
    BASE.write(args.package / "CONTENTS.json", dict(schema_version=1, source_sha=source,
               files=[BASE.identity(p, args.package) for p in sorted(args.package.rglob("*")) if p.is_file()]))
    args.zip.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.zip, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(args.package.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(args.package).as_posix())
    verify(args.zip, args.package)
    print(json.dumps(dict(zip=args.zip.name, sha256=BASE.sha(args.zip), source_sha=source)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    freeze = sub.add_parser("prepare")
    for name in ["viewer", "core", "fixture-source", "fixtures", "package", "zip", "fixture", "nuget", "runtime-directory"]:
        freeze.add_argument("--" + name, type=Path, required=True)
    freeze.add_argument("--source", required=True)
    check = sub.add_parser("verify")
    check.add_argument("--zip", type=Path, required=True)
    check.add_argument("--package", type=Path)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args)
    else:
        print(json.dumps(verify(args.zip, args.package), ensure_ascii=False))


if __name__ == "__main__":
    main()
