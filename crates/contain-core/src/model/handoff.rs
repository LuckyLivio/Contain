//! Serialized handoff counters shared by capture and persisted readback.
#[derive(Clone, Debug, Default, serde::Serialize, serde::Deserialize)]
#[serde(default)]
pub struct HandoffStats {
    pub initial_accepted: u64,
    pub received: u64,
    pub submitted: u64,
    pub overflow: u64,
    pub oversized: u64,
    pub disconnected: u64,
    pub serialize_failed: u64,
    pub drained: u64,
    pub pending: u64,
    pub high_water_records: u64,
    pub high_water_bytes: u64,
    pub record_capacity: u64,
    pub byte_capacity: u64,
    pub receiver_finished: bool,
    pub writer_finished: bool,
    pub stop_and_drain_ns: u64,
}
impl HandoffStats {
    pub fn dropped(&self) -> u64 {
        self.overflow + self.oversized + self.disconnected + self.serialize_failed
    }
}
