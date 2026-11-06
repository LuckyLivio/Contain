//! Session-scoped literal matching shared by paged queries and future exports.
use super::*;
use crate::model::SystemEvent;
use sha2::{Digest, Sha256};

/// Same bounded transport as the desktop reader; oversized exports fail whole.
pub const MAX_SEARCH_EXPORT_BYTES: usize = 64 * 1024 * 1024;

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
    pub name: String,
    pub matched_total: u64,
    pub search_fingerprint: String,
    pub events: Vec<SystemEvent>,
}

impl Storage {
    /// Count, fingerprint and retain a page from the same complete ordered scan.
    pub fn events_search(
        &self,
        session: &str,
        search: &str,
        offset: u64,
        limit: u32,
    ) -> Result<EventSearchPage> {
        anyhow::ensure!((1..=4096).contains(&limit), "page limit must be 1..4096");
        self.search_view(session, search, Some((offset, limit)), None)
    }

    /// One bounded complete match set, not a collection of independently read
    /// pages. The caller must also bound the final serialized envelope.
    pub fn events_search_all(
        &self,
        session: &str,
        search: &str,
        maximum_bytes: usize,
    ) -> Result<EventSearchPage> {
        anyhow::ensure!(
            maximum_bytes <= MAX_SEARCH_EXPORT_BYTES,
            "export byte limit exceeds 64 MiB"
        );
        self.search_view(session, search, None, Some(maximum_bytes))
    }

    fn search_view(
        &self,
        session: &str,
        search: &str,
        window: Option<(u64, u32)>,
        maximum_bytes: Option<usize>,
    ) -> Result<EventSearchPage> {
        let _snapshot = if self.connection.is_autocommit() {
            Some(self.connection.unchecked_transaction()?)
        } else {
            None
        };
        // Context and events are read within this same snapshot. The exact UUID
        // is required here; another session with the same name never substitutes.
        let name: String = self.connection.query_row(
            "SELECT a.name FROM applications a JOIN installation_sessions s ON s.application_id=a.id WHERE s.id=?1",
            [session], |row| row.get(0),
        ).optional()?.with_context(|| format!("session not found: {session}"))?;
        let context = serde_json::to_vec(&(session, &name, search))?;
        // Reserve the envelope keys, final hash and largest integer spelling.
        // CLI still validates its exact serialized byte count before stdout.
        let mut retained_bytes = context.len().saturating_add(1024);
        if let Some(maximum) = maximum_bytes {
            anyhow::ensure!(
                retained_bytes <= maximum,
                "search result exceeds export byte limit; narrow the search"
            );
        }
        let mut fingerprint = Sha256::new();
        fingerprint.update(b"contain-search-view-v1\0");
        fingerprint.update((context.len() as u64).to_le_bytes());
        fingerprint.update(context);
        let matcher = EventSearch::new(search);
        let mut page = EventSearchPage {
            name,
            matched_total: 0,
            search_fingerprint: String::new(),
            events: Vec::with_capacity(window.map_or(0, |(_, limit)| limit as usize)),
        };
        self.visit_search_events(session, |event| {
            if !matcher.matches(&event) {
                return Ok(());
            }
            // Include full event content and ordered boundaries, not merely IDs
            // or cardinality: an equal-count replacement must invalidate export.
            let bytes = serde_json::to_vec(&event)?;
            fingerprint.update((bytes.len() as u64).to_le_bytes());
            fingerprint.update(&bytes);
            if let Some(maximum) = maximum_bytes {
                retained_bytes = retained_bytes.saturating_add(bytes.len()).saturating_add(1);
                anyhow::ensure!(
                    retained_bytes <= maximum,
                    "search result exceeds export byte limit; narrow the search"
                );
            }
            if window.is_none_or(|(offset, limit)| {
                page.matched_total >= offset && page.events.len() < limit as usize
            }) {
                page.events.push(event);
            }
            page.matched_total += 1;
            Ok(())
        })?;
        page.search_fingerprint = format!("{:x}", fingerprint.finalize());
        Ok(page)
    }

    /// The only timeline reader used by page search and complete export. Modern
    /// documents are streamed; old databases retain the existing compatibility
    /// mapping and unfinished events retain their exposed Unknown attribution.
    fn visit_search_events(
        &self,
        session: &str,
        mut visit: impl FnMut(SystemEvent) -> Result<()>,
    ) -> Result<()> {
        let state = self.stream_state(session)?;
        let unfinished = state.as_deref().is_some_and(|s| s != "finished");
        let missing: bool = self.connection.query_row("SELECT EXISTS(SELECT 1 FROM observations o WHERE session_id=?1 AND NOT EXISTS(SELECT 1 FROM event_documents d WHERE d.id=o.id))", [session], |row| row.get(0))?;
        if missing && state.is_none() {
            for event in self.load_by_id(session)?.events {
                visit(event)?;
            }
        } else {
            let sql = if unfinished {
                // This table name is chosen by storage, never by user input;
                // sequence is unique within each raw session.
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
                    stream::suppress_unfinished(&mut event);
                }
                visit(event)?;
            }
        }
        Ok(())
    }
}
