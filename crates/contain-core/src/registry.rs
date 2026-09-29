use crate::model::{Confidence, RegistryChange};
use anyhow::{Context, Result};
use std::collections::BTreeMap;
use std::io::ErrorKind;
use winreg::RegKey;
use winreg::enums::HKEY_CURRENT_USER;

pub type RegistrySnapshot = BTreeMap<String, String>;

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
        let encoded = format!(
            "{:?}:{}",
            value.vtype,
            value
                .bytes
                .iter()
                .map(|b| format!("{b:02x}"))
                .collect::<String>()
        );
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
}
