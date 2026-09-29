use crate::model::{Confidence, FileChange};
use anyhow::{Context, Result};
use notify::{RecommendedWatcher, RecursiveMode, Watcher};
use std::cell::RefCell;
use std::collections::{BTreeMap, HashSet};
use std::fs::File;
use std::fs::Metadata;
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use walkdir::WalkDir;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FileState {
    pub hash: String,
    pub size: u64,
}

#[derive(Default)]
pub struct Snapshot {
    pub files: BTreeMap<String, FileState>,
    pub unreadable: HashSet<String>,
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
    let mut snapshot = Snapshot::default();
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
            let mut file = match File::open(path) {
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
            snapshot.files.insert(path_string, FileState { hash, size });
        }
        snapshot.unreadable.extend(skipped.into_inner());
    }
    Ok(snapshot)
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
    fn diff_distinguishes_created_modified_deleted() {
        let before = Snapshot {
            files: BTreeMap::from([
                (
                    "a".into(),
                    FileState {
                        hash: "1".into(),
                        size: 1,
                    },
                ),
                (
                    "b".into(),
                    FileState {
                        hash: "1".into(),
                        size: 1,
                    },
                ),
            ]),
            unreadable: HashSet::new(),
        };
        let after = Snapshot {
            files: BTreeMap::from([
                (
                    "b".into(),
                    FileState {
                        hash: "2".into(),
                        size: 2,
                    },
                ),
                (
                    "c".into(),
                    FileState {
                        hash: "3".into(),
                        size: 3,
                    },
                ),
            ]),
            unreadable: HashSet::new(),
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
                },
            )]),
            unreadable: HashSet::new(),
        };
        let after = Snapshot {
            files: BTreeMap::new(),
            unreadable: HashSet::from(["C:\\x".into()]),
        };
        assert!(diff(&before, &after, &HashSet::new()).is_empty());
    }
}
