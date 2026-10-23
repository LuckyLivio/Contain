//! Opt-in original-clock evidence. Separate consumer, finite fixed-size memory,
//! bounded sidecar, no changes to the primary FILETIME stream or attribution.
use super::header;
use crate::windows::native;
use ferrisetw::trace::{FileTrace, TraceHeader, TraceTrait};
use serde::ser::SerializeStruct;
use serde::{Serialize, Serializer};
use sha2::Digest;
use std::{
    fs::{File, OpenOptions},
    io::{self, BufWriter, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread::{self, JoinHandle},
};

pub const ENV: &str = "CONTAIN_ETW_RAW_CLOCK_PATH";
const RECORD_CAPACITY: usize = 262_144;
const MEMORY_BUDGET: usize = 32 * 1024 * 1024;
const DISK_BUDGET: usize = 128 * 1024 * 1024;
const PROTOCOL: &str = "contain-raw-clock-companion-1";
const SERIALIZATION_BUFFER: usize = 64 * 1024;

// No owned variable-length callback payloads. Vec backing storage is allocated
// once; later records cannot enlarge it. Serialization occurs after STOP/join.
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
impl Serialize for Row {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut value = serializer.serialize_struct("RawClockRecord", 13)?;
        value.serialize_field("sequence", &self.sequence)?;
        value.serialize_field("event_key", &format!("{:016x}", self.event_key))?;
        value.serialize_field("header_timestamp", &self.timestamp.to_string())?;
        value.serialize_field(
            "provider",
            &uuid::Uuid::from_u128(self.provider).to_string(),
        )?;
        value.serialize_field("event_id", &self.event_id)?;
        value.serialize_field("version", &self.version)?;
        value.serialize_field("channel", &self.channel)?;
        value.serialize_field("level", &self.level)?;
        value.serialize_field("opcode", &self.opcode)?;
        value.serialize_field("task", &self.task)?;
        value.serialize_field("keyword", &format!("{:016x}", self.keyword))?;
        value.serialize_field("process_id", &self.process_id)?;
        value.serialize_field("thread_id", &self.thread_id)?;
        value.end()
    }
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
    duplicate_event_keys: u64,
    record_capacity: usize,
    byte_capacity: usize,
    bytes: usize,
    fixed_record_size: usize,
    allocated_record_bytes: usize,
    disk_byte_capacity: usize,
    serialization_buffer_bytes: usize,
}
struct Recorder {
    rows: Vec<Row>,
    stats: Stats,
}
impl Recorder {
    fn new(capacity: usize) -> Self {
        assert!(
            capacity <= RECORD_CAPACITY
                && capacity * size_of::<Row>() + SERIALIZATION_BUFFER <= MEMORY_BUDGET
        );
        let rows = Vec::with_capacity(capacity);
        let stats = Stats {
            record_capacity: capacity,
            byte_capacity: MEMORY_BUDGET,
            fixed_record_size: size_of::<Row>(),
            allocated_record_bytes: rows.capacity() * size_of::<Row>(),
            disk_byte_capacity: DISK_BUDGET,
            serialization_buffer_bytes: SERIALIZATION_BUFFER,
            ..Default::default()
        };
        Self { rows, stats }
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
        let Some(key) = key else {
            self.stats.missing_event_key += 1;
            return;
        };
        row.event_key = key;
        if row.timestamp <= 0 {
            self.stats.invalid_timestamp += 1;
        }
        if self.rows.len() >= self.stats.record_capacity {
            self.stats.capacity_dropped += 1;
            return;
        }
        self.rows.push(row);
        self.stats.stored += 1;
        self.stats.bytes = self.rows.len() * size_of::<Row>();
    }
    fn finish(&mut self) {
        // In-place ordering is only diagnostic serialization. Sequence retains
        // raw-consumer delivery order; primary events are never reordered.
        self.rows.sort_unstable_by_key(|row| row.event_key);
        self.stats.duplicate_event_keys = self
            .rows
            .windows(2)
            .filter(|pair| pair[0].event_key == pair[1].event_key)
            .count() as u64;
    }
}

