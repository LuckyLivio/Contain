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
    follow_cache_updated: bool,
    last_activity: Instant,
    received: u64,
    follow: Option<crate::follow::FollowBuffer>,
}
impl LiveState {
    fn seed(&mut self, record: ProcessRecord) {
        // Repeated observer polling refreshes last_seen only. It must not make
        // every idle page recheck the complete provisional backlog.
        let changed = record.creation_time.is_some_and(|birth| {
            self.cache
                .processes
                .get(&(record.pid, birth))
                .is_none_or(|old| {
                    (!record.image.is_empty() && record.image != old.image)
                        || (record.parent_pid.is_some() && record.parent_pid != old.parent_pid)
                        || (record.parent_creation_time.is_some()
                            && record.parent_creation_time != old.parent_creation_time)
                        || record
                            .ended_at
                            .is_some_and(|end| old.ended_at.is_none_or(|old_end| end < old_end))
                        || (matches!(record.confidence, Confidence::Certain | Confidence::High)
                            && (record.confidence != old.confidence
                                || record.evidence != old.evidence))
                })
        });
        self.follow_cache_updated |= changed;
        self.cache.seed(record);
    }
}
pub(crate) struct LiveCapture(Arc<Mutex<LiveState>>);
impl LiveCapture {
    /// Source receipt is measured separately from evidence persistence or actors.
    pub(crate) fn received(&self) -> u64 {
        self.0.lock().unwrap().received
    }
    pub(crate) fn seed_follow_initial(
        &self,
        records: &[(ProcessRecord, u64)],
        threads: &[InitialThreadIdentity],
    ) {
        let mut live = self.0.lock().unwrap();
        if live.follow.is_none() {
            return;
        }
        live.follow_cache_updated |= !records.is_empty() || !threads.is_empty();
        for (record, at) in records {
            // Startup ETW facts now precede these snapshots. Check contradictory
            // observations without replacing the wider lifecycle interval.
            if record
                .creation_time
                .is_some_and(|birth| live.cache.processes.contains_key(&(record.pid, birth)))
            {
                live.cache.seed(record.clone());
            }
            live.cache.seed_snapshot(record.clone(), *at);
        }
        for identity in threads {
            if let Some(process) = live
                .cache
                .processes
                .get(&(identity.owner_pid, identity.owner_creation_time))
                .cloned()
            {
                live.cache.seed_thread_snapshot(identity.clone(), process);
            }
        }
    }
    pub(crate) fn bind_follow_root(&self, record: ProcessRecord, session: &str) {
        let mut live = self.0.lock().unwrap();
        let root = (record.pid, record.creation_time.unwrap_or(0));
        live.seed(record);
        if let Some(follow) = &mut live.follow {
            follow.bind(root, session);
        }
    }
    pub(crate) fn quiet_ms(&self) -> u64 {
        self.0.lock().unwrap().last_activity.elapsed().as_millis() as u64
    }
    pub(crate) fn reset_activity(&self) {
        self.0.lock().unwrap().last_activity = Instant::now();
    }
    pub(crate) fn drain_state(
        &self,
        records: Vec<ProcessRecord>,
        root: (u32, u64),
        session: &str,
        lifecycle: bool,
    ) -> DrainDiagnostics {
        let (candidates, context_lost) = {
            let mut live = self.0.lock().unwrap();
            for record in records {
                live.seed(record);
            }
            live.cache.attach(root, session);
            if lifecycle {
                let pending = live.cache.processes.values().filter(|p| {
                    (p.pid, p.creation_time.unwrap_or(0)) != root
                        && p.ended_at.is_none()
                        && if live.follow.is_some() {
                            crate::follow::is_direct_actor(p, root)
                        } else {
                            p.confidence == Confidence::High
                        }
                });
                let mut state = DrainDiagnostics {
                    quiet_ms: live.last_activity.elapsed().as_millis() as u64,
                    lifecycle_context_lost: live.cache.dropped,
                    ..Default::default()
                };
                for process in pending {
                    state.pending_verified_instances += 1;
                    if state.pending_examples.len() < 8 {
                        state.pending_examples.push(DrainProcess {
                            pid: process.pid,
                            creation_time: process.creation_time.unwrap_or(0),
                        });
                    }
                }
                return state;
            }
            (
                live.cache
                    .processes
                    .values()
                    .filter(|p| {
                        (p.pid, p.creation_time.unwrap_or(0)) != root
                            && p.confidence == Confidence::High
                            && (live.follow.is_none() || crate::follow::is_direct_actor(p, root))
                    })
                    .map(|p| (p.pid, p.creation_time))
                    .collect::<Vec<_>>(),
                live.cache.dropped,
            )
        };
        // Never hold the receiver's lifetime lock during Win32 process queries.
        let mut state = DrainDiagnostics {
            quiet_ms: self.quiet_ms(),
            lifecycle_context_lost: context_lost,
            ..Default::default()
        };
        for (pid, birth) in candidates {
            if native::process_identity(pid).is_some_and(|p| Some(p.creation_time) == birth) {
                state.pending_verified_instances += 1;
                if state.pending_examples.len() < 8 {
                    state.pending_examples.push(DrainProcess {
                        pid,
                        creation_time: birth.unwrap_or(0),
                    });
                }
            }
        }
        state
    }
}

