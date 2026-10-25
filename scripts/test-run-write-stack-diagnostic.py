"""Independent observer entry guards. All collectors and children are mocked."""
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest import mock

SPEC = importlib.util.spec_from_file_location("stack_entry", Path(__file__).with_name("run-write-stack-diagnostic.py"))
ENTRY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ENTRY)

SETTINGS = "\n".join(("FileIO", "FileIOInit", "Filename", "Loader", "ProcessThread", "FileCreate", "FileCleanup",
                       "FileClose", "FileWrite", "FileSetInformation", "FileDelete", "FileRename", "FileOpEnd"))


class StackEntryTests(unittest.TestCase):
    def plan(self, root):
        child_runner = Path(__file__).with_name("run-write-diagnostic.py")
        child_entries = []
        for name in ("contain", "fixture", "runner", "analyzer", "clock", "matrix", "scorer", "build"):
            filename = {"clock": "v04-clock-protocol.py", "scorer": "score-fixture.py"}.get(name, name)
            path = child_runner if name == "runner" else root / filename
            if name != "runner":
                path.write_bytes(name.encode())
            child_entries.append(dict(name=name, path=str(path), sha256=ENTRY.sha(path)))
        child = dict(protocol="contain-write-diagnostic-entry-1", runtime_commit="a" * 40,
                     sequences=["A", "B", "C", "D"], files_per_sequence=2, workers=1,
                     formal_acceptance=False, output_parent=str(root / "old-output"), inputs=child_entries)
        child_path = root / "child-plan.json"
        child_path.write_text(json.dumps(child))
        paths = dict(wrapper=Path(ENTRY.__file__), child_plan=child_path, child_runner=child_runner,
                     python=Path(sys.executable))
        for name in ("wpr", "xperf", "profile"):
            paths[name] = root / name
            paths[name].write_bytes(name.encode())
        plan = dict(protocol="contain-write-stack-entry-1", runtime_commit="a" * 40,
                    formal_acceptance=False, observer_seconds=60, profile_name="ContainWrite",
                    output_parent=str(root), inputs=[dict(name=name, path=str(path), sha256=ENTRY.sha(path))
                                                   for name, path in paths.items()])
        path = root / "plan.json"
        path.write_text(json.dumps(plan))
        return path, plan, child, paths

    def test_frozen_inputs_and_original_child_inputs_verified(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, _, _, _ = self.plan(root)
            _, _, wrapper_before, _, child_before = ENTRY.verified_plan(path, ENTRY.sha(path))
            self.assertEqual(len(wrapper_before), 7)
            self.assertEqual(len(child_before), 8)
            (root / "fixture").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "input mismatch: fixture"):
                ENTRY.verified_plan(path, ENTRY.sha(path))

    def test_wrapper_plan_and_tool_tampering_prevent_launch(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, _, _, _ = self.plan(root)
            digest = ENTRY.sha(path)
            with self.assertRaisesRegex(ValueError, "wrapper plan SHA"):
                ENTRY.verified_plan(path, "0" * 64)
            (root / "wpr").write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "input mismatch: wpr"):
                ENTRY.verified_plan(path, digest)

    def test_fixed_window_formal_gate_and_duplicate_inputs_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path, plan, _, _ = self.plan(Path(folder))
            for field, value, message in (("observer_seconds", 600, "60-second"),
                                          ("formal_acceptance", True, "acceptance request"),
                                          ("inputs", plan["inputs"] + [plan["inputs"][0]], "duplicate")):
                bad = dict(plan)
                bad[field] = value
                path.write_text(json.dumps(bad))
                with self.assertRaisesRegex(ValueError, message):
                    ENTRY.verified_plan(path, ENTRY.sha(path))

    def test_preflight_only_reads_inputs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, _, _, _ = self.plan(root)
            original = set(root.iterdir())
            with mock.patch("sys.argv", ["entry", "--plan", str(path), "--plan-sha256", ENTRY.sha(path), "--preflight"]), \
                    mock.patch.object(ENTRY.subprocess, "Popen") as launch, \
                    mock.patch.object(ENTRY.subprocess, "run") as run, mock.patch("sys.stdout", new_callable=io.StringIO):
                self.assertEqual(ENTRY.main(), 0)
                launch.assert_not_called()
                run.assert_not_called()
            self.assertEqual(original, set(root.iterdir()))

    def test_derived_child_plan_changes_only_output_parent(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            path, _, original, _ = self.plan(root)
            original_hash = ENTRY.sha(path)
            directory = root / "new"
            directory.mkdir()
            derived, parent = ENTRY.derived_child_plan(original, directory)
            current = ENTRY.read(derived)
            self.assertEqual({key for key in current if current[key] != original[key]}, {"output_parent"})
            self.assertEqual(current["inputs"], original["inputs"])
            self.assertTrue(parent.is_dir())
            self.assertEqual(ENTRY.sha(path), original_hash)
            with self.assertRaises(FileExistsError):
                ENTRY.derived_child_plan(original, directory)

    def test_absent_or_similar_instance_is_not_ownership(self):
        item = dict(exit=0, timed_out=False)
        name = ENTRY.collector_name("unique")
        self.assertFalse(ENTRY.owned_status(item, "WPR is not recording", "unique"))
        self.assertFalse(ENTRY.owned_status(item, "Collector Name: " + name + "-other", "unique"))
        self.assertFalse(ENTRY.owned_status(item, "Error: cannot query " + name, "unique"))
        self.assertTrue(ENTRY.owned_status(item, "Collector Name\t: " + name, "unique"))
        self.assertFalse(ENTRY.owned_status(dict(exit=1, timed_out=False), name, "unique"))
        self.assertFalse(ENTRY.owned_status(dict(exit=None, timed_out=True), name, "unique"))

    def test_required_active_settings_are_not_inferred_from_profile_name(self):
        self.assertEqual(ENTRY.required_file_settings(SETTINGS), [])
        self.assertIn("FileWrite", ENTRY.required_file_settings(SETTINGS.replace("FileWrite", "")))
        self.assertEqual(len(ENTRY.required_file_settings("ContainWrite.Verbose.File")), 13)

    def simulate(self, root, failure=None):
        _, plan, child_plan, inputs = self.plan(root)
        commands = []
        ticks = [0.0]
        process = SimpleNamespace(pid=2345, returncode=None)
        if failure in ("timeout", "interrupt"):
            def poll():
                if failure == "interrupt" and len([row for row in commands if row[0] == "observer-active"]) == 1 and not any("stop" in row[0] for row in commands):
                    raise KeyboardInterrupt
                return process.returncode
        else:
            process.returncode = 0 if failure != "child" else 7
            poll = lambda: process.returncode
        process.poll = poll

        def run(owner, label, command, timeout=10):
            command = [str(value) for value in command]
            commands.append((label, command, timeout))
            item = dict(label=label, command=command, exit=0, timed_out=False)
            owner.record["commands"].append(item)
            output = ""
            if label == "instance-before":
                output = "WPR is not recording"
                if failure == "prestatus":
                    item["exit"] = 5
                    output = "Access denied"
            if label == "observer-start":
                if failure == "start_interrupt":
                    raise KeyboardInterrupt
                if failure == "start":
                    item["exit"] = 5
            if label == "observer-active" or label.endswith("-status"):
                output = "Collector Name: " + ENTRY.collector_name(owner.record["instance"]) + "\n" + SETTINGS
                if failure == "unowned":
                    output = "WPR is not recording"
                if failure == "settings" and label == "observer-active":
                    output = output.replace("FileWrite", "")
                if failure == "stopstatus" and label.endswith("-status"):
                    output = "WPR is not recording"
            if label.endswith("-after-status") and failure not in ("stop_active", "stop_timeout"):
                output = "WPR is not recording"
            if label.endswith("-collector"):
                output = ENTRY.collector_name(owner.record["instance"])
            if label in ("observer-stop", "final-owned-stop"):
                if failure == "stop_timeout":
                    item.update(exit=None, timed_out=True)
                else:
                    Path(owner.record["trace_path"]).write_bytes(b"mock ETL not real stacks")
            return item, output

        with mock.patch.object(ENTRY.Commands, "run", autospec=True, side_effect=run), \
                mock.patch.object(ENTRY.subprocess, "Popen", return_value=process) as popen, \
                mock.patch.object(ENTRY.time, "monotonic", side_effect=lambda: ticks[0]), \
                mock.patch.object(ENTRY.time, "sleep", side_effect=lambda seconds: ticks.__setitem__(0, ticks[0] + seconds)), \
                mock.patch("sys.stdout", new_callable=io.StringIO):
            directory, record = ENTRY.launch(plan, inputs, [], child_plan, [])
        self.assertFalse(record["formal_acceptance"])
        for label, command, _ in commands:
            if "-stop" in command or "-start" in command:
                self.assertEqual(command[-2:], ["-instancename", record["instance"]])
            self.assertNotIn("-cancel", command)
        self.assertLessEqual(popen.call_count, 1)
        self.assertFalse(hasattr(process, "terminate"))
        self.assertFalse(hasattr(process, "kill"))
        return directory, record, commands, popen.call_count

    def test_success_records_pending_not_verified_stacks(self):
        with tempfile.TemporaryDirectory() as folder:
            directory, record, commands, launches = self.simulate(Path(folder))
            self.assertEqual(record["status"], "completed_pending_stack_and_event_review")
            self.assertEqual(record["trace_stack_evidence"], "configured_not_verified")
            self.assertIsNone(record["stack_count"])
            self.assertTrue(record["observer_stop_succeeded"])
            self.assertFalse(record["observer_recovery_required"])
            self.assertEqual(launches, 1)
            self.assertEqual(len([row for row in commands if "-stop" in row[1]]), 1)
            self.assertTrue((directory / "attempt.json").exists())

    def test_start_failure_never_launches_or_stops_uncertain_trace(self):
        for failure in ("start", "start_interrupt"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as folder:
                _, record, commands, launches = self.simulate(Path(folder), failure)
                self.assertEqual(launches, 0)
                self.assertTrue(record["observer_recovery_required"])
                self.assertFalse(any("-stop" in row[1] for row in commands))

    def test_failed_prestatus_never_launches_observer(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, launches = self.simulate(Path(folder), "prestatus")
        self.assertEqual(launches, 0)
        self.assertFalse(any("-start" in row[1] for row in commands))
        self.assertEqual(record["status"], "failed")

    def test_no_named_ownership_never_launches_child_or_stops(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, launches = self.simulate(Path(folder), "unowned")
        self.assertEqual(launches, 0)
        self.assertFalse(any("-stop" in row[1] for row in commands))
        self.assertTrue(record["observer_recovery_required"])

    def test_missing_actual_settings_stop_owned_observer_without_child(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, launches = self.simulate(Path(folder), "settings")
        self.assertEqual(launches, 0)
        self.assertEqual(record["active_file_settings_missing"], ["FileWrite"])
        self.assertEqual(len([row for row in commands if "-stop" in row[1]]), 1)

    def test_timeout_stops_owned_observer_once_and_retains_live_child(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, launches = self.simulate(Path(folder), "timeout")
        self.assertEqual(launches, 1)
        self.assertTrue(record["child_still_running"])
        self.assertEqual(record["observer_stop_reason"], "observer_time_limit_child_not_killed")
        self.assertEqual(record["status"], "failed")
        self.assertEqual(len([row for row in commands if "-stop" in row[1]]), 1)

    def test_interrupt_stops_only_owned_observer_and_preserves_child_state(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, _ = self.simulate(Path(folder), "interrupt")
        self.assertEqual(record["status"], "interrupted")
        self.assertTrue(record["child_still_running"])
        self.assertEqual(len([row for row in commands if "-stop" in row[1]]), 1)

    def test_stop_timeout_is_retained_without_retry_or_false_completion(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, _ = self.simulate(Path(folder), "stop_timeout")
        self.assertEqual(record["status"], "failed")
        self.assertTrue(record["observer_recovery_required"])
        self.assertTrue(record["observer_stop_controller_timed_out"])
        self.assertEqual(len([row for row in commands if "-stop" in row[1]]), 1)

    def test_stop_identity_failure_is_not_retried(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, _ = self.simulate(Path(folder), "stopstatus")
        self.assertFalse(any("-stop" in row[1] for row in commands))
        self.assertEqual(len([row for row in commands if row[0].endswith("-status")]), 1)
        self.assertTrue(record["observer_recovery_required"])

    def test_stop_exit_zero_with_still_active_instance_is_not_success(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, commands, _ = self.simulate(Path(folder), "stop_active")
        self.assertTrue(record["observer_stop_command_succeeded"])
        self.assertFalse(record["observer_absent_after_stop"])
        self.assertFalse(record["observer_stop_succeeded"])
        self.assertTrue(record["observer_recovery_required"])
        self.assertIsNone(record["retained_observer_bytes"])
        self.assertEqual(len([row for row in commands if "-stop" in row[1]]), 1)

    def test_child_failure_preserved_after_successful_observer_stop(self):
        with tempfile.TemporaryDirectory() as folder:
            _, record, _, launches = self.simulate(Path(folder), "child")
        self.assertEqual(launches, 1)
        self.assertEqual(record["child_exit"], 7)
        self.assertEqual(record["status"], "failed")
        self.assertTrue(record["observer_stop_succeeded"])

    def test_multiple_child_attempts_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in ("admin-one", "admin-two"):
                (root / name).mkdir()
                (root / name / "attempt.json").write_text("{}")
            with self.assertRaisesRegex(RuntimeError, "More than one"):
                ENTRY.read_child_state(root)

    def test_live_trace_hash_is_unavailable_not_a_stable_digest(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "trace.etl"
            path.write_bytes(b"still writing")
            self.assertIsNone(ENTRY.artifact_file(path, False)["sha256"])
            self.assertEqual(ENTRY.artifact_file(path, True)["sha256"], ENTRY.sha(path))


if __name__ == "__main__":
    unittest.main()
