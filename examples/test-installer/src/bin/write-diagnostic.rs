//! A fixed, independent eight-file diagnostic; never a cleanup utility.
use anyhow::{Context, Result, ensure};
use clap::Parser;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{self, Write},
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::{Component, Path, PathBuf},
};
use windows_sys::Win32::{
    Foundation::FILETIME,
    Storage::FileSystem::{
        FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_READ_ATTRIBUTES,
        FILE_SHARE_READ, FILE_SHARE_WRITE,
    },
    System::{
        Performance::{QueryPerformanceCounter, QueryPerformanceFrequency},
        SystemInformation::GetSystemTimePreciseAsFileTime,
        Threading::{
            GetCurrentProcess, GetCurrentThread, GetCurrentThreadId, GetProcessTimes,
            GetThreadTimes,
        },
    },
};

// Reuse the fixture's independent native observer, not collector evidence.
#[path = "../truth/provenance.rs"]
mod provenance;

const PREFIX: &str = "contain-write-diagnostic-";
const MARKER: &str = ".contain-write-diagnostic-marker";
const MARKER_BYTES: &[u8] = b"Contain write diagnostic container v1";
const FILES_PER_CASE: usize = 2;

#[derive(Parser)]
struct Args {
    /// New marked direct TEMP child. It must contain only the marker file.
    #[arg(long)]
    container: PathBuf,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Case {
    A,
    B,
    C,
    D,
}

impl Case {
    fn name(self) -> &'static str {
        match self {
            Self::A => "A",
            Self::B => "B",
            Self::C => "C",
            Self::D => "D",
        }
    }

    fn actions(self) -> &'static [&'static str] {
        match self {
            Self::A => &["write"],
            Self::B => &["write", "rename"],
            Self::C => &["write", "delete"],
            Self::D => &["write", "rename", "delete"],
        }
    }

    fn renamed(self) -> bool {
        matches!(self, Self::B | Self::D)
    }

    fn deleted(self) -> bool {
        matches!(self, Self::C | Self::D)
    }
}

fn cases() -> impl Iterator<Item = (Case, usize)> {
    [Case::A, Case::B, Case::C, Case::D]
        .into_iter()
        .flat_map(|case| (0..FILES_PER_CASE).map(move |index| (case, index)))
}

fn qpc() -> Result<u64> {
    let mut value = 0_i64;
    // SAFETY: the API receives a valid writable LARGE_INTEGER.
    ensure!(
        unsafe { QueryPerformanceCounter(&mut value) } != 0 && value >= 0,
        "independent QPC sample unavailable"
    );
    Ok(value as u64)
}

fn filetime(value: FILETIME) -> u64 {
    (u64::from(value.dwHighDateTime) << 32) | u64::from(value.dwLowDateTime)
}

fn calibration() -> Result<Value> {
    let before = qpc()?;
    let mut wall = FILETIME::default();
    // SAFETY: wall is a valid writable FILETIME.
    unsafe { GetSystemTimePreciseAsFileTime(&mut wall) };
    let after = qpc()?;
    ensure!(after >= before, "independent QPC calibration regressed");
    Ok(
        json!({"qpc_before":before.to_string(), "qpc_after":after.to_string(),
        "filetime_ticks":filetime(wall).to_string()}),
    )
}

fn actor() -> Result<Value> {
    let before = qpc()?;
    let mut process_created = FILETIME::default();
    let mut thread_created = FILETIME::default();
    let mut exit = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: current-process pseudo handle and all output pointers are valid.
    if unsafe {
        GetProcessTimes(
            GetCurrentProcess(),
            &mut process_created,
            &mut exit,
            &mut kernel,
            &mut user,
        )
    } == 0
    {
        return Err(io::Error::last_os_error().into());
    }
    // SAFETY: current-thread pseudo handle and all output pointers are valid.
    if unsafe {
        GetThreadTimes(
            GetCurrentThread(),
            &mut thread_created,
            &mut exit,
            &mut kernel,
            &mut user,
        )
    } == 0
    {
        return Err(io::Error::last_os_error().into());
    }
    // SAFETY: GetCurrentThreadId has no preconditions.
    let tid = unsafe { GetCurrentThreadId() };
    Ok(json!({"pid":std::process::id(),"thread_id":tid,
        "creation_time":filetime(process_created).to_string(),
        "thread_creation_time":filetime(thread_created).to_string(),
        "identity_source":"GetProcessTimes_current_process_and_GetThreadTimes_current_thread",
        "query_qpc_before":before.to_string(),"query_qpc_after":qpc()?.to_string()}))
}

