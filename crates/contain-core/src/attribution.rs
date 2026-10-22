//! Deterministic policy. Sources provide facts; this module decides whether they imply ownership.
use crate::model::*;
use crate::windows::native::normalize_path;

pub fn attribute(event: &mut SystemEvent, processes: &[ProcessRecord], session_id: &str) {
    event.confidence = Confidence::Unknown;
    if !matches!(
        event.evidence.source,
        EvidenceSource::EtwFile | EvidenceSource::EtwRegistry | EvidenceSource::EtwProcess
    ) {
        event.evidence.rule = AttributionRule::SnapshotOnly;
        event.reason = "This source does not supply a verified operation writer.".into();
        return;
    }
    let (Some(pid), Some(created)) = (event.evidence.pid, event.evidence.process_creation_time)
    else {
        event.evidence.rule = AttributionRule::MissingWriter;
        event.reason = "Source cannot verify the writer process instance; timing and path do not establish ownership.".into();
        return;
    };
    let candidate = processes
        .iter()
        .find(|p| p.pid == pid && p.creation_time == Some(created));
    let Some(process) = candidate else {
        event.evidence.rule = if processes.iter().any(|p| p.pid == pid) {
            AttributionRule::AmbiguousLifetime
        } else {
            AttributionRule::UnrelatedProcess
        };
        event.reason =
            "Writer PID and creation time do not match a verified installation process.".into();
        return;
    };
    let image_matches = event
        .evidence
        .process_image
        .as_ref()
        .is_some_and(|image| normalize_path(image, &[]) == normalize_path(&process.image, &[]));
    if !image_matches
        || event.timestamp_ticks < created
        || process
            .ended_at
            .is_some_and(|end| event.timestamp_ticks > end)
        || !matches!(process.confidence, Confidence::Certain | Confidence::High)
    {
        event.evidence.rule = AttributionRule::AmbiguousLifetime;
        event.reason =
            "Process lifetime or executable identity cannot be verified for the event time.".into();
        return;
    }
    event.confidence = Confidence::High;
    event.evidence.parent_pid = process.parent_pid;
    event.evidence.ancestor_pid = process.evidence.ancestor_pid.or(Some(process.pid));
    event.evidence.session_id = Some(session_id.into());
    event.evidence.rule = if process.confidence == Confidence::Certain {
        AttributionRule::InstallerPid
    } else {
        AttributionRule::DescendantProcess
    };
    event.reason = format!(
        "{} from PID {} (creation time {}) matches a verified {} process. Operation completion: {}.",
        event.operation,
        pid,
        created,
        if process.confidence == Confidence::Certain {
            "installer"
        } else {
            "descendant"
        },
        match event.success {
            Some(true) => "success",
            Some(false) => "failed",
            None => "not supplied",
        }
    );
}

