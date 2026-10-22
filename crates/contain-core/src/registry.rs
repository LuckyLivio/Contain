use crate::model::{Confidence, RegistryChange, StateObservation};
use anyhow::{Context, Result};
use std::collections::BTreeMap;
use std::io::ErrorKind;
use winreg::RegKey;
use winreg::enums::HKEY_CURRENT_USER;

pub type RegistrySnapshot = BTreeMap<String, String>;

pub struct ObservedRegistrySnapshot {
    pub values: RegistrySnapshot,
    pub observations: BTreeMap<String, StateObservation>,
}

pub fn change_observation(
    change: &RegistryChange,
    observations: Option<&BTreeMap<String, StateObservation>>,
) -> StateObservation {
    observations.and_then(|reads| reads.get(&change.name)).cloned().unwrap_or_else(|| StateObservation {
        method: if change.operation == "deleted" { "registry_after_absence_unproven" } else { "registry_read_unavailable" }.into(),
        timestamp_domain: "wall_filetime_100ns_diagnostic_only".into(),
        failure: Some("No after-value read is available. Absence from unlocked enumeration is not an atomic absence observation and does not prove deletion or its actor.".into()),
        ..Default::default()
    })
}

/// These bounds describe unlocked reads, not an atomic snapshot. Re-reading can
/// detect a changed value but cannot exclude an intervening write/restore (ABA).
/// No comparison with converted ETW FILETIME timestamps is used as a proof.
pub fn observed_snapshot(key: &str) -> Result<ObservedRegistrySnapshot> {
    validate_key(key)?;
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let subkey = match root.open_subkey(key) {
        Ok(subkey) => subkey,
        Err(error) if error.kind() == ErrorKind::NotFound => {
            return Ok(ObservedRegistrySnapshot {
                values: BTreeMap::new(),
                observations: BTreeMap::new(),
            });
        }
        Err(error) => return Err(error).with_context(|| format!("opening HKCU\\{key}")),
    };
    let mut result = ObservedRegistrySnapshot {
        values: BTreeMap::new(),
        observations: BTreeMap::new(),
    };
    let mut entries = subkey.enum_values();
    loop {
        let started = crate::windows::native::now_ticks();
        let Some(entry) = entries.next() else { break };
        let (name, value) = entry?;
        let agrees = subkey
            .get_raw_value(&name)
            .is_ok_and(|again| again == value);
        result.observations.insert(name.clone(), StateObservation {
            method: "registry_value_double_read_unlocked".into(),
            timestamp_domain: "wall_filetime_100ns_diagnostic_only".into(),
            started_ticks: Some(started),
            finished_ticks: Some(crate::windows::native::now_ticks()),
            failure: Some(if agrees {
                "Equal unlocked reads do not exclude concurrent write/restore (ABA); no common source-clock interval or registry exclusion proof is available."
            } else {
                "The value changed or became unreadable during the unlocked observation window."
            }.into()),
            ..Default::default()
        });
        result.values.insert(name, encode_value(&value));
    }
    Ok(result)
}

fn encode_value(value: &winreg::RegValue) -> String {
    format!(
        "{:?}:{}",
        value.vtype,
        value
            .bytes
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect::<String>()
    )
}

pub fn validate_key(key: &str) -> Result<()> {
    let components: Vec<_> = key.split('\u{5c}').collect();
    if components.len() < 2
        || !components[0].eq_ignore_ascii_case("Software")
        || components
            .iter()
            .any(|part| part.is_empty() || *part == "." || *part == "..")
    {
        anyhow::bail!(
            "registry key must be a subkey of HKCU\\Software, for example Software\\Contain\\Demo"
        );
    }
    Ok(())
}

