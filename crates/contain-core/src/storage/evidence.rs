use crate::model::*;
use anyhow::Result;
use rusqlite::{Connection, OptionalExtension, Transaction, params};

pub fn save(tx: &Transaction<'_>, capture: &Capture) -> Result<()> {
    tx.prepare_cached("INSERT INTO capture_metadata VALUES (?1,?2,?3) ON CONFLICT(session_id) DO UPDATE SET manifest_version=excluded.manifest_version,backend_json=excluded.backend_json")?.execute(params![
            capture.id,
            capture.schema_version,
            serde_json::to_string(&capture.backend)?
        ],
    )?;
    for process in &capture.processes {
        tx.prepare_cached("INSERT OR REPLACE INTO process_instances VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)")?.execute(params![
                capture.id,
                process.pid,
                process.creation_time.unwrap_or(0),
                process.parent_pid,
                process.parent_creation_time,
                process.image,
                process.first_seen,
                process.last_seen,
                process.ended_at,
                process.confidence.as_str(),
                process.reason,
                serde_json::to_string(&process.evidence)?
            ],
        )?;
    }
    save_events(tx, capture)?;
    for file in &capture.files {
        tx.prepare_cached("INSERT OR REPLACE INTO change_evidence VALUES (?1,'file',?2,?3)")?
            .execute(params![
                capture.id,
                file.path,
                serde_json::to_string(&file.evidence)?
            ])?;
    }
    for registry in &capture.registry {
        tx.prepare_cached("INSERT OR REPLACE INTO change_evidence VALUES (?1,'registry',?2,?3)")?
            .execute(params![
                capture.id,
                format!("{}\\{}", registry.key, registry.name),
                serde_json::to_string(&registry.evidence)?
            ])?;
    }
    for change in &capture.inventory {
        tx.prepare_cached("INSERT INTO inventory_changes(session_id,kind,name,operation,before_state_json,after_state_json,confidence,reason,evidence_json) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9)")?.execute(params![capture.id,change.kind,change.name,change.operation,change.before.as_ref().map(serde_json::to_string).transpose()?,change.after.as_ref().map(serde_json::to_string).transpose()?,change.confidence.as_str(),change.reason,serde_json::to_string(&change.evidence)?])?;
    }
    Ok(())
}

