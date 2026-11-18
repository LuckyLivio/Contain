using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

public enum PackageCheckState
{
    None,
    MissingSpec,
    Passed,
    Failed,
    CheckerOrSpecError
}

public sealed record PackageFailureItem(
    string Title,
    string Badge,
    string Explanation);

/// <summary>
/// Drives the "检查交付包" page for recipients of a Windows ZIP package.
/// Auto-discovers sibling package-spec.json when present, never fabricates expected hashes,
/// and runs bundled contain-workflow.exe verify strictly without --run.
/// </summary>
public sealed class PackageCheckViewModel : ObservableObject, IDisposable
{
    private readonly IPackageCheckService service;
    private readonly Func<DateTimeOffset> clock;
    private readonly LatestRequest requests = new();
    private readonly object operationsGate = new();
    private readonly HashSet<Task> pendingOperations = [];

    private bool disposed;
    private bool checking;
    private string zipPathInput = "";
    private string specPathInput = "";
    private PackageZipSelection? currentZip;
    private PackageSpecSelection? currentSpec;

    private PackageCheckState state = PackageCheckState.None;
    private string fileStatusBadge = "";
    private string launchStatusBadge = "";
    private string launchStatusNote = "";
    private string headline = "";
    private string summaryNote = "";
    private string checkedTimeText = "";
    private string errorDetail = "";
    private bool technicalDetailsExpanded;
    private string technicalDetailsText = "";
    private string statusMessage = "";

