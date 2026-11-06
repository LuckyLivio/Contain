//! Bounded provisional admission for follow-installer capture. No path or PID
//! alone is an actor proof; all retained candidates are re-resolved after STOP.
use crate::{lifetime::LifetimeCache, model::*};
use std::collections::{BTreeSet, VecDeque};

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

impl FollowBuffer {
    pub fn bind(&mut self, root: (u32, u64), session: &str) {
        if self.root.is_none() && root.1 != 0 {
            self.root = Some(root);
            self.session = session.into();
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
            self.stats.candidate_dropped += 1;
            return;
        };
        if size > CANDIDATE_RECORD_BYTES
            || self.pending.len() >= CANDIDATE_RECORDS
            || self.bytes.saturating_add(size) > CANDIDATE_BYTES
        {
            self.stats.candidate_dropped += 1;
            return;
        }
        self.bytes += size;
        self.pending_ids.insert(event.id.clone());
        self.pending.push_back((event, size));
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
    ) -> Vec<SystemEvent> {
        // A completion can precede its request in this page. Register all IDs
        // before classification, just as when the whole page was buffered first.
        for event in &incoming {
            self.pending_ids.insert(event.id.clone());
        }
        let mut admitted = self.flush(cache, false);
        for event in incoming {
            self.stats.candidates += 1;
            self.pending_ids.remove(&event.id);
            match self.select(&event, cache, false, false) {
                Selection::Admit => admitted.push(event),
                Selection::Discard => {}
                Selection::Pending => self.buffer(event),
            }
        }
        admitted
    }
    pub fn flush(&mut self, cache: &mut LifetimeCache, final_pass: bool) -> Vec<SystemEvent> {
        let mut admitted = Vec::new();
        if let Some(root) = self.root {
            cache.attach(root, &self.session);
        }
        let count = self.pending.len();
        for _ in 0..count {
            let (event, bytes) = self.pending.pop_front().unwrap();
            self.bytes -= bytes;
            self.pending_ids.remove(&event.id);
            match self.select(&event, cache, final_pass, true) {
                Selection::Admit => admitted.push(event),
                Selection::Discard => {}
                Selection::Pending => {
                    self.bytes += bytes;
                    self.pending_ids.insert(event.id.clone());
                    self.pending.push_back((event, bytes));
                }
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
        let unrelated_completion = completion
            && !completion_match
            && !related.is_some_and(|id| self.pending_ids.contains(id));
        let unselected_metadata = matches!(event.operation.as_str(), "open_requested" | "close");
        if completion_match
            || (!completion && (matches || self.resources.contains(&event.resource)))
        {
            // Preserve the per-record guard before cloning a selected resource
            // or request ID. Pending records have already passed this check.
            if !size_checked
                && Self::serialized_size(event).is_none_or(|size| size > CANDIDATE_RECORD_BYTES)
            {
                self.stats.candidate_dropped += 1;
                return Selection::Discard;
            }
            if matches && !self.resources.contains(&event.resource) {
                if self.resources.len() >= RESOURCE_LIMIT {
                    self.stats.candidate_dropped += 1;
                    return Selection::Discard;
                }
                self.resources.insert(event.resource.clone());
            }
            if !completion
                && event.raw.irp.is_some()
                && matches!(
                    event.operation.as_str(),
                    "write_requested" | "delete_requested" | "rename_requested"
                )
            {
                if self.requests.len() >= REQUEST_LIMIT {
                    self.stats.candidate_dropped += 1;
                    return Selection::Discard;
                }
                self.requests.insert(event.id.clone());
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
                        &mut cache
                    )
                    .is_empty()
            );
        }
        let admitted = follow.receive(
            vec![file("root", 7), file("child", 8), file("background", 9)],
            &mut cache,
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
        let admitted = follow.receive(vec![completion, request, competitor], &mut cache);
        assert_eq!(
            admitted.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
            ["root", "competitor"]
        );
        assert_eq!(admitted[1].confidence, Confidence::Unknown);
        assert_eq!(follow.flush(&mut cache, true)[0].id, "end");
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
        let mut event = follow.receive(vec![file("child", 8)], &mut cache).remove(0);
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
                .receive(vec![file("new-resource", 7)], &mut cache)
                .is_empty()
        );
        assert_eq!(follow.stats.candidate_dropped, 1);
        assert_eq!(follow.resources.len(), RESOURCE_LIMIT);
        follow.resources.clear();
        follow.requests = (0..REQUEST_LIMIT).map(|i| format!("request-{i}")).collect();
        let mut request = file("new-request", 7);
        request.raw.irp = Some("irp".into());
        assert!(follow.receive(vec![request], &mut cache).is_empty());
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
        assert!(follow.receive(vec![oversized], &mut cache).is_empty());
        assert_eq!(follow.stats.candidate_dropped, 1);
        assert!(follow.resources.is_empty());
        assert!(follow.requests.is_empty());
        assert!(follow.pending.is_empty());
        let admitted = follow.receive(vec![event], &mut cache);
        assert_eq!(admitted.len(), 1);
        assert_eq!(follow.resources.len(), 1);
        assert_eq!(follow.requests.len(), 1);
        assert_eq!(follow.stats.candidate_dropped, 1);
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
