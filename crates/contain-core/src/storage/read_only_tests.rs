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
fn session_search_matches_before_paging_with_literal_text_and_original_ids() {
    for version in [4, 2, 3] {
        let file = TestFile::new();
        let mut capture = sample();
        capture.events = (0..130)
            .rev() // Deliberately persist out of timeline order.
            .map(|index| SystemEvent {
                id: format!("search-{index:03}"),
                timestamp_ticks: 134_350_000_000_000_017,
                sequence: index / 2, // Equal time and sequence requires the ID tie-break.
                operation: if index == 129 {
                    "DeleteRequested"
                } else {
                    "write_requested"
                }
                .into(),
                resource: if (70..123).contains(&index) {
                    r"C:\Synthetic Path\中文文件%_Needle.txt".into()
                } else {
                    r"C:\Synthetic Path\ordinary.txt".into()
                },
                confidence: if index == 129 {
                    Confidence::High
                } else {
                    Confidence::Unknown
                },
                ..Default::default()
            })
            .collect();
        let mut other = capture.clone();
        other.id = "other-session".into();
        // Same display name must never change session search scope.
        for event in &mut other.events {
            event.id = format!("other-{}", event.id);
        }
        {
            let mut db = Storage::open(&file.0).unwrap();
            db.save(&capture).unwrap();
            db.save(&other).unwrap();
            if version < 4 {
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
        }
        let before = fs::read(&file.0).unwrap();
        let db = Storage::open_read_only(&file.0).unwrap();
        for needle in [
            "NEEDLE",
            "中文文件",
            "%",
            "_Needle",
            r"c:\synthetic path\中文文件%_needle.txt",
        ] {
            let first = db.events_search(&capture.id, needle, 0, 50).unwrap();
            let second = db.events_search(&capture.id, needle, 50, 50).unwrap();
            assert_eq!((first.matched_total, second.matched_total), (53, 53));
            assert_eq!((first.events.len(), second.events.len()), (50, 3));
            // Expected identities come from a fixed fixture specification, not
            // the production matcher or a second query over stored resources.
            let expected: Vec<_> = (70..123)
                .map(|index| format!("search-{index:03}"))
                .collect();
            let actual: Vec<_> = first
                .events
                .iter()
                .chain(&second.events)
                .map(|e| e.id.clone())
                .collect();
            assert_eq!(actual, expected);
            let whole = db
                .events_search_all(&capture.id, needle, MAX_SEARCH_EXPORT_BYTES)
                .unwrap();
            assert_eq!(whole.matched_total, 53);
            assert_eq!(whole.name, capture.name);
            assert_eq!(
                whole
                    .events
                    .iter()
                    .map(|event| event.id.clone())
                    .collect::<Vec<_>>(),
                expected
            );
            assert_eq!(first.search_fingerprint, second.search_fingerprint);
            assert_eq!(first.search_fingerprint, whole.search_fingerprint);
            assert_eq!(whole.search_fingerprint.len(), 64);
            assert!(
                whole
                    .search_fingerprint
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
            );
            let selected = &second.events[0];
            assert_eq!(selected.id, "search-120");
            let explained = db.for_event(&selected.id).unwrap();
            assert_eq!(explained.id, capture.id);
            let explained_event = explained
                .events
                .iter()
                .find(|e| e.id == selected.id)
                .unwrap();
            assert_eq!(
                serde_json::to_value(selected).unwrap(),
                serde_json::to_value(explained_event).unwrap()
            );
        }
        for needle in ["deleterequested", "HIGH", "search-129"] {
            let page = db.events_search(&capture.id, needle, 0, 50).unwrap();
            assert_eq!(page.matched_total, 1);
            assert_eq!(page.events[0].id, "search-129");
        }
        for needle in ["missing", "%missing", "针", "' OR 1=1 --"] {
            let page = db.events_search(&capture.id, needle, 0, 50).unwrap();
            assert_eq!(page.matched_total, 0);
            assert!(page.events.is_empty());
        }
        let all = db.events_search(&capture.id, "", 0, 50).unwrap();
        assert_eq!(all.matched_total, 130);
        assert_eq!(all.events.len(), 50);
        // `_` occurs literally in the first 129 operations; treating it as a
        // SQL wildcard would incorrectly include DeleteRequested as well.
        assert_eq!(
            db.events_search(&capture.id, "_", 0, 50)
                .unwrap()
                .matched_total,
            129
        );
        let after = db
            .events_search(&capture.id, "needle", u64::MAX, 50)
            .unwrap();
        assert_eq!(after.matched_total, 53);
        assert!(after.events.is_empty());
        assert!(db.events_search(&capture.id, "needle", 0, 0).is_err());
        assert!(db.events_search(&capture.id, "needle", 0, 4097).is_err());
        drop(db);
        assert_eq!(fs::read(&file.0).unwrap(), before);
    }
}

#[test]
fn session_search_ascii_case_rule_and_writable_snapshot_lifetime() {
    let file = TestFile::new();
    let mut capture = sample();
    capture.events[0].resource = "C:\\Path With Spaces\\Ä-file.txt".into();
    let mut db = Storage::open(&file.0).unwrap();
    db.save(&capture).unwrap();
    assert_eq!(
        db.events_search(&capture.id, "path with spaces", 0, 50)
            .unwrap()
            .matched_total,
        1
    );
    assert_eq!(
        db.events_search(&capture.id, "Ä-file", 0, 50)
            .unwrap()
            .matched_total,
        1
    );
    assert_eq!(
        db.events_search(&capture.id, "ä-file", 0, 50)
            .unwrap()
            .matched_total,
        0
    );
    assert!(db.connection.is_autocommit());
    // Search releases the temporary read view; subsequent writes still work.
    db.connection
        .execute(
            "UPDATE event_documents SET document='broken' WHERE id='event-0'",
            [],
        )
        .unwrap();
    assert!(db.events_search(&capture.id, "file", 0, 50).is_err());
    assert!(db.connection.is_autocommit());
}

#[test]
fn session_search_uses_read_snapshot_and_rejects_bad_documents() {
    let file = TestFile::new();
    {
        let mut db = Storage::open(&file.0).unwrap();
        db.connection
            .pragma_update(None, "journal_mode", "WAL")
            .unwrap();
        db.save(&sample()).unwrap();
    }
    let db = Storage::open_read_only(&file.0).unwrap();
    assert_eq!(
        db.events_search("sample-session", "write", 0, 1)
            .unwrap()
            .matched_total,
        5
    );
    {
        let writer = Connection::open(&file.0).unwrap();
        writer
            .execute(
                "UPDATE event_documents SET document='invalid' WHERE id='event-4'",
                [],
            )
            .unwrap();
    }
    // A subsequent query on the same explicit read view keeps the old snapshot.
    assert_eq!(
        db.events_search("sample-session", "write", 4, 1)
            .unwrap()
            .events[0]
            .id,
        "event-4"
    );
    drop(db);
    let db = Storage::open_read_only(&file.0).unwrap();
    // Even a malformed document after a full page must fail, not claim a partial
    // count/list is a successful complete search.
    assert!(db.events_search("sample-session", "write", 0, 1).is_err());
}

#[test]
fn session_search_matches_unfinished_confidence_as_exposed_by_history() {
    let file = TestFile::new();
    let capture = sample();
    {
        let mut db = Storage::open(&file.0).unwrap();
        let mut writer = stream::RawBuffer::new(&mut db, &capture, stream::DEFAULT_QUOTA).unwrap();
        let mut event = capture.events[0].clone();
        event.confidence = Confidence::High;
        writer.append(&mut db, vec![event]);
        writer.finish(&mut db).unwrap();
    }
    let db = Storage::open_read_only(&file.0).unwrap();
    assert_eq!(
        db.events_search(&capture.id, "High", 0, 50)
            .unwrap()
            .matched_total,
        0
    );
    let page = db.events_search(&capture.id, "Unknown", 0, 50).unwrap();
    assert_eq!(page.matched_total, 1);
    let explained = db.for_event(&page.events[0].id).unwrap();
    assert_eq!(
        serde_json::to_value(&page.events[0]).unwrap(),
        serde_json::to_value(&explained.events[0]).unwrap()
    );
}

#[test]
fn complete_search_fingerprint_detects_equal_count_replacement_and_name_change() {
    let file = TestFile::new();
    let capture = sample();
    {
        let mut db = Storage::open(&file.0).unwrap();
        db.connection
            .pragma_update(None, "journal_mode", "WAL")
            .unwrap();
        db.save(&capture).unwrap();
    }
    let pinned = Storage::open_read_only(&file.0).unwrap();
    let page = pinned.events_search(&capture.id, "write", 0, 2).unwrap();
    let whole = pinned
        .events_search_all(&capture.id, "write", MAX_SEARCH_EXPORT_BYTES)
        .unwrap();
    assert_eq!(whole.events.len(), 5);
    assert_eq!(whole.search_fingerprint, page.search_fingerprint);
    // Retain every ID and total but replace content outside the visible page.
    let mut replacement = capture.events[4].clone();
    replacement.resource = "C:\\synthetic\\替换 %_ <&> \"file\".txt".into();
    replacement.reason = "SYNTHETIC replaced event with identical ID/count".into();
    let writer = Connection::open(&file.0).unwrap();
    writer
        .execute(
            "UPDATE event_documents SET document=?1 WHERE id=?2",
            params![serde_json::to_string(&replacement).unwrap(), replacement.id],
        )
        .unwrap();
    // The explicit read-only snapshot must still return all original content.
    let old_export = pinned
        .events_search_all(&capture.id, "write", MAX_SEARCH_EXPORT_BYTES)
        .unwrap();
    assert_eq!(old_export.search_fingerprint, page.search_fingerprint);
    assert_eq!(
        serde_json::to_value(&old_export.events).unwrap(),
        serde_json::to_value(&whole.events).unwrap()
    );
    drop(pinned);
    let fresh = Storage::open_read_only(&file.0).unwrap();
    let changed = fresh
        .events_search_all(&capture.id, "write", MAX_SEARCH_EXPORT_BYTES)
        .unwrap();
    assert_eq!(changed.matched_total, whole.matched_total);
    assert_eq!(
        changed
            .events
            .iter()
            .map(|event| &event.id)
            .collect::<Vec<_>>(),
        whole
            .events
            .iter()
            .map(|event| &event.id)
            .collect::<Vec<_>>()
    );
    assert_ne!(changed.search_fingerprint, whole.search_fingerprint);
    assert_eq!(changed.events[4].resource, replacement.resource);
    assert_eq!(
        fresh
            .events_search(&capture.id, "write", 2, 2)
            .unwrap()
            .search_fingerprint,
        changed.search_fingerprint
    );
    assert_ne!(
        fresh
            .events_search(&capture.id, "WRITE", 0, 2)
            .unwrap()
            .search_fingerprint,
        changed.search_fingerprint
    );
    drop(fresh);
    writer
        .execute(
            "UPDATE applications SET name='SYNTHETIC renamed record' WHERE id=?1",
            [&capture.id],
        )
        .unwrap();
    let renamed = Storage::open_read_only(&file.0)
        .unwrap()
        .events_search_all(&capture.id, "write", MAX_SEARCH_EXPORT_BYTES)
        .unwrap();
    assert_eq!(renamed.name, "SYNTHETIC renamed record");
    assert_eq!(renamed.matched_total, changed.matched_total);
    assert_ne!(renamed.search_fingerprint, changed.search_fingerprint);
}

#[test]
fn complete_search_refuses_limits_bad_late_documents_and_missing_session() {
    let file = TestFile::new();
    let mut db = Storage::open(&file.0).unwrap();
    let mut capture = sample();
    // A late matching resource alone exceeds the deliberately small budget.
    // Expected failure is independent of the serializer's overhead accounting.
    capture.events[4].resource = "x".repeat(8192);
    db.save(&capture).unwrap();
    assert_eq!(
        db.events_search_all(&capture.id, "write", 65536)
            .unwrap()
            .events
            .len(),
        5
    );
    assert!(db.events_search_all(&capture.id, "write", 8192).is_err());
    assert!(db.events_search_all(&capture.id, "write", 0).is_err());
    assert!(
        db.events_search_all(&capture.id, "write", MAX_SEARCH_EXPORT_BYTES + 1)
            .is_err()
    );
    assert!(
        db.events_search_all("missing-session", "write", MAX_SEARCH_EXPORT_BYTES)
            .is_err()
    );
    let empty = db
        .events_search_all(&capture.id, "never-present", MAX_SEARCH_EXPORT_BYTES)
        .unwrap();
    assert_eq!(empty.matched_total, 0);
    assert!(empty.events.is_empty());
    assert_eq!(
        empty.search_fingerprint,
        db.events_search(&capture.id, "never-present", 0, 50)
            .unwrap()
            .search_fingerprint
    );
    assert!(db.connection.is_autocommit());
    db.connection
        .execute(
            "UPDATE event_documents SET document='broken' WHERE id='event-4'",
            [],
        )
        .unwrap();
    assert!(
        db.events_search_all(&capture.id, "write", MAX_SEARCH_EXPORT_BYTES)
            .is_err()
    );
    assert!(db.connection.is_autocommit());
}

#[test]
fn exact_session_id_wins_over_a_newer_uuid_shaped_record_name() {
    let file = TestFile::new();
    let mut requested = sample();
    requested.id = "11111111-1111-4111-8111-111111111111".into();
    requested.name = "shared record name".into();
    let mut shadow = sample();
    shadow.id = "22222222-2222-4222-8222-222222222222".into();
    shadow.name = requested.id.clone();
    shadow.started_at = "2026-02-02T03:04:05+00:00".into();
    for event in &mut shadow.events {
        event.id = format!("shadow-{}", event.id);
    }
    let mut latest_name = sample();
    latest_name.id = "33333333-3333-4333-8333-333333333333".into();
    latest_name.name = requested.name.to_uppercase();
    latest_name.started_at = "2026-03-02T03:04:05+00:00".into();
    for event in &mut latest_name.events {
        event.id = format!("latest-{}", event.id);
    }
    {
        let mut db = Storage::open(&file.0).unwrap();
        for capture in [&requested, &shadow, &latest_name] {
            db.save(capture).unwrap();
        }
    }
    let before = fs::read(&file.0).unwrap();
    let db = Storage::open_read_only(&file.0).unwrap();
    assert_eq!(db.load(&requested.id).unwrap().id, requested.id);
    assert_eq!(db.load_summary(&requested.id).unwrap().id, requested.id);
    assert_eq!(db.load_by_id(&requested.id).unwrap().id, requested.id);
    assert_eq!(
        db.load_summary_by_id(&requested.id).unwrap().id,
        requested.id
    );
    assert_eq!(db.load(&requested.name).unwrap().id, latest_name.id);
    assert_eq!(db.load_summary(&requested.name).unwrap().id, latest_name.id);
    assert!(db.load_by_id(&requested.name).is_err());
    assert!(db.load_summary_by_id(&requested.name).is_err());
    let page = db.events_page_compatible(&requested.id, 0, 2).unwrap();
    assert_eq!(page[0].id, requested.events[0].id);
    let explained = db.for_event(&page[0].id).unwrap();
    assert_eq!(explained.id, requested.id);
    assert_eq!(explained.events[0].id, page[0].id);
    drop(db);
    assert_eq!(fs::read(&file.0).unwrap(), before);
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
