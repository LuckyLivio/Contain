//! Direct final-read API experiment, not a live ETW or whole-CLI benchmark.
//! Build the same harness against the baseline and candidate source trees.
#![allow(unexpected_cfgs)]
use anyhow::{Context, Result, bail, ensure};
use contain_core::filesystem::{self, Snapshot};
use serde_json::{Value, json};
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::Instant;

const CASES: &[&str] = &[
    "read_open",
    "write_open",
    "overwrite",
    "truncate_open",
    "rename_file",
    "replace_file",
    "rename_parent",
    "preexisting_writer",
];
const ORIGINAL: &[u8] = b"original content";
const REPLACEMENT: &[u8] = b"replacement content";

enum Reader {
    None,
    Shared(Snapshot),
    #[cfg(contain_candidate_observer)]
    Leased(filesystem::ObservedSnapshot),
}

impl Reader {
    fn begin(mode: &str, root: &Path) -> Result<Self> {
        match mode {
            "none" => Ok(Self::None),
            "shared" => Ok(Self::Shared(filesystem::snapshot(&[root.to_path_buf()])?)),
            #[cfg(contain_candidate_observer)]
            "leased" => Ok(Self::Leased(filesystem::observed_snapshot(&[
                root.to_path_buf()
            ])?)),
            _ => bail!("observer mode unsupported by this build: {mode}"),
        }
    }
    fn finish(self, root: &Path) -> Value {
        let (snapshot, reads) = match self {
            Self::None => return Value::Null,
            Self::Shared(snapshot) => (snapshot, Value::Null),
            #[cfg(contain_candidate_observer)]
            Self::Leased(observed) => {
                // No trace ran: never manufacture complete source coverage.
                let snapshot = observed.finish(&Snapshot::default(), false);
                let reads: Vec<_> = snapshot.observations.iter().map(|(path, read)| json!({
                    "resource": Path::new(path).strip_prefix(root).unwrap().to_string_lossy(),
                    "method": read.method, "content_stable": read.content_stable,
                    "path_pinned": read.path_pinned, "write_delete_excluded": read.write_delete_excluded,
                    "retained_handles": read.retained_handles, "exclusion_duration_ns": read.exclusion_duration_ns,
                    "final_source_complete": read.final_source_complete,
                    "mutation_coverage_complete": read.mutation_coverage_complete, "failure": read.failure,
                })).collect();
                (snapshot, json!(reads))
            }
        };
        let files: Vec<_> = snapshot
            .files
            .iter()
            .map(|(path, state)| {
                json!({
                    "resource": Path::new(path).strip_prefix(root).unwrap().to_string_lossy(),
                    "size": state.size, "blake3": state.hash,
                })
            })
            .collect();
        json!({"files": files, "unreadable_count": snapshot.unreadable.len(), "reads": reads})
    }
}

fn prepare(root: &Path) -> Result<()> {
    fs::create_dir_all(root.join("directory"))?;
    fs::write(root.join("directory/target.bin"), ORIGINAL)?;
    fs::write(root.join("directory/replacement.bin"), REPLACEMENT)?;
    Ok(())
}

