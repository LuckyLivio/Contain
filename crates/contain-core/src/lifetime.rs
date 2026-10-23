//! Retained process/thread intervals. Resolution is event-time based and rejects ambiguity.
use crate::{model::*, windows::native};
use std::collections::{BTreeMap, BTreeSet};

pub type ProcessIdentity = (u32, u64);
const CAPACITY: usize = 32_768;

#[derive(Default)]
pub struct LifetimeCache {
    pub processes: BTreeMap<ProcessIdentity, ProcessRecord>,
    threads: BTreeMap<(u32, u64), (u32, Option<u64>)>,
    conflicting_threads: BTreeSet<(u32, u64)>,
    conflicting_processes: BTreeSet<ProcessIdentity>,
    pending_process_stops: BTreeMap<ProcessIdentity, u64>,
    pending_thread_stops: BTreeSet<(u32, u32, u64)>,
    pub dropped: u64,
    snapshot_from: BTreeMap<ProcessIdentity, u64>,
}

impl LifetimeCache {
    pub fn seed_snapshot(&mut self, process: ProcessRecord, observed_at: u64) {
        let Some(birth) = process.creation_time else {
            return;
        };
        let key = (process.pid, birth);
        // A lifecycle start already proves a wider interval than a later snapshot.
        if self.processes.contains_key(&key) {
            return;
        }
        self.seed(process);
        if self.processes.contains_key(&key) {
            self.snapshot_from.insert(key, observed_at);
        }
    }
    /// Unknown/missing exit is pending, never evidence that a child is gone.
    /// The caller still applies the monotonic maximum drain deadline.
    pub fn descendants_pending(&self, root: ProcessIdentity) -> bool {
        self.processes.values().any(|p| {
            (p.pid, p.creation_time.unwrap_or(0)) != root
                && p.confidence == Confidence::High
                && p.ended_at.is_none()
        })
    }
    pub fn seed(&mut self, mut process: ProcessRecord) {
        let Some(birth) = process.creation_time else {
            return;
        };
        let key = (process.pid, birth);
        if !self.processes.contains_key(&key) && self.processes.len() >= CAPACITY {
            self.dropped += 1;
            return;
        }
        if let Some(stop) = self.pending_process_stops.remove(&key) {
            process.ended_at = Some(process.ended_at.map_or(stop, |old| old.min(stop)));
        }
        match self.processes.get_mut(&key) {
            Some(old) => {
                if (!old.image.is_empty()
                    && !process.image.is_empty()
                    && native::normalize_path(&old.image, &[])
                        != native::normalize_path(&process.image, &[]))
                    || matches!((old.parent_pid, process.parent_pid), (Some(a), Some(b)) if a != b)
                    || matches!((old.parent_creation_time, process.parent_creation_time), (Some(a), Some(b)) if a != b)
                {
                    self.conflicting_processes.insert(key);
                }
                if self.conflicting_processes.contains(&key) {
                    // The owned launch handle independently proves the root. It
                    // does not make contradictory ETW ancestry safe to reuse.
                    if !(old.confidence == Confidence::Certain
                        && old.evidence.source == EvidenceSource::ProcessApi
                        && old.evidence.rule == AttributionRule::InstallerPid)
                    {
                        old.confidence = Confidence::Unknown;
                        old.evidence.rule = AttributionRule::AmbiguousLifetime;
                        old.reason = "Conflicting executable or parent identities were reported for this process instance.".into();
                    }
                } else if matches!(process.confidence, Confidence::Certain | Confidence::High) {
                    old.confidence = process.confidence;
                    old.evidence = process.evidence;
                    old.reason = process.reason;
                }
                if old.image.is_empty() {
                    old.image = process.image;
                }
                old.last_seen = process.last_seen;
                old.ended_at = match (old.ended_at, process.ended_at) {
                    (Some(a), Some(b)) => Some(a.min(b)),
                    (a, b) => a.or(b),
                };
                old.parent_pid = old.parent_pid.or(process.parent_pid);
                old.parent_creation_time =
                    old.parent_creation_time.or(process.parent_creation_time);
            }
            None => {
                self.processes.insert(key, process);
            }
        }
    }

