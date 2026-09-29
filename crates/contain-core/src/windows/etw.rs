//! Manifest ETW providers, decoded through TDH by ferrisetw. No injection or elevation.
use super::native;
use crate::model::{AttributionEvidence, BackendReport, EvidenceSource, SystemEvent};
use crate::monitor::EventSource;
use ferrisetw::parser::Parser;
use ferrisetw::provider::{Provider, TraceFlags};
use ferrisetw::trace::{TraceProperties, TraceTrait};
use ferrisetw::{EventRecord, SchemaLocator, UserTrace};
use std::collections::HashMap;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicU64, Ordering},
    mpsc::{self, Receiver, SyncSender},
};
use std::thread::{self, JoinHandle};
use windows_sys::Win32::System::Diagnostics::Etw::EVENT_TRACE_CONTROL_STOP;

const FILE_PROVIDER: &str = "edd08927-9cc4-4e65-b970-c2560fb5c289";
const REGISTRY_PROVIDER: &str = "70eb4f03-c1de-4f73-a051-33d13d5413bd";
const QUEUE_CAPACITY: usize = 8192;
const OBJECT_CAPACITY: usize = 16384;

struct Decoder {
    paths: HashMap<u64, String>,
    roots: Vec<String>,
    registry_root: Option<String>,
    devices: Vec<(String, String)>,
    tx: SyncSender<SystemEvent>,
    dropped: Arc<AtomicU64>,
    errors: Arc<AtomicU64>,
}

impl Decoder {
    fn send(&self, event: SystemEvent) {
        if self.tx.try_send(event).is_err() {
            self.dropped.fetch_add(1, Ordering::Relaxed);
        }
    }

    fn file(&mut self, record: &EventRecord, locator: &SchemaLocator) {
        let id = record.event_id();
        if !matches!(id, 12 | 14 | 16 | 26 | 27 | 30) {
            return;
        }
        let Ok(schema) = locator.event_schema(record) else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let parser = Parser::create(record, &schema);
        let Ok(object) = parser.try_parse::<u64>("FileObject") else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        if id == 14 {
            self.paths.remove(&object);
            return;
        }
        let supplied_path = parser
            .try_parse::<String>("FileName")
            .or_else(|_| parser.try_parse::<String>("FilePath"))
            .ok()
            .map(|p| native::normalize_path(&p, &self.devices));
        if matches!(id, 12 | 30) {
            // A reused object must not keep its previous file name, including out-of-scope opens.
            self.paths.remove(&object);
            if let Some(path) = supplied_path
                .as_ref()
                .filter(|p| native::in_scope(p, &self.roots))
            {
                if self.paths.len() < OBJECT_CAPACITY {
                    self.paths.insert(object, path.clone());
                } else {
                    self.dropped.fetch_add(1, Ordering::Relaxed);
                }
            }
        }
        if id == 12 {
            return;
        } // Create/Open does not establish that a new file was created.
        let path = supplied_path.or_else(|| self.paths.get(&object).cloned());
        if id == 27 {
            self.paths.remove(&object);
        } // Do not guess whether FilePath is the old or new name.
        let Some(path) = path.filter(|p| native::in_scope(p, &self.roots)) else {
            return;
        };
        let timestamp_ticks = record.raw_timestamp().max(0) as u64;
        // Manifest v1 supplies the issuing thread; a kernel worker's header PID is not a writer.
        let writer = parser
            .try_parse::<u32>("IssuingThreadId")
            .ok()
            .and_then(|tid| native::writer_from_thread(tid, timestamp_ticks));
        let operation = match id {
            30 => "create_new_file",
            16 => "write_requested",
            26 => "delete_requested",
            27 => "rename_requested",
            _ => return,
        };
        self.send(make_event(
            timestamp_ticks,
            "file",
            operation,
            path,
            EvidenceSource::EtwFile,
            writer,
            None,
        ));
    }

