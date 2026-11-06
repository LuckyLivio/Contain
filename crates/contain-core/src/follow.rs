//! Bounded provisional admission for follow-installer capture. No path or PID
//! alone is an actor proof; all retained candidates are re-resolved after STOP.
use crate::{lifetime::LifetimeCache, model::*, profile};
use std::collections::{BTreeMap, BTreeSet, VecDeque};

pub(crate) const CANDIDATE_RECORDS: usize = 2048;
pub(crate) const CANDIDATE_BYTES: usize = 8 * 1024 * 1024;
const CANDIDATE_RECORD_BYTES: usize = 256 * 1024;
const RESOURCE_LIMIT: usize = 4096;
const REQUEST_LIMIT: usize = 16384;

pub(crate) struct FollowBuffer {
    root: Option<(u32, u64)>,
    session: String,
    pending: VecDeque<(SystemEvent, usize)>,
    bytes: usize,
    resources: BTreeSet<String>,
    requests: BTreeSet<String>,
    pending_ids: BTreeSet<String>,
    // Retry hints only: each entry is backed by at least one bounded pending
    // completion. They never supply writer identity or completion success.
    pending_completion_requests: BTreeMap<String, usize>,
    selection_generation: u64,
    last_retry_generation: u64,
    cache_dirty: bool,
    diagnostics: Option<FollowDiagnostics>,
    #[cfg(test)]
    retry_visits: u64,
    pub stats: FollowInstallerStats,
}
impl Default for FollowBuffer {
    fn default() -> Self {
        Self {
            root: None,
            session: String::new(),
            pending: VecDeque::new(),
            bytes: 0,
            resources: BTreeSet::new(),
            requests: BTreeSet::new(),
            pending_ids: BTreeSet::new(),
            pending_completion_requests: BTreeMap::new(),
            selection_generation: 0,
            last_retry_generation: 0,
            cache_dirty: false,
            diagnostics: None,
            #[cfg(test)]
            retry_visits: 0,
            stats: FollowInstallerStats {
                candidate_record_limit: CANDIDATE_RECORDS as u64,
                candidate_byte_limit: CANDIDATE_BYTES as u64,
                resource_limit: RESOURCE_LIMIT as u64,
                direct_children_only: true,
                baseline_available: false,
                ..Default::default()
            },
        }
    }
}
enum Selection {
    Admit,
    Discard,
    Pending,
}

#[derive(Clone, Copy)]
enum Change {
    Bind,
    Cache,
    Resource,
    Request,
    Completion,
    RelatedRemoved,
    Flush,
}
#[derive(Clone, Copy)]
enum DropReason {
    Serialization,
    RecordBytes,
    PendingRecords,
    PendingBytes,
    Resources,
    Requests,
}
#[derive(Default)]
struct FollowDiagnostics {
    changes: [u64; 7],
    drops: [u64; 6],
    pending_before_bind: u64,
    pending_no_identity: u64,
    pending_unverified_actor: u64,
    last_pending_reason: usize,
    pending_drops: [u64; 3],
}
impl FollowDiagnostics {
    fn finish(self) {
        let [bind, cache, resource, request, completion, removed, flush] = self.changes;
        let [before_bind, no_identity, unverified_actor] = self.pending_drops;
        let [
            serialization,
            record_bytes,
            pending_records,
            pending_bytes,
            resources,
            requests,
        ] = self.drops;
        profile::counters([
            ("follow_dirty_bind", bind),
            ("follow_dirty_cache", cache),
            ("follow_dirty_resource", resource),
            ("follow_dirty_request", request),
            ("follow_dirty_completion", completion),
            ("follow_dirty_related_removed", removed),
            ("follow_dirty_flush", flush),
            ("follow_drop_serialization", serialization),
            ("follow_drop_record_bytes", record_bytes),
            ("follow_drop_pending_records", pending_records),
            ("follow_drop_pending_bytes", pending_bytes),
            ("follow_drop_resources", resources),
            ("follow_drop_requests", requests),
            ("follow_drop_pending_before_bind", before_bind),
            ("follow_drop_pending_no_identity", no_identity),
            ("follow_drop_pending_unverified_actor", unverified_actor),
            // Attempts, including retries, rather than distinct lost records.
            (
                "follow_pending_before_bind_attempts",
                self.pending_before_bind,
            ),
            (
                "follow_pending_no_identity_attempts",
                self.pending_no_identity,
            ),
            (
                "follow_pending_unverified_actor_attempts",
                self.pending_unverified_actor,
            ),
        ]);
    }
}