    pub fn ingest(&mut self, event: &SystemEvent) {
        if event.evidence.source != EvidenceSource::EtwProcess {
            return;
        }
        let Some(pid) = event.evidence.pid else {
            return;
        };
        match event.operation.as_str() {
            "process_start" | "process_stop" => {
                let Some(birth) = event.evidence.process_creation_time else {
                    return;
                };
                if event.operation == "process_start" {
                    self.snapshot_from.remove(&(pid, birth));
                    self.seed(ProcessRecord {
                        pid,
                        creation_time: Some(birth),
                        parent_pid: event.evidence.parent_pid,
                        image: event.resource.clone(),
                        first_seen: native::timestamp(birth),
                        last_seen: event.timestamp.clone(),
                        evidence: event.evidence.clone(),
                        ..Default::default()
                    });
                } else {
                    let at = event.timestamp_ticks;
                    if let Some(process) = self.processes.get_mut(&(pid, birth)) {
                        // Duplicate or inconsistent stops cannot extend an established lifetime.
                        process.ended_at = Some(process.ended_at.map_or(at, |end| end.min(at)));
                        process.last_seen = event.timestamp.clone();
                    } else if self.pending_process_stops.contains_key(&(pid, birth))
                        || self.pending_process_stops.len() < CAPACITY
                    {
                        self.pending_process_stops
                            .entry((pid, birth))
                            .and_modify(|end| *end = (*end).min(at))
                            .or_insert(at);
                    } else {
                        self.dropped += 1;
                    }
                }
            }
            "thread_start" => {
                if let Some(tid) = event.raw.thread_id {
                    let key = (tid, event.timestamp_ticks);
                    if let Some((owner, _)) = self.threads.get(&key) {
                        // Identical starts are idempotent, including after a stop.
                        // Conflicting starts must never select the last observed owner.
                        if *owner != pid {
                            self.conflicting_threads.insert(key);
                        }
                    } else if self.threads.len() < CAPACITY * 4 {
                        let stop = self
                            .pending_thread_stops
                            .range((tid, pid, event.timestamp_ticks)..=(tid, pid, u64::MAX))
                            .next()
                            .copied();
                        if let Some(stop) = stop {
                            self.pending_thread_stops.remove(&stop);
                        }
                        self.threads.insert(key, (pid, stop.map(|(_, _, at)| at)));
                    } else {
                        self.dropped += 1;
                    }
                }
            }
            "thread_stop" => {
                if let Some(tid) = event.raw.thread_id {
                    let candidates: Vec<_> = self
                        .threads
                        .iter()
                        .filter(|((id, start), (owner, end))| {
                            *id == tid
                                && *owner == pid
                                && *start <= event.timestamp_ticks
                                && end.is_none_or(|end| event.timestamp_ticks <= end)
                        })
                        .map(|(k, _)| *k)
                        .collect();
                    if candidates.len() == 1 {
                        self.threads.get_mut(&candidates[0]).unwrap().1 =
                            Some(event.timestamp_ticks);
                    } else if candidates.is_empty() {
                        let stop = (tid, pid, event.timestamp_ticks);
                        if self.pending_thread_stops.contains(&stop)
                            || self.pending_thread_stops.len() < CAPACITY * 4
                        {
                            self.pending_thread_stops.insert(stop);
                        } else {
                            self.dropped += 1;
                        }
                    }
                }
            }
            _ => {}
        }
    }

    pub fn resolve(&self, pid: u32, at: u64) -> Option<&ProcessRecord> {
        let mut candidates = self
            .processes
            .range((pid, 0)..=(pid, u64::MAX))
            .filter(|(_, p)| {
                p.creation_time.is_some_and(|birth| birth <= at)
                    && p.ended_at.is_none_or(|end| at <= end)
                    && self
                        .snapshot_from
                        .get(&(pid, p.creation_time.unwrap_or(0)))
                        .is_none_or(|from| at >= *from)
            })
            .map(|(_, p)| p);
        let first = candidates.next()?;
        (candidates.next().is_none()
            && !self
                .conflicting_processes
                .contains(&(pid, first.creation_time.unwrap())))
        .then_some(first)
    }

