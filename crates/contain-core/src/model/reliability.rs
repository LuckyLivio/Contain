use super::*;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct RawEvidence {
    /// Name provenance is retained during ordinary capture, independently of the
    /// optional payload diagnostics. It does not establish a current path.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub file_name: Option<Box<FileNameEvidence>>,
    /// A handle query binds an already-running thread to a process instance.
    /// It proves nothing before observed_at and does not establish ownership.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub initial_thread_identity: Option<InitialThreadIdentity>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub clock: Option<SourceClock>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub registry_fields: Option<Box<RegistryFields>>,
    /// Optional diagnostic observations, never an input to attribution or naming.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub file_diagnostics: Option<Box<FileDiagnostics>>,
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

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileNameEvidence {
    pub path_source: String,
    /// Timestamp of the source name observation, not of a later cached use.
    #[serde(with = "ticks")]
    pub observed_ticks: u64,
    pub source_event_id: String,
    /// No current decoder operation performs a handle-based current-name query.
    pub current_path_verified: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct FileDiagnosticField {
    pub status: String,
    /// Exact integers are decimal strings; absent/failed fields remain None.
    pub value: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct FileDiagnosticSchemaField {
    pub name: String,
    pub flags: u32,
    /// Original TDH numbers, not the parser's fallback type enumeration.
    pub in_type: Option<u16>,
    pub out_type: Option<u16>,
    pub length_or_index: u16,
    pub count_or_index: u16,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct FileDiagnosticSchema {
    pub property_count: u32,
    pub top_level_property_count: u32,
    pub fields: Vec<FileDiagnosticSchemaField>,
    pub truncated: bool,
    pub invalid_layout: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct FileCacheOrigin {
    pub event_key: Option<String>,
    pub event_id: u16,
    pub event_timestamp: String,
    pub source_event_id: Option<String>,
    pub object_generation: String,
    pub file_key: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct FilePendingDiagnostic {
    pub action: String,
    pub prior_request_id: Option<String>,
    pub prior_request_timestamp: Option<String>,
    pub current_request_id: Option<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct FileDiagnostics {
    pub schema_version: u32,
    pub descriptor: ClockRecordIdentity,
    pub header_flags: u16,
    pub payload_bytes: usize,
    pub payload_prefix_hex: Option<String>,
    pub payload_truncated: bool,
    pub schema: Option<FileDiagnosticSchema>,
    pub fields: std::collections::BTreeMap<String, FileDiagnosticField>,
    pub path_source: String,
    pub cache_name: Option<String>,
    pub cache_origin: Option<FileCacheOrigin>,
    pub cache_created: bool,
    pub cache_invalidated_reason: Option<String>,
    pub invalidated_cache_origin: Option<FileCacheOrigin>,
    pub pending_irp: Option<FilePendingDiagnostic>,
    /// A cached name is not a query of the object's current path.
    pub current_path_verified: bool,
    pub truncated_fields: Vec<String>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct FileDiagnosticSample {
    pub reason: String,
    pub clock: SourceClock,
    pub diagnostics: FileDiagnostics,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct FileDiagnosticReport {
    pub schema_version: u32,
    pub enabled: bool,
    pub counts: std::collections::BTreeMap<String, u64>,
    /// Admission refers to the existing decoder decision, before queue delivery.
    pub admitted_records: u64,
    pub attached_records: u64,
    pub attached_bytes: u64,
    pub parsed_fields: u64,
    pub missing_fields: u64,
    pub parse_errors: u64,
    pub truncated_records: u64,
    pub budget_dropped: u64,
    pub oversized_records: u64,
    pub record_byte_limit: usize,
    pub total_byte_limit: usize,
    pub cache_entry_limit: usize,
    pub cache_entries_peak: usize,
    pub context_sample_limit: usize,
    pub context_samples_per_reason: usize,
    pub context_byte_limit: usize,
    pub context_bytes: u64,
    pub context_samples_omitted: u64,
    /// First-N samples per fixed reason; not a complete diagnostic event stream.
    pub context_samples: Vec<FileDiagnosticSample>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct InitialThreadIdentity {
    pub thread_id: u32,
    #[serde(with = "ticks")]
    pub thread_creation_time: u64,
    pub owner_pid: u32,
    #[serde(with = "ticks")]
    pub owner_creation_time: u64,
    /// Existing trace_start_requested calibration, not an invented offset.
    #[serde(with = "ticks")]
    pub capture_started_ticks: u64,
    #[serde(with = "ticks")]
    pub observed_at: u64,
}

/// Owned manifest fields retained without borrowing an EVENT_RECORD payload.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct RegistryFields {
    pub provider: String,
    pub version: u8,
    pub base_object: Option<String>,
    pub key_name: String,
    pub base_name: String,
    pub relative_name: String,
    pub value_name: Option<String>,
    /// Diagnostic copies only: never use truncated names to recover a resource.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub diagnostics: Option<RegistryFieldDiagnostics>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct RegistryFieldDiagnostics {
    /// Fixed field/error labels, without arbitrary TDH error text or payloads.
    pub parse_errors: std::collections::BTreeMap<String, String>,
    pub truncated_fields: Vec<String>,
    pub context_owner_available: bool,
    pub path_gap_reason: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct InitialIdentitySnapshot {
    pub started_ticks: u64,
    pub finished_ticks: u64,
    pub enumerated: u64,
    pub queried: u64,
    pub resolved: u64,
    pub failed: u64,
    pub skipped: u64,
    pub query_ns: u64,
    pub elapsed_ns: u64,
    pub thread_enumerated: u64,
    pub thread_queried: u64,
    pub thread_resolved: u64,
    pub thread_failed: u64,
    pub thread_outside_boundary: u64,
    pub thread_query_ns: u64,
    /// A budget-limited enumeration has an unknown remainder, not zero.
    pub thread_unqueried: Option<u64>,
    pub thread_enumeration_complete: bool,
    pub budget_exhausted: bool,
    pub query_errors: std::collections::BTreeMap<String, u64>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct ConfidenceDimensions {
    /// Verified actor instance, not proof of an explicit application API call.
    pub actor: Confidence,
    /// Resolution of the recorded name, not current path or physical identity.
    pub resource: Confidence,
    /// Request result evidence, independently of final state validation.
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
    pub providers: std::collections::BTreeMap<String, ProviderStats>,
    pub arrival_peak_per_100ms: Option<u64>,
    pub overflow_first_ns: Vec<u64>,
    pub overflow_last_ns: Option<u64>,
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

/// Fixed provider labels; counters contain no paths or process identifiers.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct ProviderStats {
    pub callback_records: u64,
    pub identity_queries: u64,
    pub unresolved: u64,
    pub enqueued: u64,
    pub filtered: u64,
    pub overflow: u64,
    pub disconnected: u64,
    pub decode_failed: u64,
    pub context_evictions: u64,
    pub elapsed_ns: std::collections::BTreeMap<String, u64>,
}
