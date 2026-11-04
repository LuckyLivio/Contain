//! Scoped capture workers. Receiving never executes SQLite or process polling.
//! Both queues are bounded; overload is counted, never hidden or retried forever.
use crate::{
    lifetime::LifetimeCache,
    model::*,
    profile,
    storage::{
        Storage,
        stream::{PAGE, RawBuffer, StreamStats},
    },
    windows::{etw::EtwSource, native},
};
use anyhow::{Result, anyhow};
use std::{
    collections::VecDeque,
    io,
    sync::{
        Arc, Condvar, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

const RECORDS: usize = 4096;
const BYTES: usize = 16 * 1024 * 1024;
const RECORD_BYTES: usize = 256 * 1024;

pub use crate::model::HandoffStats;

struct QueueState {
    records: VecDeque<(SystemEvent, usize)>,
    bytes: usize,
    closed: bool,
    stats: HandoffStats,
}
struct Queue {
    state: Mutex<QueueState>,
    ready: Condvar,
    capacity: usize,
    byte_capacity: usize,
}
struct QueueCloser(Arc<Queue>);
impl Drop for QueueCloser {
    fn drop(&mut self) {
        self.0.close();
    }
}
impl Queue {
    fn new(capacity: usize, bytes: usize) -> Self {
        Self {
            state: Mutex::new(QueueState {
                records: VecDeque::new(),
                bytes: 0,
                closed: false,
                stats: HandoffStats {
                    record_capacity: capacity as u64,
                    byte_capacity: bytes as u64,
                    ..Default::default()
                },
            }),
            ready: Condvar::new(),
            capacity,
            byte_capacity: bytes,
        }
    }
    fn push(&self, event: SystemEvent) {
        let _timer = profile::timer("receiver_size_and_handoff");
        // Count serialized bytes without allocating another payload buffer.
        struct Size(usize);
        impl io::Write for Size {
            fn write(&mut self, data: &[u8]) -> io::Result<usize> {
                self.0 += data.len();
                Ok(data.len())
            }
            fn flush(&mut self) -> io::Result<()> {
                Ok(())
            }
        }
        let mut size = Size(0);
        let encoded = serde_json::to_writer(&mut size, &event);
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        state.stats.received += 1;
        if encoded.is_err() {
            state.stats.serialize_failed += 1;
        } else if state.closed {
            state.stats.disconnected += 1;
        } else if size.0 > RECORD_BYTES {
            state.stats.oversized += 1;
        } else if state.records.len() == self.capacity
            || state.bytes.saturating_add(size.0) > self.byte_capacity
        {
            state.stats.overflow += 1;
        } else {
            state.bytes += size.0;
            state.records.push_back((event, size.0));
            state.stats.submitted += 1;
            state.stats.high_water_records = state
                .stats
                .high_water_records
                .max(state.records.len() as u64);
            state.stats.high_water_bytes = state.stats.high_water_bytes.max(state.bytes as u64);
            self.ready.notify_one();
        }
    }
    fn page(&self) -> Option<Vec<SystemEvent>> {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        while state.records.is_empty() && !state.closed {
            let (next, timeout) = self
                .ready
                .wait_timeout(state, Duration::from_millis(100))
                .unwrap_or_else(std::sync::PoisonError::into_inner);
            state = next;
            // Preserve RawBuffer's existing 100-ms partial-batch flush cadence
            // while the installer is idle. An empty tick is not end-of-stream.
            if timeout.timed_out() && state.records.is_empty() && !state.closed {
                return Some(Vec::new());
            }
        }
        if state.records.is_empty() {
            return None;
        }
        let mut page = Vec::with_capacity(PAGE.min(state.records.len()));
        while page.len() < PAGE {
            let Some((event, bytes)) = state.records.pop_front() else {
                break;
            };
            state.bytes -= bytes;
            page.push(event);
            state.stats.drained += 1;
        }
        Some(page)
    }
    fn close(&self) {
        self.state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .closed = true;
        self.ready.notify_all();
    }
    fn stats(&self) -> HandoffStats {
        let state = self
            .state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let mut stats = state.stats.clone();
        stats.pending = state.records.len() as u64;
        stats
    }
}

struct LiveState {
    cache: LifetimeCache,
    last_activity: Instant,
}
pub(crate) struct LiveCapture(Arc<Mutex<LiveState>>);
impl LiveCapture {
    pub(crate) fn quiet_ms(&self) -> u64 {
        self.0.lock().unwrap().last_activity.elapsed().as_millis() as u64
    }
    pub(crate) fn reset_activity(&self) {
        self.0.lock().unwrap().last_activity = Instant::now();
    }
    pub(crate) fn descendants(
        &self,
        records: Vec<ProcessRecord>,
        root: (u32, u64),
        session: &str,
        lifecycle: bool,
    ) -> bool {
        let candidates = {
            let mut live = self.0.lock().unwrap();
            for record in records {
                live.cache.seed(record);
            }
            live.cache.attach(root, session);
            if live.cache.dropped > 0 {
                return true;
            }
            if lifecycle {
                return live.cache.descendants_pending(root);
            }
            live.cache
                .processes
                .values()
                .filter(|p| p.pid != root.0 && p.confidence == Confidence::High)
                .map(|p| (p.pid, p.creation_time))
                .collect::<Vec<_>>()
        };
        // Never hold the receiver's lifetime lock during Win32 process queries.
        candidates.into_iter().any(|(pid, birth)| {
            native::process_identity(pid).is_some_and(|p| Some(p.creation_time) == birth)
        })
    }
}

/// Even an operation error/unwind first stops ETW, drains both workers and joins.
/// The DB borrow ends before caller postprocessing or the E1 policy guard runs.
pub(crate) fn record_while<T>(
    source: &mut EtwSource,
    db: &mut Storage,
    mut journal: RawBuffer,
    operation: impl FnOnce(&LiveCapture) -> Result<T>,
) -> Result<(T, BackendReport, StreamStats)> {
    let receiver = source.take_receiver();
    let probe = source.queue_probe();
    let epoch = profile::epoch();
    let queue = Arc::new(Queue::new(RECORDS, BYTES));
    let done = Arc::new(AtomicBool::new(false));
    let live = LiveCapture(Arc::new(Mutex::new(LiveState {
        cache: LifetimeCache::default(),
        last_activity: Instant::now(),
    })));
    let initial_accepted = journal.stats.accepted;
    thread::scope(|scope| {
        let writer_queue = queue.clone();
        let writer_done = done.clone();
        let writer = thread::Builder::new()
            .name("contain-writer".into())
            .spawn_scoped(scope, move || {
                let _close = QueueCloser(writer_queue.clone());
                profile::worker_start(epoch);
                profile::observe_queue(probe);
                let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    while let Some(page) = writer_queue.page() {
                        journal.append(db, page);
                    }
                    // A receiver failure also closes the handoff. Never run the
                    // deferred checkpoint until STOP has completed (or failed).
                    while !writer_done.load(Ordering::Acquire) {
                        thread::sleep(Duration::from_millis(1));
                    }
                    journal.finish(db)
                }));
                writer_queue.close();
                (result, profile::worker_finish())
            });
        let writer = match writer {
            Ok(writer) => writer,
            Err(error) => {
                let _ = stop_and_signal(source, &done);
                return Err(anyhow!(
                    "starting evidence writer failed; capture unfinished: {error}"
                ));
            }
        };
        let receiver_queue = queue.clone();
        let receiver_done = done.clone();
        let receiver_live = live.0.clone();
        let reader = thread::Builder::new()
            .name("contain-receiver".into())
            .spawn_scoped(scope, move || {
                let _close = QueueCloser(receiver_queue.clone());
                profile::worker_start(epoch);
                let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    loop {
                        let page = receiver.drain();
                        if page.is_empty() {
                            if receiver_done.load(Ordering::Acquire) {
                                break;
                            }
                            thread::sleep(Duration::from_millis(1));
                            continue;
                        }
                        {
                            let _timer = profile::timer("receiver_lifetime_update");
                            let mut live = receiver_live.lock().unwrap();
                            for event in &page {
                                live.cache.ingest(event);
                                if event.event_type == "file"
                                    || (event.event_type == "registry"
                                        && event.raw.resource_resolved)
                                {
                                    live.last_activity = Instant::now();
                                }
                            }
                        }
                        for event in page {
                            receiver_queue.push(event);
                        }
                    }
                }));
                receiver_queue.close();
                (result, profile::worker_finish())
            });
        let reader = match reader {
            Ok(reader) => reader,
            Err(error) => {
                let _ = stop_and_signal(source, &done);
                queue.close();
                let _ = writer.join();
                return Err(anyhow!(
                    "starting evidence receiver failed; capture unfinished: {error}"
                ));
            }
        };
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| operation(&live)));
        // The callback consumer is still allowed to emit while STOP is completing.
        let stop_clock = Instant::now();
        let stopped = stop_and_signal(source, &done);
        let (reader_result, reader_profile) = reader
            .join()
            .map_err(|_| anyhow!("receiver thread panicked; capture unfinished"))?;
        let (writer_result, writer_profile) = writer
            .join()
            .map_err(|_| anyhow!("writer thread panicked; capture unfinished"))?;
        profile::merge_worker(reader_profile);
        profile::merge_worker(writer_profile);
        stopped?;
        let mut backend = source.final_report();
        let mut handoff = queue.stats();
        handoff.initial_accepted = initial_accepted;
        handoff.receiver_finished = reader_result.is_ok();
        handoff.writer_finished = matches!(&writer_result, Ok(Ok(_)));
        handoff.stop_and_drain_ns = stop_clock.elapsed().as_nanos() as u64;
        backend.dropped_events += handoff.dropped();
        backend.handoff = Some(handoff);
        if reader_result.is_err() {
            return Err(anyhow!("receiver failed; capture unfinished"));
        }
        let stream = writer_result.map_err(|_| anyhow!("writer failed; capture unfinished"))??;
        match result {
            Ok(result) => Ok((result?, backend, stream)),
            Err(panic) => std::panic::resume_unwind(panic),
        }
    })
}

