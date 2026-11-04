//! Deterministic, disk-backed production Storage::save benchmark.
//! Synthetic evidence only: this cannot measure or validate Windows ETW capture.
use anyhow::{Context, Result, bail, ensure};
use contain_offline::{model::*, storage::Storage};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, fs, path::Path, time::Instant};

const SEED: u64 = 0x434f_4e54_4149_4e31;
const TICKS: u64 = 134_000_000_000_000_000;
const SESSION: &str = "synthetic-storage-benchmark";

fn hash(bytes: impl AsRef<[u8]>) -> String {
    format!("{:x}", Sha256::digest(bytes.as_ref()))
}

fn fixture(count: usize) -> Capture {
    let mut capture = Capture {
        id: SESSION.into(),
        name: "Synthetic storage benchmark".into(),
        installer: r"C:\synthetic-only\fixture.exe".into(),
        started_at: "2026-01-01T00:00:00Z".into(),
        finished_at: "2026-01-01T00:01:00Z".into(),
        exit_code: Some(0),
        schema_version: 3,
        watch_roots: vec![r"C:\synthetic-only".into()],
        registry_key: Some(r"Software\Contain\SyntheticBenchmark".into()),
        warnings: vec!["Synthetic fixture; no installer or Windows provider was executed.".into()],
        backend: BackendReport {
            etw_file: "synthetic; not collected".into(),
            etw_registry: "not collected".into(),
            etw_process: "synthetic; not collected".into(),
            ..Default::default()
        },
        quality: CaptureQuality {
            level: QualityLevel::Degraded,
            reasons: vec!["Offline synthetic evidence does not establish capture coverage.".into()],
        },
        ..Default::default()
    };
    let mut random = SEED;
    for i in 0..count {
        // Fixed LCG input data, not OS randomness or historical capture reconstruction.
        random = random.wrapping_mul(6364136223846793005).wrapping_add(1);
        let id = format!("synthetic-event-{i:08}");
        let path = format!(r"C:\synthetic-only\file-{i:08}.dat");
        let pid = 1000 + (i % 4) as u32;
        let birth = TICKS - 100 + u64::from(pid);
        let known = i % 7 != 0;
        let evidence = AttributionEvidence {
            source: EvidenceSource::EtwFile,
            pid: known.then_some(pid),
            process_creation_time: known.then_some(birth),
            process_image: known.then(|| capture.installer.clone()),
            session_id: known.then(|| SESSION.into()),
            rule: if known {
                AttributionRule::DescendantProcess
            } else {
                AttributionRule::MissingWriter
            },
            ..Default::default()
        };
        let actor = if known {
            Confidence::High
        } else {
            Confidence::Unknown
        };
        capture.events.push(SystemEvent {
            id: id.clone(),
            sequence: i as u64 + 1,
            timestamp: "2026-01-01T00:00:01Z".into(),
            timestamp_ticks: TICKS + 2000 + i as u64,
            event_type: "file".into(),
            operation: "write_requested".into(),
            resource: path.clone(),
            confidence: actor,
            reason: "Synthetic actor evidence; no application API intent or ownership claim."
                .into(),
            evidence,
            raw: RawEvidence {
                file_name: Some(Box::new(FileNameEvidence {
                    path_source: if i % 3 == 0 {
                        "file_object_cache"
                    } else {
                        "supplied_FileName"
                    }
                    .into(),
                    observed_ticks: TICKS + 2000 + i as u64,
                    source_event_id: id.clone(),
                    current_path_verified: false,
                })),
                object_generation: Some(format!("synthetic-generation-{i}")),
                header_pid: Some(pid),
                resource_resolved: true,
                ..Default::default()
            },
            dimensions: ConfidenceDimensions {
                actor,
                resource: Confidence::High,
                operation: Confidence::Unknown,
            },
            ..Default::default()
        });
        capture.files.push(FileChange {
            path: path.clone(),
            operation: "created".into(),
            after_hash: Some(format!("synthetic-{random:016x}")),
            after_size: Some(random % 65536),
            reason: "Synthetic state difference; target ownership remains Unknown.".into(),
            evidence: vec![AttributionEvidence::default()],
            ..Default::default()
        });
        capture.registry.push(RegistryChange {
            key: r"HKCU\Software\Contain\SyntheticBenchmark".into(),
            name: format!("value-{i:08}"),
            operation: "created".into(),
            after_value: Some(format!("synthetic-{random:016x}")),
            reason: "Synthetic state difference; writer unavailable.".into(),
            evidence: vec![AttributionEvidence::default()],
            ..Default::default()
        });
        capture.operations.push(NormalizedOperation {
            id: format!("synthetic-operation-{i:08}"),
            operation: "UnresolvedRequest".into(),
            resource: path.clone(),
            raw_events: vec![id.clone()],
            confidence: actor,
            reason: "Synthetic request without completion; outcome unavailable.".into(),
            ..Default::default()
        });
        capture.edges.push(EvidenceEdge {
            from: format!("event:{id}"),
            to: format!("resource:synthetic-generation-{i}"),
            relation: "observed_resource".into(),
            confidence: Confidence::High,
            reason: "Synthetic recorded name, not verified current path.".into(),
        });
        if known {
            capture.edges.push(EvidenceEdge {
                from: format!("process:{pid}:{birth}"),
                to: format!("event:{id}"),
                relation: "actor".into(),
                confidence: Confidence::High,
                reason: "Synthetic exact process instance.".into(),
            });
        }
    }
    for pid in 1000..1004 {
        let birth = TICKS - 100 + u64::from(pid);
        capture.processes.push(ProcessRecord {
            pid,
            creation_time: Some(birth),
            image: capture.installer.clone(),
            confidence: Confidence::High,
            reason: "Synthetic exact process instance.".into(),
            ..Default::default()
        });
        capture.edges.push(EvidenceEdge {
            from: format!("session:{SESSION}"),
            to: format!("process:{pid}:{birth}"),
            relation: "process".into(),
            confidence: Confidence::High,
            reason: "Synthetic process association.".into(),
        });
    }
    capture.stats.events_received = count as u64;
    capture.stats.events_retained = count as u64;
    capture.stats.events_normalized = count as u64;
    capture.stats.unknown_actor_events = count.div_ceil(7) as u64;
    capture.stats.known_actor_events = count as u64 - capture.stats.unknown_actor_events;
    capture.stats.known_resource_events = count as u64;
    // Non-SQL ordering catches accidental reliance on insert order during readback.
    capture.files.reverse();
    capture.registry.reverse();
    capture
}