fn name_is_valid(name: &str) -> bool {
    name.strip_prefix(PREFIX)
        .is_some_and(|suffix| suffix.len() == 32 && suffix.bytes().all(|b| b.is_ascii_hexdigit()))
}

fn directory_lease(path: &Path) -> Result<File> {
    let handle = OpenOptions::new()
        .access_mode(FILE_READ_ATTRIBUTES)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)?;
    let metadata = handle.metadata()?;
    ensure!(metadata.is_dir(), "fixture directory required");
    ensure!(
        metadata.file_attributes() & 0x400 == 0,
        "fixture directory is a reparse point"
    );
    Ok(handle)
}

fn validate_container(path: &Path, temp: &Path) -> Result<PathBuf> {
    ensure!(path.is_absolute(), "container must be absolute");
    ensure!(
        !path.components().any(|c| matches!(c, Component::ParentDir)),
        "container must not contain parent traversal"
    );
    let metadata = fs::symlink_metadata(path)?;
    ensure!(
        metadata.is_dir() && metadata.file_attributes() & 0x400 == 0,
        "container must be a plain directory"
    );
    let container = path.canonicalize()?;
    let name = container.file_name().and_then(|v| v.to_str()).unwrap_or("");
    ensure!(
        container.parent() == Some(temp) && name_is_valid(name),
        "container must be a direct TEMP child named contain-write-diagnostic-<32 hex digits>"
    );
    let mut entries = fs::read_dir(&container)?.collect::<io::Result<Vec<_>>>()?;
    ensure!(
        entries.len() == 1
            && entries
                .pop()
                .is_some_and(|entry| entry.file_name() == MARKER),
        "container must contain only the diagnostic marker; existing payloads are never reused"
    );
    let marker = container.join(MARKER);
    let metadata = fs::symlink_metadata(&marker)?;
    ensure!(
        metadata.is_file() && metadata.file_attributes() & 0x400 == 0,
        "marker must be a plain file"
    );
    ensure!(fs::read(marker)? == MARKER_BYTES, "marker content mismatch");
    Ok(container)
}

struct Fixture {
    root: PathBuf,
    truth: PathBuf,
    _leases: Vec<File>,
}

impl Fixture {
    fn prepare(container: &Path) -> Result<Self> {
        let temp = std::env::temp_dir().canonicalize()?;
        let temp_lease = directory_lease(&temp)?;
        let container = validate_container(container, &temp)?;
        let container_lease = directory_lease(&container)?;
        // Recheck the marker and empty-container condition while the lease is held.
        validate_container(&container, &temp)?;
        let name = container.file_name().context("container has no name")?;
        let truth = temp.join(format!("{}-truth", name.to_string_lossy()));
        let root = container.join("payload");
        // Both calls are atomic create-directory operations; never create_dir_all.
        // A failure leaves new diagnostic artifacts intact and never cleans up.
        fs::create_dir(&truth).context("truth directory must not already exist")?;
        let truth_lease = directory_lease(&truth)?;
        fs::create_dir(&root).context("payload directory must not already exist")?;
        let root_lease = directory_lease(&root)?;
        Ok(Self {
            root,
            truth,
            _leases: vec![temp_lease, container_lease, truth_lease, root_lease],
        })
    }

    fn checked_leaf(&self, name: &str, exists: bool) -> Result<PathBuf> {
        ensure!(
            Path::new(name).components().count() == 1
                && matches!(
                    Path::new(name).components().next(),
                    Some(Component::Normal(_))
                ),
            "diagnostic filename must be a single normal component"
        );
        let path = self.root.join(name);
        match fs::symlink_metadata(&path) {
            Ok(metadata) => ensure!(
                exists && metadata.is_file() && metadata.file_attributes() & 0x400 == 0,
                "refusing unexpected or reparse diagnostic file"
            ),
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                ensure!(!exists, "expected diagnostic file is missing");
            }
            Err(error) => return Err(error.into()),
        }
        Ok(path)
    }
}

struct Journal {
    file: File,
    order: u64,
    frequency: u64,
    actor: Value,
    provenance: Value,
}

impl Journal {
    fn new(path: &Path) -> Result<Self> {
        let mut frequency = 0_i64;
        // SAFETY: the API receives a valid writable LARGE_INTEGER.
        ensure!(
            unsafe { QueryPerformanceFrequency(&mut frequency) } != 0 && frequency > 0,
            "independent QPC frequency unavailable"
        );
        Ok(Self {
            file: OpenOptions::new().write(true).create_new(true).open(path)?,
            order: 0,
            frequency: frequency as u64,
            actor: actor()?,
            provenance: provenance::observe(),
        })
    }

