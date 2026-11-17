using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

public enum EnvironmentOutcomeKind
{
    None,
    Ready,
    ActionRequired,
    InsufficientInfo,
    CheckerError
}

public sealed record EnvironmentItemRow(
    string RawName,
    string DisplayTitle,
    string StatusBadge,
    string SummaryLine,
    string RemedyLine,
    string RawDetail,
    string OfficialGuidanceLine = "");

/// <summary>
/// Drives the "检查项目环境" page. Folder selection only reads declaration file presence;
/// structured CLI checks run only on explicit user action and reject superseded project results.
/// </summary>
public sealed class EnvironmentCheckViewModel : ObservableObject, IDisposable
{
    private const string ManifestDigestPrefix = "canonical JSON SHA-256 ";

    private sealed record CheckSnapshot(
        string ProjectPath,
        string RequirementSignature,
        DateTimeOffset CheckedAt,
        IReadOnlyDictionary<string, WorkflowCheckItem> ChecksByName);

    private readonly IEnvironmentCheckService service;
    private readonly Func<DateTimeOffset> clock;
    private readonly LatestRequest requests = new();
    private readonly object operationsGate = new();
    private readonly HashSet<Task> pendingOperations = [];

    private bool disposed;
    private bool checking;
    private string folderInput = "";
    private ProjectFolderInfo? currentProject;
    private CheckSnapshot? previousSnapshot;
    private EnvironmentOutcomeKind outcome = EnvironmentOutcomeKind.None;
    private string headline = "";
    private string outcomeNote = "";
    private string lastCheckedText = "";
    private string comparisonNote = "";
    private string errorDetail = "";
    private string recheckFailureTitle = "";
    private string recheckFailureText = "";
    private string recheckFailureDetail = "";
    private string detailedReportText = "";
    private bool showDetailedReport;
    private bool passedExpanded;
    private string statusMessage = "";