fn canonical(capture: &Capture) -> Result<Value> {
    let mut capture = capture.clone();
    capture.stats.phase_ms.remove("final_persistence_commit");
    capture.files.sort_by(|a, b| a.path.cmp(&b.path));
    capture
        .registry
        .sort_by(|a, b| (&a.key, &a.name).cmp(&(&b.key, &b.name)));
    capture.processes.sort_by_key(|p| (p.creation_time, p.pid));
    capture.events.sort_by(|a, b| {
        (a.timestamp_ticks, a.sequence, &a.id).cmp(&(b.timestamp_ticks, b.sequence, &b.id))
    });
    capture.operations.sort_by(|a, b| a.id.cmp(&b.id));
    capture
        .edges
        .sort_by(|a, b| (&a.from, &a.to, &a.relation).cmp(&(&b.from, &b.to, &b.relation)));
    Ok(serde_json::to_value(capture)?)
}

fn sizes(path: &Path) -> Value {
    let size = |suffix: &str| {
        fs::metadata(format!("{}{suffix}", path.display()))
            .ok()
            .map(|m| m.len())
    };
    json!({"db_bytes": size(""), "wal_bytes": size("-wal"), "shm_bytes": size("-shm")})
}

fn measure<T>(
    timings: &mut BTreeMap<String, u64>,
    name: &str,
    action: impl FnOnce() -> Result<T>,
) -> Result<T> {
    let start = Instant::now();
    let result = action();
    timings.insert(name.into(), start.elapsed().as_nanos() as u64);
    result
}

