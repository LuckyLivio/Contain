"""Synthetic packaging/metadata tests; no Windows capture or installer execution."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path, PurePosixPath, PureWindowsPath
import struct
import subprocess
import tempfile
import unittest
from unittest import mock
import zipfile


SPEC = importlib.util.spec_from_file_location("trial_package", Path(__file__).with_name("package-trial.py"))
TRIAL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRIAL)


def synthetic_pe(imports=("KERNEL32.dll", "api-ms-win-core-synch-l1-2-0.dll"), delayed=()):
    data = bytearray(4096)
    data[:2] = b"MZ"; struct.pack_into("<I", data, 0x3c, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HH", data, 0x84, 0x8664, 1)
    struct.pack_into("<H", data, 0x94, 0xf0)
    optional = 0x98
    struct.pack_into("<H", data, optional, 0x20b)
    struct.pack_into("<I", data, optional + 60, 0x200)
    struct.pack_into("<I", data, optional + 108, 16)
    section = optional + 0xf0
    struct.pack_into("<IIII", data, section + 8, 0xe00, 0x1000, 0xe00, 0x200)
    if imports:
        struct.pack_into("<II", data, optional + 112 + 8, 0x1000, (len(imports) + 1) * 20)
    for index, name in enumerate(imports):
        struct.pack_into("<IIIII", data, 0x200 + index * 20, 0x1600, 0, 0, 0x1200 + index * 100, 0x1700)
        encoded = name.encode() + b"\0"; start = 0x400 + index * 100
        data[start:start + len(encoded)] = encoded
    if delayed:
        struct.pack_into("<II", data, optional + 112 + 13 * 8, 0x1800, (len(delayed) + 1) * 32)
    for index, name in enumerate(delayed):
        struct.pack_into("<IIIIIIII", data, 0xa00 + index * 32, 1, 0x1a00 + index * 100, 0, 0x1600, 0, 0, 0, 0)
        encoded = name.encode() + b"\0"; start = 0xc00 + index * 100
        data[start:start + len(encoded)] = encoded
    return bytes(data)


class TrialTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.repo = self.root / "repo"; self.repo.mkdir()
        for relative in ["scripts/" + name for name in TRIAL.SCRIPTS] + list(TRIAL.RESOURCES):
            path = self.repo / relative; path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic package resource\n")
        self.contain = self.root / "contain-input.exe"; self.contain.write_bytes(synthetic_pe())
        self.fixture = self.root / "fixture-input.exe"; self.fixture.write_bytes(synthetic_pe())
        self.package = self.root / "package"
        self.source = "a" * 40; self.tree = "b" * 64

    def tearDown(self):
        self.temporary.cleanup()

    def prepare(self, runtime=None):
        args = argparse.Namespace(repository=self.repo, contain=self.contain, fixture=self.fixture,
            output=self.package, profile="debug", runtime_directory=runtime)
        with mock.patch.object(TRIAL, "source_identity", return_value=(self.source, self.tree)), \
                mock.patch.object(TRIAL.subprocess, "check_output", return_value="synthetic compiler version\n"):
            self.assertEqual(TRIAL.prepare(args), 0)
        return TRIAL.read(self.package / "BUILD.json")

    def run_evidence(self, name, manifest, clock=False, product_status="passed"):
        directory = self.root / name; directory.mkdir()
        binaries = [dict(path="C:\\synthetic-package\\" + row["name"], bytes=row["bytes"], sha256=row["sha256"])
                    for row in manifest["binaries"]]
        source = dict(head=self.source, runtime_tree_sha256=self.tree)
        digest = TRIAL.sha(self.package / "BUILD.json")
        identity = dict(build_mode="prebuilt", prebuilt_manifest_verified=True, prebuilt_manifest_unchanged=True,
            prebuilt_manifest_sha256=digest, prebuilt_manifest_after_sha256=digest,
            binary_before=binaries, binary_after=dict(unchanged=True, binaries=[dict(before=row, after=row, unchanged=True) for row in binaries]),
            source_before_build=source, source_after_build=source, fixture_clock="1" if clock else None,
            raw_clock_path="C:\\synthetic-output\\clock.json" if clock else None, database=f"C:\\synthetic-output\\{name}.db")
        TRIAL.write(directory / "run-identity.json", identity)
        TRIAL.write(directory / "product-checks.json", dict(schema_version=1, protocol_revision="contain-demo-result-1",
            planned_check_names=sorted(TRIAL.DEMO_PLAN), missing_planned_checks=[],
            checks=[dict(name=name, domain="product", required=True, status=product_status) for name in sorted(TRIAL.DEMO_PRODUCT_PLAN)]
                + [dict(name="clock_diagnostic", domain="diagnostic", required=clock, status="passed" if clock else "not_run")],
            product=dict(passed=product_status == "passed",
                required_passed=len(TRIAL.DEMO_PRODUCT_PLAN) * int(product_status == "passed"), required_failed=len(TRIAL.DEMO_PRODUCT_PLAN) * int(product_status == "failed"),
                required_not_run=len(TRIAL.DEMO_PRODUCT_PLAN) * int(product_status == "not_run")),
            bounded_pass=product_status == "passed", clock_requested=clock,
            diagnostic_execution=dict(status="completed" if clock else "not_requested"),
            evidence=dict(status="complete" if clock else "not_evaluated")))
        return directory

    def reports(self):
        for name in ("default", "dual-clock"):
            root = self.package / "examples" / name; root.mkdir(parents=True)
            (root / "cli-report.txt").write_text("Synthetic test report; not ETW\n")
            TRIAL.write(root / "summary.json", dict(schema_version=1, protocol_revision=TRIAL.PROTOCOL,
                scenario=name, scope="real_etw_controlled_fixture_cli_excerpt", source_sha=self.source, complete=True, event_id="synthetic-event",
                report_sha256=TRIAL.sha(root / "cli-report.txt")))

    def finalize_args(self, default, clock):
        analysis = self.root / "analysis.json"
        TRIAL.write(analysis, dict(source_sha=self.source, diagnostic_evidence_complete=True, missing_evidence=[]))
        quality = self.root / "quality.json"
        TRIAL.write(quality, dict(source_revision=self.source, current_required_gates_passed=True))
        return argparse.Namespace(package=self.package, default_directory=default, clock_directory=clock,
            clock_analysis=analysis, quality_summary=quality, output=self.root / "trial.zip")

    def test_actual_pe_imports_and_delayed_dependencies_are_read(self):
        self.contain.write_bytes(synthetic_pe(delayed=("VCRUNTIME140.dll",)))
        result = TRIAL.pe_imports(self.contain)
        self.assertEqual(result["machine"], "AMD64")
        self.assertEqual(result["imports"], ["api-ms-win-core-synch-l1-2-0.dll", "kernel32.dll"])
        self.assertEqual(result["delay_imports"], ["vcruntime140.dll"])

    def test_bad_architecture_out_of_bounds_and_non_pe_are_rejected(self):
        bad = bytearray(synthetic_pe()); struct.pack_into("<H", bad, 0x84, 0x14c)
        for data in (b"not an executable", bytes(bad), synthetic_pe()[:300]):
            with self.subTest(size=len(data)):
                self.contain.write_bytes(data)
                with self.assertRaises(ValueError): TRIAL.pe_imports(self.contain)

    def test_non_system_and_debug_runtime_are_not_silently_assumed_installed(self):
        self.contain.write_bytes(synthetic_pe(imports=("VCRUNTIME140D.dll",)))
        with self.assertRaisesRegex(ValueError, "unprovided_non_system_runtime_dependency"):
            self.prepare()

    def test_explicit_runtime_is_packaged_and_recursively_inspected(self):
        self.contain.write_bytes(synthetic_pe(imports=("VCRUNTIME140.dll",)))
        runtime = self.root / "redist"; runtime.mkdir()
        (runtime / "VCRUNTIME140.dll").write_bytes(synthetic_pe(imports=("KERNEL32.dll",)))
        manifest = self.prepare(runtime)
        self.assertEqual(manifest["runtime_files"][0]["name"], "vcruntime140.dll")
        self.assertTrue((self.package / "vcruntime140.dll").is_file())
        self.assertEqual(manifest["pe_dependencies"]["vcruntime140.dll"]["imports"], ["kernel32.dll"])

    def test_frozen_executable_script_and_resource_changes_are_rejected(self):
        self.prepare()
        for name in ("contain.exe", "scripts/demo.ps1", "docs/schema/cli-v3.schema.json"):
            path = self.package / name; before = path.read_bytes()
            with self.subTest(path=name):
                path.write_bytes(before + b"changed")
                with self.assertRaisesRegex(ValueError, "package_file_changed"): TRIAL.verify_package(self.package)
                path.write_bytes(before)

    def test_prebuilt_identity_requires_same_manifest_and_actual_binary_before_after(self):
        manifest = self.prepare(); directory = self.run_evidence("default", manifest)
        path = directory / "run-identity.json"; value = TRIAL.read(path); digest = TRIAL.sha(self.package / "BUILD.json")
        self.assertTrue(TRIAL.run_identity_matches(directory, manifest, digest)[0])
        for kind in ("manifest", "source", "binary", "duplicate", "not_prebuilt"):
            bad = copy.deepcopy(value)
            if kind == "manifest": bad["prebuilt_manifest_after_sha256"] = "0" * 64
            elif kind == "source": bad["source_after_build"]["head"] = "0" * 40
            elif kind == "binary": bad["binary_after"]["binaries"][0]["after"]["sha256"] = "0" * 64
            elif kind == "duplicate": bad["binary_before"].append(bad["binary_before"][0])
            else: bad["build_mode"] = "cargo"
            path.write_text(json.dumps(bad))
            with self.subTest(kind=kind): self.assertFalse(TRIAL.run_identity_matches(directory, manifest, digest)[0])
        path.write_text(json.dumps(value))

    def test_successful_scoped_package_retains_formal_unmet_and_same_binary_bytes(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        args = self.finalize_args(default, clock)
        self.assertEqual(TRIAL.finalize(args), 0)
        result = TRIAL.read(self.package / "VALIDATION.json")
        self.assertTrue(result["candidate_verified"])
        self.assertEqual(result["formal"], dict(status="requirements_unmet", accepted=False))
        with zipfile.ZipFile(args.output) as archive:
            self.assertEqual(archive.read("contain.exe"), self.contain.read_bytes())
            self.assertEqual(archive.read("contain-test-installer.exe"), self.fixture.read_bytes())
            self.assertNotIn("private.db", archive.namelist())

    def test_missing_evidence_and_not_run_product_remain_failed_but_diagnostic_zip_exists(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest, product_status="not_run")
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        args = self.finalize_args(default, clock)
        args.clock_analysis.write_text(json.dumps(dict(source_sha=self.source, diagnostic_evidence_complete=False)))
        self.assertEqual(TRIAL.finalize(args), 2)
        result = TRIAL.read(self.package / "VALIDATION.json")
        self.assertFalse(result["candidate_verified"])
        self.assertEqual(result["candidate_status"], "diagnostic_build_unverified")
        self.assertEqual(result["default_product"]["required_not_run"], len(TRIAL.DEMO_PRODUCT_PLAN))
        self.assertTrue(args.output.is_file())

    def test_product_failure_cannot_be_hidden_by_formal_or_clock_success(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest, product_status="failed")
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        args = self.finalize_args(default, clock)
        self.assertEqual(TRIAL.finalize(args), 2)
        self.assertFalse(TRIAL.read(self.package / "VALIDATION.json")["checks"]["default_product"])

    def test_missing_actual_report_text_cannot_be_counted_as_verified(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        (self.package / "examples/default/cli-report.txt").unlink()
        args = self.finalize_args(default, clock)
        self.assertEqual(TRIAL.finalize(args), 2)
        self.assertFalse(TRIAL.read(self.package / "VALIDATION.json")["checks"]["default_actual_cli_report"])

    def test_missing_planned_checks_and_changed_required_counts_are_rejected(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        path = default / "product-checks.json"; original = TRIAL.read(path)
        for kind in ("missing_planned", "wrong_count", "duplicate", "wrong_contract", "delete_required_and_plan", "required_to_optional", "domain_changed"):
            value = copy.deepcopy(original)
            if kind == "missing_planned":
                value["planned_check_names"].append("must_execute")
                value["missing_planned_checks"].append("must_execute")
            elif kind == "wrong_count": value["product"]["required_passed"] = 2
            elif kind == "duplicate": value["checks"].append(value["checks"][0])
            elif kind == "delete_required_and_plan":
                omitted = value["checks"].pop(0)["name"]
                value["planned_check_names"].remove(omitted)
                value["product"]["required_passed"] -= 1
            elif kind in {"required_to_optional", "domain_changed"}:
                selected = next(row for row in value["checks"] if row["name"] == "capture_execution")
                selected["required"] = False
                if kind == "domain_changed": selected["domain"] = "diagnostic"
                value["product"]["required_passed"] -= 1
            else: value["protocol_revision"] = "unknown"
            path.write_text(json.dumps(value))
            with self.subTest(kind=kind): self.assertFalse(TRIAL.product_result(default)["passed"])

    def test_completed_diagnostic_with_incomplete_evidence_does_not_verify_candidate(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        path = clock / "product-checks.json"; value = TRIAL.read(path)
        value["evidence"]["status"] = "incomplete"
        value["checks"][-1]["status"] = "failed"
        value["bounded_pass"] = False
        path.write_text(json.dumps(value))
        args = self.finalize_args(default, clock)
        self.assertEqual(TRIAL.finalize(args), 2)
        result = TRIAL.read(self.package / "VALIDATION.json")
        self.assertTrue(result["checks"]["dual_clock_product"])
        self.assertTrue(result["checks"]["dual_clock_diagnostic_executed"])
        self.assertFalse(result["checks"]["dual_clock_demo_bounded_pass"])
        self.assertFalse(result["candidate_verified"])
        self.assertEqual(result["dual_clock_product"]["required_diagnostic_failed"], 1)

    def test_final_zip_uses_allowlist_not_private_files_dropped_into_package(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        (self.package / "private.db").write_bytes(b"synthetic private sentinel")
        (self.package / "raw-clock.json").write_text("synthetic private sentinel")
        args = self.finalize_args(default, clock)
        self.assertEqual(TRIAL.finalize(args), 0)
        with zipfile.ZipFile(args.output) as archive:
            self.assertNotIn("private.db", archive.namelist())
            self.assertNotIn("raw-clock.json", archive.namelist())

    def test_output_paths_cannot_overwrite_or_escape(self):
        for name in ("../private.db", "/private.db", "C:/private.db", "scripts\\demo.ps1", "scripts/../demo.ps1"):
            with self.subTest(name=name), self.assertRaises(ValueError): TRIAL.relative_path(name)
        self.prepare()
        with self.assertRaisesRegex(ValueError, "output_must_be_new"):
            TRIAL.new_directory(self.package)

    def test_manifest_relative_paths_reject_windows_roots_without_drive(self):
        # Pure path flavours exercise Windows rooted-but-not-absolute spelling
        # on every host. They do not simulate native Windows filesystem access.
        for flavor in (PurePosixPath, PureWindowsPath):
            with mock.patch.object(TRIAL, "Path", flavor):
                for name in ("/private.db", "/", "//server/share/private.db", "C:private.db", "C:/private.db"):
                    with self.subTest(flavor=flavor.__name__, name=name), self.assertRaises(ValueError):
                        TRIAL.relative_path(name)
                self.assertEqual(TRIAL.relative_path("scripts/demo.ps1").parts, ("scripts", "demo.ps1"))

    def test_public_text_replaces_fixture_values_but_rejects_unrelated_paths(self):
        root = r"C:\Users\PRIVATE\Temp\contain-demo-123"
        text = 'Recorded name: "' + root.replace("\\", "\\\\") + '\\output.txt"\n'
        safe = TRIAL.public_text(text, {root: "<fixture-root>"})
        self.assertIn("<fixture-root>", safe)
        self.assertNotIn("PRIVATE", safe)
        for text in (r"C:\Users\OTHER\secret", r"\\server\share", "S-1-5-21-12345"):
            with self.subTest(text=text), self.assertRaisesRegex(ValueError, "private_path"):
                TRIAL.public_text(text, {root: "<fixture-root>"})

    def test_json_requires_one_clean_machine_readable_document(self):
        good = json.dumps(dict(schema_version=3, kind="inspect", data={"id": "synthetic"}))
        self.assertEqual(TRIAL.json_document(good, "inspect"), {"id": "synthetic"})
        for text in ("progress\n" + good, good + "\nexplanation", good + good):
            with self.assertRaises(ValueError): TRIAL.json_document(text, "inspect")

    def test_windows_spelling_only_removes_documented_prefix_not_path_components(self):
        self.assertEqual(TRIAL.windows_spelling(r"\\?\C:\Fixture\Child"), r"c:\fixture\child")
        self.assertNotEqual(TRIAL.windows_spelling(r"C:\Fixture\..\Other"), r"c:\other")

    def test_report_executes_packaged_cli_and_preserves_text_with_explicit_redaction(self):
        # The command responses here are synthetic unit data, never CI evidence.
        manifest = self.prepare(); directory = self.run_evidence("default", manifest)
        root = r"C:\Users\PRIVATE\Temp\contain-demo-" + "d" * 32
        installer = r"\\?\D:\PRIVATE_BUILD\contain-test-installer.exe"
        identity_path = directory / "run-identity.json"; value = TRIAL.read(identity_path)
        value["fixture_root"] = root; identity_path.write_text(json.dumps(value))
        event = dict(id="11111111-1111-1111-1111-111111111111", event_type="file", confidence="High",
            operation="write_requested", resource=root + "\\settings.txt",
            evidence=dict(pid=100, process_creation_time="134350000000000001", source="EtwFile"))
        capture = dict(id="22222222-2222-2222-2222-222222222222", name="TestFixture", installer=installer,
            watch_roots=["\\\\?\\" + root], registry_key="Software\\Contain\\Demo\\" + Path(root.replace("\\", "/")).name,
            backend=dict(etw_process="active", etw_file="active", etw_registry="active"), events=[event],
            processes=[dict(pid=100, creation_time="134350000000000001", image=installer)],
            capture_state="finished", quality=dict(level="Degraded"))
        TRIAL.write(directory / "capture.json", dict(data=capture))
        TRIAL.write(directory / "ground-truth.json", [dict(pid=100, creation_time="134350000000000001")] * 21)
        def command(argv, **kwargs):
            self.assertEqual(argv[0], str(self.package.resolve() / "contain.exe"))
            kind = argv[3]
            if "--json" in argv:
                payload = capture if kind == "inspect" else dict(events=[event]) if kind == "history" else dict(event=event, app_id=capture["id"])
                stdout = json.dumps(dict(schema_version=3, kind=kind, data=payload))
            elif kind == "inspect":
                stdout = "TestFixture\nInstaller: " + installer + "\nWatched directories: \\\\?\\" + root + "\nRecording: finished | Quality: Degraded\nLimitation: C:\\PRIVATE_WARNING\\error\n"
            elif kind == "diff":
                stdout = "STATE DIFFERENCES WITH UNKNOWN ATTRIBUTION\n  FILE modified " + root + "\\settings.txt [Unknown]\n  INVENTORY changed C:\\PRIVATE_MACHINE\\service\n"
            elif kind == "history":
                stdout = "time | event=" + event["id"] + " | write_requested [High] " + root + "\\settings.txt\n"
            else:
                stdout = "Event " + event["id"] + "\nRecorded name/resource: " + json.dumps(root + "\\settings.txt") + "\nExplicit application API intent: unavailable; final state validated: false (not ownership)\nLimitation: operation completion was not observed.\nLimitation: C:\\PRIVATE_WARNING\\details\n"
            return subprocess.CompletedProcess(argv, 0, stdout, "")
        args = argparse.Namespace(package=self.package, capture_directory=directory,
            private_output=self.root / "report-private", public_output=self.root / "report-public", scenario="default")
        with mock.patch.object(TRIAL, "snapshot"), mock.patch.object(TRIAL.subprocess, "run", side_effect=command):
            self.assertEqual(TRIAL.report(args), 0)
        report = (args.public_output / "cli-report.txt").read_text()
        self.assertNotIn("PRIVATE", report)
        self.assertIn("<fixture-root>", report)
        self.assertIn("explain " + event["id"], report)
        self.assertIn("Recording: finished | Quality: Degraded", report)
        self.assertIn("Limitation: operation completion was not observed.", report)
        result = TRIAL.read(args.public_output / "summary.json")
        self.assertEqual(len(result["commands"]), 7)
        self.assertEqual(result["omitted_limitation_lines"], 1)
        self.assertEqual(result["omitted_explain_limitation_lines"], 1)
        self.assertFalse(result["candidate_verified"])
        # A post-command package change invalidates report success too.
        args.private_output = self.root / "changed-private"
        args.public_output = self.root / "changed-public"
        original = TRIAL.verify_package
        checked = 0
        def changed_at_end(package):
            nonlocal checked
            checked += 1
            if checked == 2:
                raise ValueError("package_file_changed")
            return original(package)
        with mock.patch.object(TRIAL, "snapshot"), mock.patch.object(TRIAL.subprocess, "run", side_effect=command), \
                mock.patch.object(TRIAL, "verify_package", side_effect=changed_at_end):
            self.assertEqual(TRIAL.report(args), 2)
        self.assertFalse(TRIAL.read(args.public_output / "summary.json")["complete"])


if __name__ == "__main__":
    unittest.main()
