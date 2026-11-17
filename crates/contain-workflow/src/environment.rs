use crate::{
    manifest::Manifest,
    report::{Check, Report, Status},
    runner,
};
use std::path::Path;

#[derive(Debug, Clone, Default)]
pub struct CheckOptions {
    pub safe_probes: bool,
    pub manifest_source: Option<String>,
}

pub fn is_supported_probe(program: &str, args: &[String]) -> bool {
    let arg_strs: Vec<&str> = args.iter().map(String::as_str).collect();
    matches!(
        (program, arg_strs.as_slice()),
        (
            "rustc" | "cargo" | "git" | "dotnet" | "python",
            ["--version"]
        ) | ("python", ["-V"])
            | ("rustup", ["--version"])
            | ("rustup", ["target", "list", "--installed"])
            | ("rustup", ["component", "list", "--installed"])
            | ("rustup", ["toolchain", "list"])
            | ("dotnet", ["--list-sdks" | "--list-runtimes"])
    )
}

/// Extract only a bounded numeric-headed version token (never paths, identifiers, or secrets).
pub fn extract_safe_version_token(args: &[String], output: &str) -> Option<String> {
    if !matches!(
        args.iter()
            .map(String::as_str)
            .collect::<Vec<_>>()
            .as_slice(),
        ["--version"] | ["-V"]
    ) {
        return None;
    }
    let first_line = output.lines().find(|l| !l.trim().is_empty())?;
    for raw in first_line.split_whitespace() {
        let token = raw
            .trim_end_matches([',', ';', ')'])
            .trim_start_matches('(');
        if token.is_empty() || token.len() > 48 {
            continue;
        }
        let mut parts = token.split('.');
        let (Some(major), Some(minor)) = (parts.next(), parts.next()) else {
            continue;
        };
        if !major.is_empty()
            && major.bytes().all(|b| b.is_ascii_digit())
            && !minor.is_empty()
            && minor
                .bytes()
                .take_while(|b| *b != b'-' && *b != b'+')
                .count()
                > 0
            && minor
                .bytes()
                .take_while(|b| *b != b'-' && *b != b'+')
                .all(|b| b.is_ascii_digit())
            && token
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || ".-+".contains(c))
        {
            return Some(token.into());
        }
    }
    None
}

pub fn check(m: &Manifest, root: &Path) -> Report {
    check_with_options(m, root, CheckOptions::default())
}

pub fn check_with_options(m: &Manifest, root: &Path, options: CheckOptions) -> Report {
    prepare(m, root, options).0
}

fn prepare(
    m: &Manifest,
    root: &Path,
    options: CheckOptions,
) -> (Report, std::collections::BTreeMap<String, String>) {
    let mut environment = std::collections::BTreeMap::new();
    let selection = m.windows_toolchain.as_ref().map(|requirement| {
        (
            requirement,
            crate::toolchain::select_with_policy(root, requirement, options.safe_probes),
        )
    });
    if let Some((_, Ok(selected))) = &selection {
        environment = selected.environment.clone();
    }
    let mut report = check_with_policy(
        m,
        std::env::consts::OS,
        &crate::report::host_arch(),
        options.safe_probes,
        |program, args| runner::probe_in_environment(program, args, Some(root), &environment),
        |name| std::env::var_os(name).is_some_and(|v| !v.is_empty()),
    );
    report.manifest_source = options.manifest_source;
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
    if let Some((requirement, result)) = selection {
        let required_text = format!(
            "MSVC {}.*, Windows SDK {}",
            requirement.msvc_family, requirement.sdk_version
        );
        report.checks.push(match result {
            Ok(selected) => {
                let observed_text =
                    format!("MSVC {}, Windows SDK {}", selected.msvc, selected.sdk);
                Check::new(
                    "selected_windows_toolchain",
                    Status::Passed,
                    format!(
                        "MSVC {}; Windows SDK {}; validated cl/link/rc resolution; build children inherit selected environment",
                        selected.msvc, selected.sdk
                    ),
                )
                .with_evidence(
                    Some("toolchain_selected".into()),
                    Some(required_text),
                    Some(observed_text),
                    None,
                )
            }
            Err(reason) => {
                let remedy_text = format!(
                    "Install MSVC {} x64 build tools and Windows SDK {}; open a fresh shell if active versions conflict",
                    requirement.msvc_family, requirement.sdk_version
                );
                let status = if reason == "bundled_toolchain_selector_missing" {
                    Status::NeedsConfirmation
                } else {
                    Status::Failed
                };
                Check::new(
                    "selected_windows_toolchain",
                    status,
                    format!(
                        "{reason}; install required MSVC/SDK or use a fresh shell to resolve active-version conflicts"
                    ),
                )
                .with_evidence(Some(reason), Some(required_text), None, Some(remedy_text))
            }
        });
    }
    (report, environment)
}

