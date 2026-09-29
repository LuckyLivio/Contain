use crate::{
    inventory,
    monitor::EventSource,
    windows::{etw::EtwSource, native},
};
use serde::Serialize;

#[derive(Serialize)]
pub struct DoctorReport {
    pub platform: String,
    pub elevated: bool,
    pub process_observation: bool,
    pub etw_process: String,
    pub etw_file: String,
    pub etw_registry: String,
    pub service_count: Option<usize>,
    pub scheduled_task_count: Option<usize>,
    pub startup_count: Option<usize>,
    pub warnings: Vec<String>,
}

pub fn probe() -> DoctorReport {
    let scope = native::current_user_sid()
        .map(|sid| format!("\\registry\\user\\{sid}\\software\\contain\\demo").to_lowercase());
    let mut source = EtwSource::start(&[], scope, true);
    let report = source.stop();
    let inventory = inventory::snapshot();
    DoctorReport {
        platform: std::env::consts::OS.into(),
        elevated: native::elevated(),
        process_observation: native::process_identity(std::process::id()).is_some(),
        etw_process: report.etw_process,
        etw_file: report.etw_file,
        etw_registry: report.etw_registry,
        service_count: inventory.services.map(|rows| rows.len()),
        scheduled_task_count: inventory.tasks.map(|rows| rows.len()),
        startup_count: inventory.startup.map(|rows| rows.len()),
        warnings: report
            .warnings
            .into_iter()
            .chain(inventory.warnings)
            .collect(),
    }
}
