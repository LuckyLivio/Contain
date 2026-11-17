using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Contain.Desktop.Model;

public enum ProjectManifestKind
{
    EnvironmentManifest,
    DeclaredProjectFiles,
    UnknownFormat
}

public sealed record ProjectFolderInfo(
    string ProjectName,
    string ProjectPath,
    ProjectManifestKind ManifestKind,
    IReadOnlyList<string> DetectedFiles);

public sealed record WorkflowCheckItem(
    string Name,
    string Status,
    string Detail,
    string? Outcome = null,
    string? Required = null,
    string? Observed = null,
    string? Remedy = null);

public sealed record WorkflowReportData(
    int SchemaVersion,
    string Workflow,
    string HostOs,
    string HostArch,
    string ProcessArch,
    string? OsVersion,
    string? ManifestSource,
    IReadOnlyList<WorkflowCheckItem> Checks);

public sealed record EnvironmentCheckExecution(
    int ExitCode,
    WorkflowReportData Report,
    string ReportMarkdown,
    string ReportJson);

public sealed class EnvironmentCheckException : Exception
{
    public string Code { get; }
    public string Diagnostic { get; }

    public EnvironmentCheckException(string code, string message, string diagnostic = "", Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Diagnostic = diagnostic;
    }
}

public interface IEnvironmentCheckService
{
    ProjectFolderInfo InspectProject(string projectPath);
    Task<EnvironmentCheckExecution> CheckAsync(string projectPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Invokes the packaged contain-workflow.exe binary with --safe-probes and --infer-if-missing,
/// reading only structured report.json output rather than parsing console text.
/// </summary>
public sealed class WorkflowCliEnvironmentService : IEnvironmentCheckService
{
    private const int MaxStreamBytes = 4 * 1024 * 1024;
    private const string ExpectedVersionPrefix = "contain-workflow ";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] SupportedDeclarationFiles =
        ["contain.environment.json", "Cargo.toml", "rust-toolchain.toml", "global.json"];

    private readonly TimeSpan commandTimeout;
    private readonly SemaphoreSlim versionLock = new(1, 1);
    private bool versionChecked;

    public string ExecutablePath { get; }

    public WorkflowCliEnvironmentService(string executablePath, TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = Path.GetFullPath(executablePath);
        this.commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(120);
        if (this.commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
    }

    public ProjectFolderInfo InspectProject(string projectPath) => InspectProjectDirectory(projectPath);

    public static ProjectFolderInfo InspectProjectDirectory(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || projectPath.Contains('\0'))
            throw new EnvironmentCheckException("invalid_project_path", "请选择有效的本地项目文件夹。");

        var full = NormalizeDirectoryPath(projectPath);
        if (!Directory.Exists(full))
            throw new EnvironmentCheckException("project_missing", "所选项目文件夹不存在或无法访问。");

        var detected = new List<string>();
        foreach (var file in SupportedDeclarationFiles)
        {
            if (File.Exists(Path.Combine(full, file)))
                detected.Add(file);
        }

        var kind = detected.Contains("contain.environment.json")
            ? ProjectManifestKind.EnvironmentManifest
            : detected.Count > 0
                ? ProjectManifestKind.DeclaredProjectFiles
                : ProjectManifestKind.UnknownFormat;

        return new ProjectFolderInfo(ExtractFolderName(full), full, kind, detected);
    }

    public async Task<EnvironmentCheckExecution> CheckAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var info = InspectProject(projectPath);
        await EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var tempRoot = Path.Combine(Path.GetTempPath(), "Contain-EnvCheck-" + Guid.NewGuid().ToString("N"));
        var reportDir = Path.Combine(tempRoot, "report");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var (exitCode, _, stderr) = await RunProcessAsync(
                ["check", "--project", info.ProjectPath, "--safe-probes", "--infer-if-missing", "--report", reportDir],
                cancellationToken).ConfigureAwait(false);

            var jsonPath = Path.Combine(reportDir, "report.json");
            var mdPath = Path.Combine(reportDir, "report.md");
            if (!File.Exists(jsonPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"contain-workflow 退出码 {exitCode}，未生成结构化报告。"
                    : stderr.Trim();
                throw new EnvironmentCheckException(
                    "report_not_generated",
                    "无法解析该项目的环境声明文件，或检查程序未能生成报告。",
                    detail);
            }

            var rawJson = await File.ReadAllTextAsync(jsonPath, StrictUtf8, cancellationToken).ConfigureAwait(false);
            var rawMd = File.Exists(mdPath)
                ? await File.ReadAllTextAsync(mdPath, StrictUtf8, cancellationToken).ConfigureAwait(false)
                : "";
            var parsed = ParseReport(rawJson);
            return new EnvironmentCheckExecution(exitCode, parsed, rawMd, rawJson);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static WorkflowReportData ParseReport(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new EnvironmentCheckException("report_contract", "CLI 报告根节点不是 JSON 对象。");

            var schema = root.TryGetProperty("schema_version", out var sv) && sv.TryGetInt32(out var svValue) ? svValue : -1;
            var workflow = root.TryGetProperty("workflow", out var wf) && wf.ValueKind == JsonValueKind.String ? wf.GetString() ?? "" : "";
            if (schema != 1 || workflow != "source")
                throw new EnvironmentCheckException("report_contract", "CLI 报告版本或类型不匹配（要求 schema 1 / source）。");

            var hostOs = OptionalString(root, "host_os") ?? "";
            var hostArch = OptionalString(root, "host_arch") ?? "";
            var processArch = OptionalString(root, "process_arch") ?? "";
            var osVersion = OptionalString(root, "os_version");
            var manifestSource = OptionalString(root, "manifest_source");

            if (!root.TryGetProperty("checks", out var checksEl) || checksEl.ValueKind != JsonValueKind.Array)
                throw new EnvironmentCheckException("report_contract", "CLI 报告缺少 checks 数组。");

            var checks = new List<WorkflowCheckItem>();
            foreach (var item in checksEl.EnumerateArray())
            {
                var name = OptionalString(item, "name");
                var status = OptionalString(item, "status");
                var detail = OptionalString(item, "detail");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(status) || detail is null)
                    throw new EnvironmentCheckException("report_contract", "CLI 报告包含不完整的检查项。");

                checks.Add(new WorkflowCheckItem(
                    name,
                    status,
                    detail,
                    OptionalString(item, "outcome"),
                    OptionalString(item, "required"),
                    OptionalString(item, "observed"),
                    OptionalString(item, "remedy")));
            }

            return new WorkflowReportData(schema, workflow, hostOs, hostArch, processArch, osVersion, manifestSource, checks);
        }
        catch (EnvironmentCheckException)
        {
            throw;
        }
        catch (JsonException error)
        {
            throw new EnvironmentCheckException("report_json", "CLI 报告 JSON 格式无效。", error.Message, error);
        }
    }

