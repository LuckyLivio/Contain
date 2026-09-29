//! Supplemental diagnostic only. Original fixture and oracle remain unchanged.
#[allow(dead_code)] // Shared frozen oracle also exposes file helpers used by the original fixture.
mod truth;
use anyhow::{Context, Result, ensure};
use clap::Parser;
use std::{
    fs,
    os::windows::fs::MetadataExt,
    path::{Path, PathBuf},
    process::Command,
    thread,
    time::{Duration, Instant},
};
use winreg::{RegKey, enums::HKEY_CURRENT_USER};

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
        fs::write(
            root.join(format!(".registry-{}-ready", args.phase)),
            b"ready",
        )?;
        while !root.join(".fixture-go").is_file() {
            ensure!(Instant::now() < deadline, "registry noise start deadline");
            thread::sleep(Duration::from_millis(1)); // Start signal only; no sleeps between operations.
        }
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
