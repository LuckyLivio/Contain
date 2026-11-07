//! Read-only controlled-fixture diagnostic. Not part of the desktop package.
//! Replays retained source facts with the production resolver; never changes a
//! stored event or substitutes fixture truth for an identity observation.
use anyhow::{Result, ensure};
use contain_core::{attribution, lifetime::LifetimeCache, model::*, windows::native};
use rusqlite::{Connection, OpenFlags, params};
use serde_json::{Value, json};
use std::{
    collections::{BTreeMap, BTreeSet},
    fs::{self, File},
    io::{BufRead, BufReader},
    path::Path,
};

const INPUT_BYTES: u64 = 64 * 1024 * 1024;
const TARGET_LIMIT: usize = 32;

fn read_json(path: &Path) -> Result<Value> {
    ensure!(fs::metadata(path)?.len() <= INPUT_BYTES, "input_bound");
    Ok(serde_json::from_reader(BufReader::new(File::open(path)?))?)
}

fn decimal(value: &Value) -> Result<u64> {
    let text = value
        .as_str()
        .ok_or_else(|| anyhow::anyhow!("ticks_type"))?;
    ensure!(
        !text.is_empty() && text.bytes().all(|b| b.is_ascii_digit()),
        "ticks_type"
    );
    Ok(text.parse()?)
}

fn valid_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 100
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
}

fn page(db: &Connection, session: &str, cursor: (u64, u64)) -> Result<Vec<SystemEvent>> {
    let mut query = db.prepare_cached("SELECT document FROM raw_stream WHERE session_id=?1 AND (ticks,sequence)>(?2,?3) ORDER BY ticks,sequence LIMIT 256")?;
    query
        .query_map(params![session, cursor.0, cursor.1], |row| {
            row.get::<_, String>(0)
        })?
        .map(|row| Ok(serde_json::from_str(&row?)?))
        .collect()
}

fn is_direct(process: &ProcessRecord, root: (u32, u64)) -> bool {
    (process.pid == root.0
        && process.creation_time == Some(root.1)
        && process.confidence == Confidence::Certain)
        || (process.parent_pid == Some(root.0)
            && process.parent_creation_time == Some(root.1)
            && process.confidence == Confidence::High)
}

fn actor(event: &SystemEvent) -> Value {
    json!({"pid":event.evidence.pid,
        "creation_time":event.evidence.process_creation_time.map(|v|v.to_string()),
        "parent_pid":event.evidence.parent_pid,"confidence":event.confidence,
        "rule":event.evidence.rule,
        "resource_resolved":event.raw.resource_resolved,"completion":event.success})
}

fn resolve(
    cache: &LifetimeCache,
    event: &SystemEvent,
    session: &str,
    root: (u32, u64),
) -> SystemEvent {
    let mut resolved = event.clone();
    cache.resolve_event(&mut resolved);
    let processes = cache
        .processes
        .values()
        .filter(|p| is_direct(p, root))
        .cloned()
        .collect::<Vec<_>>();
    attribution::attribute(&mut resolved, &processes, session);
    resolved
}

fn completion(db: &Connection, session: &str, request: &str) -> Result<Value> {
    // LIMIT 3 distinguishes a unique join from duplicates with a fixed memory
    // bound. The production join refuses every non-unique result.
    let mut query = db.prepare_cached(
        "SELECT document FROM raw_stream WHERE session_id=?1 AND related=?2 LIMIT 3",
    )?;
    let events: Vec<SystemEvent> = query
        .query_map(params![session, request], |row| row.get::<_, String>(0))?
        .map(|row| Ok(serde_json::from_str(&row?)?))
        .collect::<Result<_>>()?;
    Ok(
        json!({"matching_records_capped":events.len(),"count_capped_at":3,
            "production_join_unique":events.len()==1,
            "basis":if events.len()==1 {"retained_scoped_request_generation_link"} else if events.is_empty() {"no_retained_completion"} else {"ambiguous_multiple_completions"},
            "records":events.iter().map(|e|json!({"event_id":e.id,"related_event":e.raw.related_event,
                "timestamp_ticks":e.timestamp_ticks.to_string(),"success":e.success,"status":e.raw.status})).collect::<Vec<_>>()
        }),
    )
}

