use contain_core::model::{Capture, Confidence, EvidenceSource, SystemEvent};
use std::fmt::Write;

macro_rules! line {
    ($output:expr, $($args:tt)*) => {
        writeln!($output, $($args)*).expect("writing rendered text")
    };
}

struct Console<'a>(std::io::StdoutLock<'a>);

impl Write for Console<'_> {
    fn write_str(&mut self, text: &str) -> std::fmt::Result {
        std::io::Write::write_all(&mut self.0, text.as_bytes()).map_err(|_| std::fmt::Error)
    }
}

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

fn available(value: &str) -> &str {
    if value.is_empty() {
        "unavailable"
    } else {
        value
    }
}

fn optional_count(value: Option<u64>) -> String {
    value.map_or_else(|| "unavailable".into(), |n| n.to_string())
}

fn final_accounting_available(capture: &Capture) -> bool {
    // A raw journal can exist before drain, snapshot and final provider counters.
    // Its presence cannot turn their default zeros into measured final results.
    capture.capture_state.as_deref() == Some("finished")
        && (capture.backend.pipeline.is_some() || !capture.stats.phase_ms.is_empty())
}

fn recorded_count(value: u64, measured: bool) -> String {
    optional_count((measured || value != 0).then_some(value))
}

fn limitations(capture: &Capture) -> Vec<&str> {
    let mut lines = Vec::new();
    for text in capture
        .quality
        .reasons
        .iter()
        .chain(&capture.warnings)
        .chain(&capture.backend.warnings)
        .map(String::as_str)
        .chain(
            capture
                .backend
                .stream
                .as_ref()
                .and_then(|s| s.error.as_deref()),
        )
    {
        if !lines.contains(&text) {
            lines.push(text);
        }
    }
    lines
}

/// Shared by the CLI and offline examples; it never needs the full event graph.
pub fn summary_text(capture: &Capture) -> String {
    let mut out = String::new();
    summary_to(capture, &mut out);
    out
}

