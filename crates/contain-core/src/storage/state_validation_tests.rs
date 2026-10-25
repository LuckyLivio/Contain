use super::*;
use crate::model::*;
use crate::storage::stream::{DEFAULT_QUOTA, RawBuffer};

fn event(id: &str, kind: &str, operation: &str, success: Option<bool>) -> SystemEvent {
    SystemEvent {
        id: id.into(),
        sequence: id.bytes().map(u64::from).sum(),
        timestamp_ticks: 134_000_000_000_000_000,
        event_type: kind.into(),
        operation: operation.into(),
        resource: r"same-resource".into(),
        confidence: Confidence::High,
        success,
        ..Default::default()
    }
}

fn fixture() -> (Storage, Capture) {
    fixture_at(Path::new(":memory:"))
}

fn fixture_at(path: &Path) -> (Storage, Capture) {
    let mut db = Storage::open(path).unwrap();
    let mut capture = Capture {
        id: "state-validation".into(),
        name: "state-validation".into(),
        schema_version: 3,
        events: vec![
            event("file-selected", "file", "write_requested", None),
            event("file-unselected", "file", "write_requested", Some(true)),
            event("file-open", "file", "open_requested", Some(true)),
            event("file-failed", "file", "delete_requested", Some(false)),
            event("registry-selected", "registry", "set_value", Some(true)),
            event("registry-unselected", "registry", "set_value", Some(true)),
            event("registry-pending", "registry", "set_value", None),
        ],
        ..Default::default()
    };
    capture.backend.etw_events_lost = Some(0);
    capture.backend.etw_buffers_lost = Some(0);
    let mut initial = capture.clone();
    initial.events.clear();
    let mut raw = RawBuffer::new(&mut db, &initial, DEFAULT_QUOTA).unwrap();
    raw.append(&mut db, capture.events.clone());
    raw.finish(&mut db).unwrap();
    db.prepare_raw(&capture.id).unwrap();
    db.append_evidence(&capture).unwrap();
    (db, capture)
}

