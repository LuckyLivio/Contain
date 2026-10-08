"""Synthetic evidence tests; no ETW capture or changes to the acceptance scorer."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("summary", Path(__file__).with_name("summarize-checkpoint.py"))
SUMMARY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SUMMARY)
COMMIT = "a" * 40
URL = "https://github.com/example/project/actions/runs/123"


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value), encoding="utf-8")


def result(variant, passed=True):
    return {
        "checkpoint_variant": variant,
        "acceptance_pass": passed,
        "checkpoint_validation": {"expected_variant": variant, "original_quality_pass": passed,
                                  "pass_all": True, "checks": {"checkpoint_complete": True}},
        "backend": {"pipeline": {"queue_overflow": 6, "overflow_first_ns": [], "overflow_last_ns": 90},
                    "stream": {"checkpoint_variant": variant, "wal_peak_bytes": 1024,
                               "checkpoint": {"mode": "PASSIVE", "busy": 0, "log_frames": 2, "checkpointed_frames": 2, "duration_ns": 100}}},
        "stats": {"drain_timed_out": False},
        "score": {"groups": {"target_success": {"expected": 17, "observed": 17 if passed else 4}},
                  "rows": [{"resource": "SENSITIVE_BODY_PATH"}], "definition": "not a numeric score"},
    }


def profile():
    return {
        "stages": {"raw_commit_including_autocheckpoint": {"count": 1, "max_ns": 10, "total_ns": 10}},
        "queue_spans": [{"stage": "raw_commit_including_autocheckpoint", "start_ns": 20, "end_ns": 30,
                         "before": {"enqueued": 1, "overflow": 1, "dequeued": 0, "pending": 1},
                         "after": {"enqueued": 3, "overflow": 5, "dequeued": 1, "pending": 2}}],
    }


def small(root, number, noise, passed=True):
    directory = root / f"checkpoint-abc-final-D3-{number}-{'noise' if noise else 'ordinary'}"
    row = result("E1", passed)
    row.update(variant="D3", round=number, registry_noise=noise,
               measurement={"elapsed_seconds": 5 + number}, harness_wall_seconds=30 + number)
    write(directory / "result.json", row)
    write(directory / "profile.json", profile())
    for name in ["final-summary.json", "checkpoint-abc-summary.json"]:
        path = root / name
        summary = json.loads(path.read_text())
        summary["trials"] = [old for old in summary["trials"] if (old["round"], old["registry_noise"]) != (number, noise)] + [row]
        write(path, summary)
    return directory


def stress(root, files, trial, mode, passed=True):
    directory = root / f"run-{files}" / f"{mode}-stress-{files}-{trial}"
    row = result("E0" if mode == "baseline" else "E1", passed)
    row.update(mode=mode, files=files, trial=trial, role="stress", elapsed_seconds=20 + trial,
               sampled_wal_peak_bytes=4096)
    row["score"]["groups"]["target_success"]["expected"] = {1000: 2500, 10000: 25000}[files]
    row["score"]["groups"]["target_success"]["observed"] = row["score"]["groups"]["target_success"]["expected"] if passed else 4
    write(directory / "result.json", row)
    write(directory / "profile.json", profile())
    comparison = directory.parent / "comparison.json"
    report = json.loads((comparison if comparison.exists() else directory.parent / "environment.json").read_text())
    report["trials"] = [old for old in report.get("trials", []) if (old["mode"], old["trial"]) != (mode, trial)] + [row]
    write(comparison, report)
    return directory


def metadata(root):
    common = {"commit": COMMIT, "phase": "final", "expected_attempts": 10, "run_id": "checkpoint-abc", "trials": []}
    write(root / "final-summary.json", common)
    write(root / "checkpoint-abc-summary.json", common)
    for files in [1000, 10000]:
        write(root / f"run-{files}" / "environment.json", {
            "baseline_commit": COMMIT, "candidate_commit": COMMIT,
            "baseline_variant": "D3", "candidate_variant": "D3", "rustc": "rustc 1.98.1",
            "candidate_lock_sha256": "b" * 64, "baseline_lock_sha256": "b" * 64, "scorer_sha256": "c" * 64,
            "checkpoint_experiment": {"fixed_hotpath": "D3", "expected_attempts": 6},
        })


class SummaryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="contain-checkpoint-summary-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        metadata(self.root)

    def report(self):
        return SUMMARY.summarize(self.root, URL, COMMIT)

    def full_preceding_trials(self):
        for trial in range(1, 6):
            for noise in [False, True]:
                small(self.root, trial, noise)
        for trial in range(1, 4):
            for mode in ["baseline", "candidate"]:
                stress(self.root, 1000, trial, mode, passed=not (trial == 2 and mode == "baseline"))

    def test_summary_duplicates_do_not_add_attempts_and_baseline_failure_does_not_block_candidate_gate(self):
        self.full_preceding_trials()
        report = self.report()
        self.assertEqual(report["attempt_count"], 16)
        self.assertEqual(report["reported_preceding_gates"], {"small": True, "thousand": True, "ten_thousand_eligible": True})
        self.assertTrue(report["verified_eligibility"]["ten_thousand_eligible"])
        baseline = next(group for group in report["groups"] if group["files"] == 1000 and group["checkpoint_variant"] == "E0")
        self.assertEqual(baseline["gate_counts"]["original_quality_pass"], {"pass": 2, "fail": 1, "unknown": 0})
        self.assertEqual(baseline["gate_counts"]["experiment_pass"]["pass"], 3)
        ten_thousand = [group for group in report["groups"] if group["files"] == 10000]
        self.assertTrue(all(group["status"] == "not_run" and group["attempts"] == 0 and group["required_by_reported_preceding_gates"] is True for group in ten_thousand))
        self.assertEqual(report, self.report())  # Deterministic, without timestamps.

    def test_failed_small_attempt_remains_and_marks_conditional_scale_not_run(self):
        self.full_preceding_trials()
        failed = small(self.root, 3, True, passed=False)
        (failed / "profile.json").unlink()
        report = self.report()
        self.assertEqual(report["attempt_count"], 16)
        self.assertFalse(report["reported_preceding_gates"]["ten_thousand_eligible"])
        row = next(row for row in report["trials"] if row["label"] == "small:E1:noise:3")
        self.assertFalse(row["gates"]["original_quality_pass"])
        self.assertTrue(row["gates"]["experiment_pass"])
        self.assertEqual(row["score"]["groups"]["target_success"]["observed"], 4)
        self.assertIsNone(row["profile_stages"]["raw_commit_including_autocheckpoint"]["max_ns"])
        self.assertIn({"kind": "missing_profile"}, row["issues"])
        self.assertTrue(all(group["required_by_reported_preceding_gates"] is False for group in report["groups"] if group["files"] == 10000))

    def test_all_twenty_two_attempts_include_failed_large_trials(self):
        self.full_preceding_trials()
        for trial in range(1, 4):
            for mode in ["baseline", "candidate"]:
                stress(self.root, 10000, trial, mode, passed=False)
        report = self.report()
        self.assertEqual(report["attempt_count"], 22)
        self.assertTrue(report["evidence_integrity"]["pass"])
        for group in report["groups"]:
            if group["files"] == 10000:
                self.assertEqual(group["status"], "complete")
                self.assertEqual(group["gate_counts"]["combined_pass"], {"pass": 0, "fail": 3, "unknown": 0})
                self.assertEqual(group["gate_counts"]["experiment_pass"]["pass"], 3)
                self.assertEqual(group["capture_wall_median_seconds"], 22)
                self.assertEqual(group["missing_labels"], [])

    def test_partial_failure_missing_fields_are_unknown_not_zero_or_pass(self):
        directory = self.root / "run-1000" / "candidate-stress-1000-1"
        write(directory / "result.json", {"mode": "candidate", "files": 1000, "trial": 1,
              "checkpoint_variant": "E1", "acceptance_pass": False, "harness_error": "SENSITIVE_BODY_PATH"})
        report = self.report()
        self.assertEqual(report["attempt_count"], 1)
        row = report["trials"][0]
        self.assertIsNone(row["gates"]["experiment_pass"])
        self.assertIsNone(row["capture_wall_seconds"])
        self.assertIsNone(row["stream"]["persisted"])
        self.assertTrue(row["harness_error_present"])
        self.assertNotIn("SENSITIVE_BODY_PATH", json.dumps(report))
        group = next(group for group in report["groups"] if group["files"] == 1000 and group["checkpoint_variant"] == "E1")
        self.assertEqual(group["status"], "partial_or_mislabeled")
        self.assertEqual(len(group["missing_labels"]), 2)
        self.assertEqual(group["gate_counts"]["experiment_pass"]["unknown"], 1)

    def test_recomputes_diagnostics_hashes_remote_json_and_keeps_wall_scopes_separate(self):
        directory = small(self.root, 1, False)
        write(directory / "commit-tail.json", {"wrong_result_for_audit_only": 999})
        report = self.report()
        row = report["trials"][0]
        diagnostics = row["queue_window_diagnostics"]
        self.assertEqual(diagnostics["queue_spans"]["raw_commit_including_autocheckpoint"]["distinct_overflow_increments"], 4)
        self.assertEqual(diagnostics["queue_spans"]["raw_commit_including_autocheckpoint"]["fraction_of_total_overflow"], 4 / 6)
        self.assertEqual(row["capture_wall_seconds"], 6)
        self.assertEqual(row["harness_wall_seconds"], 31)
        self.assertEqual(len(row["sources"]["artifact_commit_tail"]["sha256"]), 64)
        self.assertNotIn("SENSITIVE_BODY_PATH", json.dumps(report))
        self.assertNotIn("rows", row["score"])

    def test_duplicate_labels_and_orphan_profiles_are_visible(self):
        directory = small(self.root, 1, False)
        duplicate = self.root / "checkpoint-def-final-D3-1-ordinary"
        write(duplicate / "result.json", json.loads((directory / "result.json").read_text()))
        write(self.root / "orphan" / "profile.json", profile())
        report = self.report()
        self.assertEqual(report["attempt_count"], 2)
        self.assertEqual(len(report["orphan_profiles_without_results"]), 1)
        self.assertEqual(report["groups"][0]["duplicate_labels"], ["small:E1:ordinary:1"])
        self.assertFalse(report["reported_preceding_gates"]["small"])

    def test_label_mismatch_and_unreadable_result_are_not_dropped(self):
        directory = stress(self.root, 1000, 1, "candidate")
        row = json.loads((directory / "result.json").read_text())
        row["checkpoint_variant"] = "E0"
        write(directory / "result.json", row)
        bad = self.root / "run-1000" / "baseline-stress-1000-2" / "result.json"
        bad.parent.mkdir(parents=True)
        bad.write_text("[malformed SENSITIVE_BODY_PATH", encoding="utf-8")
        report = self.report()
        self.assertEqual(report["attempt_count"], 2)
        self.assertTrue(any({"kind": "checkpoint_role_mismatch"} in item["issues"] for item in report["trials"]))
        self.assertTrue(any(any(issue["kind"] == "unreadable_json" for issue in item["issues"]) for item in report["trials"]))
        self.assertNotIn("SENSITIVE_BODY_PATH", json.dumps(report))

    def test_commit_mismatch_preserves_reported_gate_but_blocks_verified_eligibility(self):
        self.full_preceding_trials()
        for name in ["final-summary.json", "checkpoint-abc-summary.json"]:
            path = self.root / name
            summary = json.loads(path.read_text())
            summary["commit"] = "d" * 40
            write(path, summary)
        report = self.report()
        self.assertTrue(report["reported_preceding_gates"]["small"])
        self.assertFalse(report["verified_eligibility"]["small"])
        self.assertFalse(report["verified_eligibility"]["ten_thousand_eligible"])
        self.assertFalse(report["evidence_integrity"]["pass"])
        self.assertTrue(all({"kind": "source_commit_mismatch"} in row["issues"] for row in report["trials"] if row["scale"] == "small"))

    def test_fixed_denominator_and_d3_mismatch_block_verified_stage(self):
        self.full_preceding_trials()
        path = self.root / "run-1000" / "candidate-stress-1000-1" / "result.json"
        row = json.loads(path.read_text())
        row["score"]["groups"]["target_success"]["expected"] = 17
        write(path, row)
        env = self.root / "run-1000" / "comparison.json"
        context = json.loads(env.read_text())
        context["baseline_variant"] = "D0"
        write(env, context)
        report = self.report()
        self.assertTrue(report["reported_preceding_gates"]["thousand"])
        self.assertFalse(report["verified_eligibility"]["thousand"])
        self.assertTrue(any(any(issue["kind"] == "fixed_target_denominator_unverified" for issue in row["issues"]) for row in report["trials"]))
        self.assertTrue(any({"kind": "fixed_d3_unverified"} in row["issues"] for row in report["trials"]))

    def test_manifest_only_attempt_is_reported_without_invented_result(self):
        self.full_preceding_trials()
        path = self.root / "run-1000" / "candidate-stress-1000-3" / "result.json"
        path.unlink()
        report = self.report()
        self.assertEqual(report["attempt_count"], 15)
        comparison = next(item for item in report["manifests"] if item["source"]["path"] == "run-1000/comparison.json")
        self.assertEqual(comparison["trial_reconciliation"]["missing_result_paths"], ["run-1000/candidate-stress-1000-3/result.json"])
        self.assertFalse(report["verified_eligibility"]["thousand"])

    def test_specific_small_run_manifest_and_contradictory_gate_booleans_are_checked(self):
        directory = small(self.root, 1, False)
        extra = self.root / "checkpoint-def-final-D3-2-noise"
        row = result("E1")
        row.update(variant="D3", round=2, registry_noise=True)
        row["acceptance_pass"] = False
        write(extra / "result.json", row)
        write(extra / "profile.json", profile())
        write(self.root / "checkpoint-def-summary.json", {"commit": "d" * 40, "run_id": "checkpoint-def", "trials": [row]})
        report = self.report()
        original = next(item for item in report["trials"] if item["attempt"] == directory.name)
        other = next(item for item in report["trials"] if item["attempt"] == extra.name)
        self.assertEqual(original["source_commit"], COMMIT)
        self.assertEqual(other["source_commit"], "d" * 40)
        self.assertIn({"kind": "source_gate_conjunction_mismatch"}, other["issues"])

    def test_same_source_lock_and_matching_summary_disagreement_are_unverified(self):
        self.full_preceding_trials()
        summary_path = self.root / "final-summary.json"
        summary = json.loads(summary_path.read_text())
        summary["commit"] = "d" * 40
        write(summary_path, summary)
        env_path = self.root / "run-1000" / "environment.json"
        env = json.loads(env_path.read_text())
        env["baseline_lock_sha256"] = "d" * 64
        write(env_path, env)
        report = self.report()
        issues = [issue["kind"] for item in report["manifests"] for issue in item["issues"]]
        self.assertIn("same_lock_provenance_mismatch", issues)
        self.assertIn("matching_small_summaries_disagree", issues)
        self.assertFalse(report["verified_eligibility"]["small"])
        self.assertFalse(report["verified_eligibility"]["thousand"])

    def test_environment_commit_cannot_be_hidden_by_correct_comparison(self):
        self.full_preceding_trials()
        path = self.root / "run-1000" / "environment.json"
        env = json.loads(path.read_text())
        env["baseline_commit"] = env["candidate_commit"] = "d" * 40
        env["scorer_sha256"] = "e" * 64
        write(path, env)
        report = self.report()
        self.assertTrue(report["reported_preceding_gates"]["thousand"])
        self.assertFalse(report["verified_eligibility"]["thousand"])
        issues = [issue["kind"] for item in report["manifests"] for issue in item["issues"]]
        self.assertIn("manifest_source_commit_mismatch", issues)
        self.assertIn("comparison_environment_disagreement", issues)

    def test_duplicate_manifest_row_does_not_verify_seven_matches_to_six_results(self):
        self.full_preceding_trials()
        path = self.root / "run-1000" / "comparison.json"
        manifest = json.loads(path.read_text())
        manifest["trials"].append(manifest["trials"][0])
        write(path, manifest)
        report = self.report()
        self.assertEqual(report["attempt_count"], 16)
        self.assertTrue(report["reported_preceding_gates"]["thousand"])
        self.assertFalse(report["verified_eligibility"]["thousand"])
        item = next(item for item in report["manifests"] if item["source"]["path"] == "run-1000/comparison.json")
        self.assertEqual(len(item["trial_reconciliation"]["duplicate_result_paths"]), 1)
        self.assertTrue(item["trial_reconciliation"]["declared_attempt_count_mismatch"])


if __name__ == "__main__":
    unittest.main()
