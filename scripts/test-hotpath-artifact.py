"""Regression coverage for evidence retention when a comparison aborts early."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("hotpath-artifact.py")
spec = importlib.util.spec_from_file_location("artifact", SCRIPT)
artifact = importlib.util.module_from_spec(spec)
spec.loader.exec_module(artifact)


class PartialArtifactTests(unittest.TestCase):
    def test_unscored_capture_is_not_published(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary)/"private"
            dest = Path(temporary)/"public"
            source.mkdir()
            (source/"capture.json").write_text('{"machine_secret":"must stay private"}')
            (source/"profile.json").write_text('{"batches":[]}')
            result = artifact.write_trial(source, dest, "E1", "scorer interrupted")
            self.assertFalse(result["acceptance_pass"])
            self.assertTrue(result["incomplete_scoring"])
            self.assertEqual({p.name for p in dest.iterdir()}, {"profile.json", "result.json"})
            self.assertNotIn("machine_secret", (dest/"result.json").read_text())

    def test_stress_partial_run_without_comparison_keeps_failed_attempt(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)/"private"
            dest = Path(temporary)/"public"
            trial = root/"run-example"/"candidate-stress-1000-1"
            trial.mkdir(parents=True)
            measurement = dict(mode="candidate", files=1000, trial=1, acceptance_pass=False, harness_error="capture exit 1")
            (trial/"measurement.json").write_text(json.dumps(measurement))
            (trial/"profile.json").write_text('{"batches":[]}')
            (trial/"capture.stdout.txt").write_text("private stdout")
            subprocess.run([sys.executable, str(SCRIPT.with_name("hotpath-stress-artifact.py")), str(root), str(dest)], check=True)
            exported = dest/"run-example"/trial.name
            retained = json.loads((exported/"result.json").read_text())
            self.assertEqual(retained.pop("artifact_export"), dict(missing_capture=True))
            self.assertEqual(retained, measurement)
            self.assertEqual({p.name for p in exported.iterdir()}, {"profile.json", "result.json"})

    def test_truncated_scoring_does_not_hide_later_attempts(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)/"private"
            dest = Path(temporary)/"public"
            for index, bad_text in enumerate(("", '{"data":')):
                trial = root/"run-example"/f"candidate-stress-1000-{index+1}"
                trial.mkdir(parents=True)
                (trial/"measurement.json").write_text(json.dumps(dict(mode="candidate", acceptance_pass=False, trial=index+1)))
                (trial/"capture.json").write_text(bad_text)
                (trial/"score.json").write_text("{}")
                (trial/"ground-truth.json").write_text("[]")
                (trial/"profile.json").write_text('{"batches":[]}')
            subprocess.run([sys.executable, str(SCRIPT.with_name("hotpath-stress-artifact.py")), str(root), str(dest)], check=True)
            attempts = sorted((dest/"run-example").iterdir())
            self.assertEqual(len(attempts), 2)
            for attempt in attempts:
                retained = json.loads((attempt/"result.json").read_text())
                self.assertFalse(retained["acceptance_pass"])
                self.assertEqual(retained["artifact_export"]["scoring_read_error"], "JSONDecodeError")
                self.assertEqual({p.name for p in attempt.iterdir()}, {"profile.json", "result.json"})


if __name__ == "__main__":
    unittest.main()
