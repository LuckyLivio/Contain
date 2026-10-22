//! Normalized operations retain exact source references; snapshots never invent an actor.
use crate::{filesystem::Snapshot, model::*};
use std::collections::BTreeMap;

pub fn correlate(
    events: &mut [SystemEvent],
    before: &Snapshot,
    after: &Snapshot,
) -> Vec<NormalizedOperation> {
    let mut completions: BTreeMap<_, Vec<_>> = BTreeMap::new();
    for event in events.iter().filter(|e| e.event_type == "completion") {
        if let Some(request) = &event.raw.related_event {
            completions.entry(request.clone()).or_default().push((
                event.id.clone(),
                event.success,
                event.raw.status,
            ));
        }
    }
    let mut result = Vec::new();
    for e in events.iter_mut().filter(|e| {
        e.event_type == "file" && !matches!(e.operation.as_str(), "open_requested" | "close")
    }) {
        let mut refs = vec![e.id.clone()];
        let mut ambiguous_completion = false;
        if let Some(matches) = completions.get(&e.id) {
            refs.extend(matches.iter().map(|(id, _, _)| id.clone()));
            if let [(.., success, status)] = matches.as_slice() {
                e.success = *success;
                e.raw.status = *status;
            } else {
                // Match paged storage: more than one completion is ambiguous,
                // even when statuses happen to agree. Never select by input order.
                e.success = None;
                e.raw.status = None;
                ambiguous_completion = true;
            }
        }
        let operation = match (e.operation.as_str(), e.success) {
            ("write_requested", Some(true)) => "Modified",
            ("delete_requested", Some(true)) => "Deleted",
            ("delete_requested", None) => "DeleteRequested",
            (_, Some(false)) => "Failed",
            ("rename_requested", _) => "RenameRequested",
            ("create_new_file", _) => "CreateObserved",
            _ => "WriteRequested",
        };
        result.push(NormalizedOperation {
            id: format!("op:{}", e.id),
            operation: operation.into(),
            resource: e.resource.clone(),
            raw_events: refs,
            success: e.success,
            confidence: e.confidence,
            reason: if ambiguous_completion {
                "Multiple completion records refer to this request; result is ambiguous and all raw references are retained."
            } else if e.success == Some(false) {
                "Request failed; this is not a state deletion."
            } else if e.success.is_some() {
                "IRP completion matched the scoped request generation; raw status is retained."
            } else {
                "Completion or rename endpoints unresolved; raw request retained."
            }
            .into(),
            resource_identity: e
                .raw
                .object_generation
                .as_ref()
                .map(|g| format!("open-generation:{g}")),
            ..Default::default()
        });
    }
    // Stable volume/file index + creation time, not matching hashes or path similarity.
    let mut old_ids: BTreeMap<&str, Vec<&String>> = BTreeMap::new();
    let mut new_ids: BTreeMap<&str, Vec<&String>> = BTreeMap::new();
    for (path, state) in &before.files {
        if let Some(id) = &state.identity {
            old_ids.entry(id).or_default().push(path);
        }
    }
    for (path, state) in &after.files {
        if let Some(id) = &state.identity {
            new_ids.entry(id).or_default().push(path);
        }
    }
    for (identity, old) in &old_ids {
        if let Some(new) = new_ids.get(identity)
            && old.len() == 1
            && new.len() == 1
            && old[0] != new[0]
        {
            result.push(NormalizedOperation{id:uuid::Uuid::new_v4().to_string(),operation:"Renamed".into(),resource:new[0].clone(),previous_path:Some(old[0].clone()),resource_identity:Some((*identity).into()),success:Some(true),reason:"Unique stable file identity survived at a new path across snapshots. Intermediate names and actor are not inferred.".into(),..Default::default()});
        }
    }
    for (path, old) in &before.files {
        if let Some(new) = after.files.get(path)
            && old.identity.is_some()
            && new.identity.is_some()
            && old.identity != new.identity
        {
            result.push(NormalizedOperation{id:uuid::Uuid::new_v4().to_string(),operation:"Replaced".into(),resource:path.clone(),resource_identity:new.identity.clone(),success:Some(true),reason:"Same path has a different stable file identity. Source/backup names and actor are not inferred.".into(),..Default::default()});
        }
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::filesystem::FileState;
    #[test]
    fn missing_delete_completion_remains_a_delete_request() {
        let mut events = [SystemEvent {
            id: "request".into(),
            event_type: "file".into(),
            operation: "delete_requested".into(),
            ..Default::default()
        }];
        let operations = correlate(&mut events, &Snapshot::default(), &Snapshot::default());
        assert_eq!(operations[0].operation, "DeleteRequested");
        assert_eq!(operations[0].success, None);
    }
    #[test]
    fn conflicting_completions_never_select_the_last_result() {
        let request = SystemEvent {
            id: "request".into(),
            event_type: "file".into(),
            operation: "delete_requested".into(),
            ..Default::default()
        };
        let completion = |id: &str, success| SystemEvent {
            id: id.into(),
            event_type: "completion".into(),
            success: Some(success),
            raw: RawEvidence {
                related_event: Some("request".into()),
                status: Some(if success { 0 } else { 0xc0000022 }),
                ..Default::default()
            },
            ..Default::default()
        };
        for completions in [
            [completion("failed", false), completion("success", true)],
            [completion("success", true), completion("failed", false)],
        ] {
            let mut events = vec![request.clone()];
            events.extend(completions);
            let operations = correlate(&mut events, &Snapshot::default(), &Snapshot::default());
            assert_eq!(events[0].success, None);
            assert_eq!(operations[0].operation, "DeleteRequested");
            assert_eq!(operations[0].raw_events.len(), 3);
        }
    }
    #[test]
    fn rename_uses_identity_not_hash_and_replacement_is_separate() {
        let mut a = Snapshot::default();
        let mut b = Snapshot::default();
        a.files.insert(
            "old".into(),
            FileState {
                identity: Some("id1".into()),
                ..Default::default()
            },
        );
        b.files.insert(
            "new".into(),
            FileState {
                identity: Some("id1".into()),
                ..Default::default()
            },
        );
        b.files.insert(
            "old".into(),
            FileState {
                identity: Some("id2".into()),
                ..Default::default()
            },
        );
        let ops = correlate(&mut [], &a, &b);
        assert!(ops.iter().any(|o| o.operation == "Renamed"));
        assert!(ops.iter().any(|o| o.operation == "Replaced"));
        assert!(ops.iter().all(|o| o.confidence == Confidence::Unknown));
    }
    #[test]
    fn failed_completion_never_normalizes_as_deleted() {
        let request = SystemEvent {
            id: "r".into(),
            event_type: "file".into(),
            operation: "delete_requested".into(),
            ..Default::default()
        };
        let completion = SystemEvent {
            id: "c".into(),
            event_type: "completion".into(),
            success: Some(false),
            raw: RawEvidence {
                related_event: Some("r".into()),
                status: Some(0xc0000022),
                ..Default::default()
            },
            ..Default::default()
        };
        let ops = correlate(
            &mut [request, completion],
            &Snapshot::default(),
            &Snapshot::default(),
        );
        assert_eq!(ops[0].operation, "Failed");
        assert_eq!(ops[0].raw_events, vec!["r", "c"]);
    }
}
