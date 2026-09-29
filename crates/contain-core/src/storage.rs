use crate::model::{Capture, Confidence, FileChange, ProcessRecord, RegistryChange};
use anyhow::{Context, Result};
use rusqlite::{Connection, OptionalExtension, params};
use std::fs;
use std::path::Path;

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
        connection.execute_batch("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS applications (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, installer TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS installation_sessions (
                id TEXT PRIMARY KEY, application_id TEXT NOT NULL REFERENCES applications(id),
                started_at TEXT NOT NULL, finished_at TEXT NOT NULL, exit_code INTEGER,
                watch_roots_json TEXT NOT NULL, registry_key TEXT, warnings_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS processes (
                session_id TEXT NOT NULL REFERENCES installation_sessions(id), pid INTEGER NOT NULL,
                parent_pid INTEGER, image TEXT NOT NULL, first_seen TEXT NOT NULL,
                confidence TEXT NOT NULL, reason TEXT NOT NULL,
                PRIMARY KEY(session_id, pid)
            );
            CREATE TABLE IF NOT EXISTS system_events (
                id INTEGER PRIMARY KEY, session_id TEXT NOT NULL REFERENCES installation_sessions(id),
                event_type TEXT NOT NULL, resource TEXT NOT NULL, operation TEXT NOT NULL,
                observed_at TEXT NOT NULL, process_id INTEGER, confidence TEXT NOT NULL,
                reason TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS file_changes (
                event_id INTEGER PRIMARY KEY REFERENCES system_events(id), before_hash TEXT,
                after_hash TEXT, after_size INTEGER, notification_seen INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS registry_changes (
                event_id INTEGER PRIMARY KEY REFERENCES system_events(id), registry_key TEXT NOT NULL,
                value_name TEXT NOT NULL, before_value TEXT, after_value TEXT
            );")?;
        Ok(Self { connection })
    }

    pub fn save(&mut self, capture: &Capture) -> Result<()> {
        let tx = self.connection.transaction()?;
        tx.execute(
            "INSERT INTO applications(id,name,installer) VALUES (?1,?2,?3)",
            params![capture.id, capture.name, capture.installer],
        )?;
        tx.execute("INSERT INTO installation_sessions(id,application_id,started_at,finished_at,exit_code,watch_roots_json,registry_key,warnings_json) VALUES (?1,?1,?2,?3,?4,?5,?6,?7)",
            params![capture.id, capture.started_at, capture.finished_at, capture.exit_code,
                serde_json::to_string(&capture.watch_roots)?, capture.registry_key, serde_json::to_string(&capture.warnings)?])?;
        for process in &capture.processes {
            tx.execute(
                "INSERT INTO processes VALUES (?1,?2,?3,?4,?5,?6,?7)",
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
        tx.commit()?;
        Ok(())
    }

    pub fn list(&self) -> Result<Vec<(String, String, String)>> {
        let mut statement = self.connection.prepare("SELECT a.id,a.name,s.started_at FROM applications a JOIN installation_sessions s ON s.application_id=a.id ORDER BY s.started_at DESC")?;
        let rows = statement.query_map([], |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)))?;
        rows.collect::<rusqlite::Result<Vec<_>>>()
            .map_err(Into::into)
    }

    pub fn load(&self, identity: &str) -> Result<Capture> {
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
            })
        })? {
            registry.push(row?);
        }
        Ok(Capture {
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
        })
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
            }],
            registry: vec![RegistryChange {
                key: "HKCU\\Software\\X".into(),
                name: "Installed".into(),
                operation: "created".into(),
                before_value: None,
                after_value: Some("01".into()),
                confidence: Confidence::Unknown,
                reason: "snapshot".into(),
            }],
            warnings: vec![],
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