#[test]
fn file_name_origin_survives_reopen_pages_and_explanation_without_ownership() {
    let path =
        std::env::temp_dir().join(format!("contain-name-origin-{}.db", uuid::Uuid::new_v4()));
    let origin = 134_350_000_000_000_001;
    let mut capture = Capture {
        id: "name-origins".into(),
        name: "name-origins".into(),
        schema_version: 3,
        files: vec![FileChange {
            path: r"C:\fixture\old.tmp".into(),
            operation: "modified".into(),
            ..Default::default()
        }],
        ..Default::default()
    };
    // Same FileKey is deliberately shared: it never supplies a current name or
    // physical identity. The third row reuses both a path and object number.
    for (id, name, object, generation, observed, source, source_event) in [
        (
            "historical-write",
            "old.tmp",
            "object-a",
            "open-a",
            origin,
            "file_object_cache",
            "open-original",
        ),
        (
            "other-object-rename",
            "new.dat",
            "object-b",
            "open-b",
            origin + 10,
            "supplied_FileName",
            "other-object-rename",
        ),
        (
            "same-name-replacement",
            "old.tmp",
            "object-a",
            "open-c",
            origin + 20,
            "supplied_FileName",
            "same-name-replacement",
        ),
        (
            "legacy-no-name-proof",
            "old.tmp",
            "object-a",
            "open-c",
            origin + 30,
            "",
            "",
        ),
    ] {
        let mut e = event(id, "file", "write_requested", Some(true));
        e.timestamp_ticks = observed + 1;
        e.resource = format!(r"C:\fixture\{name}");
        e.evidence = AttributionEvidence {
            source: EvidenceSource::EtwFile,
            pid: Some(7),
            process_creation_time: Some(origin - 1),
            ..Default::default()
        };
        e.raw.resource_resolved = true;
        e.raw.file_object = Some(object.into());
        e.raw.file_key = Some("shared-key".into());
        e.raw.object_generation = Some(generation.into());
        e.raw.file_name = (!source.is_empty()).then(|| {
            Box::new(FileNameEvidence {
                path_source: source.into(),
                observed_ticks: observed,
                source_event_id: source_event.into(),
                current_path_verified: false,
            })
        });
        if id == "other-object-rename" {
            e.operation = "rename_requested".into();
        }
        capture.events.push(e);
    }
    crate::reliability::finish(&mut capture);
    let expected: std::collections::HashMap<_, _> = capture
        .events
        .iter()
        .map(|e| (e.id.clone(), serde_json::to_value(e).unwrap()))
        .collect();
    let mut db = Storage::open(&path).unwrap();
    db.save(&capture).unwrap();
    drop(db);
    let db = Storage::open(&path).unwrap();
    let full = db.load(&capture.id).unwrap();
    let summary = db.load_summary(&capture.id).unwrap();
    assert_eq!(summary.stats.known_actor_events, 4);
    assert_eq!(summary.stats.known_resource_events, 4);
    assert_eq!(summary.stats.high_confidence_events, 4);
    assert_eq!(summary.files[0].confidence, Confidence::Unknown);
    assert!(summary.events.is_empty());
    for (offset, e) in full.events.iter().enumerate() {
        let page = db.events_page(&capture.id, offset as u64, 1).unwrap();
        let explained = db.for_event(&e.id).unwrap();
        let explained_event = explained.events.iter().find(|x| x.id == e.id).unwrap();
        for view in [e, &page[0], explained_event] {
            assert_eq!(serde_json::to_value(view).unwrap(), expected[&e.id]);
            assert_eq!(view.confidence, Confidence::High);
            assert_eq!(view.dimensions.actor, Confidence::High);
            assert_eq!(view.success, Some(true));
            assert!(!view.state_validated);
        }
        if let Some(name) = &e.raw.file_name {
            assert_eq!(
                serde_json::to_value(e).unwrap()["raw"]["file_name"]["observed_ticks"],
                name.observed_ticks.to_string()
            );
        } else {
            assert!(crate::reliability::resource_evidence_reason(e).contains("unavailable"));
        }
        let original_edge = capture
            .edges
            .iter()
            .find(|edge| edge.from == format!("event:{}", e.id) && edge.to.starts_with("resource:"))
            .unwrap();
        assert!(
            explained
                .edges
                .iter()
                .any(|edge| edge.from == original_edge.from && edge.reason == original_edge.reason)
        );
    }
    assert!(db.events_page(&capture.id, 4, 1).unwrap().is_empty());
    drop(db);
    for suffix in ["", "-wal", "-shm"] {
        let artifact = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
        if artifact.exists() {
            std::fs::remove_file(artifact).unwrap();
        }
    }
}

fn assert_views(db: &Storage, capture: &Capture, expected: &[&str]) {
    let full = db.load(&capture.id).unwrap();
    assert!(db.load_summary(&capture.id).unwrap().events.is_empty());
    let page = db.events_page(&capture.id, 0, 256).unwrap();
    assert_eq!(full.events.len(), capture.events.len());
    for e in &full.events {
        assert_eq!(
            e.state_validated,
            expected.contains(&e.id.as_str()),
            "{}",
            e.id
        );
        let paged = page.iter().find(|other| other.id == e.id).unwrap();
        let explanation = db.for_event(&e.id).unwrap();
        assert_eq!(
            serde_json::to_value(e).unwrap(),
            serde_json::to_value(paged).unwrap()
        );
        assert_eq!(
            serde_json::to_value(e).unwrap(),
            serde_json::to_value(&explanation.events[0]).unwrap()
        );
    }
}

