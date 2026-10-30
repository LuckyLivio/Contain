use super::*;
use crate::model::{AttributionEvidence, EvidenceSource, SystemEvent};

struct TestFile(std::path::PathBuf);
impl TestFile {
    fn new() -> Self {
        Self(std::env::temp_dir().join(format!(
            "contain 只读 & [records] ${}.db",
            uuid::Uuid::new_v4()
        )))
    }
}
impl Drop for TestFile {
    fn drop(&mut self) {
        for suffix in ["", "-wal", "-shm"] {
            let _ = fs::remove_file(format!("{}{suffix}", self.0.display()));
        }
    }
}
fn sample() -> Capture {
    Capture {
        id: "sample-session".into(),
        name: "synthetic read-only sample".into(),
        schema_version: 3,
        started_at: "2026-01-02T03:04:05.120+00:00".into(),
        finished_at: "2026-01-02T03:04:06.123400+00:00".into(),
        events: (0..5)
            .map(|index| SystemEvent {
                id: format!("event-{index}"),
                timestamp: "2026-01-02T03:04:05.120+00:00".into(),
                timestamp_ticks: 134_350_000_000_000_017,
                sequence: index,
                operation: "write_requested".into(),
                resource: "C:\\synthetic\\历史.txt".into(),
                evidence: AttributionEvidence {
                    source: EvidenceSource::EtwFile,
                    pid: Some(42),
                    process_creation_time: Some(134_350_000_000_000_013),
                    ..Default::default()
                },
                ..Default::default()
            })
            .collect(),
        ..Default::default()
    }
}

#[test]
fn read_only_missing_and_non_database_inputs_never_create_a_database() {
    let missing = TestFile::new();
    assert!(Storage::open_read_only(&missing.0).is_err());
    assert!(!missing.0.exists());
    assert!(Storage::open_read_only(&std::env::temp_dir()).is_err());
    fs::write(&missing.0, b"not SQLite: damaged synthetic input").unwrap();
    let original = fs::read(&missing.0).unwrap();
    assert!(Storage::open_read_only(&missing.0).is_err());
    assert_eq!(fs::read(&missing.0).unwrap(), original);
}

#[test]
fn read_only_rejects_unrecognized_and_future_database_without_mutation() {
    for future in [false, true] {
        let file = TestFile::new();
        {
            let db = Connection::open(&file.0).unwrap();
            db.execute_batch("CREATE TABLE unrelated(value TEXT);")
                .unwrap();
            if future {
                db.pragma_update(None, "user_version", 99).unwrap();
            }
        }
        let before = fs::read(&file.0).unwrap();
        let error = Storage::open_read_only(&file.0).err().unwrap().to_string();
        assert!(error.contains(if future { "newer" } else { "Not a supported" }));
        assert_eq!(fs::read(&file.0).unwrap(), before);
    }
}

#[test]
fn read_only_empty_contain_database_stays_empty_and_cannot_be_written() {
    let file = TestFile::new();
    drop(Storage::open(&file.0).unwrap());
    let before = fs::read(&file.0).unwrap();
    let mut db = Storage::open_read_only(&file.0).unwrap();
    assert!(db.list().unwrap().is_empty());
    assert!(db.list_details().unwrap().is_empty());
    assert!(db.save(&sample()).is_err());
    drop(db);
    assert_eq!(fs::read(&file.0).unwrap(), before);
}

#[test]
fn read_only_current_database_keeps_schema_and_exact_query_evidence() {
    let file = TestFile::new();
    let capture = sample();
    {
        let mut db = Storage::open(&file.0).unwrap();
        db.save(&capture).unwrap();
        db.connection.execute_batch("DROP INDEX system_events_session_resource; DROP INDEX normalized_operations_session_id;").unwrap();
    }
    let before = fs::read(&file.0).unwrap();
    let db = Storage::open_read_only(&file.0).unwrap();
    let details = db.list_details().unwrap();
    assert_eq!(details.len(), 1);
    assert_eq!(details[0].started_at, capture.started_at);
    assert_eq!(details[0].finished_at, capture.finished_at);
    assert!(details[0].capture_state.is_none());
    let summary = db.load_summary(&capture.id).unwrap();
    assert!(summary.events.is_empty());
    assert!(summary.operations.is_empty());
    assert!(summary.edges.is_empty());
    let page = db.events_page_compatible(&capture.id, 2, 2).unwrap();
    assert_eq!(
        page.iter().map(|e| e.id.as_str()).collect::<Vec<_>>(),
        ["event-2", "event-3"]
    );
    let explained = db.for_event(&page[0].id).unwrap();
    assert_eq!(
        serde_json::to_value(&explained.events[0]).unwrap(),
        serde_json::to_value(&page[0]).unwrap()
    );
    assert_eq!(
        serde_json::to_value(&page[0]).unwrap()["timestamp_ticks"],
        "134350000000000017"
    );
    assert_eq!(page[0].timestamp, capture.events[0].timestamp);
    assert_eq!(
        db.connection
            .query_row::<u32, _, _>(
                "SELECT COUNT(*) FROM sqlite_master WHERE name='system_events_session_resource'",
                [],
                |r| r.get(0)
            )
            .unwrap(),
        0
    );
    drop(db);
    assert_eq!(fs::read(&file.0).unwrap(), before);
}

