//! A stalled desktop pipe must never stall the evidence collector.
use contain_core::install_status::InstallStatus;
use std::{
    collections::VecDeque,
    io::Write,
    sync::{Arc, Condvar, Mutex, mpsc},
    time::Duration,
};

const CAPACITY: usize = 64;

#[derive(Default)]
struct Pending {
    rows: VecDeque<(bool, InstallStatus)>,
    last: Option<InstallStatus>,
    closed: bool,
}

impl Pending {
    fn push(&mut self, row: &InstallStatus) {
        let critical = self.last.as_ref().is_none_or(|last| {
            row.stage != last.stage
                || row.step != last.step
                || row.installer_exited != last.installer_exited
                || row.data_saved != last.data_saved
        });
        self.last = Some(row.clone());
        if !critical && self.rows.back().is_some_and(|(critical, _)| !critical) {
            self.rows.pop_back();
        }
        if self.rows.len() == CAPACITY {
            if let Some(index) = self.rows.iter().position(|(critical, _)| !critical) {
                self.rows.remove(index);
            } else if critical {
                // Production has fewer than 32 fixed checkpoints. Still bound
                // storage if a future caller adds an unbounded number of steps.
                self.rows.remove(usize::from(
                    self.rows.front().is_some_and(|(_, row)| row.sequence == 1),
                ));
            } else {
                return;
            }
        }
        self.rows.push_back((critical, row.clone()));
    }
}

pub(crate) struct StatusOutput {
    pending: Arc<(Mutex<Pending>, Condvar)>,
    done: mpsc::Receiver<()>,
}

fn write_status(
    output: &mut impl Write,
    mut row: InstallStatus,
    wire_sequence: &mut u64,
) -> std::io::Result<()> {
    // Internal measurements may be coalesced. The public v3 stream retains its
    // existing contiguous sequence contract, assigned only after dequeue.
    *wire_sequence = wire_sequence
        .checked_add(1)
        .ok_or_else(|| std::io::Error::other("install status sequence exhausted"))?;
    row.sequence = *wire_sequence;
    let envelope =
        serde_json::json!({ "schema_version": 3, "kind": "install_status", "data": row });
    serde_json::to_writer(&mut *output, &envelope).map_err(std::io::Error::other)?;
    output.write_all(b"\n")?;
    output.flush()
}

impl StatusOutput {
    pub(crate) fn new() -> Self {
        let pending = Arc::new((Mutex::new(Pending::default()), Condvar::new()));
        let worker = pending.clone();
        let (finished, done) = mpsc::channel();
        std::thread::spawn(move || {
            let mut stdout = std::io::stdout().lock();
            let mut wire_sequence = 0;
            loop {
                let row = {
                    let (state, ready) = &*worker;
                    let mut state = state
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner);
                    while state.rows.is_empty() && !state.closed {
                        state = ready
                            .wait(state)
                            .unwrap_or_else(std::sync::PoisonError::into_inner);
                    }
                    state.rows.pop_front().map(|(_, row)| row)
                };
                let Some(row) = row else { break };
                if write_status(&mut stdout, row, &mut wire_sequence).is_err() {
                    break;
                }
            }
            let _ = finished.send(());
        });
        Self { pending, done }
    }

    pub(crate) fn publish(&self, status: &InstallStatus) {
        let (state, ready) = &*self.pending;
        let mut state = state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        state.push(status);
        ready.notify_one();
    }
}

impl Drop for StatusOutput {
    fn drop(&mut self) {
        let (state, ready) = &*self.pending;
        state
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .closed = true;
        ready.notify_one();
        // Bounded best-effort delivery after recording has ended. A disconnected
        // or stalled observer cannot keep this process alive indefinitely. If a
        // terminal row is lost, clients must report an unconfirmed result.
        let _ = self.done.recv_timeout(Duration::from_secs(5));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slow_observer_coalesces_counts_and_preserves_task_milestones() {
        let mut pending = Pending::default();
        let mut row = InstallStatus::new("synthetic.db".into(), false);
        row.publish(&mut |_| {});
        pending.push(&row);
        row.stage = "capturing".into();
        row.step = "installer_running".into();
        row.publish(&mut |_| {});
        pending.push(&row);
        for count in 0..10_000 {
            row.processed_count = Some(count);
            row.publish(&mut |_| {});
            pending.push(&row);
        }
        row.installer_exited = true;
        row.publish(&mut |_| {});
        pending.push(&row);
        row.stage = "finalizing".into();
        row.publish(&mut |_| {});
        pending.push(&row);
        row.stage = "finished".into();
        row.publish(&mut |_| {});
        pending.push(&row);
        assert!(pending.rows.len() < 10);
        assert_eq!(pending.rows.front().unwrap().1.sequence, 1);
        let rows: Vec<_> = pending.rows.iter().map(|(_, row)| row).collect();
        assert!(
            rows.iter()
                .any(|row| row.stage == "capturing" && row.installer_exited)
        );
        assert!(rows.iter().any(|row| row.stage == "finalizing"));
        assert_eq!(rows.last().unwrap().stage, "finished");
        assert!(rows.last().unwrap().sequence > rows.len() as u64);
        let session = row.session_id.clone();
        let mut encoded = Vec::new();
        let mut wire_sequence = 0;
        for (_, row) in pending.rows {
            write_status(&mut encoded, row, &mut wire_sequence).unwrap();
        }
        let encoded = String::from_utf8(encoded).unwrap();
        let delivered: Vec<serde_json::Value> = encoded
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        for (index, envelope) in delivered.iter().enumerate() {
            assert_eq!(envelope["schema_version"], 3);
            assert_eq!(envelope["kind"], "install_status");
            assert_eq!(envelope["data"]["sequence"], index + 1);
            assert_eq!(envelope["data"]["session_id"], session);
        }
        assert_eq!(delivered.first().unwrap()["data"]["stage"], "preparing");
        assert!(
            delivered
                .iter()
                .any(|row| row["data"]["stage"] == "finalizing")
        );
        assert_eq!(delivered.last().unwrap()["data"]["stage"], "finished");
    }

    #[test]
    fn even_unbounded_future_step_changes_have_a_fixed_queue_limit() {
        let mut pending = Pending::default();
        let mut row = InstallStatus::new("synthetic.db".into(), false);
        for index in 0..1000 {
            row.step = index.to_string();
            row.publish(&mut |_| {});
            pending.push(&row);
            assert!(pending.rows.len() <= CAPACITY);
        }
        assert_eq!(pending.rows.front().unwrap().1.sequence, 1);
        assert_eq!(pending.rows.back().unwrap().1.step, "999");
    }
}
