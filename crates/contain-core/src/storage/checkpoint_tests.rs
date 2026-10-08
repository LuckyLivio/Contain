//! Disk-backed checkpoint tests. Abrupt exits establish process-crash recovery,
//! not durability after a power failure or an operating-system crash.
use super::{
    Storage, checkpoint,
    stream::{DEFAULT_QUOTA, RawBuffer},
};
use crate::model::{Capture, Confidence, QualityLevel, SystemEvent};
use rusqlite::{Connection, OpenFlags};
use std::{
    path::{Path, PathBuf},
    process::{Command, Stdio},
};

struct OwnedDatabase(PathBuf);
impl OwnedDatabase {
    fn new() -> Self {
        Self(std::env::temp_dir().join(format!(
            "contain-checkpoint-test-{}.db",
            uuid::Uuid::new_v4()
        )))
    }
    fn path(&self) -> &Path {
        &self.0
    }
}
impl Drop for OwnedDatabase {
    fn drop(&mut self) {
        // Each path is derived from this test's unique database, with no recursion.
        for suffix in ["", "-wal", "-shm"] {
            let path = PathBuf::from(format!("{}{suffix}", self.0.display()));
            if let Err(error) = std::fs::remove_file(&path)
                && error.kind() != std::io::ErrorKind::NotFound
                && !std::thread::panicking()
            {
                panic!("removing test database {}: {error}", path.display());
            }
        }
    }
}

fn capture() -> Capture {
    Capture {
        id: "checkpoint".into(),
        name: "checkpoint".into(),
        schema_version: 3,
        ..Default::default()
    }
}
fn event(sequence: u64) -> SystemEvent {
    SystemEvent {
        id: format!("checkpoint-{sequence}"),
        sequence,
        timestamp_ticks: sequence,
        event_type: "file".into(),
        operation: "write_requested".into(),
        resource: r"C:\fixture\checkpoint.txt".into(),
        // Read APIs must suppress provisional confidence until finalization.
        confidence: Confidence::High,
        success: Some(true),
        ..Default::default()
    }
}
fn policy(variant: &'static str) -> checkpoint::Policy {
    checkpoint::Policy {
        variant: Some(variant),
        budget: 512 * 1024 * 1024,
    }
}
fn pragma(db: &Storage, name: &str) -> u32 {
    db.connection
        .pragma_query_value(None, name, |row| row.get(0))
        .unwrap()
}
fn raw_count(connection: &Connection) -> u64 {
    connection
        .query_row(
            "SELECT (SELECT COUNT(*) FROM raw_ingest) + (SELECT COUNT(*) FROM raw_stream)",
            [],
            |row| row.get(0),
        )
        .unwrap()
}
fn assert_unfinished(db: &Storage, rows: usize, state: &str) -> Capture {
    let loaded = db.load("checkpoint").unwrap();
    assert_eq!(loaded.capture_state.as_deref(), Some(state));
    assert!(matches!(loaded.quality.level, QualityLevel::Incomplete));
    assert_eq!(loaded.events.len(), rows);
    assert!(
        loaded
            .events
            .iter()
            .all(|event| event.confidence == Confidence::Unknown && event.success.is_none())
    );
    assert_eq!(raw_count(&db.connection), rows as u64);
    loaded
}

