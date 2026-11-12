import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('source_facts', Path(__file__).with_name('summarize-source-capture-facts.py'))
M = importlib.util.module_from_spec(spec); spec.loader.exec_module(M)

def inspect(data):
    return dict(schema_version=3, kind='inspect', data=data)


class SourceFactsTests(unittest.TestCase):
    def test_saved_facts_with_missing_values_preserved(self):
        result = M.summarize(inspect({'backend': {'pipeline': {'providers': {'process': {'overflow': 5, 'filtered': 8}}},
            'initial_identity_snapshot': {'thread_unqueried': None, 'budget_exhausted': True, 'thread_resolved': 3}}}))
        self.assertEqual(result['providers']['process']['overflow'], 5)
        self.assertIsNone(result['providers']['file'])
        self.assertIsNone(result['initial_identity_snapshot']['thread_unqueried'])
        self.assertTrue(result['initial_identity_snapshot']['budget_exhausted'])
        self.assertEqual(result['initial_identity_snapshot']['thread_resolved'], 3)
        self.assertIsNone(result['initial_identity_snapshot']['query_errors'])

    def test_absent_history_is_unavailable(self):
        self.assertIsNone(M.summarize(inspect({}))['initial_identity_snapshot'])
        self.assertIsNone(M.summarize(inspect({}))['etw_session'])

    def test_source_phases_keep_cumulative_values_and_only_safe_numeric_labels(self):
        result = M.summarize(inspect({'backend': {'pipeline': {
            'etw_session': {'ready': {'events_lost': 7}, 'before_stop': {'events_lost': 11},
                'stopped': {'events_lost': 13, 'control_error': None, 'allocated_buffers': 64,
                    'path': 'SECRET', 'free_buffers': True}, 'unexpected': 'SECRET'},
            'providers': {'file': {'elapsed_ns': {'callback': 100, 'lock_wait': 5, 'SECRET': 6}}}
        }}}))
        session = result['etw_session']
        self.assertEqual([session[p]['events_lost'] for p in ('ready', 'before_stop', 'stopped')], [7, 11, 13])
        self.assertEqual(session['stopped']['allocated_buffers'], 64)
        self.assertIsNone(session['stopped']['free_buffers'])
        self.assertIsNone(session['stopped']['control_error'])
        self.assertEqual(result['provider_elapsed_ns']['file']['callback'], 100)
        self.assertEqual(result['provider_elapsed_ns']['file']['lock_wait'], 5)
        self.assertNotIn('SECRET', str(result))

    def test_reject_wrong_envelope(self):
        for doc in ({}, dict(schema_version=2, kind='inspect', data={}), dict(schema_version=3, kind='history', data={})):
            with self.assertRaises(ValueError): M.summarize(doc)

    def test_only_fixed_numeric_labels_survive(self):
        result = M.summarize(inspect({'backend': {'pipeline': {'providers': {'file': {'callback_records': True, 'overflow': -1, 'path': 'SECRET'}}},
            'initial_identity_snapshot': {'query_errors': {'thread_query_win32_5': 2, 'SECRET': 9}, 'path': 'SECRET'}}}))
        self.assertNotIn('SECRET', str(result))
        self.assertIsNone(result['providers']['file']['callback_records'])
        self.assertIsNone(result['providers']['file']['overflow'])
        self.assertEqual(result['initial_identity_snapshot']['query_errors'], {'thread_query_win32_5': 2})
        self.assertEqual(result['initial_identity_snapshot']['omitted_error_labels'], 1)


if __name__ == '__main__': unittest.main()
