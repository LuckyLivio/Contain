//! Optional metadata observer. It never retains a handle across a workload call.
use super::*;
use std::os::windows::io::{AsRawHandle, IntoRawHandle};
use windows_sys::Win32::{
    Foundation::CloseHandle,
    Storage::FileSystem::{
        FILE_ID_INFO, FILE_NAME_NORMALIZED, FILE_NAME_OPENED, FILE_SHARE_DELETE, FileIdInfo,
        GetFileInformationByHandleEx, GetFinalPathNameByHandleW, GetVolumeInformationByHandleW,
        VOLUME_NAME_GUID,
    },
};

const PATH_CHARS: usize = 4096;

pub(super) struct Observer {
    journal: Journal,
    observations: u64,
    close_failed: bool,
}

impl Observer {
    pub(super) fn new(path: &Path) -> Result<Self> {
        let mut journal = Journal::new(path)?;
        journal.append(&json!({"schema_version":1,"kind":"write_identity_observer_session",
            "variant":"transient_metadata_handles_v1","actor":journal.actor,
            "qpc_frequency":journal.frequency.to_string(),"provenance":journal.provenance,
            "max_observations":28,"path_buffer_chars":PATH_CHARS,
            "original_write_handle":"unavailable: std::fs::write owns and closes it internally",
            "limitations":["Observations add opens, metadata queries, closes and journal I/O; compare with the unchanged variant.",
                "Each opened name belongs to this observer handle, not the original write handle.",
                "Equal volume and file IDs across closed-handle gaps are sampled identity evidence, not proof of uninterrupted identity or a unique current path.",
                "No observation establishes a writer, low-level request cause, or final-state ownership."]}))?;
        Ok(Self {
            journal,
            observations: 0,
            close_failed: false,
        })
    }

    fn record(
        &mut self,
        case: Case,
        index: usize,
        stage: &str,
        path: &Path,
        after: u64,
    ) -> Result<()> {
        ensure!(
            self.observations < 28,
            "fixed identity observation bound exceeded"
        );
        self.observations += 1;
        let start = qpc()?;
        let opened = OpenOptions::new()
            .access_mode(FILE_READ_ATTRIBUTES)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(path);
        let open_error = opened.as_ref().err().and_then(io::Error::raw_os_error);
        let open_end = qpc()?;
        let mut row = json!({"schema_version":1,"kind":"write_identity_observation",
            "variant":"transient_metadata_handles_v1","order":self.observations,
            "case":case.name(),"file_index":index,"stage":stage,"after_api_order":after,
            "actor":self.journal.actor,"qpc_frequency":self.journal.frequency.to_string(),
            "requested_path":path,"requested_path_kind":"observer_open_argument",
            "open":{"method":"OpenOptions access FILE_READ_ATTRIBUTES, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT",
                "access":FILE_READ_ATTRIBUTES,"share_mode":FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,
                "start_qpc":start.to_string(),"end_qpc":open_end.to_string(),
                "success":opened.is_ok(),"error":open_error},
            "file_identity":null,"volume":null,"observer_opened_name":null,
            "observer_normalized_name":null,"close":null,"handle_retained_across_workload_call":false});
        let close_ok = if let Ok(handle) = opened {
            let raw = handle.as_raw_handle();
            // File owns the handle during all queries, including any error return.
            row["file_identity"] = sample("GetFileInformationByHandleEx(FileIdInfo)", || {
                let mut info = FILE_ID_INFO::default();
                // SAFETY: the live File handle and correctly sized output structure are valid.
                let ok = unsafe {
                    GetFileInformationByHandleEx(
                        raw,
                        FileIdInfo,
                        (&mut info as *mut FILE_ID_INFO).cast(),
                        std::mem::size_of::<FILE_ID_INFO>() as u32,
                    )
                } != 0;
                let error = (!ok)
                    .then(|| io::Error::last_os_error().raw_os_error())
                    .flatten();
                json!({"success":ok,"error":error,
                    "volume_serial_number":ok.then(|| info.VolumeSerialNumber.to_string()),
                    "file_id_128_hex":ok.then(|| info.FileId.Identifier.iter().map(|b| format!("{b:02x}")).collect::<String>())})
            })?;
            row["volume"] = sample("GetVolumeInformationByHandleW", || {
                let mut label = [0_u16; 261];
                let mut filesystem = [0_u16; 261];
                let (mut serial, mut maximum_component, mut flags) = (0, 0, 0);
                // SAFETY: handle is live and all fixed buffers/output pointers are valid.
                let ok = unsafe {
                    GetVolumeInformationByHandleW(
                        raw,
                        label.as_mut_ptr(),
                        label.len() as u32,
                        &mut serial,
                        &mut maximum_component,
                        &mut flags,
                        filesystem.as_mut_ptr(),
                        filesystem.len() as u32,
                    )
                } != 0;
                let error = (!ok)
                    .then(|| io::Error::last_os_error().raw_os_error())
                    .flatten();
                json!({"success":ok,"error":error,"filesystem":ok.then(|| wide(&filesystem)),
                    "label":ok.then(|| wide(&label)),"format_serial_number_32":ok.then(|| serial.to_string()),
                    "maximum_component_length":ok.then_some(maximum_component),"filesystem_flags":ok.then_some(flags)})
            })?;
            row["observer_opened_name"] = name(&handle, FILE_NAME_OPENED | VOLUME_NAME_GUID)?;
            row["observer_normalized_name"] =
                name(&handle, FILE_NAME_NORMALIZED | VOLUME_NAME_GUID)?;
            let close_start = qpc()?;
            let raw = handle.into_raw_handle();
            // SAFETY: ownership was transferred from File; this is the only close attempt.
            let ok = unsafe { CloseHandle(raw) } != 0;
            let error = (!ok)
                .then(|| io::Error::last_os_error().raw_os_error())
                .flatten();
            row["close"] = self.close_observation(ok, error, close_start, qpc)?;
            ok
        } else {
            true
        };
        if !close_ok {
            row["handle_retained_across_workload_call"] = Value::Null;
        }
        row["end_qpc"] = json!(qpc()?.to_string());
        // Serialize only after the observer handle has been closed. Native query failures
        // remain recorded, but a failed close prevents the next dependent workload action.
        self.journal.append(&row)?;
        ensure!(
            close_ok,
            "observer CloseHandle failed; dependent workload stopped"
        );
        Ok(())
    }