    pub fn attach(&mut self, root: ProcessIdentity, session: &str) {
        // A later conflicting start/stop can invalidate ancestry that was valid
        // during an earlier drain pass. Revalidate existing claims and propagate
        // revocation through descendants before considering any new promotions.
        loop {
            let invalid: Vec<_> = self
                .processes
                .iter()
                .filter_map(|(&key, process)| {
                    if process.confidence != Confidence::High {
                        return None;
                    }
                    let valid = process
                        .parent_pid
                        .zip(process.parent_creation_time)
                        .and_then(|(pid, birth)| {
                            self.resolve(pid, key.1).map(|parent| (birth, parent))
                        })
                        .is_some_and(|(birth, parent)| {
                            parent.creation_time == Some(birth)
                                && birth < key.1
                                && matches!(
                                    parent.confidence,
                                    Confidence::Certain | Confidence::High
                                )
                        });
                    (!valid).then_some(key)
                })
                .collect();
            if invalid.is_empty() {
                break;
            }
            for key in invalid {
                let process = self.processes.get_mut(&key).unwrap();
                process.confidence = Confidence::Unknown;
                process.evidence.rule = AttributionRule::AmbiguousLifetime;
                process.reason = "Previously associated parent chain is no longer unambiguous at process creation; descendant attribution revoked.".into();
            }
        }
        for _ in 0..self.processes.len() {
            let additions: Vec<_> = self
                .processes
                .iter()
                .filter_map(|(&key, p)| {
                    if matches!(p.confidence, Confidence::Certain | Confidence::High)
                        || p.image.is_empty()
                        || self.conflicting_processes.contains(&key)
                    {
                        return None;
                    }
                    let parent = self.resolve(p.parent_pid?, key.1)?;
                    if !matches!(parent.confidence, Confidence::Certain | Confidence::High)
                        || parent.creation_time == Some(key.1)
                        || p.parent_creation_time
                            .is_some_and(|birth| parent.creation_time != Some(birth))
                    {
                        return None;
                    }
                    Some((key, parent.pid, parent.creation_time))
                })
                .collect();
            if additions.is_empty() {
                break;
            }
            for (key, parent, birth) in additions {
                let p = self.processes.get_mut(&key).unwrap();
                p.parent_creation_time = birth;
                p.confidence = Confidence::High;
                p.reason = "ETW creation relationship resolves to an unambiguous retained parent lifetime in this session.".into();
                p.evidence.parent_pid = Some(parent);
                p.evidence.ancestor_pid = Some(root.0);
                p.evidence.session_id = Some(session.into());
                p.evidence.rule = AttributionRule::DescendantProcess;
            }
        }
    }

