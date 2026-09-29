use crate::model::{Capture, Confidence, FileChange, ProcessRecord, RegistryChange};
use anyhow::{Context, Result};
use rusqlite::{Connection, OptionalExtension, params};
use std::fs;
use std::path::Path;
mod evidence;
mod migration;
mod reliability;
pub mod stream;

pub struct Storage {
    connection: Connection,
}

impl Storage {
    pub fn open(path: &Path) -> Result<Self> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let connection = Connection::open(path)
            .with_context(|| format!("opening database {}", path.display()))?;
        Self::from_connection(connection)
    }

    fn from_connection(mut connection: Connection) -> Result<Self> {
        migration::check_version(&connection)?;
        connection.execute_batch(include_str!("storage/v1.sql"))?;
        migration::apply(&mut connection)?;
        connection.busy_timeout(std::time::Duration::from_millis(250))?;
        stream::schema(&connection)?;
        Ok(Self { connection })
    }

    pub fn save(&mut self, capture: &Capture) -> Result<()> {
        let persistence_clock = std::time::Instant::now();
        let tx = self.connection.transaction()?;
        tx.execute(
            "INSERT INTO applications(id,name,installer) VALUES (?1,?2,?3) ON CONFLICT(id) DO UPDATE SET name=excluded.name,installer=excluded.installer",
            params![capture.id, capture.name, capture.installer],
        )?;
        tx.execute("INSERT INTO installation_sessions(id,application_id,started_at,finished_at,exit_code,watch_roots_json,registry_key,warnings_json) VALUES (?1,?1,?2,?3,?4,?5,?6,?7) ON CONFLICT(id) DO UPDATE SET finished_at=excluded.finished_at,exit_code=excluded.exit_code,warnings_json=excluded.warnings_json",
            params![capture.id, capture.started_at, capture.finished_at, capture.exit_code,
                serde_json::to_string(&capture.watch_roots)?, capture.registry_key, serde_json::to_string(&capture.warnings)?])?;
        for process in &capture.processes {
            tx.execute(
                "INSERT OR IGNORE INTO processes VALUES (?1,?2,?3,?4,?5,?6,?7)",
                params![
                    capture.id,
                    process.pid,
                    process.parent_pid,
                    process.image,
                    process.first_seen,
                    process.confidence.as_str(),
                    process.reason
                ],
            )?;
        }
        for file in &capture.files {
            tx.execute("INSERT INTO system_events(session_id,event_type,resource,operation,observed_at,process_id,confidence,reason) VALUES (?1,'file',?2,?3,?4,NULL,?5,?6)",
                params![capture.id, file.path, file.operation, capture.finished_at, file.confidence.as_str(), file.reason])?;
            let id = tx.last_insert_rowid();
            tx.execute(
                "INSERT INTO file_changes VALUES (?1,?2,?3,?4,?5)",
                params![
                    id,
                    file.before_hash,
                    file.after_hash,
                    file.after_size,
                    file.notification_seen
                ],
            )?;
        }
        for change in &capture.registry {
            tx.execute("INSERT INTO system_events(session_id,event_type,resource,operation,observed_at,process_id,confidence,reason) VALUES (?1,'registry',?2,?3,?4,NULL,?5,?6)",
                params![capture.id, format!("{}\\{}", change.key, change.name), change.operation, capture.finished_at, change.confidence.as_str(), change.reason])?;
            let id = tx.last_insert_rowid();
            tx.execute(
                "INSERT INTO registry_changes VALUES (?1,?2,?3,?4,?5)",
                params![
                    id,
                    change.key,
                    change.name,
                    change.before_value,
                    change.after_value
                ],
            )?;
        }
        evidence::save(&tx, capture)?;
        reliability::save(&tx, capture)?;
        stream::save_documents(&tx, capture)?;
        tx.commit()?;
        let mut stats = capture.stats.clone();
        stats.phase_ms.insert(
            "final_persistence_commit".into(),
            persistence_clock.elapsed().as_millis() as u64,
        );
        self.connection.execute(
            "UPDATE reliability_metadata SET stats_json=?1 WHERE session_id=?2",
            params![serde_json::to_string(&stats)?, capture.id],
        )?;
        Ok(())
    }

    pub fn list(&self) -> Result<Vec<(String, String, String)>> {
        let mut statement = self.connection.prepare("SELECT a.id,a.name,s.started_at FROM applications a JOIN installation_sessions s ON s.application_id=a.id ORDER BY s.started_at DESC")?;
        let rows = statement.query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))?;
        rows.collect::<rusqlite::Result<Vec<_>>>()
            .map_err(Into::into)
    }

    pub fn load(&self, identity: &str) -> Result<Capture> {
        self.load_internal(identity, true)
    }
    pub fn load_summary(&self, identity: &str) -> Result<Capture> {
        self.load_internal(identity, false)
    }
    fn load_internal(&self, identity: &str, full: bool) -> Result<Capture> {
        let header = self.connection.query_row(
            "SELECT a.id,a.name,a.installer,s.started_at,s.finished_at,s.exit_code,s.watch_roots_json,s.registry_key,s.warnings_json
             FROM applications a JOIN installation_sessions s ON s.application_id=a.id
             WHERE a.id=?1 OR a.name=?1 COLLATE NOCASE ORDER BY s.started_at DESC LIMIT 1",
            [identity], |row| Ok((row.get::<_, String>(0)?,row.get::<_, String>(1)?,row.get::<_, String>(2)?,row.get::<_, String>(3)?,row.get::<_, String>(4)?,row.get::<_, Option<i32>>(5)?,row.get::<_, String>(6)?,row.get::<_, Option<String>>(7)?,row.get::<_, String>(8)?))
        ).optional()?.with_context(|| format!("app not found: {identity}"))?;
        let mut processes = Vec::new();
        let mut statement = self.connection.prepare("SELECT pid,parent_pid,image,first_seen,confidence,reason FROM processes WHERE session_id=?1 ORDER BY first_seen,pid")?;
        for row in statement.query_map([&header.0], |row| {
            Ok(ProcessRecord {
                pid: row.get(0)?,
                parent_pid: row.get(1)?,
                image: row.get(2)?,
                first_seen: row.get(3)?,
                confidence: parse_confidence(&row.get::<_, String>(4)?),
                reason: row.get(5)?,
                ..Default::default()
            })
        })? {
            processes.push(row?);
        }
        let mut files = Vec::new();
        let mut statement = self.connection.prepare("SELECT e.resource,e.operation,f.before_hash,f.after_hash,f.after_size,f.notification_seen,e.confidence,e.reason FROM system_events e JOIN file_changes f ON f.event_id=e.id WHERE e.session_id=?1 ORDER BY e.resource")?;
        for row in statement.query_map([&header.0], |row| {
            Ok(FileChange {
                path: row.get(0)?,
                operation: row.get(1)?,
                before_hash: row.get(2)?,
                after_hash: row.get(3)?,
                after_size: row.get(4)?,
                notification_seen: row.get(5)?,
                confidence: parse_confidence(&row.get::<_, String>(6)?),
                reason: row.get(7)?,
                ..Default::default()
            })
        })? {
            files.push(row?);
        }
        let mut registry = Vec::new();
        let mut statement = self.connection.prepare("SELECT r.registry_key,r.value_name,e.operation,r.before_value,r.after_value,e.confidence,e.reason FROM system_events e JOIN registry_changes r ON r.event_id=e.id WHERE e.session_id=?1 ORDER BY r.registry_key,r.value_name")?;
        for row in statement.query_map([&header.0], |row| {
            Ok(RegistryChange {
                key: row.get(0)?,
                name: row.get(1)?,
                operation: row.get(2)?,
                before_value: row.get(3)?,
                after_value: row.get(4)?,
                confidence: parse_confidence(&row.get::<_, String>(5)?),
                reason: row.get(6)?,
                ..Default::default()
            })
        })? {
            registry.push(row?);
        }
        let mut capture = Capture {
            id: header.0,
            name: header.1,
            installer: header.2,
            started_at: header.3,
            finished_at: header.4,
            exit_code: header.5,
            watch_roots: serde_json::from_str(&header.6)?,
            registry_key: header.7,
            warnings: serde_json::from_str(&header.8)?,
            processes,
            files,
            registry,
            ..Default::default()
        };
        evidence::load(&self.connection, &mut capture, full)?;
        reliability::load(&self.connection, &mut capture, full)?;
        capture.capture_state = self.stream_state(&capture.id)?;
        if capture
            .capture_state
            .as_deref()
            .is_some_and(|s| s != "finished")
        {
            capture.quality.level = crate::model::QualityLevel::Incomplete;
            capture.quality.reasons.push("Unfinished capture; committed raw evidence remains readable. No final attribution.".into());
            if full {
                capture.events.clear();
                capture.operations.clear();
                capture.edges.clear();
                let mut offset = 0;
                loop {
                    let page = self.events_page(&capture.id, offset, 256)?;
                    if page.is_empty() {
                        break;
                    }
                    offset += page.len() as u64;
                    capture.events.extend(page);
                }
            }
        }
        Ok(capture)
    }

    pub fn for_event(&self, id: &str) -> Result<Capture> {
        let session: String = self
            .connection
            .query_row(
                "SELECT session_id FROM observations WHERE id=?1",
                [id],
                |r| r.get(0),
            )
            .with_context(|| format!("event not found: {id}"))?;
        self.load(&session)
    }
}

