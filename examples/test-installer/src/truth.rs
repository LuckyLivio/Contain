//! Independent ground truth: syscall boundaries and the fixture's own process handle.
//! This module intentionally does not depend on contain-core or its attribution rules.
use anyhow::Result;
use serde_json::json;
use std::{
    cell::{Cell, RefCell},
    fs::{File, OpenOptions},
    io::Write,
    path::Path,
    time::{SystemTime, UNIX_EPOCH},
};
use windows_sys::Win32::{
    Foundation::FILETIME,
    System::{
        Performance::{QueryPerformanceCounter, QueryPerformanceFrequency},
        SystemInformation::GetSystemTimePreciseAsFileTime,
        Threading::{GetCurrentProcess, GetCurrentThreadId, GetProcessTimes},
    },
};

// Independent Win32 measurements, deliberately not shared with contain-core.
// Opt-in keeps historical fixture measurements and the frozen scorer unchanged.
struct ClockProtocol {
    frequency: u64,
    sequence: Cell<u64>,
    marker_sequence: Cell<u64>,
}

fn qpc() -> Result<u64> {
    let mut value = 0_i64;
    // SAFETY: the API receives a valid writable LARGE_INTEGER.
    anyhow::ensure!(
        unsafe { QueryPerformanceCounter(&mut value) } != 0 && value >= 0,
        "independent QPC sample unavailable"
    );
    Ok(value as u64)
}

fn calibration() -> Result<serde_json::Value> {
    let before = qpc()?;
    let mut filetime = FILETIME::default();
    // SAFETY: filetime is a valid writable FILETIME.
    unsafe { GetSystemTimePreciseAsFileTime(&mut filetime) };
    let after = qpc()?;
    anyhow::ensure!(after >= before, "independent QPC sample regressed");
    let wall = (u64::from(filetime.dwHighDateTime) << 32) | u64::from(filetime.dwLowDateTime);
    Ok(
        json!({"qpc_before":before.to_string(),"filetime_ticks":wall.to_string(),"qpc_after":after.to_string()}),
    )
}

impl ClockProtocol {
    fn new() -> Result<Self> {
        let mut frequency = 0_i64;
        // SAFETY: the API receives a valid writable LARGE_INTEGER.
        anyhow::ensure!(
            unsafe { QueryPerformanceFrequency(&mut frequency) } != 0 && frequency > 0,
            "independent QPC frequency unavailable"
        );
        Ok(Self {
            frequency: frequency as u64,
            sequence: Cell::new(0),
            marker_sequence: Cell::new(0),
        })
    }
}

