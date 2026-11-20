use crate::{
    artifact::{safe_name, sha256},
    manifest::{InstallerSpec, Manifest, Tool},
    project_installer,
    runner::Step,
};
use anyhow::{Context, Result, bail};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    fs,
    io::Write,
    path::{Path, PathBuf},
};

pub const DEFAULT_ONBOARDING_PUBLISH_DIR: &str = "target/workflow/publish/win-x64";
pub const DEFAULT_SUGGESTED_APP_VERSION: &str = "0.1.0";

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OnboardingEvidenceField {
    pub label: String,
    pub value: String,
    pub source: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DotnetOnboardingInspection {
    pub schema_version: u32,
    pub protocol: String,
    pub status: String,
    pub source_fingerprint_sha256: String,
    pub has_existing_installer_identity: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub existing_app_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub csproj_file: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub app_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub assembly_name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub entry_executable: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub target_framework: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub app_version: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub version_source: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sdk_requirement_summary: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sdk_source: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub host_detected_compatible_sdks: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub packager_detected: Option<bool>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub matching_generated_manifest_sha256: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub publish_dir: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub publish_command: Option<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub evidence_fields: Vec<OnboardingEvidenceField>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub proposed_manifest: Option<Manifest>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub unsupported_code: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub unsupported_title: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub unsupported_reason: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub actionable_next_step: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DotnetOnboardingApplyResult {
    pub schema_version: u32,
    pub protocol: String,
    pub status: String,
    pub wrote_file: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub saved_manifest_filename: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub saved_manifest_sha256: Option<String>,
    pub inspection: DotnetOnboardingInspection,
}

#[derive(Debug, Clone)]
struct RawPropertyEntry {
    value: String,
    conditional: bool,
    dynamic_expr: bool,
}

#[derive(Debug, Clone, Default)]
struct ParsedCsproj {
    sdk_attr: Option<String>,
    has_import: bool,
    has_project_reference: bool,
    properties: BTreeMap<String, Vec<RawPropertyEntry>>,
}

/// Purely read-only inspection of a folder for C# console and WPF onboarding.
/// Never executes project code, MSBuild targets, or `dotnet restore`, and never writes files.
pub fn inspect_project(project_root: &Path) -> Result<DotnetOnboardingInspection> {
    if !project_root.is_dir() {
        bail!("project directory does not exist");
    }

    let existing_id = match project_installer::inspect_existing_identity(project_root, None) {
        Ok(Some((id, _))) => Some(id),
        _ => None,
    };

    let manifest_path = project_root.join("contain.environment.json");
    if manifest_path.is_file() {
        let bytes = fs::read(&manifest_path)?;
        let raw_sha = sha256(&bytes);
        if let Ok(mut preview) = inspect_project_files(project_root, existing_id.clone())
            && let Some(generated) = &preview.proposed_manifest
            && generated_manifest_bytes(generated)? == bytes
        {
            preview.status = "existing_manifest".into();
            preview.source_fingerprint_sha256 = raw_sha.clone();
            preview.matching_generated_manifest_sha256 = Some(raw_sha);
            preview.proposed_manifest = None;
            return Ok(preview);
        }
        return Ok(DotnetOnboardingInspection {
            schema_version: 1,
            protocol: "contain-dotnet-onboarding-1".into(),
            status: "existing_manifest".into(),
            source_fingerprint_sha256: raw_sha,
            has_existing_installer_identity: existing_id.is_some(),
            existing_app_id: existing_id,
            csproj_file: None,
            app_name: None,
            assembly_name: None,
            entry_executable: None,
            target_framework: None,
            app_version: None,
            version_source: None,
            sdk_requirement_summary: None,
            sdk_source: None,
            host_detected_compatible_sdks: vec![],
            packager_detected: Some(project_installer::resolve_iscc_executable(None).is_some()),
            matching_generated_manifest_sha256: None,
            publish_dir: None,
            publish_command: None,
            evidence_fields: vec![],
            proposed_manifest: None,
            unsupported_code: None,
            unsupported_title: None,
            unsupported_reason: None,
            actionable_next_step: None,
        });
    }

    inspect_project_files(project_root, existing_id)
}

fn inspect_project_files(
    project_root: &Path,
    existing_id: Option<String>,
) -> Result<DotnetOnboardingInspection> {
    let mut root_csprojs: Vec<(String, PathBuf)> = Vec::new();
    let mut root_other_dotnet_projs: Vec<String> = Vec::new();
    let mut root_slns: Vec<String> = Vec::new();
    let mut build_files = Vec::new();

    for entry in fs::read_dir(project_root)? {
        let entry = entry?;
        let path = entry.path();
        if !path.is_file() {
            continue;
        }
        let Some(name) = path.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        let lower = name.to_ascii_lowercase();
        if lower == "directory.build.props" || lower == "directory.build.targets" {
            build_files.push((0, path));
        } else if lower.ends_with(".csproj") {
            root_csprojs.push((name.to_string(), path));
        } else if lower.ends_with(".fsproj") || lower.ends_with(".vbproj") {
            root_other_dotnet_projs.push(name.to_string());
        } else if lower.ends_with(".sln") || lower.ends_with(".slnx") {
            root_slns.push(name.to_string());
        }
    }
    root_csprojs.sort_by(|a, b| a.0.cmp(&b.0));
    root_other_dotnet_projs.sort();
    root_slns.sort();
    let absolute_root = std::path::absolute(project_root)?;
    collect_inherited_build_files(&absolute_root, &mut build_files)?;
    // A linked project directory can have a different physical parent with build overrides.
    let resolved_root = project_root.canonicalize()?;
    if resolved_root != absolute_root {
        collect_inherited_build_files(&resolved_root, &mut build_files)?;
    }
    build_files.sort();

    let fingerprint = compute_source_fingerprint(
        project_root,
        &root_csprojs,
        &root_other_dotnet_projs,
        &root_slns,
        &build_files,
    )?;

    if root_csprojs.len() > 1 {
        let names = root_csprojs
            .iter()
            .take(5)
            .map(|(n, _)| n.as_str())
            .collect::<Vec<_>>()
            .join("、");
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "multiple_csproj_in_root",
            format!("这个目录包含 {} 个 C# 项目文件", root_csprojs.len()),
            format!("当前文件夹根部同时存在多个 .csproj（{names}），Contain 不会默认挑选第一个。"),
            "请把要发布的应用放在独立文件夹并重新选择该文件夹，或手动编写 contain.environment.json 明确指定构建项目与入口。",
        ));
    }

    if root_csprojs.is_empty() {
        if !root_other_dotnet_projs.is_empty() {
            let names = root_other_dotnet_projs.join("、");
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "non_csharp_dotnet_project",
                "暂不支持自动配置非 C# 的 .NET 项目".into(),
                format!("检测到项目文件 {names}，自动配置支持 C#（.csproj）控制台和 WPF 应用。"),
                "请选择 C# 应用所在文件夹，或为当前项目手动添加 contain.environment.json。",
            ));
        }

        let child_csprojs = scan_child_csprojs(project_root, 3, 64);
        if child_csprojs.len() > 1 {
            let sample = child_csprojs
                .iter()
                .take(4)
                .cloned()
                .collect::<Vec<_>>()
                .join("、");
            let first_dir = Path::new(&child_csprojs[0])
                .parent()
                .map(|p| p.to_string_lossy().replace('/', "\\"))
                .unwrap_or_default();
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "multiple_child_projects",
                format!("这个目录包含 {} 个子目录 C# 项目", child_csprojs.len()),
                format!(
                    "所选根目录本身没有 .csproj，但在子目录中发现多个项目（{sample}），无法自动决定发布哪一个。"
                ),
                &format!(
                    "请点击右上角“选择文件夹”，直接选择要发布的应用所在文件夹（例如“{first_dir}”）。"
                ),
            ));
        } else if child_csprojs.len() == 1 {
            let child_rel = &child_csprojs[0];
            let child_dir = Path::new(child_rel)
                .parent()
                .map(|p| p.to_string_lossy().replace('/', "\\"))
                .unwrap_or_default();
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "csproj_in_subdirectory",
                "所选根目录未找到 .csproj（项目位于子目录）".into(),
                format!("在子目录中发现 {child_rel}，首版仅识别所选目录根部的单个 C# 项目。"),
                &format!("请点击“选择文件夹”，直接选择包含该 .csproj 的子文件夹“{child_dir}”。"),
            ));
        } else {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "no_csproj_found",
                "未在根目录找到 C# 项目文件 (.csproj)".into(),
                if root_slns.is_empty() {
                    "所选目录没有 contain.environment.json，也没有可识别的 .csproj 文件。".into()
                } else {
                    format!(
                        "检测到解决方案文件（{}），但未找到 C# 项目的 .csproj 文件。",
                        root_slns.join("、")
                    )
                },
                "请选择根目录包含单个 C# 应用项目 (.csproj) 的文件夹。",
            ));
        }
    }

    let (csproj_name, csproj_path) = &root_csprojs[0];
    if !root_other_dotnet_projs.is_empty() {
        let extra = root_other_dotnet_projs.join("、");
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "multiple_dotnet_projects_in_root",
            "这个目录同时包含多个 .NET 项目文件".into(),
            format!("除 {csproj_name} 外还检测到 {extra}，无法唯一确定发布对象。"),
            "请将要发布的 C# 应用放在独立文件夹后重新选择。",
        ));
    }

    if !build_files.is_empty() {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "directory_build_props_present",
            "项目目录或上级目录包含 Directory.Build.props / targets 外部构建属性".into(),
            "在不执行 MSBuild 的前提下，无法静态保证外部构建属性未改写目标框架、输出类型或程序集入口名。".into(),
            "请为该项目手动提供 contain.environment.json，或使用没有外部属性注入的独立应用项目目录。",
        ));
    }

    let raw_xml = match fs::read_to_string(csproj_path) {
        Ok(s) => s,
        Err(e) => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "unreadable_csproj",
                format!("无法读取 {csproj_name}"),
                format!("读取项目文件失败（可能不是有效 UTF-8 文本）：{e}"),
                "请确认 .csproj 为有效的 UTF-8 编码 XML 文件后重新选择。",
            ));
        }
    };

    let parsed = match parse_csproj_xml(&raw_xml) {
        Ok(p) => p,
        Err(reason) => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "malformed_csproj_xml",
                format!("无法解析 {csproj_name} 的 XML 结构"),
                reason,
                "请先修正 .csproj 的 XML 语法后重新选择文件夹。",
            ));
        }
    };

    if parsed.has_import {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "msbuild_import_present",
            format!("{csproj_name} 包含自定义 MSBuild <Import>"),
            "项目使用了外部 <Import>，在不运行 MSBuild 的只读模式下无法静态确定最终入口与构建行为。".into(),
            "请改为标准 SDK 风格应用项目配置，或手动提供 contain.environment.json。",
        ));
    }

    let Some(sdk) = parsed.sdk_attr.as_deref() else {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "non_sdk_style_csproj",
            format!("{csproj_name} 不是现代 SDK 风格项目"),
            "未检测到 <Project Sdk=\"Microsoft.NET.Sdk\">，可能为旧版 .NET Framework 项目格式。"
                .into(),
            "请先迁移为现代 Microsoft.NET.Sdk 应用项目，或手动编写 contain.environment.json。",
        ));
    };
    if !sdk.eq_ignore_ascii_case("Microsoft.NET.Sdk") {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "unsupported_project_sdk",
            format!("暂不支持 {sdk} 项目类型自动生成安装包配置"),
            format!(
                "{csproj_name} 声明的 SDK 为“{sdk}”，自动配置支持 Microsoft.NET.Sdk 控制台和 WPF 应用。"
            ),
            "请为此项目提供 contain.environment.json，或选择标准 Microsoft.NET.Sdk 应用目录。",
        ));
    }

    // A windowed executable can belong to several UI frameworks. Only opt in
    // to the WPF workflow when all framework switches are statically known.
    let mut use_wpf = false;
    for flag in ["UseWPF", "UseWindowsForms", "UseMaui", "UseWinUI"] {
        match resolve_single_static_property(&parsed, &flag.to_ascii_lowercase()) {
            PropState::Missing => {}
            PropState::Present(value) if value.eq_ignore_ascii_case("false") => {}
            PropState::Present(value) if value.eq_ignore_ascii_case("true") => {
                if flag == "UseWPF" {
                    use_wpf = true;
                } else {
                    return Ok(unsupported(
                        &fingerprint,
                        existing_id,
                        "gui_framework_project",
                        format!("{csproj_name} 启用了 {flag}"),
                        "自动配置支持控制台应用和单独使用 WPF 的桌面应用。".into(),
                        "请为此项目提供 contain.environment.json 来指定构建步骤。",
                    ));
                }
            }
            _ => {
                return Ok(unsupported(
                    &fingerprint,
                    existing_id,
                    "ambiguous_gui_framework",
                    format!("无法从 {csproj_name} 确定 {flag}"),
                    format!("自动配置需要 {flag} 为无条件的 true 或 false。"),
                    "请手动添加 contain.environment.json 来指定构建步骤。",
                ));
            }
        }
    }

    if use_wpf && parsed.has_project_reference {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "wpf_project_reference",
            format!("{csproj_name} 引用了其他项目"),
            "WPF 自动配置目前支持独立的单项目应用。".into(),
            "请为包含 ProjectReference 的应用提供 contain.environment.json 来指定构建步骤。",
        ));
    }

    if let Some(aot) = parsed.properties.get("publishaot")
        && aot
            .iter()
            .any(|e| e.conditional || e.dynamic_expr || e.value.eq_ignore_ascii_case("true"))
    {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "native_aot_unsupported",
            format!("{csproj_name} 启用了 NativeAOT (PublishAot)"),
            "当前首版打包校验针对标准 .NET 自包含多文件运行时（含 coreclr.dll），暂不支持 NativeAOT。".into(),
            "请移除 PublishAot 或在 contain.environment.json 中自行管理构建。",
        ));
    }

    // TargetFramework vs TargetFrameworks
    if let Some(tfms) = parsed.properties.get("targetframeworks") {
        let shown = tfms.first().map(|e| e.value.as_str()).unwrap_or("多目标");
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "multi_target_framework",
            format!("{csproj_name} 声明了多目标框架 (<TargetFrameworks>)"),
            format!(
                "检测到 <TargetFrameworks>{shown}</TargetFrameworks>，无法静态唯一确定要打包的目标框架。"
            ),
            "请改为单目标框架 <TargetFramework>（如 net10.0），或手动添加 contain.environment.json 在发布命令中指定 -f <目标框架>。",
        ));
    }

    let tfm = match resolve_single_static_property(&parsed, "targetframework") {
        PropState::Present(v) => v,
        PropState::Missing => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "missing_target_framework",
                format!("{csproj_name} 缺少 <TargetFramework> 声明"),
                "未找到静态的单个 <TargetFramework> 属性。".into(),
                "请在 .csproj 的 <PropertyGroup> 中明确声明单个目标框架（例如 <TargetFramework>net10.0</TargetFramework>）。",
            ));
        }
        PropState::DynamicOrConditional(reason) => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "dynamic_target_framework",
                format!("{csproj_name} 的 <TargetFramework> 无法静态确定"),
                reason,
                "请在 .csproj 中使用不带条件或 $(...) 变量的字面量 <TargetFramework>（如 net10.0），或手动编写 contain.environment.json。",
            ));
        }
    };

    let Some(tfm_major) = parse_supported_tfm(&tfm) else {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "unsupported_target_framework",
            format!("暂不支持目标框架“{tfm}”的自包含 Windows x64 打包"),
            format!(
                "{csproj_name} 声明 <TargetFramework>{tfm}</TargetFramework>；首版仅支持现代 .NET（如 net8.0、net9.0、net10.0）。"
            ),
            "请将项目升级到受支持的现代 .NET 单目标框架（如 net8.0 / net9.0 / net10.0），或选择现代 .NET 控制台项目。",
        ));
    };

    if use_wpf && !is_supported_wpf_tfm(&tfm, tfm_major) {
        return Ok(unsupported(
            &fingerprint,
            existing_id,
            "unsupported_wpf_target",
            format!("WPF 项目声明的目标框架为 {tfm}"),
            "WPF 自动配置需要 .NET 8 或更高版本的 Windows 专用目标框架。".into(),
            "请选择使用如 net8.0-windows 目标框架的 WPF 应用，或手动提供 contain.environment.json。",
        ));
    }

    match resolve_single_static_property(&parsed, "outputtype") {
        PropState::Present(v) if !use_wpf && v.eq_ignore_ascii_case("Exe") => {}
        PropState::Present(v) if use_wpf && v.eq_ignore_ascii_case("WinExe") => {}
        PropState::Present(v) if v.eq_ignore_ascii_case("Library") => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "class_library_project",
                format!("{csproj_name} 是类库项目（OutputType=Library）"),
                "类库生成供其他程序引用的 .dll。自动配置需要可独立启动的应用入口。".into(),
                "请选择包含可执行入口的控制台应用或 WPF 应用文件夹。",
            ));
        }
        PropState::Missing => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "class_library_project",
                format!("{csproj_name} 未声明可执行的 OutputType（默认为类库）"),
                "SDK 风格 C# 项目未声明 OutputType 时默认按类库（Library）编译，不会生成可独立启动的应用入口。".into(),
                "请选择包含明确 OutputType 声明的应用项目目录。",
            ));
        }
        PropState::Present(v) if v.eq_ignore_ascii_case("WinExe") => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "winexe_project",
                format!("{csproj_name} 声明为窗口程序（OutputType=WinExe）"),
                "此窗口程序缺少明确的 WPF 声明（UseWPF=true）。".into(),
                "请为该窗口程序手动提供 contain.environment.json。",
            ));
        }
        PropState::Present(v) => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "unsupported_output_type",
                format!("不支持的输出类型 OutputType={v}"),
                "可自动配置的组合为控制台 Exe 或 WPF WinExe。".into(),
                "请核对 .csproj 中的 <OutputType> 设置。",
            ));
        }
        PropState::DynamicOrConditional(reason) => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "dynamic_output_type",
                format!("{csproj_name} 的 <OutputType> 依赖条件或动态属性"),
                reason,
                "请在 .csproj 中明确声明应用的输出类型，或为此项目提供 contain.environment.json。",
            ));
        }
    }

    // AssemblyName resolution (must respect explicit AssemblyName when different from csproj stem!)
    let csproj_stem = Path::new(csproj_name)
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("App")
        .to_string();
    let (assembly_name, assembly_source_desc) = match resolve_single_static_property(
        &parsed,
        "assemblyname",
    ) {
        PropState::Present(v) => {
            if v.contains('/') || v.contains('\\') || !safe_name(&format!("{v}.exe")) {
                return Ok(unsupported(
                    &fingerprint,
                    existing_id,
                    "invalid_assembly_name",
                    format!("{csproj_name} 的 <AssemblyName> 包含非法文件名字符"),
                    format!(
                        "声明的 <AssemblyName>{v}</AssemblyName> 不能直接作为安全的 Windows .exe 文件名。"
                    ),
                    "请将 <AssemblyName> 修改为合法的文件名标识后重新选择。",
                ));
            }
            let diff_note = if v != csproj_stem {
                format!(
                    "依据 {csproj_name} 明确声明的 <AssemblyName>{v}</AssemblyName>（与项目文件名 {csproj_stem} 不同，入口采用实际程序集名）"
                )
            } else {
                format!("依据 {csproj_name} 明确声明的 <AssemblyName>{v}</AssemblyName>")
            };
            (v, diff_note)
        }
        PropState::Missing => {
            if !safe_name(&format!("{csproj_stem}.exe")) {
                return Ok(unsupported(
                    &fingerprint,
                    existing_id,
                    "invalid_csproj_stem",
                    "项目文件名包含无法生成合法 .exe 的字符".into(),
                    format!("{csproj_name} 无法映射为合法的可执行文件名。"),
                    "请在 .csproj 中明确添加合法的 <AssemblyName> 后重新选择。",
                ));
            }
            (
                csproj_stem.clone(),
                format!(
                    "项目未单独声明 <AssemblyName>，按 .NET 默认规则采用项目文件名 {csproj_stem}"
                ),
            )
        }
        PropState::DynamicOrConditional(reason) => {
            return Ok(unsupported(
                &fingerprint,
                existing_id,
                "dynamic_assembly_name",
                format!("{csproj_name} 的程序集入口名 (<AssemblyName>) 无法静态确定"),
                reason,
                "Contain 不会猜测动态入口名；请在 .csproj 中改为字面量 <AssemblyName>，或手动提供 contain.environment.json。",
            ));
        }
    };

    // Version resolution
    let (app_version, version_source_code, version_source_desc) =
        match resolve_single_static_property(&parsed, "version") {
            PropState::Present(v) => match project_installer::convert_to_four_part_version(&v) {
                Ok(_) => (
                    v.clone(),
                    "csproj_declared".to_string(),
                    format!("依据 {csproj_name} 明确声明的 <Version>{v}</Version>"),
                ),
                Err(e) => {
                    return Ok(unsupported(
                        &fingerprint,
                        existing_id,
                        "invalid_csproj_version",
                        format!("{csproj_name} 中的 <Version>{v}</Version> 格式不受支持"),
                        format!("{e}"),
                        "请在 .csproj 中使用标准数字版本号（如 0.1.0 或 1.0.0）后重新选择。",
                    ));
                }
            },
            PropState::Missing => (
                DEFAULT_SUGGESTED_APP_VERSION.to_string(),
                "suggested_default".to_string(),
                format!(
                    "建议初始版本 {DEFAULT_SUGGESTED_APP_VERSION}（{csproj_name} 未声明 <Version>，仅写入 contain.environment.json，不修改 .csproj）"
                ),
            ),
            PropState::DynamicOrConditional(reason) => {
                return Ok(unsupported(
                    &fingerprint,
                    existing_id,
                    "dynamic_version",
                    format!("{csproj_name} 的 <Version> 依赖动态属性或条件"),
                    reason,
                    "请在 .csproj 中使用字面量 <Version>（如 0.1.0），或移除动态表达式后由 Contain 提供建议版本。",
                ));
            }
        };

    // Display name resolution (<Product> if static and valid, otherwise assembly_name)
    let (app_name, app_name_source_desc) = match resolve_single_static_property(&parsed, "product")
    {
        PropState::Present(p) if project_installer::validate_display_name(&p).is_ok() => (
            p.trim().to_string(),
            format!("依据 {csproj_name} 声明的 <Product>{}</Product>", p.trim()),
        ),
        _ => (
            assembly_name.clone(),
            format!("采用程序集名 {assembly_name}"),
        ),
    };

    // global.json inspection + read-only host SDK directory check (zero subprocesses!)
    let global_json_path = project_root.join("global.json");
    let (dotnet_tool, sdk_req_summary, sdk_source_code, sdk_source_desc) = if global_json_path
        .is_file()
    {
        let g_val: serde_json::Value = match fs::read(&global_json_path)
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
        {
            Some(v) => v,
            None => {
                return Ok(unsupported(
                    &fingerprint,
                    existing_id,
                    "invalid_global_json",
                    "根目录中的 global.json 格式无效".into(),
                    "无法将 global.json 解析为合法的 JSON 对象。".into(),
                    "请修正或移除损坏的 global.json 后重新选择文件夹。",
                ));
            }
        };
        let pinned_ver = g_val
            .pointer("/sdk/version")
            .and_then(|v| v.as_str())
            .map(str::trim)
            .filter(|s| !s.is_empty());
        let roll_forward = g_val
            .pointer("/sdk/rollForward")
            .and_then(|v| v.as_str())
            .unwrap_or("");
        if let Some(ver) = pinned_ver {
            if roll_forward.eq_ignore_ascii_case("disable") {
                let tool = Tool {
                    program: "dotnet".into(),
                    version: ver.into(),
                    version_args: vec!["--version".into()],
                    remedy: format!(
                        "Install .NET SDK {ver} x64 from https://dotnet.microsoft.com/download/dotnet/{tfm_major}.0 (pinned by global.json with rollForward=disable)"
                    ),
                    version_policy: None,
                };
                (
                    tool,
                    format!(".NET SDK {ver}（精确固定）"),
                    "global_json_pinned".to_string(),
                    format!("依据项目 global.json 声明 sdk.version={ver}（rollForward=disable）"),
                )
            } else {
                let req_ver = format!("{tfm_major}.0");
                let tool = Tool {
                    program: "dotnet".into(),
                    version: req_ver.clone(),
                    version_args: vec!["--version".into()],
                    remedy: format!(
                        "Install .NET SDK {tfm_major}.0 x64 from https://dotnet.microsoft.com/download/dotnet/{tfm_major}.0"
                    ),
                    version_policy: Some("dotnet_major_or_newer".into()),
                };
                (
                    tool,
                    format!(".NET SDK {tfm_major}.0 及以上"),
                    "global_json_and_tfm".to_string(),
                    format!("项目声明 TargetFramework={tfm}（含允许前滚的 global.json {ver}）"),
                )
            }
        } else {
            let req_ver = format!("{tfm_major}.0");
            let tool = Tool {
                program: "dotnet".into(),
                version: req_ver,
                version_args: vec!["--version".into()],
                remedy: format!(
                    "Install .NET SDK {tfm_major}.0 x64 from https://dotnet.microsoft.com/download/dotnet/{tfm_major}.0"
                ),
                version_policy: Some("dotnet_major_or_newer".into()),
            };
            (
                tool,
                format!(".NET SDK {tfm_major}.0 及以上"),
                "target_framework_major".to_string(),
                format!(
                    "依据 {csproj_name} 的 <TargetFramework>{tfm}</TargetFramework>（不强制锁定特定 10.0.xxx 小版本）"
                ),
            )
        }
    } else {
        let req_ver = format!("{tfm_major}.0");
        let tool = Tool {
            program: "dotnet".into(),
            version: req_ver,
            version_args: vec!["--version".into()],
            remedy: format!(
                "Install .NET SDK {tfm_major}.0 x64 from https://dotnet.microsoft.com/download/dotnet/{tfm_major}.0"
            ),
            version_policy: Some("dotnet_major_or_newer".into()),
        };
        (
            tool,
            format!(".NET SDK {tfm_major}.0 及以上（兼容 {tfm}，不绑定特定补丁版本）"),
            "target_framework_major".to_string(),
            format!("项目仅声明 <TargetFramework>{tfm}</TargetFramework>（无 global.json）"),
        )
    };

    let exact_sdk = dotnet_tool
        .version_policy
        .is_none()
        .then_some(dotnet_tool.version.as_str());
    let host_compatible_sdks = detect_host_sdk_directories_readonly(tfm_major, exact_sdk);
    let entry_exe = format!("{assembly_name}.exe");
    let publish_dir = DEFAULT_ONBOARDING_PUBLISH_DIR.to_string();
    let publish_args = vec![
        "publish".to_string(),
        csproj_name.clone(),
        "-c".to_string(),
        "Release".to_string(),
        "-r".to_string(),
        "win-x64".to_string(),
        "--self-contained".to_string(),
        "true".to_string(),
        "-p:PublishSingleFile=false".to_string(),
        "--nologo".to_string(),
        "-o".to_string(),
        publish_dir.clone(),
    ];
    let publish_command = format!("dotnet {}", publish_args.join(" "));

    let mut artifacts = vec![
        entry_exe.clone(),
        format!("{assembly_name}.dll"),
        format!("{assembly_name}.deps.json"),
        format!("{assembly_name}.runtimeconfig.json"),
        "coreclr.dll".into(),
    ];
    let (application_type, type_source) = if use_wpf {
        artifacts.push("PresentationFramework.dll".into());
        (
            "WPF 桌面应用",
            format!("依据 {csproj_name} 声明的 OutputType=WinExe 和 UseWPF=true"),
        )
    } else {
        (
            "控制台应用 (OutputType=Exe)",
            format!("依据 {csproj_name} 声明的 <OutputType>Exe</OutputType>"),
        )
    };

    let proposed = Manifest {
        schema_version: 1,
        target: "windows-x64".into(),
        windows_toolchain: None,
        tools: vec![dotnet_tool],
        required_variables: vec![],
        build: vec![Step {
            name: format!("发布 {app_name} (Windows x64 自包含多文件)"),
            program: "dotnet".into(),
            args: publish_args,
            expected_exit: 0,
            stdout_sha256: None,
            timeout_ms: 300_000,
        }],
        verify: vec![],
        artifact_dir: Some(publish_dir.clone()),
        artifacts,
        installer: Some(InstallerSpec {
            app_id: None,
            app_name: Some(app_name.clone()),
            version: Some(app_version.clone()),
            publisher: None,
            publish_dir: Some(publish_dir.clone()),
            entry: Some(entry_exe.clone()),
            publish_kind: Some(project_installer::SUPPORTED_PUBLISH_KIND.into()),
        }),
        pending: vec![],
    };
    proposed
        .validate()
        .context("generated onboarding manifest failed schema validation")?;

    let evidence_fields = vec![
        OnboardingEvidenceField {
            label: "项目文件".into(),
            value: csproj_name.clone(),
            source: "根目录唯一 C# SDK 风格项目".into(),
        },
        OnboardingEvidenceField {
            label: "应用类型".into(),
            value: application_type.into(),
            source: type_source,
        },
        OnboardingEvidenceField {
            label: "目标框架与 SDK 要求".into(),
            value: format!("{tfm} → 检查 {sdk_req_summary}"),
            source: sdk_source_desc.clone(),
        },
        OnboardingEvidenceField {
            label: "启动入口文件".into(),
            value: entry_exe.clone(),
            source: assembly_source_desc,
        },
        OnboardingEvidenceField {
            label: "应用显示名".into(),
            value: app_name.clone(),
            source: app_name_source_desc,
        },
        OnboardingEvidenceField {
            label: "安装包版本号".into(),
            value: app_version.clone(),
            source: version_source_desc,
        },
        OnboardingEvidenceField {
            label: "独立发布输出目录".into(),
            value: publish_dir.clone(),
            source: "Contain 独立子目录（不修改 .csproj，不覆盖项目原有 bin/obj 输出）".into(),
        },
    ];

    Ok(DotnetOnboardingInspection {
        schema_version: 1,
        protocol: "contain-dotnet-onboarding-1".into(),
        status: "ready_to_confirm".into(),
        source_fingerprint_sha256: fingerprint,
        has_existing_installer_identity: existing_id.is_some(),
        existing_app_id: existing_id,
        csproj_file: Some(csproj_name.clone()),
        app_name: Some(app_name),
        assembly_name: Some(assembly_name),
        entry_executable: Some(entry_exe),
        target_framework: Some(tfm),
        app_version: Some(app_version),
        version_source: Some(version_source_code),
        sdk_requirement_summary: Some(sdk_req_summary),
        sdk_source: Some(sdk_source_code),
        host_detected_compatible_sdks: host_compatible_sdks,
        packager_detected: Some(project_installer::resolve_iscc_executable(None).is_some()),
        matching_generated_manifest_sha256: None,
        publish_dir: Some(publish_dir),
        publish_command: Some(publish_command),
        evidence_fields,
        proposed_manifest: Some(proposed),
        unsupported_code: None,
        unsupported_title: None,
        unsupported_reason: None,
        actionable_next_step: None,
    })
}