#[test]
fn initial_thread_proof_survives_reopen_pagination_and_late_loss_downgrade() {
    for late_loss in [false, true] {
        let path = std::env::temp_dir().join(format!(
            "contain-initial-identity-{}.db",
            uuid::Uuid::new_v4()
        ));
        let birth = 134_350_000_000_000_017;
        let proof = InitialThreadIdentity {
            thread_id: 7,
            thread_creation_time: birth + 1,
            owner_pid: 2,
            owner_creation_time: birth,
            capture_started_ticks: birth + 2,
            observed_at: birth + 3,
        };
        let mut source = event(
            "initial-thread-write",
            "file",
            "write_requested",
            Some(true),
        );
        source.timestamp_ticks = birth + 4;
        source.evidence = AttributionEvidence {
            source: EvidenceSource::EtwFile,
            pid: Some(2),
            process_creation_time: Some(birth),
            ..Default::default()
        };
        source.raw.initial_thread_identity = Some(proof.clone());
        source.raw.thread_id = Some(7);
        let mut capture = Capture {
            id: "initial-thread-readback".into(),
            name: "initial-thread-readback".into(),
            schema_version: 3,
            ..Default::default()
        };
        capture.backend.etw_events_lost = Some(0);
        capture.backend.etw_buffers_lost = Some(0);
        let mut db = Storage::open(&path).unwrap();
        let mut raw = RawBuffer::new(&mut db, &capture, DEFAULT_QUOTA).unwrap();
        raw.append(&mut db, vec![source.clone()]);
        raw.finish(&mut db).unwrap();
        capture.events.push(source);
        db.prepare_raw(&capture.id).unwrap();
        db.append_evidence(&capture).unwrap();
        if late_loss {
            capture.backend.context_losses = 1;
        }
        db.mark_finished(&capture).unwrap();
        drop(db);

        let db = Storage::open(&path).unwrap();
        let full = db.load(&capture.id).unwrap();
        let page = db.events_page(&capture.id, 0, 1).unwrap();
        let explanation = db.for_event("initial-thread-write").unwrap();
        assert_eq!(db.list().unwrap().len(), 1);
        assert!(db.load_summary(&capture.id).unwrap().events.is_empty());
        assert!(db.events_page(&capture.id, 1, 1).unwrap().is_empty());
        assert_eq!(full.events.len(), 1);
        for restored in [&full.events[0], &page[0], &explanation.events[0]] {
            assert_eq!(restored.raw.initial_thread_identity.as_ref(), Some(&proof));
            assert!(!restored.state_validated);
            assert_eq!(
                restored.confidence,
                if late_loss {
                    Confidence::Unknown
                } else {
                    Confidence::High
                }
            );
            let json = serde_json::to_value(restored).unwrap();
            assert_eq!(
                json["raw"]["initial_thread_identity"]["owner_creation_time"],
                birth.to_string()
            );
            assert_eq!(json, serde_json::to_value(&full.events[0]).unwrap());
        }
        drop(db);
        for suffix in ["", "-wal", "-shm"] {
            let artifact = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
            if artifact.exists() {
                std::fs::remove_file(artifact).unwrap();
            }
        }
    }
}

#[test]
fn selected_file_and_registry_ids_survive_full_paged_and_explain_views() {
    let (mut db, mut capture) = fixture();
    // Deliberately mark ineligible records too: persistence independently rejects
    // failed/open file requests and registry requests without successful status.
    for e in &mut capture.events {
        e.state_validated = !e.id.ends_with("unselected");
    }
    db.evidence_group(&capture.id, |db| {
        db.persist_state_validation(&capture.id, &capture.events)
    })
    .unwrap();
    db.mark_finished(&capture).unwrap();
    assert_views(&db, &capture, &["file-selected", "registry-selected"]);
}

#[test]
fn late_loss_clears_snapshot_validation_in_every_event_view() {
    let (mut db, mut capture) = fixture();
    for e in &mut capture.events {
        e.state_validated = e.id.ends_with("selected");
    }
    db.evidence_group(&capture.id, |db| {
        db.persist_state_validation(&capture.id, &capture.events)
    })
    .unwrap();
    capture.backend.context_losses = 1;
    db.mark_finished(&capture).unwrap();
    assert_views(&db, &capture, &[]);
    assert!(
        db.load(&capture.id)
            .unwrap()
            .events
            .iter()
            .all(|e| e.confidence == Confidence::Unknown)
    );
}

