"""Reject unreviewed or unbound native experiment preparation before capture."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('prepare', Path(__file__).with_name('prepare-backlog-comparison.py'))
S = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(S)
SOURCE = 'a' * 40


class PreparationContracts(unittest.TestCase):
    def reject(self, values, reason):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'new'
            with patch.object(S, 'git', side_effect=values), self.assertRaisesRegex(ValueError, reason):
                S.prepare(Path(directory), SOURCE, output, 'b' * 64)
            self.assertFalse(output.exists(), 'rejected identity must not create a source checkout or capture directory')

    def test_wrong_head_and_dirty_source_are_not_measured(self):
        self.reject([b'0' * 40 + b'\n'], 'candidate_head_differs')
        self.reject([SOURCE.encode() + b'\n', b'M core.rs'], 'candidate_tracked_source_dirty')

    def test_behavior_capacity_or_dependency_change_is_rejected(self):
        for changed in ('crates/contain-core/src/ingest.rs', 'Cargo.toml', 'crates/contain-core/src/drain.rs'):
            self.reject([SOURCE.encode(), b'', changed.encode()], 'non_observer_core_change')

    def test_missing_patch_or_unreviewed_patch_is_rejected(self):
        self.reject([SOURCE.encode(), b'', b''], 'missing_patch')
        self.reject([SOURCE.encode(), b'', S.OBSERVER_FILES[0].encode(), b'patch'], 'patch_hash_differs')

    def test_reviewed_cost_input_change_or_missing_samples_stops_before_native_work(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            for name in S.COST_INPUTS:
                path=root/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(name.encode())
            result=dict(protocol='contain-receiver-window-offline-overhead-1', accepted=True, synthetic=True,
                real_etw=False, native_ingest=False, retries=0, fixed_work_and_snapshot_inputs_equal=True,
                input_sha256={name:S.sha((root/name).read_bytes()) for name in S.COST_INPUTS},
                samples=[dict(warmup=index<3,exit_code=0) for index in range(21)])
            S.validate_offline(root,result)
            (root/S.COST_INPUTS[0]).write_text('changed input')
            with self.assertRaisesRegex(ValueError,'reviewed_offline_input_changed'): S.validate_offline(root,result)
            (root/S.COST_INPUTS[0]).write_bytes(S.COST_INPUTS[0].encode())
            result['samples'].pop()
            with self.assertRaisesRegex(ValueError,'reviewed_offline_samples_incomplete'): S.validate_offline(root,result)

    def test_paths_exclude_capture_behavior_and_schema(self):
        self.assertNotIn('crates/contain-core/src/capture.rs', S.OBSERVER_FILES)
        self.assertNotIn('crates/contain-core/src/ingest.rs', S.OBSERVER_FILES)
        self.assertNotIn('crates/contain-core/src/drain.rs', S.OBSERVER_FILES)
        self.assertNotIn('crates/contain-core/src/windows/etw.rs', S.OBSERVER_FILES)
        self.assertTrue(all('schema' not in path for path in S.OBSERVER_FILES))


if __name__ == '__main__':
    unittest.main()