    public EnvironmentCheckViewModel(IEnvironmentCheckService service, Func<DateTimeOffset>? clock = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    public ObservableCollection<EnvironmentItemRow> ActionItems { get; } = [];
    public ObservableCollection<EnvironmentItemRow> ResolvedItems { get; } = [];
    public ObservableCollection<EnvironmentItemRow> PendingItems { get; } = [];
    public ObservableCollection<EnvironmentItemRow> PassedItems { get; } = [];

    public bool IsChecking => checking;
    public bool HasProject => currentProject is not null;
    public string ProjectName => currentProject?.ProjectName ?? "尚未选择项目文件夹";
    public string ProjectPath => currentProject?.ProjectPath ?? "";
    public string FolderInput
    {
        get => folderInput;
        set => ApplyFolderInput(value ?? "");
    }

    public string SelectionScopeNote => currentProject is null ? "" : currentProject.ManifestKind switch
    {
        ProjectManifestKind.EnvironmentManifest =>
            "已识别 contain.environment.json。点击“开始检查”后只运行首版受支持的只读工具与 Windows 工具链探测；不会自动安装依赖、修改系统配置或执行构建。",
        ProjectManifestKind.DeclaredProjectFiles =>
            $"未找到 contain.environment.json；检测到 {string.Join("、", currentProject.DetectedFiles)}。点击“开始检查”将复用 infer 提取已声明的精确工具版本，无法从现有文件可靠判断的 MSVC/SDK、环境变量与构建项将标为“待确认”。",
        _ =>
            "未找到 contain.environment.json，也未发现受支持的根目录声明文件（Cargo.toml、rust-toolchain.toml、global.json）。点击“开始检查”只能核验本机 Windows x64 平台，项目依赖将标为“待确认”。"
    };

    public bool CanStartCheck => !disposed && !checking && currentProject is not null;
    public bool CanRecheck => !disposed && !checking && currentProject is not null && HasOutcome;
    public bool HasOutcome => outcome != EnvironmentOutcomeKind.None;
    public EnvironmentOutcomeKind Outcome => outcome;
    public bool IsReady => outcome == EnvironmentOutcomeKind.Ready;
    public bool IsActionRequired => outcome == EnvironmentOutcomeKind.ActionRequired;
    public bool IsInsufficientInfo => outcome == EnvironmentOutcomeKind.InsufficientInfo;
    public bool IsCheckerError => outcome == EnvironmentOutcomeKind.CheckerError;

    public string OutcomeBadge => outcome switch
    {
        EnvironmentOutcomeKind.Ready => "环境已满足",
        EnvironmentOutcomeKind.ActionRequired => "检查出缺项 / 版本不匹配",
        EnvironmentOutcomeKind.InsufficientInfo => "信息不足 · 含待确认项",
        EnvironmentOutcomeKind.CheckerError => "检查程序或配置出错",
        _ => ""
    };

    public string Headline => headline;
    public string OutcomeNote => outcomeNote;
    public string LastCheckedText => lastCheckedText;
    public string ComparisonNote => comparisonNote;
    public string ErrorDetail => errorDetail;
    public bool HasRecheckFailure => recheckFailureText.Length > 0;
    public string RecheckFailureTitle => recheckFailureTitle;
    public string RecheckFailureText => recheckFailureText;
    public string RecheckFailureDetail => recheckFailureDetail;
    public bool HasActionItems => ActionItems.Count > 0;
    public bool HasResolvedItems => ResolvedItems.Count > 0;
    public string ResolvedSectionTitle => $"本次复查已解决（{ResolvedItems.Count} 项）";
    public bool HasPendingItems => PendingItems.Count > 0;
    public bool HasPassedItems => PassedItems.Count > 0;
    public string PassedHeader => $"已满足的项目（{PassedItems.Count} 项）";
    public bool PassedExpanded
    {
        get => passedExpanded;
        set => SetProperty(ref passedExpanded, value);
    }

    public bool CanToggleDetailedReport => HasOutcome && detailedReportText.Length > 0;
    public bool ShowDetailedReport
    {
        get => showDetailedReport;
        set
        {
            if (SetProperty(ref showDetailedReport, value))
                OnPropertyChanged(nameof(DetailedReportButtonText));
        }
    }
    public string DetailedReportButtonText => showDetailedReport ? "收起详细报告" : "查看详细报告";
    public string DetailedReportText => detailedReportText;
    public string StatusMessage => statusMessage;
    public string CheckingBannerText => currentProject is null
        ? "正在检查项目环境…"
        : $"正在检查“{currentProject.ProjectName}”的本机开发环境，窗口可正常切换或浏览其他页面…";

    /// <summary>Cancelling the folder picker leaves the current project, results, and any active check untouched.</summary>
    public void CancelSelection() { }

    public bool SelectProject(string path)
    {
        if (disposed) return false;
        requests.Invalidate();
        checking = false;
        ResetProjectSession();

        try
        {
            var info = service.InspectProject(path);
            currentProject = info;
            folderInput = info.ProjectPath;
            statusMessage = $"已选择项目“{info.ProjectName}”，点击“开始检查”运行探测。";
            NotifyAll();
            return true;
        }
        catch (EnvironmentCheckException error)
        {
            currentProject = null;
            folderInput = path;
            outcome = EnvironmentOutcomeKind.CheckerError;
            headline = "无法打开所选项目文件夹";
            outcomeNote = error.Message;
            errorDetail = error.Diagnostic;
            detailedReportText = "";
            statusMessage = error.Message;
            NotifyAll();
            return false;
        }
    }

    private void ApplyFolderInput(string rawValue)
    {
        if (disposed || string.Equals(folderInput, rawValue, StringComparison.Ordinal)) return;
        folderInput = rawValue;
        OnPropertyChanged(nameof(FolderInput));

        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
        {
            requests.Invalidate();
            checking = false;
            currentProject = null;
            ResetProjectSession();
            statusMessage = "";
            NotifyAll();
            return;
        }

        if (Directory.Exists(trimmed))
        {
            SelectProject(trimmed);
            return;
        }

        requests.Invalidate();
        checking = false;
        currentProject = null;
        ResetProjectSession();
        statusMessage = "输入的文件夹路径尚不存在；可继续输入完整路径或点击“选择文件夹”。";
        NotifyAll();
    }

    public Task<bool> CheckAsync() => Track(() => CheckCoreAsync());

    private async Task<bool> CheckCoreAsync()
    {
        if (disposed || currentProject is null) return false;
        var targetProject = currentProject;
        var hasValidPriorForProject = previousSnapshot is not null
            && string.Equals(previousSnapshot.ProjectPath, targetProject.ProjectPath, StringComparison.OrdinalIgnoreCase);

        var ticket = requests.Begin();
        checking = true;
        if (hasValidPriorForProject)
        {
            ClearRecheckFailureBanner();
            statusMessage = $"正在重新检查“{targetProject.ProjectName}”…";
        }
        else
        {
            ClearOutcomeCore();
            statusMessage = $"正在调用随包 contain-workflow 检查“{targetProject.ProjectName}”…";
        }
        NotifyAll();

        try
        {
            var execution = await service.CheckAsync(targetProject.ProjectPath, ticket.Token).ConfigureAwait(true);
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            checking = false;
            ApplyReport(targetProject, execution);
            NotifyAll();
            return true;
        }
        catch (OperationCanceledException) when (!requests.IsCurrent(ticket))
        {
            return false;
        }
        catch (EnvironmentCheckException error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            checking = false;
            ApplyCheckerError(targetProject, error.Message, error.Diagnostic);
            NotifyAll();
            return false;
        }
        catch (Exception error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            checking = false;
            ApplyCheckerError(targetProject, "环境检查程序运行异常，未能完成本次检查。", $"{error.GetType().Name}: {error.Message}");
            NotifyAll();
            return false;
        }
    }

    public void ToggleDetailedReport()
    {
        if (!CanToggleDetailedReport) return;
        ShowDetailedReport = !ShowDetailedReport;
    }

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

    private bool IsStillCurrent(LatestRequest.Ticket ticket, string expectedProjectPath) =>
        requests.IsCurrent(ticket)
        && currentProject is not null
        && string.Equals(currentProject.ProjectPath, expectedProjectPath, StringComparison.OrdinalIgnoreCase);

    private void ApplyReport(ProjectFolderInfo project, EnvironmentCheckExecution execution)
    {
        var checkedAt = clock();
        var report = execution.Report;
        var currentSignature = ComputeRequirementSignature(report);

        ActionItems.Clear();
        ResolvedItems.Clear();
        PendingItems.Clear();
        PassedItems.Clear();
        errorDetail = "";
        comparisonNote = "";
        ClearRecheckFailureBanner();

        var canCompareWithPrevious = false;
        if (previousSnapshot is not null
            && string.Equals(previousSnapshot.ProjectPath, project.ProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(previousSnapshot.RequirementSignature, currentSignature, StringComparison.Ordinal))
            {
                canCompareWithPrevious = true;
            }
            else
            {
                comparisonNote = "检测到项目环境要求已变更，已按最新声明开始新的比较（修改项目要求不计为已解决）。";
            }
        }

        foreach (var check in report.Checks)
        {
            var row = FormatRow(check, report.ManifestSource);
            switch (check.Status)
            {
                case "failed":
                    ActionItems.Add(row);
                    break;
                case "passed":
                    PassedItems.Add(row);
                    if (canCompareWithPrevious
                        && previousSnapshot!.ChecksByName.TryGetValue(check.Name, out var prior)
                        && !string.Equals(prior.Status, "passed", StringComparison.Ordinal))
                    {
                        ResolvedItems.Add(FormatResolvedRow(prior, check));
                    }
                    break;
                default:
                    PendingItems.Add(row);
                    break;
            }
        }

        previousSnapshot = new CheckSnapshot(
            project.ProjectPath,
            currentSignature,
            checkedAt,
            BuildChecksMap(report.Checks));

        passedExpanded = false;
        showDetailedReport = false;
        lastCheckedText = $"检查时间：{FormatTimestamp(checkedAt)}";
        detailedReportText = BuildDetailedReport(project, execution, checkedAt);

        if (ActionItems.Count > 0)
        {
            outcome = EnvironmentOutcomeKind.ActionRequired;
            headline = BuildActionHeadline(ActionItems[0], ActionItems.Count);
            if (ResolvedItems.Count > 0)
            {
                outcomeNote = $"本次复查已确认解决 {ResolvedItems.Count} 项（见下方“本次复查已解决”）；仍缺的 {ActionItems.Count} 项继续置顶显示，已满足的 {PassedItems.Count} 项默认收起。";
                statusMessage = $"已完成“{project.ProjectName}”重新检查：已解决 {ResolvedItems.Count} 项，仍需补齐 {ActionItems.Count} 项。";
            }
            else
            {
                outcomeNote = PendingItems.Count > 0
                    ? $"另有 {PendingItems.Count} 项待确认，已满足的 {PassedItems.Count} 项已折叠在下方。按下方补齐建议操作后可点击“重新检查”。"
                    : $"共发现 {ActionItems.Count} 项缺失或版本不匹配；已满足的 {PassedItems.Count} 项默认收起。补齐后可点击“重新检查”。";
                statusMessage = $"已完成“{project.ProjectName}”环境检查：需要补齐 {ActionItems.Count} 项。";
            }
        }
        else if (PendingItems.Count > 0)
        {
            outcome = EnvironmentOutcomeKind.InsufficientInfo;
            if (ResolvedItems.Count > 0)
            {
                headline = $"{ResolvedItems[0].DisplayTitle} · 仍有 {PendingItems.Count} 项要求待确认";
                outcomeNote = $"本次复查新报告已确认解决 {ResolvedItems.Count} 项；当前没有明确缺项（已满足 {PassedItems.Count} 项），其余 {PendingItems.Count} 项需人工确认。";
                statusMessage = $"已完成“{project.ProjectName}”重新检查：已解决 {ResolvedItems.Count} 项，含 {PendingItems.Count} 项待确认。";
            }
            else
            {
                headline = report.ManifestSource == "inferred_from_project_files"
                    ? $"信息不足：已检查可识别声明，仍有 {PendingItems.Count} 项要求待确认"
                    : $"信息不足：未发现明确冲突，但有 {PendingItems.Count} 项待确认或未实测";
                outcomeNote = report.ManifestSource == "inferred_from_project_files"
                    ? $"该项目没有 contain.environment.json；已通过 infer 检查根目录支持文件中的已知要求（已满足 {PassedItems.Count} 项），其余工具链与构建方式需补充清单确认。"
                    : $"当前没有报错缺项（已满足 {PassedItems.Count} 项），但部分探测超出首版只读范围或尚未声明，不能判定为可直接构建。";
                statusMessage = $"已完成“{project.ProjectName}”环境检查：含 {PendingItems.Count} 项待确认。";
            }
        }
        else
        {
            outcome = EnvironmentOutcomeKind.Ready;
            if (ResolvedItems.Count == 1)
            {
                headline = $"{ResolvedItems[0].DisplayTitle} · “{project.ProjectName}”声明的 {PassedItems.Count} 项检查全部通过";
                outcomeNote = $"同一项目且要求未变的前提下重新检查通过：{ResolvedItems[0].SummaryLine} 其余已满足项可在下方展开或查看详细报告。";
                statusMessage = $"已完成“{project.ProjectName}”重新检查：{ResolvedItems[0].DisplayTitle}。";
            }
            else if (ResolvedItems.Count > 1)
            {
                headline = $"本次已解决 {ResolvedItems.Count} 项（含 {StripResolvedPrefix(ResolvedItems[0].DisplayTitle)}）· “{project.ProjectName}”全部 {PassedItems.Count} 项检查通过";
                outcomeNote = $"同一项目且要求未变的前提下重新检查通过，新报告已确认上述 {ResolvedItems.Count} 项恢复正常。";
                statusMessage = $"已完成“{project.ProjectName}”重新检查：已解决 {ResolvedItems.Count} 项，全部通过。";
            }
            else
            {
                headline = $"环境已满足：“{project.ProjectName}”声明的 {PassedItems.Count} 项检查全部通过";
                outcomeNote = "本机已具备该项目清单声明的平台、工具版本与 Windows 编译工具链。可展开下方列表或查看详细报告核对每项结果。";
                statusMessage = $"已完成“{project.ProjectName}”环境检查：全部 {PassedItems.Count} 项满足。";
            }
        }
    }

    private void ApplyCheckerError(ProjectFolderInfo project, string message, string diagnostic)
    {
        var failedAt = clock();
        if (previousSnapshot is not null
            && string.Equals(previousSnapshot.ProjectPath, project.ProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            var priorTimeText = FormatTimestamp(previousSnapshot.CheckedAt);
            var failTimeText = FormatTimestamp(failedAt);
            ResolvedItems.Clear();
            comparisonNote = "";
            lastCheckedText = $"上次有效结果时间：{priorTimeText}";
            recheckFailureTitle = $"重新检查未完成（失败时间：{failTimeText}）";
            recheckFailureText = $"{message} 当前保留 {priorTimeText} 的上次有效检查结果。";
            recheckFailureDetail = diagnostic;
            statusMessage = $"“{project.ProjectName}”重新检查出错（{failTimeText}），已保留 {priorTimeText} 的上次有效结果。";
            return;
        }

        ClearOutcomeCore();
        outcome = EnvironmentOutcomeKind.CheckerError;
        headline = "检查未能完成：检查程序或项目配置文件出错";
        outcomeNote = $"{message}（这不是“电脑缺少某项开发工具”的结论，请先检查桌面包完整性或项目配置文件格式。）";
        lastCheckedText = $"检查失败时间：{FormatTimestamp(failedAt)}";
        errorDetail = diagnostic;
        var sb = new StringBuilder();
        sb.AppendLine($"项目名称：{project.ProjectName}");
        sb.AppendLine($"项目路径：{project.ProjectPath}");
        sb.AppendLine($"失败时间：{FormatTimestamp(failedAt)}");
        sb.AppendLine($"错误说明：{message}");
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            sb.AppendLine();
            sb.AppendLine("诊断输出：");
            sb.AppendLine(diagnostic);
        }
        detailedReportText = sb.ToString().TrimEnd();
        statusMessage = $"“{project.ProjectName}”环境检查程序出错。";
    }

    private static string BuildActionHeadline(EnvironmentItemRow first, int count)
    {
        if (count == 1)
            return $"需要补齐 1 项：{first.SummaryLine}";
        return $"需要补齐 {count} 项：优先处理“{first.DisplayTitle}”（{first.SummaryLine}）";
    }

    public static EnvironmentItemRow FormatRow(WorkflowCheckItem item, string? manifestSource = null)
    {
        var label = FriendlyCheckTitle(item.Name);
        if (item.Status == "failed")
        {
            var badge = item.Outcome switch
            {
                "version_mismatch" => "版本不匹配",
                "unavailable" => "未检测到",
                "variable_missing" => "缺少变量",
                _ => "未满足"
            };
            var summary = item.Outcome switch
            {
                "version_mismatch" when !string.IsNullOrWhiteSpace(item.Required) && !string.IsNullOrWhiteSpace(item.Observed) =>
                    $"项目要求 {label} {item.Required}，当前检测到 {item.Observed}",
                "version_mismatch" when !string.IsNullOrWhiteSpace(item.Required) =>
                    $"项目要求 {label} 包含或匹配 {item.Required}，当前已安装项不匹配",
                "unavailable" when !string.IsNullOrWhiteSpace(item.Required) =>
                    $"未检测到可用的 {label}（项目要求 {item.Required}）",
                "variable_missing" when !string.IsNullOrWhiteSpace(item.Required) =>
                    $"缺少必需环境变量 {item.Required}（仅检查名称是否非空）",
                _ when !string.IsNullOrWhiteSpace(item.Required) =>
                    $"{label} 不满足要求（项目要求 {item.Required}）",
                _ => $"{label} 未通过检查"
            };

            var officialGuidance = item.Name == "tool:dotnet"
                ? BuildDotNetSdkGuidance(item.Required).GuidanceLine
                : "";

            var remedy = FormatProvenanceRemedy(item, manifestSource);
            return new EnvironmentItemRow(item.Name, label, badge, summary, remedy, item.Detail, officialGuidance);
        }

        if (item.Status == "passed")
        {
            var summary = !string.IsNullOrWhiteSpace(item.Observed)
                ? $"已满足：{item.Observed}"
                : item.Detail;
            return new EnvironmentItemRow(item.Name, label, "已满足", summary, "", item.Detail);
        }

        var pendingBadge = item.Status == "not_run" ? "未实测" : "待确认";
        var pendingSummary = item.Outcome == "unsupported_probe" && !string.IsNullOrWhiteSpace(item.Required)
            ? $"该项探测命令不在首版只读安全白名单内，未自动执行（声明要求：{item.Required}）"
            : item.Detail;
        var pendingRemedy = !string.IsNullOrWhiteSpace(item.Remedy) && item.Remedy != item.Detail
            ? FormatProvenanceRemedy(item, manifestSource)
            : "";
        return new EnvironmentItemRow(item.Name, label, pendingBadge, pendingSummary, pendingRemedy, item.Detail);
    }

    public static EnvironmentItemRow FormatResolvedRow(WorkflowCheckItem previous, WorkflowCheckItem current)
    {
        var label = FriendlyCheckTitle(current.Name);
        var confirmed = !string.IsNullOrWhiteSpace(current.Observed)
            ? current.Observed
            : current.Required;
        var title = !string.IsNullOrWhiteSpace(confirmed)
            ? $"已解决：{label} {confirmed}"
            : $"已解决：{label}";

        var summary = previous.Outcome switch
        {
            "version_mismatch" when !string.IsNullOrWhiteSpace(previous.Observed) && !string.IsNullOrWhiteSpace(confirmed) =>
                $"上次检查检测到 {previous.Observed}（版本不匹配），本次新报告已确认通过（检测到 {confirmed}）。",
            "unavailable" when !string.IsNullOrWhiteSpace(confirmed) =>
                $"上次检查未检测到可用项，本次新报告已确认通过（检测到 {confirmed}）。",
            _ when previous.Status == "failed" && !string.IsNullOrWhiteSpace(confirmed) =>
                $"上次检查未满足要求，本次新报告已确认通过（{confirmed}）。",
            _ =>
                $"上次状态为“{previous.Status}”，本次新报告已确认通过（{confirmed ?? current.Detail}）。"
        };

        return new EnvironmentItemRow(current.Name, title, "本次已解决", summary, "", current.Detail);
    }

    public static (bool IsSupportedReleaseBand, string GuidanceLine) BuildDotNetSdkGuidance(string? required)
    {
        if (string.IsNullOrWhiteSpace(required))
        {
            return (false, "版本提示：项目未声明具体的 .NET SDK 版本号，请先核对项目环境配置（第一版由你决定是否安装）。");
        }

        var trimmed = required.Trim();
        var parts = trimmed.Split('.');
        if (parts.Length == 3
            && parts.All(static p => p.Length > 0 && p.All(char.IsAsciiDigit))
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sdkPatch)
            && major is 8 or 9 or 10
            && minor == 0
            && sdkPatch is >= 100 and <= 499
            && (sdkPatch % 100) <= 59)
        {
            var url = $"https://dotnet.microsoft.com/download/dotnet/{major}.{minor}";
            return (
                true,
                $"官方获取入口：前往微软 .NET {major}.{minor} 下载页 {url} 获取 .NET SDK {trimmed}（Windows x64，可与本机现有 SDK 并行安装；安装后可用 dotnet --list-sdks 核对，是否安装由你决定）。");
        }

        return (
            false,
            $"版本提示：项目要求 .NET SDK {trimmed}，不在已知受支持的微软正式 SDK 发行段内，不承诺该版本可从官方渠道下载；请先核对项目要求（是否安装或调整由你决定）。");
    }