#[test]
fn e0_and_e1_keep_live_commits_and_restore_before_full_finalization() {
    for variant in ["E0", "E1"] {
        let file = OwnedDatabase::new();
        let mut db = Storage::open(file.path()).unwrap();
        let mut c = capture();
        let p = policy(variant);
        let stats = checkpoint::scoped(&mut db, p, |db| {
            let mut writer = RawBuffer::with_policy(db, &c, DEFAULT_QUOTA, p)?;
            let expected_pages = if variant == "E1" { 0 } else { 4096 };
            assert_eq!(pragma(db, "wal_autocheckpoint"), expected_pages);
            assert_eq!(writer.stats.wal_autocheckpoint_pages, Some(expected_pages));
            assert_eq!(pragma(db, "synchronous"), 1);
            writer.append(db, (1..=257).map(event).collect());
            assert_eq!(writer.stats.persisted, 256);
            // Opening a second connection while the producer is active establishes
            // that commit visibility has not been deferred with checkpointing.
            let live_reader =
                Connection::open_with_flags(file.path(), OpenFlags::SQLITE_OPEN_READ_ONLY)?;
            assert_eq!(raw_count(&live_reader), 256);
            let state: String =
                live_reader.query_row("SELECT state FROM capture_runs", [], |row| row.get(0))?;
            assert_eq!(state, "capturing");
            drop(live_reader);
            let stats = writer.finish(db)?;
            assert_eq!(pragma(db, "wal_autocheckpoint"), 4096);
            assert_eq!(pragma(db, "synchronous"), 1);
            assert_eq!(stats.accepted, 257);
            assert_eq!(stats.persisted, 257);
            assert_eq!(stats.failed, 0);
            assert!(stats.error.is_none(), "{:?}", stats.error);
            assert!(stats.wal_samples >= 5);
            assert!(stats.wal_peak_bytes >= stats.wal_initial_bytes);
            if variant == "E1" {
                let report = stats.checkpoint.as_ref().unwrap();
                assert_eq!(report.mode, "PASSIVE");
                assert_eq!(report.busy, Some(0));
                assert!(report.log_frames.unwrap() > 0);
                assert_eq!(report.log_frames, report.checkpointed_frames);
                assert!(report.error.is_none());
                assert!(stats.checkpoint_completed);
                assert_eq!(stats.restored_wal_autocheckpoint_pages, Some(4096));
            } else {
                assert!(stats.checkpoint.is_none());
            }
            assert_unfinished(db, 257, "finalizing");
            Ok(stats)
        })
        .unwrap();
        assert_eq!(pragma(&db, "wal_autocheckpoint"), 4096);
        c.backend.stream = Some(stats);
        c.backend.etw_events_lost = Some(0);
        c.backend.etw_buffers_lost = Some(0);
        db.prepare_raw(&c.id).unwrap();
        db.save(&c).unwrap();
        db.mark_finished(&c).unwrap();
        assert_eq!(pragma(&db, "synchronous"), 2);
        assert_eq!(db.stream_state(&c.id).unwrap().as_deref(), Some("finished"));
        assert_eq!(raw_count(&db.connection), 257);
        drop(db);
        let reopened = Storage::open(file.path()).unwrap();
        assert_eq!(
            reopened.stream_state(&c.id).unwrap().as_deref(),
            Some("finished")
        );
        assert_eq!(raw_count(&reopened.connection), 257);
        assert_eq!(
            reopened
                .load_summary(&c.id)
                .unwrap()
                .backend
                .stream
                .unwrap()
                .persisted,
            257
        );
    }
}

#[test]
fn pinned_reader_exposes_partial_passive_checkpoint_without_fabricating_lost_rows() {
    let file = OwnedDatabase::new();
    let mut db = Storage::open(file.path()).unwrap();
    let c = capture();
    let p = policy("E1");
    checkpoint::scoped(&mut db, p, |db| {
        let mut writer = RawBuffer::with_policy(db, &c, DEFAULT_QUOTA, p)?;
        writer.append(db, (1..=256).map(event).collect());
        let reader = Connection::open_with_flags(file.path(), OpenFlags::SQLITE_OPEN_READ_ONLY)?;
        reader.execute_batch("BEGIN")?;
        assert_eq!(raw_count(&reader), 256); // Establish a WAL snapshot end mark.
        writer.append(db, vec![event(257)]);
        let stats = writer.finish(db)?;
        let report = stats.checkpoint.as_ref().unwrap();
        assert_eq!(report.busy, Some(0));
        assert!(report.log_frames.unwrap() > report.checkpointed_frames.unwrap());
        assert!(!stats.checkpoint_completed);
        assert!(report.error.as_deref().unwrap().contains("incomplete"));
        assert!(stats.error.as_deref().unwrap().contains("incomplete"));
        assert_eq!(stats.accepted, 257);
        assert_eq!(stats.persisted, 257);
        assert_eq!(stats.failed, 0);
        assert_eq!(stats.quota_dropped, 0);
        assert_eq!(stats.restored_wal_autocheckpoint_pages, Some(4096));
        assert_eq!(pragma(db, "wal_autocheckpoint"), 4096);
        assert_eq!(raw_count(&reader), 256); // Reader still owns the earlier snapshot.
        let loaded = assert_unfinished(db, 257, "failed");
        assert!(loaded.backend.has_loss());
        assert!(!loaded.backend.source_intact());
        assert_eq!(loaded.backend.dropped_events, 0);
        reader.execute_batch("ROLLBACK")?;
        Ok(())
    })
    .unwrap();
    drop(db);
    let reopened = Storage::open(file.path()).unwrap();
    assert!(
        assert_unfinished(&reopened, 257, "failed")
            .backend
            .has_loss()
    );
}

