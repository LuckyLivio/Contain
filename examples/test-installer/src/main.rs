mod truth;
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

#[derive(Clone, Copy, Debug, ValueEnum)]
enum Role {
    Parent,
    Child,
    Grandchild,
    Unrelated,
    Detached,
    Short,
    Stress,
    Worker,
}

#[derive(Parser)]
struct Args {
    #[arg(long)]
    root: PathBuf,
    #[arg(long, value_enum, default_value = "parent")]
    role: Role,
    #[arg(long)]
    cleanup: bool,
    #[arg(long, default_value_t = 10000)]
    files: usize,
    #[arg(long, default_value_t = 0)]
    worker: usize,
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
    let truth = truth::Truth::new(&root, &format!("{:?}", args.role).to_lowercase())?;
    let (key, _) = RegKey::predef(HKEY_CURRENT_USER).create_subkey(&key_path)?;
    if matches!(args.role, Role::Unrelated) {
        let deadline = Instant::now() + Duration::from_secs(60);
        while !root.join(".fixture-go").is_file() {
            if Instant::now() > deadline {
                anyhow::bail!("fixture start signal timed out");
            }
            thread::sleep(Duration::from_millis(20));
        }
        thread::sleep(Duration::from_millis(200));
        truth.write(
            &root.join("unrelated.txt"),
            b"independent process: never attribute to installer",
        )?;
        truth.write(
            &root.join("shared.txt"),
            b"independent process also touched this file",
        )?;
        truth.set(&key, &key_path, "NoiseOnly", "independent registry writer")?;
        hold_for_observation();
        return Ok(());
    }
    match args.role {
        Role::Parent => {
            let status = Command::new(std::env::current_exe()?)
                .args(["--role", "short", "--root"])
                .arg(&root)
                .status()?;
            anyhow::ensure!(status.success(), "short child failed");
            use std::os::windows::fs::OpenOptionsExt;
            truth.write(&root.join("locked.txt"), b"must survive failed deletion")?;
            let lock = fs::OpenOptions::new()
                .read(true)
                .share_mode(0)
                .open(root.join("locked.txt"))?;
            anyhow::ensure!(
                !truth.record(
                    "delete_requested",
                    &root.join("locked.txt").to_string_lossy(),
                    None,
                    || fs::remove_file(root.join("locked.txt"))
                )?,
                "locked deletion unexpectedly succeeded"
            );
            drop(lock);

            fs::write(root.join(".contain-demo-marker"), b"Contain test fixture")?;
            truth.write(&root.join("app.bin"), b"test application payload")?;
            truth.write(&root.join("settings.txt"), b"updated by installer")?;
            truth.write(&root.join("shared.txt"), b"installer first write")?;
            if root.join("rename-me.txt").exists() {
                truth.rename(&root.join("rename-me.txt"), &root.join("intermediate.txt"))?;
                truth.rename(&root.join("intermediate.txt"), &root.join("renamed.txt"))?;
            }
            if root.join("delete-me.txt").exists() {
                truth.delete(&root.join("delete-me.txt"))?;
            }
            truth.set(&key, &key_path, "Installed", "initial")?;
            fs::write(root.join(".fixture-go"), b"start independent writer")?;
            let status = Command::new(std::env::current_exe()?)
                .args(["--role", "child", "--root"])
                .arg(&root)
                .spawn()?
                .wait()?;
            if !status.success() {
                anyhow::bail!("child failed: {status}");
            }
            // Preserve original creation ancestry while this child outlives its launcher.
            #[allow(clippy::zombie_processes)]
            let _detached = Command::new(std::env::current_exe()?)
                .args(["--role", "detached", "--root"])
                .arg(&root)
                .spawn()?;
            return Ok(());
        }
        Role::Child => {
            fs::create_dir_all(root.join("cache"))?;
            truth.write(&root.join("cache/index.bin"), b"child cache payload")?;
            truth.set(&key, &key_path, "Installed", "updated by child")?;
            truth.set(&key, &key_path, "ChildObserved", "yes")?;
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
            truth.write(
                &root.join("Projects/user-notes.txt"),
                b"sample user data: preserve",
            )?;
            truth.write(&root.join("transient.txt"), b"transient payload")?;
            truth.delete(&root.join("transient.txt"))?;
            truth.set(&key, &key_path, "GrandchildObserved", "yes")?;
            let (temporary_key, _) = key.create_subkey("TransientKey")?;
            temporary_key.set_value("TransientValue", &"present")?;
            temporary_key.delete_value("TransientValue")?;
            drop(temporary_key);
            key.delete_subkey("TransientKey")?;
        }
        Role::Detached => {
            thread::sleep(Duration::from_millis(2500));
            truth.write(
                &root.join("detached.txt"),
                b"child write after installer exit",
            )?;
            fs::write(root.join(".detached-done"), b"complete")?;
            return Ok(());
        }
        Role::Short => {
            truth.write(&root.join("short.txt"), b"short-lived writer")?;
            return Ok(());
        }
        Role::Stress => {
            anyhow::ensure!(
                (4..=100000).contains(&args.files),
                "stress count must be 4..100000"
            );
            fs::write(root.join(".contain-demo-marker"), b"Contain test fixture")?;
            let mut children = Vec::new();
            for worker in 0..4 {
                let count = args.files / 4 + usize::from(worker < args.files % 4);
                children.push(
                    Command::new(std::env::current_exe()?)
                        .args(["--role", "worker", "--root"])
                        .arg(&root)
                        .args([
                            "--files",
                            &count.to_string(),
                            "--worker",
                            &worker.to_string(),
                        ])
                        .spawn()?,
                );
            }
            for mut child in children {
                anyhow::ensure!(child.wait()?.success(), "stress worker failed");
            }
            return Ok(());
        }
        Role::Worker => {
            let dir = root.join(format!("worker-{}", args.worker));
            fs::create_dir_all(&dir)?;
            for i in 0..args.files {
                let original = dir.join(format!("{i}.tmp"));
                let renamed = dir.join(format!("{i}.dat"));
                truth.write(&original, b"synthetic payload")?;
                truth.rename(&original, &renamed)?;
                if i % 2 == 0 {
                    truth.delete(&renamed)?;
                }
            }
            return Ok(());
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
