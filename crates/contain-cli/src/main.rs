use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use contain_core::capture::{InstallOptions, install};
use contain_core::cleanup::plan;
use contain_core::model::Capture;
use contain_core::storage::Storage;
use std::env;
use std::path::PathBuf;

#[derive(Parser)]
#[command(
    name = "contain",
    version,
    about = "Observe Windows application installation footprints"
)]
struct Cli {
    #[arg(
        long,
        global = true,
        help = "SQLite database path (default: LOCALAPPDATA\\Contain\\contain.db)"
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
            help = "Existing directory to watch; repeat for more directories"
        )]
        watch_roots: Vec<PathBuf>,
        #[arg(long, help = "Subkey below HKCU, e.g. Software\\Contain\\Demo")]
        registry_key: Option<String>,
        #[arg(
            long,
            default_value_t = 300,
            help = "Time to keep observing after installer exit"
        )]
        settle_ms: u64,
        #[arg(long, help = "Write captured manifest as JSON")]
        manifest: Option<PathBuf>,
        #[arg(last = true)]
        args: Vec<String>,
    },
    List,
    Inspect {
        app: String,
        #[arg(long)]
        json: bool,
    },
    Diff {
        app: String,
    },
    History {
        app: Option<String>,
    },
    Remove {
        app: String,
        #[arg(long)]
        dry_run: bool,
    },
    Doctor,
}

fn database_path(override_path: Option<PathBuf>) -> Result<PathBuf> {
    match override_path {
        Some(path) => Ok(path),
        None => Ok(PathBuf::from(
            env::var_os("LOCALAPPDATA").context("LOCALAPPDATA is unavailable; pass --db")?,
        )
        .join("Contain")
        .join("contain.db")),
    }
}

