//! Exercises the actual detached receiver and SQLite writer without claiming
//! synthetic records provide live ETW continuity or attribution evidence.
use super::*;
use crate::storage::stream::DEFAULT_QUOTA;
use std::path::{Path, PathBuf};

fn event(sequence: u64) -> SystemEvent {
    SystemEvent {
        id: format!("ingest-{sequence}"),
        sequence,
        timestamp_ticks: sequence,
        event_type: "file".into(),
        resource: format!("fixture-{sequence}"),
        confidence: Confidence::High,
        ..Default::default()
    }
}

fn fixture() -> (PathBuf, Storage, Capture, RawBuffer) {
    let path = std::env::temp_dir().join(format!("contain-ingest-{}.db", uuid::Uuid::new_v4()));
    let mut db = Storage::open(&path).unwrap();
    let capture = Capture {
        id: "ingest".into(),
        name: "ingest".into(),
        schema_version: 3,
        ..Default::default()
    };
    let journal = RawBuffer::new(&mut db, &capture, DEFAULT_QUOTA).unwrap();
    (path, db, capture, journal)
}

fn remove_database(path: &Path) {
    for suffix in ["", "-wal", "-shm"] {
        match std::fs::remove_file(format!("{}{suffix}", path.display())) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => panic!("test database cleanup failed: {error}"),
        }
    }
}

fn assert_raw_reopen(path: &Path, expected: &[u64], state: &str) {
    let db = Storage::open(path).unwrap();
    let capture = db.load("ingest").unwrap();
    assert_eq!(capture.capture_state.as_deref(), Some(state));
    assert_eq!(
        capture
            .events
            .iter()
            .map(|event| event.sequence)
            .collect::<Vec<_>>(),
        expected
    );
    assert!(
        capture
            .events
            .iter()
            .all(|event| event.confidence == Confidence::Unknown && !event.state_validated)
    );
}

#[test]
fn immediate_stop_drains_receiver_handoff_sqlite_and_partial_tail() {
    let count = PAGE as u64 * 3 + 17;
    let (path, mut db, _, journal) = fixture();
    let mut source = EtwSource::synthetic((1..=count).map(event).collect());
    let (_, backend, stream) = record_while(
        &mut source,
        &mut db,
        journal,
        LifetimeCache::default(),
        |_| Ok(()),
    )
    .unwrap();
    assert_eq!(stream.accepted, count);
    assert_eq!(stream.persisted, count);
    assert_eq!(stream.failed + stream.quota_dropped, 0);
    assert!(stream.error.is_none());
    let handoff = backend.handoff.unwrap();
    assert_eq!(handoff.initial_accepted, 0);
    assert_eq!(handoff.received, count);
    assert_eq!(handoff.submitted, count);
    assert_eq!(handoff.drained, count);
    assert_eq!(handoff.dropped() + handoff.pending, 0);
    assert!(handoff.receiver_finished && handoff.writer_finished);
    assert!(handoff.high_water_records <= handoff.record_capacity);
    assert!(handoff.high_water_bytes <= handoff.byte_capacity);
    assert_eq!(backend.events_received, count);
    drop(db);
    assert_raw_reopen(&path, &(1..=count).collect::<Vec<_>>(), "finalizing");
    remove_database(&path);
}

#[test]
fn idle_writer_commits_a_partial_batch_before_stop() {
    let (path, mut db, _, journal) = fixture();
    let mut source = EtwSource::synthetic(vec![event(1)]);
    let observer = rusqlite::Connection::open(&path).unwrap();
    let (_, _, stream) = record_while(
        &mut source,
        &mut db,
        journal,
        LifetimeCache::default(),
        |_| {
            let deadline = Instant::now() + Duration::from_secs(5);
            loop {
                let committed: u64 = observer.query_row(
                    "SELECT COUNT(*) FROM raw_ingest WHERE session_id='ingest'",
                    [],
                    |row| row.get(0),
                )?;
                if committed == 1 {
                    return Ok(());
                }
                anyhow::ensure!(
                    Instant::now() < deadline,
                    "partial raw batch was not committed while capture remained active"
                );
                thread::sleep(Duration::from_millis(5));
            }
        },
    )
    .unwrap();
    assert_eq!(stream.persisted, 1);
    drop(observer);
    drop(db);
    assert_raw_reopen(&path, &[1], "finalizing");
    remove_database(&path);
}

