//! Pure Windows path spelling and scope matching; no filesystem identity claim.
pub fn normalize_path(path: &str, devices: &[(String, String)]) -> String {
    let mut path = path
        .trim_start_matches("\\\\?\\")
        .trim_start_matches("\\??\\")
        .to_string();
    for (device, drive) in devices {
        if path
            .to_ascii_lowercase()
            .starts_with(&format!("{}\\", device.to_ascii_lowercase()))
        {
            path = format!("{drive}{}", &path[device.len()..]);
            break;
        }
    }
    path.replace('/', "\\").to_lowercase()
}

pub fn in_scope(path: &str, roots: &[String]) -> bool {
    roots.iter().any(|root| {
        path == root
            || path
                .strip_prefix(root)
                .is_some_and(|tail| tail.starts_with('\\'))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_recorded_windows_spelling_without_resolving_identity() {
        for (input, expected) in [
            (r"C:\Apps\Example.DAT", r"c:\apps\example.dat"),
            ("C:/Apps/Example.DAT", r"c:\apps\example.dat"),
            (r"\\?\C:\Apps\Example.DAT", r"c:\apps\example.dat"),
            (r"\??\C:\Apps\Example.DAT", r"c:\apps\example.dat"),
            (r"\\Server\Share\Example.DAT", r"\\server\share\example.dat"),
            (r"C:\Apps\..\Elsewhere.DAT", r"c:\apps\..\elsewhere.dat"),
            ("", ""),
        ] {
            assert_eq!(normalize_path(input, &[]), expected, "{input}");
        }
        // Spelling normalization is not a filesystem lookup or proof that two
        // different paths name the same object; dot components stay literal.
        assert_ne!(
            normalize_path(r"C:\Apps\..\Elsewhere.DAT", &[]),
            normalize_path(r"C:\Elsewhere.DAT", &[])
        );
    }

    #[test]
    fn device_mapping_requires_a_complete_device_component() {
        let devices = vec![
            (r"\Device\OtherVolume".into(), "D:".into()),
            (r"\Device\HarddiskVolume1".into(), "C:".into()),
            (r"\Device\HarddiskVolume10".into(), "E:".into()),
        ];
        for (input, expected) in [
            (r"\Device\OtherVolume\File.DAT", r"d:\file.dat"),
            (
                r"\DEVICE\HARDDISKVOLUME1\Apps\File.DAT",
                r"c:\apps\file.dat",
            ),
            (r"\Device\HarddiskVolume10\File.DAT", r"e:\file.dat"),
            (r"\Device\Unknown\File.DAT", r"\device\unknown\file.dat"),
            (
                r"\Device\HarddiskVolume1Extra\File.DAT",
                r"\device\harddiskvolume1extra\file.dat",
            ),
            (r"\Device\HarddiskVolume1", r"\device\harddiskvolume1"),
        ] {
            assert_eq!(normalize_path(input, &devices), expected, "{input}");
        }
    }

    #[test]
    fn scope_matches_normalized_complete_components_not_string_neighbors() {
        let roots = [r"C:\Apps\Fixture", r"D:\Other"].map(|root| normalize_path(root, &[]));
        for (input, expected) in [
            (r"C:\Apps\Fixture", true),
            (r"c:\apps\fixture\file.dat", true),
            ("C:/APPS/FIXTURE/nested/file.dat", true),
            (r"D:\Other\child", true),
            (r"C:\Apps\FixtureBackup\file.dat", false),
            (r"C:\Apps\Fixture.txt", false),
            (r"C:\Apps\Fixtur", false),
            (r"C:\Apps", false),
            (r"E:\Other\child", false),
            ("", false),
        ] {
            assert_eq!(
                in_scope(&normalize_path(input, &[]), &roots),
                expected,
                "{input}"
            );
        }
        assert!(!in_scope(r"c:\apps\fixture", &[]));
    }
}
