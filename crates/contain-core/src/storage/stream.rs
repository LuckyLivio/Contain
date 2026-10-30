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
        CREATE TABLE IF NOT EXISTS raw_selections(session_id TEXT NOT NULL, sequence INTEGER NOT NULL, event_id TEXT NOT NULL, document TEXT NOT NULL, PRIMARY KEY(session_id,sequence));
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
    #[serde(default)]
    pub sqlite_journal_mode: Option<String>,
    pub sqlite_synchronous: Option<u32>,
    pub wal_autocheckpoint_pages: Option<u32>,
    pub sqlite_page_size_bytes: Option<u32>,
    #[serde(default)]
    pub checkpoint_variant: Option<String>,
    #[serde(default)]
    pub wal_budget_bytes: Option<u64>,
    #[serde(default)]
    pub wal_initial_bytes: Option<u64>,
    #[serde(default)]
    pub wal_peak_bytes: Option<u64>,
    #[serde(default)]
    pub wal_growth_bytes: Option<u64>,
    #[serde(default)]
    pub wal_budget_overshoot_bytes: Option<u64>,
    #[serde(default)]
    pub wal_samples: u64,
    #[serde(default)]
    pub wal_budget_exceeded: bool,
    #[serde(default)]
    pub disk_free_min_bytes: Option<u64>,
    #[serde(default)]
    pub disk_reserve_bytes: Option<u64>,
    #[serde(default)]
    pub checkpoint: Option<checkpoint::Report>,
    #[serde(default)]
    pub checkpoint_completed: bool,
    #[serde(default)]
    pub restored_wal_autocheckpoint_pages: Option<u32>,
}

struct RawRow {
    sequence: u64,
    ticks: u64,
    kind: String,
    header_pid: Option<u32>,
    related: Option<String>,
    document: String,
}

