import copy
import importlib.util
from pathlib import Path
import unittest

spec=importlib.util.spec_from_file_location("oracle",Path(__file__).with_name("score-fixture.py"));oracle=importlib.util.module_from_spec(spec);spec.loader.exec_module(oracle)
class OracleTests(unittest.TestCase):
    def setUp(self):
        self.t=dict(role="worker",pid=7,creation_time="10",operation="write_requested",resource="C:\\root\\x",destination=None,start_ticks="100",end_ticks="200",success=True)
        self.e=dict(id="event",event_type="file",operation="write_requested",resource=self.t["resource"],timestamp_ticks="150",confidence="High",evidence=dict(pid=7,process_creation_time="10"))
        self.c=dict(events=[self.e],watch_roots=["C:\\root"],stats=dict(events_dropped=0),backend={},quality=dict(level="Degraded"))
    def test_event_cannot_satisfy_two_truth_operations(self):
        r=oracle.score(self.c,[self.t,copy.deepcopy(self.t)])
        self.assertEqual(r["observed"],1);self.assertEqual(r["groups"]["target_success"]["expected"],2)
        self.assertEqual(sum(x["attribution"] is None for x in r["rows"]),1)
    def test_unexpected_positive_is_audited_and_no_positive_precision_is_null(self):
        self.e["resource"]="C:\\root\\unexpected"
        r=oracle.score(self.c,[self.t]);self.assertEqual(r["false_positive_target_events"],1)
        self.e["confidence"]="Unknown"
        self.assertIsNone(oracle.score(self.c,[self.t])["precision"])
    def test_failed_noise_unsupported_and_unobserved_remain_separate(self):
        self.t["role"]="noise";self.e["confidence"]="Unknown"
        r=oracle.score(self.c,[self.t]);self.assertEqual(r["groups"]["noise_success"]["correctly_not_assigned_to_target_app"],1)
        self.t["success"]=False;r=oracle.score(self.c,[self.t]);self.assertEqual(r["groups"]["failed"]["expected"],1)
        self.t["operation"]="unsupported_diagnostic";r=oracle.score(self.c,[self.t]);self.assertEqual(r["groups"]["unsupported"]["unsupported_out_of_scope"],1)
    def test_wrong_actor_is_not_hidden_by_other_correct_raw_records(self):
        wrong=copy.deepcopy(self.e);wrong["id"]="wrong";wrong["evidence"]["pid"]=8;self.c["events"].append(wrong)
        r=oracle.score(self.c,[self.t]);self.assertEqual(r["correctly_attributed"],1);self.assertEqual(r["false_positive_target_events"],1)
    def test_overlapping_noise_interval_does_not_invalidate_correct_target(self):
        noise=copy.deepcopy(self.t);noise["role"]="noise";noise["pid"]=8
        self.assertEqual(oracle.score(self.c,[self.t,noise])["false_positive_target_events"],0)
    def test_snapshot_cannot_observe_a_transient_syscall(self):
        self.e["event_type"]="file_state";r=oracle.score(self.c,[self.t]);self.assertEqual(r["observed"],0)
    def test_control_exclusions_are_exact_and_never_override_instrumented_noise(self):
        self.e["resource"]="C:\\root\\nested\\.fixture-go"
        self.assertEqual(oracle.score(self.c,[self.t])["false_positive_target_events"],1)
        self.e["resource"]="C:\\root\\.fixture-go"
        self.assertEqual(oracle.score(self.c,[self.t])["excluded_control_positive_events"],1)
        self.t["resource"]=self.e["resource"];self.t["role"]="noise"
        self.assertEqual(oracle.score(self.c,[self.t])["false_positive_target_events"],1)
if __name__=="__main__": unittest.main()
