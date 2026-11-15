use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::{fs, path::Path};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Status {
    Passed,
    Failed,
    NotRun,
    NeedsConfirmation,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Check {
    pub name: String,
    pub status: Status,
    pub detail: String,
}
impl Check {
    pub fn new(name: impl Into<String>, status: Status, detail: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            status,
            detail: detail.into(),
        }
    }
}
#[derive(Debug, Serialize, Deserialize)]
pub struct Report {
    pub schema_version: u32,
    pub workflow: String,
    pub host_os: String,
    pub host_arch: String,
    pub process_arch: String,
    pub os_version: Option<String>,
    #[serde(default)]
    pub architecture: Option<crate::architecture::Architecture>,
    pub checks: Vec<Check>,
}
impl Report {
    pub fn new(workflow: &str) -> Self {
        let architecture = crate::architecture::detect();
        Self {
            schema_version: 1,
            workflow: workflow.into(),
            host_os: std::env::consts::OS.into(),
            host_arch: architecture.native.clone(),
            process_arch: architecture.process.clone(),
            architecture: Some(architecture),
            os_version: windows_version(),
            checks: vec![],
        }
    }
    pub fn passed(&self) -> bool {
        self.checks.iter().all(|c| c.status == Status::Passed)
    }
    pub fn write(&self, directory: &Path) -> Result<()> {
        ensure!(
            !directory.exists(),
            "report directory already exists; use a new directory"
        );
        if let Some(parent) = directory.parent().filter(|p| !p.as_os_str().is_empty()) {
            fs::create_dir_all(parent)?;
        }
        fs::create_dir(directory)?;
        fs::write(
            directory.join("report.json"),
            serde_json::to_vec_pretty(self)?,
        )?;
        let mut text = format!(
            "# Contain {}\n\nPlatform: {} / {}\n\n",
            self.workflow, self.host_os, self.host_arch
        );
        text.push_str(&format!("Process architecture: {}\n\n", self.process_arch));
        if let Some(a) = &self.architecture {
            text.push_str(&format!(
                "Architecture probe: {}; emulated: {:?}\n\n",
                a.probe, a.emulated
            ));
        }
        if let Some(v) = &self.os_version {
            text.push_str(&format!("Windows: {v}\n\n"));
        }
        for c in &self.checks {
            let status = match c.status {
                Status::Passed => "通过",
                Status::Failed => "失败",
                Status::NotRun => "未实测",
                Status::NeedsConfirmation => "待确认",
            };
            text.push_str(&format!(
                "- {} — {}: {}\n",
                c.name.replace(['\n', '\r'], " "),
                status,
                c.detail.replace(['\n', '\r'], " ")
            ));
        }
        fs::write(directory.join("report.md"), text)?;
        Ok(())
    }
}
fn windows_version() -> Option<String> {
    #[cfg(windows)]
    {
        crate::runner::probe("cmd.exe", &["/D".into(), "/C".into(), "ver".into()])
            .ok()
            .map(|s| s.trim().into())
    }
    #[cfg(not(windows))]
    {
        None
    }
}
pub fn host_arch() -> String {
    crate::architecture::detect().native
}
