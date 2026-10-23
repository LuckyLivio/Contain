//! Clock observations are metadata, never a substitute for source timestamps.
use super::*;

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SourceClock {
    /// Exact signed EVENT_HEADER value delivered by ProcessTrace, without clamping.
    pub header_timestamp: String,
    pub timestamp_domain: String,
    #[serde(default, with = "ticks::optional")]
    pub raw_qpc: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub event_key: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub record_identity: Option<ClockRecordIdentity>,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct ClockRecordIdentity {
    pub provider: String,
    pub event_id: u16,
    pub version: u8,
    pub channel: u8,
    pub level: u8,
    pub opcode: u8,
    pub task: u16,
    pub keyword: String,
    pub process_id: u32,
    pub thread_id: u32,
}

/// Exact numeric fields copied from successful OpenTraceW output. Signed clock
/// values are decimal strings, including zero/negative values; never fabricated.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct OpenTraceHeader {
    pub process_trace_mode: u32,
    pub reserved_flags: u32,
    pub perf_frequency: String,
    pub start_time: String,
    pub boot_time: String,
    pub end_time: String,
    pub buffer_size: u32,
    pub log_file_mode: u32,
    pub pointer_size: u32,
    pub events_lost: u32,
    pub buffers_lost: u32,
    pub timer_resolution: u32,
    pub number_of_processors: u32,
    pub provider_version: u32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ClockCalibration {
    pub phase: String,
    #[serde(with = "ticks")]
    pub qpc_before: u64,
    #[serde(with = "ticks")]
    pub filetime_ticks: u64,
    #[serde(with = "ticks")]
    pub qpc_after: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct TraceClockMetadata {
    pub schema_version: u32,
    pub configuration_source: String,
    pub clock_type: String,
    pub client_context: u32,
    pub process_trace_mode: u32,
    pub delivered_timestamp_domain: String,
    pub raw_qpc_available: bool,
    /// True only after OpenTrace returns an actual numeric header.
    pub trace_header_available: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub trace_header: Option<OpenTraceHeader>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub raw_companion: Option<serde_json::Value>,
    #[serde(default, with = "ticks::optional")]
    pub qpc_frequency: Option<u64>,
    pub calibrations: Vec<ClockCalibration>,
    pub errors: Vec<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn old_evidence_does_not_invent_clock_metadata() {
        let event = SystemEvent::default();
        let value = serde_json::to_value(&event).unwrap();
        assert!(value["raw"].get("clock").is_none());
        let restored: SystemEvent = serde_json::from_value(value).unwrap();
        assert!(restored.raw.clock.is_none());
        let backend = serde_json::to_value(BackendReport::default()).unwrap();
        assert!(backend.get("clock").is_none());
    }

    #[test]
    fn clock_strings_preserve_signed_header_and_large_samples() {
        let source = SourceClock {
            header_timestamp: "-1".into(),
            timestamp_domain: "filetime_100ns".into(),
            raw_qpc: None,
            event_key: None,
            record_identity: None,
        };
        let value = serde_json::to_value(&source).unwrap();
        assert_eq!(value["header_timestamp"], "-1");
        assert!(value["raw_qpc"].is_null());
        let large = 134_350_000_000_000_017;
        let sample = ClockCalibration {
            phase: "test".into(),
            qpc_before: large,
            filetime_ticks: large + 1,
            qpc_after: large + 2,
        };
        let value = serde_json::to_value(&sample).unwrap();
        assert_eq!(value["qpc_before"], large.to_string());
        assert_eq!(
            serde_json::from_value::<ClockCalibration>(value)
                .unwrap()
                .qpc_after,
            large + 2
        );
    }
}
