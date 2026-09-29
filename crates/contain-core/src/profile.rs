//! Opt-in, bounded diagnostics. No per-event I/O, paths or payloads are recorded.
//! Timings are elapsed time, may be nested, and include scheduler preemption.
use serde::Serialize;
use std::{cell::RefCell, collections::BTreeMap, time::Instant};

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
}
thread_local! {
    static DATA: RefCell<Option<Profile>> = const { RefCell::new(None) };
}
pub fn start() {
    if std::env::var_os("CONTAIN_PROFILE_PATH").is_some() {
        DATA.with(|p| *p.borrow_mut() = Some(Profile {
            start: Instant::now(), last_drain: None, stages: BTreeMap::new(),
            batches: Vec::new(), omitted_batches: 0,
            checkpoint_timing: "not isolated; automatic checkpoint is included in commit; scheduling unchanged",
        }));
    }
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
