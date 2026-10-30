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
    if case != "interrupted" {
        c.files.push(FileChange {
            path: c.watch_roots[0].clone() + r"\recorded.tmp",
            operation: "modified".into(),
            before_hash: Some("synthetic-before".into()),
            after_hash: Some("synthetic-after".into()),
            after_size: Some(32),
            reason: "Synthetic before/after difference; final ownership is Unknown".into(),
            ..Default::default()
        });
    }
    persist_fixture(path, c, raw_events, case == "interrupted")
}

fn persist_fixture(
    path: &Path,
    mut c: Capture,
    raw_events: Vec<SystemEvent>,
    interrupted: bool,
) -> Result<String> {
    ensure!(!path.exists(), "refusing to replace an existing database");
    // State differences belong to finalization. Persisting them in both the
    // initial stream document and the final save would duplicate their rows.
    let files = std::mem::take(&mut c.files);
    let mut db = Storage::open(path)?;
    let mut writer = RawBuffer::new(&mut db, &c, DEFAULT_QUOTA)?;
    writer.append(&mut db, raw_events.clone());
    c.backend.stream = Some(writer.finish(&mut db)?);
    if !interrupted {
        db.prepare_raw(&c.id)?;
        c.files = files;
        c.events = raw_events;
        c.finished_at = "2026-10-01T00:00:02Z".into();
        c.exit_code = Some(0);
        c.backend.events_received = c.events.len() as u64;
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

/// Bind the existing historical model to an actual disposable fixture directory.
/// The alias is supplied by the Windows preflight after filesystem verification;
/// the production report independently verifies that directory identity again.
/// Neither the modeled child paths nor their historical names must exist now.
pub fn generate_report_fixture(
    directory: &Path,
    fixture_root: &Path,
    observed_root: &str,
    installer: &Path,
) -> Result<()> {
    ensure!(!directory.exists(), "choose a new output directory");
    let root = fixture_root.canonicalize()?;
    ensure!(root.is_dir(), "fixture root must be a directory");
    ensure!(
        std::fs::read(root.join(".contain-demo-marker"))? == b"Contain test fixture",
        "fixture marker mismatch"
    );
    let leaf = root
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("");
    let guid = leaf.strip_prefix("contain-demo-").unwrap_or("");
    ensure!(
        guid.len() == 32 && guid.bytes().all(|byte| byte.is_ascii_hexdigit()),
        "fixture GUID missing"
    );
    ensure!(installer.is_file(), "packaged fixture executable missing");
    let mut c = capture("report");
    c.name = "SyntheticReport".into();
    c.watch_roots = vec![root.to_string_lossy().into_owned()];
    c.installer = installer.canonicalize()?.to_string_lossy().into_owned();
    c.processes[0].image = c.installer.clone();
    c.processes[0].evidence.process_image = Some(c.installer.clone());
    c.registry_key = Some(format!(r"Software\Contain\Demo\{leaf}"));
    // Reuse all three historical/unknown/old-record model cases. These labels
    // are synthetic inputs, and never attest to a running Windows provider.
    let mut raw = events(&c, true);
    raw[0].resource = format!(r"{observed_root}\historical-deleted.tmp");
    raw[1].resource = format!(r"{}\recorded.tmp", c.watch_roots[0]);
    raw[2].resource = format!(r"{observed_root}\recorded.tmp");
    c.files = vec![
        FileChange {
            path: raw[0].resource.clone(),
            operation: "deleted".into(),
            before_hash: Some("synthetic-before".into()),
            reason: "SYNTHETIC deleted state; final ownership is Unknown".into(),
            ..Default::default()
        },
        FileChange {
            path: format!(r"{}\recorded.tmp", c.watch_roots[0]),
            operation: "modified".into(),
            before_hash: Some("synthetic-before".into()),
            after_hash: Some("synthetic-after".into()),
            after_size: Some(32),
            reason: "SYNTHETIC state difference; final ownership is Unknown".into(),
            ..Default::default()
        },
    ];
    let truth: Vec<_> = raw
        .iter()
        .map(|event| {
            serde_json::json!({"synthetic":true, "pid":100, "creation_time":BIRTH.to_string(),
                "operation":event.operation, "resource":event.resource,
                "note":"SYNTHETIC model inputs, not an independently observed installer"})
        })
        .collect();
    std::fs::create_dir(directory)?;
    let database = directory.join("report.db");
    let id = persist_fixture(&database, c, raw, false)?;
    // Reopen through production Storage rather than serializing a parallel
    // model representation. The actual CLI must agree with this saved view.
    let reopened = Storage::open(&database)?.load(&id)?;
    for (name, value) in [
        (
            "capture.json",
            serde_json::json!({"schema_version":3,"kind":"inspect","data":reopened}),
        ),
        ("ground-truth.json", serde_json::to_value(truth)?),
        (
            "metadata.json",
            serde_json::json!({"kind":"synthetic_report_fixture", "schema_version":1,
                "source":"existing historical model + production RawBuffer/Storage",
                "event_count":3, "real_etw":false}),
        ),
    ] {
        std::fs::write(directory.join(name), serde_json::to_vec_pretty(&value)?)?;
    }
    Ok(())
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
    fn report_fixture_uses_saved_views_and_preserves_historical_unknown_evidence() {
        let directory =
            std::env::temp_dir().join(format!("contain-report-model-{}", uuid::Uuid::new_v4()));
        let root = directory.join(format!("contain-demo-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(&root).unwrap();
        let marker = root.join(".contain-demo-marker");
        std::fs::write(&marker, b"Contain test fixture").unwrap();
        let installer = directory.join("fixture.exe");
        std::fs::write(&installer, b"SYNTHETIC test input, never executed").unwrap();
        let output = directory.join("output");
        let alias = root.to_str().unwrap();
        generate_report_fixture(&output, &root, alias, &installer).unwrap();
        let db = Storage::open(&output.join("report.db")).unwrap();
        let full = db.load("SyntheticReport").unwrap();
        let saved: serde_json::Value =
            serde_json::from_slice(&std::fs::read(output.join("capture.json")).unwrap()).unwrap();
        assert_eq!(saved["data"], serde_json::to_value(&full).unwrap());
        assert_eq!(full.events.len(), 3);
        assert_eq!(full.files.len(), 2);
        assert_eq!(full.events[0].confidence, Confidence::High);
        assert_eq!(
            full.events[0].evidence.process_image.as_deref(),
            Some(full.installer.as_str())
        );
        assert_eq!(full.events[1].confidence, Confidence::Unknown);
        assert!(full.events[2].raw.file_name.is_none());
        assert_eq!(
            full.events[0].resource,
            format!(r"{alias}\historical-deleted.tmp")
        );
        assert!(!root.join("historical-deleted.tmp").exists());
        assert!(
            full.files
                .iter()
                .all(|f| f.confidence == Confidence::Unknown)
        );
        assert!(full.events.iter().all(|e| !e.state_validated));
        let summary = db.load_summary(&full.id).unwrap();
        assert_eq!(render::summary_text(&summary), render::summary_text(&full));
        for (index, event) in full.events.iter().enumerate() {
            let page = db.events_page(&full.id, index as u64, 1).unwrap();
            assert_eq!(page[0].id, event.id);
            let focused = db.for_event(&event.id).unwrap();
            assert_eq!(
                render::explanation(&full, &event.id).unwrap(),
                render::explanation(&focused, &event.id).unwrap()
            );
        }
        let explanation = render::explain_text(&full, &full.events[0].id).unwrap();
        assert!(explanation.contains("historical"));
        assert!(
            !full.events[0]
                .raw
                .file_name
                .as_ref()
                .unwrap()
                .current_path_verified
        );
        assert_eq!(std::fs::read(&marker).unwrap(), b"Contain test fixture");
        assert!(generate_report_fixture(&output, &root, alias, &installer).is_err());
        drop(db);
        std::fs::write(&marker, b"wrong marker").unwrap();
        assert!(
            generate_report_fixture(&directory.join("refused"), &root, alias, &installer).is_err()
        );
        assert!(!directory.join("refused").exists());
        std::fs::remove_dir_all(directory).unwrap();
    }

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
                assert_eq!(full.files.len(), 1);
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
