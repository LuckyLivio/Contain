use contain_core::model::{Capture, Confidence, EvidenceSource, SystemEvent};

const SOURCE_SCOPE: &str = "Source confidence describes actor attribution, recorded-name resolution and request result separately. It does not establish an explicit application API call, current path, final state ownership or permission to delete.";

fn source_scope_text(event: &SystemEvent) -> String {
    if event.evidence.source != EvidenceSource::EtwFile || event.event_type != "file" {
        return String::new();
    }
    let name = event.raw.file_name.as_ref();
    let diagnostic = event.raw.file_diagnostics.as_ref();
    let source = name
        .map(|n| n.path_source.as_str())
        .or_else(|| diagnostic.map(|d| d.path_source.as_str()))
        .unwrap_or("unavailable");
    let supplied = matches!(source, "supplied_FileName" | "supplied_FilePath");
    let observed = name.map(|n| n.observed_ticks.to_string()).or_else(|| {
        if supplied {
            Some(event.timestamp_ticks.to_string())
        } else {
            diagnostic
                .and_then(|d| d.cache_origin.as_ref())
                .map(|o| o.event_timestamp.clone())
        }
    });
    let origin = name.map(|n| n.source_event_id.as_str()).or_else(|| {
        if supplied {
            Some(event.id.as_str())
        } else {
            diagnostic
                .and_then(|d| d.cache_origin.as_ref())
                .and_then(|o| o.source_event_id.as_deref())
        }
    });
    format!(
        "Name source: {source}; name observation FILETIME: {}; source event: {}\nName scope: {}\nNative request result: {:?}; NTSTATUS: {:?}; actor fields: PID {:?} birth {:?}; identity confidence {:?}\nIssuing TID (source field, not a thread birth): {:?}; event/open generation: {:?}\nExplicit application API intent: unavailable; final state validated: {} (not ownership)",
        observed.as_deref().unwrap_or("unavailable"),
        origin.unwrap_or("unavailable"),
        contain_core::reliability::resource_evidence_reason(event),
        event.success,
        event.raw.status,
        event.evidence.pid,
        event.evidence.process_creation_time,
        event.dimensions.actor,
        event.raw.thread_id,
        event.raw.object_generation,
        event.state_validated
    )
}

pub fn summary(capture: &Capture) {
    println!("{SOURCE_SCOPE}");
    println!(
        "────────────────────────────────\n{}\nInstalled: {}\nProcesses: {} tracked\nFilesystem: {} state changes\nRegistry: {} value changes",
        capture.name,
        capture.started_at,
        capture.processes.len(),
        capture.files.len(),
        capture.registry.len()
    );
    for kind in ["service", "scheduled_task", "startup"] {
        println!(
            "{kind}: {} changes",
            capture
                .inventory
                .iter()
                .filter(|item| item.kind == kind)
                .count()
        );
    }
    println!(
        "ETW file: {} | registry: {}",
        capture.backend.etw_file, capture.backend.etw_registry
    );
    println!(
        "Capture quality: {:?}\nReceived: {} | Retained: {} | Normalized: {} | Dropped: {}\nKnown actors: {} | Unknown actors: {} | Unknown resources: {}",
        capture.quality.level,
        capture.stats.events_received,
        capture.stats.events_retained,
        capture.stats.events_normalized,
        capture.stats.events_dropped,
        capture.stats.known_actor_events,
        capture.stats.unknown_actor_events,
        capture.stats.unknown_resource_events
    );
    for reason in &capture.quality.reasons {
        println!("  {reason}");
    }
    if capture.events.is_empty() {
        return;
    }
    println!("\nEvidence quality (source events, including state observations)");
    for confidence in [
        Confidence::Certain,
        Confidence::High,
        Confidence::Medium,
        Confidence::Low,
        Confidence::Unknown,
    ] {
        println!(
            "  {:<8} {}",
            confidence.as_str(),
            capture
                .events
                .iter()
                .filter(|event| event.confidence == confidence)
                .count()
        );
    }
}