#[test]
fn validation_is_session_scoped_transactional_and_empty_selection_is_a_noop() {
    let (mut db, mut capture) = fixture();
    db.persist_state_validation(&capture.id, &capture.events)
        .unwrap();
    capture.events[0].state_validated = true;
    assert!(
        db.persist_state_validation(&capture.id, &capture.events)
            .is_err()
    );
    db.evidence_group(&capture.id, |db| {
        db.persist_state_validation("different-session", &capture.events)
    })
    .unwrap();
    db.mark_finished(&capture).unwrap();
    assert_views(&db, &capture, &[]);
}

#[test]
fn unfinished_raw_readback_suppresses_provisional_state_validation() {
    let mut db = Storage::open(Path::new(":memory:")).unwrap();
    let capture = Capture {
        id: "unfinished-validation".into(),
        schema_version: 3,
        ..Default::default()
    };
    let mut raw = RawBuffer::new(&mut db, &capture, DEFAULT_QUOTA).unwrap();
    let mut e = event("provisional", "file", "write_requested", Some(true));
    e.state_validated = true;
    e.dimensions = ConfidenceDimensions {
        actor: Confidence::High,
        resource: Confidence::High,
        operation: Confidence::High,
    };
    e.raw.header_pid = Some(42);
    e.raw.status = Some(0);
    raw.append(&mut db, vec![e]);
    raw.finish(&mut db).unwrap();
    assert!(!db.load(&capture.id).unwrap().events[0].state_validated);
    assert!(!db.events_page(&capture.id, 0, 256).unwrap()[0].state_validated);
    assert!(!db.for_event("provisional").unwrap().events[0].state_validated);
    for e in [
        db.load(&capture.id).unwrap().events.remove(0),
        db.events_page(&capture.id, 0, 256).unwrap().remove(0),
        db.for_event("provisional").unwrap().events.remove(0),
    ] {
        assert_eq!(e.dimensions.actor, Confidence::Unknown);
        assert_eq!(e.dimensions.resource, Confidence::Unknown);
        assert_eq!(e.dimensions.operation, Confidence::Unknown);
        assert_eq!(e.success, None);
        assert_eq!(e.raw.header_pid, Some(42));
        assert_eq!(e.raw.status, Some(0));
    }
}

#[test]
fn unfinished_statistics_count_committed_raw_rows_without_reusing_provisional_totals() {
    for indexed in [false, true] {
        let mut db = Storage::open(Path::new(":memory:")).unwrap();
        let mut capture = Capture {
            id: "unfinished-counts".into(),
            schema_version: 3,
            ..Default::default()
        };
        capture.stats.events_received = 99;
        capture.stats.events_dropped = 9;
        capture.stats.events_retained = 999;
        capture.stats.events_normalized = 888;
        capture.stats.known_actor_events = 777;
        capture.stats.known_resource_events = 666;
        capture.stats.unknown_actor_events = 555;
        capture.stats.unknown_resource_events = 444;
        capture.stats.high_confidence_events = 333;
        capture.stats.phase_ms.insert("retained_phase".into(), 543);
        let mut raw = RawBuffer::new(&mut db, &capture, DEFAULT_QUOTA).unwrap();
        raw.append(
            &mut db,
            vec![
                event("one", "file", "write_requested", Some(true)),
                event("two", "registry", "set_value", Some(true)),
                event("three", "process", "started", None),
            ],
        );
        raw.finish(&mut db).unwrap();
        if indexed {
            db.prepare_raw(&capture.id).unwrap();
        }
        for loaded in [
            db.load_summary(&capture.id).unwrap(),
            db.load(&capture.id).unwrap(),
            db.for_event("one").unwrap(),
        ] {
            assert_eq!(loaded.stats.events_retained, 3);
            assert_eq!(loaded.stats.events_normalized, 0);
            assert_eq!(loaded.stats.known_actor_events, 0);
            assert_eq!(loaded.stats.known_resource_events, 0);
            assert_eq!(loaded.stats.unknown_actor_events, 2);
            assert_eq!(loaded.stats.unknown_resource_events, 2);
            assert_eq!(loaded.stats.high_confidence_events, 0);
            assert_eq!(loaded.stats.events_received, 99);
            assert_eq!(loaded.stats.events_dropped, 9);
            assert_eq!(loaded.stats.phase_ms["retained_phase"], 543);
        }
    }
}

