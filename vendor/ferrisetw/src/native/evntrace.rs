//! Safe wrappers for the native ETW API
//!
//! This module makes sure the calls are safe memory-wise, but does not attempt to ensure they are called in the right order.<br/>
//! Thus, you should prefer using `UserTrace`s, `KernelTrace`s and `TraceBuilder`s, that will ensure these API are correctly used.
use std::collections::HashMap;
use std::ffi::c_void;
use std::panic::AssertUnwindSafe;
use std::sync::Arc;
use std::sync::Mutex;

use once_cell::sync::Lazy;

use widestring::U16CStr;
use windows::core::GUID;
use windows::core::PCWSTR;
use windows::Win32::Foundation::ERROR_ALREADY_EXISTS;
use windows::Win32::Foundation::ERROR_CTX_CLOSE_PENDING;
use windows::Win32::Foundation::ERROR_SUCCESS;
use windows::Win32::Foundation::FILETIME;
use windows::Win32::System::Diagnostics::Etw;
use windows::Win32::System::Diagnostics::Etw::EVENT_CONTROL_CODE_ENABLE_PROVIDER;
use windows::Win32::System::Diagnostics::Etw::TRACE_QUERY_INFO_CLASS;

use super::etw_types::*;
use crate::native::etw_types::event_record::EventRecord;
use crate::provider::event_filter::EventFilterDescriptor;
use crate::provider::Provider;
use crate::trace::callback_data::CallbackData;
use crate::trace::{RealTimeTraceTrait, TraceProperties};

pub type TraceHandle = Etw::PROCESSTRACE_HANDLE;
pub type ControlHandle = Etw::CONTROLTRACE_HANDLE;

/// Evntrace native module errors
#[derive(Debug)]
pub enum EvntraceNativeError {
    /// Represents an Invalid Handle Error
    InvalidHandle,
    /// Represents an ERROR_ALREADY_EXISTS
    AlreadyExist,
    /// Represents an standard IO Error
    IoError(std::io::Error),
}

pub(crate) type EvntraceNativeResult<T> = Result<T, EvntraceNativeError>;

// CloseTrace may leave buffered callbacks pending. Context is therefore an opaque,
// never-reused token, not a pointer to trace-owned memory. Acquiring an Arc and
// removing a registration share one lock; a callback already acquired at close
// remains alive, while later callbacks cannot attach to a newly allocated trace.
static CALLBACK_CONTEXTS: CallbackContexts = CallbackContexts::new();
struct CallbackContexts(Lazy<Mutex<ContextState>>);
#[derive(Default)]
struct ContextState {
    last_token: usize,
    callbacks: HashMap<usize, Arc<CallbackData>>,
    owners: HashMap<usize, usize>,
}
#[derive(Debug, PartialEq, Eq)]
enum ContextError {
    AlreadyExist,
    Exhausted,
}

impl CallbackContexts {
    pub const fn new() -> Self {
        Self(Lazy::new(|| Mutex::new(ContextState::default())))
    }
    fn insert(&self, owner: usize, callback: &Arc<CallbackData>) -> Result<usize, ContextError> {
        let mut state = self.0.lock().unwrap();
        if state.owners.contains_key(&owner) {
            return Err(ContextError::AlreadyExist);
        }
        let token = state
            .last_token
            .checked_add(1)
            .ok_or(ContextError::Exhausted)?;
        state.callbacks.insert(token, Arc::clone(callback));
        state.owners.insert(owner, token);
        state.last_token = token;
        Ok(token)
    }

    fn remove(&self, owner: usize) {
        let removed = {
            let mut state = self.0.lock().unwrap();
            state
                .owners
                .remove(&owner)
                .and_then(|token| state.callbacks.remove(&token))
        };
        // Dropping captured user state must not run while holding the registry lock.
        drop(removed);
    }

    fn acquire(&self, token: usize) -> Option<Arc<CallbackData>> {
        self.0.lock().unwrap().callbacks.get(&token).cloned()
    }
}

/// This will be called by the ETW framework whenever an ETW event is available
extern "system" fn trace_callback_thunk(p_record: *mut Etw::EVENT_RECORD) {
    match std::panic::catch_unwind(AssertUnwindSafe(|| {
        let record_from_ptr = unsafe {
            // Safety: lifetime is valid at least until the end of the callback. A correct lifetime will be attached when we pass the reference to the child function
            EventRecord::from_ptr(p_record)
        };

        if let Some(event_record) = record_from_ptr {
            if let Some(callback_data) =
                CALLBACK_CONTEXTS.acquire(event_record.user_context() as usize)
            {
                callback_data.on_event(event_record);
            }
        }
    })) {
        Ok(_) => {}
        Err(e) => {
            log::error!("UNIMPLEMENTED PANIC: {e:?}");
            std::process::exit(1);
        }
    }
}