pub fn compose_files(files: &mut [FileChange], events: &mut [SystemEvent], complete: bool) {
    let mut by_path: std::collections::HashMap<String, Vec<usize>> =
        std::collections::HashMap::new();
    for (i, event) in events.iter().enumerate() {
        if event.event_type == "file"
            && matches!(
                event.operation.as_str(),
                "write_requested" | "delete_requested" | "rename_requested" | "create_new_file"
            )
            && event.success != Some(false)
        {
            by_path
                .entry(normalize_path(&event.resource, &[]))
                .or_default()
                .push(i);
        }
    }
    for file in files {
        file.confidence = Confidence::Unknown;
        file.evidence.clear();
        file.reason =
            "Snapshot state difference has no compatible verified writer evidence.".into();
        let path = normalize_path(&file.path, &[]);
        let Some(indices) = by_path.get(&path) else {
            continue;
        };
        for &i in indices {
            events[i].state_validated = false;
        }
        let related: Vec<_> = indices.iter().map(|&i| &events[i]).collect();
        file.evidence = related.iter().map(|event| event.evidence.clone()).collect();
        if related.is_empty() {
            continue;
        }
        let all_attributed = related
            .iter()
            .all(|event| event.confidence == Confidence::High && event.raw.resource_resolved);
        let supports_state = |event: &&SystemEvent| match file.operation.as_str() {
            "created" | "modified" => matches!(
                event.operation.as_str(),
                "write_requested" | "create_new_file"
            ),
            "deleted" => event.operation == "delete_requested",
            _ => false,
        };
        let supporting_time = related
            .iter()
            .copied()
            .filter(supports_state)
            .map(|e| e.timestamp_ticks)
            .max();
        let contradictory_time = related
            .iter()
            .filter(|e| {
                e.success == Some(true)
                    && match file.operation.as_str() {
                        "created" | "modified" => e.operation == "delete_requested",
                        "deleted" => {
                            matches!(e.operation.as_str(), "write_requested" | "create_new_file")
                        }
                        _ => false,
                    }
            })
            .map(|e| e.timestamp_ticks)
            .max();
        let compatible = supporting_time
            .is_some_and(|at| contradictory_time.is_none_or(|conflict| at > conflict));
        if complete && all_attributed && compatible {
            file.confidence = Confidence::High;
            file.reason = "Verified installer-family file activity matches a before/after state change; this is session attribution, not exclusive resource ownership.".into();
            for &i in indices {
                if match file.operation.as_str() {
                    "created" | "modified" => matches!(
                        events[i].operation.as_str(),
                        "write_requested" | "create_new_file"
                    ),
                    "deleted" => events[i].operation == "delete_requested",
                    _ => false,
                } {
                    events[i].state_validated = true;
                }
            }
        } else {
            file.confidence = Confidence::Unknown;
            file.reason = if !complete { "Capture reported event loss or incomplete decoding; final state ownership is not inferred." } else if !compatible { "Observed operation types or their ordering do not support the snapshot state; path coincidence cannot validate this change." } else { "Observed writers include an unrelated or unresolved process; final state ownership is ambiguous." }.into();
        }
    }
}

/// Production snapshot composition requires a per-resource observation sealed
/// after source shutdown. `compose_files` remains the lower-level policy helper.
pub fn compose_file_observed(
    file: &mut FileChange,
    events: &mut [SystemEvent],
    complete: bool,
    observation: Option<StateObservation>,
) {
    let read_proven = observation.as_ref().is_some_and(|read| {
        read.method == "deny_write_delete_read_handle"
            && read.baseline_stable
            && read.content_stable
            && read.path_pinned
            && read.write_delete_excluded
            && read.held_through_trace_stop
            && read.final_source_complete
            && read.identity.is_some()
            && read.identity == read.baseline_identity
            && read.baseline_hash.is_some()
            && read.baseline_hash == file.before_hash
            && read.observed_hash.is_some()
            && read.observed_hash == file.after_hash
            && read.failure.is_none()
    });
    let path = normalize_path(&file.path, &[]);
    let mutations: Vec<_> = events
        .iter()
        .filter(|event| {
            event.event_type == "file"
                && normalize_path(&event.resource, &[]) == path
                && event.success != Some(false)
                && matches!(
                    event.operation.as_str(),
                    "write_requested" | "delete_requested" | "rename_requested" | "create_new_file"
                )
        })
        .collect();
    // An ETW open generation is not a physical file ID. Requiring one consistent
    // generation is an extra refusal rule; it does not turn the two identifiers
    // into equivalents or justify rename/replacement ownership.
    let generation = mutations
        .first()
        .and_then(|e| e.raw.object_generation.as_ref());
    let object = mutations.first().and_then(|e| e.raw.file_object.as_ref());
    let file_keys: std::collections::HashSet<_> = mutations
        .iter()
        .filter_map(|e| e.raw.file_key.as_ref())
        .collect();
    let writes_proven = !mutations.is_empty()
        && generation.is_some_and(|g| !g.is_empty())
        && object.is_some_and(|o| !o.is_empty() && o != "0000000000000000")
        && file_keys.len() <= 1
        && mutations.iter().all(|event| {
            event.operation == "write_requested"
                && event.success == Some(true)
                && event.confidence == Confidence::High
                && event.raw.resource_resolved
                && event.raw.object_generation.as_ref() == generation
                && event.raw.file_object.as_ref() == object
        });
    let mutation_coverage = observation
        .as_ref()
        .is_some_and(|read| read.mutation_coverage_complete);
    let covered = complete
        && mutation_coverage
        && file.operation == "modified"
        && read_proven
        && writes_proven;
    compose_files(std::slice::from_mut(file), events, covered);
    if covered && file.confidence == Confidence::High {
        file.reason = "A stable baseline and a read handle denying writes/deletes identify the same physical file; all recorded mutations are successful verified installer-family writes in one open generation. The file and its ancestor paths remained pinned through trace stop and complete drain. This active observation temporarily blocks writes/deletes; it explains the observed modification, not exclusive ownership of every byte or permission to delete.".into();
    } else {
        file.reason = if !complete {
            "Final trace/drain coverage is incomplete; source actor evidence is retained but the observed state remains Unknown."
        } else if !mutation_coverage {
            "Stable file reads do not establish complete mutation coverage: open/truncate disposition and unresolved file paths are not fully represented. Source actor evidence and read observations are retained, but final state attribution remains Unknown."
        } else if !read_proven {
            "A stable baseline, pinned physical file, and write/delete exclusion through final trace drain were not all established; source actor evidence is retained."
        } else {
            "The controlled state cannot be explained by one consistent generation of successful installer-family writes: creation, deletion, rename, replacement, unknown completion, or another writer requires Unknown."
        }.into();
    }
    if let Some(observation) = observation {
        file.evidence.push(AttributionEvidence {
            source: EvidenceSource::Snapshot,
            observation: Some(observation),
            ..Default::default()
        });
    }
}

