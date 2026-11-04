// Installed only after StartTrace succeeds. A failed provider enable or
// OpenTrace must stop that owned producer, but a StartTrace name collision must
// never stop the already existing session. Keep cleanup armed across `?` and
// unwinding until the completed trace takes ownership of its control handle.
pub(super) struct StartupOwnership<F: FnOnce()> {
    cleanup: Option<F>,
}

impl<F: FnOnce()> StartupOwnership<F> {
    pub(super) fn new(cleanup: F) -> Self {
        Self {
            cleanup: Some(cleanup),
        }
    }

    pub(super) fn transfer(mut self) {
        self.cleanup = None;
    }
}

impl<F: FnOnce()> Drop for StartupOwnership<F> {
    fn drop(&mut self) {
        if let Some(cleanup) = self.cleanup.take() {
            cleanup();
        }
    }
}

#[cfg(test)]
mod startup_ownership_tests {
    use super::StartupOwnership;
    use std::cell::Cell;

    #[test]
    fn startup_ownership_cleans_owned_producer_on_provider_or_open_failure() {
        for stage in ["provider", "open"] {
            let stops = Cell::new(0);
            let result = (|| -> Result<(), &str> {
                let _owned = StartupOwnership::new(|| stops.set(stops.get() + 1));
                Err(stage)?;
                Ok(())
            })();
            assert_eq!(result, Err(stage));
            assert_eq!(stops.get(), 1);
        }
    }

    #[test]
    fn startup_ownership_success_transfers_without_stopping_producer() {
        let stops = Cell::new(0);
        StartupOwnership::new(|| stops.set(stops.get() + 1)).transfer();
        assert_eq!(stops.get(), 0);
    }

    #[test]
    fn startup_ownership_rejected_start_never_obtains_cleanup_rights() {
        for failure in ["already_exists", "permission_denied"] {
            let stops = Cell::new(0);
            let result = (|| -> Result<(), &str> {
                let started: Result<(), &str> = Err(failure);
                started?;
                let _owned = StartupOwnership::new(|| stops.set(stops.get() + 1));
                Ok(())
            })();
            assert_eq!(result, Err(failure));
            assert_eq!(stops.get(), 0);
        }
    }

    #[test]
    fn startup_ownership_unwind_cleans_once() {
        let stops = Cell::new(0);
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let _owned = StartupOwnership::new(|| stops.set(stops.get() + 1));
            panic!("synthetic startup failure");
        }));
        assert!(result.is_err());
        assert_eq!(stops.get(), 1);
    }
}