fn filter_invalid_trace_handles(h: TraceHandle) -> Option<TraceHandle> {
    // See https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-opentracew#return-value
    // We're conservative and we always filter out u32::MAX, although it could be valid on 64-bit setups.
    // But it turns out runtime detection of the current OS bitness is not that easy. Plus, it is not clear whether this depends on how the architecture the binary is compiled for, or the actual OS architecture.
    if h.Value == u64::MAX || h.Value == u32::MAX as u64 {
        None
    } else {
        Some(h)
    }
}

fn filter_invalid_control_handle(h: ControlHandle) -> Option<ControlHandle> {
    // The control handle is 0 if the handle is not valid.
    // (https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracew)
    if h.Value == 0 {
        None
    } else {
        Some(h)
    }
}

/// Create a new session.
///
/// This builds an `EventTraceProperties`, calls `StartTraceW` and returns the built `EventTraceProperties` as well as the trace ControlHandle
pub(crate) fn start_trace<T>(
    trace_name: &U16CStr,
    etl_dump_file: Option<(&U16CStr, DumpFileLoggingMode, Option<u32>)>,
    trace_properties: &TraceProperties,
    enable_flags: Etw::EVENT_TRACE_FLAG,
) -> EvntraceNativeResult<(EventTraceProperties, ControlHandle)>
where
    T: RealTimeTraceTrait,
{
    let mut properties =
        EventTraceProperties::new::<T>(trace_name, etl_dump_file, trace_properties, enable_flags);

    let mut control_handle = ControlHandle::default();
    let status = unsafe {
        // Safety:
        //  * first argument points to a valid and allocated address (this is an output and will be modified)
        //  * second argument is a valid, null terminated widestring (note that it will be copied to the EventTraceProperties...from where it already comes. This will probably be overwritten by Windows, but heck.)
        //  * third argument is a valid, allocated EVENT_TRACE_PROPERTIES (and will be mutated)
        //  * Note: the string (that will be overwritten to itself) ends with a null widechar before the end of its buffer (see EventTraceProperties::new())
        Etw::StartTraceW(
            &mut control_handle,
            PCWSTR::from_raw(properties.trace_name_array().as_ptr()),
            properties.as_mut_ptr(),
        )
    }
    .ok();

    if let Err(status) = status {
        let code = status.code();

        if code == ERROR_ALREADY_EXISTS.to_hresult() {
            return Err(EvntraceNativeError::AlreadyExist);
        } else if code != ERROR_SUCCESS.to_hresult() {
            return Err(EvntraceNativeError::IoError(
                std::io::Error::from_raw_os_error(code.0),
            ));
        }
    }

    match filter_invalid_control_handle(control_handle) {
        None => Err(EvntraceNativeError::InvalidHandle),
        Some(handle) => Ok((properties, handle)),
    }
}

/// Subscribe to a started trace
///
/// Microsoft calls this "opening" the trace (and this calls `OpenTraceW`)
#[allow(clippy::borrowed_box)] // Being Boxed is really important, let's keep the Box<...> in the function signature to make the intent clearer
pub(crate) fn open_trace(
    subscription_source: SubscriptionSource,
    callback_data: &Box<Arc<CallbackData>>,
) -> EvntraceNativeResult<TraceHandle> {
    let mut log_file =
        EventTraceLogfile::create(callback_data, subscription_source, trace_callback_thunk);

    // The builder's context initially identifies this live owner. Windows only
    // receives a fresh opaque token; it never receives that allocation address.
    let owner = log_file.context_ptr() as usize;
    let token = CALLBACK_CONTEXTS
        .insert(owner, callback_data.as_ref())
        .map_err(|error| match error {
            ContextError::AlreadyExist => EvntraceNativeError::AlreadyExist,
            ContextError::Exhausted => EvntraceNativeError::IoError(std::io::Error::other(
                "ETW callback context token space exhausted",
            )),
        })?;

    let trace_handle = unsafe {
        // This function modifies the data pointed to by log_file.
        // This is fine because there is currently no other ref `self` (the current function takes a `&mut self`, and `self` is not used anywhere else in the current function)
        //
        // > On success, OpenTrace will update the structure with information from the opened file or session.
        // https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-opentracea
        let native = log_file.as_mut_ptr();
        // Context is copied unchanged into EVENT_RECORD.UserContext. It is an
        // integer token encoded in the pointer-sized field, never dereferenced.
        (*native).Context = token as *mut c_void;
        Etw::OpenTraceW(native)
    };

    if filter_invalid_trace_handles(trace_handle).is_none() {
        let error = std::io::Error::last_os_error();
        // OpenTrace never installed a consumer, so no close_trace will follow.
        CALLBACK_CONTEXTS.remove(owner);
        Err(EvntraceNativeError::IoError(error))
    } else {
        callback_data.set_trace_header(log_file.trace_header());
        Ok(trace_handle)
    }
}

