//! Manifest ETW providers, decoded through TDH by ferrisetw. No injection or elevation.
use super::native;
use crate::model::{AttributionEvidence, BackendReport, EvidenceSource, SystemEvent};
use crate::monitor::EventSource;
mod decode;
mod file_diagnostics;
mod metrics;
use ferrisetw::provider::{EventFilter, Provider, TraceFlags};
use ferrisetw::trace::{TraceProperties, TraceTrait};
use ferrisetw::{EventRecord, SchemaLocator, UserTrace};
use metrics::{Count, Metrics, Time, measured};
use std::collections::HashMap;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicU64, Ordering},
    mpsc::{self, Receiver, SyncSender},
};
use std::thread::{self, JoinHandle};
use std::time::Instant;
use windows_sys::Win32::System::Diagnostics::Etw::EVENT_TRACE_CONTROL_STOP;

const FILE_PROVIDER: &str = "edd08927-9cc4-4e65-b970-c2560fb5c289";
const REGISTRY_PROVIDER: &str = "70eb4f03-c1de-4f73-a051-33d13d5413bd";
// Half the original queue budget; the other half is the bounded persistence
// handoff. Splitting ownership does not enlarge the queued-record budget.
const QUEUE_CAPACITY: usize = 4096;
const OBJECT_CAPACITY: usize = 16384;
pub(crate) const FILE_EVENT_IDS: &[u16] = &[12, 14, 16, 24, 26, 27, 30];

struct Decoder {
    file_diagnostics: Option<file_diagnostics::Diagnostics>,
    deferred_registry: bool,
    file_watermark: u64,
    file_time_regressions: u64,
    lifetimes: crate::lifetime::LifetimeCache,
    paths: HashMap<u64, (String, String, Option<crate::model::FileNameEvidence>)>,
    pending: HashMap<String, (String, u64)>,
    registry_context: super::registry_context::RegistryContext,
    roots: Vec<String>,
    registry_root: Option<String>,
    devices: Vec<(String, String)>,
    tx: SyncSender<(SystemEvent, Instant)>,
    metrics: Arc<Metrics>,
    dropped: Arc<AtomicU64>,
    errors: Arc<AtomicU64>,
    registry_path_gaps: Arc<AtomicU64>,
    received: Arc<AtomicU64>,
}

fn make_event(
    metrics: &Metrics,
    timestamp_ticks: u64,
    action: (&str, &str),
    resource: String,
    source: EvidenceSource,
    writer: Option<native::ProcessIdentity>,
    success: Option<bool>,
) -> SystemEvent {
    let _timer = metrics.timer(Time::Construct);
    SystemEvent {
        id: uuid::Uuid::new_v4().to_string(),
        timestamp: native::timestamp(timestamp_ticks),
        timestamp_ticks,
        event_type: action.0.into(),
        operation: action.1.into(),
        resource,
        success,
        evidence: AttributionEvidence {
            source,
            pid: writer.as_ref().map(|p| p.pid),
            process_creation_time: writer.as_ref().map(|p| p.creation_time),
            process_image: writer.map(|p| p.image),
            ..Default::default()
        },
        ..Default::default()
    }
}

pub struct EtwSource {
    raw_clock: Option<super::clock::companion::Companion>,
    decoder: Option<Arc<Mutex<Decoder>>>,
    initial_records: Vec<(crate::model::ProcessRecord, u64)>,
    initial_threads: Vec<crate::model::InitialThreadIdentity>,
    identity_boundary: u64,
    trace: Option<UserTrace>,
    consumer: Option<JoinHandle<Result<(), String>>>,
    name: String,
    rx: Receiver<(SystemEvent, Instant)>,
    receiver_detached: bool,
    metrics: Arc<Metrics>,
    dropped: Arc<AtomicU64>,
    errors: Arc<AtomicU64>,
    registry_path_gaps: Arc<AtomicU64>,
    received: Arc<AtomicU64>,
    report: BackendReport,
}

pub(crate) struct EtwReceiver {
    rx: Receiver<(SystemEvent, Instant)>,
    metrics: Arc<Metrics>,
}

impl EtwReceiver {
    pub(crate) fn drain(&self) -> Vec<SystemEvent> {
        crate::profile::drain();
        let _timer = crate::profile::timer("capture_queue_take");
        self.rx
            .try_iter()
            .take(crate::storage::stream::PAGE)
            .map(|(event, at)| {
                self.metrics.dequeued(at);
                event
            })
            .collect()
    }
}

