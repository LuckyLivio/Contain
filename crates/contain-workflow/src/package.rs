use crate::{
    artifact::{Artifact, pe_x64, safe_name, sha256},
    manifest::validate_step,
    report::{Check, Report, Status},
    runner::{self, Step},
};
use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::{fs, path::Path};
#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Launch {
    #[serde(default)]
    pub args: Vec<String>,
    pub timeout_ms: u64,
    pub expected_exit: Option<i32>,
    pub window_title: Option<String>,
}
#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PackageSpec {
    pub schema_version: u32,
    pub target: String,
    pub sha256: String,
    pub entry: String,
    #[serde(default)]
    pub comparison_entry: Option<String>,
    pub required_files: Vec<String>,
    pub launch: Option<Launch>,
    pub tests: Vec<Step>,
}
impl PackageSpec {
    pub fn read(path: &Path) -> Result<Self> {
        let s: Self = serde_json::from_slice(&fs::read(path)?)?;
        s.validate()?;
        Ok(s)
    }
    pub fn validate(&self) -> Result<()> {
        ensure!(
            self.schema_version == 1 && self.target == "windows-x64",
            "only schema 1 / windows-x64 supported"
        );
        ensure!(
            self.sha256.len() == 64 && self.sha256.bytes().all(|b| b.is_ascii_hexdigit()),
            "expected SHA-256 required"
        );
        ensure!(
            safe_name(&self.entry) && self.entry.to_ascii_lowercase().ends_with(".exe"),
            "invalid entry EXE"
        );
        ensure!(
            self.comparison_entry
                .as_ref()
                .is_none_or(|e| safe_name(e) && e.to_ascii_lowercase().ends_with(".exe")),
            "invalid comparison EXE"
        );
        ensure!(
            self.required_files.iter().all(|s| safe_name(s)),
            "invalid required file"
        );
        if let Some(l) = &self.launch {
            ensure!(
                l.expected_exit.is_some() != l.window_title.is_some(),
                "launch must specify exactly one expected exit or exact window title"
            );
            ensure!(
                (1..=3600000).contains(&l.timeout_ms)
                    && l.window_title.as_ref().is_none_or(|s| !s.is_empty()),
                "invalid launch expectation"
            );
        }
        for s in &self.tests {
            validate_step(s)?;
            ensure!(
                safe_name(&s.program),
                "package tests must execute an in-package relative path"
            );
        }
        Ok(())
    }
}
#[derive(Deserialize)]
struct Contents {
    schema_version: u32,
    source_sha: String,
    files: Vec<Member>,
}
#[derive(Deserialize)]
struct Member {
    path: String,
    bytes: u64,
    sha256: String,
}
fn contents(a: &Artifact) -> Result<()> {
    let Some(bytes) = a.files.get("CONTENTS.json") else {
        return Ok(());
    };
    let c: Contents = serde_json::from_slice(bytes)?;
    ensure!(
        c.schema_version == 1
            && c.source_sha.len() == 40
            && c.source_sha.bytes().all(|b| b.is_ascii_hexdigit()),
        "invalid CONTENTS source identity"
    );
    let mut seen = std::collections::BTreeSet::new();
    for row in c.files {
        ensure!(
            row.path != "CONTENTS.json" && seen.insert(row.path.clone()),
            "duplicate/self manifest member"
        );
        let data = a
            .files
            .get(&row.path)
            .ok_or_else(|| anyhow::anyhow!("manifest member missing"))?;
        ensure!(
            data.len() as u64 == row.bytes && sha256(data) == row.sha256,
            "manifest identity mismatch"
        );
    }
    ensure!(
        seen.len() + 1 == a.files.len(),
        "manifest membership mismatch"
    );
    Ok(())
}
pub fn comparison(a: &Artifact, b: &Artifact, entry: &str) -> Check {
    match (a.exe_hash(entry), b.exe_hash(entry)) {
        (Ok(left), Ok(right)) => Check::new(
            "comparison_identity",
            if left == right {
                Status::Failed
            } else {
                Status::Passed
            },
            format!(
                "entry {entry}; baseline EXE SHA-256 {left}; candidate EXE SHA-256 {right}; {}",
                if left == right {
                    "identical bytes; different-build comparison rejected before execution"
                } else {
                    "different bytes; this alone does not prove different behavior"
                }
            ),
        ),
        _ => Check::new(
            "comparison_identity",
            Status::Failed,
            "comparison entry missing; no execution allowed",
        ),
    }
}
pub fn compare(left: &Path, right: &Path, entry: &str) -> Report {
    let mut r = Report::new("compare");
    match (Artifact::read(left), Artifact::read(right)) {
        (Ok(a), Ok(b)) => {
            r.checks.push(Check::new(
                "package_identity",
                Status::Passed,
                format!(
                    "baseline ZIP {}; candidate ZIP {}",
                    a.zip_sha256, b.zip_sha256
                ),
            ));
            r.checks.push(comparison(&a, &b, entry));
        }
        _ => r.checks.push(Check::new(
            "comparison_identity",
            Status::Failed,
            "could not read safe, bounded ZIP inputs",
        )),
    }
    r
}
pub fn verify(
    zip: &Path,
    s: &PackageSpec,
    compare_to: Option<&Path>,
    run: bool,
    work: Option<&Path>,
) -> Report {
    let mut r = Report::new("package");
    let a = match Artifact::read(zip) {
        Ok(a) => Some(a),
        Err(_) => {
            r.checks.push(Check::new(
                "static_archive",
                Status::Failed,
                "ZIP unreadable, corrupt, unsafe or exceeds 1 GiB / 10000-member limits",
            ));
            None
        }
    };
    if let Some(a) = &a {
        r.checks.push(Check::new(
            "package_identity",
            if a.zip_sha256.eq_ignore_ascii_case(&s.sha256) {
                Status::Passed
            } else {
                Status::Failed
            },
            format!("actual ZIP SHA-256 {}; expected {}", a.zip_sha256, s.sha256),
        ));
        match a.files.get(&s.entry) {
            Some(data) => r.checks.push(Check::new(
                "static_entry",
                if pe_x64(data) {
                    Status::Passed
                } else {
                    Status::Failed
                },
                format!(
                    "{}; EXE SHA-256 {}; Windows x64 PE header checked (not a loader test)",
                    s.entry,
                    sha256(data)
                ),
            )),
            None => r.checks.push(Check::new(
                "static_entry",
                Status::Failed,
                "declared entry missing",
            )),
        }
        for name in &s.required_files {
            r.checks.push(Check::new(
                format!("static_file:{name}"),
                if a.files.contains_key(name) {
                    Status::Passed
                } else {
                    Status::Failed
                },
                "required package member",
            ));
        }
        for test in &s.tests {
            r.checks.push(Check::new(
                format!("static_test:{}", test.name),
                if a.files.contains_key(&test.program) {
                    Status::Passed
                } else {
                    Status::Failed
                },
                "test executable must be present in package",
            ));
        }
        if a.files.contains_key("CONTENTS.json") {
            r.checks.push(Check::new(
                "static_contents",
                if contents(a).is_ok() {
                    Status::Passed
                } else {
                    Status::Failed
                },
                "existing Contain CONTENTS.json membership, size and SHA-256 contract",
            ));
        }
        if let Some(other) = compare_to {
            r.checks.push(match Artifact::read(other) {
                Ok(b) => comparison(&b, a, s.comparison_entry.as_deref().unwrap_or(&s.entry)),
                Err(_) => Check::new(
                    "comparison_identity",
                    Status::Failed,
                    "baseline ZIP unavailable or unsafe",
                ),
            });
        }
    }
    let ready = r.passed() && a.is_some();
    let native_x64 =
        cfg!(all(windows, target_arch = "x86_64")) && crate::report::host_arch() == "x86_64";
    let can_run = run && native_x64 && ready;
    let mut extracted = None;
    if can_run {
        if let (Some(a), Some(work)) = (&a, work) {
            match extract(a, work) {
                Ok(()) => match fs::canonicalize(work) {
                    Ok(p) => extracted = Some(p),
                    Err(_) => r.checks.push(Check::new(
                        "extraction",
                        Status::Failed,
                        "cannot resolve work directory",
                    )),
                },
                Err(_) => r.checks.push(Check::new(
                    "extraction",
                    Status::Failed,
                    "requires a new writable work directory; existing files are preserved",
                )),
            }
        } else {
            r.checks.push(Check::new(
                "extraction",
                Status::Failed,
                "--run requires --work-dir pointing to a new directory",
            ));
        }
    }
    let reason = if !ready {
        "static/identity gate failed; execution blocked"
    } else if !run {
        "--run not requested"
    } else if !native_x64 {
        "requires actual Windows x64"
    } else {
        "extraction unavailable"
    };
    let launch = if let (Some(root), Some(l)) = (&extracted, &s.launch) {
        let exe = root.join(&s.entry);
        if let Some(title) = &l.window_title {
            runner::window_launch(&exe, &l.args, title, l.timeout_ms, root)
        } else {
            runner::execute(
                &Step {
                    name: "launch".into(),
                    program: exe.to_string_lossy().into(),
                    args: l.args.clone(),
                    expected_exit: l.expected_exit.unwrap(),
                    timeout_ms: l.timeout_ms,
                },
                root,
            )
        }
    } else {
        Check::new(
            "launch",
            Status::NotRun,
            if s.launch.is_none() {
                "no launch expectation specified"
            } else {
                reason
            },
        )
    };
    r.checks.push(launch);
    if s.tests.is_empty() {
        r.checks.push(Check::new(
            "specified_tests",
            Status::NotRun,
            "no tests specified",
        ));
    }
    for test in &s.tests {
        r.checks.push(if let Some(root) = &extracted {
            let mut test = test.clone();
            test.program = root.join(&test.program).to_string_lossy().into();
            runner::execute(&test, root)
        } else {
            Check::new(&test.name, Status::NotRun, reason)
        });
    }
    r
}
fn extract(a: &Artifact, root: &Path) -> Result<()> {
    // create_dir (not create_dir_all) refuses existing roots, links and junctions.
    fs::create_dir(root)?;
    for (name, data) in &a.files {
        let path = root.join(name);
        if let Some(p) = path.parent() {
            fs::create_dir_all(p)?;
        }
        use std::io::Write;
        fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(path)?
            .write_all(data)?;
    }
    Ok(())
}