pub struct RawBuffer {
    session: String,
    batch: Vec<RawRow>,
    bytes: usize,
    last_flush: Instant,
    pub stats: StreamStats,
}
impl RawBuffer {
    pub fn new(db: &mut Storage, capture: &Capture, quota: u64) -> Result<Self> {
        Self::with_policy(db, capture, quota, checkpoint::Policy::default())
    }
    pub(crate) fn with_policy(
        db: &mut Storage,
        capture: &Capture,
        quota: u64,
        policy: checkpoint::Policy,
    ) -> Result<Self> {
        db.connection.execute_batch(
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA wal_autocheckpoint=4096;",
        )?;
        db.save(capture)?;
        if policy.deferred() {
            db.connection.pragma_update(None, "wal_autocheckpoint", 0)?;
            profile::checkpoint_deferred();
        }
        let stats = StreamStats {
            quota_bytes: quota,
            checkpoint_variant: policy.variant.map(str::to_owned),
            wal_budget_bytes: policy.variant.map(|_| policy.budget),
            sqlite_version: Some(rusqlite::version().into()),
            sqlite_journal_mode: Some(db.connection.pragma_query_value(
                None,
                "journal_mode",
                |r| r.get(0),
            )?),
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
        let mut writer = Self {
            session: capture.id.clone(),
            batch: Vec::with_capacity(PAGE),
            bytes: 0,
            last_flush: Instant::now(),
            stats,
        };
        if let Err(e) = checkpoint::sample(db, &mut writer.stats) {
            checkpoint::record_error(
                &mut writer.stats,
                format!("WAL budget sampling failed: {e:#}"),
            );
        }
        Ok(writer)
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
            self.batch.push(RawRow {
                sequence: e.sequence,
                ticks: e.timestamp_ticks,
                kind: e.event_type,
                header_pid: e.raw.header_pid,
                related: e.raw.related_event,
                document,
            });
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
        let _queue_span = profile::queue_span("raw_batch_flush");
        let start = Instant::now();
        let result = (|| -> Result<()> {
            checkpoint::sample(db, &mut self.stats)?;
            let tx = db.connection.transaction()?;
            {
                let _insert_clock = profile::timer("raw_insert");
                let mut insert =
                    tx.prepare_cached("INSERT INTO raw_ingest VALUES (?1,?2,?3,?4,?5,?6,?7)")?;
                for e in &self.batch {
                    insert.execute(params![
                        self.session,
                        e.sequence,
                        e.ticks,
                        e.kind,
                        e.header_pid,
                        e.related,
                        e.document
                    ])?;
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
            {
                let _queue_span = profile::queue_span("raw_commit_including_autocheckpoint");
                measured!("raw_commit_including_autocheckpoint", tx.commit())?;
            }
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
                if let Err(e) = checkpoint::sample(db, &mut self.stats) {
                    checkpoint::record_error(
                        &mut self.stats,
                        format!("WAL budget sampling failed: {e:#}"),
                    );
                }
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
        // Caller has stopped the producer and drained its queue before finish.
        // Do not retry or escalate PASSIVE: partial results remain explicit failures.
        checkpoint::complete(db, &mut self.stats);
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
        anyhow::ensure!(
            self.stats.checkpoint_variant.as_deref() != Some("E1")
                || self.stats.restored_wal_autocheckpoint_pages == Some(checkpoint::AUTO_PAGES),
            "automatic checkpoint restoration failed; postprocessing stopped"
        );
        Ok(self.stats)
    }
}

impl Storage {
    /// Build post-capture indexes through the existing indexed journal. All rows stay
    /// in the crash-readable staging table until this transaction commits atomically.
    /// SQLite scans the PK in bounded pages; no Rust full-session event vector.
    pub(crate) fn prepare_raw(&mut self, session: &str) -> Result<()> {
        let _timer = profile::timer("post_raw_index_materialization");
        let result = (|| -> Result<()> {
            let tx = self.connection.transaction()?;
            let mut after: i64 = -1;
            loop {
                let last: Option<i64> = tx.query_row(
                    "SELECT MAX(sequence) FROM (SELECT sequence FROM raw_ingest WHERE session_id=?1 AND sequence>?2 ORDER BY sequence LIMIT 256)",
                    params![session, after], |r| r.get(0))?;
                let Some(last) = last else {
                    break;
                };
                tx.execute("INSERT INTO raw_stream SELECT * FROM raw_ingest WHERE session_id=?1 AND sequence>?2 AND sequence<=?3 ORDER BY sequence", params![session,after,last])?;
                after = last;
            }
            tx.execute("DELETE FROM raw_ingest WHERE session_id=?1", [session])?;
            tx.commit()?;
            Ok(())
        })();
        if let Err(error) = &result {
            // If even this write fails, last committed 'finalizing' is still incomplete.
            let _ = self.connection.execute(
                "UPDATE capture_runs SET state='failed',error=?2 WHERE session_id=?1",
                params![
                    session,
                    format!("raw index materialization failed: {error:#}")
                ],
            );
        }
        result
    }
    pub(crate) fn validate_diagnostic_location(&self, diagnostic: &std::path::Path) -> Result<()> {
        if let Some(database) = self.connection.path().filter(|p| *p != ":memory:") {
            let diagnostic = std::path::absolute(diagnostic)?;
            let filename = diagnostic
                .file_name()
                .context("diagnostic filename required")?;
            let diagnostic = diagnostic
                .parent()
                .context("diagnostic parent required")?
                .canonicalize()?
                .join(filename);
            let diagnostic = crate::path::normalize_path(&diagnostic.to_string_lossy(), &[]);
            let database = std::fs::canonicalize(database)?;
            let database = crate::path::normalize_path(&database.to_string_lossy(), &[]);
            anyhow::ensure!(
                !["", "-wal", "-shm"]
                    .iter()
                    .any(|suffix| diagnostic == format!("{database}{suffix}")),
                "Raw clock sidecar must not replace the capture database, WAL or SHM"
            );
        }
        Ok(())
    }
    pub fn validate_capture_location(&self, roots: &[String]) -> Result<()> {
        if let Some(path) = self.connection.path().filter(|p| *p != ":memory:") {
            let path = std::fs::canonicalize(path)?;
            let path = crate::path::normalize_path(&path.to_string_lossy(), &[]);
            let roots: Vec<_> = roots
                .iter()
                .map(|p| crate::path::normalize_path(p, &[]))
                .collect();
            anyhow::ensure!(
                !crate::path::in_scope(&path, &roots),
                "Capture database, WAL and SHM must be outside watched roots; choose --db outside that scope"
            );
        }
        Ok(())
    }
    fn raw_table(&self, session: &str) -> Result<&'static str> {
        let staged: bool = self.connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM raw_ingest WHERE session_id=?1)",
            [session],
            |r| r.get(0),
        )?;
        // The staging-to-indexed move is atomic, so one session is never split.
        Ok(if staged { "raw_ingest" } else { "raw_stream" })
    }
    pub(crate) fn raw_page(&self, session: &str, after: (u64, u64)) -> Result<Vec<SystemEvent>> {
        let _clock = profile::timer("post_raw_read");
        let table = self.raw_table(session)?;
        let sql = format!(
            "SELECT document FROM {table} WHERE session_id=?1 AND (ticks,sequence)>(?2,?3) ORDER BY ticks,sequence LIMIT 256"
        );
        let mut q = self.connection.prepare_cached(&sql)?;
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
    /// Called after lifetime resolution and actor attribution, at the existing
    /// registry selection boundary. Retain positive evidence of the actual
    /// decision without changing a byte of the committed raw event.
    ///
    /// The optional table is auxiliary evidence in v4 databases. Old databases
    /// have no witnesses; their missing final records remain unadjudicated.
    pub(crate) fn select_raw_only_registry(
        &mut self,
        session: &str,
        event: &SystemEvent,
        lifetime_resolution_applied: bool,
    ) -> Result<bool> {
        if event.event_type != "registry"
            || event.raw.resource_resolved
            || event.confidence == Confidence::High
        {
            return Ok(false);
        }
        // Other registry operations retain the same production selection, but
        // the bounded clock audit currently supports only SetValue records.
        if event.operation != "set_value" {
            return Ok(true);
        }
        anyhow::ensure!(
            !self.connection.is_autocommit(),
            "Selection witness must share the derived evidence transaction"
        );
        let document: String = self.connection.query_row(
            "SELECT document FROM raw_stream WHERE session_id=?1 AND sequence=?2 AND json_extract(document,'$.id')=?3",
            params![session, event.sequence, event.id], |r| r.get(0),
        )?;
        use sha2::{Digest, Sha256};
        let witness = serde_json::json!({
            "schema_version": 1, "event_id": event.id, "sequence": event.sequence,
            "raw_document_sha256": format!("{:x}", Sha256::digest(document.as_bytes())),
            "stage": "post_resolve_post_attribute", "decision": "raw_only",
            "reason": "unresolved_registry_non_high",
            "lifetime_resolution_applied": lifetime_resolution_applied,
            "event_type": event.event_type, "operation": event.operation,
            "resource_resolved": event.raw.resource_resolved,
            "post_attribution_confidence": event.confidence,
            "post_attribution_rule": event.evidence.rule,
        });
        self.connection.execute(
            "INSERT INTO raw_selections(session_id,sequence,event_id,document) VALUES (?1,?2,?3,?4)",
            params![session, event.sequence, event.id, serde_json::to_string(&witness)?],
        )?;
        Ok(true)
    }
    /// At most four decoded pages per caller group. Pages are processed and released
    /// individually; SQLite's bounded page cache spills to WAL as necessary.
    pub(crate) fn evidence_group<T>(
        &mut self,
        session: &str,
        write: impl FnOnce(&mut Self) -> Result<T>,
    ) -> Result<T> {
        anyhow::ensure!(self.connection.is_autocommit(), "Nested evidence group");
        self.connection.execute_batch("BEGIN IMMEDIATE")?;
        let result = write(self).and_then(|value| {
            measured!(
                "post_derived_group_commit",
                self.connection.execute_batch("COMMIT")
            )?;
            Ok(value)
        });
        if let Err(error) = &result {
            if !self.connection.is_autocommit() {
                self.connection.execute_batch("ROLLBACK")?;
            }
            // A full/unavailable disk can also prevent reporting the failure. In that
            // case the previous unfinished state remains visible and never promotes.
            let _ = self.connection.execute(
                "UPDATE capture_runs SET state='failed',error=?2 WHERE session_id=?1",
                params![
                    session,
                    format!("derived group persistence failed: {error:#}")
                ],
            );
        }
        result
    }
    pub(crate) fn append_evidence(&mut self, c: &Capture) -> Result<()> {
        let _clock = profile::timer("post_derived_write");
        let tx = self.connection.savepoint()?;
        measured!("post_observations", evidence::save_events(&tx, c))?;
        measured!("post_details_edges", reliability::save_details(&tx, c))?;
        measured!("post_documents", save_documents(&tx, c))?;
        measured!("post_derived_page_release", tx.commit())?;
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
                params![session, crate::path::normalize_path(resource, &[])],
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
    /// Persist the exact observations validated by snapshot association. Call inside
    /// an evidence_group so resources share one transaction and keep one bounded
    /// resource page in memory. Never infer registry NT paths from snapshot names.
    pub(crate) fn persist_state_validation(
        &mut self,
        session: &str,
        events: &[SystemEvent],
    ) -> Result<()> {
        if !events
            .iter()
            .any(|e| e.state_validated && e.confidence == Confidence::High)
        {
            return Ok(());
        }
        anyhow::ensure!(
            !self.connection.is_autocommit(),
            "State validation requires an evidence group"
        );
        let mut observation = self.connection.prepare_cached(
            "UPDATE observations SET state_validated=1 WHERE session_id=?1 AND id=?2 AND confidence='High' AND ((event_type='file' AND operation NOT IN ('open_requested','close') AND success IS NOT 0) OR (event_type='registry' AND success=1))",
        )?;
        let mut document = self.connection.prepare_cached(
            "UPDATE event_documents SET document=json_set(document,'$.state_validated',json('true')) WHERE session_id=?1 AND id=?2 AND EXISTS(SELECT 1 FROM observations o WHERE o.session_id=?1 AND o.id=?2 AND o.state_validated=1)",
        )?;
        for event in events
            .iter()
            .filter(|e| e.state_validated && e.confidence == Confidence::High)
        {
            observation.execute(params![session, event.id])?;
            document.execute(params![session, event.id])?;
        }
        Ok(())
    }
    pub(crate) fn mark_finished(&mut self, c: &Capture) -> Result<()> {
        let pending: bool = self.connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM raw_ingest WHERE session_id=?1)",
            [&c.id],
            |r| r.get(0),
        )?;
        anyhow::ensure!(!pending, "Raw index materialization is incomplete");
        // Stream commits survive application exit; final completion also syncs the WAL.
        self.connection.execute_batch("PRAGMA synchronous=FULL;")?;
        let tx = self.connection.transaction()?;
        if !c.backend.source_intact() {
            // This also covers records persisted before a later failure became known.
            // Summary and detailed views cross the same completion boundary: a
            // diagnostic discovered after save() must not leave a stale High
            // summary beside downgraded event documents after reopening.
            tx.execute(
                "UPDATE capture_metadata SET backend_json=?2 WHERE session_id=?1",
                params![c.id, serde_json::to_string(&c.backend)?],
            )?;
            tx.execute("UPDATE system_events SET confidence='Unknown',reason='Session continuity unverified; final promotion suppressed' WHERE session_id=?1 AND event_type IN ('file','registry')",[&c.id])?;
            tx.execute("UPDATE observations SET confidence='Unknown',state_validated=0,rule=?2,reason='Session continuity unverified; final promotion suppressed' WHERE session_id=?1 AND event_type IN ('file','registry')",params![c.id,serde_json::to_string(&AttributionRule::EventLoss)?])?;
            tx.execute("UPDATE evidence_edges SET confidence='Unknown',reason='Session continuity unverified; resource association suppressed' WHERE session_id=?1 AND from_node IN (SELECT 'event:'||id FROM observations WHERE session_id=?1 AND event_type IN ('file','registry'))",[&c.id])?;
            tx.execute("UPDATE normalized_operations SET detail_json=json_set(detail_json,'$.confidence','Unknown') WHERE session_id=?1",[&c.id])?;
            tx.execute("UPDATE normalized_operations SET operation='UnresolvedRequest',detail_json=json_set(detail_json,'$.operation','UnresolvedRequest','$.success',NULL,'$.reason','Session continuity unverified; raw request only') WHERE session_id=?1 AND json_array_length(detail_json,'$.raw_events')>0",[&c.id])?;
            tx.execute("UPDATE observations SET success=NULL WHERE session_id=?1 AND event_type IN ('file','completion')",[&c.id])?;
            tx.execute("UPDATE observation_details SET raw_json=json_set(raw_json,'$.resource_resolved',json('false')),dimensions_json=json_set(dimensions_json,'$.resource','Unknown') WHERE event_id IN (SELECT id FROM observations WHERE session_id=?1 AND event_type IN ('file','registry'))",[&c.id])?;
            tx.execute("UPDATE observation_details SET dimensions_json=json_set(dimensions_json,'$.operation','Unknown') WHERE event_id IN (SELECT id FROM observations WHERE session_id=?1 AND event_type IN ('file','completion'))",[&c.id])?;
            tx.execute("UPDATE event_documents SET document=json_set(document,'$.confidence','Unknown','$.state_validated',json('false'),'$.evidence.rule','EventLoss','$.raw.resource_resolved',json('false'),'$.dimensions.resource','Unknown','$.reason','Session continuity unverified; final promotion suppressed') WHERE session_id=?1 AND json_extract(document,'$.event_type') IN ('file','registry')",[&c.id])?;
            tx.execute("UPDATE event_documents SET document=json_set(document,'$.success',NULL,'$.dimensions.operation','Unknown') WHERE session_id=?1 AND json_extract(document,'$.event_type') IN ('file','completion')",[&c.id])?;
            // Snapshot observations are independent documents. Retain measured
            // identities, hashes and intervals, but revoke a provisional final
            // continuity claim in the same transaction as event promotion.
            // Preserve array order and leave legacy evidence without observation
            // objects unchanged.
            tx.execute(
                "UPDATE change_evidence SET evidence_json=(
                    SELECT json_group_array(json(patched)) FROM (
                        SELECT CASE WHEN json_type(value,'$.observation')='object'
                            THEN json_set(value,'$.observation.final_source_complete',json('false'))
                            ELSE value END AS patched
                        FROM json_each(change_evidence.evidence_json) ORDER BY CAST(key AS INTEGER)
                    )
                ) WHERE session_id=?1 AND kind IN ('file','registry') AND EXISTS (
                    SELECT 1 FROM json_each(change_evidence.evidence_json)
                    WHERE json_extract(value,'$.observation.final_source_complete')=1
                )",
                [&c.id],
            )?;
            let metadata: Option<(String, String)> = tx
                .query_row(
                    "SELECT stats_json,quality_json FROM reliability_metadata WHERE session_id=?1",
                    [&c.id],
                    |row| Ok((row.get(0)?, row.get(1)?)),
                )
                .optional()?;
            if let Some((stats, quality)) = metadata {
                // Keep the persisted phase measurements, including save()'s
                // final-commit measurement, rather than replacing them with an
                // earlier in-memory stats snapshot.
                let mut stats: CaptureStats = serde_json::from_str(&stats)?;
                let mut quality: CaptureQuality = serde_json::from_str(&quality)?;
                stats.events_dropped = c.backend.dropped_events;
                stats.high_confidence_events = 0;
                stats.known_resource_events = 0;
                stats.unknown_resource_events = tx.query_row(
                    "SELECT COUNT(*) FROM observations WHERE session_id=?1 AND event_type IN ('file','registry')",
                    [&c.id], |row| row.get(0),
                )?;
                if c.backend.has_loss()
                    || (c.backend.etw_file == "active"
                        && (c.backend.etw_events_lost.is_none()
                            || c.backend.etw_buffers_lost.is_none()))
                {
                    quality.level = QualityLevel::Incomplete;
                    let reason =
                        "Session continuity unverified; final attribution promotion suppressed";
                    if !quality.reasons.iter().any(|existing| existing == reason) {
                        quality.reasons.push(reason.into());
                    }
                }
                tx.execute(
                    "UPDATE reliability_metadata SET stats_json=?2,quality_json=?3 WHERE session_id=?1",
                    params![c.id, serde_json::to_string(&stats)?, serde_json::to_string(&quality)?],
                )?;
            }
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
            let raw:Option<(String,String)>=self.connection.query_row("SELECT session_id,document FROM raw_stream WHERE json_extract(document,'$.id')=?1 UNION ALL SELECT session_id,document FROM raw_ingest WHERE json_extract(document,'$.id')=?1 LIMIT 1",[id],|r|Ok((r.get(0)?,r.get(1)?))).optional()?;
            if let Some((session, document)) = raw {
                let mut c = self.load_summary(&session)?;
                let mut e: SystemEvent = serde_json::from_str(&document)?;
                suppress_unfinished(&mut e);
                if c.capture_state.as_deref().is_none_or(|s| s == "finished") {
                    e.reason="Raw journal evidence without a finalized observation; no attribution promotion".into();
                }
                c.events.push(e);
                return Ok(Some(c));
            }
            return Ok(None);
        };
        let mut c = self.load_summary(&session)?;
        if c.capture_state.as_deref().is_some_and(|s| s != "finished") {
            // Derived rows may have committed before an interrupted finalization.
            // Explain the same committed raw record as paged/full history, never
            // the stale derived document or its provisional ancestry edges.
            let table = self.raw_table(&session)?;
            let sql = format!(
                "SELECT document FROM {table} WHERE session_id=?1 AND json_extract(document,'$.id')=?2 LIMIT 1"
            );
            let raw: Option<String> = self
                .connection
                .query_row(&sql, params![session, id], |row| row.get(0))
                .optional()?;
            let raw = raw
                .context("Unfinished session has no committed raw record for this derived event")?;
            let mut e = serde_json::from_str(&raw)?;
            suppress_unfinished(&mut e);
            c.events.push(e);
            return Ok(Some(c));
        }
        c.events.push(serde_json::from_str(&document)?);
        // The full explanation includes outgoing edges of the selected event.
        // Query them once using the primary-key prefix; do not scan all edges
        // in this session for every ancestor or traverse shared resource nodes.
        let event_node = format!("event:{id}");
        let mut outgoing = self.connection.prepare_cached("SELECT from_node,to_node,relation,confidence,reason FROM evidence_edges WHERE session_id=?1 AND from_node=?2 AND to_node<>?2 LIMIT 4097")?;
        for row in outgoing.query_map(params![session, event_node], |r| {
            Ok(EvidenceEdge {
                from: r.get(0)?,
                to: r.get(1)?,
                relation: r.get(2)?,
                confidence: super::parse_confidence(&r.get::<_, String>(3)?),
                reason: r.get(4)?,
            })
        })? {
            c.edges.push(row?);
            anyhow::ensure!(c.edges.len() <= 4096, "Explanation edge quota exceeded");
        }
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
            format!(
                "SELECT document FROM {} WHERE session_id=?1 ORDER BY ticks,sequence LIMIT ?2 OFFSET ?3",
                self.raw_table(session)?
            )
        } else {
            "SELECT document FROM event_documents WHERE session_id=?1 ORDER BY ticks,sequence,id LIMIT ?2 OFFSET ?3".into()
        };
        let mut q = self.connection.prepare_cached(&sql)?;
        let mut rows = q
            .query_map(params![session, limit, offset], |r| r.get::<_, String>(0))?
            .map(|r| Ok(serde_json::from_str(&r?)?))
            .collect::<Result<Vec<SystemEvent>>>()?;
        if unfinished {
            for e in &mut rows {
                suppress_unfinished(e);
            }
        }
        Ok(rows)
    }
}