    public PackageCheckViewModel(IPackageCheckService service, Func<DateTimeOffset>? clock = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    public ObservableCollection<string> VerifiedSummaryItems { get; } = [];
    public ObservableCollection<PackageFailureItem> FailureItems { get; } = [];

    public bool IsChecking => checking;
    public bool HasZip => currentZip is not null;
    public string ZipFileName => currentZip?.ZipFileName ?? "尚未选择交付包 (.zip)";
    public string ZipPath => currentZip?.ZipPath ?? "";

    public string ZipPathInput
    {
        get => zipPathInput;
        set => ApplyZipPathInput(value ?? "");
    }

    public bool HasSpec => currentSpec is not null;
    public string SpecFileName => currentSpec?.SpecFileName ?? "";
    public string SpecPath => currentSpec?.SpecPath ?? "";
    public string SpecStatusLine => currentZip is null
        ? ""
        : currentSpec is null
            ? "未在 ZIP 同目录找到 package-spec.json。"
            : currentSpec.IsAutoDiscovered
                ? $"采用清单：{currentSpec.SpecFileName}（同目录自动找到）"
                : $"采用清单：{currentSpec.SpecFileName}（手动选择：{currentSpec.SpecPath}）";

    public string SpecPathInput
    {
        get => specPathInput;
        set => ApplySpecPathInput(value ?? "");
    }

    public PackageCheckState State => state;
    public bool IsMissingSpec => state == PackageCheckState.MissingSpec;
    public bool IsPassed => state == PackageCheckState.Passed;
    public bool IsFailed => state == PackageCheckState.Failed;
    public bool IsCheckerOrSpecError => state == PackageCheckState.CheckerOrSpecError;
    public bool HasReportOutcome => state is PackageCheckState.Passed or PackageCheckState.Failed;
    public bool ShowSpecPickerSurface => currentZip is not null && state is PackageCheckState.MissingSpec or PackageCheckState.Failed or PackageCheckState.CheckerOrSpecError;

    public string MissingSpecExplanation =>
        "校验清单（package-spec.json）是交付者随包提供的预期文件与哈希信息，用来核对收到的压缩包是否完整一致。";

    public string PickSpecButtonText => currentSpec is null ? "选择校验清单" : "更换校验清单";
    public bool CanPickZip => !disposed;
    public bool CanPickSpec => !disposed && currentZip is not null && !checking;
    public bool CanRecheck => !disposed && !checking && currentZip is not null && currentSpec is not null;

    public string FileStatusBadge => fileStatusBadge;
    public string LaunchStatusBadge => launchStatusBadge;
    public string LaunchStatusNote => launchStatusNote;
    public string Headline => headline;
    public string SummaryNote => summaryNote;
    public string CheckedTimeText => checkedTimeText;
    public string ErrorDetail => errorDetail;
    public bool HasVerifiedSummaryItems => VerifiedSummaryItems.Count > 0;
    public bool HasFailureItems => FailureItems.Count > 0;

    public bool CanShowTechnicalDetails => technicalDetailsText.Length > 0;
    public bool TechnicalDetailsExpanded
    {
        get => technicalDetailsExpanded;
        set => SetProperty(ref technicalDetailsExpanded, value);
    }
    public string TechnicalDetailsText => technicalDetailsText;

    public string CheckingBannerText => currentZip is null
        ? "正在静态核对交付包…"
        : $"正在只读核对“{currentZip.ZipFileName}”的文件完整性与哈希（不解压、不启动包内程序）…";

    public string StatusMessage => statusMessage;

    /// <summary>Cancelling a file dialog preserves the current ZIP, spec, and result untouched.</summary>
    public void CancelSelection() { }

    public Task<bool> SelectZipAsync(string path) => Track(() => SelectZipCoreAsync(path));

    private async Task<bool> SelectZipCoreAsync(string path)
    {
        if (disposed) return false;
        requests.Invalidate();
        checking = false;
        ResetAllCore();

        PackageZipSelection zipSelection;
        try
        {
            zipSelection = service.InspectZip(path);
        }
        catch (EnvironmentCheckException error)
        {
            zipPathInput = path;
            state = PackageCheckState.CheckerOrSpecError;
            headline = "无法打开所选的 ZIP 压缩包";
            summaryNote = error.Message;
            errorDetail = error.Diagnostic;
            statusMessage = error.Message;
            NotifyAll();
            return false;
        }

        currentZip = zipSelection;
        zipPathInput = zipSelection.ZipPath;

        if (!string.IsNullOrEmpty(zipSelection.AutoDiscoveredSpecPath))
        {
            var autoSpec = service.InspectSpec(zipSelection.AutoDiscoveredSpecPath, isAutoDiscovered: true);
            currentSpec = autoSpec;
            specPathInput = autoSpec.SpecPath;
            return await RunVerificationCoreAsync(zipSelection, autoSpec).ConfigureAwait(true);
        }

        state = PackageCheckState.MissingSpec;
        headline = $"同目录未找到交付清单（{WorkflowCliPackageService.DefaultSpecFileName}）";
        summaryNote = MissingSpecExplanation;
        statusMessage = $"已选择“{zipSelection.ZipFileName}”；请选择配套的 package-spec.json 继续核对。";
        NotifyAll();
        return false;
    }

    public Task<bool> SelectSpecAsync(string specPath) => Track(() => SelectSpecCoreAsync(specPath));

    private async Task<bool> SelectSpecCoreAsync(string specPath)
    {
        if (disposed || currentZip is null) return false;
        var targetZip = currentZip;
        requests.Invalidate();
        checking = false;
        ClearResultCore();

        PackageSpecSelection specSelection;
        try
        {
            specSelection = service.InspectSpec(specPath, isAutoDiscovered: false);
        }
        catch (EnvironmentCheckException error)
        {
            currentSpec = null;
            specPathInput = specPath;
            state = PackageCheckState.CheckerOrSpecError;
            headline = "无法读取所选的校验清单文件";
            summaryNote = error.Message;
            errorDetail = error.Diagnostic;
            statusMessage = error.Message;
            NotifyAll();
            return false;
        }

        currentSpec = specSelection;
        specPathInput = specSelection.SpecPath;
        return await RunVerificationCoreAsync(targetZip, specSelection).ConfigureAwait(true);
    }

    public Task<bool> RecheckAsync() => Track(() =>
    {
        if (disposed || checking || currentZip is null || currentSpec is null)
            return Task.FromResult(false);
        return RunVerificationCoreAsync(currentZip, currentSpec);
    });

    private void ApplyZipPathInput(string rawValue)
    {
        if (disposed || string.Equals(zipPathInput, rawValue, StringComparison.Ordinal))
            return;

        zipPathInput = rawValue;
        OnPropertyChanged(nameof(ZipPathInput));

        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
        {
            requests.Invalidate();
            checking = false;
            ResetAllCore();
            statusMessage = "";
            NotifyAll();
            return;
        }

        if (trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
        {
            _ = SelectZipAsync(trimmed);
            return;
        }

        requests.Invalidate();
        checking = false;
        ResetAllCore();
        zipPathInput = rawValue;
        statusMessage = "输入的 ZIP 路径尚不存在；可继续输入完整 .zip 文件路径或点击“选择 ZIP 文件”。";
        NotifyAll();
    }

    private void ApplySpecPathInput(string rawValue)
    {
        if (disposed || string.Equals(specPathInput, rawValue, StringComparison.Ordinal))
            return;

        specPathInput = rawValue;
        OnPropertyChanged(nameof(SpecPathInput));

        if (currentZip is null)
            return;

        var trimmed = rawValue.Trim();
        if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
        {
            _ = SelectSpecAsync(trimmed);
        }
    }

    private async Task<bool> RunVerificationCoreAsync(
        PackageZipSelection targetZip,
        PackageSpecSelection targetSpec)
    {
        var ticket = requests.Begin();
        checking = true;
        ClearResultCore();
        statusMessage = $"正在调用随包 contain-workflow 静态校验“{targetZip.ZipFileName}”…";
        NotifyAll();

        try
        {
            var execution = await service.VerifyStaticAsync(
                targetZip.ZipPath,
                targetSpec.SpecPath,
                ticket.Token).ConfigureAwait(true);

            if (!IsStillCurrent(ticket, targetZip.ZipPath, targetSpec.SpecPath))
                return false;

            checking = false;
            var passed = ApplyVerificationReport(targetZip, targetSpec, execution);
            NotifyAll();
            return passed;
        }
        catch (OperationCanceledException) when (!requests.IsCurrent(ticket))
        {
            return false;
        }
        catch (EnvironmentCheckException error)
        {
            if (!IsStillCurrent(ticket, targetZip.ZipPath, targetSpec.SpecPath))
                return false;

            checking = false;
            ApplySpecOrCheckerError(targetZip, targetSpec, error.Message, error.Diagnostic);
            NotifyAll();
            return false;
        }
        catch (Exception error)
        {
            if (!IsStillCurrent(ticket, targetZip.ZipPath, targetSpec.SpecPath))
                return false;

            checking = false;
            ApplySpecOrCheckerError(
                targetZip,
                targetSpec,
                "交付包校验程序运行异常，未能完成本次静态核对。",
                $"{error.GetType().Name}: {error.Message}");
            NotifyAll();
            return false;
        }
    }

    private bool IsStillCurrent(LatestRequest.Ticket ticket, string expectedZipPath, string expectedSpecPath) =>
        requests.IsCurrent(ticket)
        && currentZip is not null
        && currentSpec is not null
        && string.Equals(currentZip.ZipPath, expectedZipPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(currentSpec.SpecPath, expectedSpecPath, StringComparison.OrdinalIgnoreCase);

    private bool ApplyVerificationReport(
        PackageZipSelection zip,
        PackageSpecSelection spec,
        PackageVerificationExecution execution)
    {
        var checkedAt = clock();
        checkedTimeText = $"检查时间：{FormatTimestamp(checkedAt)}";
        technicalDetailsExpanded = false;

        var checks = execution.Report.Checks;
        var launchIndex = -1;
        for (var i = 0; i < checks.Count; i++)
        {
            if (string.Equals(checks[i].Name, "launch", StringComparison.Ordinal))
            {
                launchIndex = i;
                break;
            }
        }

        var staticChecks = launchIndex >= 0
            ? checks.Take(launchIndex).ToList()
            : checks.Where(static c => c.Status != "not_run").ToList();
        var runtimeChecks = launchIndex >= 0
            ? checks.Skip(launchIndex).ToList()
            : checks.Where(static c => c.Status == "not_run").ToList();

        launchStatusBadge = "启动未测试";
        var unrunTestCount = Math.Max(0, runtimeChecks.Count(static c => c.Name is not ("launch" or "specified_tests")));

        var staticPassed = staticChecks.Count > 0 && staticChecks.All(static c => c.Status == "passed");
        technicalDetailsText = BuildTechnicalDetails(zip, spec, execution, staticChecks, runtimeChecks, checkedAt);

        if (staticPassed)
        {
            state = PackageCheckState.Passed;
            fileStatusBadge = "文件校验通过";
            headline = $"文件与交付清单一致：“{zip.ZipFileName}”";
            summaryNote = $"所选压缩包与清单“{spec.SpecFileName}”声明的文件身份、入口与必需成员全部吻合。";
            launchStatusNote = unrunTestCount > 0
                ? $"本次仅做只读静态核对，未解压或启动包内程序（启动检查与 {unrunTestCount} 项预设运行测试均未执行）。"
                : "本次仅做只读静态核对，未解压或启动包内任何程序。";

            PopulatePassedSummary(staticChecks);
            statusMessage = $"已完成“{zip.ZipFileName}”校验：文件校验通过，启动未测试。";
            return true;
        }

        state = PackageCheckState.Failed;
        fileStatusBadge = "文件校验失败";
        launchStatusNote = "因文件校验未通过且桌面端不自动执行包内程序，启动检查与运行测试均未执行。";
        PopulateFailureReasons(zip, spec, staticChecks);
        statusMessage = $"已完成“{zip.ZipFileName}”校验：文件校验失败（启动未测试）。";
        return false;
    }

    private void PopulatePassedSummary(IReadOnlyList<WorkflowCheckItem> staticChecks)
    {
        VerifiedSummaryItems.Clear();

        if (staticChecks.Any(static c => c.Name == "package_identity" && c.Status == "passed"))
        {
            VerifiedSummaryItems.Add("压缩包整体 SHA-256 哈希与交付清单完全一致");
        }

        var entryCheck = staticChecks.FirstOrDefault(static c => c.Name == "static_entry" && c.Status == "passed");
        if (entryCheck is not null)
        {
            var entryName = ExtractEntryName(entryCheck.Detail);
            VerifiedSummaryItems.Add(string.IsNullOrEmpty(entryName)
                ? "主启动程序存在且具备有效的 Windows x64 PE 文件头（未实际加载运行）"
                : $"主启动程序 {entryName} 存在且具备有效的 Windows x64 PE 文件头（未实际加载运行）");
        }

        var requiredFiles = staticChecks
            .Where(static c => c.Name.StartsWith("static_file:", StringComparison.Ordinal) && c.Status == "passed")
            .Select(static c => c.Name["static_file:".Length..])
            .ToList();
        if (requiredFiles.Count > 0)
        {
            if (requiredFiles.Count <= 4)
            {
                VerifiedSummaryItems.Add($"清单声明的 {requiredFiles.Count} 个必需随包文件全部存在（{string.Join("、", requiredFiles)}）");
            }
            else
            {
                var sample = string.Join("、", requiredFiles.Take(3));
                VerifiedSummaryItems.Add($"清单声明的 {requiredFiles.Count} 个必需随包文件全部存在（含 {sample} 等）");
            }
        }

        if (staticChecks.Any(static c => c.Name == "static_contents" && c.Status == "passed"))
        {
            VerifiedSummaryItems.Add("包内 CONTENTS.json 记录的全部文件列表、字节大小与逐文件 SHA-256 核对一致");
        }

        var testExeCount = staticChecks.Count(static c => c.Name.StartsWith("static_test:", StringComparison.Ordinal) && c.Status == "passed");
        if (testExeCount > 0)
        {
            VerifiedSummaryItems.Add($"清单声明的 {testExeCount} 项预设自检对应的包内程序均存在（仅确认文件存在，未运行）");
        }
    }

    private void PopulateFailureReasons(
        PackageZipSelection zip,
        PackageSpecSelection spec,
        IReadOnlyList<WorkflowCheckItem> staticChecks)
    {
        FailureItems.Clear();

        var archiveFail = staticChecks.FirstOrDefault(static c => c.Name == "static_archive" && c.Status == "failed");
        if (archiveFail is not null)
        {
            headline = $"文件校验失败：“{zip.ZipFileName}”压缩包损坏或包含不安全路径";
            summaryNote = "无法安全读取该 ZIP 文件，未继续核对包内成员。请重新下载或联系交付方重发完好压缩包。";
            FailureItems.Add(new PackageFailureItem(
                "压缩包无法读取或违反安全边界",
                "文件损坏",
                "ZIP 格式无效、包含越界/重复/符号链接成员，或超过 1 GiB / 10,000 个成员限制。"));
            return;
        }

        var identityFailed = staticChecks.Any(static c => c.Name == "package_identity" && c.Status == "failed");
        var entryFail = staticChecks.FirstOrDefault(static c => c.Name == "static_entry" && c.Status == "failed");
        var missingFiles = staticChecks
            .Where(static c => c.Name.StartsWith("static_file:", StringComparison.Ordinal) && c.Status == "failed")
            .Select(static c => c.Name["static_file:".Length..])
            .ToList();
        var missingTestExes = staticChecks
            .Where(static c => c.Name.StartsWith("static_test:", StringComparison.Ordinal) && c.Status == "failed")
            .Select(static c => c.Name["static_test:".Length..])
            .ToList();
        var contentsFailed = staticChecks.Any(static c => c.Name == "static_contents" && c.Status == "failed");

        // Case 1: Only package_identity failed while all declared entry/files (+ internal CONTENTS if present) passed -> spec belongs to another ZIP/version!
        if (identityFailed
            && entryFail is null
            && missingFiles.Count == 0
            && missingTestExes.Count == 0
            && !contentsFailed)
        {
            headline = "文件校验失败：这份清单对应另一个 ZIP（整体哈希不匹配）";
            summaryNote = $"当前压缩包内必需文件齐全，但压缩包整体 SHA-256 与“{spec.SpecFileName}”不一致。若选错了版本或别的包的清单，可点击“更换校验清单”。";
            FailureItems.Add(new PackageFailureItem(
                "这份清单对应另一个 ZIP",
                "哈希不匹配",
                $"压缩包结构完好，但实际 SHA-256 与“{spec.SpecFileName}”记录的预期值不同（常见于选错了旧版/其他包的清单，或 ZIP 被重新压缩过）。"));
            return;
        }

        // Case 2: Missing required files, missing/invalid entry, or tampered package contents
        if (missingFiles.Count == 1 && identityFailed)
        {
            headline = $"文件校验失败：压缩包身份不匹配，且缺少 {missingFiles[0]}";
        }
        else if (missingFiles.Count > 1 && identityFailed)
        {
            headline = $"文件校验失败：压缩包身份不匹配，且缺少 {missingFiles.Count} 个必需文件（含 {missingFiles[0]}）";
        }
        else if (missingFiles.Count > 0)
        {
            headline = $"文件校验失败：缺少 {missingFiles[0]} 等必需文件";
        }
        else if (entryFail is not null)
        {
            headline = entryFail.Detail.Contains("missing", StringComparison.OrdinalIgnoreCase)
                ? "文件校验失败：压缩包缺少清单声明的主启动程序（或这份清单对应另一个软件包）"
                : "文件校验失败：主启动程序不是有效的 Windows x64 可执行文件";
        }
        else
        {
            headline = $"文件校验失败：“{zip.ZipFileName}”与交付清单不一致";
        }

        summaryNote = "请优先核对下方列出的具体缺件与不匹配原因；若当前选错了别的软件包的清单，可点击“更换校验清单”。";

        foreach (var missing in missingFiles.Take(20))
        {
            FailureItems.Add(new PackageFailureItem(
                $"缺少 {missing}",
                "缺少必需文件",
                $"交付清单要求压缩包内包含“{missing}”，但当前 ZIP 中未找到该文件。请勿解压运行不完整的软件包。"));
        }
        if (missingFiles.Count > 20)
        {
            FailureItems.Add(new PackageFailureItem(
                $"另有 {missingFiles.Count - 20} 个必需文件缺失",
                "缺少必需文件",
                "可在下方展开“查看详细哈希与技术信息”查看完整缺失列表。"));
        }

        if (entryFail is not null)
        {
            if (entryFail.Detail.Contains("missing", StringComparison.OrdinalIgnoreCase))
            {
                FailureItems.Add(new PackageFailureItem(
                    "缺少清单声明的主启动程序",
                    "入口缺失",
                    "当前 ZIP 中不存在该清单要求的入口 EXE。这通常说明这份清单对应另一个不同的 ZIP，或压缩包打包不完整。"));
            }
            else
            {
                var entryName = ExtractEntryName(entryFail.Detail);
                FailureItems.Add(new PackageFailureItem(
                    string.IsNullOrEmpty(entryName) ? "主启动程序架构无效" : $"主启动程序 {entryName} 架构无效",
                    "PE 头不符",
                    "入口文件未通过 Windows x64 PE 可执行头静态检查。"));
            }
        }

        if (identityFailed)
        {
            FailureItems.Add(new PackageFailureItem(
                "压缩包身份（SHA-256）不匹配",
                "身份不匹配",
                $"当前 ZIP 的实际 SHA-256 与清单“{spec.SpecFileName}”记录的交付哈希不一致（具体哈希值可在下方展开查看）。"));
        }

        if (contentsFailed)
        {
            FailureItems.Add(new PackageFailureItem(
                "包内 CONTENTS.json 成员清单校验失败",
                "内部清单不符",
                "ZIP 内部文件列表、字节大小或逐文件哈希与包内 CONTENTS.json 封装记录不一致。"));
        }

        foreach (var testName in missingTestExes)
        {
            FailureItems.Add(new PackageFailureItem(
                $"缺少自检可执行文件（{testName}）",
                "缺少文件",
                "清单声明的预设自检程序不在当前压缩包内。"));
        }
    }

    private void ApplySpecOrCheckerError(
        PackageZipSelection zip,
        PackageSpecSelection spec,
        string message,
        string diagnostic)
    {
        var failedAt = clock();
        state = PackageCheckState.CheckerOrSpecError;
        checkedTimeText = $"失败时间：{FormatTimestamp(failedAt)}";
        headline = $"无法完成校验：清单“{spec.SpecFileName}”或校验组件出错";
        summaryNote = $"{message} 可点击“更换校验清单”重新选择正确的 package-spec.json。";
        errorDetail = diagnostic;

        var sb = new StringBuilder();
        sb.AppendLine($"待测 ZIP：{zip.ZipPath}");
        sb.AppendLine($"所选清单：{spec.SpecPath}");
        sb.AppendLine($"失败时间：{FormatTimestamp(failedAt)}");
        sb.AppendLine($"错误说明：{message}");
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            sb.AppendLine();
            sb.AppendLine("诊断输出：");
            sb.AppendLine(diagnostic);
        }
        technicalDetailsExpanded = false;
        technicalDetailsText = sb.ToString().TrimEnd();
        statusMessage = $"“{zip.ZipFileName}”校验未能完成：{message}";
    }

    private static string BuildTechnicalDetails(
        PackageZipSelection zip,
        PackageSpecSelection spec,
        PackageVerificationExecution execution,
        IReadOnlyList<WorkflowCheckItem> staticChecks,
        IReadOnlyList<WorkflowCheckItem> runtimeChecks,
        DateTimeOffset checkedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"待测压缩包：{zip.ZipPath}");
        sb.AppendLine($"采用校验清单：{spec.SpecPath}（{(spec.IsAutoDiscovered ? "同目录自动匹配" : "手动选择")}）");
        sb.AppendLine($"检查时间：{FormatTimestamp(checkedAt)}");
        sb.AppendLine($"CLI 退出码：{execution.ExitCode}（静态检查不传 --run）");

        var idCheck = staticChecks.FirstOrDefault(static c => c.Name == "package_identity");
        if (idCheck is not null && TryParseIdentityHashes(idCheck.Detail, out var actualHash, out var expectedHash))
        {
            sb.AppendLine($"清单预期 ZIP SHA-256：{expectedHash}");
            sb.AppendLine($"当前实际 ZIP SHA-256：{actualHash}");
        }

        var entryCheck = staticChecks.FirstOrDefault(static c => c.Name == "static_entry");
        if (entryCheck is not null)
        {
            sb.AppendLine($"启动入口静态检查：{entryCheck.Detail}");
        }

        var passedStatic = staticChecks.Count(static c => c.Status == "passed");
        sb.AppendLine($"静态项统计：通过 {passedStatic} / {staticChecks.Count} 项；运行项未测试：{runtimeChecks.Count} 项");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(execution.ReportMarkdown))
        {
            sb.AppendLine("--- contain-workflow report.md ---");
            sb.AppendLine(execution.ReportMarkdown.TrimEnd());
        }
        return sb.ToString().TrimEnd();
    }