#[test]
fn preinitialization_journal_records_are_separate_from_handoff_conservation() {
    let count = PAGE as u64 + 3;
    let (path, mut db, _, mut journal) = fixture();
    // Identity initialization and callback records use disjoint sequence spaces.
    journal.append(&mut db, vec![event(10_000), event(10_001)]);
    let mut source = EtwSource::synthetic((1..=count).map(event).collect());
    let (_, backend, stream) = record_while(
        &mut source,
        &mut db,
        journal,
        LifetimeCache::default(),
        |_| Ok(()),
    )
    .unwrap();
    let handoff = backend.handoff.unwrap();
    assert_eq!(handoff.initial_accepted, 2);
    assert_eq!(handoff.received, count);
    assert_eq!(handoff.received, handoff.submitted + handoff.dropped());
    assert_eq!(handoff.submitted, handoff.drained + handoff.pending);
    assert_eq!(stream.accepted, handoff.initial_accepted + handoff.drained);
    assert_eq!(stream.persisted, count + 2);
    drop(db);
    let mut expected = (1..=count).collect::<Vec<_>>();
    expected.extend([10_000, 10_001]);
    assert_raw_reopen(&path, &expected, "finalizing");
    remove_database(&path);
}

fn lifecycle(
    sequence: u64,
    operation: &str,
    pid: u32,
    birth: u64,
    tid: Option<u32>,
    at: u64,
) -> SystemEvent {
    SystemEvent {
        id: format!("lifecycle-{sequence}"),
        sequence,
        timestamp_ticks: at,
        event_type: "lifecycle".into(),
        operation: operation.into(),
        resource: if pid == 10 {
            "unrelated.exe"
        } else {
            "child.exe"
        }
        .into(),
        evidence: AttributionEvidence {
            source: EvidenceSource::EtwProcess,
            pid: Some(pid),
            process_creation_time: (operation == "process_start").then_some(birth),
            parent_pid: Some(if pid == 10 { 99 } else { 1 }),
            ..Default::default()
        },
        raw: RawEvidence {
            thread_id: tid,
            ..Default::default()
        },
        ..Default::default()
    }
}

#[test]
fn follow_receiver_uses_moved_startup_lifecycle_without_rejournal_or_false_actor() {
    let (path, mut db, _, mut journal) = fixture();
    let startup = vec![
        lifecycle(10_000, "process_start", 10, 1100, None, 1100),
        lifecycle(10_001, "thread_start", 10, 1100, Some(7), 1200),
    ];
    let mut initial_cache = LifetimeCache::default();
    for event in &startup {
        initial_cache.ingest(event);
    }
    journal.append(&mut db, startup);
    let mut incoming: Vec<_> = (1..=32)
        .map(|sequence| {
            let mut event = event(sequence);
            event.timestamp_ticks = 6000;
            event.operation = "write_requested".into();
            event.evidence = AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            };
            event.raw.thread_id = Some(7);
            event
        })
        .collect();
    incoming.extend([
        lifecycle(33, "thread_start", 1, 5000, Some(8), 5100),
        lifecycle(34, "process_start", 2, 5200, None, 5200),
        lifecycle(35, "thread_start", 2, 5200, Some(9), 5300),
    ]);
    for (request_sequence, tid) in [(36, 8), (38, 9)] {
        let request_id = format!("target-{request_sequence}");
        incoming.push(SystemEvent {
            id: request_id.clone(),
            sequence: request_sequence,
            timestamp_ticks: 6000 + request_sequence,
            event_type: "file".into(),
            operation: "write_requested".into(),
            resource: format!("target-{request_sequence}.txt"),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(tid),
                irp: Some(request_id.clone()),
                ..Default::default()
            },
            ..Default::default()
        });
        incoming.push(SystemEvent {
            id: format!("end-{request_sequence}"),
            sequence: request_sequence + 1,
            timestamp_ticks: 6001 + request_sequence,
            event_type: "completion".into(),
            operation: "operation_end".into(),
            success: Some(true),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                related_event: Some(request_id),
                status: Some(0),
                ..Default::default()
            },
            ..Default::default()
        });
    }
    let mut source = EtwSource::synthetic(incoming);
    let (_, backend, stream) =
        record_while_follow(&mut source, &mut db, journal, initial_cache, |live| {
            live.seed_follow_initial(
                &[(
                    ProcessRecord {
                        pid: 10,
                        creation_time: Some(1100),
                        image: "unrelated.exe".into(),
                        evidence: AttributionEvidence {
                            source: EvidenceSource::ProcessApi,
                            ..Default::default()
                        },
                        ..Default::default()
                    },
                    2000,
                )],
                &[],
            );
            live.bind_follow_root(
                ProcessRecord {
                    pid: 1,
                    creation_time: Some(5000),
                    image: "installer.exe".into(),
                    confidence: Confidence::Certain,
                    evidence: AttributionEvidence {
                        source: EvidenceSource::ProcessApi,
                        rule: AttributionRule::InstallerPid,
                        ..Default::default()
                    },
                    ..Default::default()
                },
                "ingest",
            );
            Ok(())
        })
        .unwrap();
    let follow = backend.follow_installer.unwrap();
    assert_eq!(follow.excluded_other, 32);
    assert_eq!(follow.admitted, 4);
    assert_eq!(follow.candidate_dropped, 0);
    assert_eq!(stream.accepted, 9);
    assert_eq!(stream.persisted, 9);
    assert_eq!(stream.failed + stream.quota_dropped, 0);
    assert_eq!(backend.handoff.unwrap().initial_accepted, 2);
    drop(db);
    assert_raw_reopen(
        &path,
        &[10_000, 10_001, 33, 34, 35, 36, 37, 38, 39],
        "finalizing",
    );
    remove_database(&path);
}