fn generated_manifest_bytes(manifest: &Manifest) -> Result<Vec<u8>> {
    let mut json_bytes = serde_json::to_vec_pretty(manifest)?;
    json_bytes.push(b'\n');
    Ok(json_bytes)
}

pub fn apply_onboarding(
    project_root: &Path,
    expected_fingerprint: Option<&str>,
) -> Result<DotnetOnboardingApplyResult> {
    let manifest_path = project_root.join("contain.environment.json");
    if manifest_path.is_file() {
        let inspection = inspect_project(project_root)?;
        return Ok(DotnetOnboardingApplyResult {
            schema_version: 1,
            protocol: "contain-dotnet-onboarding-apply-1".into(),
            status: "preserved_existing_manifest".into(),
            wrote_file: false,
            saved_manifest_filename: Some("contain.environment.json".into()),
            saved_manifest_sha256: None,
            inspection,
        });
    }

    let current = inspect_project(project_root)?;
    if let Some(exp) = expected_fingerprint
        && current.source_fingerprint_sha256 != exp
    {
        return Ok(DotnetOnboardingApplyResult {
            schema_version: 1,
            protocol: "contain-dotnet-onboarding-apply-1".into(),
            status: "source_changed_refresh_required".into(),
            wrote_file: false,
            saved_manifest_filename: None,
            saved_manifest_sha256: None,
            inspection: current,
        });
    }

    if current.status != "ready_to_confirm" {
        return Ok(DotnetOnboardingApplyResult {
            schema_version: 1,
            protocol: "contain-dotnet-onboarding-apply-1".into(),
            status: "unsupported".into(),
            wrote_file: false,
            saved_manifest_filename: None,
            saved_manifest_sha256: None,
            inspection: current,
        });
    }

    let manifest = current
        .proposed_manifest
        .as_ref()
        .context("missing proposed_manifest")?;
    manifest.validate()?;
    let json_bytes = generated_manifest_bytes(manifest)?;
    let manifest_sha256 = sha256(&json_bytes);

    match fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&manifest_path)
    {
        Ok(mut f) => {
            f.write_all(&json_bytes)?;
            f.flush()?;
            // Verify written manifest parses cleanly via Manifest::read
            let _ = Manifest::read(&manifest_path)?;
            Ok(DotnetOnboardingApplyResult {
                schema_version: 1,
                protocol: "contain-dotnet-onboarding-apply-1".into(),
                status: "applied".into(),
                wrote_file: true,
                saved_manifest_filename: Some("contain.environment.json".into()),
                saved_manifest_sha256: Some(manifest_sha256),
                inspection: current,
            })
        }
        Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
            let refreshed = inspect_project(project_root)?;
            Ok(DotnetOnboardingApplyResult {
                schema_version: 1,
                protocol: "contain-dotnet-onboarding-apply-1".into(),
                status: "preserved_existing_manifest".into(),
                wrote_file: false,
                saved_manifest_filename: Some("contain.environment.json".into()),
                saved_manifest_sha256: None,
                inspection: refreshed,
            })
        }
        Err(e) => Err(e).context("failed to write contain.environment.json"),
    }
}

