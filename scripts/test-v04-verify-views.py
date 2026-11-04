import copy
import importlib.util
from pathlib import Path
import unittest


SPEC = importlib.util.spec_from_file_location("views", Path(__file__).with_name("v04-verify-views.py"))
VIEWS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VIEWS)


class ViewTests(unittest.TestCase):
    def setUp(self):
        event = dict(id="e", event_type="file", confidence="High", reason="verified ancestry", success=None,
                     evidence=dict(pid=1, process_creation_time="2"))
        edge = {"from": "process:1:2", "to": "event:e", "confidence": "High"}
        self.capture = dict(id="a", events=[event], files=[dict(confidence="Unknown")], registry=[], inventory=[], operations=[],
                            quality=dict(level="Degraded"), edges=[edge], processes=[dict(pid=1, creation_time="2", confidence="Certain")])
        self.history = dict(app_id="a", events=copy.deepcopy(self.capture["events"]))
        self.diff = dict(app_id="a", **{key: copy.deepcopy(self.capture[key]) for key in ("files", "registry", "inventory", "operations", "quality")})
        self.explanations = {"e": dict(app_id="a", event=copy.deepcopy(event), quality=dict(level="Degraded"), operations=[], edges=[edge])}

    def verify(self):
        return VIEWS.verify(self.capture, self.history, self.diff, self.history["events"], self.explanations)

    def test_same_source_event_and_unknown_snapshot_are_consistent(self):
        self.assertTrue(self.verify()["acceptance_pass"])

    def test_history_or_explain_promoting_unknown_is_rejected(self):
        self.history["events"][0]["confidence"] = "Certain"
        self.assertFalse(self.verify()["acceptance_pass"])
        self.history["events"] = copy.deepcopy(self.capture["events"])
        self.explanations["e"]["event"]["confidence"] = "Certain"
        self.assertFalse(self.verify()["acceptance_pass"])

    def test_diff_state_conflict_is_rejected(self):
        self.diff["files"][0]["confidence"] = "High"
        self.assertFalse(self.verify()["acceptance_pass"])

    def test_missing_ancestry_chain_is_rejected(self):
        self.explanations["e"]["edges"] = []
        self.assertIn("positive source event lacks chain to installer instance: e", self.verify()["issues"])

    def test_partial_explanation_checks_are_explicitly_incomplete(self):
        self.explanations = {}
        result = self.verify()
        self.assertTrue(result["checks_pass"])
        self.assertFalse(result["complete"])
        self.assertFalse(result["acceptance_pass"])

    def test_missing_page_and_duplicate_event_are_rejected(self):
        result = VIEWS.verify(self.capture, self.history, self.diff, [], self.explanations)
        self.assertFalse(result["acceptance_pass"])
        self.capture["events"].append(copy.deepcopy(self.capture["events"][0]))
        self.assertIn("inspect contains duplicate event IDs", self.verify()["issues"])


if __name__ == "__main__":
    unittest.main()
