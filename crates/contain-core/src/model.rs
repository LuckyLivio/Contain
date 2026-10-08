use serde::{Deserialize, Serialize};
mod reliability;
mod ticks;
pub use reliability::*;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum Confidence {
    Certain,
    High,
    Medium,
    Low,
    #[default]
    Unknown,
}

impl Confidence {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Certain => "Certain",
            Self::High => "High",
            Self::Medium => "Medium",
            Self::Low => "Low",
            Self::Unknown => "Unknown",
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct ProcessRecord {
    pub pid: u32,
    pub parent_pid: Option<u32>,
    pub image: String,
    pub first_seen: String,
    pub confidence: Confidence,
    pub reason: String,
    #[serde(default, with = "ticks::optional")]
    pub creation_time: Option<u64>,
    #[serde(default, with = "ticks::optional")]
    pub parent_creation_time: Option<u64>,
    pub last_seen: String,
    #[serde(default, with = "ticks::optional")]
    pub ended_at: Option<u64>,
    pub evidence: AttributionEvidence,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct FileChange {
    pub path: String,
    pub operation: String,
    pub before_hash: Option<String>,
    pub after_hash: Option<String>,
    pub after_size: Option<u64>,
    pub notification_seen: bool,
    pub confidence: Confidence,
    pub reason: String,
    pub evidence: Vec<AttributionEvidence>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct RegistryChange {
    pub key: String,
    pub name: String,
    pub operation: String,
    pub before_value: Option<String>,
    pub after_value: Option<String>,
    pub confidence: Confidence,
    pub reason: String,
    pub evidence: Vec<AttributionEvidence>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Capture {
    #[serde(default)]
    pub capture_state: Option<String>,
    pub schema_version: u32,
    pub id: String,
    pub name: String,
    pub installer: String,
    pub started_at: String,
    pub finished_at: String,
    pub exit_code: Option<i32>,
    pub watch_roots: Vec<String>,
    pub registry_key: Option<String>,
    pub processes: Vec<ProcessRecord>,
    pub files: Vec<FileChange>,
    pub registry: Vec<RegistryChange>,
    pub warnings: Vec<String>,
    pub events: Vec<SystemEvent>,
    pub inventory: Vec<InventoryChange>,
    pub backend: BackendReport,
    #[serde(default)]
    pub stats: CaptureStats,
    #[serde(default)]
    pub quality: CaptureQuality,
    #[serde(default)]
    pub operations: Vec<NormalizedOperation>,
    #[serde(default)]
    pub edges: Vec<EvidenceEdge>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum EvidenceSource {
    ProcessApi,
    EtwFile,
    EtwRegistry,
    EtwProcess,
    FileNotification,
    ServiceInventory,
    TaskInventory,
    StartupRegistry,
    #[default]
    Snapshot,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum AttributionRule {
    InstallerPid,
    DescendantProcess,
    ExecutablePathMatch,
    UnrelatedProcess,
    AmbiguousLifetime,
    MixedWriters,
    EventLoss,
    #[default]
    SnapshotOnly,
    MissingWriter,
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttributionEvidence {
    pub source: EvidenceSource,
    pub pid: Option<u32>,
    /// Windows FILETIME ticks (100 ns since 1601), serialized without losing precision.
    #[serde(default, with = "ticks::optional")]
    pub process_creation_time: Option<u64>,
    pub process_image: Option<String>,
    pub parent_pid: Option<u32>,
    pub ancestor_pid: Option<u32>,
    pub session_id: Option<String>,
    pub rule: AttributionRule,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct SystemEvent {
    pub id: String,
    pub timestamp: String,
    #[serde(with = "ticks")]
    pub timestamp_ticks: u64,
    pub event_type: String,
    pub operation: String,
    pub resource: String,
    pub confidence: Confidence,
    pub reason: String,
    pub evidence: AttributionEvidence,
    /// None means the source does not provide an operation completion status.
    pub success: Option<bool>,
    pub state_validated: bool,
    #[serde(default)]
    pub sequence: u64,
    #[serde(default)]
    pub raw: RawEvidence,
    #[serde(default)]
    pub dimensions: ConfidenceDimensions,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct BackendReport {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub initial_identity_snapshot: Option<InitialIdentitySnapshot>,
    #[serde(default)]
    pub context_losses: u64,
    #[serde(default)]
    pub file_event_id_filter: Option<bool>,
    #[serde(default)]
    pub stream: Option<crate::storage::stream::StreamStats>,
    #[serde(default)]
    pub pipeline: Option<PipelineStats>,
    #[serde(default)]
    pub etw_process: String,
    #[serde(default)]
    pub events_received: u64,
    pub etw_file: String,
    pub etw_registry: String,
    pub dropped_events: u64,
    pub etw_events_lost: Option<u64>,
    #[serde(default)]
    pub etw_buffers_lost: Option<u64>,
    pub decode_errors: u64,
    #[serde(default)]
    pub registry_path_gaps: u64,
    pub warnings: Vec<String>,
}

impl BackendReport {
    pub fn has_loss(&self) -> bool {
        self.dropped_events > 0
            // Storage finalization may fail without losing an individual row.
            // Suppress promotion without inventing a dropped-event count.
            || self.stream.as_ref().is_some_and(|s| s.error.is_some())
            || self.context_losses > 0
            || self.decode_errors > 0
            || self.etw_events_lost.unwrap_or(0) > 0
            || self.etw_buffers_lost.unwrap_or(0) > 0
    }
    pub fn source_intact(&self) -> bool {
        !self.has_loss() && self.etw_events_lost == Some(0) && self.etw_buffers_lost == Some(0)
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ServiceState {
    pub name: String,
    pub display_name: String,
    pub binary_path: String,
    pub startup_type: String,
    pub account: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct TaskAction {
    pub executable: String,
    pub arguments: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ScheduledTaskState {
    pub path: String,
    pub actions: Vec<TaskAction>,
    pub triggers: Vec<String>,
    pub enabled: bool,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct StartupEntry {
    pub source: String,
    pub name: String,
    pub command: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", content = "state")]
pub enum InventoryState {
    Service(ServiceState),
    ScheduledTask(ScheduledTaskState),
    Startup(StartupEntry),
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct InventoryChange {
    pub kind: String,
    pub name: String,
    pub operation: String,
    pub before: Option<InventoryState>,
    pub after: Option<InventoryState>,
    pub confidence: Confidence,
    pub reason: String,
    pub evidence: AttributionEvidence,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CleanupClass {
    Safe,
    Review,
    UserData,
    Unknown,
}

impl CleanupClass {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Safe => "SAFE",
            Self::Review => "REVIEW",
            Self::UserData => "USER_DATA",
            Self::Unknown => "UNKNOWN",
        }
    }
}

pub struct CleanupCandidate {
    pub path: String,
    pub class: CleanupClass,
    pub reason: String,
    pub size: u64,
}
