//! Opt-in original-clock evidence. Fixed callback buffers feed a bounded disk
//! writer; no changes to the primary FILETIME stream or attribution.
use super::header;
use crate::windows::native;
use ferrisetw::trace::{FileTrace, TraceHeader, TraceTrait};
use serde::Serialize;
mod spool;
use std::{
    fs::{File, OpenOptions},
    io::{self, Write},
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread::{self, JoinHandle},
};

pub const ENV: &str = "CONTAIN_ETW_RAW_CLOCK_PATH";
const MEMORY_BUDGET: usize = 32 * 1024 * 1024;
const DISK_BUDGET: usize = 128 * 1024 * 1024;
const PROTOCOL: &str = "contain-raw-clock-companion-2";
const METADATA_BUDGET: usize = 64 * 1024;
const SERIALIZATION_BUFFER: usize = 64 * 1024;

// The wire layout is explicit: Rust padding/endian choices never reach disk.
#[derive(Clone, Copy)]
struct Row {
    sequence: u64,
    event_key: u64,
    timestamp: i64,
    provider: u128,
    event_id: u16,
    version: u8,
    channel: u8,
    level: u8,
    opcode: u8,
    task: u16,
    keyword: u64,
    process_id: u32,
    thread_id: u32,
}

#[derive(Clone, Default, Serialize)]
struct Stats {
    received: u64,
    filtered_nonprimary: u64,
    readiness_probe_records: u64,
    stored: u64,
    missing_event_key: u64,
    invalid_timestamp: u64,
    capacity_dropped: u64,
    duplicate_event_keys: Option<u64>,
    record_capacity: usize,
    byte_capacity: usize,
    bytes: usize,
    fixed_record_size: usize,
    allocated_record_bytes: usize,
    disk_byte_capacity: usize,
    serialization_buffer_bytes: usize,
    accepted: u64,
    queued: u64,
    queue_dropped: u64,
    quota_dropped: u64,
    writer_closed_dropped: u64,
    not_persisted: u64,
    writer_memory_bytes: usize,
}
struct Recorder {
    producer: spool::Producer,
    stats: Stats,
}
impl Recorder {
    fn new(producer: spool::Producer) -> Self {
        Self {
            producer,
            stats: Stats {
                record_capacity: spool::ROW_BUDGET,
                byte_capacity: MEMORY_BUDGET,
                fixed_record_size: spool::ROW_BYTES,
                allocated_record_bytes: spool::ALLOCATED_BYTES,
                writer_memory_bytes: spool::WRITER_MEMORY_BYTES,
                disk_byte_capacity: DISK_BUDGET,
                serialization_buffer_bytes: SERIALIZATION_BUFFER,
                ..Default::default()
            },
        }
    }
    fn push(&mut self, key: Option<u64>, mut row: Row) {
        self.stats.received += 1;
        row.sequence = self.stats.received;
        if !matches!(
            row.provider,
            0xedd08927_9cc4_4e65_b970_c2560fb5c289
                | 0x22fb2cd6_0e7b_422b_a0c7_2fad1fd0e716
                | 0x70eb4f03_c1de_4f73_a051_33d13d5413bd
        ) {
            self.stats.filtered_nonprimary += 1;
            return;
        }
        let Some(key) = key.filter(|key| *key != 0) else {
            self.stats.missing_event_key += 1;
            return;
        };
        row.event_key = key;
        if row.timestamp <= 0 {
            self.stats.invalid_timestamp += 1;
        }
        self.producer.push(row);
    }
    fn seal(&mut self) {
        self.producer.close();
        self.refresh();
    }
    fn refresh(&mut self) {
        self.stats.accepted = self.producer.accepted;
        self.stats.queued = self.producer.queued;
        self.stats.queue_dropped = self.producer.queue_dropped;
        self.stats.quota_dropped = self.producer.quota_dropped;
        self.stats.writer_closed_dropped = self.producer.writer_closed_dropped;
        self.stats.capacity_dropped = self.stats.queue_dropped + self.stats.quota_dropped;
    }
    fn finalized(&mut self, outcome: &spool::Outcome) {
        self.refresh();
        self.stats.stored = outcome.records;
        self.stats.bytes = outcome.bytes as usize;
        self.stats.not_persisted = self.stats.accepted.saturating_sub(outcome.records);
        // All-key duplicate validation is performed by the independent bounded
        // binary reader. Absence of a producer-side check is never reported as 0.
        self.stats.duplicate_event_keys = None;
    }
}