pub fn inspect(capture: &Capture, verbose: bool) {
    summary(capture);
    if !verbose {
        println!("Use --verbose for source evidence or explain <event-id> for one observation.");
        return;
    }
    println!(
        "\nInstaller: {}\nExit code: {:?}\nPROCESSES",
        capture.installer, capture.exit_code
    );
    for process in &capture.processes {
        println!(
            "  PID {} birth={:?} parent={:?} [{}] {}\n    {}",
            process.pid,
            process.creation_time,
            process.parent_pid,
            process.confidence.as_str(),
            process.image,
            process.reason
        );
    }
    diff(capture);
    println!("\nSOURCE EVENTS");
    for event in capture
        .events
        .iter()
        .filter(|event| matches!(event.event_type.as_str(), "file" | "registry"))
    {
        println!(
            "  {} recorded name/resource: {}\n    Source actor: {:?} PID {:?}\n    [actor attribution {}] {}\n    Source: {:?}; state validated: {}",
            event.operation,
            event.resource,
            event.evidence.process_image,
            event.evidence.pid,
            event.confidence.as_str(),
            event.reason,
            event.evidence.source,
            event.state_validated
        );
        let scope = source_scope_text(event);
        if !scope.is_empty() {
            println!("    {scope}");
        }
    }
    println!("\nWarnings");
    for warning in &capture.warnings {
        println!("  {warning}");
    }
}

pub fn diff(capture: &Capture) {
    for attributed in [true, false] {
        println!(
            "\n{}",
            if attributed {
                "ATTRIBUTED"
            } else {
                "UNATTRIBUTED"
            }
        );
        for file in &capture.files {
            if (file.confidence != Confidence::Unknown) == attributed {
                println!(
                    "  FILE {} {} [{}]",
                    file.operation,
                    file.path,
                    file.confidence.as_str()
                );
            }
        }
        for registry in &capture.registry {
            if (registry.confidence != Confidence::Unknown) == attributed {
                println!(
                    "  REGISTRY {} {}\\{} [{}]",
                    registry.operation,
                    registry.key,
                    registry.name,
                    registry.confidence.as_str()
                );
            }
        }
        for item in &capture.inventory {
            if (item.confidence != Confidence::Unknown) == attributed {
                println!(
                    "  {} {} {} [{}]",
                    item.kind,
                    item.operation,
                    item.name,
                    item.confidence.as_str()
                );
            }
        }
    }
    println!(
        "Unattributed changes are not assumed to belong to {}.",
        capture.name
    );
}

pub fn history(capture: &Capture) {
    println!("{SOURCE_SCOPE}");
    for event in &capture.events {
        println!(
            "{} {:<20} {:?} PID={:?} [{}] {}",
            event.timestamp,
            event.operation,
            event.evidence.source,
            event.evidence.pid,
            event.confidence.as_str(),
            event.resource
        );
        let scope = source_scope_text(event);
        if !scope.is_empty() {
            println!("  {scope}");
        }
    }
    if capture.events.is_empty() {
        println!("This legacy capture has no source-event timeline.");
    }
}

