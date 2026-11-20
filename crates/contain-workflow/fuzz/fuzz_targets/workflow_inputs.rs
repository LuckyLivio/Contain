#![no_main]
use contain_workflow::{artifact, manifest::Manifest, package::PackageSpec};
use libfuzzer_sys::fuzz_target;
fuzz_target!(|data: &[u8]| {
    if let Ok(m) = serde_json::from_slice::<Manifest>(data) {
        let _ = m.validate();
    }
    if let Ok(s) = serde_json::from_slice::<PackageSpec>(data) {
        let _ = s.validate();
    }
    if let Ok(name) = std::str::from_utf8(data) {
        let _ = artifact::safe_name(name);
    }
    let _ = artifact::pe_x64(data);
});
