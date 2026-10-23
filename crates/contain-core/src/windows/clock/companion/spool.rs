//! Fixed-size callback buffers and a separately owned, finite disk writer.
use super::{DISK_BUDGET, MEMORY_BUDGET, METADATA_BUDGET, Row};
use serde::Serialize;
use sha2::Digest;
use std::{
    fs::File,
    io::{self, Seek, SeekFrom, Write},
    os::windows::io::AsRawHandle,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, Receiver, RecvTimeoutError, SyncSender, TryRecvError},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

pub(super) const ROW_BYTES: usize = 64;
const CHUNK_ROWS: usize = 4096;
const POOL_CHUNKS: usize = 32;
const WRITER_STACK: usize = 1024 * 1024;
pub(super) const ROW_BUDGET: usize = (DISK_BUDGET - METADATA_BUDGET) / ROW_BYTES;
pub(super) const ALLOCATED_BYTES: usize = CHUNK_ROWS * POOL_CHUNKS * ROW_BYTES;
pub(super) const WRITER_MEMORY_BYTES: usize = ALLOCATED_BYTES + WRITER_STACK;
const _: () = {
    assert!(WRITER_MEMORY_BYTES + METADATA_BUDGET < MEMORY_BUDGET);
    assert!(ROW_BUDGET * ROW_BYTES + METADATA_BUDGET <= DISK_BUDGET);
};

impl Row {
    pub(super) fn wire(self) -> [u8; ROW_BYTES] {
        let mut row = [0; ROW_BYTES];
        row[0..8].copy_from_slice(&self.sequence.to_le_bytes());
        row[8..16].copy_from_slice(&self.event_key.to_le_bytes());
        row[16..24].copy_from_slice(&self.timestamp.to_le_bytes());
        row[24..40].copy_from_slice(&self.provider.to_le_bytes());
        row[40..42].copy_from_slice(&self.event_id.to_le_bytes());
        row[42] = self.version;
        row[43] = self.channel;
        row[44] = self.level;
        row[45] = self.opcode;
        row[46..48].copy_from_slice(&self.task.to_le_bytes());
        row[48..56].copy_from_slice(&self.keyword.to_le_bytes());
        row[56..60].copy_from_slice(&self.process_id.to_le_bytes());
        row[60..64].copy_from_slice(&self.thread_id.to_le_bytes());
        row
    }
}

struct Chunk {
    bytes: Vec<u8>,
}
impl Chunk {
    fn empty(rows: usize) -> Self {
        Self {
            bytes: Vec::with_capacity(rows * ROW_BYTES),
        }
    }
}

#[derive(Default)]
struct Progress {
    bytes: AtomicU64,
    calls: AtomicU64,
    chunks: AtomicU64,
    write_ns: AtomicU64,
    queued_chunks: AtomicU64,
    peak_chunks: AtomicU64,
    abort: AtomicBool,
}

#[derive(Default, Serialize)]
pub(super) struct Outcome {
    pub bytes: u64,
    pub records: u64,
    pub partial_record_bytes: u64,
    pub write_calls: u64,
    pub written_chunks: u64,
    pub queue_high_water_chunks: u64,
    pub write_elapsed_ns: u64,
    pub drain_elapsed_ns: u64,
    pub finished: bool,
    pub drained: bool,
    pub timed_out: bool,
    pub cancel_requested: bool,
    pub cancel_error: Option<u32>,
    pub error: Option<String>,
    pub sha256: Option<String>,
    pub blake3: Option<String>,
}

pub(super) struct Producer {
    free: Receiver<Chunk>,
    queue: Option<SyncSender<Chunk>>,
    current: Option<Chunk>,
    progress: Arc<Progress>,
    chunk_rows: usize,
    record_budget: usize,
    pub accepted: u64,
    pub queued: u64,
    pub queue_dropped: u64,
    pub quota_dropped: u64,
    pub writer_closed_dropped: u64,
}

pub(super) struct Worker {
    handle: Option<JoinHandle<Outcome>>,
    progress: Arc<Progress>,
}