/// Include the actor's ancestry chain and the operation's exact raw references.
pub fn explanation(c: &Capture, id: &str) -> anyhow::Result<serde_json::Value> {
    let e = c
        .events
        .iter()
        .find(|e| e.id == id)
        .ok_or_else(|| anyhow::anyhow!("event not found"))?;
    let mut nodes = std::collections::BTreeSet::from([format!("event:{id}")]);
    loop {
        let old = nodes.len();
        for edge in &c.edges {
            if nodes.contains(&edge.to) {
                nodes.insert(edge.from.clone());
            }
        }
        if nodes.len() == old {
            break;
        }
    }
    let edges: Vec<_> = c
        .edges
        .iter()
        .filter(|edge| nodes.contains(&edge.to) || edge.from == format!("event:{id}"))
        .collect();
    let operations: Vec<_> = c
        .operations
        .iter()
        .filter(|o| o.raw_events.iter().any(|r| r == id))
        .collect();
    Ok(
        serde_json::json!({"app_id":c.id,"application":c.name,"event":e,"edges":edges,"operations":operations,"quality":c.quality}),
    )
}
pub fn explain(c: &Capture, id: &str) -> anyhow::Result<()> {
    let value = explanation(c, id)?;
    let e = &value["event"];
    println!(
        "Event {id}\nRecorded name/resource: {}\nSource operation: {}\nActor: {} PID {} birth {}\nApplication: {}\nSource actor attribution: {}\nActor / recorded-name resolution / completion evidence: {} / {} / {}\nRequest result: {} (NTSTATUS {})\nEvidence: {}",
        e["resource"],
        e["operation"],
        e["evidence"]["process_image"],
        e["evidence"]["pid"],
        e["evidence"]["process_creation_time"],
        c.name,
        e["confidence"],
        e["dimensions"]["actor"],
        e["dimensions"]["resource"],
        e["dimensions"]["operation"],
        e["success"],
        e["raw"]["status"],
        e["reason"]
    );
    if let Some(event) = c.events.iter().find(|event| event.id == id) {
        println!("{}", source_scope_text(event));
    }
    if let Some(edges) = value["edges"].as_array() {
        for edge in edges {
            println!(
                "  {} -> {} [{}]: {}",
                edge["from"], edge["to"], edge["confidence"], edge["reason"]
            );
        }
    }
    if e["success"].is_null() {
        println!("Limitation: operation completion was not observed.");
    }
    for reason in &c.quality.reasons {
        println!("Limitation: {reason}");
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use contain_core::model::*;

    fn named_write() -> SystemEvent {
        SystemEvent {
            id: "write".into(),
            event_type: "file".into(),
            operation: "write_requested".into(),
            timestamp_ticks: 200,
            resource: r"C:\fixture\old.tmp".into(),
            confidence: Confidence::High,
            success: Some(true),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                pid: Some(7),
                process_creation_time: Some(10),
                ..Default::default()
            },
            raw: RawEvidence {
                resource_resolved: true,
                thread_id: Some(9),
                object_generation: Some("open-generation".into()),
                status: Some(0),
                file_name: Some(Box::new(FileNameEvidence {
                    path_source: "file_object_cache".into(),
                    observed_ticks: 100,
                    source_event_id: "original-open".into(),
                    current_path_verified: false,
                })),
                ..Default::default()
            },
            ..Default::default()
        }
    }

    #[test]
    fn source_scope_separates_native_actor_name_intent_and_state() {
        let event = named_write();
        let text = source_scope_text(&event);
        assert!(text.contains("historical open name"));
        assert!(text.contains("name observation FILETIME: 100; source event: original-open"));
        assert!(text.contains("not a verified current path"));
        assert!(text.contains("Native request result: Some(true)"));
        assert!(text.contains("Issuing TID (source field, not a thread birth): Some(9)"));
        assert!(text.contains("Explicit application API intent: unavailable"));
        assert!(text.contains("final state validated: false (not ownership)"));
        let capture = Capture {
            events: vec![event],
            ..Default::default()
        };
        let json = explanation(&capture, "write").unwrap();
        assert_eq!(json["event"]["confidence"], "High");
        assert_eq!(json["event"]["success"], true);
        assert_eq!(json["event"]["raw"]["file_name"]["observed_ticks"], "100");
        assert_eq!(json["event"]["state_validated"], false);
    }

    #[test]
    fn legacy_name_provenance_stays_unavailable_without_guessing_from_path() {
        let mut event = named_write();
        event.raw.file_name = None;
        let text = source_scope_text(&event);
        assert!(text.contains("Name source: unavailable; name observation FILETIME: unavailable; source event: unavailable"));
        assert!(!text.contains("historical open name"));
        let encoded = serde_json::to_value(&event).unwrap();
        assert!(encoded["raw"].get("file_name").is_none());
        let restored: SystemEvent = serde_json::from_value(encoded).unwrap();
        assert!(restored.raw.file_name.is_none());
        event.evidence.source = EvidenceSource::Snapshot;
        assert!(source_scope_text(&event).is_empty());
    }
}
