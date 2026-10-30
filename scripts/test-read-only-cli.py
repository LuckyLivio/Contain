"""Validator regressions only; native Windows integration is a separate CI step."""
import ast
import copy
import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import unittest


SPEC = importlib.util.spec_from_file_location("cli_validation", Path(__file__).with_name("verify-read-only-cli.py"))
CLI = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CLI)


def fixture(case):
    """Minimal synthetic validator input, never a claimed CLI execution result."""
    app = "synthetic-" + case
    interrupted = case == "interrupted"
    capture = dict(id=app, name="Synthetic " + case, installer=CLI.INSTALLER,
                   watch_roots=[CLI.ROOT], registry_key=None, started_at="2026-10-01T00:00:00Z",
                   registry=[], inventory=[], warnings=["SYNTHETIC model fixture"],
                   processes=[dict(pid=100, creation_time=str(CLI.BIRTH), image=CLI.INSTALLER, confidence="Certain")],
                   capture_state="finalizing" if interrupted else "finished",
                   quality=dict(level="Incomplete" if interrupted else "Degraded"),
                   finished_at="" if interrupted else "2026-10-01T00:00:02Z", exit_code=None if interrupted else 0,
                   files=[] if interrupted else [dict(path=CLI.RESOURCE, confidence="Unknown")],
                   operations=[] if interrupted else [{}, {}, {}],
                   backend=dict(etw_process="active", etw_file="active", etw_registry="not_requested",
                                stream=dict(persisted=3), etw_events_lost=None if interrupted else 0,
                                etw_buffers_lost=None if interrupted else 0), events=[])
    for i in range(3):
        missing_actor = case == "historical" and i == 1
        historical = case == "historical" and i == 0
        unknown = interrupted or missing_actor
        event = dict(id=f"{app}-event-{i}", resource=CLI.RESOURCE, event_type="file", operation="write_requested",
                     timestamp_ticks=str(CLI.BIRTH + 10 + i), confidence="Unknown" if unknown else "High",
                     success=None if unknown else True, state_validated=False,
                     dimensions=dict(actor="Unknown" if unknown else "High", operation="Unknown" if unknown else "High",
                                     resource="Unknown" if interrupted else "High"),
                     evidence=dict(pid=None if missing_actor else 100,
                                   process_creation_time=None if missing_actor else str(CLI.BIRTH)),
                     raw=dict(file_object=f"synthetic-object-{i}", object_generation=f"synthetic-generation-{i}",
                              status=None if missing_actor else 0))
        if not (case == "historical" and i == 2):
            event["raw"]["file_name"] = dict(current_path_verified=False,
                path_source="file_object_cache" if historical else "supplied_FileName",
                observed_ticks=str(CLI.BIRTH + (1 if historical else 10 + i)),
                source_event_id="synthetic-earlier-open" if historical else event["id"])
        capture["events"].append(event)
    return capture