    fn append(&mut self, value: &Value) -> Result<()> {
        let mut bytes = serde_json::to_vec(value)?;
        bytes.push(b'\n');
        self.file.write_all(&bytes)?;
        Ok(())
    }

    fn call(&mut self, mut detail: Value, run: impl FnOnce() -> io::Result<()>) -> Result<bool> {
        let before = calibration()?;
        let start = qpc()?;
        let result = run();
        let end = qpc()?;
        let after = calibration()?;
        ensure!(end >= start, "independent operation QPC regressed");
        self.order += 1;
        detail["schema_version"] = json!(1);
        detail["kind"] = json!("write_diagnostic_api_call");
        detail["phase"] = json!("workload");
        detail["order"] = json!(self.order);
        detail["actor"] = self.actor.clone();
        detail["pid"] = self.actor["pid"].clone();
        detail["creation_time"] = self.actor["creation_time"].clone();
        detail["success"] = json!(result.is_ok());
        detail["api_return"] = json!(if result.is_ok() { "Ok(())" } else { "Err" });
        detail["error"] = json!(result.as_ref().err().and_then(|e| e.raw_os_error()));
        detail["error_kind"] = json!(result.as_ref().err().map(|e| format!("{:?}", e.kind())));
        detail["start_ticks"] = before["filetime_ticks"].clone();
        detail["end_ticks"] = after["filetime_ticks"].clone();
        detail["filetime_boundary_scope"] =
            json!("outer precise FILETIME calibration brackets, not exact syscall timestamps");
        detail["clock"] = json!({"schema_version":1,"qpc_frequency":self.frequency.to_string(),
            "sequence":self.order,"thread_id":self.actor["thread_id"],
            "start_qpc":start.to_string(),"end_qpc":end.to_string(),"before":before,"after":after,
            "provenance":self.provenance});
        // Log serialization and I/O happen only after the API and clock bounds.
        self.append(&detail)?;
        Ok(result.is_ok())
    }
}

fn payload(case: Case, index: usize) -> Vec<u8> {
    format!(
        "Contain write diagnostic v1;case={};file={index}\n",
        case.name()
    )
    .into_bytes()
}

fn details(
    case: Case,
    index: usize,
    action: &str,
    resource: &Path,
    destination: Option<&Path>,
) -> Value {
    let api = match action {
        "write" => "std::fs::write",
        "rename" => "std::fs::rename",
        "delete" => "std::fs::remove_file",
        _ => unreachable!("fixed diagnostic actions only"),
    };
    json!({"case":case.name(),"file_index":index,"action":action,
        "operation":format!("{action}_requested"),"api":api,"resource":resource,
        "destination":destination,"requested_io_bytes":null,"written_bytes_observable":false,
        "close_result":null,"close_boundary":null})
}

fn run_case(fixture: &Fixture, journal: &mut Journal, case: Case, index: usize) -> Result<bool> {
    let original_name = format!("{}-{index}.tmp", case.name());
    let final_name = format!("{}-{index}.dat", case.name());
    let mut current = fixture.checked_leaf(&original_name, false)?;
    let bytes = payload(case, index);
    for action in case.actions() {
        let success = match *action {
            "write" => {
                let mut row = details(case, index, action, &current, None);
                row["requested_io_bytes"] = json!(bytes.len());
                row["payload_utf8"] = json!(String::from_utf8_lossy(&bytes));
                row["payload_sha256"] = json!(format!("{:x}", Sha256::digest(&bytes)));
                row["close_boundary"] = json!(
                    "implicit File Drop inside std::fs::write; close time and result not observable"
                );
                journal.call(row, || fs::write(&current, &bytes))?
            }
            "rename" => {
                fixture.checked_leaf(&original_name, true)?;
                let destination = fixture.checked_leaf(&final_name, false)?;
                let success = journal.call(
                    details(case, index, action, &current, Some(&destination)),
                    || fs::rename(&current, &destination),
                )?;
                if success {
                    current = destination;
                }
                success
            }
            "delete" => {
                let name = if case.renamed() {
                    &final_name
                } else {
                    &original_name
                };
                fixture.checked_leaf(name, true)?;
                journal.call(details(case, index, action, &current, None), || {
                    fs::remove_file(&current)
                })?
            }
            _ => unreachable!("fixed diagnostic actions only"),
        };
        if !success {
            journal.append(
                &json!({"schema_version":1,"kind":"write_diagnostic_sequence_stopped",
                "case":case.name(),"file_index":index,"after_action":action,
                "reason":"failed API; dependent actions were not attempted"}),
            )?;
            return Ok(false);
        }
    }
    Ok(true)
}