struct LimitedWriter<'a> {
    file: &'a mut File,
    bytes: usize,
    hash: blake3::Hasher,
    sha256: sha2::Sha256,
    budget: usize,
}
impl Write for LimitedWriter<'_> {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if self.bytes.saturating_add(bytes.len()) > self.budget {
            return Err(io::Error::other("raw clock sidecar disk budget exhausted"));
        }
        let count = self.file.write(bytes)?;
        self.bytes += count;
        self.hash.update(&bytes[..count]);
        self.sha256.update(&bytes[..count]);
        Ok(count)
    }
    fn flush(&mut self) -> io::Result<()> {
        self.file.flush()
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
        .map(|root| native::normalize_path(root, &[]))
        .collect();
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
    file: File,
    session: String,
    actual: TraceHeader,
}
impl Companion {
    pub(crate) fn open(session: &str, roots: &[String], probe: u128) -> Result<Self, String> {
        let path = std::env::var_os(ENV).ok_or("raw clock destination missing")?;
        let (path, mut file) = reserve(Path::new(&path), roots)?;
        // Retain an explicit incomplete artifact even if startup fails or exits.
        serde_json::to_writer(&mut file, &serde_json::json!({"schema_version":1,"protocol":PROTOCOL,"session_name":session,"ready":false,"stopped":false,"errors":["startup incomplete"],"records":[]})).map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
        let recorder = Arc::new(Mutex::new(Recorder::new(RECORD_CAPACITY)));
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
            file,
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
        recorder.finish();
        if recorder.stats.missing_event_key > 0
            || recorder.stats.invalid_timestamp > 0
            || recorder.stats.capacity_dropped > 0
            || recorder.stats.duplicate_event_keys > 0
        {
            errors.push(format!("raw clock evidence incomplete or ambiguous: missing_key={}, invalid_timestamp={}, capacity_dropped={}, duplicate_key={}", recorder.stats.missing_event_key, recorder.stats.invalid_timestamp, recorder.stats.capacity_dropped, recorder.stats.duplicate_event_keys));
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
            stats: &'a Stats,
            errors: &'a [String],
            records: &'a [Row],
        }
        let document = Document {
            schema_version: 1,
            protocol: PROTOCOL,
            session_name: &self.session,
            header: header(self.actual),
            ready,
            stopped,
            producer_stopped,
            header_sample_phase: "open_before_process_trace",
            consumer_loss_counters_available: false,
            stats: &recorder.stats,
            errors: &errors,
            records: &recorder.rows,
        };
        let mut writer = LimitedWriter {
            file: &mut self.file,
            bytes: 0,
            hash: blake3::Hasher::new(),
            sha256: sha2::Sha256::new(),
            budget: DISK_BUDGET,
        };
        let result = (|| -> Result<(), String> {
            writer
                .file
                .seek(SeekFrom::Start(0))
                .map_err(|e| e.to_string())?;
            writer.file.set_len(0).map_err(|e| e.to_string())?;
            {
                let mut buffer = BufWriter::with_capacity(SERIALIZATION_BUFFER, &mut writer);
                serde_json::to_writer(&mut buffer, &document).map_err(|e| e.to_string())?;
                buffer.flush().map_err(|e| e.to_string())?;
            }
            writer.file.sync_all().map_err(|e| e.to_string())?;
            Ok(())
        })();
        let bytes = writer.bytes;
        let hash = writer.hash.finalize().to_hex().to_string();
        let sha256 = format!("{:x}", writer.sha256.finalize());
        let written = result.is_ok();
        if let Err(error) = result {
            errors.push(format!(
                "raw clock sidecar persistence failed; partial artifact retained: {error}"
            ));
        }
        serde_json::json!({"protocol":PROTOCOL,"session_name":self.session,"path":self.path,"header":header(self.actual),"ready":ready,"stopped":stopped,"producer_stopped":producer_stopped,
            "header_sample_phase":"open_before_process_trace","consumer_loss_counters_available":false,
            "loss_scope":"Only exact-key coverage of retained primary records can be checked; no consumer-wide loss-rate claim",
            "stats":recorder.stats,"errors":errors,"written":written,"file_bytes":bytes,"blake3":hash,"sha256":sha256})
    }
}

