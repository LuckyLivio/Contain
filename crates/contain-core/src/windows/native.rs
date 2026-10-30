//! Small Win32 wrappers. All owned handles are closed in this module.
use chrono::{DateTime, Utc};
use std::mem::{size_of, zeroed};
use std::os::windows::io::{AsHandle, AsRawHandle, BorrowedHandle, FromRawHandle, OwnedHandle};
use std::ptr::null_mut;
use windows_sys::Win32::Foundation::{
    CloseHandle, FILETIME, GetLastError, INVALID_HANDLE_VALUE, WAIT_TIMEOUT,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation,
};
use windows_sys::Win32::Storage::FileSystem::QueryDosDeviceW;
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, TH32CS_SNAPTHREAD, THREADENTRY32, Thread32First, Thread32Next,
};
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

/// Supplemental startup observation. The query completion is the validity lower
/// bound; creation time alone never backdates a snapshot.
pub fn observed_process_identity(pid: u32) -> Option<(ProcessIdentity, u64)> {
    // SAFETY: the owned read-only handle remains open through the zero wait.
    unsafe {
        let raw = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            0,
            pid,
        );
        if raw.is_null() {
            return None;
        }
        let handle = OwnedHandle::from_raw_handle(raw);
        let identity = identity_from_handle(handle.as_handle(), pid)?;
        if WaitForSingleObject(handle.as_raw_handle(), 0) != WAIT_TIMEOUT {
            return None;
        }
        let at = now_ticks();
        (identity.creation_time != 0 && identity.creation_time <= at).then_some((identity, at))
    }
}

#[derive(Debug, PartialEq, Eq)]
pub enum IdentityQueryError {
    Win32(u32),
    EndedOrChanged,
    AfterTraceStart,
}

/// Toolhelp entries are candidates only, not thread identity evidence. Each
/// caller must verify the actual thread and owner handles before accepting one.
pub struct ThreadSnapshot {
    handle: OwnedHandle,
    first: bool,
    done: bool,
}
impl ThreadSnapshot {
    pub fn open() -> Result<Self, u32> {
        // SAFETY: read-only snapshot. OwnedHandle closes exactly this result.
        unsafe {
            let raw = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if raw == INVALID_HANDLE_VALUE {
                return Err(GetLastError());
            }
            Ok(Self {
                handle: OwnedHandle::from_raw_handle(raw),
                first: true,
                done: false,
            })
        }
    }
}
impl Iterator for ThreadSnapshot {
    type Item = Result<(u32, u32), u32>;
    fn next(&mut self) -> Option<Self::Item> {
        if self.done {
            return None;
        }
        // SAFETY: entry is sized per Toolhelp's contract and handle stays live.
        unsafe {
            let mut entry: THREADENTRY32 = zeroed();
            entry.dwSize = size_of::<THREADENTRY32>() as u32;
            let ok = if self.first {
                self.first = false;
                Thread32First(self.handle.as_raw_handle(), &mut entry)
            } else {
                Thread32Next(self.handle.as_raw_handle(), &mut entry)
            };
            if ok == 0 {
                self.done = true;
                let error = GetLastError();
                return (error != windows_sys::Win32::Foundation::ERROR_NO_MORE_FILES)
                    .then_some(Err(error));
            }
            // Older structures may not contain the owner field at all.
            if (entry.dwSize as usize)
                < std::mem::offset_of!(THREADENTRY32, th32OwnerProcessID) + size_of::<u32>()
            {
                self.done = true;
                return Some(Err(windows_sys::Win32::Foundation::ERROR_BAD_LENGTH));
            }
            Some(Ok((entry.th32ThreadID, entry.th32OwnerProcessID)))
        }
    }
}

pub fn initial_thread_identity(
    tid: u32,
    listed_owner: u32,
    capture_started_ticks: u64,
) -> Result<(crate::model::InitialThreadIdentity, ProcessIdentity), IdentityQueryError> {
    initial_thread_identity_with(tid, listed_owner, capture_started_ticks, || {})
}