impl EtwSource {
    pub fn ensure_raw_clock_ready(&self) -> anyhow::Result<()> {
        anyhow::ensure!(
            !super::clock::companion::configured()
                || self.raw_clock.as_ref().is_some_and(|raw| raw.ready()),
            "Requested raw QPC companion is unavailable or failed readiness; installer was not launched. {:?}",
            self.report.clock.as_ref().map(|clock| &clock.errors)
        );
        Ok(())
    }
    /// Unit-test injection only. It provides no ETW loss or clock evidence.
    #[cfg(test)]
    pub(crate) fn synthetic(events: Vec<SystemEvent>) -> Self {
        assert!(events.len() <= QUEUE_CAPACITY);
        let mut source = Self::start(&[], None, false);
        let (tx, rx) = mpsc::sync_channel(QUEUE_CAPACITY);
        source.rx = rx;
        for event in events {
            let pending = source.metrics.entering();
            tx.send((event, Instant::now())).unwrap();
            source.metrics.admitted(pending);
            source.received.fetch_add(1, Ordering::Relaxed);
        }
        source
    }
    pub(crate) fn take_receiver(&mut self) -> EtwReceiver {
        assert!(
            !self.receiver_detached,
            "capture receiver may only be detached once"
        );
        self.receiver_detached = true;
        let (_, empty) = mpsc::sync_channel(1);
        EtwReceiver {
            rx: std::mem::replace(&mut self.rx, empty),
            metrics: self.metrics.clone(),
        }
    }