    private static string FormatProvenanceRemedy(WorkflowCheckItem item, string? manifestSource)
    {
        if (string.IsNullOrWhiteSpace(item.Remedy))
            return "检查说明：" + item.Detail;

        if (item.Name.StartsWith("tool:", StringComparison.Ordinal))
        {
            if (string.Equals(manifestSource, "inferred_from_project_files", StringComparison.Ordinal))
            {
                var sourceFile = item.Name == "tool:dotnet" ? "global.json" : "rust-toolchain.toml";
                return $"项目声明文件（{sourceFile}）推断说明：{item.Remedy}";
            }
            return $"项目清单（contain.environment.json）自带说明：{item.Remedy}";
        }

        return "下一步：" + item.Remedy;
    }

    public static string ComputeRequirementSignature(WorkflowReportData report)
    {
        var sb = new StringBuilder();
        sb.Append("source=").Append(report.ManifestSource ?? "contain.environment.json").Append('\n');
        foreach (var check in report.Checks)
        {
            if (check.Name == "environment_manifest"
                && check.Detail.StartsWith(ManifestDigestPrefix, StringComparison.Ordinal))
            {
                sb.Append("manifest_sha=").Append(check.Detail[ManifestDigestPrefix.Length..].Trim()).Append('\n');
                continue;
            }
            if (check.Name == "source_revision")
                continue;

            sb.Append(check.Name).Append('=').Append(check.Required ?? "").Append('\n');
        }
        return sb.ToString();
    }

