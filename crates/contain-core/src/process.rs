use crate::model::{Confidence, ProcessRecord};
use chrono::Utc;
use std::collections::BTreeMap;
use sysinfo::{Pid, ProcessesToUpdate, System};

pub struct ProcessObserver {
    root_pid: u32,
    system: System,
    known: BTreeMap<u32, u64>,
    records: BTreeMap<u32, ProcessRecord>,
}

impl ProcessObserver {
    pub fn new(root_pid: u32, installer: &str) -> Self {
        let root = ProcessRecord {
            pid: root_pid,
            parent_pid: None,
            image: installer.into(),
            first_seen: Utc::now().to_rfc3339(),
            confidence: Confidence::Certain,
            reason: "Process launched by Contain.".into(),
        };
        Self {
            root_pid,
            system: System::new(),
            known: BTreeMap::new(),
            records: BTreeMap::from([(root_pid, root)]),
        }
    }

    pub fn poll(&mut self) {
        self.system.refresh_processes(ProcessesToUpdate::All, true);
        if let Some(root) = self.system.process(Pid::from_u32(self.root_pid)) {
            self.known
                .entry(self.root_pid)
                .or_insert_with(|| root.start_time());
        }
        let mut found = true;
        while found {
            found = false;
            for (pid, process) in self.system.processes() {
                let id = pid.as_u32();
                let parent = process.parent().map(Pid::as_u32);
                if self.records.contains_key(&id)
                    || !parent.is_some_and(|p| self.parent_is_current(p, process.start_time()))
                {
                    continue;
                }
                self.known.insert(id, process.start_time());
                self.records.insert(
                    id,
                    ProcessRecord {
                        pid: id,
                        parent_pid: parent,
                        image: process
                            .exe()
                            .map(|p| p.to_string_lossy().into_owned())
                            .unwrap_or_else(|| process.name().to_string_lossy().into_owned()),
                        first_seen: Utc::now().to_rfc3339(),
                        confidence: Confidence::High,
                        reason: format!(
                            "Sampled parent chain reaches installer PID {}.",
                            self.root_pid
                        ),
                    },
                );
                found = true;
            }
        }
    }

    fn parent_is_current(&self, parent: u32, child_start: u64) -> bool {
        let Some(&known_start) = self.known.get(&parent) else {
            return false;
        };
        self.system
            .process(Pid::from_u32(parent))
            .is_some_and(|process| {
                valid_parent_start(known_start, process.start_time(), child_start)
            })
    }

    pub fn finish(self) -> Vec<ProcessRecord> {
        self.records.into_values().collect()
    }
}

fn valid_parent_start(known_start: u64, current_start: u64, child_start: u64) -> bool {
    known_start == current_start && known_start <= child_start
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn root_is_certain() {
        let records = ProcessObserver::new(42, "fixture.exe").finish();
        assert_eq!(records[0].confidence, Confidence::Certain);
    }
    #[test]
    fn reused_or_younger_parent_pid_is_ambiguous() {
        assert!(valid_parent_start(100, 100, 101));
        assert!(!valid_parent_start(100, 200, 201));
        assert!(!valid_parent_start(200, 200, 100));
    }
}
