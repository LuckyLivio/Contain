use crate::report::{Check, Status};
use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use std::{
    path::Path,
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant},
};
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Step {
    pub name: String,
    pub program: String,
    #[serde(default)]
    pub args: Vec<String>,
    pub expected_exit: i32,
    pub timeout_ms: u64,
}
fn stop(child: &mut Child) {
    #[cfg(windows)]
    {
        let _ = Command::new("taskkill.exe")
            .args(["/PID", &child.id().to_string(), "/T", "/F"])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
    }
    let _ = child.kill();
    let _ = child.wait();
}
pub fn execute(step: &Step, directory: &Path) -> Check {
    match run(step, directory) {
        Ok(code) if code == Some(step.expected_exit) => Check::new(
            &step.name,
            Status::Passed,
            format!(
                "expected exit {} observed; output omitted",
                step.expected_exit
            ),
        ),
        Ok(code) => Check::new(
            &step.name,
            Status::Failed,
            format!(
                "expected exit {}, observed {code:?}; output omitted",
                step.expected_exit
            ),
        ),
        Err(_) => Check::new(
            &step.name,
            Status::Failed,
            "could not start or timed out; output omitted",
        ),
    }
}
fn run(step: &Step, directory: &Path) -> Result<Option<i32>> {
    let mut child = Command::new(&step.program)
        .args(&step.args)
        .current_dir(directory)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()?;
    let start = Instant::now();
    loop {
        match child.try_wait() {
            Ok(Some(status)) => return Ok(status.code()),
            Err(e) => {
                stop(&mut child);
                return Err(e.into());
            }
            _ => {}
        }
        if start.elapsed() >= Duration::from_millis(step.timeout_ms) {
            stop(&mut child);
            bail!("timeout");
        }
        thread::sleep(Duration::from_millis(20));
    }
}
/// Probe output is used only for version matching, never serialized into a report.
/// Redirect to an anonymous temporary file to avoid pipe deadlocks; cap reads.
pub fn probe(program: &str, args: &[String]) -> Result<String> {
    probe_in(program, args, None)
}
pub fn probe_in(program: &str, args: &[String], directory: Option<&Path>) -> Result<String> {
    use std::io::{Read, Seek};
    let path = std::env::temp_dir().join(format!(
        "contain-probe-{}-{}",
        std::process::id(),
        COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
    ));
    let mut file = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create_new(true)
        .open(&path)?;
    let result = (|| {
        let mut command = Command::new(program);
        command
            .args(args)
            .stdin(Stdio::null())
            .stdout(file.try_clone()?)
            .stderr(Stdio::null());
        if let Some(directory) = directory {
            command.current_dir(directory);
        }
        let mut child = command.spawn().context("probe launch")?;
        let start = Instant::now();
        loop {
            if let Some(status) = child.try_wait()? {
                if !status.success() {
                    bail!("probe failed");
                }
                break;
            }
            if file.metadata()?.len() > 65536 || start.elapsed() > Duration::from_secs(10) {
                stop(&mut child);
                bail!("probe timeout");
            }
            thread::sleep(Duration::from_millis(20));
        }
        file.rewind()?;
        let mut text = String::new();
        file.by_ref().take(65536).read_to_string(&mut text)?;
        Ok(text)
    })();
    drop(file);
    let _ = std::fs::remove_file(path);
    result
}
static COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

pub fn window_launch(
    program: &Path,
    args: &[String],
    title: &str,
    timeout_ms: u64,
    directory: &Path,
) -> Check {
    #[cfg(windows)]
    {
        let mut child = match Command::new(program)
            .args(args)
            .current_dir(directory)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
        {
            Ok(c) => c,
            Err(_) => return Check::new("launch", Status::Failed, "CreateProcess failed"),
        };
        let start = Instant::now();
        let mut found = false;
        while start.elapsed() < Duration::from_millis(timeout_ms) {
            if has_window(child.id(), title) {
                found = true;
                break;
            }
            if !matches!(child.try_wait(), Ok(None)) {
                break;
            }
            thread::sleep(Duration::from_millis(50));
        }
        stop(&mut child);
        Check::new(
            "launch",
            if found {
                Status::Passed
            } else {
                Status::Failed
            },
            "visible window title checked against the launched process PID; window presence alone is not test acceptance",
        )
    }
    #[cfg(not(windows))]
    {
        let _ = (program, args, title, timeout_ms, directory);
        Check::new("launch", Status::NotRun, "requires Windows")
    }
}
#[cfg(windows)]
fn has_window(pid: u32, title: &str) -> bool {
    use windows_sys::Win32::{
        Foundation::{HWND, LPARAM},
        UI::WindowsAndMessaging::*,
    };
    struct Query<'a> {
        pid: u32,
        title: &'a str,
        found: bool,
    }
    unsafe extern "system" fn visit(hwnd: HWND, data: LPARAM) -> windows_sys::core::BOOL {
        unsafe {
            let query = &mut *(data as *mut Query<'_>);
            let mut pid = 0;
            GetWindowThreadProcessId(hwnd, &mut pid);
            if pid == query.pid && IsWindowVisible(hwnd) != 0 {
                let mut buffer = [0u16; 4096];
                let n = GetWindowTextW(hwnd, buffer.as_mut_ptr(), buffer.len() as i32);
                if n > 0 && String::from_utf16_lossy(&buffer[..n as usize]) == query.title {
                    query.found = true;
                    return 0;
                }
            }
            1
        }
    }
    let mut query = Query {
        pid,
        title,
        found: false,
    };
    unsafe {
        EnumWindows(Some(visit), &mut query as *mut _ as LPARAM);
    }
    query.found
}