fn initial_thread_identity_with(
    tid: u32,
    listed_owner: u32,
    capture_started_ticks: u64,
    before_validate: impl FnOnce(),
) -> Result<(crate::model::InitialThreadIdentity, ProcessIdentity), IdentityQueryError> {
    // SAFETY: read-only, independently owned thread and process handles remain
    // open together until validation completes. No debug privilege is enabled.
    unsafe {
        let raw = OpenThread(
            THREAD_QUERY_LIMITED_INFORMATION | SYNCHRONIZATION_SYNCHRONIZE,
            0,
            tid,
        );
        if raw.is_null() {
            return Err(IdentityQueryError::Win32(GetLastError()));
        }
        let thread = OwnedHandle::from_raw_handle(raw);
        let mut created = zeroed();
        let mut exited = zeroed();
        let mut kernel = zeroed();
        let mut user = zeroed();
        if GetThreadTimes(
            thread.as_raw_handle(),
            &mut created,
            &mut exited,
            &mut kernel,
            &mut user,
        ) == 0
        {
            return Err(IdentityQueryError::Win32(GetLastError()));
        }
        let birth = ticks(created);
        let pid = GetProcessIdOfThread(thread.as_raw_handle());
        if pid == 0 || pid != listed_owner || birth == 0 {
            return Err(IdentityQueryError::EndedOrChanged);
        }
        if birth > capture_started_ticks {
            return Err(IdentityQueryError::AfterTraceStart);
        }
        let raw = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            0,
            pid,
        );
        if raw.is_null() {
            return Err(IdentityQueryError::Win32(GetLastError()));
        }
        let process = OwnedHandle::from_raw_handle(raw);
        let owner = identity_from_handle(process.as_handle(), pid)
            .ok_or_else(|| IdentityQueryError::Win32(GetLastError()))?;
        before_validate();
        // GetThreadTimes leaves ExitTime undefined for running threads. A zero
        // exit time is therefore not proof of life; use nonblocking waits.
        if owner.creation_time == 0
            || owner.creation_time > birth
            || WaitForSingleObject(process.as_raw_handle(), 0) != WAIT_TIMEOUT
            || WaitForSingleObject(thread.as_raw_handle(), 0) != WAIT_TIMEOUT
        {
            return Err(IdentityQueryError::EndedOrChanged);
        }
        let observed_at = now_ticks();
        if observed_at < capture_started_ticks {
            return Err(IdentityQueryError::EndedOrChanged);
        }
        Ok((
            crate::model::InitialThreadIdentity {
                thread_id: tid,
                thread_creation_time: birth,
                owner_pid: pid,
                owner_creation_time: owner.creation_time,
                capture_started_ticks,
                observed_at,
            },
            owner,
        ))
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

pub use crate::path::{in_scope, normalize_path};

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
pub fn synchronize_providers(
    name: &str,
    registry: bool,
    filter_ids: bool,
    event_keys: bool,
) -> Result<(), u32> {
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
            if event_keys {
                params.EnableProperty |= EVENT_ENABLE_PROPERTY_EVENT_KEY;
            }
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

#[cfg(test)]
mod identity_tests {
    use super::*;

    #[test]
    fn initial_thread_handle_query_binds_current_owner_and_completion() {
        // SAFETY: these functions only return this process/thread's identifiers.
        let (tid, pid) = unsafe { (GetCurrentThreadId(), GetCurrentProcessId()) };
        let boundary = now_ticks();
        let (sample, process) = initial_thread_identity(tid, pid, boundary).unwrap();
        assert_eq!(sample.owner_pid, pid);
        assert_eq!(sample.thread_id, tid);
        assert_eq!(sample.owner_creation_time, process.creation_time);
        assert!(sample.owner_creation_time <= sample.thread_creation_time);
        assert!(sample.thread_creation_time <= boundary);
        assert!(sample.observed_at >= boundary);
        assert_eq!(
            initial_thread_identity(tid, pid.wrapping_add(1), boundary).unwrap_err(),
            IdentityQueryError::EndedOrChanged
        );
        assert_eq!(
            initial_thread_identity(tid, pid, 1).unwrap_err(),
            IdentityQueryError::AfterTraceStart
        );
    }

    #[test]
    fn initial_thread_query_rejects_exit_during_handle_validation() {
        let (send_tid, receive_tid) = std::sync::mpsc::channel();
        let (finish, wait) = std::sync::mpsc::channel();
        let thread = std::thread::spawn(move || {
            // SAFETY: this returns the calling thread's identifier only.
            send_tid.send(unsafe { GetCurrentThreadId() }).unwrap();
            wait.recv().unwrap();
        });
        let tid = receive_tid.recv().unwrap();
        // SAFETY: this returns the calling process's identifier only.
        let pid = unsafe { GetCurrentProcessId() };
        let result = initial_thread_identity_with(tid, pid, now_ticks(), || {
            finish.send(()).unwrap();
            thread.join().unwrap();
        });
        assert_eq!(result.unwrap_err(), IdentityQueryError::EndedOrChanged);
    }

    #[test]
    fn thread_enumeration_is_only_a_candidate_list_and_reports_current_thread() {
        // SAFETY: these functions return this process/thread's identifiers only.
        let expected = unsafe { (GetCurrentThreadId(), GetCurrentProcessId()) };
        let mut found = false;
        for entry in ThreadSnapshot::open().unwrap().take(32768) {
            if entry.unwrap() == expected {
                found = true;
                break;
            }
        }
        assert!(found);
    }
}
