//! Small Win32 wrappers. All owned handles are closed in this module.
use chrono::{DateTime, Utc};
use std::mem::{size_of, zeroed};
use std::os::windows::io::{AsRawHandle, BorrowedHandle};
use std::ptr::null_mut;
use windows_sys::Win32::Foundation::{CloseHandle, FILETIME};
use windows_sys::Win32::Security::{
    GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation,
};
use windows_sys::Win32::Storage::FileSystem::QueryDosDeviceW;
use windows_sys::Win32::System::Threading::*;

const EPOCH: u64 = 116_444_736_000_000_000;

pub fn now_ticks() -> u64 {
    (Utc::now().timestamp_nanos_opt().unwrap_or(0) / 100) as u64 + EPOCH
}

pub fn timestamp(ticks: u64) -> String {
    let nanos = (i128::from(ticks) - i128::from(EPOCH)) * 100;
    let secs = nanos.div_euclid(1_000_000_000) as i64;
    DateTime::<Utc>::from_timestamp(secs, nanos.rem_euclid(1_000_000_000) as u32)
        .map(|time| time.to_rfc3339())
        .unwrap_or_default()
}

fn ticks(time: FILETIME) -> u64 {
    (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime)
}

#[derive(Clone, Debug)]
pub struct ProcessIdentity {
    pub pid: u32,
    pub creation_time: u64,
    pub image: String,
}

pub fn process_identity(pid: u32) -> Option<ProcessIdentity> {
    // SAFETY: read-only process query; output buffers are valid and handle is closed below.
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if handle.is_null() {
            return None;
        }
        let result = identity_from_handle(BorrowedHandle::borrow_raw(handle), pid);
        CloseHandle(handle);
        result
    }
}

/// Caller supplies a live process handle; this function borrows, never closes it.
pub fn identity_from_handle(handle: BorrowedHandle<'_>, pid: u32) -> Option<ProcessIdentity> {
    let handle = handle.as_raw_handle();
    // SAFETY: Windows validates the handle; all four FILETIME outputs and image buffer are allocated.
    unsafe {
        let mut created = zeroed();
        let mut exited = zeroed();
        let mut kernel = zeroed();
        let mut user = zeroed();
        if GetProcessTimes(handle, &mut created, &mut exited, &mut kernel, &mut user) == 0 {
            return None;
        }
        let mut image = vec![0u16; 32768];
        let mut length = image.len() as u32;
        if QueryFullProcessImageNameW(handle, 0, image.as_mut_ptr(), &mut length) == 0 {
            return None;
        }
        Some(ProcessIdentity {
            pid,
            creation_time: ticks(created),
            image: String::from_utf16_lossy(&image[..length as usize]),
        })
    }
}

pub fn writer_from_thread(tid: u32, event_ticks: u64) -> Option<ProcessIdentity> {
    // SAFETY: read-only query. Creation time rejects a reused TID born after the event.
    unsafe {
        let handle = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, 0, tid);
        if handle.is_null() {
            return None;
        }
        let mut created = zeroed();
        let mut exited = zeroed();
        let mut kernel = zeroed();
        let mut user = zeroed();
        let ok = GetThreadTimes(handle, &mut created, &mut exited, &mut kernel, &mut user) != 0;
        let pid = GetProcessIdOfThread(handle);
        CloseHandle(handle);
        if !ok || ticks(created) > event_ticks {
            return None;
        }
        process_identity(pid).filter(|process| process.creation_time <= event_ticks)
    }
}

pub fn elevated() -> bool {
    // SAFETY: token is queried only; the allocated output matches TOKEN_ELEVATION.
    unsafe {
        let mut token = null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return false;
        }
        let mut elevation: TOKEN_ELEVATION = zeroed();
        let mut size = 0;
        let ok = GetTokenInformation(
            token,
            TokenElevation,
            (&mut elevation as *mut TOKEN_ELEVATION).cast(),
            size_of::<TOKEN_ELEVATION>() as u32,
            &mut size,
        ) != 0;
        CloseHandle(token);
        ok && elevation.TokenIsElevated != 0
    }
}

pub fn current_user_sid() -> Option<String> {
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Authorization::ConvertSidToStringSidW;
    use windows_sys::Win32::Security::{TOKEN_USER, TokenUser};
    // SAFETY: token buffer is u64-aligned, sized from the API; SID/string lifetimes end after copying.
    unsafe {
        let mut token = null_mut();
        if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
            return None;
        }
        let mut length = 0;
        GetTokenInformation(token, TokenUser, null_mut(), 0, &mut length);
        let mut buffer = vec![0u64; (length as usize).div_ceil(8)];
        let ok = GetTokenInformation(
            token,
            TokenUser,
            buffer.as_mut_ptr().cast(),
            length,
            &mut length,
        ) != 0;
        CloseHandle(token);
        if !ok {
            return None;
        }
        let user = &*buffer.as_ptr().cast::<TOKEN_USER>();
        let mut string = null_mut();
        if ConvertSidToStringSidW(user.User.Sid, &mut string) == 0 {
            return None;
        }
        let mut len = 0;
        while *string.add(len) != 0 {
            len += 1;
        }
        let sid = String::from_utf16_lossy(std::slice::from_raw_parts(string, len));
        LocalFree(string.cast());
        Some(sid)
    }
}

