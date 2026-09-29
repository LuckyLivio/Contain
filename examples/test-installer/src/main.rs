use anyhow::{Context, Result};
use clap::Parser;
use std::fs;
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::thread;
use std::time::Duration;
use winreg::RegKey;
use winreg::enums::HKEY_CURRENT_USER;

#[derive(Parser)]
struct Args {
    #[arg(long)]
    root: PathBuf,
    #[arg(long)]
    child: bool,
    #[arg(long)]
    cleanup: bool,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("test installer: {error:#}");
        std::process::exit(1);
    }
}

fn checked_root(path: &Path) -> Result<(PathBuf, String)> {
    if fs::symlink_metadata(path)?.file_attributes() & 0x400 != 0 {
        anyhow::bail!("demo root is a reparse point; refusing to use it");
    }
    let root = path
        .canonicalize()
        .with_context(|| format!("demo root must exist: {}", path.display()))?;
    let temp = std::env::temp_dir().canonicalize()?;
    let name = root
        .file_name()
        .context("demo root has no name")?
        .to_string_lossy()
        .into_owned();
    if root.parent() != Some(temp.as_path()) || !name.starts_with("contain-demo-") {
        anyhow::bail!(
            "demo root must be a direct child of the system temp directory named contain-demo-*"
        );
    }
    Ok((root, name))
}

fn ensure_no_reparse_tree(root: &Path) -> Result<()> {
    for entry in fs::read_dir(root)? {
        let entry = entry?;
        let metadata = fs::symlink_metadata(entry.path())?;
        if metadata.file_attributes() & 0x400 != 0 {
            anyhow::bail!("reparse point in demo tree; refusing cleanup");
        }
        if metadata.is_dir() {
            ensure_no_reparse_tree(&entry.path())?;
        }
    }
    Ok(())
}

fn run() -> Result<()> {
    let args = Args::parse();
    let (root, name) = checked_root(&args.root)?;
    let key_path = format!("Software\\Contain\\Demo\\{name}");
    if args.cleanup {
        if !root.join(".contain-demo-marker").is_file() {
            anyhow::bail!("demo marker missing; refusing cleanup");
        }
        ensure_no_reparse_tree(&root)?;
        fs::remove_dir_all(&root)?;
        let _ = RegKey::predef(HKEY_CURRENT_USER).delete_subkey_all(&key_path);
        println!(
            "Demo fixture cleaned: {} and HKCU\\{}",
            root.display(),
            key_path
        );
        return Ok(());
    }
    if args.child {
        fs::create_dir_all(root.join("cache"))?;
        fs::write(root.join("cache").join("index.bin"), b"test cache content")?;
        fs::create_dir_all(root.join("Projects"))?;
        fs::write(
            root.join("Projects").join("user-notes.txt"),
            b"sample user data: preserve",
        )?;
        let (key, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(&key_path)?;
        key.set_value("ChildObserved", &"yes")?;
        thread::sleep(Duration::from_millis(400));
        return Ok(());
    }
    fs::write(root.join(".contain-demo-marker"), b"Contain test fixture")?;
    fs::write(root.join("app.bin"), b"test application payload")?;
    fs::create_dir_all(root.join("Startup"))?;
    fs::write(
        root.join("Startup").join("simulated-entry.txt"),
        b"demo startup artifact, not a real autostart entry",
    )?;
    let (key, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(&key_path)?;
    key.set_value("Installed", &"yes")?;
    let status = Command::new(std::env::current_exe()?)
        .arg("--root")
        .arg(&root)
        .arg("--child")
        .spawn()?
        .wait()?;
    if !status.success() {
        anyhow::bail!("fixture child failed: {status}");
    }
    println!(
        "Test fixture wrote {} and HKCU\\{}",
        root.display(),
        key_path
    );
    Ok(())
}
