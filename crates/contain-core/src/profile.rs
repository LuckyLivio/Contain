//! Opt-in, bounded diagnostics. No per-event I/O, paths or payloads are recorded.
//! Wall timings may be nested and include scheduler preemption. Optional own
//! thread CPU accounting is sampled at window boundaries, never per event.
use serde::Serialize;
use std::{cell::RefCell, collections::BTreeMap, time::Instant};

const MAX_QUEUE_SPANS: usize = 8192;
const MAX_COUNTERS: usize = 128;

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
    units: u64,
    cpu_ns: Option<u64>,
}

/// All completed windows contribute even after the bounded sample fills. Only
/// non-overlapping stages may be added together; writer windows can be nested.
#[derive(Default, Serialize)]
struct QueueStageTotal {
    count: u64,
    elapsed_ns: u64,
    max_elapsed_ns: u64,
    cpu_count: u64,
    cpu_ns: u64,
    units: u64,
    enqueued: u64,
    overflow: u64,
    dequeued: u64,
    max_pending_before: u64,
    max_pending_after: u64,
}
impl QueueStageTotal {
    fn add(&mut self, span: &QueueSpan) {
        self.count += 1;
        let elapsed = span.end_ns.saturating_sub(span.start_ns);
        self.elapsed_ns = self.elapsed_ns.saturating_add(elapsed);
        self.max_elapsed_ns = self.max_elapsed_ns.max(elapsed);
        if let Some(cpu_ns) = span.cpu_ns {
            self.cpu_count += 1;
            self.cpu_ns = self.cpu_ns.saturating_add(cpu_ns);
        }
        self.units = self.units.saturating_add(span.units);
        self.enqueued = self
            .enqueued
            .saturating_add(span.after.enqueued.saturating_sub(span.before.enqueued));
        self.overflow = self
            .overflow
            .saturating_add(span.after.overflow.saturating_sub(span.before.overflow));
        self.dequeued = self
            .dequeued
            .saturating_add(span.after.dequeued.saturating_sub(span.before.dequeued));
        self.max_pending_before = self.max_pending_before.max(span.before.pending);
        self.max_pending_after = self.max_pending_after.max(span.after.pending);
    }
    fn merge(&mut self, other: Self) {
        self.count += other.count;
        self.elapsed_ns = self.elapsed_ns.saturating_add(other.elapsed_ns);
        self.max_elapsed_ns = self.max_elapsed_ns.max(other.max_elapsed_ns);
        self.cpu_count += other.cpu_count;
        self.cpu_ns = self.cpu_ns.saturating_add(other.cpu_ns);
        self.units = self.units.saturating_add(other.units);
        self.enqueued = self.enqueued.saturating_add(other.enqueued);
        self.overflow = self.overflow.saturating_add(other.overflow);
        self.dequeued = self.dequeued.saturating_add(other.dequeued);
        self.max_pending_before = self.max_pending_before.max(other.max_pending_before);
        self.max_pending_after = self.max_pending_after.max(other.max_pending_after);
    }
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
    queue_stage_totals: BTreeMap<&'static str, QueueStageTotal>,
    counters: BTreeMap<&'static str, u64>,
    omitted_counter_names: u64,
    thread_cpu_enabled: bool,
    thread_cpu_timing: &'static str,
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
            queue_stage_totals: BTreeMap::new(), counters: BTreeMap::new(), omitted_counter_names: 0,
            thread_cpu_enabled: std::env::var_os("CONTAIN_PROFILE_THREAD_CPU").is_some(),
            thread_cpu_timing: "optional Windows GetThreadTimes own-thread kernel+user; 100 ns units, accounting may be coarser; null/unavailable is not zero CPU",
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

pub struct QueueTimer(Option<(&'static str, u64, QueueSnapshot, Option<u64>)>, u64);

impl QueueTimer {
    /// Work already counted by the caller, without per-record profile calls.
    pub fn units(mut self, units: usize) -> Self {
        self.1 = units as u64;
        self
    }
}

pub fn queue_span(stage: &'static str) -> QueueTimer {
    QueueTimer(
        DATA.with(|p| {
            let p = p.borrow();
            let p = p.as_ref()?;
            let probe = p.queue_probe.as_ref()?;
            // Window brackets both counter samples, including their small sampling cost.
            Some((
                stage,
                p.start.elapsed().as_nanos() as u64,
                probe(),
                p.thread_cpu_enabled.then(thread_cpu_ns).flatten(),
            ))
        }),
        0,
    )
}

impl Drop for QueueTimer {
    fn drop(&mut self) {
        let Some((stage, start_ns, before, cpu_start)) = self.0 else {
            return;
        };
        DATA.with(|p| {
            if let Some(p) = &mut *p.borrow_mut()
                && let Some(probe) = &p.queue_probe
            {
                let after = probe();
                let cpu_ns = cpu_start
                    .and_then(|before| thread_cpu_ns().map(|after| after.saturating_sub(before)));
                let end_ns = p.start.elapsed().as_nanos() as u64;
                let span = QueueSpan {
                    stage,
                    start_ns,
                    end_ns,
                    before,
                    after,
                    units: self.1,
                    cpu_ns,
                };
                p.queue_stage_totals.entry(stage).or_default().add(&span);
                if p.queue_spans.len() < MAX_QUEUE_SPANS {
                    p.queue_spans.push(span);
                } else {
                    p.omitted_queue_spans += 1;
                }
            }
        });
    }
}

#[cfg(windows)]
fn thread_cpu_ns() -> Option<u64> {
    use windows_sys::Win32::{
        Foundation::FILETIME,
        System::Threading::{GetCurrentThread, GetThreadTimes},
    };
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: the pseudo-handle refers to this thread and all outputs are valid.
    let ok = unsafe {
        GetThreadTimes(
            GetCurrentThread(),
            &mut created,
            &mut exited,
            &mut kernel,
            &mut user,
        )
    };
    if ok == 0 {
        return None;
    }
    let ticks =
        |time: FILETIME| (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime);
    Some(
        ticks(kernel)
            .saturating_add(ticks(user))
            .saturating_mul(100),
    )
}

#[cfg(not(windows))]
fn thread_cpu_ns() -> Option<u64> {
    None
}
pub fn epoch() -> Option<Instant> {
    DATA.with(|p| p.borrow().as_ref().map(|p| p.start))
}

#[derive(Clone, Copy)]
pub(crate) struct WorkerContext {
    epoch: Instant,
    thread_cpu_enabled: bool,
}

pub(crate) fn worker_context() -> Option<WorkerContext> {
    DATA.with(|p| {
        p.borrow().as_ref().map(|p| WorkerContext {
            epoch: p.start,
            thread_cpu_enabled: p.thread_cpu_enabled,
        })
    })
}

/// Worker diagnostics share the capture's monotonic epoch, but never a hot-path
/// mutex. Completed workers return bounded data for merging after they join.
pub(crate) struct WorkerProfile {
    stages: BTreeMap<&'static str, Distribution>,
    batches: Vec<[u64; 4]>,
    omitted_batches: u64,
    queue_spans: Vec<QueueSpan>,
    omitted_queue_spans: u64,
    queue_stage_totals: BTreeMap<&'static str, QueueStageTotal>,
    counters: BTreeMap<&'static str, u64>,
    omitted_counter_names: u64,
}

pub(crate) fn worker_start(context: Option<WorkerContext>) {
    if let Some(context) = context {
        begin();
        DATA.with(|p| {
            let mut p = p.borrow_mut();
            let p = p.as_mut().unwrap();
            p.start = context.epoch;
            p.thread_cpu_enabled = context.thread_cpu_enabled;
        });
    } else {
        DATA.with(|p| *p.borrow_mut() = None);
    }
}

pub(crate) fn worker_finish() -> Option<WorkerProfile> {
    DATA.with(|p| p.borrow_mut().take()).map(|p| WorkerProfile {
        stages: p.stages,
        batches: p.batches,
        omitted_batches: p.omitted_batches,
        queue_spans: p.queue_spans,
        omitted_queue_spans: p.omitted_queue_spans,
        queue_stage_totals: p.queue_stage_totals,
        counters: p.counters,
        omitted_counter_names: p.omitted_counter_names,
    })
}

pub(crate) fn merge_worker(worker: Option<WorkerProfile>) {
    let Some(worker) = worker else { return };
    DATA.with(|data| {
        let mut data = data.borrow_mut();
        let Some(data) = data.as_mut() else { return };
        for (stage, total) in worker.queue_stage_totals {
            data.queue_stage_totals
                .entry(stage)
                .or_default()
                .merge(total);
        }
        data.omitted_counter_names += worker.omitted_counter_names;
        merge_counters(data, worker.counters);
        for (name, incoming) in worker.stages {
            let target = data.stages.entry(name).or_default();
            target.count += incoming.count;
            target.total_ns += incoming.total_ns;
            target.max_ns = target.max_ns.max(incoming.max_ns);
            target.buckets.resize(64, 0);
            for (index, value) in incoming.buckets.into_iter().enumerate() {
                target.buckets[index] += value;
            }
        }
        data.omitted_batches += worker.omitted_batches;
        for batch in worker.batches {
            if data.batches.len() < 4096 {
                data.batches.push(batch);
            } else {
                data.omitted_batches += 1;
            }
        }
        data.omitted_queue_spans += worker.omitted_queue_spans;
        for span in worker.queue_spans {
            if data.queue_spans.len() < MAX_QUEUE_SPANS {
                data.queue_spans.push(span);
            } else {
                data.omitted_queue_spans += 1;
            }
        }
    });
}

fn merge_counters(data: &mut Profile, values: impl IntoIterator<Item = (&'static str, u64)>) {
    for (name, value) in values {
        if let Some(total) = data.counters.get_mut(name) {
            *total = total.saturating_add(value);
        } else if data.counters.len() < MAX_COUNTERS {
            data.counters.insert(name, value);
        } else {
            data.omitted_counter_names += 1;
        }
    }
}

/// Merge one page's fixed numeric counters. Names are static program labels;
/// callers never pass event IDs, resource names, paths or other payload data.
pub(crate) fn counters(values: impl IntoIterator<Item = (&'static str, u64)>) {
    DATA.with(|data| {
        if let Some(data) = data.borrow_mut().as_mut() {
            merge_counters(data, values);
        }
    });
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
    fn worker_diagnostics_keep_one_epoch_and_global_bounds() {
        begin();
        let epoch = epoch();
        let context = worker_context();
        add("shared", 8);
        let worker = std::thread::spawn(move || {
            worker_start(context);
            add("shared", 16);
            observe_queue(QueueSnapshot::default);
            for _ in 0..4100 {
                batch(1, 2, 3);
            }
            for _ in 0..MAX_QUEUE_SPANS + 1 {
                drop(queue_span("worker").units(2));
            }
            counters([("records", 7), ("worker_only", 3)]);
            worker_finish()
        })
        .join()
        .unwrap();
        batch(1, 2, 3);
        observe_queue(QueueSnapshot::default);
        drop(queue_span("main"));
        counters([("records", 5)]);
        merge_worker(worker);
        let p = DATA.with(|p| p.borrow_mut().take()).unwrap();
        assert_eq!(p.start, epoch.unwrap());
        assert_eq!(p.stages["shared"].count, 2);
        assert_eq!(p.stages["shared"].total_ns, 24);
        assert_eq!(p.stages["shared"].max_ns, 16);
        assert_eq!(p.batches.len(), 4096);
        assert_eq!(p.omitted_batches, 5);
        assert_eq!(p.queue_spans.len(), MAX_QUEUE_SPANS);
        assert_eq!(p.omitted_queue_spans, 2);
        assert_eq!(p.stages["raw_batch_flush"].count, 4101);
        assert_eq!(
            p.queue_stage_totals["worker"].count,
            (MAX_QUEUE_SPANS + 1) as u64
        );
        assert_eq!(
            p.queue_stage_totals["worker"].units,
            (MAX_QUEUE_SPANS * 2 + 2) as u64
        );
        assert_eq!(p.counters["records"], 12);
        assert_eq!(p.counters["worker_only"], 3);
        assert!(!enabled());
    }

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

    #[test]
    fn all_stage_losses_survive_sample_truncation_and_cpu_unavailability() {
        begin();
        DATA.with(|p| p.borrow_mut().as_mut().unwrap().thread_cpu_enabled = false);
        let counters = Rc::new(Cell::new(QueueSnapshot::default()));
        let probe = counters.clone();
        observe_queue(move || probe.get());
        for index in 0..MAX_QUEUE_SPANS + 3 {
            let _span = queue_span("receiver_follow_classify").units(4);
            if index >= MAX_QUEUE_SPANS {
                let mut current = counters.get();
                current.overflow += 1;
                counters.set(current);
            }
        }
        let p = DATA.with(|p| p.borrow_mut().take()).unwrap();
        assert_eq!(p.queue_spans.len(), MAX_QUEUE_SPANS);
        assert_eq!(p.omitted_queue_spans, 3);
        let total = &p.queue_stage_totals["receiver_follow_classify"];
        assert_eq!(total.count, (MAX_QUEUE_SPANS + 3) as u64);
        assert_eq!(total.units, (MAX_QUEUE_SPANS * 4 + 12) as u64);
        assert_eq!(total.overflow, 3);
        assert_eq!(total.cpu_count, 0);
        assert_eq!(total.cpu_ns, 0);
        assert!(p.queue_spans.iter().all(|span| span.cpu_ns.is_none()));
    }

    #[test]
    fn counters_are_bounded_merge_existing_names_and_do_nothing_when_disabled() {
        begin();
        for index in 0..MAX_COUNTERS + 2 {
            let name: &'static str = Box::leak(format!("counter_{index}").into_boxed_str());
            counters([(name, 1)]);
        }
        counters([("counter_0", 4)]);
        let p = DATA.with(|p| p.borrow_mut().take()).unwrap();
        assert_eq!(p.counters.len(), MAX_COUNTERS);
        assert_eq!(p.omitted_counter_names, 2);
        assert_eq!(p.counters["counter_0"], 5);
        counters([("disabled", 1)]);
        drop(queue_span("disabled").units(10));
        assert!(worker_finish().is_none());
    }

    #[test]
    fn cpu_aggregates_distinguish_missing_samples_from_measured_zero() {
        let span = QueueSpan {
            stage: "cpu_test",
            start_ns: 10,
            end_ns: 30,
            before: QueueSnapshot::default(),
            after: QueueSnapshot::default(),
            units: 1,
            cpu_ns: Some(0),
        };
        let mut total = QueueStageTotal::default();
        total.add(&span);
        total.add(&QueueSpan {
            cpu_ns: None,
            ..span
        });
        let mut other = QueueStageTotal::default();
        other.add(&QueueSpan {
            cpu_ns: Some(100),
            ..span
        });
        total.merge(other);
        assert_eq!(total.count, 3);
        assert_eq!(total.cpu_count, 2);
        assert_eq!(total.cpu_ns, 100);
        assert_eq!(total.elapsed_ns, 60);
    }

    #[test]
    fn workers_share_the_captured_cpu_setting_and_disable_without_context() {
        begin();
        DATA.with(|p| p.borrow_mut().as_mut().unwrap().thread_cpu_enabled = true);
        let context = worker_context();
        DATA.with(|p| p.borrow_mut().as_mut().unwrap().thread_cpu_enabled = false);
        let enabled_in_worker = std::thread::spawn(move || {
            worker_start(context);
            let cpu_enabled = DATA.with(|p| p.borrow().as_ref().unwrap().thread_cpu_enabled);
            worker_start(None);
            assert!(!enabled());
            cpu_enabled
        })
        .join()
        .unwrap();
        assert!(enabled_in_worker);
        DATA.with(|p| p.borrow_mut().take());
    }
}