#[test]
fn document_write_failure_rolls_back_typed_validation_without_losing_raw_evidence() {
    let (mut db, mut capture) = fixture();
    capture.events[0].state_validated = true;
    db.connection.execute_batch("CREATE TRIGGER reject_validation BEFORE UPDATE ON event_documents BEGIN SELECT RAISE(ABORT,'injected document failure'); END;").unwrap();
    assert!(
        db.evidence_group(&capture.id, |db| {
            db.persist_state_validation(&capture.id, &capture.events)
        })
        .is_err()
    );
    let validated: u64 = db
        .connection
        .query_row(
            "SELECT COUNT(*) FROM observations WHERE state_validated=1",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(validated, 0);
    assert_eq!(
        db.load(&capture.id).unwrap().events.len(),
        capture.events.len()
    );
    assert_eq!(
        db.stream_state(&capture.id).unwrap().as_deref(),
        Some("failed")
    );
}

fn stored_summary(capture: &Capture) -> Capture {
    let mut summary = capture.clone();
    summary.events.clear();
    summary.files = vec![FileChange {
        path: "same-resource".into(),
        operation: "modified".into(),
        confidence: Confidence::High,
        reason: "Provisional state association".into(),
        ..Default::default()
    }];
    summary.registry = vec![RegistryChange {
        key: "registry".into(),
        name: "value".into(),
        operation: "modified".into(),
        confidence: Confidence::High,
        reason: "Provisional state association".into(),
        ..Default::default()
    }];
    summary.stats.high_confidence_events = capture.events.len() as u64;
    summary.stats.known_resource_events = capture.events.len() as u64;
    summary.stats.phase_ms.insert("retained_phase".into(), 123);
    let evidence = vec![
        AttributionEvidence {
            observation: Some(StateObservation {
                method: "controlled-test-handle".into(),
                timestamp_domain: "filetime_100ns".into(),
                started_ticks: Some(134_350_000_000_000_017),
                finished_ticks: Some(134_350_000_000_000_027),
                baseline_identity: Some("volume:file-before".into()),
                identity: Some("volume:file-after".into()),
                baseline_hash: Some("before-hash".into()),
                observed_hash: Some("after-hash".into()),
                baseline_stable: true,
                content_stable: true,
                path_pinned: true,
                write_delete_excluded: true,
                held_through_trace_stop: true,
                final_source_complete: true,
                mutation_coverage_complete: true,
                exclusion_duration_ns: Some(1234),
                retained_handles: Some(2),
                failure: None,
            }),
            ..Default::default()
        },
        // Legacy/source evidence must remain valid without an observation key.
        AttributionEvidence::default(),
    ];
    summary.files[0].evidence = evidence.clone();
    summary.registry[0].evidence = evidence;
    summary
}

fn disk_path() -> std::path::PathBuf {
    std::env::temp_dir().join(format!(
        "contain-state-recovery-{}.db",
        uuid::Uuid::new_v4()
    ))
}

fn remove_database(path: &Path) {
    for suffix in ["", "-wal", "-shm"] {
        let owned_path = format!("{}{suffix}", path.display());
        match std::fs::remove_file(owned_path) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => panic!("test database cleanup failed: {error}"),
        }
    }
}

