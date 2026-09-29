use crate::attribution::inventory_evidence;
use crate::model::*;
use anyhow::{Context, Result};
use serde::de::DeserializeOwned;
use std::collections::BTreeMap;
use std::io::Read;
use std::os::windows::process::CommandExt;
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};
use winreg::{RegKey, enums::*};

#[derive(Default)]
pub struct InventorySnapshot {
    pub services: Option<Vec<ServiceState>>,
    pub tasks: Option<Vec<ScheduledTaskState>>,
    pub startup: Option<Vec<StartupEntry>>,
    pub warnings: Vec<String>,
}

fn powershell<T: DeserializeOwned>(script: &str) -> Result<T> {
    let executable = std::env::var_os("SystemRoot")
        .map(std::path::PathBuf::from)
        .context("SystemRoot missing")?
        .join("System32/WindowsPowerShell/v1.0/powershell.exe");
    let mut child = Command::new(executable)
        .args(["-NoProfile", "-NonInteractive", "-Command", script])
        .creation_flags(0x08000000)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;
    let mut stdout = child.stdout.take().context("no inventory output pipe")?;
    let mut stderr = child.stderr.take().context("no inventory error pipe")?;
    // Drain both pipes while waiting; inventory size cannot deadlock a full stdout pipe.
    let out = thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).map(|_| bytes)
    });
    let err = thread::spawn(move || {
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes).map(|_| bytes)
    });
    let deadline = Instant::now() + Duration::from_secs(20);
    let status = loop {
        if let Some(status) = child.try_wait()? {
            break Some(status);
        }
        if Instant::now() > deadline {
            let _ = child.kill();
            let _ = child.wait();
            break None;
        }
        thread::sleep(Duration::from_millis(20));
    };
    let stdout = out
        .join()
        .map_err(|_| anyhow::anyhow!("inventory output worker panicked"))??;
    let stderr = err
        .join()
        .map_err(|_| anyhow::anyhow!("inventory error worker panicked"))??;
    let status = status.context("inventory timed out after 20 seconds")?;
    if !status.success() {
        anyhow::bail!(
            "inventory failed: {}",
            String::from_utf8_lossy(&stderr).trim()
        );
    }
    serde_json::from_slice(&stdout).context("invalid inventory JSON")
}

pub fn startup() -> Result<Vec<StartupEntry>> {
    let mut entries = Vec::new();
    for (hive, label) in [(HKEY_CURRENT_USER, "HKCU"), (HKEY_LOCAL_MACHINE, "HKLM")] {
        for (view, view_name) in [(KEY_WOW64_64KEY, "64"), (KEY_WOW64_32KEY, "32")] {
            let path = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
            let key = match RegKey::predef(hive).open_subkey_with_flags(path, KEY_READ | view) {
                Ok(key) => key,
                Err(error) if error.kind() == std::io::ErrorKind::NotFound => continue,
                Err(error) => {
                    return Err(error)
                        .with_context(|| format!("reading {label} Run ({view_name}-bit)"));
                }
            };
            for value in key.enum_values() {
                let (name, value) = value?;
                entries.push(StartupEntry {
                    source: format!("{label}\\{path} [{view_name}]"),
                    name,
                    command: value.to_string(),
                });
            }
        }
    }
    Ok(entries)
}

pub fn snapshot() -> InventorySnapshot {
    let (services, tasks) = thread::scope(|scope| {
        let services =
            scope.spawn(|| powershell::<Vec<ServiceState>>(include_str!("windows/services.ps1")));
        let tasks = scope
            .spawn(|| powershell::<Vec<ScheduledTaskState>>(include_str!("windows/tasks.ps1")));
        (
            services
                .join()
                .unwrap_or_else(|_| Err(anyhow::anyhow!("service inventory worker failed"))),
            tasks
                .join()
                .unwrap_or_else(|_| Err(anyhow::anyhow!("task inventory worker failed"))),
        )
    });
    let mut result = InventorySnapshot::default();
    match services {
        Ok(rows) => result.services = Some(rows),
        Err(error) => result
            .warnings
            .push(format!("Service inventory unavailable: {error:#}")),
    }
    match tasks {
        Ok(rows) => result.tasks = Some(rows),
        Err(error) => result
            .warnings
            .push(format!("Scheduled task inventory unavailable: {error:#}")),
    }
    match startup() {
        Ok(rows) => result.startup = Some(rows),
        Err(error) => result
            .warnings
            .push(format!("Startup inventory unavailable: {error:#}")),
    }
    result
}

