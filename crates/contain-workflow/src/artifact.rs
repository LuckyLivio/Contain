use anyhow::{Result, ensure};
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, BTreeSet},
    fs::File,
    io::{Read, Seek, SeekFrom},
    path::Path,
};
pub const MAX_EXPANDED: u64 = 1024 * 1024 * 1024;
pub fn sha256(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
pub fn safe_name(name: &str) -> bool {
    !name.is_empty()
        && !name.contains(['\\', ':'])
        && name.split('/').all(|part| {
            let stem = part.split('.').next().unwrap_or("").to_ascii_uppercase();
            !part.is_empty()
                && part != "."
                && part != ".."
                && !part.ends_with(['.', ' '])
                && !part.chars().any(|c| c < ' ' || "<>\"|?*".contains(c))
                && ![
                    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6",
                    "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
                    "LPT8", "LPT9",
                ]
                .contains(&stem.as_str())
        })
}
pub struct Artifact {
    pub zip_sha256: String,
    pub files: BTreeMap<String, Vec<u8>>,
}
impl Artifact {
    pub fn read(path: &Path) -> Result<Self> {
        let mut file = File::open(path)?;
        ensure!(
            file.metadata()?.len() <= MAX_EXPANDED,
            "ZIP exceeds size limit"
        );
        let mut hasher = Sha256::new();
        std::io::copy(&mut file, &mut hasher)?;
        let zip_sha256 = format!("{:x}", hasher.finalize());
        file.seek(SeekFrom::Start(0))?;
        let mut archive = zip::ZipArchive::new(file)?;
        ensure!(archive.len() <= 10000, "too many ZIP members");
        let mut names = BTreeSet::new();
        let mut files = BTreeMap::new();
        let mut total = 0u64;
        for i in 0..archive.len() {
            let mut member = archive.by_index(i)?;
            let name = member.name()?.to_string();
            let normalized = name.trim_end_matches('/');
            ensure!(safe_name(normalized), "unsafe Windows archive path");
            ensure!(
                names.insert(normalized.to_lowercase()),
                "duplicate Windows archive path"
            );
            let mode = member.unix_mode().unwrap_or(0) & 0o170000;
            ensure!(
                mode == 0 || mode == 0o100000 || mode == 0o040000,
                "special ZIP member rejected"
            );
            if member.is_dir() {
                continue;
            }
            total = total
                .checked_add(member.size())
                .ok_or_else(|| anyhow::anyhow!("size overflow"))?;
            ensure!(total <= MAX_EXPANDED, "expanded ZIP exceeds limit");
            let mut data = Vec::new();
            member
                .by_ref()
                .take(MAX_EXPANDED + 1)
                .read_to_end(&mut data)?;
            ensure!(data.len() as u64 == member.size(), "ZIP size mismatch");
            files.insert(name, data);
        }
        for name in files.keys() {
            let mut parts = name.split('/').collect::<Vec<_>>();
            parts.pop();
            while !parts.is_empty() {
                ensure!(
                    !files
                        .keys()
                        .any(|f| f.eq_ignore_ascii_case(&parts.join("/"))),
                    "file/directory conflict"
                );
                parts.pop();
            }
        }
        Ok(Self { zip_sha256, files })
    }
    pub fn exe_hash(&self, entry: &str) -> Result<String> {
        Ok(sha256(
            self.files
                .get(entry)
                .ok_or_else(|| anyhow::anyhow!("entry missing"))?,
        ))
    }
}
pub fn pe_x64(data: &[u8]) -> bool {
    if data.get(..2) != Some(b"MZ") {
        return false;
    }
    let Some(offset) = data.get(0x3c..0x40) else {
        return false;
    };
    let offset = u32::from_le_bytes(offset.try_into().unwrap()) as usize;
    offset.checked_add(26).is_some_and(|end| end <= data.len())
        && data.get(offset..offset + 4) == Some(b"PE\0\0")
        && data.get(offset + 4..offset + 6) == Some(&[0x64, 0x86])
        && data.get(offset + 24..offset + 26) == Some(&[0x0b, 0x02])
}