fn unsupported(
    fingerprint: &str,
    existing_id: Option<String>,
    code: &str,
    title: String,
    reason: String,
    next_step: &str,
) -> DotnetOnboardingInspection {
    DotnetOnboardingInspection {
        schema_version: 1,
        protocol: "contain-dotnet-onboarding-1".into(),
        status: "unsupported".into(),
        source_fingerprint_sha256: fingerprint.to_string(),
        has_existing_installer_identity: existing_id.is_some(),
        existing_app_id: existing_id,
        csproj_file: None,
        app_name: None,
        assembly_name: None,
        entry_executable: None,
        target_framework: None,
        app_version: None,
        version_source: None,
        sdk_requirement_summary: None,
        sdk_source: None,
        host_detected_compatible_sdks: vec![],
        packager_detected: None,
        matching_generated_manifest_sha256: None,
        publish_dir: None,
        publish_command: None,
        evidence_fields: vec![],
        proposed_manifest: None,
        unsupported_code: Some(code.into()),
        unsupported_title: Some(title),
        unsupported_reason: Some(reason),
        actionable_next_step: Some(next_step.into()),
    }
}

fn collect_inherited_build_files(root: &Path, files: &mut Vec<(usize, PathBuf)>) -> Result<()> {
    // MSBuild searches upward independently for the nearest props and targets.
    for name in ["Directory.Build.props", "Directory.Build.targets"] {
        if files.iter().any(|(_, path)| {
            path.file_name()
                .is_some_and(|file| file.eq_ignore_ascii_case(name))
        }) {
            continue;
        }
        for (depth, ancestor) in root.ancestors().enumerate().skip(1) {
            let path = ancestor.join(name);
            match fs::metadata(&path) {
                Ok(metadata) if metadata.is_file() => {
                    files.push((depth, path));
                    break;
                }
                Err(e) if e.kind() != std::io::ErrorKind::NotFound => return Err(e.into()),
                _ => {}
            }
        }
    }
    Ok(())
}