    private static bool TryParseIdentityHashes(string detail, out string actualSha, out string expectedSha)
    {
        // Format: "actual ZIP SHA-256 <64hex>; expected <64hex>"
        actualSha = "";
        expectedSha = "";
        const string actualPrefix = "actual ZIP SHA-256 ";
        const string expectedToken = "; expected ";
        if (!detail.StartsWith(actualPrefix, StringComparison.Ordinal))
            return false;
        var sepIdx = detail.IndexOf(expectedToken, StringComparison.Ordinal);
        if (sepIdx <= actualPrefix.Length)
            return false;
        actualSha = detail[actualPrefix.Length..sepIdx].Trim();
        expectedSha = detail[(sepIdx + expectedToken.Length)..].Trim();
        return actualSha.Length > 0 && expectedSha.Length > 0;
    }

    private static string ExtractEntryName(string detail)
    {
        var semi = detail.IndexOf(';');
        return semi > 0 ? detail[..semi].Trim() : "";
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public async Task CloseAsync()
    {
        requests.Dispose();
        disposed = true;
        Task[] snapshot;
        lock (operationsGate) snapshot = [.. pendingOperations];
        try { await Task.WhenAll(snapshot).ConfigureAwait(true); }
        catch { /* Shutdown awaits child cleanup only. */ }
    }

    public void Dispose()
    {
        disposed = true;
        requests.Dispose();
    }

    private void ResetAllCore()
    {
        currentZip = null;
        currentSpec = null;
        zipPathInput = "";
        specPathInput = "";
        ClearResultCore();
    }

    private void ClearResultCore()
    {
        VerifiedSummaryItems.Clear();
        FailureItems.Clear();
        state = PackageCheckState.None;
        fileStatusBadge = "";
        launchStatusBadge = "";
        launchStatusNote = "";
        headline = "";
        summaryNote = "";
        checkedTimeText = "";
        errorDetail = "";
        technicalDetailsExpanded = false;
        technicalDetailsText = "";
    }

    private Task<T> Track<T>(Func<Task<T>> operation)
    {
        var task = operation();
        lock (operationsGate)
        {
            if (!task.IsCompleted)
            {
                pendingOperations.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (operationsGate) pendingOperations.Remove(completed);
                }, TaskScheduler.Default);
            }
        }
        return task;
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(HasZip));
        OnPropertyChanged(nameof(ZipFileName));
        OnPropertyChanged(nameof(ZipPath));
        OnPropertyChanged(nameof(ZipPathInput));
        OnPropertyChanged(nameof(HasSpec));
        OnPropertyChanged(nameof(SpecFileName));
        OnPropertyChanged(nameof(SpecPath));
        OnPropertyChanged(nameof(SpecStatusLine));
        OnPropertyChanged(nameof(SpecPathInput));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsMissingSpec));
        OnPropertyChanged(nameof(IsPassed));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCheckerOrSpecError));
        OnPropertyChanged(nameof(HasReportOutcome));
        OnPropertyChanged(nameof(ShowSpecPickerSurface));
        OnPropertyChanged(nameof(MissingSpecExplanation));
        OnPropertyChanged(nameof(PickSpecButtonText));
        OnPropertyChanged(nameof(CanPickZip));
        OnPropertyChanged(nameof(CanPickSpec));
        OnPropertyChanged(nameof(CanRecheck));
        OnPropertyChanged(nameof(FileStatusBadge));
        OnPropertyChanged(nameof(LaunchStatusBadge));
        OnPropertyChanged(nameof(LaunchStatusNote));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(SummaryNote));
        OnPropertyChanged(nameof(CheckedTimeText));
        OnPropertyChanged(nameof(ErrorDetail));
        OnPropertyChanged(nameof(HasVerifiedSummaryItems));
        OnPropertyChanged(nameof(HasFailureItems));
        OnPropertyChanged(nameof(CanShowTechnicalDetails));
        OnPropertyChanged(nameof(TechnicalDetailsExpanded));
        OnPropertyChanged(nameof(TechnicalDetailsText));
        OnPropertyChanged(nameof(CheckingBannerText));
        OnPropertyChanged(nameof(StatusMessage));
    }
}
