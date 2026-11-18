using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Contain.Desktop.Model;

public enum ManifestPreviewStatus
{
    Missing,
    Invalid,
    Valid
}

public sealed record ManifestStepPreview(
    int StepNumber,
    string PhaseKey,
    string PhaseBadge,
    string Name,
    string Program,
    IReadOnlyList<string> Args,
    int ExpectedExit,
    ulong TimeoutMs,
    string? StdoutSha256,
    string CommandLineText,
    string ExpectationText)
{
    public string DisplayHeader => $"{StepNumber}. [{PhaseBadge}] {Name}";
}

public sealed record BuildManifestPreview(
    ManifestPreviewStatus Status,
    string ProjectName,
    string WorkingDirectory,
    string RawFileSha256,
    string ValidationError,
    IReadOnlyList<ManifestStepPreview> BuildSteps,
    IReadOnlyList<ManifestStepPreview> VerifySteps,
    IReadOnlyList<ManifestStepPreview> AllSteps,
    string? ArtifactDir,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<string> PendingItems,
    bool HasUnsupportedSafeProbes)
{
    public static BuildManifestPreview Missing(string projectName, string workingDirectory) =>
        new(
            ManifestPreviewStatus.Missing,
            projectName,
            workingDirectory,
            "",
            "",
            [],
            [],
            [],
            null,
            [],
            [],
            false);

    public static BuildManifestPreview Invalid(string projectName, string workingDirectory, string error) =>
        new(
            ManifestPreviewStatus.Invalid,
            projectName,
            workingDirectory,
            "",
            error,
            [],
            [],
            [],
            null,
            [],
            [],
            false);
}

public sealed record BuildProgressUpdate(
    string Stage,
    string State,
    int? StepIndex = null,
    int? PhaseTotal = null,
    string? StepName = null)
{
    public string FormatDisplayText(string projectName) => (Stage, State) switch
    {
        ("environment_check", "running") => $"正在重新核验“{projectName}”的本机开发环境…",
        ("environment_check", "passed") => "环境核验已通过，准备按顺序执行构建步骤…",
        ("environment_check", "failed") => "构建前重新核验环境未通过，已停止执行构建步骤。",
        ("build", "running") when StepIndex is not null && PhaseTotal is not null && !string.IsNullOrWhiteSpace(StepName) =>
            $"正在执行构建步骤（第 {StepIndex} / {PhaseTotal} 步）：{StepName}",
        ("build", "passed") when StepIndex is not null && PhaseTotal is not null && !string.IsNullOrWhiteSpace(StepName) =>
            $"已完成构建步骤（第 {StepIndex} / {PhaseTotal} 步）：{StepName}",
        ("build", "failed") when StepIndex is not null && !string.IsNullOrWhiteSpace(StepName) =>
            $"构建步骤失败（第 {StepIndex} 步）：{StepName}",
        ("verify", "running") when StepIndex is not null && PhaseTotal is not null && !string.IsNullOrWhiteSpace(StepName) =>
            $"正在执行构建后验证步骤（第 {StepIndex} / {PhaseTotal} 步）：{StepName}",
        ("verify", "passed") when StepIndex is not null && PhaseTotal is not null && !string.IsNullOrWhiteSpace(StepName) =>
            $"已完成验证步骤（第 {StepIndex} / {PhaseTotal} 步）：{StepName}",
        ("verify", "failed") when StepIndex is not null && !string.IsNullOrWhiteSpace(StepName) =>
            $"验证步骤失败（第 {StepIndex} 步）：{StepName}",
        ("completed", "passed") => "构建与验证已完成，正在确认产物文件…",
        ("completed", "failed") => "构建未通过，正在整理失败诊断与日志…",
        _ => $"正在构建“{projectName}”…"
    };
}

public sealed record BuildStepExecutionResult(
    string Phase,
    int Index,
    string Name,
    string Status,
    int ExpectedExit,
    int? ObservedExit,
    bool TimedOut,
    string? LaunchError,
    string StdoutText,
    string StderrText);

public sealed record ProjectBuildExecution(
    int ExitCode,
    WorkflowReportData Report,
    string ReportMarkdown,
    string ReportJson,
    IReadOnlyList<BuildStepExecutionResult> StepResults,
    string CombinedLogText,
    string? ConfirmedArtifactDirectory);