fn compute_source_fingerprint(
    project_root: &Path,
    root_csprojs: &[(String, PathBuf)],
    other_projs: &[String],
    slns: &[String],
    build_files: &[(usize, PathBuf)],
) -> Result<String> {
    let mut hasher = Sha256::new();
    hasher.update(format!("dir_build={}\n", !build_files.is_empty()).as_bytes());
    for (depth, path) in build_files {
        let name = path
            .file_name()
            .expect("named build file")
            .to_string_lossy();
        hasher.update(format!("build={depth}/{name}\n").as_bytes());
        let mut contents = Sha256::new();
        std::io::copy(&mut fs::File::open(path)?, &mut contents)?;
        hasher.update(contents.finalize());
    }
    for o in other_projs {
        hasher.update(format!("other={o}\n").as_bytes());
    }
    for s in slns {
        hasher.update(format!("sln={s}\n").as_bytes());
    }
    for (name, path) in root_csprojs {
        hasher.update(format!("csproj={name}\n").as_bytes());
        if let Ok(bytes) = fs::read(path) {
            hasher.update((bytes.len() as u64).to_le_bytes());
            hasher.update(&bytes);
        }
    }
    let global_json = project_root.join("global.json");
    if global_json.is_file()
        && let Ok(bytes) = fs::read(&global_json)
    {
        hasher.update(b"global.json\n");
        hasher.update((bytes.len() as u64).to_le_bytes());
        hasher.update(&bytes);
    }
    Ok(format!("{:x}", hasher.finalize()))
}

