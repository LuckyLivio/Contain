use crate::runner::Step;
use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::{fs, path::Path};
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Tool {
    pub program: String,
    pub version: String,
    pub version_args: Vec<String>,
    pub remedy: String,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct WindowsToolchain {
    pub msvc_family: String,
    pub sdk_version: String,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Manifest {
    pub schema_version: u32,
    pub target: String,
    #[serde(default)]
    pub windows_toolchain: Option<WindowsToolchain>,
    pub tools: Vec<Tool>,
    /// Names only. Secret values must never be serialized.
    pub required_variables: Vec<String>,
    pub build: Vec<Step>,
    pub verify: Vec<Step>,
    #[serde(default)]
    pub pending: Vec<String>,
}
impl Manifest {
    pub fn read(path: &Path) -> Result<Self> {
        let m: Self = serde_json::from_slice(&fs::read(path)?)?;
        m.validate()?;
        Ok(m)
    }
    pub fn validate(&self) -> Result<()> {
        ensure!(
            self.schema_version == 1 && self.target == "windows-x64",
            "only schema 1 / windows-x64 supported"
        );
        if let Some(t) = &self.windows_toolchain {
            ensure!(
                t.msvc_family.split('.').count() == 2
                    && t.sdk_version.split('.').count() == 4
                    && t.msvc_family
                        .chars()
                        .chain(t.sdk_version.chars())
                        .all(|c| c.is_ascii_digit() || c == '.'),
                "invalid Windows toolchain versions"
            );
        }
        if let Some(t) = &self.windows_toolchain {
            ensure!(
                t.msvc_family
                    .split('.')
                    .chain(t.sdk_version.split('.'))
                    .all(|v| !v.is_empty() && v.bytes().all(|c| c.is_ascii_digit())),
                "invalid Windows toolchain numeric components"
            );
        }
        for v in &self.required_variables {
            ensure!(
                valid_variable(v),
                "required_variables must contain names only"
            );
        }
        for t in &self.tools {
            ensure!(
                !t.program.is_empty() && !t.version.is_empty() && !t.remedy.is_empty(),
                "incomplete tool requirement"
            );
            ensure!(
                t.version
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || "._-+".contains(c)),
                "version must be a literal token"
            );
        }
        for s in self.build.iter().chain(&self.verify) {
            validate_step(s)?;
        }
        Ok(())
    }
}
pub fn validate_step(s: &Step) -> Result<()> {
    ensure!(
        s.stdout_sha256
            .as_ref()
            .is_none_or(|v| v.len() == 64 && v.bytes().all(|b| b.is_ascii_hexdigit())),
        "invalid stdout SHA-256"
    );
    ensure!(
        !s.name.is_empty() && !s.program.is_empty() && (1..=3_600_000).contains(&s.timeout_ms),
        "invalid command or timeout (1..3600000 ms)"
    );
    Ok(())
}
fn valid_variable(s: &str) -> bool {
    let mut chars = s.chars();
    chars
        .next()
        .is_some_and(|c| c.is_ascii_alphabetic() || c == '_')
        && chars.all(|c| c.is_ascii_alphanumeric() || c == '_')
}
/// Intentionally bounded discovery: only root Cargo.toml, rust-toolchain.toml and global.json.
/// A draft cannot execute until every unknown has been explicitly resolved.
pub fn infer(root: &Path) -> Result<Manifest> {
    let mut m = Manifest { schema_version:1, target:"windows-x64".into(), windows_toolchain:None, tools:vec![], required_variables:vec![], build:vec![], verify:vec![],
        pending:vec!["Confirm Windows x64 target, native SDK/MSVC prerequisites and required variable names; these cannot be inferred from supported files".into()] };
    let cargo = root.join("Cargo.toml");
    if cargo.exists() {
        let value: toml::Value = toml::from_str(&fs::read_to_string(cargo)?)?;
        if let Some(version) = value.get("package").and_then(|p| p.get("rust-version")) {
            m.pending.push(format!(
                "Cargo rust-version {version:?} is a minimum, not an exact installed toolchain"
            ));
        }
        m.build.push(step(
            "Rust workspace",
            "cargo",
            &["build", "--workspace", "--locked"],
        ));
        m.verify.push(step(
            "Rust tests",
            "cargo",
            &["test", "--workspace", "--locked"],
        ));
        m.pending
            .push("Confirm workspace build targets/features and system dependencies".into());
    }
    let rust = root.join("rust-toolchain.toml");
    if rust.exists() {
        let value: toml::Value = toml::from_str(&fs::read_to_string(rust)?)?;
        let t = value.get("toolchain");
        if let Some(channel) = t.and_then(|t| t.get("channel")).and_then(|c| c.as_str()) {
            if channel.split('.').count() == 3
                && channel.chars().all(|c| c.is_ascii_digit() || c == '.')
            {
                m.tools.push(Tool {
                    program: "rustc".into(),
                    version: channel.into(),
                    version_args: vec!["--version".into()],
                    remedy: format!(
                        "rustup toolchain install {channel}; select this toolchain in the project"
                    ),
                });
            } else {
                m.pending.push(format!("rust-toolchain.toml channel {channel} is not an exact version; choose and verify a pin"));
            }
        } else {
            m.pending
                .push("rust-toolchain.toml has no declared channel".into());
        }
        if let Some(t) = t {
            for field in ["components", "targets"] {
                if let Some(v) = t.get(field) {
                    m.pending
                        .push(format!("Confirm rust-toolchain {field}: {v:?}"));
                }
            }
        }
    } else if root.join("Cargo.toml").exists() {
        m.pending.push("No exact Rust toolchain declaration".into());
    }
    let global = root.join("global.json");
    if global.exists() {
        let value: serde_json::Value = serde_json::from_slice(&fs::read(global)?)?;
        if let Some(v) = value.pointer("/sdk/version").and_then(|v| v.as_str()) {
            m.tools.push(Tool {
                program: "dotnet".into(),
                version: v.into(),
                version_args: vec!["--version".into()],
                remedy: format!("Install .NET SDK {v} from https://dotnet.microsoft.com/download"),
            });
            m.pending.push("Confirm .NET project path, runtime/Windows Desktop versions, restore and publish commands; global.json only declares the SDK".into());
        } else {
            m.pending.push("global.json has no sdk.version".into());
        }
    }
    if m.tools.is_empty() && m.build.is_empty() {
        m.pending.push("No supported declared build requirements found; other project formats are unsupported in phase one".into());
    }
    m.validate()?;
    Ok(m)
}
fn step(name: &str, program: &str, args: &[&str]) -> Step {
    Step {
        name: name.into(),
        program: program.into(),
        args: args.iter().map(|s| (*s).into()).collect(),
        expected_exit: 0,
        stdout_sha256: None,
        timeout_ms: 600000,
    }
}
