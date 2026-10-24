//! Independent fixture observations; no collector data or shared helper is used.
use serde_json::json;
use sha2::{Digest, Sha256};
use windows_sys::Win32::System::{
    LibraryLoader::{GetModuleHandleW, GetProcAddress},
    SystemInformation::{ComputerNamePhysicalDnsFullyQualified, GetComputerNameExW},
};

fn decode_boot(status: i32, length: u32, data: &[u64; 4]) -> Result<String, String> {
    let bytes: Vec<_> = data[..2]
        .iter()
        .flat_map(|word| word.to_le_bytes())
        .collect();
    if status != 0 || length != 32 || bytes.iter().all(|byte| *byte == 0) {
        return Err(format!(
            "independent boot query rejected: status={:08x}, bytes={length}",
            status as u32
        ));
    }
    Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
}

pub(super) fn observe() -> serde_json::Value {
    let mut errors = Vec::new();
    let before = super::qpc()
        .map_err(|error| errors.push(error.to_string()))
        .ok();
    let mut hostname = [0_u16; 1024];
    let mut count = hostname.len() as u32;
    // SAFETY: the API receives a writable UTF-16 buffer with its actual capacity.
    let named = unsafe {
        GetComputerNameExW(
            ComputerNamePhysicalDnsFullyQualified,
            hostname.as_mut_ptr(),
            &mut count,
        )
    };
    let mut machine_hash = None;
    if named == 0 {
        errors.push(format!(
            "independent physical DNS name unavailable: {}",
            std::io::Error::last_os_error()
        ));
    } else if count == 0 || count as usize >= hostname.len() {
        errors.push("independent physical DNS name length invalid".into());
    } else {
        match String::from_utf16(&hostname[..count as usize]) {
            Ok(name) => {
                let mut digest = Sha256::new();
                for unit in name.to_lowercase().encode_utf16() {
                    digest.update(unit.to_le_bytes());
                }
                machine_hash = Some(format!("{:x}", digest.finalize()));
            }
            Err(_) => errors.push("independent physical DNS name encoding invalid".into()),
        }
    }
    let module_name: Vec<u16> = "ntdll.dll\0".encode_utf16().collect();
    // SAFETY: borrow the system-loaded ntdll handle; this never loads another DLL.
    let module = unsafe { GetModuleHandleW(module_name.as_ptr()) };
    // SAFETY: query only the fixed native export in that system module.
    let export = if module.is_null() {
        None
    } else {
        unsafe { GetProcAddress(module, c"NtQuerySystemInformation".as_ptr().cast()) }
    };
    let mut status_hex = None;
    let mut returned_bytes = None;
    let mut boot_identifier = None;
    if let Some(export) = export {
        type Query = unsafe extern "system" fn(i32, *mut core::ffi::c_void, u32, *mut u32) -> i32;
        // SAFETY: this is the documented signature of the named native export.
        let query: Query = unsafe { std::mem::transmute(export) };
        let mut data = [0_u64; 4];
        let mut length = 0_u32;
        // SAFETY: aligned writable 32-byte buffer; accept exactly that native layout.
        let status = unsafe { query(90, data.as_mut_ptr().cast(), 32, &mut length) };
        status_hex = Some(format!("{:08x}", status as u32));
        returned_bytes = Some(length);
        match decode_boot(status, length, &data) {
            Ok(value) => boot_identifier = Some(value),
            Err(error) => errors.push(error),
        }
    } else {
        errors.push("independent native boot query export unavailable".into());
    }
    let after = super::qpc()
        .map_err(|error| errors.push(error.to_string()))
        .ok();
    if before > after {
        errors.push("independent provenance QPC regressed".into());
    }
    json!({"schema_version":1,"phase":"fixture_clock_initialized",
        "status":if errors.is_empty(){"observed"}else{"unavailable"},
        "machine_source":"GetComputerNameExW_PhysicalDnsFullyQualified_lower_utf16le_sha256",
        "machine_name_sha256":machine_hash,
        "boot_source":"NtQuerySystemInformation_SystemBootEnvironmentInformation_90_32",
        "boot_identifier_le_hex":boot_identifier,"nt_status":status_hex,"returned_bytes":returned_bytes,
        "qpc_before":before.map(|tick|tick.to_string()),"qpc_after":after.map(|tick|tick.to_string()),"errors":errors})
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn independent_provenance_rejects_unknown_boot_layout_or_status() {
        assert!(decode_boot(0, 32, &[0; 4]).is_err());
        for (status, length) in [(-1, 32), (0, 31), (0, 33)] {
            assert!(decode_boot(status, length, &[1, 0, 0, 0]).is_err());
        }
        assert_eq!(
            decode_boot(0, 32, &[1, 0, 0, 0]).unwrap(),
            "01000000000000000000000000000000"
        );
    }
}