    pub fn resolve_event(&self, event: &mut SystemEvent) {
        if event.evidence.process_creation_time.is_some() {
            return;
        } // Already verified via live handle.
        let pid = if event.evidence.source == EvidenceSource::EtwFile {
            let Some(tid) = event.raw.thread_id else {
                return;
            };
            let mut candidates =
                self.threads
                    .range((tid, 0)..=(tid, u64::MAX))
                    .filter(|((_, start), (_, end))| {
                        *start <= event.timestamp_ticks
                            && end.is_none_or(|e| event.timestamp_ticks <= e)
                    });
            let Some(((.., start), (pid, _))) = candidates.next() else {
                return;
            };
            if candidates.next().is_some() || self.conflicting_threads.contains(&(tid, *start)) {
                return;
            }
            // The thread must start in the same process lifetime as the event.
            let Some(owner) = self.resolve(*pid, *start) else {
                return;
            };
            if self
                .resolve(*pid, event.timestamp_ticks)
                .map(|p| p.creation_time)
                != Some(owner.creation_time)
            {
                return;
            }
            *pid
        } else {
            let Some(pid) = event.evidence.pid else {
                return;
            };
            pid
        };
        if let Some(process) = self.resolve(pid, event.timestamp_ticks) {
            // A callback may precede a delayed stop/start for a reused PID. Its
            // context path must not be reassigned to a different process lifetime.
            if event.evidence.source == EvidenceSource::EtwRegistry
                && let Some(generation) = &event.raw.object_generation
                && !generation.starts_with(&format!("{pid}:{}:", process.creation_time.unwrap()))
            {
                event.raw.resource_resolved = false;
                return;
            }
            event.evidence.pid = Some(pid);
            event.evidence.process_creation_time = process.creation_time;
            event.evidence.process_image = Some(process.image.clone());
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn lifecycle(operation: &str, pid: u32, birth: Option<u64>, at: u64) -> SystemEvent {
        SystemEvent {
            timestamp_ticks: at,
            operation: operation.into(),
            resource: "helper.exe".into(),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwProcess,
                pid: Some(pid),
                process_creation_time: birth,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(7),
                ..Default::default()
            },
            ..Default::default()
        }
    }
    fn file_event(at: u64) -> SystemEvent {
        SystemEvent {
            timestamp_ticks: at,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(7),
                ..Default::default()
            },
            ..Default::default()
        }
    }
    #[test]
    fn duplicate_thread_start_does_not_reopen_ended_thread() {
        let mut cache = LifetimeCache::default();
        cache.seed(record(2, 100, None));
        let start = lifecycle("thread_start", 2, None, 110);
        cache.ingest(&start);
        cache.ingest(&lifecycle("thread_stop", 2, None, 150));
        cache.ingest(&start);
        let mut event = file_event(200);
        cache.resolve_event(&mut event);
        assert!(event.evidence.process_creation_time.is_none());
        let mut before_stop = file_event(120);
        cache.resolve_event(&mut before_stop);
        assert_eq!(before_stop.evidence.process_creation_time, Some(100));
    }
    #[test]
    fn stop_before_start_does_not_create_an_unbounded_process_lifetime() {
        let mut cache = LifetimeCache::default();
        cache.ingest(&lifecycle("process_stop", 2, Some(100), 150));
        cache.ingest(&lifecycle("process_start", 2, Some(100), 100));
        assert!(cache.resolve(2, 200).is_none());
        assert!(cache.resolve(2, 120).is_some());
    }
    #[test]
    fn conflicting_thread_start_cannot_choose_last_owner() {
        let mut cache = LifetimeCache::default();
        cache.seed(record(2, 100, None));
        cache.seed(record(3, 100, None));
        cache.ingest(&lifecycle("thread_start", 2, None, 110));
        cache.ingest(&lifecycle("thread_start", 3, None, 110));
        let mut event = file_event(120);
        cache.resolve_event(&mut event);
        assert!(event.evidence.process_creation_time.is_none());
    }
    #[test]
    fn later_conflicting_stop_cannot_extend_process_lifetime() {
        let mut cache = LifetimeCache::default();
        cache.seed(record(2, 100, None));
        cache.ingest(&lifecycle("process_stop", 2, Some(100), 150));
        cache.ingest(&lifecycle("process_stop", 2, Some(100), 250));
        assert!(cache.resolve(2, 200).is_none());
    }
    #[test]
    fn thread_stop_before_start_retains_the_end_boundary() {
        let mut cache = LifetimeCache::default();
        cache.seed(record(2, 100, None));
        cache.ingest(&lifecycle("thread_stop", 2, None, 150));
        cache.ingest(&lifecycle("thread_start", 2, None, 110));
        let mut during = file_event(120);
        cache.resolve_event(&mut during);
        assert_eq!(during.evidence.process_creation_time, Some(100));
        let mut after = file_event(200);
        cache.resolve_event(&mut after);
        assert!(after.evidence.process_creation_time.is_none());
    }
    #[test]
    fn earlier_thread_stop_shrinks_an_already_closed_interval() {
        let mut cache = LifetimeCache::default();
        cache.seed(record(2, 100, None));
        cache.ingest(&lifecycle("thread_start", 2, None, 110));
        cache.ingest(&lifecycle("thread_stop", 2, None, 250));
        cache.ingest(&lifecycle("thread_stop", 2, None, 150));
        let mut event = file_event(200);
        cache.resolve_event(&mut event);
        assert!(event.evidence.process_creation_time.is_none());
    }
    #[test]
    fn late_parent_conflict_revokes_all_descendants_but_preserves_owned_root() {
        let mut cache = LifetimeCache::default();
        let mut root = record(1, 10, None);
        root.confidence = Confidence::Certain;
        root.evidence.source = EvidenceSource::ProcessApi;
        root.evidence.rule = AttributionRule::InstallerPid;
        cache.seed(root);
        for (pid, birth, parent) in [(2, 100, 1), (3, 120, 2)] {
            let mut child = lifecycle("process_start", pid, Some(birth), birth);
            child.evidence.parent_pid = Some(parent);
            cache.ingest(&child);
        }
        cache.attach((1, 10), "s");
        assert_eq!(cache.processes[&(3, 120)].confidence, Confidence::High);
        let mut conflict = lifecycle("process_start", 1, Some(10), 10);
        conflict.resource = "conflicting.exe".into();
        cache.ingest(&conflict);
        cache.attach((1, 10), "s");
        assert_eq!(cache.processes[&(1, 10)].confidence, Confidence::Certain);
        assert_eq!(cache.processes[&(2, 100)].confidence, Confidence::Unknown);
        assert_eq!(cache.processes[&(3, 120)].confidence, Confidence::Unknown);
    }
    #[test]
    fn known_parent_creation_time_cannot_be_reassigned_after_pid_reuse() {
        let mut cache = LifetimeCache::default();
        let mut root = record(1, 20, None);
        root.confidence = Confidence::Certain;
        cache.seed(root);
        let mut child = record(2, 30, None);
        child.parent_pid = Some(1);
        child.parent_creation_time = Some(10);
        child.confidence = Confidence::High;
        cache.seed(child);
        cache.attach((1, 20), "s");
        assert_eq!(cache.processes[&(2, 30)].confidence, Confidence::Unknown);
        assert_eq!(cache.processes[&(2, 30)].parent_creation_time, Some(10));
    }
    #[test]
    fn conflicting_parent_or_image_cannot_promote_a_process() {
        for image_conflict in [false, true] {
            let mut cache = LifetimeCache::default();
            let mut root = record(1, 10, None);
            root.confidence = Confidence::Certain;
            cache.seed(root);
            let mut child = lifecycle("process_start", 2, Some(100), 100);
            child.evidence.parent_pid = Some(1);
            cache.ingest(&child);
            let mut conflict = child;
            if image_conflict {
                conflict.resource = "other.exe".into();
            } else {
                conflict.evidence.parent_pid = Some(9);
            }
            cache.ingest(&conflict);
            cache.attach((1, 10), "s");
            assert!(cache.resolve(2, 120).is_none());
            assert_eq!(cache.processes[&(2, 100)].confidence, Confidence::Unknown);
        }
    }
    fn record(pid: u32, birth: u64, end: Option<u64>) -> ProcessRecord {
        ProcessRecord {
            pid,
            creation_time: Some(birth),
            ended_at: end,
            image: "helper.exe".into(),
            ..Default::default()
        }
    }
    #[test]
    fn snapshot_cannot_resolve_before_query_or_hide_pid_reuse() {
        let mut c = LifetimeCache::default();
        c.seed_snapshot(record(5, 100, None), 200);
        assert!(c.resolve(5, 199).is_none());
        assert!(c.resolve(5, 200).is_some());
        c.seed(record(5, 300, None));
        assert!(c.resolve(5, 350).is_none());
        c.processes.get_mut(&(5, 100)).unwrap().ended_at = Some(250);
        assert_eq!(c.resolve(5, 350).unwrap().creation_time, Some(300));
    }

