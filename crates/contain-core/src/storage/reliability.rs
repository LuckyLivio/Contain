use crate::model::*;
use anyhow::Result;
use rusqlite::{Connection, OptionalExtension, Transaction, params};
pub fn save(tx: &Transaction<'_>, c: &Capture) -> Result<()> {
    tx.prepare_cached("INSERT INTO reliability_metadata VALUES (?1,?2,?3) ON CONFLICT(session_id) DO UPDATE SET stats_json=excluded.stats_json,quality_json=excluded.quality_json")?.execute(params![
            c.id,
            serde_json::to_string(&c.stats)?,
            serde_json::to_string(&c.quality)?
        ],
    )?;
    save_details(tx, c)?;
    Ok(())
}
pub fn load(db: &Connection, c: &mut Capture, full: bool) -> Result<()> {
    let row = db
        .query_row(
            "SELECT stats_json,quality_json FROM reliability_metadata WHERE session_id=?1",
            [&c.id],
            |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)),
        )
        .optional()?;
    if let Some((stats, quality)) = row {
        c.stats = serde_json::from_str(&stats)?;
        c.quality = serde_json::from_str(&quality)?;
    } else {
        c.quality.reasons.push(
            "Legacy capture: raw correlation metadata and capture quality were not recorded."
                .into(),
        );
    }
    if !full {
        return Ok(());
    }
    let mut stmt=db.prepare("SELECT d.event_id,d.sequence,d.raw_json,d.dimensions_json FROM observation_details d JOIN observations o ON o.id=d.event_id WHERE o.session_id=?1")?;
    let indices: std::collections::HashMap<_, _> = c
        .events
        .iter()
        .enumerate()
        .map(|(i, e)| (e.id.clone(), i))
        .collect();
    let mut rows = stmt.query([&c.id])?;
    while let Some(r) = rows.next()? {
        if let Some(i) = indices.get(&r.get::<_, String>(0)?) {
            let e = &mut c.events[*i];
            e.sequence = r.get(1)?;
            e.raw = serde_json::from_str(&r.get::<_, String>(2)?)?;
            e.dimensions = serde_json::from_str(&r.get::<_, String>(3)?)?;
        }
    }
    let mut stmt = db
        .prepare("SELECT detail_json FROM normalized_operations WHERE session_id=?1 ORDER BY id")?;
    for r in stmt.query_map([&c.id], |r| r.get::<_, String>(0))? {
        c.operations.push(serde_json::from_str(&r?)?);
    }
    let mut stmt=db.prepare("SELECT from_node,to_node,relation,confidence,reason FROM evidence_edges WHERE session_id=?1 ORDER BY from_node,to_node,relation")?;
    for r in stmt.query_map([&c.id], |r| {
        Ok(EvidenceEdge {
            from: r.get(0)?,
            to: r.get(1)?,
            relation: r.get(2)?,
            confidence: super::parse_confidence(&r.get::<_, String>(3)?),
            reason: r.get(4)?,
        })
    })? {
        c.edges.push(r?);
    }
    c.events.sort_by(|a, b| {
        (a.timestamp_ticks, a.sequence, &a.id).cmp(&(b.timestamp_ticks, b.sequence, &b.id))
    });
    Ok(())
}

pub(super) fn save_details(tx: &Connection, c: &Capture) -> Result<()> {
    for e in &c.events {
        tx.prepare_cached("INSERT INTO observation_details VALUES (?1,?2,?3,?4)")?
            .execute(params![
                e.id,
                e.sequence,
                serde_json::to_string(&e.raw)?,
                serde_json::to_string(&e.dimensions)?
            ])?;
    }
    for o in &c.operations {
        tx.prepare_cached("INSERT INTO normalized_operations VALUES (?1,?2,?3,?4,?5)")?
            .execute(params![
                o.id,
                c.id,
                o.operation,
                o.resource,
                serde_json::to_string(o)?
            ])?;
    }
    for e in &c.edges {
        tx.prepare_cached("INSERT OR IGNORE INTO evidence_edges VALUES (?1,?2,?3,?4,?5,?6)")?
            .execute(params![
                c.id,
                e.from,
                e.to,
                e.relation,
                e.confidence.as_str(),
                e.reason
            ])?;
    }
    Ok(())
}
