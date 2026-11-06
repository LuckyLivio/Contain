//! Session-scoped literal matching shared by paged queries and future exports.
use super::*;
use crate::model::SystemEvent;

/// ASCII letters compare without case; all other characters (including spaces,
/// `%` and `_`) compare literally. No tokenization, normalization or glob syntax.
pub struct EventSearch {
    needle: String,
}

impl EventSearch {
    pub fn new(text: &str) -> Self {
        Self {
            needle: text.to_ascii_lowercase(),
        }
    }

    pub fn matches(&self, event: &SystemEvent) -> bool {
        [
            event.operation.as_str(),
            event.resource.as_str(),
            event.confidence.as_str(),
            event.id.as_str(),
            event.event_type.as_str(),
        ]
        .iter()
        .any(|value| value.to_ascii_lowercase().contains(&self.needle))
    }
}

pub struct EventSearchPage {
    pub matched_total: u64,
    pub events: Vec<SystemEvent>,
}

impl Storage {
    /// Match the complete session before pagination, using the same predicate
    /// for modern documents and the authoritative legacy compatibility mapping.
    /// Modern records stream one document at a time; only this page is retained.
    /// Count and page share one read transaction, including writable CLI opens.
    pub fn events_search(
        &self,
        session: &str,
        search: &str,
        offset: u64,
        limit: u32,
    ) -> Result<EventSearchPage> {
        anyhow::ensure!((1..=4096).contains(&limit), "page limit must be 1..4096");
        let _snapshot = if self.connection.is_autocommit() {
            Some(self.connection.unchecked_transaction()?)
        } else {
            None
        };
        let state = self.stream_state(session)?;
        let unfinished = state.as_deref().is_some_and(|s| s != "finished");
        let missing: bool = self.connection.query_row("SELECT EXISTS(SELECT 1 FROM observations o WHERE session_id=?1 AND NOT EXISTS(SELECT 1 FROM event_documents d WHERE d.id=o.id))", [session], |row| row.get(0))?;
        let matcher = EventSearch::new(search);
        let mut page = EventSearchPage {
            matched_total: 0,
            events: Vec::with_capacity(limit as usize),
        };
        let mut visit = |event: SystemEvent| {
            if matcher.matches(&event) {
                if page.matched_total >= offset && page.events.len() < limit as usize {
                    page.events.push(event);
                }
                page.matched_total += 1;
            }
        };
        if missing && state.is_none() {
            for event in self.load_by_id(session)?.events {
                visit(event);
            }
        } else {
            let sql = if unfinished {
                // The table name is selected by production storage code, never
                // by user input. Sequence is unique within each raw session.
                format!(
                    "SELECT document FROM {} WHERE session_id=?1 ORDER BY ticks,sequence",
                    self.raw_table(session)?
                )
            } else {
                "SELECT document FROM event_documents WHERE session_id=?1 ORDER BY ticks,sequence,id".into()
            };
            let mut query = self.connection.prepare(&sql)?;
            let mut rows = query.query([session])?;
            while let Some(row) = rows.next()? {
                let mut event: SystemEvent = serde_json::from_str(&row.get::<_, String>(0)?)?;
                if unfinished {
                    // Search exactly the confidence exposed by history/explain,
                    // not a stale raw or derived attribution claim.
                    stream::suppress_unfinished(&mut event);
                }
                visit(event);
            }
        }
        Ok(page)
    }
}