pub fn device_map() -> Vec<(String, String)> {
    let mut result = Vec::new();
    for letter in b'A'..=b'Z' {
        let drive = format!("{}:", letter as char);
        let name: Vec<u16> = drive.encode_utf16().chain(Some(0)).collect();
        let mut buffer = vec![0u16; 32768];
        // SAFETY: null-terminated input and writable buffer are valid for the synchronous call.
        let len =
            unsafe { QueryDosDeviceW(name.as_ptr(), buffer.as_mut_ptr(), buffer.len() as u32) };
        if len > 0 {
            let end = buffer.iter().position(|&x| x == 0).unwrap_or(len as usize);
            result.push((String::from_utf16_lossy(&buffer[..end]), drive));
        }
    }
    result
}

pub fn normalize_path(path: &str, devices: &[(String, String)]) -> String {
    let mut path = path
        .trim_start_matches("\\\\?\\")
        .trim_start_matches("\\??\\")
        .to_string();
    for (device, drive) in devices {
        if path
            .to_ascii_lowercase()
            .starts_with(&format!("{}\\", device.to_ascii_lowercase()))
        {
            path = format!("{drive}{}", &path[device.len()..]);
            break;
        }
    }
    path.replace('/', "\\").to_lowercase()
}

pub fn in_scope(path: &str, roots: &[String]) -> bool {
    roots.iter().any(|root| {
        path == root
            || path
                .strip_prefix(root)
                .is_some_and(|tail| tail.starts_with('\\'))
    })
}

#[repr(C)]
struct TracePropertiesBuffer {
    properties: windows_sys::Win32::System::Diagnostics::Etw::EVENT_TRACE_PROPERTIES,
    name: [u16; 256],
}

/// Query/flush/stop only the random session name owned by the caller.
pub fn control_trace(name: &str, control: u32) -> Result<TraceLoss, u32> {
    use windows_sys::Win32::System::Diagnostics::Etw::*;
    // SAFETY: repr(C) storage has the alignment, capacity and offsets expected by ControlTraceW.
    unsafe {
        let mut buffer: TracePropertiesBuffer = zeroed();
        buffer.properties.Wnode.BufferSize = size_of::<TracePropertiesBuffer>() as u32;
        buffer.properties.LoggerNameOffset = size_of::<EVENT_TRACE_PROPERTIES>() as u32;
        let name: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
        let status = ControlTraceW(
            CONTROLTRACE_HANDLE { Value: 0 },
            name.as_ptr(),
            &mut buffer.properties,
            control,
        );
        if status != 0 {
            return Err(status);
        }
        Ok(TraceLoss {
            events: u64::from(buffer.properties.EventsLost),
            realtime_buffers: u64::from(buffer.properties.RealTimeBuffersLost),
            allocated_buffers: u64::from(buffer.properties.NumberOfBuffers),
            buffer_size_kib: u64::from(buffer.properties.BufferSize),
            buffers: u64::from(buffer.properties.LogBuffersLost)
                + u64::from(buffer.properties.RealTimeBuffersLost),
        })
    }
}

#[derive(Debug)]
pub struct TraceLoss {
    pub realtime_buffers: u64,
    pub allocated_buffers: u64,
    pub buffer_size_kib: u64,
    pub events: u64,
    pub buffers: u64,
}

/// Private ephemeral ETW provider used only to acknowledge the realtime consumer.
pub struct TraceProbe(windows_sys::Win32::System::Diagnostics::Etw::REGHANDLE);
impl TraceProbe {
    pub fn register(id: u128) -> Result<Self, u32> {
        use windows_sys::Win32::System::Diagnostics::Etw::EventRegister;
        let guid = windows_sys::core::GUID::from_u128(id);
        let mut handle = 0;
        // SAFETY: valid GUID/output; no callback or external context, handle is owned until Drop.
        let status = unsafe { EventRegister(&guid, None, std::ptr::null(), &mut handle) };
        if status == 0 {
            Ok(Self(handle))
        } else {
            Err(status)
        }
    }
    pub fn emit(&self) -> Result<(), u32> {
        use windows_sys::Win32::System::Diagnostics::Etw::EventWriteString;
        let text: Vec<u16> = "Contain consumer readiness"
            .encode_utf16()
            .chain(Some(0))
            .collect();
        // SAFETY: owned registration and a live nul-terminated string; no user data emitted.
        let status = unsafe { EventWriteString(self.0, 4, 1, text.as_ptr()) };
        if status == 0 { Ok(()) } else { Err(status) }
    }
}
impl Drop for TraceProbe {
    fn drop(&mut self) {
        // SAFETY: this registration is owned exactly once by TraceProbe.
        unsafe {
            windows_sys::Win32::System::Diagnostics::Etw::EventUnregister(self.0);
        }
    }
}