#[test]
fn sampled_wal_threshold_keeps_crossing_commit_and_stops_future_raw_writes() {
    let file = OwnedDatabase::new();
    let mut db = Storage::open(file.path()).unwrap();
    let c = capture();
    let p = policy("E1");
    checkpoint::scoped(&mut db, p, |db| {
        let mut writer = RawBuffer::with_policy(db, &c, DEFAULT_QUOTA, p)?;
        let initial = writer.stats.wal_initial_bytes.unwrap();
        // Set a deterministic low threshold relative to the schema's actual WAL
        // overhead; this exercises the production sampler, not a mocked filesize.
        let budget = initial + 4096;
        writer.stats.wal_budget_bytes = Some(budget);
        writer.append(db, (1..=257).map(event).collect());
        assert_eq!(writer.stats.persisted, 256);
        assert_eq!(writer.stats.failed, 1);
        assert!(writer.stats.wal_budget_exceeded);
        let peak = writer.stats.wal_peak_bytes.unwrap();
        assert!(peak > budget);
        assert_eq!(writer.stats.wal_budget_overshoot_bytes, Some(peak - budget));
        assert_eq!(writer.stats.wal_growth_bytes, Some(peak - initial));
        assert!(
            writer
                .stats
                .error
                .as_deref()
                .unwrap()
                .contains("stop threshold")
        );
        writer.append(db, (258..=300).map(event).collect());
        let stats = writer.finish(db)?;
        assert_eq!(stats.accepted, 300);
        assert_eq!(stats.persisted, 256);
        assert_eq!(stats.failed, 44);
        assert_eq!(stats.quota_dropped, 0);
        assert_eq!(
            stats.accepted,
            stats.persisted + stats.failed + stats.quota_dropped
        );
        assert!(stats.checkpoint_completed);
        assert_eq!(pragma(db, "wal_autocheckpoint"), 4096);
        assert!(assert_unfinished(db, 256, "failed").backend.has_loss());
        Ok(())
    })
    .unwrap();
    drop(db);
    let reopened = Storage::open(file.path()).unwrap();
    assert_unfinished(&reopened, 256, "failed");
}

#[test]
fn deferred_checkpoint_abrupt_exit_child() {
    let Some(path) = std::env::var_os("CONTAIN_CHECKPOINT_CRASH_PATH") else {
        return;
    };
    let phase = std::env::var("CONTAIN_CHECKPOINT_CRASH_PHASE").unwrap();
    let mut db = Storage::open(Path::new(&path)).unwrap();
    let mut c = capture();
    let p = policy("E1");
    checkpoint::scoped(&mut db, p, |db| -> anyhow::Result<()> {
        let mut writer = RawBuffer::with_policy(db, &c, DEFAULT_QUOTA, p)?;
        writer.append(db, (1..=257).map(event).collect());
        assert_eq!(writer.stats.persisted, 256);
        if phase == "before_tail_flush" {
            std::process::exit(73);
        }
        writer.flush(db);
        assert_eq!(writer.stats.persisted, 257);
        assert_eq!(pragma(db, "wal_autocheckpoint"), 0);
        if phase == "before_checkpoint" {
            std::process::exit(73);
        }
        let stats = writer.finish(db)?;
        assert!(stats.checkpoint_completed);
        assert!(stats.error.is_none());
        assert_eq!(pragma(db, "wal_autocheckpoint"), 4096);
        if phase == "after_checkpoint" {
            std::process::exit(73);
        }
        assert_eq!(phase, "after_final_commit");
        c.backend.stream = Some(stats);
        c.backend.etw_events_lost = Some(0);
        c.backend.etw_buffers_lost = Some(0);
        db.prepare_raw(&c.id)?;
        db.save(&c)?;
        db.mark_finished(&c)?;
        assert_eq!(pragma(db, "synchronous"), 2);
        assert_eq!(db.stream_state(&c.id)?.as_deref(), Some("finished"));
        // Exit bypasses both the restoration guard and SQLite connection close.
        std::process::exit(73);
    })
    .unwrap();
}

#[test]
fn deferred_commits_survive_abrupt_exit_at_each_completion_boundary() {
    for (phase, rows, state) in [
        ("before_tail_flush", 256, "capturing"),
        ("before_checkpoint", 257, "capturing"),
        ("after_checkpoint", 257, "finalizing"),
        ("after_final_commit", 257, "finished"),
    ] {
        let file = OwnedDatabase::new();
        let output = Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "storage::checkpoint_tests::deferred_checkpoint_abrupt_exit_child",
                "--nocapture",
            ])
            .env("CONTAIN_CHECKPOINT_CRASH_PATH", file.path())
            .env("CONTAIN_CHECKPOINT_CRASH_PHASE", phase)
            .stdin(Stdio::null())
            .output()
            .unwrap();
        assert_eq!(
            output.status.code(),
            Some(73),
            "phase={phase}; stdout={}; stderr={}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        let db = Storage::open(file.path()).unwrap();
        assert_eq!(raw_count(&db.connection), rows as u64, "phase={phase}");
        assert_eq!(
            db.stream_state("checkpoint").unwrap().as_deref(),
            Some(state)
        );
        let loaded = if state == "finished" {
            db.load_summary("checkpoint").unwrap()
        } else {
            assert_unfinished(&db, rows, state)
        };
        let stats = loaded.backend.stream.unwrap();
        assert_eq!(stats.persisted, rows as u64);
        assert_eq!(stats.sqlite_synchronous, Some(1)); // Capture-time policy.
        assert_eq!(stats.wal_autocheckpoint_pages, Some(0));
        assert_eq!(stats.checkpoint_variant.as_deref(), Some("E1"));
        assert_eq!(
            stats.checkpoint_completed,
            matches!(phase, "after_checkpoint" | "after_final_commit")
        );
        if stats.checkpoint_completed {
            assert_eq!(stats.restored_wal_autocheckpoint_pages, Some(4096));
        }
    }
}