fn main() {
    if let Err(error) = run() {
        eprintln!("Contain error: {error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let cli = Cli::parse();
    let db_path = database_path(cli.db)?;
    match cli.command {
        Commands::Install {
            installer,
            name,
            watch_roots,
            registry_key,
            settle_ms,
            manifest,
            args,
        } => {
            println!("Contain\n────────────────────────────────\nWatching installation...");
            let capture = install(InstallOptions {
                name,
                installer,
                args,
                watch_roots,
                registry_key,
                settle_ms,
            })?;
            Storage::open(&db_path)?.save(&capture)?;
            if let Some(path) = manifest {
                std::fs::write(&path, serde_json::to_vec_pretty(&capture)?)
                    .with_context(|| format!("writing manifest {}", path.display()))?;
            }
            summary(&capture);
            println!(
                "\nCaptured as {}\nInspect with: contain inspect {}",
                capture.id, capture.id
            );
        }
        Commands::List | Commands::History { app: None } => {
            let rows = Storage::open(&db_path)?.list()?;
            if rows.is_empty() {
                println!("No captured applications yet.");
            }
            for (id, name, started) in rows {
                println!("{name:<28} {started}  {id}");
            }
        }
        Commands::History { app: Some(app) } => {
            let capture = Storage::open(&db_path)?.load(&app)?;
            println!(
                "{}  {}  exit={:?}",
                capture.started_at, capture.name, capture.exit_code
            );
        }
        Commands::Inspect { app, json } => {
            let capture = Storage::open(&db_path)?.load(&app)?;
            if json {
                println!("{}", serde_json::to_string_pretty(&capture)?);
            } else {
                summary(&capture);
                println!(
                    "\nInstaller: {}\nStarted: {}\nFinished: {}\nExit code: {:?}",
                    capture.installer, capture.started_at, capture.finished_at, capture.exit_code
                );
                println!("\nPROCESSES");
                for p in &capture.processes {
                    println!(
                        "  PID {} parent={:?} {} [{}] {}",
                        p.pid,
                        p.parent_pid,
                        p.image,
                        p.confidence.as_str(),
                        p.reason
                    );
                }
                println!("\nFILES");
                for file in &capture.files {
                    println!(
                        "  {} {} ({} B) [{}]",
                        symbol(&file.operation),
                        file.path,
                        file.after_size.unwrap_or(0),
                        file.confidence.as_str()
                    );
                }
                println!("\nREGISTRY VALUES");
                for change in &capture.registry {
                    println!(
                        "  {} {}\\{} [{}]",
                        symbol(&change.operation),
                        change.key,
                        change.name,
                        change.confidence.as_str()
                    );
                }
                println!("Services and scheduled tasks: not observed in v0.1.");
                println!("\nWATCH ROOTS");
                for root in &capture.watch_roots {
                    println!("  {root}");
                }
                if let Some(key) = &capture.registry_key {
                    println!("Registry scope: HKCU\\{key}");
                }
                println!("\nLIMITATIONS");
                for warning in &capture.warnings {
                    println!("  • {warning}");
                }
            }
        }
        Commands::Diff { app } => {
            let capture = Storage::open(&db_path)?.load(&app)?;
            println!("{} — observed session diff\n", capture.name);
            println!("FILES");
            for f in &capture.files {
                println!(
                    "{} {} [{}] {}",
                    symbol(&f.operation),
                    f.path,
                    f.confidence.as_str(),
                    f.reason
                );
            }
            println!("\nREGISTRY");
            for r in &capture.registry {
                println!(
                    "{} {}\\{} [{}] {}",
                    symbol(&r.operation),
                    r.key,
                    r.name,
                    r.confidence.as_str(),
                    r.reason
                );
            }
            println!(
                "\nNo writer PID is available for file or registry changes. These are observations, not ownership claims."
            );
        }
        Commands::Remove { app, dry_run } => {
            if !dry_run {
                anyhow::bail!(
                    "v0.1 only supports `contain remove <app> --dry-run`; no destructive removal is implemented"
                );
            }
            let capture = Storage::open(&db_path)?.load(&app)?;
            let candidates = plan(&capture);
            println!(
                "Dry-run cleanup review for {} (no system changes)\n",
                capture.name
            );
            for candidate in &candidates {
                println!(
                    "{:<10} {:>10} B  {}\n             {}",
                    candidate.class.as_str(),
                    candidate.size,
                    candidate.path,
                    candidate.reason
                );
            }
            println!(
                "\n{} surviving tracked file(s). No deletion performed. Registry values are not cleaned.",
                candidates.len()
            );
        }
        Commands::Doctor => {
            println!("Contain v{}", env!("CARGO_PKG_VERSION"));
            println!("Platform: {}", env::consts::OS);
            println!("Database: {}", db_path.display());
            println!("Filesystem: scoped watcher + before/after BLAKE3 state");
            println!("Processes: sampled parent chain");
            println!("Registry: optional scoped HKCU values snapshot");
            println!(
                "ETW, services, scheduled tasks, official uninstall, cleanup executor: unavailable"
            );
        }
    }
    Ok(())
}

fn symbol(operation: &str) -> &str {
    match operation {
        "created" => "+",
        "modified" => "~",
        "deleted" => "-",
        _ => "?",
    }
}

fn summary(capture: &Capture) {
    let created = capture
        .files
        .iter()
        .filter(|x| x.operation == "created")
        .count();
    let modified = capture
        .files
        .iter()
        .filter(|x| x.operation == "modified")
        .count();
    let deleted = capture
        .files
        .iter()
        .filter(|x| x.operation == "deleted")
        .count();
    let bytes: u64 = capture.files.iter().filter_map(|x| x.after_size).sum();
    println!(
        "────────────────────────────────\nApp: {}\nProcesses: {} observed\nFiles: +{} ~{} -{} ({} B current changed state)\nRegistry values: {} changed\nAttribution: installer Certain; files/registry Unknown",
        capture.name,
        capture.processes.len(),
        created,
        modified,
        deleted,
        bytes,
        capture.registry.len()
    );
}
