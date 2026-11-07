use crate::install_status::{InstallStatus, providers_ready};
use crate::lifetime::LifetimeCache;
use crate::profile::{self, measured};
use crate::storage::{Storage, stream::RawBuffer};
use crate::{
    attribution, filesystem, inventory,
    model::*,
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

pub struct InstallOptions {
    pub name: Option<String>,
    pub installer: PathBuf,
    pub args: Vec<String>,
    pub watch_roots: Vec<PathBuf>,
    pub follow_installer: bool,
    pub registry_key: Option<String>,
    pub settle_ms: u64,
    pub etw: bool,
    pub max_drain_ms: u64,
    pub evidence_quota_bytes: u64,
}

pub fn install(options: InstallOptions, db: &mut Storage) -> Result<Capture> {
    let mut status = InstallStatus::new(String::new(), options.etw);
    install_controlled(options, db, &mut status, false, false, &mut |_| {})
}

/// Desktop observation uses isolated installer output and an exact session ID.
/// Notification is infallible: a disconnected observer must not abandon a running installer.
pub fn install_observed(
    options: InstallOptions,
    db: &mut Storage,
    status: &mut InstallStatus,
    require_etw: bool,
    notify: &mut dyn FnMut(&InstallStatus),
) -> Result<Capture> {
    install_controlled(options, db, status, require_etw, true, notify)
}

fn install_controlled(
    options: InstallOptions,
    db: &mut Storage,
    status: &mut InstallStatus,
    require_etw: bool,
    isolate_output: bool,
    notify: &mut dyn FnMut(&InstallStatus),
) -> Result<Capture> {
    if status.sequence == 0 {
        status.publish(notify);
    }
    let result = (|| {
        let policy = crate::storage::checkpoint::Policy::from_env()?;
        crate::storage::checkpoint::scoped(db, policy, |db| {
            let capture = install_inner(
                options,
                db,
                policy,
                status,
                require_etw,
                isolate_output,
                notify,
            )?;
            status.begin_step("checkpoint_finalize", notify);
            Ok(capture)
        })
    })();
    let result = result.and_then(|capture: Capture| {
        anyhow::ensure!(
            !isolate_output || capture.capture_state.as_deref() == Some("finished"),
            "Capture did not reach persisted finished state; evidence remains available under session {}",
            status.session_id
        );
        Ok(capture)
    });
    match &result {
        Ok(capture) => status.finished(capture, notify),
        Err(error) => {
            // Query only this attempt. Never substitute an older same-name session.
            status.data_saved = match db.load_summary_by_id(&status.session_id) {
                Ok(saved) => {
                    status.quality = Some(saved.quality);
                    true
                }
                Err(_) => status.data_saved,
            };
            status.fail(format!("{error:#}"), notify);
        }
    }
    result
}

fn install_inner(
    options: InstallOptions,
    db: &mut Storage,
    policy: crate::storage::checkpoint::Policy,
    status: &mut InstallStatus,
    require_etw: bool,
    isolate_output: bool,
    notify: &mut dyn FnMut(&InstallStatus),
) -> Result<Capture> {
    profile::start();
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
    let follow = options.follow_installer;
    anyhow::ensure!(
        !follow || options.etw && options.watch_roots.is_empty() && options.registry_key.is_none(),
        "Follow-installer mode requires ETW and does not accept predefined watch roots or registry scope"
    );
    anyhow::ensure!(
        !follow || options.max_drain_ms <= 10000,
        "Follow-installer mode has a maximum 10000-ms descendant drain budget"
    );
    let id = status.session_id.clone();
    tracing::info!(session_id = %id, etw_requested = options.etw, "capture starting");
    let roots = if follow {
        Vec::new()
    } else if options.watch_roots.is_empty() {
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
    if let Some(path) = std::env::var_os("CONTAIN_ETW_RAW_CLOCK_PATH") {
        if !options.etw {
            anyhow::bail!(
                "raw ETW clock diagnostics require ETW; snapshot-only is not a substitute"
            );
        }
        db.validate_diagnostic_location(std::path::Path::new(&path))?;
    }
    let mut warnings = Vec::new();
    status.begin_step("baseline_inventory", notify);
    let before_inventory = if follow {
        Default::default()
    } else {
        inventory::snapshot()
    };
    let before_registry = options
        .registry_key
        .as_deref()
        .map(registry::snapshot)
        .transpose()?;
    let observer = if follow {
        None
    } else {
        match filesystem::FileObserver::start(&roots) {
            Ok(observer) => Some(observer),
            Err(error) => {
                warnings.push(format!(
                "Directory notifications unavailable: {error}. Snapshot fallback remains active."
            ));
                None
            }
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
    let quota = if follow {
        options.evidence_quota_bytes.min(64 * 1024 * 1024)
    } else {
        options.evidence_quota_bytes
    };
    let mut journal = RawBuffer::with_policy(db, &initial, quota, policy)?;
    status.data_saved = true;
    status.begin_step("provider_start", notify);
    let mut source = if follow {
        EtwSource::start_follow_installer(options.etw, db.capture_self_io_paths())
    } else {
        EtwSource::start(&watch_roots, nt_root.clone(), options.etw)
    };
    status.backend(source.provider_status());
    status.publish(notify);
    anyhow::ensure!(
        !(require_etw || follow)
            || providers_ready(source.provider_status(), options.registry_key.is_some()),
        "Required ETW providers are unavailable; installer was not launched. Check existing trace permissions or explicitly select snapshot mode. {:?}",
        source.provider_status().warnings
    );
    source.ensure_raw_clock_ready()?;
    phases.insert(
        "provider_readiness".into(),
        ready_clock.elapsed().as_millis() as u64,
    );
    // This is the same bounded cache later owned by the live receiver. Starts
    // drained while taking the initial snapshots must remain available there;
    // a thread born after trace readiness is correctly excluded from snapshots.
    let mut initial_cache = LifetimeCache::default();
    source.initialize_identities(|mut page| {
        if follow {
            page.retain(|e| e.event_type == "lifecycle");
            for event in &page {
                initial_cache.ingest(event);
            }
        }
        journal.append(db, page)
    });
    let initial_identities = source.take_initial_identities();
    let initial_threads = source.take_initial_threads();
    let lifecycle_receiver =
        crate::hotpath::Variant::from_env().lifecycle_receiver() && source.lifecycle_active();
    let receive_clock = Instant::now();
    let record = if follow {
        crate::ingest::record_while_follow
    } else {
        crate::ingest::record_while
    };
    let (observations, mut backend, stream) = record(
        &mut source,
        db,
        journal,
        initial_cache,
        |live: &crate::ingest::LiveCapture| {
            if follow {
                live.seed_follow_initial(&initial_identities, &initial_threads);
            }
            // Baseline reads now occur inside the active trace, before launch.
            // The dedicated receiver remains active during both snapshots.
            status.begin_step("baseline_files", notify);
            let before_files = filesystem::baseline_snapshot_progress(&roots, &mut |count| {
                status.processed(count, "entries", notify);
            })?;
            status.publish(notify);
            let installer_clock = Instant::now();
            let started_at = native::timestamp(native::now_ticks());
            let mut command = Command::new(&installer);
            command.args(&options.args);
            if isolate_output {
                // Installer text must never enter the progress protocol, and its
                // descendants must not retain the desktop's protocol pipe handles.
                command
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null());
            }
            let mut child = command
                .spawn()
                .with_context(|| format!("launching {}", installer.display()))?;
            let identity = native::identity_from_handle(child.as_handle(), child.id())
                .unwrap_or_else(|| {
                    warnings.push(
                        "Installer creation time query failed; descendant attribution is disabled."
                            .into(),
                    );
                    native::ProcessIdentity {
                        pid: child.id(),
                        creation_time: 0,
                        image: installer.to_string_lossy().into_owned(),
                    }
                });
            let root_pid = identity.pid;
            let root_birth = identity.creation_time;
            status.stage = "capturing".into();
            status.installer_started = true;
            status.installer_pid = Some(root_pid);
            status.installer_creation_time = (root_birth != 0).then(|| root_birth.to_string());
            status.begin_step("installer_running", notify);
            let capture_received_start = live.received();
            let mut progress_at = Instant::now();
            let mut process_observer = ProcessObserver::new(identity, &id);
            if follow {
                live.bind_follow_root(process_observer.records()[0].clone(), &id);
            }
            let mut poll_at = Instant::now();
            let exit_code = loop {
                if Instant::now() >= progress_at {
                    status.processed(
                        live.received().saturating_sub(capture_received_start),
                        "source_records",
                        notify,
                    );
                    progress_at = Instant::now() + Duration::from_millis(250);
                }
                if !lifecycle_receiver && Instant::now() >= poll_at {
                    measured!("capture_process_poll", process_observer.poll());
                    poll_at = Instant::now() + Duration::from_millis(30);
                }
                if let Some(status) = child.try_wait()? {
                    break status.code();
                }
                thread::sleep(Duration::from_millis(1));
            };
            phases.insert(
                "installer_root".into(),
                installer_clock.elapsed().as_millis() as u64,
            );
            let exited_at = native::now_ticks();
            status.processed(
                live.received().saturating_sub(capture_received_start),
                "source_records",
                notify,
            );
            status.installer_exited = true;
            status.installer_exit_code = exit_code;
            status.publish(notify);
            status.begin_step("descendant_drain", notify);
            let drain_received_start = live.received();
            live.reset_activity();
            let drain_start = Instant::now();
            let mut drain_timed_out = false;
            loop {
                if Instant::now() >= progress_at {
                    status.processed(
                        live.received().saturating_sub(drain_received_start),
                        "source_records",
                        notify,
                    );
                    progress_at = Instant::now() + Duration::from_millis(250);
                }
                if !lifecycle_receiver && Instant::now() >= poll_at {
                    measured!("capture_process_poll", process_observer.poll());
                    poll_at = Instant::now() + Duration::from_millis(30);
                }
                let descendants_live = live.descendants(
                    process_observer.records(),
                    (root_pid, root_birth),
                    &id,
                    lifecycle_receiver,
                );
                match crate::drain::decide(
                    drain_start.elapsed().as_millis() as u64,
                    live.quiet_ms(),
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
                thread::sleep(Duration::from_millis(1));
            }
            status.processed(
                live.received().saturating_sub(drain_received_start),
                "source_records",
                notify,
            );
            status.publish(notify);
            phases.insert(
                "descendant_quiet_drain".into(),
                drain_start.elapsed().as_millis() as u64,
            );
            let after_clock = Instant::now();
            status.begin_step("final_files", notify);
            let observed = filesystem::observed_snapshot_progress(&roots, &mut |count| {
                status.processed(count, "entries", notify);
            })?;
            status.publish(notify);
            phases.insert(
                "after_file_snapshot_hash".into(),
                after_clock.elapsed().as_millis() as u64,
            );
            status.begin_step("final_registry", notify);
            let after_registry = options
                .registry_key
                .as_deref()
                .map(registry::observed_snapshot)
                .transpose()?;
            status.begin_step("source_stop", notify);
            Ok((
                before_files,
                observed,
                after_registry,
                process_observer,
                root_pid,
                root_birth,
                exit_code,
                exited_at,
                started_at,
                drain_timed_out,
            ))
        },
    )?;
    status.stage = "finalizing".into();
    status.capture_finished = true;
    status.backend(&backend);
    status.begin_step("evidence_replay", notify);
    let (
        before_files,
        observed,
        after_registry,
        process_observer,
        root_pid,
        root_birth,
        exit_code,
        exited_at,
        started_at,
        drain_timed_out,
    ) = observations;
    let after_files = observed.finish(
        &before_files,
        backend.source_intact()
            && stream.failed == 0
            && stream.quota_dropped == 0
            && stream.error.is_none()
            && !drain_timed_out,
    );
    if let Some(handoff) = &backend.handoff {
        phases.insert(
            "etw_stop_and_queue_drain".into(),
            handoff.stop_and_drain_ns / 1_000_000,
        );
    }
    profile::add(
        "capture_receive_wall",
        receive_clock.elapsed().as_nanos() as u64,
    );
    if let Some(checkpoint) = &stream.checkpoint {
        phases.insert(
            "post_raw_checkpoint".into(),
            checkpoint.duration_ns / 1_000_000,
        );
    }
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
    let association_clock = Instant::now();
    db.prepare_raw(&id)?;
    let mut processes = process_observer.finish();
    if let Some(root) = processes
        .iter_mut()
        .find(|p| p.pid == root_pid && p.confidence == Confidence::Certain)
    {
        root.ended_at = Some(exited_at);
    }
    let mut cache = LifetimeCache::default();
    for (p, at) in initial_identities {
        cache.seed_snapshot(p, at);
    }
    for identity in initial_threads {
        if let Some(process) = cache
            .processes
            .get(&(identity.owner_pid, identity.owner_creation_time))
            .cloned()
        {
            cache.seed_thread_snapshot(identity, process);
        }
    }
    for p in &processes {
        cache.seed(p.clone());
    }
    let mut cursor = (0, 0);
    let mut replayed = 0u64;
    loop {
        let page = db.raw_page(&id, cursor)?;
        if page.is_empty() {
            break;
        }
        for e in &page {
            measured!("lifetime_ingest", cache.ingest(e));
        }
        replayed = replayed.saturating_add(page.len() as u64);
        status.processed(replayed, "raw_events", notify);
        let last = page.last().unwrap();
        cursor = (last.timestamp_ticks, last.sequence);
    }
    status.publish(notify);
    let lifecycle_complete = backend.etw_process == "active"
        && backend.etw_events_lost == Some(0)
        && backend.etw_buffers_lost == Some(0)
        && !backend.has_loss()
        && cache.dropped == 0;
    if lifecycle_complete {
        measured!("lifetime_attach", cache.attach((root_pid, root_birth), &id));
    }
    backend.context_losses += cache.dropped;
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
    if follow {
        processes.retain(|p| crate::follow::is_direct_actor(p, (root_pid, root_birth)));
    }
    let source_intact = backend.source_intact();
    status.begin_step("evidence_normalization", notify);
    let mut normalized_count = 0u64;
    let mut raw_stats = CaptureStats::default();
    let mut cursor = (0, 0);
    loop {
        let done = db.evidence_group(&id, |db| {
            for _ in 0..4 {
                let mut page = db.raw_page(&id, cursor)?;
                if page.is_empty() {
                    return Ok(true);
                }
                normalized_count = normalized_count.saturating_add(page.len() as u64);
                let last = page.last().unwrap();
                cursor = (last.timestamp_ticks, last.sequence);
                let mut retained = Vec::with_capacity(page.len());
                let mut ops = Vec::new();
                for mut e in page.drain(..) {
                    let had_registry_path = e.event_type == "registry" && e.raw.resource_resolved;
                    if lifecycle_complete {
                        measured!("post_lifetime_resolve", cache.resolve_event(&mut e));
                    }
                    if had_registry_path && !e.raw.resource_resolved {
                        backend.registry_path_gaps += 1;
                    }
                    measured!(
                        "post_attribution",
                        attribution::attribute(&mut e, &processes, &id)
                    );
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
                    // Unresolved open/close metadata remains in committed raw evidence;
                    // it is not a user mutation or a new monitored category.
                    if e.event_type == "registry_context" {
                        continue;
                    }
                    if db.select_raw_only_registry(&id, &e, lifecycle_complete)? {
                        continue;
                    }
                    if !source_intact {
                        suppress(&mut e);
                    }
                    if e.event_type == "file"
                        && !matches!(e.operation.as_str(), "open_requested" | "close")
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
                            measured!(
                                "post_attribution",
                                attribution::attribute(&mut e, &processes, &id)
                            );
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
                measured!("post_quality_graph", crate::reliability::finish(&mut batch));
                add_stats(&mut raw_stats, &batch.stats);
                db.append_evidence(&batch)?;
            }
            Ok(false)
        })?;
        status.processed(normalized_count, "raw_events", notify);
        if done {
            break;
        }
    }
    status.publish(notify);
    phases.insert(
        "lifetime_attribution_and_paged_persistence".into(),
        association_clock.elapsed().as_millis() as u64,
    );
    drop(cache);
    status.begin_step("evidence_association", notify);
    let mut resources_processed = 0u64;
    let correlation_clock = Instant::now();
    let state_association_timer = profile::timer("post_state_association");
    let operations = crate::correlation::correlate(&mut [], &before_files, &after_files);

    let seen = observer
        .as_ref()
        .map(|o| o.seen_paths())
        .unwrap_or_default();
    let notification_gaps =
        observer
            .as_ref()
            .map(|o| o.gaps())
            .unwrap_or(if follow { 0 } else { 1 });
    if let Some(observer) = &observer {
        let gaps = observer.gaps();
        if gaps > 0 {
            warnings.push(format!("Directory notifications reported {gaps} errors, rescan requests or capacity drops. Snapshot evidence remains separate."));
        }
    }
    let mut files = filesystem::diff(&before_files, &after_files, &seen);
    let registry_observations = after_registry.as_ref().map(|s| &s.observations);
    let mut registry = match (
        options.registry_key.as_deref(),
        before_registry,
        after_registry.as_ref(),
    ) {
        (Some(key), Some(before), Some(after)) => registry::diff(key, &before, &after.values),
        _ => Vec::new(),
    };
    let complete = backend.source_intact() && !drain_timed_out && notification_gaps == 0;
    // Persist exactly the observations validated by composition, for both files
    // and registry values. One post-stop transaction retains the existing
    // per-resource bound instead of accumulating a full-session event vector.
    db.evidence_group(&id, |db| {
        for file in &mut files {
            status.processed(resources_processed, "resources", notify);
            resources_processed += 1;
            match db.resource_events(&id, &file.path)? {
                Some(mut events) => {
                    let proof =
                        filesystem::change_observation(&before_files, &after_files, &file.path);
                    attribution::compose_file_observed(file, &mut events, complete, proof);
                    db.persist_state_validation(&id, &events)?;
                }
                None => {
                    let proof =
                        filesystem::change_observation(&before_files, &after_files, &file.path);
                    attribution::compose_file_observed(file, &mut [], false, proof);
                    backend.context_losses += 1;
                    if let Some(p) = &mut backend.pipeline {
                        p.context_evictions += 1;
                    }
                    warnings.push(
                        "Per-resource association quota exceeded; session promotion suppressed"
                            .into(),
                    );
                }
            }
        }
        for change in &mut registry {
            status.processed(resources_processed, "resources", notify);
            resources_processed += 1;
            let proof = Some(registry::change_observation(change, registry_observations));
            if let Some(root) = &nt_root {
                match db.resource_events(&id, &format!("{root}\\{}", change.name))? {
                    Some(mut events) => {
                        attribution::compose_registry_observed(
                            change,
                            &mut events,
                            nt_root.as_deref(),
                            proof,
                        );
                        db.persist_state_validation(&id, &events)?;
                    }
                    None => {
                        attribution::compose_registry_observed(
                            change,
                            &mut [],
                            nt_root.as_deref(),
                            proof,
                        );
                        backend.context_losses += 1;
                        if let Some(p) = &mut backend.pipeline {
                            p.context_evictions += 1;
                        }
                        warnings.push(
                            "Per-resource association quota exceeded; session promotion suppressed"
                                .into(),
                        );
                    }
                }
            } else {
                // A missing SID prevents ETW resource lookup, not preservation
                // of the independently observed value/read limitation.
                attribution::compose_registry_observed(change, &mut [], None, proof);
            }
        }
        Ok(())
    })?;
    status.processed(resources_processed, "resources", notify);
    status.publish(notify);
    warnings.push("File observations use bounded temporary read leases where available; they describe observed hashes and identities, not complete mutation coverage or ownership. Final file and registry state attribution remains Unknown where mutation coverage or read/clock proof is incomplete; verified source actors remain separate.".into());
    phases.insert(
        "correlation_attribution".into(),
        correlation_clock.elapsed().as_millis() as u64,
    );
    drop(state_association_timer);
    let inventory_clock = Instant::now();
    status.begin_step("final_inventory", notify);
    let after_inventory = if follow {
        Default::default()
    } else {
        inventory::snapshot()
    };
    let inventory = inventory::diff(&before_inventory, &after_inventory, &processes, &id);
    phases.insert(
        "after_inventory".into(),
        inventory_clock.elapsed().as_millis() as u64,
    );
    warnings.extend(before_inventory.warnings);
    warnings.extend(after_inventory.warnings);
    warnings.extend(backend.warnings.iter().cloned());
    if backend.registry_path_gaps > 0 {
        warnings.push(format!("Registry paths could not be safely resolved in {} records (missing names/context or conflicting process generations); unresolved records are retained only for verified installer actors. Registry state attribution uses Unknown snapshot fallback unless complete event evidence exists.", backend.registry_path_gaps));
    }
    if !lifecycle_complete {
        warnings.push("Process lifecycle evidence was unavailable or incomplete. Sampling can miss short-lived/detached children; unresolved actors remain Unknown.".into());
    }
    warnings.push("File ETW operation requests and snapshot state changes are separate evidence; failed operations never validate state changes.".into());
    if follow {
        warnings.push("Follow-installer mode records verified source operations only; no installation-before baseline or final-file ownership is established.".into());
        warnings.push("Follow-installer capture is limited to the owned EXE and verified direct children. Unresolved or unrelated candidates can be excluded; absence from this session does not prove absence of filesystem activity. Historical cached file names are not verified current paths.".into());
    }
    let skipped = before_files.unreadable.len() + after_files.unreadable.len();
    if skipped > 0 {
        warnings.push(format!(
            "{skipped} unreadable/reparse scan entries excluded from diff."
        ));
    }
    if backend.has_loss() {
        warnings.push(format!("Capture incomplete: {} raw record losses, {} context losses, {:?} ETW events lost, {:?} ETW buffers lost, {} decode errors, journal error={:?}; state attribution remains Unknown.",backend.dropped_events,backend.context_losses,backend.etw_events_lost,backend.etw_buffers_lost,backend.decode_errors,backend.stream.as_ref().and_then(|s|s.error.as_deref())));
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
    if !capture.backend.source_intact() {
        capture.stats.high_confidence_events = 0;
        for f in &mut capture.files {
            f.confidence = Confidence::Unknown;
            f.reason = "Session continuity unverified; final promotion suppressed".into();
            for evidence in &mut f.evidence {
                if let Some(read) = &mut evidence.observation {
                    read.final_source_complete = false;
                }
            }
        }
        for r in &mut capture.registry {
            r.confidence = Confidence::Unknown;
            r.reason = "Session continuity unverified; final promotion suppressed".into();
            for evidence in &mut r.evidence {
                if let Some(read) = &mut evidence.observation {
                    read.final_source_complete = false;
                }
            }
        }
    }
    status.begin_step("sqlite_save", notify);
    measured!("post_summary_write", db.save(&capture))?;
    measured!(
        "post_final_downgrade_full_commit",
        db.mark_finished(&capture)
    )?;
    // Summary returns no full raw-event vector. Full/paged history remains in Storage.
    status.begin_step("result_readback", notify);
    capture = measured!("post_summary_read", db.load_summary_by_id(&capture.id))?;
    profile::add("post_wall", association_clock.elapsed().as_nanos() as u64);
    profile::finish()?;
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

#[cfg(test)]
fn state_coverage_intact(
    backend: &BackendReport,
    trace_stopped: Instant,
    snapshots_finished: Instant,
) -> bool {
    // Preserve the historical counterexample: a snapshot taken after stopping
    // has a blind interval even with zero losses. Those counters cannot establish
    // final-state authorship, even when an earlier same-path event has a known actor.
    backend.source_intact() && snapshots_finished <= trace_stopped
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn failed_attempt_cannot_open_old_same_name_record_as_its_result() {
        assert_failed_attempt_keeps_its_session(false);
    }

    #[test]
    fn failed_attempt_does_not_borrow_evidence_from_a_name_matching_its_session_id() {
        assert_failed_attempt_keeps_its_session(true);
    }

    fn assert_failed_attempt_keeps_its_session(name_matches_session_id: bool) {
        let directory =
            std::env::temp_dir().join(format!("contain-status-test-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        let path = directory.join("records.db");
        let mut db = Storage::open(&path).unwrap();
        let mut status = InstallStatus::new(path.to_string_lossy().into_owned(), false);
        let id = status.session_id.clone();
        let old = Capture {
            id: "old-same-name".into(),
            name: if name_matches_session_id {
                id.clone()
            } else {
                "same name".into()
            },
            schema_version: 3,
            ..Default::default()
        };
        db.save(&old).unwrap();
        let options = InstallOptions {
            name: Some(old.name.clone()),
            installer: directory.join("missing.exe"),
            args: vec![],
            watch_roots: vec![directory.clone()],
            follow_installer: false,
            registry_key: None,
            settle_ms: 1,
            etw: false,
            max_drain_ms: 1,
            evidence_quota_bytes: 1024,
        };
        let mut rows = Vec::new();
        let error = install_observed(options, &mut db, &mut status, false, &mut |row| {
            rows.push(serde_json::to_value(row).unwrap())
        })
        .unwrap_err();
        assert!(format!("{error:#}").contains("installer not found"));
        assert_eq!(rows.len(), 2);
        assert_eq!(rows[0]["stage"], "preparing");
        assert_eq!(rows[1]["stage"], "failed");
        assert_eq!(rows[1]["session_id"], id);
        assert!(!status.installer_started && !status.capture_finished && !status.data_saved);
        assert!(db.load_summary_by_id(&id).is_err());
        if name_matches_session_id {
            assert_eq!(db.load_summary(&id).unwrap().id, old.id);
        } else {
            assert!(db.load_summary(&id).is_err());
        }
        assert_eq!(db.load_summary(&old.id).unwrap().name, old.name);
        drop(db);
        std::fs::remove_file(path).unwrap();
        std::fs::remove_dir(directory).unwrap();
    }

    #[test]
    fn after_stop_snapshot_cannot_promote_an_earlier_attributed_mutation() {
        let stopped = Instant::now();
        let backend = BackendReport {
            etw_events_lost: Some(0),
            etw_buffers_lost: Some(0),
            ..Default::default()
        };
        let complete = state_coverage_intact(&backend, stopped, stopped + Duration::from_millis(1));
        assert!(!complete);
        let file_event = SystemEvent {
            event_type: "file".into(),
            operation: "write_requested".into(),
            resource: "C:\\test\\shared.txt".into(),
            confidence: Confidence::High,
            success: Some(true),
            raw: RawEvidence {
                resource_resolved: true,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut files = [FileChange {
            path: file_event.resource.clone(),
            operation: "modified".into(),
            ..Default::default()
        }];
        let mut file_events = [file_event];
        attribution::compose_files(&mut files, &mut file_events, complete);
        assert_eq!(files[0].confidence, Confidence::Unknown);
        assert_eq!(file_events[0].confidence, Confidence::High);
        assert!(!file_events[0].state_validated);
        let mut registry_events = [SystemEvent {
            event_type: "registry".into(),
            operation: "set_value".into(),
            resource: "root\\value".into(),
            confidence: Confidence::High,
            success: Some(true),
            raw: RawEvidence {
                resource_resolved: true,
                ..Default::default()
            },
            ..Default::default()
        }];
        let mut changes = [RegistryChange {
            name: "value".into(),
            operation: "modified".into(),
            ..Default::default()
        }];
        attribution::compose_registry(&mut changes, &mut registry_events, Some("root"), complete);
        assert_eq!(changes[0].confidence, Confidence::Unknown);
        assert_eq!(registry_events[0].confidence, Confidence::High);
        assert!(!registry_events[0].state_validated);
        assert!(state_coverage_intact(&backend, stopped, stopped));
        let lossy = BackendReport {
            dropped_events: 1,
            ..backend
        };
        assert!(!state_coverage_intact(&lossy, stopped, stopped));
    }
}
