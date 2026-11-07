"""Isolated packaging contracts; synthetic bytes are not native Windows evidence."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch
import warnings
import zipfile


SPEC = importlib.util.spec_from_file_location("modern_package", Path(__file__).with_name("package-modern-desktop.py"))
PACKAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PACKAGE)
SOURCE = "1" * 40
CORE = b"isolated synthetic core, never executed"
CORE_HASH = hashlib.sha256(CORE).hexdigest()
PRODUCTION_CORE_HASH = PACKAGE.CORE_SHA


def json_bytes(data):
    return (json.dumps(data) + "\n").encode()


def zip_bytes(path, members, source=SOURCE, manifest=None):
    if manifest is None:
        manifest = dict(schema_version=1, source_sha=source, files=[
            dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
            for name, data in members.items()])
    with zipfile.ZipFile(path, "w") as archive:
        for name, data in members.items():
            archive.writestr(name, data)
        archive.writestr("CONTENTS.json", json_bytes(manifest))


def framework_options():
    return dict(tfm="net10.0", includedFrameworks=[
        dict(name="Microsoft.NETCore.App", version=PACKAGE.RUNTIME),
        dict(name="Microsoft.WindowsDesktop.App", version=PACKAGE.RUNTIME)])


def dependency_options():
    keys = ["Contain.Viewer/" + PACKAGE.VERSION, "Contain.Desktop.Model/" + PACKAGE.VERSION,
            "WPF-UI/4.3.0", "WPF-UI.Abstractions/4.3.0", "CommunityToolkit.Mvvm/8.4.2"]
    target = ".NETCoreApp,Version=v10.0/win-x64"
    runtime_pack = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/" + PACKAGE.RUNTIME
    return dict(runtimeTarget=dict(name=target), libraries={key: {} for key in keys + [runtime_pack]},
                targets={target: {**{key: {} for key in keys}, runtime_pack: {"native": {"createdump.exe": {}}}}})


def published_members():
    names = ["Contain.Viewer.exe", "Contain.Viewer.dll", "Contain.Desktop.Model.dll",
             "Wpf.Ui.dll", "Wpf.Ui.Abstractions.dll", "CommunityToolkit.Mvvm.dll",
             "PresentationFramework.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "createdump.exe"]
    return {**{name: b"isolated synthetic assembly" for name in names},
            "Contain.Viewer.runtimeconfig.json": json_bytes(dict(runtimeOptions=framework_options())),
            "Contain.Viewer.deps.json": json_bytes(dependency_options())}


def build_metadata():
    return dict(schema_version=1, protocol="contain-modern-desktop-package-1", version=PACKAGE.VERSION,
                source_sha=SOURCE, viewer_configuration="Release", target="win-x64", framework="net10.0-windows",
                ui_framework="WPF + WPF UI", sdk=PACKAGE.SDK, runtime=PACKAGE.RUNTIME,
                self_contained=True, trimming=False, aot=False, core_version="0.3.2-beta.10",
                core_source_sha=PACKAGE.CORE_SOURCE, core_sha256=CORE_HASH,
                core_profile="debug", core_baseline_zip_sha256=PACKAGE.BASELINE_SHA,
                reused_core_binary=True,
                dependencies={"WPF-UI": "4.3.0", "WPF-UI.Abstractions": "4.3.0", "CommunityToolkit.Mvvm": "8.4.2"},
                native_validation="separate FINAL-ACCEPTANCE.json; not implied by build")


class ModernPackageContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="contain-package-contract-")
        self.root = Path(self.temp.name)
        self.addCleanup(self.temp.cleanup)
        self.core_patch = patch.object(PACKAGE, "CORE_SHA", CORE_HASH)
        self.core_patch.start()
        self.addCleanup(self.core_patch.stop)

    def archive(self, members=None, build=None, filename="candidate.zip"):
        members = published_members() if members is None else copy.deepcopy(members)
        members["contain.exe"] = CORE
        build = copy.deepcopy(build or build_metadata())
        if "files" not in build:
            build["files"] = [dict(path=n, bytes=len(b), sha256=hashlib.sha256(b).hexdigest())
                              for n, b in members.items()]
        members["BUILD.json"] = json_bytes(build)
        path = self.root / filename
        zip_bytes(path, members)
        return path

    def viewer(self, members=None):
        folder = self.root / "published"
        folder.mkdir(exist_ok=True)
        for name, data in (members or published_members()).items():
            path = folder / name
            path.parent.mkdir(exist_ok=True, parents=True)
            path.write_bytes(data)
        return folder

    def args(self):
        baseline = self.root / "baseline.zip"
        zip_bytes(baseline, {"contain.exe": CORE, "vcruntime140.dll": b"synthetic runtime",
                            "contain-test-installer.exe": b"synthetic fixture",
                            "BUILD.json": json_bytes(dict(core_profile="debug"))}, PACKAGE.CORE_SOURCE)
        return argparse.Namespace(package=self.root / "frozen", zip=self.root / "new.zip",
                                  fixture=self.root / "fixture.exe", source=SOURCE,
                                  baseline=baseline, viewer=self.viewer(), fixtures=self.root / "samples",
                                  nuget=self.root / "nuget")

    def process_output(self, command, **kwargs):
        if command[:3] == ["git", "rev-parse", "HEAD"]:
            return SOURCE + "\n"
        if command[:2] == ["git", "status"]:
            return b""
        if command == ["dotnet", "--version"]:
            return PACKAGE.SDK + "\n"
        if command[-1] == "--version" and Path(command[0]).name == "contain.exe":
            return "contain 0.3.2-beta.10\n"
        raise AssertionError("unexpected subprocess")

    def assert_inputs_unchanged(self, args, baseline_hash, viewer_bytes):
        self.assertEqual(PACKAGE.sha(args.baseline), baseline_hash)
        self.assertEqual({p.relative_to(args.viewer).as_posix(): p.read_bytes()
                          for p in args.viewer.rglob("*") if p.is_file()}, viewer_bytes)

    def test_production_constants_keep_accepted_baseline_and_separate_versions(self):
        self.assertEqual(PACKAGE.BASELINE_SHA, "a7ef3faed3c42aada77fe5619b732fe092b8787f89311047105ff6082b132656")
        self.assertEqual(PACKAGE.CORE_SOURCE, "b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4")
        self.assertEqual(PRODUCTION_CORE_HASH, "a45f193a84fb3d5556e0258941d9f55bddded677098959c6b1a07b1e724e66f6")
        self.assertEqual(PACKAGE.VERSION, "0.4.0-beta.1")
        self.assertEqual(PACKAGE.RUNTIME, "10.0.14")

    def test_valid_manifest_and_self_contained_runtime_are_accepted_without_native_claim(self):
        archive = self.archive()
        build = PACKAGE.verify(archive)
        self.assertNotIn("accepted", build)
        self.assertIn("not implied by build", build["native_validation"])
        PACKAGE.runtime_contract(self.viewer())

    def test_manifest_requires_all_members_and_exact_hashes(self):
        for flaw in ["missing", "extra", "hash", "size", "duplicate"]:
            with self.subTest(flaw=flaw):
                members = {"one.dll": b"one", "two.dll": b"two"}
                rows = [dict(path=n, bytes=len(b), sha256=hashlib.sha256(b).hexdigest()) for n, b in members.items()]
                if flaw == "missing": rows.pop()
                if flaw == "extra": rows.append(dict(path="extra", bytes=0, sha256=hashlib.sha256(b"").hexdigest()))
                if flaw == "hash": rows[0]["sha256"] = "0" * 64
                if flaw == "size": rows[0]["bytes"] += 1
                if flaw == "duplicate": rows.append(rows[0])
                path = self.root / (flaw + ".zip")
                zip_bytes(path, members, manifest=dict(schema_version=1, source_sha=SOURCE, files=rows))
                with zipfile.ZipFile(path) as archive, self.assertRaises(ValueError):
                    PACKAGE.verify_manifest(archive)

    def test_unsafe_windows_archive_paths_are_rejected(self):
        for name in ["../outside", "/absolute", "folder\\escape", "C:stream", "folder/./file",
                     "folder//file", "CON.txt", "folder/Lpt1.dll", "file. ", "file."]:
            with self.subTest(path=name):
                path = self.root / "unsafe.zip"
                zip_bytes(path, {name: b"x"})
                with zipfile.ZipFile(path) as archive, self.assertRaises(ValueError):
                    PACKAGE.verify_manifest(archive)

    def test_case_alias_and_duplicate_zip_names_are_rejected(self):
        for names in [["core.dll", "CORE.DLL"], ["core.dll", "core.dll"]]:
            path = self.root / "aliases.zip"
            with warnings.catch_warnings(), zipfile.ZipFile(path, "w") as archive:
                warnings.simplefilter("ignore", UserWarning)
                for name in names: archive.writestr(name, b"x")
            with zipfile.ZipFile(path) as archive, self.assertRaises(ValueError):
                PACKAGE.archive_members(archive)

    def test_zip_symlink_is_rejected(self):
        path = self.root / "symlink.zip"
        info = zipfile.ZipInfo("library.dll")
        info.create_system = 3
        info.external_attr = (0o120777 << 16)
        with zipfile.ZipFile(path, "w") as archive: archive.writestr(info, "../private")
        with zipfile.ZipFile(path) as archive, self.assertRaises(ValueError): PACKAGE.archive_members(archive)

    def test_runtime_patch_framework_dependent_and_duplicate_frameworks_fail(self):
        for mode in ["patch", "tfm", "framework", "frameworks", "duplicate"]:
            with self.subTest(mode=mode):
                options = framework_options()
                if mode == "patch": options["includedFrameworks"][0]["version"] = "10.0.13"
                if mode == "tfm": options["tfm"] = "net8.0"
                if mode in ["framework", "frameworks"]: options[mode] = {}
                if mode == "duplicate": options["includedFrameworks"][1] = options["includedFrameworks"][0]
                members = published_members()
                members["Contain.Viewer.runtimeconfig.json"] = json_bytes(dict(runtimeOptions=options))
                with self.assertRaises(ValueError): PACKAGE.runtime_contract(self.viewer(members))
                with self.assertRaises(ValueError): PACKAGE.verify(self.archive(members))

    def test_dependency_library_target_architecture_and_versions_are_checked(self):
        for mode in ["missing", "wrong_version", "extra_version", "architecture", "target"]:
            with self.subTest(mode=mode):
                deps = dependency_options()
                if mode == "missing": deps["libraries"].pop("WPF-UI/4.3.0")
                if mode == "wrong_version": deps["libraries"]["WPF-UI/4.2.1"] = deps["libraries"].pop("WPF-UI/4.3.0")
                if mode == "extra_version": deps["libraries"]["WPF-UI/4.2.1"] = {}
                if mode == "architecture": deps["runtimeTarget"]["name"] = ".NETCoreApp,Version=v10.0/win-arm64"
                if mode == "target": deps["targets"][deps["runtimeTarget"]["name"]].pop("WPF-UI/4.3.0")
                members = published_members()
                members["Contain.Viewer.deps.json"] = json_bytes(deps)
                with self.assertRaises(ValueError): PACKAGE.runtime_contract(self.viewer(members))
                with self.assertRaises(ValueError): PACKAGE.verify(self.archive(members))

    def test_missing_runtime_or_component_binary_is_rejected(self):
        for name in ["coreclr.dll", "Contain.Viewer.exe", "Wpf.Ui.Abstractions.dll", "CommunityToolkit.Mvvm.dll"]:
            with self.subTest(name=name):
                members = published_members()
                members.pop(name)
                with self.assertRaises(ValueError): PACKAGE.verify(self.archive(members))

    def test_createdump_requires_the_pinned_runtime_native_declaration(self):
        members = published_members()
        deps = dependency_options()
        deps["targets"][deps["runtimeTarget"]["name"]].pop("runtimepack.Microsoft.NETCore.App.Runtime.win-x64/" + PACKAGE.RUNTIME)
        members["Contain.Viewer.deps.json"] = json_bytes(deps)
        with self.assertRaisesRegex(ValueError, "createdump"):
            PACKAGE.verify(self.archive(members))

    def test_self_consistent_build_with_wrong_provenance_or_claims_is_rejected(self):
        changes = {"source_sha": "2" * 40, "core_source_sha": "2" * 40, "core_sha256": "0" * 64,
                   "core_baseline_zip_sha256": "0" * 64, "core_version": "0.4.0-beta.1",
                   "runtime": "8.0.33", "sdk": "10.0.100", "self_contained": False,
                   "core_profile": "release",
                   "target": "win-arm64", "ui_framework": "WinForms", "aot": True,
                   "trimming": True, "reused_core_binary": False,
                   "native_validation": "old WinForms acceptance passed"}
        for field, value in changes.items():
            with self.subTest(field=field):
                build = build_metadata()
                build[field] = value
                with self.assertRaises(ValueError): PACKAGE.verify(self.archive(build=build))

    def test_build_file_identities_must_match_complete_manifest(self):
        build = build_metadata()
        build["files"] = []
        with self.assertRaisesRegex(ValueError, "build file identities"):
            PACKAGE.verify(self.archive(build=build))

    def test_rehashed_modified_core_still_fails_frozen_identity(self):
        archive = self.archive()
        with zipfile.ZipFile(archive) as current:
            members = {n: current.read(n) for n in current.namelist() if n != "CONTENTS.json"}
        members["contain.exe"] = b"changed core"
        build = json.loads(members["BUILD.json"])
        build["files"] = [dict(path=n, bytes=len(b), sha256=hashlib.sha256(b).hexdigest())
                          for n, b in members.items() if n != "BUILD.json"]
        members["BUILD.json"] = json_bytes(build)
        zip_bytes(archive, members)
        with self.assertRaisesRegex(ValueError, "accepted core"):
            PACKAGE.verify(archive)

    def test_private_database_trace_and_old_acceptance_cannot_enter_user_package(self):
        for name, data in [("personal.db", b"private"), ("notes.sqlite3", b"private"),
                           ("samples/viewer-demo.db-wal", b"private"), ("trace.etl", b"private"),
                           ("diagnostic.log", b"private"), ("VALIDATION.json", b"{}"),
                           ("FINAL-ACCEPTANCE.json", b"{}"), ("contain-test-installer.exe", b"fixture"),
                           ("licenses/private-notes.txt", b"private"),
                           ("renamed.dll", b"SQLite format 3\x00private")]:
            with self.subTest(name=name):
                members = published_members()
                members[name] = data
                with self.assertRaises(ValueError): PACKAGE.verify(self.archive(members))

    def test_extracted_exact_members_and_content_are_read_only_verified(self):
        archive = self.archive()
        extracted = self.root / "extracted"
        with zipfile.ZipFile(archive) as z: z.extractall(extracted)
        before = PACKAGE.sha(archive)
        PACKAGE.verify(archive, extracted)
        original = (extracted / "Contain.Viewer.dll").read_bytes()
        (extracted / "Contain.Viewer.dll").write_bytes(b"changed")
        with self.assertRaises(ValueError): PACKAGE.verify(archive, extracted)
        (extracted / "Contain.Viewer.dll").write_bytes(original)
        (extracted / "private.db").write_bytes(b"private")
        with self.assertRaises(ValueError): PACKAGE.verify(archive, extracted)
        self.assertEqual(PACKAGE.sha(archive), before)
        self.assertEqual((extracted / "private.db").read_bytes(), b"private")

    def test_preexisting_freeze_outputs_are_preserved_before_subprocess(self):
        for output in ["package", "zip", "fixture"]:
            with self.subTest(output=output):
                args = self.args()
                path = getattr(args, output)
                if output == "package": path.mkdir(); (path / "keep.txt").write_bytes(b"preserve")
                else: path.write_bytes(b"preserve")
                with patch.object(PACKAGE.subprocess, "check_output") as process, self.assertRaises(ValueError):
                    PACKAGE.prepare(args)
                process.assert_not_called()
                if output == "package": self.assertEqual((path / "keep.txt").read_bytes(), b"preserve")
                else: self.assertEqual(path.read_bytes(), b"preserve")
                if output != "package": self.assertFalse(args.package.exists())
                if output == "package": (path / "keep.txt").unlink(); path.rmdir()
                else: path.unlink()

    def test_changed_baseline_fails_before_any_destination_is_created(self):
        args = self.args()
        before = PACKAGE.sha(args.baseline)
        viewer_bytes = {p.name: p.read_bytes() for p in args.viewer.iterdir()}
        with patch.object(PACKAGE.subprocess, "check_output", side_effect=self.process_output), self.assertRaisesRegex(ValueError, "baseline"):
            PACKAGE.prepare(args)
        self.assertFalse(args.package.exists())
        self.assertFalse(args.zip.exists())
        self.assertFalse(args.fixture.exists())
        self.assert_inputs_unchanged(args, before, viewer_bytes)

    def test_wrong_source_dirty_tree_or_sdk_fails_before_freeze(self):
        for mode in ["source", "dirty", "sdk"]:
            with self.subTest(mode=mode):
                args = self.args()
                def output(command, **kwargs):
                    if mode == "source" and command[:2] == ["git", "rev-parse"]: return "2" * 40 + "\n"
                    if mode == "dirty" and command[:2] == ["git", "status"]: return b" M source.cs\n"
                    if mode == "sdk" and command == ["dotnet", "--version"]: return "10.0.100\n"
                    return self.process_output(command, **kwargs)
                with patch.object(PACKAGE.subprocess, "check_output", side_effect=output), self.assertRaises(ValueError):
                    PACKAGE.prepare(args)
                self.assertFalse(args.package.exists())
                self.assertFalse(args.zip.exists())

    def test_output_overlap_is_rejected_before_any_source_or_process_access(self):
        for mode in ["zip_in_package", "package_in_viewer", "fixture_is_zip", "fixture_in_samples"]:
            with self.subTest(mode=mode):
                args = self.args()
                if mode == "zip_in_package": args.zip = args.package / "recursive.zip"
                if mode == "package_in_viewer": args.package = args.viewer / "recursive"
                if mode == "fixture_is_zip": args.fixture = args.zip
                if mode == "fixture_in_samples": args.fixture = args.fixtures / "fixture.exe"
                with patch.object(PACKAGE.subprocess, "check_output") as process, self.assertRaisesRegex(ValueError, "overlap"):
                    PACKAGE.prepare(args)
                process.assert_not_called()
                self.assertFalse(args.package.exists())

    def test_valid_prepare_keeps_source_bytes_and_acceptance_separate(self):
        args = self.args()
        args.fixtures.mkdir()
        sample = args.fixtures / "viewer-demo.db"
        sample.write_bytes(b"SQLite format 3\x00synthetic isolated sample")
        before = PACKAGE.sha(args.baseline)
        viewer_bytes = {p.name: p.read_bytes() for p in args.viewer.iterdir()}
        checked = []
        # Native sample queries and runtime license-pack inspection have their own
        # existing tests. Here the spies ensure the real prepare path calls them.
        helper = types.SimpleNamespace(
            validate_sample=lambda core, fixtures: checked.append((core.name, fixtures)),
            copy_runtime_licenses=lambda frameworks, destination, nuget: [])
        fake_spec = types.SimpleNamespace(loader=types.SimpleNamespace(exec_module=lambda module: None))
        with patch.object(PACKAGE, "BASELINE_SHA", before), \
                patch.object(PACKAGE.subprocess, "check_output", side_effect=self.process_output), \
                patch.object(PACKAGE.importlib.util, "spec_from_file_location", return_value=fake_spec), \
                patch.object(PACKAGE.importlib.util, "module_from_spec", return_value=helper):
            PACKAGE.prepare(args)
            build = PACKAGE.verify(args.zip, args.package)
        self.assertEqual(checked, [("contain.exe", args.fixtures)])
        self.assertEqual(args.fixture.read_bytes(), b"synthetic fixture")
        self.assert_inputs_unchanged(args, before, viewer_bytes)
        self.assertEqual((args.package / "samples/viewer-demo.db").read_bytes(), sample.read_bytes())
        with zipfile.ZipFile(args.zip) as archive:
            self.assertNotIn("contain-test-installer.exe", archive.namelist())
            self.assertNotIn("VALIDATION.json", archive.namelist())
            self.assertNotIn("FINAL-ACCEPTANCE.json", archive.namelist())
        self.assertEqual(build["core_version"], "0.3.2-beta.10")
        self.assertEqual(build["version"], "0.4.0-beta.1")

    def test_published_and_extracted_links_do_not_pass_as_regular_files(self):
        args = self.args()
        outside = self.root / "outside.dll"
        outside.write_bytes(b"same bytes")
        link = args.viewer / "alias.dll"
        try:
            link.symlink_to(outside)
        except OSError as error:
            # Do not silently skip on a runner without symlink privileges: the
            # archive-link case remains covered and the local limitation is explicit.
            raise AssertionError("symlink contract needs supported test filesystem") from error
        with self.assertRaisesRegex(ValueError, "link"):
            PACKAGE.runtime_contract(args.viewer)
        archive = self.archive()
        extracted = self.root / "extracted"
        with zipfile.ZipFile(archive) as z: z.extractall(extracted)
        target = extracted / "Contain.Viewer.dll"
        outside.write_bytes(target.read_bytes())
        target.unlink()
        target.symlink_to(outside)
        with self.assertRaisesRegex(ValueError, "link"):
            PACKAGE.verify(archive, extracted)
        self.assertTrue(target.is_symlink())

    def test_unreviewed_published_files_fail_before_copying(self):
        for name in ["private.db", "install.log", "report.json", "contain.exe", "contain-test-installer.exe"]:
            with self.subTest(name=name):
                args = self.args()
                (args.viewer / name).write_bytes(b"must stay local")
                with patch.object(PACKAGE, "BASELINE_SHA", PACKAGE.sha(args.baseline)), \
                        patch.object(PACKAGE.subprocess, "check_output", side_effect=self.process_output), self.assertRaises(ValueError):
                    PACKAGE.prepare(args)
                self.assertFalse(args.package.exists())
                self.assertFalse(args.fixture.exists())
                self.assertEqual((args.viewer / name).read_bytes(), b"must stay local")
                (args.viewer / name).unlink()


if __name__ == "__main__":
    unittest.main()
