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
    let mut db = Storage::open(Path::new(":memory:")).unwrap();
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
    raw.append(&mut db, vec![e]);
    raw.finish(&mut db).unwrap();
    assert!(!db.load(&capture.id).unwrap().events[0].state_validated);
    assert!(!db.events_page(&capture.id, 0, 256).unwrap()[0].state_validated);
    assert!(!db.for_event("provisional").unwrap().events[0].state_validated);
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