fn finalized_stats(recorder: &Mutex<Recorder>, outcome: &spool::Outcome) -> Stats {
    let mut recorder = recorder
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    recorder.finalized(outcome);
    // Return only an owned copy. A timed-out consumer can still enter a callback;
    // it must never wait on the metadata writer while this mutex is held.
    recorder.stats.clone()
}

struct BoundedMetadata {
    bytes: Vec<u8>,
}
impl BoundedMetadata {
    fn new() -> Self {
        Self {
            bytes: Vec::with_capacity(METADATA_BUDGET),
        }
    }
}
impl Write for BoundedMetadata {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if self.bytes.len().saturating_add(bytes.len()) > METADATA_BUDGET {
            return Err(io::Error::other("raw clock metadata budget exhausted"));
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

pub(crate) fn configured() -> bool {
    std::env::var_os(ENV).is_some()
}

fn reserve(path: &Path, roots: &[String]) -> Result<(PathBuf, File), String> {
    if !path.is_absolute() {
        return Err("raw clock destination must be absolute".into());
    }
    let parent = path
        .parent()
        .ok_or("raw clock destination has no parent")?
        .canonicalize()
        .map_err(|e| e.to_string())?;
    let name = path
        .file_name()
        .ok_or("raw clock destination has no file name")?;
    let spelling = name.to_string_lossy();
    if spelling.contains(':') || spelling.ends_with(['.', ' ']) {
        return Err(
            "raw clock destination may not use alternate streams or trailing dot/space aliases"
                .into(),
        );
    }
    let path = parent.join(name);
    let normalized = native::normalize_path(&path.to_string_lossy(), &[]);
    let roots: Vec<_> = roots
        .iter()
        .map(|root| {
            // Compare both sides after the same filesystem resolution. Lexical
            // normalization alone cannot expand a short-name or directory alias.
            let root = Path::new(root)
                .canonicalize()
                .map_err(|e| format!("resolving raw clock watched root: {e}"))?;
            Ok(native::normalize_path(&root.to_string_lossy(), &[]))
        })
        .collect::<Result<_, String>>()?;
    if native::in_scope(&normalized, &roots) {
        return Err("raw clock destination must be outside watched roots".into());
    }
    let file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&path)
        .map_err(|e| format!("creating raw clock evidence without overwrite: {e}"))?;
    Ok((path, file))
}

pub(crate) struct Companion {
    trace: Option<FileTrace>,
    consumer: Option<JoinHandle<Result<(), String>>>,
    recorder: Arc<Mutex<Recorder>>,
    ready: Arc<AtomicBool>,
    path: PathBuf,
    file: Option<File>,
    rows_path: PathBuf,
    writer: Option<spool::Worker>,
    session: String,
    actual: TraceHeader,
}
impl Companion {
    pub(crate) fn open(session: &str, roots: &[String], probe: u128) -> Result<Self, String> {
        let path = std::env::var_os(ENV).ok_or("raw clock destination missing")?;
        let (path, mut file) = reserve(Path::new(&path), roots)?;
        // Retain an explicit incomplete artifact even if startup fails or exits.
        persist_startup_metadata(
            &file,
            &serde_json::json!({"schema_version":2,"protocol":PROTOCOL,"session_name":session,"ready":false,"stopped":false,"errors":["startup incomplete"],"records":[]}),
        )?;
        let rows_path = path.with_file_name(format!(
            "{}.rows.bin",
            path.file_name().unwrap().to_string_lossy()
        ));
        let (rows_path, rows_file) = reserve(&rows_path, roots)
            .map_err(|error| retain_startup_failure(&mut file, session, None, error))?;
        let (producer, writer) = spool::start(rows_file).map_err(|error| {
            retain_startup_failure(
                &mut file,
                session,
                None,
                format!("raw clock writer startup: {error}"),
            )
        })?;
        let recorder = Arc::new(Mutex::new(Recorder::new(producer)));
        let sink = recorder.clone();
        let ready = Arc::new(AtomicBool::new(false));
        let signal = ready.clone();
        let (trace, handle) = FileTrace::subscribe(session, true, move |record, _| {
            if record.provider_id().to_u128() == probe {
                sink.lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner)
                    .stats
                    .readiness_probe_records += 1;
                signal.store(true, Ordering::Release);
                return;
            }
            let row = Row {
                sequence: 0,
                event_key: 0,
                timestamp: record.raw_timestamp(),
                provider: record.provider_id().to_u128(),
                event_id: record.event_id(),
                version: record.version(),
                channel: record.channel(),
                level: record.level(),
                opcode: record.opcode(),
                task: record.task(),
                keyword: record.keyword(),
                process_id: record.process_id(),
                thread_id: record.thread_id(),
            };
            sink.lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .push(record.event_key(), row);
        })
        .map_err(|e| {
            retain_startup_failure(
                &mut file,
                session,
                None,
                format!("raw clock OpenTrace failed: {e:?}"),
            )
        })?;
        let actual = trace.trace_header().ok_or_else(|| {
            retain_startup_failure(
                &mut file,
                session,
                None,
                "raw clock actual OpenTrace header missing".into(),
            )
        })?;
        validate_header(actual)
            .map_err(|error| retain_startup_failure(&mut file, session, Some(actual), error))?;
        let consumer = thread::Builder::new()
            .name("contain-raw-clock".into())
            .spawn(move || {
                FileTrace::process_from_handle(handle)
                    .map_err(|e| format!("raw clock ProcessTrace: {e:?}"))
            })
            .map_err(|e| {
                retain_startup_failure(
                    &mut file,
                    session,
                    Some(actual),
                    format!("Raw consumer thread startup failed: {e}"),
                )
            })?;
        Ok(Self {
            trace: Some(trace),
            consumer: Some(consumer),
            recorder,
            ready,
            path,
            file: Some(file),
            rows_path,
            writer: Some(writer),
            session: session.into(),
            actual,
        })
    }
    pub(crate) fn ready(&self) -> bool {
        self.ready.load(Ordering::Acquire)
            && self
                .consumer
                .as_ref()
                .is_some_and(|consumer| !consumer.is_finished())
    }
    pub(crate) fn finish(mut self, producer_stopped: bool) -> serde_json::Value {
        let mut errors = Vec::new();
        if !producer_stopped {
            errors.push("Producer STOP failed; raw consumer aborted before join; tail completeness is unverified".into());
        }
        let stopped = match finish_consumer(self.consumer.take().unwrap(), producer_stopped, || {
            self.trace.take();
        }) {
            Ok(()) => true,
            Err(error) => {
                errors.push(error);
                false
            }
        };
        let ready = self.ready.load(Ordering::Acquire);
        if !ready {
            errors.push("raw clock consumer never passed readiness".into());
        }
        let mut recorder = self
            .recorder
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        recorder.seal();
        drop(recorder);
        let outcome = self
            .writer
            .take()
            .unwrap()
            .finish(std::time::Duration::from_secs(30));
        let stats = finalized_stats(&self.recorder, &outcome);
        let stats_final_available = stopped && outcome.finished;
        if let Some(error) = &outcome.error {
            errors.push(error.clone());
        }
        if !outcome.drained {
            errors.push("raw clock writer did not complete its drain".into());
        }
        let binary = serde_json::json!({
            "file_name":self.rows_path.file_name().unwrap().to_string_lossy(),
            "format":"contain-raw-clock-le64-1", "record_size":spool::ROW_BYTES,
            "records":outcome.records, "bytes":outcome.bytes,
            "sha256":outcome.sha256, "blake3":outcome.blake3,
            "complete":stats_final_available && outcome.drained && stats.not_persisted == 0,
            "metadata_byte_capacity":METADATA_BUDGET,
            "byte_capacity":spool::ROW_BUDGET * spool::ROW_BYTES,
        });
        if stats.missing_event_key > 0
            || stats.invalid_timestamp > 0
            || stats.capacity_dropped > 0
            || stats.not_persisted > 0
            || stats.writer_closed_dropped > 0
        {
            errors.push(format!("raw clock evidence incomplete or ambiguous: missing_key={}, invalid_timestamp={}, capacity_dropped={}, not_persisted={}, writer_closed_dropped={}", stats.missing_event_key, stats.invalid_timestamp, stats.capacity_dropped, stats.not_persisted, stats.writer_closed_dropped));
        }
        #[derive(Serialize)]
        struct Document<'a> {
            schema_version: u32,
            protocol: &'static str,
            session_name: &'a str,
            header: crate::model::OpenTraceHeader,
            ready: bool,
            stopped: bool,
            producer_stopped: bool,
            header_sample_phase: &'static str,
            consumer_loss_counters_available: bool,
            stats_final_available: bool,
            stats: &'a Stats,
            errors: &'a [String],
            binary: &'a serde_json::Value,
            writer: &'a spool::Outcome,
        }
        let document = Document {
            schema_version: 2,
            protocol: PROTOCOL,
            session_name: &self.session,
            header: header(self.actual),
            ready,
            stopped,
            producer_stopped,
            header_sample_phase: "open_before_process_trace",
            consumer_loss_counters_available: false,
            stats_final_available,
            stats: &stats,
            errors: &errors,
            binary: &binary,
            writer: &outcome,
        };
        // Bound serialization before transferring file ownership to the writer.
        // No seek, truncate, write or sync operation runs on this caller thread.
        let mut metadata = BoundedMetadata::new();
        let result = serde_json::to_writer(&mut metadata, &document)
            .map_err(|error| error.to_string())
            .and_then(|()| {
                spool::metadata(self.file.take().unwrap(), metadata.bytes)
                    .map_err(|error| format!("raw clock metadata writer startup: {error}"))
            });
        let metadata_writer = match result {
            Ok(writer) => writer.finish(std::time::Duration::from_secs(30)),
            Err(error) => spool::Outcome {
                error: Some(error),
                ..Default::default()
            },
        };
        let written = metadata_writer.finished
            && metadata_writer.drained
            && !metadata_writer.timed_out
            && metadata_writer.error.is_none();
        if !written {
            errors.push(format!(
                "raw clock sidecar persistence incomplete; artifact retained: {}",
                metadata_writer
                    .error
                    .as_deref()
                    .unwrap_or("metadata writer did not finish")
            ));
        }
        let bytes = metadata_writer.bytes;
        let hash = &metadata_writer.blake3;
        let sha256 = &metadata_writer.sha256;
        let metadata_persistence = serde_json::json!({
            "bytes":metadata_writer.bytes, "write_calls":metadata_writer.write_calls,
            "write_elapsed_ns":metadata_writer.write_elapsed_ns,
            "drain_elapsed_ns":metadata_writer.drain_elapsed_ns,
            "finished":metadata_writer.finished, "drained":metadata_writer.drained,
            "timed_out":metadata_writer.timed_out,
            "cancel_requested":metadata_writer.cancel_requested,
            "cancel_error":metadata_writer.cancel_error, "error":metadata_writer.error,
        });
        serde_json::json!({"protocol":PROTOCOL,"session_name":self.session,"path":self.path,"header":header(self.actual),"ready":ready,"stopped":stopped,"producer_stopped":producer_stopped,
            "header_sample_phase":"open_before_process_trace","consumer_loss_counters_available":false,"stats_final_available":stats_final_available,
            "loss_scope":"Only exact-key coverage of retained primary records can be checked; no consumer-wide loss-rate claim",
            "stats":stats,"binary":binary,"writer":outcome,"metadata_writer":metadata_persistence,"errors":errors,"written":written,"file_bytes":bytes,"blake3":hash,"sha256":sha256})
    }
}