    fn registry(&mut self, record: &EventRecord, locator: &SchemaLocator) {
        let Some(scope) = self.registry_root.as_ref() else {
            return;
        };
        let id = record.event_id();
        if !matches!(id, 1 | 3 | 5 | 6) {
            return;
        }
        let Ok(schema) = locator.event_schema(record) else {
            self.errors.fetch_add(1, Ordering::Relaxed);
            return;
        };
        let parser = Parser::create(record, &schema);
        let key = if id == 1 {
            match (
                parser.try_parse::<String>("BaseName"),
                parser.try_parse::<String>("RelativeName"),
            ) {
                (Ok(base), Ok(relative)) => format!(
                    "{}\\{}",
                    base.trim_end_matches('\\'),
                    relative.trim_start_matches('\\')
                ),
                _ => {
                    self.errors.fetch_add(1, Ordering::Relaxed);
                    return;
                }
            }
        } else {
            match parser.try_parse::<String>("KeyName") {
                Ok(key) => key,
                Err(_) => {
                    self.errors.fetch_add(1, Ordering::Relaxed);
                    return;
                }
            }
        };
        let key = key.to_lowercase();
        if !native::in_scope(&key, std::slice::from_ref(scope)) {
            return;
        }
        let resource = match parser.try_parse::<String>("ValueName") {
            Ok(value) => format!("{key}\\{value}"),
            Err(_) => key,
        };
        let timestamp_ticks = record.raw_timestamp().max(0) as u64;
        let writer = native::process_identity(record.process_id())
            .filter(|p| p.creation_time <= timestamp_ticks);
        let success = parser
            .try_parse::<u32>("Status")
            .ok()
            .map(|status| status == 0);
        let operation = match id {
            1 => "create_key",
            3 => "delete_key",
            5 => "set_value",
            6 => "delete_value",
            _ => return,
        };
        self.send(make_event(
            timestamp_ticks,
            "registry",
            operation,
            resource,
            EvidenceSource::EtwRegistry,
            writer,
            success,
        ));
    }
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
    report: BackendReport,
}

impl EtwSource {
    pub fn start(roots: &[String], registry_root: Option<String>, enabled: bool) -> Self {
        let (tx, rx) = mpsc::sync_channel(QUEUE_CAPACITY);
        let dropped = Arc::new(AtomicU64::new(0));
        let errors = Arc::new(AtomicU64::new(0));
        let name = format!("Contain-{}", uuid::Uuid::new_v4());
        let mut source = Self {
            trace: None,
            consumer: None,
            name: name.clone(),
            rx,
            dropped: dropped.clone(),
            errors: errors.clone(),
            report: BackendReport::default(),
        };
        if !enabled {
            source.report.etw_file = "disabled".into();
            source.report.etw_registry = "disabled".into();
            return source;
        }
        let devices = native::device_map();
        let roots = roots
            .iter()
            .map(|p| native::normalize_path(p, &devices))
            .collect();
        let registry_enabled = registry_root.is_some();
        let decoder = Arc::new(Mutex::new(Decoder {
            paths: HashMap::new(),
            roots,
            registry_root,
            devices,
            tx,
            dropped,
            errors,
        }));
        let file_decoder = decoder.clone();
        let file = Provider::by_guid(FILE_PROVIDER)
            .any(0x1eb0)
            .level(4)
            .trace_flags(TraceFlags::EVENT_ENABLE_PROPERTY_PROCESS_START_KEY)
            .add_callback(move |record, locator| {
                if let Ok(mut decoder) = file_decoder.lock() {
                    decoder.file(record, locator);
                }
            })
            .build();
        let mut builder = UserTrace::new()
            .named(name.clone())
            .enable(file)
            .set_trace_properties(TraceProperties {
                buffer_size: 64,
                min_buffer: 8,
                max_buffer: 64,
                ..Default::default()
            });
        if registry_enabled {
            let registry = Provider::by_guid(REGISTRY_PROVIDER)
                .any(0x5300)
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
                source.trace = Some(trace);
                source.consumer = Some(thread::spawn(move || {
                    UserTrace::process_from_handle(handle).map_err(|e| format!("{e:?}"))
                }));
                source.report.etw_file = "active".into();
                source.report.etw_registry = if registry_enabled {
                    "active"
                } else {
                    "not_requested"
                }
                .into();
            }
            Err(error) => {
                // ferrisetw can fail after StartTrace during provider enablement; clean only our UUID session.
                let _ = native::control_trace(&name, EVENT_TRACE_CONTROL_STOP);
                source.report.etw_file = "unavailable".into();
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
            // Stop the producer before closing the consumer so final ETW buffers can drain.
            match native::control_trace(&self.name, EVENT_TRACE_CONTROL_STOP) {
                Ok(lost) => self.report.etw_events_lost = Some(lost),
                Err(code) => self
                    .report
                    .warnings
                    .push(format!("ETW stop/statistics failed: Win32 {code}")),
            }
            if let Some(consumer) = self.consumer.take() {
                match consumer.join() {
                    Ok(Ok(())) => {}
                    result => self
                        .report
                        .warnings
                        .push(format!("ETW consumer ended with {result:?}")),
                }
            }
            drop(trace);
        }
        self.report.dropped_events = self.dropped.load(Ordering::Relaxed);
        self.report.decode_errors = self.errors.load(Ordering::Relaxed);
        self.report.clone()
    }
}

impl Drop for EtwSource {
    fn drop(&mut self) {
        self.stop();
    }
}
