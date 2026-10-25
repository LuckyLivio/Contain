use super::*;

fn marked_container() -> PathBuf {
    let name = format!("{PREFIX}{:016x}{:016x}", std::process::id(), qpc().unwrap());
    let path = std::env::temp_dir().join(name);
    fs::create_dir(&path).unwrap();
    fs::write(path.join(MARKER), MARKER_BYTES).unwrap();
    path
}

#[test]
fn fixed_cases_preserve_api_order_and_success_denominator() {
    let planned: Vec<_> = cases().collect();
    assert_eq!(planned.len(), 8);
    assert_eq!(
        planned
            .iter()
            .map(|(case, _)| case.actions().len())
            .sum::<usize>(),
        16
    );
    for (case, expected, renamed, deleted) in [
        (Case::A, vec!["write"], false, false),
        (Case::B, vec!["write", "rename"], true, false),
        (Case::C, vec!["write", "delete"], false, true),
        (Case::D, vec!["write", "rename", "delete"], true, true),
    ] {
        assert_eq!(case.actions(), expected);
        assert_eq!((case.renamed(), case.deleted()), (renamed, deleted));
        assert_eq!(planned.iter().filter(|(c, _)| *c == case).count(), 2);
    }
    let contents: std::collections::HashSet<_> =
        planned.into_iter().map(|(c, i)| payload(c, i)).collect();
    assert_eq!(
        contents.len(),
        8,
        "every diagnostic payload must be distinguishable"
    );
}

#[test]
fn only_fixed_unique_temp_container_names_are_accepted() {
    assert!(name_is_valid(&format!("{PREFIX}{}", "a".repeat(32))));
    for name in [
        "contain-demo-existing",
        "contain-write-diagnostic-",
        "C:/user/file",
        "../contain-write-diagnostic-x",
    ] {
        assert!(!name_is_valid(name));
    }
    assert!(!name_is_valid(&format!("{PREFIX}{}", "g".repeat(32))));
    assert!(!name_is_valid(&format!("{PREFIX}{}", "a".repeat(33))));
}

#[test]
fn nonempty_container_and_reuse_are_refused_without_deletion() {
    let path = marked_container();
    let existing = path.join("user-sentinel.txt");
    fs::write(&existing, b"must remain unchanged").unwrap();
    assert!(Fixture::prepare(&path).is_err());
    assert_eq!(fs::read(existing).unwrap(), b"must remain unchanged");
    assert!(!path.join("payload").exists());
    let fresh = marked_container();
    let fixture = Fixture::prepare(&fresh).unwrap();
    assert!(fixture.truth.parent() == fresh.canonicalize().unwrap().parent());
    assert!(!fixture.truth.starts_with(&fresh));
    assert!(Fixture::prepare(&fresh).is_err());
    assert!(fixture.checked_leaf("../user-sentinel.txt", false).is_err());
    assert!(fixture.checked_leaf("A-0.tmp", true).is_err());
    fs::write(
        fixture.root.join("A-0.tmp"),
        b"created only inside this new test root",
    )
    .unwrap();
    assert!(fixture.checked_leaf("A-0.tmp", false).is_err());
}

#[test]
fn foreign_marker_and_parent_traversal_are_refused() {
    let path = marked_container();
    fs::write(path.join(MARKER), b"wrong marker").unwrap();
    assert!(Fixture::prepare(&path).is_err());
    assert_eq!(fs::read(path.join(MARKER)).unwrap(), b"wrong marker");
    let temp = std::env::temp_dir().canonicalize().unwrap();
    assert!(validate_container(&path.join(".."), &temp).is_err());
    assert!(validate_container(Path::new("relative"), &temp).is_err());
}