public static class WorkflowManifestReader
{
    private const int MaxManifestBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> AllowedRootFields = new(StringComparer.Ordinal)
    {
        "schema_version",
        "target",
        "windows_toolchain",
        "tools",
        "required_variables",
        "build",
        "verify",
        "artifact_dir",
        "artifacts",
        "pending"
    };
    private static readonly HashSet<string> AllowedToolFields = new(StringComparer.Ordinal)
    {
        "program",
        "version",
        "version_args",
        "remedy"
    };
    private static readonly HashSet<string> AllowedStepFields = new(StringComparer.Ordinal)
    {
        "name",
        "program",
        "args",
        "expected_exit",
        "stdout_sha256",
        "timeout_ms"
    };
    private static readonly HashSet<string> ReservedDosNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Directly reads and validates contain.environment.json in-process without invoking contain-workflow.exe.
    /// </summary>
    public static BuildManifestPreview ReadPreview(string projectPath)
    {
        var normalizedDir = WorkflowCliEnvironmentService.NormalizeDirectoryPath(projectPath);
        var projectName = WorkflowCliEnvironmentService.ExtractFolderName(normalizedDir);
        var manifestPath = Path.Combine(normalizedDir, "contain.environment.json");
        if (!File.Exists(manifestPath))
            return BuildManifestPreview.Missing(projectName, normalizedDir);

        byte[] rawBytes;
        try
        {
            var fileInfo = new FileInfo(manifestPath);
            if (fileInfo.Length > MaxManifestBytes)
                return BuildManifestPreview.Invalid(projectName, normalizedDir, "contain.environment.json 体积超过 1 MiB 安全上限。");
            rawBytes = File.ReadAllBytes(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BuildManifestPreview.Invalid(projectName, normalizedDir, $"无法读取 contain.environment.json：{ex.Message}");
        }

        string jsonText;
        try
        {
            jsonText = StrictUtf8.GetString(rawBytes);
        }
        catch (DecoderFallbackException)
        {
            return BuildManifestPreview.Invalid(projectName, normalizedDir, "contain.environment.json 不是有效的 UTF-8 文本。");
        }

        var rawSha256 = Convert.ToHexStringLower(SHA256.HashData(rawBytes));
        return ParsePreviewJson(projectName, normalizedDir, jsonText, rawSha256);
    }

    public static BuildManifestPreview ParsePreviewJson(
        string projectName,
        string workingDirectory,
        string jsonText,
        string rawSha256)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return BuildManifestPreview.Invalid(projectName, workingDirectory, "根节点必须是 JSON 对象。");

            foreach (var prop in root.EnumerateObject())
            {
                if (!AllowedRootFields.Contains(prop.Name))
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, $"包含未知字段“{prop.Name}”。");
            }

            if (!root.TryGetProperty("schema_version", out var svEl) || !svEl.TryGetUInt32(out var sv) || sv != 1)
                return BuildManifestPreview.Invalid(projectName, workingDirectory, "仅支持 schema_version = 1。");

            if (!TryRequiredString(root, "target", out var target) || target != "windows-x64")
                return BuildManifestPreview.Invalid(projectName, workingDirectory, "仅支持 target = \"windows-x64\"。");

