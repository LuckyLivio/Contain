"""Fixed-plan regressions over synthetic summaries; not Windows acceptance."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


MATRIX = load("capture_matrix", "summarize-capture-loss-matrix.py")
FIXTURES = load("capture_matrix_fixtures", "test-upload-beta10-assets.py")


def plan():
    return dict(schema_version=1, protocol=MATRIX.PROTOCOL, maximum_native_captures=6, retries=0,
        order=copy.deepcopy(MATRIX.ORDER), package_identity=dict(
            baseline=dict(version="0.3.2-beta.9", source_sha=MATRIX.BASELINE_SOURCE, zip_sha256=MATRIX.BASELINE_ZIP_SHA),
            previous=dict(version="0.3.2-beta.10", source_sha=MATRIX.PREVIOUS_SOURCE, zip_sha256=MATRIX.PREVIOUS_ZIP_SHA),
            candidate=dict(version="0.3.2-beta.10", source_sha="c"*40, zip_sha256="d"*64),
            fixture_source="c"*40, same_fixture_sha256="e"*64,
            identity_diagnostic=dict(source_sha="c"*40, sha256="f"*64),
            background_files=12000, resource_policy=MATRIX.RESOURCE_POLICY))


def cases(value):
    identity = value["package_identity"]
    return [dict(id=name, package=name.split("/")[1],
        source_sha=identity["baseline" if name.endswith("/beta9") else name.split("/")[1]]["source_sha"],
        attempted=True, identity_replay_available=True, summary=FIXTURES.phase(not name.endswith("/candidate")))
        for name in MATRIX.ORDER]


class MatrixTests(unittest.TestCase):
    def setUp(self):
        self.plan = plan()
        self.cases = cases(self.plan)
        self.execution = dict(attempted=copy.deepcopy(MATRIX.ORDER), not_run=[], stop_reason=None)

    def aggregate(self):
        return MATRIX.aggregate(self.plan, self.execution, self.cases, "a"*64)

    def test_two_candidates_must_pass_while_all_historical_failures_are_retained(self):
        result = self.aggregate()
        self.assertTrue(result["accepted"], result)
        self.assertTrue(result["improvement_claim"])
        self.assertEqual(len(result["cases"]), 6)
        self.assertFalse(result["cases"][0]["summary"]["experiment_accepted"])
        self.assertFalse(result["cases"][4]["summary"]["experiment_accepted"])
        self.assertEqual([item["round"] for item in result["comparisons"]], ["r1", "r2"])

    def test_passing_first_candidate_cannot_replace_failing_second(self):
        self.cases[3]["summary"].update(accepted=False, experiment_accepted=False)
        result = self.aggregate()
        self.assertTrue(result["cases"][2]["summary"]["accepted"])
        self.assertFalse(result["accepted"])
        self.assertFalse(result["improvement_claim"])
        self.assertTrue(result["completed"])
        self.assertEqual(len(result["cases"]), 6)

    def test_remaining_attempts_are_not_filled_after_unconfirmed_exit(self):
        self.execution.update(attempted=MATRIX.ORDER[:2], not_run=MATRIX.ORDER[2:],
                              stop_reason="owned_process_termination_unverified")
        for case in self.cases[2:]:
            case["attempted"] = False
        result = self.aggregate()
        self.assertFalse(result["completed"])
        self.assertFalse(result["accepted"])
        self.assertEqual(result["not_run"], MATRIX.ORDER[2:])

    def test_reordered_duplicated_or_omitted_attempts_cannot_pass(self):
        for attempted in (MATRIX.ORDER[::-1], MATRIX.ORDER + [MATRIX.ORDER[-1]], MATRIX.ORDER[:-1]):
            self.execution["attempted"] = attempted
            with self.subTest(attempted=attempted):
                self.assertFalse(self.aggregate()["accepted"])

    def test_case_order_and_declared_order_are_both_binding(self):
        self.cases[0], self.cases[1] = self.cases[1], self.cases[0]
        self.assertFalse(self.aggregate()["completed"])
        self.cases[0], self.cases[1] = self.cases[1], self.cases[0]
        self.plan["order"] = MATRIX.ORDER[::-1]
        self.assertFalse(self.aggregate()["accepted"])

    def test_previous_zip_and_source_are_frozen_original_bytes(self):
        for field, wrong in (("zip_sha256", "0"*64), ("source_sha", "0"*40)):
            original = copy.deepcopy(self.plan)
            self.plan["package_identity"]["previous"][field] = wrong
            with self.subTest(field=field):
                self.assertFalse(self.aggregate()["accepted"])
            self.plan = original

    def test_shared_fixture_and_diagnostic_belong_to_current_source(self):
        for field in ("fixture_source", "identity_diagnostic"):
            original = copy.deepcopy(self.plan)
            if field == "fixture_source":
                self.plan["package_identity"][field] = "b"*40
            else:
                self.plan["package_identity"][field]["source_sha"] = "b"*40
            with self.subTest(field=field):
                self.assertFalse(self.aggregate()["accepted"])
            self.plan = original

    def test_same_enlarged_limits_do_not_replace_existing_bounds(self):
        for case in self.cases:
            case["summary"]["handoff"]["record_capacity"] = 8192
        self.assertFalse(self.aggregate()["comparable"])

    def test_previous_capture_needs_complete_truth_and_same_resources(self):
        previous = self.cases[1]["summary"]
        previous["handoff"]["byte_capacity"] *= 2
        self.assertFalse(self.aggregate()["comparable"])
        previous["handoff"]["byte_capacity"] //= 2
        previous["workload_complete"] = False
        self.assertFalse(self.aggregate()["accepted"])

    def test_residual_candidate_and_raw_losses_are_separate_failures(self):
        original = copy.deepcopy(self.cases)
        for layer, field in (("follow_installer", "candidate_dropped"), ("pipeline", "queue_overflow"),
                             ("counters", "dropped_events"), ("stream", "failed")):
            self.cases = copy.deepcopy(original)
            self.cases[3]["summary"][layer][field] = 1
            with self.subTest(layer=layer, field=field):
                self.assertFalse(self.aggregate()["accepted"])

    def test_nominal_accepted_flags_cannot_hide_accounting_mismatch(self):
        self.cases[3]["summary"]["handoff"]["submitted"] += 1
        self.assertFalse(self.aggregate()["accepted"])

    def test_no_average_or_best_sample_hides_unrelated_false_attribution(self):
        self.cases[3]["summary"]["background"]["falsely_attributed_events"] = 1
        self.assertFalse(self.aggregate()["accepted"])

    def test_unknown_failures_also_require_saved_identity_replay(self):
        self.cases[1]["identity_replay_available"] = False
        result = self.aggregate()
        self.assertFalse(result["identity_replays_complete"])
        self.assertFalse(result["accepted"])

    def test_changed_case_source_is_rejected(self):
        self.cases[3]["source_sha"] = "b"*40
        self.assertFalse(self.aggregate()["accepted"])

    def test_fixed_plan_cannot_use_boolean_limits_or_schema(self):
        for field in ("schema_version", "maximum_native_captures", "retries"):
            original = copy.deepcopy(self.plan)
            self.plan[field] = True if field != "retries" else False
            with self.subTest(field=field):
                self.assertFalse(self.aggregate()["accepted"])
            self.plan = original

    def test_summary_hash_binds_exact_pre_capture_plan_and_missing_replay_is_visible(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "public").mkdir()
            plan_path = directory / "public/run-plan.json"
            plan_path.write_text(json.dumps(self.plan), encoding="utf-8")
            (directory / "public/execution.json").write_text(json.dumps(self.execution), encoding="utf-8")
            for name in MATRIX.ORDER:
                output = directory / name / "public"
                output.mkdir(parents=True)
                (output / "follow-identity-diagnostic.json").write_text('{"complete":true}', encoding="utf-8")
            (directory / MATRIX.ORDER[1] / "public/follow-identity-diagnostic.json").unlink()
            by_name = {case["id"]: case["summary"] for case in self.cases}
            with mock.patch.object(MATRIX.PAIR, "summarize", side_effect=lambda path: by_name[path.relative_to(directory).as_posix()]):
                result = MATRIX.summarize(directory)
            self.assertEqual(result["plan_sha256"], hashlib.sha256(plan_path.read_bytes()).hexdigest())
            self.assertFalse(result["cases"][1]["identity_replay_available"])
            self.assertFalse(result["accepted"])


if __name__ == "__main__":
    unittest.main()
