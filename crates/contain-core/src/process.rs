use crate::model::{
    AttributionEvidence, AttributionRule, Confidence, EvidenceSource, ProcessRecord,
};
use crate::windows::native::{self, ProcessIdentity};
use std::collections::BTreeMap;
use sysinfo::{Pid, ProcessesToUpdate, System};

pub struct ProcessObserver {
    root: ProcessIdentity,
    session_id: String,
    system: System,
    records: BTreeMap<(u32, u64), ProcessRecord>,
}

impl ProcessObserver {
    pub fn new(root: ProcessIdentity, session_id: &str) -> Self {
        let time = native::timestamp(native::now_ticks());
        let evidence = AttributionEvidence {
            source: EvidenceSource::ProcessApi,
            pid: Some(root.pid),
            process_creation_time: (root.creation_time != 0).then_some(root.creation_time),
            process_image: Some(root.image.clone()),
            session_id: Some(session_id.into()),
            rule: AttributionRule::InstallerPid,
            ..Default::default()
        };
        let record = ProcessRecord { pid: root.pid, image: root.image.clone(), first_seen: time.clone(), last_seen: time,
            creation_time: (root.creation_time != 0).then_some(root.creation_time), confidence: Confidence::Certain,
            reason: if root.creation_time != 0 { "Contain launched this process and queried its creation time through the owned process handle." } else { "Contain launched this process; creation time is unavailable and descendant attribution is disabled." }.into(),
            evidence, ..Default::default() };
        Self {
            records: BTreeMap::from([((root.pid, root.creation_time), record)]),
            root,
            session_id: session_id.into(),
            system: System::new(),
        }
    }

    pub fn poll(&mut self) {
        self.system.refresh_processes(ProcessesToUpdate::All, true);
        let time = native::timestamp(native::now_ticks());
        let mut identities = BTreeMap::new();
        for (&key, record) in &mut self.records {
            if let Some(identity) = crate::profile::measured!(
                "process_poll_identity_query",
                native::process_identity(key.0)
            )
            .filter(|p| p.creation_time == key.1)
            {
                identities.insert(key.0, identity);
                record.last_seen = time.clone();
            }
        }
        let mut progress = true;
        while progress {
            progress = false;
            for (pid, process) in self.system.processes() {
                let Some(parent_pid) = process.parent().map(Pid::as_u32) else {
                    continue;
                };
                let Some(parent) = identities.get(&parent_pid) else {
                    continue;
                };
                if identities.contains_key(&pid.as_u32()) {
                    continue;
                }
                let Some(identity) = crate::profile::measured!(
                    "process_poll_identity_query",
                    native::process_identity(pid.as_u32())
                ) else {
                    continue;
                };
                if identity.creation_time < parent.creation_time {
                    continue;
                }
                let parent_creation_time = parent.creation_time;
                let evidence = AttributionEvidence {
                    source: EvidenceSource::ProcessApi,
                    pid: Some(identity.pid),
                    process_creation_time: Some(identity.creation_time),
                    process_image: Some(identity.image.clone()),
                    parent_pid: Some(parent_pid),
                    ancestor_pid: Some(self.root.pid),
                    session_id: Some(self.session_id.clone()),
                    rule: AttributionRule::DescendantProcess,
                };
                self.records.entry((identity.pid, identity.creation_time)).or_insert(ProcessRecord {
                    pid: identity.pid, parent_pid: Some(parent_pid), image: identity.image.clone(),
                    first_seen: time.clone(), last_seen: time.clone(), creation_time: Some(identity.creation_time),
                    parent_creation_time: Some(parent_creation_time), ended_at: None,
                    confidence: Confidence::High, reason: "Sampled live parent chain with matching process creation times reaches installer.".into(), evidence,
                });
                identities.insert(identity.pid, identity);
                progress = true;
            }
        }
    }

    pub fn finish(self) -> Vec<ProcessRecord> {
        self.records.into_values().collect()
    }
    pub fn records(&self) -> Vec<ProcessRecord> {
        self.records.values().cloned().collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn direct_owned_identity_is_certain_and_missing_birth_stays_missing() {
        let identity = native::process_identity(std::process::id()).expect("current process query");
        let records = ProcessObserver::new(identity.clone(), "session").finish();
        assert_eq!(records[0].confidence, Confidence::Certain);
        assert_eq!(records[0].creation_time, Some(identity.creation_time));
        let records = ProcessObserver::new(
            ProcessIdentity {
                creation_time: 0,
                ..identity
            },
            "session",
        )
        .finish();
        assert!(records[0].evidence.process_creation_time.is_none());
    }
}
