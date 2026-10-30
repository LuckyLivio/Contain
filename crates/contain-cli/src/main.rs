use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use contain_core::{
    capture::{InstallOptions, install},
    cleanup::plan,
    doctor,
    storage::Storage,
};
use std::{env, path::PathBuf};
pub mod render;

#[derive(Parser)]
#[command(
    name = "contain",
    version,
    about = "Observe, attribute and explain Windows application changes"
)]
struct Cli {
    #[arg(
        long,
        global = true,
        help = "SQLite path; defaults to LOCALAPPDATA\\Contain\\contain.db"
    )]
    db: Option<PathBuf>,
    #[arg(
        long,
        global = true,
        requires = "db",
        help = "Read an existing database without creating or migrating the original; only list/inspect/diff/history/explain"
    )]
    read_only: bool,
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand)]
enum Commands {
    Install {
        installer: PathBuf,
        #[arg(long)]
        name: Option<String>,
        #[arg(
            long = "watch",
            help = "Existing watch root; repeat for multiple directories"
        )]
        watch_roots: Vec<PathBuf>,
        #[arg(long)]
        registry_key: Option<String>,
        #[arg(long, default_value_t = 1500)]
        settle_ms: u64,
        #[arg(long, default_value_t = 10000)]
        max_drain_ms: u64,
        #[arg(long, default_value_t = 256)]
        evidence_quota_mib: u64,
        #[arg(
            long,
            help = "Use directory notifications and snapshots without attempting ETW"
        )]
        no_etw: bool,
        #[arg(long)]
        manifest: Option<PathBuf>,
        #[arg(last = true)]
        args: Vec<String>,
    },
    List {
        #[arg(long)]
        json: bool,
        #[arg(
            long,
            help = "Include recording state and recorded end without reading history"
        )]
        details: bool,
    },
    Inspect {
        app: String,
        #[arg(long)]
        verbose: bool,
        #[arg(long)]
        json: bool,
        #[arg(
            long,
            conflicts_with = "verbose",
            help = "Return summary metadata without event history; JSON kind is inspect_summary"
        )]
        summary: bool,
    },
    Explain {
        event: String,
        #[arg(long)]
        json: bool,
    },
    Diff {
        app: String,
        #[arg(long)]
        json: bool,
        #[arg(
            long,
            help = "Read state differences without loading source events or normalized operations"
        )]
        state_only: bool,
    },
    History {
        app: String,
        #[arg(long)]
        json: bool,
        #[arg(long)]
        limit: Option<u32>,
        #[arg(long, default_value_t = 0)]
        offset: u64,
    },
    Remove {
        app: String,
        #[arg(long)]
        dry_run: bool,
    },
    Doctor {
        #[arg(long)]
        json: bool,
    },
}

fn main() {
    let _ = tracing_subscriber::fmt()
        .json()
        .with_writer(std::io::stderr)
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env().unwrap_or_else(|_| "warn".into()),
        )
        .try_init();
    if let Err(error) = run() {
        eprintln!("Contain error: {error:#}");
        std::process::exit(1);
    }
}

fn envelope(kind: &str, value: impl serde::Serialize) -> Result<String> {
    Ok(serde_json::to_string_pretty(
        &serde_json::json!({"schema_version":3,"kind":kind,"data":value}),
    )?)
}

fn run() -> Result<()> {
    run_cli(Cli::parse())
}

