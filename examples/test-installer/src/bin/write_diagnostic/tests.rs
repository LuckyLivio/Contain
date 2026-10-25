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
