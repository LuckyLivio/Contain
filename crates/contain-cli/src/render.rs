use contain_core::model::{Capture, Confidence};

pub fn summary(capture: &Capture) {
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
            "  {} {}\n    Writer: {:?} PID {:?}\n    [{}] {}\n    Source: {:?}; state validated: {}",
            event.operation,
            event.resource,
            event.evidence.process_image,
            event.evidence.pid,
            event.confidence.as_str(),
            event.reason,
            event.evidence.source,
            event.state_validated
        );
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
        "Event {id}\nResource: {}\nOperation: {}\nActor: {} PID {} birth {}\nApplication: {}\nApp attribution: {}\nActor / resource / operation: {} / {} / {}\nResult: {} (NTSTATUS {})\nEvidence: {}",
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