    pub(crate) fn queue_probe(
        &self,
    ) -> impl Fn() -> crate::profile::QueueSnapshot + Send + Clone + 'static {
        let metrics = self.metrics.clone();
        move || metrics.queue_snapshot()
    }

    /// After the detached receiver joins, refresh queue counters without issuing
    /// another STOP or adding cumulative Windows loss counters a second time.
    pub(crate) fn final_report(&mut self) -> BackendReport {
        self.stop_with(|_| {})
    }
    pub fn lifecycle_active(&self) -> bool {
        self.report.etw_process == "active"
    }

    pub fn start(roots: &[String], registry_root: Option<String>, enabled: bool) -> Self {
        let (tx, rx) = mpsc::sync_channel(QUEUE_CAPACITY);
        let dropped = Arc::new(AtomicU64::new(0));
        let errors = Arc::new(AtomicU64::new(0));
        let registry_path_gaps = Arc::new(AtomicU64::new(0));
        let received = Arc::new(AtomicU64::new(0));
        let metrics = Arc::new(Metrics::default());
        if crate::profile::enabled() {
            let queue_metrics = metrics.clone();
            crate::profile::observe_queue(move || queue_metrics.queue_snapshot());
        }
        let name = format!("Contain-{}", uuid::Uuid::new_v4());
        let mut source = Self {
            raw_clock: None,
            decoder: None,
            initial_records: Vec::new(),
            initial_threads: Vec::new(),
            identity_boundary: native::now_ticks(),
            trace: None,
            consumer: None,
            name: name.clone(),
            rx,
            receiver_detached: false,
            metrics: metrics.clone(),
            dropped: dropped.clone(),
            errors: errors.clone(),
            registry_path_gaps: registry_path_gaps.clone(),
            received: received.clone(),
            report: BackendReport::default(),
        };
        if !enabled {
            source.report.etw_file = "disabled".into();
            source.report.etw_process = "disabled".into();
            source.report.etw_registry = "disabled".into();
            return source;
        }
        let mut clock = super::clock::metadata();
        super::clock::observe(&mut clock, "trace_start_requested");
        source.identity_boundary = clock
            .calibrations
            .last()
            .map_or(0, |point| point.filetime_ticks);
        source.report.clock = Some(clock);
        let devices = native::device_map();
        let probe_id = uuid::Uuid::new_v4();
        let probe = match native::TraceProbe::register(probe_id.as_u128()) {
            Ok(probe) => probe,
            Err(code) => {
                source.report.etw_file = "unavailable".into();
                source.report.etw_process = "unavailable".into();
                source.report.etw_registry = "unavailable".into();
                source.report.warnings.push(format!(
                    "ETW readiness probe registration failed: Win32 {code}"
                ));
                return source;
            }
        };
        let consumer_ready = Arc::new(AtomicBool::new(false));
        let raw_clock_requested = super::clock::companion::configured();
        let file_diagnostics = file_diagnostics::Diagnostics::configured();
        let diagnostic_keys = raw_clock_requested || file_diagnostics.is_some();
        let key_flags = if diagnostic_keys {
            TraceFlags::EVENT_ENABLE_PROPERTY_EVENT_KEY
        } else {
            TraceFlags::empty()
        };
        let callback_ready = consumer_ready.clone();
        let readiness_provider = Provider::by_guid(probe_id.to_string().as_str())
            .any(1)
            .level(4)
            .add_callback(move |_, _| {
                callback_ready.store(true, Ordering::Release);
            })
            .build();
        let roots = roots
            .iter()
            .map(|p| native::normalize_path(p, &devices))
            .collect::<Vec<_>>();
        let companion_roots = roots.clone();
        let registry_enabled = registry_root.is_some();
        let decoder = Arc::new(Mutex::new(Decoder {
            file_diagnostics,
            deferred_registry: registry_enabled
                && crate::hotpath::Variant::from_env().deferred_registry(),
            file_watermark: 0,
            file_time_regressions: 0,
            lifetimes: Default::default(),
            paths: HashMap::new(),
            pending: HashMap::new(),
            registry_context: Default::default(),
            roots,
            registry_root,
            devices,
            tx,
            metrics: metrics.clone(),
            dropped,
            errors,
            registry_path_gaps,
            received,
        }));
        source.decoder = Some(decoder.clone());
        source.report.pipeline = Some(Default::default());
        let file_decoder = decoder.clone();
        let filter_ids = std::env::var_os("CONTAIN_ETW_EVENT_ID_FILTER").is_none_or(|v| v != "0");
        source.report.file_event_id_filter = Some(filter_ids);
        let mut file = Provider::by_guid(FILE_PROVIDER)
            .any(0x1ef0)
            .level(4)
            .trace_flags(TraceFlags::EVENT_ENABLE_PROPERTY_PROCESS_START_KEY | key_flags);
        if filter_ids {
            file = file.add_filter(EventFilter::ByEventIds(FILE_EVENT_IDS.to_vec()));
        }
        let file = file
            .add_callback(move |record, locator| {
                dispatch(&file_decoder, record, locator, 0, Decoder::file);
            })
            .build();
        let lifecycle_decoder = decoder.clone();
        let lifecycle = Provider::by_guid("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716")
            .any(0x30)
            .level(5)
            .trace_flags(key_flags)
            .add_callback(move |record, locator| {
                dispatch(&lifecycle_decoder, record, locator, 1, Decoder::lifecycle);
            })
            .build();
        let mut builder = UserTrace::new()
            .named(name.clone())
            .enable(file)
            .enable(lifecycle)
            .enable(readiness_provider)
            .set_trace_properties(TraceProperties {
                buffer_size: 64,
                min_buffer: 8,
                max_buffer: 64,
                ..Default::default()
            });
        if registry_enabled {
            let decoder = decoder.clone();
            let registry = Provider::by_guid(REGISTRY_PROVIDER)
                .any(0x7301)
                .level(4)
                .trace_flags(TraceFlags::EVENT_ENABLE_PROPERTY_PROCESS_START_KEY | key_flags)
                .add_callback(move |record, locator| {
                    dispatch(&decoder, record, locator, 2, Decoder::registry);
                })
                .build();
            builder = builder.enable(registry);
        }
        match builder.start() {
            Ok((trace, handle)) => {
                tracing::info!(session = %name, registry = registry_enabled, "ETW session started");
                if let Some(clock) = &mut source.report.clock {
                    super::clock::opened(clock, trace.trace_header());
                }
                source.trace = Some(trace);
                if raw_clock_requested {
                    match super::clock::companion::Companion::open(
                        &name,
                        &companion_roots,
                        probe_id.as_u128(),
                    ) {
                        Ok(raw) => source.raw_clock = Some(raw),
                        Err(error) => {
                            if let Some(clock) = &mut source.report.clock {
                                clock.errors.push(error);
                            }
                        }
                    }
                }
                source.consumer = match thread::Builder::new()
                    .name("contain-etw-primary".into())
                    .spawn(move || {
                        UserTrace::process_from_handle(handle).map_err(|e| format!("{e:?}"))
                    }) {
                    Ok(consumer) => Some(consumer),
                    Err(error) => {
                        if let Some(clock) = &mut source.report.clock {
                            clock
                                .errors
                                .push(format!("Primary consumer thread startup failed: {error}"));
                        }
                        source.stop();
                        source.report.etw_file = "unavailable".into();
                        source.report.etw_process = "unavailable".into();
                        source.report.etw_registry = "unavailable".into();
                        return source;
                    }
                };
                source.report.etw_file = "active".into();
                source.report.etw_process = "active".into();
                source.report.etw_registry = if registry_enabled {
                    "active"
                } else {
                    "not_requested"
                }
                .into();
                let ready = native::synchronize_providers(
                    &name,
                    registry_enabled,
                    filter_ids,
                    diagnostic_keys,
                )
                .and_then(|()| {
                    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(3);
                    while !(consumer_ready.load(Ordering::Acquire)
                        && source
                            .consumer
                            .as_ref()
                            .is_some_and(|consumer| !consumer.is_finished())
                        && (!raw_clock_requested
                            || source.raw_clock.as_ref().is_some_and(|raw| raw.ready())))
                        && std::time::Instant::now() < deadline
                    {
                        probe.emit()?;
                        native::control_trace(
                            &name,
                            windows_sys::Win32::System::Diagnostics::Etw::EVENT_TRACE_CONTROL_FLUSH,
                        )?;
                        thread::sleep(std::time::Duration::from_millis(20));
                    }
                    if consumer_ready.load(Ordering::Acquire)
                        && source
                            .consumer
                            .as_ref()
                            .is_some_and(|consumer| !consumer.is_finished())
                        && (!raw_clock_requested
                            || source.raw_clock.as_ref().is_some_and(|raw| raw.ready()))
                    {
                        Ok(())
                    } else {
                        Err(1460)
                    }
                });
                if let Err(code) = ready {
                    source.stop();
                    source.report.etw_file = "unavailable".into();
                    source.report.etw_process = "unavailable".into();
                    source.report.etw_registry = "unavailable".into();
                    let reason = format!("ETW provider/consumer readiness failed: Win32 {code}");
                    if raw_clock_requested {
                        if let Some(clock) = &mut source.report.clock {
                            clock.errors.push(reason.clone());
                        }
                        source.report.warnings.push(format!(
                            "{reason}; required raw clock diagnostic cannot launch the installer."
                        ));
                    } else {
                        source
                            .report
                            .warnings
                            .push(format!("{reason}; using snapshots."));
                    }
                } else if let Some(clock) = &mut source.report.clock {
                    super::clock::observe(clock, "consumer_ready");
                }
            }
            Err(error) => {
                if raw_clock_requested {
                    tracing::warn!(session = %name, error = ?error, "ETW unavailable; required raw clock diagnostic will abort before installer launch");
                    if let Some(clock) = &mut source.report.clock {
                        clock
                            .errors
                            .push(format!("Primary ETW startup failed: {error:?}"));
                    }
                } else {
                    tracing::warn!(session = %name, error = ?error, "ETW unavailable; using snapshot fallback");
                }
                // ferrisetw can fail after StartTrace during provider enablement; clean only our UUID session.
                let _ = native::control_trace(&name, EVENT_TRACE_CONTROL_STOP);
                source.report.etw_file = "unavailable".into();
                source.report.etw_process = "unavailable".into();
                source.report.etw_registry = "unavailable".into();
                source.report.warnings.push(if raw_clock_requested {
                    format!("ETW unavailable: {error:?}. Required raw clock diagnostic cannot launch the installer; no elevation requested.")
                } else {
                    format!("ETW unavailable: {error:?}. Continuing with process sampling and snapshots; no elevation requested.")
                });
            }
        }
        source
    }
}