fn lifecycle_facts(
    db: &Connection,
    session: &str,
    raw: &SystemEvent,
    oracle_pid: u64,
) -> Result<Value> {
    let mut query = db.prepare_cached("SELECT document FROM raw_stream WHERE session_id=?1 AND kind='lifecycle' ORDER BY ticks,sequence")?;
    let mut facts = Vec::new();
    let mut matching = 0u64;
    for row in query.query_map([session], |row| row.get::<_, String>(0))? {
        let event: SystemEvent = serde_json::from_str(&row?)?;
        let process_fact = matches!(event.operation.as_str(), "process_start" | "process_stop")
            && event.evidence.pid.map(u64::from) == Some(oracle_pid);
        let thread_fact = matches!(event.operation.as_str(), "thread_start" | "thread_stop")
            && raw.raw.thread_id.is_some()
            && event.raw.thread_id == raw.raw.thread_id;
        if process_fact || thread_fact {
            matching += 1;
            if facts.len() < 16 {
                facts.push(json!({"event_id":event.id,"operation":event.operation,
                    "timestamp_ticks":event.timestamp_ticks.to_string(),"pid":event.evidence.pid,
                    "creation_time":event.evidence.process_creation_time.map(|v|v.to_string()),
                    "parent_pid":event.evidence.parent_pid,"thread_id":event.raw.thread_id}));
            }
        }
    }
    Ok(
        json!({"matching_records":matching,"sample_limit":16,"omitted":matching.saturating_sub(16),"records":facts}),
    )
}

fn truth(directory: &Path, expected: &Value) -> Result<BTreeMap<String, Value>> {
    let mut rows = BTreeMap::new();
    let mut files = 0;
    let mut bytes = 0u64;
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let filename = entry.file_name();
        let filename = filename.to_string_lossy();
        if !filename.starts_with("ground-truth-") || !filename.ends_with(".jsonl") {
            continue;
        }
        files += 1;
        bytes = bytes.saturating_add(entry.metadata()?.len());
        ensure!(files <= 16 && bytes <= INPUT_BYTES, "truth_bound");
        for line in BufReader::new(File::open(entry.path())?).lines() {
            let line = line?;
            ensure!(line.len() <= 256 * 1024, "truth_record_bound");
            let row: Value = serde_json::from_str(&line)?;
            for label in ["parent", "child"] {
                let target = expected["targets"]
                    .as_array()
                    .and_then(|rows| rows.iter().find(|t| t["label"] == label))
                    .ok_or_else(|| anyhow::anyhow!("target_missing"))?;
                if row["role"] == target["role"]
                    && row["operation"] == "write_requested"
                    && row["success"] == true
                    && row["resource"]
                        .as_str()
                        .zip(target["path"].as_str())
                        .is_some_and(|(a, b)| {
                            native::normalize_path(a, &[]) == native::normalize_path(b, &[])
                        })
                {
                    ensure!(!rows.contains_key(label), "ambiguous_truth");
                    decimal(&row["creation_time"])?;
                    ensure!(
                        decimal(&row["start_ticks"])? <= decimal(&row["end_ticks"])?,
                        "truth_interval"
                    );
                    ensure!(
                        row["pid"]
                            .as_u64()
                            .is_some_and(|pid| pid > 0 && pid <= u32::MAX.into()),
                        "truth_pid"
                    );
                    rows.insert(label.into(), row.clone());
                }
            }
        }
    }
    ensure!(rows.len() == 2, "truth_missing");
    Ok(rows)
}

