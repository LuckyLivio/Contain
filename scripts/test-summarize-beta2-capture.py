"""Retain every ABBA sample and reject failed, incomplete, or unequal work."""
import copy
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location("beta2_comparison", Path(__file__).with_name("summarize-beta2-capture.py"))
S = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(S)


def inputs():
    plan = dict(protocol="contain-beta2-comparison-1", order=S.ORDER, maximum_native_captures=4,
                retries=0, background_files=12000, profile=False)
    execution = dict(attempted=S.ORDER, not_run=[], stop_reason=None)
    case = dict(workload_complete=True, accepted=True, background=dict(files=12000),
                counters=dict(etw_events_lost=0), stream=dict(quota_bytes=67108864),
                handoff=dict(record_capacity=4096, byte_capacity=16777216),
                follow_installer=dict(candidate_record_limit=2048, candidate_byte_limit=8388608, resource_limit=4096))
    return plan, execution, {name: copy.deepcopy(case) for name in S.ORDER}


class ComparisonContracts(unittest.TestCase):
    def test_zero_loss_both_versions_has_no_improvement_claim(self):
        result = S.summarize(*inputs())
        self.assertTrue(result["accepted"])
        self.assertFalse(result["source_loss_improvement_claim"])
        self.assertEqual(list(result["cases"]), S.ORDER)

    def test_every_candidate_failure_is_retained_and_rejected(self):
        for name in ["r1/candidate", "r2/candidate"]:
            args = inputs()
            args[2][name]["accepted"] = False
            result = S.summarize(*args)
            self.assertFalse(result["accepted"])
            self.assertFalse(result["cases"][name]["accepted"])

    def test_incomplete_work_or_changed_bounds_is_not_comparable(self):
        for flaw in ["background", "bounds", "workload", "missing"]:
            args = inputs()
            case = args[2]["r1/baseline"]
            if flaw == "background": case["background"]["files"] = 11999
            if flaw == "bounds": case["handoff"]["record_capacity"] = 8192
            if flaw == "workload": case["workload_complete"] = False
            if flaw == "missing": args[2].pop("r1/baseline")
            self.assertFalse(S.summarize(*args)["accepted"])

    def test_reordered_retried_or_unterminated_attempt_is_rejected(self):
        for flaw in ["order", "retry", "termination", "not_run"]:
            args = inputs()
            if flaw == "order": args[0]["order"] = list(reversed(S.ORDER))
            if flaw == "retry": args[0]["retries"] = 1
            if flaw == "termination": args[1]["stop_reason"] = "unverified"
            if flaw == "not_run": args[1]["not_run"] = ["r2/baseline"]
            self.assertFalse(S.summarize(*args)["accepted"])

    def test_positive_loss_change_requires_both_rounds(self):
        args = inputs()
        args[2]["r1/baseline"]["counters"]["etw_events_lost"] = 10
        self.assertFalse(S.summarize(*args)["source_loss_improvement_claim"])
        args[2]["r2/baseline"]["counters"]["etw_events_lost"] = 5
        self.assertTrue(S.summarize(*args)["source_loss_improvement_claim"])


if __name__ == "__main__":
    unittest.main()
