//! Explicit disk-backed readback experiment; no ETW or accuracy claim.
use super::*;
use crate::model::{AttributionEvidence, EvidenceSource, NormalizedOperation};
use std::time::Instant;

fn fixture(id: &str, count: usize) -> Capture {
    let mut capture = Capture {
        id: id.into(),
        name: id.into(),
        schema_version: 3,
        ..Default::default()
    };
    for i in (0..count).rev() {
        let evidence = vec![AttributionEvidence {
            pid: Some(i as u32 + 1),
            process_creation_time: Some(134_000_000_000_000_000 + i as u64),
            source: EvidenceSource::EtwFile,
            ..Default::default()
        }];
        capture.files.push(FileChange {
            path: format!(r"C:\synthetic-readback\file-{i:05}.dat"),
            evidence: evidence.clone(),
            ..Default::default()
        });
        capture.registry.push(RegistryChange {
            key: r"HKCU\Software\Contain\SyntheticReadback".into(),
            name: format!("value-{i:05}"),
            evidence,
            ..Default::default()
        });
    }
    capture
}

fn verify(capture: &Capture, count: usize) {
    assert_eq!(capture.files.len(), count);
    assert_eq!(capture.registry.len(), count);
    for (i, (file, value)) in capture.files.iter().zip(&capture.registry).enumerate() {
        assert_eq!(file.evidence.len(), 1);
        assert_eq!(value.evidence.len(), 1);
        assert_eq!(file.evidence[0].pid, Some(i as u32 + 1));
        assert_eq!(value.evidence[0].pid, Some(i as u32 + 1));
        assert_eq!(file.confidence, Confidence::Unknown);
        assert_eq!(value.confidence, Confidence::Unknown);
    }
}

#[test]
fn state_evidence_readback_matches_exact_resources_after_database_ordering() {
    let mut db = Storage::open(Path::new(":memory:")).unwrap();
    db.save(&fixture("readback", 37)).unwrap();
    verify(&db.load_summary("readback").unwrap(), 37);
    verify(&db.load("readback").unwrap(), 37);
}

#[test]
fn existing_schema_four_gains_session_indexes_without_rewriting_rows() {
    let mut db = Storage::open(Path::new(":memory:")).unwrap();
    db.save(&fixture("readback", 37)).unwrap();
    db.connection.execute_batch("DROP INDEX system_events_session_resource; DROP INDEX normalized_operations_session_id;").unwrap();
    let db = Storage::from_connection(db.connection).unwrap();
    assert_eq!(migration::check_version(&db.connection).unwrap(), 4);
    verify(&db.load("readback").unwrap(), 37);
    for (sql, index) in [
        (
            "SELECT resource FROM system_events WHERE session_id=?1 ORDER BY resource",
            "system_events_session_resource",
        ),
        (
            "SELECT detail_json FROM normalized_operations WHERE session_id=?1 ORDER BY id",
            "normalized_operations_session_id",
        ),
    ] {
        let mut q = db
            .connection
            .prepare(&format!("EXPLAIN QUERY PLAN {sql}"))
            .unwrap();
        let plan: Vec<String> = q
            .query_map(["readback"], |r| r.get(3))
            .unwrap()
            .map(Result::unwrap)
            .collect();
        assert!(
            plan.iter()
                .any(|entry| entry.contains("SEARCH") && entry.contains(index)),
            "{plan:?}"
        );
        assert!(
            plan.iter().all(|entry| !entry.contains("TEMP B-TREE")),
            "{plan:?}"
        );
    }
}