fn diagnose(args: &BTreeMap<String, String>) -> Result<Value> {
    let get = |key: &str| {
        args.get(key)
            .ok_or_else(|| anyhow::anyhow!("argument_missing"))
    };
    let session = get("--session")?;
    ensure!(valid_id(session), "session_invalid");
    let capture_doc = read_json(Path::new(get("--capture")?))?;
    ensure!(
        capture_doc["schema_version"] == 3 && capture_doc["kind"] == "inspect",
        "capture_document"
    );
    let capture: Capture = serde_json::from_value(capture_doc["data"].clone())?;
    ensure!(
        capture.id == *session && capture.capture_state.as_deref() == Some("finished"),
        "capture_session"
    );
    let expected = read_json(Path::new(get("--expected")?))?;
    let selection = read_json(Path::new(get("--selection")?))?;
    ensure!(selection["session_id"] == *session, "selection_session");
    let ids = selection["candidate_event_ids"]
        .as_array()
        .ok_or_else(|| anyhow::anyhow!("selection_ids"))?;
    ensure!(ids.len() <= 3 * TARGET_LIMIT, "selection_bound");
    let ids: BTreeSet<String> = ids
        .iter()
        .map(|v| {
            let id = v.as_str().ok_or_else(|| anyhow::anyhow!("selection_id"))?;
            ensure!(valid_id(id), "selection_id");
            Ok(id.to_owned())
        })
        .collect::<Result<_>>()?;
    let truth = truth(Path::new(get("--truth")?), &expected)?;
    let db = Connection::open_with_flags(
        Path::new(get("--db")?),
        OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
    )?;
    db.execute_batch("PRAGMA query_only=ON; BEGIN DEFERRED;")?;
    let state: String = db.query_row(
        "SELECT state FROM capture_runs WHERE session_id=?1",
        [session],
        |r| r.get(0),
    )?;
    ensure!(state == "finished", "database_session");
    let mut cache = LifetimeCache::default();
    let seed_processes: Vec<_> = capture
        .processes
        .iter()
        .filter(|p| p.evidence.source == EvidenceSource::ProcessApi)
        .cloned()
        .collect();
    let roots: Vec<_> = seed_processes
        .iter()
        .filter(|p| {
            p.confidence == Confidence::Certain
                && p.evidence.rule == AttributionRule::InstallerPid
                && p.creation_time.is_some()
        })
        .collect();
    ensure!(roots.len() == 1, "owned_root_missing_or_ambiguous");
    let root = (roots[0].pid, roots[0].creation_time.unwrap());
    for process in &seed_processes {
        cache.seed(process.clone());
    }
    let mut cursor = (0, 0);
    let mut replayed = 0u64;
    let mut raw_targets = BTreeMap::new();
    loop {
        let events = page(&db, session, cursor)?;
        if events.is_empty() {
            break;
        }
        for event in &events {
            cache.ingest(event);
            if ids.contains(&event.id)
                && event.event_type == "file"
                && event.operation == "write_requested"
            {
                ensure!(raw_targets.len() < 3 * TARGET_LIMIT, "raw_target_bound");
                raw_targets.insert(event.id.clone(), event.clone());
            }
        }
        let last = events.last().unwrap();
        cursor = (last.timestamp_ticks, last.sequence);
        replayed = replayed.saturating_add(events.len() as u64);
        ensure!(replayed <= 1_000_000, "replay_record_bound");
    }
    cache.attach(root, session);
    let mut targets = Vec::new();
    for label in ["parent", "child"] {
        let answer = expected["targets"]
            .as_array()
            .unwrap()
            .iter()
            .find(|t| t["label"] == label)
            .unwrap();
        let path = answer["path"]
            .as_str()
            .ok_or_else(|| anyhow::anyhow!("target_path"))?;
        let oracle = &truth[label];
        let mut events: Vec<_> = raw_targets
            .values()
            .filter(|e| {
                native::normalize_path(&e.resource, &[]) == native::normalize_path(path, &[])
            })
            .collect();
        let oracle_start = decimal(&oracle["start_ticks"])?;
        let oracle_end = decimal(&oracle["end_ticks"])?;
        events.sort_by_key(|e| {
            (
                !(oracle_start <= e.timestamp_ticks && e.timestamp_ticks <= oracle_end),
                e.timestamp_ticks,
                &e.id,
            )
        });
        let Some(raw) = events.first() else {
            targets.push(json!({"label":label,"status":"no_retained_target_write","retained_write_requests":0}));
            continue;
        };
        let final_event = capture
            .events
            .iter()
            .find(|e| e.id == raw.id)
            .ok_or_else(|| anyhow::anyhow!("final_target_missing"))?;
        let resolved = resolve(&cache, raw, session, root);
        let process = resolved
            .evidence
            .pid
            .zip(resolved.evidence.process_creation_time)
            .and_then(|key| cache.processes.get(&key));
        let oracle_match = resolved.evidence.pid.map(u64::from) == oracle["pid"].as_u64()
            && resolved.evidence.process_creation_time == Some(decimal(&oracle["creation_time"])?);
        let oracle_interval = decimal(&oracle["start_ticks"])? <= raw.timestamp_ticks
            && raw.timestamp_ticks <= decimal(&oracle["end_ticks"])?;
        let identity_available =
            resolved.confidence == Confidence::High && oracle_match && oracle_interval;
        let lifecycle_complete = capture.backend.etw_process == "active"
            && capture.backend.etw_events_lost == Some(0)
            && capture.backend.etw_buffers_lost == Some(0)
            && !capture.backend.has_loss()
            && cache.dropped == 0;
        targets.push(json!({"label":label,"event_id":raw.id,"retained_write_requests":events.len(),
            "timestamp_ticks":raw.timestamp_ticks.to_string(),"issuing_thread_id":raw.raw.thread_id,
            "source_event_id":raw.raw.event_id,
            "header_pid_diagnostic_only":raw.raw.header_pid,
            "request_generation_present":raw.raw.irp.is_some(),
            "oracle":{"pid":oracle["pid"],"creation_time":oracle["creation_time"],"start_ticks":oracle["start_ticks"],"end_ticks":oracle["end_ticks"]},
            "raw_actor":actor(raw),"final_actor":actor(final_event),"retained_evidence_dry_run":actor(&resolved),
            "dry_run_reason":resolved.reason,
            "dry_run_process":process.map(|p|json!({"pid":p.pid,"creation_time":p.creation_time.map(|v|v.to_string()),
                "parent_pid":p.parent_pid,"parent_creation_time":p.parent_creation_time.map(|v|v.to_string()),
                "ended_at":p.ended_at.map(|v|v.to_string()),"confidence":p.confidence,"rule":p.evidence.rule,
                "image_matches":resolved.evidence.process_image.as_ref().is_some_and(|image|native::normalize_path(image,&[])==native::normalize_path(&p.image,&[]))})),
            "oracle_actor_matches":oracle_match,"oracle_syscall_interval_contains_event":oracle_interval,
            "direct_parent_instance_matches":process.is_some_and(|p|label=="parent" && (p.pid,p.creation_time.unwrap_or(0))==root
                || label=="child" && p.parent_pid==Some(root.0) && p.parent_creation_time==Some(root.1)),
            "production_lifetime_resolution_applied":lifecycle_complete,
            "continuity_promotion_suppressed":!capture.backend.source_intact(),
            "classification":if identity_available && !capture.backend.source_intact() {"retained_identity_available_but_session_loss_suppresses_promotion"}
                else if identity_available {"retained_identity_available"} else {"identity_unresolved_or_mismatched_with_retained_evidence"},
            "supporting_retained_lifecycle":lifecycle_facts(&db,session,raw,oracle["pid"].as_u64().unwrap())?,
            "completion":completion(&db,session,&raw.id)?
        }));
    }
    Ok(
        json!({"schema_version":1,"protocol":"contain-follow-identity-diagnostic-1","session_id":session,
        "complete":true,"read_only":true,"fixture_truth_used_as_identity_input":false,"stored_events_modified":false,
        "replayed_retained_raw_records":replayed,"replay_cache_dropped":cache.dropped,
        "seeded_saved_process_api_records":seed_processes.len(),
        "initial_snapshot_facts":"unavailable_not_reconstructed_from_truth",
        "dry_run_scope":"saved_process_api_and_retained_raw_lifecycle_only; does_not_override_final_continuity_rule",
        "targets":targets}),
    )
}

