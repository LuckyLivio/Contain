use crate::{
    manifest::Manifest,
    report::{Check, Report, Status},
    runner,
};
use std::path::Path;
pub fn check(m: &Manifest, root: &Path) -> Report {
    prepare(m, root).0
}
fn prepare(m: &Manifest, root: &Path) -> (Report, std::collections::BTreeMap<String, String>) {
    let mut environment = std::collections::BTreeMap::new();
    let selection = m
        .windows_toolchain
        .as_ref()
        .map(|requirement| crate::toolchain::select(root, requirement));
    if let Some(Ok(selected)) = &selection {
        environment = selected.environment.clone();
    }
    let mut report = check_with(
        m,
        std::env::consts::OS,
        &crate::report::host_arch(),
        |program, args| runner::probe_in_environment(program, args, Some(root), &environment),
        |name| std::env::var_os(name).is_some_and(|v| !v.is_empty()),
    );
    report.checks.push(Check::new(
        "environment_manifest",
        Status::Passed,
        format!(
            "canonical JSON SHA-256 {}",
            crate::artifact::sha256(&serde_json::to_vec(m).expect("serializable manifest"))
        ),
    ));
    if let Ok(head) = runner::probe_in("git", &["rev-parse".into(), "HEAD".into()], Some(root)) {
        let head = head.trim();
        if head.len() == 40 && head.bytes().all(|b| b.is_ascii_hexdigit()) {
            let clean =
                runner::probe_in("git", &["status".into(), "--porcelain".into()], Some(root))
                    .ok()
                    .map(|s| s.is_empty());
            report.checks.push(Check::new("source_revision", Status::Passed, format!("Git HEAD {head}; worktree clean {clean:?}; HEAD does not identify uncommitted content")));
        }
    }
    if let Some(selection) = selection {
        report.checks.push(match selection {
            Ok(selected)=>Check::new("selected_windows_toolchain",Status::Passed,format!("MSVC {}; Windows SDK {}; validated cl/link/rc resolution; build children inherit selected environment",selected.msvc,selected.sdk)),
            Err(reason)=>Check::new("selected_windows_toolchain",Status::Failed,format!("{reason}; install required MSVC/SDK or use a fresh shell to resolve active-version conflicts")),
        });
    }
    (report, environment)
}
pub fn check_with(
    m: &Manifest,
    os: &str,
    arch: &str,
    mut probe: impl FnMut(&str, &[String]) -> anyhow::Result<String>,
    has_variable: impl Fn(&str) -> bool,
) -> Report {
    let mut r = Report::new("source");
    r.checks.push(Check::new(
        "platform",
        if os == "windows" && arch == "x86_64" {
            Status::Passed
        } else {
            Status::Failed
        },
        "requires Windows x64",
    ));
    for t in &m.tools {
        let (status, outcome) = match probe(&t.program, &t.version_args) {
            Ok(output) if output.split_whitespace().any(|s| s == t.version) => {
                (Status::Passed, "version matched")
            }
            Ok(_) => (Status::Failed, "version mismatch"),
            Err(_) => (Status::Failed, "tool unavailable or version probe failed"),
        };
        r.checks.push(Check::new(
            format!("tool:{}", t.program),
            status,
            format!("{outcome}; required {}; remedy: {}", t.version, t.remedy),
        ));
    }
    for name in &m.required_variables {
        r.checks.push(Check::new(
            format!("variable:{name}"),
            if has_variable(name) {
                Status::Passed
            } else {
                Status::Failed
            },
            "presence/nonempty only; value never recorded",
        ));
    }
    for pending in &m.pending {
        r.checks.push(Check::new(
            "requirement",
            Status::NeedsConfirmation,
            pending,
        ));
    }
    if m.build.is_empty() {
        r.checks.push(Check::new(
            "build plan",
            Status::NeedsConfirmation,
            "no build command declared",
        ));
    }
    r
}
pub fn build(m: &Manifest, root: &Path, execute: bool) -> Report {
    build_to(m, root, execute, None)
}
pub fn build_to(m: &Manifest, root: &Path, execute: bool, delivery: Option<&Path>) -> Report {
    let (report, mut environment) = prepare(m, root);
    if let Some(path) = delivery {
        environment.insert(
            "CONTAIN_DELIVERY_DIR".into(),
            std::path::absolute(path)
                .unwrap_or_else(|_| path.to_path_buf())
                .to_string_lossy()
                .into(),
        );
    }
    execute_build_in(m, root, execute, report, &environment)
}
/// Separated from probing so gate transitions can exercise real child processes in fixtures.
pub fn execute_build(m: &Manifest, root: &Path, execute: bool, r: Report) -> Report {
    execute_build_in(m, root, execute, r, &std::collections::BTreeMap::new())
}
fn execute_build_in(
    m: &Manifest,
    root: &Path,
    execute: bool,
    mut r: Report,
    environment: &std::collections::BTreeMap<String, String>,
) -> Report {
    let ready = r.passed();
    let mut building = execute && ready;
    for s in &m.build {
        let check = if building {
            runner::execute_in(s, root, environment)
        } else {
            Check::new(
                &s.name,
                Status::NotRun,
                "build requires --execute, passing requirements and successful preceding build steps",
            )
        };
        building &= check.status == Status::Passed;
        r.checks.push(check);
    }
    let built = execute && ready && r.passed();
    let mut verified = built;
    for s in &m.verify {
        let c = if verified {
            runner::execute_in(s, root, environment)
        } else {
            Check::new(
                &s.name,
                Status::NotRun,
                "requires successful build and preceding verification",
            )
        };
        verified &= c.status == Status::Passed;
        r.checks.push(c);
    }
    r
}