fn retain_startup_failure(
    file: &mut File,
    session: &str,
    actual: Option<TraceHeader>,
    error: String,
) -> String {
    let result = persist_startup_metadata(
        file,
        &serde_json::json!({"schema_version":2,"protocol":PROTOCOL,"session_name":session,
            "header":actual.map(header),"ready":false,"stopped":false,"producer_stopped":false,
            "errors":[error],"records":[]}),
    );
    match result {
        Ok(()) => error,
        Err(persistence) => {
            format!("{error}; retaining startup failure also failed: {persistence}")
        }
    }
}

fn persist_startup_metadata(file: &File, document: &impl Serialize) -> Result<(), String> {
    let mut bytes = BoundedMetadata::new();
    serde_json::to_writer(&mut bytes, document).map_err(|error| error.to_string())?;
    let clone = file.try_clone().map_err(|error| error.to_string())?;
    let outcome = spool::metadata(clone, bytes.bytes)
        .map_err(|error| error.to_string())?
        .finish(std::time::Duration::from_secs(30));
    if outcome.finished && outcome.drained && !outcome.timed_out && outcome.error.is_none() {
        Ok(())
    } else {
        Err(outcome
            .error
            .unwrap_or_else(|| "startup metadata persistence incomplete".into()))
    }
}