#[test]
fn loss_discovered_after_summary_write_is_atomic_with_final_event_downgrade() {
    let path = disk_path();
    let (mut db, mut capture) = fixture_at(&path);
    for e in &mut capture.events {
        e.state_validated = e.id.ends_with("selected");
    }
    db.evidence_group(&capture.id, |db| {
        db.persist_state_validation(&capture.id, &capture.events)
    })
    .unwrap();
    let mut summary = stored_summary(&capture);
    db.save(&summary).unwrap();
    let phases_before = db.load_summary(&capture.id).unwrap().stats.phase_ms;
    // The storage finalization boundary must honor newly supplied loss even if
    // a previously committed summary did not yet contain that diagnostic.
    summary.backend.context_losses = 1;
    db.mark_finished(&summary).unwrap();
    drop(db);
    let db = Storage::open(&path).unwrap();
    let loaded = db.load_summary(&capture.id).unwrap();
    assert_eq!(loaded.backend.context_losses, 1);
    assert_eq!(loaded.stats.high_confidence_events, 0);
    assert_eq!(loaded.stats.known_resource_events, 0);
    assert_eq!(
        loaded.stats.unknown_resource_events,
        capture.events.len() as u64
    );
    assert_eq!(loaded.stats.phase_ms, phases_before);
    assert!(matches!(loaded.quality.level, QualityLevel::Incomplete));
    assert_eq!(loaded.files[0].confidence, Confidence::Unknown);
    assert_eq!(loaded.registry[0].confidence, Confidence::Unknown);
    assert!(
        !loaded.files[0].evidence[0]
            .observation
            .as_ref()
            .unwrap()
            .final_source_complete
    );
    assert!(
        !loaded.registry[0].evidence[0]
            .observation
            .as_ref()
            .unwrap()
            .final_source_complete
    );
    let mut expected_file_evidence = summary.files[0].evidence.clone();
    expected_file_evidence[0]
        .observation
        .as_mut()
        .unwrap()
        .final_source_complete = false;
    let mut expected_registry_evidence = summary.registry[0].evidence.clone();
    expected_registry_evidence[0]
        .observation
        .as_mut()
        .unwrap()
        .final_source_complete = false;
    assert_eq!(loaded.files[0].evidence, expected_file_evidence);
    assert_eq!(loaded.registry[0].evidence, expected_registry_evidence);
    assert_views(&db, &capture, &[]);
    assert!(
        db.load(&capture.id)
            .unwrap()
            .events
            .iter()
            .all(|e| e.confidence == Confidence::Unknown)
    );
    drop(db);
    remove_database(&path);
}

#[test]
fn interrupted_final_downgrade_reopens_raw_explanation_instead_of_stale_document() {
    let path = disk_path();
    let (mut db, mut capture) = fixture_at(&path);
    capture.events[0].state_validated = true;
    db.evidence_group(&capture.id, |db| {
        db.persist_state_validation(&capture.id, &capture.events)
    })
    .unwrap();
    let mut summary = stored_summary(&capture);
    db.save(&summary).unwrap();
    summary.backend.context_losses = 1;
    db.connection.execute_batch("CREATE TRIGGER interrupt_final BEFORE UPDATE ON event_documents BEGIN SELECT RAISE(ABORT,'injected finalization interruption'); END;").unwrap();
    assert!(db.mark_finished(&summary).is_err());
    drop(db);
    let db = Storage::open(&path).unwrap();
    let loaded = db.load_summary(&capture.id).unwrap();
    assert_eq!(loaded.capture_state.as_deref(), Some("finalizing"));
    assert_eq!(loaded.stats.high_confidence_events, 0);
    assert_eq!(loaded.files[0].confidence, Confidence::Unknown);
    assert_eq!(loaded.registry[0].confidence, Confidence::Unknown);
    assert!(
        !loaded.files[0].evidence[0]
            .observation
            .as_ref()
            .unwrap()
            .final_source_complete
    );
    assert!(
        !loaded.registry[0].evidence[0]
            .observation
            .as_ref()
            .unwrap()
            .final_source_complete
    );
    let committed_loss: u64 = db.connection.query_row(
        "SELECT json_extract(backend_json,'$.context_losses') FROM capture_metadata WHERE session_id=?1",
        [&capture.id], |row| row.get(0),
    ).unwrap();
    assert_eq!(
        committed_loss, 0,
        "the interrupted transaction must roll back its summary diagnostic too"
    );
    let stale: bool = db
        .connection
        .query_row(
            "SELECT json_extract(document,'$.state_validated') FROM event_documents WHERE id=?1",
            [&capture.events[0].id],
            |row| row.get(0),
        )
        .unwrap();
    assert!(
        stale,
        "the failed transaction must retain its previous document"
    );
    let page = db.events_page(&capture.id, 0, 256).unwrap();
    for event in page {
        let explanation = db.for_event(&event.id).unwrap();
        assert_eq!(explanation.events.len(), 1);
        assert_eq!(event.confidence, Confidence::Unknown);
        assert!(!event.state_validated);
        assert_eq!(
            serde_json::to_value(&event).unwrap(),
            serde_json::to_value(&explanation.events[0]).unwrap()
        );
        assert!(explanation.edges.is_empty());
        assert!(explanation.operations.is_empty());
    }
    drop(db);
    remove_database(&path);
}