fn scan_child_csprojs(root: &Path, max_depth: usize, max_dirs: usize) -> Vec<String> {
    let mut found = Vec::new();
    let mut visited_dirs = 0usize;
    scan_child_recursive(
        root,
        root,
        0,
        max_depth,
        &mut visited_dirs,
        max_dirs,
        &mut found,
    );
    found.sort();
    found
}

fn scan_child_recursive(
    root: &Path,
    dir: &Path,
    depth: usize,
    max_depth: usize,
    visited_dirs: &mut usize,
    max_dirs: usize,
    found: &mut Vec<String>,
) {
    if depth >= max_depth || *visited_dirs >= max_dirs {
        return;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let Ok(ft) = entry.file_type() else {
            continue;
        };
        if ft.is_symlink() {
            continue;
        }
        let p = entry.path();
        let Some(name) = p.file_name().and_then(|n| n.to_str()) else {
            continue;
        };
        if ft.is_dir() {
            let lower = name.to_ascii_lowercase();
            if matches!(
                lower.as_str(),
                ".git" | "bin" | "obj" | "target" | "node_modules" | ".vs"
            ) {
                continue;
            }
            *visited_dirs += 1;
            if *visited_dirs > max_dirs {
                return;
            }
            scan_child_recursive(
                root,
                &p,
                depth + 1,
                max_depth,
                visited_dirs,
                max_dirs,
                found,
            );
        } else if ft.is_file()
            && depth > 0
            && name.to_ascii_lowercase().ends_with(".csproj")
            && let Ok(rel) = p.strip_prefix(root)
        {
            found.push(rel.to_string_lossy().replace('\\', "/"));
        }
    }
}

