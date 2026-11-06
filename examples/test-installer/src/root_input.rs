//! Opt-in root selection for the controlled zero-argument desktop fixture.
//! Kept independent of Win32 so the selection contract can also run on Linux.

use std::{ffi::OsStr, path::Path};

pub(crate) fn select_root<'a>(
    explicit: Option<&'a Path>,
    configured: Option<&'a OsStr>,
    cleanup: bool,
) -> Result<(&'a Path, bool), &'static str> {
    if let Some(path) = explicit {
        return Ok((path, false));
    }
    if cleanup {
        return Err("fixture cleanup requires an explicit --root");
    }
    let path = configured
        .filter(|value| !value.is_empty())
        .map(Path::new)
        .ok_or("specify --root or the guarded CONTAIN_FIXTURE_ROOT fixture setting")?;
    if !path.is_absolute() {
        return Err("CONTAIN_FIXTURE_ROOT must be an absolute existing marked fixture directory");
    }
    Ok((path, true))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn explicit_root_wins_without_reading_environment_contents() {
        let explicit = Path::new("explicit-root");
        for configured in [None, Some(OsStr::new("")), Some(OsStr::new("other-root"))] {
            assert_eq!(
                select_root(Some(explicit), configured, false),
                Ok((explicit, false))
            );
            assert_eq!(
                select_root(Some(explicit), configured, true),
                Ok((explicit, false))
            );
        }
    }

    #[test]
    fn zero_argument_root_preserves_literal_absolute_path() {
        let configured = std::env::temp_dir().join("contain-demo-中文 空格 %_ &[x]");
        assert_eq!(
            select_root(None, Some(configured.as_os_str()), false),
            Ok((configured.as_path(), true))
        );
    }

    #[test]
    fn absent_empty_and_relative_roots_are_not_inferred() {
        for configured in [
            None,
            Some(OsStr::new("")),
            Some(OsStr::new("relative-root")),
        ] {
            assert!(select_root(None, configured, false).is_err());
        }
    }

    #[test]
    fn environment_alone_cannot_authorize_cleanup() {
        let configured = std::env::temp_dir().join("contain-demo-explicit-cleanup-required");
        assert!(select_root(None, Some(configured.as_os_str()), true).is_err());
    }
}
