//! Crash-readable, byte-quota-limited raw evidence journal in the session database.
//! Only the application consumer uses SQLite; ETW callbacks never call this module.
use super::*;
use crate::model::*;
use crate::profile::{self, measured};
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
        CREATE INDEX IF NOT EXISTS raw_stream_id ON raw_stream(json_extract(document,'$.id'));
        CREATE TABLE IF NOT EXISTS event_documents(id TEXT PRIMARY KEY, session_id TEXT NOT NULL, ticks INTEGER NOT NULL, sequence INTEGER NOT NULL, resource TEXT NOT NULL, document TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS event_documents_order ON event_documents(session_id,ticks,sequence,id);
        CREATE INDEX IF NOT EXISTS event_documents_resource ON event_documents(session_id,resource);
        CREATE INDEX IF NOT EXISTS observations_resource ON observations(session_id,resource);")?;
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
    #[serde(default)]
    pub sqlite_version: Option<String>,
    pub sqlite_synchronous: Option<u32>,
    pub wal_autocheckpoint_pages: Option<u32>,
    pub sqlite_page_size_bytes: Option<u32>,
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
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA wal_autocheckpoint=4096;",
        )?;
        db.save(capture)?;
        let stats = StreamStats {
            quota_bytes: quota,
            sqlite_version: Some(rusqlite::version().into()),
            sqlite_synchronous: Some(db.connection.pragma_query_value(
                None,
                "synchronous",
                |r| r.get(0),
            )?),
            wal_autocheckpoint_pages: Some(db.connection.pragma_query_value(
                None,
                "wal_autocheckpoint",
                |r| r.get(0),
            )?),
            sqlite_page_size_bytes: Some(db.connection.pragma_query_value(
                None,
                "page_size",
                |r| r.get(0),
            )?),
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
            let document = match measured!("raw_serialize", serde_json::to_string(&e)) {
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
                let _insert_clock = profile::timer("raw_insert");
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
            measured!("raw_commit_including_autocheckpoint", tx.commit())?;
            Ok(())
        })();
        let elapsed = start.elapsed().as_nanos() as u64;
        profile::batch(self.batch.len(), self.bytes, elapsed);
        self.stats.persistence_ns += elapsed;
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
    pub(crate) fn raw_page(&self, session: &str, after: (u64, u64)) -> Result<Vec<SystemEvent>> {
        let _clock = profile::timer("post_raw_read");
        let mut q=self.connection.prepare_cached("SELECT document FROM raw_stream WHERE session_id=?1 AND (ticks,sequence)>(?2,?3) ORDER BY ticks,sequence LIMIT 256")?;
        q.query_map(params![session, after.0, after.1], |r| {
            r.get::<_, String>(0)
        })?
        .map(|r| Ok(serde_json::from_str(&r?)?))
        .collect()
    }
    pub(crate) fn scoped_header(&self, session: &str, pid: u32) -> Result<bool> {
        let _clock = profile::timer("post_header_query");
        Ok(self.connection.query_row("SELECT EXISTS(SELECT 1 FROM raw_stream WHERE session_id=?1 AND kind='file' AND header_pid=?2)",params![session,pid],|r|r.get(0))?)
    }
    pub(crate) fn completion(&self, session: &str, id: &str) -> Result<Option<SystemEvent>> {
        let _clock = profile::timer("post_completion_query");
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
    pub(crate) fn append_evidence(&mut self, c: &Capture) -> Result<()> {
        let _clock = profile::timer("post_derived_write");
        let tx = self.connection.transaction()?;
        measured!("post_observations", evidence::save_events(&tx, c))?;
        measured!("post_details_edges", reliability::save_details(&tx, c))?;
        measured!("post_documents", save_documents(&tx, c))?;
        measured!("post_derived_commit", tx.commit())?;
        Ok(())
    }
    pub(crate) fn resource_events(
        &self,
        session: &str,
        resource: &str,
    ) -> Result<Option<Vec<SystemEvent>>> {
        let _clock = profile::timer("post_resource_read");
        let mut q = self.connection.prepare_cached(
            "SELECT document FROM event_documents WHERE session_id=?1 AND resource=?2 LIMIT 513",
        )?;
        let rows = q
            .query_map(
                params![
                    session,
                    crate::windows::native::normalize_path(resource, &[])
                ],
                |r| r.get::<_, String>(0),
            )?
            .collect::<rusqlite::Result<Vec<_>>>()?;
        if rows.len() > 512 {
            return Ok(None);
        }
        Ok(Some(
            rows.into_iter()
                .map(|r| Ok(serde_json::from_str(&r)?))
                .collect::<Result<Vec<_>>>()?,
        ))
    }
    pub(crate) fn mark_finished(&mut self, c: &Capture) -> Result<()> {
        // Stream commits survive application exit; final completion also syncs the WAL.
        self.connection.execute_batch("PRAGMA synchronous=FULL;")?;
        let tx = self.connection.transaction()?;
        if !c.backend.source_intact() {
            // This also covers records persisted before a later failure became known.
            tx.execute("UPDATE observations SET confidence='Unknown',rule=?2,reason='Session continuity unverified; final promotion suppressed' WHERE session_id=?1 AND event_type IN ('file','registry')",params![c.id,serde_json::to_string(&AttributionRule::EventLoss)?])?;
            tx.execute("UPDATE evidence_edges SET confidence='Unknown',reason='Session continuity unverified; resource association suppressed' WHERE session_id=?1 AND from_node IN (SELECT 'event:'||id FROM observations WHERE session_id=?1 AND event_type IN ('file','registry'))",[&c.id])?;
            tx.execute("UPDATE normalized_operations SET detail_json=json_set(detail_json,'$.confidence','Unknown') WHERE session_id=?1",[&c.id])?;
            tx.execute("UPDATE normalized_operations SET operation='UnresolvedRequest',detail_json=json_set(detail_json,'$.operation','UnresolvedRequest','$.success',NULL,'$.reason','Session continuity unverified; raw request only') WHERE session_id=?1 AND json_array_length(detail_json,'$.raw_events')>0",[&c.id])?;
            tx.execute("UPDATE observations SET success=NULL WHERE session_id=?1 AND event_type IN ('file','completion')",[&c.id])?;
            tx.execute("UPDATE observation_details SET raw_json=json_set(raw_json,'$.resource_resolved',json('false')),dimensions_json=json_set(dimensions_json,'$.resource','Unknown') WHERE event_id IN (SELECT id FROM observations WHERE session_id=?1 AND event_type IN ('file','registry'))",[&c.id])?;
            tx.execute("UPDATE observation_details SET dimensions_json=json_set(dimensions_json,'$.operation','Unknown') WHERE event_id IN (SELECT id FROM observations WHERE session_id=?1 AND event_type IN ('file','completion'))",[&c.id])?;
            tx.execute("UPDATE event_documents SET document=json_set(document,'$.confidence','Unknown','$.evidence.rule','EventLoss','$.raw.resource_resolved',json('false'),'$.dimensions.resource','Unknown','$.reason','Session continuity unverified; final promotion suppressed') WHERE session_id=?1 AND json_extract(document,'$.event_type') IN ('file','registry')",[&c.id])?;
            tx.execute("UPDATE event_documents SET document=json_set(document,'$.success',NULL,'$.dimensions.operation','Unknown') WHERE session_id=?1 AND json_extract(document,'$.event_type') IN ('file','completion')",[&c.id])?;
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

    pub(crate) fn event_explanation(&self, id: &str) -> Result<Option<Capture>> {
        let found: Option<(String, String)> = self
            .connection
            .query_row(
                "SELECT session_id,document FROM event_documents WHERE id=?1",
                [id],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .optional()?;
        let Some((session, document)) = found else {
            let raw:Option<(String,String)>=self.connection.query_row("SELECT session_id,document FROM raw_stream WHERE json_extract(document,'$.id')=?1 LIMIT 1",[id],|r|Ok((r.get(0)?,r.get(1)?))).optional()?;
            if let Some((session, document)) = raw {
                let mut c = self.load_summary(&session)?;
                let mut e: SystemEvent = serde_json::from_str(&document)?;
                e.confidence = Confidence::Unknown;
                e.success = None;
                e.reason="Raw journal evidence without a finalized observation; no attribution promotion".into();
                c.events.push(e);
                return Ok(Some(c));
            }
            return Ok(None);
        };
        let mut c = self.load_summary(&session)?;
        anyhow::ensure!(
            c.capture_state.as_deref().is_none_or(|s| s == "finished"),
            "Session is unfinished; use paged history for raw evidence"
        );
        c.events.push(serde_json::from_str(&document)?);
        let mut nodes = vec![format!("event:{id}")];
        let mut visited = std::collections::HashSet::new();
        while let Some(node) = nodes.pop() {
            if !visited.insert(node.clone()) {
                continue;
            }
            anyhow::ensure!(visited.len() <= 4096, "Explanation ancestry quota exceeded");
            let mut q=self.connection.prepare_cached("SELECT from_node,to_node,relation,confidence,reason FROM evidence_edges WHERE session_id=?1 AND to_node=?2 LIMIT 4097")?;
            for row in q.query_map(params![session, node], |r| {
                Ok(EvidenceEdge {
                    from: r.get(0)?,
                    to: r.get(1)?,
                    relation: r.get(2)?,
                    confidence: super::parse_confidence(&r.get::<_, String>(3)?),
                    reason: r.get(4)?,
                })
            })? {
                let edge = row?;
                nodes.push(edge.from.clone());
                c.edges.push(edge);
                anyhow::ensure!(c.edges.len() <= 4096, "Explanation edge quota exceeded");
            }
        }
        let mut q=self.connection.prepare_cached("SELECT detail_json FROM normalized_operations WHERE session_id=?1 AND EXISTS(SELECT 1 FROM json_each(normalized_operations.detail_json,'$.raw_events') WHERE value=?2) LIMIT 4097")?;
        c.operations = q
            .query_map(params![session, id], |r| r.get::<_, String>(0))?
            .map(|r| Ok(serde_json::from_str(&r?)?))
            .collect::<Result<Vec<_>>>()?;
        anyhow::ensure!(
            c.operations.len() <= 4096,
            "Explanation operation quota exceeded"
        );
        Ok(Some(c))
    }
    pub fn events_page(&self, session: &str, offset: u64, limit: u32) -> Result<Vec<SystemEvent>> {
        anyhow::ensure!((1..=4096).contains(&limit), "page limit must be 1..4096");
        let unfinished = self.stream_state(session)?.is_some_and(|s| s != "finished");
        if self.stream_state(session)?.is_none() {
            let missing:bool=self.connection.query_row("SELECT EXISTS(SELECT 1 FROM observations o WHERE session_id=?1 AND NOT EXISTS(SELECT 1 FROM event_documents d WHERE d.id=o.id))",[session],|r|r.get(0))?;
            anyhow::ensure!(
                !missing,
                "Legacy capture has no paged document index; use history without --limit"
            );
        }
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
            crate::windows::native::normalize_path(&e.resource, &[]),
            serde_json::to_string(e)?
        ])?;
    }
    Ok(())
}
pub(super) fn save_state_validation(tx: &rusqlite::Transaction<'_>, c: &Capture) -> Result<()> {
    let resources = c
        .files
        .iter()
        .filter(|f| f.confidence == Confidence::High)
        .map(|f| crate::windows::native::normalize_path(&f.path, &[]));
    for resource in resources {
        tx.prepare_cached("UPDATE observations SET state_validated=1 WHERE session_id=?1 AND resource=?2 AND event_type='file' AND operation NOT IN ('open_requested','close') AND success IS NOT 0")?.execute(params![c.id,resource])?;
        tx.prepare_cached("UPDATE event_documents SET document=json_set(document,'$.state_validated',json('true')) WHERE session_id=?1 AND resource=?2 AND id IN (SELECT id FROM observations WHERE session_id=?1 AND resource=?2 AND state_validated=1)")?.execute(params![c.id,resource])?;
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
        e.dimensions.resource = Confidence::High;
        e.dimensions.operation = Confidence::High;
        c.events = vec![e];
        c.edges.push(EvidenceEdge {
            from: "event:e1".into(),
            to: "resource:C:\\fixture\\a".into(),
            relation: "write_requested".into(),
            confidence: Confidence::High,
            reason: "Provisional resource association".into(),
        });
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
        assert_eq!(full.edges[0].confidence, Confidence::Unknown);
        assert_eq!(
            serde_json::to_string(&full.events[0]).unwrap(),
            serde_json::to_string(&page[0]).unwrap()
        );
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
    #[test]
    fn busy_writer_and_reopen_preserve_prior_commits() {
        let path =
            std::env::temp_dir().join(format!("contain-stream-test-{}.db", uuid::Uuid::new_v4()));
        {
            let mut db = Storage::open(&path).unwrap();
            let c = capture();
            let mut w = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
            w.append(&mut db, vec![event(1)]);
            w.flush(&mut db);
            let locked = Connection::open(&path).unwrap();
            locked.execute_batch("BEGIN IMMEDIATE").unwrap();
            w.append(&mut db, vec![event(2)]);
            w.flush(&mut db);
            assert_eq!(w.stats.failed, 1);
            locked.execute_batch("ROLLBACK").unwrap();
            drop(locked);
            // Simulate interruption: no successful finish or final summary transaction.
        }
        {
            let db = Storage::open(&path).unwrap();
            let c = db.load("stream").unwrap();
            assert_eq!(c.events.len(), 1);
            assert_eq!(c.events[0].confidence, Confidence::Unknown);
            assert_eq!(c.capture_state.as_deref(), Some("capturing"));
            assert!(matches!(c.quality.level, QualityLevel::Incomplete));
        }
        for suffix in ["", "-wal", "-shm"] {
            let owned = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
            if owned.exists() {
                std::fs::remove_file(owned).unwrap();
            }
        }
    }

    #[test]
    fn abrupt_exit_child() {
        let Some(path) = std::env::var_os("CONTAIN_STREAM_CRASH_TEST") else {
            return;
        };
        let mut db = Storage::open(Path::new(&path)).unwrap();
        let mut writer = RawBuffer::new(&mut db, &capture(), DEFAULT_QUOTA).unwrap();
        writer.append(&mut db, (1..=257).map(event).collect());
        // Deliberately bypass all Rust destructors, SQLite close and final flush.
        std::process::exit(73);
    }

    #[test]
    fn committed_wal_survives_abrupt_process_exit_without_finish() {
        let path =
            std::env::temp_dir().join(format!("contain-stream-crash-{}.db", uuid::Uuid::new_v4()));
        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "storage::stream::tests::abrupt_exit_child"])
            .env("CONTAIN_STREAM_CRASH_TEST", &path)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .unwrap();
        assert_eq!(status.code(), Some(73));
        {
            let db = Storage::open(&path).unwrap();
            let c = db.load("stream").unwrap();
            assert_eq!(c.events.len(), 256);
            assert_eq!(c.capture_state.as_deref(), Some("capturing"));
            assert!(c.events.iter().all(|e| e.confidence == Confidence::Unknown));
            assert_eq!(c.backend.stream.unwrap().sqlite_synchronous, Some(1));
        }
        for suffix in ["", "-wal", "-shm"] {
            let owned = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
            if owned.exists() {
                std::fs::remove_file(owned).unwrap();
            }
        }
    }
}

#[cfg(test)]
#[path = "replay.rs"]
mod replay;
