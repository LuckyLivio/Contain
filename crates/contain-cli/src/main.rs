use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use contain_core::{
    capture::{InstallOptions, install},
    cleanup::plan,
    doctor,
    storage::Storage,
};
use std::{env, path::PathBuf};
mod render;

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
    },
    Inspect {
        app: String,
        #[arg(long)]
        verbose: bool,
        #[arg(long)]
        json: bool,
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
    },
    History {
        app: String,
        #[arg(long)]
        json: bool,
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
    let cli = Cli::parse();
    let db = match cli.db {
        Some(path) => path,
        None => PathBuf::from(
            env::var_os("LOCALAPPDATA")
                .context("LOCALAPPDATA is missing; supply --db explicitly")?,
        )
        .join("Contain/contain.db"),
    };
    match cli.command {
        Commands::Install {
            installer,
            name,
            watch_roots,
            registry_key,
            settle_ms,
            max_drain_ms,
            no_etw,
            manifest,
            args,
        } => {
            println!(
                "Contain\n────────────────────────────────\nCollecting baseline and watching installation..."
            );
            let capture = install(InstallOptions {
                name,
                installer,
                args,
                watch_roots,
                registry_key,
                settle_ms,
                max_drain_ms,
                etw: !no_etw,
            })?;
            Storage::open(&db)?.save(&capture)?;
            if let Some(path) = manifest {
                std::fs::write(&path, envelope("inspect", &capture)?)
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
        Commands::List { json } => {
            let rows = Storage::open(&db)?.list()?;
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
        Commands::Inspect { app, json, verbose } => {
            let capture = Storage::open(&db)?.load(&app)?;
            if json {
                println!("{}", envelope("inspect", &capture)?);
            } else {
                render::inspect(&capture, verbose);
            }
        }
        Commands::Explain { event, json } => {
            let capture = Storage::open(&db)?.for_event(&event)?;
            let data = render::explanation(&capture, &event)?;
            if json {
                println!("{}", envelope("explain", data)?);
            } else {
                render::explain(&capture, &event)?;
            }
        }
        Commands::Diff { app, json } => {
            let capture = Storage::open(&db)?.load(&app)?;
            if json {
                println!(
                    "{}",
                    envelope(
                        "diff",
                        serde_json::json!({"app_id":capture.id,"files":capture.files,"registry":capture.registry,"inventory":capture.inventory,"operations":capture.operations,"quality":capture.quality,"warning":"Unattributed changes are not assumed to belong to this app."})
                    )?
                );
            } else {
                render::diff(&capture);
            }
        }
        Commands::History { app, json } => {
            let capture = Storage::open(&db)?.load(&app)?;
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
