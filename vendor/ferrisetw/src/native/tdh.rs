//! Native API - Event Tracing tdh header
//!
//! The `tdh` module is an abstraction layer for the Windows tdh library. This module act as a
//! internal API that holds all `unsafe` calls to functions exported by the `tdh` Windows library.
//!
//! This module shouldn't be accessed directly. Modules from the the crate level provide a safe API to interact
//! with the crate
use std::alloc::Layout;

use super::etw_types::*;
use crate::native::etw_types::event_record::EventRecord;
use crate::native::tdh_types::Property;
use crate::traits::*;
use widestring::U16CStr;
use windows::Win32::Foundation::ERROR_INSUFFICIENT_BUFFER;
use windows::Win32::System::Diagnostics::Etw::{self, EVENT_PROPERTY_INFO, TRACE_EVENT_INFO};
use windows::core::GUID;

/// Tdh native module errors
#[derive(Debug)]
pub enum TdhNativeError {
    /// Represents an allocation error
    AllocationError,
    /// Represents an standard IO Error
    IoError(std::io::Error),
}

pub type TdhNativeResult<T> = Result<T, TdhNativeError>;

impl std::fmt::Display for TdhNativeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::AllocationError => write!(f, "allocation error"),
            Self::IoError(e) => write!(f, "i/o error {}", e),
        }
    }
}

/// Read-only wrapper over an [TRACE_EVENT_INFO]
///
/// [TRACE_EVENT_INFO]: https://docs.microsoft.com/en-us/windows/win32/api/tdh/ns-tdh-trace_event_info
pub struct TraceEventInfo {
    /// Pointer to a valid TRACE_EVENT_INFO buffer
    data: *const u8,
    /// Pointer to the same buffer, but mutable (used only when deallocating the data)
    mut_data_for_dealloc: *mut u8,
    /// Layout used to allocate the TRACE_EVENT_INFO buffer
    layout: Layout,
}

// Safety: TraceEventInfo contains a pointer to data that is never mutated (except on deallocation), and that itself does not contain pointers
unsafe impl Send for TraceEventInfo {}
// Safety: see above
unsafe impl Sync for TraceEventInfo {}

macro_rules! extract_utf16_string {
    ($self: ident, $member_name: ident) => {
        let provider_name_offset = $self.as_raw().$member_name;
        let provider_name_ptr = unsafe {
            // Safety: we trust Microsoft for providing correctly aligned data
            $self.data.offset(provider_name_offset as isize)
        };
        if provider_name_offset == 0 || provider_name_ptr.is_null() {
            return String::new();
        }
        let provider_name = unsafe {
            // Safety:
            //  * we trust Microsoft for providing correctly aligned data
            //  * we will copy into a String before the buffer gets invalid
            U16CStr::from_ptr_str(provider_name_ptr as *const u16)
        };
        return provider_name.to_string_lossy();
    };
}

impl TraceEventInfo {
    /// Create a instance of `Self` suitable for the given event
    pub fn build_from_event(event: &EventRecord) -> TdhNativeResult<Self> {
        let mut buffer_size = 0;
        let status = unsafe {
            // Safety:
            //  * the `EVENT_RECORD` was passed by Microsoft and has not been modified: it is thus valid and correctly aligned
            Etw::TdhGetEventInformation(event.as_raw_ptr(), None, None, &mut buffer_size)
        };
        if status != ERROR_INSUFFICIENT_BUFFER.0 {
            return Err(TdhNativeError::IoError(std::io::Error::from_raw_os_error(
                status as i32,
            )));
        }

        if buffer_size == 0 {
            return Err(TdhNativeError::AllocationError);
        }

        let layout = Layout::from_size_align(
            buffer_size as usize,
            std::mem::align_of::<Etw::TRACE_EVENT_INFO>(),
        )
        .map_err(|_| TdhNativeError::AllocationError)?;
        let data = unsafe {
            // Safety: size is not zero
            std::alloc::alloc(layout)
        };
        if data.is_null() {
            return Err(TdhNativeError::AllocationError);
        }

        let status = unsafe {
            // Safety:
            //  * the `EVENT_RECORD` was passed by Microsoft and has not been modified: it is thus valid and correctly aligned
            //  * `data` has been successfully allocated, with the required size and the correct alignment
            Etw::TdhGetEventInformation(
                event.as_raw_ptr(),
                None,
                Some(data.cast::<TRACE_EVENT_INFO>()),
                &mut buffer_size,
            )
        };

        if status != 0 {
            return Err(TdhNativeError::IoError(std::io::Error::from_raw_os_error(
                status as i32,
            )));
        }

        Ok(Self {
            data,
            mut_data_for_dealloc: data,
            layout,
        })
    }

    fn as_raw(&self) -> &TRACE_EVENT_INFO {
        let p = self.data.cast::<TRACE_EVENT_INFO>();
        unsafe {
            // Safety: the API enforces self.data to point to a valid, allocated TRACE_EVENT_INFO
            p.as_ref().unwrap()
        }
    }

