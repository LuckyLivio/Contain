//! Synthetic in-memory policy throughput. This does not start or validate ETW.
use contain_core::{
    correlation::correlate,
    filesystem::Snapshot,
    lifetime::LifetimeCache,
    model::{AttributionEvidence, EvidenceSource, RawEvidence, SystemEvent},
    windows::registry_context::RegistryContext,
};
use serde_json::{Value, json};
use std::{hint::black_box, path::PathBuf, process::Command, time::Instant};

fn lifecycle(operation: &str, index: usize) -> SystemEvent {
    // Use realistic 18-digit Windows FILETIME values above JSON's exact-float range.
    let birth = 134_350_000_000_000_000 + (index as u64) * 4 + 1;
    SystemEvent {
        timestamp_ticks: birth + u64::from(operation == "process_stop"),
        event_type: "lifecycle".into(),
        operation: operation.into(),
        resource: "C:\\fixture\\installer.exe".into(),
        evidence: AttributionEvidence {
            source: EvidenceSource::EtwProcess,
            pid: Some((index % 64) as u32 + 1),
            process_creation_time: Some(birth),
            ..Default::default()
        },
        ..Default::default()
    }
}

fn measured(
    name: &str,
    items: usize,
    records: usize,
    samples: usize,
    warmups: usize,
    expected: u64,
    mut run: impl FnMut() -> u64,
) -> Value {
    for _ in 0..warmups {
        assert_eq!(black_box(run()), expected);
    }
    let mut timings = Vec::with_capacity(samples);
    for _ in 0..samples {
        let start = Instant::now();
        let checksum = black_box(run());
        let elapsed = start.elapsed().as_nanos() as f64;
        assert_eq!(checksum, expected, "benchmark must validate its outputs");
        timings.push(elapsed / items as f64);
    }
    let sample_order = timings.clone();
    timings.sort_by(f64::total_cmp);
    let median = if samples.is_multiple_of(2) {
        (timings[samples / 2 - 1] + timings[samples / 2]) / 2.0
    } else {
        timings[samples / 2]
    };
    let p95_index = ((samples as f64 * 0.95).ceil() as usize).saturating_sub(1);
    json!({
        "name": name,
        "work_items_per_sample": items,
        "input_records_per_sample": records,
        "measured_samples": samples,
        "warmup_samples": warmups,
        "median_ns_per_work_item": median,
        "min_ns_per_work_item": timings[0],
        "p95_ns_per_work_item": timings[p95_index],
        "median_work_items_per_second": 1_000_000_000.0 / median,
        "sample_ns_per_work_item": sample_order,
        "validated_checksum": expected,
    })
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut items = 20_000_usize;
    let mut samples = 7_usize;
    let mut warmups = 2_usize;
    let mut output =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../target/quality/event-pipeline.json");
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--events" => items = args.next().ok_or("--events requires a value")?.parse()?,
            "--samples" => samples = args.next().ok_or("--samples requires a value")?.parse()?,
            "--warmups" => warmups = args.next().ok_or("--warmups requires a value")?.parse()?,
            "--output" => output = PathBuf::from(args.next().ok_or("--output requires a path")?),
            "--bench" => {}
            other => return Err(format!("unknown argument: {other}").into()),
        }
    }
    if !(100..=1_000_000).contains(&items)
        || !items.is_multiple_of(2)
        || !(3..=50).contains(&samples)
        || warmups > 10
    {
        return Err(
            "use an even --events in 100..1000000, --samples 3..50, --warmups 0..10".into(),
        );
    }

    // Fixture generation and JSON encoding are outside all timed regions.
    let lifecycle_events: Vec<_> = (0..items / 2)
        .flat_map(|index| {
            [
                lifecycle("process_start", index),
                lifecycle("process_stop", index),
            ]
        })
        .collect();
    let json_events: Vec<_> = lifecycle_events
        .iter()
        .take(64)
        .map(|event| serde_json::to_vec(event).unwrap())
        .collect();
    let expected_ticks: u64 = (0..items)
        .map(|i| lifecycle_events[i % json_events.len()].timestamp_ticks)
        .fold(0_u64, u64::wrapping_add);
    let mut file_events: Vec<_> = (0..items / 2)
        .flat_map(|index| {
            let id = format!("request-{index}");
            [
                SystemEvent {
                    id: id.clone(),
                    event_type: "file".into(),
                    operation: "write_requested".into(),
                    resource: format!("C:\\fixture\\{index}.bin"),
                    ..Default::default()
                },
                SystemEvent {
                    id: format!("completion-{index}"),
                    event_type: "completion".into(),
                    success: Some(true),
                    raw: RawEvidence {
                        related_event: Some(id),
                        status: Some(0),
                        ..Default::default()
                    },
                    ..Default::default()
                },
            ]
        })
        .collect();
    let empty_snapshot = Snapshot::default();
    let mut results = Vec::new();
    results.push(measured(
        "event_json_parse_exact_ticks",
        items,
        items,
        samples,
        warmups,
        expected_ticks,
        || {
            let mut checksum = 0_u64;
            for index in 0..items {
                let event: SystemEvent =
                    serde_json::from_slice(black_box(&json_events[index % json_events.len()]))
                        .unwrap();
                assert_eq!(
                    event.timestamp_ticks,
                    lifecycle_events[index % json_events.len()].timestamp_ticks
                );
                checksum = checksum.wrapping_add(black_box(event.timestamp_ticks));
            }
            checksum
        },
    ));
    results.push(measured(
        "lifetime_ingest_resolve_batched_512",
        items,
        items,
        samples,
        warmups,
        items as u64,
        || {
            let mut checks = 0;
            // Bound each cache independently so this is not a capacity/drop test.
            // Allocation, inserts, two queries per pair and destruction are timed.
            for batch in lifecycle_events.chunks(512) {
                let mut cache = LifetimeCache::default();
                for pair in batch.as_chunks::<2>().0 {
                    let pid = pair[0].evidence.pid.unwrap();
                    cache.ingest(black_box(&pair[0]));
                    assert_eq!(
                        cache
                            .resolve(pid, pair[0].timestamp_ticks)
                            .and_then(|p| p.creation_time),
                        pair[0].evidence.process_creation_time
                    );
                    cache.ingest(black_box(&pair[1]));
                    assert!(cache.resolve(pid, pair[1].timestamp_ticks + 1).is_none());
                    checks += 2;
                }
                assert_eq!(cache.dropped, 0);
            }
            checks
        },
    ));
    results.push(measured(
        "completion_correlation_batched_512",
        items,
        items,
        samples,
        warmups,
        (items / 2) as u64,
        || {
            let mut count = 0;
            for batch in file_events.chunks_mut(512) {
                let operations = correlate(black_box(batch), &empty_snapshot, &empty_snapshot);
                assert_eq!(operations.len(), batch.len() / 2);
                assert!(
                    operations
                        .iter()
                        .all(|operation| operation.operation == "Modified"
                            && operation.success == Some(true)
                            && operation.raw_events.len() == 2)
                );
                count += black_box(operations.len() as u64);
            }
            count
        },
    ));
    results.push(measured(
        "registry_open_get_close",
        items,
        0,
        samples,
        warmups,
        items as u64,
        || {
            let mut cache = RegistryContext::default();
            let mut count = 0;
            for index in 0..items {
                let owner = (1, 10);
                let at = index as u64 + 10;
                assert!(cache.observe_object(owner, 1, at));
                let path = cache.open(
                    owner,
                    1,
                    0,
                    "\\registry\\user\\sid",
                    black_box("software\\fixture"),
                    at,
                );
                assert!(path.is_some());
                assert_eq!(cache.get(owner, 1, at), path);
                cache.close(owner, 1);
                assert!(cache.get(owner, 1, at).is_none());
                count += 1;
            }
            assert_eq!(cache.dropped, 0);
            count
        },
    ));

    let command_text = |program: &str, arguments: &[&str]| -> Option<String> {
        Command::new(program)
            .args(arguments)
            .output()
            .ok()
            .filter(|output| output.status.success())
            .map(|output| String::from_utf8_lossy(&output.stdout).trim().to_owned())
    };
    let report = json!({
        "schema_version": 1,
        "measurement_kind": "synthetic_in_memory",
        "etw_validation": "not_performed",
        "etw_providers_started": false,
        "sqlite_measured": false,
        "profile": if cfg!(debug_assertions) { "debug" } else { "release" },
        "source_commit": command_text("git", &["rev-parse", "HEAD"]),
        "working_tree_status": command_text("git", &["status", "--porcelain"]),
        "compiler": command_text("rustc", &["-vV"]),
        "os": std::env::consts::OS,
        "os_version": command_text("cmd", &["/C", "ver"]),
        "arch": std::env::consts::ARCH,
        "processor_identifier": std::env::var("PROCESSOR_IDENTIFIER").ok(),
        "logical_processors": std::thread::available_parallelism().ok().map(|count| count.get()),
        "recorded_at_utc": chrono::Utc::now().to_rfc3339(),
        "process_id": std::process::id(),
        "fixture_generation_timed": false,
        "assertions_timed": true,
        "notes": "Work item definitions differ by case; compare the same named case only. No raw EVENT_RECORD, TDH decoding, kernel delivery, queues, disk persistence, installer slowdown or live ETW throughput is measured. p95 summarizes whole-sample averages, not individual event latency.",
        "results": results,
    });
    if let Some(parent) = output.parent() {
        std::fs::create_dir_all(parent)?;
    }
    std::fs::write(&output, serde_json::to_vec_pretty(&report)?)?;
    println!("{}", serde_json::to_string_pretty(&report)?);
    eprintln!("Synthetic benchmark saved to {}", output.display());
    Ok(())
}
