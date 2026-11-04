//! Disk-backed synthetic replay. Diagnostic only; never an ETW acceptance test.
use super::*;

#[test]
#[ignore = "explicit disk-backed storage cost experiment"]
fn synthetic_disk_replay() {
    crate::profile::start();
    let path = std::env::temp_dir().join(format!("contain-replay-{}.db", uuid::Uuid::new_v4()));
    let started = Instant::now();
    let mut db = Storage::open(&path).unwrap();
    let c = Capture {
        id: "synthetic-replay".into(),
        schema_version: 3,
        ..Default::default()
    };
    let mut w = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA).unwrap();
    let write_start = Instant::now();
    let mut wal_peak = 0;
    for page in 0..64 {
        let events = (1..=PAGE as u64)
            .map(|i| {
                let n = page * PAGE as u64 + i;
                SystemEvent {
                    id: format!("safe-synthetic-{n}"),
                    sequence: n,
                    timestamp_ticks: 134_000_000_000_000_000 + n,
                    timestamp: "synthetic".into(),
                    event_type: "file".into(),
                    operation: "write_requested".into(),
                    resource: format!("C:\\synthetic-only\\file-{}.dat", n % 1000),
                    raw: RawEvidence {
                        header_pid: Some(1234),
                        thread_id: Some(1235),
                        resource_resolved: true,
                        ..Default::default()
                    },
                    ..Default::default()
                }
            })
            .collect();
        w.append(&mut db, events);
        wal_peak = wal_peak.max(
            std::fs::metadata(format!("{}-wal", path.display()))
                .map(|m| m.len())
                .unwrap_or(0),
        );
    }
    let stats = w.finish(&mut db).unwrap();
    assert_eq!(stats.persisted, 16384);
    assert_eq!(stats.failed + stats.quota_dropped, 0);
    let write_ns = write_start.elapsed().as_nanos() as u64;
    let post_start = Instant::now();
    db.prepare_raw(&c.id).unwrap();
    let mut cursor = (0, 0);
    let mut rows = 0;
    loop {
        let done = db
            .evidence_group(&c.id, |db| {
                for _ in 0..4 {
                    let page = db.raw_page(&c.id, cursor).unwrap();
                    if page.is_empty() {
                        return Ok(true);
                    }
                    let last = page.last().unwrap();
                    cursor = (last.timestamp_ticks, last.sequence);
                    rows += page.len();
                    let mut batch = Capture {
                        id: c.id.clone(),
                        events: page,
                        ..Default::default()
                    };
                    crate::reliability::finish(&mut batch);
                    db.append_evidence(&batch).unwrap();
                }
                Ok(false)
            })
            .unwrap();
        if done {
            break;
        }
    }
    assert_eq!(rows, 16384);
    db.mark_finished(&c).unwrap();
    let post_ns = post_start.elapsed().as_nanos() as u64;
    drop(db);
    let report = serde_json::json!({"synthetic_only":true,"records":rows,"stream":stats,
        "write_ns":write_ns,"post_ns":post_ns,"end_to_end_ns":started.elapsed().as_nanos() as u64,
        "wal_sampled_peak_bytes":wal_peak,"db_after_close_bytes":std::fs::metadata(&path).unwrap().len()});
    println!("REPLAY: {report}");
    crate::profile::finish().unwrap();
    for suffix in ["", "-wal", "-shm"] {
        let file = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
        if file.exists() {
            std::fs::remove_file(file).unwrap();
        }
    }
}