fn suppress_unfinished(event: &mut SystemEvent) {
    event.confidence = Confidence::Unknown;
    // These are conclusions of final association, not raw provider fields.
    // Keep the original header/status/identity payload for later inspection.
    event.dimensions = ConfidenceDimensions::default();
    event.success = None;
    event.state_validated = false;
    event.reason = "Unfinished session: raw evidence only; no final attribution".into();
}

/// Final continuity is not established for an interrupted capture, even when a
/// previous summary transaction retained a provisional observation document.
pub(super) fn suppress_unfinished_state_evidence(
    db: &Storage,
    capture: &mut Capture,
) -> Result<()> {
    let table = db.raw_table(&capture.id)?;
    let sql = format!(
        "SELECT COUNT(*), COALESCE(SUM(CASE WHEN kind IN ('file','registry') THEN 1 ELSE 0 END),0) FROM {table} WHERE session_id=?1"
    );
    let (retained, relevant): (u64, u64) = db
        .connection
        .query_row(&sql, [&capture.id], |row| Ok((row.get(0)?, row.get(1)?)))?;
    // Match the committed raw projection used by events_page(), including its
    // deliberate removal of every derived confidence dimension. Other counters
    // and phase measurements cannot be rebuilt here and remain as recorded.
    capture.stats.events_retained = retained;
    capture.stats.events_normalized = 0;
    capture.stats.high_confidence_events = 0;
    capture.stats.known_actor_events = 0;
    capture.stats.known_resource_events = 0;
    capture.stats.unknown_actor_events = relevant;
    capture.stats.unknown_resource_events = relevant;
    for evidence in capture
        .files
        .iter_mut()
        .flat_map(|change| &mut change.evidence)
        .chain(
            capture
                .registry
                .iter_mut()
                .flat_map(|change| &mut change.evidence),
        )
    {
        if let Some(observation) = &mut evidence.observation {
            observation.final_source_complete = false;
        }
    }
    Ok(())
}

