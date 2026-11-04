//! Additive structured capture progress. This describes confirmed checkpoints,
//! never inferred terminal text or a percentage. A saved session may be unfinished.
use crate::model::{BackendReport, Capture, CaptureQuality};
use serde::Serialize;
use std::time::{Duration, Instant};

#[derive(Clone, Debug, Serialize)]
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
    pub step: String,
    pub elapsed_ms: u64,
    pub stage_elapsed_ms: u64,
    pub step_elapsed_ms: u64,
    pub processed_count: Option<u64>,
    pub processed_unit: Option<String>,
    pub last_progress_elapsed_ms: Option<u64>,
    pub installer_interaction: String,
    #[serde(skip)]
    started: Instant,
    #[serde(skip)]
    stage_started: Instant,
    #[serde(skip)]
    last_stage: String,
    #[serde(skip)]
    step_started: Instant,
    #[serde(skip)]
    emitted: Instant,
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
            step: "database_open".into(),
            elapsed_ms: 0,
            stage_elapsed_ms: 0,
            step_elapsed_ms: 0,
            processed_count: None,
            processed_unit: None,
            last_progress_elapsed_ms: None,
            installer_interaction: "not_started".into(),
            started: Instant::now(),
            stage_started: Instant::now(),
            last_stage: "preparing".into(),
            step_started: Instant::now(),
            emitted: Instant::now(),
        }
    }
    pub fn publish(&mut self, notify: &mut dyn FnMut(&Self)) {
        if self.stage != self.last_stage {
            self.stage_started = Instant::now();
            self.last_stage = self.stage.clone();
        }
        self.elapsed_ms = millis(self.started.elapsed());
        self.stage_elapsed_ms = millis(self.stage_started.elapsed()).min(self.elapsed_ms);
        self.step_elapsed_ms = millis(self.step_started.elapsed()).min(self.elapsed_ms);
        self.installer_interaction = if !self.installer_started {
            "not_started"
        } else if !self.installer_exited {
            "may_need_input"
        } else {
            "not_needed"
        }
        .into();
        self.sequence += 1;
        self.emitted = Instant::now();
        notify(self);
    }
    /// A real code-path checkpoint, independent of UI repaint or a heartbeat.
    pub(crate) fn begin_step(&mut self, step: &str, notify: &mut dyn FnMut(&Self)) {
        self.step = step.into();
        self.step_started = Instant::now();
        self.processed_count = None;
        self.processed_unit = None;
        self.last_progress_elapsed_ms = Some(millis(self.started.elapsed()));
        self.publish(notify);
    }
    /// Counts completed work only. A repeated count is not a new progress instant.
    pub(crate) fn processed(&mut self, count: u64, unit: &str, notify: &mut dyn FnMut(&Self)) {
        if self.processed_count == Some(count) && self.processed_unit.as_deref() == Some(unit) {
            return;
        }
        if count > self.processed_count.unwrap_or(0) {
            self.last_progress_elapsed_ms = Some(millis(self.started.elapsed()));
        }
        self.processed_count = Some(count);
        self.processed_unit = Some(unit.into());
        if self.emitted.elapsed() >= Duration::from_millis(250) {
            self.publish(notify);
        }
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
        self.begin_step("completed", notify);
    }
}

fn millis(duration: Duration) -> u64 {
    duration.as_millis().min(u64::MAX as u128) as u64
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
    fn stage_clock_survives_step_changes_and_resets_only_on_stage_change() {
        let mut status = InstallStatus::new("synthetic.db".into(), false);
        status.started = Instant::now() - Duration::from_secs(10);
        status.stage_started = Instant::now() - Duration::from_secs(5);
        let stage_clock = status.stage_started;
        status.begin_step("baseline_files", &mut |_| {});
        assert_eq!(status.stage_started, stage_clock);
        assert!(status.stage_elapsed_ms >= 5000);
        assert!(status.elapsed_ms >= status.stage_elapsed_ms);
        status.stage = "capturing".into();
        status.publish(&mut |_| {});
        assert_ne!(status.stage_started, stage_clock);
        assert_eq!(status.last_stage, "capturing");
        assert!(status.elapsed_ms >= status.stage_elapsed_ms);
    }
    #[test]
    fn counts_are_throttled_and_refresh_is_not_progress() {
        let mut status = InstallStatus::new("synthetic.db".into(), false);
        let mut rows = Vec::new();
        status.begin_step("final_files", &mut |row| rows.push(row.clone()));
        let checkpoint = status.last_progress_elapsed_ms;
        // Zero is a known completed-work measurement, not new activity.
        status.processed(0, "entries", &mut |_| panic!("unthrottled output"));
        assert_eq!(status.last_progress_elapsed_ms, checkpoint);
        status.emitted = Instant::now() + Duration::from_secs(3600);
        for count in 1..1000 {
            status.processed(count, "entries", &mut |_| panic!("unthrottled output"));
        }
        assert_eq!(rows.len(), 1);
        let progress = status.last_progress_elapsed_ms;
        status.publish(&mut |row| rows.push(row.clone()));
        assert_eq!(status.last_progress_elapsed_ms, progress);
        assert_eq!(rows.last().unwrap().processed_count, Some(999));
        status.fail("injected failure".into(), &mut |_| {});
        assert_eq!(status.step, "final_files");
        assert_eq!(status.processed_count, Some(999));
        assert!(!status.capture_finished && !status.data_saved);
        let document = serde_json::to_value(&status).unwrap();
        assert!(document.get("started").is_none());
        assert!(
            document["elapsed_ms"].as_u64().unwrap()
                >= document["step_elapsed_ms"].as_u64().unwrap()
        );
    }
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
