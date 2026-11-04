//! Additive structured capture progress. This describes confirmed checkpoints,
//! never inferred terminal text or a percentage. A saved session may be unfinished.
use crate::model::{BackendReport, Capture, CaptureQuality};
use serde::Serialize;

#[derive(Debug, Serialize)]
pub struct InstallStatus {
    pub session_id: String,
    pub database: String,
    pub sequence: u64,
    pub stage: String,
    pub installer_started: bool,
    pub installer_pid: Option<u32>,
    pub installer_creation_time: Option<String>,
    pub installer_exited: bool,
    pub installer_exit_code: Option<i32>,
    pub capture_finished: bool,
    pub data_saved: bool,
    pub etw_requested: bool,
    pub etw_process: Option<String>,
    pub etw_file: Option<String>,
    pub etw_registry: Option<String>,
    pub quality: Option<CaptureQuality>,
    pub error: Option<String>,
}

impl InstallStatus {
    pub fn new(database: String, etw_requested: bool) -> Self {
        Self {
            session_id: uuid::Uuid::new_v4().to_string(),
            database,
            sequence: 0,
            stage: "preparing".into(),
            installer_started: false,
            installer_pid: None,
            installer_creation_time: None,
            installer_exited: false,
            installer_exit_code: None,
            capture_finished: false,
            data_saved: false,
            etw_requested,
            etw_process: None,
            etw_file: None,
            etw_registry: None,
            quality: None,
            error: None,
        }
    }
    pub fn publish(&mut self, notify: &mut dyn FnMut(&Self)) {
        self.sequence += 1;
        notify(self);
    }
    pub fn fail(&mut self, error: String, notify: &mut dyn FnMut(&Self)) {
        self.stage = "failed".into();
        self.error = Some(error);
        self.publish(notify);
    }
    pub(crate) fn backend(&mut self, backend: &BackendReport) {
        self.etw_process = Some(backend.etw_process.clone());
        self.etw_file = Some(backend.etw_file.clone());
        self.etw_registry = Some(backend.etw_registry.clone());
    }
    pub(crate) fn finished(&mut self, capture: &Capture, notify: &mut dyn FnMut(&Self)) {
        self.backend(&capture.backend);
        self.stage = "finished".into();
        self.capture_finished = true;
        self.data_saved = true;
        self.quality = Some(capture.quality.clone());
        self.publish(notify);
    }
}

/// Registry ETW is required only when a registry scope was explicitly selected.
/// Provider activation is a startup check, never a guarantee of later continuity.
pub(crate) fn providers_ready(backend: &BackendReport, registry_requested: bool) -> bool {
    backend.etw_process == "active"
        && backend.etw_file == "active"
        && (!registry_requested || backend.etw_registry == "active")
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn progress_preserves_identity_and_exit_is_not_completion() {
        let mut status = InstallStatus::new("synthetic 记录 & [x].db".into(), true);
        let id = status.session_id.clone();
        let mut rows = Vec::new();
        let mut emit = |s: &InstallStatus| rows.push(serde_json::to_value(s).unwrap());
        status.publish(&mut emit);
        status.installer_started = true;
        status.installer_pid = Some(123);
        status.installer_creation_time = Some("133700000000000001".into());
        status.stage = "capturing".into();
        status.publish(&mut emit);
        status.installer_exited = true;
        status.installer_exit_code = Some(0);
        status.publish(&mut emit);
        assert!(!status.capture_finished);
        assert!(!status.data_saved);
        status.stage = "finalizing".into();
        status.capture_finished = true;
        status.publish(&mut emit);
        status.finished(&Capture::default(), &mut emit);
        for (index, row) in rows.iter().enumerate() {
            assert_eq!(row["sequence"], index + 1);
            assert_eq!(row["session_id"], id);
            assert_eq!(row["database"], "synthetic 记录 & [x].db");
        }
        assert_eq!(rows[2]["stage"], "capturing");
        assert_eq!(rows[2]["capture_finished"], false);
        assert_eq!(rows[3]["stage"], "finalizing");
        assert_eq!(rows[4]["data_saved"], true);
        assert_eq!(rows[1]["installer_creation_time"], "133700000000000001");
    }
    #[test]
    fn failure_retains_saved_evidence_and_never_claims_finished() {
        let mut status = InstallStatus::new("synthetic.db".into(), false);
        status.data_saved = true;
        status.installer_started = true;
        status.fail("injected failure".into(), &mut |_| {});
        assert_eq!(status.stage, "failed");
        assert!(status.data_saved);
        assert!(!status.capture_finished);
        assert_eq!(status.error.as_deref(), Some("injected failure"));
        assert_ne!(
            status.session_id,
            InstallStatus::new("synthetic.db".into(), false).session_id
        );
    }
    #[test]
    fn required_etw_checks_only_selected_providers() {
        let mut backend = BackendReport {
            etw_process: "active".into(),
            etw_file: "active".into(),
            etw_registry: "not_requested".into(),
            ..Default::default()
        };
        assert!(providers_ready(&backend, false));
        assert!(!providers_ready(&backend, true));
        backend.etw_registry = "active".into();
        assert!(providers_ready(&backend, true));
        for unavailable in ["unavailable", "disabled", "unknown_future_state"] {
            backend.etw_file = unavailable.into();
            assert!(!providers_ready(&backend, false));
        }
    }
}
