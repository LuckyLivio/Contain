use crate::lifetime::LifetimeCache;
use crate::storage::{Storage, stream::RawBuffer};
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
    pub evidence_quota_bytes: u64,
}

pub fn install(options: InstallOptions, db: &mut Storage) -> Result<Capture> {
    let capture_clock = Instant::now();
    let mut phases = std::collections::BTreeMap::new();
    let before_clock = Instant::now();
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
    db.validate_capture_location(&watch_roots)?;
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
    phases.insert(
        "before_snapshot_inventory".into(),
        before_clock.elapsed().as_millis() as u64,
    );
    let ready_clock = Instant::now();
    let initial = Capture {
        schema_version: 3,
        id: id.clone(),
        name: options.name.clone().unwrap_or_else(|| {
            installer
                .file_stem()
                .unwrap_or_default()
                .to_string_lossy()
                .into_owned()
        }),
        installer: installer.to_string_lossy().into_owned(),
        started_at: native::timestamp(native::now_ticks()),
        watch_roots: watch_roots.clone(),
        registry_key: options.registry_key.clone(),
        quality: CaptureQuality {
            level: QualityLevel::Incomplete,
            reasons: vec!["Capture in progress; committed raw evidence is provisional".into()],
        },
        ..Default::default()
    };
    let mut journal = RawBuffer::new(db, &initial, options.evidence_quota_bytes)?;
    let mut source = EtwSource::start(&watch_roots, nt_root.clone(), options.etw);
    phases.insert(
        "provider_readiness".into(),
        ready_clock.elapsed().as_millis() as u64,
    );
    let installer_clock = Instant::now();
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
    let mut live_cache = LifetimeCache::default();
    let mut poll_at = Instant::now();
    let exit_code = loop {
        if Instant::now() >= poll_at {
            process_observer.poll();
            poll_at = Instant::now() + Duration::from_millis(30);
        }
        let incoming = source.drain();
        let full_page = incoming.len() == crate::storage::stream::PAGE;
        for e in &incoming {
            live_cache.ingest(e);
        }
        journal.append(db, incoming);
        if let Some(status) = child.try_wait()? {
            break status.code();
        }
        if !full_page {
            thread::sleep(Duration::from_millis(1));
        }
    };
    phases.insert(
        "installer_root".into(),
        installer_clock.elapsed().as_millis() as u64,
    );
    let exited_at = native::now_ticks();
    let root_birth = process_observer
        .records()
        .iter()
        .find(|p| p.pid == root_pid)
        .and_then(|p| p.creation_time)
        .unwrap_or(0);
    let mut cache = live_cache;
    for p in process_observer.records() {
        cache.seed(p);
    }
    let mut last_activity = Instant::now();
    let drain_start = Instant::now();
    let mut drain_timed_out = false;
    loop {
        if Instant::now() >= poll_at {
            process_observer.poll();
            poll_at = Instant::now() + Duration::from_millis(30);
        }
        for p in process_observer.records() {
            cache.seed(p);
        }
        let incoming = source.drain();
        let full_page = incoming.len() == crate::storage::stream::PAGE;
        for e in &incoming {
            cache.ingest(e);
            if e.event_type == "file" || (e.event_type == "registry" && e.raw.resource_resolved) {
                last_activity = Instant::now();
            }
        }
        journal.append(db, incoming);
        cache.attach((root_pid, root_birth), &id);
        let descendants_live = cache
            .processes
            .values()
            .filter(|p| p.pid != root_pid && p.confidence == Confidence::High)
            .any(|p| {
                native::process_identity(p.pid)
                    .is_some_and(|live| Some(live.creation_time) == p.creation_time)
            });
        match crate::drain::decide(
            drain_start.elapsed().as_millis() as u64,
            last_activity.elapsed().as_millis() as u64,
            descendants_live,
            options.settle_ms.max(if options.etw { 1200 } else { 0 }),
            options.max_drain_ms,
        ) {
            crate::drain::Decision::Timeout => {
                drain_timed_out = true;
                break;
            }
            crate::drain::Decision::Quiet => break,
            crate::drain::Decision::Wait => {}
        }
        if !full_page {
            thread::sleep(Duration::from_millis(1));
        }
    }
    let stop_clock = Instant::now();
    let mut backend = source.stop_with(|incoming| journal.append(db, incoming));
    let stream = journal.finish(db)?;
    phases.insert(
        "raw_persistence_overlapping".into(),
        stream.persistence_ns / 1_000_000,
    );
    backend.dropped_events += stream.quota_dropped + stream.failed;
    if let Some(p) = &mut backend.pipeline {
        p.retention_dropped = stream.quota_dropped;
        p.persistence_succeeded = Some(stream.persisted);
        p.persistence_failed = Some(stream.failed);
    }
    if let Some(error) = &stream.error {
        backend.warnings.push(error.clone());
    }
    backend.stream = Some(stream);
    phases.insert(
        "etw_stop_and_queue_drain".into(),
        stop_clock.elapsed().as_millis() as u64,
    );
    phases.insert(
        "descendant_quiet_drain".into(),
        drain_start.elapsed().as_millis() as u64,
    );
    let association_clock = Instant::now();
    let mut processes = process_observer.finish();
    if let Some(root) = processes
        .iter_mut()
        .find(|p| p.pid == root_pid && p.confidence == Confidence::Certain)
    {
        root.ended_at = Some(exited_at);
    }
    drop(cache);
    let mut cache = LifetimeCache::default();
    for p in &processes {
        cache.seed(p.clone());
    }
    let mut cursor = (0, 0);
    loop {
        let page = db.raw_page(&id, cursor)?;
        if page.is_empty() {
            break;
        }
        for e in &page {
            cache.ingest(e);
        }
        let last = page.last().unwrap();
        cursor = (last.timestamp_ticks, last.sequence);
    }
    let lifecycle_complete = backend.etw_process == "active"
        && backend.etw_events_lost == Some(0)
        && backend.etw_buffers_lost == Some(0)
        && backend.dropped_events == 0
        && backend.decode_errors == 0
        && cache.dropped == 0;
    if lifecycle_complete {
        cache.attach((root_pid, root_birth), &id);
    }
    backend.dropped_events += cache.dropped;
    if let Some(p) = &mut backend.pipeline {
        p.context_evictions += cache.dropped;
    }
    if lifecycle_complete {
        processes = cache
            .processes
            .values()
            .filter(|p| matches!(p.confidence, Confidence::Certain | Confidence::High))
            .cloned()
            .collect();
    }
    let source_intact = backend.dropped_events == 0
        && backend.decode_errors == 0
        && backend.etw_events_lost == Some(0)
        && backend.etw_buffers_lost == Some(0);
    let mut raw_stats = CaptureStats::default();
    let mut cursor = (0, 0);
    loop {
        let mut page = db.raw_page(&id, cursor)?;
        if page.is_empty() {
            break;
        }
        let last = page.last().unwrap();
        cursor = (last.timestamp_ticks, last.sequence);
        let mut retained = Vec::with_capacity(page.len());
        let mut ops = Vec::new();
        for mut e in page.drain(..) {
            if lifecycle_complete {
                cache.resolve_event(&mut e);
            }
            attribution::attribute(&mut e, &processes, &id);
            if e.event_type == "lifecycle"
                && !processes.iter().any(|p| {
                    Some(p.pid) == e.evidence.pid
                        && p.creation_time == e.evidence.process_creation_time
                })
                && !e
                    .evidence
                    .pid
                    .is_some_and(|pid| db.scoped_header(&id, pid).unwrap_or(true))
            {
                continue;
            }
            if e.event_type == "registry"
                && !e.raw.resource_resolved
                && e.confidence != Confidence::High
            {
                continue;
            }
            if !source_intact {
                suppress(&mut e);
            }
            if e.event_type == "file" && !matches!(e.operation.as_str(), "open_requested" | "close")
            {
                let mut pair = vec![e];
                if let Some(mut completion) = db.completion(&id, &pair[0].id)? {
                    if !source_intact {
                        completion.success = None;
                    }
                    pair.push(completion);
                }
                let mut normalized = crate::correlation::correlate(
                    &mut pair,
                    &Default::default(),
                    &Default::default(),
                );
                e = pair.remove(0);
                if source_intact {
                    attribution::attribute(&mut e, &processes, &id);
                }
                for o in &mut normalized {
                    o.confidence = e.confidence;
                }
                ops.extend(normalized);
            }
            retained.push(e);
        }
        let mut batch = Capture {
            id: id.clone(),
            events: retained,
            operations: ops,
            backend: backend.clone(),
            ..Default::default()
        };
        crate::reliability::finish(&mut batch);
        add_stats(&mut raw_stats, &batch.stats);
        db.append_evidence(&batch)?;
    }
    phases.insert(
        "lifetime_attribution_and_paged_persistence".into(),
        association_clock.elapsed().as_millis() as u64,
    );
    drop(cache);
    let after_clock = Instant::now();
    let after_files = filesystem::snapshot(&roots)?;
    phases.insert(
        "after_file_snapshot_hash".into(),
        after_clock.elapsed().as_millis() as u64,
    );
    let correlation_clock = Instant::now();
    let operations = crate::correlation::correlate(&mut [], &before_files, &after_files);

    let seen = observer
        .as_ref()
        .map(|o| o.seen_paths())
        .unwrap_or_default();
    let notification_gaps = observer.as_ref().map(|o| o.gaps()).unwrap_or(1);
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
    for file in &mut files {
        match db.resource_events(&id, &file.path) {
            Ok(mut events) => {
                attribution::compose_files(std::slice::from_mut(file), &mut events, complete)
            }
            Err(error) => {
                backend.dropped_events += 1;
                if let Some(p) = &mut backend.pipeline {
                    p.context_evictions += 1;
                }
                warnings.push(error.to_string());
            }
        }
    }
    if let Some(root) = &nt_root {
        for change in &mut registry {
            match db.resource_events(&id, &format!("{root}\\{}", change.name)) {
                Ok(mut events) => attribution::compose_registry(
                    std::slice::from_mut(change),
                    &mut events,
                    nt_root.as_deref(),
                    complete && backend.registry_path_gaps == 0,
                ),
                Err(error) => {
                    backend.dropped_events += 1;
                    if let Some(p) = &mut backend.pipeline {
                        p.context_evictions += 1;
                    }
                    warnings.push(error.to_string());
                }
            }
        }
    }
    phases.insert(
        "correlation_attribution".into(),
        correlation_clock.elapsed().as_millis() as u64,
    );
    let inventory_clock = Instant::now();
    let after_inventory = inventory::snapshot();
    let inventory = inventory::diff(&before_inventory, &after_inventory, &processes, &id);
    phases.insert(
        "after_inventory".into(),
        inventory_clock.elapsed().as_millis() as u64,
    );
    warnings.extend(before_inventory.warnings);
    warnings.extend(after_inventory.warnings);
    warnings.extend(backend.warnings.iter().cloned());
    if backend.registry_path_gaps > 0 {
        warnings.push(format!("Registry ETW omitted absolute hive/key names in {} provider records; unresolved records are retained only for verified installer actors. Registry state attribution uses Unknown snapshot fallback unless complete event evidence exists.", backend.registry_path_gaps));
    }
    if !lifecycle_complete {
        warnings.push("Process lifecycle evidence was unavailable or incomplete. Sampling can miss short-lived/detached children; unresolved actors remain Unknown.".into());
    }
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
        events: Vec::new(),
        inventory,
        backend,
        operations,
        ..Default::default()
    };
    capture.stats.phase_ms = phases;
    capture.stats.capture_elapsed_ms = capture_clock.elapsed().as_millis() as u64;
    capture.stats.drain_timed_out = drain_timed_out;
    capture.stats.snapshot_gaps = skipped as u64;
    capture.stats.notification_gaps = notification_gaps;
    crate::timeline::complete(&mut capture, exited_at);
    crate::reliability::finish(&mut capture);
    add_stats(&mut capture.stats, &raw_stats);
    if capture.backend.dropped_events > 0 {
        capture.stats.high_confidence_events = 0;
        for f in &mut capture.files {
            f.confidence = Confidence::Unknown;
        }
        for r in &mut capture.registry {
            r.confidence = Confidence::Unknown;
        }
    }
    db.save(&capture)?;
    db.mark_finished(&capture)?;
    // Summary returns no full raw-event vector. Full/paged history remains in Storage.
    capture = db.load_summary(&capture.id)?;
    tracing::info!(session_id = %capture.id, events = capture.events.len(), files = capture.files.len(),
        dropped = capture.backend.dropped_events, "capture completed");
    Ok(capture)
}

fn suppress(e: &mut SystemEvent) {
    if e.event_type == "completion" {
        e.success = None;
    }
    if matches!(e.event_type.as_str(), "file" | "registry") {
        e.confidence = Confidence::Unknown;
        e.evidence.rule = AttributionRule::EventLoss;
        e.raw.resource_resolved = false;
        e.reason = "Source continuity unverified; no application promotion".into();
    }
}
fn add_stats(to: &mut CaptureStats, from: &CaptureStats) {
    to.events_retained += from.events_retained;
    to.events_normalized += from.events_normalized;
    to.known_actor_events += from.known_actor_events;
    to.unknown_actor_events += from.unknown_actor_events;
    to.known_resource_events += from.known_resource_events;
    to.unknown_resource_events += from.unknown_resource_events;
    to.high_confidence_events += from.high_confidence_events;
}
