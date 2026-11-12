//! Offline validation of the actual production persistence and rendering modules.
//! This deliberately has no capture, Win32 provider, installer or cleanup executor.
//! It is not an alternate implementation of the storage or evidence algorithms.
extern crate self as contain_core;

#[path = "../../../crates/contain-core/src/model.rs"]
pub mod model;
#[path = "../../../crates/contain-core/src/path.rs"]
pub mod path;
// Capture-worker diagnostics remain compiled, but the offline target has no worker.
#[allow(dead_code)]
#[path = "../../../crates/contain-core/src/profile.rs"]
pub mod profile;
#[path = "../../../crates/contain-core/src/reliability.rs"]
pub mod reliability;
// Capture-only entrypoints are not all called here; all storage code and generic
// regressions remain compiled. The Windows workspace retains its normal lints.
#[allow(dead_code)]
#[path = "../../../crates/contain-core/src/install_status.rs"]
pub mod install_status;
#[path = "../../../crates/contain-cli/src/render.rs"]
pub mod render;
#[allow(dead_code)]
#[path = "../../../crates/contain-core/src/storage.rs"]
pub mod storage;
pub mod workflow;

// Exercise the exact ETW producer ownership guard without starting a provider.
#[cfg(test)]
#[path = "../../../vendor/ferrisetw/src/trace/startup_ownership.rs"]
mod etw_startup_ownership;

// Exercise the actual monotonic boundary policy without simulating Windows.
#[cfg(test)]
#[path = "../../../crates/contain-core/src/drain.rs"]
mod drain;
