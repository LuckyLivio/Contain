//! Instrumentation-only offline cost probe. No queue, ETW source, capture,
//! installer, attribution or persistence algorithm is replaced by this tool.
use anyhow::{Result, ensure};
use contain_offline::profile::{self, QueueSnapshot};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{cell::Cell, hint::black_box, rc::Rc, time::Instant};

const STAGES: [&str; 6] = [
    "receiver_drain",
    "receiver_lock_wait",
    "receiver_lifecycle_ingest",
    "receiver_follow_classify",
    "receiver_follow_pending_scan",
    "receiver_handoff",
];

fn main() -> Result<()> {
    let mut args = std::env::args().skip(1);
    ensure!(
        args.next().as_deref() == Some("--spans"),
        "use --spans COUNT"
    );
    let spans: usize = args
        .next()
        .ok_or_else(|| anyhow::anyhow!("missing count"))?
        .parse()?;
    ensure!(
        (256..=1_000_000).contains(&spans) && args.next().is_none(),
        "spans must be 256..1000000"
    );
    profile::start();
    let enabled = profile::enabled();
    let snapshot = Rc::new(Cell::new(QueueSnapshot::default()));
    let probe = snapshot.clone();
    profile::observe_queue(move || probe.get());
    let mut block = [0xa5_u8; 4096];
    let mut output_digest = Sha256::new();
    let started = Instant::now();
    let diagnostic_epoch = profile::epoch().unwrap_or(started);
    for index in 0..spans {
        let _span = profile::queue_span(STAGES[index % STAGES.len()]).units(32);
        let _stage = profile::timer("offline_fixed_sha256_body");
        // Fixed numeric counter inputs test diagnostic sampling, not a model of
        // producer scheduling. The 256-step sawtooth reaches exactly 4096.
        let mut state = snapshot.get();
        // Delay the first anomaly beyond the historical 8192-span sample cap.
        let phase = index.saturating_sub(8192) % 256;
        if index >= 8192 {
            if phase < 128 {
                state.enqueued += 32;
                state.pending += 32;
            } else {
                state.dequeued += 32;
                state.pending -= 32;
            }
        }
        if index >= 8192 && phase == 127 {
            state.overflow += 1;
        }
        state.high_water = state.high_water.max(state.pending);
        if state.pending >= 1024 && state.first_backlog_ns.is_none() {
            state.first_backlog_ns = Some(diagnostic_epoch.elapsed().as_nanos() as u64);
        }
        if state.overflow > 0 && state.first_overflow_ns.is_none() {
            state.first_overflow_ns = Some(diagnostic_epoch.elapsed().as_nanos() as u64);
        }
        snapshot.set(state);
        block[..8].copy_from_slice(&(index as u64).to_le_bytes());
        output_digest.update(black_box(Sha256::digest(black_box(block))));
    }
    let elapsed_ns = started.elapsed().as_nanos();
    let output_sha256 = format!("{:x}", output_digest.finalize());
    let final_snapshot = snapshot.get();
    // All disk output is outside the timed loop, as in actual profile finish.
    profile::finish()?;
    println!(
        "{}",
        json!({
            "protocol": "contain-receiver-window-offline-cost-1",
            "synthetic": true,
            "real_etw": false,
            "native_ingest": false,
            "debug_assertions_disabled": !cfg!(debug_assertions),
            "architecture": std::env::consts::ARCH,
            "os": std::env::consts::OS,
            "profile_enabled": enabled,
            "spans": spans,
            "body_bytes_per_span": block.len(),
            "elapsed_ns": elapsed_ns,
            "output_sha256": output_sha256,
            "snapshot_input": {
                "enqueued": final_snapshot.enqueued,
                "overflow": final_snapshot.overflow,
                "dequeued": final_snapshot.dequeued,
                "pending": final_snapshot.pending,
            },
            "scope": "Actual production profile module with fixed nonempty SHA256 work; numeric snapshots are synthetic inputs. Timed loop excludes profile JSON I/O. This does not measure event collection, consumer scheduling, ETW decoding or installer overhead."
        })
    );
    Ok(())
}
