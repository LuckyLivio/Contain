use crate::{
    artifact::{pe_x64, safe_name, sha256},
    manifest::Manifest,
    report::{Check, Report, Status},
};
use anyhow::{Context, Result, bail, ensure};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, hash_map::RandomState},
    fs,
    hash::{BuildHasher, Hasher},
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
    time::{SystemTime, UNIX_EPOCH},
};

pub const CONTAIN_OWN_APP_ID: &str = "8f4e2d91-5c3a-4b7e-9a16-2d8c4e7f1035";
pub const SUPPORTED_PUBLISH_KIND: &str = "dotnet-self-contained-win-x64";
const CHINESE_SIMPLIFIED_ISL: &str = include_str!("../../../installer/ChineseSimplified.isl");

static UUID_COUNTER: AtomicU64 = AtomicU64::new(1);

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AttestedFile {
    pub path: String,
    pub bytes: u64,
    pub sha256: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BuildOutputAttestation {
    pub schema_version: u32,
    pub protocol: String,
    pub build_passed: bool,
    pub manifest_canonical_sha256: String,
    pub manifest_raw_sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub publish_rel_dir: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resolved_publish_dir: Option<String>,
    pub files: Vec<AttestedFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PersistedInstallerIdentity {
    pub schema_version: u32,
    pub app_id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProjectInstallerReceipt {
    pub schema_version: u32,
    pub protocol: String,
    pub project_name: String,
    pub display_name: String,
    pub app_id: String,
    pub short_id: String,
    pub identity_source: String,
    pub app_version: String,
    pub version_info_version: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub publisher: Option<String>,
    pub publish_kind: String,
    pub publish_rel_dir: String,
    pub entry: String,
    pub entry_sha256: String,
    pub manifest_sha256: String,
    pub attestation_sha256: String,
    pub payload_file_count: usize,
    pub install_scope: String,
    pub default_install_dir: String,
    pub start_menu_shortcut: String,
    pub installer_file: String,
    pub installer_bytes: u64,
    pub installer_sha256: String,
    pub authenticode_status: String,
    pub signed: bool,
    pub inno_setup_version: String,
}

#[derive(Debug, Clone, Default)]
pub struct CsprojMetadata {
    pub csproj_count: usize,
    pub single_csproj_name: Option<String>,
    pub explicit_version: Option<String>,
    pub assembly_name: Option<String>,
    pub product_name: Option<String>,
}

pub fn write_build_attestation(
    manifest: &Manifest,
    manifest_path: &Path,
    project_root: &Path,
    delivery_dir: Option<&Path>,
    report_dir: &Path,
) -> Result<()> {
    let raw_bytes = fs::read(manifest_path)?;
    let manifest_raw_sha256 = sha256(&raw_bytes);
    let manifest_canonical_sha256 = sha256(&serde_json::to_vec(manifest)?);

    let publish_rel_dir = manifest
        .installer
        .as_ref()
        .and_then(|i| i.publish_dir.clone())
        .or_else(|| manifest.artifact_dir.clone());

    let (resolved_publish_dir, files) = if let Some(rel) = &publish_rel_dir {
        if safe_name(rel) {
            let base = match delivery_dir.filter(|p| p.is_dir()) {
                Some(d) if d.join(rel).is_dir() => d.join(rel),
                _ => project_root.join(rel),
            };
            let abs = std::path::absolute(&base).unwrap_or(base);
            if abs.is_dir() {
                match scan_directory_files(&abs) {
                    Ok(scanned) => (Some(abs.to_string_lossy().into_owned()), scanned),
                    Err(_) => (Some(abs.to_string_lossy().into_owned()), Vec::new()),
                }
            } else {
                (Some(abs.to_string_lossy().into_owned()), Vec::new())
            }
        } else {
            (None, Vec::new())
        }
    } else {
        (None, Vec::new())
    };

    let attestation = BuildOutputAttestation {
        schema_version: 1,
        protocol: "contain-build-output-attestation-1".into(),
        build_passed: true,
        manifest_canonical_sha256,
        manifest_raw_sha256,
        publish_rel_dir,
        resolved_publish_dir,
        files,
    };
    fs::write(
        report_dir.join("build-attestation.json"),
        serde_json::to_vec_pretty(&attestation)?,
    )?;
    Ok(())
}

pub fn scan_directory_files(base_dir: &Path) -> Result<Vec<AttestedFile>> {
    let mut map = BTreeMap::new();
    scan_recursive(base_dir, base_dir, &mut map)?;
    Ok(map.into_values().collect())
}

fn scan_recursive(
    base_dir: &Path,
    current_dir: &Path,
    out: &mut BTreeMap<String, AttestedFile>,
) -> Result<()> {
    for entry in fs::read_dir(current_dir)? {
        let entry = entry?;
        let ft = entry.file_type()?;
        ensure!(
            !ft.is_symlink(),
            "symlinks/reparse points are not permitted in publish output"
        );
        let path = entry.path();
        if ft.is_dir() {
            scan_recursive(base_dir, &path, out)?;
        } else if ft.is_file() {
            let rel = path
                .strip_prefix(base_dir)
                .context("strip publish prefix")?
                .components()
                .map(|c| c.as_os_str().to_string_lossy().into_owned())
                .collect::<Vec<_>>()
                .join("/");
            ensure!(
                safe_name(&rel),
                "unsafe relative path in publish output: {rel}"
            );
            let data = fs::read(&path)?;
            let digest = sha256(&data);
            out.insert(
                rel.clone(),
                AttestedFile {
                    path: rel,
                    bytes: data.len() as u64,
                    sha256: digest,
                },
            );
        } else {
            bail!("unsupported special file in publish output");
        }
    }
    Ok(())
}

pub fn discover_csproj_metadata(project_root: &Path) -> CsprojMetadata {
    let Ok(entries) = fs::read_dir(project_root) else {
        return CsprojMetadata::default();
    };
    let mut csprojs = Vec::new();
    for entry in entries.flatten() {
        let p = entry.path();
        if p.is_file()
            && p.extension()
                .and_then(|e| e.to_str())
                .is_some_and(|e| e.eq_ignore_ascii_case("csproj"))
            && let Some(name) = p.file_name().and_then(|n| n.to_str())
        {
            csprojs.push((name.to_string(), p));
        }
    }
    csprojs.sort_by(|a, b| a.0.cmp(&b.0));
    let mut meta = CsprojMetadata {
        csproj_count: csprojs.len(),
        ..Default::default()
    };
    if csprojs.len() == 1 {
        let (name, path) = &csprojs[0];
        meta.single_csproj_name = Some(name.clone());
        if let Ok(xml) = fs::read_to_string(path) {
            meta.explicit_version = extract_simple_xml_tag(&xml, "Version");
            meta.assembly_name = extract_simple_xml_tag(&xml, "AssemblyName");
            meta.product_name = extract_simple_xml_tag(&xml, "Product");
        }
    }
    meta
}

fn extract_simple_xml_tag(xml: &str, tag: &str) -> Option<String> {
    let open = format!("<{tag}>");
    let close = format!("</{tag}>");
    let mut matches = Vec::new();
    let mut rest = xml;
    while let Some(start) = rest.find(&open) {
        let after_open = &rest[start + open.len()..];
        let end = after_open.find(&close)?;
        let val = after_open[..end].trim();
        if !val.is_empty() && !val.contains("$(") && !val.contains('<') && !val.contains('>') {
            matches.push(val.to_string());
        }
        rest = &after_open[end + close.len()..];
    }
    if !matches.is_empty() && matches.windows(2).all(|w| w[0] == w[1]) {
        Some(matches.remove(0))
    } else {
        None
    }
}

pub fn build_dotnet_publish_guidance(csproj: &CsprojMetadata, suggested_dir: &str) -> String {
    let target = csproj.single_csproj_name.as_deref().unwrap_or(".");
    format!(
        "dotnet publish {target} -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o {suggested_dir}"
    )
}

pub fn normalize_valid_app_id(raw: &str) -> Result<String> {
    let trimmed = raw.trim().trim_start_matches('{').trim_end_matches('}');
    let lower = trimmed.to_ascii_lowercase();
    let parts: Vec<&str> = lower.split('-').collect();
    let valid_shape = lower.len() == 36
        && parts.len() == 5
        && parts[0].len() == 8
        && parts[1].len() == 4
        && parts[2].len() == 4
        && parts[3].len() == 4
        && parts[4].len() == 12
        && parts
            .iter()
            .all(|seg| seg.bytes().all(|b| b.is_ascii_hexdigit()));
    ensure!(valid_shape, "invalid_app_id");
    ensure!(
        lower != "00000000-0000-0000-0000-000000000000",
        "invalid_app_id"
    );
    ensure!(lower != CONTAIN_OWN_APP_ID, "forbidden_contain_identity");
    Ok(lower)
}

pub fn generate_random_uuid_v4() -> String {
    loop {
        let mut hasher = Sha256::new();
        for i in 0..4u64 {
            let mut h = RandomState::new().build_hasher();
            h.write_u64(i);
            hasher.update(h.finish().to_le_bytes());
        }
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_nanos())
            .unwrap_or(0);
        hasher.update(nanos.to_le_bytes());
        hasher.update(std::process::id().to_le_bytes());
        hasher.update(UUID_COUNTER.fetch_add(1, Ordering::Relaxed).to_le_bytes());
        let digest = hasher.finalize();
        let mut b = [0u8; 16];
        b.copy_from_slice(&digest[..16]);
        b[6] = (b[6] & 0x0f) | 0x40;
        b[8] = (b[8] & 0x3f) | 0x80;
        let candidate = format!(
            "{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
            b[0],
            b[1],
            b[2],
            b[3],
            b[4],
            b[5],
            b[6],
            b[7],
            b[8],
            b[9],
            b[10],
            b[11],
            b[12],
            b[13],
            b[14],
            b[15]
        );
        if candidate != CONTAIN_OWN_APP_ID && candidate != "00000000-0000-0000-0000-000000000000" {
            return candidate;
        }
    }
}

pub fn inspect_existing_identity(
    project_root: &Path,
    manifest_app_id: Option<&str>,
) -> std::result::Result<Option<(String, String)>, (&'static str, String)> {
    let file_path = project_root.join("contain.installer.json");
    let persisted = if file_path.exists() {
        let bytes = fs::read(&file_path).map_err(|e| {
            (
                "invalid_identity_file",
                format!("无法读取 contain.installer.json：{e}"),
            )
        })?;
        let parsed: PersistedInstallerIdentity = serde_json::from_slice(&bytes).map_err(|e| {
            (
                "invalid_identity_file",
                format!("contain.installer.json 格式无效：{e}"),
            )
        })?;
        if parsed.schema_version != 1 {
            return Err((
                "invalid_identity_file",
                "contain.installer.json 仅支持 schema_version = 1。".into(),
            ));
        }
        let norm = normalize_valid_app_id(&parsed.app_id).map_err(|e| {
            let msg = e.to_string();
            if msg.contains("forbidden_contain_identity") {
                (
                    "forbidden_contain_identity",
                    "用户项目不能使用 Contain 自身的安装身份（AppId）。".into(),
                )
            } else {
                (
                    "invalid_app_id",
                    "contain.installer.json 中的 app_id 必须是有效的非零 UUID。".into(),
                )
            }
        })?;
        Some(norm)
    } else {
        None
    };

    let manifest_norm = if let Some(raw) = manifest_app_id {
        let norm = normalize_valid_app_id(raw).map_err(|e| {
            let msg = e.to_string();
            if msg.contains("forbidden_contain_identity") {
                (
                    "forbidden_contain_identity",
                    "用户项目不能使用 Contain 自身的安装身份（AppId）。".into(),
                )
            } else {
                (
                    "invalid_app_id",
                    "contain.environment.json 中 installer.app_id 必须是标准 UUID 格式（如 xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx）。".into(),
                )
            }
        })?;
        Some(norm)
    } else {
        None
    };

    match (persisted, manifest_norm) {
        (Some(p), Some(m)) => {
            if p != m {
                Err((
                    "conflicting_app_id",
                    format!(
                        "contain.environment.json 中的 installer.app_id ({m}) 与项目已持久化的 contain.installer.json ({p}) 不一致；请统一为同一 UUID 以保持升级身份稳定。"
                    ),
                ))
            } else {
                Ok(Some((p, "persisted_and_manifest".into())))
            }
        }
        (Some(p), None) => Ok(Some((p, "contain.installer.json".into()))),
        (None, Some(m)) => Ok(Some((m, "manifest_to_persist".into()))),
        (None, None) => Ok(None),
    }
}

pub fn resolve_or_persist_identity(
    project_root: &Path,
    manifest_app_id: Option<&str>,
) -> Result<(String, String)> {
    match inspect_existing_identity(project_root, manifest_app_id) {
        Ok(Some((id, source))) => {
            let file_path = project_root.join("contain.installer.json");
            if !file_path.exists() {
                let doc = PersistedInstallerIdentity {
                    schema_version: 1,
                    app_id: id.clone(),
                };
                fs::write(&file_path, serde_json::to_vec_pretty(&doc)?)?;
                return Ok((id, "manifest_persisted_to_file".into()));
            }
            Ok((id, source))
        }
        Ok(None) => {
            let id = generate_random_uuid_v4();
            let doc = PersistedInstallerIdentity {
                schema_version: 1,
                app_id: id.clone(),
            };
            fs::write(
                project_root.join("contain.installer.json"),
                serde_json::to_vec_pretty(&doc)?,
            )?;
            Ok((id, "generated_and_persisted".into()))
        }
        Err((_, msg)) => bail!("{msg}"),
    }
}

pub fn validate_display_name(raw: &str) -> Result<String> {
    let trimmed = raw.trim();
    ensure!(
        !trimmed.is_empty()
            && trimmed.chars().count() <= 80
            && !trimmed.chars().any(char::is_control),
        "项目显示名称必须为 1..80 个非控制字符"
    );
    Ok(trimmed.to_string())
}

pub fn is_valid_windows_segment(name: &str) -> bool {
    if name.is_empty()
        || name == "."
        || name == ".."
        || name.ends_with(['.', ' '])
        || name.chars().any(|c| c < ' ' || "<>:\"/\\|?*".contains(c))
    {
        return false;
    }
    let stem = name.split('.').next().unwrap_or("").to_ascii_uppercase();
    ![
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8",
        "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ]
    .contains(&stem.as_str())
}

fn sanitize_segment_token(raw: &str, max_chars: usize) -> String {
    let mut out = String::new();
    for ch in raw.chars() {
        if out.chars().count() >= max_chars {
            break;
        }
        if ch.is_control() || "<>:\"/\\|?*".contains(ch) {
            if !out.ends_with('-') && !out.is_empty() {
                out.push('-');
            }
        } else {
            out.push(ch);
        }
    }
    out.trim_matches([' ', '.', '-']).to_string()
}

pub fn derive_and_validate_setup_file_stem(display_name: &str, entry_stem: &str) -> Result<String> {
    let mut candidate = sanitize_segment_token(display_name, 48);
    if candidate.is_empty() || !is_valid_windows_segment(&candidate) {
        candidate = sanitize_segment_token(entry_stem, 48);
    }
    if candidate.is_empty() || !is_valid_windows_segment(&candidate) {
        candidate = "App".into();
    }
    let full_exe = format!("{candidate}-Setup-x64.exe");
    ensure!(
        is_valid_windows_segment(&full_exe),
        "无法为应用生成合法的 Windows 安装包文件名：{full_exe}"
    );
    Ok(candidate)
}

pub fn derive_and_validate_install_paths(
    display_name: &str,
    setup_file_stem: &str,
    short_id: &str,
) -> Result<(String, String)> {
    ensure!(
        short_id.len() == 8 && short_id.bytes().all(|b| b.is_ascii_hexdigit()),
        "invalid short_id"
    );
    let mut base = sanitize_segment_token(display_name, 40);
    if base.is_empty() || !is_valid_windows_segment(&base) {
        base = setup_file_stem.to_string();
    }
    let install_dir_leaf = format!("{base}-{short_id}");
    ensure!(
        is_valid_windows_segment(&install_dir_leaf)
            && !install_dir_leaf.eq_ignore_ascii_case("Contain"),
        "非法的默认安装目录名称：{install_dir_leaf}"
    );

    let shortcut_name = format!("{base} ({short_id})");
    ensure!(
        is_valid_windows_segment(&format!("{shortcut_name}.lnk"))
            && !shortcut_name.eq_ignore_ascii_case("Contain"),
        "非法的开始菜单快捷方式名称：{shortcut_name}"
    );
    Ok((install_dir_leaf, shortcut_name))
}

pub fn convert_to_four_part_version(semver: &str) -> Result<String> {
    ensure!(
        !semver.is_empty()
            && semver.len() <= 64
            && semver
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || "._-+".contains(c)),
        "版本号只能包含字母、数字与 ._-+"
    );
    let core = semver.split(['-', '+']).next().unwrap_or("");
    let parts: Vec<&str> = core.split('.').collect();
    ensure!(
        parts.len() >= 2
            && parts.len() <= 4
            && parts
                .iter()
                .all(|p| !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit())),
        "版本号核心段需为 2..4 段非负整数（例如 0.1.0 或 1.2.3）"
    );
    let mut nums = [0u16; 4];
    for (i, p) in parts.iter().take(4).enumerate() {
        nums[i] = p.parse::<u16>().context("版本号数值段超出 0..65535 范围")?;
    }
    if parts.len() < 4
        && let Some((_, pre)) = semver.split_once('-')
    {
        for token in pre.split('.') {
            if !token.is_empty()
                && token.bytes().all(|b| b.is_ascii_digit())
                && let Ok(n) = token.parse::<u16>()
            {
                nums[3] = n;
                break;
            }
        }
    }
    Ok(format!("{}.{}.{}.{}", nums[0], nums[1], nums[2], nums[3]))
}

pub fn escape_iss_setup_value(raw: &str) -> Result<String> {
    ensure!(
        !raw.chars().any(char::is_control),
        "template parameter contains control characters"
    );
    let braces_escaped = raw.replace('{', "{{");
    if braces_escaped.starts_with(' ')
        || braces_escaped.ends_with(' ')
        || braces_escaped.contains('"')
        || braces_escaped.contains(';')
    {
        Ok(format!("\"{}\"", braces_escaped.replace('"', "\"\"")))
    } else {
        Ok(braces_escaped)
    }
}

pub fn escape_iss_quoted(raw: &str) -> Result<String> {
    ensure!(
        !raw.chars().any(char::is_control),
        "template parameter contains control characters"
    );
    Ok(raw.replace('{', "{{").replace('"', "\"\""))
}

pub fn escape_iss_cm_arg(raw: &str) -> Result<String> {
    ensure!(
        !raw.chars().any(char::is_control),
        "template parameter contains control characters"
    );
    Ok(raw
        .replace('{', "{{")
        .replace('"', "\"\"")
        .replace('&', "&&")
        .replace(',', "%2c"))
}

pub fn escape_iss_pascal_string(raw: &str) -> Result<String> {
    ensure!(
        !raw.chars().any(char::is_control),
        "template parameter contains control characters"
    );
    Ok(raw.replace('\'', "''"))
}

#[allow(clippy::too_many_arguments)]
pub fn render_project_iss(
    app_id: &str,
    short_id: &str,
    display_name: &str,
    app_version: &str,
    four_part_version: &str,
    publisher: Option<&str>,
    install_dir_leaf: &str,
    shortcut_name: &str,
    entry_rel_posix: &str,
    payload_dir: &Path,
    output_dir: &Path,
    output_base_filename: &str,
) -> Result<String> {
    let norm_app_id = normalize_valid_app_id(app_id)?;
    let entry_rel_win = entry_rel_posix.replace('/', "\\");
    ensure!(
        !entry_rel_win.chars().any(char::is_control) && !entry_rel_win.contains('"'),
        "invalid entry path"
    );

    let esc_display = escape_iss_setup_value(display_name)?;
    let esc_ver = escape_iss_setup_value(app_version)?;
    let esc_ver_name = escape_iss_setup_value(&format!("{display_name} {app_version}"))?;
    let esc_uninstall_name = escape_iss_setup_value(&format!("{display_name} ({short_id})"))?;
    let esc_desc = escape_iss_setup_value(&format!(
        "{display_name} Windows x64 Installer ({app_version})"
    ))?;
    let esc_dir_leaf = escape_iss_quoted(install_dir_leaf)?;
    let esc_shortcut = escape_iss_quoted(shortcut_name)?;
    let esc_entry_win = escape_iss_quoted(&entry_rel_win)?;
    let esc_payload = escape_iss_quoted(&payload_dir.to_string_lossy())?;
    let esc_output_dir = escape_iss_setup_value(&output_dir.to_string_lossy())?;
    let esc_output_base = escape_iss_setup_value(output_base_filename)?;
    let esc_cm_display = escape_iss_cm_arg(display_name)?;
    let esc_pascal_entry = escape_iss_pascal_string(&entry_rel_win)?;
    let esc_pascal_display = escape_iss_pascal_string(display_name)?;

    let publisher_line = if let Some(pub_name) = publisher.filter(|s| !s.trim().is_empty()) {
        format!(
            "AppPublisher={}\r\n",
            escape_iss_setup_value(pub_name.trim())?
        )
    } else {
        String::new()
    };

    let iss = format!(
        "[Setup]\r\n\
AppId={{{{{norm_app_id}}}\r\n\
AppName={esc_display}\r\n\
AppVersion={esc_ver}\r\n\
AppVerName={esc_ver_name}\r\n\
{publisher_line}\
DefaultDirName={{localappdata}}\\Programs\\{esc_dir_leaf}\r\n\
UsePreviousAppDir=yes\r\n\
DisableProgramGroupPage=yes\r\n\
PrivilegesRequired=lowest\r\n\
ArchitecturesAllowed=x64compatible\r\n\
ArchitecturesInstallIn64BitMode=x64compatible\r\n\
OutputDir={esc_output_dir}\r\n\
OutputBaseFilename={esc_output_base}\r\n\
Compression=lzma2\r\n\
SolidCompression=yes\r\n\
WizardStyle=modern\r\n\
UninstallDisplayIcon={{app}}\\{esc_entry_win}\r\n\
UninstallDisplayName={esc_uninstall_name}\r\n\
VersionInfoVersion={four_part_version}\r\n\
VersionInfoProductName={esc_display}\r\n\
VersionInfoProductTextVersion={esc_ver}\r\n\
VersionInfoDescription={esc_desc}\r\n\
CloseApplications=no\r\n\
RestartApplications=no\r\n\
\r\n\
[Languages]\r\n\
Name: \"chinesesimplified\"; MessagesFile: \"ChineseSimplified.isl\"\r\n\
\r\n\
[Tasks]\r\n\
Name: \"desktopicon\"; Description: \"{{cm:CreateDesktopIcon}}\"; GroupDescription: \"{{cm:AdditionalIcons}}\"; Flags: unchecked\r\n\
\r\n\
[Files]\r\n\
Source: \"{esc_payload}\\*\"; DestDir: \"{{app}}\"; Flags: ignoreversion recursesubdirs createallsubdirs\r\n\
\r\n\
[Icons]\r\n\
Name: \"{{userprograms}}\\{esc_shortcut}\"; Filename: \"{{app}}\\{esc_entry_win}\"; WorkingDir: \"{{app}}\"\r\n\
Name: \"{{userdesktop}}\\{esc_shortcut}\"; Filename: \"{{app}}\\{esc_entry_win}\"; WorkingDir: \"{{app}}\"; Tasks: desktopicon\r\n\
\r\n\
[Run]\r\n\
Filename: \"{{app}}\\{esc_entry_win}\"; Description: \"{{cm:LaunchProgram,{esc_cm_display}}}\"; WorkingDir: \"{{app}}\"; Flags: nowait postinstall skipifsilent\r\n\
\r\n\
[Code]\r\n\
const\r\n\
  PROJ_GENERIC_READ = $80000000;\r\n\
  PROJ_GENERIC_WRITE = $40000000;\r\n\
  PROJ_OPEN_EXISTING = 3;\r\n\
  PROJ_FILE_ATTR_NORMAL = $00000080;\r\n\
  PROJ_INVALID_HANDLE = -1;\r\n\
  PROJ_ERR_SHARING_VIOLATION = 32;\r\n\
\r\n\
function ProjCreateFile(FileName: String; DesiredAccess, ShareMode: Cardinal; SecurityAttributes: Integer; CreationDisposition, FlagsAndAttributes: Cardinal; TemplateFile: THandle): THandle;\r\n\
  external 'CreateFileW@kernel32.dll stdcall';\r\n\
function ProjCloseHandle(ObjectHandle: THandle): BOOL;\r\n\
  external 'CloseHandle@kernel32.dll stdcall';\r\n\
function ProjGetLastError(): Cardinal;\r\n\
  external 'GetLastError@kernel32.dll stdcall';\r\n\
\r\n\
function IsEntryExecutableInUse(const TargetDir: String): Boolean;\r\n\
var\r\n\
  EntryPath: String;\r\n\
  Handle: THandle;\r\n\
begin\r\n\
  Result := False;\r\n\
  EntryPath := AddBackslash(TargetDir) + '{esc_pascal_entry}';\r\n\
  if not FileExists(EntryPath) then\r\n\
    Exit;\r\n\
  Handle := ProjCreateFile(EntryPath, PROJ_GENERIC_READ or PROJ_GENERIC_WRITE, 0, 0, PROJ_OPEN_EXISTING, PROJ_FILE_ATTR_NORMAL, 0);\r\n\
  if Handle = THandle(PROJ_INVALID_HANDLE) then\r\n\
  begin\r\n\
    if ProjGetLastError() = PROJ_ERR_SHARING_VIOLATION then\r\n\
      Result := True;\r\n\
  end\r\n\
  else\r\n\
    ProjCloseHandle(Handle);\r\n\
end;\r\n\
\r\n\
function EnsureTargetAppClosed(const TargetDir: String; const ActionWord: String): Boolean;\r\n\
var\r\n\
  PromptText: String;\r\n\
begin\r\n\
  while IsEntryExecutableInUse(TargetDir) do\r\n\
  begin\r\n\
    if WizardSilent() then\r\n\
    begin\r\n\
      Result := False;\r\n\
      Exit;\r\n\
    end;\r\n\
    PromptText := '检测到“{esc_pascal_display}”当前正在运行。请先正常关闭程序后点击“确定”继续' + ActionWord + '，或点击“取消”退出。';\r\n\
    if MsgBox(PromptText, mbConfirmation, MB_OKCANCEL) <> IDOK then\r\n\
    begin\r\n\
      Result := False;\r\n\
      Exit;\r\n\
    end;\r\n\
  end;\r\n\
  Result := True;\r\n\
end;\r\n\
\r\n\
function PrepareToInstall(var NeedsRestart: Boolean): String;\r\n\
begin\r\n\
  NeedsRestart := False;\r\n\
  if not EnsureTargetAppClosed(WizardDirValue(), '安装或更新') then\r\n\
    Result := '请先关闭正在运行的程序后再继续安装。'\r\n\
  else\r\n\
    Result := '';\r\n\
end;\r\n\
\r\n\
function InitializeUninstall(): Boolean;\r\n\
var\r\n\
  PromptText: String;\r\n\
begin\r\n\
  while IsEntryExecutableInUse(ExpandConstant('{{app}}')) do\r\n\
  begin\r\n\
    if UninstallSilent() then\r\n\
    begin\r\n\
      Result := False;\r\n\
      Exit;\r\n\
    end;\r\n\
    PromptText := '检测到“{esc_pascal_display}”当前正在运行。请先正常关闭程序后点击“确定”继续卸载，或点击“取消”退出。';\r\n\
    if MsgBox(PromptText, mbConfirmation, MB_OKCANCEL) <> IDOK then\r\n\
    begin\r\n\
      Result := False;\r\n\
      Exit;\r\n\
    end;\r\n\
  end;\r\n\
  Result := True;\r\n\
end;\r\n"
    );
    Ok(iss)
}

pub fn resolve_iscc_executable(explicit_iscc: Option<&Path>) -> Option<PathBuf> {
    if let Some(p) = explicit_iscc {
        return p.is_file().then(|| p.to_path_buf());
    }
    if let Some(env_override) = std::env::var_os("CONTAIN_ISCC_PATH")
        && !env_override.is_empty()
    {
        let p = PathBuf::from(env_override);
        return p.is_file().then_some(p);
    }
    if let Some(paths) = std::env::var_os("PATH") {
        for dir in std::env::split_paths(&paths) {
            let cand = dir.join(if cfg!(windows) { "ISCC.exe" } else { "iscc" });
            if cand.is_file() {
                return Some(cand);
            }
        }
    }
    if cfg!(windows) {
        for env_key in ["LOCALAPPDATA", "ProgramFiles(x86)", "ProgramFiles"] {
            if let Some(base) = std::env::var_os(env_key) {
                let base_path = PathBuf::from(base);
                let cand = if env_key == "LOCALAPPDATA" {
                    base_path
                        .join("Programs")
                        .join("Inno Setup 6")
                        .join("ISCC.exe")
                } else {
                    base_path.join("Inno Setup 6").join("ISCC.exe")
                };
                if cand.is_file() {
                    return Some(cand);
                }
            }
        }
    }
    None
}

fn is_strict_subpath(root: &Path, candidate: &Path) -> bool {
    if cfg!(windows) {
        let r = root
            .to_string_lossy()
            .replace('/', "\\")
            .trim_end_matches('\\')
            .to_ascii_lowercase();
        let c = candidate
            .to_string_lossy()
            .replace('/', "\\")
            .trim_end_matches('\\')
            .to_ascii_lowercase();
        c.len() > r.len() && c.starts_with(&format!("{r}\\"))
    } else {
        candidate != root && candidate.starts_with(root)
    }
}

fn is_forbidden_payload_file(rel_posix: &str) -> bool {
    let file_name = rel_posix.rsplit('/').next().unwrap_or(rel_posix);
    let lower = file_name.to_ascii_lowercase();
    lower == "contain.environment.json"
        || lower == "contain.installer.json"
        || lower == ".contain-records-test-root"
        || lower.ends_with(".db")
        || lower.ends_with(".sqlite")
        || lower.ends_with(".sqlite3")
        || lower.ends_with(".db-wal")
        || lower.ends_with(".db-shm")
        || lower.ends_with(".local.log")
        || lower.ends_with(".csproj")
        || lower.ends_with(".sln")
}

fn verify_dotnet_self_contained_multifile(
    publish_dir: &Path,
    entry_rel_posix: &str,
    csproj: &CsprojMetadata,
    suggested_dir: &str,
) -> std::result::Result<(), (&'static str, String, String)> {
    let guidance_cmd = build_dotnet_publish_guidance(csproj, suggested_dir);
    let entry_path = publish_dir.join(entry_rel_posix.replace('/', std::path::MAIN_SEPARATOR_STR));
    let entry_bytes = fs::read(&entry_path).map_err(|e| {
        (
            "missing_entry",
            format!("无法读取声明的入口程序 {entry_rel_posix}：{e}"),
            guidance_cmd.clone(),
        )
    })?;
    if !pe_x64(&entry_bytes) {
        return Err((
            "invalid_entry_pe",
            format!(
                "入口程序 {entry_rel_posix} 不是有效的 Windows x64 可执行文件（PE32+ x86_64）。"
            ),
            guidance_cmd,
        ));
    }

    let entry_parent = entry_path.parent().unwrap_or(publish_dir);
    let stem = entry_path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("App");

    let runtimeconfig_path = entry_parent.join(format!("{stem}.runtimeconfig.json"));
    let deps_path = entry_parent.join(format!("{stem}.deps.json"));
    let managed_dll_path = entry_parent.join(format!("{stem}.dll"));

    if !runtimeconfig_path.is_file() {
        let remedy =
            format!("当前首版仅支持 Windows x64、.NET 自包含、多文件发布；请使用：{guidance_cmd}");
        if !managed_dll_path.is_file() && !entry_parent.join("coreclr.dll").is_file() {
            return Err((
                "single_file_or_unsupported_stack",
                format!(
                    "未找到 {stem}.runtimeconfig.json 与多文件运行时组件（可能为单文件发布或非 .NET 项目）。"
                ),
                remedy,
            ));
        }
        return Err((
            "not_dotnet_publish_output",
            format!("发布目录缺少 {stem}.runtimeconfig.json，无法确认为 .NET 发布输出。"),
            remedy,
        ));
    }

    let rc_json: serde_json::Value =
        serde_json::from_slice(&fs::read(&runtimeconfig_path).map_err(|e| {
            (
                "invalid_runtimeconfig",
                format!("无法读取 {stem}.runtimeconfig.json：{e}"),
                guidance_cmd.clone(),
            )
        })?)
        .map_err(|e| {
            (
                "invalid_runtimeconfig",
                format!("{stem}.runtimeconfig.json 不是有效的 JSON：{e}"),
                guidance_cmd.clone(),
            )
        })?;

    let included_frameworks = rc_json
        .pointer("/runtimeOptions/includedFrameworks")
        .and_then(|v| v.as_array());
    let has_included_netcore = included_frameworks.is_some_and(|arr| {
        arr.iter().any(|item| {
            item.get("name")
                .and_then(|n| n.as_str())
                .is_some_and(|n| n == "Microsoft.NETCore.App")
        })
    });
    if !has_included_netcore {
        return Err((
            "framework_dependent_output",
            format!(
                "当前构建通过，但产物（{entry_rel_posix}）是依赖本机 .NET 运行时的普通编译结果，换到未安装对应 .NET 运行时的电脑可能无法运行。"
            ),
            format!("请将构建步骤改为 win-x64 自包含多文件发布后重新构建，例如：{guidance_cmd}"),
        ));
    }

    if !managed_dll_path.is_file() || !deps_path.is_file() {
        return Err((
            "incomplete_dotnet_publish",
            format!("发布目录缺少 {stem}.dll 或 {stem}.deps.json。"),
            format!("请重新执行完整自包含多文件发布：{guidance_cmd}"),
        ));
    }

    let deps_json: serde_json::Value =
        serde_json::from_slice(&fs::read(&deps_path).map_err(|e| {
            (
                "invalid_deps_json",
                format!("无法读取 {stem}.deps.json：{e}"),
                guidance_cmd.clone(),
            )
        })?)
        .map_err(|e| {
            (
                "invalid_deps_json",
                format!("{stem}.deps.json 格式无效：{e}"),
                guidance_cmd.clone(),
            )
        })?;

    let runtime_target = deps_json
        .pointer("/runtimeTarget/name")
        .and_then(|v| v.as_str())
        .unwrap_or("");
    if !runtime_target.contains("win-x64") {
        return Err((
            "wrong_publish_rid",
            format!("发布目标平台为“{runtime_target}”，不是受支持的 win-x64。"),
            format!("请指定 -r win-x64 重新发布：{guidance_cmd}"),
        ));
    }

    for required_runtime_dll in ["coreclr.dll", "hostfxr.dll", "hostpolicy.dll"] {
        let dll_path = entry_parent.join(required_runtime_dll);
        if !dll_path.is_file() {
            return Err((
                "missing_self_contained_runtime",
                format!("发布目录缺少自包含 .NET 运行时组件 {required_runtime_dll}。"),
                format!(
                    "请确认开启多文件自包含发布（--self-contained true -p:PublishSingleFile=false）：{guidance_cmd}"
                ),
            ));
        }
    }

    let coreclr_bytes = fs::read(entry_parent.join("coreclr.dll")).map_err(|e| {
        (
            "missing_self_contained_runtime",
            format!("无法读取 coreclr.dll：{e}"),
            guidance_cmd.clone(),
        )
    })?;
    if !pe_x64(&coreclr_bytes) {
        return Err((
            "wrong_publish_rid",
            "发布目录中的 coreclr.dll 不是 Windows x64（PE32+ x86_64）运行时。".into(),
            format!("请指定 -r win-x64 重新发布：{guidance_cmd}"),
        ));
    }

    Ok(())
}

struct TempStageDir(PathBuf);
impl TempStageDir {
    fn new() -> Result<Self> {
        let dir = std::env::temp_dir().join(format!(
            "Contain-ProjInstaller-{}",
            generate_random_uuid_v4()
        ));
        fs::create_dir_all(&dir)?;
        Ok(Self(dir))
    }
}
impl Drop for TempStageDir {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn write_utf8_bom(path: &Path, content: &str) -> Result<()> {
    let mut bytes = Vec::with_capacity(3 + content.len());
    bytes.extend_from_slice(&[0xEF, 0xBB, 0xBF]);
    bytes.extend_from_slice(content.as_bytes());
    fs::write(path, bytes)?;
    Ok(())
}

fn query_inno_setup_version(iscc_path: &Path) -> String {
    #[cfg(windows)]
    {
        let script = r#"
$roots = @(
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
  'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
)
foreach ($r in $roots) {
  $e = Get-ItemProperty -Path $r -ErrorAction SilentlyContinue | Where-Object {
    $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'Inno Setup*' -and
    $_.PSObject.Properties['DisplayVersion'] -and $_.DisplayVersion
  } | Select-Object -First 1
  if ($e) { [Console]::Write($e.DisplayVersion); exit 0 }
}
"#;
        if let Ok(out) = Command::new("powershell.exe")
            .args([
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                script,
            ])
            .output()
            && out.status.success()
        {
            let v = String::from_utf8_lossy(&out.stdout).trim().to_string();
            if !v.is_empty() {
                return v;
            }
        }
    }
    if let Ok(out) = Command::new(iscc_path).arg("/?").output() {
        let combined = format!(
            "{}\n{}",
            String::from_utf8_lossy(&out.stdout),
            String::from_utf8_lossy(&out.stderr)
        );
        for line in combined.lines() {
            if let Some(idx) = line.find("Inno Setup") {
                let tail = line[idx + "Inno Setup".len()..].trim();
                if let Some(tok) = tail.split_whitespace().next()
                    && tok.chars().any(|c| c.is_ascii_digit())
                {
                    return tok.to_string();
                }
            }
        }
    }
    "6.x".into()
}

fn query_authenticode_status(exe_path: &Path) -> (String, bool) {
    #[cfg(windows)]
    {
        if let Ok(out) = Command::new("powershell.exe")
            .args([
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "[Console]::Write((Get-AuthenticodeSignature -LiteralPath $env:CONTAIN_SIGN_CHECK_FILE).Status.ToString())",
            ])
            .env("CONTAIN_SIGN_CHECK_FILE", exe_path)
            .output()
            && out.status.success()
        {
            let status = String::from_utf8_lossy(&out.stdout).trim().to_string();
            if !status.is_empty() {
                let signed = status.eq_ignore_ascii_case("Valid");
                return (status, signed);
            }
        }
    }
    let _ = exe_path;
    ("NotSigned".into(), false)
}

pub fn package_installer(
    project_root: &Path,
    manifest_override: Option<&Path>,
    attestation_path: &Path,
    output_dir: &Path,
    explicit_iscc: Option<&Path>,
    report_dir: &Path,
) -> Result<(Report, Option<ProjectInstallerReceipt>)> {
    ensure!(
        !output_dir.exists(),
        "installer output directory already exists; choose a new directory so previous installers are preserved"
    );

    let abs_project =
        std::path::absolute(project_root).unwrap_or_else(|_| project_root.to_path_buf());
    let project_folder_name = abs_project
        .file_name()
        .and_then(|n| n.to_str())
        .filter(|s| !s.is_empty())
        .unwrap_or("Project")
        .to_string();

    let mut report = Report::new("project_installer");
    let platform_ok = report.host_os == "windows"
        && report.host_arch == "x86_64"
        && report.process_arch == "x86_64";
    report.checks.push(
        Check::new(
            "platform",
            if platform_ok {
                Status::Passed
            } else {
                Status::Failed
            },
            "requires native Windows x64",
        )
        .with_evidence(
            Some(
                if platform_ok {
                    "matched"
                } else {
                    "platform_mismatch"
                }
                .into(),
            ),
            Some("windows / x86_64".into()),
            Some(format!("{} / {}", report.host_os, report.host_arch)),
            if platform_ok {
                None
            } else {
                Some("请在 64 位 Windows (x64) 开发机上生成 Windows 安装包。".into())
            },
        ),
    );

    let manifest_path = manifest_override
        .map(Path::to_path_buf)
        .unwrap_or_else(|| abs_project.join("contain.environment.json"));
    let manifest_bytes = fs::read(&manifest_path)
        .with_context(|| format!("failed to read {}", manifest_path.display()))?;
    let manifest_raw_sha = sha256(&manifest_bytes);
    let manifest = Manifest::read(&manifest_path)?;
    let manifest_canonical_sha = sha256(&serde_json::to_vec(&manifest)?);
    report.manifest_source = Some("contain.environment.json".into());

    let att_bytes = fs::read(attestation_path).with_context(|| {
        format!(
            "failed to read build attestation {}",
            attestation_path.display()
        )
    })?;
    let attestation_sha = sha256(&att_bytes);
    let attestation: BuildOutputAttestation =
        serde_json::from_slice(&att_bytes).context("invalid build-attestation.json")?;

    let csproj = discover_csproj_metadata(&abs_project);
    let inst_spec = manifest.installer.clone().unwrap_or_default();

    // 1. Manifest binding check
    let manifest_bound = attestation.schema_version == 1
        && attestation.protocol == "contain-build-output-attestation-1"
        && attestation.build_passed
        && attestation.manifest_raw_sha256 == manifest_raw_sha
        && attestation.manifest_canonical_sha256 == manifest_canonical_sha;
    report.checks.push(if manifest_bound {
        Check::new(
            "manifest_binding",
            Status::Passed,
            format!("bound to verified manifest SHA-256 {manifest_canonical_sha}"),
        )
        .with_evidence(
            Some("bound".into()),
            Some(manifest_canonical_sha.clone()),
            Some(manifest_canonical_sha.clone()),
            None,
        )
    } else {
        Check::new(
            "manifest_binding",
            Status::Failed,
            "contain.environment.json changed or latest build did not pass",
        )
        .with_evidence(
            Some("manifest_changed_since_build".into()),
            Some(attestation.manifest_canonical_sha256.clone()),
            Some(manifest_canonical_sha.clone()),
            Some(
                "项目构建清单已变更或尚未完成有效构建；请先点击“重新检查”并执行“开始构建”。".into(),
            ),
        )
    });

    // 2. Verified publish directory & freshness check
    let mut verified_publish_dir: Option<(String, PathBuf)> = None;
    let output_check = match (
        &attestation.publish_rel_dir,
        &attestation.resolved_publish_dir,
    ) {
        (Some(rel_dir), Some(resolved_str)) if safe_name(rel_dir) => {
            let expected_rel = inst_spec
                .publish_dir
                .as_deref()
                .or(manifest.artifact_dir.as_deref());
            if expected_rel != Some(rel_dir.as_str()) {
                Check::new(
                    "verified_output_binding",
                    Status::Failed,
                    "declared publish_dir/artifact_dir differs from attested build output",
                )
                .with_evidence(
                    Some("manifest_changed_since_build".into()),
                    expected_rel.map(str::to_string),
                    Some(rel_dir.clone()),
                    Some("发布目录声明已变更，请点击“再次构建”重新生成产物。".into()),
                )
            } else {
                let resolved_path = PathBuf::from(resolved_str);
                let within_project = is_strict_subpath(&abs_project, &resolved_path);
                if !within_project || !resolved_path.is_dir() {
                    Check::new(
                        "verified_output_binding",
                        Status::Failed,
                        "publish output directory missing or overlaps project root",
                    )
                    .with_evidence(
                        Some("artifacts_modified_or_stale".into()),
                        Some(rel_dir.clone()),
                        None,
                        Some("未找到已验证的独立发布子目录（不能直接打包项目根目录），请点击“再次构建”。".into()),
                    )
                } else if attestation.files.is_empty() {
                    Check::new(
                        "verified_output_binding",
                        Status::Failed,
                        "verified publish directory contains no files",
                    )
                    .with_evidence(
                        Some("artifacts_modified_or_stale".into()),
                        Some(rel_dir.clone()),
                        Some("0 files".into()),
                        Some("上次构建未在发布目录中留下已验证产物，请点击“再次构建”。".into()),
                    )
                } else if let Some(bad) = attestation
                    .files
                    .iter()
                    .find(|f| is_forbidden_payload_file(&f.path))
                {
                    Check::new(
                        "verified_output_binding",
                        Status::Failed,
                        format!("publish directory contains non-distributable file: {}", bad.path),
                    )
                    .with_evidence(
                        Some("forbidden_payload_member".into()),
                        Some(rel_dir.clone()),
                        Some(bad.path.clone()),
                        Some(format!(
                            "发布目录中包含不应随安装包分发的项目配置、日志或数据库文件（{}）；请使用干净独立的发布输出子目录（如 artifacts/publish）重新构建。",
                            bad.path
                        )),
                    )
                } else {
                    match scan_directory_files(&resolved_path) {
                        Ok(current_files) if current_files == attestation.files => {
                            verified_publish_dir = Some((rel_dir.clone(), resolved_path));
                            Check::new(
                                "verified_output_binding",
                                Status::Passed,
                                format!(
                                    "verified {} unchanged file(s) in {rel_dir}",
                                    current_files.len()
                                ),
                            )
                            .with_evidence(
                                Some("fresh_verified_output".into()),
                                Some(rel_dir.clone()),
                                Some(format!("{} files", current_files.len())),
                                None,
                            )
                        }
                        Ok(_) => Check::new(
                            "verified_output_binding",
                            Status::Failed,
                            "publish directory files changed after the last verified build",
                        )
                        .with_evidence(
                            Some("artifacts_modified_or_stale".into()),
                            Some(rel_dir.clone()),
                            None,
                            Some("发布目录中的文件在构建完成后被修改、替换或增删；为避免打入旧文件或未验证产物，请点击“再次构建”后再生成安装包。".into()),
                        ),
                        Err(e) => Check::new(
                            "verified_output_binding",
                            Status::Failed,
                            format!("failed to verify publish directory contents: {e}"),
                        )
                        .with_evidence(
                            Some("artifacts_modified_or_stale".into()),
                            Some(rel_dir.clone()),
                            None,
                            Some("无法核实发布目录文件完整性（请确保不含符号链接），请重新构建。".into()),
                        ),
                    }
                }
            }
        }
        _ => {
            let cmd = build_dotnet_publish_guidance(&csproj, "artifacts/publish");
            Check::new(
                "verified_output_binding",
                Status::Failed,
                "no verified publish subdirectory declared",
            )
            .with_evidence(
                Some("missing_publish_dir".into()),
                Some("artifact_dir / installer.publish_dir".into()),
                None,
                Some(format!(
                    "请在 contain.environment.json 中声明独立的发布输出子目录（如 \"artifact_dir\": \"artifacts/publish\"），并使用自包含发布命令构建：{cmd}"
                )),
            )
        }
    };
    report.checks.push(output_check);

    // 3. Entry resolution + .NET win-x64 self-contained multi-file publish verification
    let mut resolved_entry: Option<(String, String)> = None; // (entry_rel_posix, entry_sha256)
    if let Some((ref rel_dir, ref publish_dir_path)) = verified_publish_dir {
        let entry_candidate: std::result::Result<String, (&'static str, String, String)> =
            if let Some(ref explicit_entry) = inst_spec.entry {
                if attestation.files.iter().any(|f| f.path == *explicit_entry) {
                    Ok(explicit_entry.clone())
                } else {
                    Err((
                        "missing_entry",
                        format!(
                            "在已验证的发布目录 {rel_dir} 中未找到声明的入口程序“{explicit_entry}”。"
                        ),
                        format!(
                            "请核对 contain.environment.json 中 installer.entry 的文件名是否与实际发布生成的 .exe 一致（位于 {rel_dir} 内）。"
                        ),
                    ))
                }
            } else {
                let exe_candidates: Vec<&AttestedFile> = attestation
                    .files
                    .iter()
                    .filter(|f| {
                        let lower = f.path.to_ascii_lowercase();
                        let file_only = lower.rsplit('/').next().unwrap_or(&lower);
                        lower.ends_with(".exe") && file_only != "createdump.exe"
                    })
                    .collect();
                match exe_candidates.as_slice() {
                    [single] => Ok(single.path.clone()),
                    [] => Err((
                        "missing_entry",
                        format!("发布目录 {rel_dir} 中未找到任何应用程序 .exe 入口文件。"),
                        format!(
                            "请确认使用 win-x64 自包含发布生成可执行程序，并在 contain.environment.json 的 installer.entry 中声明主程序文件名（例如 \"entry\": \"App.exe\"）。参考命令：{}",
                            build_dotnet_publish_guidance(&csproj, rel_dir)
                        ),
                    )),
                    many => {
                        let names = many
                            .iter()
                            .take(5)
                            .map(|f| f.path.as_str())
                            .collect::<Vec<_>>()
                            .join("、");
                        Err((
                            "ambiguous_entry",
                            format!("发布目录中存在多个可执行文件（{names}），启动入口有歧义。"),
                            "请在 contain.environment.json 中增加 \"installer\": { \"entry\": \"主程序名.exe\", ... } 明确指定唯一启动入口。".into(),
                        ))
                    }
                }
            };

        match entry_candidate {
            Ok(entry_rel) => {
                let entry_sha = attestation
                    .files
                    .iter()
                    .find(|f| f.path == entry_rel)
                    .map(|f| f.sha256.clone())
                    .unwrap_or_default();
                report.checks.push(
                    Check::new(
                        "installer_entry",
                        Status::Passed,
                        format!("entry {entry_rel} ({entry_sha})"),
                    )
                    .with_evidence(
                        Some("entry_resolved".into()),
                        inst_spec.entry.clone().or_else(|| Some(entry_rel.clone())),
                        Some(entry_rel.clone()),
                        None,
                    ),
                );

                if let Some(ref pk) = inst_spec.publish_kind
                    && pk != SUPPORTED_PUBLISH_KIND
                {
                    let cmd = build_dotnet_publish_guidance(&csproj, rel_dir);
                    report.checks.push(
                        Check::new(
                            "dotnet_self_contained_publish",
                            Status::Failed,
                            format!("unsupported publish_kind '{pk}'"),
                        )
                        .with_evidence(
                            Some("unsupported_publish_kind".into()),
                            Some(SUPPORTED_PUBLISH_KIND.into()),
                            Some(pk.clone()),
                            Some(format!(
                                "当前仅支持 \"{SUPPORTED_PUBLISH_KIND}\"（Windows x64 .NET 自包含多文件发布）；参考命令：{cmd}"
                            )),
                        ),
                    );
                } else {
                    match verify_dotnet_self_contained_multifile(
                        publish_dir_path,
                        &entry_rel,
                        &csproj,
                        rel_dir,
                    ) {
                        Ok(()) => {
                            resolved_entry = Some((entry_rel.clone(), entry_sha));
                            report.checks.push(
                                Check::new(
                                    "dotnet_self_contained_publish",
                                    Status::Passed,
                                    format!(
                                        "verified Windows x64 self-contained multi-file .NET publish for {entry_rel}"
                                    ),
                                )
                                .with_evidence(
                                    Some("self_contained_verified".into()),
                                    Some(SUPPORTED_PUBLISH_KIND.into()),
                                    Some(SUPPORTED_PUBLISH_KIND.into()),
                                    None,
                                ),
                            );
                        }
                        Err((code, detail, remedy)) => {
                            report.checks.push(
                                Check::new("dotnet_self_contained_publish", Status::Failed, detail)
                                    .with_evidence(
                                        Some(code.into()),
                                        Some(SUPPORTED_PUBLISH_KIND.into()),
                                        None,
                                        Some(remedy),
                                    ),
                            );
                        }
                    }
                }
            }
            Err((code, detail, remedy)) => {
                report.checks.push(
                    Check::new("installer_entry", Status::Failed, detail).with_evidence(
                        Some(code.into()),
                        inst_spec.entry.clone(),
                        None,
                        Some(remedy),
                    ),
                );
                report.checks.push(
                    Check::new(
                        "dotnet_self_contained_publish",
                        Status::NotRun,
                        "requires unambiguous executable entry first",
                    )
                    .with_evidence(
                        Some("step_not_run".into()),
                        None,
                        None,
                        None,
                    ),
                );
            }
        }
    } else {
        report.checks.push(
            Check::new(
                "installer_entry",
                Status::NotRun,
                "requires fresh verified publish output first",
            )
            .with_evidence(Some("step_not_run".into()), None, None, None),
        );
        report.checks.push(
            Check::new(
                "dotnet_self_contained_publish",
                Status::NotRun,
                "requires fresh verified publish output first",
            )
            .with_evidence(Some("step_not_run".into()), None, None, None),
        );
    }

    // 4. Version resolution (must be explicitly declared in installer.version or .csproj <Version>)
    let mut resolved_versions: Option<(String, String)> = None;
    let version_result: std::result::Result<(String, String), (&'static str, String, String)> =
        match (&inst_spec.version, &csproj.explicit_version) {
            (Some(mv), Some(cv)) if mv != cv => Err((
                "conflicting_version",
                format!(
                    "contain.environment.json 的 installer.version（{mv}）与 .csproj 的 <Version>（{cv}）不一致。"
                ),
                "请将两处版本号统一为同一个版本（或仅保留其中一处明确声明）。".into(),
            )),
            (Some(v), _) | (None, Some(v)) => match convert_to_four_part_version(v) {
                Ok(four) => Ok((v.clone(), four)),
                Err(e) => Err((
                    "invalid_version",
                    format!("应用版本号“{v}”格式不合法：{e}"),
                    "请使用标准语义化版本号（例如 \"0.1.0\" 或 \"1.0.0\"）。".into(),
                )),
            },
            (None, None) => Err((
                "missing_version",
                "项目未明确声明应用版本号（缺少 installer.version 或 .csproj 中的 <Version>）。".into(),
                "请在 contain.environment.json 的 \"installer\" 中添加 \"version\": \"0.1.0\"，或在项目 .csproj 的 <PropertyGroup> 中添加 <Version>0.1.0</Version>。".into(),
            )),
        };

    match version_result {
        Ok((app_ver, four_ver)) => {
            report.checks.push(
                Check::new(
                    "installer_version",
                    Status::Passed,
                    format!("version {app_ver} (PE VersionInfo {four_ver})"),
                )
                .with_evidence(
                    Some("version_verified".into()),
                    Some(app_ver.clone()),
                    Some(app_ver.clone()),
                    None,
                ),
            );
            resolved_versions = Some((app_ver, four_ver));
        }
        Err((code, detail, remedy)) => {
            report.checks.push(
                Check::new("installer_version", Status::Failed, detail).with_evidence(
                    Some(code.into()),
                    Some("explicit version".into()),
                    None,
                    Some(remedy),
                ),
            );
        }
    }

    // 5. Project identity, display name, filename & template safety check
    let raw_display = inst_spec
        .app_name
        .as_deref()
        .or(csproj.product_name.as_deref())
        .or(csproj.assembly_name.as_deref())
        .unwrap_or(&project_folder_name);
    let entry_stem_fallback = resolved_entry
        .as_ref()
        .and_then(|(e, _)| Path::new(e).file_stem().and_then(|s| s.to_str()))
        .unwrap_or(&project_folder_name);

    let existing_id_check = inspect_existing_identity(&abs_project, inst_spec.app_id.as_deref());
    let mut validated_naming: Option<(String, String)> = None; // (display_name, setup_file_stem)

    match (
        existing_id_check,
        validate_display_name(raw_display).and_then(|dn| {
            let stem = derive_and_validate_setup_file_stem(&dn, entry_stem_fallback)?;
            let _ = escape_iss_setup_value(&dn)?;
            if let Some(ref pub_name) = inst_spec.publisher {
                let _ = escape_iss_setup_value(pub_name)?;
            }
            Ok((dn, stem))
        }),
    ) {
        (Err((code, msg)), _) => {
            report.checks.push(
                Check::new("project_identity_and_naming", Status::Failed, msg.clone())
                    .with_evidence(Some(code.into()), None, None, Some(msg)),
            );
        }
        (Ok(_), Err(e)) => {
            let msg = e.to_string();
            report.checks.push(
                Check::new("project_identity_and_naming", Status::Failed, msg.clone())
                    .with_evidence(
                        Some("invalid_naming_or_template_param".into()),
                        None,
                        None,
                        Some(format!("请检查项目名称或发布者字段：{msg}")),
                    ),
            );
        }
        (Ok(_), Ok((dn, stem))) => {
            validated_naming = Some((dn.clone(), stem.clone()));
            report.checks.push(
                Check::new(
                    "project_identity_and_naming",
                    Status::Passed,
                    format!("display name '{dn}', installer stem '{stem}-Setup-x64.exe'"),
                )
                .with_evidence(
                    Some("naming_verified".into()),
                    None,
                    Some(format!("{stem}-Setup-x64.exe")),
                    None,
                ),
            );
        }
    }

    // 6. Inno Setup compiler availability check
    let resolved_iscc = resolve_iscc_executable(explicit_iscc);
    match &resolved_iscc {
        Some(iscc_path) => {
            let ver = query_inno_setup_version(iscc_path);
            report.checks.push(
                Check::new(
                    "inno_setup_compiler",
                    Status::Passed,
                    format!("Inno Setup compiler available (version {ver})"),
                )
                .with_evidence(
                    Some("compiler_available".into()),
                    Some("Inno Setup 6 (ISCC.exe)".into()),
                    Some(ver),
                    None,
                ),
            );
        }
        None => {
            report.checks.push(
                Check::new(
                    "inno_setup_compiler",
                    Status::Failed,
                    "Inno Setup 6 compiler (ISCC.exe) was not found on this machine",
                )
                .with_evidence(
                    Some("inno_setup_missing".into()),
                    Some("Inno Setup 6 (ISCC.exe)".into()),
                    None,
                    Some(
                        "本机未安装打包编译器 Inno Setup 6（ISCC.exe）。Inno Setup 仅在制作安装包的开发电脑上需要，收到安装包的最终用户无需安装。可执行 winget install --exact --id JRSoftware.InnoSetup --scope user 或前往官方页面 https://jrsoftware.org/isdl.php 安装后重试。"
                            .into(),
                    ),
                ),
            );
        }
    }

    // Stop before creating output_dir or mutating contain.installer.json if any prerequisite failed
    if !report.passed() {
        report.checks.push(
            Check::new(
                "installer_compile",
                Status::NotRun,
                "skipped because one or more packaging prerequisites did not pass",
            )
            .with_evidence(Some("step_not_run".into()), None, None, None),
        );
        report.write(report_dir)?;
        return Ok((report, None));
    }

    let (publish_rel_dir, publish_dir_path) = verified_publish_dir.expect("verified above");
    let (entry_rel_posix, entry_sha256) = resolved_entry.expect("verified above");
    let (app_version, version_info_version) = resolved_versions.expect("verified above");
    let (display_name, setup_file_stem) = validated_naming.expect("verified above");
    let iscc_exe = resolved_iscc.expect("verified above");
    let iscc_version = query_inno_setup_version(&iscc_exe);

    let (app_id, identity_source) =
        resolve_or_persist_identity(&abs_project, inst_spec.app_id.as_deref())?;
    let short_id = app_id[..8].to_string();
    let (install_dir_leaf, shortcut_name) =
        derive_and_validate_install_paths(&display_name, &setup_file_stem, &short_id)?;
    let output_base_filename = format!("{setup_file_stem}-Setup-x64");

    // Stage verified payload files into a clean temporary directory and re-verify hashes
    let stage = TempStageDir::new()?;
    let staged_payload = stage.0.join("payload");
    let staged_iss_dir = stage.0.join("iss");
    fs::create_dir_all(&staged_payload)?;
    fs::create_dir_all(&staged_iss_dir)?;

    for item in &attestation.files {
        let src = publish_dir_path.join(item.path.replace('/', std::path::MAIN_SEPARATOR_STR));
        let dst = staged_payload.join(item.path.replace('/', std::path::MAIN_SEPARATOR_STR));
        if let Some(parent) = dst.parent() {
            fs::create_dir_all(parent)?;
        }
        let bytes = fs::read(&src)?;
        ensure!(
            bytes.len() as u64 == item.bytes && sha256(&bytes) == item.sha256,
            "publish file '{}' changed during staging",
            item.path
        );
        fs::write(&dst, &bytes)?;
    }

    let abs_output_dir =
        std::path::absolute(output_dir).unwrap_or_else(|_| output_dir.to_path_buf());
    let iss_text = render_project_iss(
        &app_id,
        &short_id,
        &display_name,
        &app_version,
        &version_info_version,
        inst_spec.publisher.as_deref(),
        &install_dir_leaf,
        &shortcut_name,
        &entry_rel_posix,
        &staged_payload,
        &abs_output_dir,
        &output_base_filename,
    )?;

    let staged_isl_path = staged_iss_dir.join("ChineseSimplified.isl");
    let staged_iss_path = staged_iss_dir.join("project-installer.iss");
    write_utf8_bom(&staged_isl_path, CHINESE_SIMPLIFIED_ISL)?;
    write_utf8_bom(&staged_iss_path, &iss_text)?;

    fs::create_dir_all(&abs_output_dir)?;
    let compile_out = Command::new(&iscc_exe)
        .arg("/Qp")
        .arg(&staged_iss_path)
        .output();

    match compile_out {
        Ok(out) if out.status.success() => {
            let installer_file_name = format!("{output_base_filename}.exe");
            let installer_path = abs_output_dir.join(&installer_file_name);
            if !installer_path.is_file() {
                let _ = fs::remove_dir_all(&abs_output_dir);
                report.checks.push(
                    Check::new(
                        "installer_compile",
                        Status::Failed,
                        "ISCC exited 0 but expected Setup EXE was not produced",
                    )
                    .with_evidence(
                        Some("iscc_compile_failed".into()),
                        Some(installer_file_name),
                        None,
                        Some("Inno Setup 未生成预期的安装包文件，请检查磁盘空间或杀毒软件拦截后重试。".into()),
                    ),
                );
                report.write(report_dir)?;
                return Ok((report, None));
            }

            let setup_bytes = fs::read(&installer_path)?;
            if setup_bytes.get(..2) != Some(b"MZ") {
                let _ = fs::remove_dir_all(&abs_output_dir);
                bail!("compiled Setup EXE is not a valid Windows PE executable");
            }
            let installer_bytes = setup_bytes.len() as u64;
            let installer_sha256 = sha256(&setup_bytes);
            let (authenticode_status, signed) = query_authenticode_status(&installer_path);

            fs::write(
                abs_output_dir.join(format!("{output_base_filename}.sha256")),
                format!("{installer_sha256} *{installer_file_name}\n"),
            )?;

            let default_install_dir = format!("%LOCALAPPDATA%\\Programs\\{install_dir_leaf}");
            let start_menu_shortcut = format!("{shortcut_name}.lnk");
            let receipt = ProjectInstallerReceipt {
                schema_version: 1,
                protocol: "contain-project-installer-receipt-1".into(),
                project_name: project_folder_name,
                display_name: display_name.clone(),
                app_id: app_id.clone(),
                short_id: short_id.clone(),
                identity_source,
                app_version: app_version.clone(),
                version_info_version: version_info_version.clone(),
                publisher: inst_spec.publisher.clone(),
                publish_kind: SUPPORTED_PUBLISH_KIND.into(),
                publish_rel_dir: publish_rel_dir.clone(),
                entry: entry_rel_posix.clone(),
                entry_sha256: entry_sha256.clone(),
                manifest_sha256: manifest_canonical_sha,
                attestation_sha256: attestation_sha,
                payload_file_count: attestation.files.len(),
                install_scope: "current_user_lowest_privilege".into(),
                default_install_dir: default_install_dir.clone(),
                start_menu_shortcut: start_menu_shortcut.clone(),
                installer_file: installer_file_name.clone(),
                installer_bytes,
                installer_sha256: installer_sha256.clone(),
                authenticode_status: authenticode_status.clone(),
                signed,
                inno_setup_version: iscc_version.clone(),
            };

            let receipt_json = serde_json::to_vec_pretty(&receipt)?;
            fs::write(abs_output_dir.join("installer-receipt.json"), &receipt_json)?;

            let publisher_display = receipt
                .publisher
                .as_deref()
                .unwrap_or("未声明（不默认填充第三方发布者）");
            let summary_md = format!(
                "# {display_name} Windows 安装包生成收据\n\n\
- 安装包文件：`{installer_file_name}`\n\
- 文件大小：`{installer_bytes}` 字节\n\
- SHA-256：`{installer_sha256}`\n\
- 代码签名状态：`{authenticode_status}`\n\
- 应用显示名：`{display_name}`\n\
- 应用版本：`{app_version}`（PE VersionInfo：`{version_info_version}`）\n\
- 发布者：`{publisher_display}`\n\
- 稳定安装身份（AppId）：`{{{app_id}}}`\n\
- 默认安装目录：`{default_install_dir}`\n\
- 开始菜单快捷方式：`{start_menu_shortcut}`\n\
- 启动入口：`{entry_rel_posix}`（SHA-256：`{entry_sha256}`）\n\
- 产物来源：`{publish_rel_dir}`（Windows x64 .NET 自包含多文件发布，共 `{}` 个文件）\n\
- 打包编译器：Inno Setup `{iscc_version}`\n",
                attestation.files.len()
            );
            fs::write(abs_output_dir.join("installer-summary.md"), summary_md)?;

            report.checks.push(
                Check::new(
                    "installer_compile",
                    Status::Passed,
                    format!(
                        "compiled {installer_file_name} ({installer_bytes} bytes, SHA-256 {installer_sha256}, signature {authenticode_status})"
                    ),
                )
                .with_evidence(
                    Some("installer_compiled".into()),
                    Some(installer_file_name),
                    Some(installer_sha256),
                    None,
                ),
            );
            report.write(report_dir)?;
            fs::write(report_dir.join("installer-receipt.json"), &receipt_json)?;
            Ok((report, Some(receipt)))
        }
        Ok(out) => {
            let _ = fs::remove_dir_all(&abs_output_dir);
            let err_tail = format!(
                "{}\n{}",
                String::from_utf8_lossy(&out.stdout),
                String::from_utf8_lossy(&out.stderr)
            );
            let compact: String = err_tail
                .lines()
                .rev()
                .filter(|l| !l.trim().is_empty())
                .take(4)
                .collect::<Vec<_>>()
                .into_iter()
                .rev()
                .collect::<Vec<_>>()
                .join(" | ");
            report.checks.push(
                Check::new(
                    "installer_compile",
                    Status::Failed,
                    format!("ISCC failed with exit code {:?}", out.status.code()),
                )
                .with_evidence(
                    Some("iscc_compile_failed".into()),
                    None,
                    out.status.code().map(|c| c.to_string()),
                    Some(if compact.is_empty() {
                        "Inno Setup 编译失败，请检查安装目录权限或应用文件名。".into()
                    } else {
                        format!("Inno Setup 编译失败：{compact}")
                    }),
                ),
            );
            report.write(report_dir)?;
            Ok((report, None))
        }
        Err(e) => {
            let _ = fs::remove_dir_all(&abs_output_dir);
            report.checks.push(
                Check::new(
                    "installer_compile",
                    Status::Failed,
                    format!("failed to launch ISCC: {e}"),
                )
                .with_evidence(
                    Some("iscc_compile_failed".into()),
                    None,
                    None,
                    Some(format!("无法启动 Inno Setup 编译器：{e}")),
                ),
            );
            report.write(report_dir)?;
            Ok((report, None))
        }
    }
}