impl EventSource for EtwSource {
    fn drain(&mut self) -> Vec<SystemEvent> {
        if self.receiver_detached {
            return Vec::new();
        }
        crate::profile::drain();
        let _clock = crate::profile::timer("capture_queue_take");
        self.rx
            .try_iter()
            .take(crate::storage::stream::PAGE)
            .map(|(event, at)| {
                self.metrics.dequeued(at);
                event
            })
            .collect()
    }

    fn stop(&mut self) -> BackendReport {
        self.stop_with(|_| {})
    }
}

impl Drop for EtwSource {
    fn drop(&mut self) {
        self.stop();
    }
}

fn dispatch(
    decoder: &Mutex<Decoder>,
    record: &EventRecord,
    locator: &SchemaLocator,
    provider: usize,
    decode: fn(&mut Decoder, &EventRecord, &SchemaLocator),
) {
    let start = Instant::now();
    if let Ok(mut d) = decoder.lock() {
        let metrics = d.metrics.clone();
        metrics.provider(provider);
        // Decode/callback body and pre-lock wait are reported separately.
        let _callback = metrics.timer(Time::Callback);
        metrics.record_lock(start.elapsed().as_nanos() as u64);
        metrics.add(Count::Callback, 1);
        let errors = d.errors.load(Ordering::Relaxed);
        let before = d.received.load(Ordering::Relaxed);
        let attempted = metrics.get(Count::Attempted);
        decode(&mut d, record, locator);
        if metrics.get(Count::Attempted) > attempted {
            metrics.add(
                if d.errors.load(Ordering::Relaxed) > errors {
                    Count::Failed
                } else {
                    Count::Succeeded
                },
                1,
            );
        } else {
            metrics.add(Count::Unsupported, 1);
        }
        if d.received.load(Ordering::Relaxed) == before {
            metrics.add(
                if d.errors.load(Ordering::Relaxed) > errors {
                    Count::FailedWithoutOutput
                } else {
                    Count::Filtered
                },
                1,
            );
        }
    }
}

