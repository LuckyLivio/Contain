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
    pub phase_ms: std::collections::BTreeMap<String, u64>,
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
#[serde(default)]
pub struct PipelineStats {
    pub callback_records: u64,
    pub deliberately_filtered: u64,
    pub unsupported_records: u64,
    pub decode_attempted: u64,
    pub decode_succeeded: u64,
    pub decode_failed: u64,
    pub failed_without_output: u64,
    pub enqueue_disconnected: u64,
    pub enqueued: u64,
    pub queue_overflow: u64,
    pub dequeued: u64,
    pub queue_pending: u64,
    pub queue_high_water: u64,
    pub queue_max_delay_ns: u64,
    pub context_evictions: u64,
    pub retention_dropped: u64,
    pub persistence_succeeded: Option<u64>,
    pub persistence_failed: Option<u64>,
    pub elapsed_ns: std::collections::BTreeMap<String, u64>,
    pub etw_realtime_buffers_lost: Option<u64>,
    pub etw_log_buffers_lost: Option<u64>,
    pub etw_allocated_buffers: Option<u64>,
    pub etw_buffer_size_kib: Option<u64>,
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