/// Even an operation error/unwind first stops ETW, drains both workers and joins.
/// The DB borrow ends before caller postprocessing or the E1 policy guard runs.
pub(crate) fn record_while<T>(
    source: &mut EtwSource,
    db: &mut Storage,
    journal: RawBuffer,
    initial_cache: LifetimeCache,
    operation: impl FnOnce(&LiveCapture) -> Result<T>,
) -> Result<(T, BackendReport, StreamStats)> {
    record_while_mode(source, db, journal, initial_cache, false, operation)
}

pub(crate) fn record_while_follow<T>(
    source: &mut EtwSource,
    db: &mut Storage,
    journal: RawBuffer,
    initial_cache: LifetimeCache,
    operation: impl FnOnce(&LiveCapture) -> Result<T>,
) -> Result<(T, BackendReport, StreamStats)> {
    record_while_mode(source, db, journal, initial_cache, true, operation)
}

fn record_while_mode<T>(
    source: &mut EtwSource,
    db: &mut Storage,
    mut journal: RawBuffer,
    initial_cache: LifetimeCache,
    follow: bool,
    operation: impl FnOnce(&LiveCapture) -> Result<T>,
) -> Result<(T, BackendReport, StreamStats)> {
    let receiver = source.take_receiver();
    let probe = source.queue_probe();
    let receiver_probe = probe.clone();
    let worker_context = profile::worker_context();
    let queue = Arc::new(Queue::new(RECORDS, BYTES));
    let done = Arc::new(AtomicBool::new(false));
    let live = LiveCapture(Arc::new(Mutex::new(LiveState {
        cache: initial_cache,
        follow_cache_updated: follow,
        last_activity: Instant::now(),
        received: 0,
        follow: follow.then(Default::default),
    })));
    let initial_accepted = journal.stats.accepted;
    thread::scope(|scope| {
        let writer_queue = queue.clone();
        let writer_done = done.clone();
        let writer = thread::Builder::new()
            .name("contain-writer".into())
            .spawn_scoped(scope, move || {
                let _close = QueueCloser(writer_queue.clone());
                profile::worker_start(worker_context);
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
                profile::worker_start(worker_context);
                profile::observe_queue(receiver_probe);
                let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                    loop {
                        let page = {
                            let _span = profile::queue_span("receiver_drain");
                            receiver.drain()
                        };
                        if page.is_empty() {
                            if receiver_done.load(Ordering::Acquire) {
                                break;
                            }
                            let _span = profile::queue_span("receiver_idle_wait");
                            thread::sleep(Duration::from_millis(1));
                            continue;
                        }
                        let mut accepted = Vec::new();
                        {
                            let _timer = profile::timer("receiver_lifetime_update");
                            let mut live = {
                                let _span = profile::queue_span("receiver_lock_wait");
                                receiver_live.lock().unwrap()
                            };
                            live.received = live.received.saturating_add(page.len() as u64);
                            // Resolve the page against all lifecycle facts received in
                            // it, including thread starts delivered after file records.
                            {
                                let _span = profile::queue_span("receiver_lifecycle_ingest")
                                    .units(page.len());
                                let mut lifecycle_events = 0;
                                for event in &page {
                                    let lifecycle =
                                        event.evidence.source == EvidenceSource::EtwProcess;
                                    lifecycle_events += u64::from(lifecycle);
                                    live.follow_cache_updated |= lifecycle;
                                    live.cache.ingest(event);
                                }
                                profile::counters([
                                    ("receiver_pages", 1),
                                    ("receiver_page_records", page.len() as u64),
                                    ("receiver_lifecycle_records", lifecycle_events),
                                ]);
                            }
                            let mut follow_candidates = Vec::new();
                            {
                                let _span =
                                    profile::queue_span("receiver_page_split").units(page.len());
                                for event in page {
                                    if live.follow.is_some()
                                        && matches!(
                                            event.event_type.as_str(),
                                            "file" | "completion"
                                        )
                                    {
                                        follow_candidates.push(event);
                                    } else {
                                        if event.event_type == "file"
                                            || (event.event_type == "registry"
                                                && event.raw.resource_resolved)
                                        {
                                            live.last_activity = Instant::now();
                                        }
                                        accepted.push(event);
                                    }
                                }
                            }
                            if let Some(mut follow) = live.follow.take() {
                                let cache_updated = std::mem::take(&mut live.follow_cache_updated);
                                let activity_before = follow.target_mutations;
                                let selected = follow.receive(
                                    follow_candidates,
                                    &mut live.cache,
                                    cache_updated,
                                );
                                if follow.target_mutations != activity_before {
                                    live.last_activity = Instant::now();
                                }
                                accepted.extend(selected);
                                live.follow = Some(follow);
                            }
                        }
                        {
                            let _span =
                                profile::queue_span("receiver_handoff").units(accepted.len());
                            for event in accepted {
                                receiver_queue.push(event);
                            }
                        }
                    }
                    let mut live = receiver_live.lock().unwrap();
                    if let Some(mut follow) = live.follow.take() {
                        for event in follow.flush(&mut live.cache, false) {
                            receiver_queue.push(event);
                        }
                        for event in follow.flush(&mut live.cache, true) {
                            receiver_queue.push(event);
                        }
                        live.follow = Some(follow);
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
        let live = live.0.lock().unwrap();
        if let Some(follow) = &live.follow {
            // Count live-stage evictions separately from final-replay evictions;
            // these are stage occurrences, not unique dropped raw records.
            backend.context_losses = backend.context_losses.saturating_add(live.cache.dropped);
            if let Some(pipeline) = &mut backend.pipeline {
                pipeline.context_evictions = pipeline
                    .context_evictions
                    .saturating_add(live.cache.dropped);
            }
            let mut stats = follow.stats.clone();
            stats.excluded_self_io = source.excluded_self_io();
            backend.context_losses = backend
                .context_losses
                .saturating_add(stats.candidate_dropped);
            backend.follow_installer = Some(stats);
        }
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
    fn startup_live() -> LiveCapture {
        let mut cache = LifetimeCache::default();
        for (operation, at, tid) in [("process_start", 100, None), ("thread_start", 110, Some(7))] {
            cache.ingest(&SystemEvent {
                timestamp_ticks: at,
                operation: operation.into(),
                resource: "observed.exe".into(),
                evidence: AttributionEvidence {
                    source: EvidenceSource::EtwProcess,
                    pid: Some(2),
                    process_creation_time: (operation == "process_start").then_some(100),
                    ..Default::default()
                },
                raw: RawEvidence {
                    thread_id: tid,
                    ..Default::default()
                },
                ..Default::default()
            });
        }
        LiveCapture(Arc::new(Mutex::new(LiveState {
            cache,
            follow_cache_updated: true,
            last_activity: Instant::now(),
            received: 0,
            follow: Some(Default::default()),
        })))
    }
    #[test]
    fn startup_lifecycle_interval_survives_later_matching_initial_snapshot() {
        let live = startup_live();
        live.seed_follow_initial(
            &[(
                ProcessRecord {
                    pid: 2,
                    creation_time: Some(100),
                    image: "observed.exe".into(),
                    evidence: AttributionEvidence {
                        source: EvidenceSource::ProcessApi,
                        ..Default::default()
                    },
                    ..Default::default()
                },
                200,
            )],
            &[],
        );
        let mut event = SystemEvent {
            timestamp_ticks: 150,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(7),
                ..Default::default()
            },
            ..Default::default()
        };
        live.0.lock().unwrap().cache.resolve_event(&mut event);
        assert_eq!(
            event.evidence.pid.zip(event.evidence.process_creation_time),
            Some((2, 100))
        );
    }
    #[test]
    fn drain_reports_exact_pending_instances_and_applies_exit_without_polling() {
        let live = startup_live();
        let root = ProcessRecord {
            pid: 1,
            creation_time: Some(50),
            image: "installer.exe".into(),
            confidence: Confidence::Certain,
            evidence: AttributionEvidence {
                source: EvidenceSource::ProcessApi,
                rule: AttributionRule::InstallerPid,
                ..Default::default()
            },
            ..Default::default()
        };
        live.bind_follow_root(root, "s");
        {
            let mut state = live.0.lock().unwrap();
            let child = state.cache.processes.get_mut(&(2, 100)).unwrap();
            child.parent_pid = Some(1);
            child.parent_creation_time = Some(50);
        }
        let state = live.drain_state(Vec::new(), (1, 50), "s", true);
        assert_eq!(state.pending_verified_instances, 1);
        assert_eq!(state.pending_examples[0].pid, 2);
        assert_eq!(state.pending_examples[0].creation_time, 100);
        // An exit for a reused PID cannot close the observed instance.
        let stop = |birth| SystemEvent {
            event_type: "lifecycle".into(),
            operation: "process_stop".into(),
            timestamp_ticks: 300,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwProcess,
                pid: Some(2),
                process_creation_time: Some(birth),
                ..Default::default()
            },
            ..Default::default()
        };
        live.0.lock().unwrap().cache.ingest(&stop(200));
        assert_eq!(
            live.drain_state(Vec::new(), (1, 50), "s", true)
                .pending_verified_instances,
            1
        );
        live.0.lock().unwrap().cache.ingest(&stop(100));
        assert_eq!(
            live.drain_state(Vec::new(), (1, 50), "s", true)
                .pending_verified_instances,
            0
        );
        live.0.lock().unwrap().cache.dropped = 1;
        let state = live.drain_state(Vec::new(), (1, 50), "s", true);
        assert_eq!(state.pending_verified_instances, 0);
        assert_eq!(state.lifecycle_context_lost, 1);
    }
    #[test]
    fn drain_examples_are_bounded_without_hiding_pending_instances() {
        let live = startup_live();
        let root = ProcessRecord {
            pid: 1,
            creation_time: Some(50),
            confidence: Confidence::Certain,
            evidence: AttributionEvidence {
                source: EvidenceSource::ProcessApi,
                rule: AttributionRule::InstallerPid,
                ..Default::default()
            },
            ..Default::default()
        };
        live.bind_follow_root(root, "s");
        for pid in 10..22 {
            live.0.lock().unwrap().cache.seed(ProcessRecord {
                pid,
                creation_time: Some(100),
                parent_pid: Some(1),
                parent_creation_time: Some(50),
                confidence: Confidence::High,
                ..Default::default()
            });
        }
        let state = live.drain_state(Vec::new(), (1, 50), "s", true);
        assert_eq!(state.pending_verified_instances, 12);
        assert_eq!(state.pending_examples.len(), 8);
        assert!(
            state
                .pending_examples
                .iter()
                .all(|p| p.creation_time == 100)
        );
    }
    #[test]
    fn initial_snapshot_conflict_with_startup_lifecycle_revokes_identity() {
        let live = startup_live();
        live.seed_follow_initial(
            &[(
                ProcessRecord {
                    pid: 2,
                    creation_time: Some(100),
                    image: "conflicting.exe".into(),
                    evidence: AttributionEvidence {
                        source: EvidenceSource::ProcessApi,
                        ..Default::default()
                    },
                    ..Default::default()
                },
                200,
            )],
            &[],
        );
        let mut event = SystemEvent {
            timestamp_ticks: 250,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(7),
                ..Default::default()
            },
            ..Default::default()
        };
        live.0.lock().unwrap().cache.resolve_event(&mut event);
        assert!(event.evidence.process_creation_time.is_none());
    }
    #[test]
    fn observer_last_seen_refresh_does_not_dirty_follow_context_but_new_evidence_does() {
        let mut live = LiveState {
            cache: LifetimeCache::default(),
            follow_cache_updated: false,
            last_activity: Instant::now(),
            received: 0,
            follow: Some(Default::default()),
        };
        let mut record = ProcessRecord {
            pid: 1,
            creation_time: Some(100),
            image: "installer.exe".into(),
            confidence: Confidence::Certain,
            ..Default::default()
        };
        live.seed(record.clone());
        assert!(std::mem::take(&mut live.follow_cache_updated));
        record.last_seen = "later polling sample".into();
        live.seed(record.clone());
        assert!(!live.follow_cache_updated);
        record.ended_at = Some(300);
        live.seed(record.clone());
        assert!(std::mem::take(&mut live.follow_cache_updated));
        live.seed(record.clone());
        assert!(!live.follow_cache_updated);
        record.parent_pid = Some(2);
        live.seed(record);
        assert!(live.follow_cache_updated);
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