pub fn diff(
    before: &InventorySnapshot,
    after: &InventorySnapshot,
    processes: &[ProcessRecord],
    session_id: &str,
) -> Vec<InventoryChange> {
    let mut changes = Vec::new();
    if let (Some(old), Some(new)) = (&before.services, &after.services) {
        compare(
            "service",
            old.iter()
                .map(|x| (x.name.clone(), InventoryState::Service(x.clone()))),
            new.iter()
                .map(|x| (x.name.clone(), InventoryState::Service(x.clone()))),
            processes,
            session_id,
            &mut changes,
        );
    }
    if let (Some(old), Some(new)) = (&before.tasks, &after.tasks) {
        compare(
            "scheduled_task",
            old.iter()
                .map(|x| (x.path.clone(), InventoryState::ScheduledTask(x.clone()))),
            new.iter()
                .map(|x| (x.path.clone(), InventoryState::ScheduledTask(x.clone()))),
            processes,
            session_id,
            &mut changes,
        );
    }
    if let (Some(old), Some(new)) = (&before.startup, &after.startup) {
        compare(
            "startup",
            old.iter().map(|x| {
                (
                    format!("{}\\{}", x.source, x.name),
                    InventoryState::Startup(x.clone()),
                )
            }),
            new.iter().map(|x| {
                (
                    format!("{}\\{}", x.source, x.name),
                    InventoryState::Startup(x.clone()),
                )
            }),
            processes,
            session_id,
            &mut changes,
        );
    }
    changes
}

fn compare(
    kind: &str,
    old: impl Iterator<Item = (String, InventoryState)>,
    new: impl Iterator<Item = (String, InventoryState)>,
    processes: &[ProcessRecord],
    session_id: &str,
    changes: &mut Vec<InventoryChange>,
) {
    let old: BTreeMap<_, _> = old.collect();
    let new: BTreeMap<_, _> = new.collect();
    let mut names: Vec<_> = old.keys().chain(new.keys()).collect();
    names.sort();
    names.dedup();
    for name in names {
        let before = old.get(name);
        let after = new.get(name);
        if before == after {
            continue;
        }
        let (command, source) = match after.or(before) {
            Some(InventoryState::Service(state)) => {
                (state.binary_path.as_str(), EvidenceSource::ServiceInventory)
            }
            Some(InventoryState::ScheduledTask(state)) => (
                if state.actions.len() == 1 {
                    state.actions[0].executable.as_str()
                } else {
                    ""
                },
                EvidenceSource::TaskInventory,
            ),
            Some(InventoryState::Startup(state)) => {
                (state.command.as_str(), EvidenceSource::StartupRegistry)
            }
            None => continue,
        };
        let (confidence, evidence, reason) =
            inventory_evidence(command, source, processes, session_id);
        changes.push(InventoryChange {
            kind: kind.into(),
            name: name.clone(),
            operation: if before.is_none() {
                "created"
            } else if after.is_none() {
                "removed"
            } else {
                "changed"
            }
            .into(),
            before: before.cloned(),
            after: after.cloned(),
            confidence,
            reason,
            evidence,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn service_diff_is_not_high_based_on_timing() {
        let before = InventorySnapshot {
            services: Some(vec![]),
            ..Default::default()
        };
        let after = InventorySnapshot {
            services: Some(vec![ServiceState {
                name: "demo".into(),
                display_name: "Demo".into(),
                binary_path: "C:\\other.exe".into(),
                startup_type: "Auto".into(),
                account: None,
            }]),
            ..Default::default()
        };
        let changes = diff(&before, &after, &[], "s");
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].confidence, Confidence::Unknown);
    }
    #[test]
    fn failed_inventory_is_not_an_empty_snapshot() {
        let before = InventorySnapshot::default();
        let after = InventorySnapshot {
            services: Some(vec![]),
            ..Default::default()
        };
        assert!(diff(&before, &after, &[], "s").is_empty());
    }
}
