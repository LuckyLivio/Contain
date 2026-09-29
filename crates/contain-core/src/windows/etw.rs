//! Manifest ETW providers, decoded through TDH by ferrisetw. No injection or elevation.
use super::native;
use crate::model::{AttributionEvidence, BackendReport, EvidenceSource, SystemEvent};
use crate::monitor::EventSource;
mod decode;
use ferrisetw::provider::{Provider, TraceFlags};
use ferrisetw::trace::{TraceProperties, TraceTrait};
use ferrisetw::{EventRecord, SchemaLocator, UserTrace};
use std::collections::HashMap;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicU64, Ordering},
    mpsc::{self, Receiver, SyncSender},
};
use std::thread::{self, JoinHandle};
use windows_sys::Win32::System::Diagnostics::Etw::EVENT_TRACE_CONTROL_STOP;

const FILE_PROVIDER: &str = "edd08927-9cc4-4e65-b970-c2560fb5c289";
const REGISTRY_PROVIDER: &str = "70eb4f03-c1de-4f73-a051-33d13d5413bd";
const QUEUE_CAPACITY: usize = 8192;
const OBJECT_CAPACITY: usize = 16384;

struct Decoder {
    paths: HashMap<u64, (String, String)>,
    pending: HashMap<String, (String, u64)>,
    registry_context: super::registry_context::RegistryContext,
    roots: Vec<String>,
    registry_root: Option<String>,
    devices: Vec<(String, String)>,
    tx: SyncSender<SystemEvent>,
    dropped: Arc<AtomicU64>,
    errors: Arc<AtomicU64>,
    registry_path_gaps: Arc<AtomicU64>,
    received: Arc<AtomicU64>,
}

fn make_event(
    timestamp_ticks: u64,
    kind: &str,
    operation: &str,
    resource: String,
    source: EvidenceSource,
    writer: Option<native::ProcessIdentity>,
    success: Option<bool>,
) -> SystemEvent {
    SystemEvent {
        id: uuid::Uuid::new_v4().to_string(),
        timestamp: native::timestamp(timestamp_ticks),
        timestamp_ticks,
        event_type: kind.into(),
        operation: operation.into(),
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
    trace: Option<UserTrace>,
    consumer: Option<JoinHandle<Result<(), String>>>,
    name: String,
    rx: Receiver<SystemEvent>,
    dropped: Arc<AtomicU64>,
    errors: Arc<AtomicU64>,
    registry_path_gaps: Arc<AtomicU64>,
    received: Arc<AtomicU64>,
    report: BackendReport,
}

impl EtwSource {
    pub fn start(roots: &[String], registry_root: Option<String>, enabled: bool) -> Self {
        let (tx, rx) = mpsc::sync_channel(QUEUE_CAPACITY);
        let dropped = Arc::new(AtomicU64::new(0));
        let errors = Arc::new(AtomicU64::new(0));
        let registry_path_gaps = Arc::new(AtomicU64::new(0));
        let received = Arc::new(AtomicU64::new(0));
        let name = format!("Contain-{}", uuid::Uuid::new_v4());
        let mut source = Self {
            trace: None,
            consumer: None,
            name: name.clone(),
            rx,
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
            paths: HashMap::new(),
            pending: HashMap::new(),
            registry_context: Default::default(),
            roots,
            registry_root,
            devices,
            tx,
            dropped,
            errors,
            registry_path_gaps,
            received,
        }));
        let file_decoder = decoder.clone();
        let file = Provider::by_guid(FILE_PROVIDER)
            .any(0x1ef0)
            .level(4)
            .trace_flags(TraceFlags::EVENT_ENABLE_PROPERTY_PROCESS_START_KEY)
            .add_callback(move |record, locator| {
                if let Ok(mut decoder) = file_decoder.lock() {
                    decoder.file(record, locator);
                }
            })
            .build();
        let lifecycle_decoder = decoder.clone();
        let lifecycle = Provider::by_guid("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716")
            .any(0x30)
            .level(5)
            .add_callback(move |record, locator| {
                if let Ok(mut decoder) = lifecycle_decoder.lock() {
                    decoder.lifecycle(record, locator);
                }
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
            let registry = Provider::by_guid(REGISTRY_PROVIDER)
                .any(0x7301)
                .level(4)
                .trace_flags(TraceFlags::EVENT_ENABLE_PROPERTY_PROCESS_START_KEY)
                .add_callback(move |record, locator| {
                    if let Ok(mut decoder) = decoder.lock() {
                        decoder.registry(record, locator);
                    }
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
                let ready = native::synchronize_providers(&name, registry_enabled).and_then(|()| {
                    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(3);
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
        self.rx.try_iter().collect()
    }

    fn stop(&mut self) -> BackendReport {
        if let Some(trace) = self.trace.take() {
            let mut trace = Some(trace);
            // Stop the producer before closing the consumer so final ETW buffers can drain.
            match native::control_trace(&self.name, EVENT_TRACE_CONTROL_STOP) {
                Ok(lost) => {
                    self.report.etw_events_lost = Some(lost.events);
                    self.report.etw_buffers_lost = Some(lost.buffers);
                }
                Err(code) => {
                    self.report
                        .warnings
                        .push(format!("ETW stop/statistics failed: Win32 {code}"));
                    // Closing the consumer handle unblocks ProcessTrace even when stop failed.
                    drop(trace.take());
                }
            }
            if let Some(consumer) = self.consumer.take() {
                match consumer.join() {
                    Ok(Ok(())) => {}
                    result => {
                        self.errors.fetch_add(1, Ordering::Relaxed);
                        self.report
                            .warnings
                            .push(format!("ETW consumer ended with {result:?}"));
                    }
                }
            }
            drop(trace);
        }
        self.report.dropped_events = self.dropped.load(Ordering::Relaxed);
        self.report.events_received = self.received.load(Ordering::Relaxed);
        self.report.decode_errors = self.errors.load(Ordering::Relaxed);
        self.report.registry_path_gaps = self.registry_path_gaps.load(Ordering::Relaxed);
        self.report.clone()
    }
}

impl Drop for EtwSource {
    fn drop(&mut self) {
        self.stop();
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
            paths: HashMap::new(),
            pending: HashMap::new(),
            registry_context: Default::default(),
            roots: vec![],
            registry_root: None,
            devices: vec![],
            tx,
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
}
