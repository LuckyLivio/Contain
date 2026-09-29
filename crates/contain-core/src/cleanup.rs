use crate::filesystem::is_reparse;
use crate::model::{Capture, CleanupCandidate, CleanupClass, Confidence};
use std::fs;
use std::path::Path;

pub fn plan(capture: &Capture) -> Vec<CleanupCandidate> {
    capture
        .files
        .iter()
        .filter(|change| change.operation != "deleted")
        .filter_map(|change| {
            let path = Path::new(&change.path);
            let metadata = fs::symlink_metadata(path).ok()?;
            if is_reparse(&metadata) || !metadata.is_file() {
                return Some(CleanupCandidate {
                    path: change.path.clone(),
                    class: CleanupClass::Unknown,
                    reason: "Current target is a link or not a regular file; never traverse it."
                        .into(),
                    size: 0,
                });
            }
            let size = metadata.len();
            let current = fs::read(path)
                .ok()
                .map(|bytes| blake3::hash(&bytes).to_hex().to_string());
            let classification = classify(&change.path);
            let (class, reason) = if classification.0 == CleanupClass::UserData {
                classification
            } else if current != change.after_hash {
                (
                    CleanupClass::Unknown,
                    "File changed since capture; owner and content may differ.",
                )
            } else {
                if change.confidence == Confidence::High {
                    classification
                } else {
                    (
                        CleanupClass::Unknown,
                        "Writer is not attributed with High confidence; preserve.",
                    )
                }
            };
            Some(CleanupCandidate {
                path: change.path.clone(),
                class,
                reason: reason.into(),
                size,
            })
        })
        .collect()
}

fn classify(path: &str) -> (CleanupClass, &'static str) {
    let lower = path.to_ascii_lowercase().replace('/', "\\");
    let parts: Vec<_> = lower.split('\\').collect();
    if parts
        .iter()
        .any(|part| matches!(*part, "documents" | "projects" | "user-data" | "userdata"))
    {
        (
            CleanupClass::UserData,
            "Path resembles user data; always preserve by default.",
        )
    } else if parts.iter().any(|part| matches!(*part, "cache" | "caches")) {
        (
            CleanupClass::Review,
            "Attributed cache candidate; human review is still required.",
        )
    } else {
        (
            CleanupClass::Unknown,
            "Application ownership is not established.",
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Confidence, FileChange};
    use uuid::Uuid;

    fn capture_with_file(path: &Path, hash: &str) -> Capture {
        Capture {
            id: "test".into(),
            name: "test".into(),
            installer: "test.exe".into(),
            started_at: "start".into(),
            finished_at: "end".into(),
            exit_code: Some(0),
            watch_roots: vec![],
            registry_key: None,
            processes: vec![],
            registry: vec![],
            warnings: vec![],
            files: vec![FileChange {
                path: path.to_string_lossy().into_owned(),
                operation: "created".into(),
                before_hash: None,
                after_hash: Some(hash.into()),
                after_size: Some(3),
                notification_seen: true,
                confidence: Confidence::Unknown,
                reason: "test".into(),
                ..Default::default()
            }],
            ..Default::default()
        }
    }

    #[test]
    fn user_data_is_preserved() {
        assert_eq!(
            classify("C:\\test\\Projects\\work.txt").0,
            CleanupClass::UserData
        );
        assert_eq!(classify("C:\\test\\cache\\x").0, CleanupClass::Review);
        assert_eq!(classify("C:\\test\\app.exe").0, CleanupClass::Unknown);
    }

    #[test]
    fn changed_and_missing_files_are_never_cleanup_targets() {
        let path = std::env::temp_dir().join(format!("contain-cleanup-test-{}", Uuid::new_v4()));
        fs::write(&path, b"old").unwrap();
        let hash = blake3::hash(b"old").to_hex().to_string();
        let capture = capture_with_file(&path, &hash);
        fs::write(&path, b"new").unwrap();
        let candidates = plan(&capture);
        assert_eq!(candidates[0].class, CleanupClass::Unknown);
        assert!(candidates[0].reason.contains("changed"));
        fs::remove_file(&path).unwrap();
        assert!(plan(&capture).is_empty());
    }

    #[test]
    fn symlink_is_never_read_as_cleanup_target_when_available() {
        let base = std::env::temp_dir().join(format!("contain-link-test-{}", Uuid::new_v4()));
        fs::create_dir(&base).unwrap();
        let target = base.join("target");
        let link = base.join("link");
        fs::write(&target, b"old").unwrap();
        if std::os::windows::fs::symlink_file(&target, &link).is_ok() {
            let hash = blake3::hash(b"old").to_hex();
            let capture = capture_with_file(&link, hash.as_ref());
            let candidates = plan(&capture);
            assert_eq!(candidates[0].class, CleanupClass::Unknown);
            assert!(candidates[0].reason.contains("link"));
            fs::remove_file(&link).unwrap();
        }
        fs::remove_file(&target).unwrap();
        fs::remove_dir(&base).unwrap();
    }
}
