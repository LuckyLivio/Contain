//! Receiver-local fixed-capacity windows. Producer milestones arrive through
//! atomic queue snapshots; no callback acquires this recorder or formats data.
use super::QueueSpan;
use serde::Serialize;
use std::collections::VecDeque;

pub(super) const PRE: usize = 64;
pub(super) const POST: usize = 64;
pub(crate) const BACKLOG_THRESHOLD: u64 = 1024;

#[derive(Clone, Copy, Default, Serialize)]
struct FollowState {
    pending_records: u64,
    pending_bytes: u64,
    // Fixed order documented in the parent report; cumulative page counters,
    // not distinct missing operations. No arbitrary names enter the recorder.
    drops: [u64; 6],
    pending_reason_drops: [u64; 3],
    pending_attempts: [u64; 3],
    scan_visits: u64,
}

#[derive(Clone, Serialize)]
struct Observation {
    #[serde(flatten)]
    span: QueueSpan,
    follow: FollowState,
}

#[derive(Serialize)]
struct Window {
    trigger_ns: u64,
    observed_at_ns: u64,
    detection_lag_ns: u64,
    queue_depth: u64,
    trigger_before_retained_context: bool,
    trigger_contained_by_completed_span: bool,
    pre_records: usize,
    post_records: usize,
    pre_capacity_full: bool,
    post_capacity_full: bool,
    earlier_observations_omitted: u64,
    later_observations_omitted: u64,
    records: Vec<Observation>,
}

impl Window {
    fn new(
        at: u64,
        depth: u64,
        prior: &VecDeque<Observation>,
        count: u64,
        event: &Observation,
    ) -> Self {
        let mut records = Vec::with_capacity(PRE + 1 + POST);
        records.extend(prior.iter().cloned());
        records.push(event.clone());
        Self {
            trigger_ns: at,
            observed_at_ns: event.span.end_ns,
            detection_lag_ns: event.span.end_ns.saturating_sub(at),
            queue_depth: depth,
            trigger_before_retained_context: records
                .iter()
                .map(|row| row.span.start_ns)
                .min()
                .is_some_and(|start| at < start),
            trigger_contained_by_completed_span: records
                .iter()
                .any(|row| row.span.start_ns <= at && at <= row.span.end_ns),
            pre_records: prior.len(),
            post_records: 0,
            pre_capacity_full: prior.len() == PRE,
            post_capacity_full: false,
            earlier_observations_omitted: count.saturating_sub(prior.len() as u64),
            later_observations_omitted: 0,
            records,
        }
    }
    fn append(&mut self, event: &Observation) {
        if self.post_records < POST {
            self.trigger_contained_by_completed_span |=
                event.span.start_ns <= self.trigger_ns && self.trigger_ns <= event.span.end_ns;
            self.trigger_before_retained_context &= self.trigger_ns < event.span.start_ns;
            self.records.push(event.clone());
            self.post_records += 1;
            self.post_capacity_full = self.post_records == POST;
        } else {
            self.later_observations_omitted += 1;
        }
    }
}