pub fn compose_registry(
    changes: &mut [RegistryChange],
    events: &mut [SystemEvent],
    nt_root: Option<&str>,
    complete: bool,
) {
    for change in changes.iter_mut() {
        change.confidence = Confidence::Unknown;
        change.evidence.clear();
        change.reason =
            "Snapshot value difference has no compatible verified writer evidence.".into();
    }
    let Some(nt_root) = nt_root else {
        return;
    };
    for change in changes {
        let resource = format!("{}\\{}", nt_root, change.name).to_lowercase();
        let mut related: Vec<_> = events
            .iter_mut()
            .filter(|event| {
                event.event_type == "registry"
                    && event.resource.to_lowercase() == resource
                    && event.success != Some(false)
            })
            .collect();
        for event in &mut related {
            event.state_validated = false;
        }
        change.evidence = related.iter().map(|event| event.evidence.clone()).collect();
        let expected = match change.operation.as_str() {
            "created" | "modified" => "set_value",
            "deleted" => "delete_value",
            _ => "",
        };
        let supporting_time = related
            .iter()
            .filter(|e| e.operation == expected)
            .map(|e| e.timestamp_ticks)
            .max();
        let contradictory_time = related
            .iter()
            .filter(|e| e.operation != expected && e.success == Some(true))
            .map(|e| e.timestamp_ticks)
            .max();
        let compatible = supporting_time
            .is_some_and(|at| contradictory_time.is_none_or(|conflict| at > conflict));
        if complete
            && compatible
            && !related.is_empty()
            && related.iter().all(|event| {
                event.confidence == Confidence::High
                    && event.success == Some(true)
                    && event.raw.resource_resolved
            })
        {
            change.confidence = Confidence::High;
            change.reason = "Successful registry activity from verified installation processes matches the scoped value-state diff.".into();
            for event in related {
                if event.operation == expected {
                    event.state_validated = true;
                }
            }
        } else if !compatible && !related.is_empty() {
            change.reason = "Observed registry operations do not support this value-state difference; writer timing alone does not validate it.".into();
        }
    }
}

pub fn compose_registry_observed(
    change: &mut RegistryChange,
    events: &mut [SystemEvent],
    nt_root: Option<&str>,
    observation: Option<StateObservation>,
) {
    // Even two matching reads have an ABA gap. Until a source-domain interval or
    // an exclusion mechanism is proven, retain actor evidence without promoting
    // the final value. File leases cannot be applied to registry values.
    compose_registry(std::slice::from_mut(change), events, nt_root, false);
    change.reason = "The final registry value was observed without writer exclusion; concurrent write/restore and clock-domain uncertainty are not excluded. Source actor evidence is retained, but final value ownership remains Unknown.".into();
    if let Some(observation) = observation {
        change.evidence.push(AttributionEvidence {
            source: EvidenceSource::Snapshot,
            observation: Some(observation),
            ..Default::default()
        });
    }
}

