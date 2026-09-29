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

pub fn inspect(capture: &Capture) {
    summary(capture);
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