fn run() -> Result<()> {
    let values: Vec<_> = std::env::args().skip(1).collect();
    ensure!(values.len() == 12, "arguments");
    let mut args = BTreeMap::new();
    for pair in values.as_chunks::<2>().0 {
        ensure!(
            [
                "--db",
                "--session",
                "--capture",
                "--expected",
                "--truth",
                "--selection"
            ]
            .contains(&pair[0].as_str()),
            "argument"
        );
        ensure!(
            args.insert(pair[0].clone(), pair[1].clone()).is_none(),
            "duplicate_argument"
        );
    }
    println!("{}", serde_json::to_string_pretty(&diagnose(&args)?)?);
    Ok(())
}

fn main() {
    if run().is_err() {
        // Private input names, JSON values, SQL messages and paths stay local.
        eprintln!(
            "Read-only identity diagnostic failed; no source identity conclusion was produced."
        );
        std::process::exit(1);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn root() -> ProcessRecord {
        ProcessRecord {
            pid: 10,
            creation_time: Some(100),
            image: r"C:\controlled\fixture.exe".into(),
            confidence: Confidence::Certain,
            ended_at: Some(300),
            evidence: AttributionEvidence {
                source: EvidenceSource::ProcessApi,
                rule: AttributionRule::InstallerPid,
                ..Default::default()
            },
            ..Default::default()
        }
    }

    fn request() -> SystemEvent {
        SystemEvent {
            id: "target".into(),
            timestamp_ticks: 200,
            event_type: "file".into(),
            operation: "write_requested".into(),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(11),
                resource_resolved: true,
                ..Default::default()
            },
            ..Default::default()
        }
    }

    #[test]
    fn retained_actor_can_resolve_while_source_loss_still_forbids_promotion() {
        let mut cache = LifetimeCache::default();
        cache.seed(root());
        cache.ingest(&SystemEvent {
            operation: "thread_start".into(),
            timestamp_ticks: 150,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwProcess,
                pid: Some(10),
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(11),
                ..Default::default()
            },
            ..Default::default()
        });
        cache.attach((10, 100), "session");
        let raw = request();
        let resolved = resolve(&cache, &raw, "session", (10, 100));
        assert_eq!(resolved.confidence, Confidence::High);
        assert_eq!(resolved.evidence.pid, Some(10));
        assert_eq!(resolved.evidence.process_creation_time, Some(100));
        let backend = BackendReport {
            dropped_events: 1,
            etw_events_lost: Some(0),
            etw_buffers_lost: Some(0),
            ..Default::default()
        };
        assert!(!backend.source_intact());
        assert_eq!(
            raw.evidence.pid, None,
            "dry run never modifies original evidence"
        );
    }

    #[test]
    fn missing_thread_interval_cannot_be_replaced_by_root_or_fixture_truth() {
        let mut cache = LifetimeCache::default();
        cache.seed(root());
        let resolved = resolve(&cache, &request(), "session", (10, 100));
        assert_eq!(resolved.confidence, Confidence::Unknown);
        assert_eq!(resolved.evidence.pid, None);
        assert_eq!(resolved.evidence.rule, AttributionRule::MissingWriter);
    }

    #[test]
    fn child_needs_retained_parent_instance_and_issuing_thread_interval() {
        let mut cache = LifetimeCache::default();
        cache.seed(root());
        cache.ingest(&SystemEvent {
            operation: "process_start".into(),
            timestamp_ticks: 120,
            resource: root().image,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwProcess,
                pid: Some(20),
                process_creation_time: Some(120),
                parent_pid: Some(10),
                ..Default::default()
            },
            ..Default::default()
        });
        cache.ingest(&SystemEvent {
            operation: "thread_start".into(),
            timestamp_ticks: 150,
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwProcess,
                pid: Some(20),
                ..Default::default()
            },
            raw: RawEvidence {
                thread_id: Some(11),
                ..Default::default()
            },
            ..Default::default()
        });
        cache.attach((10, 100), "session");
        let resolved = resolve(&cache, &request(), "session", (10, 100));
        assert_eq!(resolved.confidence, Confidence::High);
        assert_eq!(resolved.evidence.pid, Some(20));
        assert_eq!(resolved.evidence.process_creation_time, Some(120));
        let child = &cache.processes[&(20, 120)];
        assert_eq!(child.parent_pid, Some(10));
        assert_eq!(child.parent_creation_time, Some(100));
        assert_eq!(resolved.evidence.rule, AttributionRule::DescendantProcess);
    }

    #[test]
    fn duplicate_completions_remain_ambiguous_and_other_sessions_are_ignored() {
        let db = Connection::open_in_memory().unwrap();
        db.execute_batch("CREATE TABLE raw_stream(session_id TEXT,related TEXT,document TEXT);")
            .unwrap();
        for (session, id, status) in [
            ("wanted", "one", 0),
            ("other", "foreign", 1),
            ("wanted", "two", 0),
        ] {
            let event = SystemEvent {
                id: id.into(),
                success: Some(status == 0),
                raw: RawEvidence {
                    related_event: Some("target".into()),
                    status: Some(status),
                    ..Default::default()
                },
                ..Default::default()
            };
            db.execute(
                "INSERT INTO raw_stream VALUES (?1,'target',?2)",
                params![session, serde_json::to_string(&event).unwrap()],
            )
            .unwrap();
        }
        let result = completion(&db, "wanted", "target").unwrap();
        assert_eq!(result["matching_records_capped"], 2);
        assert_eq!(result["production_join_unique"], false);
        assert_eq!(result["basis"], "ambiguous_multiple_completions");
    }

    #[test]
    fn exact_session_diagnostic_preserves_database_and_omits_private_paths() {
        let directory =
            std::env::temp_dir().join(format!("contain-identity-test-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&directory).unwrap();
        let session = "diagnostic-session";
        let mut parent = request();
        parent.id = "parent-write".into();
        parent.resource = r"C:\controlled-private\parent-target.txt".into();
        parent.sequence = 2;
        let mut child = parent.clone();
        child.id = "child-write".into();
        child.resource = r"C:\controlled-private\child-target.txt".into();
        child.sequence = 3;
        // Deliberately no thread facts: fixture identities must not fill the gap.
        let mut capture = Capture {
            id: session.into(),
            schema_version: 3,
            capture_state: Some("finished".into()),
            processes: vec![root()],
            events: vec![parent.clone(), child.clone()],
            backend: BackendReport {
                dropped_events: 1,
                etw_process: "active".into(),
                etw_events_lost: Some(0),
                etw_buffers_lost: Some(0),
                ..Default::default()
            },
            ..Default::default()
        };
        for event in &mut capture.events {
            event.evidence.rule = AttributionRule::EventLoss;
        }
        let database = directory.join("private.db");
        let db = Connection::open(&database).unwrap();
        db.execute_batch("CREATE TABLE capture_runs(session_id TEXT,state TEXT); CREATE TABLE raw_stream(session_id TEXT,sequence INTEGER,ticks INTEGER,kind TEXT,related TEXT,document TEXT);").unwrap();
        db.execute("INSERT INTO capture_runs VALUES (?1,'finished')", [session])
            .unwrap();
        for event in [&parent, &child] {
            db.execute(
                "INSERT INTO raw_stream VALUES (?1,?2,?3,'file',NULL,?4)",
                params![
                    session,
                    event.sequence,
                    event.timestamp_ticks,
                    serde_json::to_string(event).unwrap()
                ],
            )
            .unwrap();
        }
        drop(db);
        let expected = json!({"targets":[
            {"label":"parent","role":"followparent","path":parent.resource},
            {"label":"child","role":"followchild","path":child.resource}
        ]});
        let selection = json!({"session_id":session,"candidate_event_ids":[parent.id,child.id]});
        let mut args = BTreeMap::from([
            ("--session".into(), session.into()),
            ("--db".into(), database.to_string_lossy().into_owned()),
            ("--truth".into(), directory.to_string_lossy().into_owned()),
        ]);
        for (argument, filename, document) in [
            (
                "--capture",
                "inspect.json",
                json!({"schema_version":3,"kind":"inspect","data":capture}),
            ),
            ("--expected", "expected.json", expected),
            ("--selection", "selection.json", selection),
        ] {
            let path = directory.join(filename);
            fs::write(&path, serde_json::to_vec(&document).unwrap()).unwrap();
            args.insert(argument.into(), path.to_string_lossy().into_owned());
        }
        let oracle = |role: &str, event: &SystemEvent, pid| json!({"role":role,"operation":"write_requested","success":true,"resource":event.resource,"pid":pid,"creation_time":"100","start_ticks":"190","end_ticks":"210"});
        fs::write(
            directory.join("ground-truth-fixture.jsonl"),
            format!(
                "{}\n{}\n",
                oracle("followparent", &parent, 10),
                oracle("followchild", &child, 20)
            ),
        )
        .unwrap();
        let before = fs::read(&database).unwrap();
        let result = diagnose(&args).unwrap();
        assert_eq!(
            fs::read(&database).unwrap(),
            before,
            "read-only replay must not modify the database"
        );
        assert_eq!(result["complete"], true);
        assert_eq!(
            result["targets"][0]["retained_evidence_dry_run"]["pid"],
            Value::Null
        );
        assert_eq!(result["targets"][0]["final_actor"]["rule"], "EventLoss");
        assert_eq!(
            result["targets"][0]["classification"],
            "identity_unresolved_or_mismatched_with_retained_evidence"
        );
        let public = serde_json::to_string(&result).unwrap();
        assert!(!public.contains("controlled-private"));
        assert!(!public.contains("fixture.exe"));
        assert!(!public.contains(&directory.to_string_lossy().to_string()));
        args.insert("--session".into(), "another-session".into());
        assert!(
            diagnose(&args).is_err(),
            "do not silently pick a different session"
        );
        fs::remove_dir_all(directory).unwrap();
    }
}