/// Attach a provider to a trace
pub(crate) fn enable_provider(
    control_handle: ControlHandle,
    provider: &Provider,
) -> EvntraceNativeResult<()> {
    match filter_invalid_control_handle(control_handle) {
        None => Err(EvntraceNativeError::InvalidHandle),
        Some(handle) => {
            let owned_event_filter_descriptors: Vec<EventFilterDescriptor> = provider
                .filters()
                .iter()
                .filter_map(|filter| filter.to_event_filter_descriptor().ok()) // Silently ignoring invalid filters (basically, empty ones)
                .collect();

            let parameters = EnableTraceParameters::create(
                provider.guid(),
                provider.trace_flags(),
                &owned_event_filter_descriptors,
            );

            let res = unsafe {
                Etw::EnableTraceEx2(
                    handle,
                    &provider.guid() as *const GUID,
                    EVENT_CONTROL_CODE_ENABLE_PROVIDER.0,
                    provider.level(),
                    provider.any(),
                    provider.all(),
                    0,
                    Some(parameters.as_ptr()),
                )
            }
            .ok();

            res.map_err(|err| {
                EvntraceNativeError::IoError(std::io::Error::from_raw_os_error(err.code().0))
            })
        }
    }
}

/// Start processing a trace (this call is blocking until the trace is stopped)
///
/// You probably want to spawn a thread that will block on this call.
pub(crate) fn process_trace(trace_handle: TraceHandle) -> EvntraceNativeResult<()> {
    if filter_invalid_trace_handles(trace_handle).is_none() {
        Err(EvntraceNativeError::InvalidHandle)
    } else {
        let result = unsafe {
            // We want to start processing events as soon as January 1601.
            // * for ETL file traces, this is fine, this means "process everything from the file"
            // * for real-time traces, this means we might process a few events already waiting in the buffers when the processing is starting. This is fine, I suppose.
            let mut start = FILETIME::default();
            Etw::ProcessTrace(&[trace_handle], Some(&mut start as *mut FILETIME), None)
        }
        .ok();

        result.map_err(|err| {
            EvntraceNativeError::IoError(std::io::Error::from_raw_os_error(err.code().0))
        })
    }
}

/// Call `ControlTraceW` on the trace
///
/// # Notes
///
/// In case you want to stop the trace, you probably want to drop the instance rather than calling `control(EVENT_TRACE_CONTROL_STOP)` yourself,
/// because stop the trace makes the trace handle invalid.
/// A stopped trace could theoretically(?) be re-used, but the trace handle should be re-created, so `open` should be called again.
pub(crate) fn control_trace(
    properties: &mut EventTraceProperties,
    control_handle: ControlHandle,
    control_code: Etw::EVENT_TRACE_CONTROL,
) -> EvntraceNativeResult<()> {
    match filter_invalid_control_handle(control_handle) {
        None => Err(EvntraceNativeError::InvalidHandle),
        Some(handle) => {
            let result = unsafe {
                // Safety:
                //  * the trace handle is valid (by construction)
                //  * depending on the control code, the `Properties` can be mutated. This is fine because properties is declared as `&mut` in this function, which means no other Rust function has a reference to it, and the mutation can only happen in the call to `ControlTraceW`, which returns immediately.
                Etw::ControlTraceW(
                    handle,
                    PCWSTR::null(),
                    properties.as_mut_ptr(),
                    control_code,
                )
            }
            .ok();

            result.map_err(|err| {
                EvntraceNativeError::IoError(std::io::Error::from_raw_os_error(err.code().0))
            })
        }
    }
}

