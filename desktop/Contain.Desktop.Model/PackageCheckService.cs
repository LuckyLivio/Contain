using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Contain.Desktop.Model;

public sealed record PackageZipSelection(
    string ZipFileName,
    string ZipPath,
    string? AutoDiscoveredSpecPath);

public sealed record PackageSpecSelection(
    string SpecFileName,
    string SpecPath,
    bool IsAutoDiscovered);

public sealed record PackageVerificationExecution(
    int ExitCode,
    WorkflowReportData Report,
    string ReportMarkdown,
    string ReportJson);

public interface IPackageCheckService
{
    PackageZipSelection InspectZip(string zipPath);
    PackageSpecSelection InspectSpec(string specPath, bool isAutoDiscovered);
    Task<PackageVerificationExecution> VerifyStaticAsync(
        string zipPath,
        string specPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Invokes bundled contain-workflow.exe verify in static-only mode (never passing --run),
/// reading structured schema 1 / package reports without extracting or executing ZIP contents.
/// </summary>
public sealed class WorkflowCliPackageService : IPackageCheckService
{
    public const string DefaultSpecFileName = "package-spec.json";
    private const int MaxStreamBytes = 8 * 1024 * 1024;
    private const string ExpectedVersionPrefix = "contain-workflow ";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly TimeSpan commandTimeout;
    private readonly SemaphoreSlim versionLock = new(1, 1);
    private bool versionChecked;

    public string ExecutablePath { get; }

    public WorkflowCliPackageService(string executablePath, TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = Path.GetFullPath(executablePath);
        this.commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(120);
        if (this.commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
    }

    public PackageZipSelection InspectZip(string zipPath) => InspectZipFile(zipPath);

    public PackageSpecSelection InspectSpec(string specPath, bool isAutoDiscovered) =>
        InspectSpecFile(specPath, isAutoDiscovered);

    public static PackageZipSelection InspectZipFile(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || zipPath.Contains('\0'))
            throw new EnvironmentCheckException("invalid_zip_path", "请选择有效的本地 ZIP 压缩包文件。");

        var fullZip = Path.GetFullPath(zipPath.Trim());
        if (!File.Exists(fullZip))
            throw new EnvironmentCheckException("zip_missing", "所选 ZIP 文件不存在或无法访问。");

        var dir = Path.GetDirectoryName(fullZip);
        string? autoSpec = null;
        if (!string.IsNullOrEmpty(dir))
        {
            var candidateSpec = Path.Combine(dir, DefaultSpecFileName);
            if (File.Exists(candidateSpec))
                autoSpec = Path.GetFullPath(candidateSpec);
        }

        return new PackageZipSelection(Path.GetFileName(fullZip), fullZip, autoSpec);
    }

    public static PackageSpecSelection InspectSpecFile(string specPath, bool isAutoDiscovered)
    {
        if (string.IsNullOrWhiteSpace(specPath) || specPath.Contains('\0'))
            throw new EnvironmentCheckException("invalid_spec_path", "请选择有效的校验清单文件。");

        var fullSpec = Path.GetFullPath(specPath.Trim());
        if (!File.Exists(fullSpec))
            throw new EnvironmentCheckException("spec_missing", "所选校验清单文件不存在或无法访问。");

        return new PackageSpecSelection(Path.GetFileName(fullSpec), fullSpec, isAutoDiscovered);
    }

    public async Task<PackageVerificationExecution> VerifyStaticAsync(
        string zipPath,
        string specPath,
        CancellationToken cancellationToken = default)
    {
        var zipInfo = InspectZip(zipPath);
        var specInfo = InspectSpec(specPath, isAutoDiscovered: false);
        await EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var tempRoot = Path.Combine(Path.GetTempPath(), "Contain-PkgVerify-" + Guid.NewGuid().ToString("N"));
        var reportDir = Path.Combine(tempRoot, "report");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Strictly static verification: never pass --run or --work-dir.
            var (exitCode, _, stderr) = await RunProcessAsync(
                ["verify", "--zip", zipInfo.ZipPath, "--spec", specInfo.SpecPath, "--report", reportDir],
                cancellationToken).ConfigureAwait(false);

            var jsonPath = Path.Combine(reportDir, "report.json");
            var mdPath = Path.Combine(reportDir, "report.md");
            if (!File.Exists(jsonPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"contain-workflow verify 退出码 {exitCode}，未生成结构化报告。"
                    : stderr.Trim();
                throw new EnvironmentCheckException(
                    "spec_or_report_invalid",
                    "无法读取或解析所选的校验清单（package-spec.json 格式无效，或与 schema 1 / windows-x64 规范不符）。",
                    detail);
            }

            var rawJson = await File.ReadAllTextAsync(jsonPath, StrictUtf8, cancellationToken).ConfigureAwait(false);
            var rawMd = File.Exists(mdPath)
                ? await File.ReadAllTextAsync(mdPath, StrictUtf8, cancellationToken).ConfigureAwait(false)
                : "";
            var parsed = ParsePackageReport(rawJson);
            return new PackageVerificationExecution(exitCode, parsed, rawMd, rawJson);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static WorkflowReportData ParsePackageReport(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new EnvironmentCheckException("report_contract", "CLI 交付包报告根节点不是 JSON 对象。");

            var schema = root.TryGetProperty("schema_version", out var sv) && sv.TryGetInt32(out var svVal) ? svVal : -1;
            var workflow = OptionalString(root, "workflow") ?? "";
            if (schema != 1 || workflow != "package")
                throw new EnvironmentCheckException("report_contract", "CLI 交付包报告版本或类型不匹配（要求 schema 1 / package）。");

            if (!root.TryGetProperty("checks", out var checksEl) || checksEl.ValueKind != JsonValueKind.Array)
                throw new EnvironmentCheckException("report_contract", "CLI 交付包报告缺少 checks 数组。");

            var checks = new List<WorkflowCheckItem>();
            foreach (var item in checksEl.EnumerateArray())
            {
                var name = OptionalString(item, "name");
                var status = OptionalString(item, "status");
                var detail = OptionalString(item, "detail");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(status) || detail is null)
                    throw new EnvironmentCheckException("report_contract", "CLI 交付包报告包含不完整的检查项。");

                checks.Add(new WorkflowCheckItem(
                    name,
                    status,
                    detail,
                    OptionalString(item, "outcome"),
                    OptionalString(item, "required"),
                    OptionalString(item, "observed"),
                    OptionalString(item, "remedy")));
            }

            return new WorkflowReportData(
                schema,
                workflow,
                OptionalString(root, "host_os") ?? "",
                OptionalString(root, "host_arch") ?? "",
                OptionalString(root, "process_arch") ?? "",
                OptionalString(root, "os_version"),
                OptionalString(root, "manifest_source"),
                checks);
        }
        catch (EnvironmentCheckException)
        {
            throw;
        }
        catch (JsonException error)
        {
            throw new EnvironmentCheckException("report_json", "CLI 交付包报告 JSON 格式无效。", error.Message, error);
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
                    "随包校验组件版本无效或无法运行，请确认已完整解压桌面包。",
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
            throw new EnvironmentCheckException("cli_timeout", "交付包静态校验超时，已停止校验进程。");
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
                    throw new EnvironmentCheckException("output_limit", "交付包检查输出超过安全上限。");
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
}