enum PropState {
    Present(String),
    Missing,
    DynamicOrConditional(String),
}

fn resolve_single_static_property(parsed: &ParsedCsproj, key_lower: &str) -> PropState {
    let Some(entries) = parsed.properties.get(key_lower) else {
        return PropState::Missing;
    };
    if entries.iter().any(|e| e.conditional) {
        return PropState::DynamicOrConditional(format!(
            "属性 {key_lower} 依赖 MSBuild 条件或目标，无法从静态声明确定。"
        ));
    }
    if entries.iter().any(|e| e.dynamic_expr) {
        return PropState::DynamicOrConditional(format!(
            "属性 {key_lower} 使用了 $(...) 或 @(...) MSBuild 动态表达式，无法在只读模式下静态求值。"
        ));
    }
    let first = entries.first().map_or("", |e| e.value.trim());
    if entries.iter().any(|e| e.value.trim() != first) {
        return PropState::DynamicOrConditional(format!(
            "属性 {key_lower} 有多个不同的声明值，无法静态确定采用哪一个。"
        ));
    }
    if first.is_empty() {
        PropState::Missing
    } else {
        PropState::Present(first.to_string())
    }
}

fn parse_supported_tfm(tfm: &str) -> Option<u32> {
    let trimmed = tfm.trim().to_ascii_lowercase();
    let rest = trimmed.strip_prefix("net")?;
    let (ver_part, os_suffix) = match rest.split_once('-') {
        Some((v, s)) => (v, Some(s)),
        None => (rest, None),
    };
    // Reject classic .NET Framework like net48 / net472 (which have no dot!)
    let (maj_str, min_str) = ver_part.split_once('.')?;
    let major: u32 = maj_str.parse().ok()?;
    let minor: u32 = min_str.parse().ok()?;
    if !(6..=99).contains(&major) || minor != 0 {
        return None;
    }
    if let Some(suffix) = os_suffix
        && !suffix.starts_with("windows")
    {
        return None;
    }
    Some(major)
}