#[derive(Serialize)]
pub(super) struct Windows {
    protocol: &'static str,
    backlog_threshold_records: u64,
    pre_capacity: usize,
    post_capacity: usize,
    maximum_records_per_window: usize,
    receiver_observations: u64,
    first_backlog_ns: Option<u64>,
    first_overflow_ns: Option<u64>,
    source_high_water: u64,
    source_max_delay_ns: u64,
    first_backlog: Option<Window>,
    first_overflow: Option<Window>,
    maximum_backlog: Option<Window>,
    drop_reason_order: [&'static str; 6],
    pending_reason_order: [&'static str; 3],
    limitations: [&'static str; 6],
    #[serde(skip)]
    prior: VecDeque<Observation>,
    #[serde(skip)]
    follow: FollowState,
}

impl Default for Windows {
    fn default() -> Self {
        Self {
            protocol: "contain-receiver-backlog-window-1",
            backlog_threshold_records: BACKLOG_THRESHOLD,
            pre_capacity: PRE,
            post_capacity: POST,
            maximum_records_per_window: PRE + 1 + POST,
            receiver_observations: 0,
            first_backlog_ns: None,
            first_overflow_ns: None,
            source_high_water: 0,
            source_max_delay_ns: 0,
            first_backlog: None,
            first_overflow: None,
            maximum_backlog: None,
            drop_reason_order: [
                "serialization",
                "record_bytes",
                "pending_records",
                "pending_bytes",
                "resources",
                "requests",
            ],
            pending_reason_order: ["before_bind", "no_identity", "unverified_actor"],
            limitations: [
                "Completed receiver spans and page checkpoints only; nested spans overlap and must not be added together.",
                "Producer milestone time uses the shared monotonic capture epoch; observed_at is the later receiver sampling boundary.",
                "Null first milestones mean not observed by this probe, not proof that no brief backlog existed; no receiver samples means no measured window.",
                "Short pre/post context is retained as incomplete; initial source/snapshot work may precede the receiver and is only visible in existing main-thread profile spans.",
                "Wall duration includes scheduling and lock waits; null CPU remains unavailable, and queue delay is a record age rather than a thread pause.",
                "Follow counts are cumulative page-boundary snapshots; their deltas describe a page, not the exact inner stage that rejected each candidate.",
            ],
            prior: VecDeque::with_capacity(PRE),
            follow: FollowState::default(),
        }
    }
}

impl Windows {
    pub(super) fn observations(&self) -> u64 {
        self.receiver_observations
    }

    pub(super) fn follow_pending(&mut self, records: u64, bytes: u64) {
        self.follow.pending_records = records;
        self.follow.pending_bytes = bytes;
    }

    pub(super) fn counter(&mut self, name: &'static str, value: u64) {
        let slot = match name {
            "follow_drop_serialization" => Some(&mut self.follow.drops[0]),
            "follow_drop_record_bytes" => Some(&mut self.follow.drops[1]),
            "follow_drop_pending_records" => Some(&mut self.follow.drops[2]),
            "follow_drop_pending_bytes" => Some(&mut self.follow.drops[3]),
            "follow_drop_resources" => Some(&mut self.follow.drops[4]),
            "follow_drop_requests" => Some(&mut self.follow.drops[5]),
            "follow_drop_pending_before_bind" => Some(&mut self.follow.pending_reason_drops[0]),
            "follow_drop_pending_no_identity" => Some(&mut self.follow.pending_reason_drops[1]),
            "follow_drop_pending_unverified_actor" => {
                Some(&mut self.follow.pending_reason_drops[2])
            }
            "follow_pending_before_bind_attempts" => Some(&mut self.follow.pending_attempts[0]),
            "follow_pending_no_identity_attempts" => Some(&mut self.follow.pending_attempts[1]),
            "follow_pending_unverified_actor_attempts" => {
                Some(&mut self.follow.pending_attempts[2])
            }
            "follow_scan_visits" => Some(&mut self.follow.scan_visits),
            _ => None,
        };
        if let Some(slot) = slot {
            *slot = slot.saturating_add(value);
        }
    }

    pub(super) fn observe(&mut self, span: &QueueSpan) {
        if !span.stage.starts_with("receiver_") {
            return;
        }
        let event = Observation {
            span: span.clone(),
            follow: self.follow,
        };
        let before = &span.before;
        let after = &span.after;
        let depth = before
            .pending
            .max(after.pending)
            .max(before.high_water)
            .max(after.high_water);
        self.source_high_water = self.source_high_water.max(depth);
        self.source_max_delay_ns = self
            .source_max_delay_ns
            .max(before.max_delay_ns)
            .max(after.max_delay_ns);
        for window in [
            &mut self.first_backlog,
            &mut self.first_overflow,
            &mut self.maximum_backlog,
        ]
        .into_iter()
        .flatten()
        {
            window.append(&event);
        }
        if self.first_backlog.is_none()
            && let Some(at) = before.first_backlog_ns.or(after.first_backlog_ns)
        {
            self.first_backlog_ns = Some(at);
            self.first_backlog = Some(Window::new(
                at,
                depth,
                &self.prior,
                self.receiver_observations,
                &event,
            ));
        }
        if self.first_overflow.is_none()
            && let Some(at) = before.first_overflow_ns.or(after.first_overflow_ns)
        {
            self.first_overflow_ns = Some(at);
            self.first_overflow = Some(Window::new(
                at,
                depth,
                &self.prior,
                self.receiver_observations,
                &event,
            ));
        }
        if self
            .maximum_backlog
            .as_ref()
            .is_none_or(|window| depth > window.queue_depth)
        {
            // A high-water advance can occur inside this completed span. The
            // maximum's trigger is this sample boundary, not invented event time.
            self.maximum_backlog = Some(Window::new(
                span.end_ns,
                depth,
                &self.prior,
                self.receiver_observations,
                &event,
            ));
        }
        if self.prior.len() == PRE {
            self.prior.pop_front();
        }
        self.prior.push_back(event);
        self.receiver_observations += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::super::QueueSnapshot;
    use super::*;
    fn span(index: u64, pending: u64, first: Option<u64>, overflow: Option<u64>) -> QueueSpan {
        QueueSpan {
            stage: "receiver_follow_classify",
            start_ns: index * 10,
            end_ns: index * 10 + 9,
            before: QueueSnapshot::default(),
            after: QueueSnapshot {
                pending,
                high_water: pending,
                first_backlog_ns: first,
                first_overflow_ns: overflow,
                ..Default::default()
            },
            units: 12,
            cpu_ns: None,
        }
    }
    #[test]
    fn late_trigger_retains_pre_and_post_after_the_old_sample_limit() {
        let mut windows = Windows::default();
        for index in 0..8300 {
            windows.observe(&span(index, 1, None, None));
        }
        windows.counter("follow_drop_pending_records", 4);
        windows.follow_pending(20, 400);
        windows.observe(&span(8300, 1024, Some(83003), None));
        for index in 8301..8400 {
            windows.observe(&span(index, 1024, Some(83003), Some(83015)));
        }
        let first = windows.first_backlog.as_ref().unwrap();
        assert_eq!(first.trigger_ns, 83003);
        assert_eq!(first.records.len(), PRE + 1 + POST);
        assert_eq!(first.records[PRE].span.start_ns, 83000);
        assert_eq!(first.records[PRE].follow.drops[2], 4);
        assert_eq!(first.records[PRE].follow.pending_records, 20);
        assert!(first.pre_capacity_full && first.post_capacity_full);
        assert_eq!(first.earlier_observations_omitted, 8300 - PRE as u64);
        assert_eq!(first.later_observations_omitted, 99 - POST as u64);
        assert_eq!(windows.first_overflow.as_ref().unwrap().trigger_ns, 83015);
        assert_eq!(windows.prior.len(), PRE);
    }
    #[test]
    fn early_trigger_and_missing_cpu_are_never_filled_with_fabricated_context() {
        let mut windows = Windows::default();
        windows.observe(&span(0, 2048, Some(0), Some(0)));
        let first = windows.first_overflow.as_ref().unwrap();
        assert_eq!(first.records.len(), 1);
        assert_eq!(first.pre_records, 0);
        assert_eq!(first.post_records, 0);
        assert!(!first.pre_capacity_full && !first.post_capacity_full);
        assert!(first.records[0].span.cpu_ns.is_none());
        assert_eq!(first.detection_lag_ns, 9);
        let value = serde_json::to_value(windows).unwrap();
        assert!(value.get("prior").is_none());
        assert_eq!(
            value["first_overflow"]["records"][0]["cpu_ns"],
            serde_json::Value::Null
        );
    }
    #[test]
    fn no_trigger_reports_maximum_context_and_larger_peak_replaces_it() {
        let mut windows = Windows::default();
        for index in 0..100 {
            windows.observe(&span(index, index % 100, None, None));
        }
        windows.observe(&span(100, 600, None, None));
        windows.observe(&span(101, 400, None, None));
        assert!(windows.first_backlog.is_none() && windows.first_overflow.is_none());
        let maximum = windows.maximum_backlog.as_ref().unwrap();
        assert_eq!(maximum.queue_depth, 600);
        assert_eq!(maximum.pre_records, PRE);
        assert_eq!(maximum.post_records, 1);
        assert_eq!(maximum.records[PRE].span.start_ns, 1000);
    }
}