fn summary_to(capture: &Capture, out: &mut impl Write) {
    line!(
        out,
        "{}\nSession: {}\nInstaller: {}",
        capture.name,
        available(&capture.id),
        available(&capture.installer)
    );
    line!(
        out,
        "Started: {} | Recorded end: {} | Installer exit: {}",
        available(&capture.started_at),
        available(&capture.finished_at),
        capture
            .exit_code
            .map_or_else(|| "unavailable".into(), |code| code.to_string())
    );
    line!(
        out,
        "Recording: {} | Quality: {:?}",
        capture
            .capture_state
            .as_deref()
            .unwrap_or("unavailable (legacy record)"),
        capture.quality.level
    );
    if capture
        .capture_state
        .as_deref()
        .is_some_and(|state| state != "finished")
    {
        line!(
            out,
            "Unfinished recording: evidence is provisional; final attribution is unavailable."
        );
    }
    line!(
        out,
        "Watched directories: {}",
        if capture.watch_roots.is_empty() {
            "unavailable".into()
        } else {
            capture.watch_roots.join("; ")
        }
    );
    line!(
        out,
        "HKCU registry scope: {}",
        capture
            .registry_key
            .as_deref()
            .unwrap_or("not selected / not recorded")
    );
    line!(
        out,
        "Sources: ETW process={} | file={} | registry={}",
        available(&capture.backend.etw_process),
        available(&capture.backend.etw_file),
        available(&capture.backend.etw_registry)
    );

    let linked = |p: &&contain_core::model::ProcessRecord| {
        p.creation_time.is_some() && matches!(p.confidence, Confidence::Certain | Confidence::High)
    };
    let linked_count = capture.processes.iter().filter(linked).count();
    line!(
        out,
        "Processes: {} recorded; {} Certain/High links with birth identity; {} other/unresolved",
        capture.processes.len(),
        linked_count,
        capture.processes.len() - linked_count
    );
    for process in capture
        .processes
        .iter()
        .filter(linked)
        .chain(capture.processes.iter().filter(|p| !linked(p)))
        .take(4)
    {
        line!(
            out,
            "  PID {} birth={} [{}] {}",
            process.pid,
            optional_count(process.creation_time),
            process.confidence.as_str(),
            available(&process.image)
        );
    }
    if capture.processes.len() > 4 {
        line!(
            out,
            "  {} more process records; inspect --verbose shows all identities and reasons.",
            capture.processes.len() - 4
        );
    }

    let measured = final_accounting_available(capture);
    let unfinished = capture
        .capture_state
        .as_deref()
        .is_some_and(|state| state != "finished");
    // Storage reconstructs these public-view counters from committed raw rows
    // for unfinished sessions. Other diagnostics remain as originally recorded.
    let view_counts = measured || (unfinished && capture.backend.stream.is_some());
    line!(
        out,
        "Recorded timeline: {} observations (includes state/lifecycle); {} normalized operations",
        recorded_count(capture.stats.events_retained, view_counts),
        if unfinished {
            "unavailable (unfinished)".into()
        } else {
            recorded_count(capture.stats.events_normalized, measured)
        }
    );
    line!(
        out,
        "File/registry source observations: {} known actors | {} unknown actors | {} unresolved names/resources",
        recorded_count(capture.stats.known_actor_events, view_counts),
        recorded_count(capture.stats.unknown_actor_events, view_counts),
        recorded_count(capture.stats.unknown_resource_events, view_counts)
    );
    line!(
        out,
        "Recorded state differences: {} files ({} Unknown), {} registry values ({} Unknown), {} inventory entries",
        capture.files.len(),
        capture
            .files
            .iter()
            .filter(|f| f.confidence == Confidence::Unknown)
            .count(),
        capture.registry.len(),
        capture
            .registry
            .iter()
            .filter(|r| r.confidence == Confidence::Unknown)
            .count(),
        capture.inventory.len()
    );
    line!(
        out,
        "Loss: ETW events={} | ETW buffers={} | application records={} | decode errors={}",
        optional_count(capture.backend.etw_events_lost),
        optional_count(capture.backend.etw_buffers_lost),
        recorded_count(capture.backend.dropped_events, measured),
        recorded_count(
            capture.backend.decode_errors,
            measured && capture.backend.pipeline.is_some()
        )
    );
    line!(
        out,
        "Coverage gaps: snapshot={} | notification={} | drain timed out={}",
        recorded_count(capture.stats.snapshot_gaps, measured),
        recorded_count(capture.stats.notification_gaps, measured),
        if capture.stats.drain_timed_out {
            "yes"
        } else if measured {
            "no"
        } else {
            "unavailable"
        }
    );
    if let Some(stream) = &capture.backend.stream {
        line!(
            out,
            "Raw journal: {} persisted | {} write failures | {} quota refusals",
            stream.persisted,
            stream.failed,
            stream.quota_dropped
        );
    }
    if let Some(handoff) = &capture.backend.handoff
        && (handoff.dropped() != 0
            || handoff.pending != 0
            || !handoff.receiver_finished
            || !handoff.writer_finished)
    {
        line!(
            out,
            "Handoff: {} dropped | {} pending | receiver finished={} | writer finished={}",
            handoff.dropped(),
            handoff.pending,
            handoff.receiver_finished,
            handoff.writer_finished
        );
    }
    let limits = limitations(capture);
    for reason in limits.iter().take(5) {
        line!(out, "Limitation: {reason}");
    }
    if limits.len() > 5 {
        line!(
            out,
            "{} more limitations; inspect --verbose shows all.",
            limits.len() - 5
        );
    }
    line!(
        out,
        "State differences are not ownership. A recorded name may be historical; source confidence does not prove an explicit application API call or a current path."
    );
    line!(
        out,
        "Next (reuse the same --db): inspect {} --verbose; diff {}; history {} --limit 20 --offset 0; explain <event-id>.",
        available(&capture.id),
        available(&capture.id),
        available(&capture.id)
    );
}

pub fn summary(capture: &Capture) {
    let stdout = std::io::stdout();
    summary_to(capture, &mut Console(stdout.lock()));
}

pub fn inspect(capture: &Capture, verbose: bool) {
    let stdout = std::io::stdout();
    inspect_to(capture, verbose, &mut Console(stdout.lock()));
}

pub fn inspect_text(capture: &Capture, verbose: bool) -> String {
    let mut out = String::new();
    inspect_to(capture, verbose, &mut out);
    out
}

fn inspect_to(capture: &Capture, verbose: bool, out: &mut impl Write) {
    summary_to(capture, out);
    if !verbose {
        return;
    }
    line!(
        out,
        "\nInstaller: {}\nExit code: {:?}\nPROCESSES",
        capture.installer,
        capture.exit_code
    );
    for process in &capture.processes {
        line!(
            out,
            "  PID {} birth={:?} parent={:?} [{}] {}\n    {}",
            process.pid,
            process.creation_time,
            process.parent_pid,
            process.confidence.as_str(),
            process.image,
            process.reason
        );
    }
    diff_to(capture, out);
    line!(out, "\nSOURCE EVENTS");
    for event in capture
        .events
        .iter()
        .filter(|event| matches!(event.event_type.as_str(), "file" | "registry"))
    {
        line!(
            out,
            "  Event {}: {} recorded name/resource: {}\n    Source actor: {:?} PID {:?}\n    [actor attribution {}] {}\n    Source: {:?}; state validated: {}",
            event.id,
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
            line!(out, "    {scope}");
        }
    }
    line!(out, "\nWarnings");
    for warning in limitations(capture) {
        line!(out, "  {warning}");
    }
}

