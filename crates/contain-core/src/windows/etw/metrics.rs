//! Cumulative counters shared with the consumer; timers are elapsed nanoseconds,
//! not CPU utilization. No path or schema labels are allocated per record.
use crate::model::{PipelineStats, ProviderStats};
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering::Relaxed};
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
    IdentityQueries,
    Unresolved,
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
    Context,
}
pub struct Metrics {
    counts: [AtomicU64; 16],
    provider_counts: [[AtomicU64; 16]; 3],
    provider_times: [[AtomicU64; 10]; 3],
    provider: AtomicUsize,
    times: [AtomicU64; 10],
    max_delay: AtomicU64,
    profile_start: Option<Instant>,
    arrivals: AtomicU64,
    peak_arrivals: AtomicU64,
    overflow_times: [AtomicU64; 64],
    last_overflow: AtomicU64,
}
impl Default for Metrics {
    fn default() -> Self {
        Self {
            counts: Default::default(),
            provider_counts: Default::default(),
            provider_times: Default::default(),
            provider: AtomicUsize::new(0),
            times: Default::default(),
            max_delay: AtomicU64::new(0),
            profile_start: crate::profile::epoch(),
            arrivals: AtomicU64::new(0),
            peak_arrivals: AtomicU64::new(0),
            overflow_times: std::array::from_fn(|_| AtomicU64::new(0)),
            last_overflow: AtomicU64::new(0),
        }
    }
}
impl Metrics {
    pub fn queue_snapshot(&self) -> crate::profile::QueueSnapshot {
        crate::profile::QueueSnapshot {
            enqueued: self.get(Count::Enqueued),
            overflow: self.get(Count::Overflow),
            dequeued: self.get(Count::Dequeued),
            pending: self.get(Count::Pending),
        }
    }

    pub fn provider(&self, provider: usize) {
        self.provider.store(provider, Relaxed);
    }
    pub fn record_lock(&self, ns: u64) {
        self.times[Time::Lock as usize].fetch_add(ns, Relaxed);
        self.provider_times[self.provider.load(Relaxed)][Time::Lock as usize]
            .fetch_add(ns, Relaxed);
    }
    pub fn add(&self, c: Count, n: u64) {
        let prior = self.counts[c as usize].fetch_add(n, Relaxed);
        if !matches!(c, Count::Dequeued | Count::Pending | Count::HighWater) {
            self.provider_counts[self.provider.load(Relaxed)][c as usize].fetch_add(n, Relaxed);
        }
        if matches!(c, Count::Overflow)
            && let Some(start) = self.profile_start
        {
            let at = start.elapsed().as_nanos() as u64;
            self.last_overflow.store(at, Relaxed);
            if prior < 64 {
                self.overflow_times[prior as usize].store(at, Relaxed);
            }
        }
    }
    pub fn get(&self, c: Count) -> u64 {
        self.counts[c as usize].load(Relaxed)
    }
    pub fn timer(&self, t: Time) -> Timer<'_> {
        if matches!(t, Time::Identity) {
            self.add(Count::IdentityQueries, 1);
        }
        Timer(self, t, Instant::now(), self.provider.load(Relaxed))
    }
    pub fn entering(&self) -> u64 {
        if let Some(start) = self.profile_start {
            let window = (start.elapsed().as_millis() as u64 / 100).min(u32::MAX as u64);
            let count = self.count_arrival(window);
            self.peak_arrivals.fetch_max(count, Relaxed);
        }
        self.counts[Count::Pending as usize].fetch_add(1, Relaxed) + 1
    }
    fn count_arrival(&self, window: u64) -> u64 {
        // Explicit CAS retains compatibility with toolchains on either side of
        // the fetch_update -> try_update rename, without suppressing warnings.
        let mut old = self.arrivals.load(Relaxed);
        loop {
            let count = if old >> 32 == window {
                (old & 0xffffffff) + 1
            } else {
                1
            };
            match self
                .arrivals
                .compare_exchange_weak(old, (window << 32) | count, Relaxed, Relaxed)
            {
                Ok(_) => return count,
                Err(current) => old = current,
            }
        }
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
            providers: ["file", "process", "registry"]
                .into_iter()
                .enumerate()
                .map(|(i, name)| {
                    let c = |c: Count| self.provider_counts[i][c as usize].load(Relaxed);
                    (
                        name.into(),
                        ProviderStats {
                            callback_records: c(Count::Callback),
                            identity_queries: c(Count::IdentityQueries),
                            unresolved: c(Count::Unresolved),
                            enqueued: c(Count::Enqueued),
                            filtered: c(Count::Filtered),
                            overflow: c(Count::Overflow),
                            disconnected: c(Count::Disconnected),
                            decode_failed: c(Count::Failed),
                            context_evictions: c(Count::ContextEvictions),
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
                                "context",
                            ]
                            .into_iter()
                            .enumerate()
                            .map(|(j, k)| (k.into(), self.provider_times[i][j].load(Relaxed)))
                            .collect(),
                        },
                    )
                })
                .collect(),
            overflow_last_ns: self.profile_start.map(|_| self.last_overflow.load(Relaxed)),
            arrival_peak_per_100ms: self.profile_start.map(|_| self.peak_arrivals.load(Relaxed)),
            overflow_first_ns: self
                .overflow_times
                .iter()
                .map(|v| v.load(Relaxed))
                .filter(|v| *v != 0)
                .collect(),
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
                "context",
            ]
            .into_iter()
            .enumerate()
            .map(|(i, k)| (k.into(), self.times[i].load(Relaxed)))
            .collect(),
            ..Default::default()
        }
    }
}
pub struct Timer<'a>(&'a Metrics, Time, Instant, usize);
impl Drop for Timer<'_> {
    fn drop(&mut self) {
        let elapsed = self.2.elapsed().as_nanos() as u64;
        self.0.times[self.1 as usize].fetch_add(elapsed, Relaxed);
        if !matches!(self.1, Time::QueueDelay) {
            self.0.provider_times[self.3][self.1 as usize].fetch_add(elapsed, Relaxed);
        }
    }
}

macro_rules! measured {
    ($metrics:expr, $category:ident, $body:expr) => {{
        let _timer = $metrics.timer(Time::$category);
        $body
    }};
}
pub(super) use measured;

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn arrival_windows_count_concurrent_updates_and_reset_on_next_window() {
        let metrics = std::sync::Arc::new(Metrics::default());
        let workers: Vec<_> = (0..4)
            .map(|_| {
                let metrics = metrics.clone();
                std::thread::spawn(move || {
                    for _ in 0..1000 {
                        metrics.count_arrival(7);
                    }
                })
            })
            .collect();
        for worker in workers {
            worker.join().unwrap();
        }
        assert_eq!(metrics.count_arrival(7), 4001);
        assert_eq!(metrics.count_arrival(8), 1);
        assert_eq!(metrics.count_arrival(8), 2);
    }
}