    #[test]
    fn delayed_pid_reuse_cannot_reassign_a_registry_context_path() {
        let mut c = LifetimeCache::default();
        c.seed(record(5, 100, Some(200)));
        c.seed(record(5, 300, None));
        let mut e = SystemEvent {
            timestamp_ticks: 350,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwRegistry,
                pid: Some(5),
                ..Default::default()
            },
            raw: RawEvidence {
                object_generation: Some("5:100:0000000000000011:150".into()),
                resource_resolved: true,
                ..Default::default()
            },
            ..Default::default()
        };
        c.resolve_event(&mut e);
        assert!(e.evidence.process_creation_time.is_none());
        assert!(!e.raw.resource_resolved);
        e.raw.object_generation = Some("5:300:0000000000000011:320".into());
        e.raw.resource_resolved = true;
        c.resolve_event(&mut e);
        assert_eq!(e.evidence.process_creation_time, Some(300));
        assert!(e.raw.resource_resolved);
    }

    #[test]
    fn missing_exit_and_reused_pid_keep_descendant_drain_pending() {
        let mut c = LifetimeCache::default();
        let mut child = record(2, 100, None);
        child.confidence = Confidence::High;
        c.seed(child);
        assert!(c.descendants_pending((1, 10)));
        c.seed(record(2, 300, Some(400)));
        assert!(c.descendants_pending((1, 10))); // Another lifetime exiting cannot close it.
        c.processes.get_mut(&(2, 100)).unwrap().ended_at = Some(200);
        assert!(!c.descendants_pending((1, 10)));
        let mut reused_root_pid = record(1, 300, None);
        reused_root_pid.confidence = Confidence::High;
        c.seed(reused_root_pid);
        assert!(c.descendants_pending((1, 10))); // A descendant can reuse the exited root's PID.
    }
    #[test]
    fn reused_pid_resolves_only_its_interval() {
        let mut c = LifetimeCache::default();
        c.seed(record(5, 100, Some(200)));
        c.seed(record(5, 500, Some(600)));
        assert_eq!(c.resolve(5, 150).unwrap().creation_time, Some(100));
        assert_eq!(c.resolve(5, 550).unwrap().creation_time, Some(500));
        assert!(c.resolve(5, 350).is_none());
    }
    #[test]
    fn overlapping_or_unknown_end_rejects_ambiguous_pid() {
        let mut c = LifetimeCache::default();
        c.seed(record(5, 100, None));
        c.seed(record(5, 150, Some(300)));
        assert!(c.resolve(5, 200).is_none());
    }

    #[test]
    fn reordered_process_and_thread_reuse_preserves_event_time_actor_identity() {
        fn permutations(
            order: &mut [usize; 6],
            offset: usize,
            check: &mut impl FnMut(&[usize; 6]),
        ) {
            if offset == order.len() {
                check(order);
                return;
            }
            for next in offset..order.len() {
                order.swap(offset, next);
                permutations(order, offset + 1, check);
                order.swap(offset, next);
            }
        }

        let mut old_start = lifecycle("process_start", 2, Some(100), 100);
        old_start.evidence.parent_pid = Some(1);
        let mut reused_start = lifecycle("process_start", 2, Some(300), 300);
        reused_start.evidence.parent_pid = Some(99);
        let callbacks = [
            old_start,
            lifecycle("process_stop", 2, Some(100), 200),
            reused_start,
            lifecycle("thread_start", 2, None, 110),
            lifecycle("thread_stop", 2, None, 190),
            lifecycle("thread_start", 2, None, 310),
        ];
        let mut checked = 0;
        permutations(&mut [0, 1, 2, 3, 4, 5], 0, &mut |order| {
            let mut cache = LifetimeCache::default();
            let mut root = record(1, 10, None);
            root.confidence = Confidence::Certain;
            root.evidence.source = EvidenceSource::ProcessApi;
            root.evidence.rule = AttributionRule::InstallerPid;
            cache.seed(root);
            for &index in order {
                cache.ingest(&callbacks[index]);
                cache.attach((1, 10), "session");
            }
            let processes: Vec<_> = cache.processes.values().cloned().collect();
            assert_eq!(cache.processes[&(2, 100)].confidence, Confidence::High);
            assert_eq!(cache.processes[&(2, 300)].confidence, Confidence::Unknown);
            for (at, expected_birth, expected_confidence) in [
                (109, None, Confidence::Unknown),
                (110, Some(100), Confidence::High),
                (150, Some(100), Confidence::High),
                (190, Some(100), Confidence::High),
                (191, None, Confidence::Unknown),
                (250, None, Confidence::Unknown),
                (309, None, Confidence::Unknown),
                (310, Some(300), Confidence::Unknown),
                (350, Some(300), Confidence::Unknown),
            ] {
                let mut event = file_event(at);
                cache.resolve_event(&mut event);
                crate::attribution::attribute(&mut event, &processes, "session");
                assert_eq!(
                    event.evidence.process_creation_time, expected_birth,
                    "callback order {order:?}, event time {at}"
                );
                assert_eq!(
                    event.confidence, expected_confidence,
                    "callback order {order:?}, event time {at}"
                );
                assert_eq!(
                    event.evidence.session_id.as_deref(),
                    expected_birth
                        .filter(|_| expected_confidence == Confidence::High)
                        .map(|_| "session")
                );
            }
            checked += 1;
        });
        assert_eq!(checked, 720);
    }

    #[test]
    fn exited_parent_preserves_detached_birth_ancestry() {
        let mut c = LifetimeCache::default();
        let mut root = record(1, 10, Some(200));
        root.confidence = Confidence::Certain;
        c.seed(root);
        let mut child = record(2, 100, Some(500));
        child.parent_pid = Some(1);
        c.seed(child);
        c.attach((1, 10), "s");
        assert_eq!(c.resolve(2, 400).unwrap().confidence, Confidence::High);
    }
    #[test]
    fn short_lived_thread_resolves_after_exit() {
        let mut c = LifetimeCache::default();
        c.seed(record(2, 100, Some(200)));
        c.threads.insert((7, 110), (2, Some(190)));
        let mut e = SystemEvent {
            timestamp_ticks: 150,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(7),
                ..Default::default()
            },
            ..Default::default()
        };
        c.resolve_event(&mut e);
        assert_eq!(e.evidence.process_creation_time, Some(100));
    }
}