pub(super) fn save_documents(tx: &Connection, c: &Capture) -> Result<()> {
    let mut q = tx.prepare_cached("INSERT INTO event_documents VALUES (?1,?2,?3,?4,?5,?6)")?;
    for e in &c.events {
        q.execute(params![
            e.id,
            c.id,
            e.timestamp_ticks,
            e.sequence,
            crate::path::normalize_path(&e.resource, &[]),
            serde_json::to_string(e)?
        ])?;
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    #[test]
    fn diagnostic_sidecar_cannot_target_database_or_sqlite_sidecars() {
        let directory = std::env::temp_dir().join(format!(
            "contain-diagnostic-location-{}",
            uuid::Uuid::new_v4()
        ));
        std::fs::create_dir(&directory).unwrap();
        let database = directory.join("capture.db");
        let db = Storage::open(&database).unwrap();
        for suffix in ["", "-wal", "-shm"] {
            let path = directory.join(format!("CAPTURE.DB{suffix}"));
            assert!(db.validate_diagnostic_location(&path).is_err());
        }
        assert!(
            db.validate_diagnostic_location(&directory.join("clock.json"))
                .is_ok()
        );
        assert!(
            db.validate_diagnostic_location(&directory.join("missing/clock.json"))
                .is_err()
        );
        drop(db);
        std::fs::remove_dir_all(directory).unwrap();
    }
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
    fn actual_raw_only_selection_is_bound_atomic_and_readable_after_reopen() {
        let directory =
            std::env::temp_dir().join(format!("contain-selection-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = directory.join("capture.db");
        let mut db = Storage::open(&path).unwrap();
        let c = capture();
        let mut raw = event(1);
        raw.event_type = "registry".into();
        raw.operation = "set_value".into();
        raw.raw.resource_resolved = true;
        raw.evidence.source = EvidenceSource::EtwRegistry;
        let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        writer.append(&mut db, vec![raw.clone()]);
        writer.finish(&mut db).unwrap();
        db.prepare_raw(&c.id).unwrap();
        let original: String = db
            .connection
            .query_row("SELECT document FROM raw_stream", [], |r| r.get(0))
            .unwrap();
        let mut resolved = raw.clone();
        // Lifetime resolution can invalidate a name originally present. The
        // witness records the real selection input, not the original flag.
        resolved.raw.resource_resolved = false;
        resolved.confidence = Confidence::Unknown;
        resolved.evidence.rule = AttributionRule::MissingWriter;
        assert!(db.select_raw_only_registry(&c.id, &resolved, true).is_err());
        db.evidence_group(&c.id, |db| {
            let mut retained = resolved.clone();
            retained.confidence = Confidence::High;
            assert!(!db.select_raw_only_registry(&c.id, &retained, true)?);
            retained.confidence = Confidence::Unknown;
            retained.raw.resource_resolved = true;
            assert!(!db.select_raw_only_registry(&c.id, &retained, true)?);
            assert!(!db.select_raw_only_registry(&c.id, &event(1), true)?);
            assert!(db.select_raw_only_registry(&c.id, &resolved, true)?);
            Ok(())
        })
        .unwrap();
        drop(db);
        let db = Storage::open(&path).unwrap();
        assert_eq!(
            db.connection
                .query_row::<u32, _, _>("PRAGMA user_version", [], |r| r.get(0))
                .unwrap(),
            4
        );
        let after: String = db
            .connection
            .query_row("SELECT document FROM raw_stream", [], |r| r.get(0))
            .unwrap();
        assert_eq!(original, after);
        let witness: String = db
            .connection
            .query_row("SELECT document FROM raw_selections", [], |r| r.get(0))
            .unwrap();
        let witness: serde_json::Value = serde_json::from_str(&witness).unwrap();
        use sha2::{Digest, Sha256};
        assert_eq!(
            witness["raw_document_sha256"],
            format!("{:x}", Sha256::digest(original.as_bytes()))
        );
        assert_eq!(witness["event_id"], raw.id);
        assert_eq!(witness["resource_resolved"], false);
        assert_eq!(witness["post_attribution_confidence"], "Unknown");
        assert_eq!(
            db.connection
                .query_row::<u32, _, _>("SELECT COUNT(*) FROM observations", [], |r| r.get(0))
                .unwrap(),
            0
        );
        drop(db);
        std::fs::remove_dir_all(directory).unwrap();
    }

    #[test]
    fn selection_witness_rolls_back_with_failed_derived_group() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = capture();
        let mut raw = event(1);
        raw.event_type = "registry".into();
        raw.operation = "set_value".into();
        let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        writer.append(&mut db, vec![raw.clone()]);
        writer.finish(&mut db).unwrap();
        db.prepare_raw(&c.id).unwrap();
        assert!(
            db.evidence_group(&c.id, |db| {
                assert!(db.select_raw_only_registry(&c.id, &raw, false)?);
                // Duplicate selection is an error, never overwrite the first proof.
                db.select_raw_only_registry(&c.id, &raw, false)
            })
            .is_err()
        );
        assert_eq!(
            db.connection
                .query_row::<u32, _, _>("SELECT COUNT(*) FROM raw_selections", [], |r| r.get(0))
                .unwrap(),
            0
        );
        assert_eq!(db.raw_page(&c.id, (0, 0)).unwrap().len(), 1);
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
        db.connection.execute_batch("CREATE TRIGGER reject_raw BEFORE INSERT ON raw_ingest BEGIN SELECT RAISE(ABORT,'injected write failure'); END;").unwrap();
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
        db.prepare_raw(&c.id).unwrap();
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
    fn interrupted_derived_group_rolls_back_without_losing_committed_raw() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = capture();
        let mut w = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        w.append(&mut db, (1..=257).map(event).collect());
        w.finish(&mut db).unwrap();
        db.prepare_raw(&c.id).unwrap();
        let mut page = c.clone();
        page.events = vec![event(1)];
        assert!(
            db.evidence_group(&c.id, |db| {
                db.append_evidence(&page)?;
                db.append_evidence(&page)
            })
            .is_err()
        );
        assert!(db.connection.is_autocommit());
        assert_eq!(
            db.connection
                .query_row("SELECT COUNT(*) FROM observations", [], |r| r
                    .get::<_, u64>(0))
                .unwrap(),
            0
        );
        let loaded = db.load(&c.id).unwrap();
        assert_eq!(loaded.events.len(), 257);
        assert_eq!(loaded.capture_state.as_deref(), Some("failed"));
        assert!(
            loaded
                .backend
                .stream
                .as_ref()
                .unwrap()
                .error
                .as_ref()
                .unwrap()
                .contains("derived group persistence")
        );
        assert!(
            loaded
                .events
                .iter()
                .all(|e| e.confidence == Confidence::Unknown)
        );
    }

    #[test]
    fn v3_raw_history_migrates_without_losing_indexes_or_paged_order() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = capture();
        let mut w = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        w.append(&mut db, (1..=257).rev().map(event).collect());
        w.finish(&mut db).unwrap();
        db.prepare_raw(&c.id).unwrap();
        db.connection
            .execute_batch("DROP TABLE raw_ingest; PRAGMA user_version=3;")
            .unwrap();
        let mut db = Storage::from_connection(db.connection).unwrap();
        let page = db.raw_page(&c.id, (0, 0)).unwrap();
        assert_eq!(page.first().unwrap().sequence, 1);
        assert_eq!(page.last().unwrap().sequence, 256);
        let mut newer = c.clone();
        newer.id = "new-session".into();
        let mut w = RawBuffer::new(&mut db, &newer, DEFAULT_QUOTA).unwrap();
        w.append(&mut db, vec![event(500)]);
        w.finish(&mut db).unwrap();
        for session in [&c.id, &newer.id] {
            let table = db.raw_table(session).unwrap();
            let sql = format!(
                "EXPLAIN QUERY PLAN SELECT document FROM {table} WHERE session_id=?1 AND (ticks,sequence)>(?2,?3) ORDER BY ticks,sequence LIMIT 256"
            );
            let mut q = db.connection.prepare(&sql).unwrap();
            let plan: Vec<String> = q
                .query_map(params![session, 0, 0], |r| r.get(3))
                .unwrap()
                .map(Result::unwrap)
                .collect();
            assert!(plan.iter().all(|s| !s.contains("TEMP B-TREE")), "{plan:?}");
            assert!(plan.iter().any(|s| s.contains("INDEX")), "{plan:?}");
        }
        assert_eq!(db.load(&c.id).unwrap().events.len(), 257);
        assert_eq!(db.load(&newer.id).unwrap().events.len(), 1);
    }

    #[test]
    fn staged_scalars_and_atomic_index_failure_preserve_raw_evidence() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = capture();
        let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        let mut rows: Vec<_> = (1..=257).map(event).collect();
        for e in &mut rows {
            e.timestamp_ticks += 134_000_000_000_000_000;
            e.raw.header_pid = Some(42);
        }
        rows[256].raw.related_event = Some("e1".into());
        writer.append(&mut db, rows);
        writer.finish(&mut db).unwrap();
        assert!(db.mark_finished(&c).is_err());
        let ticks: u64 = db
            .connection
            .query_row("SELECT ticks FROM raw_ingest WHERE sequence=257", [], |r| {
                r.get(0)
            })
            .unwrap();
        assert_eq!(ticks, 134_000_000_000_000_257);
        // The first page was copied inside the transaction before the next insert aborts.
        db.connection.execute_batch("CREATE TRIGGER interrupt_index BEFORE INSERT ON raw_stream WHEN NEW.sequence=257 BEGIN SELECT RAISE(ABORT,'interrupted index build'); END;").unwrap();
        assert!(db.prepare_raw(&c.id).is_err());
        assert_eq!(
            db.connection
                .query_row("SELECT COUNT(*) FROM raw_stream", [], |r| r
                    .get::<_, u64>(0))
                .unwrap(),
            0
        );
        assert_eq!(db.events_page(&c.id, 0, 4096).unwrap().len(), 257);
        let loaded = db.load(&c.id).unwrap();
        assert!(matches!(loaded.quality.level, QualityLevel::Incomplete));
        assert!(
            loaded
                .backend
                .stream
                .unwrap()
                .error
                .unwrap()
                .contains("index materialization")
        );
        assert_eq!(
            db.for_event("e257").unwrap().events[0].confidence,
            Confidence::Unknown
        );
        db.connection
            .execute_batch("DROP TRIGGER interrupt_index")
            .unwrap();
        db.prepare_raw(&c.id).unwrap();
        assert_eq!(db.completion(&c.id, "e1").unwrap().unwrap().sequence, 257);
        assert!(db.scoped_header(&c.id, 42).unwrap());
        // Derived data can be partially committed and still must not promote a session.
        let mut provisional = c.clone();
        let mut e = event(1);
        e.confidence = Confidence::High;
        provisional.events = vec![e];
        db.append_evidence(&provisional).unwrap();
        let loaded = db.load(&c.id).unwrap();
        assert_eq!(loaded.events.len(), 257);
        assert!(
            loaded
                .events
                .iter()
                .all(|e| e.confidence == Confidence::Unknown)
        );
        assert!(loaded.operations.is_empty() && loaded.edges.is_empty());
        // Every historical raw index survives; no global index drop was used.
        assert_eq!(db.connection.query_row("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('raw_stream_time','raw_stream_related','raw_stream_header','raw_stream_id')",[],|r|r.get::<_,u64>(0)).unwrap(),4);
    }

    #[test]
    fn completion_lookup_rejects_duplicate_or_conflicting_records_in_either_order() {
        for statuses in [
            [Some(true), Some(false)],
            [Some(false), Some(true)],
            [Some(true), Some(true)],
            [Some(false), Some(false)],
            [None, Some(true)],
        ] {
            let mut db = Storage::open(Path::new(":memory:")).unwrap();
            let c = capture();
            let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
            let mut rows = vec![event(1)];
            for (i, status) in statuses.into_iter().enumerate() {
                let mut completion = event(i as u64 + 2);
                completion.event_type = "completion".into();
                completion.operation = "operation_end".into();
                completion.raw.related_event = Some("e1".into());
                completion.success = status;
                rows.push(completion);
            }
            writer.append(&mut db, rows);
            writer.finish(&mut db).unwrap();
            db.prepare_raw(&c.id).unwrap();
            assert!(db.completion(&c.id, "e1").unwrap().is_none());
            assert_eq!(db.raw_page(&c.id, (0, 0)).unwrap().len(), 3);
        }
    }

    #[test]
    fn completion_lookup_preserves_unique_result_and_isolates_sessions() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        for (session, success) in [("first", true), ("second", false)] {
            let mut c = capture();
            c.id = session.into();
            let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
            let mut completion = event(2);
            completion.id = format!("{session}-completion");
            completion.event_type = "completion".into();
            completion.raw.related_event = Some("same-request".into());
            completion.success = Some(success);
            writer.append(&mut db, vec![completion]);
            writer.finish(&mut db).unwrap();
            db.prepare_raw(&c.id).unwrap();
        }
        assert_eq!(
            db.completion("first", "same-request")
                .unwrap()
                .unwrap()
                .success,
            Some(true)
        );
        assert_eq!(
            db.completion("second", "same-request")
                .unwrap()
                .unwrap()
                .success,
            Some(false)
        );
        assert!(db.completion("absent", "same-request").unwrap().is_none());
        assert!(db.completion("first", "absent").unwrap().is_none());
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
