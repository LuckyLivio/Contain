"""Checks denominator/gate invariants and preservation of failed matrix attempts."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock


SPEC = importlib.util.spec_from_file_location("matrix", Path(__file__).with_name("v04-matrix.py"))
MATRIX = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MATRIX)


def data(files=1000, workers=4):
    capture = dict(backend=dict(etw_file="active", events_received=10, dropped_events=0, decode_errors=0, etw_events_lost=0, etw_buffers_lost=0,
        pipeline=dict(callback_records=10, decode_attempted=10, unsupported_records=0, decode_succeeded=10, decode_failed=0,
                      deliberately_filtered=0, failed_without_output=0, queue_overflow=0, enqueue_disconnected=0,
                      enqueued=10, dequeued=10, queue_pending=0, context_evictions=0),
        stream=dict(accepted=10, persisted=10, failed=0, quota_dropped=0)), stats=dict(drain_timed_out=False), events=[], files=[], registry=[])
    score = dict(incorrect_attribution=0, false_positive_target_events=0, groups=dict(
        target_success=dict(expected=MATRIX.target_operations(files, workers), observation_rate=1.0, correct_attribution_rate=1.0),
        noise_success=dict(expected=300)))
    return capture, score


def handoff_data(**changes):
    result = dict(initial_accepted=2, received=8, submitted=8, overflow=0, oversized=0,
                  disconnected=0, serialize_failed=0, drained=8, pending=0,
                  high_water_records=8, high_water_bytes=4096, record_capacity=4096, byte_capacity=16 * 1024 * 1024,
                  receiver_finished=True, writer_finished=True)
    result.update(changes)
    return result


class MatrixTests(unittest.TestCase):
    def arguments(self, *extra):
        return MATRIX.parser().parse_args(["--baseline", "baseline.exe", "--candidate", "candidate.exe",
            "--fixture", "fixture.exe", "--output", "output", "--files", "100", "1000", "10000", "--workers", "4", *extra])

    def test_stabilization_preregisters_ten_trials_at_thousand_files_and_preserves_legacy_plan(self):
        legacy = self.arguments()
        MATRIX.validate_parameters(legacy)
        self.assertEqual(len(MATRIX.plan_attempts(legacy)), 18)
        args = self.arguments("--stabilization", "--baseline-revision", "a" * 40, "--candidate-revision", "b" * 40)
        MATRIX.validate_parameters(args)
        plan = MATRIX.plan_attempts(args)
        self.assertEqual(len(plan), 32)
        counts = MATRIX.itertools.groupby(plan, key=lambda row: row["files"])
        self.assertEqual({files: len(list(rows)) for files, rows in counts}, {100: 6, 1000: 20, 10000: 6})
        for left, right in zip(plan[::2], plan[1::2]):
            self.assertEqual({key: value for key, value in left.items() if key != "mode"},
                             {key: value for key, value in right.items() if key != "mode"})
            self.assertEqual([left["mode"], right["mode"]], ["baseline", "candidate"] if left["trial"] % 2 else ["candidate", "baseline"])

    def test_stabilization_rejects_fallback_missing_scales_short_revisions_and_fewer_trials(self):
        valid = self.arguments("--stabilization", "--baseline-revision", "a" * 40, "--candidate-revision", "b" * 40)
        for patch in (dict(snapshot_only=True), dict(files=[100, 1000]), dict(trials=2), dict(stability_trials=9),
                      dict(baseline_revision="a072072"), dict(candidate_revision=None), dict(workers=[4, 4])):
            with self.subTest(patch=patch):
                args = copy.deepcopy(valid)
                args.__dict__.update(patch)
                with self.assertRaises(ValueError):
                    MATRIX.validate_parameters(args)
        args = self.arguments("--trials", "10")
        with self.assertRaises(ValueError):
            MATRIX.validate_parameters(args)

    def test_unvalidated_clock_provider_contract_never_passes_stabilization(self):
        for status in ("unavailable", "invalid", "diagnostic_ready"):
            gate = MATRIX.stabilization_gate({"pass": True}, dict(status=status))
            self.assertFalse(gate["pass"])
            self.assertEqual(gate["status"], "blocked")
            self.assertFalse(gate["checks"]["provider_timing_contract_validated"])

    def test_executable_replacement_is_detected_against_the_frozen_pair(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = SimpleNamespace(**{name: root / name for name in ("baseline", "candidate", "fixture")})
            for name in ("baseline", "candidate", "fixture"):
                getattr(args, name).write_bytes(name.encode())
            args.frozen_hashes = {name: MATRIX.sha(getattr(args, name)) for name in ("baseline", "candidate", "fixture")}
            self.assertTrue(MATRIX.frozen_binaries(args)["match"])
            args.fixture.write_bytes(b"replaced")
            self.assertFalse(MATRIX.frozen_binaries(args)["match"])

    def test_stabilization_retains_every_failed_planned_attempt(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            argv = ["v04-matrix.py", "--stabilization", "--files", "100", "1000", "10000", "--workers", "4",
                    "--baseline-revision", "a" * 40, "--candidate-revision", "b" * 40, "--output", str(root / "result")]
            for name in ("baseline", "candidate", "fixture"):
                binary = root / (name + ".exe")
                binary.write_bytes(name.encode())
                argv += ["--" + name, str(binary)]
            def failed(args, directory, mode, files, workers, noise, trial, seed, seed_index):
                return dict(mode=mode, files=files, workers=workers, noise_files=noise, trial=trial,
                            seed_index=seed_index, status="failed", acceptance_pass=False, harness_error="injected failure")
            with mock.patch.object(MATRIX.sys, "argv", argv), mock.patch.object(MATRIX.sys, "platform", "win32"), \
                 mock.patch.object(MATRIX.subprocess, "check_output", return_value="S-1-5-21"), \
                 mock.patch.object(MATRIX.subprocess, "CREATE_NO_WINDOW", 0, create=True), \
                 mock.patch.object(MATRIX, "run_trial", side_effect=failed) as run, mock.patch.object(MATRIX.sys, "stdout", io.StringIO()):
                self.assertEqual(MATRIX.main(), 2)
            report = json.loads((root / "result/comparison.json").read_text())
            self.assertEqual(run.call_count, 32)
            self.assertEqual(report["planned_attempts"], 32)
            self.assertEqual(len(report["trials"]), 32)
            self.assertFalse(report["interrupted"])
            self.assertTrue(all(row["status"] == "failed" for row in report["trials"]))
            self.assertEqual(report["environment"]["protocol"]["status"], "blocked")

    def test_handoff_balances_and_missing_or_bad_counters_fail_closed(self):
        capture, score = data()
        handoff = handoff_data()
        capture["backend"]["handoff"] = handoff
        self.assertTrue(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])
        for field in ("initial_accepted", "received", "submitted", "overflow", "oversized", "disconnected", "serialize_failed", "drained", "pending"):
            for changed in (None, -1, True, handoff[field] + 1):
                with self.subTest(field=field, changed=changed):
                    candidate = copy.deepcopy(capture)
                    candidate["backend"]["handoff"][field] = changed
                    self.assertFalse(MATRIX.quality_gate(candidate, score, 1000, 4, 100, False)["pass"])
        capture["backend"]["handoff"] = {}
        self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])

    def test_balanced_handoff_drop_or_pending_queue_still_fails(self):
        for drop in ("overflow", "oversized", "disconnected", "serialize_failed"):
            capture, score = data()
            handoff = handoff_data(submitted=7, drained=7)
            handoff[drop] = 1
            capture["backend"]["handoff"] = handoff
            capture["backend"]["stream"].update(accepted=9, persisted=9)
            gate = MATRIX.quality_gate(capture, score, 1000, 4, 100, False)
            self.assertTrue(gate["checks"]["handoff_balanced"])
            self.assertFalse(gate["checks"]["handoff_loss_zero"])
            self.assertFalse(gate["pass"])
        capture, score = data()
        capture["backend"]["handoff"] = handoff_data(drained=7, pending=1)
        gate = MATRIX.quality_gate(capture, score, 1000, 4, 100, False)
        self.assertTrue(gate["checks"]["handoff_balanced"])
        self.assertFalse(gate["checks"]["handoff_final_empty"])
        self.assertFalse(gate["pass"])

    def test_handoff_workers_must_finish_and_high_water_must_fit_recorded_limits(self):
        for patch in (dict(receiver_finished=False), dict(writer_finished=1), dict(writer_finished=None),
                      dict(high_water_records=4097), dict(high_water_bytes=16 * 1024 * 1024 + 1),
                      dict(record_capacity=0), dict(byte_capacity=None), dict(high_water_records=True)):
            with self.subTest(patch=patch):
                capture, score = data()
                capture["backend"]["handoff"] = handoff_data(**patch)
                self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])
        capture, score = data()
        capture["backend"]["handoff"] = handoff_data()
        del capture["backend"]["handoff"]["receiver_finished"]
        self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])

    def test_original_workload_and_uneven_worker_denominators(self):
        self.assertEqual(MATRIX.target_operations(100, 4), 252)
        self.assertEqual(MATRIX.target_operations(1000, 4), 2500)
        self.assertEqual(MATRIX.target_operations(10000, 4), 25000)
        self.assertEqual(MATRIX.target_operations(4, 8), 12)
        self.assertEqual(MATRIX.target_operations(100, 1), 250)

    def test_thousand_gate_remains_exact_and_other_scale_remains_95_percent(self):
        capture, score = data()
        self.assertTrue(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])
        score["groups"]["target_success"]["correct_attribution_rate"] = 2499 / 2500
        self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])
        capture, score = data(10000)
        score["groups"]["target_success"].update(observation_rate=.95, correct_attribution_rate=.95)
        self.assertTrue(MATRIX.quality_gate(capture, score, 10000, 4, 100, False)["pass"])
        score["groups"]["target_success"]["observation_rate"] = .94999
        self.assertFalse(MATRIX.quality_gate(capture, score, 10000, 4, 100, False)["pass"])

    def test_unknown_loss_and_counter_mismatch_never_pass(self):
        capture, score = data()
        for patch in (dict(etw_events_lost=None), dict(etw_events_lost=1), dict(dropped_events=1)):
            candidate = copy.deepcopy(capture)
            candidate["backend"].update(patch)
            self.assertFalse(MATRIX.quality_gate(candidate, score, 1000, 4, 100, False)["pass"])
        capture["backend"]["stream"]["persisted"] = 9
        self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])

    def test_snapshot_pass_never_claims_etw_release_pass(self):
        capture, score = data()
        capture["backend"]["etw_file"] = "disabled"
        capture["events"] = [dict(event_type="file_state", confidence="Unknown")]
        score["groups"]["target_success"].update(observation_rate=0.0, correct_attribution_rate=0.0)
        result = MATRIX.quality_gate(capture, score, 1000, 4, 100, True)
        self.assertTrue(result["pass"])
        self.assertIsNone(result["etw_release_gate"])
        capture["files"] = [dict(confidence="High")]
        self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, True)["pass"])

    def test_both_historical_callback_admission_balances_remain_mandatory(self):
        for field in ("deliberately_filtered", "failed_without_output", "queue_overflow", "enqueue_disconnected"):
            capture, score = data()
            capture["backend"]["pipeline"][field] = 1
            self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"], field)
        capture, score = data()
        capture["backend"]["events_received"] = None
        self.assertFalse(MATRIX.quality_gate(capture, score, 1000, 4, 100, False)["pass"])

    def test_timeout_preserves_samples_and_elapsed_wall(self):
        class Process:
            pid = 1
            returncode = None
            def poll(self):
                return self.returncode
            def kill(self):
                self.returncode = 1
            def wait(self):
                return self.returncode
        class Measurements:
            values = dict(parent_cpu_seconds=.1, parent_peak_working_set_bytes=4096)
            errors = set()
            def __init__(self, pid):
                pass
            def sample(self):
                pass
            def close(self):
                pass
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = SimpleNamespace(baseline=root / "baseline.exe", candidate=root / "candidate.exe", fixture=root / "fixture.exe",
                                   baseline_variant=None, candidate_variant=None, keep_fixtures=False, snapshot_only=True, timeout=1)
            with mock.patch.object(MATRIX.tempfile, "gettempdir", return_value=temporary), \
                 mock.patch.object(MATRIX.subprocess, "Popen", return_value=Process()), \
                 mock.patch.object(MATRIX.subprocess, "CREATE_NO_WINDOW", 0, create=True), \
                 mock.patch.object(MATRIX, "ProcessMetrics", Measurements), \
                 mock.patch.object(MATRIX.time, "perf_counter", side_effect=[10, 12, 13]):
                record = MATRIX.run_trial(args, root / "attempt", "candidate", 1000, 4, 0, 1, None)
            self.assertEqual(record["status"], "failed")
            self.assertTrue(record["timeout_terminated_capture"])
            self.assertEqual(record["elapsed_seconds"], 3)
            self.assertEqual(record["parent_peak_working_set_bytes"], 4096)
            self.assertEqual(record["parent_cpu_seconds"], .1)

    def test_missing_seed_is_retained_as_failed_attempt_before_capture(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            args = SimpleNamespace(baseline=root / "baseline.exe", candidate=root / "candidate.exe", fixture=root / "fixture.exe",
                                   baseline_variant=None, candidate_variant=None, keep_fixtures=False)
            with mock.patch.object(MATRIX.tempfile, "gettempdir", return_value=temporary):
                record = MATRIX.run_trial(args, root / "attempt", "candidate", 1000, 4, 0, 1, root / "missing.db")
            persisted = json.loads((root / "attempt/measurement.json").read_text())
            self.assertEqual(persisted, record)
            self.assertEqual(record["status"], "failed")
            self.assertFalse(record["acceptance_pass"])
            self.assertEqual(record["expected_target_operations"], 2500)
            self.assertTrue(Path(record["fixture_root"]).is_dir())

    def test_seed_backup_includes_committed_wal_and_does_not_modify_source(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with MATRIX.closing(MATRIX.sqlite3.connect(root / "seed.db")) as connection:
                connection.execute("PRAGMA journal_mode=WAL")
                connection.execute("CREATE TABLE marker(value)")
                connection.execute("INSERT INTO marker VALUES (17)")
                connection.commit()
                MATRIX.backup_database(root / "seed.db", root / "trial.db")
                with MATRIX.closing(MATRIX.sqlite3.connect(root / "trial.db")) as copied:
                    self.assertEqual(copied.execute("SELECT value FROM marker").fetchall(), [(17,)])
                    copied.execute("INSERT INTO marker VALUES (18)")
                    copied.commit()
                self.assertEqual(connection.execute("SELECT value FROM marker").fetchall(), [(17,)])


if __name__ == "__main__":
    unittest.main()
