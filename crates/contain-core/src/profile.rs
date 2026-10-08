//! Opt-in, bounded diagnostics. No per-event I/O, paths or payloads are recorded.
//! Timings are elapsed time, may be nested, and include scheduler preemption.
use serde::Serialize;
use std::{cell::RefCell, collections::BTreeMap, time::Instant};

const MAX_QUEUE_SPANS: usize = 8192;

/// Independent monotonic counters, plus an approximate occupancy sample. The
/// callback may advance between loads; this is not an atomic queue balance.
#[derive(Clone, Copy, Debug, Default, Serialize)]
pub struct QueueSnapshot {
    pub enqueued: u64,
    pub overflow: u64,
    pub dequeued: u64,
    pub pending: u64,
}

#[derive(Serialize)]
struct QueueSpan {
    stage: &'static str,
    start_ns: u64,
    end_ns: u64,
    before: QueueSnapshot,
    after: QueueSnapshot,
}

#[derive(Default, Serialize)]
pub struct Distribution {
    count: u64,
    total_ns: u64,
    max_ns: u64,
    // Log2 nanosecond buckets; percentile values are upper bounds.
    buckets: Vec<u64>,
}
impl Distribution {
    fn add(&mut self, n: u64) {
        if self.buckets.is_empty() {
            self.buckets.resize(64, 0);
        }
        self.count += 1;
        self.total_ns += n;
        self.max_ns = self.max_ns.max(n);
        self.buckets[(64 - n.leading_zeros()).min(63) as usize] += 1;
    }
}
#[derive(Serialize)]
struct Profile {
    #[serde(skip)]
    start: Instant,
    #[serde(skip)]
    last_drain: Option<Instant>,
    stages: BTreeMap<&'static str, Distribution>,
    // elapsed ns, records, payload bytes, flush ns. Limited to 4096 batches.
    batches: Vec<[u64; 4]>,
    omitted_batches: u64,
    checkpoint_timing: &'static str,
    #[serde(skip)]
    queue_probe: Option<Box<dyn Fn() -> QueueSnapshot>>,
    queue_spans: Vec<QueueSpan>,
    omitted_queue_spans: u64,
}
thread_local! {
    static DATA: RefCell<Option<Profile>> = const { RefCell::new(None) };
}
fn begin() {
    DATA.with(|p| {
        *p.borrow_mut() = Some(Profile {
            start: Instant::now(), last_drain: None, stages: BTreeMap::new(),
            batches: Vec::new(), omitted_batches: 0,
            checkpoint_timing: "not isolated; automatic checkpoint is included in commit; scheduling unchanged",
            queue_probe: None, queue_spans: Vec::new(), omitted_queue_spans: 0,
        });
    });
}
pub fn start() {
    if std::env::var_os("CONTAIN_PROFILE_PATH").is_some() {
        begin();
    } else {
        DATA.with(|p| *p.borrow_mut() = None);
    }
}

pub(crate) fn checkpoint_deferred() {
    DATA.with(|p| {
        if let Some(p) = &mut *p.borrow_mut() {
            p.checkpoint_timing = "E1: automatic checkpoint disabled during capture; explicit PASSIVE after stop, queue drain and final raw batch; restored to 4096 before postprocessing";
        }
    });
}

/// One callback counter handle; no event payloads, I/O or decoder lock required.
pub fn observe_queue(probe: impl Fn() -> QueueSnapshot + 'static) {
    DATA.with(|p| {
        if let Some(p) = &mut *p.borrow_mut() {
            p.queue_probe = Some(Box::new(probe));
        }
    });
}

