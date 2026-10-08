//! Opt-in checkpoint experiment. The WAL budget is a sampled stop threshold,
//! not a physical file cap; one transaction and SQLite bookkeeping may overshoot.
use super::{Storage, stream::StreamStats};
use anyhow::{Context, Result};
use std::{path::PathBuf, time::Instant};

pub(crate) const AUTO_PAGES: u32 = 4096;
const WAL_BUDGET: u64 = 512 * 1024 * 1024;
const DISK_RESERVE: u64 = 64 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Default)]
pub(crate) struct Policy {
    pub variant: Option<&'static str>,
    pub budget: u64,
}
impl Policy {
    pub fn from_env() -> Result<Self> {
        let variant = std::env::var("CONTAIN_CHECKPOINT_VARIANT").ok();
        let budget = std::env::var("CONTAIN_WAL_BUDGET_BYTES").ok();
        Self::parse(variant.as_deref(), budget.as_deref())
    }
    fn parse(variant: Option<&str>, budget: Option<&str>) -> Result<Self> {
        let variant = match variant {
            None => {
                anyhow::ensure!(
                    budget.is_none(),
                    "CONTAIN_WAL_BUDGET_BYTES requires CONTAIN_CHECKPOINT_VARIANT=E0 or E1"
                );
                return Ok(Self::default());
            }
            Some("E0") => "E0",
            Some("E1") => "E1",
            _ => anyhow::bail!("CONTAIN_CHECKPOINT_VARIANT must be E0 or E1"),
        };
        let budget = budget
            .map(str::parse::<u64>)
            .transpose()
            .context("CONTAIN_WAL_BUDGET_BYTES must be a positive integer")?
            .unwrap_or(WAL_BUDGET);
        anyhow::ensure!(budget > 0, "WAL budget must be positive");
        Ok(Self {
            variant: Some(variant),
            budget,
        })
    }
    pub fn deferred(self) -> bool {
        self.variant == Some("E1")
    }
}

// A safe borrowing guard restores the connection setting on early Result exits
// and unwinding. Normal exits also surface a restoration failure to the caller.
struct Restore<'a> {
    db: &'a mut Storage,
    pending: bool,
}
impl Restore<'_> {
    fn restore(&mut self) -> Result<()> {
        if self.pending {
            restore(self.db)?;
            self.pending = false;
        }
        Ok(())
    }
}
impl Drop for Restore<'_> {
    fn drop(&mut self) {
        let _ = self.restore();
    }
}
pub(crate) fn scoped<T>(
    db: &mut Storage,
    policy: Policy,
    action: impl FnOnce(&mut Storage) -> Result<T>,
) -> Result<T> {
    let mut guard = Restore {
        db,
        pending: policy.deferred(),
    };
    let result = action(&mut *guard.db);
    let restored = guard.restore();
    match (result, restored) {
        (result, Ok(())) => result,
        (Ok(_), Err(e)) => Err(e.context("restoring automatic checkpoint")),
        (Err(e), Err(restore)) => Err(e.context(format!(
            "automatic checkpoint restoration also failed: {restore:#}"
        ))),
    }
}
pub(crate) fn restore(db: &Storage) -> Result<u32> {
    db.connection
        .pragma_update(None, "wal_autocheckpoint", AUTO_PAGES)?;
    let pages = db
        .connection
        .pragma_query_value(None, "wal_autocheckpoint", |r| r.get::<_, u32>(0))?;
    anyhow::ensure!(
        pages == AUTO_PAGES,
        "automatic checkpoint was not restored to {AUTO_PAGES}"
    );
    Ok(pages)
}

#[derive(Clone, Debug, Default, serde::Serialize, serde::Deserialize)]
pub struct Report {
    pub mode: String,
    pub busy: Option<i64>,
    pub log_frames: Option<i64>,
    pub checkpointed_frames: Option<i64>,
    pub duration_ns: u64,
    pub error: Option<String>,
}

pub(crate) fn complete(db: &Storage, stats: &mut StreamStats) {
    if stats.checkpoint_variant.as_deref() != Some("E1") {
        return;
    }
    let start = Instant::now();
    let result = db
        .connection
        .query_row("PRAGMA wal_checkpoint(PASSIVE)", [], |r| {
            Ok((
                r.get::<_, i64>(0)?,
                r.get::<_, i64>(1)?,
                r.get::<_, i64>(2)?,
            ))
        });
    let mut report = Report {
        mode: "PASSIVE".into(),
        duration_ns: start.elapsed().as_nanos() as u64,
        ..Default::default()
    };
    crate::profile::add("post_raw_checkpoint", report.duration_ns);
    match result {
        Ok((busy, log, done)) => {
            report.busy = Some(busy);
            report.log_frames = Some(log);
            report.checkpointed_frames = Some(done);
            // PASSIVE can return busy=0 while a reader prevents full progress.
            stats.checkpoint_completed = busy == 0 && log >= 0 && done == log;
            if !stats.checkpoint_completed {
                report.error = Some(format!(
                    "PASSIVE checkpoint incomplete: busy={busy}, log={log}, checkpointed={done}"
                ));
            }
        }
        Err(e) => report.error = Some(format!("PASSIVE checkpoint failed: {e}")),
    }
    if let Some(error) = &report.error {
        record_error(stats, error.clone());
    }
    stats.checkpoint = Some(report);
    match restore(db) {
        Ok(pages) => stats.restored_wal_autocheckpoint_pages = Some(pages),
        Err(e) => record_error(
            stats,
            format!("automatic checkpoint restoration failed: {e:#}"),
        ),
    }
}

