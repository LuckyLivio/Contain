"""Beta.2 freeze and provenance regressions using isolated synthetic bytes."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location("beta2_package", Path(__file__).with_name("package-beta2-desktop.py"))
P = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(P)
SPEC2 = importlib.util.spec_from_file_location("archive_test_fixtures", Path(__file__).with_name("test-package-modern-desktop.py"))
F = importlib.util.module_from_spec(SPEC2)
SPEC2.loader.exec_module(F)
F.PACKAGE.VERSION = P.VERSION
SOURCE = "1" * 40
CORE = b"synthetic newly built beta2 core"


def metadata():
    return dict(schema_version=1, protocol=P.PROTOCOL, version=P.VERSION, source_sha=SOURCE,
                viewer_configuration="Release", target="win-x64", framework="net10.0-windows",
                ui_framework="WPF + WPF UI", sdk=P.BASE.SDK, runtime=P.BASE.RUNTIME, self_contained=True,
                trimming=False, aot=False, core_version=P.VERSION, core_source_sha=SOURCE,
                core_sha256=hashlib.sha256(CORE).hexdigest(), core_profile="debug", reused_core_binary=False,
                dependencies=P.BASE.DEPENDENCIES, native_validation=P.BASE.NATIVE_VALIDATION)


class Beta2Freeze(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="contain-beta2-freeze-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def archive(self, change=None, extra=None):
        members = F.published_members() | {"contain.exe": CORE}
        members.update(extra or {})
        build = metadata()
        if change:
            build.update(change)
        build["files"] = [dict(path=n, bytes=len(b), sha256=hashlib.sha256(b).hexdigest()) for n, b in members.items()]
        members["BUILD.json"] = F.json_bytes(build)
        path = self.root / "product.zip"
        F.zip_bytes(path, members)
        return path

    def args(self):
        viewer = self.root / "viewer"
        viewer.mkdir()
        for name, data in F.published_members().items():
            (viewer / name).write_bytes(data)
        core = self.root / "contain.exe"
        core.write_bytes(CORE)
        fixture = self.root / "fixture-source.exe"
        fixture.write_bytes(b"synthetic fixture")
        runtime = self.root / "runtime"
        runtime.mkdir()
        (runtime / "vcruntime140.dll").write_bytes(b"synthetic runtime")
        fixtures = self.root / "samples"
        fixtures.mkdir()
        (fixtures / "viewer-demo.db").write_bytes(b"SQLite format 3\x00synthetic")
        return argparse.Namespace(viewer=viewer, core=core, fixture_source=fixture, runtime_directory=runtime,
             fixtures=fixtures, nuget=self.root / "nuget", package=self.root / "package",
             zip=self.root / "frozen.zip", fixture=self.root / "frozen-fixture.exe", source=SOURCE)

    def process(self, command, **kwargs):
        if command[:3] == ["git", "rev-parse", "HEAD"]:
            return SOURCE + "\n"
        if command[:2] == ["git", "status"]:
            return b""
        if command == ["dotnet", "--version"]:
            return P.BASE.SDK + "\n"
        if command[-1] == "--version":
            return "contain " + P.VERSION + "\n"
        raise AssertionError("unexpected process")

    def test_new_core_has_same_source_and_no_inherited_acceptance(self):
        build = P.verify(self.archive())
        self.assertEqual(build["core_source_sha"], SOURCE)
        self.assertFalse(build["reused_core_binary"])
        self.assertNotIn("accepted", build)
        self.assertIn("not implied by build", build["native_validation"])

    def test_historical_core_and_false_acceptance_are_rejected(self):
        for change in [{"core_source_sha": "2" * 40}, {"source_sha": "2" * 40},
                       {"core_version": "0.3.2-beta.10"}, {"reused_core_binary": True},
                       {"native_validation": "accepted"}, {"core_sha256": "0" * 64},
                       {"runtime": "10.0.13"}]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                P.verify(self.archive(change))

    def test_private_material_and_fixture_do_not_enter_zip(self):
        for name, data in [("personal.db", b"private"), ("trace.etl", b"private"),
                           ("FINAL-ACCEPTANCE.json", b"{}"), ("contain-test-installer.exe", b"fixture"),
                           ("hidden.dll", b"SQLite format 3\x00private")]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                P.verify(self.archive(extra={name: data}))

    def test_changed_extracted_binary_invalidates_verification(self):
        archive = self.archive()
        folder = self.root / "extracted"
        with zipfile.ZipFile(archive) as z:
            z.extractall(folder)
        before = P.BASE.sha(archive)
        P.verify(archive, folder)
        (folder / "contain.exe").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            P.verify(archive, folder)
        self.assertEqual(P.BASE.sha(archive), before)

    def test_existing_output_is_preserved(self):
        args = self.args()
        args.zip.write_bytes(b"keep")
        with patch.object(P.subprocess, "check_output") as process, self.assertRaises(ValueError):
            P.prepare(args)
        process.assert_not_called()
        self.assertEqual(args.zip.read_bytes(), b"keep")

    def test_output_overlap_is_rejected(self):
        args = self.args()
        args.fixture = args.package / "fixture.exe"
        with patch.object(P.subprocess, "check_output") as process, self.assertRaises(ValueError):
            P.prepare(args)
        process.assert_not_called()

    def test_old_core_version_fails_before_copy(self):
        args = self.args()
        def output(command, **kwargs):
            return "contain 0.3.2-beta.10\n" if command[-1] == "--version" and command[0] != "dotnet" else self.process(command, **kwargs)
        with patch.object(P.subprocess, "check_output", side_effect=output), self.assertRaises(ValueError):
            P.prepare(args)
        self.assertFalse(args.package.exists())

    def test_freeze_copies_new_core_and_keeps_fixture_separate(self):
        args = self.args()
        checked = []
        helper = types.SimpleNamespace(validate_sample=lambda core, fixtures: checked.append(core),
                                       copy_runtime_licenses=lambda *args: [])
        fake_spec = types.SimpleNamespace(loader=types.SimpleNamespace(exec_module=lambda module: None))
        before = args.core.read_bytes()
        with patch.object(P.subprocess, "check_output", side_effect=self.process), \
             patch.object(P.importlib.util, "spec_from_file_location", return_value=fake_spec), \
             patch.object(P.importlib.util, "module_from_spec", return_value=helper):
            P.prepare(args)
        build = P.verify(args.zip, args.package)
        self.assertEqual(args.core.read_bytes(), before)
        self.assertEqual((args.package / "contain.exe").read_bytes(), before)
        self.assertEqual(args.fixture.read_bytes(), args.fixture_source.read_bytes())
        self.assertEqual(checked, [args.package / "contain.exe"])
        self.assertEqual(build["core_source_sha"], SOURCE)
        with zipfile.ZipFile(args.zip) as z:
            self.assertNotIn("contain-test-installer.exe", z.namelist())


if __name__ == "__main__":
    unittest.main()