impl EtwSource {
    /// Setup only, before the installer starts. No process query holds the decoder
    /// lock. Drain committed raw pages between queries; no metadata worker or PID cache.
    pub fn initialize_identities(&mut self, mut sink: impl FnMut(Vec<SystemEvent>)) {
        if !self.lifecycle_active() || self.identity_boundary == 0 {
            return;
        }
        use crate::model::{InitialIdentitySnapshot, ProcessRecord};
        use sysinfo::{ProcessesToUpdate, System};
        let clock = Instant::now();
        let _timer = crate::profile::timer("initial_identity_snapshot");
        let mut stats = InitialIdentitySnapshot {
            started_ticks: native::now_ticks(),
            ..Default::default()
        };
        let mut system = System::new();
        sink(self.drain());
        crate::profile::measured!(
            "initial_process_enumeration",
            system.refresh_processes(ProcessesToUpdate::All, true)
        );
        stats.enumerated = system.processes().len() as u64;
        for pid in system.processes().keys() {
            sink(self.drain());
            // Supplemental snapshot is incomplete when limits expire, never "all processes".
            if stats.queried >= 4096 || clock.elapsed().as_secs() >= 2 {
                break;
            }
            stats.queried += 1;
            let query = Instant::now();
            let identity = native::observed_process_identity(pid.as_u32())
                .filter(|(_, at)| *at >= self.identity_boundary);
            stats.query_ns += query.elapsed().as_nanos() as u64;
            if let Some((identity, at)) = identity {
                stats.resolved += 1;
                let record = ProcessRecord {
                    pid: identity.pid, creation_time: Some(identity.creation_time), image: identity.image,
                    first_seen: native::timestamp(at), last_seen: native::timestamp(at),
                    evidence: AttributionEvidence { source: EvidenceSource::ProcessApi, ..Default::default() },
                    reason: "Supplemental identity observed after ETW readiness; usable only at or after this query completed.".into(),
                    ..Default::default()
                };
                if let Some(decoder) = &self.decoder {
                    decoder
                        .lock()
                        .unwrap()
                        .lifetimes
                        .seed_snapshot(record.clone(), at);
                }
                self.initial_records.push((record, at));
            } else {
                stats.failed += 1;
            }
        }
        sink(self.drain());
        stats.skipped = stats.enumerated - stats.queried;
        let mut process_keys: std::collections::BTreeSet<_> = self
            .initial_records
            .iter()
            .filter_map(|(p, _)| p.creation_time.map(|birth| (p.pid, birth)))
            .collect();
        match native::ThreadSnapshot::open() {
            Ok(mut threads) => loop {
                sink(self.drain());
                if stats.thread_queried >= 32768 || clock.elapsed().as_secs() >= 2 {
                    stats.budget_exhausted = true;
                    break;
                }
                let Some(entry) = threads.next() else {
                    stats.thread_enumeration_complete = true;
                    stats.thread_unqueried = Some(0);
                    break;
                };
                let (tid, pid) = match entry {
                    Ok(entry) => entry,
                    Err(code) => {
                        *stats
                            .query_errors
                            .entry(format!("thread_enumeration_win32_{code}"))
                            .or_default() += 1;
                        break;
                    }
                };
                stats.thread_enumerated += 1;
                stats.thread_queried += 1;
                let query = Instant::now();
                let identity = native::initial_thread_identity(tid, pid, self.identity_boundary);
                stats.thread_query_ns += query.elapsed().as_nanos() as u64;
                match identity {
                    Ok((identity, owner)) => {
                        let key = (owner.pid, owner.creation_time);
                        if !process_keys.contains(&key) && process_keys.len() >= 4096 {
                            stats.thread_failed += 1;
                            *stats
                                .query_errors
                                .entry("process_capacity".into())
                                .or_default() += 1;
                            continue;
                        }
                        if process_keys.insert(key) {
                            let at = identity.observed_at;
                            let record = ProcessRecord {
                                pid: owner.pid, creation_time: Some(owner.creation_time), image: owner.image,
                                first_seen: native::timestamp(at), last_seen: native::timestamp(at),
                                evidence: AttributionEvidence { source: EvidenceSource::ProcessApi, ..Default::default() },
                                reason: "Process and preexisting thread handles were queried together after ETW readiness; validity begins at query completion.".into(),
                                ..Default::default()
                            };
                            if let Some(decoder) = &self.decoder {
                                decoder
                                    .lock()
                                    .unwrap()
                                    .lifetimes
                                    .seed_snapshot(record.clone(), at);
                            }
                            self.initial_records.push((record, at));
                        }
                        stats.thread_resolved += 1;
                        self.initial_threads.push(identity);
                    }
                    Err(native::IdentityQueryError::AfterTraceStart) => {
                        stats.thread_outside_boundary += 1
                    }
                    Err(error) => {
                        stats.thread_failed += 1;
                        let label = match error {
                            native::IdentityQueryError::Win32(code) => {
                                format!("thread_query_win32_{code}")
                            }
                            _ => "thread_ended_or_changed".into(),
                        };
                        *stats.query_errors.entry(label).or_default() += 1;
                    }
                }
            },
            Err(code) => {
                *stats
                    .query_errors
                    .entry(format!("thread_snapshot_win32_{code}"))
                    .or_default() += 1;
            }
        }
        sink(self.drain());
        stats.budget_exhausted |= stats.skipped > 0;
        stats.finished_ticks = native::now_ticks();
        stats.elapsed_ns = clock.elapsed().as_nanos() as u64;
        self.report.warnings.push(format!("Bounded initial process snapshot: {} queried, {} resolved, {} failed, {} skipped. Per-process query completion bounds validity; not an instantaneous or complete snapshot.", stats.queried, stats.resolved, stats.failed, stats.skipped));
        self.report.warnings.push(format!("Bounded initial thread snapshot: {} queried, {} resolved, {} failed, {} born after trace start; enumeration complete: {}. Unqueried remainder: {:?}. No event before each query completion is covered.", stats.thread_queried, stats.thread_resolved, stats.thread_failed, stats.thread_outside_boundary, stats.thread_enumeration_complete, stats.thread_unqueried));
        self.report.initial_identity_snapshot = Some(stats);
    }