class ReadOnlyCliTests(unittest.TestCase):
    def test_all_assertions_have_safe_public_check_identifiers(self):
        tree = ast.parse(Path(CLI.__file__).read_text(encoding="utf-8"))
        for node in ast.walk(tree):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "require":
                self.assertIsInstance(node.args[1], ast.Constant)
                message = node.args[1].value
                self.assertIn(message, CLI.SAFE_CHECK_MESSAGES)
                diagnostic = CLI.safe_failure(ValueError(message))
                self.assertEqual(diagnostic["check_message"], message)
                self.assertRegex(diagnostic["check_code"], r"^[a-z0-9_]+$")

    def test_public_failure_diagnostics_never_include_private_exception_text(self):
        private = r"C:\Users\private\secret.db"
        for error in (OSError(private), ValueError(private), KeyError(private), TypeError(private),
                      sqlite3.OperationalError(private), subprocess.TimeoutExpired([private], 120),
                      json.JSONDecodeError(private, private, 0)):
            with self.subTest(error=type(error).__name__):
                report = CLI.safe_failure(error)
                self.assertNotIn(private, json.dumps(report))
                self.assertNotIn("private", report["check_message"])
                self.assertEqual(report["failure"], "local_validation_failed")
        self.assertEqual(CLI.safe_failure(ValueError("unavailable loss counter changed"))["check_code"],
                         "unavailable_loss_counter_changed")

    def test_known_synthetic_contracts(self):
        for case in CLI.CASES:
            with self.subTest(case=case):
                CLI.validate_fixture(case, fixture(case))

    def test_json_stdout_must_be_one_document(self):
        good = json.dumps(dict(schema_version=3, kind="inspect", data={}))
        self.assertEqual(CLI.document(good + "\n", "inspect"), {})
        for bad in ("Loading\n" + good, good + "\nDone", good + good):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                CLI.document(bad, "inspect")

    def test_wrong_json_envelope_is_rejected(self):
        for version, kind in ((2, "inspect"), (3, "history")):
            with self.subTest(version=version, kind=kind), self.assertRaises(ValueError):
                CLI.document(json.dumps(dict(schema_version=version, kind=kind, data={})), "inspect")

    def test_unknown_actor_and_state_cannot_be_promoted(self):
        capture = fixture("historical")
        capture["events"][1]["confidence"] = "High"
        with self.assertRaisesRegex(ValueError, "promoted"):
            CLI.validate_fixture("historical", capture)
        capture = fixture("finished")
        capture["files"][0]["confidence"] = "High"
        with self.assertRaisesRegex(ValueError, "ownership"):
            CLI.validate_fixture("finished", capture)

    def test_valid_actor_evidence_cannot_be_erased(self):
        capture = fixture("finished")
        capture["events"][0]["evidence"]["process_creation_time"] = None
        with self.assertRaisesRegex(ValueError, "actor evidence"):
            CLI.validate_fixture("finished", capture)

    def test_actor_name_and_completion_dimensions_stay_separate(self):
        capture = fixture("historical")
        capture["events"][1]["dimensions"]["actor"] = "High"
        with self.assertRaisesRegex(ValueError, "dimensions"):
            CLI.validate_fixture("historical", capture)
        capture = fixture("historical")
        capture["events"][1]["raw"]["status"] = 0
        with self.assertRaisesRegex(ValueError, "completion"):
            CLI.validate_fixture("historical", capture)

    def test_history_name_and_legacy_provenance_remain_distinct(self):
        capture = fixture("historical")
        capture["events"][0]["raw"]["file_name"]["current_path_verified"] = True
        with self.assertRaisesRegex(ValueError, "current path"):
            CLI.validate_fixture("historical", capture)
        capture = fixture("historical")
        capture["events"][2]["raw"]["file_name"] = copy.deepcopy(capture["events"][1]["raw"]["file_name"])
        with self.assertRaisesRegex(ValueError, "legacy"):
            CLI.validate_fixture("historical", capture)

    def test_exact_decimal_ticks_not_rounded_numbers(self):
        for field in ("timestamp_ticks", "observed_ticks"):
            capture = fixture("finished")
            target = capture["events"][0]
            if field == "observed_ticks":
                target = target["raw"]["file_name"]
            target[field] = int(target[field])
            with self.subTest(field=field), self.assertRaises(ValueError):
                CLI.validate_fixture("finished", capture)

    def test_interrupted_unavailable_is_not_zero(self):
        for key in ("etw_events_lost", "etw_buffers_lost"):
            capture = fixture("interrupted")
            capture["backend"][key] = 0
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "unavailable"):
                CLI.validate_fixture("interrupted", capture)
        sections = CLI.golden_sections("interrupted")
        sections["INSPECT"] = sections["INSPECT"].replace("ETW events=unavailable", "ETW events=0")
        with self.assertRaises(ValueError):
            CLI.reviewed_text("interrupted", sections)

    def test_missing_and_changed_ids_or_generations_are_rejected(self):
        for mutation in (lambda c: c["events"].pop(),
                         lambda c: c["events"][0].update(id="private-event"),
                         lambda c: c["events"][1]["raw"].update(object_generation="synthetic-generation-0")):
            capture = fixture("finished")
            mutation(capture)
            with self.assertRaises(ValueError):
                CLI.validate_fixture("finished", capture)

    def test_reviewed_examples_keep_exact_trailing_newlines(self):
        for case in CLI.CASES:
            sections = CLI.golden_sections(case)
            self.assertEqual(set(sections), {"INSPECT", "DETAIL", "HISTORY", "EXPLAIN"})
            self.assertTrue(all(text.endswith("\n") for text in sections.values()))
            self.assertTrue(CLI.reviewed_text(case, sections).startswith("SYNTHETIC — actual Windows"))

    def test_publication_rejects_private_warning_even_with_synthetic_id(self):
        sections = CLI.golden_sections("finished")
        sections["INSPECT"] += "Warning: C:\\Users\\private\\secret\n"
        with self.assertRaisesRegex(ValueError, "reviewed"):
            CLI.reviewed_text("finished", sections)
        capture = fixture("finished")
        capture["watch_roots"] = [r"C:\Users\private"]
        with self.assertRaisesRegex(ValueError, "scope"):
            CLI.validate_fixture("finished", capture)

    def test_text_pages_require_correct_event_ids_and_order(self):
        good = "time | event=e0 | write\ntime | event=e1 | write\nsource event: e0\n"
        CLI.check_page_text(good, ["e0", "e1"], ["e0", "e1", "e2"])
        CLI.check_page_text("No source events.\n", [], ["e0"])
        for expected in (["e1", "e0"], ["e0"], []):
            with self.subTest(expected=expected), self.assertRaises(ValueError):
                CLI.check_page_text(good, expected, ["e0", "e1", "e2"])


if __name__ == "__main__":
    unittest.main()