impl FollowBuffer {
    pub fn bind(&mut self, root: (u32, u64), session: &str) {
        if self.root.is_none() && root.1 != 0 {
            self.root = Some(root);
            self.session = session.into();
            self.cache_dirty = true;
            self.changed(Change::Bind);
            profile::counters([("follow_dirty_bind", 1)]);
        }
    }
    fn changed(&mut self, reason: Change) {
        self.selection_generation = self.selection_generation.wrapping_add(1);
        if let Some(diagnostics) = &mut self.diagnostics {
            diagnostics.changes[reason as usize] += 1;
        }
    }
    fn dropped(&mut self, reason: DropReason) {
        self.stats.candidate_dropped += 1;
        if let Some(diagnostics) = &mut self.diagnostics {
            diagnostics.drops[reason as usize] += 1;
        }
    }
    fn dropped_pending(&mut self, reason: DropReason) {
        self.dropped(reason);
        if let Some(diagnostics) = &mut self.diagnostics {
            diagnostics.pending_drops[diagnostics.last_pending_reason] += 1;
        }
    }
    fn request_candidate_removed(&mut self, id: &str) {
        if self.pending_completion_requests.contains_key(id) {
            self.changed(Change::RelatedRemoved);
        }
    }
    fn track_pending(&mut self, event: SystemEvent, size: usize) {
        if event.event_type == "completion"
            && let Some(id) = &event.raw.related_event
        {
            *self
                .pending_completion_requests
                .entry(id.clone())
                .or_default() += 1;
        }
        self.bytes += size;
        self.pending_ids.insert(event.id.clone());
        self.pending.push_back((event, size));
    }
    fn untrack_pending(&mut self, event: &SystemEvent, size: usize) {
        self.bytes -= size;
        self.pending_ids.remove(&event.id);
        if event.event_type == "completion"
            && let Some(id) = &event.raw.related_event
        {
            let count = self.pending_completion_requests.get_mut(id).unwrap();
            *count -= 1;
            if *count == 0 {
                self.pending_completion_requests.remove(id);
            }
        }
    }
    #[cfg(test)]
    pub fn push(&mut self, event: SystemEvent) {
        self.stats.candidates += 1;
        self.buffer(event);
    }
    fn serialized_size(event: &SystemEvent) -> Option<usize> {
        struct Size(usize);
        impl std::io::Write for Size {
            fn write(&mut self, b: &[u8]) -> std::io::Result<usize> {
                self.0 = self.0.saturating_add(b.len());
                Ok(b.len())
            }
            fn flush(&mut self) -> std::io::Result<()> {
                Ok(())
            }
        }
        let mut size = Size(0);
        serde_json::to_writer(&mut size, event).ok()?;
        Some(size.0)
    }
    fn buffer(&mut self, event: SystemEvent) {
        let Some(size) = Self::serialized_size(&event) else {
            self.dropped_pending(DropReason::Serialization);
            self.request_candidate_removed(&event.id);
            return;
        };
        let reason = if size > CANDIDATE_RECORD_BYTES {
            Some(DropReason::RecordBytes)
        } else if self.pending.len() >= CANDIDATE_RECORDS {
            Some(DropReason::PendingRecords)
        } else if self.bytes.saturating_add(size) > CANDIDATE_BYTES {
            Some(DropReason::PendingBytes)
        } else {
            None
        };
        if let Some(reason) = reason {
            self.dropped_pending(reason);
            self.request_candidate_removed(&event.id);
            return;
        }
        self.track_pending(event, size);
        self.stats.candidate_high_water_records = self
            .stats
            .candidate_high_water_records
            .max(self.pending.len() as u64);
        self.stats.candidate_high_water_bytes =
            self.stats.candidate_high_water_bytes.max(self.bytes as u64);
    }
    /// Classify a bounded receiver page before spending provisional queue space.
    /// Keep every unresolved record eligible for later lifecycle evidence. Already
    /// selected records remain original raw evidence and are re-resolved after STOP.
    pub fn receive(
        &mut self,
        incoming: Vec<SystemEvent>,
        cache: &mut LifetimeCache,
        cache_updated: bool,
    ) -> Vec<SystemEvent> {
        self.diagnostics = profile::enabled().then(FollowDiagnostics::default);
        profile::counters([
            ("follow_receive_pages", 1),
            ("follow_incoming_records", incoming.len() as u64),
        ]);
        self.cache_dirty |= cache_updated;
        if self.cache_dirty {
            let _span = profile::queue_span("receiver_follow_cache_attach");
            if let Some(root) = self.root {
                cache.attach(root, &self.session);
            }
            self.cache_dirty = false;
            self.changed(Change::Cache);
        }
        // A completion can precede its request in this page. Register all IDs
        // before classification, just as when the whole page was buffered first.
        {
            let _span = profile::queue_span("receiver_follow_register").units(incoming.len());
            for event in &incoming {
                self.pending_ids.insert(event.id.clone());
            }
        }
        let mut admitted = self.retry_changed(cache);
        {
            let _span = profile::queue_span("receiver_follow_classify").units(incoming.len());
            for event in incoming {
                self.stats.candidates += 1;
                self.pending_ids.remove(&event.id);
                match self.select(&event, cache, false, false) {
                    Selection::Admit => {
                        self.request_candidate_removed(&event.id);
                        admitted.push(event);
                    }
                    Selection::Discard => self.request_candidate_removed(&event.id),
                    Selection::Pending => self.buffer(event),
                }
            }
        }
        // A current request can release a historical completion, and a newly
        // selected resource can release an earlier competing writer. Each
        // receive performs at most two bounded scans. Changes during the last
        // scan remain dirty for the next page or the forced STOP passes.
        admitted.extend(self.retry_changed(cache));
        if let Some(diagnostics) = self.diagnostics.take() {
            diagnostics.finish();
        }
        admitted
    }
    fn retry_changed(&mut self, cache: &LifetimeCache) -> Vec<SystemEvent> {
        let mut admitted = Vec::new();
        if self.last_retry_generation != self.selection_generation {
            // Snapshot before scanning: facts admitted during this scan must
            // still release earlier candidates on the next bounded pass.
            self.last_retry_generation = self.selection_generation;
            admitted.extend(self.scan_pending(cache, false));
        }
        admitted
    }
    pub fn flush(&mut self, cache: &mut LifetimeCache, final_pass: bool) -> Vec<SystemEvent> {
        self.diagnostics = profile::enabled().then(FollowDiagnostics::default);
        // STOP always rechecks the complete retained context, even if no live
        // notification marked it dirty. This is not an attribution cache.
        {
            let _span = profile::queue_span("receiver_follow_cache_attach_stop");
            if let Some(root) = self.root {
                cache.attach(root, &self.session);
            }
        }
        self.cache_dirty = false;
        self.changed(Change::Flush);
        self.last_retry_generation = self.selection_generation;
        let admitted = self.scan_pending(cache, final_pass);
        if let Some(diagnostics) = self.diagnostics.take() {
            diagnostics.finish();
        }
        admitted
    }
    fn scan_pending(&mut self, cache: &LifetimeCache, final_pass: bool) -> Vec<SystemEvent> {
        let mut admitted = Vec::new();
        let count = self.pending.len();
        let stage = if final_pass {
            "receiver_follow_pending_final"
        } else {
            "receiver_follow_pending_scan"
        };
        let _span = profile::queue_span(stage).units(count);
        profile::counters([
            ("follow_scan_calls", 1),
            ("follow_scan_visits", count as u64),
        ]);
        for _ in 0..count {
            #[cfg(test)]
            {
                self.retry_visits += 1;
            }
            let (event, bytes) = self.pending.pop_front().unwrap();
            self.untrack_pending(&event, bytes);
            match self.select(&event, cache, final_pass, true) {
                Selection::Admit => {
                    self.request_candidate_removed(&event.id);
                    admitted.push(event);
                }
                Selection::Discard => self.request_candidate_removed(&event.id),
                Selection::Pending => self.track_pending(event, bytes),
            }
        }
        admitted
    }
    fn select(
        &mut self,
        event: &SystemEvent,
        cache: &LifetimeCache,
        final_pass: bool,
        size_checked: bool,
    ) -> Selection {
        let Some(root) = self.root else {
            if final_pass {
                self.stats.unresolved += 1;
                return Selection::Discard;
            }
            if let Some(diagnostics) = &mut self.diagnostics {
                diagnostics.pending_before_bind += 1;
                diagnostics.last_pending_reason = 0;
            }
            return Selection::Pending;
        };
        // resolve_event reads these identity fields only. Do not clone file
        // names, diagnostics, payloads or reasons on every provisional retry.
        let mut candidate = SystemEvent {
            timestamp_ticks: event.timestamp_ticks,
            evidence: event.evidence.clone(),
            raw: RawEvidence {
                thread_id: event.raw.thread_id,
                initial_thread_identity: event.raw.initial_thread_identity.clone(),
                object_generation: event.raw.object_generation.clone(),
                ..Default::default()
            },
            ..Default::default()
        };
        cache.resolve_event(&mut candidate);
        let identity = candidate
            .evidence
            .pid
            .zip(candidate.evidence.process_creation_time);
        let actor = identity.and_then(|key| cache.processes.get(&key));
        let matches = actor.is_some_and(|p| is_direct_actor(p, root));
        let known_other = identity.is_some_and(|key| key.1 < root.1)
            || actor.is_some_and(|p| {
                p.parent_pid.is_some_and(|pid| pid != root.0) && identity != Some(root)
            });
        let completion = event.event_type == "completion";
        let related = event.raw.related_event.as_ref();
        let completion_match = completion && related.is_some_and(|id| self.requests.remove(id));
        if completion_match {
            self.changed(Change::Completion);
        }
        let unrelated_completion = completion
            && !completion_match
            && !related.is_some_and(|id| self.pending_ids.contains(id));
        let unselected_metadata = matches!(event.operation.as_str(), "open_requested" | "close");
        if completion_match
            || (!completion && (matches || self.resources.contains(&event.resource)))
        {
            // Preserve the per-record guard before cloning a selected resource
            // or request ID. Pending records have already passed this check.
            if !size_checked {
                let dropped = match Self::serialized_size(event) {
                    None => Some(DropReason::Serialization),
                    Some(size) if size > CANDIDATE_RECORD_BYTES => Some(DropReason::RecordBytes),
                    Some(_) => None,
                };
                if let Some(reason) = dropped {
                    self.dropped(reason);
                    return Selection::Discard;
                }
            }
            if matches && !self.resources.contains(&event.resource) {
                if self.resources.len() >= RESOURCE_LIMIT {
                    self.dropped(DropReason::Resources);
                    return Selection::Discard;
                }
                self.resources.insert(event.resource.clone());
                self.changed(Change::Resource);
            }
            if !completion
                && event.raw.irp.is_some()
                && matches!(
                    event.operation.as_str(),
                    "write_requested" | "delete_requested" | "rename_requested"
                )
            {
                if self.requests.len() >= REQUEST_LIMIT {
                    self.dropped(DropReason::Requests);
                    return Selection::Discard;
                }
                if self.requests.insert(event.id.clone()) {
                    self.changed(Change::Request);
                }
            }
            self.stats.admitted += 1;
            Selection::Admit
        } else if final_pass || known_other || unrelated_completion || unselected_metadata {
            if known_other {
                self.stats.excluded_other += 1;
            } else {
                self.stats.unresolved += 1;
            }
            Selection::Discard
        } else {
            if let Some(diagnostics) = &mut self.diagnostics {
                if identity.is_none() {
                    diagnostics.pending_no_identity += 1;
                    diagnostics.last_pending_reason = 1;
                } else {
                    diagnostics.pending_unverified_actor += 1;
                    diagnostics.last_pending_reason = 2;
                }
            }
            Selection::Pending
        }
    }
}