    pub fn take_initial_identities(&mut self) -> Vec<(crate::model::ProcessRecord, u64)> {
        std::mem::take(&mut self.initial_records)
    }

    pub fn take_initial_threads(&mut self) -> Vec<crate::model::InitialThreadIdentity> {
        std::mem::take(&mut self.initial_threads)
    }

    pub fn stop_with(&mut self, mut sink: impl FnMut(Vec<SystemEvent>)) -> BackendReport {
        let mut producer_stopped = false;
        if let Some(trace) = self.trace.take() {
            if let Some(clock) = &mut self.report.clock {
                super::clock::observe(clock, "trace_stop_requested");
            }
            let mut trace = Some(trace);
            let name = self.name.clone();
            // ControlTrace STOP may itself wait while final buffers are delivered.
            let stop =
                thread::spawn(move || native::control_trace(&name, EVENT_TRACE_CONTROL_STOP));
            while !stop.is_finished() {
                sink(self.drain());
                thread::sleep(std::time::Duration::from_millis(1));
            }
            match stop.join() {
                Ok(Ok(lost)) => {
                    producer_stopped = true;
                    self.report.etw_events_lost = Some(lost.events);
                    self.report.etw_buffers_lost = Some(lost.buffers);
                    if let Some(p) = &mut self.report.pipeline {
                        p.etw_realtime_buffers_lost = Some(lost.realtime_buffers);
                        p.etw_allocated_buffers = Some(lost.allocated_buffers);
                        p.etw_buffer_size_kib = Some(lost.buffer_size_kib);
                    }
                }
                other => {
                    self.report
                        .warnings
                        .push(format!("ETW stop/statistics failed: {other:?}"));
                    drop(trace.take());
                }
            }
            if let Some(consumer) = self.consumer.take() {
                while !consumer.is_finished() {
                    sink(self.drain());
                    thread::sleep(std::time::Duration::from_millis(1));
                }
                if !matches!(consumer.join(), Ok(Ok(()))) {
                    self.errors.fetch_add(1, Ordering::Relaxed);
                    self.report
                        .warnings
                        .push("ETW consumer failed during drain".into());
                }
            }
            sink(self.drain());
            drop(trace);
            if let Some(clock) = &mut self.report.clock {
                super::clock::observe(clock, "consumer_stopped");
            }
        }
        if let Some(raw) = self.raw_clock.take() {
            let summary = raw.finish(producer_stopped);
            if summary["written"] != true
                || summary["errors"]
                    .as_array()
                    .is_some_and(|errors| !errors.is_empty())
            {
                self.report.warnings.push("Original QPC companion incomplete; retained sidecar and clock diagnostics must be inspected".into());
            }
            if let Some(clock) = &mut self.report.clock {
                clock.raw_companion = Some(summary);
            }
        }
        // Producer and ProcessTrace have ended; drain every remaining bounded page.
        loop {
            let page = self.drain();
            if page.is_empty() {
                break;
            }
            sink(page);
        }
        self.report.dropped_events = self.dropped.load(Ordering::Relaxed);
        self.report.context_losses = self.metrics.get(Count::ContextEvictions);
        self.report.events_received = self.received.load(Ordering::Relaxed);
        self.report.decode_errors = self.errors.load(Ordering::Relaxed);
        self.report.registry_path_gaps = self.registry_path_gaps.load(Ordering::Relaxed);
        if let Some(decoder) = &self.decoder {
            let decoder = match decoder.lock() {
                Ok(decoder) => decoder,
                Err(poison) => {
                    self.report.decode_errors = self.report.decode_errors.max(1);
                    let warning =
                        "ETW decoder panicked; source continuity is unverified".to_string();
                    if !self.report.warnings.contains(&warning) {
                        self.report.warnings.push(warning);
                    }
                    poison.into_inner()
                }
            };
            if decoder.file_time_regressions > 0 {
                let warning = format!(
                    "{} file records arrived before the file context watermark; rejected ambiguous object/IRP reuse and suppressed application promotion.",
                    decoder.file_time_regressions
                );
                if !self.report.warnings.contains(&warning) {
                    self.report.warnings.push(warning);
                }
            }
            self.report.file_diagnostics = decoder
                .file_diagnostics
                .as_ref()
                .map(|diagnostics| diagnostics.report.clone());
        }
        if let Some(old) = self.report.pipeline.take() {
            let mut p = self.metrics.snapshot();
            p.etw_realtime_buffers_lost = old.etw_realtime_buffers_lost;
            p.etw_allocated_buffers = old.etw_allocated_buffers;
            p.etw_buffer_size_kib = old.etw_buffer_size_kib;
            self.report.pipeline = Some(p);
        }
        self.report.clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn startup_identity_sampling_is_available_without_registry_provider() {
        // Only the provider status is injected. The read-only identity queries
        // exercise startup coverage without starting ETW or requiring elevation.
        let mut source = EtwSource::start(&[], None, false);
        source.report.etw_process = "active".into();
        source.initialize_identities(|_| {});
        assert!(source.report.initial_identity_snapshot.is_some());
        let stats = source.report.initial_identity_snapshot.as_ref().unwrap();
        assert_eq!(stats.queried, stats.resolved + stats.failed);
        assert_eq!(stats.enumerated, stats.queried + stats.skipped);
        assert_eq!(
            stats.thread_queried,
            stats.thread_resolved + stats.thread_failed + stats.thread_outside_boundary
        );
        assert_eq!(source.initial_threads.len() as u64, stats.thread_resolved);
        assert!(source.initial_records.len() <= 4096);
        for sample in &source.initial_threads {
            assert!(sample.observed_at >= stats.started_ticks);
            assert!(sample.thread_creation_time <= source.identity_boundary);
            assert!(
                source
                    .initial_records
                    .iter()
                    .any(|(p, _)| p.pid == sample.owner_pid
                        && p.creation_time == Some(sample.owner_creation_time))
            );
        }
        eprintln!(
            "read_only_native_sampling_no_etw={}",
            serde_json::to_string(stats).unwrap()
        );
    }
    #[test]
    fn full_channel_drops_without_blocking_and_counts_loss() {
        let (tx, rx) = mpsc::sync_channel(1);
        let dropped = Arc::new(AtomicU64::new(0));
        let mut source = EtwSource::start(&[], None, false);
        source.rx = rx;
        let decoder = Decoder {
            file_diagnostics: None,
            deferred_registry: false,
            file_watermark: 0,
            file_time_regressions: 0,
            lifetimes: Default::default(),
            paths: HashMap::new(),
            pending: HashMap::new(),
            registry_context: Default::default(),
            roots: vec![],
            registry_root: None,
            devices: vec![],
            tx,
            metrics: source.metrics.clone(),
            dropped: dropped.clone(),
            errors: Arc::new(AtomicU64::new(0)),
            registry_path_gaps: Arc::new(AtomicU64::new(0)),
            received: Arc::new(AtomicU64::new(0)),
        };
        let metrics = source.metrics.clone();
        let profile = crate::profile::test_profile(|| {
            crate::profile::observe_queue(move || metrics.queue_snapshot());
            let _span = crate::profile::queue_span("consumer_stalled");
            // The span is open before the producer starts. Joining without reading
            // guarantees two real Full errors, without sleeps or ETW permissions.
            thread::spawn(move || {
                for _ in 0..3 {
                    decoder.send(SystemEvent::default());
                }
            })
            .join()
            .unwrap();
        });
        let span = &profile["queue_spans"][0];
        assert_eq!(span["before"]["overflow"], 0);
        assert_eq!(span["after"]["overflow"], 2);
        assert_eq!(span["after"]["enqueued"], 1);
        assert_eq!(span["after"]["dequeued"], 0);
        assert_eq!(span["after"]["pending"], 1);
        assert_eq!(dropped.load(Ordering::Relaxed), 2);
        assert_eq!(source.drain().len(), 1);
        assert_eq!(source.metrics.get(Count::Pending), 0);
        assert_eq!(source.metrics.get(Count::Dequeued), 1);
    }

    #[test]
    fn short_writer_stall_then_stop_commits_all_pages_and_partial_tail() {
        use crate::storage::{
            Storage,
            stream::{DEFAULT_QUOTA, PAGE, RawBuffer},
        };
        let path = std::env::temp_dir().join(format!("contain-stall-{}.db", uuid::Uuid::new_v4()));
        let mut db = Storage::open(&path).unwrap();
        let c = crate::model::Capture {
            id: "stall".into(),
            schema_version: 3,
            ..Default::default()
        };
        let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
        let locked = rusqlite::Connection::open(&path).unwrap();
        locked.execute_batch("BEGIN IMMEDIATE").unwrap();
        let mut source = EtwSource::start(&[], None, false);
        let (tx, rx) = mpsc::sync_channel(1024);
        source.rx = rx;
        let metrics = source.metrics.clone();
        let count = PAGE * 3 + 17;
        let producer = thread::spawn(move || {
            for n in 1..=count {
                let pending = metrics.entering();
                tx.send((
                    SystemEvent {
                        id: format!("stall-{n}"),
                        sequence: n as u64,
                        timestamp_ticks: n as u64,
                        ..Default::default()
                    },
                    Instant::now(),
                ))
                .unwrap();
                metrics.admitted(pending);
            }
            // This sleep injects a storage stall only in a unit test, never in the fixture.
            thread::sleep(std::time::Duration::from_millis(30));
            locked.execute_batch("ROLLBACK").unwrap();
        });
        while source.metrics.get(Count::Enqueued) < PAGE as u64 {
            thread::yield_now();
        }
        writer.append(&mut db, source.drain());
        producer.join().unwrap();
        source.stop_with(|page| writer.append(&mut db, page));
        let stats = writer.finish(&mut db).unwrap();
        assert_eq!(stats.persisted, count as u64);
        assert_eq!(stats.failed + stats.quota_dropped, 0);
        let p = source.metrics.snapshot();
        assert_eq!(p.enqueued, p.dequeued);
        assert_eq!(p.queue_pending + p.queue_overflow, 0);
        assert_eq!(db.events_page(&c.id, 0, 4096).unwrap().len(), count);
        drop(db);
        for suffix in ["", "-wal", "-shm"] {
            let file = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
            if file.exists() {
                std::fs::remove_file(file).unwrap();
            }
        }
    }

    #[test]
    fn final_drain_consumes_multiple_pages_and_balances_queue() {
        let mut source = EtwSource::start(&[], None, false);
        let (tx, rx) = mpsc::sync_channel(1024);
        source.rx = rx;
        let count = crate::storage::stream::PAGE * 3 + 17;
        for n in 0..count {
            let pending = source.metrics.entering();
            tx.send((
                SystemEvent {
                    sequence: n as u64,
                    ..Default::default()
                },
                Instant::now(),
            ))
            .unwrap();
            source.metrics.admitted(pending);
        }
        drop(tx);
        let mut received = 0;
        let mut largest = 0;
        source.stop_with(|page| {
            received += page.len();
            largest = largest.max(page.len());
        });
        let p = source.metrics.snapshot();
        assert_eq!(received, count);
        assert!(largest <= crate::storage::stream::PAGE);
        assert_eq!(p.enqueued, p.dequeued);
        assert_eq!(p.queue_pending, 0);
        assert_eq!(p.queue_overflow, 0);
    }
}