#[test]
fn read_only_v1_migrates_only_the_in_memory_copy() {
    let file = TestFile::new();
    {
        let db = Connection::open(&file.0).unwrap();
        db.execute_batch(include_str!("v1.sql")).unwrap();
        db.execute_batch("INSERT INTO applications VALUES ('legacy','Synthetic legacy','synthetic.exe'); INSERT INTO installation_sessions VALUES ('legacy','legacy','start','end',0,'[]',NULL,'[]');
            INSERT INTO system_events VALUES (1,'legacy','file','synthetic.txt','created','end',NULL,'Unknown','snapshot');
            INSERT INTO file_changes VALUES (1,NULL,'original-hash',7,1);").unwrap();
    }
    let before = fs::read(&file.0).unwrap();
    let db = Storage::open_read_only(&file.0).unwrap();
    let capture = db.load("legacy").unwrap();
    assert_eq!(capture.schema_version, 1);
    assert_eq!(
        capture.files[0].after_hash.as_deref(),
        Some("original-hash")
    );
    assert_eq!(capture.files[0].confidence, Confidence::Unknown);
    assert!(capture.warnings.iter().any(|w| w.contains("Legacy")));
    assert!(
        db.events_page_compatible("legacy", 0, 1)
            .unwrap()
            .is_empty()
    );
    assert_eq!(migration::check_version(&db.connection).unwrap(), 4);
    drop(db);
    assert_eq!(fs::read(&file.0).unwrap(), before);
    let original = Connection::open_with_flags(&file.0, OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap();
    assert_eq!(migration::check_version(&original).unwrap(), 0);
}

#[test]
fn read_only_v2_v3_history_uses_existing_mapping_without_changing_original() {
    for version in [2, 3] {
        let file = TestFile::new();
        let capture = sample();
        {
            let mut db = Storage::open(&file.0).unwrap();
            db.save(&capture).unwrap();
            db.connection
                .execute_batch("DELETE FROM event_documents; DROP TABLE raw_ingest;")
                .unwrap();
            if version == 2 {
                db.connection.execute_batch("DROP TABLE reliability_metadata; DROP TABLE observation_details; DROP TABLE normalized_operations; DROP TABLE evidence_edges;").unwrap();
            }
            db.connection
                .pragma_update(None, "user_version", version)
                .unwrap();
        }
        let before = fs::read(&file.0).unwrap();
        let db = Storage::open_read_only(&file.0).unwrap();
        assert!(db.load_summary(&capture.id).unwrap().events.is_empty());
        let full = db.load(&capture.id).unwrap();
        let page = db.events_page_compatible(&capture.id, 2, 2).unwrap();
        assert_eq!(
            serde_json::to_value(&page).unwrap(),
            serde_json::to_value(&full.events[2..4]).unwrap()
        );
        assert!(
            db.events_page_compatible(&capture.id, u64::MAX, 2)
                .unwrap()
                .is_empty()
        );
        assert!(db.events_page_compatible(&capture.id, 0, 0).is_err());
        assert!(db.events_page_compatible(&capture.id, 0, 4097).is_err());
        let explanation = db.for_event(&page[0].id).unwrap();
        let explained = explanation
            .events
            .iter()
            .find(|e| e.id == page[0].id)
            .unwrap();
        assert_eq!(
            serde_json::to_value(explained).unwrap(),
            serde_json::to_value(&page[0]).unwrap()
        );
        drop(db);
        assert_eq!(fs::read(&file.0).unwrap(), before);
        let original =
            Connection::open_with_flags(&file.0, OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap();
        assert_eq!(migration::check_version(&original).unwrap(), version);
    }
}

#[test]
fn list_details_preserves_unfinished_state_without_loading_bad_event_documents() {
    let file = TestFile::new();
    let capture = sample();
    {
        let mut db = Storage::open(&file.0).unwrap();
        db.save(&capture).unwrap();
        db.connection
            .execute(
                "INSERT INTO capture_runs VALUES (?1,'capturing',?2,NULL)",
                params![
                    capture.id,
                    serde_json::to_string(&stream::StreamStats::default()).unwrap()
                ],
            )
            .unwrap();
        db.connection
            .execute_batch("UPDATE event_documents SET document='not valid JSON'")
            .unwrap();
    }
    let db = Storage::open_read_only(&file.0).unwrap();
    let list = db.list_details().unwrap();
    assert_eq!(list[0].capture_state.as_deref(), Some("capturing"));
    let summary = db.load_summary(&capture.id).unwrap();
    assert!(matches!(
        summary.quality.level,
        crate::model::QualityLevel::Incomplete
    ));
    assert!(summary.events.is_empty());
}

#[test]
fn read_only_query_keeps_one_snapshot_while_another_writer_commits() {
    let file = TestFile::new();
    let mut writer = Storage::open(&file.0).unwrap();
    writer.save(&sample()).unwrap();
    let reader = Storage::open_read_only(&file.0).unwrap();
    assert_eq!(reader.list_details().unwrap().len(), 1);
    let second = Capture {
        id: "second".into(),
        name: "second synthetic".into(),
        schema_version: 3,
        ..Default::default()
    };
    writer.save(&second).unwrap();
    assert_eq!(reader.list_details().unwrap().len(), 1);
    drop(reader);
    let reopened = Storage::open_read_only(&file.0).unwrap();
    assert_eq!(reopened.list_details().unwrap().len(), 2);
}