    pub(super) fn close_observation(
        &mut self,
        success: bool,
        error: Option<i32>,
        start: u64,
        end: impl FnOnce() -> Result<u64>,
    ) -> Result<Value> {
        // A later clock/serialization error must never erase an unsuccessful close.
        self.close_failed |= !success;
        Ok(
            json!({"method":"CloseHandle","success":success,"error":error,
            "start_qpc":start.to_string(),"end_qpc":end()?.to_string()}),
        )
    }
}

fn sample(method: &str, run: impl FnOnce() -> Value) -> Result<Value> {
    let start = qpc()?;
    let mut value = run();
    value["method"] = json!(method);
    value["start_qpc"] = json!(start.to_string());
    value["end_qpc"] = json!(qpc()?.to_string());
    Ok(value)
}

fn wide(value: &[u16]) -> String {
    String::from_utf16_lossy(&value[..value.iter().position(|v| *v == 0).unwrap_or(value.len())])
}

fn name(handle: &File, flags: u32) -> Result<Value> {
    sample("GetFinalPathNameByHandleW", || {
        let mut buffer = [0_u16; PATH_CHARS];
        // SAFETY: handle is live and buffer advertises its actual writable capacity.
        let returned = unsafe {
            GetFinalPathNameByHandleW(
                handle.as_raw_handle(),
                buffer.as_mut_ptr(),
                buffer.len() as u32,
                flags,
            )
        };
        let error = (returned == 0)
            .then(|| io::Error::last_os_error().raw_os_error())
            .flatten();
        let fits = returned > 0 && (returned as usize) < buffer.len();
        json!({"success":fits,"error":error,"flags":flags,"returned_chars":returned,
            "capacity_chars":buffer.len(),"buffer_limit_exceeded":(returned as usize)>=buffer.len(),
            "name":fits.then(|| String::from_utf16_lossy(&buffer[..returned as usize])),
            "name_source":"this transient observer handle; not an ETW name or original write-handle name"})
    })
}

pub(super) fn observe(
    observer: &mut Option<Observer>,
    case: Case,
    index: usize,
    stage: &str,
    path: &Path,
    after: u64,
) -> Result<()> {
    if let Some(observer) = observer {
        observer.record(case, index, stage, path, after)?;
    }
    Ok(())
}

pub(super) fn ensure_ready(observer: &Option<Observer>) -> Result<()> {
    ensure!(
        !observer
            .as_ref()
            .is_some_and(|observer| observer.close_failed),
        "observer previously failed to close; no further workload calls permitted"
    );
    Ok(())
}
