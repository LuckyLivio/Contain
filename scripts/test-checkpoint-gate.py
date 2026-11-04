import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("checkpoint_gate", Path(__file__).with_name("checkpoint-gate.py"))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def record(variant="E1"):
    return dict(acceptance_pass=True, backend=dict(stream=dict(
        checkpoint_variant=variant, sqlite_synchronous=1, sqlite_journal_mode="wal",
        wal_autocheckpoint_pages=0 if variant == "E1" else 4096,
        wal_budget_bytes=gate.WAL_BUDGET_BYTES, wal_peak_bytes=1024, wal_samples=2,
        disk_reserve_bytes=64*1024*1024, disk_free_min_bytes=2*1024*1024*1024,
        wal_budget_exceeded=False, error=None,
        checkpoint=(dict(mode="PASSIVE", busy=0, log_frames=8, checkpointed_frames=8, error=None) if variant == "E1" else None),
        checkpoint_completed=variant == "E1", restored_wal_autocheckpoint_pages=4096,
    )))


class CheckpointGateTests(unittest.TestCase):
    def test_both_valid_variants_and_original_failure_preserved(self):
        for variant in ("E0", "E1"):
            with self.subTest(variant=variant):
                self.assertTrue(gate.validate(record(variant), variant)["acceptance_pass"])
                failed = record(variant)
                failed["acceptance_pass"] = False
                self.assertFalse(gate.validate(failed, variant)["acceptance_pass"])

    def test_busy_zero_is_insufficient_for_passive_completion(self):
        for frames in (3, -1, None):
            bad = record()
            bad["backend"]["stream"]["checkpoint"]["checkpointed_frames"] = frames
            self.assertFalse(gate.validate(bad, "E1")["acceptance_pass"])

    def test_storage_errors_reject_even_when_original_quality_passes(self):
        changes = dict(error="disk sample failed", wal_budget_exceeded=True,
                       restored_wal_autocheckpoint_pages=0, checkpoint_completed=False,
                       checkpoint_variant="E0", wal_budget_bytes=256*1024*1024,
                       wal_peak_bytes=None, sqlite_synchronous=2, wal_autocheckpoint_pages=4096,
                       sqlite_journal_mode="memory", disk_reserve_bytes=1, disk_free_min_bytes=64*1024*1024,
                       wal_samples=0)
        for field, value in changes.items():
            with self.subTest(field=field):
                bad = record()
                bad["backend"]["stream"][field] = value
                self.assertFalse(gate.validate(bad, "E1")["acceptance_pass"])

    def test_missing_stream_never_passes(self):
        result = gate.validate(dict(acceptance_pass=True), "E1")
        self.assertFalse(result["acceptance_pass"])

    def test_sampled_peak_cannot_pass_with_false_exceeded_flag(self):
        bad = record()
        bad["backend"]["stream"]["wal_peak_bytes"] = gate.WAL_BUDGET_BYTES
        self.assertFalse(gate.validate(bad, "E1")["acceptance_pass"])

    def test_checkpoint_error_and_busy_reject(self):
        for patch in (dict(error="busy"), dict(busy=1), dict(mode="FULL"), dict(log_frames=-1, checkpointed_frames=-1)):
            bad = record()
            bad["backend"]["stream"]["checkpoint"].update(patch)
            self.assertFalse(gate.validate(bad, "E1")["acceptance_pass"])


if __name__ == "__main__":
    unittest.main()
