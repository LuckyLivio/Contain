use crate::{manifest::WindowsToolchain, runner};
use serde::Deserialize;
use std::{collections::BTreeMap, path::Path};
#[derive(Deserialize)]
pub struct Selection {
    pub msvc: String,
    pub sdk: String,
    pub environment: BTreeMap<String, String>,
}
#[derive(Deserialize)]
struct Reply {
    status: String,
    msvc: Option<String>,
    sdk: Option<String>,
    environment: Option<BTreeMap<String, String>>,
    reason: Option<String>,
}
pub fn select(root: &Path, required: &WindowsToolchain) -> Result<Selection, String> {
    let args = vec![
        "-NoLogo".into(),
        "-NoProfile".into(),
        "-NonInteractive".into(),
        "-File".into(),
        "scripts/select-workflow-toolchain.ps1".into(),
        "-Family".into(),
        required.msvc_family.clone(),
        "-Sdk".into(),
        required.sdk_version.clone(),
    ];
    let output = runner::probe_with_timeout(
        "powershell.exe",
        &args,
        Some(root),
        &BTreeMap::new(),
        std::time::Duration::from_secs(75),
    )
    .map_err(|_| "Windows toolchain selector unavailable".to_string())?;
    validate(&output, required)
}
fn validate(output: &str, required: &WindowsToolchain) -> Result<Selection, String> {
    let reply: Reply = serde_json::from_str(output.trim_start_matches('\u{feff}'))
        .map_err(|_| "toolchain selection reply invalid".to_string())?;
    if reply.status != "passed" {
        let reason = reply
            .reason
            .filter(|s| !s.is_empty() && s.chars().all(|c| c.is_ascii_lowercase() || c == '_'))
            .unwrap_or("toolchain_selection_failed".into());
        return Err(reason);
    }
    let selection = Selection {
        msvc: reply.msvc.unwrap_or_default(),
        sdk: reply.sdk.unwrap_or_default(),
        environment: reply.environment.unwrap_or_default(),
    };
    if !selection
        .msvc
        .starts_with(&(required.msvc_family.clone() + "."))
        || selection.sdk != required.sdk_version
        || selection
            .environment
            .get("VCToolsVersion")
            .map(|s| s.trim_end_matches('\\'))
            != Some(&*selection.msvc)
        || selection
            .environment
            .get("WindowsSDKVersion")
            .map(|s| s.trim_end_matches('\\'))
            != Some(&*selection.sdk)
    {
        return Err("selected_toolchain_conflict".into());
    }
    Ok(selection)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn wrong_actual_selection_rejected_even_if_installed() {
        let r = WindowsToolchain {
            msvc_family: "14.44".into(),
            sdk_version: "10.0.26100.0".into(),
        };
        for (msvc, sdk) in [("14.45.1", "10.0.26100.0"), ("14.44.1", "10.0.22621.0")] {
            let text=serde_json::json!({"status":"passed","msvc":msvc,"sdk":sdk,"environment":{"VCToolsVersion":msvc,"WindowsSDKVersion":sdk}}).to_string();
            assert!(validate(&text, &r).is_err());
        }
        let text=serde_json::json!({"status":"passed","msvc":"14.44.1","sdk":"10.0.26100.0","environment":{"VCToolsVersion":"14.44.1","WindowsSDKVersion":"10.0.26100.0\\"}}).to_string();
        assert!(validate(&text, &r).is_ok());
    }
}