pub fn snapshot(key: &str) -> Result<RegistrySnapshot> {
    validate_key(key)?;
    let root = RegKey::predef(HKEY_CURRENT_USER);
    let subkey = match root.open_subkey(key) {
        Ok(subkey) => subkey,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(BTreeMap::new()),
        Err(error) => return Err(error).with_context(|| format!("opening HKCU\\{key}")),
    };
    let mut values = BTreeMap::new();
    for entry in subkey.enum_values() {
        let (name, value) = entry?;
        // A lossless encoding avoids interpreting arbitrary registry value types as text.
        let encoded = encode_value(&value);
        values.insert(name, encoded);
    }
    Ok(values)
}

pub fn diff(key: &str, before: &RegistrySnapshot, after: &RegistrySnapshot) -> Vec<RegistryChange> {
    let mut names: Vec<_> = before.keys().chain(after.keys()).collect();
    names.sort();
    names.dedup();
    names
        .into_iter()
        .filter_map(|name| {
            let old = before.get(name);
            let new = after.get(name);
            if old == new {
                return None;
            }
            Some(RegistryChange {
                key: format!("HKCU\\{key}"),
                name: name.clone(),
                operation: if old.is_none() {
                    "created"
                } else if new.is_none() {
                    "deleted"
                } else {
                    "modified"
                }
                .into(),
                before_value: old.cloned(),
                after_value: new.cloned(),
                confidence: Confidence::Unknown,
                reason: "Scoped registry values changed during session; writer process is unknown."
                    .into(),
                evidence: vec![],
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn limits_scope() {
        assert!(validate_key("Software\\Contain\\Demo").is_ok());
        assert!(validate_key("SYSTEM\\ControlSet001").is_err());
        assert!(validate_key("Software\\..\\Run").is_err());
    }

    #[test]
    fn equal_live_registry_reads_are_diagnostic_not_an_atomic_value_proof() {
        let name = format!("Software\\Contain\\SnapshotTest\\{}", uuid::Uuid::new_v4());
        let root = RegKey::predef(HKEY_CURRENT_USER);
        let (key, _) = root.create_subkey(&name).unwrap();
        key.set_value("value", &"stable during this test").unwrap();
        let read = observed_snapshot(&name).unwrap();
        let ordinary = snapshot(&name).unwrap();
        drop(key);
        // Delete only the exact unique test key created above.
        root.delete_subkey_all(&name).unwrap();
        assert_eq!(read.values, ordinary);
        let observation = &read.observations["value"];
        assert!(observation.started_ticks.is_some() && observation.finished_ticks.is_some());
        assert!(!observation.content_stable);
        assert!(!observation.mutation_coverage_complete);
        assert!(!observation.write_delete_excluded);
        assert!(observation.failure.as_ref().unwrap().contains("ABA"));
    }

    #[test]
    fn deleted_value_and_missing_sid_retain_read_limitations_without_source_events() {
        let mut deleted = RegistryChange {
            name: "gone".into(),
            operation: "deleted".into(),
            ..Default::default()
        };
        let proof = change_observation(&deleted, Some(&BTreeMap::new()));
        assert_eq!(proof.method, "registry_after_absence_unproven");
        crate::attribution::compose_registry_observed(
            &mut deleted,
            &mut [],
            None,
            Some(proof.clone()),
        );
        assert_eq!(deleted.confidence, Confidence::Unknown);
        assert_eq!(deleted.evidence[0].observation.as_ref(), Some(&proof));

        let mut created = RegistryChange {
            name: "new".into(),
            operation: "created".into(),
            ..Default::default()
        };
        let reads = BTreeMap::from([(
            "new".into(),
            StateObservation {
                method: "registry_value_double_read_unlocked".into(),
                started_ticks: Some(123),
                ..Default::default()
            },
        )]);
        let proof = change_observation(&created, Some(&reads));
        crate::attribution::compose_registry_observed(
            &mut created,
            &mut [],
            None,
            Some(proof.clone()),
        );
        assert_eq!(created.confidence, Confidence::Unknown);
        assert_eq!(created.evidence[0].observation.as_ref(), Some(&proof));
    }
}
