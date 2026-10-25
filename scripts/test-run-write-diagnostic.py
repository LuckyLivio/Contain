"""Frozen small-entry guards and raw evidence preservation; no ETW execution."""
import importlib.util
import io
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest
from types import SimpleNamespace
from unittest import mock

SPEC = importlib.util.spec_from_file_location("write_entry", Path(__file__).with_name("run-write-diagnostic.py"))
ENTRY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ENTRY)


class EntryTests(unittest.TestCase):
    def plan(self, root):
        inputs = []
        for name in ("contain", "fixture", "runner", "analyzer", "clock", "matrix", "scorer", "build"):
            filename = {"clock": "v04-clock-protocol.py", "scorer": "score-fixture.py"}.get(name, name)
            path = Path(ENTRY.__file__) if name == "runner" else root / filename
            if name != "runner":
                path.write_bytes(name.encode())
            inputs.append(dict(name=name, path=str(path), sha256=ENTRY.sha(path)))
        plan = dict(protocol="contain-write-diagnostic-entry-1", runtime_commit="a" * 40,
                    sequences=["A", "B", "C", "D"], files_per_sequence=2, workers=1,
                    formal_acceptance=False, output_parent=str(root), inputs=inputs)
        path = root / "plan.json"
        path.write_text(json.dumps(plan))
        return path, plan

    def database(self, root, state="finished"):
        path = root / "capture.db"
        connection = sqlite3.connect(path)
        connection.executescript("CREATE TABLE capture_runs(session_id TEXT,state TEXT);"
                                 "CREATE TABLE raw_stream(session_id TEXT,sequence INTEGER,document TEXT);")
        connection.execute("INSERT INTO capture_runs VALUES('session',?)", (state,))
        for sequence, kind in ((2, "completion"), (1, "lifecycle"), (3, "file")):
            connection.execute("INSERT INTO raw_stream VALUES('session',?,?)",
                               (sequence, json.dumps(dict(id=str(sequence), event_type=kind,
                                evidence=dict(source="EtwFile" if kind == "file" else "EtwProcess")))))
        connection.commit()
        connection.close()
        exported = root / "capture.json"
        exported.write_text(json.dumps(dict(data=dict(id="session", events=[dict(id="3", confidence="High")]))))
        return path, exported

    def test_tampered_binary_and_plan_are_rejected_before_launch(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, _ = self.plan(root)
            digest = ENTRY.sha(path)
            with self.assertRaisesRegex(ValueError, "plan SHA"):
                ENTRY.verified_plan(path, "0" * 64)
            (root / "contain").write_bytes(b"different")
            with self.assertRaisesRegex(ValueError, "input mismatch: contain"):
                ENTRY.verified_plan(path, digest)

    def test_fixed_workload_and_input_inventory_cannot_expand(self):
        with tempfile.TemporaryDirectory() as folder:
            path, plan = self.plan(Path(folder))
            plan["files_per_sequence"] = 1000
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError, "fixed eight-file"):
                ENTRY.verified_plan(path, ENTRY.sha(path))
            plan["files_per_sequence"] = 2
            plan["inputs"].append(plan["inputs"][0])
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError, "duplicate"):
                ENTRY.verified_plan(path, ENTRY.sha(path))

    def test_imported_sibling_must_be_the_frozen_file(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, plan = self.plan(root)
            wrong = root / "different-clock.py"
            wrong.write_bytes((root / "v04-clock-protocol.py").read_bytes())
            next(entry for entry in plan["inputs"] if entry["name"] == "clock")["path"] = str(wrong)
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError, "actual sibling"):
                ENTRY.verified_plan(path, ENTRY.sha(path))

    def test_partial_checkpoint_is_preserved_without_blocking_next_checkpoint(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "attempt.json"
            with mock.patch.object(ENTRY.json, "dump", side_effect=KeyboardInterrupt):
                with self.assertRaises(KeyboardInterrupt):
                    ENTRY.save(path, dict(status="running"))
            leftovers = list(Path(folder).glob("attempt.json.*.new"))
            self.assertEqual(len(leftovers), 1)
            ENTRY.save(path, dict(status="interrupted"))
            self.assertEqual(ENTRY.read(path)["status"], "interrupted")
            self.assertTrue(leftovers[0].exists())

    def test_preflight_does_not_launch_or_create_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, _ = self.plan(root)
            before = set(root.iterdir())
            with mock.patch("sys.argv", ["entry", "--plan", str(path), "--plan-sha256", ENTRY.sha(path), "--preflight"]), \
                    mock.patch.object(ENTRY.subprocess, "Popen") as launch:
                self.assertEqual(ENTRY.main(), 0)
                launch.assert_not_called()
            self.assertEqual(before, set(root.iterdir()))

    def test_inherited_experiment_switches_are_removed_not_empty_strings(self):
        with mock.patch.dict(os.environ, {"CONTAIN_CHECKPOINT_VARIANT": "", "CONTAIN_HOTPATH_VARIANT": "stale",
                                          "CONTAIN_ETW_EVENT_ID_FILTER": "0", "PATH": "keep"}, clear=True):
            environment = ENTRY.capture_environment(Path("diagnostic"))
        self.assertNotIn("CONTAIN_CHECKPOINT_VARIANT", environment)
        self.assertNotIn("CONTAIN_HOTPATH_VARIANT", environment)
        self.assertNotIn("CONTAIN_ETW_EVENT_ID_FILTER", environment)
        self.assertEqual(environment["PATH"], "keep")
        self.assertEqual(environment["CONTAIN_FILE_DIAGNOSTICS"], "1")

    def test_export_keeps_raw_completion_lifecycle_and_final_views_separate(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            database, original = self.database(root)
            hashes = ENTRY.sha(database), ENTRY.sha(original)
            result = root / "evidence.json"
            summary = ENTRY.export_raw(database, original, result)
            value = ENTRY.read(result)["data"]
            self.assertEqual([r["id"] for r in value["raw_events"]], ["1", "2", "3"])
            self.assertEqual(value["events"], [dict(id="3", confidence="High")])
            self.assertEqual(summary["rows"], 3)
            self.assertEqual(summary["source_file_records"], 1)
            self.assertEqual(summary["file_diagnostic_records"], 0)
            self.assertEqual(hashes, (ENTRY.sha(database), ENTRY.sha(original)))
            with self.assertRaises(FileExistsError):
                ENTRY.export_raw(database, original, result)
            self.assertEqual(hashes, (ENTRY.sha(database), ENTRY.sha(original)))

    def test_unfinished_database_and_sidecars_are_not_silently_exported(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            database, original = self.database(root, "running")
            with self.assertRaisesRegex(RuntimeError, "not finished"):
                ENTRY.export_raw(database, original, root / "evidence.json")
            Path(str(database) + "-wal").write_bytes(b"preserve")
            with self.assertRaisesRegex(RuntimeError, "sidecars"):
                ENTRY.export_raw(database, original, root / "evidence.json")
            self.assertEqual(Path(str(database) + "-wal").read_bytes(), b"preserve")
            self.assertFalse((root / "evidence.json").exists())

    def fake_run(self, root, interrupt):
        output = root / "output"
        output.mkdir()
        plan = dict(protocol="contain-write-diagnostic-entry-1", runtime_commit="a" * 40, output_parent=str(output))
        inputs = {name: root / name for name in ("contain", "fixture", "matrix", "analyzer")}
        process = SimpleNamespace(pid=123, returncode=None if interrupt else 0,
                                  poll=lambda: None if interrupt else 0)
        meter = SimpleNamespace(values=dict(parent_cpu_seconds=1.25), errors={"sample_limited"}, close=mock.Mock(),
                                sample=mock.Mock(side_effect=KeyboardInterrupt) if interrupt else mock.Mock())
        with mock.patch("sys.argv", ["entry", "--plan", "unused", "--plan-sha256", "unused"]), \
                mock.patch.object(ENTRY, "verified_plan", return_value=(plan, inputs, [])), \
                mock.patch.object(ENTRY.ctypes.windll.shell32, "IsUserAnAdmin", return_value=1), \
                mock.patch.object(ENTRY.tempfile, "gettempdir", return_value=str(root)), \
                mock.patch.object(ENTRY, "module", return_value=SimpleNamespace(ProcessMetrics=lambda pid: meter)), \
                mock.patch.object(ENTRY.subprocess, "Popen", return_value=process), \
                mock.patch.object(ENTRY.subprocess, "run") as command, \
                mock.patch.object(ENTRY, "export_raw", side_effect=sqlite3.OperationalError("synthetic database failure")), \
                mock.patch("sys.stdout", new_callable=io.StringIO):
            if interrupt:
                with self.assertRaises(KeyboardInterrupt):
                    ENTRY.main()
                command.assert_not_called()
            else:
                self.assertEqual(ENTRY.main(), 1)
                self.assertEqual(command.call_count, 1, "analysis must not start after export failure")
        meter.close.assert_called_once()
        return ENTRY.read(next(output.glob("admin-*/attempt.json")))

    def test_interrupt_keeps_partial_measurements_and_uncertain_child_state(self):
        with tempfile.TemporaryDirectory() as folder:
            record = self.fake_run(Path(folder), True)
        self.assertEqual(record["status"], "interrupted")
        self.assertEqual(record["failed_stage"], "capture_running")
        self.assertEqual(record["parent_metrics"]["parent_cpu_seconds"], 1.25)
        self.assertFalse(record["parent_metrics_complete"])
        self.assertTrue(record["child_still_running_at_entry_finish"])
        self.assertIsNone(record["capture_elapsed_seconds"])

    def test_database_exception_records_failure_instead_of_running(self):
        with tempfile.TemporaryDirectory() as folder:
            record = self.fake_run(Path(folder), False)
        self.assertEqual(record["status"], "failed")
        self.assertEqual(record["failed_stage"], "export")
        self.assertIn("OperationalError", record["error"])
        self.assertFalse(record["formal_acceptance"])


if __name__ == "__main__":
    unittest.main()
