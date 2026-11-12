"""Stage A requires both isolated native results for exactly the frozen ZIP."""
import copy
import importlib.util
import json
import re
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("ux_acceptance", Path(__file__).with_name("ux-delivery-acceptance.py"))
S = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(S)
SOURCE, ZIP = "1" * 40, "2" * 64


def inputs():
    needs = {key: dict(result="success") for key in ("build", "wpf", "capture")}
    build = dict(source_sha=SOURCE, version="0.4.0-beta.2", core_source_sha=SOURCE,
                 reused_core_binary=False, core_sha256="3" * 64,
                 files=[dict(path="Contain.Viewer.exe", sha256="4" * 64), dict(path="Contain.Viewer.dll", sha256="5" * 64)])
    desktop = dict(accepted=True, source_sha=SOURCE, native_windows=True, actual_etw=True,
                   cold_restart_verified=True, retries=0, failed=0, not_run=0, passed=14,
                   planned_checks=S.NATIVE_CHECKS.copy(), checks=[dict(name=name,status="passed") for name in S.NATIVE_CHECKS],
                   core_sha256=build["core_sha256"], viewer_exe_sha256="4" * 64, viewer_dll_sha256="5" * 64)
    load = dict(accepted=True, source_sha=SOURCE, zip_sha256=ZIP, maximum_native_captures=1,
                retries=0, background_files=12000, core_sha256=build["core_sha256"])
    return [needs, desktop, load, build, SOURCE, ZIP]


class NativeResultContracts(unittest.TestCase):
    def test_both_native_jobs_are_required(self):
        self.assertTrue(S.final(*inputs())["accepted"])
        for failed in ("build", "wpf", "capture"):
            args = inputs()
            args[0][failed]["result"] = "failure"
            result = S.final(*args)
            self.assertFalse(result["accepted"])
            self.assertEqual(result["jobs"][failed], "failure")
            self.assertEqual(result["desktop"], args[1])
            self.assertEqual(result["load"], args[2])

    def test_native_plan_matches_the_actual_harness(self):
        source = (Path(__file__).parents[1] / "desktop/Contain.Desktop.Tests/Program.cs").read_text()
        actual = re.search(r'internal static readonly string\[\] Plan\s*=\s*\[(.*?)\];', source, re.S)
        self.assertIsNotNone(actual)
        self.assertEqual(re.findall(r'"([^"\n]+)"', actual.group(1)), S.NATIVE_CHECKS)
        self.assertEqual(len(S.NATIVE_CHECKS), 14)

    def test_incomplete_duplicate_reordered_or_failed_native_checks_cannot_claim_acceptance(self):
        for flaw in ["missing_plan", "short_plan", "duplicate_plan", "reordered_plan", "missing_checks",
                     "duplicate_check", "reordered_checks", "failed_check", "not_run_check", "passed_count"]:
            args = inputs()
            desktop = args[1]
            if flaw == "missing_plan": desktop.pop("planned_checks")
            if flaw == "short_plan": desktop["planned_checks"].pop()
            if flaw == "duplicate_plan": desktop["planned_checks"][-1] = desktop["planned_checks"][0]
            if flaw == "reordered_plan": desktop["planned_checks"].reverse()
            if flaw == "missing_checks": desktop["checks"].pop()
            if flaw == "duplicate_check": desktop["checks"][-1] = desktop["checks"][0].copy()
            if flaw == "reordered_checks": desktop["checks"].reverse()
            if flaw == "failed_check": desktop["checks"][3]["status"] = "failed"
            if flaw == "not_run_check": desktop["checks"][3]["status"] = "not_run"
            if flaw == "passed_count": desktop["passed"] = 13
            with self.subTest(flaw=flaw):
                self.assertFalse(S.final(*args)["accepted"])

    def test_tools_require_exact_regular_membership_and_hashes(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            tool = root / "fixture.exe"
            tool.write_bytes(b"synthetic validation tool")
            original = dict(source_sha=SOURCE, synthetic_stores_only=True,
                            files=[dict(path="fixture.exe", sha256=S.PACKAGE.BASE.sha(tool))])
            manifest = root / "TOOLS.json"
            def check(data):
                manifest.write_text(json.dumps(data))
                S.verify_tools(root, SOURCE, S.PACKAGE.BASE.sha(manifest))
            check(original)
            for flaw in ["extra", "missing", "duplicate", "escape", "hash", "symlink"]:
                data = copy.deepcopy(original)
                extra = root / "extra.exe"
                if flaw == "extra": extra.write_bytes(b"unlisted tool")
                if flaw == "missing": data["files"].append(dict(path="missing.exe",sha256="0"*64))
                if flaw == "duplicate": data["files"].append(data["files"][0].copy())
                if flaw == "escape": data["files"][0]["path"] = "../fixture.exe"
                if flaw == "hash": data["files"][0]["sha256"] = "0"*64
                if flaw == "symlink": extra.symlink_to(tool)
                with self.subTest(flaw=flaw), self.assertRaises(ValueError):
                    check(data)
                if extra.exists() or extra.is_symlink(): extra.unlink()
            check(original)

    def test_historical_or_changed_product_identities_fail(self):
        for section, field, value in [(1,"source_sha","6"*40),(2,"zip_sha256","6"*64),
                (3,"core_source_sha","6"*40),(3,"reused_core_binary",True),
                (1,"core_sha256","6"*64),(1,"viewer_dll_sha256","6"*64),(2,"core_sha256","6"*64)]:
            args = inputs()
            args[section][field] = value
            self.assertFalse(S.final(*args)["accepted"])

    def test_missing_or_partial_native_evidence_is_rejected(self):
        for section in [0, 1, 2, 3]:
            args = inputs()
            args[section] = {}
            self.assertFalse(S.final(*args)["accepted"])
        for field, value in [("actual_etw", False), ("cold_restart_verified", False), ("not_run", 1), ("failed", 1), ("retries", 1)]:
            args = inputs()
            args[1][field] = value
            self.assertFalse(S.final(*args)["accepted"])

    def test_wrong_workload_and_live_count_fail(self):
        for field, value in [("background_files",11999),("maximum_native_captures",2),("retries",1),("accepted",False)]:
            args = inputs()
            args[2][field] = value
            self.assertFalse(S.final(*args)["accepted"])

    def test_load_requires_the_actual_oracle_source_and_worker_termination(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "public").mkdir()
            path = root / "public/follow-experiment-summary.json"
            path.write_text(json.dumps(dict(source_sha=SOURCE)))
            sample = dict(accepted=True, workload_complete=True, all_owned_processes_exited=True, background=dict(files=12000))
            with patch.object(S.PAIR, "summarize", return_value=sample):
                self.assertTrue(S.capture(root, SOURCE, ZIP)["accepted"])
            for field in ["workload_complete", "all_owned_processes_exited"]:
                failed = copy.deepcopy(sample)
                failed[field] = False
                with patch.object(S.PAIR, "summarize", return_value=failed):
                    self.assertFalse(S.capture(root, SOURCE, ZIP)["accepted"])
            path.write_text(json.dumps(dict(source_sha="6"*40)))
            with patch.object(S.PAIR, "summarize", return_value=sample):
                self.assertFalse(S.capture(root, SOURCE, ZIP)["accepted"])


if __name__ == "__main__":
    unittest.main()