    private async Task EnsureVersionAsync(CancellationToken cancellationToken)
    {
        await versionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (versionChecked) return;
            var (code, stdout, stderr) = await RunProcessAsync(["--version"], cancellationToken).ConfigureAwait(false);
            var trimmed = stdout.Trim();
            if (code != 0 || !trimmed.StartsWith(ExpectedVersionPrefix, StringComparison.Ordinal))
                throw new EnvironmentCheckException(
                    "cli_version",
                    "随包环境检查组件版本无效或无法运行，请确认已完整解压桌面包。",
                    string.IsNullOrWhiteSpace(stderr) ? trimmed : stderr.Trim());
            versionChecked = true;
        }
        finally
        {
            versionLock.Release();
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(ExecutablePath))
            throw new EnvironmentCheckException(
                "cli_missing",
                "找不到桌面包附带的 contain-workflow.exe，请完整解压整个桌面 ZIP 后再运行。",
                ExecutablePath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
                StandardOutputEncoding = StrictUtf8,
                StandardErrorEncoding = StrictUtf8
            }
        };
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(commandTimeout);
        try
        {
            if (!process.Start())
                throw new EnvironmentCheckException("cli_start", "无法启动 contain-workflow.exe。");

            var stdoutTask = ReadLimitedAsync(process, process.StandardOutput.BaseStream, deadline.Token);
            var stderrTask = ReadLimitedAsync(process, process.StandardError.BaseStream, deadline.Token);
            var waitTask = process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdoutTask, stderrTask, waitTask).ConfigureAwait(false);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new EnvironmentCheckException("cli_timeout", "环境检查超时，已停止本次探测进程。");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (EnvironmentCheckException)
        {
            Kill(process);
            throw;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or DecoderFallbackException or InvalidOperationException)
        {
            Kill(process);
            throw new EnvironmentCheckException("cli_process", "运行 contain-workflow.exe 时发生进程或 I/O 错误。", error.Message, error);
        }
    }

    private static async Task<string> ReadLimitedAsync(Process process, Stream source, CancellationToken token)
    {
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                    return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
                if (output.Length + read > MaxStreamBytes)
                    throw new EnvironmentCheckException("output_limit", "环境检查输出超过安全上限。");
                output.Write(buffer, 0, read);
            }
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    public static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.TrimEndingDirectorySeparator(full);
    }

    internal static string ExtractFolderName(string normalizedPath)
    {
        var name = Path.GetFileName(normalizedPath);
        return string.IsNullOrEmpty(name) ? normalizedPath : name;
    }
}