fn finish_consumer(
    consumer: JoinHandle<Result<(), String>>,
    producer_stopped: bool,
    close: impl FnOnce(),
) -> Result<(), String> {
    // A failed STOP leaves the producer live: close our subscription before
    // waiting. After a successful STOP, retain callbacks until buffered delivery
    // has ended, then close. CloseTrace alone is never evidence of a complete tail.
    let mut close = Some(close);
    if !producer_stopped {
        close.take().unwrap()();
    }
    let started = std::time::Instant::now();
    while !consumer.is_finished() && started.elapsed() < std::time::Duration::from_secs(30) {
        thread::sleep(std::time::Duration::from_millis(1));
    }
    if !consumer.is_finished() {
        if let Some(close) = close.take() {
            close();
        }
        let grace = std::time::Instant::now();
        while !consumer.is_finished() && grace.elapsed() < std::time::Duration::from_secs(1) {
            thread::sleep(std::time::Duration::from_millis(1));
        }
        // ProcessTrace/driver non-response must not cause an unbounded join.
        if consumer.is_finished() {
            let _ = consumer.join();
        }
        return Err("raw clock consumer drain deadline exceeded; tail incomplete".into());
    }
    let result = consumer.join();
    if let Some(close) = close {
        close();
    }
    match result {
        Ok(result) => result,
        Err(_) => Err("raw clock consumer panicked".into()),
    }
}
impl Drop for Companion {
    fn drop(&mut self) {
        // Also handles startup/capture unwinding. Close this consumer only;
        // producer shutdown belongs to EtwSource and its UUID session.
        self.trace.take();
        if let Some(consumer) = self.consumer.take() {
            let _ = finish_consumer(consumer, false, || {});
        }
        self.recorder
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .seal();
        self.writer.take();
    }
}

