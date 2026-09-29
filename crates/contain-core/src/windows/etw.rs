//! Manifest ETW providers, decoded through TDH by ferrisetw. No injection or elevation.
use super::native;
use crate::model::{AttributionEvidence, BackendReport, EvidenceSource, SystemEvent};
use crate::monitor::EventSource;
mod decode;
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
const QUEUE_CAPACITY: usize = 8192;
const OBJECT_CAPACITY: usize = 16384;
pub(crate) const FILE_EVENT_IDS: &[u16] = &[12, 14, 16, 24, 26, 27, 30];

struct Decoder {
    deferred_registry: bool,
    lifetimes: crate::lifetime::LifetimeCache,
    paths: HashMap<u64, (String, String)>,
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
    decoder: Option<Arc<Mutex<Decoder>>>,
    initial_records: Vec<(crate::model::ProcessRecord, u64)>,
    trace: Option<UserTrace>,
    consumer: Option<JoinHandle<Result<(), String>>>,
    name: String,
    rx: Receiver<(SystemEvent, Instant)>,
    metrics: Arc<Metrics>,
    dropped: Arc<AtomicU64>,
    errors: Arc<AtomicU64>,
    registry_path_gaps: Arc<AtomicU64>,
    received: Arc<AtomicU64>,
    report: BackendReport,
}

impl EtwSource {
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
        let name = format!("Contain-{}", uuid::Uuid::new_v4());
        let mut source = Self {
            decoder: None,
            initial_records: Vec::new(),
            trace: None,
            consumer: None,
            name: name.clone(),
            rx,
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
            .collect();
        let registry_enabled = registry_root.is_some();
        let decoder = Arc::new(Mutex::new(Decoder {
            deferred_registry: crate::hotpath::Variant::from_env().deferred_registry(),
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
            .trace_flags(TraceFlags::EVENT_ENABLE_PROPERTY_PROCESS_START_KEY);
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
                .trace_flags(TraceFlags::EVENT_ENABLE_PROPERTY_PROCESS_START_KEY)
                .add_callback(move |record, locator| {
                    dispatch(&decoder, record, locator, 2, Decoder::registry);
                })
                .build();
            builder = builder.enable(registry);
        }
        match builder.start() {
            Ok((trace, handle)) => {
                tracing::info!(session = %name, registry = registry_enabled, "ETW session started");
                source.trace = Some(trace);
                source.consumer = Some(thread::spawn(move || {
                    UserTrace::process_from_handle(handle).map_err(|e| format!("{e:?}"))
                }));
                source.report.etw_file = "active".into();
                source.report.etw_process = "active".into();
                source.report.etw_registry = if registry_enabled {
                    "active"
                } else {
                    "not_requested"
                }
                .into();
                let ready = native::synchronize_providers(&name, registry_enabled, filter_ids)
                    .and_then(|()| {
                        let deadline =
                            std::time::Instant::now() + std::time::Duration::from_secs(3);
                        while !consumer_ready.load(Ordering::Acquire)
                            && std::time::Instant::now() < deadline
                        {
                            probe.emit()?;
                            native::control_trace(
                            &name,
                            windows_sys::Win32::System::Diagnostics::Etw::EVENT_TRACE_CONTROL_FLUSH,
                        )?;
                            thread::sleep(std::time::Duration::from_millis(20));
                        }
                        if consumer_ready.load(Ordering::Acquire) {
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
                    source.report.warnings.push(format!(
                        "ETW provider/consumer readiness failed: Win32 {code}; using snapshots."
                    ));
                }
            }
            Err(error) => {
                tracing::warn!(session = %name, error = ?error, "ETW unavailable; using snapshot fallback");
                // ferrisetw can fail after StartTrace during provider enablement; clean only our UUID session.
                let _ = native::control_trace(&name, EVENT_TRACE_CONTROL_STOP);
                source.report.etw_file = "unavailable".into();
                source.report.etw_process = "unavailable".into();
                source.report.etw_registry = "unavailable".into();
                source.report.warnings.push(format!("ETW unavailable: {error:?}. Continuing with process sampling and snapshots; no elevation requested."));
            }
        }
        source
    }
}

impl EventSource for EtwSource {
    fn drain(&mut self) -> Vec<SystemEvent> {
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
        // Include the observed mutex wait in total callback elapsed time.
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
        if !crate::hotpath::Variant::from_env().deferred_registry()
            || self.report.etw_registry != "active"
        {
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
        system.refresh_processes(ProcessesToUpdate::All, true);
        stats.enumerated = system.processes().len() as u64;
        for pid in system.processes().keys() {
            sink(self.drain());
            // Supplemental snapshot is incomplete when limits expire, never "all processes".
            if stats.queried >= 4096 || clock.elapsed().as_secs() >= 2 {
                break;
            }
            stats.queried += 1;
            let query = Instant::now();
            let identity = native::process_identity(pid.as_u32());
            stats.query_ns += query.elapsed().as_nanos() as u64;
            let at = native::now_ticks();
            if let Some(identity) = identity {
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
        stats.finished_ticks = native::now_ticks();
        stats.elapsed_ns = clock.elapsed().as_nanos() as u64;
        self.report.warnings.push(format!("Bounded initial process snapshot: {} queried, {} resolved, {} failed, {} skipped. Per-process query completion bounds validity; not an instantaneous or complete snapshot.", stats.queried, stats.resolved, stats.failed, stats.skipped));
        self.report.initial_identity_snapshot = Some(stats);
    }

    pub fn take_initial_identities(&mut self) -> Vec<(crate::model::ProcessRecord, u64)> {
        std::mem::take(&mut self.initial_records)
    }

    pub fn stop_with(&mut self, mut sink: impl FnMut(Vec<SystemEvent>)) -> BackendReport {
        if let Some(trace) = self.trace.take() {
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
    fn full_channel_drops_without_blocking_and_counts_loss() {
        let (tx, rx) = mpsc::sync_channel(1);
        let dropped = Arc::new(AtomicU64::new(0));
        let decoder = Decoder {
            deferred_registry: false,
            lifetimes: Default::default(),
            paths: HashMap::new(),
            pending: HashMap::new(),
            registry_context: Default::default(),
            roots: vec![],
            registry_root: None,
            devices: vec![],
            tx,
            metrics: Arc::new(Metrics::default()),
            dropped: dropped.clone(),
            errors: Arc::new(AtomicU64::new(0)),
            registry_path_gaps: Arc::new(AtomicU64::new(0)),
            received: Arc::new(AtomicU64::new(0)),
        };
        decoder.send(SystemEvent::default());
        decoder.send(SystemEvent::default());
        assert_eq!(dropped.load(Ordering::Relaxed), 1);
        assert_eq!(rx.try_iter().count(), 1);
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
