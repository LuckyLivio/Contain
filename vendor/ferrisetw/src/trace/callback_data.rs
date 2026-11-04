use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{OnceLock, RwLock};

use windows::Win32::System::Diagnostics::Etw;

use crate::native::etw_types::event_record::EventRecord;
use crate::provider::Provider;
use crate::schema_locator::SchemaLocator;
use crate::trace::RealTimeTraceTrait;
use crate::EtwCallback;

/// Data used by callbacks when the trace is running
// NOTE: this structure is accessed in an unsafe block in a separate thread (see the `trace_callback_thunk` function)
//       Thus, this struct must not be mutated (outside of interior mutability and/or using Mutex and other synchronization mechanisms) when the associated trace is running.
#[derive(Debug)]
pub enum CallbackData {
    RealTime(RealTimeCallbackData),
    FromFile(CallbackDataFromFile),
}

#[derive(Debug)]
pub struct RealTimeCallbackData {
    header: OnceLock<crate::trace::TraceHeader>,
    /// Represents how many events have been handled so far
    events_handled: AtomicUsize,
    schema_locator: SchemaLocator,
    /// List of Providers associated with the Trace. This also owns the callback closures and their state
    providers: Vec<Provider>,
}

pub struct CallbackDataFromFile {
    header: OnceLock<crate::trace::TraceHeader>,
    pub(crate) raw_timestamps: bool,
    /// Represents how many events have been handled so far
    events_handled: AtomicUsize,
    schema_locator: SchemaLocator,
    /// This trace is reading from an ETL file, and has a single callback
    callback: RwLock<EtwCallback>,
}

impl CallbackData {
    pub(crate) fn trace_header(&self) -> Option<crate::trace::TraceHeader> {
        match self {
            Self::RealTime(cb) => cb.header.get().copied(),
            Self::FromFile(cb) => cb.header.get().copied(),
        }
    }
    pub(crate) fn set_trace_header(&self, header: crate::trace::TraceHeader) {
        let slot = match self {
            Self::RealTime(cb) => &cb.header,
            Self::FromFile(cb) => &cb.header,
        };
        let _ = slot.set(header);
    }
    pub(crate) fn raw_timestamps(&self) -> bool {
        matches!(self, Self::FromFile(cb) if cb.raw_timestamps)
    }
    pub fn on_event(&self, record: &EventRecord) {
        match self {
            CallbackData::RealTime(rt_cb) => rt_cb.on_event(record),
            CallbackData::FromFile(f_cb) => f_cb.on_event(record),
        }
    }

    pub fn events_handled(&self) -> usize {
        match self {
            CallbackData::RealTime(rt_cb) => rt_cb.events_handled(),
            CallbackData::FromFile(f_cb) => f_cb.events_handled(),
        }
    }
}

impl std::default::Default for RealTimeCallbackData {
    fn default() -> Self {
        Self {
            header: OnceLock::new(),
            events_handled: AtomicUsize::new(0),
            schema_locator: SchemaLocator::new(),
            providers: Vec::new(),
        }
    }
}

impl RealTimeCallbackData {
    pub fn new() -> Self {
        Default::default()
    }

    pub fn add_provider(&mut self, provider: Provider) {
        self.providers.push(provider)
    }

    pub fn providers(&self) -> &[Provider] {
        &self.providers
    }

    /// How many events have been handled since this instance was created
    pub fn events_handled(&self) -> usize {
        self.events_handled.load(Ordering::Relaxed)
    }

    pub fn provider_flags<T: RealTimeTraceTrait>(&self) -> Etw::EVENT_TRACE_FLAG {
        Etw::EVENT_TRACE_FLAG(T::enable_flags(&self.providers))
    }

    pub fn on_event(&self, record: &EventRecord) {
        self.events_handled.fetch_add(1, Ordering::Relaxed);

        for prov in &self.providers {
            if prov.guid() == record.provider_id() {
                prov.on_event(record, &self.schema_locator);
            }
        }
    }
}

impl CallbackDataFromFile {
    pub fn new(callback: EtwCallback) -> Self {
        Self {
            header: OnceLock::new(),
            raw_timestamps: false,
            events_handled: AtomicUsize::new(0),
            schema_locator: SchemaLocator::new(),
            callback: RwLock::new(callback),
        }
    }

    /// How many events have been handled since this instance was created
    pub fn events_handled(&self) -> usize {
        self.events_handled.load(Ordering::Relaxed)
    }

    pub fn on_event(&self, record: &EventRecord) {
        self.events_handled.fetch_add(1, Ordering::Relaxed);
        if let Ok(mut cb) = self.callback.write() {
            cb(record, &self.schema_locator);
        }
    }
}

impl std::fmt::Debug for CallbackDataFromFile {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CallbackDataFromFile")
            .field("events_handled", &self.events_handled)
            .field("schema_locator", &self.schema_locator)
            .finish()
    }
}