fn channels(
    rows: usize,
    chunks: usize,
    budget: usize,
) -> (Producer, Receiver<Chunk>, SyncSender<Chunk>) {
    assert!(rows > 0 && chunks > 0 && rows * chunks * ROW_BYTES <= MEMORY_BUDGET);
    let (queue, receive) = mpsc::sync_channel(chunks);
    let (recycle, free) = mpsc::sync_channel(chunks);
    for _ in 0..chunks {
        recycle.send(Chunk::empty(rows)).expect("new pool receiver");
    }
    (
        Producer {
            free,
            queue: Some(queue),
            current: None,
            progress: Arc::new(Progress::default()),
            chunk_rows: rows,
            record_budget: budget,
            accepted: 0,
            queued: 0,
            queue_dropped: 0,
            quota_dropped: 0,
            writer_closed_dropped: 0,
        },
        receive,
        recycle,
    )
}

pub(super) fn start(file: File) -> io::Result<(Producer, Worker)> {
    let (producer, receive, recycle) = channels(CHUNK_ROWS, POOL_CHUNKS, ROW_BUDGET);
    let progress = producer.progress.clone();
    let worker_progress = progress.clone();
    let handle = thread::Builder::new()
        .name("contain-clock-writer".into())
        .stack_size(WRITER_STACK)
        .spawn(move || {
            let mut file = file;
            let mut result = write_chunks(&mut file, receive, recycle, &worker_progress);
            if result.error.is_none()
                && let Err(error) = file.sync_all()
            {
                result.error = Some(format!("sync raw clock records: {error}"));
                result.drained = false;
            }
            result
        })?;
    Ok((
        producer,
        Worker {
            handle: Some(handle),
            progress,
        },
    ))
}

/// Metadata is small, but its filesystem operations can still stall. Keep seek,
/// truncate, writes and durability synchronization on the same cancellable thread.
pub(super) fn metadata(file: File, bytes: Vec<u8>) -> io::Result<Worker> {
    if bytes.len() > METADATA_BUDGET {
        return Err(io::Error::other("raw clock metadata budget exhausted"));
    }
    let progress = Arc::new(Progress::default());
    let worker_progress = progress.clone();
    let handle = thread::Builder::new()
        .name("contain-clock-metadata".into())
        .stack_size(WRITER_STACK)
        .spawn(move || {
            let mut file = file;
            commit_metadata(&mut file, &bytes, &worker_progress, |file| {
                file.set_len(bytes.len() as u64)?;
                file.sync_all()
            })
        })?;
    Ok(Worker {
        handle: Some(handle),
        progress,
    })
}

fn commit_metadata<W: Write + Seek>(
    file: &mut W,
    bytes: &[u8],
    progress: &Progress,
    sync: impl FnOnce(&mut W) -> io::Result<()>,
) -> Outcome {
    let mut sha256 = sha2::Sha256::new();
    let mut blake3 = blake3::Hasher::new();
    let started = Instant::now();
    let result = (|| -> io::Result<()> {
        file.seek(SeekFrom::Start(0))?;
        let mut offset = 0;
        while offset < bytes.len() {
            if progress.abort.load(Ordering::Acquire) {
                return Err(io::Error::other("raw clock metadata writer cancelled"));
            }
            progress.calls.fetch_add(1, Ordering::Relaxed);
            match file.write(&bytes[offset..]) {
                Ok(0) => {
                    return Err(io::Error::new(
                        io::ErrorKind::WriteZero,
                        "raw clock metadata write returned zero",
                    ));
                }
                Ok(count) => {
                    sha256.update(&bytes[offset..offset + count]);
                    blake3.update(&bytes[offset..offset + count]);
                    progress.bytes.fetch_add(count as u64, Ordering::Relaxed);
                    offset += count;
                }
                Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                Err(error) => return Err(error),
            }
        }
        sync(file)
    })();
    progress
        .write_ns
        .store(started.elapsed().as_nanos() as u64, Ordering::Relaxed);
    Outcome {
        finished: true,
        drained: result.is_ok(),
        error: result
            .err()
            .map(|error| format!("raw clock metadata persistence: {error}")),
        sha256: Some(format!("{:x}", sha256.finalize())),
        blake3: Some(blake3.finalize().to_hex().to_string()),
        ..snapshot(progress)
    }
}

