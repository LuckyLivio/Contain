//! Crash-readable, byte-quota-limited raw evidence journal in the session database.
//! Only the application consumer uses SQLite; ETW callbacks never call this module.
use super::*;
use crate::model::*;
use std::time::{Duration, Instant};

pub const PAGE: usize = 256;
const BATCH_BYTES: usize = 1024 * 1024;
const RECORD_BYTES: usize = 256 * 1024;
pub const DEFAULT_QUOTA: u64 = 256 * 1024 * 1024;

pub(super) fn schema(db: &Connection) -> Result<()> {
    db.execute_batch("CREATE TABLE IF NOT EXISTS capture_runs(session_id TEXT PRIMARY KEY, state TEXT NOT NULL, stats_json TEXT NOT NULL, error TEXT);
        CREATE TABLE IF NOT EXISTS raw_stream(session_id TEXT NOT NULL, sequence INTEGER NOT NULL, ticks INTEGER NOT NULL, kind TEXT NOT NULL, header_pid INTEGER, related TEXT, document TEXT NOT NULL, PRIMARY KEY(session_id,sequence));
        CREATE INDEX IF NOT EXISTS raw_stream_time ON raw_stream(session_id,ticks,sequence);
        CREATE INDEX IF NOT EXISTS raw_stream_related ON raw_stream(session_id,related);
        CREATE INDEX IF NOT EXISTS raw_stream_header ON raw_stream(session_id,kind,header_pid);
        CREATE TABLE IF NOT EXISTS event_documents(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, ticks INTEGER NOT NULL, sequence INTEGER NOT NULL, resource TEXT NOT NULL, document TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS event_documents_order ON event_documents(session_id,ticks,sequence,id);
        CREATE INDEX IF NOT EXISTS event_documents_resource ON event_documents(session_id,resource);")?;
    Ok(())
}

#[derive(Clone, Debug, Default, serde::Serialize, serde::Deserialize)]
pub struct StreamStats {
    pub accepted: u64,
    pub persisted: u64,
    pub failed: u64,
    pub quota_dropped: u64,
    pub committed_bytes: u64,
    pub quota_bytes: u64,
    pub batches: u64,
    pub max_batch_bytes: u64,
    pub persistence_ns: u64,
    pub error: Option<String>,
}

pub struct RawBuffer {
    session: String,
    batch: Vec<String>,
    bytes: usize,
    last_flush: Instant,
    pub stats: StreamStats,
}
impl RawBuffer {
    pub fn new(db: &mut Storage, capture: &Capture, quota: u64) -> Result<Self> {
        db.connection.execute_batch(
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=256;",
        )?;
        db.save(capture)?;
        let stats = StreamStats {
            quota_bytes: quota,
            ..Default::default()
        };
        db.connection.execute(
            "INSERT INTO capture_runs VALUES (?1,'capturing',?2,NULL)",
            params![capture.id, serde_json::to_string(&stats)?],
        )?;
        Ok(Self {
            session: capture.id.clone(),
            batch: Vec::with_capacity(PAGE),
            bytes: 0,
            last_flush: Instant::now(),
            stats,
        })
    }
    pub fn append(&mut self, db: &mut Storage, incoming: Vec<SystemEvent>) {
        for e in incoming {
            self.stats.accepted += 1;
            if self.stats.error.is_some() {
                self.stats.failed += 1;
                continue;
            }
            let document = match serde_json::to_string(&e) {
                Ok(d) => d,
                Err(err) => {
                    self.stats.failed += 1;
                    self.stats.error = Some(err.to_string());
                    continue;
                }
            };
            let size = document.len();
            if size > RECORD_BYTES
                || self.stats.committed_bytes + self.bytes as u64 + size as u64
                    > self.stats.quota_bytes
            {
                self.stats.quota_dropped += 1;
                continue;
            }
            if self.bytes + size > BATCH_BYTES {
                self.flush(db);
            }
            if self.stats.error.is_some() {
                self.stats.failed += 1;
                continue;
            }
            self.bytes += size;
            self.batch.push(document);
            if self.batch.len() >= PAGE {
                self.flush(db);
            }
        }
        if self.last_flush.elapsed() >= Duration::from_millis(100) {
            self.flush(db);
        }
    }
    pub fn flush(&mut self, db: &mut Storage) {
        if self.batch.is_empty() {
            return;
        }
        let start = Instant::now();
        let result = (|| -> Result<()> {
            let tx = db.connection.transaction()?;
            {
                let mut insert=tx.prepare_cached("INSERT INTO raw_stream VALUES (?1,json_extract(?2,'$.sequence'),CAST(json_extract(?2,'$.timestamp_ticks') AS INTEGER),json_extract(?2,'$.event_type'),json_extract(?2,'$.raw.header_pid'),json_extract(?2,'$.raw.related_event'),?2)")?;
                for e in &self.batch {
                    insert.execute(params![self.session, e])?;
                }
            }
            let mut stats = self.stats.clone();
            stats.persisted += self.batch.len() as u64;
            stats.committed_bytes += self.bytes as u64;
            stats.batches += 1;
            tx.execute(
                "UPDATE capture_runs SET stats_json=?2 WHERE session_id=?1",
                params![self.session, serde_json::to_string(&stats)?],
            )?;
            tx.commit()?;
            Ok(())
        })();
        self.stats.persistence_ns += start.elapsed().as_nanos() as u64;
        self.stats.max_batch_bytes = self.stats.max_batch_bytes.max(self.bytes as u64);
        match result {
            Ok(()) => {
                self.stats.persisted += self.batch.len() as u64;
                self.stats.committed_bytes += self.bytes as u64;
                self.stats.batches += 1;
            }
            Err(e) => {
                self.stats.failed += self.batch.len() as u64;
                self.stats.error = Some(format!("raw evidence persistence failed: {e:#}"));
            }
        }
        self.batch.clear();
        self.bytes = 0;
        self.last_flush = Instant::now();
    }
    pub fn finish(mut self, db: &mut Storage) -> Result<StreamStats> {
        self.flush(db);
        // Failure can also prevent saving the error. The last committed state stays unfinished.
        db.connection.execute(
            "UPDATE capture_runs SET state=?2,stats_json=?3,error=?4 WHERE session_id=?1",
            params![
                self.session,
                if self.stats.error.is_some() {
                    "failed"
                } else {
                    "finalizing"
                },
                serde_json::to_string(&self.stats)?,
                self.stats.error
            ],
        )?;
        Ok(self.stats)
    }
}

impl Storage {
    pub fn validate_capture_location(&self, roots: &[String]) -> Result<()> {
        if let Some(path) = self.connection.path().filter(|p| *p != ":memory:") {
            let path = std::fs::canonicalize(path)?;
            let path = crate::windows::native::normalize_path(&path.to_string_lossy(), &[]);
            let roots: Vec<_> = roots
                .iter()
                .map(|p| crate::windows::native::normalize_path(p, &[]))
                .collect();
            anyhow::ensure!(
                !crate::windows::native::in_scope(&path, &roots),
                "Capture database, WAL and SHM must be outside watched roots; choose --db outside that scope"
            );
        }
        Ok(())
    }
    pub fn raw_page(&self, session: &str, after: (u64, u64)) -> Result<Vec<SystemEvent>> {
        let mut q=self.connection.prepare_cached("SELECT document FROM raw_stream WHERE session_id=?1 AND (ticks,sequence)>(?2,?3) ORDER BY ticks,sequence LIMIT 256")?;
        q.query_map(params![session, after.0, after.1], |r| {
            r.get::<_, String>(0)
        })?
        .map(|r| Ok(serde_json::from_str(&r?)?))
        .collect()
    }
    pub fn scoped_header(&self, session: &str, pid: u32) -> Result<bool> {
        Ok(self.connection.query_row("SELECT EXISTS(SELECT 1 FROM raw_stream WHERE session_id=?1 AND kind='file' AND header_pid=?2)",params![session,pid],|r|r.get(0))?)
    }
    pub fn completion(&self, session: &str, id: &str) -> Result<Option<SystemEvent>> {
        let mut q = self.connection.prepare_cached(
            "SELECT document FROM raw_stream WHERE session_id=?1 AND related=?2 LIMIT 2",
        )?;
        let rows = q
            .query_map(params![session, id], |r| r.get::<_, String>(0))?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        if rows.len() == 1 {
            Ok(Some(serde_json::from_str(&rows[0])?))
        } else {
            Ok(None)
        }
    }
    pub fn append_evidence(&mut self, c: &Capture) -> Result<()> {
        let tx = self.connection.transaction()?;
        evidence::save_events(&tx, c)?;
        reliability::save_details(&tx, c)?;
        save_documents(&tx, c)?;
        tx.commit()?;
        Ok(())
    }
    pub fn resource_events(&self, session: &str, resource: &str) -> Result<Vec<SystemEvent>> {
        let mut q = self.connection.prepare_cached(
            "SELECT document FROM event_documents WHERE session_id=?1 AND resource=?2 LIMIT 513",
        )?;
        let rows = q
            .query_map(params![session, resource.to_lowercase()], |r| {
                r.get::<_, String>(0)
            })?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        anyhow::ensure!(rows.len() <= 512, "per-resource association quota exceeded");
        rows.into_iter()
            .map(|r| Ok(serde_json::from_str(&r)?))
            .collect()
    }
    pub fn mark_finished(&mut self, c: &Capture) -> Result<()> {
        let tx = self.connection.transaction()?;
        if c.backend.dropped_events > 0
            || c.backend.decode_errors > 0
            || c.backend.etw_events_lost != Some(0)
            || c.backend.etw_buffers_lost != Some(0)
        {
            // This also covers records persisted before a later failure became known.
            tx.execute("UPDATE observations SET confidence='Unknown',rule=?2,reason='Session continuity unverified; final promotion suppressed' WHERE session_id=?1 AND event_type IN ('file','registry')",params![c.id,serde_json::to_string(&AttributionRule::EventLoss)?])?;
            tx.execute("UPDATE normalized_operations SET detail_json=json_set(detail_json,'$.confidence','Unknown') WHERE session_id=?1",[&c.id])?;
            tx.execute("UPDATE observations SET success=NULL WHERE session_id=?1 AND event_type IN ('file','completion')",[&c.id])?;
            tx.execute("UPDATE observation_details SET raw_json=json_set(raw_json,'$.resource_resolved',json('false')),dimensions_json=json_set(dimensions_json,'$.resource','Unknown') WHERE event_id IN (SELECT id FROM observations WHERE session_id=?1 AND event_type IN ('file','registry'))",[&c.id])?;
            tx.execute("UPDATE event_documents SET document=json_set(document,'$.confidence','Unknown','$.evidence.rule','EventLoss','$.raw.resource_resolved',json('false')) WHERE session_id=?1 AND json_extract(document,'$.event_type') IN ('file','registry')",[&c.id])?;
        }
        tx.execute(
            "UPDATE capture_runs SET state='finished' WHERE session_id=?1 AND state='finalizing'",
            [&c.id],
        )?;
        tx.commit()?;
        Ok(())
    }
    pub fn stream_state(&self, id: &str) -> Result<Option<String>> {
        Ok(self
            .connection
            .query_row(
                "SELECT state FROM capture_runs WHERE session_id=?1",
                [id],
                |r| r.get(0),
            )
            .optional()?)
    }
    pub fn events_page(&self, session: &str, offset: u64, limit: u32) -> Result<Vec<SystemEvent>> {
        anyhow::ensure!((1..=4096).contains(&limit), "page limit must be 1..4096");
        let unfinished = self.stream_state(session)?.is_some_and(|s| s != "finished");
        let sql = if unfinished {
            "SELECT document FROM raw_stream WHERE session_id=?1 ORDER BY ticks,sequence LIMIT ?2 OFFSET ?3"
        } else {
            "SELECT document FROM event_documents WHERE session_id=?1 ORDER BY ticks,sequence,id LIMIT ?2 OFFSET ?3"
        };
        let mut q = self.connection.prepare_cached(sql)?;
        let mut rows = q
            .query_map(params![session, limit, offset], |r| r.get::<_, String>(0))?
            .map(|r| Ok(serde_json::from_str(&r?)?))
            .collect::<Result<Vec<SystemEvent>>>()?;
        if unfinished {
            for e in &mut rows {
                e.confidence = Confidence::Unknown;
                e.success = None;
                e.reason = "Unfinished session: raw evidence only; no final attribution".into();
            }
        }
        Ok(rows)
    }
}
pub(super) fn save_documents(tx: &rusqlite::Transaction<'_>, c: &Capture) -> Result<()> {
    let mut q = tx.prepare_cached("INSERT INTO event_documents VALUES (?1,?2,?3,?4,?5,?6)")?;
    for e in &c.events {
        q.execute(params![
            e.id,
            c.id,
            e.timestamp_ticks,
            e.sequence,
            e.resource.to_lowercase(),
            serde_json::to_string(e)?
        ])?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn capture() -> Capture {
        Capture {
            id: "stream".into(),
            name: "stream".into(),
            schema_version: 3,
            ..Default::default()
        }
    }
    fn event(sequence: u64) -> SystemEvent {
        SystemEvent {
            id: format!("e{sequence}"),
            sequence,
            timestamp_ticks: sequence,
            event_type: "file".into(),
            resource: "C:\\fixture\\a".into(),
            operation: "write_requested".into(),
            ..Default::default()
        }
    }
    #[test]
    fn commits_before_finish_and_byte_quota_balances_without_tail() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = capture();
        let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        writer.append(&mut db, (1..=257).map(event).collect());
        assert_eq!(db.events_page(&c.id, 0, 4096).unwrap().len(), 256);
        assert_eq!(
            db.load_summary(&c.id).unwrap().capture_state.as_deref(),
            Some("capturing")
        );
        let stats = writer.finish(&mut db).unwrap();
        assert_eq!(stats.persisted, 257);
        assert_eq!(
            stats.accepted,
            stats.persisted + stats.failed + stats.quota_dropped
        );
        assert!(stats.max_batch_bytes <= BATCH_BYTES as u64);
        assert_eq!(db.load(&c.id).unwrap().events.len(), 257);
        assert!(db.load_summary(&c.id).unwrap().events.is_empty());
        let mut c2 = capture();
        c2.id = "quota".into();
        let mut w = RawBuffer::new(&mut db, &c2, 0).unwrap();
        w.append(&mut db, vec![event(1)]);
        let s = w.finish(&mut db).unwrap();
        assert_eq!(s.quota_dropped, 1);
        assert_eq!(s.persisted, 0);
    }
    #[test]
    fn write_failure_preserves_committed_batch_and_unfinished_state() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = capture();
        let mut w = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        w.append(&mut db, vec![event(1)]);
        w.flush(&mut db);
        db.connection.execute_batch("CREATE TRIGGER reject_raw BEFORE INSERT ON raw_stream BEGIN SELECT RAISE(ABORT,'injected write failure'); END;").unwrap();
        w.append(&mut db, vec![event(2), event(3)]);
        w.flush(&mut db);
        w.append(&mut db, vec![event(4)]);
        let s = w.finish(&mut db).unwrap();
        assert_eq!(s.persisted, 1);
        assert_eq!(s.failed, 3);
        assert!(s.error.is_some());
        assert_eq!(db.load(&c.id).unwrap().events.len(), 1);
        assert_eq!(db.stream_state(&c.id).unwrap().as_deref(), Some("failed"));
    }
    #[test]
    fn late_loss_downgrades_previously_persisted_high_and_pages_agree() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let mut c = capture();
        let mut w = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        w.append(&mut db, vec![event(1)]);
        w.finish(&mut db).unwrap();
        let mut e = event(1);
        e.confidence = Confidence::High;
        e.raw.resource_resolved = true;
        e.success = Some(true);
        c.events = vec![e];
        db.append_evidence(&c).unwrap();
        assert_eq!(
            db.load(&c.id).unwrap().events[0].confidence,
            Confidence::Unknown
        );
        c.backend.dropped_events = 1;
        db.mark_finished(&c).unwrap();
        let full = db.load(&c.id).unwrap();
        let page = db.events_page(&c.id, 0, 1).unwrap();
        assert_eq!(full.events[0].confidence, Confidence::Unknown);
        assert_eq!(page[0].confidence, Confidence::Unknown);
        assert!(!full.events[0].raw.resource_resolved);
        assert_eq!(full.events[0].success, None);
    }
    #[test]
    fn sqlite_full_is_a_failure_not_a_successful_empty_capture() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = capture();
        let mut w = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        let pages: u64 = db
            .connection
            .pragma_query_value(None, "page_count", |r| r.get(0))
            .unwrap();
        db.connection
            .pragma_update(None, "max_page_count", pages + 1)
            .unwrap();
        let mut e = event(1);
        e.resource = "x".repeat(100_000);
        w.append(&mut db, vec![e]);
        w.flush(&mut db);
        assert_eq!(w.stats.failed, 1);
        assert!(w.stats.error.as_deref().unwrap().contains("full"));
        assert_eq!(db.events_page(&c.id, 0, 256).unwrap().len(), 0);
    }
}