fn sample(path: &Path, input: &Capture, result: &mut Value) -> Result<()> {
    ensure!(
        !path.exists(),
        "sample database already exists; refusing to overwrite"
    );
    let expected = canonical(input)?;
    let mut timings = BTreeMap::new();
    let work = (|| -> Result<()> {
        let mut db = Storage::open(path)?;
        measure(&mut timings, "save_ns", || db.save(input))?;
        result["after_save"] = sizes(path);
        let summary = measure(&mut timings, "summary_ns", || db.load_summary(SESSION))?;
        let mut projected = input.clone();
        projected.events.clear();
        projected.operations.clear();
        projected.edges.clear();
        ensure!(
            canonical(&summary)? == canonical(&projected)?,
            "summary differs from input projection"
        );
        let full = measure(&mut timings, "full_ns", || db.load(SESSION))?;
        ensure!(
            canonical(&full)? == expected,
            "full read differs from input evidence"
        );
        let pages = measure(&mut timings, "all_pages_ns", || {
            let mut events = Vec::new();
            loop {
                let page = db.events_page(SESSION, events.len() as u64, 127)?;
                if page.is_empty() {
                    break;
                }
                events.extend(page);
            }
            Ok(events)
        })?;
        ensure!(
            serde_json::to_value(&pages)? == serde_json::to_value(&full.events)?,
            "paged history differs from full history"
        );
        let indices = [0, input.events.len() / 2, input.events.len() - 1];
        let explanations = measure(&mut timings, "three_explanations_ns", || {
            indices
                .iter()
                .map(|&i| db.for_event(&input.events[i].id))
                .collect::<Result<Vec<_>>>()
        })?;
        for (&i, explanation) in indices.iter().zip(&explanations) {
            let event = &input.events[i];
            ensure!(
                explanation.events.len() == 1
                    && serde_json::to_value(&explanation.events[0])?
                        == serde_json::to_value(event)?,
                "focused explanation event mismatch"
            );
            ensure!(
                explanation.operations.len() == 1
                    && serde_json::to_value(&explanation.operations[0])?
                        == serde_json::to_value(&input.operations[i])?,
                "focused explanation operation mismatch"
            );
            let event_node = format!("event:{}", event.id);
            let mut expected_edges: Vec<_> = input
                .edges
                .iter()
                .filter(|e| e.from == event_node || e.to == event_node)
                .cloned()
                .collect();
            let parents: Vec<_> = expected_edges
                .iter()
                .filter(|e| e.to == event_node)
                .map(|e| e.from.clone())
                .collect();
            expected_edges.extend(
                input
                    .edges
                    .iter()
                    .filter(|e| parents.contains(&e.to))
                    .cloned(),
            );
            let mut expected_explanation = explanation.clone();
            expected_explanation.edges = expected_edges;
            ensure!(
                canonical(explanation)? == canonical(&expected_explanation)?,
                "focused explanation graph mismatch"
            );
        }
        ensure!(
            db.list()?
                == vec![(
                    input.id.clone(),
                    input.name.clone(),
                    input.started_at.clone()
                )],
            "list does not identify saved session"
        );
        drop(db);
        result["after_close"] = sizes(path);
        let reopened = Storage::open(path)?;
        let restored = measure(&mut timings, "reopened_full_ns", || reopened.load(SESSION))?;
        ensure!(
            canonical(&restored)? == expected,
            "reopened evidence differs from original"
        );
        ensure!(
            canonical(&reopened.load_summary(SESSION)?)? == canonical(&projected)?,
            "reopened summary differs"
        );
        let first_page = reopened.events_page(SESSION, 0, 127)?;
        ensure!(
            serde_json::to_value(&first_page)? == serde_json::to_value(&pages[..first_page.len()])?,
            "reopened page differs"
        );
        ensure!(
            canonical(&reopened.for_event(&input.events[0].id)?)? == canonical(&explanations[0])?,
            "reopened explain differs"
        );
        result["after_reopen"] = sizes(path);
        result["semantic_sha256"] = hash(serde_json::to_vec(&expected)?).into();
        result["counts"] = json!({"events":full.events.len(),"files":full.files.len(),"registry_values":full.registry.len(),"processes":full.processes.len(),"operations":full.operations.len(),"edges":full.edges.len(),"paged_events":pages.len(),"explanations_checked":explanations.len()});
        Ok(())
    })();
    result["timings"] = serde_json::to_value(timings)?;
    result["final_files"] = sizes(path);
    work
}