/// Release waiting workers even when STOP unwinds. No completed-capture report
/// is returned on that path, because producer termination cannot be established.
fn stop_and_signal(source: &mut EtwSource, done: &AtomicBool) -> Result<()> {
    signal_after(done, || {
        source.stop();
    })
}

fn signal_after(done: &AtomicBool, stop: impl FnOnce()) -> Result<()> {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(stop));
    done.store(true, Ordering::Release);
    result.map_err(|_| {
        anyhow!("ETW stop panicked; capture unfinished and source continuity unverified")
    })
}

use crate::monitor::EventSource;

#[cfg(test)]
mod tests {
    use super::*;
    fn event(sequence: u64) -> SystemEvent {
        SystemEvent {
            sequence,
            id: sequence.to_string(),
            ..Default::default()
        }
    }
    #[test]
    fn stop_failure_releases_waiting_workers_without_claiming_completion() {
        let done = AtomicBool::new(false);
        let result = signal_after(&done, || panic!("injected stop failure"));
        assert!(result.is_err());
        assert!(done.load(Ordering::Acquire));
    }
    #[test]
    fn saturated_writer_is_bounded_and_every_record_accounted_for() {
        let queue = Arc::new(Queue::new(2, BYTES));
        let writer_queue = queue.clone();
        let (resume, gate) = std::sync::mpsc::channel();
        let writer = thread::spawn(move || {
            gate.recv().unwrap();
            let mut records = Vec::new();
            while let Some(page) = writer_queue.page() {
                records.extend(page);
            }
            records
        });
        queue.push(event(1));
        queue.push(event(2));
        queue.push(event(3));
        assert_eq!(queue.stats().overflow, 1);
        assert_eq!(queue.stats().pending, 2);
        queue.close();
        resume.send(()).unwrap();
        let records = writer.join().unwrap();
        assert_eq!(
            records.iter().map(|e| e.sequence).collect::<Vec<_>>(),
            [1, 2]
        );
        let stats = queue.stats();
        assert_eq!(stats.received, stats.submitted + stats.dropped());
        assert_eq!(stats.submitted, stats.drained);
        assert_eq!(stats.pending, 0);
    }
    #[test]
    fn byte_budget_oversized_record_and_closed_queue_have_separate_counts() {
        let size = serde_json::to_vec(&event(1)).unwrap().len();
        let queue = Queue::new(100, size);
        queue.push(event(1));
        queue.push(event(2));
        assert_eq!(queue.stats().overflow, 1);
        let mut huge = event(3);
        huge.resource = "x".repeat(RECORD_BYTES + 1);
        queue.push(huge);
        assert_eq!(queue.stats().oversized, 1);
        queue.close();
        queue.push(event(4));
        assert_eq!(queue.stats().disconnected, 1);
        assert!(queue.stats().high_water_bytes <= size as u64);
        assert_eq!(queue.page().unwrap().len(), 1);
        assert!(queue.page().is_none());
    }
}

#[cfg(test)]
#[path = "ingest_tests.rs"]
mod integration_tests;