    pub fn provider_guid(&self) -> GUID {
        self.as_raw().ProviderGuid
    }

    pub fn event_id(&self) -> u16 {
        self.as_raw().EventDescriptor.Id
    }

    pub fn event_version(&self) -> u8 {
        self.as_raw().EventDescriptor.Version
    }

    pub fn decoding_source(&self) -> DecodingSource {
        let ds = self.as_raw().DecodingSource;
        DecodingSource::from(ds)
    }

    pub fn provider_name(&self) -> String {
        extract_utf16_string!(self, ProviderNameOffset);
    }

    pub fn task_name(&self) -> String {
        extract_utf16_string!(self, TaskNameOffset);
    }

    pub fn opcode_name(&self) -> String {
        extract_utf16_string!(self, OpcodeNameOffset);
    }

    pub fn properties(&self) -> PropertyIterator {
        PropertyIterator::new(self)
    }

    pub fn diagnostic_properties(
        &self,
        max_fields: usize,
        max_name_bytes: usize,
    ) -> crate::schema::DiagnosticSchema {
        let raw = self.as_raw();
        let mut result = crate::schema::DiagnosticSchema {
            property_count: raw.PropertyCount,
            top_level_property_count: raw.TopLevelPropertyCount,
            truncated: raw.PropertyCount as usize > max_fields,
            ..Default::default()
        };
        let first = raw.EventPropertyInfoArray.as_ptr();
        let offset = first as usize - self.data as usize;
        let available =
            self.layout.size().saturating_sub(offset) / std::mem::size_of::<EVENT_PROPERTY_INFO>();
        result.invalid_layout = raw.PropertyCount as usize > available;
        let count = (raw.PropertyCount as usize).min(available).min(max_fields);
        for index in 0..count {
            // Bounds above are against the allocated TDH buffer, including its
            // variable-length property array. No EVENT_RECORD is retained.
            let property = unsafe { &*first.add(index) };
            let name_offset = property.NameOffset as usize;
            let mut units = Vec::new();
            let mut terminated = false;
            if name_offset != 0 && name_offset < self.layout.size() {
                let remaining = (self.layout.size() - name_offset) / 2;
                for i in 0..remaining.min(max_name_bytes.saturating_add(1)) {
                    let value = unsafe {
                        self.data
                            .add(name_offset + i * 2)
                            .cast::<u16>()
                            .read_unaligned()
                    };
                    if value == 0 {
                        terminated = true;
                        break;
                    }
                    units.push(value);
                }
            } else {
                result.invalid_layout = true;
            }
            let mut name = match String::from_utf16(&units) {
                Ok(name) => name,
                Err(_) => {
                    result.invalid_layout = true;
                    String::from_utf16_lossy(&units)
                }
            };
            let mut end = name.len().min(max_name_bytes);
            while !name.is_char_boundary(end) {
                end -= 1;
            }
            result.truncated |= !terminated || end != name.len();
            name.truncate(end);
            let flags = property.Flags.0 as u32;
            let scalar = flags & (0x1 | 0x80) == 0;
            result.fields.push(crate::schema::DiagnosticProperty {
                name,
                flags,
                in_type: scalar.then(|| unsafe { property.Anonymous1.nonStructType.InType }),
                out_type: scalar.then(|| unsafe { property.Anonymous1.nonStructType.OutType }),
                length_or_index: unsafe { property.Anonymous3.length },
                count_or_index: unsafe { property.Anonymous2.count },
            });
        }
        result
    }
}

#[cfg(test)]
mod contain_diagnostic_schema_tests {
    use super::*;

    fn fixture(name: &[u16]) -> TraceEventInfo {
        let name_offset = std::mem::size_of::<TRACE_EVENT_INFO>();
        let layout = Layout::from_size_align(
            name_offset + name.len() * 2,
            std::mem::align_of::<TRACE_EVENT_INFO>(),
        )
        .unwrap();
        let data = unsafe { std::alloc::alloc_zeroed(layout) };
        assert!(!data.is_null());
        // A self-owned synthetic TDH buffer. No ETW session or OS query.
        unsafe {
            let raw = &mut *data.cast::<TRACE_EVENT_INFO>();
            raw.PropertyCount = 1;
            raw.TopLevelPropertyCount = 1;
            let property = &mut raw.EventPropertyInfoArray[0];
            property.NameOffset = name_offset as u32;
            property.Flags = Etw::PROPERTY_FLAGS(0x20);
            property.Anonymous1.nonStructType.InType = 0xf001;
            property.Anonymous1.nonStructType.OutType = 0xf002;
            property.Anonymous3.length = 4;
            property.Anonymous2.count = 1;
            for (index, unit) in name.iter().enumerate() {
                data.add(name_offset + index * 2).cast::<u16>().write(*unit);
            }
        }
        TraceEventInfo {
            data,
            mut_data_for_dealloc: data,
            layout,
        }
    }

