//! Owned numeric OpenTrace output. No pointers into Windows-owned memory escape.
use windows::Win32::System::Diagnostics::Etw::EVENT_TRACE_LOGFILEW;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct TraceHeader {
    pub process_trace_mode: u32,
    pub reserved_flags: u32,
    pub perf_frequency: i64,
    pub start_time: i64,
    pub boot_time: i64,
    pub end_time: i64,
    pub buffer_size: u32,
    pub log_file_mode: u32,
    pub pointer_size: u32,
    pub events_lost: u32,
    pub buffers_lost: u32,
    pub timer_resolution: u32,
    pub number_of_processors: u32,
    pub provider_version: u32,
}

impl TraceHeader {
    pub(crate) fn from_logfile(log: &EVENT_TRACE_LOGFILEW) -> Self {
        let header = &log.LogfileHeader;
        Self {
            // Both union members are populated by the successful OpenTraceW.
            process_trace_mode: unsafe { log.Anonymous1.ProcessTraceMode },
            reserved_flags: header.ReservedFlags,
            perf_frequency: header.PerfFreq,
            start_time: header.StartTime,
            boot_time: header.BootTime,
            end_time: header.EndTime,
            buffer_size: header.BufferSize,
            log_file_mode: header.LogFileMode,
            pointer_size: unsafe { header.Anonymous2.Anonymous.PointerSize },
            events_lost: unsafe { header.Anonymous2.Anonymous.EventsLost },
            buffers_lost: header.BuffersLost,
            timer_resolution: header.TimerResolution,
            number_of_processors: header.NumberOfProcessors,
            provider_version: header.ProviderVersion,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn header_snapshot_copies_actual_output_without_inventing_validity() {
        let mut log = EVENT_TRACE_LOGFILEW::default();
        log.Anonymous1.ProcessTraceMode = 0x10001100;
        log.LogfileHeader.ReservedFlags = 3;
        log.LogfileHeader.PerfFreq = -17;
        log.LogfileHeader.StartTime = 134_350_000_000_000_017;
        log.LogfileHeader.BootTime = 0;
        log.LogfileHeader.Anonymous2.Anonymous.PointerSize = 8;
        log.LogfileHeader.Anonymous2.Anonymous.EventsLost = 19;
        let snapshot = TraceHeader::from_logfile(&log);
        assert_eq!(snapshot.process_trace_mode, 0x10001100);
        assert_eq!(snapshot.reserved_flags, 3);
        assert_eq!(snapshot.perf_frequency, -17);
        assert_eq!(snapshot.start_time, 134_350_000_000_000_017);
        assert_eq!(snapshot.boot_time, 0);
        assert_eq!(snapshot.pointer_size, 8);
        assert_eq!(snapshot.events_lost, 19);
    }
}