    private static Dictionary<string, WorkflowCheckItem> BuildChecksMap(IReadOnlyList<WorkflowCheckItem> checks)
    {
        var map = new Dictionary<string, WorkflowCheckItem>(StringComparer.Ordinal);
        foreach (var check in checks)
        {
            map.TryAdd(check.Name, check);
        }
        return map;
    }

    private static string StripResolvedPrefix(string displayTitle) =>
        displayTitle.StartsWith("已解决：", StringComparison.Ordinal)
            ? displayTitle["已解决：".Length..]
            : displayTitle;

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    internal static string FriendlyCheckTitle(string rawName) => rawName switch
    {
        "platform" => "Windows x64 平台架构",
        "tool:rustc" => "Rust 编译器 (rustc)",
        "tool:cargo" => "Cargo 构建工具 (cargo)",
        "tool:rustup" => "Rust 目标平台组件 (rustup)",
        "tool:dotnet" => ".NET SDK (dotnet)",
        "tool:python" => "Python 解释器 (python)",
        "tool:git" => "Git 版本控制 (git)",
        "selected_windows_toolchain" => "VS C++ 工具集与 Windows SDK",
        "environment_manifest" => "环境清单标识 (contain.environment.json)",
        "source_revision" => "Git 源码提交状态",
        "requirement" => "待确认的环境或构建要求",
        "build plan" => "构建命令声明",
        _ when rawName.StartsWith("tool:", StringComparison.Ordinal) => $"工具 ({rawName["tool:".Length..]})",
        _ when rawName.StartsWith("variable:", StringComparison.Ordinal) => $"环境变量 ({rawName["variable:".Length..]})",
        _ => rawName
    };