fn main() -> Result<()> {
    let mut args = std::env::args().skip(1);
    let mut output = None;
    let mut revision = None;
    let mut records = 5000;
    let mut samples = 3;
    while let Some(arg) = args.next() {
        let value = args
            .next()
            .with_context(|| format!("missing value for {arg}"))?;
        match arg.as_str() {
            "--output" => output = Some(std::path::PathBuf::from(value)),
            "--revision" => revision = Some(value),
            "--records" => records = value.parse::<usize>()?,
            "--samples" => samples = value.parse::<usize>()?,
            _ => bail!("unknown argument {arg}"),
        }
    }
    let output=output.context("use --output <new-directory> --revision <compiled-source-sha> [--records 5000] [--samples 3]")?;
    let revision =
        revision.context("--revision must identify the source of this compiled binary")?;
    ensure!(
        records > 0 && records <= 100_000 && samples > 0 && samples <= 100,
        "records must be 1..100000 and samples 1..100"
    );
    ensure!(
        revision.len() == 40 && revision.bytes().all(|b| b.is_ascii_hexdigit()),
        "--revision requires a full commit SHA"
    );
    if let Some(parent) = output.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::create_dir(&output).context("use a new output directory; previous samples are retained")?;
    let input = fixture(records);
    let mut report = json!({"format_version":1,"synthetic_only":true,"source_revision":revision,"binary_sha256":hash(fs::read(std::env::current_exe()?)?),"input_sha256":hash(serde_json::to_vec(&input)?),"seed":SEED.to_string(),"os":std::env::consts::OS,"architecture":std::env::consts::ARCH,"sqlite_version":rusqlite::version(),"records":records,"requested_samples":samples,"timing_scope":"Storage APIs only; fixture generation, verification, connection open and close excluded; all_pages and three_explanations include collecting return values","storage_scope":"Storage::save transaction and its existing journal/durability policy; no raw ingest or ETW; absent WAL/SHM files are null, not sampled peaks","resource_metrics":{"cpu_ns":null,"peak_rss_bytes":null,"read_bytes":null,"write_bytes":null},"samples":[]});
    let mut failed = 0;
    for i in 0..samples {
        let mut row = json!({"sample":i+1,"status":"running"});
        let path = output.join(format!("synthetic-sample-{}.db", i + 1));
        match sample(&path, &input, &mut row) {
            Ok(()) => row["status"] = "passed".into(),
            Err(error) => {
                failed += 1;
                row["status"] = "failed".into();
                row["error"] = format!("{error:#}").into();
            }
        }
        report["samples"].as_array_mut().unwrap().push(row);
        report["failed_samples"] = failed.into();
        fs::write(
            output.join("results.json"),
            serde_json::to_vec_pretty(&report)?,
        )?;
    }
    println!("{}", serde_json::to_string_pretty(&report)?);
    ensure!(
        failed == 0,
        "{failed} sample(s) failed; all results and databases retained"
    );
    Ok(())
}