impl Producer {
    pub(super) fn push(&mut self, row: Row) {
        if self.accepted >= self.record_budget as u64 {
            self.quota_dropped += 1;
            return;
        }
        if self.progress.abort.load(Ordering::Acquire) || self.queue.is_none() {
            self.writer_closed_dropped += 1;
            return;
        }
        if self.current.is_none() {
            match self.free.try_recv() {
                Ok(chunk) => self.current = Some(chunk),
                Err(TryRecvError::Empty) => {
                    self.queue_dropped += 1;
                    return;
                }
                Err(TryRecvError::Disconnected) => {
                    self.writer_closed_dropped += 1;
                    return;
                }
            }
        }
        let chunk = self.current.as_mut().expect("available buffer");
        chunk.bytes.extend_from_slice(&row.wire());
        self.accepted += 1;
        if chunk.bytes.len() == self.chunk_rows * ROW_BYTES {
            self.submit();
        }
    }
    fn submit(&mut self) {
        let Some(chunk) = self.current.take() else {
            return;
        };
        let count = (chunk.bytes.len() / ROW_BYTES) as u64;
        // Pool ownership limits outstanding chunks; this send never waits for disk.
        let pending = self.progress.queued_chunks.fetch_add(1, Ordering::Relaxed) + 1;
        self.progress
            .peak_chunks
            .fetch_max(pending, Ordering::Relaxed);
        match self.queue.as_ref().expect("open producer").try_send(chunk) {
            Ok(()) => self.queued += count,
            Err(_) => {
                self.progress.queued_chunks.fetch_sub(1, Ordering::Relaxed);
                self.progress.abort.store(true, Ordering::Release);
                // Accepted but not submitted records remain visible as not persisted.
            }
        }
    }
    pub(super) fn close(&mut self) {
        if self.queue.is_some() {
            self.submit();
            self.queue.take();
        }
    }
}
impl Drop for Producer {
    fn drop(&mut self) {
        self.close();
    }
}

fn write_chunks(
    sink: &mut impl Write,
    receive: Receiver<Chunk>,
    recycle: SyncSender<Chunk>,
    progress: &Progress,
) -> Outcome {
    let mut sha256 = sha2::Sha256::new();
    let mut blake3 = blake3::Hasher::new();
    let mut error = None;
    'chunks: loop {
        let mut chunk = match receive.recv_timeout(Duration::from_millis(50)) {
            Ok(chunk) => chunk,
            Err(RecvTimeoutError::Disconnected) => break,
            Err(RecvTimeoutError::Timeout) => {
                if progress.abort.load(Ordering::Acquire) {
                    error = Some("raw clock writer cancelled while waiting for records".into());
                    break;
                }
                continue;
            }
        };
        progress.queued_chunks.fetch_sub(1, Ordering::Relaxed);
        let mut offset = 0;
        while offset < chunk.bytes.len() {
            if progress.abort.load(Ordering::Acquire) {
                error = Some("raw clock writer cancelled; remaining records not persisted".into());
                break 'chunks;
            }
            progress.calls.fetch_add(1, Ordering::Relaxed);
            let started = Instant::now();
            let written = sink.write(&chunk.bytes[offset..]);
            progress
                .write_ns
                .fetch_add(started.elapsed().as_nanos() as u64, Ordering::Relaxed);
            match written {
                Ok(0) => {
                    error = Some("raw clock writer returned zero bytes".into());
                    break 'chunks;
                }
                Ok(count) => {
                    sha256.update(&chunk.bytes[offset..offset + count]);
                    blake3.update(&chunk.bytes[offset..offset + count]);
                    progress.bytes.fetch_add(count as u64, Ordering::Relaxed);
                    offset += count;
                }
                Err(failure) if failure.kind() == io::ErrorKind::Interrupted => continue,
                Err(failure) => {
                    error = Some(format!("write raw clock records: {failure}"));
                    break 'chunks;
                }
            }
        }
        progress.chunks.fetch_add(1, Ordering::Relaxed);
        chunk.bytes.clear();
        // Returning a buffer must not wait if capture is already ending.
        let _ = recycle.try_send(chunk);
    }
    if error.is_some() {
        progress.abort.store(true, Ordering::Release);
    }
    Outcome {
        finished: true,
        drained: error.is_none(),
        error,
        sha256: Some(format!("{:x}", sha256.finalize())),
        blake3: Some(blake3.finalize().to_hex().to_string()),
        ..snapshot(progress)
    }
}

