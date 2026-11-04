import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("interference", Path(__file__).with_name("read-interference.py"))
interference = importlib.util.module_from_spec(spec)
spec.loader.exec_module(interference)


class InterferenceTests(unittest.TestCase):
    def test_failure_is_counted_with_error_and_different_final_bytes(self):
        case = {"case": "overwrite", "during": {"operation": {"success": False, "error_code": 32},
            "final_state": [{"bytes": [1]}]}, "after_release_control": {
            "operation": {"success": True}, "final_state": [{"bytes": [2]}]}}
        result = interference.summarize([{"arm": "candidate", "result": {"cases": [case]}}])[0]
        self.assertEqual(result["attempts"], 1)
        self.assertEqual(result["success"], 0)
        self.assertEqual(result["error_codes"], {"32": 1})
        self.assertEqual(result["after_release_success"], 1)
        self.assertEqual(result["final_state_matches_control"], 0)

    def test_missing_harness_result_is_not_counted_as_success(self):
        self.assertEqual(interference.summarize([{"arm": "baseline", "result": None}]), [])


if __name__ == "__main__":
    unittest.main()
