use crate::model::{Confidence, FileChange, StateObservation};
use anyhow::{Context, Result};
use notify::{RecommendedWatcher, RecursiveMode, Watcher};
use std::cell::RefCell;
use std::collections::{BTreeMap, HashSet};
use std::fs::File;
use std::fs::Metadata;
#[cfg(test)]
use std::fs::OpenOptions;
use std::os::windows::fs::MetadataExt;
#[cfg(test)]
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsHandle;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;
use walkdir::WalkDir;

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct FileState {
    pub hash: String,
    pub size: u64,
    pub identity: Option<String>,
}

#[derive(Default)]
pub struct Snapshot {
    pub files: BTreeMap<String, FileState>,
    pub unreadable: HashSet<String>,
    pub observations: BTreeMap<String, StateObservation>,
}

/// Bound for the test-only controlled-read mechanism. Production observations
/// do not acquire exclusive leases while mutation coverage cannot be established.
pub const OBSERVATION_HANDLE_LIMIT: usize = 256;

#[cfg(test)]
#[derive(Default)]
struct ReadLeases {
    directories: BTreeMap<PathBuf, File>,
    files: BTreeMap<String, (File, Instant)>,
}

#[cfg(test)]
impl ReadLeases {
    fn open(&mut self, path: &Path) -> Result<File> {
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
        };
        let mut ancestors: Vec<_> = path.ancestors().skip(1).collect();
        ancestors.reverse();
        let missing: Vec<_> = ancestors
            .into_iter()
            .filter(|p| !self.directories.contains_key(*p))
            .collect();
        anyhow::ensure!(
            self.directories.len() + self.files.len() + missing.len() < OBSERVATION_HANDLE_LIMIT,
            "Controlled observation handle quota (256, including ancestors) exhausted"
        );
        // Pin from the volume root down. Opening a leaf alone would not prevent
        // an ancestor from being renamed and a different tree taking its path.
        let mut new_directories = Vec::new();
        for parent in missing {
            let directory = OpenOptions::new()
                .read(true)
                // Also deny direct directory write opens. Attribute-only access
                // is not a sharing-mode guarantee; this is not namespace isolation.
                .share_mode(FILE_SHARE_READ)
                .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
                .open(parent)?;
            let metadata = directory.metadata()?;
            anyhow::ensure!(
                metadata.is_dir() && !is_reparse(&metadata),
                "Controlled observation refuses reparse or non-directory ancestors"
            );
            new_directories.push((parent.to_path_buf(), directory));
        }
        let file = OpenOptions::new()
            .read(true)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(path)?;
        let metadata = file.metadata()?;
        anyhow::ensure!(
            metadata.is_file() && !is_reparse(&metadata),
            "Controlled observation refuses reparse or non-file resources"
        );
        anyhow::ensure!(
            crate::windows::native::file_identity(file.as_handle()).is_some(),
            "Controlled observation requires a single-link physical file identity"
        );
        self.directories.extend(new_directories);
        Ok(file)
    }
}

pub struct ObservedSnapshot {
    snapshot: Snapshot,
    #[cfg(test)]
    leases: ReadLeases,
}