fn snapshot(progress: &Progress) -> Outcome {
    let bytes = progress.bytes.load(Ordering::Acquire);
    Outcome {
        bytes,
        records: bytes / ROW_BYTES as u64,
        partial_record_bytes: bytes % ROW_BYTES as u64,
        write_calls: progress.calls.load(Ordering::Relaxed),
        written_chunks: progress.chunks.load(Ordering::Relaxed),
        queue_high_water_chunks: progress.peak_chunks.load(Ordering::Relaxed),
        write_elapsed_ns: progress.write_ns.load(Ordering::Relaxed),
        ..Default::default()
    }
}

impl Worker {
    pub(super) fn finish(mut self, timeout: Duration) -> Outcome {
        self.wait(timeout)
    }
    fn wait(&mut self, timeout: Duration) -> Outcome {
        let started = Instant::now();
        let Some(handle) = self.handle.take() else {
            return snapshot(&self.progress);
        };
        while !handle.is_finished() && started.elapsed() < timeout {
            thread::sleep(Duration::from_millis(1));
        }
        let timed_out = !handle.is_finished();
        let mut cancel_error = None;
        if timed_out {
            self.progress.abort.store(true, Ordering::Release);
            // SAFETY: the JoinHandle owns this live thread handle through cancellation.
            if unsafe {
                windows_sys::Win32::System::IO::CancelSynchronousIo(handle.as_raw_handle())
            } == 0
            {
                cancel_error = Some(unsafe { windows_sys::Win32::Foundation::GetLastError() });
            }
            let grace = Instant::now();
            while !handle.is_finished() && grace.elapsed() < Duration::from_secs(1) {
                thread::sleep(Duration::from_millis(1));
            }
        }
        let finished = handle.is_finished();
        let mut result = if finished {
            handle.join().unwrap_or_else(|_| Outcome {
                error: Some("raw clock writer panicked".into()),
                ..snapshot(&self.progress)
            })
        } else {
            // An unresponsive driver cannot force an unbounded join. The detached
            // writer retains only the preallocated pool and finite disk quota.
            Outcome {
                error: Some("raw clock writer still pending after cancellation; artifact is incomplete and may still change".into()),
                ..snapshot(&self.progress)
            }
        };
        result.finished = finished;
        result.timed_out = timed_out;
        result.cancel_requested = timed_out;
        result.cancel_error = cancel_error;
        result.drain_elapsed_ns = started.elapsed().as_nanos() as u64;
        if timed_out {
            result.drained = false;
            result
                .error
                .get_or_insert_with(|| "raw clock writer drain deadline exceeded".into());
        }
        result
    }
}
impl Drop for Worker {
    fn drop(&mut self) {
        if self.handle.is_some() {
            self.progress.abort.store(true, Ordering::Release);
            let _ = self.wait(Duration::from_millis(10));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::windows::clock::companion::tests::row;

    #[test]
    fn wire_is_exact_little_endian_and_preserves_signed_qpc_and_full_identity() {
        let value = Row {
            sequence: 7,
            event_key: 8,
            ..row(-9)
        }
        .wire();
        assert_eq!(value.len(), ROW_BYTES);
        assert_eq!(u64::from_le_bytes(value[0..8].try_into().unwrap()), 7);
        assert_eq!(u64::from_le_bytes(value[8..16].try_into().unwrap()), 8);
        assert_eq!(i64::from_le_bytes(value[16..24].try_into().unwrap()), -9);
        assert_eq!(
            u128::from_le_bytes(value[24..40].try_into().unwrap()),
            row(0).provider
        );
        assert_eq!(&value[40..48], &[16, 0, 1, 2, 4, 5, 6, 0]);
        assert_eq!(&value[48..56], &[255; 8]);
        assert_eq!(u32::from_le_bytes(value[56..60].try_into().unwrap()), 42);
        assert_eq!(u32::from_le_bytes(value[60..64].try_into().unwrap()), 43);
    }

    #[test]
    fn stalled_writer_cannot_grow_pool_or_block_callback_and_final_chunk_drains() {
        let (mut producer, receive, recycle) = channels(2, 2, 99);
        for _ in 0..5 {
            producer.push(row(1));
        }
        assert_eq!(producer.accepted, 4);
        assert_eq!(producer.queue_dropped, 1);
        let progress = producer.progress.clone();
        let writer = thread::spawn(move || {
            let mut sink = Vec::new();
            let outcome = write_chunks(&mut sink, receive, recycle, &progress);
            (outcome, sink)
        });
        producer.current = Some(producer.free.recv_timeout(Duration::from_secs(5)).unwrap());
        producer.push(row(2));
        producer.close();
        let (result, sink) = writer.join().unwrap();
        assert!(result.drained);
        assert_eq!(result.records, 5);
        assert_eq!(producer.queued, 5);
        assert_eq!(sink.len(), 5 * ROW_BYTES);
        assert_eq!(
            result.sha256.unwrap(),
            format!("{:x}", sha2::Sha256::digest(&sink))
        );
    }

    #[test]
    fn quota_is_record_aligned_and_retains_tail_without_expanding_memory() {
        let (mut producer, receive, recycle) = channels(8, 2, 3);
        for _ in 0..5 {
            producer.push(row(1));
        }
        producer.close();
        let result = write_chunks(&mut Vec::new(), receive, recycle, &producer.progress);
        assert_eq!(result.records, 3);
        assert_eq!(producer.quota_dropped, 2);
        assert!(result.drained);
    }

    struct Failing {
        remaining: usize,
    }
    impl Write for Failing {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            if self.remaining == 0 {
                return Err(io::Error::other("injected write failure"));
            }
            let count = self.remaining.min(bytes.len());
            self.remaining -= count;
            Ok(count)
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }
    #[test]
    fn partial_write_failure_preserves_exact_written_bytes_and_incomplete_record() {
        let (mut producer, receive, recycle) = channels(2, 2, 99);
        producer.push(row(1));
        producer.push(row(2));
        producer.close();
        let result = write_chunks(
            &mut Failing { remaining: 70 },
            receive,
            recycle,
            &producer.progress,
        );
        assert!(!result.drained);
        assert_eq!(result.bytes, 70);
        assert_eq!(result.records, 1);
        assert_eq!(result.partial_record_bytes, 6);
        assert!(result.error.unwrap().contains("injected write failure"));
        let mut expected = Vec::new();
        expected.extend_from_slice(&row(1).wire());
        expected.extend_from_slice(&row(2).wire()[..6]);
        assert_eq!(
            result.sha256.unwrap(),
            format!("{:x}", sha2::Sha256::digest(&expected))
        );
        assert!(producer.progress.abort.load(Ordering::Acquire));
    }

    #[test]
    fn cancellation_stops_queued_work_and_marks_it_incomplete() {
        let (mut producer, receive, recycle) = channels(2, 2, 99);
        producer.push(row(1));
        producer.close();
        producer.progress.abort.store(true, Ordering::Release);
        let result = write_chunks(&mut Vec::new(), receive, recycle, &producer.progress);
        assert!(!result.drained);
        assert_eq!(result.records, 0);
        assert!(result.error.unwrap().contains("cancelled"));
    }

    #[test]
    fn drain_deadline_requests_cancellation_and_does_not_claim_success() {
        let progress = Arc::new(Progress::default());
        let handle = thread::spawn(|| {
            thread::sleep(Duration::from_millis(40));
            Outcome {
                drained: true,
                ..Default::default()
            }
        });
        let worker = Worker {
            handle: Some(handle),
            progress: progress.clone(),
        };
        let outcome = worker.finish(Duration::from_millis(1));
        assert!(outcome.timed_out);
        assert!(outcome.cancel_requested);
        assert!(!outcome.drained);
        assert!(outcome.error.unwrap().contains("deadline"));
        assert!(progress.abort.load(Ordering::Acquire));
    }

    #[test]
    fn metadata_sync_failure_is_not_a_completed_commit() {
        let progress = Progress::default();
        let mut file = io::Cursor::new(Vec::new());
        let result = commit_metadata(&mut file, b"abc", &progress, |_| {
            Err(io::Error::other("injected durability failure"))
        });
        assert!(result.finished);
        assert!(!result.drained);
        assert_eq!(result.bytes, 3);
        assert_eq!(file.into_inner(), b"abc");
        assert!(
            result
                .error
                .unwrap()
                .contains("injected durability failure")
        );
    }

    #[test]
    fn blocked_metadata_sync_returns_incomplete_without_a_final_hash() {
        let progress = Arc::new(Progress::default());
        let worker_progress = progress.clone();
        let (entered, enter) = mpsc::sync_channel(1);
        let (release, wait) = mpsc::sync_channel(1);
        let (finished, finish) = mpsc::sync_channel(1);
        let handle = thread::spawn(move || {
            let mut file = io::Cursor::new(Vec::new());
            let result = commit_metadata(&mut file, b"abc", &worker_progress, |_| {
                entered.send(()).unwrap();
                wait.recv_timeout(Duration::from_secs(5)).unwrap();
                Ok(())
            });
            finished.send(()).unwrap();
            result
        });
        enter.recv_timeout(Duration::from_secs(5)).unwrap();
        let worker = Worker {
            handle: Some(handle),
            progress,
        };
        let outcome = worker.finish(Duration::from_millis(1));
        assert!(outcome.timed_out && outcome.cancel_requested);
        assert!(!outcome.finished && !outcome.drained);
        assert_eq!(outcome.sha256, None);
        assert_eq!(outcome.blake3, None);
        assert_eq!(outcome.bytes, 3);
        release.send(()).unwrap();
        finish.recv_timeout(Duration::from_secs(5)).unwrap();
    }

    #[test]
    #[ignore = "Explicit synthetic disk-volume check; not live ETW or an unpaced throughput claim"]
    fn paced_historical_callback_volume_fits_disk_quota_and_drains_every_record() {
        const RECORDS: u64 = 896_972;
        let retained = std::env::var_os("CONTAIN_CLOCK_SPOOL_TEST_OUTPUT");
        let path = retained
            .as_ref()
            .map(std::path::PathBuf::from)
            .unwrap_or_else(|| {
                std::env::temp_dir()
                    .join(format!("contain-clock-volume-{}.bin", uuid::Uuid::new_v4()))
            });
        let (_, file) = super::super::reserve(&path, &[]).unwrap();
        let (mut producer, worker) = start(file).unwrap();
        let started = Instant::now();
        // The test controls producer pacing solely to exercise finite storage and
        // recycling. Real callbacks never wait for this pool; saturation is tested
        // separately and is an observable failure rather than hidden backpressure.
        for sequence in 1..=RECORDS {
            if producer.current.is_none() {
                producer.current =
                    Some(producer.free.recv_timeout(Duration::from_secs(5)).unwrap());
            }
            producer.push(Row {
                sequence,
                event_key: sequence,
                ..row(sequence as i64)
            });
        }
        producer.close();
        let outcome = worker.finish(Duration::from_secs(30));
        let elapsed = started.elapsed();
        assert!(
            outcome.drained,
            "{}",
            serde_json::to_string(&outcome).unwrap()
        );
        assert_eq!(producer.queue_dropped, 0);
        assert_eq!(producer.quota_dropped, 0);
        assert_eq!(producer.writer_closed_dropped, 0);
        assert_eq!(producer.accepted, RECORDS);
        assert_eq!(outcome.records, RECORDS);
        assert_eq!(outcome.bytes, 57_406_208);
        assert_eq!(outcome.partial_record_bytes, 0);
        assert_eq!(std::fs::metadata(&path).unwrap().len(), outcome.bytes);
        let mut input = io::BufReader::with_capacity(64 * 1024, File::open(&path).unwrap());
        let mut digest = sha2::Sha256::new();
        use io::Read;
        for index in 0..RECORDS {
            let mut bytes = [0; ROW_BYTES];
            input.read_exact(&mut bytes).unwrap();
            digest.update(bytes);
            assert_eq!(
                u64::from_le_bytes(bytes[0..8].try_into().unwrap()),
                index + 1
            );
            assert_eq!(
                u64::from_le_bytes(bytes[8..16].try_into().unwrap()),
                index + 1
            );
        }
        assert_eq!(
            outcome.sha256.as_ref().unwrap(),
            &format!("{:x}", digest.finalize())
        );
        drop(input);
        println!(
            "{{\"scope\":\"synthetic_paced_fixed_record_spool\",\"records\":{RECORDS},\"elapsed_ns\":{},\"allocated_record_bytes\":{ALLOCATED_BYTES},\"writer\":{}}}",
            elapsed.as_nanos(),
            serde_json::to_string(&outcome).unwrap()
        );
        if retained.is_none() {
            std::fs::remove_file(path).unwrap();
        }
    }
}