    #[test]
    fn diagnostic_schema_keeps_unknown_types_and_bounds_names_and_field_counts() {
        let info = fixture(&[73, 79, 83, 105, 122, 101, 0]);
        let schema = info.diagnostic_properties(24, 64);
        assert_eq!(schema.fields[0].name, "IOSize");
        assert_eq!(schema.fields[0].flags, 0x20);
        assert_eq!(schema.fields[0].in_type, Some(0xf001));
        assert_eq!(schema.fields[0].out_type, Some(0xf002));
        assert_eq!(schema.fields[0].length_or_index, 4);
        assert!(!schema.invalid_layout);
        assert!(!schema.truncated);
        let short = info.diagnostic_properties(24, 4);
        assert_eq!(short.fields[0].name, "IOSi");
        assert!(short.truncated);
        assert!(info.diagnostic_properties(0, 64).fields.is_empty());
        assert!(info.diagnostic_properties(0, 64).truncated);
    }

    #[test]
    fn diagnostic_schema_marks_invalid_utf16_offsets_and_layout() {
        let info = fixture(&[0xd800, 0]);
        assert!(info.diagnostic_properties(24, 64).invalid_layout);
        unsafe {
            (*info.mut_data_for_dealloc.cast::<TRACE_EVENT_INFO>()).EventPropertyInfoArray[0]
                .NameOffset = u32::MAX;
        }
        assert!(info.diagnostic_properties(24, 64).invalid_layout);
        unsafe {
            (*info.mut_data_for_dealloc.cast::<TRACE_EVENT_INFO>()).PropertyCount = u32::MAX;
        }
        let malformed = info.diagnostic_properties(24, 64);
        assert!(malformed.invalid_layout);
        assert!(malformed.truncated);
        assert!(malformed.fields.len() <= 24);
    }
}

impl Drop for TraceEventInfo {
    fn drop(&mut self) {
        unsafe {
            // Safety:
            // * ptr is a block of memory currently allocated via alloc::alloc
            // * layout is th one that was used to allocate that block of memory
            std::alloc::dealloc(self.mut_data_for_dealloc, self.layout);
        }
    }
}

pub struct PropertyIterator<'info> {
    next_index: u32,
    count: u32,
    te_info: &'info TraceEventInfo,
}

impl<'info> PropertyIterator<'info> {
    fn new(te_info: &'info TraceEventInfo) -> Self {
        let count = te_info.as_raw().PropertyCount;
        Self {
            next_index: 0,
            count,
            te_info,
        }
    }
}

impl<'info> Iterator for PropertyIterator<'info> {
    type Item = Result<Property, crate::native::tdh_types::PropertyError>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.next_index == self.count {
            return None;
        }

        let properties_array = &self.te_info.as_raw().EventPropertyInfoArray;
        let properties_array = properties_array as *const EVENT_PROPERTY_INFO;
        let cur_property_ptr = unsafe {
            // Safety:
            //  * index being in the right bounds, this guarantees the resulting pointer lies in the same allocated object
            properties_array.offset(self.next_index as isize) // we assume there will not be more than 2 billion properties for an event
        };
        let curr_prop = unsafe {
            // Safety:
            //  * this pointer has been allocated by a Microsoft API
            match cur_property_ptr.as_ref() {
                None => {
                    // This should not happen, as there is no reason the Microsoft API has put a null pointer at an index below self.count
                    // Ideally, I probably should return an `Err` here. But I prefer keeping a simple return type, and stop the iteration here in case this (normally impossible error) happens
                    return None;
                }
                Some(r) => r,
            }
        };

        let te_info_data = self.te_info.as_raw() as *const TRACE_EVENT_INFO as *const u8;
        let property_name_offset = curr_prop.NameOffset;
        let property_name_ptr = unsafe {
            // Safety: offset comes from a Microsoft API
            te_info_data.offset(property_name_offset as isize)
        };
        if property_name_ptr.is_null() {
            // This is really a safety net, there is no reason the offset nullifies the base pointer
            // This is not supposed to happen, so a simple `None` (instead of a proper `Err`) will do
            return None;
        }

        let property_name = unsafe {
            // Safety:
            //  * we trust Microsoft for providing correctly aligned data
            //  * we will copy into a String before the buffer gets invalid
            U16CStr::from_ptr_str(property_name_ptr as *const u16)
        };
        let property_name = property_name.to_string_lossy();

        self.next_index += 1;
        Some(Property::new(property_name, curr_prop))
    }
}

pub fn property_size(event: &EventRecord, name: &str) -> TdhNativeResult<u32> {
    let mut property_size = 0;

    let name = name.into_utf16();
    let desc = Etw::PROPERTY_DATA_DESCRIPTOR {
        ArrayIndex: u32::MAX,
        PropertyName: name.as_ptr() as u64,
        ..Default::default()
    };

    unsafe {
        let status = Etw::TdhGetPropertySize(event.as_raw_ptr(), None, &[desc], &mut property_size);
        if status != 0 {
            return Err(TdhNativeError::IoError(std::io::Error::from_raw_os_error(
                status as i32,
            )));
        }
    }

    Ok(property_size)
}
