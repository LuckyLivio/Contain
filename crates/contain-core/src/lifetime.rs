//! Retained process/thread intervals. Resolution is event-time based and rejects ambiguity.
use crate::{model::*, windows::native};
use std::collections::BTreeMap;

pub type ProcessIdentity = (u32, u64);
const CAPACITY: usize = 32_768;

#[derive(Default)]
pub struct LifetimeCache {
    pub processes: BTreeMap<ProcessIdentity, ProcessRecord>,
    threads: BTreeMap<(u32, u64), (u32, Option<u64>)>,
    pub dropped: u64,
}

impl LifetimeCache {
    pub fn seed(&mut self, process: ProcessRecord) {
        let Some(birth) = process.creation_time else {
            return;
        };
        let key = (process.pid, birth);
        if !self.processes.contains_key(&key) && self.processes.len() >= CAPACITY {
            self.dropped += 1;
            return;
        }
        match self.processes.get_mut(&key) {
            Some(old) => {
                if matches!(process.confidence, Confidence::Certain | Confidence::High) {
                    old.confidence = process.confidence;
                    old.evidence = process.evidence;
                    old.reason = process.reason;
                }
                if old.image.is_empty() {
                    old.image = process.image;
                }
                old.last_seen = process.last_seen;
                old.ended_at = old.ended_at.or(process.ended_at);
                old.parent_pid = old.parent_pid.or(process.parent_pid);
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
                } else if let Some(process) = self.processes.get_mut(&(pid, birth)) {
                    process.ended_at = Some(event.timestamp_ticks);
                    process.last_seen = event.timestamp.clone();
                }
            }
            "thread_start" => {
                if let Some(tid) = event.raw.thread_id {
                    if self.threads.len() < CAPACITY * 4 {
                        self.threads
                            .insert((tid, event.timestamp_ticks), (pid, None));
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
                                && end.is_none()
                        })
                        .map(|(k, _)| *k)
                        .collect();
                    if candidates.len() == 1 {
                        self.threads.get_mut(&candidates[0]).unwrap().1 =
                            Some(event.timestamp_ticks);
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
            .map(|(_, p)| p)
            .filter(|p| {
                p.creation_time.is_some_and(|birth| birth <= at)
                    && p.ended_at.is_none_or(|end| at <= end)
            });
        let first = candidates.next()?;
        candidates.next().is_none().then_some(first)
    }

    pub fn attach(&mut self, root: ProcessIdentity, session: &str) {
        for _ in 0..self.processes.len() {
            let additions: Vec<_> = self
                .processes
                .iter()
                .filter_map(|(&key, p)| {
                    if matches!(p.confidence, Confidence::Certain | Confidence::High)
                        || p.image.is_empty()
                    {
                        return None;
                    }
                    let parent = self.resolve(p.parent_pid?, key.1)?;
                    if !matches!(parent.confidence, Confidence::Certain | Confidence::High)
                        || parent.creation_time == Some(key.1)
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
            if candidates.next().is_some() {
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
            event.evidence.pid = Some(pid);
            event.evidence.process_creation_time = process.creation_time;
            event.evidence.process_image = Some(process.image.clone());
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
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