fn run_cli(cli: Cli) -> Result<()> {
    if cli.read_only {
        anyhow::ensure!(
            !matches!(
                &cli.command,
                Commands::Install { .. } | Commands::Remove { .. } | Commands::Doctor { .. }
            ),
            "--read-only supports list, inspect, diff, history and explain only; capture, cleanup and active provider probes are disabled"
        );
    }
    let db = match cli.db {
        Some(path) => path,
        None => PathBuf::from(
            env::var_os("LOCALAPPDATA")
                .context("LOCALAPPDATA is missing; supply --db explicitly")?,
        )
        .join("Contain/contain.db"),
    };
    let open = || {
        if cli.read_only {
            Storage::open_read_only(&db)
        } else {
            Storage::open(&db)
        }
    };
    match cli.command {
        Commands::Install {
            installer,
            name,
            watch_roots,
            registry_key,
            settle_ms,
            max_drain_ms,
            evidence_quota_mib,
            no_etw,
            manifest,
            args,
        } => {
            println!(
                "Contain\n────────────────────────────────\nCollecting baseline and watching installation..."
            );
            let mut storage = Storage::open(&db)?;
            let capture = install(
                InstallOptions {
                    name,
                    installer,
                    args,
                    watch_roots,
                    registry_key,
                    settle_ms,
                    max_drain_ms,
                    etw: !no_etw,
                    evidence_quota_bytes: evidence_quota_mib
                        .checked_mul(1024 * 1024)
                        .context("evidence quota overflow")?,
                },
                &mut storage,
            )?;
            if let Some(path) = manifest {
                std::fs::write(&path, envelope("inspect", storage.load(&capture.id)?)?)
                    .with_context(|| format!("writing manifest {}", path.display()))?;
            }
            render::summary(&capture);
            for warning in &capture.warnings {
                eprintln!("Warning: {warning}");
            }
            println!(
                "\nApp identity: {}\nInspect with: contain --db \"{}\" inspect {}",
                capture.id,
                db.display(),
                capture.id
            );
        }
        Commands::List { json, details } => {
            let storage = open()?;
            if details {
                let rows = storage.list_details()?;
                if json {
                    println!("{}", envelope("list_details", &rows)?);
                } else if rows.is_empty() {
                    println!("No captured applications yet.");
                } else {
                    for row in rows {
                        println!(
                            "{:<28} {}  {}  recording={}",
                            row.name,
                            row.started_at,
                            row.id,
                            row.capture_state.as_deref().unwrap_or("unknown (legacy)")
                        );
                    }
                }
                return Ok(());
            }
            let rows = storage.list()?;
            if json {
                println!("{}",envelope("list",rows.iter().map(|(id,name,started)|serde_json::json!({"id":id,"name":name,"started_at":started})).collect::<Vec<_>>())?);
            } else if rows.is_empty() {
                println!("No captured applications yet.");
            } else {
                for (id, name, started) in rows {
                    println!("{name:<28} {started}  {id}");
                }
            }
        }
        Commands::Inspect {
            app,
            json,
            verbose,
            summary,
        } => {
            let storage = open()?;
            let capture = if (json || verbose) && !summary {
                storage.load(&app)?
            } else {
                storage.load_summary(&app)?
            };
            if json {
                if summary {
                    let mut value = serde_json::to_value(&capture)?;
                    value["view_scope"] = "summary".into();
                    value["history_included"] = false.into();
                    value["summary_counts"] =
                        serde_json::to_value(render::summary_counts(&capture))?;
                    println!("{}", envelope("inspect_summary", value)?);
                } else {
                    println!("{}", envelope("inspect", &capture)?);
                }
            } else {
                render::inspect(&capture, verbose);
            }
        }
        Commands::Explain { event, json } => {
            let capture = open()?.for_event(&event)?;
            let data = render::explanation(&capture, &event)?;
            if json {
                println!("{}", envelope("explain", data)?);
            } else {
                render::explain(&capture, &event)?;
            }
        }
        Commands::Diff {
            app,
            json,
            state_only,
        } => {
            let storage = open()?;
            let capture = if state_only {
                storage.load_summary(&app)?
            } else {
                storage.load(&app)?
            };
            if json {
                let mut value = serde_json::json!({"app_id":capture.id,"files":capture.files,"registry":capture.registry,"inventory":capture.inventory,"operations":capture.operations,"quality":capture.quality,"warning":"Unattributed changes are not assumed to belong to this app."});
                if state_only {
                    value["view_scope"] = "state_only".into();
                }
                println!("{}", envelope("diff", value)?);
            } else {
                render::diff(&capture);
            }
        }
        Commands::History {
            app,
            json,
            limit,
            offset,
        } => {
            let storage = open()?;
            let mut capture = if limit.is_some() {
                storage.load_summary(&app)?
            } else {
                storage.load(&app)?
            };
            if let Some(limit) = limit {
                capture.events = if cli.read_only {
                    storage.events_page_compatible(&capture.id, offset, limit)?
                } else {
                    storage.events_page(&capture.id, offset, limit)?
                };
            }
            if json {
                println!(
                    "{}",
                    envelope(
                        "history",
                        serde_json::json!({"app_id":capture.id,"events":capture.events})
                    )?
                );
            } else {
                render::history(&capture);
            }
        }
        Commands::Remove { app, dry_run } => {
            if !dry_run {
                anyhow::bail!(
                    "Only `remove <app> --dry-run` is supported. No deletion executor is implemented."
                );
            }
            let capture = Storage::open(&db)?.load(&app)?;
            println!("Cleanup review for {} (dry-run)\n", capture.name);
            let candidates = plan(&capture);
            for item in &candidates {
                println!(
                    "{:<10} {:>10} B {}\n  {}",
                    item.class.as_str(),
                    item.size,
                    item.path,
                    item.reason
                );
            }
            println!(
                "\n{} surviving files. No deletion performed; registry values are preserved.",
                candidates.len()
            );
        }
        Commands::Doctor { json } => {
            let report = doctor::probe();
            if json {
                println!("{}", envelope("doctor", &report)?);
            } else {
                println!(
                    "Contain {}\nDatabase: {}\nAdmin privileges: {}\nProcess observation: {}\nETW file provider: {}\nETW registry provider: {}\nServices: {:?}\nScheduled tasks: {:?}\nStartup entries: {:?}",
                    env!("CARGO_PKG_VERSION"),
                    db.display(),
                    report.elevated,
                    report.process_observation,
                    report.etw_file,
                    report.etw_registry,
                    report.service_count,
                    report.scheduled_task_count,
                    report.startup_count
                );
                for warning in &report.warnings {
                    println!("Warning: {warning}");
                }
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod command_tests {
    use super::*;
    #[test]
    fn read_only_requires_an_explicit_database_and_rejects_active_commands() {
        assert!(Cli::try_parse_from(["contain", "--read-only", "list"]).is_err());
        for command in [
            vec!["doctor", "--json"],
            vec!["install", "synthetic.exe"],
            vec!["remove", "sample", "--dry-run"],
        ] {
            let mut args = vec![
                "contain",
                "--db",
                "missing-synthetic-database.db",
                "--read-only",
            ];
            args.extend(command);
            let error = run_cli(Cli::try_parse_from(args).unwrap()).unwrap_err();
            assert!(
                error
                    .to_string()
                    .contains("active provider probes are disabled")
            );
        }
    }
    #[test]
    fn additive_view_options_preserve_original_defaults() {
        let base = Cli::try_parse_from(["contain", "inspect", "sample", "--json"]).unwrap();
        assert!(!base.read_only);
        assert!(matches!(
            base.command,
            Commands::Inspect {
                summary: false,
                verbose: false,
                json: true,
                ..
            }
        ));
        let summary = Cli::try_parse_from([
            "contain",
            "--db",
            "记录 & [x].db",
            "--read-only",
            "inspect",
            "sample",
            "--summary",
            "--json",
        ])
        .unwrap();
        assert!(matches!(
            summary.command,
            Commands::Inspect { summary: true, .. }
        ));
        assert!(
            Cli::try_parse_from(["contain", "inspect", "sample", "--summary", "--verbose"])
                .is_err()
        );
        let details = Cli::try_parse_from(["contain", "list", "--details", "--json"]).unwrap();
        assert!(matches!(
            details.command,
            Commands::List {
                details: true,
                json: true
            }
        ));
        let diff =
            Cli::try_parse_from(["contain", "diff", "sample", "--state-only", "--json"]).unwrap();
        assert!(matches!(
            diff.command,
            Commands::Diff {
                state_only: true,
                json: true,
                ..
            }
        ));
    }
}
