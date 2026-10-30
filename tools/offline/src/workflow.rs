//! Deterministic synthetic inputs, persisted and queried through production APIs.
//! These are model examples, never reconstructed private captures or ETW proof.
use crate::storage::stream::{DEFAULT_QUOTA, RawBuffer};
use crate::{model::*, reliability, render, storage::Storage};
use anyhow::{Result, ensure};
use std::path::Path;

pub const CASES: [&str; 3] = ["finished", "historical", "interrupted"];
const BIRTH: u64 = 134_350_000_000_000_001;

fn capture(case: &str) -> Capture {
    let id = format!("synthetic-{case}");
    Capture {
        id: id.clone(),
        name: format!("Synthetic {case}"),
        schema_version: 3,
        installer: r"C:\synthetic\setup.exe".into(),
        started_at: "2026-10-01T00:00:00Z".into(),
        watch_roots: vec![r"C:\synthetic\observed".into()],
        registry_key: None,
        processes: vec![ProcessRecord {
            pid: 100,
            creation_time: Some(BIRTH),
            image: r"C:\synthetic\setup.exe".into(),
            first_seen: "2026-10-01T00:00:00Z".into(),
            confidence: Confidence::Certain,
            reason: "Synthetic owned installer identity".into(),
            evidence: AttributionEvidence {
                source: EvidenceSource::ProcessApi,
                pid: Some(100),
                process_creation_time: Some(BIRTH),
                rule: AttributionRule::InstallerPid,
                session_id: Some(id),
                ..Default::default()
            },
            ..Default::default()
        }],
        backend: BackendReport {
            etw_process: "active".into(),
            etw_file: "active".into(),
            etw_registry: "not_requested".into(),
            etw_events_lost: Some(0),
            etw_buffers_lost: Some(0),
            ..Default::default()
        },
        warnings: vec![
            "SYNTHETIC model fixture; provider labels are inputs, no Windows capture was executed."
                .into(),
        ],
        ..Default::default()
    }
}

fn events(c: &Capture, historical: bool) -> Vec<SystemEvent> {
    (0..3)
        .map(|i| {
            let id = format!("{}-event-{i}", c.id);
            let mut event = SystemEvent {
                id: id.clone(),
                timestamp: "2026-10-01T00:00:01Z".into(),
                timestamp_ticks: BIRTH + 10 + i,
                sequence: i + 1,
                event_type: "file".into(),
                operation: "write_requested".into(),
                // The repeated string deliberately does not merge two object generations.
                resource: r"C:\synthetic\observed\recorded.tmp".into(),
                confidence: Confidence::High,
                success: Some(true),
                reason:
                    "Synthetic source request linked to the installer instance; no ownership claim"
                        .into(),
                evidence: c.processes[0].evidence.clone(),
                raw: RawEvidence {
                    resource_resolved: true,
                    status: Some(0),
                    file_object: Some(format!("synthetic-object-{i}")),
                    object_generation: Some(format!("synthetic-generation-{i}")),
                    file_name: Some(Box::new(FileNameEvidence {
                        path_source: if historical && i == 0 {
                            "file_object_cache"
                        } else {
                            "supplied_FileName"
                        }
                        .into(),
                        observed_ticks: if historical && i == 0 {
                            BIRTH + 1
                        } else {
                            BIRTH + 10 + i
                        },
                        source_event_id: if historical && i == 0 {
                            "synthetic-earlier-open".into()
                        } else {
                            id
                        },
                        current_path_verified: false,
                    })),
                    ..Default::default()
                },
                ..Default::default()
            };
            event.evidence.source = EvidenceSource::EtwFile;
            if historical && i == 1 {
                event.evidence = AttributionEvidence {
                    source: EvidenceSource::EtwFile,
                    rule: AttributionRule::MissingWriter,
                    ..Default::default()
                };
                event.confidence = Confidence::Unknown;
                event.success = None;
                event.raw.status = None;
                event.reason =
                    "Synthetic unresolved writer and conflicting completion evidence".into();
            }
            if historical && i == 2 {
                // Roundtrip a historical document with the optional new field absent.
                let mut old = serde_json::to_value(&event).unwrap();
                old["raw"].as_object_mut().unwrap().remove("file_name");
                event = serde_json::from_value(old).unwrap();
            }
            event
        })
        .collect()
}

