import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("demo_measure", Path(__file__).with_name("demo-measure.py"))
MEASURE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MEASURE)


class CaptureMeasurementTests(unittest.TestCase):
    def test_exact_argument_boundaries_and_failure_exit_are_preserved(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            command = [sys.executable, "-c", "import sys; print(repr(sys.argv[1:])); print('diagnostic', file=sys.stderr); sys.exit(7)",
                       "space and quote\"", "--option-like", ""]
            code = MEASURE.measure(command, directory, directory / "absent.db")
            self.assertEqual(code, 7)
            self.assertIn(repr(command[3:]), (directory / "capture.stdout.log").read_text())
            self.assertIn("diagnostic", (directory / "capture.stderr.log").read_text())
            report = json.loads((directory / "measurement.json").read_text())
            self.assertEqual(report["capture_exit"], 7)
            self.assertIsNone(report["sqlite_bytes"])
            self.assertGreater(report["elapsed_seconds"], 0)

    def test_launch_failure_keeps_unavailable_metrics_and_reason(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            with self.assertRaises(OSError):
                MEASURE.measure([str(directory / "nonexistent.exe")], directory, directory / "absent.db")
            report = json.loads((directory / "measurement.json").read_text())
            self.assertIsNone(report["capture_exit"])
            self.assertIsNone(report["parent_cpu_seconds"])
            self.assertIsNone(report["parent_io"])
            self.assertIsNone(report["sampled_db_peak_bytes"])
            self.assertIsNone(report["sampled_wal_peak_bytes"])
            self.assertEqual(report["filesystem_samples"], 0)
            self.assertTrue(report["sampling_errors"])

    def test_previous_measurement_is_never_overwritten_or_command_launched(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            (directory / "measurement.json").write_text("original evidence")
            with patch.object(MEASURE.subprocess, "Popen") as spawn:
                with self.assertRaises(FileExistsError):
                    MEASURE.measure([sys.executable], directory, directory / "absent.db")
                spawn.assert_not_called()
            self.assertEqual((directory / "measurement.json").read_text(), "original evidence")

    def test_diagnostic_mode_and_final_database_size_are_explicit(self):
        with tempfile.TemporaryDirectory() as root, patch.dict(MEASURE.os.environ, {"CONTAIN_ETW_RAW_CLOCK_PATH": "private.json"}):
            directory = Path(root)
            database = directory / "sample.db"
            database.write_bytes(b"sample")
            self.assertEqual(MEASURE.measure([sys.executable, "-c", "pass"], directory, database), 0)
            report = json.loads((directory / "measurement.json").read_text())
            self.assertTrue(report["raw_clock_enabled"])
            self.assertEqual(report["sqlite_bytes"], 6)
            self.assertIn("not physical disk", report["measurement"])

    def test_final_database_stat_failure_retains_exit_and_unavailable_measurement(self):
        with tempfile.TemporaryDirectory() as root:
            directory = Path(root)
            database = directory / "inaccessible.db"
            # Sampling succeeds; only the final stat fails. This must not leave
            # an empty report or turn a successful capture into a failed launch.
            with patch.object(MEASURE.MATRIX, "sampled_size", return_value=0), \
                    patch.object(Path, "stat", side_effect=PermissionError("injected final stat failure")):
                self.assertEqual(MEASURE.measure([sys.executable, "-c", "pass"], directory, database), 0)
            report = json.loads((directory / "measurement.json").read_text())
            self.assertEqual(report["capture_exit"], 0)
            self.assertIsNone(report["sqlite_bytes"])
            self.assertTrue(any(error.startswith("final_database_stat: PermissionError:")
                                for error in report["sampling_errors"]))


if __name__ == "__main__":
    unittest.main()