pub fn diff(capture: &Capture) {
    let stdout = std::io::stdout();
    diff_to(capture, &mut Console(stdout.lock()));
}

pub fn diff_text(capture: &Capture) -> String {
    let mut out = String::new();
    diff_to(capture, &mut out);
    out
}

fn diff_to(capture: &Capture, out: &mut impl Write) {
    for attributed in [true, false] {
        line!(
            out,
            "\n{}",
            if attributed {
                "STATE DIFFERENCES WITH ATTRIBUTION EVIDENCE"
            } else {
                "STATE DIFFERENCES WITH UNKNOWN ATTRIBUTION"
            }
        );
        for file in &capture.files {
            if (file.confidence != Confidence::Unknown) == attributed {
                line!(
                    out,
                    "  FILE {} {} [{}]",
                    file.operation,
                    file.path,
                    file.confidence.as_str()
                );
            }
        }
        for registry in &capture.registry {
            if (registry.confidence != Confidence::Unknown) == attributed {
                line!(
                    out,
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
                line!(
                    out,
                    "  {} {} {} [{}]",
                    item.kind,
                    item.operation,
                    item.name,
                    item.confidence.as_str()
                );
            }
        }
    }
    line!(
        out,
        "State differences are not ownership. Unattributed changes are not assumed to belong to {}.",
        capture.name
    );
}

pub fn history(capture: &Capture) {
    let stdout = std::io::stdout();
    history_to(capture, &mut Console(stdout.lock()));
}

pub fn history_text(capture: &Capture) -> String {
    let mut out = String::new();
    history_to(capture, &mut out);
    out
}

fn history_to(capture: &Capture, out: &mut impl Write) {
    line!(out, "{SOURCE_SCOPE}");
    for event in &capture.events {
        line!(
            out,
            "{} | event={} | {:<20} {:?} PID={:?} [{}] {}",
            event.timestamp,
            event.id,
            event.operation,
            event.evidence.source,
            event.evidence.pid,
            event.confidence.as_str(),
            event.resource
        );
        let scope = source_scope_text(event);
        if !scope.is_empty() {
            line!(out, "  {scope}");
        }
    }
    if capture.events.is_empty() {
        line!(
            out,
            "No events in this view. An empty page does not establish that no events occurred."
        );
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
    let stdout = std::io::stdout();
    explain_to(c, id, &mut Console(stdout.lock()))
}

pub fn explain_text(c: &Capture, id: &str) -> anyhow::Result<String> {
    let mut out = String::new();
    explain_to(c, id, &mut out)?;
    Ok(out)
}

fn explain_to(c: &Capture, id: &str, out: &mut impl Write) -> anyhow::Result<()> {
    let value = explanation(c, id)?;
    let e = &value["event"];
    line!(
        out,
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
        line!(out, "{}", source_scope_text(event));
    }
    if let Some(edges) = value["edges"].as_array() {
        for edge in edges {
            line!(
                out,
                "  {} -> {} [{}]: {}",
                edge["from"],
                edge["to"],
                edge["confidence"],
                edge["reason"]
            );
        }
    }
    if e["success"].is_null() {
        line!(out, "Limitation: operation completion was not observed.");
    }
    for reason in &c.quality.reasons {
        line!(out, "Limitation: {reason}");
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

    fn finished_capture() -> Capture {
        Capture {
            schema_version: 3,
            capture_state: Some("finished".into()),
            id: "synthetic-session".into(),
            name: "Synthetic Reader".into(),
            installer: r"C:\synthetic\setup.exe".into(),
            started_at: "2026-01-01T00:00:00Z".into(),
            finished_at: "2026-01-01T00:00:01Z".into(),
            exit_code: Some(0),
            watch_roots: vec![r"C:\synthetic\watched".into()],
            registry_key: Some(r"Software\Synthetic".into()),
            processes: vec![ProcessRecord {
                pid: 7,
                creation_time: Some(10),
                confidence: Confidence::Certain,
                image: "synthetic-setup.exe".into(),
                reason: "Owned installer handle with birth identity".into(),
                ..Default::default()
            }],
            files: vec![FileChange {
                path: r"C:\synthetic\watched\data".into(),
                operation: "Created".into(),
                ..Default::default()
            }],
            events: vec![named_write()],
            stats: CaptureStats {
                events_retained: 8,
                events_normalized: 1,
                known_actor_events: 1,
                ..Default::default()
            },
            backend: BackendReport {
                etw_process: "active".into(),
                etw_file: "active".into(),
                etw_registry: "not_requested".into(),
                etw_events_lost: Some(0),
                etw_buffers_lost: None,
                pipeline: Some(PipelineStats::default()),
                ..Default::default()
            },
            ..Default::default()
        }
    }

    #[test]
    fn summary_uses_persisted_counters_and_answers_scope_status_and_next_steps() {
        let mut capture = finished_capture();
        let expected = summary_text(&capture);
        assert!(expected.contains("Installer: C:\\synthetic\\setup.exe"));
        assert!(expected.contains("Recording: finished | Quality: Degraded"));
        assert!(expected.contains("Watched directories: C:\\synthetic\\watched"));
        assert!(expected.contains("HKCU registry scope: Software\\Synthetic"));
        assert!(
            expected.contains("Sources: ETW process=active | file=active | registry=not_requested")
        );
        assert!(expected.contains("1 Certain/High links with birth identity"));
        assert!(expected.contains("PID 7 birth=10 [Certain] synthetic-setup.exe"));
        assert!(expected.contains(
            "Recorded timeline: 8 observations (includes state/lifecycle); 1 normalized operations"
        ));
        assert!(expected.contains("1 known actors | 0 unknown actors"));
        assert!(expected.contains("Recorded state differences: 1 files (1 Unknown)"));
        assert!(expected.contains("ETW events=0 | ETW buffers=unavailable"));
        assert!(
            expected.contains("Next (reuse the same --db): inspect synthetic-session --verbose")
        );
        assert!(!expected.contains("Installed:"));
        capture.events.clear();
        capture.operations.clear();
        capture.edges.clear();
        assert_eq!(
            summary_text(&capture),
            expected,
            "summary read must not require full graph materialization"
        );
    }

    #[test]
    fn missing_legacy_accounting_is_unavailable_not_zero_or_finished() {
        let mut encoded = serde_json::to_value(Capture::default()).unwrap();
        for key in ["stats", "capture_state"] {
            encoded.as_object_mut().unwrap().remove(key);
        }
        let capture: Capture = serde_json::from_value(encoded).unwrap();
        let text = inspect_text(&capture, false);
        assert!(text.contains("Recording: unavailable (legacy record)"));
        assert!(text.contains("Recorded timeline: unavailable observations"));
        assert!(text.contains("ETW events=unavailable | ETW buffers=unavailable"));
        assert!(text.contains("application records=unavailable | decode errors=unavailable"));
        assert!(text.contains("drain timed out=unavailable"));
        assert!(text.contains("Installer exit: unavailable"));
        assert!(!text.contains("Recording: finished"));
    }

    #[test]
    fn nonzero_legacy_counters_survive_without_inventing_missing_zeros() {
        let mut capture = Capture::default();
        capture.stats.events_retained = 4;
        capture.backend.dropped_events = 2;
        let text = summary_text(&capture);
        assert!(text.contains("Recorded timeline: 4 observations"));
        assert!(text.contains("application records=2"));
        assert!(text.contains("decode errors=unavailable"));
    }

    #[test]
    fn unfinished_summary_exposes_failures_and_pending_work() {
        let mut capture = finished_capture();
        capture.capture_state = Some("failed".into());
        capture.quality.level = QualityLevel::Incomplete;
        capture.stats.drain_timed_out = true;
        capture.stats.snapshot_gaps = 2;
        capture.backend.stream = Some(contain_core::storage::stream::StreamStats {
            persisted: 3,
            failed: 1,
            quota_dropped: 4,
            error: Some("synthetic write failure".into()),
            ..Default::default()
        });
        capture.backend.handoff = Some(HandoffStats {
            pending: 2,
            ..Default::default()
        });
        let text = summary_text(&capture);
        assert!(text.contains("Recording: failed | Quality: Incomplete"));
        assert!(text.contains("Unfinished recording: evidence is provisional"));
        assert!(text.contains("snapshot=2 | notification=unavailable | drain timed out=yes"));
        assert!(text.contains("Raw journal: 3 persisted | 1 write failures | 4 quota refusals"));
        assert!(text.contains("2 pending | receiver finished=false | writer finished=false"));
        assert!(text.contains("Limitation: synthetic write failure"));
    }

    #[test]
    fn default_limits_warnings_without_hiding_the_remainder_or_verbose_evidence() {
        let mut capture = finished_capture();
        capture.quality.reasons = vec!["same limitation".into()];
        capture.warnings = vec!["same limitation".into()];
        capture.backend.warnings = (0..6).map(|i| format!("synthetic warning {i}")).collect();
        let summary = inspect_text(&capture, false);
        assert_eq!(summary.matches("Limitation: same limitation").count(), 1);
        assert!(summary.contains("2 more limitations; inspect --verbose shows all."));
        assert!(!summary.contains("synthetic warning 5"));
        let detail = inspect_text(&capture, true);
        assert!(detail.contains("synthetic warning 5"));
        assert!(detail.contains("Owned installer handle with birth identity"));
        assert!(detail.contains("Event write: write_requested"));
        assert!(detail.contains("historical open name"));
        assert!(detail.contains("[actor attribution High]"));
    }

    #[test]
    fn process_preview_is_bounded_and_missing_birth_is_not_a_verified_link() {
        let mut capture = finished_capture();
        for pid in 8..13 {
            capture.processes.push(ProcessRecord {
                pid,
                confidence: Confidence::High,
                ..Default::default()
            });
        }
        let text = summary_text(&capture);
        assert!(
            text.contains(
                "6 recorded; 1 Certain/High links with birth identity; 5 other/unresolved"
            )
        );
        assert!(text.contains("2 more process records"));
        assert!(!text.contains("PID 12"));
        assert!(inspect_text(&capture, true).contains("PID 12"));
    }

    #[test]
    fn history_exposes_explain_ids_and_does_not_mislabel_an_empty_page_as_legacy() {
        let mut capture = finished_capture();
        let text = history_text(&capture);
        assert!(text.contains("event=write | write_requested"));
        assert!(text.contains("historical open name"));
        assert!(text.contains("[High]"));
        let explained = explain_text(&capture, "write").unwrap();
        assert!(explained.contains("Event write"));
        assert!(explained.contains("historical open name"));
        assert!(explain_text(&capture, "missing").is_err());
        capture.events.clear();
        let empty = history_text(&capture);
        assert!(empty.contains(
            "No events in this view. An empty page does not establish that no events occurred."
        ));
        assert!(!empty.contains("legacy capture"));
    }

    #[test]
    fn diff_separates_state_attribution_without_claiming_ownership() {
        let capture = finished_capture();
        let text = diff_text(&capture);
        assert!(text.contains("STATE DIFFERENCES WITH UNKNOWN ATTRIBUTION\n  FILE Created"));
        assert!(text.contains("[Unknown]"));
        assert!(text.contains("State differences are not ownership."));
    }

    #[test]
    fn unfinished_journal_does_not_make_unmeasured_final_diagnostics_zero() {
        let mut capture = finished_capture();
        capture.capture_state = Some("capturing".into());
        capture.backend.pipeline = None;
        capture.backend.stream = Some(contain_core::storage::stream::StreamStats {
            persisted: 3,
            ..Default::default()
        });
        capture.backend.etw_events_lost = None;
        capture.backend.etw_buffers_lost = None;
        // These are the committed-raw projection reconstructed by Storage.
        capture.stats.events_retained = 3;
        capture.stats.events_normalized = 0;
        capture.stats.known_actor_events = 0;
        capture.stats.unknown_actor_events = 3;
        let text = summary_text(&capture);
        assert!(text.contains("Recorded timeline: 3 observations"));
        assert!(text.contains("unavailable (unfinished) normalized operations"));
        assert!(text.contains("0 known actors | 3 unknown actors"));
        assert!(text.contains(
            "snapshot=unavailable | notification=unavailable | drain timed out=unavailable"
        ));
        assert!(text.contains("application records=unavailable | decode errors=unavailable"));
        assert!(text.contains("ETW events=unavailable | ETW buffers=unavailable"));
        assert!(text.contains("Raw journal: 3 persisted | 0 write failures | 0 quota refusals"));
        assert!(text.contains("Recorded state differences:"));
        capture.backend.pipeline = Some(PipelineStats::default());
        capture.stats.phase_ms.insert("raw_persistence".into(), 1);
        let with_partial_metadata = summary_text(&capture);
        assert!(with_partial_metadata.contains(
            "snapshot=unavailable | notification=unavailable | drain timed out=unavailable"
        ));
        assert!(
            with_partial_metadata
                .contains("application records=unavailable | decode errors=unavailable")
        );
    }
}