pub fn check_with(
    m: &Manifest,
    os: &str,
    arch: &str,
    probe: impl FnMut(&str, &[String]) -> anyhow::Result<String>,
    has_variable: impl Fn(&str) -> bool,
) -> Report {
    check_with_policy(m, os, arch, false, probe, has_variable)
}

pub fn check_with_policy(
    m: &Manifest,
    os: &str,
    arch: &str,
    safe_probes: bool,
    mut probe: impl FnMut(&str, &[String]) -> anyhow::Result<String>,
    has_variable: impl Fn(&str) -> bool,
) -> Report {
    let mut r = Report::new("source");
    let platform_ok = os == "windows" && arch == "x86_64";
    r.checks.push(
        Check::new(
            "platform",
            if platform_ok {
                Status::Passed
            } else {
                Status::Failed
            },
            "requires Windows x64",
        )
        .with_evidence(
            Some(
                if platform_ok {
                    "matched"
                } else {
                    "platform_mismatch"
                }
                .into(),
            ),
            Some("windows / x86_64".into()),
            Some(format!("{os} / {arch}")),
            if platform_ok {
                None
            } else {
                Some("Run on native 64-bit Windows x64".into())
            },
        ),
    );
    for t in &m.tools {
        if safe_probes && !is_supported_probe(&t.program, &t.version_args) {
            r.checks.push(
                Check::new(
                    format!("tool:{}", t.program),
                    Status::NeedsConfirmation,
                    format!(
                        "unsupported probe skipped in safe mode; required {}; remedy: {}",
                        t.version, t.remedy
                    ),
                )
                .with_evidence(
                    Some("unsupported_probe".into()),
                    Some(t.version.clone()),
                    None,
                    Some(t.remedy.clone()),
                ),
            );
            continue;
        }
        let (status, outcome_label, outcome_code, observed) =
            match probe(&t.program, &t.version_args) {
                Ok(output) if output.split_whitespace().any(|s| s == t.version) => (
                    Status::Passed,
                    "version matched",
                    "version_matched",
                    Some(t.version.clone()),
                ),
                Ok(output) => (
                    Status::Failed,
                    "version mismatch",
                    "version_mismatch",
                    extract_safe_version_token(&t.version_args, &output),
                ),
                Err(_) => (
                    Status::Failed,
                    "tool unavailable or version probe failed",
                    "unavailable",
                    None,
                ),
            };
        r.checks.push(
            Check::new(
                format!("tool:{}", t.program),
                status,
                format!(
                    "{outcome_label}; required {}; remedy: {}",
                    t.version, t.remedy
                ),
            )
            .with_evidence(
                Some(outcome_code.into()),
                Some(t.version.clone()),
                observed,
                Some(t.remedy.clone()),
            ),
        );
    }
    for name in &m.required_variables {
        let present = has_variable(name);
        r.checks.push(
            Check::new(
                format!("variable:{name}"),
                if present {
                    Status::Passed
                } else {
                    Status::Failed
                },
                "presence/nonempty only; value never recorded",
            )
            .with_evidence(
                Some(
                    if present {
                        "variable_present"
                    } else {
                        "variable_missing"
                    }
                    .into(),
                ),
                Some(name.clone()),
                None,
                if present {
                    None
                } else {
                    Some(format!("Set required environment variable {name}"))
                },
            ),
        );
    }
    for pending in &m.pending {
        r.checks.push(
            Check::new("requirement", Status::NeedsConfirmation, pending).with_evidence(
                Some("pending_confirmation".into()),
                None,
                None,
                Some(pending.clone()),
            ),
        );
    }
    if m.build.is_empty() {
        r.checks.push(
            Check::new(
                "build plan",
                Status::NeedsConfirmation,
                "no build command declared",
            )
            .with_evidence(
                Some("no_build_plan".into()),
                None,
                None,
                Some("Declare reviewed build steps in contain.environment.json".into()),
            ),
        );
    }
    r
}
pub fn build(m: &Manifest, root: &Path, execute: bool) -> Report {
    build_to(m, root, execute, None)
}
pub fn build_to(m: &Manifest, root: &Path, execute: bool, delivery: Option<&Path>) -> Report {
    let (report, mut environment) = prepare(m, root, CheckOptions::default());
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