pub struct QueueTimer(Option<(&'static str, u64, QueueSnapshot)>);

pub fn queue_span(stage: &'static str) -> QueueTimer {
    QueueTimer(DATA.with(|p| {
        let p = p.borrow();
        let p = p.as_ref()?;
        let probe = p.queue_probe.as_ref()?;
        // Window brackets both counter samples, including their small sampling cost.
        Some((stage, p.start.elapsed().as_nanos() as u64, probe()))
    }))
}

impl Drop for QueueTimer {
    fn drop(&mut self) {
        let Some((stage, start_ns, before)) = self.0 else {
            return;
        };
        DATA.with(|p| {
            if let Some(p) = &mut *p.borrow_mut()
                && let Some(probe) = &p.queue_probe
            {
                let after = probe();
                let end_ns = p.start.elapsed().as_nanos() as u64;
                if p.queue_spans.len() < MAX_QUEUE_SPANS {
                    p.queue_spans.push(QueueSpan {
                        stage,
                        start_ns,
                        end_ns,
                        before,
                        after,
                    });
                } else {
                    p.omitted_queue_spans += 1;
                }
            }
        });
    }
}
pub fn epoch() -> Option<Instant> {
    DATA.with(|p| p.borrow().as_ref().map(|p| p.start))
}
pub fn enabled() -> bool {
    DATA.with(|p| p.borrow().is_some())
}
pub struct Timer(&'static str, Option<Instant>);
pub fn timer(stage: &'static str) -> Timer {
    Timer(stage, enabled().then(Instant::now))
}
impl Drop for Timer {
    fn drop(&mut self) {
        if let Some(at) = self.1 {
            add(self.0, at.elapsed().as_nanos() as u64);
        }
    }
}
pub fn add(stage: &'static str, ns: u64) {
    DATA.with(|p| {
        if let Some(p) = &mut *p.borrow_mut() {
            p.stages.entry(stage).or_default().add(ns);
        }
    });
}
pub fn drain() {
    DATA.with(|p| {
        if let Some(p) = &mut *p.borrow_mut() {
            let now = Instant::now();
            if let Some(last) = p.last_drain.replace(now) {
                p.stages
                    .entry("capture_between_drains")
                    .or_default()
                    .add(now.duration_since(last).as_nanos() as u64);
            }
        }
    });
}
pub fn batch(records: usize, bytes: usize, ns: u64) {
    DATA.with(|p| {
        if let Some(p) = &mut *p.borrow_mut() {
            p.stages.entry("raw_batch_flush").or_default().add(ns);
            if p.batches.len() < 4096 {
                p.batches.push([
                    p.start.elapsed().as_nanos() as u64,
                    records as u64,
                    bytes as u64,
                    ns,
                ]);
            } else {
                p.omitted_batches += 1;
            }
        }
    });
}
pub fn finish() -> anyhow::Result<()> {
    let data = DATA.with(|p| p.borrow_mut().take());
    if let (Some(data), Some(path)) = (data, std::env::var_os("CONTAIN_PROFILE_PATH")) {
        // Called only after ETW has stopped; external wall time includes this output.
        std::fs::write(path, serde_json::to_vec_pretty(&data)?)?;
    }
    Ok(())
}
macro_rules! measured {
    ($stage:literal, $body:expr) => {{
        let _timer = $crate::profile::timer($stage);
        $body
    }};
}
pub(crate) use measured;

#[cfg(test)]
pub(crate) fn test_profile(action: impl FnOnce()) -> serde_json::Value {
    begin();
    action();
    serde_json::to_value(DATA.with(|p| p.borrow_mut().take()).unwrap()).unwrap()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::Cell, rc::Rc};

    #[test]
    fn queue_spans_count_stall_arrivals_and_keep_nested_windows_separate() {
        begin();
        let counters = Rc::new(Cell::new(QueueSnapshot::default()));
        let probe = counters.clone();
        observe_queue(move || probe.get());
        {
            let _batch = queue_span("raw_batch_flush");
            counters.set(QueueSnapshot {
                enqueued: 3,
                pending: 3,
                ..Default::default()
            });
            {
                let _commit = queue_span("raw_commit_including_autocheckpoint");
                counters.set(QueueSnapshot {
                    enqueued: 8,
                    overflow: 5,
                    pending: 8,
                    dequeued: 0,
                });
            }
            counters.set(QueueSnapshot {
                enqueued: 8,
                overflow: 7,
                pending: 8,
                dequeued: 0,
            });
        }
        let p = DATA.with(|p| p.borrow_mut().take()).unwrap();
        assert_eq!(p.queue_spans.len(), 2);
        let commit = &p.queue_spans[0];
        let batch = &p.queue_spans[1];
        assert_eq!(commit.after.overflow - commit.before.overflow, 5);
        assert_eq!(batch.after.overflow - batch.before.overflow, 7);
        assert_eq!(commit.after.enqueued - commit.before.enqueued, 5);
        assert!(batch.start_ns <= commit.start_ns && commit.end_ns <= batch.end_ns);
        assert!(
            serde_json::to_value(p)
                .unwrap()
                .get("queue_probe")
                .is_none()
        );
    }

    #[test]
    fn queue_diagnostics_are_bounded_and_absent_without_a_probe() {
        begin();
        drop(queue_span("no_probe"));
        observe_queue(QueueSnapshot::default);
        for _ in 0..MAX_QUEUE_SPANS + 3 {
            drop(queue_span("raw_batch_flush"));
        }
        let p = DATA.with(|p| p.borrow_mut().take()).unwrap();
        assert_eq!(p.queue_spans.len(), MAX_QUEUE_SPANS);
        assert_eq!(p.omitted_queue_spans, 3);
        drop(queue_span("disabled"));
        assert!(!enabled());
    }
}