/// No manual SQL/table population: use the same writer, finalizer and read APIs.
pub fn write_fixture(path: &Path, case: &str) -> Result<String> {
    ensure!(CASES.contains(&case), "unknown synthetic case: {case}");
    ensure!(!path.exists(), "refusing to replace an existing database");
    let mut c = capture(case);
    if case == "interrupted" {
        c.backend.etw_events_lost = None;
        c.backend.etw_buffers_lost = None;
        c.warnings
            .push("Synthetic interrupted capture: final provider counters unavailable".into());
    }
    let raw_events = events(&c, case == "historical");
    let mut db = Storage::open(path)?;
    let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA)?;
    writer.append(&mut db, raw_events.clone());
    c.backend.stream = Some(writer.finish(&mut db)?);
    if case != "interrupted" {
        db.prepare_raw(&c.id)?;
        c.events = raw_events;
        c.finished_at = "2026-10-01T00:00:02Z".into();
        c.exit_code = Some(0);
        c.backend.events_received = c.events.len() as u64;
        c.files.push(FileChange {
            path: c.watch_roots[0].clone() + r"\recorded.tmp",
            operation: "modified".into(),
            before_hash: Some("synthetic-before".into()),
            after_hash: Some("synthetic-after".into()),
            after_size: Some(32),
            reason: "Synthetic before/after difference; final ownership is Unknown".into(),
            ..Default::default()
        });
        for event in &c.events {
            c.operations.push(NormalizedOperation {
                id: format!("operation-{}", event.id),
                operation: "WriteRequested".into(),
                resource: event.resource.clone(),
                raw_events: vec![event.id.clone()],
                confidence: event.confidence,
                success: event.success,
                reason: "Synthetic native request, not an explicit application API call".into(),
                ..Default::default()
            });
        }
        reliability::finish(&mut c);
        db.save(&c)?;
        db.mark_finished(&c)?;
    }
    Ok(c.id)
}

pub fn generate(directory: &Path) -> Result<()> {
    ensure!(
        !directory.exists(),
        "choose a new output directory; existing evidence is preserved"
    );
    std::fs::create_dir_all(directory)?;
    for case in CASES {
        let path = directory.join(format!("{case}.db"));
        let id = write_fixture(&path, case)?;
        let db = Storage::open(&path)?;
        let summary = db.load_summary(&id)?;
        let full = db.load(&id)?;
        let selected = &full.events[0].id;
        let focused = db.for_event(selected)?;
        let text = format!(
            "SYNTHETIC — production storage and text-rendering functions; not the Windows CLI or ETW.\n\nINSPECT\n{}\nDETAIL\n{}\nHISTORY\n{}\nEXPLAIN\n{}",
            render::inspect_text(&summary, false),
            render::inspect_text(&full, true),
            render::history_text(&full),
            render::explain_text(&focused, selected)?
        );
        std::fs::write(directory.join(format!("{case}.txt")), text)?;
        std::fs::write(
            directory.join(format!("{case}.json")),
            serde_json::to_string_pretty(
                &serde_json::json!({"schema_version":3,"kind":"inspect","data":full}),
            )?,
        )?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn synthetic_workflow_reopens_and_preserves_summary_page_explanation_agreement() {
        let directory =
            std::env::temp_dir().join(format!("contain-workflow-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir(&directory).unwrap();
        for case in CASES {
            let path = directory.join(format!("{case}.db"));
            let id = write_fixture(&path, case).unwrap();
            let db = Storage::open(&path).unwrap();
            assert_eq!(db.list().unwrap().len(), 1);
            let full = db.load(&id).unwrap();
            let summary = db.load_summary(&id).unwrap();
            assert!(summary.events.is_empty());
            assert_eq!(render::summary_text(&summary), render::summary_text(&full));
            assert_eq!(full.events.len(), 3);
            let mut paged = db.events_page(&id, 0, 2).unwrap();
            paged.extend(db.events_page(&id, 2, 2).unwrap());
            assert!(db.events_page(&id, 3, 2).unwrap().is_empty());
            assert_eq!(
                serde_json::to_value(&paged).unwrap(),
                serde_json::to_value(&full.events).unwrap()
            );
            for event in &full.events {
                let focused = db.for_event(&event.id).unwrap();
                assert_eq!(
                    render::explanation(&full, &event.id).unwrap(),
                    render::explanation(&focused, &event.id).unwrap()
                );
                assert!(render::history_text(&full).contains(&event.id));
            }
            if case == "interrupted" {
                // Raw writer drained; interruption precedes derived finalization.
                assert_eq!(summary.capture_state.as_deref(), Some("finalizing"));
                assert!(matches!(summary.quality.level, QualityLevel::Incomplete));
                assert!(
                    full.events
                        .iter()
                        .all(|e| e.confidence == Confidence::Unknown && !e.state_validated)
                );
            } else {
                assert_eq!(summary.capture_state.as_deref(), Some("finished"));
                assert_eq!(full.files[0].confidence, Confidence::Unknown);
                assert_eq!(full.events[0].confidence, Confidence::High);
                assert!(full.events.iter().all(|e| !e.state_validated));
            }
            if case == "historical" {
                assert!(
                    render::explain_text(&full, &full.events[0].id)
                        .unwrap()
                        .contains("historical")
                );
                assert_eq!(full.events[1].confidence, Confidence::Unknown);
                assert!(full.events[2].raw.file_name.is_none());
                assert!(
                    render::explain_text(&full, &full.events[2].id)
                        .unwrap()
                        .contains("unavailable")
                );
            }
        }
        std::fs::remove_dir_all(directory).unwrap();
    }
}