fn replace(source: &Path, destination: &Path) -> std::io::Result<()> {
    use windows_sys::Win32::Storage::FileSystem::{
        MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    };
    let source: Vec<_> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<_> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    // SAFETY: owned terminated UTF-16 buffers live through this synchronous call.
    if unsafe {
        MoveFileExW(
            source.as_ptr(),
            destination.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    } == 0
    {
        Err(std::io::Error::last_os_error())
    } else {
        Ok(())
    }
}

fn perform(case: &str, root: &Path, existing: Option<&mut File>) -> std::io::Result<()> {
    let target = root.join("directory/target.bin");
    match case {
        "read_open" => File::open(target).map(drop),
        "write_open" => OpenOptions::new().write(true).open(target).map(drop),
        "overwrite" => fs::write(target, b"overwritten content"),
        "truncate_open" => OpenOptions::new()
            .write(true)
            .truncate(true)
            .open(target)
            .map(drop),
        "rename_file" => fs::rename(target, root.join("directory/renamed.bin")),
        "replace_file" => replace(&root.join("directory/replacement.bin"), &target),
        "rename_parent" => fs::rename(root.join("directory"), root.join("renamed-directory")),
        "preexisting_writer" => {
            let file = existing.expect("preexisting writer must open before ready barrier");
            file.set_len(0)?;
            file.write_all(b"existing writer content")
        }
        _ => Err(std::io::Error::new(
            std::io::ErrorKind::InvalidInput,
            "unknown operation",
        )),
    }
}

fn worker(root: &Path, case: &str) -> Result<()> {
    ensure!(CASES.contains(&case), "unknown case");
    let mut existing = if case == "preexisting_writer" {
        Some(
            OpenOptions::new()
                .write(true)
                .open(root.join("directory/target.bin"))?,
        )
    } else {
        None
    };
    println!("ready");
    std::io::stdout().flush()?;
    let mut signal = [0];
    std::io::stdin().read_exact(&mut signal)?;
    ensure!(signal == *b"G", "invalid barrier signal");
    let clock = Instant::now();
    let result = perform(case, root, existing.as_mut());
    println!(
        "{}",
        json!({
            "case": case, "pid": std::process::id(), "success": result.is_ok(),
            "error_code": result.as_ref().err().and_then(|e| e.raw_os_error()),
            "error": result.as_ref().err().map(ToString::to_string),
            "elapsed_ns": clock.elapsed().as_nanos().to_string(), "attempts": 1,
        })
    );
    Ok(())
}

fn state(root: &Path) -> Value {
    let names = [
        "directory/target.bin",
        "directory/renamed.bin",
        "directory/replacement.bin",
        "renamed-directory/target.bin",
        "renamed-directory/replacement.bin",
    ];
    json!(
        names
            .into_iter()
            .map(|name| {
                let result = fs::read(root.join(name));
                json!({"resource": name, "exists": result.is_ok(), "bytes": result.as_ref().ok(),
            "read_error_code": result.as_ref().err().and_then(|e| e.raw_os_error())})
            })
            .collect::<Vec<_>>()
    )
}

fn one(root: &Path, mode: &str, case: &str) -> Result<Value> {
    prepare(root)?;
    let root = root.canonicalize()?;
    let mut child = Command::new(std::env::current_exe()?)
        .arg("worker")
        .arg(&root)
        .arg(case)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()?;
    let mut output = BufReader::new(child.stdout.take().context("worker stdout")?);
    let mut ready = String::new();
    output.read_line(&mut ready)?;
    ensure!(
        ready.trim() == "ready",
        "worker did not reach ready barrier: {ready}"
    );
    let clock = Instant::now();
    let reader = Reader::begin(mode, &root)?;
    let read_ns = clock.elapsed().as_nanos().to_string();
    // Deterministic post-hash retention window: no delay chosen to hit a race.
    child
        .stdin
        .take()
        .context("worker stdin")?
        .write_all(b"G")?;
    let mut operation = String::new();
    output.read_line(&mut operation)?;
    let status = child.wait()?;
    ensure!(
        status.success(),
        "worker failed before reporting an operation"
    );
    let operation: Value = serde_json::from_str(&operation)?;
    let final_state_during_window = state(&root);
    let observation = reader.finish(&root);
    Ok(
        json!({"operation": operation, "final_state": final_state_during_window,
        "observation": observation, "read_api_elapsed_ns": read_ns,
        "barrier": "worker-ready -> read-api-return -> exactly-one-operation -> worker-exit -> release"}),
    )
}

fn run(root: &Path, mode: &str) -> Result<Value> {
    ensure!(!root.exists(), "output fixture directory must not exist");
    fs::create_dir_all(root)?;
    fs::write(
        root.join(".contain-read-interference-marker"),
        b"owned interference fixture v1",
    )?;
    let mut cases = Vec::new();
    for case in CASES {
        let during = one(&root.join(case).join("during"), mode, case)?;
        // A separate identically initialized tree avoids retrying a changed name.
        // The observer above has released every handle before this control.
        let after_release = one(&root.join(case).join("after-release"), "none", case)?;
        cases.push(json!({"case": case, "during": during, "after_release_control": after_release}));
    }
    Ok(json!({"schema": 1, "mode": mode, "etw_started": false,
        "scope": "direct_final_read_api_post_hash_retention_window",
        "not_measured": ["live_ETW", "whole_CLI", "natural_overlap_probability", "source_attribution_accuracy"],
        "cases": cases}))
}

fn main() -> Result<()> {
    let args: Vec<_> = std::env::args_os().collect();
    ensure!(
        args.len() == 4,
        "usage: read_interference <run|worker> <new-root> <mode|case>"
    );
    let root = PathBuf::from(&args[2]);
    let value = args[3].to_str().context("mode/case must be UTF-8")?;
    if args[1] == "worker" {
        worker(&root, value)?;
    } else {
        ensure!(args[1] == "run", "unknown command");
        println!("{}", serde_json::to_string_pretty(&run(&root, value)?)?);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn failed_operation_retains_original_windows_error() {
        let root = std::env::temp_dir().join(format!("contain-missing-{}", uuid::Uuid::new_v4()));
        let result = perform("write_open", &root, None);
        assert!(result.is_err());
        assert!(result.unwrap_err().raw_os_error().is_some());
    }
    #[test]
    fn destructive_replacement_requires_existing_destination_tree() {
        let root = std::env::temp_dir().join(format!("contain-missing-{}", uuid::Uuid::new_v4()));
        assert!(perform("replace_file", &root, None).is_err());
        assert!(!root.exists());
    }
}
