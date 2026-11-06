//! Controlled follow-installer experiment. Targets belong to the fixture oracle,
//! never to Contain's observation configuration.
use crate::{Role, truth::Truth};
use anyhow::{Context, Result};
use std::{
    fs,
    path::Path,
    process::Command,
    thread,
    time::{Duration, Instant},
};

fn wait_pair_marker(root: &Path, name: &str) -> Result<()> {
    if !std::env::var_os("CONTAIN_FIXTURE_BACKGROUND_WAIT").is_some_and(|value| value == "1") {
        return Ok(());
    }
    let deadline = Instant::now() + Duration::from_secs(45);
    let marker = root.join(name);
    while !marker.is_file() {
        anyhow::ensure!(
            Instant::now() < deadline,
            "bounded pair background timed out"
        );
        thread::sleep(Duration::from_millis(10));
    }
    anyhow::ensure!(
        fs::read(marker)? == b"background complete",
        "pair marker mismatch"
    );
    Ok(())
}

pub(crate) fn run(role: Role, root: &Path) -> Result<bool> {
    if !matches!(
        role,
        Role::FollowParent | Role::FollowChild | Role::FollowUnrelated
    ) {
        return Ok(false);
    }
    anyhow::ensure!(
        fs::read(root.join(".contain-demo-marker"))? == b"Contain test fixture",
        "follow fixture marker mismatch"
    );
    let a = root.join("A");
    anyhow::ensure!(
        a.is_dir() && fs::symlink_metadata(&a)?.file_attributes() & 0x400 == 0,
        "follow fixture A must be an existing non-reparse directory"
    );
    let truth = Truth::new(root, &format!("{role:?}").to_lowercase())?;
    match role {
        Role::FollowParent => {
            anyhow::ensure!(
                !root.join("B").exists(),
                "follow fixture B must not exist before the child runs"
            );
            fs::write(
                root.join(".follow-go"),
                b"follow independent writer may run",
            )?;
            wait_pair_marker(root, ".pair-background-started")?;
            truth.write(
                &a.join("parent-target.txt"),
                b"Contain follow experiment: exact parent write\n",
            )?;
            let exit = Command::new(std::env::current_exe()?)
                .args(["--role", "follow-child", "--root"])
                .arg(root)
                .spawn()
                .context("launch direct follow fixture child")?
                .wait()?;
            anyhow::ensure!(exit.success(), "direct follow fixture child failed: {exit}");
            wait_pair_marker(root, ".pair-background-finished")?;
            thread::sleep(Duration::from_millis(1200));
        }
        Role::FollowChild => {
            let b = root.join("B");
            anyhow::ensure!(!b.exists(), "follow child B unexpectedly exists");
            thread::sleep(Duration::from_millis(400));
            anyhow::ensure!(
                truth.record(
                    "directory_create_requested",
                    &b.to_string_lossy(),
                    None,
                    || fs::create_dir(&b)
                )?,
                "create follow child directory failed"
            );
            truth.write(
                &b.join("child-target.txt"),
                b"Contain follow experiment: exact direct child write\n",
            )?;
            thread::sleep(Duration::from_millis(1800));
        }
        Role::FollowUnrelated => {
            fs::write(
                root.join(".follow-unrelated-ready"),
                b"independent process ready",
            )?;
            let deadline = Instant::now() + Duration::from_secs(45);
            while !root.join(".follow-go").exists() {
                anyhow::ensure!(
                    Instant::now() < deadline,
                    "follow independent writer start timed out"
                );
                thread::sleep(Duration::from_millis(10));
            }
            thread::sleep(Duration::from_millis(150));
            truth.write(
                &a.join("unrelated-target.txt"),
                b"Contain follow experiment: independent writer, never installer\n",
            )?;
            thread::sleep(Duration::from_millis(1800));
        }
        _ => unreachable!(),
    }
    Ok(true)
}

use std::os::windows::fs::MetadataExt;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn follow_roles_refuse_unmarked_directory_before_truth_or_target_writes() {
        let root = std::env::temp_dir().join(format!(
            "contain-demo-follow-guard-{}-{}",
            std::process::id(),
            crate::truth::ticks()
        ));
        fs::create_dir(&root).unwrap();
        fs::create_dir(root.join("A")).unwrap();
        for role in [Role::FollowParent, Role::FollowChild, Role::FollowUnrelated] {
            assert!(run(role, &root).is_err());
        }
        assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
        assert_eq!(fs::read_dir(root.join("A")).unwrap().count(), 0);
        fs::remove_dir(root.join("A")).unwrap();
        fs::remove_dir(root).unwrap();
    }
}