/// Similar to [`control_trace`], but using a trace name instead of a handle
pub(crate) fn control_trace_by_name(
    properties: &mut EventTraceProperties,
    trace_name: &U16CStr,
    control_code: Etw::EVENT_TRACE_CONTROL,
) -> EvntraceNativeResult<()> {
    let result = unsafe {
        // Safety:
        //  * depending on the control code, the `Properties` can be mutated. This is fine because properties is declared as `&mut` in this function, which means no other Rust function has a reference to it, and the mutation can only happen in the call to `ControlTraceW`, which returns immediately.
        Etw::ControlTraceW(
            Etw::CONTROLTRACE_HANDLE { Value: 0 },
            PCWSTR::from_raw(trace_name.as_ptr()),
            properties.as_mut_ptr(),
            control_code,
        )
    }
    .ok();

    result.map_err(|err| {
        EvntraceNativeError::IoError(std::io::Error::from_raw_os_error(err.code().0))
    })
}

/// Close the trace
///
/// It is suggested to stop the trace immediately after `close`ing it (that's what it done in the `impl Drop`), because I'm not sure how sensible it is to call other methods (apart from `stop`) afterwards
///
/// In case ETW reports there are still events in the queue that are still to trigger callbacks, this returns Ok(true).<br/>
/// If no further event callback will be invoked, this returns Ok(false)<br/>
/// On error, this returns an `Err`
#[allow(clippy::borrowed_box)] // Being Boxed is really important, let's keep the Box<...> in the function signature to make the intent clearer
pub(crate) fn close_trace(
    trace_handle: TraceHandle,
    callback_data: &Box<Arc<CallbackData>>,
) -> EvntraceNativeResult<bool> {
    match filter_invalid_trace_handles(trace_handle) {
        None => Err(EvntraceNativeError::InvalidHandle),
        Some(handle) => {
            CALLBACK_CONTEXTS.remove(callback_data.as_ref() as *const Arc<CallbackData> as usize);

            let status = unsafe { Etw::CloseTrace(handle) }.ok();

            match status {
                Ok(()) => Ok(false),
                Err(err) if err.code() == ERROR_CTX_CLOSE_PENDING.to_hresult() => Ok(true),
                Err(err) => Err(EvntraceNativeError::IoError(
                    std::io::Error::from_raw_os_error(err.code().0),
                )),
            }
        }
    }
}

/// Queries the system for system-wide ETW information (that does not require an active session).
pub(crate) fn query_info(class: TraceInformation, buf: &mut [u8]) -> EvntraceNativeResult<()> {
    let result = unsafe {
        Etw::TraceQueryInformation(
            Etw::CONTROLTRACE_HANDLE { Value: 0 },
            TRACE_QUERY_INFO_CLASS(class as i32),
            buf.as_mut_ptr().cast(),
            buf.len() as u32,
            None,
        )
    }
    .ok();

    result.map_err(|err| {
        EvntraceNativeError::IoError(std::io::Error::from_raw_os_error(err.code().0))
    })
}

#[cfg(test)]
mod contain_context_tests {
    use super::*;
    use crate::trace::callback_data::CallbackDataFromFile;

    fn callback_data() -> Arc<CallbackData> {
        Arc::new(CallbackData::FromFile(CallbackDataFromFile::new(Box::new(
            |_, _| {},
        ))))
    }

    #[test]
    fn acquired_callback_ownership_survives_close_then_releases() {
        let contexts = CallbackContexts::new();
        let data = callback_data();
        let lifetime = Arc::downgrade(&data);
        let token = contexts.insert(1, &data).unwrap();
        let acquired = contexts.acquire(token).unwrap();
        drop(data);
        contexts.remove(1);
        assert!(contexts.acquire(token).is_none());
        assert!(lifetime.upgrade().is_some());
        drop(acquired);
        assert!(lifetime.upgrade().is_none());
    }

    #[test]
    fn reused_owner_address_cannot_receive_callbacks_for_old_registration() {
        let contexts = CallbackContexts::new();
        let old = callback_data();
        let replacement = callback_data();
        let original = contexts.insert(7, &old).unwrap();
        assert_eq!(
            contexts.insert(7, &replacement),
            Err(ContextError::AlreadyExist)
        );
        assert!(Arc::ptr_eq(&contexts.acquire(original).unwrap(), &old));
        contexts.remove(7);
        let new = contexts.insert(7, &replacement).unwrap();
        assert!(new > original);
        assert!(contexts.acquire(original).is_none());
        assert!(contexts.acquire(0).is_none());
        assert!(Arc::ptr_eq(&contexts.acquire(new).unwrap(), &replacement));
    }

