"""Freeze a new WPF desktop with the byte-identical accepted Beta.10 core.

The ZIP is created BEFORE native verification. Results remain separate assets;
neither a successful build nor a previous WinForms result is desktop acceptance.
No database is collected here. Only the labelled production-store fixture ships.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import zipfile

VERSION = "0.4.0-beta.1"
CORE_SOURCE = "b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4"
CORE_SHA = "a45f193a84fb3d5556e0258941d9f55bddded677098959c6b1a07b1e724e66f6"
BASELINE_SHA = "a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656"
RUNTIME = "10.0.14"
SDK = "10.0.403"
REPO = Path(__file__).resolve().parents[1]
DEPENDENCIES = {"WPF-UI": "4.3.0", "WPF-UI.Abstractions": "4.3.0", "CommunityToolkit.Mvvm": "8.4.2"}
NATIVE_VALIDATION = "separate FINAL-ACCEPTANCE.json; not implied by build"
RUNTIME_FILES = ["Contain.Viewer.exe", "Contain.Viewer.dll", "Contain.Desktop.Model.dll", "Wpf.Ui.dll",
                 "Wpf.Ui.Abstractions.dll", "CommunityToolkit.Mvvm.dll", "PresentationFramework.dll",
                 "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "createdump.exe"]
LICENSE_FILES = ["WPF-UI-LICENSE.txt", "WPF-UI.Abstractions-LICENSE.txt", "CommunityToolkit.Mvvm-LICENSE.txt",
                 "FluentSystemIcons-LICENSE.txt", "Microsoft.NETCore.App-LICENSE.txt",
                 "Microsoft.NETCore.App-THIRD-PARTY-NOTICES.txt", "Microsoft.WindowsDesktop.App-LICENSE.txt",
                 "Microsoft.WindowsDesktop.App-THIRD-PARTY-NOTICES.txt"]


def require(value, message):
    if not value:
        raise ValueError(message)


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write(path, data):
    Path(path).write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def identity(path, root):
    return dict(path=path.relative_to(root).as_posix(), bytes=path.stat().st_size, sha256=sha(path))


def safe_names(names):
    # The package is extracted on Windows even when inspected on Linux.
    require(len(names) == len({name.casefold() for name in names}), "duplicate or Windows-aliased archive entries")
    reserved = {"CON", "PRN", "AUX", "NUL"} | {f"{prefix}{i}" for prefix in ["COM", "LPT"] for i in range(1, 10)}
    for name in names:
        path = PurePosixPath(name)
        require(name and not path.is_absolute() and ".." not in path.parts
                and path.as_posix() == name and "\\" not in name and ":" not in name
                and all(part and part == part.rstrip(" .") and part.split(".")[0].upper() not in reserved
                        and not any(ord(char) < 32 or char in '<>"|?*' for char in part)
                        for part in name.split("/")), "unsafe archive path")


def archive_members(archive):
    names = archive.namelist()
    safe_names(names)
    require(all((entry.external_attr >> 16) & 0o170000 != 0o120000 for entry in archive.infolist()),
            "archive symlink is not a regular package member")
    require(archive.testzip() is None, "archive CRC failure")
    return names


def verify_manifest(archive):
    names = archive_members(archive)
    manifest = json.loads(archive.read("CONTENTS.json"))
    require(manifest.get("schema_version") == 1 and re.fullmatch(r"[0-9a-f]{40}", manifest.get("source_sha", "")),
            "manifest source identity invalid")
    rows = manifest["files"]
    require(len(rows) == len({row["path"] for row in rows}), "duplicate manifest entries")
    require(set(names) == {row["path"] for row in rows} | {"CONTENTS.json"}, "manifest membership differs")
    for row in rows:
        require(type(row["bytes"]) is int and row["bytes"] >= 0
                and re.fullmatch(r"[0-9a-f]{64}", row["sha256"]), "manifest file identity invalid")
        data = archive.read(row["path"])
        require(len(data) == row["bytes"] and hashlib.sha256(data).hexdigest() == row["sha256"], "manifest member differs")
    return manifest


def runtime_data_contract(config, dependencies, names):
    opts = config["runtimeOptions"]
    frameworks = opts.get("includedFrameworks", [])
    require(opts.get("tfm") == "net10.0" and "framework" not in opts and "frameworks" not in opts,
            "desktop must be self-contained net10")
    require(len(frameworks) == 2 and {f["name"] for f in frameworks}
            == {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}
            and all(f["version"] == RUNTIME for f in frameworks), "runtime identity differs")
    target = ".NETCoreApp,Version=v10.0/win-x64"
    require(dependencies.get("runtimeTarget", {}).get("name") == target, "desktop runtime target differs")
    libraries = dependencies["libraries"]
    target_libraries = dependencies.get("targets", {}).get(target, {})
    expected = {"Contain.Viewer": VERSION, "Contain.Desktop.Model": VERSION, **DEPENDENCIES}
    for name, version in expected.items():
        key = name + "/" + version
        require({item for item in libraries if item.split("/")[0] == name} == {key}
                and key in target_libraries, "desktop dependency missing or version differs: " + key)
    # .NET 10's Windows runtime pack ships this helper. It is not another
    # Contain entry point; require its actual runtime-pack declaration.
    runtime_pack = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/" + RUNTIME
    require("createdump.exe" in target_libraries.get(runtime_pack, {}).get("native", {}),
            "runtime createdump helper declaration missing")
    for filename in RUNTIME_FILES:
        require(filename in names, "runtime member missing: " + filename)
    return frameworks


def tree_files(directory):
    require(directory.is_dir() and not directory.is_symlink()
            and not getattr(directory, "is_junction", lambda: False)(), "package directory must be ordinary")
    entries = list(directory.rglob("*"))
    require(all(not path.is_symlink() and not getattr(path, "is_junction", lambda: False)()
                and (path.is_file() or path.is_dir()) for path in entries), "package tree contains a link or special file")
    files = {path.relative_to(directory).as_posix(): path for path in entries if path.is_file()}
    safe_names(list(files))
    return files


def member_policy(names, prefix, published=False):
    json_names = {"Contain.Viewer.runtimeconfig.json", "Contain.Viewer.deps.json"}
    generated = {"BUILD.json", "CONTENTS.json", "LICENSE", "THIRD-PARTY-NOTICES.md", "START-HERE.txt",
                 "samples/viewer-demo.db", "samples/README.txt", "contain.exe"}
    license_names = {("licenses/" + name).casefold() for name in LICENSE_FILES}
    license_names |= {("licenses/" + framework + "-thirdpartynotices.txt").casefold()
                      for framework in ["Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"]}
    for name in names:
        path = PurePosixPath(name)
        allowed = path.suffix.lower() in {".dll", ".pdb"} or name in json_names or name in {"Contain.Viewer.exe", "createdump.exe"}
        if not published:
            allowed = allowed or name in generated or name.casefold() in license_names
        require(allowed, "unreviewed package member: " + name)
        require(name == "samples/viewer-demo.db" or prefix(name) != b"SQLite format 3\x00",
                "database outside labelled synthetic sample")


def runtime_contract(directory):
    files = tree_files(directory)
    config = json.loads((directory / "Contain.Viewer.runtimeconfig.json").read_text(encoding="utf-8"))
    dependencies = json.loads((directory / "Contain.Viewer.deps.json").read_text(encoding="utf-8"))
    return runtime_data_contract(config, dependencies, files)


def prepare(args):
    require(all(not path.exists() and not path.is_symlink() for path in [args.package, args.zip, args.fixture]),
            "refuse replacing any frozen candidate or fixture")
    outputs = [path.resolve() for path in [args.package, args.zip, args.fixture]]
    inputs = [path.resolve() for path in [args.viewer, args.baseline, args.fixtures, args.nuget]]
    require(all(left != right and left not in right.parents and right not in left.parents
                for i, left in enumerate(outputs) for right in outputs[i + 1:])
            and all(output != source and output not in source.parents and source not in output.parents
                    for output in outputs for source in inputs), "freeze inputs and outputs must not overlap")
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip()
    require(source == args.source and len(source) == 40, "source identity differs")
    require(not subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=no"], cwd=REPO), "tracked source is dirty")
    require(subprocess.check_output(["dotnet", "--version"], cwd=REPO, text=True).strip() == SDK, "SDK differs")
    require(sha(args.baseline) == BASELINE_SHA, "Beta.10 baseline bytes differ")
    frameworks = runtime_contract(args.viewer)
    files = tree_files(args.viewer)
    def prefix(name):
        with files[name].open("rb") as stream:
            return stream.read(16)
    member_policy(files, prefix, published=True)
    with zipfile.ZipFile(args.baseline) as baseline:
        old_manifest = verify_manifest(baseline)
        require(old_manifest["source_sha"] == CORE_SOURCE, "core provenance differs")
        require(hashlib.sha256(baseline.read("contain.exe")).hexdigest() == CORE_SHA, "core binary differs")
        shutil.copytree(args.viewer, args.package)
        for name in ["contain.exe", "vcruntime140.dll"]:
            require(not (args.package / name).exists(), "unexpected core member already present")
            (args.package / name).write_bytes(baseline.read(name))
        # Only the standalone fixture is extracted outside the user package.
        args.fixture.parent.mkdir(parents=True, exist_ok=True)
        args.fixture.write_bytes(baseline.read("contain-test-installer.exe"))
        baseline_build = json.loads(baseline.read("BUILD.json"))
    require(subprocess.check_output([str(args.package / "contain.exe"), "--version"], text=True).strip()
            == "contain 0.3.2-beta.10", "core strict version differs")
    # Reuse the prior packaging helpers' sample and runtime license verification;
    # no old UI acceptance predicate is imported as evidence for the new desktop.
    spec = importlib.util.spec_from_file_location("legacy_package_helpers", REPO / "scripts/package-viewer.py")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    helper.validate_sample(args.package / "contain.exe", args.fixtures)
    samples = args.package / "samples"
    samples.mkdir()
    shutil.copyfile(args.fixtures / "viewer-demo.db", samples / "viewer-demo.db")
    (samples / "README.txt").write_text("合成演示数据，不是真实安装记录。通过“打开外部库”只读查看。\n", encoding="utf-8")
    (args.package / "licenses").mkdir()
    # Explicit redistribution texts only; unrelated local files must never be
    # swept into a public ZIP by a recursive repository-directory copy.
    for name in LICENSE_FILES:
        path = REPO / "licenses" / name
        require(path.is_file() and not path.is_symlink(), "redistribution license must be a regular file")
        shutil.copyfile(path, args.package / "licenses" / name)
    licenses = helper.copy_runtime_licenses(frameworks, args.package, args.nuget)
    for name in ["LICENSE", "THIRD-PARTY-NOTICES.md"]:
        shutil.copyfile(REPO / name, args.package / name)
    (args.package / "START-HERE.txt").write_text(
        "Contain 0.4.0-beta.1 · Windows x64\n\n"
        "完整解压，然后运行 Contain.Viewer.exe。无需另装 .NET 或开发工具。\n"
        "新建记录 → 选择 EXE → 开始记录。默认跟随安装器，保存到我的记录。\n"
        "选择文件本身不会启动安装器；记录期间请保持窗口打开。\n"
        "完成后查看准确会话，搜索文件事件，查看解释或导出报告。\n"
        "个人库位置沿用 Windows LocalApplicationData/Contain/Records/contain.db，不重建旧记录。\n"
        "高级设置保留目录观察、参数和自定义保存位置。外部库仅只读。\n\n"
        "WPF UI 是第三方 MIT 开源组件。UI 0.4.0-beta.1 / 核心 0.3.2-beta.10。\n"
        "退出码、保存及覆盖质量分别表达。High 不是文件所有权或安全删除许可。\n"
        "未承诺完整软件足迹、卸载、回滚。程序不自动提权；ETW 受当前令牌权限限制。\n"
        "此包在原生验收前冻结，FINAL-ACCEPTANCE.json 是独立的最终验收依据。\n"
        "仅构建、旧 WinForms 报告或诊断预览不代表新界面通过验收。\n", encoding="utf-8")
    files = [identity(p, args.package) for p in sorted(args.package.rglob("*")) if p.is_file()]
    build = dict(schema_version=1, protocol="contain-modern-desktop-package-1", version=VERSION,
                 source_sha=source, viewer_configuration="Release", target="win-x64", framework="net10.0-windows",
                 ui_framework="WPF + WPF UI", sdk=SDK, runtime=RUNTIME, self_contained=True,
                 trimming=False, aot=False, core_version="0.3.2-beta.10", core_source_sha=CORE_SOURCE,
                 core_sha256=CORE_SHA, core_profile=baseline_build["core_profile"],
                 core_baseline_zip_sha256=BASELINE_SHA, reused_core_binary=True,
                 dependencies=DEPENDENCIES,
                 runtime_licenses=licenses, native_validation=NATIVE_VALIDATION,
                 files=files)
    write(args.package / "BUILD.json", build)
    write(args.package / "CONTENTS.json", dict(schema_version=1, source_sha=source,
          files=[identity(p, args.package) for p in sorted(args.package.rglob("*")) if p.is_file()]))
    args.zip.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.zip, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(args.package.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(args.package).as_posix())
    verify(args.zip, args.package)
    print(json.dumps(dict(zip=args.zip.name, sha256=sha(args.zip), source_sha=source)))


def verify(archive_path, extracted=None):
    with zipfile.ZipFile(archive_path) as archive:
        manifest = verify_manifest(archive)
        build = json.loads(archive.read("BUILD.json"))
        require(build["protocol"] == "contain-modern-desktop-package-1" and build["version"] == VERSION,
                "modern package identity differs")
        require(build["source_sha"] == manifest["source_sha"] and build["core_source_sha"] == CORE_SOURCE
                and build["core_sha256"] == CORE_SHA, "source identities differ")
        expected = dict(schema_version=1, viewer_configuration="Release", target="win-x64", framework="net10.0-windows",
                        ui_framework="WPF + WPF UI", sdk=SDK, runtime=RUNTIME, self_contained=True,
                        trimming=False, aot=False, core_version="0.3.2-beta.10", core_profile="debug", reused_core_binary=True,
                        core_baseline_zip_sha256=BASELINE_SHA, dependencies=DEPENDENCIES, native_validation=NATIVE_VALIDATION)
        require(all(type(build.get(key)) is type(value) and build.get(key) == value for key, value in expected.items()),
                "frozen build metadata differs")
        build_files = build.get("files", [])
        require(len(build_files) == len({row["path"] for row in build_files})
                and {row["path"]: row for row in build_files}
                == {row["path"]: row for row in manifest["files"] if row["path"] != "BUILD.json"},
                "build file identities differ from manifest")
        require(hashlib.sha256(archive.read("contain.exe")).hexdigest() == CORE_SHA, "accepted core differs")
        names = archive.namelist()
        def prefix(name):
            with archive.open(name) as stream:
                return stream.read(16)
        member_policy(names, prefix)
        runtime_data_contract(json.loads(archive.read("Contain.Viewer.runtimeconfig.json")),
                              json.loads(archive.read("Contain.Viewer.deps.json")), names)
        if extracted:
            files = tree_files(extracted)
            require(set(files) == set(names), "extracted membership differs")
            for name in archive.namelist():
                require(sha(files[name]) == hashlib.sha256(archive.read(name)).hexdigest(), "extracted member changed")
    return build


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    freeze = sub.add_parser("prepare")
    for name in ["viewer", "baseline", "fixtures", "package", "zip", "fixture", "nuget"]:
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