fn final_state(fixture: &Fixture, case: Case, index: usize) -> Value {
    let bytes = payload(case, index);
    let mut states = Vec::new();
    for (extension, expected_present) in [
        ("tmp", !case.renamed() && !case.deleted()),
        ("dat", case.renamed() && !case.deleted()),
    ] {
        let path = fixture
            .root
            .join(format!("{}-{index}.{extension}", case.name()));
        let metadata = fs::symlink_metadata(&path);
        let value = match metadata {
            Err(error) if error.kind() == io::ErrorKind::NotFound => json!({"path":path,
                "expected_present":expected_present,"present":false,"matches_expected":!expected_present,
                "read_error":null,"content_sha256":null}),
            Err(error) => json!({"path":path,"expected_present":expected_present,"present":null,
                "matches_expected":false,"read_error":error.raw_os_error(),"error_kind":format!("{:?}",error.kind())}),
            Ok(metadata) if metadata.file_attributes() & 0x400 != 0 || !metadata.is_file() => {
                json!({"path":path,
                "expected_present":expected_present,"present":true,"matches_expected":false,"read_error":"unexpected nonplain file; content not followed"})
            }
            Ok(_) => match fs::read(&path) {
                Ok(observed) => {
                    json!({"path":path,"expected_present":expected_present,"present":true,
                    "content_utf8":String::from_utf8_lossy(&observed),"content_sha256":format!("{:x}",Sha256::digest(&observed)),
                    "read_error":null,"matches_expected":expected_present && observed == bytes})
                }
                Err(error) => {
                    json!({"path":path,"expected_present":expected_present,"present":true,
                    "matches_expected":false,"read_error":error.raw_os_error(),"error_kind":format!("{:?}",error.kind())})
                }
            },
        };
        states.push(value);
    }
    json!({"case":case.name(),"file_index":index,"paths":states})
}

fn run(args: Args) -> Result<()> {
    let fixture = Fixture::prepare(&args.container)?;
    let mut journal = Journal::new(&fixture.truth.join("operations.jsonl"))?;
    journal.append(&json!({"schema_version":1,"kind":"write_diagnostic_session",
        "root":fixture.root,"truth":fixture.truth,"workers":1,"files_per_case":FILES_PER_CASE,
        "expected_api_calls":16,"actor":journal.actor,"provenance":journal.provenance,
        "close_observation":"std::fs::write includes implicit File Drop; close result and exact boundary unavailable",
        "scope":"fixed independent diagnostic; no scoring or cleanup; final reads occur after all workload calls"}))?;
    let mut succeeded = 0;
    let mut failures = Vec::new();
    for (case, index) in cases() {
        match run_case(&fixture, &mut journal, case, index) {
            Ok(true) => succeeded += 1,
            Ok(false) => failures.push(json!({"case":case.name(),"file_index":index,"reason":"API failed; see operation row"})),
            Err(error) => {
                let failure = json!({"case":case.name(),"file_index":index,"reason":format!("{error:#}")});
                journal.append(&json!({"schema_version":1,"kind":"write_diagnostic_sequence_error","error":failure}))?;
                failures.push(failure);
            }
        }
    }
    let states: Vec<_> = cases()
        .map(|(case, index)| final_state(&fixture, case, index))
        .collect();
    let matches = states.iter().all(|s| {
        s["paths"]
            .as_array()
            .is_some_and(|paths| paths.iter().all(|p| p["matches_expected"] == true))
    });
    let summary = json!({"schema_version":1,"kind":"write_diagnostic_summary","root":fixture.root,
        "truth":fixture.truth,"planned_files":8,"files_per_case":FILES_PER_CASE,"workers":1,
        "expected_api_calls":16,"recorded_api_calls":journal.order,"successful_sequences":succeeded,
        "failures":failures,"final_checks":states,"final_checks_match":matches,
        "passed":succeeded==8 && matches,"real_etw_verified":null,
        "safety_scope":"Only newly created fixed payload paths are mutated. Directory leases prevent directory replacement; this is not an adversarial filesystem isolation boundary.",
        "close_result":null});
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(fixture.truth.join("summary.json"))?;
    file.write_all(&serde_json::to_vec_pretty(&summary)?)?;
    println!("Diagnostic root: {}", fixture.root.display());
    println!("Diagnostic truth: {}", fixture.truth.display());
    println!("Successful sequences: {succeeded}/8; final state matches: {matches}");
    ensure!(
        succeeded == 8 && matches,
        "diagnostic failed; all artifacts retained"
    );
    Ok(())
}

fn main() {
    if let Err(error) = run(Args::parse()) {
        eprintln!("write diagnostic: {error:#}");
        std::process::exit(1);
    }
}

#[cfg(test)]
#[path = "write_diagnostic/tests.rs"]
mod tests;