impl ObservedSnapshot {
    /// Call after ETW stop, receiver drain, and writer finalization to attach the
    /// source outcome. Production reads hold no file or ancestor handles here.
    pub fn finish(mut self, before: &Snapshot, final_source_complete: bool) -> Snapshot {
        #[cfg(test)]
        let retained_handles = self.leases.files.len() + self.leases.directories.len();
        #[cfg(not(test))]
        let retained_handles = 0;
        for (path, observation) in &mut self.snapshot.observations {
            let baseline = before.observations.get(path);
            observation.baseline_stable =
                baseline.is_some_and(|b| b.content_stable && b.path_pinned);
            observation.baseline_identity = before.files.get(path).and_then(|f| f.identity.clone());
            observation.baseline_hash = before.files.get(path).map(|f| f.hash.clone());
            #[cfg(test)]
            {
                observation.held_through_trace_stop = self.leases.files.contains_key(path);
            }
            observation.final_source_complete = final_source_complete;
            // Unknown CreateFile disposition and filtered unresolved file paths
            // prevent a complete mutation account, even with zero reported loss.
            observation.mutation_coverage_complete = false;
            observation.retained_handles = Some(retained_handles);
            #[cfg(test)]
            if let Some((_, started)) = self.leases.files.get(path) {
                observation.exclusion_duration_ns =
                    Some(started.elapsed().as_nanos().min(u64::MAX as u128) as u64);
            }
        }
        self.snapshot
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum ObservationMode {
    Shared,
    #[cfg(test)]
    Baseline,
    #[cfg(test)]
    Retained,
}

const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;

pub fn is_reparse(metadata: &Metadata) -> bool {
    metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

pub fn canonical_roots(roots: &[PathBuf]) -> Result<Vec<PathBuf>> {
    let mut out = Vec::new();
    for root in roots {
        let resolved = root
            .canonicalize()
            .with_context(|| format!("watch root does not exist: {}", root.display()))?;
        if !resolved.is_dir() {
            anyhow::bail!("watch root is not a directory: {}", resolved.display());
        }
        if is_reparse(&std::fs::symlink_metadata(root)?) {
            anyhow::bail!("watch root is a reparse point: {}", root.display());
        }
        if !out.contains(&resolved) {
            out.push(resolved);
        }
    }
    Ok(out)
}

pub fn snapshot(roots: &[PathBuf]) -> Result<Snapshot> {
    Ok(snapshot_inner(roots, ObservationMode::Shared)?.snapshot)
}

/// Run after trace readiness and before installer launch. A shared read preserves
/// the workload's write/delete sharing behavior, but cannot prove stable content.
pub fn baseline_snapshot(roots: &[PathBuf]) -> Result<Snapshot> {
    Ok(observed_snapshot(roots)?.snapshot)
}

/// Read through ordinary shared handles and retain diagnostic hashes/identities,
/// not exclusion leases. Without complete mutation coverage, exclusive reads
/// would interfere with the workload while providing no state attribution proof.
pub fn observed_snapshot(roots: &[PathBuf]) -> Result<ObservedSnapshot> {
    let mut observed = snapshot_inner(roots, ObservationMode::Shared)?;
    for read in observed.snapshot.observations.values_mut() {
        read.failure = Some("Shared read without writer/path exclusion; content and namespace stability are unproven. Exclusive read leases are disabled because mutation coverage is incomplete.".into());
    }
    Ok(observed)
}

/// A missing after-entry cannot acquire an exclusion lease. Keep the baseline
/// read as historical evidence without turning enumeration absence into proof.
pub fn change_observation(
    before: &Snapshot,
    after: &Snapshot,
    path: &str,
) -> Option<StateObservation> {
    if let Some(read) = after.observations.get(path) {
        return Some(read.clone());
    }
    let state = before.files.get(path)?;
    let baseline = before.observations.get(path);
    Some(StateObservation {
        method: "baseline_only_after_absence_unproven".into(),
        timestamp_domain: "wall_filetime_100ns_diagnostic_only".into(),
        started_ticks: baseline.and_then(|read| read.started_ticks),
        finished_ticks: baseline.and_then(|read| read.finished_ticks),
        baseline_identity: state.identity.clone(),
        baseline_hash: Some(state.hash.clone()),
        baseline_stable: baseline.is_some_and(|read| read.content_stable && read.path_pinned),
        failure: Some("Only the baseline read is retained. Absence from the after enumeration is not an atomic absence observation and does not prove deletion or its actor.".into()),
        ..Default::default()
    })
}

fn snapshot_inner(roots: &[PathBuf], _mode: ObservationMode) -> Result<ObservedSnapshot> {
    let mut snapshot = Snapshot::default();
    #[cfg(test)]
    let mut leases = ReadLeases::default();
    for root in roots {
        let skipped = RefCell::new(HashSet::new());
        for entry in WalkDir::new(root)
            .follow_links(false)
            .into_iter()
            .filter_entry(|entry| match std::fs::symlink_metadata(entry.path()) {
                Ok(metadata) if !is_reparse(&metadata) => true,
                _ => {
                    skipped
                        .borrow_mut()
                        .insert(entry.path().to_string_lossy().into_owned());
                    false
                }
            })
        {
            let entry = entry.with_context(|| format!("scanning {}", root.display()))?;
            anyhow::ensure!(
                snapshot.files.len() + snapshot.unreadable.len() + skipped.borrow().len() < 100_000,
                "Snapshot entry quota (100000) exceeded; refusing a partial state diff"
            );
            let path = entry.path();
            let path_string = path.to_string_lossy().into_owned();
            let metadata = match std::fs::symlink_metadata(entry.path()) {
                Ok(metadata) => metadata,
                Err(_) => {
                    snapshot.unreadable.insert(path_string);
                    continue;
                }
            };
            if is_reparse(&metadata) || !entry.file_type().is_file() {
                continue;
            }
            let mut observation = StateObservation {
                method: "shared_read".into(),
                timestamp_domain: "wall_filetime_100ns_diagnostic_only".into(),
                started_ticks: Some(crate::windows::native::now_ticks()),
                ..Default::default()
            };
            let lease_started = Instant::now();
            #[cfg(not(test))]
            let controlled: Option<File> = None;
            #[cfg(test)]
            let controlled = if _mode == ObservationMode::Shared {
                None
            } else {
                match leases.open(path) {
                    Ok(file) => {
                        observation.method = "deny_write_delete_read_handle".into();
                        observation.path_pinned = true;
                        observation.write_delete_excluded = true;
                        Some(file)
                    }
                    Err(error) => {
                        observation.failure = Some(format!("Read lease unavailable: {error}"));
                        None
                    }
                }
            };
            let mut file = match controlled.map(Ok).unwrap_or_else(|| File::open(path)) {
                Ok(file) => file,
                Err(_) => {
                    snapshot.unreadable.insert(path_string);
                    continue;
                }
            };
            let size = match file.metadata() {
                Ok(metadata) => metadata.len(),
                Err(_) => {
                    snapshot.unreadable.insert(path_string);
                    continue;
                }
            };
            let hash = match blake3::Hasher::new().update_reader(&mut file) {
                Ok(hasher) => hasher.finalize().to_hex().to_string(),
                Err(_) => {
                    snapshot.unreadable.insert(path_string);
                    continue;
                }
            };
            let identity = crate::windows::native::file_identity(file.as_handle());
            observation.finished_ticks = Some(crate::windows::native::now_ticks());
            observation.identity = identity.clone();
            observation.observed_hash = Some(hash.clone());
            observation.content_stable = observation.write_delete_excluded && identity.is_some();
            if observation.content_stable {
                observation.exclusion_duration_ns =
                    Some(lease_started.elapsed().as_nanos().min(u64::MAX as u128) as u64);
            }
            #[cfg(test)]
            if _mode == ObservationMode::Retained && observation.content_stable {
                leases
                    .files
                    .insert(path_string.clone(), (file, lease_started));
            }
            snapshot
                .observations
                .insert(path_string.clone(), observation);
            snapshot.files.insert(
                path_string,
                FileState {
                    hash,
                    size,
                    identity,
                },
            );
        }
        snapshot.unreadable.extend(skipped.into_inner());
    }
    Ok(ObservedSnapshot {
        snapshot,
        #[cfg(test)]
        leases,
    })
}

pub struct FileObserver {
    _watcher: RecommendedWatcher,
    seen: Arc<Mutex<HashSet<PathBuf>>>,
    gaps: Arc<AtomicU64>,
}

impl FileObserver {
    pub fn start(roots: &[PathBuf]) -> Result<Self> {
        let seen = Arc::new(Mutex::new(HashSet::new()));
        let callback_seen = Arc::clone(&seen);
        let gaps = Arc::new(AtomicU64::new(0));
        let callback_gaps = gaps.clone();
        let mut watcher =
            notify::recommended_watcher(move |event: notify::Result<notify::Event>| {
                match (event, callback_seen.lock()) {
                    (Ok(event), Ok(mut paths)) => {
                        if event.need_rescan() {
                            callback_gaps.fetch_add(1, Ordering::Relaxed);
                        }
                        for path in event.paths {
                            if paths.len() < 100_000 || paths.contains(&path) {
                                paths.insert(path);
                            } else {
                                callback_gaps.fetch_add(1, Ordering::Relaxed);
                            }
                        }
                    }
                    _ => {
                        callback_gaps.fetch_add(1, Ordering::Relaxed);
                    }
                }
            })?;
        for root in roots {
            watcher.watch(root, RecursiveMode::Recursive)?;
        }
        Ok(Self {
            _watcher: watcher,
            seen,
            gaps,
        })
    }

    pub fn gaps(&self) -> u64 {
        self.gaps.load(Ordering::Relaxed)
    }

    pub fn seen_paths(&self) -> HashSet<PathBuf> {
        self.seen
            .lock()
            .map(|paths| paths.clone())
            .unwrap_or_default()
    }
}

pub fn diff(before: &Snapshot, after: &Snapshot, seen: &HashSet<PathBuf>) -> Vec<FileChange> {
    let mut paths: Vec<_> = before.files.keys().chain(after.files.keys()).collect();
    paths.sort();
    paths.dedup();
    paths
        .into_iter()
        .filter_map(|path| {
            if before
                .unreadable
                .iter()
                .chain(after.unreadable.iter())
                .any(|skipped| Path::new(path).starts_with(skipped))
            {
                return None;
            }
            let old = before.files.get(path);
            let new = after.files.get(path);
            if old == new {
                return None;
            }
            let notification_seen = seen.contains(Path::new(path));
            let reason = if notification_seen {
                "File notification and before/after state agree; writer PID is unavailable."
            } else {
                "Before/after state differs within watch root; writer PID is unavailable."
            };
            Some(FileChange {
                path: path.clone(),
                operation: if old.is_none() {
                    "created"
                } else if new.is_none() {
                    "deleted"
                } else {
                    "modified"
                }
                .into(),
                before_hash: old.map(|x| x.hash.clone()),
                after_hash: new.map(|x| x.hash.clone()),
                after_size: new.map(|x| x.size),
                notification_seen,
                confidence: Confidence::Unknown,
                reason: reason.into(),
                evidence: vec![],
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn production_read_does_not_block_writes_or_renames_for_unknown_state() {
        let root = TestRoot::new();
        let directory = root.0.join("directory");
        std::fs::create_dir(&directory).unwrap();
        let file = directory.join("file");
        std::fs::write(&file, "before").unwrap();
        let before = baseline_snapshot(std::slice::from_ref(&root.0)).unwrap();
        let observed = observed_snapshot(std::slice::from_ref(&root.0)).unwrap();
        // Keep the observation object alive, as capture does during STOP/drain.
        std::fs::write(&file, "concurrent overwrite").unwrap();
        std::fs::rename(&directory, root.0.join("renamed")).unwrap();
        let after = observed.finish(&before, false);
        assert!(after.observations.values().all(|read| !read.content_stable
            && !read.path_pinned
            && !read.write_delete_excluded
            && !read.held_through_trace_stop
            && !read.mutation_coverage_complete));
        assert_eq!(
            std::fs::read(root.0.join("renamed/file")).unwrap(),
            b"concurrent overwrite"
        );
    }

    struct TestRoot(PathBuf);
    impl TestRoot {
        fn new() -> Self {
            let path =
                std::env::temp_dir().join(format!("contain-read-lease-{}", uuid::Uuid::new_v4()));
            std::fs::create_dir(&path).unwrap();
            Self(path.canonicalize().unwrap())
        }
    }
    impl Drop for TestRoot {
        fn drop(&mut self) {
            let temp = std::env::temp_dir().canonicalize().unwrap();
            assert!(self.0.starts_with(&temp));
            assert!(
                self.0
                    .file_name()
                    .unwrap()
                    .to_string_lossy()
                    .starts_with("contain-read-lease-")
            );
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn controlled_read_pins_content_and_ancestor_path_until_final_drain() {
        let root = TestRoot::new();
        let directory = root.0.join("directory");
        std::fs::create_dir(&directory).unwrap();
        let file = directory.join("file");
        std::fs::write(&file, "before").unwrap();
        let before = snapshot_inner(std::slice::from_ref(&root.0), ObservationMode::Baseline)
            .unwrap()
            .snapshot;
        let key = file.to_string_lossy().into_owned();
        assert!(before.observations[&key].content_stable);
        // Baseline leases must be gone before installer launch.
        std::fs::write(&file, "after").unwrap();
        let observed =
            snapshot_inner(std::slice::from_ref(&root.0), ObservationMode::Retained).unwrap();
        assert!(observed.snapshot.observations[&key].content_stable);
        assert!(std::fs::write(&file, "concurrent").is_err());
        assert!(std::fs::rename(&file, directory.join("replacement")).is_err());
        assert!(std::fs::rename(&directory, root.0.join("moved")).is_err());
        let after = observed.finish(&before, true);
        let proof = &after.observations[&key];
        assert!(proof.held_through_trace_stop && proof.final_source_complete);
        assert_eq!(proof.identity, proof.baseline_identity);
        assert_eq!(
            after.files[&key].hash,
            blake3::hash(b"after").to_hex().as_str()
        );
        // The recorded hash remains unchanged, but the application can write again.
        std::fs::write(&file, "later").unwrap();
        assert_eq!(
            after.files[&key].hash,
            blake3::hash(b"after").to_hex().as_str()
        );
    }

    #[test]
    fn existing_writer_keeps_readable_state_but_cannot_get_stability_proof() {
        let root = TestRoot::new();
        let path = root.0.join("file");
        std::fs::write(&path, "before").unwrap();
        let writer = OpenOptions::new().write(true).open(&path).unwrap();
        let before = snapshot_inner(std::slice::from_ref(&root.0), ObservationMode::Baseline)
            .unwrap()
            .snapshot;
        let observed =
            snapshot_inner(std::slice::from_ref(&root.0), ObservationMode::Retained).unwrap();
        let after = observed.finish(&before, true);
        let key = path.to_string_lossy().into_owned();
        assert_eq!(before.files.len(), 1);
        assert_eq!(after.files.len(), 1);
        assert!(after.unreadable.is_empty());
        assert!(!after.observations[&key].baseline_stable);
        assert!(!after.observations[&key].content_stable);
        assert!(!after.observations[&key].held_through_trace_stop);
        assert!(
            after.observations[&key]
                .failure
                .as_ref()
                .unwrap()
                .contains("Read lease unavailable")
        );
        drop(writer);
    }

    #[test]
    fn controlled_observation_quota_is_bounded_and_does_not_discard_readable_files() {
        let root = TestRoot::new();
        for index in 0..300 {
            std::fs::write(root.0.join(format!("file-{index:03}")), "data").unwrap();
        }
        let observed =
            snapshot_inner(std::slice::from_ref(&root.0), ObservationMode::Retained).unwrap();
        assert_eq!(observed.snapshot.files.len(), 300);
        assert!(
            observed.leases.files.len() + observed.leases.directories.len()
                <= OBSERVATION_HANDLE_LIMIT
        );
        assert!(!observed.leases.files.is_empty());
        assert!(
            observed
                .snapshot
                .observations
                .values()
                .any(|read| read.failure.as_ref().is_some_and(|e| e.contains("quota")))
        );
        let after = observed.finish(&Snapshot::default(), true);
        assert!(
            after
                .observations
                .values()
                .all(|read| !read.baseline_stable)
        );
    }

    #[test]
    fn replacement_identity_is_retained_and_shared_baseline_is_not_stable() {
        let root = TestRoot::new();
        let path = root.0.join("file");
        std::fs::write(&path, "before").unwrap();
        let before = snapshot(std::slice::from_ref(&root.0)).unwrap();
        let replacement = root.0.join("replacement");
        std::fs::write(&replacement, "after").unwrap();
        std::fs::rename(&path, root.0.join("old")).unwrap();
        std::fs::rename(&replacement, &path).unwrap();
        let after = observed_snapshot(std::slice::from_ref(&root.0))
            .unwrap()
            .finish(&before, true);
        let key = path.to_string_lossy().into_owned();
        assert!(!after.observations[&key].baseline_stable);
        assert_ne!(
            after.observations[&key].identity,
            after.observations[&key].baseline_identity
        );
    }

    #[test]
    fn unrelated_truncate_without_write_call_cannot_validate_final_content() {
        use crate::model::{AttributionEvidence, EvidenceSource, RawEvidence, SystemEvent};
        let root = TestRoot::new();
        let path = root.0.join("file");
        std::fs::write(&path, "before").unwrap();
        let before = baseline_snapshot(std::slice::from_ref(&root.0)).unwrap();
        std::fs::write(&path, "target wrote this").unwrap();
        // CreateFile(TRUNCATE_EXISTING) changes content without a WriteFile call.
        // The current ETW representation retains this as an open request without
        // disposition, so a write-only mutation filter overlooks its effect.
        drop(
            OpenOptions::new()
                .write(true)
                .truncate(true)
                .open(&path)
                .unwrap(),
        );
        let after = observed_snapshot(std::slice::from_ref(&root.0))
            .unwrap()
            .finish(&before, true);
        let mut changes = diff(&before, &after, &HashSet::new());
        assert_eq!(changes.len(), 1);
        assert_eq!(changes[0].after_size, Some(0));
        let resource = path.to_string_lossy().into_owned();
        let target_write = SystemEvent {
            event_type: "file".into(),
            resource: resource.clone(),
            operation: "write_requested".into(),
            confidence: Confidence::High,
            success: Some(true),
            evidence: AttributionEvidence {
                source: EvidenceSource::EtwFile,
                pid: Some(2),
                ..Default::default()
            },
            raw: RawEvidence {
                resource_resolved: true,
                file_object: Some("object".into()),
                object_generation: Some("generation".into()),
                ..Default::default()
            },
            ..Default::default()
        };
        let unrelated_open = SystemEvent {
            event_type: "file".into(),
            resource: resource.clone(),
            operation: "open_requested".into(),
            confidence: Confidence::Unknown,
            raw: RawEvidence {
                resource_resolved: true,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut events = [target_write, unrelated_open];
        crate::attribution::compose_file_observed(
            &mut changes[0],
            &mut events,
            true,
            after.observations.get(&resource).cloned(),
        );
        assert_eq!(changes[0].confidence, Confidence::Unknown);
        assert!(!events[0].state_validated);
        assert_eq!(events[0].confidence, Confidence::High);
    }

    #[test]
    fn deleted_file_keeps_baseline_read_without_claiming_after_absence_or_writer() {
        let root = TestRoot::new();
        let path = root.0.join("file");
        std::fs::write(&path, "before").unwrap();
        let before = baseline_snapshot(std::slice::from_ref(&root.0)).unwrap();
        std::fs::remove_file(&path).unwrap();
        let after = observed_snapshot(std::slice::from_ref(&root.0))
            .unwrap()
            .finish(&before, true);
        let mut changes = diff(&before, &after, &HashSet::new());
        assert_eq!(changes[0].operation, "deleted");
        let proof = change_observation(&before, &after, &changes[0].path).unwrap();
        assert_eq!(proof.method, "baseline_only_after_absence_unproven");
        assert_eq!(proof.baseline_hash, changes[0].before_hash);
        assert!(proof.baseline_identity.is_some() && proof.started_ticks.is_some());
        assert!(proof.identity.is_none() && proof.observed_hash.is_none());
        assert!(!proof.content_stable && !proof.held_through_trace_stop);
        crate::attribution::compose_file_observed(
            &mut changes[0],
            &mut [],
            true,
            Some(proof.clone()),
        );
        assert_eq!(changes[0].confidence, Confidence::Unknown);
        assert_eq!(changes[0].evidence[0].observation.as_ref(), Some(&proof));
    }
    #[test]
    fn diff_distinguishes_created_modified_deleted() {
        let before = Snapshot {
            files: BTreeMap::from([
                (
                    "a".into(),
                    FileState {
                        hash: "1".into(),
                        size: 1,
                        identity: None,
                    },
                ),
                (
                    "b".into(),
                    FileState {
                        hash: "1".into(),
                        size: 1,
                        identity: None,
                    },
                ),
            ]),
            unreadable: HashSet::new(),
            observations: BTreeMap::new(),
        };
        let after = Snapshot {
            files: BTreeMap::from([
                (
                    "b".into(),
                    FileState {
                        hash: "2".into(),
                        size: 2,
                        identity: None,
                    },
                ),
                (
                    "c".into(),
                    FileState {
                        hash: "3".into(),
                        size: 3,
                        identity: None,
                    },
                ),
            ]),
            unreadable: HashSet::new(),
            observations: BTreeMap::new(),
        };
        let changes = diff(&before, &after, &HashSet::new());
        assert_eq!(
            changes
                .iter()
                .map(|c| c.operation.as_str())
                .collect::<Vec<_>>(),
            ["deleted", "modified", "created"]
        );
        assert!(changes.iter().all(|c| c.confidence == Confidence::Unknown));
    }
    #[test]
    fn unreadable_after_state_is_not_reported_as_deletion() {
        let before = Snapshot {
            files: BTreeMap::from([(
                "C:\\x\\file".into(),
                FileState {
                    hash: "a".into(),
                    size: 1,
                    identity: None,
                },
            )]),
            unreadable: HashSet::new(),
            observations: BTreeMap::new(),
        };
        let after = Snapshot {
            files: BTreeMap::new(),
            unreadable: HashSet::from(["C:\\x".into()]),
            observations: BTreeMap::new(),
        };
        assert!(diff(&before, &after, &HashSet::new()).is_empty());
    }

    #[test]
    fn unreadable_baseline_does_not_turn_an_existing_file_into_a_creation() {
        let path = r"C:\root\blocked\existing.txt";
        let before = Snapshot {
            unreadable: HashSet::from([r"C:\root\blocked".into()]),
            ..Default::default()
        };
        let after = Snapshot {
            files: BTreeMap::from([(
                path.into(),
                FileState {
                    hash: "observed-after".into(),
                    size: 14,
                    identity: Some("existing-file".into()),
                },
            )]),
            ..Default::default()
        };
        // Notifications establish no missing baseline, even when the file was seen.
        let seen = HashSet::from([PathBuf::from(path)]);
        assert!(diff(&before, &after, &seen).is_empty());
    }

    #[test]
    fn unreadable_coverage_stops_at_directory_components_and_preserves_sibling_changes() {
        let masked = r"C:\root\blocked\file.txt";
        let sibling = r"C:\root\blocked-sibling\file.txt";
        let other = r"C:\root\other\file.txt";
        for unreadable_before in [false, true] {
            let state = |hash: &str| FileState {
                hash: hash.into(),
                size: 1,
                identity: None,
            };
            let mut before = Snapshot {
                files: BTreeMap::from([
                    (masked.into(), state("before")),
                    (sibling.into(), state("before")),
                    (other.into(), state("before")),
                ]),
                ..Default::default()
            };
            let mut after = Snapshot {
                files: BTreeMap::from([
                    (masked.into(), state("after")),
                    (sibling.into(), state("after")),
                ]),
                ..Default::default()
            };
            let unreadable = if unreadable_before {
                &mut before.unreadable
            } else {
                &mut after.unreadable
            };
            unreadable.insert(r"C:\root\blocked".into());
            let changes = diff(&before, &after, &HashSet::new());
            assert_eq!(changes.len(), 2, "unreadable_before={unreadable_before}");
            assert_eq!(changes[0].path, sibling);
            assert_eq!(changes[0].operation, "modified");
            assert_eq!(changes[0].before_hash.as_deref(), Some("before"));
            assert_eq!(changes[0].after_hash.as_deref(), Some("after"));
            assert_eq!(changes[1].path, other);
            assert_eq!(changes[1].operation, "deleted");
            assert!(
                changes
                    .iter()
                    .all(|change| change.confidence == Confidence::Unknown)
            );
            assert!(changes.iter().all(|change| change.evidence.is_empty()));
        }
    }
}
