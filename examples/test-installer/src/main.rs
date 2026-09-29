use anyhow::{Context, Result};
use clap::{Parser, ValueEnum};
use std::{
    fs,
    os::windows::fs::MetadataExt,
    path::{Path, PathBuf},
    process::Command,
    thread,
    time::{Duration, Instant},
};
use winreg::{RegKey, enums::HKEY_CURRENT_USER};

#[derive(Clone, Copy, ValueEnum)]
enum Role {
    Parent,
    Child,
    Grandchild,
    Unrelated,
    Detached,
}

#[derive(Parser)]
struct Args {
    #[arg(long)]
    root: PathBuf,
    #[arg(long, value_enum, default_value = "parent")]
    role: Role,
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
        anyhow::bail!("demo root is a reparse point");
    }
    let root = path.canonicalize()?;
    let temp = std::env::temp_dir().canonicalize()?;
    let name = root
        .file_name()
        .context("demo root has no name")?
        .to_string_lossy()
        .into_owned();
    if root.parent() != Some(temp.as_path()) || !name.starts_with("contain-demo-") {
        anyhow::bail!("demo root must be a direct TEMP child named contain-demo-*");
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

fn hold_for_observation() {
    thread::sleep(Duration::from_millis(1800));
}

fn run() -> Result<()> {
    let args = Args::parse();
    let (root, name) = checked_root(&args.root)?;
    let key_path = format!("Software\\Contain\\Demo\\{name}");
    if args.cleanup {
        if fs::read(root.join(".contain-demo-marker"))? != b"Contain test fixture" {
            anyhow::bail!("demo marker mismatch");
        }
        ensure_no_reparse_tree(&root)?;
        match RegKey::predef(HKEY_CURRENT_USER).delete_subkey_all(&key_path) {
            Ok(()) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => return Err(error.into()),
        }
        fs::remove_dir_all(&root)?;
        println!("Fixture cleaned: {} and HKCU\\{}", root.display(), key_path);
        return Ok(());
    }
    if matches!(args.role, Role::Unrelated) {
        let deadline = Instant::now() + Duration::from_secs(60);
        while !root.join(".fixture-go").is_file() {
            if Instant::now() > deadline {
                anyhow::bail!("fixture start signal timed out");
            }
            thread::sleep(Duration::from_millis(20));
        }
        thread::sleep(Duration::from_millis(200));
        fs::write(
            root.join("unrelated.txt"),
            b"independent process: never attribute to installer",
        )?;
        fs::write(
            root.join("shared.txt"),
            b"independent process also touched this file",
        )?;
        hold_for_observation();
        return Ok(());
    }
    let (key, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(&key_path)?;
    match args.role {
        Role::Parent => {
            fs::write(root.join(".contain-demo-marker"), b"Contain test fixture")?;
            fs::write(root.join("app.bin"), b"test application payload")?;
            fs::write(root.join("settings.txt"), b"updated by installer")?;
            fs::write(root.join("shared.txt"), b"installer first write")?;
            if root.join("rename-me.txt").exists() {
                fs::rename(root.join("rename-me.txt"), root.join("renamed.txt"))?;
            }
            if root.join("delete-me.txt").exists() {
                fs::remove_file(root.join("delete-me.txt"))?;
            }
            key.set_value("Installed", &"initial")?;
            fs::write(root.join(".fixture-go"), b"start independent writer")?;
            let status = Command::new(std::env::current_exe()?)
                .args(["--role", "child", "--root"])
                .arg(&root)
                .spawn()?
                .wait()?;
            if !status.success() {
                anyhow::bail!("child failed: {status}");
            }
        }
        Role::Child => {
            fs::create_dir_all(root.join("cache"))?;
            fs::write(root.join("cache/index.bin"), b"child cache payload")?;
            key.set_value("Installed", &"updated by child")?;
            key.set_value("ChildObserved", &"yes")?;
            let status = Command::new(std::env::current_exe()?)
                .args(["--role", "grandchild", "--root"])
                .arg(&root)
                .spawn()?
                .wait()?;
            if !status.success() {
                anyhow::bail!("grandchild failed: {status}");
            }
        }
        Role::Grandchild => {
            fs::create_dir_all(root.join("Projects"))?;
            fs::write(
                root.join("Projects/user-notes.txt"),
                b"sample user data: preserve",
            )?;
            fs::write(root.join("transient.txt"), b"transient payload")?;
            fs::remove_file(root.join("transient.txt"))?;
            key.set_value("GrandchildObserved", &"yes")?;
            let (temporary_key, _) = key.create_subkey("TransientKey")?;
            temporary_key.set_value("TransientValue", &"present")?;
            temporary_key.delete_value("TransientValue")?;
            drop(temporary_key);
            key.delete_subkey("TransientKey")?;
        }
        Role::Detached => {
            fs::write(
                root.join("detached.txt"),
                b"detached writer has no assumed ancestry",
            )?;
        }
        Role::Unrelated => unreachable!(),
    }
    hold_for_observation();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fixture_rejects_outside_temp_and_traversal() {
        assert!(checked_root(&std::env::current_dir().unwrap()).is_err());
        assert!(checked_root(&std::env::temp_dir().join("..")).is_err());
    }
}