pub fn load(connection: &Connection, capture: &mut Capture, full: bool) -> Result<()> {
    let metadata = connection
        .query_row(
            "SELECT manifest_version,backend_json FROM capture_metadata WHERE session_id=?1",
            [&capture.id],
            |row| Ok((row.get::<_, u32>(0)?, row.get::<_, String>(1)?)),
        )
        .optional()?;
    let Some((version, backend)) = metadata else {
        capture.schema_version = 1;
        capture.warnings.push("Legacy v0.1 capture: precise process lifetimes and source-event timestamps were not recorded. Original state diff is preserved.".into());
        for process in &mut capture.processes {
            if process.confidence == Confidence::High {
                process.confidence = Confidence::Unknown;
                process.reason =
                    "Legacy ancestry record lacks process creation-time evidence.".into();
            }
        }
        return Ok(());
    };
    capture.schema_version = version;
    capture.backend = serde_json::from_str(&backend)?;
    capture.processes.clear();
    let mut statement = connection.prepare("SELECT pid,creation_time,parent_pid,parent_creation_time,image,first_seen,last_seen,ended_at,confidence,reason,evidence_json FROM process_instances WHERE session_id=?1 ORDER BY creation_time,pid")?;
    let mut rows = statement.query([&capture.id])?;
    while let Some(row) = rows.next()? {
        let birth = row.get::<_, u64>(1)?;
        capture.processes.push(ProcessRecord {
            pid: row.get(0)?,
            creation_time: (birth != 0).then_some(birth),
            parent_pid: row.get(2)?,
            parent_creation_time: row.get(3)?,
            image: row.get(4)?,
            first_seen: row.get(5)?,
            last_seen: row.get(6)?,
            ended_at: row.get(7)?,
            confidence: super::parse_confidence(&row.get::<_, String>(8)?),
            reason: row.get(9)?,
            evidence: serde_json::from_str(&row.get::<_, String>(10)?)?,
        });
    }
    if full {
        let mut statement = connection.prepare("SELECT id,timestamp,timestamp_ticks,event_type,operation,resource,confidence,reason,source,pid,process_creation_time,process_image,parent_pid,ancestor_pid,attributed_session,rule,success,state_validated FROM observations WHERE session_id=?1 ORDER BY timestamp_ticks,id")?;
        let mut rows = statement.query([&capture.id])?;
        while let Some(row) = rows.next()? {
            capture.events.push(SystemEvent {
                id: row.get(0)?,
                timestamp: row.get(1)?,
                timestamp_ticks: row.get(2)?,
                event_type: row.get(3)?,
                operation: row.get(4)?,
                resource: row.get(5)?,
                confidence: super::parse_confidence(&row.get::<_, String>(6)?),
                reason: row.get(7)?,
                evidence: AttributionEvidence {
                    source: serde_json::from_str(&row.get::<_, String>(8)?)?,
                    pid: row.get(9)?,
                    process_creation_time: row.get(10)?,
                    process_image: row.get(11)?,
                    parent_pid: row.get(12)?,
                    ancestor_pid: row.get(13)?,
                    session_id: row.get(14)?,
                    rule: serde_json::from_str(&row.get::<_, String>(15)?)?,
                },
                success: row.get(16)?,
                state_validated: row.get(17)?,
                ..Default::default()
            });
        }
    }
    let mut statement = connection
        .prepare("SELECT kind,resource,evidence_json FROM change_evidence WHERE session_id=?1")?;
    let mut rows = statement.query([&capture.id])?;
    while let Some(row) = rows.next()? {
        let kind: String = row.get(0)?;
        let resource: String = row.get(1)?;
        let evidence: Vec<AttributionEvidence> = serde_json::from_str(&row.get::<_, String>(2)?)?;
        if kind == "file" {
            if let Some(file) = capture.files.iter_mut().find(|file| file.path == resource) {
                file.evidence = evidence;
            }
        } else if let Some(registry) = capture
            .registry
            .iter_mut()
            .find(|r| format!("{}\\{}", r.key, r.name) == resource)
        {
            registry.evidence = evidence;
        }
    }
    let mut statement = connection.prepare("SELECT kind,name,operation,before_state_json,after_state_json,confidence,reason,evidence_json FROM inventory_changes WHERE session_id=?1 ORDER BY kind,name")?;
    let mut rows = statement.query([&capture.id])?;
    while let Some(row) = rows.next()? {
        capture.inventory.push(InventoryChange {
            kind: row.get(0)?,
            name: row.get(1)?,
            operation: row.get(2)?,
            before: row
                .get::<_, Option<String>>(3)?
                .map(|v| serde_json::from_str(&v))
                .transpose()?,
            after: row
                .get::<_, Option<String>>(4)?
                .map(|v| serde_json::from_str(&v))
                .transpose()?,
            confidence: super::parse_confidence(&row.get::<_, String>(5)?),
            reason: row.get(6)?,
            evidence: serde_json::from_str(&row.get::<_, String>(7)?)?,
        });
    }
    Ok(())
}

pub(super) fn save_events(tx: &Transaction<'_>, capture: &Capture) -> Result<()> {
    for event in &capture.events {
        let evidence = &event.evidence;
        tx.prepare_cached("INSERT INTO observations VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16,?17,?18,?19)")?.execute(params![event.id,capture.id,event.timestamp,event.timestamp_ticks,event.event_type,event.operation,event.resource,event.confidence.as_str(),event.reason,serde_json::to_string(&evidence.source)?,evidence.pid,evidence.process_creation_time,evidence.process_image,evidence.parent_pid,evidence.ancestor_pid,evidence.session_id,serde_json::to_string(&evidence.rule)?,event.success,event.state_validated])?;
    }
    Ok(())
}
