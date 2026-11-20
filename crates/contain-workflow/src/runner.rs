use crate::report::{Check, Status};
use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use std::{
    path::{Path, PathBuf},
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
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StepLogRecord {
    pub phase: String,
    pub index: usize,
    pub name: String,
    pub status: Status,
    pub expected_exit: i32,
    pub observed_exit: Option<i32>,
    pub timed_out: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub launch_error: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stdout_file: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stderr_file: Option<String>,
}
enum RunFailure {
    TimedOut,
    LaunchOrIo(String),
}
fn resolve_program(program: &str, directory: &Path) -> PathBuf {
    let p = Path::new(program);
    if !p.is_absolute() && program.contains(['/', '\\']) {
        directory.join(p)
    } else {
        p.to_path_buf()
    }
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
    execute_logged(step, directory, environment, "step", 0, None).0
}
pub fn execute_logged(
    step: &Step,
    directory: &Path,
    environment: &std::collections::BTreeMap<String, String>,
    phase: &str,
    index: usize,
    log_dir: Option<&Path>,
) -> (Check, StepLogRecord) {
    let (stdout_file, stderr_file) = match log_dir {
        Some(_) => (
            Some(format!("{phase}-{:02}.stdout.local.log", index + 1)),
            Some(format!("{phase}-{:02}.stderr.local.log", index + 1)),
        ),
        None => (None, None),
    };
    let required_text = format!("exit {}", step.expected_exit);
    match run(
        step,
        directory,
        environment,
        log_dir,
        stdout_file.as_deref(),
        stderr_file.as_deref(),
    ) {
        Ok((code, digest))
            if code == Some(step.expected_exit)
                && step
                    .stdout_sha256
                    .as_ref()
                    .is_none_or(|expected| Some(expected.to_lowercase()) == digest) =>
        {
            let check = Check::new(
                &step.name,
                Status::Passed,
                format!(
                    "expected exit {} matched; observed stdout SHA-256 {}; output omitted",
                    step.expected_exit,
                    digest.as_deref().unwrap_or("not requested")
                ),
            )
            .with_evidence(
                Some("step_passed".into()),
                Some(required_text),
                code.map(|c| format!("exit {c}")),
                None,
            );
            let record = StepLogRecord {
                phase: phase.into(),
                index,
                name: step.name.clone(),
                status: Status::Passed,
                expected_exit: step.expected_exit,
                observed_exit: code,
                timed_out: false,
                launch_error: None,
                stdout_file,
                stderr_file,
            };
            (check, record)
        }
        Ok((code, digest)) => {
            let check = Check::new(
                &step.name,
                Status::Failed,
                format!(
                    "expected exit {}, observed {code:?}; stdout SHA-256 {}; exit or digest mismatch; output omitted",
                    step.expected_exit,
                    digest.as_deref().unwrap_or("not requested")
                ),
            )
            .with_evidence(
                Some("exit_or_digest_mismatch".into()),
                Some(required_text),
                code.map(|c| format!("exit {c}")),
                None,
            );
            let record = StepLogRecord {
                phase: phase.into(),
                index,
                name: step.name.clone(),
                status: Status::Failed,
                expected_exit: step.expected_exit,
                observed_exit: code,
                timed_out: false,
                launch_error: None,
                stdout_file,
                stderr_file,
            };
            (check, record)
        }
        Err(failure) => {
            let (timed_out, outcome, launch_error) = match failure {
                RunFailure::TimedOut => (true, "step_timed_out", None),
                RunFailure::LaunchOrIo(msg) => (false, "step_launch_failed", Some(msg)),
            };
            let check = Check::new(
                &step.name,
                Status::Failed,
                "could not start or timed out; output omitted",
            )
            .with_evidence(Some(outcome.into()), Some(required_text), None, None);
            let record = StepLogRecord {
                phase: phase.into(),
                index,
                name: step.name.clone(),
                status: Status::Failed,
                expected_exit: step.expected_exit,
                observed_exit: None,
                timed_out,
                launch_error,
                stdout_file,
                stderr_file,
            };
            (check, record)
        }
    }
}
fn run(
    step: &Step,
    directory: &Path,
    environment: &std::collections::BTreeMap<String, String>,
    log_dir: Option<&Path>,
    stdout_name: Option<&str>,
    stderr_name: Option<&str>,
) -> std::result::Result<(Option<i32>, Option<String>), RunFailure> {
    let mut temp_capture = None;
    let mut local_stdout_path = None;
    let mut local_stdout_file = None;
    let mut local_stderr_file = None;

    if let (Some(dir), Some(out_rel), Some(err_rel)) = (log_dir, stdout_name, stderr_name) {
        let out_path = dir.join(out_rel);
        let err_path = dir.join(err_rel);
        let out_file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&out_path)
            .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?;
        let err_file = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&err_path)
            .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?;
        local_stdout_path = Some(out_path);
        local_stdout_file = Some(out_file);
        local_stderr_file = Some(err_file);
    } else if step.stdout_sha256.is_some() {
        temp_capture = Some(Capture::new().map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?);
    }

    let stdout = if let Some(f) = &local_stdout_file {
        Stdio::from(
            f.try_clone()
                .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?,
        )
    } else if let Some(c) = &temp_capture {
        Stdio::from(
            c.file
                .try_clone()
                .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?,
        )
    } else {
        Stdio::null()
    };

    let stderr = if let Some(f) = &local_stderr_file {
        Stdio::from(
            f.try_clone()
                .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?,
        )
    } else {
        Stdio::null()
    };

    let program = resolve_program(&step.program, directory);
    let mut child = Command::new(&program)
        .args(&step.args)
        .envs(environment)
        .current_dir(directory)
        .stdin(Stdio::null())
        .stdout(stdout)
        .stderr(stderr)
        .spawn()
        .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?;

    let start = Instant::now();
    loop {
        match child.try_wait() {
            Ok(Some(status)) => {
                if let Some(f) = &local_stdout_file
                    && ensure_file_size(f).is_err()
                {
                    return Err(RunFailure::TimedOut);
                }
                if let Some(f) = &local_stderr_file
                    && ensure_file_size(f).is_err()
                {
                    return Err(RunFailure::TimedOut);
                }
                let digest = if step.stdout_sha256.is_some() {
                    if let Some(out_path) = &local_stdout_path {
                        let bytes = std::fs::read(out_path)
                            .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?;
                        Some(crate::artifact::sha256(&bytes))
                    } else if let Some(c) = &temp_capture {
                        ensure_capture_size(c).map_err(|_| RunFailure::TimedOut)?;
                        let bytes = std::fs::read(&c.path)
                            .map_err(|e| RunFailure::LaunchOrIo(e.to_string()))?;
                        Some(crate::artifact::sha256(&bytes))
                    } else {
                        None
                    }
                } else {
                    None
                };
                return Ok((status.code(), digest));
            }
            Err(e) => {
                stop(&mut child);
                return Err(RunFailure::LaunchOrIo(e.to_string()));
            }
            _ => {}
        }
        let oversized = temp_capture
            .as_ref()
            .is_some_and(|c| ensure_capture_size(c).is_err())
            || local_stdout_file
                .as_ref()
                .is_some_and(|f| ensure_file_size(f).is_err())
            || local_stderr_file
                .as_ref()
                .is_some_and(|f| ensure_file_size(f).is_err());
        if oversized || start.elapsed() >= Duration::from_millis(step.timeout_ms) {
            stop(&mut child);
            return Err(RunFailure::TimedOut);
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
fn ensure_file_size(file: &std::fs::File) -> Result<()> {
    anyhow::ensure!(
        file.metadata()?.len() <= 16 * 1024 * 1024,
        "command output limit"
    );
    Ok(())
}
fn ensure_capture_size(c: &Capture) -> Result<()> {
    ensure_file_size(&c.file)
}
