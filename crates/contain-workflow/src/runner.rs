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
    #[serde(default)]
    pub stdout_sha256: Option<String>,
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
    execute_in(step, directory, &std::collections::BTreeMap::new())
}
pub fn execute_in(
    step: &Step,
    directory: &Path,
    environment: &std::collections::BTreeMap<String, String>,
) -> Check {
    match run(step, directory, environment) {
        Ok((code, digest))
            if code == Some(step.expected_exit)
                && step
                    .stdout_sha256
                    .as_ref()
                    .is_none_or(|expected| Some(expected.to_lowercase()) == digest) =>
        {
            Check::new(
                &step.name,
                Status::Passed,
                format!(
                    "expected exit {} matched; observed stdout SHA-256 {}; output omitted",
                    step.expected_exit,
                    digest.as_deref().unwrap_or("not requested")
                ),
            )
        }
        Ok((code, digest)) => Check::new(
            &step.name,
            Status::Failed,
            format!(
                "expected exit {}, observed {code:?}; stdout SHA-256 {}; exit or digest mismatch; output omitted",
                step.expected_exit,
                digest.as_deref().unwrap_or("not requested")
            ),
        ),
        Err(_) => Check::new(
            &step.name,
            Status::Failed,
            "could not start or timed out; output omitted",
        ),
    }
}
fn run(
    step: &Step,
    directory: &Path,
    environment: &std::collections::BTreeMap<String, String>,
) -> Result<(Option<i32>, Option<String>)> {
    let capture = if step.stdout_sha256.is_some() {
        Some(Capture::new()?)
    } else {
        None
    };
    let stdout = match &capture {
        Some(c) => Stdio::from(c.file.try_clone()?),
        None => Stdio::null(),
    };
    let mut child = Command::new(&step.program)
        .args(&step.args)
        .envs(environment)
        .current_dir(directory)
        .stdin(Stdio::null())
        .stdout(stdout)
        .stderr(Stdio::null())
        .spawn()?;
    let start = Instant::now();
    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                let digest = match &capture {
                    Some(c) => {
                        ensure_capture_size(c)?;
                        Some(crate::artifact::sha256(&std::fs::read(&c.path)?))
                    }
                    None => None,
                };
                return Ok((status.code(), digest));
            }
            Err(e) => {
                stop(&mut child);
                return Err(e.into());
            }
            _ => {}
        }
        if capture
            .as_ref()
            .is_some_and(|c| ensure_capture_size(c).is_err())
            || start.elapsed() >= Duration::from_millis(step.timeout_ms)
        {
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
    probe_in_environment(program, args, directory, &std::collections::BTreeMap::new())
}
pub fn probe_in_environment(
    program: &str,
    args: &[String],
    directory: Option<&Path>,
    environment: &std::collections::BTreeMap<String, String>,
) -> Result<String> {
    probe_with_timeout(
        program,
        args,
        directory,
        environment,
        Duration::from_secs(10),
    )
}
pub fn probe_with_timeout(
    program: &str,
    args: &[String],
    directory: Option<&Path>,
    environment: &std::collections::BTreeMap<String, String>,
    timeout: Duration,
) -> Result<String> {
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
            .envs(environment)
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
            if file.metadata()?.len() > 65536 || start.elapsed() > timeout {
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

struct Capture {
    path: std::path::PathBuf,
    file: std::fs::File,
}
impl Capture {
    fn new() -> Result<Self> {
        let path = std::env::temp_dir().join(format!(
            "contain-output-{}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)?
                .as_nanos(),
            COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        ));
        let file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)?;
        Ok(Self { path, file })
    }
}
impl Drop for Capture {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}
fn ensure_capture_size(c: &Capture) -> Result<()> {
    anyhow::ensure!(
        c.file.metadata()?.len() <= 16 * 1024 * 1024,
        "command output limit"
    );
    Ok(())
}