#[test]
fn failed_api_is_logged_with_error_and_independent_clock_brackets() {
    let container = marked_container();
    let fixture = Fixture::prepare(&container).unwrap();
    let journal_path = fixture.truth.join("failed-api-unit.jsonl");
    let mut journal = Journal::new(&journal_path).unwrap();
    assert!(
        !journal
            .call(
                json!({"action":"synthetic_failed_call","api":"unit-test closure"}),
                || { Err(io::Error::from_raw_os_error(5)) }
            )
            .unwrap()
    );
    drop(journal);
    let row: Value = serde_json::from_slice(&fs::read(journal_path).unwrap()).unwrap();
    assert_eq!(row["success"], false);
    assert_eq!(row["error"], 5);
    assert_eq!(row["api_return"], "Err");
    assert_eq!(row["order"], 1);
    let clock = &row["clock"];
    let tick = |v: &Value| v.as_str().unwrap().parse::<u64>().unwrap();
    assert!(tick(&clock["qpc_frequency"]) > 0);
    assert!(tick(&clock["before"]["qpc_before"]) <= tick(&clock["before"]["qpc_after"]));
    assert!(tick(&clock["before"]["qpc_after"]) <= tick(&clock["start_qpc"]));
    assert!(tick(&clock["start_qpc"]) <= tick(&clock["end_qpc"]));
    assert!(tick(&clock["end_qpc"]) <= tick(&clock["after"]["qpc_before"]));
    assert!(tick(&clock["after"]["qpc_before"]) <= tick(&clock["after"]["qpc_after"]));
    assert!(tick(&row["actor"]["creation_time"]) > 0);
    assert!(tick(&row["actor"]["thread_creation_time"]) > 0);
    assert!(row["actor"]["thread_id"].as_u64().unwrap() > 0);
    assert_eq!(fs::read_dir(&fixture.root).unwrap().count(), 0);
}

fn json_lines(path: &Path) -> Vec<Value> {
    fs::read_to_string(path)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
}

#[test]
fn identity_observation_is_opt_in_and_preserves_original_calls_and_results() {
    let args = Args::try_parse_from(["fixture", "--container", "C:/unused"]).unwrap();
    assert!(!args.observe_file_identity);
    let mut variants = Vec::new();
    for enabled in [false, true] {
        let fixture = Fixture::prepare(&marked_container()).unwrap();
        let path = fixture.truth.join("operations.jsonl");
        let observations = fixture.truth.join("identity-observations.jsonl");
        let mut journal = Journal::new(&path).unwrap();
        let mut observer = enabled.then(|| identity::Observer::new(&observations).unwrap());
        for (case, index) in cases() {
            assert!(run_case(&fixture, &mut journal, &mut observer, case, index).unwrap());
        }
        assert_eq!(journal.order, 16);
        drop(observer);
        drop(journal);
        let calls = json_lines(&path);
        assert_eq!(calls.len(), 16);
        assert!(
            calls
                .iter()
                .all(|row| row["success"] == true && row["error"].is_null())
        );
        assert!(cases().all(|(case, index)| {
            final_state(&fixture, case, index)["paths"]
                .as_array()
                .unwrap()
                .iter()
                .all(|state| state["matches_expected"] == true)
        }));
        variants.push(
            calls
                .iter()
                .map(|row| {
                    json!({
                        "case":row["case"],"index":row["file_index"],"action":row["action"],
                        "api":row["api"],"bytes":row["requested_io_bytes"],
                        "payload":row["payload_sha256"],"close_result":row["close_result"]
                    })
                })
                .collect::<Vec<_>>(),
        );
        if enabled {
            let rows = json_lines(&observations);
            assert_eq!(
                rows.len(),
                29,
                "one session plus exactly 28 bounded observations"
            );
            assert_eq!(rows[0]["variant"], "transient_metadata_handles_v1");
            let tick = |value: &Value| value.as_str().unwrap().parse::<u64>().unwrap();
            for row in &rows[1..] {
                assert_eq!(row["handle_retained_across_workload_call"], false);
                assert_eq!(row["open"]["share_mode"], 7);
                let after = row["after_api_order"].as_u64().unwrap() as usize;
                assert!(
                    tick(&calls[after - 1]["clock"]["end_qpc"]) <= tick(&row["open"]["start_qpc"])
                );
                if after < calls.len() {
                    assert!(tick(&row["end_qpc"]) <= tick(&calls[after]["clock"]["start_qpc"]));
                }
                if row["open"]["success"] == true {
                    assert_eq!(row["close"]["success"], true);
                    for field in [
                        "file_identity",
                        "volume",
                        "observer_opened_name",
                        "observer_normalized_name",
                    ] {
                        assert!(row[field]["success"].is_boolean());
                        assert!(tick(&row[field]["end_qpc"]) <= tick(&row["close"]["start_qpc"]));
                    }
                } else {
                    assert!(matches!(
                        row["stage"].as_str(),
                        Some("after_delete" | "after_rename_old_path")
                    ));
                    assert_eq!(
                        row["open"]["error"], 2,
                        "missing file must retain the real Win32 error"
                    );
                    assert!(row["close"].is_null());
                    assert!(row["file_identity"].is_null());
                }
            }
            // On NTFS these sampled pre/post-rename IDs should agree. This is a
            // fixture regression, not a continuous-identity inference across a handle gap.
            for case in ["B", "D"] {
                for index in 0..2 {
                    let find = |stage: &str| {
                        rows.iter()
                            .find(|r| {
                                r["case"] == case && r["file_index"] == index && r["stage"] == stage
                            })
                            .unwrap()
                    };
                    let before = find("before_rename");
                    let after = find("after_rename_new_path");
                    if before["volume"]["filesystem"] == "NTFS" {
                        assert_eq!(before["file_identity"]["success"], true);
                        assert_eq!(after["file_identity"]["success"], true);
                        assert_eq!(
                            before["file_identity"]["volume_serial_number"],
                            after["file_identity"]["volume_serial_number"]
                        );
                        assert_eq!(
                            before["file_identity"]["file_id_128_hex"],
                            after["file_identity"]["file_id_128_hex"]
                        );
                        assert_ne!(
                            before["observer_normalized_name"]["name"],
                            after["observer_normalized_name"]["name"]
                        );
                    }
                }
            }
        } else {
            assert!(!observations.exists());
        }
    }
    assert_eq!(
        variants[0], variants[1],
        "observer must not replace or augment workload API calls"
    );
}