fn validate_header(header: TraceHeader) -> Result<(), String> {
    use windows_sys::Win32::System::Diagnostics::Etw::{
        PROCESS_TRACE_MODE_EVENT_RECORD, PROCESS_TRACE_MODE_RAW_TIMESTAMP,
        PROCESS_TRACE_MODE_REAL_TIME,
    };
    let flags = PROCESS_TRACE_MODE_EVENT_RECORD
        | PROCESS_TRACE_MODE_REAL_TIME
        | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
    if header.process_trace_mode & flags != flags
        || header.reserved_flags != 1
        || header.perf_frequency <= 0
    {
        return Err("raw clock consumer header does not establish original QPC delivery".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn failed_startup_retains_the_actual_header_and_reason_without_a_ready_claim() {
        let path = std::env::temp_dir().join(format!(
            "contain-clock-failure-{}.json",
            uuid::Uuid::new_v4()
        ));
        let (_, mut file) = reserve(&path, &[]).unwrap();
        file.write_all(b"incomplete placeholder").unwrap();
        let bad = TraceHeader {
            perf_frequency: 0,
            ..actual()
        };
        let reason = validate_header(bad).unwrap_err();
        assert_eq!(
            retain_startup_failure(&mut file, "test-session", Some(bad), reason.clone()),
            reason
        );
        drop(file);
        let retained: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        assert_eq!(retained["ready"], false);
        assert_eq!(retained["producer_stopped"], false);
        assert_eq!(retained["errors"][0], reason);
        assert_eq!(retained["header"]["perf_frequency"], "0");
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn failed_producer_stop_closes_the_consumer_before_waiting() {
        let (signal, receive) = std::sync::mpsc::sync_channel(1);
        let consumer = thread::spawn(move || {
            receive
                .recv_timeout(std::time::Duration::from_secs(1))
                .map_err(|e| e.to_string())
        });
        assert!(finish_consumer(consumer, false, || signal.send(()).unwrap()).is_ok());
    }
    #[test]
    fn successful_producer_stop_finishes_buffered_delivery_before_close() {
        let completed = Arc::new(AtomicBool::new(false));
        let written = completed.clone();
        let consumer = thread::spawn(move || {
            written.store(true, Ordering::Release);
            Ok(())
        });
        finish_consumer(consumer, true, || {
            assert!(completed.load(Ordering::Acquire))
        })
        .unwrap();
    }
    pub(super) fn row(timestamp: i64) -> Row {
        Row {
            sequence: 0,
            event_key: 0,
            timestamp,
            provider: 0xedd08927_9cc4_4e65_b970_c2560fb5c289,
            event_id: 16,
            version: 1,
            channel: 2,
            level: 4,
            opcode: 5,
            task: 6,
            keyword: u64::MAX,
            process_id: 42,
            thread_id: 43,
        }
    }
    fn actual() -> TraceHeader {
        TraceHeader {
            process_trace_mode: 0x10001100,
            reserved_flags: 1,
            perf_frequency: 10_000_000,
            start_time: 134_350_000_000_000_017,
            boot_time: 0,
            end_time: 0,
            buffer_size: 65536,
            log_file_mode: 256,
            pointer_size: 8,
            events_lost: 0,
            buffers_lost: 0,
            timer_resolution: 0,
            number_of_processors: 1,
            provider_version: 1,
        }
    }
    #[test]
    fn raw_delivery_requires_the_actual_mode_frequency_and_clock_type() {
        assert!(validate_header(actual()).is_ok());
        for header in [
            TraceHeader {
                process_trace_mode: 0x10000100,
                ..actual()
            },
            TraceHeader {
                reserved_flags: 2,
                ..actual()
            },
            TraceHeader {
                perf_frequency: 0,
                ..actual()
            },
            TraceHeader {
                perf_frequency: -1,
                ..actual()
            },
        ] {
            assert!(validate_header(header).is_err());
        }
    }
    #[test]
    fn all_source_providers_are_retained_and_invalid_input_remains_visible() {
        let path = std::env::temp_dir().join(format!(
            "contain-clock-records-{}.bin",
            uuid::Uuid::new_v4()
        ));
        let (_, file) = reserve(&path, &[]).unwrap();
        let (producer, worker) = spool::start(file).unwrap();
        let mut recorder = Recorder::new(producer);
        recorder.push(
            None,
            Row {
                provider: 0,
                ..row(1)
            },
        );
        recorder.push(None, row(2));
        recorder.push(Some(9), row(134_350_000_000_000_017));
        recorder.push(Some(9), row(-1));
        recorder.push(
            Some(10),
            Row {
                provider: 0x22fb2cd6_0e7b_422b_a0c7_2fad1fd0e716,
                ..row(3)
            },
        );
        recorder.push(
            Some(11),
            Row {
                provider: 0x70eb4f03_c1de_4f73_a051_33d13d5413bd,
                ..row(4)
            },
        );
        recorder.seal();
        let outcome = worker.finish(std::time::Duration::from_secs(5));
        recorder.finalized(&outcome);
        assert!(outcome.drained);
        assert_eq!(recorder.stats.received, 6);
        assert_eq!(recorder.stats.filtered_nonprimary, 1);
        assert_eq!(recorder.stats.missing_event_key, 1);
        assert_eq!(recorder.stats.invalid_timestamp, 1);
        assert_eq!(recorder.stats.stored, 4);
        assert_eq!(recorder.stats.duplicate_event_keys, None);
        assert_eq!(recorder.stats.not_persisted, 0);
        assert_eq!(recorder.stats.capacity_dropped, 0);
        assert_eq!(std::fs::metadata(&path).unwrap().len(), 4 * 64);
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn sidecar_never_overwrites_or_writes_inside_watched_roots_or_aliases() {
        let folder =
            std::env::temp_dir().join(format!("contain-clock-test-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&folder).unwrap();
        let path = folder.join("original.json");
        std::fs::write(&path, b"existing evidence").unwrap();
        assert!(reserve(&path, &[]).is_err());
        assert_eq!(std::fs::read(&path).unwrap(), b"existing evidence");
        assert!(
            reserve(
                &folder.join("watched.json"),
                &[folder.to_string_lossy().into_owned()]
            )
            .is_err()
        );
        assert!(!folder.join("watched.json").exists());
        let child = folder.join("child");
        std::fs::create_dir(&child).unwrap();
        let alias_destination = folder.join("watched-alias.json");
        assert!(
            reserve(
                &alias_destination,
                &[child.join("..").to_string_lossy().into_owned()]
            )
            .is_err()
        );
        assert!(!alias_destination.exists());
        let unresolved_destination = folder.join("unresolved-root.json");
        assert!(
            reserve(
                &unresolved_destination,
                &[folder.join("missing-root").to_string_lossy().into_owned()]
            )
            .unwrap_err()
            .contains("resolving raw clock watched root")
        );
        assert!(!unresolved_destination.exists());
        for name in ["original.json:stream", "ambiguous.json.", "ambiguous.json "] {
            assert!(reserve(&folder.join(name), &[]).is_err());
        }
        std::fs::remove_dir(child).unwrap();
        std::fs::remove_file(path).unwrap();
        std::fs::remove_dir(folder).unwrap();
    }
    #[test]
    fn disk_quota_preserves_partial_evidence_and_hashes_only_written_bytes() {
        let mut metadata = BoundedMetadata::new();
        metadata
            .write_all(&vec![b'a'; METADATA_BUDGET - 1])
            .unwrap();
        assert!(metadata.write_all(b"bc").is_err());
        assert_eq!(metadata.bytes.len(), METADATA_BUDGET - 1);
        let path =
            std::env::temp_dir().join(format!("contain-clock-limit-{}.json", uuid::Uuid::new_v4()));
        let (_, mut file) = reserve(&path, &[]).unwrap();
        file.write_all(&[b'x'; 256]).unwrap();
        let result = spool::metadata(file, b"abc".to_vec())
            .unwrap()
            .finish(std::time::Duration::from_secs(5));
        assert!(result.finished && result.drained);
        assert_eq!(result.bytes, 3);
        assert_eq!(
            result.sha256.unwrap(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(std::fs::read(&path).unwrap(), b"abc");
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn callbacks_after_seal_are_counted_when_terminal_stats_are_read() {
        let path =
            std::env::temp_dir().join(format!("contain-clock-late-{}.bin", uuid::Uuid::new_v4()));
        let (_, file) = reserve(&path, &[]).unwrap();
        let (producer, worker) = spool::start(file).unwrap();
        let mut recorder = Recorder::new(producer);
        recorder.push(Some(1), row(1));
        recorder.seal();
        recorder.push(Some(2), row(2));
        assert_eq!(recorder.stats.writer_closed_dropped, 0);
        let outcome = worker.finish(std::time::Duration::from_secs(5));
        recorder.finalized(&outcome);
        assert_eq!(recorder.stats.writer_closed_dropped, 1);
        assert_eq!(recorder.stats.received, 2);
        assert_eq!(recorder.stats.stored, 1);
        assert_eq!(
            recorder.stats.received,
            recorder.stats.accepted + recorder.stats.writer_closed_dropped
        );
        std::fs::remove_file(path).unwrap();
    }
    #[test]
    fn finalized_stats_release_callback_lock_before_metadata_persistence() {
        let path =
            std::env::temp_dir().join(format!("contain-clock-lock-{}.bin", uuid::Uuid::new_v4()));
        let (_, file) = reserve(&path, &[]).unwrap();
        let (producer, worker) = spool::start(file).unwrap();
        let recorder = Mutex::new(Recorder::new(producer));
        recorder.lock().unwrap().seal();
        let outcome = worker.finish(std::time::Duration::from_secs(5));
        let snapshot = finalized_stats(&recorder, &outcome);
        // Persistence may retain this snapshot for its whole deadline. The live
        // recorder remains immediately available to late callbacks meanwhile.
        let mut callback = recorder
            .try_lock()
            .expect("metadata snapshot must not retain callback lock");
        callback.push(Some(1), row(1));
        assert_eq!(snapshot.received, 0);
        assert_eq!(callback.stats.received, 1);
        assert_eq!(callback.producer.writer_closed_dropped, 1);
        std::fs::remove_file(path).unwrap();
    }
}