fn is_supported_wpf_tfm(tfm: &str, major: u32) -> bool {
    let normalized = tfm.to_ascii_lowercase();
    let prefix = format!("net{major}.0-windows");
    let Some(platform_version) = normalized.strip_prefix(&prefix) else {
        return false;
    };
    major >= 8
        && (platform_version.is_empty()
            || ((2..=4).contains(&platform_version.split('.').count())
                && platform_version.split('.').all(|part| {
                    part.bytes().all(|b| b.is_ascii_digit()) && part.parse::<u16>().is_ok()
                })))
}

fn detect_host_sdk_directories_readonly(
    min_major: u32,
    exact_version: Option<&str>,
) -> Vec<String> {
    let mut versions = Vec::new();
    let mut roots = Vec::new();
    if let Some(dot_root) = std::env::var_os("DOTNET_ROOT") {
        roots.push(PathBuf::from(dot_root).join("sdk"));
    }
    if let Some(pf) = std::env::var_os("ProgramFiles") {
        roots.push(PathBuf::from(pf).join("dotnet").join("sdk"));
    }
    if let Some(local) = std::env::var_os("LOCALAPPDATA") {
        roots.push(
            PathBuf::from(local)
                .join("Microsoft")
                .join("dotnet")
                .join("sdk"),
        );
    }
    if cfg!(not(windows)) {
        roots.push(PathBuf::from("/usr/share/dotnet/sdk"));
        roots.push(PathBuf::from("/usr/lib/dotnet/sdk"));
    }

    for sdk_dir in roots {
        let Ok(entries) = fs::read_dir(sdk_dir) else {
            continue;
        };
        for entry in entries.flatten() {
            if !entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
                continue;
            }
            let Some(name) = entry.file_name().to_str().map(str::to_string) else {
                continue;
            };
            if name.len() <= 48
                && name
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || ".-+".contains(c))
                && let Some(maj) = name.split('.').next().and_then(|s| s.parse::<u32>().ok())
                && maj >= min_major
                && exact_version.is_none_or(|version| version == name)
                && entry.path().join("dotnet.dll").is_file()
                && !versions.contains(&name)
            {
                versions.push(name);
            }
        }
    }
    versions.sort();
    if versions.len() > 4 {
        versions.truncate(4);
    }
    versions
}

fn parse_csproj_xml(raw: &str) -> std::result::Result<ParsedCsproj, String> {
    let trimmed_bom = raw.trim_start_matches('\u{feff}');
    if trimmed_bom.contains("<!DOCTYPE") || trimmed_bom.contains("<!ENTITY") {
        return Err("不允许包含 XML DOCTYPE 或外部实体声明。".into());
    }
    let cleaned = strip_comments_and_pi(trimmed_bom)?;
    let mut parsed = ParsedCsproj::default();

    let Some(proj_open_idx) = find_tag_open(&cleaned, "Project") else {
        return Err("缺少根节点 <Project>。".into());
    };
    let proj_after = &cleaned[proj_open_idx + "<Project".len()..];
    let Some(proj_gt) = find_tag_end(proj_after) else {
        return Err("<Project> 起始标签未闭合。".into());
    };
    let proj_attrs = &proj_after[..proj_gt];
    parsed.sdk_attr = extract_xml_attr(proj_attrs, "Sdk");

    // Also support <Sdk Name="Microsoft.NET.Sdk" /> child element
    if parsed.sdk_attr.is_none()
        && let Some(sdk_idx) = find_tag_open(&cleaned, "Sdk")
    {
        let sdk_after = &cleaned[sdk_idx + "<Sdk".len()..];
        if let Some(sdk_gt) = find_tag_end(sdk_after) {
            parsed.sdk_attr = extract_xml_attr(&sdk_after[..sdk_gt], "Name");
        }
    }

    if find_tag_open(&cleaned, "Import").is_some() {
        parsed.has_import = true;
    }

    // Properties inside Choose/When and Target are not unconditional project
    // declarations. Track the enclosing elements while locating PropertyGroups.
    let mut parents = vec!["Project"];
    let mut rest = &proj_after[proj_gt + 1..];
    while let Some(lt) = rest.find('<') {
        let after_lt = &rest[lt + 1..];
        if let Some(cdata) = after_lt.strip_prefix("![CDATA[") {
            let (_, remaining) = cdata.split_once("]]>").ok_or("CDATA 缺少结束标记。")?;
            rest = remaining;
            continue;
        }
        let gt = find_tag_end(after_lt).ok_or("XML 标签未闭合。")?;
        let element = after_lt[..gt].trim_end();
        rest = &after_lt[gt + 1..];
        if let Some(name) = element.strip_prefix('/') {
            if parents.pop() != Some(name.trim()) {
                return Err(format!("XML 闭合标签 </{name}> 与起始标签不匹配。"));
            }
            continue;
        }
        if parents.is_empty() {
            return Err("项目 XML 包含多个根节点。".into());
        }
        let name_end = element
            .find(|c: char| c.is_whitespace() || c == '/')
            .unwrap_or(element.len());
        let (name, attrs) = element.split_at(name_end);
        if name.eq_ignore_ascii_case("ProjectReference") {
            parsed.has_project_reference = true;
        }
        if attrs.ends_with('/') {
            continue;
        }
        if name == "PropertyGroup" {
            let conditional = parents.len() != 1 || extract_xml_attr(attrs, "Condition").is_some();
            let (group, remaining) = rest
                .split_once("</PropertyGroup>")
                .ok_or("<PropertyGroup> 缺少闭合标签。")?;
            parse_property_group_body(group, conditional, &mut parsed.properties)?;
            rest = remaining;
        } else {
            parents.push(name);
        }
    }
    if !parents.is_empty() {
        return Err("项目 XML 缺少闭合标签。".into());
    }

    Ok(parsed)
}

