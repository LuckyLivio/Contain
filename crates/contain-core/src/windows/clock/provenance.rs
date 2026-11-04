//! Diagnostic observations only. The native boot information class is not a
//! stable public Win32 contract: failure/unknown layout stays unavailable.
use crate::model::ClockProvenanceSample;
use sha2::{Digest, Sha256};
use windows_sys::Win32::System::{
    LibraryLoader::{GetModuleHandleW, GetProcAddress},
    SystemInformation::{ComputerNamePhysicalDnsFullyQualified, GetComputerNameExW},
};

#[repr(C)]
#[derive(Default)]
struct BootEnvironment {
    identifier: [u8; 16],
    firmware_type: u32,
    flags: u64,
}

type QuerySystemInformation =
    unsafe extern "system" fn(i32, *mut core::ffi::c_void, u32, *mut u32) -> i32;

fn boot_identifier(status: i32, returned: u32, value: &BootEnvironment) -> Result<String, String> {
    if status != 0 || returned != 32 || !value.identifier.iter().any(|byte| *byte != 0) {
        return Err(format!(
            "native boot observation rejected: status={:08x}, bytes={returned}",
            status as u32
        ));
    }
    Ok(value
        .identifier
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect())
}

fn machine_hash() -> Result<String, String> {
    let mut name = [0_u16; 1024];
    let mut length = name.len() as u32;
    // SAFETY: fixed writable UTF-16 buffer and matching element capacity.
    if unsafe {
        GetComputerNameExW(
            ComputerNamePhysicalDnsFullyQualified,
            name.as_mut_ptr(),
            &mut length,
        )
    } == 0
    {
        return Err(format!(
            "physical DNS name unavailable: {}",
            std::io::Error::last_os_error()
        ));
    }
    if length == 0 || length as usize >= name.len() {
        return Err("physical DNS name length invalid".into());
    }
    let name = String::from_utf16(&name[..length as usize])
        .map_err(|_| "physical DNS name encoding invalid")?;
    let bytes: Vec<_> = name
        .to_lowercase()
        .encode_utf16()
        .flat_map(u16::to_le_bytes)
        .collect();
    Ok(format!("{:x}", Sha256::digest(bytes)))
}

pub(super) fn sample(phase: &str) -> ClockProvenanceSample {
    let mut result = ClockProvenanceSample {
        schema_version: 1,
        phase: phase.into(),
        status: "unavailable".into(),
        machine_source: "GetComputerNameExW_PhysicalDnsFullyQualified_lower_utf16le_sha256".into(),
        machine_name_sha256: None,
        boot_source: "NtQuerySystemInformation_SystemBootEnvironmentInformation_90_32".into(),
        boot_identifier_le_hex: None,
        nt_status: None,
        returned_bytes: None,
        qpc_before: None,
        qpc_after: None,
        errors: Vec::new(),
    };
    match super::qpc() {
        Ok(value) => result.qpc_before = Some(value),
        Err(error) => result.errors.push(error),
    }
    match machine_hash() {
        Ok(value) => result.machine_name_sha256 = Some(value),
        Err(error) => result.errors.push(error),
    }
    let name: Vec<_> = "ntdll.dll\0".encode_utf16().collect();
    // SAFETY: ntdll is already loaded by Windows; borrow its handle, never free it.
    let module = unsafe { GetModuleHandleW(name.as_ptr()) };
    // SAFETY: only query the named export in the loaded system module.
    let entry = if module.is_null() {
        None
    } else {
        unsafe { GetProcAddress(module, c"NtQuerySystemInformation".as_ptr().cast()) }
    };
    if let Some(entry) = entry {
        // SAFETY: the named native export has this documented function signature.
        let query: QuerySystemInformation = unsafe { std::mem::transmute(entry) };
        let mut value = BootEnvironment::default();
        let mut returned = 0;
        // SAFETY: the 32-byte ABI is checked below; all output pointers remain valid.
        let status = unsafe {
            query(
                90,
                (&mut value as *mut BootEnvironment).cast(),
                32,
                &mut returned,
            )
        };
        result.nt_status = Some(format!("{:08x}", status as u32));
        result.returned_bytes = Some(returned);
        match boot_identifier(status, returned, &value) {
            Ok(value) => result.boot_identifier_le_hex = Some(value),
            Err(error) => result.errors.push(error),
        }
    } else {
        result
            .errors
            .push("native boot query export unavailable".into());
    }
    match super::qpc() {
        Ok(value) => result.qpc_after = Some(value),
        Err(error) => result.errors.push(error),
    }
    if result.qpc_before > result.qpc_after {
        result.errors.push("native provenance QPC regressed".into());
    }
    if result.errors.is_empty() {
        result.status = "observed".into();
    }
    result
}

const _: () = assert!(std::mem::size_of::<BootEnvironment>() == 32);

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_provenance_rejects_failed_truncated_and_zero_boot_observations() {
        let mut value = BootEnvironment::default();
        assert!(boot_identifier(0, 32, &value).is_err());
        value.identifier[0] = 1;
        assert!(boot_identifier(-1, 32, &value).is_err());
        assert!(boot_identifier(0, 31, &value).is_err());
        assert!(boot_identifier(0, 33, &value).is_err());
        assert_eq!(
            boot_identifier(0, 32, &value).unwrap(),
            "01000000000000000000000000000000"
        );
    }
}
