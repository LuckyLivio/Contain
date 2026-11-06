"""Offline oracle guards, not live ETW acceptance."""
import copy
import importlib.util
import json
from pathlib import Path
import unittest


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


PAIR = module("loss_pair", "summarize-capture-loss-pair.py")
FIXTURES = module("follow_fixture", "test-verify-follow-installer-experiment.py")


class PairTests(unittest.TestCase):
    def setUp(self):
        fixture = self.fixture = FIXTURES.OracleTests("test_valid_actor_association_keeps_unknown_completion_and_ownership")
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        self.directory = fixture.explanations / "case"
        (self.directory / "private").mkdir(parents=True)
        (self.directory / "public").mkdir()
        self.truth = self.directory / "truth"
        self.truth.mkdir()
        fixture.expected.update(truth_directory=str(self.truth), background_files=1, background_exit_code=0, background_signal_exit_code=0)
        for target in fixture.expected["targets"]:
            target["content_verified"] = True
        fixture.truth += [dict(role="noise", operation=operation, success=True, resource=r"C:\fixture\noise\0.dat",start_ticks="130",end_ticks="170")
                          for operation in ("write_requested", "rename_requested", "delete_requested")]
        fixture.capture.update(started_at="1601-01-01T00:00:00+00:00",finished_at="1601-01-01T00:00:00.0000300+00:00")
        fixture.capture["backend"]["events_received"] = 3
        fixture.capture["backend"]["stream"]["accepted"] = 3
        fixture.capture["backend"]["handoff"] = dict(initial_accepted=0,received=3,submitted=3,overflow=0,oversized=0,
            disconnected=0,serialize_failed=0,drained=3,pending=0,receiver_finished=True,writer_finished=True,
            record_capacity=4096, byte_capacity=16777216)
        fixture.capture["backend"]["pipeline"] = dict(enqueued=3,dequeued=3,queue_overflow=0,enqueue_disconnected=0,queue_pending=0)
        self.save()

    def write(self, filename, document):
        (self.directory / filename).write_text(json.dumps(document), encoding="utf-8")

    def save(self):
        self.write("private/expected.json", self.fixture.expected)
        self.write("private/worker-termination.json", dict(all_owned_processes_exited=True))
        self.write("private/inspect.stdout", dict(schema_version=3, kind="inspect", data=self.fixture.capture))
        # These aggregate checks are produced by the unchanged follow oracle.
        self.write("public/follow-experiment-summary.json", self.fixture.verify())
        self.write("private/capture.stdout", dict(schema_version=3, kind="install_status", data=dict(
            session_id="session-one",stage="finished",installer_exited=True,installer_exit_code=0,capture_finished=True,data_saved=True)))
        (self.truth / "ground-truth-fixture.jsonl").write_text("\n".join(json.dumps(row) for row in self.fixture.truth))

    def summarize(self):
        return PAIR.summarize(self.directory)

    def test_complete_background_and_saved_exact_session_are_required(self):
        result = self.summarize()
        self.assertTrue(result["accepted"], result)
        self.assertTrue(result["background"]["complete"])
        self.assertTrue(result["cold_query_exact_session"])
        self.assertEqual(result["targets"][0]["retained_write_requests"], 1)

    def test_partial_or_failed_background_is_not_equal_work(self):
        self.fixture.truth.pop()
        self.save()
        self.assertFalse(self.summarize()["accepted"])
        self.assertFalse(self.summarize()["background"]["complete"])

    def test_known_independent_actor_does_not_imply_installer_ownership(self):
        event = copy.deepcopy(self.fixture.capture["events"][0])
        event.update(resource=r"C:\fixture\noise\0.dat", confidence="Unknown")
        event["evidence"].update(rule="MissingWriter", session_id=None)
        self.fixture.capture["events"].append(event)
        self.save()
        result = self.summarize()
        self.assertTrue(result["accepted"], result)
        self.assertEqual(result["background"]["observed_events"], 1)
        self.assertEqual(result["background"]["falsely_attributed_events"], 0)

    def test_unknown_confidence_with_installer_rule_is_still_false_attribution(self):
        event = copy.deepcopy(self.fixture.capture["events"][0])
        event.update(resource=r"C:\fixture\noise\0.dat", confidence="Unknown")
        event["evidence"]["session_id"] = None
        self.fixture.capture["events"].append(event)
        self.save()
        result = self.summarize()
        self.assertFalse(result["accepted"])
        self.assertEqual(result["background"]["falsely_attributed_events"], 1)

    def test_original_paths_and_error_strings_never_appear_in_aggregate(self):
        self.fixture.capture["backend"]["stream"]["error"] = r"private C:\SECRET-user\record.db"
        self.fixture.capture["backend"]["pipeline"] = dict(private_path=r"C:\SECRET-user")
        self.save()
        encoded = json.dumps(self.summarize())
        self.assertNotIn("SECRET-user", encoded)
        self.assertNotIn("C:\\\\fixture", encoded)
        self.assertIn('"error_present": true', encoded)

    def test_missing_historical_counter_is_null_and_not_zero(self):
        self.fixture.capture["backend"].pop("handoff")
        self.save()
        self.assertIsNone(self.summarize()["handoff"]["overflow"])

    def test_saved_query_to_another_session_fails(self):
        self.fixture.capture["id"] = "another-session"
        self.save()
        self.assertFalse(self.summarize()["accepted"])

    def test_missing_capture_preserves_failure_without_inventing_measurement(self):
        result = PAIR.summarize(self.directory / "missing")
        self.assertFalse(result["available"])
        self.assertFalse(result["accepted"])

    def test_baseline_loss_is_retained_and_candidate_requires_original_gate(self):
        candidate = self.summarize()
        baseline = copy.deepcopy(candidate)
        baseline["counters"]["dropped_events"] = 100
        baseline.update(accepted=False, experiment_accepted=False)
        result = PAIR.compare(baseline, candidate, {})
        self.assertTrue(result["accepted"])
        self.assertTrue(result["improvement_claim"])
        self.assertEqual(result["application_loss_change"]["reduction"], 100)
        candidate["experiment_accepted"] = candidate["accepted"] = False
        result = PAIR.compare(baseline, candidate, {})
        self.assertFalse(result["accepted"])
        self.assertFalse(result["improvement_claim"])

    def test_larger_buffer_or_different_background_cannot_claim_improvement(self):
        candidate = self.summarize()
        baseline = copy.deepcopy(candidate)
        baseline["counters"]["dropped_events"] = 100
        candidate["handoff"]["record_capacity"] += 1
        result = PAIR.compare(baseline, candidate, {})
        self.assertFalse(result["comparable"])
        self.assertFalse(result["improvement_claim"])

    def test_missing_independent_target_prevents_same_workload_claim(self):
        candidate = self.summarize()
        baseline = copy.deepcopy(candidate)
        baseline["counters"]["dropped_events"] = 100
        baseline["workload_complete"] = False
        result = PAIR.compare(baseline, candidate, {})
        self.assertFalse(result["comparable"])
        self.assertFalse(result["improvement_claim"])

    def test_noise_after_root_exit_is_not_comparable(self):
        self.fixture.truth[-1]["end_ticks"] = "210"
        self.save()
        result = self.summarize()
        self.assertFalse(result["accepted"])
        self.assertFalse(result["workload_complete"])
        self.assertFalse(result["background"]["window"]["all_noise_within_root_lifetime"])

    def test_noise_does_not_span_child_write_is_not_comparable(self):
        for row in self.fixture.truth:
            if row.get("role") == "noise":
                row.update(start_ticks="110", end_ticks="130")
        self.save()
        self.assertFalse(self.summarize()["accepted"])

    def test_loss_equation_and_pending_workers_cannot_be_hidden(self):
        self.fixture.capture["backend"]["handoff"]["pending"] = 1
        self.fixture.capture["backend"]["dropped_events"] = 1
        self.save()
        result = self.summarize()
        self.assertFalse(result["accounting"]["finished_workers"])
        self.assertFalse(result["accounting"]["handoff_drain"])
        self.assertFalse(result["accounting"]["application_loss"])
        self.assertFalse(result["accepted"])

    def test_zero_baseline_loss_does_not_establish_loss_improvement(self):
        candidate = self.summarize()
        result = PAIR.compare(candidate, candidate, {})
        self.assertTrue(result["accepted"])
        self.assertFalse(result["improvement_claim"])
        self.assertIsNone(result["application_loss_change"]["reduction_percent"])


if __name__ == "__main__":
    unittest.main()
