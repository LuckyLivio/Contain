use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Confidence {
    Certain,
    High,
    Medium,
    Low,
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

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ProcessRecord {
    pub pid: u32,
    pub parent_pid: Option<u32>,
    pub image: String,
    pub first_seen: String,
    pub confidence: Confidence,
    pub reason: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct FileChange {
    pub path: String,
    pub operation: String,
    pub before_hash: Option<String>,
    pub after_hash: Option<String>,
    pub after_size: Option<u64>,
    pub notification_seen: bool,
    pub confidence: Confidence,
    pub reason: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct RegistryChange {
    pub key: String,
    pub name: String,
    pub operation: String,
    pub before_value: Option<String>,
    pub after_value: Option<String>,
    pub confidence: Confidence,
    pub reason: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Capture {
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
