use crate::{filesystem, model::Capture, process::ProcessObserver, registry};
use anyhow::{Context, Result};
use chrono::Utc;
use std::path::PathBuf;
use std::process::Command;
use std::thread;
use std::time::{Duration, Instant};
use uuid::Uuid;

pub struct InstallOptions {
    pub name: Option<String>,
    pub installer: PathBuf,
    pub args: Vec<String>,
    pub watch_roots: Vec<PathBuf>,
    pub registry_key: Option<String>,
    pub settle_ms: u64,
}

pub fn install(options: InstallOptions) -> Result<Capture> {
    let installer = options
        .installer
        .canonicalize()
        .with_context(|| format!("installer not found: {}", options.installer.display()))?;
    if !installer.is_file() {
        anyhow::bail!("installer is not a file: {}", installer.display());
    }
    let default_root = installer
        .parent()
        .context("installer has no parent directory")?
        .to_path_buf();
    let watch_roots = if options.watch_roots.is_empty() {
        vec![default_root]
    } else {
        options.watch_roots
    };
    let roots = filesystem::canonical_roots(&watch_roots)?;
    let before_files = filesystem::snapshot(&roots)?;
    let before_registry = options
        .registry_key
        .as_deref()
        .map(registry::snapshot)
        .transpose()?;
    let observer = filesystem::FileObserver::start(&roots)?;
    let started_at = Utc::now().to_rfc3339();
    let mut child = Command::new(&installer)
        .args(&options.args)
        .spawn()
        .with_context(|| format!("launching {}", installer.display()))?;
    let mut processes = ProcessObserver::new(child.id(), &installer.to_string_lossy());
    let exit_code = loop {
        processes.poll();
        if let Some(status) = child.try_wait()? {
            break status.code();
        }
        thread::sleep(Duration::from_millis(40));
    };
    let until = Instant::now() + Duration::from_millis(options.settle_ms);
    while Instant::now() < until {
        processes.poll();
        thread::sleep(Duration::from_millis(40));
    }
    let after_files = filesystem::snapshot(&roots)?;
    let after_registry = options
        .registry_key
        .as_deref()
        .map(registry::snapshot)
        .transpose()?;
    let files = filesystem::diff(&before_files, &after_files, &observer.seen_paths());
    let registry = match (
        options.registry_key.as_deref(),
        before_registry,
        after_registry,
    ) {
        (Some(key), Some(before), Some(after)) => registry::diff(key, &before, &after),
        _ => Vec::new(),
    };
    let finished_at = Utc::now().to_rfc3339();
    let name = options.name.unwrap_or_else(|| {
        installer
            .file_stem()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned()
    });
    let mut warnings = vec![
        "File and registry changes have no writer PID; application ownership is Unknown.".into(),
        "Process polling can miss short-lived or detached children; only sampled descendants are shown.".into(),
        "Monitoring covers only selected watch roots and optional HKCU key.".into(),
    ];
    let skipped = before_files.unreadable.len() + after_files.unreadable.len();
    if skipped > 0 {
        warnings.push(format!("{skipped} file or directory scan entries were unreadable or reparse points; related changes were omitted."));
    }
    Ok(Capture {
        id: Uuid::new_v4().to_string(),
        name,
        installer: installer.to_string_lossy().into_owned(),
        started_at,
        finished_at,
        exit_code,
        watch_roots: roots
            .iter()
            .map(|p| p.to_string_lossy().into_owned())
            .collect(),
        registry_key: options.registry_key,
        processes: processes.finish(),
        files,
        registry,
        warnings,
    })
}