#[test]
fn controlled_observation_round_trips_without_changing_source_event_or_legacy_evidence() {
    let path = disk_path();
    let (mut db, capture) = fixture_at(&path);
    let mut summary = stored_summary(&capture);
    let absence = StateObservation {
        method: "baseline-handle-with-unproven-absence".into(),
        timestamp_domain: "filetime_100ns".into(),
        started_ticks: Some(134_350_000_000_000_017),
        finished_ticks: Some(134_350_000_000_000_027),
        baseline_identity: Some("volume:deleted-file".into()),
        baseline_hash: Some("deleted-file-baseline-hash".into()),
        baseline_stable: true,
        failure: Some("Final absence cannot be pinned".into()),
        ..Default::default()
    };
    summary.files.push(FileChange {
        path: "deleted-file".into(),
        operation: "deleted".into(),
        evidence: vec![AttributionEvidence {
            observation: Some(absence.clone()),
            ..Default::default()
        }],
        ..Default::default()
    });
    summary.registry.push(RegistryChange {
        key: "registry".into(),
        name: "deleted-value".into(),
        operation: "deleted".into(),
        evidence: vec![AttributionEvidence {
            observation: Some(StateObservation {
                method: "unlocked-absence".into(),
                ..absence
            }),
            ..Default::default()
        }],
        ..Default::default()
    });
    db.save(&summary).unwrap();
    db.mark_finished(&summary).unwrap();
    drop(db);
    let db = Storage::open(&path).unwrap();
    for loaded in [
        db.load_summary(&capture.id).unwrap(),
        db.load(&capture.id).unwrap(),
        db.for_event(&capture.events[0].id).unwrap(),
    ] {
        let modified_file = loaded
            .files
            .iter()
            .find(|change| change.operation == "modified")
            .unwrap();
        let modified_registry = loaded
            .registry
            .iter()
            .find(|change| change.operation == "modified")
            .unwrap();
        assert_eq!(modified_file.evidence, summary.files[0].evidence);
        assert_eq!(modified_registry.evidence, summary.registry[0].evidence);
        assert_eq!(
            loaded
                .files
                .iter()
                .find(|change| change.operation == "deleted")
                .unwrap()
                .evidence,
            summary.files[1].evidence
        );
        assert_eq!(
            loaded
                .registry
                .iter()
                .find(|change| change.operation == "deleted")
                .unwrap()
                .evidence,
            summary.registry[1].evidence
        );
        assert!(modified_file.evidence[1].observation.is_none());
        assert!(modified_registry.evidence[1].observation.is_none());
        assert!(
            loaded
                .events
                .iter()
                .all(|event| event.evidence.observation.is_none())
        );
        let value = serde_json::to_value(&modified_file.evidence).unwrap();
        assert_eq!(
            value[0]["observation"]["started_ticks"],
            "134350000000000017"
        );
        assert_eq!(
            value[0]["observation"]["finished_ticks"],
            "134350000000000027"
        );
        assert!(value[1].get("observation").is_none());
    }
    assert!(
        db.events_page(&capture.id, 0, 256)
            .unwrap()
            .iter()
            .all(|event| event.evidence.observation.is_none())
    );
    drop(db);
    remove_database(&path);
}