#[test]
fn follow_startup_cache_eviction_remains_context_loss_after_ownership_handoff() {
    let (path, mut db, _, journal) = fixture();
    let mut source = EtwSource::synthetic(Vec::new());
    // Inject an already-recorded bounded-cache eviction at the ownership edge;
    // the cache's capacity behavior is covered by the lifetime regressions.
    let mut initial_cache = LifetimeCache::default();
    initial_cache.dropped = 1;
    let (_, backend, stream) =
        record_while_follow(&mut source, &mut db, journal, initial_cache, |_| Ok(())).unwrap();
    assert_eq!(backend.context_losses, 1);
    assert_eq!(backend.dropped_events, 0);
    assert!(backend.has_loss());
    assert_eq!(stream.failed + stream.quota_dropped, 0);
    drop(db);
    remove_database(&path);
}

#[test]
fn sqlite_insert_failure_accounts_for_every_drained_record_and_retains_prior_commit() {
    let count = PAGE as u64 * 2 + 7;
    let (path, mut db, _, mut journal) = fixture();
    journal.append(&mut db, vec![event(10_000)]);
    journal.flush(&mut db);
    let control = rusqlite::Connection::open(&path).unwrap();
    control.execute_batch("CREATE TRIGGER reject_ingest BEFORE INSERT ON raw_ingest WHEN NEW.sequence < 10000 BEGIN SELECT RAISE(ABORT,'injected writer failure'); END;").unwrap();
    drop(control);
    let mut source = EtwSource::synthetic((1..=count).map(event).collect());
    let (_, backend, stream) = record_while(
        &mut source,
        &mut db,
        journal,
        LifetimeCache::default(),
        |_| Ok(()),
    )
    .unwrap();
    assert_eq!(stream.accepted, count + 1);
    assert_eq!(stream.persisted, 1);
    assert_eq!(stream.failed, count);
    assert_eq!(stream.quota_dropped, 0);
    assert!(
        stream
            .error
            .as_deref()
            .unwrap()
            .contains("injected writer failure")
    );
    let handoff = backend.handoff.unwrap();
    assert_eq!(handoff.received, count);
    assert_eq!(handoff.drained, count);
    assert_eq!(handoff.pending + handoff.dropped(), 0);
    drop(db);
    assert_raw_reopen(&path, &[10_000], "failed");
    remove_database(&path);
}

#[test]
fn sqlite_busy_writer_joins_and_reopens_last_commit_as_unfinished() {
    let (path, mut db, _, mut journal) = fixture();
    journal.append(&mut db, vec![event(10_000)]);
    journal.flush(&mut db);
    let locked = rusqlite::Connection::open(&path).unwrap();
    locked.execute_batch("BEGIN IMMEDIATE").unwrap();
    let mut source = EtwSource::synthetic((1..=PAGE as u64 + 3).map(event).collect());
    // The held SQLite write lock deterministically exercises the real busy
    // timeout. No scheduler-dependent timing threshold is used as an assertion.
    let error = record_while(
        &mut source,
        &mut db,
        journal,
        LifetimeCache::default(),
        |_| Ok(()),
    )
    .unwrap_err();
    assert!(format!("{error:#}").contains("locked"));
    locked.execute_batch("ROLLBACK").unwrap();
    drop(locked);
    drop(db);
    assert_raw_reopen(&path, &[10_000], "capturing");
    remove_database(&path);
}

#[test]
fn operation_error_and_unwind_both_stop_and_drain_before_returning_control() {
    for panic in [false, true] {
        let count = PAGE as u64 + 1;
        let (path, mut db, _, journal) = fixture();
        let mut source = EtwSource::synthetic((1..=count).map(event).collect());
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            record_while::<()>(
                &mut source,
                &mut db,
                journal,
                LifetimeCache::default(),
                |_| {
                    if panic {
                        panic!("injected operation panic");
                    }
                    Err(anyhow!("injected operation error"))
                },
            )
        }));
        if panic {
            assert!(result.is_err());
        } else {
            assert!(
                result
                    .unwrap()
                    .unwrap_err()
                    .to_string()
                    .contains("injected operation error")
            );
        }
        drop(db);
        assert_raw_reopen(&path, &(1..=count).collect::<Vec<_>>(), "finalizing");
        remove_database(&path);
    }
}
