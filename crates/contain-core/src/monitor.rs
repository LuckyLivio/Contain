use crate::model::{BackendReport, SystemEvent};

/// Event collection has no application ownership policy. Attribution runs after normalization.
pub trait EventSource {
    fn drain(&mut self) -> Vec<SystemEvent>;
    fn stop(&mut self) -> BackendReport;
}