pub fn ticks() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos() as u64
        / 100
        + 116_444_736_000_000_000
}
pub struct Truth {
    journal: RefCell<File>,
    marker_journal: Option<RefCell<File>>,
    role: String,
    birth: u64,
    clock: Option<ClockProtocol>,
}
impl Truth {
    pub fn new(root: &Path, role: &str) -> Result<Self> {
        // SAFETY: the current-process pseudo handle stays valid and all outputs are allocated.
        let birth = unsafe {
            let mut c = std::mem::zeroed();
            let mut e = std::mem::zeroed();
            let mut k = std::mem::zeroed();
            let mut u = std::mem::zeroed();
            if GetProcessTimes(GetCurrentProcess(), &mut c, &mut e, &mut k, &mut u) == 0 {
                return Err(std::io::Error::last_os_error().into());
            }
            (u64::from(c.dwHighDateTime) << 32) | u64::from(c.dwLowDateTime)
        };
        let journal_root = if let Some(path) = std::env::var_os("CONTAIN_FIXTURE_TRUTH") {
            let (path, _) = crate::checked_root(Path::new(&path))?;
            anyhow::ensure!(
                std::fs::read(path.join(".contain-demo-marker"))? == b"Contain test fixture",
                "truth root marker mismatch"
            );
            anyhow::ensure!(
                path != root,
                "truth journal must be outside the watched root"
            );
            path
        } else {
            root.to_path_buf()
        };
        let journal = OpenOptions::new()
            .create(true)
            .append(true)
            .open(journal_root.join(format!("ground-truth-{}.jsonl", std::process::id())))?;
        let clock = if std::env::var_os("CONTAIN_FIXTURE_CLOCK").is_some_and(|v| v == "1") {
            Some(ClockProtocol::new()?)
        } else {
            None
        };
        // Separate filename and sequence: these control markers must never be
        // mistaken for the frozen oracle's 21 instrumented operation rows.
        let marker_journal = clock
            .as_ref()
            .map(|_| {
                OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(journal_root.join(format!("clock-marker-{}.jsonl", std::process::id())))
                    .map(RefCell::new)
            })
            .transpose()?;
        Ok(Self {
            journal: RefCell::new(journal),
            marker_journal,
            role: role.into(),
            birth,
            clock,
        })
    }
    fn diagnostic_marker<T>(
        &self,
        marker: &str,
        resource: &str,
        run: impl FnOnce() -> std::io::Result<T>,
    ) -> Result<T> {
        let Some(clock) = &self.clock else {
            return Ok(run()?);
        };
        let journal = self
            .marker_journal
            .as_ref()
            .ok_or_else(|| anyhow::anyhow!("independent marker journal missing"))?;
        let before = calibration()?;
        // SAFETY: GetCurrentThreadId has no preconditions.
        let thread_id = unsafe { GetCurrentThreadId() };
        let start = qpc()?;
        let result = run();
        let end = qpc()?;
        let after = calibration()?;
        anyhow::ensure!(end >= start, "independent marker QPC regressed");
        let sequence = clock.marker_sequence.get() + 1;
        clock.marker_sequence.set(sequence);
        let entry = json!({"schema_version":1,"kind":"fixture_control_marker","marker":marker,
            "role":self.role,"pid":std::process::id(),"creation_time":self.birth.to_string(),
            "resource":resource,"success":result.is_ok(),"error":result.as_ref().err().and_then(|e|e.raw_os_error()),
            "clock":{"schema_version":1,"qpc_frequency":clock.frequency.to_string(),"sequence":sequence,
                "thread_id":thread_id,"start_qpc":start.to_string(),"end_qpc":end.to_string(),"before":before,"after":after}});
        let mut line = serde_json::to_vec(&entry)?;
        line.push(b'\n');
        journal.borrow_mut().write_all(&line)?;
        Ok(result?)
    }
    /// Times the pre-wait handle acquisition independently of Contain. No new
    /// oracle row, workload operation, or inferred registry ownership is added.
    pub fn open_key(&self, path: &str) -> Result<winreg::RegKey> {
        self.diagnostic_marker("initial_registry_open", &format!("HKCU\\{path}"), || {
            winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
                .create_subkey(path)
                .map(|(key, _)| key)
        })
    }
    pub fn record<T>(
        &self,
        operation: &str,
        resource: &str,
        destination: Option<&Path>,
        run: impl FnOnce() -> std::io::Result<T>,
    ) -> Result<bool> {
        let before = self.clock.as_ref().map(|_| calibration()).transpose()?;
        // SAFETY: GetCurrentThreadId has no preconditions. Record before measuring
        // the operation so the QPC interval excludes metadata/serialization work.
        let thread_id = self.clock.as_ref().map(|_| unsafe { GetCurrentThreadId() });
        let start = ticks();
        let start_qpc = self.clock.as_ref().map(|_| qpc()).transpose()?;
        let result = run();
        let end_qpc = self.clock.as_ref().map(|_| qpc()).transpose()?;
        let end = ticks();
        let after = self.clock.as_ref().map(|_| calibration()).transpose()?;
        let mut entry = json!({"role":self.role,"pid":std::process::id(),"creation_time":self.birth.to_string(),"operation":operation,"resource":resource,"destination":destination.map(|p|p.to_string_lossy()),"start_ticks":start.to_string(),"end_ticks":end.to_string(),"success":result.is_ok(),"error":result.as_ref().err().and_then(|e|e.raw_os_error())});
        if let Some(clock) = &self.clock {
            anyhow::ensure!(end_qpc >= start_qpc, "independent operation QPC regressed");
            let sequence = clock.sequence.get() + 1;
            clock.sequence.set(sequence);
            entry["clock"] = json!({
                "schema_version":1,
                "qpc_frequency":clock.frequency.to_string(),
                "sequence":sequence,
                "thread_id":thread_id,
                "start_qpc":start_qpc.map(|v|v.to_string()),
                "end_qpc":end_qpc.map(|v|v.to_string()),
                "before":before,
                "after":after,
            });
        }
        // Value::Display streams JSON fragments through Write::write_fmt. Sending
        // those fragments straight to File creates many observable WriteFile calls.
        // Serialize first so one oracle row performs one ordinary small file write.
        let mut line = serde_json::to_vec(&entry)?;
        line.push(b'\n');
        self.journal.borrow_mut().write_all(&line)?;
        Ok(result.is_ok())
    }
    pub fn write(&self, path: &Path, bytes: &[u8]) -> Result<()> {
        anyhow::ensure!(
            self.record("write_requested", &path.to_string_lossy(), None, || {
                std::fs::write(path, bytes)
            })?,
            "fixture write failed"
        );
        Ok(())
    }
    pub fn rename(&self, old: &Path, new: &Path) -> Result<()> {
        anyhow::ensure!(
            self.record(
                "rename_requested",
                &old.to_string_lossy(),
                Some(new),
                || std::fs::rename(old, new)
            )?,
            "fixture rename failed"
        );
        Ok(())
    }
    pub fn delete(&self, path: &Path) -> Result<()> {
        anyhow::ensure!(
            self.record("delete_requested", &path.to_string_lossy(), None, || {
                std::fs::remove_file(path)
            })?,
            "fixture delete failed"
        );
        Ok(())
    }
    pub fn set(&self, key: &winreg::RegKey, path: &str, name: &str, value: &str) -> Result<()> {
        anyhow::ensure!(
            self.record("set_value", &format!("HKCU\\{path}\\{name}"), None, || key
                .set_value(name, &value))?,
            "fixture registry write failed"
        );
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn journal(clock: Option<ClockProtocol>) -> (Truth, std::path::PathBuf) {
        static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
        let path = std::env::temp_dir().join(format!(
            "contain-truth-clock-{}-{}-{}.jsonl",
            std::process::id(),
            NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed),
            ticks()
        ));
        let file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
            .unwrap();
        (
            Truth {
                journal: RefCell::new(file),
                marker_journal: None,
                role: "test".into(),
                birth: 1,
                clock,
            },
            path,
        )
    }