#[test]
#[ignore = "explicit disk-backed readback scaling experiment"]
fn state_evidence_readback_scaling() {
    let output = std::env::var_os("CONTAIN_STORAGE_BENCHMARK_OUTPUT")
        .expect("set CONTAIN_STORAGE_BENCHMARK_OUTPUT to an absolute JSON output path");
    assert!(Path::new(&output).is_absolute());
    let mut samples = Vec::new();
    for count in [100, 1_000, 5_000, 10_000] {
        let path =
            std::env::temp_dir().join(format!("contain-readback-{}.db", uuid::Uuid::new_v4()));
        let mut db = Storage::open(&path).unwrap();
        let write_start = Instant::now();
        db.save(&fixture("readback", count)).unwrap();
        let save_ns = write_start.elapsed().as_nanos() as u64;
        let mut summary_ns = Vec::new();
        let mut full_ns = Vec::new();
        for round in 0..3 {
            // Alternate API order; verification is deliberately outside timing.
            for full in if round == 1 {
                [true, false]
            } else {
                [false, true]
            } {
                let start = Instant::now();
                let restored = if full {
                    db.load("readback").unwrap()
                } else {
                    db.load_summary("readback").unwrap()
                };
                let ns = start.elapsed().as_nanos() as u64;
                if full {
                    full_ns.push(ns);
                } else {
                    summary_ns.push(ns);
                }
                verify(&restored, count);
            }
        }
        drop(db);
        samples.push(serde_json::json!({
            "files": count, "registry_values": count, "save_ns": save_ns,
            "summary_ns": summary_ns, "full_ns": full_ns,
            "db_after_close_bytes": std::fs::metadata(&path).unwrap().len()
        }));
        for suffix in ["", "-wal", "-shm"] {
            let owned = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
            if owned.exists() {
                std::fs::remove_file(owned).unwrap();
            }
        }
    }
    let report = serde_json::json!({
        "schema_version": 1, "synthetic_only": true, "sqlite_version": rusqlite::version(),
        "trials": 3, "timing": "load/load_summary only; excludes fixture save, verification and connection close; warm-cache sequential samples",
        "samples": samples
    });
    std::fs::write(output, serde_json::to_vec_pretty(&report).unwrap()).unwrap();
    println!("READBACK: {report}");
}

#[test]
#[ignore = "explicit disk-backed multi-session readback experiment"]
fn history_database_readback_scaling() {
    let output = std::env::var_os("CONTAIN_STORAGE_BENCHMARK_OUTPUT")
        .expect("set CONTAIN_STORAGE_BENCHMARK_OUTPUT to an absolute JSON output path");
    assert!(Path::new(&output).is_absolute());
    let path = std::env::temp_dir().join(format!(
        "contain-history-readback-{}.db",
        uuid::Uuid::new_v4()
    ));
    let mut db = Storage::open(&path).unwrap();
    let mut samples = Vec::new();
    let mut saved_sessions = 0;
    db.save(&fixture("readback", 100)).unwrap();
    for history_sessions in [0, 10, 50] {
        let mut history_save_ns = Vec::new();
        while saved_sessions < history_sessions {
            let id = format!("history-{saved_sessions}");
            let mut prior = fixture(&id, 1_000);
            prior.operations = (0..1_000)
                .map(|i| NormalizedOperation {
                    id: format!("{id}-operation-{i}"),
                    resource: format!(r"C:\synthetic-readback\history-{i}.dat"),
                    operation: "UnresolvedRequest".into(),
                    ..Default::default()
                })
                .collect();
            let started = Instant::now();
            db.save(&prior).unwrap();
            history_save_ns.push(started.elapsed().as_nanos() as u64);
            saved_sessions += 1;
        }
        let mut summary_ns = Vec::new();
        let mut full_ns = Vec::new();
        for round in 0..3 {
            for full in if round == 1 {
                [true, false]
            } else {
                [false, true]
            } {
                let start = Instant::now();
                let restored = if full {
                    db.load("readback")
                } else {
                    db.load_summary("readback")
                }
                .unwrap();
                let ns = start.elapsed().as_nanos() as u64;
                if full {
                    full_ns.push(ns);
                } else {
                    summary_ns.push(ns);
                }
                verify(&restored, 100);
                assert!(restored.operations.is_empty());
            }
        }
        let pages: u64 = db
            .connection
            .pragma_query_value(None, "page_count", |r| r.get(0))
            .unwrap();
        let page_size: u64 = db
            .connection
            .pragma_query_value(None, "page_size", |r| r.get(0))
            .unwrap();
        samples.push(serde_json::json!({
            "history_sessions": history_sessions, "history_files": history_sessions * 1_000,
            "history_registry_values": history_sessions * 1_000,
            "history_normalized_operations": history_sessions * 1_000,
            "target_files": 100, "target_registry_values": 100,
            "database_logical_bytes": pages * page_size,
            "summary_ns": summary_ns, "full_ns": full_ns, "history_save_ns": history_save_ns
        }));
    }
    drop(db);
    let report = serde_json::json!({
        "schema_version": 1, "synthetic_only": true, "sqlite_version": rusqlite::version(),
        "trials": 3, "timing": "load/load_summary fixed 100-file/100-value target with unrelated history; excludes seed writes and verification; warm-cache samples",
        "samples": samples
    });
    std::fs::write(output, serde_json::to_vec_pretty(&report).unwrap()).unwrap();
    println!("HISTORY_READBACK: {report}");
    for suffix in ["", "-wal", "-shm"] {
        let owned = std::path::PathBuf::from(format!("{}{suffix}", path.display()));
        if owned.exists() {
            std::fs::remove_file(owned).unwrap();
        }
    }
}
