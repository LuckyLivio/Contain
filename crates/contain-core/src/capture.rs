use crate::lifetime::LifetimeCache;
use crate::{
    attribution, filesystem, inventory,
    model::*,
    monitor::EventSource,
    process::ProcessObserver,
    registry,
    windows::{etw::EtwSource, native},
};
use anyhow::{Context, Result};
use std::os::windows::io::AsHandle;
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
    pub etw: bool,
    pub max_drain_ms: u64,
}

const MAX_EVENTS: usize = 100_000;

fn drain(source: &mut impl EventSource, events: &mut Vec<SystemEvent>, dropped: &mut u64) {
    let incoming = source.drain();
    let remaining = MAX_EVENTS.saturating_sub(events.len());
    *dropped += incoming.len().saturating_sub(remaining) as u64;
    events.extend(incoming.into_iter().take(remaining));
}

pub fn install(options: InstallOptions) -> Result<Capture> {
    let capture_clock = Instant::now();
    let installer = options
        .installer
        .canonicalize()
        .with_context(|| format!("installer not found: {}", options.installer.display()))?;
    if !installer.is_file() {
        anyhow::bail!("installer is not a file: {}", installer.display());
    }
    let id = Uuid::new_v4().to_string();
    tracing::info!(session_id = %id, etw_requested = options.etw, "capture starting");
    let roots = if options.watch_roots.is_empty() {
        vec![
            installer
                .parent()
                .context("installer has no directory")?
                .to_path_buf(),
        ]
    } else {
        options.watch_roots
    };
    let roots = filesystem::canonical_roots(&roots)?;
    let watch_roots: Vec<String> = roots
        .iter()
        .map(|p| p.to_string_lossy().into_owned())
        .collect();
    let mut warnings = Vec::new();
    let before_inventory = inventory::snapshot();
    let before_files = filesystem::snapshot(&roots)?;
    let before_registry = options
        .registry_key
        .as_deref()
        .map(registry::snapshot)
        .transpose()?;
    let observer = match filesystem::FileObserver::start(&roots) {
        Ok(observer) => Some(observer),
        Err(error) => {
            warnings.push(format!(
                "Directory notifications unavailable: {error}. Snapshot fallback remains active."
            ));
            None
        }
    };
    let nt_root = options.registry_key.as_ref().and_then(|key| {
        native::current_user_sid()
            .map(|sid| format!("\\registry\\user\\{sid}\\{key}").to_lowercase())
    });
    let mut source = EtwSource::start(&watch_roots, nt_root.clone(), options.etw);
    let started_at = native::timestamp(native::now_ticks());
    let mut child = Command::new(&installer)
        .args(&options.args)
        .spawn()
        .with_context(|| format!("launching {}", installer.display()))?;
    let identity =
        native::identity_from_handle(child.as_handle(), child.id()).unwrap_or_else(|| {
            warnings.push(
                "Installer creation time query failed; descendant attribution is disabled.".into(),
            );
            native::ProcessIdentity {
                pid: child.id(),
                creation_time: 0,
                image: installer.to_string_lossy().into_owned(),
            }
        });
    let root_pid = identity.pid;
    let mut process_observer = ProcessObserver::new(identity, &id);
    let mut events = Vec::new();
    let mut overflow = 0;
    let exit_code = loop {
        process_observer.poll();
        drain(&mut source, &mut events, &mut overflow);
        if let Some(status) = child.try_wait()? {
            break status.code();
        }
        thread::sleep(Duration::from_millis(30));
    };
    let exited_at = native::now_ticks();
    let root_birth = process_observer
        .records()
        .iter()
        .find(|p| p.pid == root_pid)
        .and_then(|p| p.creation_time)
        .unwrap_or(0);
    let mut cache = LifetimeCache::default();
    for p in process_observer.records() {
        cache.seed(p);
    }
    for e in &events {
        cache.ingest(e);
    }
    let mut last_activity = Instant::now();
    let drain_start = Instant::now();
    let mut drain_timed_out = false;
    loop {
        process_observer.poll();
        for p in process_observer.records() {
            cache.seed(p);
        }
        let count = events.len();
        drain(&mut source, &mut events, &mut overflow);
        for e in &events[count..] {
            cache.ingest(e);
            if e.event_type == "file" || (e.event_type == "registry" && e.raw.resource_resolved) {
                last_activity = Instant::now();
            }
        }
        cache.attach((root_pid, root_birth), &id);
        let descendants_live = cache
            .processes
            .values()
            .filter(|p| p.pid != root_pid && p.confidence == Confidence::High)
            .any(|p| {
                native::process_identity(p.pid)
                    .is_some_and(|live| Some(live.creation_time) == p.creation_time)
            });
        if drain_start.elapsed() >= Duration::from_millis(options.max_drain_ms) {
            drain_timed_out = true;
            break;
        }
        if !descendants_live
            && last_activity.elapsed()
                >= Duration::from_millis(options.settle_ms.max(if options.etw { 1200 } else { 0 }))
        {
            break;
        }
        thread::sleep(Duration::from_millis(30));
    }
    let mut backend = source.stop();
    drain(&mut source, &mut events, &mut overflow);
    backend.dropped_events += overflow;
    let mut processes = process_observer.finish();
    if let Some(root) = processes
        .iter_mut()
        .find(|p| p.pid == root_pid && p.confidence == Confidence::Certain)
    {
        root.ended_at = Some(exited_at);
    }
    events.sort_by_key(|e| (e.timestamp_ticks, e.sequence));
    let mut cache = LifetimeCache::default();
    for p in &processes {
        cache.seed(p.clone());
    }
    for e in &events {
        cache.ingest(e);
    }
    let lifecycle_complete = backend.etw_process == "active"
        && backend.etw_events_lost == Some(0)
        && backend.etw_buffers_lost == Some(0)
        && backend.dropped_events == 0
        && backend.decode_errors == 0
        && cache.dropped == 0;
    if lifecycle_complete {
        cache.attach((root_pid, root_birth), &id);
        for e in &mut events {
            cache.resolve_event(e);
        }
    }
    backend.dropped_events += cache.dropped;
    if lifecycle_complete {
        processes = cache
            .processes
            .values()
            .filter(|p| matches!(p.confidence, Confidence::Certain | Confidence::High))
            .cloned()
            .collect();
    }
    for event in &mut events {
        attribution::attribute(event, &processes, &id);
    }
    events.retain(|e| {
        e.event_type != "lifecycle"
            && (e.event_type != "registry"
                || e.raw.resource_resolved
                || e.confidence == Confidence::High)
    });
    let after_files = filesystem::snapshot(&roots)?;
    let seen = observer
        .as_ref()
        .map(|o| o.seen_paths())
        .unwrap_or_default();
    if let Some(observer) = &observer {
        let gaps = observer.gaps();
        if gaps > 0 {
            warnings.push(format!("Directory notifications reported {gaps} errors, rescan requests or capacity drops. Snapshot evidence remains separate."));
        }
    }
    let mut files = filesystem::diff(&before_files, &after_files, &seen);
    let after_registry = options
        .registry_key
        .as_deref()
        .map(registry::snapshot)
        .transpose()?;
    let mut registry = match (
        options.registry_key.as_deref(),
        before_registry,
        after_registry,
    ) {
        (Some(key), Some(before), Some(after)) => registry::diff(key, &before, &after),
        _ => Vec::new(),
    };
    let complete = backend.etw_events_lost == Some(0)
        && backend.etw_buffers_lost == Some(0)
        && backend.dropped_events == 0
        && backend.decode_errors == 0;
    attribution::compose_files(&mut files, &mut events, complete);
    attribution::compose_registry(
        &mut registry,
        &mut events,
        nt_root.as_deref(),
        complete && backend.registry_path_gaps == 0,
    );
    let after_inventory = inventory::snapshot();
    let inventory = inventory::diff(&before_inventory, &after_inventory, &processes, &id);
    warnings.extend(before_inventory.warnings);
    warnings.extend(after_inventory.warnings);
    warnings.extend(backend.warnings.iter().cloned());
    if backend.registry_path_gaps > 0 {
        warnings.push(format!("Registry ETW omitted absolute hive/key names in {} provider records; those records cannot be scoped and were discarded. Registry state attribution uses Unknown snapshot fallback unless complete event evidence exists.", backend.registry_path_gaps));
    }
    if !registry.is_empty() && !events.iter().any(|e| e.event_type == "registry") {
        warnings.push("No scoped registry source events were delivered; registry changes are snapshot-only Unknown.".into());
    }
    warnings.push("Process sampling can miss short-lived/detached children. ETW events from dead or unqueryable writer instances remain Unknown.".into());
    warnings.push("File ETW operation requests and snapshot state changes are separate evidence; failed operations never validate state changes.".into());
    let skipped = before_files.unreadable.len() + after_files.unreadable.len();
    if skipped > 0 {
        warnings.push(format!(
            "{skipped} unreadable/reparse scan entries excluded from diff."
        ));
    }
    if backend.dropped_events > 0
        || backend.etw_events_lost.unwrap_or(0) > 0
        || backend.etw_buffers_lost.unwrap_or(0) > 0
        || backend.decode_errors > 0
    {
        warnings.push(format!("Capture incomplete: {} application drops, {:?} ETW events lost, {:?} ETW buffers lost, {} decode errors; state attribution remains Unknown.", backend.dropped_events, backend.etw_events_lost, backend.etw_buffers_lost, backend.decode_errors));
    }
    let finished_at = native::timestamp(native::now_ticks());
    let name = options.name.unwrap_or_else(|| {
        installer
            .file_stem()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned()
    });
    let mut capture = Capture {
        schema_version: 3,
        id,
        name,
        installer: installer.to_string_lossy().into_owned(),
        started_at,
        finished_at,
        exit_code,
        watch_roots,
        registry_key: options.registry_key,
        processes,
        files,
        registry,
        warnings,
        events,
        inventory,
        backend,
        ..Default::default()
    };
    capture.stats.capture_elapsed_ms = capture_clock.elapsed().as_millis() as u64;
    capture.stats.drain_timed_out = drain_timed_out;
    crate::timeline::complete(&mut capture, exited_at);
    tracing::info!(session_id = %capture.id, events = capture.events.len(), files = capture.files.len(),
        dropped = capture.backend.dropped_events, "capture completed");
    Ok(capture)
}
