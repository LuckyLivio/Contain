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

    def fake_run(self, root, interrupt, identity_variant=False):
        output = root / "output"
        output.mkdir()
        plan = dict(protocol="contain-write-diagnostic-entry-1", runtime_commit="a" * 40, output_parent=str(output))
        if identity_variant:
            plan.update(protocol="contain-write-diagnostic-entry-2", fixture_variant="transient_metadata_handles_v1")
        inputs = {name: root / name for name in ("contain", "fixture", "matrix", "analyzer")}
        process = SimpleNamespace(pid=123, returncode=None if interrupt else 0,
                                  poll=lambda: None if interrupt else 0)
        meter = SimpleNamespace(values=dict(parent_cpu_seconds=1.25), errors={"sample_limited"}, close=mock.Mock(),
                                sample=mock.Mock(side_effect=KeyboardInterrupt) if interrupt else mock.Mock())
        # Model only the Windows entry boundary; subprocesses and measurements
        # remain mocked. Do not alter global os.name (Path uses the host OS).
        admin = mock.Mock(return_value=1)
        windows_api = SimpleNamespace(windll=SimpleNamespace(shell32=SimpleNamespace(IsUserAnAdmin=admin)))
        with mock.patch("sys.argv", ["entry", "--plan", "unused", "--plan-sha256", "unused"]), \
                mock.patch.object(ENTRY, "verified_plan", return_value=(plan, inputs, [])), \
                mock.patch.object(ENTRY, "os", SimpleNamespace(name="nt", environ=os.environ)), \
                mock.patch.object(ENTRY, "ctypes", windows_api), \
                mock.patch.object(ENTRY.subprocess, "CREATE_NEW_PROCESS_GROUP", 0x200, create=True), \
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
        admin.assert_called_once_with()
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

    def test_identity_variant_is_selected_explicitly_without_changing_workload_arguments(self):
        with tempfile.TemporaryDirectory() as folder:
            record = self.fake_run(Path(folder), True, identity_variant=True)
        command = record["command"]
        self.assertEqual(command[-1], "--observe-file-identity")
        self.assertEqual(command.count("--observe-file-identity"), 1)
        self.assertNotIn("-start", command)
        self.assertEqual(record["fixture_variant"], "transient_metadata_handles_v1")
        self.assertEqual(record["workload_expected_api_calls"], 16)
        self.assertEqual(record["observer_expected_samples"], 28)
        self.assertIsNone(record["identity_observer_artifact"]["sha256"])
        self.assertIsNone(record["identity_observer_artifact"]["observed_samples"])

    def test_identity_protocol_pins_actual_python_and_rejects_wrong_variant(self):
        with tempfile.TemporaryDirectory() as folder:
            path, plan = self.plan(Path(folder))
            plan.update(protocol="contain-write-diagnostic-entry-2", fixture_variant="transient_metadata_handles_v1")
            interpreter = Path(ENTRY.sys.executable)
            plan["inputs"].append(dict(name="python", path=str(interpreter), sha256=ENTRY.sha(interpreter)))
            path.write_text(json.dumps(plan))
            _, inputs, observed = ENTRY.verified_plan(path, ENTRY.sha(path))
            self.assertEqual(inputs["python"].resolve(), interpreter.resolve())
            self.assertEqual(len(observed), 9)
            plan["fixture_variant"] = "unknown"
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError, "fixture variant"):
                ENTRY.verified_plan(path, ENTRY.sha(path))

    def test_identity_variant_cannot_be_silently_enabled_in_original_protocol(self):
        with tempfile.TemporaryDirectory() as folder:
            path, plan = self.plan(Path(folder))
            plan["fixture_variant"] = "transient_metadata_handles_v1"
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError, "original protocol"):
                ENTRY.verified_plan(path, ENTRY.sha(path))

    def test_identity_interpreter_is_required_and_must_be_the_actual_executable(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, plan = self.plan(root)
            plan.update(protocol="contain-write-diagnostic-entry-2", fixture_variant="transient_metadata_handles_v1")
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError, "Missing"):
                ENTRY.verified_plan(path, ENTRY.sha(path))
            fake_python = root / "different-python.exe"
            fake_python.write_bytes(b"does not execute")
            plan["inputs"].append(dict(name="python", path=str(fake_python), sha256=ENTRY.sha(fake_python)))
            path.write_text(json.dumps(plan))
            with self.assertRaisesRegex(ValueError, "interpreter"):
                ENTRY.verified_plan(path, ENTRY.sha(path))

    def test_identity_preflight_never_creates_artifacts_or_launches_a_process(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, plan = self.plan(root)
            interpreter = Path(ENTRY.sys.executable)
            plan.update(protocol="contain-write-diagnostic-entry-2", fixture_variant="transient_metadata_handles_v1")
            plan["inputs"].append(dict(name="python", path=str(interpreter), sha256=ENTRY.sha(interpreter)))
            path.write_text(json.dumps(plan))
            before = set(root.iterdir())
            with mock.patch("sys.argv", ["entry", "--plan", str(path), "--plan-sha256", ENTRY.sha(path), "--preflight"]), \
                    mock.patch.object(ENTRY.subprocess, "Popen") as launch, \
                    mock.patch.object(ENTRY.subprocess, "run") as run, mock.patch("sys.stdout", new_callable=io.StringIO) as stdout:
                self.assertEqual(ENTRY.main(), 0)
                launch.assert_not_called()
                run.assert_not_called()
            self.assertEqual(before, set(root.iterdir()))
            self.assertEqual(json.loads(stdout.getvalue())["fixture_variant"], "transient_metadata_handles_v1")

    def test_observer_inventory_never_turns_missing_live_or_malformed_evidence_into_counts(self):
        with tempfile.TemporaryDirectory() as folder:
            truth = Path(folder)
            missing = ENTRY.identity_observer_artifact(truth, True, True)
            self.assertEqual(missing["availability"], "unavailable_missing")
            self.assertIsNone(missing["observed_samples"])
            self.assertIsNone(missing["bytes"])
            path = truth / "identity-observations.jsonl"
            path.write_bytes(b'{"partial":')
            with mock.patch.object(ENTRY, "sha") as digest:
                live = ENTRY.identity_observer_artifact(truth, True, False)
                digest.assert_not_called()
            self.assertIsNone(live["sha256"])
            self.assertIsNone(live["observed_samples"])
            retained = ENTRY.identity_observer_artifact(truth, True, True)
            self.assertEqual(retained["availability"], "present_unvalidated")
            self.assertEqual(retained["sha256"], ENTRY.sha(path))
            self.assertIsNone(retained["observed_samples"])
            self.assertEqual(retained["semantic_validation"], "unavailable_not_performed_by_entry")
            self.assertEqual(path.read_bytes(), b'{"partial":')

    def test_observer_inventory_size_bound_does_not_hash_or_claim_complete_data(self):
        with tempfile.TemporaryDirectory() as folder:
            truth = Path(folder)
            path = truth / "identity-observations.jsonl"
            with path.open("wb") as stream:
                stream.truncate(4 * 1024 * 1024 + 1)
            with mock.patch.object(ENTRY, "sha") as digest:
                result = ENTRY.identity_observer_artifact(truth, True, True)
                digest.assert_not_called()
            self.assertEqual(result["availability"], "unavailable_size_limit")
            self.assertIsNone(result["observed_samples"])
            self.assertIsNone(result["sha256"])


if __name__ == "__main__":
    unittest.main()
