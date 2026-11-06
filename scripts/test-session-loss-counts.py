"""Read-only session accounting regression; fixtures contain no private data."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("session_loss_counts", Path(__file__).with_name("session-loss-counts.py"))
COUNTS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COUNTS)


class SessionLossCountsTests(unittest.TestCase):
    def make_db(self, root, backend=None, historical=False):
        path = root / "synthetic.db"
        with contextlib.closing(sqlite3.connect(path)) as db, db:
            db.executescript("""
                CREATE TABLE installation_sessions(id TEXT PRIMARY KEY, finished_at TEXT, exit_code INTEGER);
                INSERT INTO installation_sessions VALUES ('exact', 'synthetic-end', 0),('other','',NULL);
            """)
            if historical:
                return path
            db.executescript("""
                CREATE TABLE capture_metadata(session_id TEXT PRIMARY KEY,backend_json TEXT);
                CREATE TABLE reliability_metadata(session_id TEXT PRIMARY KEY,stats_json TEXT,quality_json TEXT);
                CREATE TABLE capture_runs(session_id TEXT PRIMARY KEY,state TEXT,stats_json TEXT,error TEXT);
                CREATE TABLE raw_ingest(session_id TEXT);
                CREATE TABLE raw_stream(session_id TEXT);
                CREATE TABLE observations(session_id TEXT);
                CREATE TABLE normalized_operations(session_id TEXT);
                INSERT INTO raw_stream VALUES ('exact'), ('exact'), ('other');
            """)
            b = backend or dict(
                dropped_events=12, context_losses=3,
                pipeline=dict(queue_overflow=5,enqueue_disconnected=1,deliberately_filtered=900),
                handoff=dict(overflow=2,oversized=0,disconnected=0,serialize_failed=1),
                stream=dict(quota_dropped=2,failed=1,error=None),
                follow_installer=dict(candidate_dropped=3,excluded_other=800))
            db.execute("INSERT INTO capture_metadata VALUES (?,?)", ('exact',json.dumps(b)))
            db.execute("INSERT INTO reliability_metadata VALUES (?,?,?)", ('exact',json.dumps(dict(events_retained=2,drain_timed_out=False)),json.dumps(dict(level='Incomplete'))))
            db.execute("INSERT INTO capture_runs VALUES (?,?,?,NULL)", ('exact','finished','{}'))
        return path

    def test_exact_session_and_accounting_separate_scope_and_candidate_losses(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self.make_db(Path(tmp))
            result = COUNTS.read_report(path, 'exact')
            self.assertEqual(result['application_loss_accounting'], dict(receive_queue_loss=6,handoff_loss=3,raw_persistence_loss=3,sum=12,matches_recorded_total=True))
            self.assertEqual(result['pipeline']['deliberately_filtered'], 900)
            self.assertEqual(result['follow_installer']['candidate_dropped'], 3)
            self.assertEqual(result['stored_rows']['raw_stream'], 2)
            self.assertEqual(result['session']['capture_state'], 'finished')
            self.assertEqual(result['session']['installer_exit_code'], 0)
            self.assertIs(result['stats']['drain_timed_out'], False)
            for wrong in ('EXACT', 'exact%', "exact' OR 1=1--"):
                with self.assertRaises(LookupError):
                    COUNTS.read_report(path, wrong)
            other = COUNTS.read_report(path, 'other')
            self.assertEqual(other['stored_rows']['raw_stream'], 1)
            self.assertEqual(other['backend']['dropped_events'], COUNTS.MISSING)

    def test_historical_missing_is_not_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = COUNTS.read_report(self.make_db(Path(tmp), historical=True), 'exact')
            self.assertEqual(result['application_loss_accounting']['sum'], COUNTS.MISSING)
            self.assertEqual(result['stats']['drain_timed_out'], COUNTS.MISSING)
            self.assertEqual(result['stored_rows']['raw_stream'], COUNTS.MISSING)
            self.assertEqual(result['stream']['error_present'], COUNTS.MISSING)

    def test_unfinished_uses_committed_journal_and_private_errors_are_hidden(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self.make_db(Path(tmp), dict(stream=dict(persisted=99,error=None)))
            with contextlib.closing(sqlite3.connect(path)) as db, db:
                db.execute("UPDATE capture_runs SET state='capturing',stats_json=?,error=?", (json.dumps(dict(persisted=2,error=None)), 'private path error'))
            result = COUNTS.read_report(path, 'exact')
            self.assertEqual(result['stream']['persisted'], 2)
            self.assertTrue(result['stream']['error_present'])
            self.assertNotIn('private',json.dumps(result))

    def test_no_schema_files_or_database_content_change(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = self.make_db(root)
            def hashes():
                return {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in root.iterdir()}
            before = hashes()
            COUNTS.read_report(path, 'exact')
            self.assertEqual(hashes(),before)
            with contextlib.closing(sqlite3.connect(path.as_uri()+'?mode=ro',uri=True)) as db, db:
                db.execute('PRAGMA query_only=ON')
                with self.assertRaises(sqlite3.OperationalError):
                    db.execute("DELETE FROM installation_sessions")

    def test_nonempty_wal_refused_instead_of_reading_stale_snapshot_or_writing_shm(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = self.make_db(root)
            with contextlib.closing(sqlite3.connect(path)) as writer, writer:
                writer.execute('PRAGMA journal_mode=WAL')
                writer.execute("UPDATE installation_sessions SET exit_code=7 WHERE id='exact'")
                writer.commit()
                def hashes():
                    return {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in root.iterdir()}
                before = hashes()
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    self.assertEqual(COUNTS.main(['--database',str(path),'--session-id','exact']),1)
                self.assertEqual(hashes(),before)
                self.assertEqual(json.loads(output.getvalue()),dict(error='nonempty_wal_close_contain_normally_do_not_delete_sidecars'))

    def test_only_whitelisted_typed_values_leave_metadata(self):
        with tempfile.TemporaryDirectory() as tmp:
            private = 'private-path-sentinel'
            result = COUNTS.read_report(self.make_db(Path(tmp), dict(
                etw_file=private, dropped_events=private, warnings=[private],
                pipeline=dict(queue_overflow=True,deliberately_filtered=-1),
                stream=dict(error=private))), 'exact')
            self.assertNotIn(private,json.dumps(result))
            self.assertEqual(result['backend']['dropped_events'], COUNTS.MISSING)
            self.assertEqual(result['pipeline']['queue_overflow'], COUNTS.MISSING)
            self.assertEqual(result['pipeline']['deliberately_filtered'], COUNTS.MISSING)
            self.assertTrue(result['stream']['error_present'])

    def test_cli_failures_omit_path_and_document(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = self.make_db(Path(tmp))
            for name,code,label in ((str(Path(tmp)/'private-missing.db'),1,'read_only_diagnostic_failed'), (str(path),2,'exact_session_id_not_found')):
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    self.assertEqual(COUNTS.main(['--database',name,'--session-id','missing']),code)
                self.assertEqual(json.loads(output.getvalue()),dict(error=label))
            with contextlib.closing(sqlite3.connect(path)) as db, db:
                db.execute("UPDATE capture_metadata SET backend_json='private-malformed-json'")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(COUNTS.main(['--database',str(path),'--session-id','exact']),1)
            self.assertNotIn('private',output.getvalue())


if __name__ == '__main__':
    unittest.main()
