use super::*;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct RawEvidence {
    pub event_id: Option<u16>,
    pub thread_id: Option<u32>,
    pub header_pid: Option<u32>,
    pub object_generation: Option<String>,
    pub registry_object: Option<String>,
    pub file_object: Option<String>,
    pub file_key: Option<String>,
    pub irp: Option<String>,
    pub status: Option<u32>,
    pub process_key: Option<String>,
    pub parent_key: Option<String>,
    pub related_event: Option<String>,
    pub resource_resolved: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct ConfidenceDimensions {
    pub actor: Confidence,
    pub resource: Confidence,
    pub operation: Confidence,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct CaptureStats {
    pub snapshot_gaps: u64,
    pub notification_gaps: u64,
    pub events_received: u64,
    pub events_retained: u64,
    pub events_normalized: u64,
    pub events_dropped: u64,
    pub known_actor_events: u64,
    pub unknown_actor_events: u64,
    pub known_resource_events: u64,
    pub unknown_resource_events: u64,
    pub high_confidence_events: u64,
    pub capture_elapsed_ms: u64,
    pub drain_timed_out: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub enum QualityLevel {
    Complete,
    #[default]
    Degraded,
    Incomplete,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct CaptureQuality {
    pub level: QualityLevel,
    pub reasons: Vec<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct NormalizedOperation {
    pub id: String,
    pub operation: String,
    pub resource: String,
    pub previous_path: Option<String>,
    pub resource_identity: Option<String>,
    pub raw_events: Vec<String>,
    pub success: Option<bool>,
    pub confidence: Confidence,
    pub reason: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct EvidenceEdge {
    pub from: String,
    pub to: String,
    pub relation: String,
    pub confidence: Confidence,
    pub reason: String,
}