pub(crate) fn is_direct_actor(p: &ProcessRecord, root: (u32, u64)) -> bool {
    (p.pid, p.creation_time.unwrap_or(0)) == root && p.confidence == Confidence::Certain
        || p.parent_pid == Some(root.0)
            && p.parent_creation_time == Some(root.1)
            && p.confidence == Confidence::High
}

#[cfg(test)]
mod tests {
    use super::*;
    fn root() -> ProcessRecord {
        ProcessRecord {
            pid: 1,
            creation_time: Some(100),
            image: "installer.exe".into(),
            confidence: Confidence::Certain,
            evidence: AttributionEvidence {
                source: EvidenceSource::ProcessApi,
                rule: AttributionRule::InstallerPid,
                ..Default::default()
            },
            ..Default::default()
        }
    }
    fn lifecycle(
        operation: &str,
        pid: u32,
        birth: Option<u64>,
        parent: Option<u32>,
        tid: Option<u32>,
        at: u64,
    ) -> SystemEvent {
        SystemEvent {
            event_type: "lifecycle".into(),
            operation: operation.into(),
            resource: format!("process-{pid}.exe"),
            timestamp_ticks: at,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwProcess,
                pid: Some(pid),
                process_creation_time: birth,
                parent_pid: parent,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: tid,
                ..Default::default()
            },
            ..Default::default()
        }
    }
    fn file(id: &str, tid: u32) -> SystemEvent {
        SystemEvent {
            id: id.into(),
            event_type: "file".into(),
            operation: "write_requested".into(),
            resource: format!(r"d:\not-preselected\{id}.txt"),
            timestamp_ticks: 250,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(tid),
                resource_resolved: true,
                ..Default::default()
            },
            ..Default::default()
        }
    }
    fn cache() -> LifetimeCache {
        let mut cache = LifetimeCache::default();
        cache.seed(root());
        cache
    }
    #[test]
    fn diagnostics_separate_pending_capacity_loss_from_normal_filtering() {
        let mut follow = FollowBuffer::default();
        let mut cache = cache();
        cache.ingest(&lifecycle(
            "process_start",
            4,
            Some(120),
            Some(99),
            None,
            120,
        ));
        cache.ingest(&lifecycle("thread_start", 4, None, None, Some(10), 121));
        let report = profile::test_profile(|| {
            profile::observe_queue(profile::QueueSnapshot::default);
            follow.bind((1, 100), "s");
            let incoming = (0..CANDIDATE_RECORDS + 1)
                .map(|index| file(&format!("unresolved-{index}"), 999))
                .collect();
            assert!(follow.receive(incoming, &mut cache, true).is_empty());
            assert!(
                follow
                    .receive(vec![file("other", 10)], &mut cache, false)
                    .is_empty()
            );
            assert!(follow.flush(&mut cache, true).is_empty());
        });
        assert_eq!(follow.stats.candidate_dropped, 1);
        assert_eq!(follow.stats.excluded_other, 1);
        let counters = &report["counters"];
        assert_eq!(counters["follow_drop_pending_records"], 1);
        assert_eq!(counters["follow_drop_pending_bytes"], 0);
        assert_eq!(counters["follow_drop_pending_no_identity"], 1);
        assert_eq!(counters["follow_drop_pending_before_bind"], 0);
        assert_eq!(counters["follow_drop_pending_unverified_actor"], 0);
        assert_eq!(counters["follow_drop_record_bytes"], 0);
        assert_eq!(counters["follow_dirty_bind"], 1);
        assert_eq!(
            counters["follow_pending_no_identity_attempts"],
            (CANDIDATE_RECORDS + 1) as u64
        );
        assert_eq!(counters["follow_scan_visits"], CANDIDATE_RECORDS as u64);
        assert_eq!(
            report["queue_stage_totals"]["receiver_follow_pending_final"]["units"],
            CANDIDATE_RECORDS as u64
        );
        assert!(follow.diagnostics.is_none());
    }
    #[test]
    fn registration_and_late_thread_delivery_do_not_drop_an_early_root_write() {
        let mut follow = FollowBuffer::default();
        let mut cache = cache();
        follow.push(file("root-write", 7));
        assert!(follow.flush(&mut cache, false).is_empty());
        follow.bind((1, 100), "s");
        assert!(follow.flush(&mut cache, false).is_empty());
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        let admitted = follow.flush(&mut cache, false);
        assert_eq!(admitted.len(), 1);
        assert_eq!(admitted[0].id, "root-write");
        // Admission never freezes a provisional actor; the full replay must re-resolve it.
        assert!(admitted[0].evidence.process_creation_time.is_none());
        assert_eq!(follow.stats.candidate_dropped, 0);
    }
    #[test]
    fn only_direct_child_passes_and_unrelated_same_directory_is_not_promoted() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        for (pid, birth, parent, tid) in [(2, 150, 1, 8), (3, 170, 2, 9), (4, 120, 99, 10)] {
            cache.ingest(&lifecycle(
                "process_start",
                pid,
                Some(birth),
                Some(parent),
                None,
                birth,
            ));
            cache.ingest(&lifecycle(
                "thread_start",
                pid,
                None,
                None,
                Some(tid),
                birth + 1,
            ));
        }
        follow.push(file("direct-child", 8));
        follow.push(file("grandchild", 9));
        follow.push(file("unrelated", 10));
        let admitted = follow.flush(&mut cache, false);
        assert_eq!(
            admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["direct-child"]
        );
        assert_eq!(follow.stats.excluded_other, 2);
        let mut event = admitted[0].clone();
        cache.resolve_event(&mut event);
        let processes = cache
            .processes
            .values()
            .filter(|p| is_direct_actor(p, (1, 100)))
            .cloned()
            .collect::<Vec<_>>();
        crate::attribution::attribute(&mut event, &processes, "s");
        assert_eq!(event.confidence, Confidence::High);
        assert_eq!(event.evidence.process_creation_time, Some(150));
        assert_eq!(event.evidence.parent_pid, Some(1));
    }
    #[test]
    fn pid_reuse_and_late_parent_conflict_never_become_a_final_actor_proof() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle(
            "process_start",
            2,
            Some(150),
            Some(1),
            None,
            150,
        ));
        cache.ingest(&lifecycle("thread_start", 2, None, None, Some(8), 151));
        follow.push(file("child", 8));
        let mut event = follow.flush(&mut cache, false).remove(0);
        cache.ingest(&lifecycle(
            "process_start",
            2,
            Some(150),
            Some(9),
            None,
            150,
        ));
        cache.attach((1, 100), "s");
        cache.resolve_event(&mut event);
        let processes = cache
            .processes
            .values()
            .filter(|p| is_direct_actor(p, (1, 100)))
            .cloned()
            .collect::<Vec<_>>();
        crate::attribution::attribute(&mut event, &processes, "s");
        assert_eq!(event.confidence, Confidence::Unknown);
        let mut reused = file("reused-pid", 7);
        reused.evidence.pid = Some(1);
        reused.evidence.process_creation_time = Some(300);
        follow.push(reused);
        assert!(follow.flush(&mut cache, true).is_empty());
    }
    #[test]
    fn completion_is_retained_only_for_an_admitted_request() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        let mut request = file("root", 7);
        request.raw.irp = Some("irp".into());
        follow.push(request);
        follow.push(SystemEvent {
            id: "completion".into(),
            event_type: "completion".into(),
            raw: RawEvidence {
                related_event: Some("root".into()),
                ..Default::default()
            },
            ..Default::default()
        });
        follow.push(SystemEvent {
            id: "unrelated-completion".into(),
            event_type: "completion".into(),
            raw: RawEvidence {
                related_event: Some("absent".into()),
                ..Default::default()
            },
            ..Default::default()
        });
        assert_eq!(
            follow
                .flush(&mut cache, false)
                .iter()
                .map(|e| e.id.as_str())
                .collect::<Vec<_>>(),
            ["root", "completion"]
        );
        assert!(follow.pending.is_empty());
    }
    #[test]
    fn saturated_unknown_backlog_does_not_discard_verified_writers_or_known_background() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        for (pid, birth, parent, tid) in [(2, 150, 1, 8), (3, 170, 99, 9)] {
            cache.ingest(&lifecycle(
                "process_start",
                pid,
                Some(birth),
                Some(parent),
                None,
                birth,
            ));
            cache.ingest(&lifecycle(
                "thread_start",
                pid,
                None,
                None,
                Some(tid),
                birth + 1,
            ));
        }
        // Fill the unchanged provisional budget with records whose identity may
        // become available later. None may be evicted to make the targets fit.
        for start in (0..CANDIDATE_RECORDS).step_by(256) {
            assert!(
                follow
                    .receive(
                        (start..start + 256)
                            .map(|i| file(&format!("unknown-{i}"), 99))
                            .collect(),
                        &mut cache,
                        false,
                    )
                    .is_empty()
            );
        }
        let admitted = follow.receive(
            vec![file("root", 7), file("child", 8), file("background", 9)],
            &mut cache,
            true,
        );
        assert_eq!(
            admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["root", "child"]
        );
        assert!(
            admitted
                .iter()
                .all(|e| e.evidence.process_creation_time.is_none())
        );
        assert_eq!(follow.pending.len(), CANDIDATE_RECORDS);
        assert_eq!(
            follow.stats.candidate_high_water_records,
            CANDIDATE_RECORDS as u64
        );
        assert!(follow.stats.candidate_high_water_bytes <= CANDIDATE_BYTES as u64);
        assert_eq!(follow.stats.candidate_dropped, 0);
        assert_eq!(follow.stats.excluded_other, 1);
        // Late identity still recovers every original unresolved candidate.
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(99), 120));
        assert_eq!(follow.flush(&mut cache, true).len(), CANDIDATE_RECORDS);
        assert_eq!(follow.bytes, 0);
        assert_eq!(
            follow.stats.candidates,
            follow.stats.admitted
                + follow.stats.excluded_other
                + follow.stats.unresolved
                + follow.stats.candidate_dropped
        );
    }
    #[test]
    fn receive_retains_reversed_completion_and_unknown_same_resource_competitor() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        let mut request = file("root", 7);
        request.raw.irp = Some("irp".into());
        let completion = SystemEvent {
            id: "end".into(),
            event_type: "completion".into(),
            raw: RawEvidence {
                related_event: Some("root".into()),
                ..Default::default()
            },
            ..Default::default()
        };
        let mut competitor = file("competitor", 99);
        competitor.resource = request.resource.clone();
        let admitted = follow.receive(vec![completion, request, competitor], &mut cache, true);
        assert_eq!(
            admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["root", "competitor", "end"]
        );
        assert_eq!(admitted[1].confidence, Confidence::Unknown);
        assert!(follow.flush(&mut cache, true).is_empty());
        assert_eq!(follow.stats.candidate_dropped, 0);
        assert!(follow.pending.is_empty());
    }
    #[test]
    fn receive_preserves_late_conflict_revocation_and_resource_request_limits() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle(
            "process_start",
            2,
            Some(150),
            Some(1),
            None,
            150,
        ));
        cache.ingest(&lifecycle("thread_start", 2, None, None, Some(8), 151));
        let mut event = follow
            .receive(vec![file("child", 8)], &mut cache, true)
            .remove(0);
        cache.ingest(&lifecycle(
            "process_start",
            2,
            Some(150),
            Some(9),
            None,
            150,
        ));
        cache.attach((1, 100), "s");
        cache.resolve_event(&mut event);
        let actors = cache
            .processes
            .values()
            .filter(|p| is_direct_actor(p, (1, 100)))
            .cloned()
            .collect::<Vec<_>>();
        crate::attribution::attribute(&mut event, &actors, "s");
        assert_eq!(event.confidence, Confidence::Unknown);
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        follow.resources = (0..RESOURCE_LIMIT)
            .map(|i| format!("bounded-{i}"))
            .collect();
        assert!(
            follow
                .receive(vec![file("new-resource", 7)], &mut cache, true)
                .is_empty()
        );
        assert_eq!(follow.stats.candidate_dropped, 1);
        assert_eq!(follow.resources.len(), RESOURCE_LIMIT);
        follow.resources.clear();
        follow.requests = (0..REQUEST_LIMIT).map(|i| format!("request-{i}")).collect();
        let mut request = file("new-request", 7);
        request.raw.irp = Some("irp".into());
        assert!(follow.receive(vec![request], &mut cache, true).is_empty());
        assert_eq!(follow.stats.candidate_dropped, 2);
        assert_eq!(follow.requests.len(), REQUEST_LIMIT);
    }
    #[test]
    fn receive_checks_verified_record_size_before_retaining_resources_or_requests() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        let mut event = file("bounded", 7);
        event.resource.clear();
        event.raw.irp = Some("irp".into());
        let overhead = FollowBuffer::serialized_size(&event).unwrap();
        event.resource = "x".repeat(CANDIDATE_RECORD_BYTES - overhead);
        assert_eq!(
            FollowBuffer::serialized_size(&event),
            Some(CANDIDATE_RECORD_BYTES)
        );
        let mut oversized = event.clone();
        oversized.resource.push('x');
        assert!(follow.receive(vec![oversized], &mut cache, true).is_empty());
        assert_eq!(follow.stats.candidate_dropped, 1);
        assert!(follow.resources.is_empty());
        assert!(follow.requests.is_empty());
        assert!(follow.pending.is_empty());
        let admitted = follow.receive(vec![event], &mut cache, true);
        assert_eq!(admitted.len(), 1);
        assert_eq!(follow.resources.len(), 1);
        assert_eq!(follow.requests.len(), 1);
        assert_eq!(follow.stats.candidate_dropped, 1);
    }
    #[test]
    fn pure_background_pages_do_not_rescan_saturated_unknown_candidates() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle(
            "process_start",
            3,
            Some(150),
            Some(99),
            None,
            150,
        ));
        cache.ingest(&lifecycle("thread_start", 3, None, None, Some(9), 151));
        for start in (0..CANDIDATE_RECORDS).step_by(256) {
            assert!(
                follow
                    .receive(
                        (start..start + 256)
                            .map(|i| file(&format!("unknown-{i}"), 99))
                            .collect(),
                        &mut cache,
                        false
                    )
                    .is_empty()
            );
        }
        let before = follow.retry_visits;
        for page in 0..100 {
            assert!(
                follow
                    .receive(
                        (0..256)
                            .map(|i| file(&format!("background-{page}-{i}"), 9))
                            .collect(),
                        &mut cache,
                        false
                    )
                    .is_empty()
            );
        }
        assert_eq!(follow.retry_visits, before);
        assert_eq!(follow.pending.len(), CANDIDATE_RECORDS);
        assert_eq!(follow.stats.excluded_other, 25_600);
        assert_eq!(follow.stats.candidate_dropped, 0);
        assert_eq!(follow.stats.candidates, CANDIDATE_RECORDS as u64 + 25_600);
        assert!(follow.bytes <= CANDIDATE_BYTES);
        // The explicit STOP pass still checks every candidate and accounts for
        // every unresolved record without a new lifecycle notification.
        assert!(follow.flush(&mut cache, true).is_empty());
        assert!(follow.retry_visits >= before + CANDIDATE_RECORDS as u64);
        assert_eq!(follow.stats.unresolved, CANDIDATE_RECORDS as u64);
        assert!(follow.pending_completion_requests.is_empty());
    }
    #[test]
    fn dirty_lifecycle_and_forced_stop_each_recover_late_thread_evidence() {
        for notify in [true, false] {
            let mut follow = FollowBuffer::default();
            follow.bind((1, 100), "s");
            let mut cache = cache();
            assert!(
                follow
                    .receive(vec![file("late", 7)], &mut cache, false)
                    .is_empty()
            );
            let before = follow.retry_visits;
            assert!(follow.receive(Vec::new(), &mut cache, false).is_empty());
            assert_eq!(follow.retry_visits, before);
            cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
            let admitted = if notify {
                follow.receive(Vec::new(), &mut cache, true)
            } else {
                follow.flush(&mut cache, true)
            };
            assert_eq!(
                admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
                ["late"]
            );
            assert!(admitted[0].evidence.process_creation_time.is_none());
        }
    }
    #[test]
    fn root_bind_and_current_request_release_historical_completion_without_new_lifecycle() {
        let mut follow = FollowBuffer::default();
        let mut cache = cache();
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        let completion = SystemEvent {
            id: "historical-end".into(),
            event_type: "completion".into(),
            raw: RawEvidence {
                related_event: Some("root".into()),
                ..Default::default()
            },
            ..Default::default()
        };
        assert!(
            follow
                .receive(vec![completion, file("before-bind", 7)], &mut cache, false)
                .is_empty()
        );
        follow.bind((1, 100), "s");
        let mut request = file("root", 7);
        request.raw.irp = Some("irp".into());
        let admitted = follow.receive(vec![request], &mut cache, false);
        assert_eq!(
            admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["before-bind", "root", "historical-end"]
        );
        assert!(follow.pending.is_empty());
        assert!(follow.pending_completion_requests.is_empty());
    }
    #[test]
    fn new_resource_retries_an_earlier_unknown_competitor_without_new_lifecycle() {
        let mut follow = FollowBuffer::default();
        follow.bind((1, 100), "s");
        let mut cache = cache();
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        let target = file("target", 7);
        let mut competitor = file("competitor", 99);
        competitor.resource = target.resource.clone();
        assert!(
            follow
                .receive(vec![competitor], &mut cache, false)
                .is_empty()
        );
        let admitted = follow.receive(vec![target], &mut cache, false);
        assert_eq!(
            admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["target", "competitor"]
        );
        assert_eq!(admitted[1].confidence, Confidence::Unknown);
        assert!(admitted[1].evidence.process_creation_time.is_none());
        assert!(follow.pending.is_empty());
    }
    #[test]
    fn admitted_metadata_releases_orphan_completion_on_current_or_historical_page() {
        for historical in [false, true] {
            let mut follow = FollowBuffer::default();
            follow.bind((1, 100), "s");
            let mut cache = cache();
            cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
            let seed = file("seed", 7);
            let resource = seed.resource.clone();
            assert_eq!(follow.receive(vec![seed], &mut cache, false).len(), 1);
            let mut metadata = file("metadata", 7);
            metadata.operation = "open_requested".into();
            metadata.resource = resource;
            let completion = SystemEvent {
                id: "orphan-end".into(),
                event_type: "completion".into(),
                raw: RawEvidence {
                    related_event: Some("metadata".into()),
                    ..Default::default()
                },
                ..Default::default()
            };
            let incoming = if historical {
                // Retained before its matching metadata became available.
                follow.push(completion);
                vec![metadata]
            } else {
                vec![completion, metadata]
            };
            let admitted = follow.receive(incoming, &mut cache, false);
            assert_eq!(
                admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
                ["metadata"]
            );
            assert!(follow.pending.is_empty());
            assert!(follow.pending_completion_requests.is_empty());
            assert_eq!(follow.stats.unresolved, 1);
            assert_eq!(follow.stats.candidate_dropped, 0);
        }
    }
    #[test]
    fn forced_scan_keeps_new_relationships_dirty_for_earlier_competitors() {
        let mut follow = FollowBuffer::default();
        let mut cache = cache();
        cache.ingest(&lifecycle("thread_start", 1, None, None, Some(7), 110));
        let target = file("target", 7);
        let mut competitor = file("competitor", 99);
        competitor.resource = target.resource.clone();
        follow.push(competitor);
        follow.push(target);
        follow.bind((1, 100), "s");
        let first = follow.flush(&mut cache, false);
        assert_eq!(
            first.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["target"]
        );
        assert_ne!(follow.selection_generation, follow.last_retry_generation);
        let second = follow.receive(Vec::new(), &mut cache, false);
        assert_eq!(
            second.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["competitor"]
        );
        assert_eq!(second[0].confidence, Confidence::Unknown);
        assert!(follow.pending.is_empty());
    }
    #[test]
    fn dropping_an_incoming_request_releases_its_historical_completion_slot() {
        let mut follow = FollowBuffer::default();
        let mut cache = cache();
        let completion = SystemEvent {
            id: "end".into(),
            event_type: "completion".into(),
            raw: RawEvidence {
                related_event: Some("request".into()),
                ..Default::default()
            },
            ..Default::default()
        };
        follow.push(completion);
        for i in 1..CANDIDATE_RECORDS {
            follow.push(file(&format!("unknown-{i}"), 99));
        }
        follow.bind((1, 100), "s");
        assert!(
            follow
                .receive(vec![file("request", 99)], &mut cache, false)
                .is_empty()
        );
        assert_eq!(follow.stats.candidate_dropped, 1);
        assert_eq!(follow.stats.unresolved, 1);
        assert_eq!(follow.pending.len(), CANDIDATE_RECORDS - 1);
        assert!(follow.pending_completion_requests.is_empty());
        let before = follow.retry_visits;
        assert!(follow.receive(Vec::new(), &mut cache, false).is_empty());
        assert_eq!(follow.retry_visits, before);
    }
    #[test]
    fn unresolved_candidates_are_bounded_and_overflow_is_counted_not_retried() {
        let mut follow = FollowBuffer::default();
        let mut cache = cache();
        for i in 0..=CANDIDATE_RECORDS {
            follow.push(file(&format!("unknown-{i}"), 99));
        }
        assert_eq!(follow.pending.len(), CANDIDATE_RECORDS);
        assert_eq!(follow.stats.candidate_dropped, 1);
        assert!(follow.bytes <= CANDIDATE_BYTES);
        follow.bind((1, 100), "s");
        assert!(follow.flush(&mut cache, true).is_empty());
        assert_eq!(follow.stats.unresolved, CANDIDATE_RECORDS as u64);
        assert_eq!(follow.bytes, 0);
        let mut oversized = file("large", 99);
        oversized.resource = "x".repeat(CANDIDATE_RECORD_BYTES + 1);
        follow.push(oversized);
        assert_eq!(follow.stats.candidate_dropped, 2);
    }
}
