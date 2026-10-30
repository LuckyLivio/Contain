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
SCORE_SPEC = importlib.util.spec_from_file_location("trial_legacy_score", Path(__file__).with_name("score-fixture.py"))
SCORE = importlib.util.module_from_spec(SCORE_SPEC)
SCORE_SPEC.loader.exec_module(SCORE)


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

    def legacy_score(self, failing=False):
        # Use the unchanged production scorer with synthetic events. Preserve
        # its failure and Unknown rows, never substitute a hand-written verdict.
        resource = r"C:\PRIVATE\fixture\settings.txt"
        event = dict(id="11111111-1111-1111-1111-111111111111", event_type="file",
            operation="write_requested", resource=resource, timestamp_ticks=20,
            confidence="High", evidence=dict(pid=789123, process_creation_time=10))
        truth = [dict(role="parent", operation="write_requested", resource=resource, pid=789123,
            creation_time=10, start_ticks=1, end_ticks=2, success=True)]
        observed = dict(event, id="22222222-2222-2222-2222-222222222222", timestamp_ticks=3)
        truth.append(dict(truth[0], start_ticks=3, end_ticks=4))
        return SCORE.score(dict(events=[event, observed] if failing else [observed], watch_roots=[r"C:\PRIVATE\fixture"],
            stats=dict(events_dropped=0), backend=dict(etw_events_lost=None, etw_buffers_lost=None),
            quality=dict(level="Degraded")), truth if failing else truth[1:])

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
        TRIAL.write(directory / "reliability.json", self.legacy_score())
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

    def test_cli_report_failure_still_preserves_original_score_without_private_fields(self):
        manifest = self.prepare(); directory = self.run_evidence("default", manifest)
        (directory / "reliability.json").write_text(json.dumps(self.legacy_score(failing=True)))
        args = argparse.Namespace(package=self.package, capture_directory=directory,
            private_output=self.root / "report-private", public_output=self.root / "report-public", scenario="default")
        # Capture is deliberately missing: independent legacy evidence survives.
        self.assertEqual(TRIAL.report(args), 2)
        summary = TRIAL.read(args.public_output / "summary.json")
        original = TRIAL.read(directory / "reliability.json")
        score = summary["legacy_score"]
        self.assertEqual(score["status"], "available")
        self.assertEqual(score["score"]["false_positive_target_events"], 1)
        self.assertEqual(score["score"]["unexpected_positive_events"], original["unexpected_positive_events"])
        self.assertEqual(score["score"]["rows"][0]["observation"], "unobserved")
        self.assertIsNone(score["score"]["rows"][0]["matched_event_id"])
        self.assertEqual(score["score"]["rows"][1]["matched_event_id"], "22222222-2222-2222-2222-222222222222")
        self.assertEqual(score["score"]["rows"][1]["reason_codes"], ["observed", "correctly_attributed"])
        self.assertIsNone(score["score"]["etw_events_lost"])
        self.assertEqual(score["source_sha256"], TRIAL.sha(directory / "reliability.json"))
        text = json.dumps(summary)
        for private in ("PRIVATE", "789123", "resource", "definition"):
            self.assertNotIn(private, text)
        self.assertFalse(summary["complete"])

    def test_missing_legacy_score_cannot_be_counted_as_verified(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        (clock / "reliability.json").write_text(json.dumps(self.legacy_score(failing=True)))
        (default / "reliability.json").unlink()
        self.assertEqual(TRIAL.finalize(self.finalize_args(default, clock)), 2)
        result = TRIAL.read(self.package / "VALIDATION.json")
        self.assertFalse(result["candidate_verified"])
        self.assertEqual(result["legacy_scores"]["default"]["status"], "unavailable")
        self.assertIsNone(result["legacy_scores"]["default"]["score"])
        self.assertEqual(result["legacy_scores"]["dual-clock"]["score"]["false_positive_target_events"], 1)

    def test_invalid_legacy_score_fields_never_publish_arbitrary_text_or_claim_complete(self):
        directory = self.root / "score-only"; directory.mkdir()
        score = self.legacy_score(failing=True)
        score["rows"][0].update(role=r"C:\PRIVATE\role", matched_event_id=r"C:\PRIVATE\event", reason="PRIVATE_REASON")
        score["unexpected_positive_events"].append(r"C:\PRIVATE\extra")
        score["etw_events_lost"] = "PRIVATE_LOSS"
        TRIAL.write(directory / "reliability.json", score)
        public = TRIAL.legacy_score_result(directory)
        self.assertEqual(public["status"], "invalid")
        self.assertEqual(len(public["score"]["unexpected_positive_events"]), 2)
        self.assertEqual(public["score"]["unexpected_positive_events"][0], score["unexpected_positive_events"][0])
        self.assertIsNone(public["score"]["unexpected_positive_events"][1])
        self.assertNotIn("PRIVATE", json.dumps(public))
        self.assertTrue(public["failure_reasons"])

    def reports(self):
        for name in ("default", "dual-clock"):
            root = self.package / "examples" / name; root.mkdir(parents=True)
            (root / "cli-report.txt").write_text("Synthetic test report; not ETW\n")
            TRIAL.write(root / "summary.json", dict(schema_version=1, protocol_revision=TRIAL.PROTOCOL,
                scenario=name, scope="real_etw_controlled_fixture_cli_excerpt", source_sha=self.source, complete=True, report_generated=True, acceptance_passed=True, failures=[], comparison=dict(json_equal=True), event_id="synthetic-event",
                exporter=TRIAL.exporter_identity(), commands=[dict(name=name, exit_code=0) for name in
                    ("inspect-json", "history-json", "explain-json", "inspect-text", "diff-text", "history-text", "explain-text")],
                checks={name: True for name in ("fixture_directory_identity", "reopened_scope_identity", "reopen_json_equal", "history_page_equal",
                    "explain_event_equal", "json_stdout_only", "all_seven_commands_executed", "text_json_agree",
                    "fixture_diff_present", "public_paths_redacted")},
                report_sha256=TRIAL.sha(root / "cli-report.txt")))

    def finalize_args(self, default, clock):
        analysis = self.root / "analysis.json"
        TRIAL.write(analysis, dict(source_sha=self.source, diagnostic_evidence_complete=True, missing_evidence=[]))
        quality = self.root / "quality.json"
        TRIAL.write(quality, dict(source_revision=self.source, current_required_gates_passed=True))
        # Synthetic metadata unit inputs only. Native evidence is produced by
        # the Windows scripts, never these tests.
        preflight = self.root / "preflight"; preflight.mkdir()
        commands = [dict(name=name, exit_code=0) for name in ("inspect-json", "history-json", "explain-json",
                    "inspect-text", "diff-text", "history-text", "explain-text")]
        TRIAL.write(preflight / "summary.json", dict(schema_version=1,
            protocol_revision="contain-synthetic-report-preflight-2", scope="synthetic_windows_cli_report_preflight",
            synthetic=True, native_capture_executed=False, native_windows=True, passed=True, complete=True,
            collection_verified=True, negative_verified=True, save_commands_executed=3,
            negative_report_commands_executed=7,
            save_implementation_sha256=TRIAL.sha(self.package / "scripts/demo-save-capture.ps1"),
            negative_case=dict(case="changed-event-id", status="passed", report_exit_code=2, commands=commands,
                checks={name: True for name in ("real_contain_exe", "shared_demo_save", "strict_mismatch_rejected",
                    "exact_field_difference", "independent_commands_completed", "report_marked_inconsistent",
                    "production_collection", "production_candidate_report_gate_rejected")},
                comparison=dict(json_equal=False, difference_count=1, differences=[dict(path="/data/events/0/id")])),
            source_sha=self.source, build_manifest_sha256=TRIAL.sha(self.package / "BUILD.json"),
            contain_sha256=TRIAL.sha(self.package / "contain.exe"), exporter=TRIAL.exporter_identity(),
            preflight_sha256=TRIAL.sha(Path(TRIAL.__file__).with_name("verify-trial-report.py")),
            windows_8dot3=dict(status="not_covered"), commands_executed=14, report_commands_executed=14,
            commands_failed=0, commands_not_run=0, cases=[dict(case=name, status="passed", commands=commands,
                checks={check: True for check in ("real_contain_exe", "all_seven_commands", "reopened_json_equal",
                    "same_event_paged_and_explained", "historical_child_absent", "unknown_preserved",
                    "all_four_sections", "private_paths_redacted", "production_collection", "shared_demo_save",
                    "serialization_sensitive_values", "save_raw_text_preserved")}) for name in ("normal", "extended")]))
        paths = self.root / "report-paths.json"
        TRIAL.write(paths, dict(schema_version=1, scope="synthetic_native_windows_directory_identity",
            native_windows=True, passed=True, exporter=TRIAL.exporter_identity(),
            test_script_sha256=TRIAL.sha(Path(TRIAL.__file__).with_name("test-report-paths.py")),
            tests_run=7, passed_count=6, failed_count=0, skipped_count=1, short_alias=dict(status="unavailable")))
        return argparse.Namespace(package=self.package, default_directory=default, clock_directory=clock,
            clock_analysis=analysis, quality_summary=quality, report_preflight=preflight,
            report_paths=paths, output=self.root / "trial.zip")

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
        for name in ("VCRUNTIME140D.dll", "unrecognized-library.dll"):
            with self.subTest(dependency=name):
                self.package = self.root / ("package-" + name)
                self.contain.write_bytes(synthetic_pe(imports=(name,)))
                with self.assertRaisesRegex(ValueError, "unprovided_non_system_runtime_dependency"):
                    self.prepare()

    def test_windows_etw_and_performance_system_imports_need_no_redist(self):
        # Locked ferrisetw and sysinfo bindings link TDH and PDH respectively.
        # This synthetic PE verifies packaging policy, not native DLL loading.
        self.contain.write_bytes(synthetic_pe(imports=("TDH.dll",), delayed=("PDH.dll",)))
        manifest = self.prepare()
        self.assertEqual(manifest["pe_dependencies"]["contain.exe"]["imports"], ["tdh.dll"])
        self.assertEqual(manifest["pe_dependencies"]["contain.exe"]["delay_imports"], ["pdh.dll"])
        self.assertEqual(manifest["runtime_files"], [])
        self.assertFalse((self.package / "tdh.dll").exists())
        self.assertFalse((self.package / "pdh.dll").exists())

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

    def test_missing_failed_or_changed_preflights_cannot_verify_candidate(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        args = self.finalize_args(default, clock)
        native = TRIAL.read(args.report_paths); preflight = TRIAL.read(args.report_preflight / "summary.json")
        cases = ("native_missing", "native_failed", "native_exporter", "native_zero_tests",
                 "preflight_missing", "preflight_failed", "preflight_source", "preflight_binary",
                 "preflight_commands", "preflight_synthetic_label", "negative_missing", "negative_commands", "negative_empty_differences", "save_missing")
        for case in cases:
            native_value, preflight_value = copy.deepcopy(native), copy.deepcopy(preflight)
            if case == "native_missing": native_value = {}
            elif case == "native_failed": native_value["passed"] = False
            elif case == "native_exporter": native_value["exporter"]["sha256"] = "0" * 64
            elif case == "native_zero_tests": native_value["tests_run"] = 0
            elif case == "preflight_missing": preflight_value = {}
            elif case == "preflight_failed": preflight_value["passed"] = False
            elif case == "preflight_source": preflight_value["source_sha"] = "0" * 40
            elif case == "preflight_binary": preflight_value["contain_sha256"] = "0" * 64
            elif case == "preflight_commands": preflight_value["cases"][0]["commands"].pop()
            elif case == "negative_missing": preflight_value.pop("negative_case")
            elif case == "negative_commands": preflight_value["negative_case"]["commands"].pop()
            elif case == "negative_empty_differences": preflight_value["negative_case"]["comparison"]["differences"] = []
            elif case == "save_missing": preflight_value["save_commands_executed"] = 0
            else: preflight_value["synthetic"] = False
            args.report_paths.write_text(json.dumps(native_value))
            (args.report_preflight / "summary.json").write_text(json.dumps(preflight_value))
            args.output = self.root / (case + ".zip")
            with self.subTest(case=case):
                self.assertEqual(TRIAL.finalize(args), 2)
                self.assertFalse(TRIAL.read(self.package / "VALIDATION.json")["candidate_verified"])
            (self.package / "VALIDATION.json").unlink(); (self.package / "CONTENTS.json").unlink()

    def test_synthetic_or_different_exporter_report_cannot_satisfy_real_candidate(self):
        manifest = self.prepare(); default = self.run_evidence("default", manifest)
        clock = self.run_evidence("clock", manifest, clock=True); self.reports()
        args = self.finalize_args(default, clock)
        path = self.package / "examples/default/summary.json"
        original = TRIAL.read(path)
        for kind in ("synthetic", "changed_exporter", "not_all_commands", "missing_checks"):
            value = copy.deepcopy(original)
            if kind == "synthetic": value["scope"] = "synthetic_windows_cli_report"
            elif kind == "changed_exporter": value["exporter"]["sha256"] = "0" * 64
            elif kind == "not_all_commands": value["commands"].pop()
            else: value["checks"]["fixture_diff_present"] = False
            path.write_text(json.dumps(value)); args.output = self.root / (kind + ".zip")
            with self.subTest(kind=kind):
                self.assertEqual(TRIAL.finalize(args), 2)
                self.assertFalse(TRIAL.read(self.package / "VALIDATION.json")["checks"]["default_actual_cli_report"])
            (self.package / "VALIDATION.json").unlink(); (self.package / "CONTENTS.json").unlink()

    def test_shared_collector_keeps_only_approved_synthetic_report_pair(self):
        self.prepare(); self.reports()
        source = self.package / "examples/default"
        value = TRIAL.read(source / "summary.json")
        value.update(scenario="synthetic", scope="synthetic_windows_cli_report")
        (source / "summary.json").write_text(json.dumps(value))
        (source / "private.db").write_text("PRIVATE sentinel")
        collected = TRIAL.collect_report(self.package, "synthetic", source)
        self.assertEqual({path.name for path in collected.iterdir()}, {"summary.json", "cli-report.txt"})
        with self.assertRaisesRegex(ValueError, "report_collection_contract_failed"):
            TRIAL.collect_report(self.package, "dual-clock", source)

    def test_report_requires_frozen_exporter_and_keeps_failure_context(self):
        manifest = self.prepare(); directory = self.run_evidence("default", manifest)
        path = self.package / "BUILD.json"; manifest["report_exporter"]["sha256"] = "0" * 64
        path.write_text(json.dumps(manifest))
        args = argparse.Namespace(package=self.package, capture_directory=directory,
            private_output=self.root / "private-report", public_output=self.root / "public-report", scenario="default")
        self.assertEqual(TRIAL.report(args), 2)
        summary = TRIAL.read(args.public_output / "summary.json")
        self.assertEqual(summary["failure"], "report_exporter_not_frozen")
        self.assertEqual(summary["commands_executed"], 0)
        self.assertEqual(summary["commands_not_run"], 7)
        self.assertTrue((args.private_output / "failure-context.json").is_file())

    def test_failed_scope_diagnosis_still_executes_independent_same_database_readback(self):
        manifest = self.prepare(); directory = self.run_evidence("default", manifest)
        path = directory / "run-identity.json"; observed = TRIAL.read(path)
        observed["fixture_root"] = str(self.root / "missing"); path.write_text(json.dumps(observed))
        TRIAL.write(directory / "capture.json", dict(data=dict(watch_roots=[observed["fixture_root"]], registry_key="wrong")))
        args = argparse.Namespace(package=self.package, capture_directory=directory,
            private_output=self.root / "diagnostic-private", public_output=self.root / "diagnostic-public", scenario="default")
        def command(argv, **kwargs):
            kind = argv[3]
            data = dict(events=[dict(id="synthetic-event")]) if kind == "inspect" else {}
            text = json.dumps(dict(schema_version=3, kind=kind, data=data)) if "--json" in argv else "PRIVATE details"
            return subprocess.CompletedProcess(argv, 0, text, "PRIVATE stderr")
        with mock.patch.object(TRIAL, "snapshot"), mock.patch.object(TRIAL.subprocess, "run", side_effect=command):
            self.assertEqual(TRIAL.diagnose_report(args), 0)
        summary = TRIAL.read(args.public_output / "summary.json")
        self.assertEqual(summary["commands_executed"], 4)
        self.assertFalse(summary["directory_identity_verified"])
        self.assertFalse(summary["candidate_verified"])
        self.assertTrue(summary["original_report_unchanged"])
        self.assertIn("fixture_directory_identity_unavailable", summary["failures"])
        self.assertNotIn("PRIVATE", json.dumps(summary))

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
        for suffix in ("-other\\private.txt", " backup\\private.txt", "(other)\\private.txt", "'backup\\private.txt",
                       "\\..\\outside.txt", "\\folder\\..\\outside.txt", "\\sub dir\\..\\outside.txt"):
            with self.subTest(suffix=suffix), self.assertRaises(ValueError):
                TRIAL.public_text(root + suffix, {root: "<fixture-root>"})
            with self.subTest(json_suffix=suffix), self.assertRaises(ValueError):
                TRIAL.public_text(json.dumps(root + suffix), {root: "<fixture-root>"})

    def test_comparison_locates_strict_changes_and_redacts_unknown_keys(self):
        before = dict(schema_version=3, kind="inspect", data=dict(events=[dict(id="one", timestamp="2026-07-16T09:10:20.120+00:00")],
            stats=dict(events_retained=9007199254740993), warnings=[None], private_mapping={"C:\\PRIVATE\\name": "secret"}))
        after = copy.deepcopy(before)
        after["data"]["events"][0].update(id="two", timestamp="2026-07-16T09:10:20.12+00:00")
        after["data"]["stats"]["events_retained"] += 1
        after["data"]["warnings"] = ["changed", False]
        after["data"]["private_mapping"] = {"SECRET_KEY": "other"}
        public, private = TRIAL.compare_json_documents(before, after)
        self.assertFalse(public["json_equal"])
        paths = {row["path"] for row in public["differences"]}
        self.assertIn("/data/events/0/id", paths)
        self.assertIn("/data/events/0/timestamp", paths)
        self.assertIn("/data/stats/events_retained", paths)
        self.assertIn("/data/warnings/0", paths)
        self.assertEqual(public["counts"]["array_length_changed"], 1)
        self.assertEqual(public["counts"]["type_changed"], 1)
        self.assertEqual(public["counts"]["field_added"], 1)
        self.assertEqual(public["counts"]["field_missing"], 1)
        self.assertNotIn("PRIVATE", json.dumps(public)); self.assertNotIn("SECRET_KEY", json.dumps(public))
        self.assertIn("PRIVATE", json.dumps(private)); self.assertIn("SECRET_KEY", json.dumps(private))
        hidden, _ = TRIAL.compare_json_documents({"data": {"private_mapping": {"id": "old"}}}, {"data": {"private_mapping": {"id": "new"}}})
        self.assertEqual(hidden["differences"][0]["path"], "/data/<unreviewed-key>/<unreviewed-key>")
        self.assertFalse(TRIAL.compare_json_documents({"data": 1}, {"data": True})[0]["json_equal"])
        self.assertFalse(TRIAL.compare_json_documents({"data": 1}, {"data": 1.0})[0]["json_equal"])

    def test_comparison_records_byte_only_difference_and_full_truncation_count(self):
        left = self.root / "left.json"; right = self.root / "right.json"
        left.write_bytes(b'{"data": [1, null]}\n'); right.write_bytes(b'{ "data": [1,null] }\r\n')
        database = self.root / "PRIVATE-database.sqlite"; database.write_bytes(b"synthetic database identity")
        public, private = TRIAL.compare_capture_files(left, right, dict(source_sha=self.source), self.package, database)
        self.assertTrue(public["json_equal"]); self.assertFalse(public["bytes_equal"])
        self.assertEqual(public["difference_count"], 0)
        # Compare path values structurally: JSON escapes Windows backslashes.
        # Both literal and JSON-escaped private roots must stay out of public output.
        public_text = json.dumps(public)
        self.assertNotIn(str(self.root), public_text)
        self.assertNotIn(json.dumps(str(self.root))[1:-1], public_text)
        self.assertEqual(private["inputs"][0]["path"], str(left.absolute()))
        self.assertEqual(private["inputs"][1]["path"], str(right.absolute()))
        self.assertEqual(private["database_snapshot"]["path"], str(database.absolute()))
        self.assertNotIn("PRIVATE", json.dumps(public))
        public, private = TRIAL.compare_json_documents(list(range(8)), [None] * 8, limit=3)
        self.assertEqual(public["difference_count"], 8)
        self.assertEqual(len(public["differences"]), 3)
        self.assertTrue(public["truncated"])

    def test_json_requires_one_clean_machine_readable_document(self):
        good = json.dumps(dict(schema_version=3, kind="inspect", data={"id": "synthetic"}))
        self.assertEqual(TRIAL.json_document(good, "inspect"), {"id": "synthetic"})
        for text in ("progress\n" + good, good + "\nexplanation", good + good):
            with self.assertRaises(ValueError): TRIAL.json_document(text, "inspect")

    def test_windows_spelling_only_removes_documented_prefix_not_path_components(self):
        self.assertEqual(TRIAL.windows_spelling(r"\\?\C:\Fixture\Child"), r"c:\fixture\child")
        self.assertNotEqual(TRIAL.windows_spelling(r"C:\Fixture\..\Other"), r"c:\other")

    def fixture_scope(self):
        root = self.root / ("contain-demo-" + "d" * 32)
        root.mkdir()
        (root / ".contain-demo-marker").write_bytes(b"Contain test fixture")
        return root, "Software\\Contain\\Demo\\" + root.name

    def test_existing_directory_identity_accepts_verified_alias_through_all_report_stages(self):
        root, key = self.fixture_scope()
        observed = str(root.parent / "unused" / ".." / root.name)
        (root.parent / "unused").mkdir()
        self.assertNotEqual(TRIAL.windows_spelling(observed), TRIAL.windows_spelling(str(root)))
        self.assertTrue(Path(observed).samefile(root))
        scope = TRIAL.fixture_scope(observed, [str(root)], key)
        # Historical/deleted children must not be resolved on today's disk.
        for alias in (observed, str(root)):
            resource = alias + "\\deleted-historical.txt"
            self.assertTrue(scope.contains_file(resource))
            self.assertFalse(scope.contains_file(alias + "-other\\private.txt"))
            self.assertFalse(scope.contains_file(alias + "\\..\\private.txt"))
            line = "  FILE Modified " + resource + " [Unknown]"
            self.assertTrue(scope.includes_diff(line))
            clean = TRIAL.public_text(line + "\n" + json.dumps(resource), scope.replacements())
            self.assertNotIn(str(root.parent), clean)
            self.assertIn("<fixture-root>", clean)
        self.assertFalse(scope.includes_diff("  FILE Modified C:\\OTHER\\private [Unknown]"))

    def test_scope_rejects_missing_different_wrong_guid_marker_and_unknown_identity(self):
        root, key = self.fixture_scope()
        other = self.root / ("contain-demo-" + "e" * 32); other.mkdir()
        (other / ".contain-demo-marker").write_bytes(b"Contain test fixture")
        for observed, captured, registry in ((str(root), str(other), key),
                (str(root), str(self.root / "missing"), key),
                (str(root), str(root), key[:-1] + "f")):
            with self.subTest(kind=registry), self.assertRaises(ValueError):
                TRIAL.fixture_scope(observed, [captured], registry)
        (root / ".contain-demo-marker").write_bytes(b"wrong marker")
        with self.assertRaises(ValueError): TRIAL.fixture_scope(str(root), [str(root)], key)
        (root / ".contain-demo-marker").write_bytes(b"Contain test fixture")
        with mock.patch.object(TRIAL, "directory_identity", return_value=None), self.assertRaises(ValueError):
            TRIAL.fixture_scope(str(root), [str(root)], key)

    def test_root_alias_redaction_covers_literal_and_json_extended_spellings(self):
        root = r"C:\Users\PRIVATE\Temp\contain-demo-" + "a" * 32
        scope = TRIAL.FixtureScope(root, [root, r"C:\Users\PRIVAT~1\Temp\CONTAl~1"],
                                   "Software\\Contain\\Demo\\" + "contain-demo-" + "a" * 32)
        for alias in scope.aliases:
            for prefix in ("", "\\\\?\\", "\\??\\"):
                path = prefix + alias + "\\old.txt"
                for text in (path, json.dumps(path)):
                    with self.subTest(text=text):
                        clean = TRIAL.public_text(text, scope.replacements())
                        self.assertIn("<fixture-root>", clean)
                        self.assertNotIn("PRIVATE", clean)
                        self.assertNotIn("PRIVAT~1", clean)

    def test_report_executes_packaged_cli_and_preserves_text_with_explicit_redaction(self):
        # The command responses here are synthetic unit data, never CI evidence.
        manifest = self.prepare(); directory = self.run_evidence("default", manifest)
        real_root, registry = self.fixture_scope()
        root = str(real_root)
        (self.root / "unused").mkdir()
        observed_alias = str(self.root / "unused" / ".." / real_root.name)
        self.assertTrue(Path(observed_alias).samefile(real_root))
        self.assertNotEqual(TRIAL.windows_spelling(observed_alias), TRIAL.windows_spelling(root))
        installer = r"\\?\D:\PRIVATE_BUILD\contain-test-installer.exe"
        identity_path = directory / "run-identity.json"; value = TRIAL.read(identity_path)
        value["fixture_root"] = observed_alias; identity_path.write_text(json.dumps(value))
        event = dict(id="11111111-1111-1111-1111-111111111111", event_type="file", confidence="High",
            operation="write_requested", resource=root + "\\settings.txt",
            evidence=dict(pid=100, process_creation_time="134350000000000001", source="EtwFile"))
        capture = dict(id="22222222-2222-2222-2222-222222222222", name="TestFixture", installer=installer,
            watch_roots=[root], registry_key=registry,
            backend=dict(etw_process="active", etw_file="active", etw_registry="active"), events=[event],
            processes=[dict(pid=100, creation_time="134350000000000001", image=installer)],
            capture_state="finished", quality=dict(level="Degraded"))
        TRIAL.write(directory / "capture.json", dict(schema_version=3, kind="inspect", data=capture))
        TRIAL.write(directory / "ground-truth.json", [dict(pid=100, creation_time="134350000000000001")] * 21)
        def command(argv, **kwargs):
            self.assertEqual(argv[0], str(self.package.resolve() / "contain.exe"))
            kind = argv[3]
            if "--json" in argv:
                payload = capture if kind == "inspect" else dict(events=[event]) if kind == "history" else dict(event=event, app_id=capture["id"])
                stdout = json.dumps(dict(schema_version=3, kind=kind, data=payload))
            elif kind == "inspect":
                stdout = "TestFixture\nInstaller: " + installer + "\nWatched directories: " + root + "\nRecording: finished | Quality: Degraded\nLimitation: C:\\PRIVATE_WARNING\\error\n"
            elif kind == "diff":
                stdout = "STATE DIFFERENCES WITH UNKNOWN ATTRIBUTION\n  FILE modified " + observed_alias + "\\settings.txt [Unknown]\n  INVENTORY changed C:\\PRIVATE_MACHINE\\service\n"
            elif kind == "history":
                stdout = "time | event=" + event["id"] + " | write_requested [High] " + root + "\\settings.txt\n"
            else:
                stdout = "Event " + event["id"] + "\nSource operation: write_requested\nRecorded name/resource: " + json.dumps(root + "\\settings.txt") + "\nExplicit application API intent: unavailable; final state validated: false (not ownership)\nLimitation: operation completion was not observed.\nLimitation: C:\\PRIVATE_WARNING\\details\n"
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
        # Mutate only the saved capture: selection must use the actual reopened
        # event, every independent command still runs, and acceptance stays false.
        saved = copy.deepcopy(capture); saved["events"][0]["id"] = "changed-saved-id"
        (directory / "capture.json").write_text(json.dumps(dict(schema_version=3, kind="inspect", data=saved)))
        args.private_output = self.root / "mismatch-private"
        args.public_output = self.root / "mismatch-public"
        with mock.patch.object(TRIAL, "snapshot"), mock.patch.object(TRIAL.subprocess, "run", side_effect=command):
            self.assertEqual(TRIAL.report(args), 2)
        failed = TRIAL.read(args.public_output / "summary.json")
        self.assertFalse(failed["complete"]); self.assertFalse(failed["acceptance_passed"])
        self.assertTrue(failed["report_generated"]); self.assertEqual(len(failed["commands"]), 7)
        self.assertEqual(failed["event_id"], event["id"])
        self.assertEqual(failed["comparison"]["differences"][0]["path"], "/data/events/0/id")
        self.assertIn("CONSISTENCY CHECK FAILED", (args.public_output / "cli-report.txt").read_text())
        self.assertFalse(TRIAL.report_matches(failed, manifest, "default", args.public_output / "cli-report.txt"))
        collected = TRIAL.collect_report(self.package, "default", args.public_output)
        self.assertTrue((collected / "cli-report.txt").is_file())
        self.assertFalse(TRIAL.read(collected / "summary.json")["complete"])
        (directory / "capture.json").write_text(json.dumps(dict(schema_version=3, kind="inspect", data=capture)))
        # A JSON view conflict is kept private, while independent text commands
        # execute and the public report marks that event section unverified.
        def wrong_explanation(argv, **kwargs):
            result = command(argv, **kwargs)
            if argv[3] == "explain" and "--json" in argv:
                doc = json.loads(result.stdout); doc["data"]["event"]["resource"] = "C:\\PRIVATE\\other"
                result.stdout = json.dumps(doc)
            return result
        args.private_output = self.root / "conflict-private"; args.public_output = self.root / "conflict-public"
        with mock.patch.object(TRIAL, "snapshot"), mock.patch.object(TRIAL.subprocess, "run", side_effect=wrong_explanation):
            self.assertEqual(TRIAL.report(args), 2)
        conflict = TRIAL.read(args.public_output / "summary.json")
        self.assertEqual(conflict["commands_executed"], 7); self.assertTrue(conflict["report_generated"])
        self.assertFalse(conflict["checks"]["explain_event_equal"])
        public = (args.public_output / "cli-report.txt").read_text()
        self.assertIn("NOT PUBLISHED", public); self.assertNotIn("PRIVATE", public)
        # No verified event does not skip independent inspect/diff or fabricate
        # event identity. Four dependent commands are explicitly not run.
        original_confidence = event["confidence"]; event["confidence"] = "Unknown"
        (directory / "capture.json").write_text(json.dumps(dict(schema_version=3, kind="inspect", data=capture)))
        args.private_output = self.root / "unknown-private"; args.public_output = self.root / "unknown-public"
        with mock.patch.object(TRIAL, "snapshot"), mock.patch.object(TRIAL.subprocess, "run", side_effect=command):
            self.assertEqual(TRIAL.report(args), 2)
        unknown = TRIAL.read(args.public_output / "summary.json")
        self.assertEqual(unknown["commands_executed"], 3); self.assertEqual(unknown["commands_not_run"], 4)
        self.assertTrue(unknown["report_generated"]); self.assertIsNone(unknown["event_id"])
        event["confidence"] = original_confidence
        (directory / "capture.json").write_text(json.dumps(dict(schema_version=3, kind="inspect", data=capture)))
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
