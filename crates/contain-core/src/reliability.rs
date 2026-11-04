use crate::model::*;

/// Describes the existing recorded-name claim, never a physical identity or a
/// current-path query. Diagnostic flags alone cannot upgrade this statement.
pub fn resource_evidence_reason(e: &SystemEvent) -> &'static str {
    if e.evidence.source == EvidenceSource::EtwFile && e.event_type == "file" {
        let source = e
            .raw
            .file_name
            .as_ref()
            .map(|n| n.path_source.as_str())
            .or_else(|| {
                e.raw
                    .file_diagnostics
                    .as_ref()
                    .map(|d| d.path_source.as_str())
            });
        match source {
            Some("file_object_cache") => {
                "Recorded historical open name from the FileObject cache; not a verified current path or physical file identity. It does not establish an explicit application call or final state ownership."
            }
            Some("supplied_FileName" | "supplied_FilePath") => {
                "Provider supplied this event's recorded name; this is not a handle query of the current path or physical file identity, an explicit application call, or final state ownership."
            }
            _ => {
                "Recorded file name; provider/cache provenance and current path verification are unavailable. Name resolution does not establish physical identity, an explicit application call, or final state ownership."
            }
        }
    } else {
        "Source path or state observation identifies this recorded resource; does not establish exclusive ownership."
    }
}
pub fn finish(c: &mut Capture) {
    for e in &mut c.events {
        e.dimensions.actor = if e.evidence.pid.is_some()
            && e.evidence.process_creation_time.is_some()
            && matches!(
                e.evidence.source,
                EvidenceSource::ProcessApi
                    | EvidenceSource::EtwFile
                    | EvidenceSource::EtwRegistry
                    | EvidenceSource::EtwProcess
            )
            && e.evidence.rule != AttributionRule::ExecutablePathMatch
        {
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
        if let (Some(pid), Some(birth)) = (e.evidence.pid, e.evidence.process_creation_time)
            && e.dimensions.actor == Confidence::High
        {
            c.edges.push(EvidenceEdge {
                from: format!("process:{pid}:{birth}"),
                to: node.clone(),
                relation: "observed_actor".into(),
                confidence: e.dimensions.actor,
                reason: e.reason.clone(),
            });
        } else if e.evidence.rule == AttributionRule::ExecutablePathMatch
            && let (Some(pid), Some(birth)) = (e.evidence.pid, e.evidence.process_creation_time)
        {
            c.edges.push(EvidenceEdge {
                from: format!("process:{pid}:{birth}"),
                to: node.clone(),
                relation: "inventory_target".into(),
                confidence: Confidence::Medium,
                reason: "Inventory executable target matches this process; the configuration writer is unknown.".into(),
            });
        }
        if e.dimensions.resource == Confidence::High {
            c.edges.push(EvidenceEdge {
                from: node,
                to: format!("resource:{}", e.resource),
                relation: e.operation.clone(),
                confidence: e.dimensions.resource,
                reason: resource_evidence_reason(e).into(),
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cached_name_does_not_claim_current_resource_or_application_intent() {
        let mut value = serde_json::to_value(SystemEvent {
            id: "native-write".into(),
            event_type: "file".into(),
            operation: "write_requested".into(),
            resource: r"C:\fixture\old.tmp".into(),
            confidence: Confidence::High,
            success: Some(true),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                pid: Some(4),
                process_creation_time: Some(10),
                ..Default::default()
            },
            raw: RawEvidence {
                resource_resolved: true,
                object_generation: Some("original-open".into()),
                ..Default::default()
            },
            ..Default::default()
        })
        .unwrap();
        let provenance = serde_json::json!({
            "path_source":"file_object_cache",
            "observed_ticks":"134350000000000001",
            "source_event_id":"original-open-event",
            "current_path_verified":false
        });
        value["raw"]["file_name"] = provenance.clone();
        let event: SystemEvent = serde_json::from_value(value).unwrap();
        assert_eq!(
            serde_json::to_value(&event).unwrap()["raw"]["file_name"],
            provenance
        );
        let mut c = Capture {
            events: vec![event],
            ..Default::default()
        };
        finish(&mut c);
        let edge = c
            .edges
            .iter()
            .find(|e| e.to.starts_with("resource:"))
            .unwrap();
        assert!(edge.reason.contains("historical open name"));
        assert!(edge.reason.contains("current path"));
        assert_eq!(c.events[0].confidence, Confidence::High);
        assert_eq!(c.events[0].dimensions.actor, Confidence::High);
        assert_eq!(c.events[0].success, Some(true));
        assert!(!c.events[0].state_validated);
    }
    #[test]
    fn inventory_target_match_does_not_claim_an_observed_writer() {
        let mut capture = Capture {
            events: vec![SystemEvent {
                id: "inventory".into(),
                event_type: "service".into(),
                evidence: AttributionEvidence {
                    source: EvidenceSource::ServiceInventory,
                    rule: AttributionRule::ExecutablePathMatch,
                    pid: Some(1),
                    process_creation_time: Some(10),
                    ..Default::default()
                },
                ..Default::default()
            }],
            ..Default::default()
        };
        finish(&mut capture);
        assert_eq!(capture.events[0].dimensions.actor, Confidence::Unknown);
        assert!(
            !capture
                .edges
                .iter()
                .any(|edge| edge.relation == "observed_actor")
        );
    }
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
                    source: EvidenceSource::EtwRegistry,
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
