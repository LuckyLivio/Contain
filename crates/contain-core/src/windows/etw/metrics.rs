//! Cumulative counters shared with the consumer; timers are elapsed nanoseconds,
//! not CPU utilization. No path or schema labels are allocated per record.
use crate::model::PipelineStats;
use std::sync::atomic::{AtomicU64, Ordering::Relaxed};
use std::time::Instant;

#[derive(Clone, Copy)]
pub enum Count {
    Callback,
    Unsupported,
    Attempted,
    Succeeded,
    Failed,
    Filtered,
    Enqueued,
    Overflow,
    Dequeued,
    Pending,
    HighWater,
    ContextEvictions,
    FailedWithoutOutput,
    Disconnected,
}
#[derive(Clone, Copy)]
pub enum Time {
    Callback,
    Lock,
    Schema,
    Properties,
    Identity,
    Path,
    Construct,
    Enqueue,
    QueueDelay,
}
#[derive(Default)]
pub struct Metrics {
    counts: [AtomicU64; 14],
    times: [AtomicU64; 9],
    max_delay: AtomicU64,
}
impl Metrics {
    pub fn record_lock(&self, ns: u64) {
        self.times[Time::Lock as usize].fetch_add(ns, Relaxed);
    }
    pub fn add(&self, c: Count, n: u64) {
        self.counts[c as usize].fetch_add(n, Relaxed);
    }
    pub fn get(&self, c: Count) -> u64 {
        self.counts[c as usize].load(Relaxed)
    }
    pub fn timer(&self, t: Time) -> Timer<'_> {
        Timer(self, t, Instant::now())
    }
    pub fn entering(&self) -> u64 {
        self.counts[Count::Pending as usize].fetch_add(1, Relaxed) + 1
    }
    pub fn admitted(&self, pending: u64) {
        self.counts[Count::HighWater as usize]
            .fetch_max(pending.min(super::QUEUE_CAPACITY as u64), Relaxed);
        self.add(Count::Enqueued, 1);
    }
    pub fn leaving(&self) {
        self.counts[Count::Pending as usize].fetch_sub(1, Relaxed);
    }
    pub fn dequeued(&self, at: Instant) {
        self.leaving();
        self.add(Count::Dequeued, 1);
        let n = at.elapsed().as_nanos() as u64;
        self.times[Time::QueueDelay as usize].fetch_add(n, Relaxed);
        self.max_delay.fetch_max(n, Relaxed);
    }
    pub fn snapshot(&self) -> PipelineStats {
        PipelineStats {
            callback_records: self.get(Count::Callback),
            unsupported_records: self.get(Count::Unsupported),
            decode_attempted: self.get(Count::Attempted),
            decode_succeeded: self.get(Count::Succeeded),
            decode_failed: self.get(Count::Failed),
            failed_without_output: self.get(Count::FailedWithoutOutput),
            enqueue_disconnected: self.get(Count::Disconnected),
            deliberately_filtered: self.get(Count::Filtered),
            enqueued: self.get(Count::Enqueued),
            queue_overflow: self.get(Count::Overflow),
            dequeued: self.get(Count::Dequeued),
            queue_pending: self.get(Count::Pending),
            queue_high_water: self.get(Count::HighWater),
            context_evictions: self.get(Count::ContextEvictions),
            queue_max_delay_ns: self.max_delay.load(Relaxed),
            elapsed_ns: [
                "callback",
                "lock_wait",
                "schema_lookup",
                "property_decode",
                "identity_query",
                "path_normalization",
                "event_construction",
                "enqueue",
                "queue_delay",
            ]
            .into_iter()
            .enumerate()
            .map(|(i, k)| (k.into(), self.times[i].load(Relaxed)))
            .collect(),
            ..Default::default()
        }
    }
}
pub struct Timer<'a>(&'a Metrics, Time, Instant);
impl Drop for Timer<'_> {
    fn drop(&mut self) {
        self.0.times[self.1 as usize].fetch_add(self.2.elapsed().as_nanos() as u64, Relaxed);
    }
}

macro_rules! measured {
    ($metrics:expr, $category:ident, $body:expr) => {{
        let _timer = $metrics.timer(Time::$category);
        $body
    }};
}
pub(super) use measured;
