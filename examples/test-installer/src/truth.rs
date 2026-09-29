//! Independent ground truth: syscall boundaries and the fixture's own process handle.
//! This module intentionally does not depend on contain-core or its attribution rules.
use anyhow::Result;
use serde_json::json;
use std::{
    cell::RefCell,
    fs::{File, OpenOptions},
    io::Write,
    path::Path,
    time::{SystemTime, UNIX_EPOCH},
};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};

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
    role: String,
    birth: u64,
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
        Ok(Self {
            journal: RefCell::new(journal),
            role: role.into(),
            birth,
        })
    }
    pub fn record<T>(
        &self,
        operation: &str,
        resource: &str,
        destination: Option<&Path>,
        run: impl FnOnce() -> std::io::Result<T>,
    ) -> Result<bool> {
        let start = ticks();
        let result = run();
        let end = ticks();
        let entry = json!({"role":self.role,"pid":std::process::id(),"creation_time":self.birth.to_string(),"operation":operation,"resource":resource,"destination":destination.map(|p|p.to_string_lossy()),"start_ticks":start.to_string(),"end_ticks":end.to_string(),"success":result.is_ok(),"error":result.as_ref().err().and_then(|e|e.raw_os_error())});
        writeln!(self.journal.borrow_mut(), "{entry}")?;
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
