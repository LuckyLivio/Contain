use crate::model::*;
pub fn finish(c: &mut Capture) {
    for e in &mut c.events {
        e.dimensions.actor =
            if e.evidence.pid.is_some() && e.evidence.process_creation_time.is_some() {
                Confidence::High
            } else {
                Confidence::Unknown
            };
        e.dimensions.resource = if e.raw.resource_resolved
            || matches!(e.event_type.as_str(), "file_state" | "registry_state")
        {
            Confidence::High
        } else {
            Confidence::Unknown
        };
        e.dimensions.operation = if e.success.is_some() {
            Confidence::High
        } else {
            Confidence::Unknown
        };
    }
    let relevant: Vec<_> = c
        .events
        .iter()
        .filter(|e| matches!(e.event_type.as_str(), "file" | "registry"))
        .collect();
    c.stats.events_received = c.backend.events_received;
    c.stats.events_retained = c.events.len() as u64;
    c.stats.events_normalized = c.operations.len() as u64;
    c.stats.events_dropped = c.backend.dropped_events;
    c.stats.known_actor_events = relevant
        .iter()
        .filter(|e| e.dimensions.actor == Confidence::High)
        .count() as u64;
    c.stats.unknown_actor_events = relevant.len() as u64 - c.stats.known_actor_events;
    c.stats.known_resource_events = relevant
        .iter()
        .filter(|e| e.dimensions.resource == Confidence::High)
        .count() as u64;
    c.stats.unknown_resource_events = relevant.len() as u64 - c.stats.known_resource_events;
    c.stats.high_confidence_events = relevant
        .iter()
        .filter(|e| e.confidence == Confidence::High)
        .count() as u64;
    let loss = c.backend.has_loss();
    let gaps = c.stats.snapshot_gaps > 0 || c.stats.notification_gaps > 0;
    let shutdown_failed = c.backend.etw_file == "active"
        && (c.backend.etw_events_lost.is_none() || c.backend.etw_buffers_lost.is_none());
    c.quality.level = if loss || gaps || shutdown_failed || c.stats.drain_timed_out {
        QualityLevel::Incomplete
    } else {
        QualityLevel::Degraded
    };
    c.quality.reasons=vec!["Scoped capture cannot prove a complete machine footprint; unsupported paths and missed provider events remain possible.".into()];
    if c.backend.etw_process != "active" {
        c.quality.reasons.push(
            "ETW process lifecycle unavailable; sampling cannot recover short-lived actors.".into(),
        );
    }
    if c.backend.registry_path_gaps > 0 {
        c.quality.reasons.push(format!(
            "{} registry provider records lacked a verified absolute path.",
            c.backend.registry_path_gaps
        ));
    }
    if loss {
        c.quality.reasons.push(
            "Source loss, decode failure, bounded cache/channel overflow or raw journal/checkpoint failure was reported.".into(),
        );
    }
    if c.stats.drain_timed_out {
        c.quality.reasons.push(
            "Session reached its maximum drain duration; later changes may be missing.".into(),
        );
    }
    if gaps {
        c.quality.reasons.push(format!(
            "{} skipped snapshot entries and {} notification gaps.",
            c.stats.snapshot_gaps, c.stats.notification_gaps
        ));
    }
    if shutdown_failed {
        c.quality
            .reasons
            .push("ETW shutdown did not return loss statistics.".into());
    }
    c.warnings.extend(c.quality.reasons.clone());
    for p in &c.processes {
        let node = format!("process:{}:{}", p.pid, p.creation_time.unwrap_or(0));
        let from = match (p.parent_pid, p.parent_creation_time) {
            (Some(pid), Some(birth)) => format!("process:{pid}:{birth}"),
            _ => format!("session:{}", c.id),
        };
        c.edges.push(EvidenceEdge {
            from,
            to: node,
            relation: "process_ancestry".into(),
            confidence: p.confidence,
            reason: p.reason.clone(),
        });
    }
    for e in &c.events {
        let node = format!("event:{}", e.id);
        if let (Some(pid), Some(birth)) = (e.evidence.pid, e.evidence.process_creation_time) {
            c.edges.push(EvidenceEdge {
                from: format!("process:{pid}:{birth}"),
                to: node.clone(),
                relation: "observed_actor".into(),
                confidence: e.dimensions.actor,
                reason: e.reason.clone(),
            });
        }
        if e.dimensions.resource == Confidence::High {
            c.edges.push(EvidenceEdge{from:node,to:format!("resource:{}",e.resource),relation:e.operation.clone(),confidence:e.dimensions.resource,reason:"Source path or state observation identifies this resource; does not establish exclusive ownership.".into()});
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn context_loss_suppresses_continuity_without_inventing_raw_record_drops() {
        let mut c = Capture::default();
        c.backend.etw_events_lost = Some(0);
        c.backend.etw_buffers_lost = Some(0);
        assert!(c.backend.source_intact());
        c.backend.context_losses = 1;
        finish(&mut c);
        assert!(!c.backend.source_intact());
        assert_eq!(c.stats.events_dropped, 0);
        assert!(matches!(c.quality.level, QualityLevel::Incomplete));
    }
    #[test]
    fn quality_reports_loss_and_keeps_actor_and_resource_independent() {
        let mut c = Capture {
            events: vec![SystemEvent {
                event_type: "registry".into(),
                evidence: AttributionEvidence {
                    pid: Some(1),
                    process_creation_time: Some(10),
                    ..Default::default()
                },
                ..Default::default()
            }],
            ..Default::default()
        };
        c.backend.dropped_events = 1;
        finish(&mut c);
        assert!(matches!(c.quality.level, QualityLevel::Incomplete));
        assert_eq!(c.stats.known_actor_events, 1);
        assert_eq!(c.stats.unknown_resource_events, 1);
        assert_eq!(c.events[0].dimensions.resource, Confidence::Unknown);
        let mut clean = Capture::default();
        finish(&mut clean);
        assert!(matches!(clean.quality.level, QualityLevel::Degraded));
    }
}
