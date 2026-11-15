use anyhow::{Result, ensure};
use clap::{Parser, Subcommand};
use contain_workflow::{
    environment,
    manifest::{self, Manifest},
    package::{self, PackageSpec},
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
        delivery_dir: Option<PathBuf>,
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
        } => {
            fresh(&report)?;
            let m = Manifest::read(
                &manifest.unwrap_or_else(|| project.join("contain.environment.json")),
            )?;
            output(environment::check(&m, &project), report)
        }
        Command::Build {
            project,
            manifest,
            report,
            execute,
            delivery_dir,
        } => {
            fresh(&report)?;
            let m = Manifest::read(
                &manifest.unwrap_or_else(|| project.join("contain.environment.json")),
            )?;
            output(
                environment::build_to(&m, &project, execute, delivery_dir.as_deref()),
                report,
            )
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
