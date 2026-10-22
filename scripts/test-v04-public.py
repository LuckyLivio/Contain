import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("public", Path(__file__).with_name("v04-public.py"))
PUBLIC = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLIC)


class PublicTests(unittest.TestCase):
    def test_missing_and_failed_attempts_survive_without_machine_payloads(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "private"
            source.mkdir()
            plans = [dict(mode=mode, files=1000, workers=4, noise_files=100, seed_index=0, trial=1) for mode in ("baseline", "candidate")]
            (source / "environment.json").write_text(json.dumps(dict(planned_attempts=plans, machine="MACHINE_SECRET", executables={}, snapshot_only=False)))
            trial = source / "0001-baseline-1000-w4-n100-db0-t1"
            trial.mkdir()
            record = dict(**plans[0], status="failed", acceptance_pass=False, fixture_root="C:\\private-user\\fixture",
                          harness_error="MACHINE_SECRET failure", backend=dict(warnings=["MACHINE_SECRET"], etw_file="active", pipeline=dict(queue_overflow=3)),
                          score=dict(unknown=42), stats=dict(phase_ms=dict(post_wall=300)))
            (trial / "measurement.json").write_text(json.dumps(record))
            (trial / "capture.json").write_text('{"inventory":"MACHINE_SECRET"}')
            (trial / "score.json").write_text(json.dumps(dict(rows=[dict(role="worker", group="target_success", resource="C:\\private-user\\fixture\\worker-0\\0.tmp", outcome="Unknown")])) )
            (trial / "ground-truth.json").write_text(json.dumps([dict(role="worker", resource="C:\\private-user\\elsewhere", destination=None, pid=7, creation_time="10")]))
            report = PUBLIC.publish(source, root / "public")
            serialized = json.dumps(report)
            self.assertNotIn("MACHINE_SECRET", serialized)
            self.assertNotIn("private-user", serialized)
            self.assertEqual(report["planned_attempts"], 2)
            self.assertEqual([row["status"] for row in report["trials"]], ["failed", "not_run"])
            self.assertFalse(report["acceptance_pass"])
            self.assertEqual(report["trials"][0]["backend"]["pipeline"]["queue_overflow"], 3)
            self.assertEqual(report["trials"][0]["scored_rows"][0]["resource"], "<FIXTURE>/worker-0/0.tmp")
            self.assertEqual(report["trials"][0]["truth_rows"][0]["resource"], "<REDACTED_OUTSIDE_FIXTURE>")
            self.assertEqual({path.name for path in (root / "public").iterdir()}, {"comparison.json"})

    def test_path_scope_has_a_separator_boundary_and_normalizes_traversal(self):
        self.assertEqual(PUBLIC.fixture_path("C:\\fixture-evil\\secret", "C:\\fixture"), "<REDACTED_OUTSIDE_FIXTURE>")
        self.assertEqual(PUBLIC.fixture_path("C:\\fixture\\..\\secret", "C:\\fixture"), "<REDACTED_OUTSIDE_FIXTURE>")
        self.assertEqual(PUBLIC.fixture_path("\\\\?\\C:\\FIXTURE\\a", "C:\\fixture"), "<FIXTURE>/a")

    def test_arbitrary_event_and_error_strings_are_not_copied_by_numeric_projection(self):
        projected = PUBLIC.numeric(dict(error="secret", stages=dict(raw_insert=dict(total_ns=12)), events=[dict(resource="secret")]))
        self.assertNotIn("secret", json.dumps(projected))
        self.assertEqual(projected["stages"]["raw_insert"]["total_ns"], 12)

    def test_missing_or_truncated_oracle_never_turns_original_pass_into_verified_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "private"
            source.mkdir()
            plan = dict(mode="candidate", files=4, workers=1, noise_files=0, seed_index=0, trial=1)
            (source / "environment.json").write_text(json.dumps(dict(planned_attempts=[plan])))
            trial = source / "0001-candidate-4-w1-n0-db0-t1"
            trial.mkdir()
            record = dict(**plan, status="completed", acceptance_pass=True, expected_target_operations=10, expected_noise_operations=0)
            (trial / "measurement.json").write_text(json.dumps(record))
            for index, rows in enumerate((None, [])):
                if rows is not None:
                    (trial / "score.json").write_text(json.dumps(dict(rows=rows)))
                    (trial / "ground-truth.json").write_text(json.dumps(rows))
                report = PUBLIC.publish(source, root / f"public{index}")
                self.assertFalse(report["acceptance_pass"])
                self.assertFalse(report["trials"][0]["artifact_complete"])
            record["files"] = 1000
            (trial / "measurement.json").write_text(json.dumps(record))
            report = PUBLIC.publish(source, root / "labels")
            self.assertTrue(report["trials"][0]["label_mismatch"])
            self.assertEqual(report["trials"][0]["files"], 4)


if __name__ == "__main__":
    unittest.main()