    #[test]
    fn token_exhaustion_never_wraps_or_reuses_a_closed_registration() {
        let contexts = CallbackContexts::new();
        contexts.0.lock().unwrap().last_token = usize::MAX - 1;
        let data = callback_data();
        let last = contexts.insert(1, &data).unwrap();
        assert_eq!(last, usize::MAX);
        assert_eq!(contexts.insert(2, &data), Err(ContextError::Exhausted));
        contexts.remove(1);
        assert_eq!(contexts.insert(1, &data), Err(ContextError::Exhausted));
        let state = contexts.0.lock().unwrap();
        assert!(state.callbacks.is_empty());
        assert!(state.owners.is_empty());
    }

    #[test]
    fn closing_during_dispatch_preserves_only_the_already_acquired_callback() {
        use std::sync::atomic::{AtomicUsize, Ordering};
        use std::sync::mpsc;
        use std::time::Duration;

        let contexts = Arc::new(CallbackContexts::new());
        let completed = Arc::new(AtomicUsize::new(0));
        let counter = completed.clone();
        let (entered, started) = mpsc::sync_channel(1);
        let (resume, released) = mpsc::sync_channel(1);
        let released = Mutex::new(released);
        let data = Arc::new(CallbackData::FromFile(CallbackDataFromFile::new(Box::new(
            move |_, _| {
                entered.send(()).unwrap();
                released
                    .lock()
                    .unwrap()
                    .recv_timeout(Duration::from_secs(2))
                    .unwrap();
                counter.fetch_add(1, Ordering::SeqCst);
            },
        ))));
        let lifetime = Arc::downgrade(&data);
        let token = contexts.insert(1, &data).unwrap();
        let dispatch = contexts.clone();
        let worker = std::thread::spawn(move || {
            let acquired = dispatch.acquire(token).unwrap();
            acquired.on_event(&EventRecord(Etw::EVENT_RECORD::default()));
        });
        started.recv_timeout(Duration::from_secs(2)).unwrap();
        contexts.remove(1);
        drop(data);
        assert!(contexts.acquire(token).is_none());
        assert!(lifetime.upgrade().is_some());
        resume.send(()).unwrap();
        worker.join().unwrap();
        assert_eq!(completed.load(Ordering::SeqCst), 1);
        assert!(lifetime.upgrade().is_none());
    }

    #[test]
    fn callback_thunk_ignores_retired_tokens_and_dispatches_only_live_registration() {
        use std::sync::atomic::{AtomicUsize, Ordering};

        let calls = Arc::new(AtomicUsize::new(0));
        let counter = calls.clone();
        let data = Box::new(Arc::new(CallbackData::FromFile(CallbackDataFromFile::new(
            Box::new(move |_, _| {
                counter.fetch_add(1, Ordering::SeqCst);
            }),
        ))));
        let owner = data.as_ref() as *const Arc<CallbackData> as usize;
        let retired = CALLBACK_CONTEXTS.insert(owner, &data).unwrap();
        CALLBACK_CONTEXTS.remove(owner);
        let current = CALLBACK_CONTEXTS.insert(owner, &data).unwrap();
        let mut record = Etw::EVENT_RECORD {
            UserContext: retired as *mut c_void,
            ..Default::default()
        };
        trace_callback_thunk(&mut record);
        assert_eq!(calls.load(Ordering::SeqCst), 0);
        record.UserContext = current as *mut c_void;
        trace_callback_thunk(&mut record);
        assert_eq!(calls.load(Ordering::SeqCst), 1);
        CALLBACK_CONTEXTS.remove(owner);
        trace_callback_thunk(&mut record);
        assert_eq!(calls.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn failed_open_releases_callback_context_for_retry() {
        // No producer is created. Both attempts must report the native missing
        // ETL error, not a stale callback registry entry from the first try.
        let path = std::env::temp_dir().join(format!(
            "contain-missing-{:016x}.etl",
            rand::random::<u64>()
        ));
        assert!(!path.exists());
        let name = widestring::U16CString::from_os_str(path.as_os_str()).unwrap();
        let data = Box::new(Arc::new(CallbackData::FromFile(CallbackDataFromFile::new(
            Box::new(|_, _| {}),
        ))));
        for attempt in 0..2 {
            let result = open_trace(SubscriptionSource::FromFile(name.clone()), &data);
            assert!(
                matches!(result, Err(EvntraceNativeError::IoError(_))),
                "attempt {}: {:?}",
                attempt,
                result
            );
        }
        let owner = data.as_ref() as *const Arc<CallbackData> as usize;
        assert!(!CALLBACK_CONTEXTS
            .0
            .lock()
            .unwrap()
            .owners
            .contains_key(&owner));
    }
}
