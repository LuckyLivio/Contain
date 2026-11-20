use anyhow::{Result, ensure};
use clap::{Parser, Subcommand};
use contain_workflow::{
    dotnet_onboarding, environment,
    manifest::{self, Manifest},
    package::{self, PackageSpec},
    project_installer,
    report::{Report, Status},
};
use std::{fs, io::Write, path::PathBuf};
#[derive(Parser)]
#[command(
    version,
    about = "Recover source environments and verify actual Windows x64 ZIPs; independent of ETW"
)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}
#[derive(Subcommand)]
enum Command {
    /// Record IsWow64Process2 native/process architectures; require native Windows x64.
    Platform {
        #[arg(long)]
        report: PathBuf,
    },
    /// Extract only declared requirements into a non-executable draft.
    Infer {
        #[arg(long)]
        project: PathBuf,
        #[arg(long)]
        output: PathBuf,
    },
    /// Check required platform, exact tool tokens and variable names.
    Check {
        #[arg(long)]
        project: PathBuf,
        #[arg(long)]
        manifest: Option<PathBuf>,
        #[arg(long)]
        report: PathBuf,
        /// Use the supported read-only probes (the default for check).
        #[arg(long, conflicts_with = "allow_unsafe_probes")]
        safe_probes: bool,
        /// Allow a trusted project's custom probe commands and toolchain selector to run.
        #[arg(long)]
        allow_unsafe_probes: bool,
        #[arg(long)]
        infer_if_missing: bool,
    },
    /// Execute reviewed manifest commands only after all requirements pass.
    Build {
        #[arg(long)]
        project: PathBuf,
        #[arg(long)]
        manifest: Option<PathBuf>,
        #[arg(long)]
        report: PathBuf,
        #[arg(long)]
        execute: bool,
        #[arg(long)]
        safe_probes: bool,
        #[arg(long)]
        delivery_dir: Option<PathBuf>,
        #[arg(long)]
        log_dir: Option<PathBuf>,
    },
    /// Static ZIP inspection; --run explicitly executes the trusted package on Windows.
    Verify {
        #[arg(long)]
        zip: PathBuf,
        #[arg(long)]
        spec: PathBuf,
        #[arg(long)]
        report: PathBuf,
        #[arg(long)]
        run: bool,
        #[arg(long)]
        work_dir: Option<PathBuf>,
        #[arg(long)]
        compare_to: Option<PathBuf>,
    },
    /// Reject a nominal different-build comparison using identical actual EXE bytes.
    Compare {
        #[arg(long)]
        baseline: PathBuf,
        #[arg(long)]
        candidate: PathBuf,
        #[arg(long)]
        entry: String,
        #[arg(long)]
        report: PathBuf,
    },
    /// Package a fresh verified Windows x64 self-contained .NET publish output into <App>-Setup-x64.exe.
    PackageInstaller {
        #[arg(long)]
        project: PathBuf,
        #[arg(long)]
        manifest: Option<PathBuf>,
        #[arg(long)]
        attestation: PathBuf,
        #[arg(long)]
        output_dir: PathBuf,
        #[arg(long)]
        iscc: Option<PathBuf>,
        #[arg(long)]
        report: PathBuf,
    },
    /// Inspect a C# console or WPF project, or write contain.environment.json upon explicit confirmation.
    OnboardDotnet {
        #[arg(long)]
        project: PathBuf,
        #[arg(long)]
        apply: bool,
        #[arg(long)]
        expected_fingerprint: Option<String>,
        #[arg(long)]
        output_json: Option<PathBuf>,
    },
}
fn output(report: Report, path: PathBuf) -> Result<i32> {
    report.write(&path)?;
    println!("Report written: report.json / report.md");
    Ok(
        if report.checks.iter().any(|c| c.status == Status::Failed) {
            1
        } else if report.passed() {
            0
        } else {
            2
        },
    )
}
fn fresh(path: &std::path::Path) -> Result<()> {
    ensure!(
        !path.exists(),
        "report directory already exists; select a new directory before execution"
    );
    Ok(())
}
fn run() -> Result<i32> {
    match Cli::parse().command {
        Command::Platform { report } => {
            fresh(&report)?;
            let mut r = Report::new("platform");
            let ok = cfg!(windows)
                && r.host_arch == "x86_64"
                && r.process_arch == "x86_64"
                && r.os_version.is_some();
            r.checks.push(contain_workflow::report::Check::new("native_windows_x64",if ok{Status::Passed}else{Status::Failed},"requires confirmed native x64 and x64 process; emulated/unknown architectures do not pass"));
            output(r, report)
        }
        Command::Infer { project, output } => {
            let m = manifest::infer(&project)?;
            fs::OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(output)?
                .write_all(&serde_json::to_vec_pretty(&m)?)?;
            println!("Draft written; unresolved requirements must be confirmed before build");
            Ok(2)
        }
        Command::Check {
            project,
            manifest,
            report,
            safe_probes,
            allow_unsafe_probes,
            infer_if_missing,
        } => {
            fresh(&report)?;
            let (m, source) = if let Some(path) = manifest {
                (Manifest::read(&path)?, "custom_manifest".to_string())
            } else {
                let default_path = project.join("contain.environment.json");
                if default_path.is_file() {
                    (
                        Manifest::read(&default_path)?,
                        "contain.environment.json".to_string(),
                    )
                } else if infer_if_missing {
                    (
                        manifest::infer(&project)?,
                        "inferred_from_project_files".to_string(),
                    )
                } else {
                    (
                        Manifest::read(&default_path)?,
                        "contain.environment.json".to_string(),
                    )
                }
            };
            output(
                environment::check_with_options(
                    &m,
                    &project,
                    environment::CheckOptions {
                        safe_probes: safe_probes || !allow_unsafe_probes,
                        manifest_source: Some(source),
                    },
                ),
                report,
            )
        }
        Command::Build {
            project,
            manifest,
            report,
            execute,
            safe_probes,
            delivery_dir,
            log_dir,
        } => {
            fresh(&report)?;
            if let Some(dir) = &log_dir {
                fresh(dir)?;
            }
            let manifest_path =
                manifest.unwrap_or_else(|| project.join("contain.environment.json"));
            let m = Manifest::read(&manifest_path)?;
            let build_report = environment::build_with_options(
                &m,
                &project,
                execute,
                environment::BuildOptions {
                    safe_probes,
                    delivery_dir: delivery_dir.clone(),
                    log_dir,
                },
            );
            let build_passed = execute && build_report.passed();
            let code = output(build_report, report.clone())?;
            if build_passed {
                project_installer::write_build_attestation(
                    &m,
                    &manifest_path,
                    &project,
                    delivery_dir.as_deref(),
                    &report,
                )?;
            }
            Ok(code)
        }
        Command::Verify {
            zip,
            spec,
            report,
            run,
            work_dir,
            compare_to,
        } => {
            fresh(&report)?;
            let spec = PackageSpec::read(&spec)?;
            output(
                package::verify(&zip, &spec, compare_to.as_deref(), run, work_dir.as_deref()),
                report,
            )
        }
        Command::Compare {
            baseline,
            candidate,
            entry,
            report,
        } => {
            fresh(&report)?;
            ensure!(
                contain_workflow::artifact::safe_name(&entry),
                "invalid entry"
            );
            output(package::compare(&baseline, &candidate, &entry), report)
        }
        Command::PackageInstaller {
            project,
            manifest,
            attestation,
            output_dir,
            iscc,
            report,
        } => {
            fresh(&report)?;
            let (inst_report, _) = project_installer::package_installer(
                &project,
                manifest.as_deref(),
                &attestation,
                &output_dir,
                iscc.as_deref(),
                &report,
            )?;
            println!("Report written: report.json / report.md");
            Ok(
                if inst_report
                    .checks
                    .iter()
                    .any(|c| c.status == Status::Failed)
                {
                    1
                } else if inst_report.passed() {
                    0
                } else {
                    2
                },
            )
        }
        Command::OnboardDotnet {
            project,
            apply,
            expected_fingerprint,
            output_json,
        } => {
            if apply {
                let res =
                    dotnet_onboarding::apply_onboarding(&project, expected_fingerprint.as_deref())?;
                let bytes = serde_json::to_vec_pretty(&res)?;
                if let Some(out_path) = output_json {
                    fs::write(out_path, &bytes)?;
                } else {
                    println!("{}", String::from_utf8_lossy(&bytes));
                }
                Ok(match res.status.as_str() {
                    "applied" | "preserved_existing_manifest" => 0,
                    "source_changed_refresh_required" => 2,
                    _ => 1,
                })
            } else {
                let res = dotnet_onboarding::inspect_project(&project)?;
                let bytes = serde_json::to_vec_pretty(&res)?;
                if let Some(out_path) = output_json {
                    fs::write(out_path, &bytes)?;
                } else {
                    println!("{}", String::from_utf8_lossy(&bytes));
                }
                Ok(match res.status.as_str() {
                    "ready_to_confirm" | "existing_manifest" => 0,
                    _ => 2,
                })
            }
        }
    }
}
fn main() {
    match run() {
        Ok(code) => std::process::exit(code),
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(1)
        }
    }
}