pub fn inventory_evidence(
    command: &str,
    source: EvidenceSource,
    processes: &[ProcessRecord],
    session_id: &str,
) -> (Confidence, AttributionEvidence, String) {
    // Task actions already store an executable separately from its arguments.
    // Service/Run command strings require an unambiguous token, never a substring
    // ending at the first `.exe` in a path or directory name.
    let executable = if source == EvidenceSource::TaskInventory {
        Some(command)
    } else if let Some(quoted) = command.trim_start().strip_prefix('"') {
        quoted.split_once('"').and_then(|(path, rest)| {
            (rest.is_empty() || rest.starts_with(char::is_whitespace)).then_some(path)
        })
    } else {
        command.split_whitespace().next()
    };
    let matched = processes.iter().find(|process| {
        matches!(process.confidence, Confidence::Certain | Confidence::High)
            && executable.is_some_and(|path| {
                !path.is_empty() && normalize_path(path, &[]) == normalize_path(&process.image, &[])
            })
    });
    match matched {
        Some(process) => (Confidence::Medium, AttributionEvidence { source, pid: Some(process.pid),
            process_creation_time: process.creation_time, process_image: Some(process.image.clone()),
            session_id: Some(session_id.into()), rule: AttributionRule::ExecutablePathMatch, ..Default::default() },
            "Inventory target exactly matches a verified process executable; the process that changed the entry is unknown.".into()),
        None => (Confidence::Unknown, AttributionEvidence { source, ..Default::default() }, "Inventory before/after difference has no verified application target or writer.".into()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn observed_write() -> (FileChange, SystemEvent, StateObservation) {
        let file = FileChange {
            path: "C:\\App\\data".into(),
            operation: "modified".into(),
            before_hash: Some("before".into()),
            after_hash: Some("after".into()),
            ..Default::default()
        };
        let mut event = event(Some(2), Some(100));
        event.operation = "write_requested".into();
        event.confidence = Confidence::High;
        event.success = Some(true);
        event.raw.resource_resolved = true;
        event.raw.object_generation = Some("generation-1".into());
        event.raw.file_object = Some("object-1".into());
        let observation = StateObservation {
            method: "deny_write_delete_read_handle".into(),
            baseline_identity: Some("physical-file-1".into()),
            identity: Some("physical-file-1".into()),
            baseline_hash: file.before_hash.clone(),
            observed_hash: file.after_hash.clone(),
            baseline_stable: true,
            content_stable: true,
            path_pinned: true,
            write_delete_excluded: true,
            held_through_trace_stop: true,
            final_source_complete: true,
            // A complete controlled model is a policy test, not a claim about
            // coverage provided by the current production ETW decoder.
            mutation_coverage_complete: true,
            ..Default::default()
        };
        (file, event, observation)
    }

    #[test]
    fn observed_snapshot_requires_resource_proof_without_erasing_source_evidence() {
        let (mut file, event, _) = observed_write();
        let mut events = [event];
        compose_file_observed(&mut file, &mut events, true, None);
        assert_eq!(file.confidence, Confidence::Unknown);
        assert!(!events[0].state_validated);
        assert_eq!(events[0].confidence, Confidence::High);
        assert_eq!(file.evidence[0].pid, Some(2));
    }

    #[test]
    fn observed_snapshot_rejects_unfinished_unstable_replaced_or_uncovered_reads() {
        for case in 0..11 {
            let (mut file, event, mut observation) = observed_write();
            match case {
                0 => observation.held_through_trace_stop = false,
                1 => observation.baseline_stable = false,
                2 => observation.content_stable = false,
                3 => observation.path_pinned = false,
                4 => observation.write_delete_excluded = false,
                5 => observation.identity = Some("replacement".into()),
                6 => observation.identity = None,
                7 => observation.final_source_complete = false,
                8 => observation.observed_hash = Some("other-read".into()),
                9 => observation.baseline_hash = Some("other-before".into()),
                10 => observation.mutation_coverage_complete = false,
                _ => unreachable!(),
            }
            let mut events = [event];
            compose_file_observed(&mut file, &mut events, true, Some(observation));
            assert_eq!(file.confidence, Confidence::Unknown, "case {case}");
            assert!(!events[0].state_validated);
            assert_eq!(events[0].confidence, Confidence::High);
        }
    }

    #[test]
    fn observed_snapshot_rejects_unknown_completion_and_object_generation_conflicts() {
        for case in 0..7 {
            let (mut file, event, observation) = observed_write();
            let mut event = event;
            let mut other = event.clone();
            match case {
                0 => other.success = None,
                1 => other.raw.object_generation = Some("different-generation".into()),
                2 => other.raw.object_generation = None,
                3 => other.operation = "rename_requested".into(),
                4 => other.operation = "delete_requested".into(),
                5 => other.confidence = Confidence::Unknown,
                6 => {
                    event.raw.file_key = Some("file-key-a".into());
                    other.raw.file_key = Some("file-key-b".into());
                }
                _ => unreachable!(),
            }
            let mut events = [other, event];
            compose_file_observed(&mut file, &mut events, true, Some(observation));
            assert_eq!(file.confidence, Confidence::Unknown, "case {case}");
            assert!(events.iter().all(|e| !e.state_validated));
            assert_eq!(events[1].confidence, Confidence::High);
            assert_eq!(
                events[0].confidence,
                if case == 5 {
                    Confidence::Unknown
                } else {
                    Confidence::High
                }
            );
        }
    }

    #[test]
    fn complete_controlled_model_is_usable_but_final_loss_revokes_only_state() {
        let (mut file, event, observation) = observed_write();
        let mut events = [event];
        compose_file_observed(&mut file, &mut events, true, Some(observation.clone()));
        assert_eq!(file.confidence, Confidence::High);
        assert!(events[0].state_validated);
        assert_eq!(
            file.evidence.last().unwrap().observation,
            Some(observation.clone())
        );
        compose_file_observed(&mut file, &mut events, false, Some(observation));
        assert_eq!(file.confidence, Confidence::Unknown);
        assert!(!events[0].state_validated);
        assert_eq!(events[0].confidence, Confidence::High);
    }

    #[test]
    fn registry_unlocked_observation_never_promotes_value_or_erases_source_actor() {
        let mut change = RegistryChange {
            name: "value".into(),
            operation: "modified".into(),
            ..Default::default()
        };
        let mut source = event(Some(2), Some(100));
        source.event_type = "registry".into();
        source.resource = "root\\value".into();
        source.operation = "set_value".into();
        source.confidence = Confidence::High;
        source.success = Some(true);
        source.raw.resource_resolved = true;
        let mut events = [source];
        compose_registry_observed(
            &mut change,
            &mut events,
            Some("root"),
            Some(StateObservation {
                method: "registry_value_double_read_unlocked".into(),
                ..Default::default()
            }),
        );
        assert_eq!(change.confidence, Confidence::Unknown);
        assert_eq!(events[0].confidence, Confidence::High);
        assert!(!events[0].state_validated);
        assert_eq!(change.evidence[0].pid, Some(2));
        assert!(change.evidence[1].observation.is_some());
    }
    fn process(pid: u32, birth: u64, confidence: Confidence) -> ProcessRecord {
        ProcessRecord {
            pid,
            creation_time: Some(birth),
            image: "C:\\App\\helper.exe".into(),
            confidence,
            ..Default::default()
        }
    }
    fn event(pid: Option<u32>, birth: Option<u64>) -> SystemEvent {
        SystemEvent {
            timestamp_ticks: 200,
            event_type: "file".into(),
            resource: "C:\\App\\data".into(),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                pid,
                process_creation_time: birth,
                process_image: Some("C:\\App\\helper.exe".into()),
                ..Default::default()
            },
            ..Default::default()
        }
    }
    #[test]
    fn incompatible_file_operation_cannot_validate_snapshot() {
        for (change, operation) in [
            ("created", "delete_requested"),
            ("modified", "delete_requested"),
            ("deleted", "write_requested"),
            ("created", "read"),
        ] {
            let mut e = event(Some(2), Some(100));
            e.operation = operation.into();
            e.success = Some(true);
            attribute(&mut e, &[process(2, 100, Confidence::High)], "s");
            let mut files = [FileChange {
                path: e.resource.clone(),
                operation: change.into(),
                ..Default::default()
            }];
            let mut events = [e];
            compose_files(&mut files, &mut events, true);
            assert_eq!(
                files[0].confidence,
                Confidence::Unknown,
                "{change}/{operation}"
            );
            assert!(!events[0].state_validated);
        }
    }
    #[test]
    fn incompatible_registry_operation_cannot_validate_snapshot() {
        let mut change = [RegistryChange {
            name: "value".into(),
            operation: "created".into(),
            ..Default::default()
        }];
        let mut e = event(Some(2), Some(100));
        e.event_type = "registry".into();
        e.resource = "root\\value".into();
        e.operation = "delete_value".into();
        e.success = Some(true);
        e.confidence = Confidence::High;
        compose_registry(&mut change, &mut [e], Some("root"), true);
        assert_eq!(change[0].confidence, Confidence::Unknown);
    }
    #[test]
    fn compatible_mutations_validate_only_matching_snapshot_direction() {
        for (change, operation) in [
            ("created", "create_new_file"),
            ("modified", "write_requested"),
            ("deleted", "delete_requested"),
        ] {
            let mut e = event(Some(2), Some(100));
            e.operation = operation.into();
            e.raw.resource_resolved = true;
            attribute(&mut e, &[process(2, 100, Confidence::High)], "s");
            let mut files = [FileChange {
                path: e.resource.clone(),
                operation: change.into(),
                ..Default::default()
            }];
            let mut events = [e];
            compose_files(&mut files, &mut events, true);
            assert_eq!(
                files[0].confidence,
                Confidence::High,
                "{change}/{operation}"
            );
            assert!(events[0].state_validated);
            compose_files(&mut files, &mut events, false);
            assert_eq!(files[0].confidence, Confidence::Unknown);
            assert!(!events[0].state_validated);
        }
    }
    #[test]
    fn later_or_simultaneous_conflicting_mutation_prevents_state_promotion() {
        for conflict_time in [200, 201] {
            let mut write = event(Some(2), Some(100));
            write.operation = "write_requested".into();
            write.confidence = Confidence::High;
            write.raw.resource_resolved = true;
            write.success = Some(true);
            let mut delete = write.clone();
            delete.operation = "delete_requested".into();
            delete.timestamp_ticks = conflict_time;
            let mut files = [FileChange {
                path: write.resource.clone(),
                operation: "created".into(),
                ..Default::default()
            }];
            compose_files(&mut files, &mut [write, delete], true);
            assert_eq!(files[0].confidence, Confidence::Unknown);
        }
    }
    #[test]
    fn registry_creation_and_deletion_use_value_mutations_only() {
        for (change, operation) in [("created", "set_value"), ("deleted", "delete_value")] {
            let mut changes = [RegistryChange {
                name: "value".into(),
                operation: change.into(),
                ..Default::default()
            }];
            let mut e = event(Some(2), Some(100));
            e.event_type = "registry".into();
            e.resource = "root\\value".into();
            e.operation = operation.into();
            e.confidence = Confidence::High;
            e.success = Some(true);
            e.raw.resource_resolved = true;
            let mut events = [e];
            compose_registry(&mut changes, &mut events, Some("root"), true);
            assert_eq!(changes[0].confidence, Confidence::High);
            assert!(events[0].state_validated);
            compose_registry(&mut changes, &mut events, Some("root"), false);
            assert_eq!(changes[0].confidence, Confidence::Unknown);
            assert!(!events[0].state_validated);
        }
    }
    #[test]
    fn inventory_executable_suffix_and_unquoted_space_are_not_exact_targets() {
        let processes = [process(2, 100, Confidence::High)];
        for command in [
            "C:\\App\\helper.exe.backup --service",
            "C:\\App\\helper.exe\\other.exe",
            "\"C:\\App\\helper.exe --service",
        ] {
            assert_eq!(
                inventory_evidence(command, EvidenceSource::ServiceInventory, &processes, "s").0,
                Confidence::Unknown,
                "{command}"
            );
        }
        let mut spaced = processes[0].clone();
        spaced.image = "C:\\Program Files\\App\\helper.exe".into();
        assert_eq!(
            inventory_evidence(
                &spaced.image,
                EvidenceSource::ServiceInventory,
                &[spaced.clone()],
                "s"
            )
            .0,
            Confidence::Unknown
        );
    }
    #[test]
    fn child_and_grandchild_events_are_high_with_instance_evidence() {
        for pid in [2, 3] {
            let mut e = event(Some(pid), Some(100));
            attribute(&mut e, &[process(pid, 100, Confidence::High)], "session");
            assert_eq!(e.confidence, Confidence::High);
        }
    }
    #[test]
    fn unrelated_concurrent_writer_in_same_root_is_unknown() {
        let mut e = event(Some(9), Some(100));
        attribute(&mut e, &[process(2, 100, Confidence::High)], "session");
        assert_eq!(e.confidence, Confidence::Unknown);
        assert_eq!(e.evidence.rule, AttributionRule::UnrelatedProcess);
    }
    #[test]
    fn reused_pid_never_inherits_ownership() {
        let mut e = event(Some(2), Some(150));
        attribute(&mut e, &[process(2, 100, Confidence::High)], "session");
        assert_eq!(e.confidence, Confidence::Unknown);
        assert_eq!(e.evidence.rule, AttributionRule::AmbiguousLifetime);
    }
    #[test]
    fn snapshot_only_and_missing_birth_are_unknown() {
        for mut e in [event(None, None), event(Some(2), None)] {
            attribute(&mut e, &[process(2, 100, Confidence::High)], "session");
            assert_eq!(e.confidence, Confidence::Unknown);
        }
    }
    #[test]
    fn detached_unverified_process_is_unknown() {
        let mut e = event(Some(2), Some(100));
        attribute(&mut e, &[process(2, 100, Confidence::Unknown)], "session");
        assert_eq!(e.confidence, Confidence::Unknown);
    }
    #[test]
    fn stale_or_wrong_image_is_unknown() {
        let mut e = event(Some(2), Some(100));
        e.evidence.process_image = Some("C:\\Other.exe".into());
        attribute(&mut e, &[process(2, 100, Confidence::High)], "session");
        assert_eq!(e.confidence, Confidence::Unknown);
    }
    #[test]
    fn mixed_writers_do_not_promote_snapshot() {
        let mut files = vec![FileChange {
            path: "C:\\App\\data".into(),
            ..Default::default()
        }];
        let mut events = vec![event(Some(2), Some(100)), event(Some(9), Some(100))];
        for e in &mut events {
            attribute(e, &[process(2, 100, Confidence::High)], "s");
        }
        compose_files(&mut files, &mut events, true);
        assert_eq!(files[0].confidence, Confidence::Unknown);
    }
    #[test]
    fn loss_and_snapshot_sources_never_gain_high_state_attribution() {
        let mut e = event(Some(2), Some(100));
        attribute(&mut e, &[process(2, 100, Confidence::High)], "s");
        let mut files = vec![FileChange {
            path: e.resource.clone(),
            ..Default::default()
        }];
        compose_files(&mut files, &mut [e.clone()], false);
        assert_eq!(files[0].confidence, Confidence::Unknown);
        e.evidence.source = EvidenceSource::Snapshot;
        attribute(&mut e, &[process(2, 100, Confidence::High)], "s");
        assert_eq!(e.confidence, Confidence::Unknown);
    }
    #[test]
    fn registry_unknown_completion_prevents_promoting_final_value() {
        let mut e = event(Some(2), Some(100));
        e.event_type = "registry".into();
        e.resource = "root\\value".into();
        e.confidence = Confidence::High;
        e.success = Some(true);
        let mut unresolved = e.clone();
        unresolved.success = None;
        unresolved.confidence = Confidence::Unknown;
        let mut changes = vec![RegistryChange {
            name: "value".into(),
            ..Default::default()
        }];
        compose_registry(&mut changes, &mut [e, unresolved], Some("root"), true);
        assert_eq!(changes[0].confidence, Confidence::Unknown);
        assert_eq!(changes[0].evidence.len(), 2);
    }
    #[test]
    fn exact_inventory_target_is_medium_not_high() {
        assert_eq!(
            inventory_evidence(
                "\"C:\\App\\helper.exe\" --service",
                EvidenceSource::ServiceInventory,
                &[process(2, 100, Confidence::High)],
                "s"
            )
            .0,
            Confidence::Medium
        );
        assert_eq!(
            inventory_evidence(
                "C:\\App\\other.exe",
                EvidenceSource::ServiceInventory,
                &[process(2, 100, Confidence::High)],
                "s"
            )
            .0,
            Confidence::Unknown
        );
    }
}