#[test]
fn observer_releases_all_handles_before_the_next_exclusive_open() {
    let fixture = Fixture::prepare(&marked_container()).unwrap();
    let path = fixture.root.join("A-0.tmp");
    fs::write(&path, payload(Case::A, 0)).unwrap();
    let output = fixture.truth.join("identity-exclusive-open.jsonl");
    let mut observer = Some(identity::Observer::new(&output).unwrap());
    identity::observe(&mut observer, Case::A, 0, "after_write", &path, 1).unwrap();
    let exclusive = OpenOptions::new()
        .access_mode(FILE_READ_ATTRIBUTES)
        .share_mode(0)
        .open(&path);
    assert!(
        exclusive.is_ok(),
        "no observer handle may survive record completion"
    );
    drop(exclusive);
    drop(observer);
    let rows = json_lines(&output);
    assert_eq!(rows[1]["close"]["success"], true);
    assert_eq!(rows[1]["observer_opened_name"]["flags"], 9);
    assert_eq!(rows[1]["observer_normalized_name"]["flags"], 1);
}

#[test]
fn close_failure_blocks_later_workload_even_when_following_clock_sample_fails() {
    let fixture = Fixture::prepare(&marked_container()).unwrap();
    let operations = fixture.truth.join("operations.jsonl");
    let mut journal = Journal::new(&operations).unwrap();
    let mut observer =
        identity::Observer::new(&fixture.truth.join("identity-observations.jsonl")).unwrap();
    // Inject only API outcomes; never pass an invalid handle to the operating system.
    assert!(
        observer
            .close_observation(false, Some(6), 1, || {
                Err(anyhow::anyhow!("synthetic post-close clock failure"))
            })
            .is_err()
    );
    let mut observer = Some(observer);
    assert!(run_case(&fixture, &mut journal, &mut observer, Case::A, 0).is_err());
    assert_eq!(journal.order, 0);
    assert_eq!(fs::read_dir(&fixture.root).unwrap().count(), 0);
    drop(journal);
    assert!(fs::read(operations).unwrap().is_empty());
}
