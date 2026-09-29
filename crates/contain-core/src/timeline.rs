use crate::{model::*, windows::native};

pub fn complete(capture: &mut Capture, exited_at: u64) {
    for process in &capture.processes {
        let timestamp_ticks = process.creation_time.unwrap_or(0);
        capture.events.push(SystemEvent {
            id: uuid::Uuid::new_v4().to_string(),
            timestamp: if timestamp_ticks > 0 {
                native::timestamp(timestamp_ticks)
            } else {
                process.first_seen.clone()
            },
            timestamp_ticks,
            event_type: "process".into(),
            operation: if process.confidence == Confidence::Certain {
                "installer_started"
            } else {
                "descendant_started"
            }
            .into(),
            resource: process.image.clone(),
            confidence: process.confidence,
            reason: process.reason.clone(),
            evidence: process.evidence.clone(),
            success: Some(true),
            state_validated: false,
            ..Default::default()
        });
    }
    if let Some(root) = capture
        .processes
        .iter()
        .find(|p| p.confidence == Confidence::Certain)
    {
        capture.events.push(SystemEvent {
            id: uuid::Uuid::new_v4().to_string(),
            timestamp: native::timestamp(exited_at),
            timestamp_ticks: exited_at,
            event_type: "process".into(),
            operation: "installer_exited".into(),
            resource: root.image.clone(),
            confidence: Confidence::Certain,
            reason: format!(
                "Owned installer handle reported exit code {:?}.",
                capture.exit_code
            ),
            evidence: root.evidence.clone(),
            success: capture.exit_code.map(|code| code == 0),
            state_validated: false,
            ..Default::default()
        });
    }
    let timestamp_ticks = native::now_ticks();
    capture.finished_at = native::timestamp(timestamp_ticks);
    for file in &capture.files {
        capture.events.push(SystemEvent { id: uuid::Uuid::new_v4().to_string(), timestamp: capture.finished_at.clone(), timestamp_ticks,
            event_type: "file_state".into(), operation: file.operation.clone(), resource: file.path.clone(), confidence: Confidence::Unknown,
            reason: "Snapshot comparison proves state difference, not the time or identity of its writer. See the file evidence for correlated activity.".into(), evidence: AttributionEvidence::default(), success: None, state_validated: true, ..Default::default() });
    }
    for registry in &capture.registry {
        capture.events.push(SystemEvent { id: uuid::Uuid::new_v4().to_string(), timestamp: capture.finished_at.clone(), timestamp_ticks,
            event_type: "registry_state".into(), operation: registry.operation.clone(), resource: format!("{}\\{}", registry.key, registry.name), confidence: Confidence::Unknown,
            reason: "Scoped value-state comparison; mutation time and writer are not supplied by this source.".into(), evidence: AttributionEvidence::default(), success: None, state_validated: true, ..Default::default() });
    }
    for change in &capture.inventory {
        capture.events.push(SystemEvent {
            id: uuid::Uuid::new_v4().to_string(),
            timestamp: capture.finished_at.clone(),
            timestamp_ticks,
            event_type: change.kind.clone(),
            operation: change.operation.clone(),
            resource: change.name.clone(),
            confidence: change.confidence,
            reason: change.reason.clone(),
            evidence: change.evidence.clone(),
            success: None,
            state_validated: true,
            ..Default::default()
        });
    }
    let mut sequence = capture
        .events
        .iter()
        .map(|e| e.sequence)
        .max()
        .unwrap_or(0)
        .max(capture.backend.events_received);
    for event in &mut capture.events {
        if event.sequence == 0 {
            sequence += 1;
            event.sequence = sequence;
        }
    }
    capture.events.sort_by(|a, b| {
        (a.timestamp_ticks, a.sequence, &a.id).cmp(&(b.timestamp_ticks, b.sequence, &b.id))
    });
}
