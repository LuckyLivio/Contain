//! Auditable clock samples. No FILETIME-to-QPC inversion or timestamp correction.
use crate::model::{ClockCalibration, SourceClock, TraceClockMetadata};
use windows_sys::Win32::{
    Foundation::FILETIME,
    System::{
        Diagnostics::Etw::{PROCESS_TRACE_MODE_EVENT_RECORD, PROCESS_TRACE_MODE_REAL_TIME},
        Performance::{QueryPerformanceCounter, QueryPerformanceFrequency},
        SystemInformation::GetSystemTimePreciseAsFileTime,
    },
};

pub fn qpc() -> Result<u64, String> {
    let mut ticks = 0_i64;
    // SAFETY: the API receives a valid writable LARGE_INTEGER.
    if unsafe { QueryPerformanceCounter(&mut ticks) } == 0 || ticks < 0 {
        return Err("QueryPerformanceCounter sample unavailable".into());
    }
    Ok(ticks as u64)
}

pub fn frequency() -> Result<u64, String> {
    let mut value = 0_i64;
    // SAFETY: the API receives a valid writable LARGE_INTEGER.
    if unsafe { QueryPerformanceFrequency(&mut value) } == 0 || value <= 0 {
        return Err("QueryPerformanceFrequency unavailable".into());
    }
    Ok(value as u64)
}

pub fn sample(phase: &str) -> Result<ClockCalibration, String> {
    let qpc_before = qpc()?;
    let mut wall = FILETIME::default();
    // SAFETY: wall is a valid writable FILETIME.
    unsafe { GetSystemTimePreciseAsFileTime(&mut wall) };
    let qpc_after = qpc()?;
    if qpc_after < qpc_before {
        return Err("QPC calibration regressed".into());
    }
    Ok(ClockCalibration {
        phase: phase.into(),
        qpc_before,
        filetime_ticks: (u64::from(wall.dwHighDateTime) << 32) | u64::from(wall.dwLowDateTime),
        qpc_after,
    })
}

pub fn record_header(header: i64) -> SourceClock {
    // ferrisetw 1.2.0 sets ClientContext=1, but its consumer does not request
    // RAW_TIMESTAMP. The getter name raw_timestamp() does not change that fact.
    SourceClock {
        header_timestamp: header.to_string(),
        timestamp_domain: "filetime_100ns".into(),
        raw_qpc: None,
    }
}

pub fn metadata() -> TraceClockMetadata {
    let mut value = TraceClockMetadata {
        schema_version: 1,
        configuration_source: "locked_ferrisetw_1.2.0".into(),
        clock_type: "qpc".into(),
        client_context: 1,
        process_trace_mode: PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
        delivered_timestamp_domain: "filetime_100ns".into(),
        raw_qpc_available: false,
        trace_header_available: false,
        qpc_frequency: None,
        calibrations: Vec::new(),
        errors: Vec::new(),
    };
    match frequency() {
        Ok(frequency) => value.qpc_frequency = Some(frequency),
        Err(error) => value.errors.push(error),
    }
    value
}

pub fn observe(metadata: &mut TraceClockMetadata, phase: &str) {
    match sample(phase) {
        Ok(sample) => metadata.calibrations.push(sample),
        Err(error) => metadata.errors.push(format!("{phase}: {error}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn converted_header_never_claims_an_original_qpc() {
        let m = metadata();
        assert!(!m.raw_qpc_available);
        assert!(!m.trace_header_available);
        assert_eq!(m.process_trace_mode & 0x1000, 0); // RAW_TIMESTAMP
        assert_eq!(m.delivered_timestamp_domain, "filetime_100ns");
        for value in [-1, 0, 134_350_000_000_000_017] {
            let raw = record_header(value);
            assert_eq!(raw.header_timestamp, value.to_string());
            assert!(raw.raw_qpc.is_none());
        }
    }

    #[test]
    fn calibration_brackets_precise_wall_clock_without_a_mapping_claim() {
        assert!(frequency().unwrap() > 0);
        let first = sample("before").unwrap();
        let last = sample("after").unwrap();
        assert!(first.qpc_before <= first.qpc_after);
        assert!(first.qpc_after <= last.qpc_before);
        assert!(last.qpc_before <= last.qpc_after);
    }
}