    private static string BuildDetailedReport(ProjectFolderInfo project, EnvironmentCheckExecution execution, DateTimeOffset checkedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"项目名称：{project.ProjectName}");
        sb.AppendLine($"项目路径：{project.ProjectPath}");
        sb.AppendLine($"检查时间：{FormatTimestamp(checkedAt)}");
        sb.AppendLine($"清单来源：{execution.Report.ManifestSource ?? "contain.environment.json"}");
        sb.AppendLine($"CLI 退出码：{execution.ExitCode}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(execution.ReportMarkdown))
        {
            sb.AppendLine("--- contain-workflow report.md ---");
            sb.AppendLine(execution.ReportMarkdown.TrimEnd());
            sb.AppendLine();
        }
        sb.AppendLine("--- contain-workflow report.json ---");
        sb.AppendLine(execution.ReportJson.TrimEnd());
        return sb.ToString().TrimEnd();
    }

    private void ResetProjectSession()
    {
        previousSnapshot = null;
        ClearOutcomeCore();
    }

    private void ClearRecheckFailureBanner()
    {
        recheckFailureTitle = "";
        recheckFailureText = "";
        recheckFailureDetail = "";
    }

    private void ClearOutcomeCore()
    {
        ActionItems.Clear();
        ResolvedItems.Clear();
        PendingItems.Clear();
        PassedItems.Clear();
        outcome = EnvironmentOutcomeKind.None;
        headline = "";
        outcomeNote = "";
        lastCheckedText = "";
        comparisonNote = "";
        errorDetail = "";
        ClearRecheckFailureBanner();
        detailedReportText = "";
        showDetailedReport = false;
        passedExpanded = false;
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
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectPath));
        OnPropertyChanged(nameof(FolderInput));
        OnPropertyChanged(nameof(SelectionScopeNote));
        OnPropertyChanged(nameof(CanStartCheck));
        OnPropertyChanged(nameof(CanRecheck));
        OnPropertyChanged(nameof(HasOutcome));
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsActionRequired));
        OnPropertyChanged(nameof(IsInsufficientInfo));
        OnPropertyChanged(nameof(IsCheckerError));
        OnPropertyChanged(nameof(OutcomeBadge));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(OutcomeNote));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(ComparisonNote));
        OnPropertyChanged(nameof(ErrorDetail));
        OnPropertyChanged(nameof(HasRecheckFailure));
        OnPropertyChanged(nameof(RecheckFailureTitle));
        OnPropertyChanged(nameof(RecheckFailureText));
        OnPropertyChanged(nameof(RecheckFailureDetail));
        OnPropertyChanged(nameof(HasActionItems));
        OnPropertyChanged(nameof(HasResolvedItems));
        OnPropertyChanged(nameof(ResolvedSectionTitle));
        OnPropertyChanged(nameof(HasPendingItems));
        OnPropertyChanged(nameof(HasPassedItems));
        OnPropertyChanged(nameof(PassedHeader));
        OnPropertyChanged(nameof(PassedExpanded));
        OnPropertyChanged(nameof(CanToggleDetailedReport));
        OnPropertyChanged(nameof(ShowDetailedReport));
        OnPropertyChanged(nameof(DetailedReportButtonText));
        OnPropertyChanged(nameof(DetailedReportText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(CheckingBannerText));
    }
}