fn parse_confidence(value: &str) -> Confidence {
    match value {
        "Certain" => Confidence::Certain,
        "High" => Confidence::High,
        "Medium" => Confidence::Medium,
        "Low" => Confidence::Low,
        _ => Confidence::Unknown,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn migrates_real_v1_tables_without_rewriting_history() {
        let connection = Connection::open_in_memory().unwrap();
        connection
            .execute_batch(include_str!("storage/v1.sql"))
            .unwrap();
        connection.execute_batch("INSERT INTO applications VALUES ('old','Legacy','setup.exe');
            INSERT INTO installation_sessions VALUES ('old','old','start','end',0,'[]',NULL,'[]');
            INSERT INTO processes VALUES ('old',7,1,'child.exe','start','High','sampled');
            INSERT INTO system_events VALUES (1,'old','file','C:\\legacy.txt','created','end',NULL,'Unknown','snapshot');
            INSERT INTO file_changes VALUES (1,NULL,'original-hash',7,1);").unwrap();
        let db = Storage::from_connection(connection).unwrap();
        let capture = db.load("old").unwrap();
        assert_eq!(capture.schema_version, 1);
        assert_eq!(
            capture.files[0].after_hash.as_deref(),
            Some("original-hash")
        );
        assert_eq!(capture.processes[0].confidence, Confidence::Unknown);
        assert!(capture.processes[0].creation_time.is_none());
        assert!(capture.events.is_empty());
        assert_eq!(
            db.connection
                .query_row("SELECT confidence FROM processes", [], |r| r
                    .get::<_, String>(0))
                .unwrap(),
            "High"
        );
        assert_eq!(migration::check_version(&db.connection).unwrap(), 3);
        assert!(capture.warnings.iter().any(|w| w.contains("Legacy")));
    }

    #[test]
    fn newer_schema_is_rejected_before_any_tables_are_created() {
        let connection = Connection::open_in_memory().unwrap();
        connection.pragma_update(None, "user_version", 99).unwrap();
        assert!(
            migration::check_version(&connection)
                .unwrap_err()
                .to_string()
                .contains("newer")
        );
        assert_eq!(
            connection
                .query_row("SELECT COUNT(*) FROM sqlite_master", [], |r| r
                    .get::<_, u32>(0))
                .unwrap(),
            0
        );
        assert!(Storage::from_connection(connection).is_err());
    }

    #[test]
    fn v2_preserves_multiple_instances_of_reused_pid_and_atomic_event_save() {
        use crate::model::*;
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let evidence = AttributionEvidence {
            pid: Some(42),
            process_creation_time: Some(134_350_000_000_000_017),
            source: EvidenceSource::EtwFile,
            rule: AttributionRule::DescendantProcess,
            ..Default::default()
        };
        let mut capture = Capture {
            id: "v2".into(),
            name: "v2".into(),
            schema_version: 2,
            processes: vec![
                ProcessRecord {
                    pid: 42,
                    creation_time: Some(100),
                    ..Default::default()
                },
                ProcessRecord {
                    pid: 42,
                    creation_time: evidence.process_creation_time,
                    evidence: evidence.clone(),
                    ..Default::default()
                },
            ],
            events: vec![SystemEvent {
                id: "event".into(),
                timestamp_ticks: 134_350_000_000_000_018,
                evidence: evidence.clone(),
                ..Default::default()
            }],
            ..Default::default()
        };
        db.save(&capture).unwrap();
        let restored = db.load("v2").unwrap();
        assert_eq!(restored.processes.len(), 2);
        assert_eq!(restored.events[0].evidence, evidence);
        assert_eq!(
            restored.events[0].timestamp_ticks,
            capture.events[0].timestamp_ticks
        );
        capture.id = "duplicate-event".into(); // Event UUID collision must roll back the entire session.
        assert!(db.save(&capture).is_err());
        assert_eq!(db.list().unwrap().len(), 1);
    }
    #[test]
    fn v2_migration_preserves_observations_and_marks_missing_details() {
        let connection = Connection::open_in_memory().unwrap();
        connection
            .execute_batch(include_str!("storage/v1.sql"))
            .unwrap();
        connection
            .execute_batch(include_str!("storage/v2.sql"))
            .unwrap();
        connection.execute_batch("INSERT INTO applications VALUES ('old','old','setup.exe'); INSERT INTO installation_sessions VALUES ('old','old','start','end',0,'[]',NULL,'[]'); INSERT INTO capture_metadata VALUES ('old',2,'{}'); INSERT INTO observations VALUES ('old-event','old','time',123,'file','write_requested','path','Unknown','legacy','EtwFile',NULL,NULL,NULL,NULL,NULL,NULL,'MissingWriter',NULL,0);").unwrap();
        connection
            .execute(
                "UPDATE capture_metadata SET backend_json=?1",
                [serde_json::to_string(&crate::model::BackendReport::default()).unwrap()],
            )
            .unwrap();
        connection.pragma_update(None, "user_version", 2).unwrap();
        connection
            .execute(
                "UPDATE observations SET source=?1,rule=?2",
                params![
                    serde_json::to_string(&crate::model::EvidenceSource::EtwFile).unwrap(),
                    serde_json::to_string(&crate::model::AttributionRule::MissingWriter).unwrap()
                ],
            )
            .unwrap();
        let db = Storage::from_connection(connection).unwrap();
        let c = db.load("old").unwrap();
        assert_eq!(migration::check_version(&db.connection).unwrap(), 3);
        assert_eq!(c.events[0].id, "old-event");
        assert_eq!(c.events[0].timestamp_ticks, 123);
        assert!(c.quality.reasons.iter().any(|r| r.contains("Legacy")));
    }
    #[test]
    fn v3_raw_dimensions_graph_and_equal_timestamp_order_survive_readback() {
        use crate::model::*;
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let c = Capture {
            id: "v3".into(),
            name: "v3".into(),
            schema_version: 3,
            events: vec![
                SystemEvent {
                    id: "later".into(),
                    timestamp_ticks: 100,
                    sequence: 2,
                    raw: RawEvidence {
                        irp: Some("abc".into()),
                        status: Some(0xc0000022),
                        ..Default::default()
                    },
                    dimensions: ConfidenceDimensions {
                        actor: Confidence::High,
                        ..Default::default()
                    },
                    ..Default::default()
                },
                SystemEvent {
                    id: "earlier".into(),
                    timestamp_ticks: 100,
                    sequence: 1,
                    ..Default::default()
                },
            ],
            operations: vec![NormalizedOperation {
                id: "op".into(),
                raw_events: vec!["later".into()],
                operation: "Failed".into(),
                ..Default::default()
            }],
            edges: vec![EvidenceEdge {
                from: "actor".into(),
                to: "event:later".into(),
                relation: "write".into(),
                confidence: Confidence::High,
                reason: "test evidence".into(),
            }],
            ..Default::default()
        };
        db.save(&c).unwrap();
        let a = db.for_event("later").unwrap();
        let b = db.load("v3").unwrap();
        assert_eq!(a.events[0].id, "earlier");
        assert_eq!(a.events[1].raw.status, Some(0xc0000022));
        assert_eq!(a.events[1].dimensions.actor, Confidence::High);
        assert_eq!(a.edges[0].reason, "test evidence");
        assert_eq!(a.operations[0].raw_events, vec!["later"]);
        assert_eq!(
            serde_json::to_string(&a.events).unwrap(),
            serde_json::to_string(&b.events).unwrap()
        );
    }
    #[test]
    fn round_trip() {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let capture = Capture {
            id: "id".into(),
            name: "test".into(),
            installer: "test.exe".into(),
            started_at: "start".into(),
            finished_at: "end".into(),
            exit_code: Some(0),
            watch_roots: vec!["C:\\x".into()],
            registry_key: None,
            processes: vec![ProcessRecord {
                pid: 1,
                parent_pid: None,
                image: "test.exe".into(),
                first_seen: "start".into(),
                confidence: Confidence::Certain,
                reason: "launched".into(),
                ..Default::default()
            }],
            files: vec![FileChange {
                path: "C:\\x\\created.txt".into(),
                operation: "created".into(),
                before_hash: None,
                after_hash: Some("abc".into()),
                after_size: Some(3),
                notification_seen: true,
                confidence: Confidence::Unknown,
                reason: "no PID".into(),
                ..Default::default()
            }],
            registry: vec![RegistryChange {
                key: "HKCU\\Software\\X".into(),
                name: "Installed".into(),
                operation: "created".into(),
                before_value: None,
                after_value: Some("01".into()),
                confidence: Confidence::Unknown,
                reason: "snapshot".into(),
                ..Default::default()
            }],
            warnings: vec![],
            schema_version: 2,
            ..Default::default()
        };
        db.save(&capture).unwrap();
        let restored = db.load("test").unwrap();
        assert_eq!(restored.id, "id");
        assert_eq!(restored.processes[0].confidence, Confidence::Certain);
        assert_eq!(restored.files[0].after_hash.as_deref(), Some("abc"));
        assert_eq!(restored.registry[0].name, "Installed");
        assert_eq!(db.list().unwrap().len(), 1);
    }
}