            if (root.TryGetProperty("windows_toolchain", out var wtEl) && wtEl.ValueKind != JsonValueKind.Null)
            {
                if (wtEl.ValueKind != JsonValueKind.Object)
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, "windows_toolchain 必须是对象。");
                foreach (var p in wtEl.EnumerateObject())
                {
                    if (p.Name is not ("msvc_family" or "sdk_version"))
                        return BuildManifestPreview.Invalid(projectName, workingDirectory, $"windows_toolchain 包含未知字段“{p.Name}”。");
                }
                if (!TryRequiredString(wtEl, "msvc_family", out var msvc)
                    || !TryRequiredString(wtEl, "sdk_version", out var sdk)
                    || !IsDotNumericSegments(msvc, 2)
                    || !IsDotNumericSegments(sdk, 4))
                {
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, "windows_toolchain 版本号格式无效。");
                }
            }

            if (!root.TryGetProperty("required_variables", out var varsEl) || varsEl.ValueKind != JsonValueKind.Array)
                return BuildManifestPreview.Invalid(projectName, workingDirectory, "缺少有效的 required_variables 数组。");
            foreach (var v in varsEl.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.String || !IsValidVariableName(v.GetString() ?? ""))
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, "required_variables 只能包含合法变量名，不能包含值。");
            }

            if (!root.TryGetProperty("tools", out var toolsEl) || toolsEl.ValueKind != JsonValueKind.Array)
                return BuildManifestPreview.Invalid(projectName, workingDirectory, "缺少有效的 tools 数组。");
            var hasUnsupportedProbe = false;
            foreach (var tool in toolsEl.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object)
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, "tools 项必须是对象。");
                foreach (var p in tool.EnumerateObject())
                {
                    if (!AllowedToolFields.Contains(p.Name))
                        return BuildManifestPreview.Invalid(projectName, workingDirectory, $"tools 项包含未知字段“{p.Name}”。");
                }
                if (!TryRequiredString(tool, "program", out var prog)
                    || !TryRequiredString(tool, "version", out var ver)
                    || !TryRequiredString(tool, "remedy", out var remedy)
                    || string.IsNullOrEmpty(prog)
                    || string.IsNullOrEmpty(ver)
                    || string.IsNullOrEmpty(remedy))
                {
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, "tools 项缺少非空的 program、version 或 remedy。");
                }
                if (!ver.All(static c => char.IsAsciiLetterOrDigit(c) || "._-+".Contains(c, StringComparison.Ordinal)))
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, $"工具“{prog}”的 version 格式无效。");
                if (!TryStringList(tool, "version_args", required: true, out var vArgs))
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, $"工具“{prog}”缺少有效的 version_args 数组。");
                if (!IsSupportedSafeProbe(prog, vArgs))
                    hasUnsupportedProbe = true;
            }

            if (!TryParseSteps(root, "build", "build", "构建", startNumber: 1, out var buildSteps, out var stepErr))
                return BuildManifestPreview.Invalid(projectName, workingDirectory, stepErr);

            if (!TryParseSteps(root, "verify", "verify", "验证", startNumber: buildSteps.Count + 1, out var verifySteps, out stepErr))
                return BuildManifestPreview.Invalid(projectName, workingDirectory, stepErr);

            string? artifactDir = null;
            if (root.TryGetProperty("artifact_dir", out var adEl) && adEl.ValueKind != JsonValueKind.Null)
            {
                if (adEl.ValueKind != JsonValueKind.String || !IsSafeRelativePath(adEl.GetString() ?? ""))
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, "artifact_dir 必须是项目内的安全相对路径。");
                artifactDir = adEl.GetString();
            }

            if (!TryStringList(root, "artifacts", required: false, out var artifacts))
                return BuildManifestPreview.Invalid(projectName, workingDirectory, "artifacts 必须是字符串数组。");
            foreach (var item in artifacts)
            {
                if (!IsSafeRelativePath(item))
                    return BuildManifestPreview.Invalid(projectName, workingDirectory, $"artifacts 包含不安全的相对路径：“{item}”。");
            }

            if (!TryStringList(root, "pending", required: false, out var pending))
                return BuildManifestPreview.Invalid(projectName, workingDirectory, "pending 必须是字符串数组。");

            var allSteps = new List<ManifestStepPreview>(buildSteps.Count + verifySteps.Count);
            allSteps.AddRange(buildSteps);
            allSteps.AddRange(verifySteps);

            return new BuildManifestPreview(
                ManifestPreviewStatus.Valid,
                projectName,
                workingDirectory,
                rawSha256,
                "",
                buildSteps,
                verifySteps,
                allSteps,
                artifactDir,
                artifacts,
                pending,
                hasUnsupportedProbe);
        }
        catch (JsonException ex)
        {
            return BuildManifestPreview.Invalid(projectName, workingDirectory, $"JSON 语法错误：{ex.Message}");
        }
    }

    public static bool IsSupportedSafeProbe(string program, IReadOnlyList<string> args)
    {
        if (args.Count == 1 && args[0] == "--version" && program is "rustc" or "cargo" or "git" or "dotnet" or "python")
            return true;
        if (args.Count == 1 && args[0] == "-V" && program == "python")
            return true;
        if (program == "rustup")
        {
            if (args.Count == 1 && args[0] == "--version") return true;
            if (args.Count == 2 && args[0] == "toolchain" && args[1] == "list") return true;
            if (args.Count == 3 && args[0] is "target" or "component" && args[1] == "list" && args[2] == "--installed") return true;
        }
        if (program == "dotnet" && args.Count == 1 && args[0] is "--list-sdks" or "--list-runtimes")
            return true;
        return false;
    }

    public static bool IsSafeRelativePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.Contains(':'))
            return false;
        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part[^1] is '.' or ' ')
                return false;
            if (part.Any(static c => c < ' ' || "<>\"|?*".Contains(c, StringComparison.Ordinal)))
                return false;
            var dotIdx = part.IndexOf('.');
            var stem = dotIdx >= 0 ? part[..dotIdx] : part;
            if (ReservedDosNames.Contains(stem))
                return false;
        }
        return true;
    }

    public static string FormatCommandLine(string program, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteIfNeeded(program));
        foreach (var arg in args)
        {
            sb.Append(' ').Append(QuoteIfNeeded(arg));
        }
        return sb.ToString();
    }

    private static string QuoteIfNeeded(string token)
    {
        if (token.Length == 0) return "\"\"";
        return token.Any(static c => char.IsWhiteSpace(c) || c == '"')
            ? $"\"{token.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : token;
    }

    private static bool TryParseSteps(
        JsonElement root,
        string propName,
        string phaseKey,
        string phaseBadge,
        int startNumber,
        out List<ManifestStepPreview> steps,
        out string error)
    {
        steps = [];
        error = "";
        if (!root.TryGetProperty(propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            error = $"缺少有效的 {propName} 数组。";
            return false;
        }

        var num = startNumber;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                error = $"{propName} 步骤项必须是对象。";
                return false;
            }
            foreach (var p in el.EnumerateObject())
            {
                if (!AllowedStepFields.Contains(p.Name))
                {
                    error = $"{propName} 步骤包含未知字段“{p.Name}”。";
                    return false;
                }
            }
            if (!TryRequiredString(el, "name", out var name)
                || !TryRequiredString(el, "program", out var program)
                || string.IsNullOrEmpty(name)
                || string.IsNullOrEmpty(program))
            {
                error = $"{propName} 步骤缺少非空的 name 或 program。";
                return false;
            }
            if (!TryStringList(el, "args", required: false, out var args))
            {
                error = $"步骤“{name}”的 args 必须是字符串数组。";
                return false;
            }
            if (!el.TryGetProperty("expected_exit", out var exitEl) || !exitEl.TryGetInt32(out var expectedExit))
            {
                error = $"步骤“{name}”缺少有效的 expected_exit。";
                return false;
            }
            if (!el.TryGetProperty("timeout_ms", out var timeoutEl)
                || !timeoutEl.TryGetUInt64(out var timeoutMs)
                || timeoutMs is < 1 or > 3_600_000)
            {
                error = $"步骤“{name}”的 timeout_ms 必须在 1..3600000 之间。";
                return false;
            }
            string? stdoutSha = null;
            if (el.TryGetProperty("stdout_sha256", out var shaEl) && shaEl.ValueKind != JsonValueKind.Null)
            {
                if (shaEl.ValueKind != JsonValueKind.String
                    || shaEl.GetString() is not { Length: 64 } hex
                    || !hex.All(Uri.IsHexDigit))
                {
                    error = $"步骤“{name}”的 stdout_sha256 必须是 64 位十六进制摘要。";
                    return false;
                }
                stdoutSha = hex;
            }

            var cmdLine = FormatCommandLine(program, args);
            var timeoutSec = Math.Max(1, (timeoutMs + 999) / 1000);
            var expectation = stdoutSha is null
                ? $"预期退出码 {expectedExit} · 超时上限 {timeoutSec}s"
                : $"预期退出码 {expectedExit} · 校验标准输出 SHA-256 ({stdoutSha[..12]}…) · 超时上限 {timeoutSec}s";

            steps.Add(new ManifestStepPreview(
                num++,
                phaseKey,
                phaseBadge,
                name,
                program,
                args,
                expectedExit,
                timeoutMs,
                stdoutSha,
                cmdLine,
                expectation));
        }

        return true;
    }

    private static bool TryRequiredString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String && prop.GetString() is { } s)
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    private static bool TryStringList(JsonElement obj, string name, bool required, out List<string> items)
    {
        items = [];
        if (!obj.TryGetProperty(name, out var prop))
            return !required;
        if (prop.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var el in prop.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String || el.GetString() is not { } s)
                return false;
            items.Add(s);
        }
        return true;
    }

    private static bool IsDotNumericSegments(string text, int expectedCount)
    {
        var parts = text.Split('.');
        return parts.Length == expectedCount && parts.All(static p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    private static bool IsValidVariableName(string value) =>
        value.Length > 0
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
