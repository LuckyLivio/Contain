using System.Diagnostics;
using System.Globalization;
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
    IReadOnlyList<string> DetectedFiles,
    string? ExistingInstallerAppId = null);

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
    BuildManifestPreview InspectBuildPreview(string projectPath);
    DotnetOnboardingPreviewData? InspectDotnetOnboarding(string projectPath);
    Task<DotnetOnboardingApplyOutcome> ApplyDotnetOnboardingAsync(
        string projectPath,
        string expectedFingerprintSha256,
        CancellationToken cancellationToken = default);
    Task<EnvironmentCheckExecution> CheckAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<ProjectBuildExecution> BuildAsync(
        string projectPath,
        BuildManifestPreview expectedPreview,
        IProgress<BuildProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default);
    Task<ProjectInstallerExecution> PackageInstallerAsync(
        string projectPath,
        string buildAttestationJson,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Invokes the packaged contain-workflow.exe binary with --safe-probes,
/// reading only structured report.json and local step log metadata rather than parsing console text.
/// </summary>
public sealed class WorkflowCliEnvironmentService : IEnvironmentCheckService
{
    private const int MaxStreamBytes = 4 * 1024 * 1024;
    private const int MaxStepLogDisplayBytes = 256 * 1024;
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

    public BuildManifestPreview InspectBuildPreview(string projectPath) => WorkflowManifestReader.ReadPreview(projectPath);

    public DotnetOnboardingPreviewData? InspectDotnetOnboarding(string projectPath)
    {
        var full = NormalizeDirectoryPath(projectPath);
        if (!HasDotnetOnboardingCandidate(full))
            return null;

        var tempFile = Path.Combine(Path.GetTempPath(), "Contain-OnboardInspect-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (_, _, stderr) = RunProcessAsync(
                ["onboard-dotnet", "--project", full, "--output-json", tempFile],
                TimeSpan.FromSeconds(15),
                CancellationToken.None).GetAwaiter().GetResult();

            if (!File.Exists(tempFile))
            {
                throw new EnvironmentCheckException(
                    "onboarding_inspect_failed",
                    "无法读取该 C# 项目的只读预览信息。",
                    stderr.Trim());
            }

            var rawJson = File.ReadAllText(tempFile, StrictUtf8);
            return DotnetOnboardingParser.ParseInspection(rawJson);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<DotnetOnboardingApplyOutcome> ApplyDotnetOnboardingAsync(
        string projectPath,
        string expectedFingerprintSha256,
        CancellationToken cancellationToken = default)
    {
        var info = InspectProject(projectPath);
        await EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var tempFile = Path.Combine(Path.GetTempPath(), "Contain-OnboardApply-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var args = new List<string> { "onboard-dotnet", "--project", info.ProjectPath, "--apply" };
            if (!string.IsNullOrWhiteSpace(expectedFingerprintSha256))
            {
                args.Add("--expected-fingerprint");
                args.Add(expectedFingerprintSha256);
            }
            args.Add("--output-json");
            args.Add(tempFile);

            var (exitCode, _, stderr) = await RunProcessAsync(args, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!File.Exists(tempFile))
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"contain-workflow onboard-dotnet --apply 退出码 {exitCode}。"
                    : stderr.Trim();
                throw new EnvironmentCheckException(
                    "onboarding_apply_failed",
                    "无法保存项目发布配置文件 contain.environment.json。",
                    detail);
            }

            var rawJson = await File.ReadAllTextAsync(tempFile, StrictUtf8, cancellationToken).ConfigureAwait(false);
            return DotnetOnboardingParser.ParseApplyOutcome(rawJson);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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

        var existingAppId = TryReadExistingInstallerAppId(full);
        return new ProjectFolderInfo(ExtractFolderName(full), full, kind, detected, existingAppId);
    }

    public static bool HasDotnetOnboardingCandidate(string normalizedProjectPath)
    {
        if (!Directory.Exists(normalizedProjectPath))
            return false;
        if (File.Exists(Path.Combine(normalizedProjectPath, "contain.environment.json")))
            return false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(normalizedProjectPath, "*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (File.Exists(Path.Combine(normalizedProjectPath, "Cargo.toml"))
                || File.Exists(Path.Combine(normalizedProjectPath, "rust-toolchain.toml")))
            {
                return false;
            }

            return HasChildCsprojBounded(normalizedProjectPath, depth: 0, maxDepth: 2);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasChildCsprojBounded(string dir, int depth, int maxDepth)
    {
        if (depth >= maxDepth)
            return false;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || name.Equals("target", StringComparison.OrdinalIgnoreCase)
                || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".vs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (Directory.EnumerateFiles(sub, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                return true;
            if (HasChildCsprojBounded(sub, depth + 1, maxDepth))
                return true;
        }
        return false;
    }

    private static string? TryReadExistingInstallerAppId(string normalizedProjectPath)
    {
        var path = Path.Combine(normalizedProjectPath, "contain.installer.json");
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("app_id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                var id = idEl.GetString()?.Trim();
                if (!string.IsNullOrEmpty(id))
                    return id;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        return null;
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
                commandTimeout,
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

    public async Task<ProjectBuildExecution> BuildAsync(
        string projectPath,
        BuildManifestPreview expectedPreview,
        IProgress<BuildProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedPreview);
        var info = InspectProject(projectPath);
        var freshPreview = InspectBuildPreview(info.ProjectPath);
        if (freshPreview.Status != ManifestPreviewStatus.Valid
            || !string.Equals(freshPreview.RawFileSha256, expectedPreview.RawFileSha256, StringComparison.Ordinal))
        {
            throw new EnvironmentCheckException(
                "manifest_changed",
                "项目构建清单在启动前已发生变化，请刷新预览并重新检查后再开始构建。");
        }

        await EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var runStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        var deliveryDir = Path.Combine(info.ProjectPath, "target", "workflow", "deliveries", "build-" + runStamp);
        var tempRoot = Path.Combine(Path.GetTempPath(), "Contain-EnvBuild-" + Guid.NewGuid().ToString("N"));
        var reportDir = Path.Combine(tempRoot, "report");
        var logDir = Path.Combine(tempRoot, "logs");
        Directory.CreateDirectory(tempRoot);

        var totalStepTimeoutMs = 0UL;
        foreach (var step in freshPreview.AllSteps)
            totalStepTimeoutMs += step.TimeoutMs;
        var buildTimeout = TimeSpan.FromMilliseconds(Math.Min(7_200_000UL, totalStepTimeoutMs + 120_000UL));
        if (buildTimeout < commandTimeout)
            buildTimeout = commandTimeout;

        try
        {
            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var progressPath = Path.Combine(logDir, "progress.jsonl");
            var pollTask = progress is null
                ? Task.CompletedTask
                : PollProgressAsync(progressPath, progress, pollCts.Token);

            int exitCode;
            string stderr;
            try
            {
                (exitCode, _, stderr) = await RunProcessAsync(
                    [
                        "build",
                        "--project", info.ProjectPath,
                        "--safe-probes",
                        "--execute",
                        "--delivery-dir", deliveryDir,
                        "--log-dir", logDir,
                        "--report", reportDir
                    ],
                    buildTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await pollCts.CancelAsync().ConfigureAwait(false);
                try { await pollTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            if (progress is not null)
                FlushRemainingProgress(progressPath, progress);

            var jsonPath = Path.Combine(reportDir, "report.json");
            var mdPath = Path.Combine(reportDir, "report.md");
            if (!File.Exists(jsonPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"contain-workflow build 退出码 {exitCode}，未生成结构化报告。"
                    : stderr.Trim();
                throw new EnvironmentCheckException(
                    "build_report_not_generated",
                    "构建程序未能生成结构化报告，请检查项目清单或桌面包完整性。",
                    detail);
            }

            var rawJson = await File.ReadAllTextAsync(jsonPath, StrictUtf8, cancellationToken).ConfigureAwait(false);
            var rawMd = File.Exists(mdPath)
                ? await File.ReadAllTextAsync(mdPath, StrictUtf8, cancellationToken).ConfigureAwait(false)
                : "";
            var parsed = ParseReport(rawJson);
            var stepResults = ReadStepResults(logDir);
            var allPassed = exitCode == 0 && parsed.Checks.All(static c => c.Status == "passed");
            var confirmedArtifactDir = allPassed
                ? ResolveConfirmedArtifactDirectory(info.ProjectPath, freshPreview, deliveryDir)
                : null;
            var attestationPath = Path.Combine(reportDir, "build-attestation.json");
            var attestationJson = allPassed && File.Exists(attestationPath)
                ? await File.ReadAllTextAsync(attestationPath, StrictUtf8, cancellationToken).ConfigureAwait(false)
                : null;
            var combinedLog = BuildCombinedLog(info, freshPreview, exitCode, stepResults, rawMd);

            return new ProjectBuildExecution(
                exitCode,
                parsed,
                rawMd,
                rawJson,
                stepResults,
                combinedLog,
                confirmedArtifactDir,
                attestationJson);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<ProjectInstallerExecution> PackageInstallerAsync(
        string projectPath,
        string buildAttestationJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(buildAttestationJson))
        {
            throw new EnvironmentCheckException(
                "missing_build_attestation",
                "缺少本次成功构建的产物核验记录，请先点击“开始构建”完成构建与验证后再生成安装包。");
        }

        var info = InspectProject(projectPath);
        await EnsureVersionAsync(cancellationToken).ConfigureAwait(false);

        var runStamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        var outputDir = Path.Combine(info.ProjectPath, "target", "workflow", "installers", "installer-" + runStamp);
        var tempRoot = Path.Combine(Path.GetTempPath(), "Contain-ProjPkg-" + Guid.NewGuid().ToString("N"));
        var attestationFile = Path.Combine(tempRoot, "build-attestation.json");
        var reportDir = Path.Combine(tempRoot, "report");
        Directory.CreateDirectory(tempRoot);

        var pkgTimeout = commandTimeout > TimeSpan.FromMinutes(5)
            ? commandTimeout
            : TimeSpan.FromMinutes(5);

        try
        {
            await File.WriteAllTextAsync(attestationFile, buildAttestationJson, StrictUtf8, cancellationToken).ConfigureAwait(false);

            var (exitCode, _, stderr) = await RunProcessAsync(
                [
                    "package-installer",
                    "--project", info.ProjectPath,
                    "--attestation", attestationFile,
                    "--output-dir", outputDir,
                    "--report", reportDir
                ],
                pkgTimeout,
                cancellationToken).ConfigureAwait(false);

            var jsonPath = Path.Combine(reportDir, "report.json");
            var mdPath = Path.Combine(reportDir, "report.md");
            if (!File.Exists(jsonPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"contain-workflow package-installer 退出码 {exitCode}，未生成结构化报告。"
                    : stderr.Trim();
                throw new EnvironmentCheckException(
                    "installer_report_not_generated",
                    "无法执行安装包打包检查（可能项目清单已被改动或语法无效）。",
                    detail);
            }

            var rawJson = await File.ReadAllTextAsync(jsonPath, StrictUtf8, cancellationToken).ConfigureAwait(false);
            var rawMd = File.Exists(mdPath)
                ? await File.ReadAllTextAsync(mdPath, StrictUtf8, cancellationToken).ConfigureAwait(false)
                : "";
            var parsed = ParseReport(rawJson, expectedWorkflow: "project_installer");

            ProjectInstallerReceiptData? receipt = null;
            var receiptPath = Path.Combine(reportDir, "installer-receipt.json");
            if (exitCode == 0 && File.Exists(receiptPath))
            {
                var rawReceipt = await File.ReadAllTextAsync(receiptPath, StrictUtf8, cancellationToken).ConfigureAwait(false);
                var summaryPath = Path.Combine(outputDir, "installer-summary.md");
                var summaryMd = File.Exists(summaryPath)
                    ? await File.ReadAllTextAsync(summaryPath, StrictUtf8, cancellationToken).ConfigureAwait(false)
                    : "";
                receipt = ParseInstallerReceipt(rawReceipt, outputDir, summaryMd);
            }

            return new ProjectInstallerExecution(exitCode, parsed, rawMd, rawJson, receipt);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static ProjectInstallerReceiptData ParseInstallerReceipt(
        string json,
        string outputDirectory,
        string summaryMarkdown)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var schema = root.TryGetProperty("schema_version", out var sv) && sv.TryGetInt32(out var svVal) ? svVal : -1;
            var protocol = OptionalString(root, "protocol") ?? "";
            if (schema != 1 || protocol != "contain-project-installer-receipt-1")
                throw new EnvironmentCheckException("installer_receipt_contract", "安装包收据协议版本不匹配。");

            var installerFile = OptionalString(root, "installer_file") ?? "";
            if (string.IsNullOrWhiteSpace(installerFile) || installerFile.Contains('/') || installerFile.Contains('\\'))
                throw new EnvironmentCheckException("installer_receipt_contract", "安装包收据中的文件名无效。");

            var fullDir = NormalizeDirectoryPath(outputDirectory);
            var fullExePath = Path.Combine(fullDir, installerFile);
            if (!File.Exists(fullExePath))
                throw new EnvironmentCheckException("installer_file_missing", $"未在输出目录找到生成的安装包：{installerFile}");

            var bytes = root.TryGetProperty("installer_bytes", out var bEl) && bEl.TryGetInt64(out var bVal) ? bVal : 0L;
            var payloadCount = root.TryGetProperty("payload_file_count", out var pEl) && pEl.TryGetInt32(out var pVal) ? pVal : 0;
            var signed = root.TryGetProperty("signed", out var sEl) && sEl.ValueKind == JsonValueKind.True;

            return new ProjectInstallerReceiptData(
                OptionalString(root, "project_name") ?? "",
                OptionalString(root, "display_name") ?? "",
                OptionalString(root, "app_id") ?? "",
                OptionalString(root, "short_id") ?? "",
                OptionalString(root, "identity_source") ?? "",
                OptionalString(root, "app_version") ?? "",
                OptionalString(root, "version_info_version") ?? "",
                OptionalString(root, "publisher"),
                OptionalString(root, "publish_kind") ?? "",
                OptionalString(root, "publish_rel_dir") ?? "",
                OptionalString(root, "entry") ?? "",
                OptionalString(root, "entry_sha256") ?? "",
                OptionalString(root, "manifest_sha256") ?? "",
                OptionalString(root, "attestation_sha256") ?? "",
                payloadCount,
                OptionalString(root, "default_install_dir") ?? "",
                OptionalString(root, "start_menu_shortcut") ?? "",
                installerFile,
                bytes,
                OptionalString(root, "installer_sha256") ?? "",
                OptionalString(root, "authenticode_status") ?? "NotSigned",
                signed,
                OptionalString(root, "inno_setup_version") ?? "",
                fullExePath,
                fullDir,
                summaryMarkdown);
        }
        catch (EnvironmentCheckException)
        {
            throw;
        }
        catch (JsonException error)
        {
            throw new EnvironmentCheckException("installer_receipt_json", "安装包收据 JSON 格式无效。", error.Message, error);
        }
    }

    public static string? ResolveConfirmedArtifactDirectory(
        string projectRoot,
        BuildManifestPreview preview,
        string? deliveryDir)
    {
        var normalizedRoot = NormalizeDirectoryPath(projectRoot);
        var declaredDir = preview.ArtifactDir ?? preview.Installer?.PublishDir;

        if (!string.IsNullOrWhiteSpace(deliveryDir) && Directory.Exists(deliveryDir))
        {
            var fullDelivery = NormalizeDirectoryPath(deliveryDir);
            if (!string.IsNullOrWhiteSpace(declaredDir))
            {
                var nested = NormalizeDirectoryPath(Path.Combine(fullDelivery, declaredDir.Replace('/', Path.DirectorySeparatorChar)));
                if (IsPathWithin(fullDelivery, nested)
                    && Directory.Exists(nested)
                    && ContainsNonEmptyFile(nested)
                    && AllDeclaredArtifactsExist(nested, preview.Artifacts))
                {
                    return nested;
                }
            }
            else if (ContainsNonEmptyFile(fullDelivery) && AllDeclaredArtifactsExist(fullDelivery, preview.Artifacts))
            {
                return fullDelivery;
            }
        }

        if (!string.IsNullOrWhiteSpace(declaredDir))
        {
            var candidate = NormalizeDirectoryPath(Path.Combine(normalizedRoot, declaredDir.Replace('/', Path.DirectorySeparatorChar)));
            if (IsPathWithin(normalizedRoot, candidate)
                && Directory.Exists(candidate)
                && ContainsNonEmptyFile(candidate)
                && AllDeclaredArtifactsExist(candidate, preview.Artifacts))
            {
                return candidate;
            }
            return null;
        }

        if (preview.Artifacts.Count > 0)
        {
            if (!AllDeclaredArtifactsExist(normalizedRoot, preview.Artifacts))
                return null;

            var firstPath = Path.GetFullPath(Path.Combine(normalizedRoot, preview.Artifacts[0].Replace('/', Path.DirectorySeparatorChar)));
            var dir = Path.GetDirectoryName(firstPath);
            if (!string.IsNullOrWhiteSpace(dir) && IsPathWithin(normalizedRoot, dir) && Directory.Exists(dir))
                return NormalizeDirectoryPath(dir);
        }

        return null;
    }

    private static bool AllDeclaredArtifactsExist(string baseDirectory, IReadOnlyList<string> artifacts)
    {
        foreach (var rel in artifacts)
        {
            var target = Path.GetFullPath(Path.Combine(baseDirectory, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathWithin(baseDirectory, target))
                return false;
            var fi = new FileInfo(target);
            if (!fi.Exists || fi.Length <= 0)
                return false;
        }
        return true;
    }

    private static bool ContainsNonEmptyFile(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (new FileInfo(file).Length > 0)
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private static bool IsPathWithin(string rootDir, string candidatePath)
    {
        var normalizedRoot = NormalizeDirectoryPath(rootDir);
        var normalizedCandidate = NormalizeDirectoryPath(candidatePath);
        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static List<BuildStepExecutionResult> ReadStepResults(string logDir)
    {
        var stepsPath = Path.Combine(logDir, "steps.json");
        var list = new List<BuildStepExecutionResult>();
        if (!File.Exists(stepsPath))
            return list;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stepsPath, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var phase = OptionalString(item, "phase") ?? "build";
                var index = item.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idx) ? idx : 0;
                var name = OptionalString(item, "name") ?? "";
                var status = OptionalString(item, "status") ?? "not_run";
                var expectedExit = item.TryGetProperty("expected_exit", out var expEl) && expEl.TryGetInt32(out var exp) ? exp : 0;
                int? observedExit = item.TryGetProperty("observed_exit", out var obsEl) && obsEl.ValueKind == JsonValueKind.Number && obsEl.TryGetInt32(out var obs)
                    ? obs
                    : null;
                var timedOut = item.TryGetProperty("timed_out", out var toEl) && toEl.ValueKind == JsonValueKind.True;
                var launchErr = OptionalString(item, "launch_error");
                var stdoutFile = OptionalString(item, "stdout_file");
                var stderrFile = OptionalString(item, "stderr_file");

                var stdoutText = ReadBoundedLogFile(logDir, stdoutFile);
                var stderrText = ReadBoundedLogFile(logDir, stderrFile);
                list.Add(new BuildStepExecutionResult(
                    phase,
                    index,
                    name,
                    status,
                    expectedExit,
                    observedExit,
                    timedOut,
                    launchErr,
                    stdoutText,
                    stderrText));
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        return list;
    }

    private static string ReadBoundedLogFile(string logDir, string? relativeName)
    {
        if (string.IsNullOrWhiteSpace(relativeName) || relativeName.Contains('/') || relativeName.Contains('\\'))
            return "";
        var path = Path.Combine(logDir, relativeName);
        if (!File.Exists(path))
            return "";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var toRead = (int)Math.Min(fs.Length, MaxStepLogDisplayBytes);
            var buf = new byte[toRead];
            var read = fs.Read(buf, 0, toRead);
            var text = Encoding.UTF8.GetString(buf, 0, read);
            if (fs.Length > MaxStepLogDisplayBytes)
                text += "\n…（日志超过显示上限，已截断）";
            return text.TrimEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static string BuildCombinedLog(
        ProjectFolderInfo project,
        BuildManifestPreview preview,
        int exitCode,
        IReadOnlyList<BuildStepExecutionResult> stepResults,
        string reportMarkdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"项目名称：{project.ProjectName}");
        sb.AppendLine($"工作目录：{project.ProjectPath}");
        sb.AppendLine($"清单 SHA-256：{preview.RawFileSha256}");
        sb.AppendLine($"contain-workflow 退出码：{exitCode}");
        sb.AppendLine();

        foreach (var step in stepResults)
        {
            var phaseName = step.Phase == "verify" ? "验证步骤" : "构建步骤";
            var statusLabel = step.Status switch
            {
                "passed" => "通过",
                "failed" => "失败",
                _ => "未执行"
            };
            sb.AppendLine($"=== [{phaseName} {step.Index + 1}] {step.Name} — {statusLabel} ===");
            if (step.Status != "not_run")
            {
                var exitInfo = step.TimedOut
                    ? "执行超时"
                    : step.LaunchError is not null
                        ? $"无法启动：{step.LaunchError}"
                        : $"预期退出码 {step.ExpectedExit}，实际退出码 {(step.ObservedExit?.ToString(CultureInfo.InvariantCulture) ?? "无")}";
                sb.AppendLine(exitInfo);
            }
            if (!string.IsNullOrWhiteSpace(step.StderrText))
            {
                sb.AppendLine("--- 标准错误 (stderr) ---");
                sb.AppendLine(step.StderrText);
            }
            if (!string.IsNullOrWhiteSpace(step.StdoutText))
            {
                sb.AppendLine("--- 标准输出 (stdout) ---");
                sb.AppendLine(step.StdoutText);
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(reportMarkdown))
        {
            sb.AppendLine("--- contain-workflow 结构化报告摘要 ---");
            sb.AppendLine(reportMarkdown.TrimEnd());
        }
        return sb.ToString().TrimEnd();
    }

    private static async Task PollProgressAsync(
        string progressPath,
        IProgress<BuildProgressUpdate> progress,
        CancellationToken token)
    {
        long position = 0;
        while (!token.IsCancellationRequested)
        {
            position = ReadNewProgressLines(progressPath, position, progress);
            await Task.Delay(120, token).ConfigureAwait(false);
        }
    }

    private static void FlushRemainingProgress(string progressPath, IProgress<BuildProgressUpdate> progress) =>
        _ = ReadNewProgressLines(progressPath, 0, progress);

    private static long ReadNewProgressLines(
        string progressPath,
        long offset,
        IProgress<BuildProgressUpdate> progress)
    {
        if (!File.Exists(progressPath))
            return offset;
        try
        {
            using var fs = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= offset)
                return offset;
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = reader.ReadToEnd();
            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
                return offset;

            var completeChunk = text[..(lastNewline + 1)];
            foreach (var rawLine in completeChunk.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawLine);
                    var root = doc.RootElement;
                    var stage = OptionalString(root, "stage");
                    var state = OptionalString(root, "state");
                    if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(state))
                        continue;
                    int? stepIdx = root.TryGetProperty("step_index", out var si) && si.TryGetInt32(out var siVal) ? siVal : null;
                    int? phaseTotal = root.TryGetProperty("phase_total", out var pt) && pt.TryGetInt32(out var ptVal) ? ptVal : null;
                    var stepName = OptionalString(root, "step_name");
                    progress.Report(new BuildProgressUpdate(stage, state, stepIdx, phaseTotal, stepName));
                }
                catch (JsonException) { }
            }
            return offset + Encoding.UTF8.GetByteCount(completeChunk);
        }
        catch (IOException)
        {
            return offset;
        }
    }

    public static WorkflowReportData ParseReport(string json, string expectedWorkflow = "source")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new EnvironmentCheckException("report_contract", "CLI 报告根节点不是 JSON 对象。");

            var schema = root.TryGetProperty("schema_version", out var sv) && sv.TryGetInt32(out var svValue) ? svValue : -1;
            var workflow = root.TryGetProperty("workflow", out var wf) && wf.ValueKind == JsonValueKind.String ? wf.GetString() ?? "" : "";
            if (schema != 1 || !string.Equals(workflow, expectedWorkflow, StringComparison.Ordinal))
                throw new EnvironmentCheckException("report_contract", $"CLI 报告版本或类型不匹配（要求 schema 1 / {expectedWorkflow}）。");

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
            var (code, stdout, stderr) = await RunProcessAsync(["--version"], commandTimeout, cancellationToken).ConfigureAwait(false);
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
        TimeSpan timeout,
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
        deadline.CancelAfter(timeout);
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
            throw new EnvironmentCheckException("cli_timeout", "操作超时，已停止随包 contain-workflow 进程。");
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
                    throw new EnvironmentCheckException("output_limit", "子进程输出超过安全上限。");
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