/// ferrisetw enables providers asynchronously. Wait for configuration before launch.
pub fn synchronize_providers(name: &str, registry: bool, filter_ids: bool) -> Result<(), u32> {
    use windows_sys::Win32::System::Diagnostics::Etw::*;
    // SAFETY: query our UUID session into aligned storage, then reapply exactly its provider
    // configuration with a bounded synchronous timeout. No global logger is modified.
    unsafe {
        let mut buffer: TracePropertiesBuffer = zeroed();
        buffer.properties.Wnode.BufferSize = size_of::<TracePropertiesBuffer>() as u32;
        buffer.properties.LoggerNameOffset = size_of::<EVENT_TRACE_PROPERTIES>() as u32;
        let name: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
        let status = ControlTraceW(
            CONTROLTRACE_HANDLE { Value: 0 },
            name.as_ptr(),
            &mut buffer.properties,
            EVENT_TRACE_CONTROL_QUERY,
        );
        if status != 0 {
            return Err(status);
        }
        let handle = CONTROLTRACE_HANDLE {
            Value: buffer.properties.Wnode.Anonymous1.HistoricalContext,
        };
        let providers = [
            (0xedd08927_9cc4_4e65_b970_c2560fb5c289u128, 4, 0x1ef0, true),
            (0x22fb2cd6_0e7b_422b_a0c7_2fad1fd0e716u128, 5, 0x30, false),
            (0x70eb4f03_c1de_4f73_a051_33d13d5413bdu128, 4, 0x7301, true),
        ];
        for (i, (id, level, keywords, start_key)) in providers.into_iter().enumerate() {
            if i == 2 && !registry {
                continue;
            }
            let guid = windows_sys::core::GUID::from_u128(id);
            let mut params: ENABLE_TRACE_PARAMETERS = zeroed();
            params.Version = ENABLE_TRACE_PARAMETERS_VERSION_2;
            params.EnableProperty = if start_key {
                EVENT_ENABLE_PROPERTY_PROCESS_START_KEY
            } else {
                0
            };
            // ferrisetw owns the allocation and alignment of the variable-length
            // EVENT_FILTER_EVENT_ID payload. Both owner and descriptor stay alive
            // across synchronous EnableTraceEx2. Only numeric descriptor fields
            // are copied between windows-rs and windows-sys representations.
            let filter = if i == 0 && filter_ids {
                Some(
                    ferrisetw::provider::EventFilter::ByEventIds(
                        super::etw::FILE_EVENT_IDS.to_vec(),
                    )
                    .to_event_filter_descriptor()
                    .map_err(|_| 87u32)?,
                )
            } else {
                None
            };
            let mut descriptor = filter.as_ref().map(|owner| {
                let raw = owner.as_event_filter_descriptor();
                EVENT_FILTER_DESCRIPTOR {
                    Ptr: raw.Ptr,
                    Size: raw.Size,
                    Type: raw.Type,
                }
            });
            if let Some(descriptor) = &mut descriptor {
                params.EnableFilterDesc = descriptor;
                params.FilterDescCount = 1;
            }
            let status = EnableTraceEx2(
                handle,
                &guid,
                EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                level,
                keywords,
                0,
                5000,
                &params,
            );
            if status != 0 {
                return Err(status);
            }
        }
        Ok(())
    }
}

pub fn file_identity(handle: BorrowedHandle<'_>) -> Option<String> {
    use windows_sys::Win32::Storage::FileSystem::{
        BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle,
    };
    // SAFETY: valid borrowed file handle and correctly sized writable output. No ownership transfer.
    unsafe {
        let mut info: BY_HANDLE_FILE_INFORMATION = zeroed();
        if GetFileInformationByHandle(handle.as_raw_handle(), &mut info) == 0
            || info.nNumberOfLinks != 1
        {
            return None;
        }
        Some(format!(
            "{:08x}:{:08x}{:08x}:{}",
            info.dwVolumeSerialNumber,
            info.nFileIndexHigh,
            info.nFileIndexLow,
            ticks(info.ftCreationTime)
        ))
    }
}
