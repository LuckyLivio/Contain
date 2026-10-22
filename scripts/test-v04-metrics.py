import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location("metrics", Path(__file__).with_name("v04-metrics.py"))
METRICS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(METRICS)


class MetricTests(unittest.TestCase):
    def score(self):
        return dict(positive_raw_predictions=4, false_positive_target_events=1, groups=dict(
            target_success=dict(expected=10, correctly_attributed=3, incorrectly_attributed=1, observed=6, unobserved=4, observed_but_unresolved=2),
            noise_success=dict(expected=5, correctly_attributed=0, incorrectly_attributed=0, observed=4, unobserved=1, observed_but_unresolved=1),
            failed=dict(expected=2), unsupported=dict(expected=4)))

    def test_known_other_actor_does_not_inflate_unknown_and_failed_is_separate(self):
        report = METRICS.metrics(self.score())
        self.assertEqual(report["precision"], .75)
        self.assertEqual(report["recall"], .3)
        self.assertEqual(report["unknown_rate"], 8 / 15)
        self.assertEqual(report["false_attribution_rate"], 1 / 15)
        self.assertEqual(report["raw_false_discovery_rate"], .25)
        self.assertIsNone(report["event_loss_rate"])
        self.assertIsNone(report["etw_events_lost"])

    def test_no_positive_does_not_mean_perfect_precision(self):
        score = self.score()
        score.update(positive_raw_predictions=0, false_positive_target_events=0)
        self.assertIsNone(METRICS.metrics(score)["precision"])
        self.assertIsNone(METRICS.metrics(score)["raw_false_discovery_rate"])


if __name__ == "__main__":
    unittest.main()
