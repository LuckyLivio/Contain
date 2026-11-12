//! Monotonic session boundary; wall-clock timestamps never drive deadlines.
#[derive(Debug, PartialEq)]
pub enum Decision {
    Wait,
    Quiet,
    Timeout,
}
pub fn decide(
    elapsed_ms: u64,
    quiet_ms: u64,
    descendants_live: bool,
    settle_ms: u64,
    max_ms: u64,
) -> Decision {
    if elapsed_ms >= max_ms {
        Decision::Timeout
    } else if !descendants_live && quiet_ms >= settle_ms {
        Decision::Quiet
    } else {
        Decision::Wait
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn live_descendant_and_activity_extend_drain_but_never_past_deadline() {
        assert_eq!(decide(2000, 2000, true, 1500, 10000), Decision::Wait);
        assert_eq!(decide(2000, 100, false, 1500, 10000), Decision::Wait);
        assert_eq!(decide(2000, 2000, false, 1500, 10000), Decision::Quiet);
        assert_eq!(decide(10000, 0, true, 1500, 10000), Decision::Timeout);
        assert_eq!(decide(0, 0, false, 0, 0), Decision::Timeout);
    }
    #[test]
    fn fixed_quiet_short_child_persistent_child_noise_and_deadline_traces() {
        // Quiet root exit settles normally.
        assert_eq!(decide(1499, 1499, false, 1500, 10000), Decision::Wait);
        assert_eq!(decide(1500, 1500, false, 1500, 10000), Decision::Quiet);
        // A child mutation at t=1000, child exit at t=1100.
        assert_eq!(decide(1100, 100, false, 1500, 10000), Decision::Wait);
        assert_eq!(decide(2500, 1500, false, 1500, 10000), Decision::Quiet);
        // Persistent verified child continues to wait, even when idle.
        assert_eq!(decide(9999, 9999, true, 1500, 10000), Decision::Wait);
        assert_eq!(decide(10000, 10000, true, 1500, 10000), Decision::Timeout);
        // Background reads/context are not activity: the quiet clock stays old.
        assert_eq!(decide(1600, 1600, false, 1500, 10000), Decision::Quiet);
        // Actual target activity continuing to deadline is still incomplete.
        assert_eq!(decide(10000, 0, false, 1500, 10000), Decision::Timeout);
    }
}