pub(crate) fn record_error(stats: &mut StreamStats, error: String) {
    match &mut stats.error {
        Some(previous) => {
            previous.push_str("; ");
            previous.push_str(&error);
        }
        None => stats.error = Some(error),
    }
}

pub(crate) fn sample(db: &Storage, stats: &mut StreamStats) -> Result<()> {
    let Some(budget) = stats.wal_budget_bytes else {
        return Ok(());
    };
    let _timer = crate::profile::timer("raw_wal_budget_sample");
    anyhow::ensure!(
        stats.sqlite_journal_mode.as_deref() == Some("wal"),
        "checkpoint experiment requires WAL journal mode"
    );
    let path = db
        .connection
        .path()
        .filter(|p| !p.is_empty() && *p != ":memory:")
        .context("checkpoint experiment requires a file-backed database")?;
    let wal = PathBuf::from(format!("{path}-wal"));
    let size = match std::fs::metadata(&wal) {
        Ok(m) => m.len(),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => 0,
        Err(e) => return Err(anyhow::Error::new(e).context("sampling WAL size")),
    };
    stats.wal_samples += 1;
    let initial = *stats.wal_initial_bytes.get_or_insert(size);
    let peak = stats.wal_peak_bytes.get_or_insert(size);
    *peak = (*peak).max(size);
    stats.wal_growth_bytes = Some(peak.saturating_sub(initial));
    stats.wal_budget_overshoot_bytes = Some(peak.saturating_sub(budget));
    stats.wal_budget_exceeded |= size >= budget;
    // Available-to-caller bytes respect disk quotas. Sample both experiment arms
    // identically; this is not a reservation and concurrent writers may consume it.
    let directory = std::fs::canonicalize(path)?
        .parent()
        .context("database has no parent")?
        .to_path_buf();
    use std::os::windows::ffi::OsStrExt;
    let wide: Vec<u16> = directory.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut free = 0;
    let ok = unsafe {
        windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW(
            wide.as_ptr(),
            &mut free,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        )
    };
    if ok == 0 {
        return Err(std::io::Error::last_os_error().into());
    }
    stats.disk_reserve_bytes = Some(DISK_RESERVE);
    stats.disk_free_min_bytes = Some(stats.disk_free_min_bytes.unwrap_or(free).min(free));
    anyhow::ensure!(
        !stats.wal_budget_exceeded,
        "sampled WAL stop threshold reached: {size} >= {budget} bytes; further raw writes stopped"
    );
    anyhow::ensure!(
        free > DISK_RESERVE,
        "available disk space {free} <= reserve {DISK_RESERVE}; further raw writes stopped"
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn explicit_policy_and_positive_budget_required() {
        assert!(Policy::parse(None, None).unwrap().variant.is_none());
        assert!(!Policy::parse(Some("E0"), None).unwrap().deferred());
        assert!(Policy::parse(Some("E1"), Some("4096")).unwrap().deferred());
        for (variant, budget) in [
            (None, Some("1")),
            (Some("typo"), None),
            (Some("E1"), Some("0")),
            (Some("E1"), Some("-1")),
        ] {
            assert!(Policy::parse(variant, budget).is_err());
        }
    }
    #[test]
    fn scope_restores_on_error_and_unwind() {
        let mut db = Storage::open(std::path::Path::new(":memory:")).unwrap();
        let policy = Policy::parse(Some("E1"), None).unwrap();
        let result: Result<()> = scoped(&mut db, policy, |db| {
            db.connection.pragma_update(None, "wal_autocheckpoint", 0)?;
            anyhow::bail!("injected early capture exit")
        });
        assert!(result.is_err());
        assert_eq!(
            db.connection
                .pragma_query_value(None, "wal_autocheckpoint", |r| r.get::<_, u32>(0))
                .unwrap(),
            AUTO_PAGES
        );
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            scoped(&mut db, policy, |db| -> Result<()> {
                db.connection.pragma_update(None, "wal_autocheckpoint", 0)?;
                panic!("injected unwind")
            })
        }));
        assert!(result.is_err());
        assert_eq!(
            db.connection
                .pragma_query_value(None, "wal_autocheckpoint", |r| r.get::<_, u32>(0))
                .unwrap(),
            AUTO_PAGES
        );
    }
}
