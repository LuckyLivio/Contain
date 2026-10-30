import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("quality_summary", Path(__file__).with_name("quality-summary.py"))
SUMMARY = importlib.util.module_from_spec(spec)
spec.loader.exec_module(SUMMARY)


class SummaryTests(unittest.TestCase):
    def test_cargo_groups_keep_zero_targets_ignored_and_failures_separate(self):
        result = SUMMARY.counts("test result: ok. 62 passed; 0 failed; 8 ignored; 0 measured; 0 filtered out\n"
                                "test result: FAILED. 1 passed; 2 failed; 0 ignored; 0 measured; 5 filtered out\n"
                                "test result: ok. 0 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out")
        self.assertEqual(len(result["groups"]), 3)
        self.assertEqual(result["groups"][0]["ignored"], 8)
        self.assertEqual(result["groups"][1]["failed"], 2)
        self.assertEqual(result["groups"][2]["passed"], 0)

    def test_nextest_summary_keeps_skips_and_failures(self):
        result = SUMMARY.counts("Summary [  3.012s] 286 tests run: 285 passed, 1 failed, 7 skipped")
        self.assertEqual(result["executed"], 286)
        self.assertEqual(result["passed"], 285)
        self.assertEqual(result["failed"], 1)
        self.assertEqual(result["skipped"], 7)
        self.assertIsNone(SUMMARY.counts("Starting 286 tests across 9 binaries\n"))

    def test_unittest_errors_expected_failures_and_skips_are_not_passes(self):
        result = SUMMARY.counts("Ran 10 tests in 0.1s\nFAILED (failures=1, errors=2, skipped=1, expected failures=1, unexpected successes=1)\n")
        self.assertEqual(result["passed"], 4)
        self.assertEqual(result["failed"], 1)
        self.assertEqual(result["errors"], 2)
        self.assertEqual(result["expected_failures"], 1)
        self.assertIsNone(SUMMARY.counts("Ran 10 tests in 0.1s\ninterrupted")["passed"])

    def test_expected_failure_is_not_double_counted_as_failure(self):
        result = SUMMARY.counts("Ran 2 tests in 0.1s\nOK (expected failures=1)\n")
        self.assertEqual((result["passed"], result["failed"], result["expected_failures"]), (1, 0, 1))

    def test_colorized_nextest_pass_and_ignored_summary(self):
        result = SUMMARY.counts("\x1b[32mSummary\x1b[0m [ 1.0s] 10 tests run: \x1b[32m10 passed\x1b[0m (1 slow), 3 skipped")
        self.assertEqual((result["executed"], result["passed"], result["failed"], result["skipped"]), (10, 10, 0, 3))

    def test_windows_source_aliases_are_normalized_without_private_roots(self):
        root = "D:/a/Contain/Contain"
        source = r"D:\a\Contain\Contain\tools\offline\src\..\..\..\crates\contain-core\src\storage.rs"
        path = SUMMARY.source_path(source, root)
        self.assertEqual(path[0], "crates/contain-core/src/storage.rs")
        self.assertTrue(path[1].startswith("tools/offline/"))
        self.assertIsNone(SUMMARY.source_path(r"C:\Users\private\capture.db", root))
        self.assertIsNone(SUMMARY.source_path(root + "/../../private.rs", root))

    def test_public_report_keeps_failures_but_never_log_bodies_or_private_arguments(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            quality = root / "target/quality"
            quality.mkdir(parents=True)
            (root / "scripts").mkdir()
            (root / "scripts/test-required.py").write_text("", encoding="utf-8")
            (quality / "failed.log").write_text("credential=SECRET_VALUE\nC:/Users/private/file\nRan 3 tests in 0.1s\nFAILED (errors=1)\n", encoding="utf-8")
            manifest = dict(source_revision="a" * 40, tools={"rustc": "rustc 1.99.1 (58cf8f9c3 2026-10-13)", "python": "C:/private/secret"}, gates=[dict(mode="Check", label="test-required", program="python", arguments=[str(root / "scripts/test-required.py"), "SECRET_VALUE"], exit_code=1, log="failed.log")])
            (quality / "gates.json").write_text(json.dumps(manifest), encoding="utf-8")
            report = SUMMARY.build(root, quality)
            self.assertEqual(report["gates"][0]["status"], "failed")
            self.assertEqual(report["gates"][0]["counts"]["passed"], 2)
            self.assertFalse(report["all_recorded_gates_passed"])
            self.assertFalse(report["required_gates_complete"])
            self.assertIsNone(report["tools"]["python"])
            serialized = json.dumps(report)
            for private in ("SECRET_VALUE", "C:/Users", str(root)):
                self.assertNotIn(private, serialized)

    def test_repeated_failed_attempt_is_not_replaced_by_later_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "failure.log").write_text("", encoding="utf-8")
            (root / "success.log").write_text("", encoding="utf-8")
            gates = [dict(mode="Check", label="fmt", program="cargo", arguments=["fmt"], exit_code=code, log=log)
                     for code, log in [(1, "failure.log"), (0, "success.log")]]
            (root / "gates.json").write_text(json.dumps(dict(source_revision="a" * 40, gates=gates)), encoding="utf-8")
            result = SUMMARY.build(root, root)
            self.assertEqual([g["attempt"] for g in result["gates"]], [1, 2])
            self.assertFalse(result["all_recorded_gates_passed"])

    def test_log_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "gates.json").write_text(json.dumps(dict(source_revision="a" * 40, gates=[dict(mode="Check", label="fmt", log="../private.log", exit_code=0)])), encoding="utf-8")
            with self.assertRaises(ValueError):
                SUMMARY.build(root, root)

    def test_old_source_gates_do_not_complete_current_revision(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "spool.log").write_text("test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 50 filtered out", encoding="utf-8")
            invocation = dict(mode="Companion", invocation_id="one", source_revision="b" * 40, tools={"rustc": "rustc 1.99.0"}, completed=True, passed=True)
            gate = dict(mode="Companion", label="companion-spool", invocation_id="one", exit_code=0, log="spool.log")
            manifest = dict(source_revision="a" * 40, invocations=[invocation], gates=[gate])
            (root / "gates.json").write_text(json.dumps(manifest), encoding="utf-8")
            result = SUMMARY.build(root, root)
            self.assertEqual(result["gates"][0]["source_revision"], "b" * 40)
            self.assertEqual(result["gates"][0]["invocation"], 1)
            self.assertEqual(result["invocations"][0]["tools"]["rustc"], "rustc 1.99.0")
            self.assertEqual(result["missing_gates"]["Companion"], ["companion-spool"])
            self.assertTrue(result["all_recorded_invocations_passed"])

    def test_latest_invocation_does_not_borrow_old_gates_or_junit(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "old.log").write_text("", encoding="utf-8")
            (root / "new.log").write_text("", encoding="utf-8")
            (root / "junit.xml").write_text("<testsuites><testsuite><testcase/></testsuite></testsuites>", encoding="utf-8")
            invocations = [dict(mode="Check", invocation_id=key, source_revision="a" * 40, completed=True, passed=passed) for key, passed in (("old", True), ("new", False))]
            gates = [dict(mode="Check", label=label, invocation_id=key, exit_code=code, log=key + ".log") for key, label, code in (("old", "nextest", 0), ("new", "fmt", 1))]
            (root / "gates.json").write_text(json.dumps(dict(source_revision="a" * 40, invocations=invocations, gates=gates)), encoding="utf-8")
            result = SUMMARY.build(root, root)
            self.assertIn("nextest", result["missing_gates"]["Check"])
            self.assertIsNone(result["nextest_junit"])
            self.assertFalse(result["current_required_gates_passed"])

    def test_coverage_and_junit_are_bound_to_their_producing_invocation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for key in ("check", "coverage"):
                (root / key).mkdir()
                (root / key / "gate.log").write_text("", encoding="utf-8")
            (root / "check/junit.xml").write_text("<testsuites><testsuite><testcase/></testsuite></testsuites>", encoding="utf-8")
            (root / "coverage.json").write_text(json.dumps(dict(data=[dict(totals={})])), encoding="utf-8")
            invocations = [dict(mode=mode, invocation_id=key, source_revision="a" * 40, completed=True, passed=True) for mode, key in (("Check", "check"), ("Coverage", "coverage"))]
            gates = [dict(mode=mode, label=label, invocation_id=key, exit_code=0, log=key + "/gate.log") for mode, key, label in (("Check", "check", "nextest"), ("Coverage", "coverage", "coverage"))]
            (root / "gates.json").write_text(json.dumps(dict(source_revision="a" * 40, invocations=invocations, gates=gates)), encoding="utf-8")
            result = SUMMARY.build(root, root)
            self.assertEqual(result["coverage_workspace"]["producing_invocation"], 2)
            self.assertEqual(result["coverage_workspace"]["source_revision"], "a" * 40)
            self.assertEqual(result["nextest_junit"]["producing_invocation"], 1)
            self.assertEqual(result["nextest_junit"]["passed"], 1)

    def test_coverage_totals_stay_unchanged_and_aliases_are_reported(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "crates/contain-core/src/storage.rs"
            path.parent.mkdir(parents=True)
            path.write_text("pub fn save() {}\n", encoding="utf-8")
            direct = str(path)
            alias = str(root / "tools/offline/src/../../../crates/contain-core/src/storage.rs")
            metric = {"lines": {"count": 10, "covered": 7, "percent": 70}}
            document = {"data": [{"totals": metric, "files": [dict(filename=p, summary=metric) for p in (direct, alias)],
                                 "functions": [dict(name="SECRET_SYMBOL", filenames=[alias], count=2, regions=[[1, 1, 1, 10, 2, 0, 0, 0], [1, 11, 1, 12, 0, 0, 0, 0]])]}]}
            report = root / "coverage.json"
            report.write_text(json.dumps(document), encoding="utf-8")
            result = SUMMARY.coverage(report, root)
            self.assertEqual(result["export_totals"][0], metric)
            self.assertEqual(len(result["source_alias_groups"]), 1)
            self.assertEqual(len(result["files"]), 2)
            self.assertEqual(result["affected_functions"][0]["enclosing_function"], "save")
            self.assertEqual(result["affected_functions"][0]["covered_code_regions"], 1)
            self.assertNotIn("SECRET_SYMBOL", json.dumps(result))

    def test_junit_never_publishes_failure_output_or_names(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "junit.xml"
            path.write_text('<testsuites><testsuite><testcase name="PRIVATE_NAME"/><testcase><failure>SECRET</failure></testcase><testcase><skipped/></testcase></testsuite></testsuites>', encoding="utf-8")
            result = SUMMARY.junit(path)
            self.assertEqual((result["passed"], result["failed"], result["skipped_entries"]), (1, 1, 1))
            self.assertNotIn("PRIVATE_NAME", json.dumps(result))
            self.assertNotIn("SECRET", json.dumps(result))

    def test_viewer_read_only_and_summary_function_regions_are_exported_without_changing_totals(self):
        # Synthetic LLVM records exercise the actual public exporter. They are
        # not coverage evidence for the production functions named here.
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sources = {
                "crates/contain-core/src/storage.rs": ["open_read_only", "list_details", "events_page_compatible", "unreviewed_helper"],
                "crates/contain-cli/src/render.rs": ["summary_counts", "unreviewed_helper"],
            }
            functions = []
            for source, names in sources.items():
                path = root / source
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("".join(f"pub fn {name}() {{\n    synthetic();\n}}\n" for name in names), encoding="utf-8")
                for index, name in enumerate(names):
                    start = index * 3 + 1
                    count = 0 if name == "list_details" else 3
                    functions.append(dict(name="PRIVATE_MANGLED_SYMBOL", filenames=[str(path)], count=count,
                                          regions=[[start, 1, start + 1, 10, count, 0, 0, 0],
                                                   [start + 1, 11, start + 1, 20, 0, 0, 0, 0]]))
            totals = {"lines": {"count": 100, "covered": 70, "percent": 70}}
            report = root / "coverage.json"
            report.write_text(json.dumps({"data": [{"totals": totals, "functions": functions}]}), encoding="utf-8")
            result = SUMMARY.coverage(report, root)
            self.assertEqual(result["export_totals"], [totals])
            affected = {row["enclosing_function"]: row for row in result["affected_functions"]}
            self.assertEqual(set(affected), {"open_read_only", "list_details", "events_page_compatible", "summary_counts"})
            for name, row in affected.items():
                with self.subTest(function=name):
                    self.assertEqual(row["code_regions"], 2)
                    self.assertEqual(row["execution_count"], 0 if name == "list_details" else 3)
                    self.assertEqual(row["covered_code_regions"], 0 if name == "list_details" else 1)
                    self.assertEqual(len(row["uncovered_code_regions"]), 2 if name == "list_details" else 1)
                    self.assertEqual(row["uncovered_code_regions"][-1]["start"], [row["definition_line"] + 1, 11])
            self.assertNotIn("PRIVATE_MANGLED_SYMBOL", json.dumps(result))
            self.assertNotIn(str(root), json.dumps(result))


if __name__ == "__main__":
    unittest.main()