fn parse_property_group_body(
    body: &str,
    group_conditional: bool,
    out: &mut BTreeMap<String, Vec<RawPropertyEntry>>,
) -> std::result::Result<(), String> {
    let mut cursor = body;
    while let Some(lt) = cursor.find('<') {
        let after_lt = &cursor[lt + 1..];
        if after_lt.starts_with('/') {
            return Err("PropertyGroup 内存在未匹配的闭合标签。".into());
        }
        let name_len = after_lt
            .find(|c: char| c.is_whitespace() || c == '>' || c == '/')
            .ok_or_else(|| "属性标签不完整。".to_string())?;
        let tag_name = &after_lt[..name_len];
        if tag_name.is_empty() {
            return Err("发现空的 XML 标签名。".into());
        }
        let after_name = &after_lt[name_len..];
        let gt_idx = find_tag_end(after_name).ok_or_else(|| format!("<{tag_name}> 未闭合。"))?;
        let attrs = &after_name[..gt_idx];
        let prop_conditional = group_conditional || extract_xml_attr(attrs, "Condition").is_some();
        if attrs.trim_end().ends_with('/') {
            out.entry(tag_name.to_ascii_lowercase())
                .or_default()
                .push(RawPropertyEntry {
                    value: String::new(),
                    conditional: prop_conditional,
                    dynamic_expr: false,
                });
            cursor = &after_name[gt_idx + 1..];
            continue;
        }
        let content_start = &after_name[gt_idx + 1..];
        let close_token = format!("</{tag_name}>");
        let close_idx = content_start
            .find(&close_token)
            .ok_or_else(|| format!("属性 <{tag_name}> 缺少闭合标签 {close_token}。"))?;
        let raw_val = &content_start[..close_idx];
        let decoded = decode_xml_entities(raw_val);
        let dynamic = decoded.contains("$(") || decoded.contains("@(");
        out.entry(tag_name.to_ascii_lowercase())
            .or_default()
            .push(RawPropertyEntry {
                value: decoded,
                conditional: prop_conditional,
                dynamic_expr: dynamic,
            });
        cursor = &content_start[close_idx + close_token.len()..];
    }
    Ok(())
}

fn strip_comments_and_pi(xml: &str) -> std::result::Result<String, String> {
    let mut no_comments = String::with_capacity(xml.len());
    let mut rest = xml;
    while let Some(idx) = rest.find("<!--") {
        no_comments.push_str(&rest[..idx]);
        let after = &rest[idx + 4..];
        let end = after
            .find("-->")
            .ok_or_else(|| "XML 注释 <!-- 未闭合。".to_string())?;
        rest = &after[end + 3..];
    }
    no_comments.push_str(rest);

    let mut out = String::with_capacity(no_comments.len());
    let mut r2 = no_comments.as_str();
    while let Some(idx) = r2.find("<?") {
        out.push_str(&r2[..idx]);
        let after = &r2[idx + 2..];
        let end = after
            .find("?>")
            .ok_or_else(|| "XML 处理指令 <? 未闭合。".to_string())?;
        r2 = &after[end + 2..];
    }
    out.push_str(r2);
    Ok(out)
}

fn find_tag_open(haystack: &str, tag: &str) -> Option<usize> {
    let pat = format!("<{tag}");
    let mut offset = 0usize;
    while let Some(pos) = haystack[offset..].find(&pat) {
        let abs = offset + pos;
        let next_ch = haystack[abs + pat.len()..].chars().next();
        if next_ch.is_some_and(|c| c == '>' || c == '/' || c.is_whitespace()) {
            return Some(abs);
        }
        offset = abs + pat.len();
    }
    None
}

fn find_tag_end(s: &str) -> Option<usize> {
    let mut in_quote: Option<char> = None;
    for (idx, ch) in s.char_indices() {
        match in_quote {
            Some(q) if ch == q => in_quote = None,
            Some(_) => {}
            None if ch == '"' || ch == '\'' => in_quote = Some(ch),
            None if ch == '>' => return Some(idx),
            _ => {}
        }
    }
    None
}

fn extract_xml_attr(attrs: &str, target_attr: &str) -> Option<String> {
    let bytes = attrs.as_bytes();
    let mut i = 0usize;
    while i < bytes.len() {
        while i < bytes.len() && (bytes[i].is_ascii_whitespace() || bytes[i] == b'/') {
            i += 1;
        }
        if i >= bytes.len() {
            break;
        }
        let name_start = i;
        while i < bytes.len()
            && !bytes[i].is_ascii_whitespace()
            && bytes[i] != b'='
            && bytes[i] != b'/'
        {
            i += 1;
        }
        let name = &attrs[name_start..i];
        while i < bytes.len() && bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        if i >= bytes.len() || bytes[i] != b'=' {
            continue;
        }
        i += 1;
        while i < bytes.len() && bytes[i].is_ascii_whitespace() {
            i += 1;
        }
        if i >= bytes.len() || (bytes[i] != b'"' && bytes[i] != b'\'') {
            continue;
        }
        let quote = bytes[i];
        i += 1;
        let val_start = i;
        while i < bytes.len() && bytes[i] != quote {
            i += 1;
        }
        if i < bytes.len() {
            let val = &attrs[val_start..i];
            i += 1;
            if name.eq_ignore_ascii_case(target_attr) {
                return Some(decode_xml_entities(val.trim()));
            }
        }
    }
    None
}

fn decode_xml_entities(raw: &str) -> String {
    raw.replace("&quot;", "\"")
        .replace("&apos;", "'")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&amp;", "&")
}