    #[test]
    fn optional_clock_preserves_default_legacy_rows() {
        let (truth, path) = journal(None);
        truth
            .record("write_requested", "fixture", None, || Ok(()))
            .unwrap();
        drop(truth);
        let row: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&path).unwrap()).unwrap();
        assert!(row.get("clock").is_none());
        assert!(row["start_ticks"].is_string());
        assert!(row["end_ticks"].is_string());
        assert_eq!(row["success"], true);
        std::fs::remove_file(path).unwrap();
    }

    #[test]
    fn control_marker_is_separate_from_frozen_oracle_and_retains_failed_call() {
        let (mut truth, path) = journal(Some(ClockProtocol::new().unwrap()));
        let marker_path = path.with_extension("marker.jsonl");
        truth.marker_journal = Some(RefCell::new(
            OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&marker_path)
                .unwrap(),
        ));
        assert!(
            truth
                .diagnostic_marker("initial_registry_open", "HKCU\\fixture", || Err::<(), _>(
                    std::io::Error::from_raw_os_error(5)
                ))
                .is_err()
        );
        truth
            .record("write_requested", "fixture", None, || Ok(()))
            .unwrap();
        drop(truth);
        let rows: Vec<serde_json::Value> = std::fs::read_to_string(&path)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0]["clock"]["sequence"], 1);
        let marker: serde_json::Value =
            serde_json::from_slice(&std::fs::read(&marker_path).unwrap()).unwrap();
        assert_eq!(marker["kind"], "fixture_control_marker");
        assert_eq!(marker["success"], false);
        assert_eq!(marker["error"], 5);
        let tick = |name| {
            marker["clock"][name]
                .as_str()
                .unwrap()
                .parse::<u64>()
                .unwrap()
        };
        assert!(tick("start_qpc") <= tick("end_qpc"));
        std::fs::remove_file(path).unwrap();
        std::fs::remove_file(marker_path).unwrap();
    }

    #[test]
    fn independent_qpc_bounds_include_failed_calls_and_exclude_journal_writes() {
        let (truth, path) = journal(Some(ClockProtocol::new().unwrap()));
        assert!(
            !truth
                .record("delete_requested", "fixture", None, || {
                    Err::<(), _>(std::io::Error::from_raw_os_error(5))
                })
                .unwrap()
        );
        truth
            .record("write_requested", "fixture", None, || Ok(()))
            .unwrap();
        drop(truth);
        let rows: Vec<serde_json::Value> = std::fs::read_to_string(&path)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        assert_eq!(rows[0]["success"], false);
        assert_eq!(rows[0]["error"], 5);
        for (index, row) in rows.iter().enumerate() {
            let clock = &row["clock"];
            let tick = |value: &serde_json::Value| value.as_str().unwrap().parse::<u64>().unwrap();
            assert_eq!(clock["sequence"], (index + 1) as u64);
            assert!(clock["thread_id"].as_u64().unwrap() > 0);
            assert!(tick(&clock["qpc_frequency"]) > 0);
            assert!(tick(&clock["before"]["qpc_before"]) <= tick(&clock["before"]["qpc_after"]));
            assert!(tick(&clock["before"]["qpc_after"]) <= tick(&clock["start_qpc"]));
            assert!(tick(&clock["start_qpc"]) <= tick(&clock["end_qpc"]));
            assert!(tick(&clock["end_qpc"]) <= tick(&clock["after"]["qpc_before"]));
            assert!(tick(&clock["after"]["qpc_before"]) <= tick(&clock["after"]["qpc_after"]));
        }
        std::fs::remove_file(path).unwrap();
    }
}