fn retain_startup_failure(
    file: &mut File,
    session: &str,
    actual: Option<TraceHeader>,
    error: String,
) -> String {
    let result = (|| -> Result<(), String> {
        file.seek(SeekFrom::Start(0)).map_err(|e| e.to_string())?;
        file.set_len(0).map_err(|e| e.to_string())?;
        serde_json::to_writer(
            &mut *file,
            &serde_json::json!({"schema_version":1,"protocol":PROTOCOL,"session_name":session,
            "header":actual.map(header),"ready":false,"stopped":false,"producer_stopped":false,
            "errors":[error],"records":[]}),
        )
        .map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())
    })();
    match result {
        Ok(()) => error,
        Err(persistence) => {
            format!("{error}; retaining startup failure also failed: {persistence}")
        }
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
            let _ = consumer.join();
        }
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
    fn row(timestamp: i64) -> Row {
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
    fn unrelated_trace_metadata_is_not_misreported_as_a_missing_primary_key() {
        let mut recorder = Recorder::new(3);
        recorder.push(
            None,
            Row {
                provider: 0,
                ..row(10)
            },
        );
        assert_eq!(recorder.stats.received, 1);
        assert_eq!(recorder.stats.missing_event_key, 0);
        assert_eq!(recorder.stats.filtered_nonprimary, 1);
        assert!(recorder.rows.is_empty());
    }
    #[test]
    fn original_records_are_bounded_and_missing_duplicate_invalid_keys_are_explicit() {
        let mut recorder = Recorder::new(3);
        recorder.push(Some(9), row(134_350_000_000_000_017));
        recorder.push(None, row(2));
        recorder.push(Some(9), row(3));
        recorder.push(Some(10), row(-1));
        recorder.push(Some(11), row(5));
        recorder.finish();
        assert_eq!(recorder.stats.received, 5);
        assert_eq!(recorder.stats.stored, 3);
        assert_eq!(recorder.stats.missing_event_key, 1);
        assert_eq!(recorder.stats.invalid_timestamp, 1);
        assert_eq!(recorder.stats.capacity_dropped, 1);
        assert_eq!(
            recorder.stats.received,
            recorder.stats.filtered_nonprimary
                + recorder.stats.stored
                + recorder.stats.missing_event_key
                + recorder.stats.capacity_dropped
        );
        assert_eq!(recorder.stats.duplicate_event_keys, 1);
        assert_eq!(recorder.stats.bytes, 3 * size_of::<Row>());
        assert!(RECORD_CAPACITY * size_of::<Row>() <= MEMORY_BUDGET);
        let value =
            serde_json::to_value(recorder.rows.iter().find(|row| row.sequence == 1).unwrap())
                .unwrap();
        assert_eq!(value["header_timestamp"], "134350000000000017");
        assert_eq!(value["event_key"], "0000000000000009");
        assert_eq!(value["keyword"], "ffffffffffffffff");
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
        for name in ["original.json:stream", "ambiguous.json.", "ambiguous.json "] {
            assert!(reserve(&folder.join(name), &[]).is_err());
        }
        std::fs::remove_file(path).unwrap();
        std::fs::remove_dir(folder).unwrap();
    }
    #[test]
    fn disk_quota_preserves_partial_evidence_and_hashes_only_written_bytes() {
        let path =
            std::env::temp_dir().join(format!("contain-clock-limit-{}.json", uuid::Uuid::new_v4()));
        let (_, mut file) = reserve(&path, &[]).unwrap();
        let mut writer = LimitedWriter {
            file: &mut file,
            bytes: 0,
            hash: blake3::Hasher::new(),
            sha256: sha2::Sha256::new(),
            budget: 4,
        };
        writer.write_all(b"abc").unwrap();
        assert!(writer.write_all(b"de").is_err());
        assert_eq!(writer.bytes, 3);
        assert_eq!(
            format!("{:x}", writer.sha256.finalize()),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        file.sync_all().unwrap();
        drop(file);
        assert_eq!(std::fs::read(&path).unwrap(), b"abc");
        std::fs::remove_file(path).unwrap();
    }
}
