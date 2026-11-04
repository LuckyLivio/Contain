//! Supplemental diagnostic only. Original fixture and oracle remain unchanged.
#[allow(dead_code)] // Shared frozen oracle also exposes file helpers used by the original fixture.
mod truth;
use anyhow::{Context, Result, ensure};
use clap::Parser;
use std::{
    fs,
    os::windows::{ffi::OsStrExt, fs::MetadataExt},
    path::{Path, PathBuf},
    process::Command,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0},
    Storage::FileSystem::{
        FILE_NOTIFY_CHANGE_FILE_NAME, FindCloseChangeNotification, FindFirstChangeNotificationW,
        FindNextChangeNotification,
    },
    System::Threading::WaitForSingleObject,
};
use winreg::{RegKey, enums::HKEY_CURRENT_USER};

struct StartNotification(HANDLE);
impl Drop for StartNotification {
    fn drop(&mut self) {
        unsafe {
            FindCloseChangeNotification(self.0);
        }
    }
}

fn wait_for_start(root: &Path, phase: &str, deadline: Instant) -> Result<()> {
    let wide: Vec<u16> = root.as_os_str().encode_wide().chain(Some(0)).collect();
    // Subscribe before publishing readiness/checking the persistent signal file.
    let handle =
        unsafe { FindFirstChangeNotificationW(wide.as_ptr(), 0, FILE_NOTIFY_CHANGE_FILE_NAME) };
    ensure!(
        handle != INVALID_HANDLE_VALUE,
        "start notification: {}",
        std::io::Error::last_os_error()
    );
    let notification = StartNotification(handle);
    fs::write(root.join(format!(".registry-{phase}-ready")), b"ready")?;
    loop {
        if root.join(".fixture-go").is_file() {
            return Ok(());
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        ensure!(!remaining.is_zero(), "registry noise start deadline");
        let status = unsafe {
            WaitForSingleObject(
                notification.0,
                remaining.as_millis().min(u32::MAX as u128) as u32,
            )
        };
        ensure!(
            status == WAIT_OBJECT_0,
            "registry noise start wait failed or timed out: {status}"
        );
        // Re-arm before checking, so a create during the check is not missed.
        ensure!(
            unsafe { FindNextChangeNotification(notification.0) } != 0,
            "start notification rearm: {}",
            std::io::Error::last_os_error()
        );
    }
}

#[derive(Parser)]
struct Args {
    #[arg(long)]
    root: PathBuf,
    #[arg(long)]
    phase: String,
}

fn checked_root(path: &Path) -> Result<(PathBuf, String)> {
    ensure!(
        fs::symlink_metadata(path)?.file_attributes() & 0x400 == 0,
        "reparse fixture root"
    );
    let root = path.canonicalize()?;
    let temp = std::env::temp_dir().canonicalize()?;
    let name = root
        .file_name()
        .context("fixture name")?
        .to_string_lossy()
        .into_owned();
    ensure!(
        root.parent() == Some(temp.as_path()) && name.starts_with("contain-demo-"),
        "fixture must be marked direct TEMP child"
    );
    ensure!(
        fs::read(root.join(".contain-demo-marker"))? == b"Contain test fixture",
        "fixture marker mismatch"
    );
    Ok((root, name))
}

fn run() -> Result<()> {
    let args = Args::parse();
    ensure!(
        ["pre", "launcher", "post"].contains(&args.phase.as_str()),
        "unknown phase"
    );
    let (root, name) = checked_root(&args.root)?;
    let deadline = Instant::now() + Duration::from_secs(20);
    if args.phase != "post" {
        wait_for_start(&root, &args.phase, deadline)?;
    }
    if args.phase == "launcher" {
        // Launcher is independently started by the harness, outside the installer tree.
        let status = Command::new(std::env::current_exe()?)
            .args([
                "--root",
                root.to_str().context("root encoding")?,
                "--phase",
                "post",
            ])
            .status()?;
        ensure!(status.success(), "post-start noise failed");
        return Ok(());
    }
    let truth = truth::Truth::new(&root, "noise")?;
    let key_path = format!(
        "Software\\Contain\\Demo\\{name}\\Noise{}",
        if args.phase == "pre" { "Pre" } else { "Post" }
    );
    let (key, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(&key_path)?;
    for i in 0..1000 {
        ensure!(
            Instant::now() < deadline,
            "registry noise operation deadline"
        );
        truth.set(
            &key,
            &key_path,
            &format!("Value{i:04}"),
            "safe diagnostic fixture",
        )?;
    }
    println!("NOISE: phase={} operations=1000", args.phase);
    Ok(())
}

fn main() {
    if let Err(e) = run() {
        eprintln!("registry noise: {e:#}");
        std::process::exit(1);
    }
}
