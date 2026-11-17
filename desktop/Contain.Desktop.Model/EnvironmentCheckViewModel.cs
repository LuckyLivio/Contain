using System.Collections.ObjectModel;
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
    string RawDetail);

/// <summary>
/// Drives the "检查项目环境" page. Folder selection only reads declaration file presence;
/// structured CLI checks run only on explicit user action and reject superseded project results.
/// </summary>
public sealed class EnvironmentCheckViewModel : ObservableObject, IDisposable
{
    private readonly IEnvironmentCheckService service;
    private readonly LatestRequest requests = new();
    private readonly object operationsGate = new();
    private readonly HashSet<Task> pendingOperations = [];

    private bool disposed;
    private bool checking;
    private string folderInput = "";
    private ProjectFolderInfo? currentProject;
    private EnvironmentOutcomeKind outcome = EnvironmentOutcomeKind.None;
    private string headline = "";
    private string outcomeNote = "";
    private string errorDetail = "";
    private string detailedReportText = "";
    private bool showDetailedReport;
    private bool passedExpanded;
    private string statusMessage = "";

    public EnvironmentCheckViewModel(IEnvironmentCheckService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public ObservableCollection<EnvironmentItemRow> ActionItems { get; } = [];
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
    public string ErrorDetail => errorDetail;
    public bool HasActionItems => ActionItems.Count > 0;
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
        ClearOutcomeCore();

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
            ClearOutcomeCore();
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
        ClearOutcomeCore();
        statusMessage = "输入的文件夹路径尚不存在；可继续输入完整路径或点击“选择文件夹”。";
        NotifyAll();
    }

    public Task<bool> CheckAsync() => Track(() => CheckCoreAsync());

    private async Task<bool> CheckCoreAsync()
    {
        if (disposed || currentProject is null) return false;
        var targetProject = currentProject;
        var ticket = requests.Begin();
        checking = true;
        ClearOutcomeCore();
        statusMessage = $"正在调用随包 contain-workflow 检查“{targetProject.ProjectName}”…";
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
        var report = execution.Report;
        foreach (var check in report.Checks)
        {
            var row = FormatRow(check);
            switch (check.Status)
            {
                case "failed":
                    ActionItems.Add(row);
                    break;
                case "passed":
                    PassedItems.Add(row);
                    break;
                default:
                    PendingItems.Add(row);
                    break;
            }
        }

        passedExpanded = false;
        showDetailedReport = false;
        detailedReportText = BuildDetailedReport(project, execution);

        if (ActionItems.Count > 0)
        {
            outcome = EnvironmentOutcomeKind.ActionRequired;
            headline = BuildActionHeadline(ActionItems[0], ActionItems.Count);
            outcomeNote = PendingItems.Count > 0
                ? $"另有 {PendingItems.Count} 项待确认，已满足的 {PassedItems.Count} 项已折叠在下方。按下方补齐建议操作后可点击“重新检查”。"
                : $"共发现 {ActionItems.Count} 项缺失或版本不匹配；已满足的 {PassedItems.Count} 项默认收起。补齐后可点击“重新检查”。";
            statusMessage = $"已完成“{project.ProjectName}”环境检查：需要补齐 {ActionItems.Count} 项。";
        }
        else if (PendingItems.Count > 0)
        {
            outcome = EnvironmentOutcomeKind.InsufficientInfo;
            headline = report.ManifestSource == "inferred_from_project_files"
                ? $"信息不足：已检查可识别声明，仍有 {PendingItems.Count} 项要求待确认"
                : $"信息不足：未发现明确冲突，但有 {PendingItems.Count} 项待确认或未实测";
            outcomeNote = report.ManifestSource == "inferred_from_project_files"
                ? $"该项目没有 contain.environment.json；已通过 infer 检查根目录支持文件中的已知要求（已满足 {PassedItems.Count} 项），其余工具链与构建方式需补充清单确认。"
                : $"当前没有报错缺项（已满足 {PassedItems.Count} 项），但部分探测超出首版只读范围或尚未声明，不能判定为可直接构建。";
            statusMessage = $"已完成“{project.ProjectName}”环境检查：含 {PendingItems.Count} 项待确认。";
        }
        else
        {
            outcome = EnvironmentOutcomeKind.Ready;
            headline = $"环境已满足：“{project.ProjectName}”声明的 {PassedItems.Count} 项检查全部通过";
            outcomeNote = "本机已具备该项目清单声明的平台、工具版本与 Windows 编译工具链。可展开下方列表或查看详细报告核对每项结果。";
            statusMessage = $"已完成“{project.ProjectName}”环境检查：全部 {PassedItems.Count} 项满足。";
        }
    }

    private void ApplyCheckerError(ProjectFolderInfo project, string message, string diagnostic)
    {
        outcome = EnvironmentOutcomeKind.CheckerError;
        headline = "检查未能完成：检查程序或项目配置文件出错";
        outcomeNote = $"{message}（这不是“电脑缺少某项开发工具”的结论，请先检查桌面包完整性或项目配置文件格式。）";
        errorDetail = diagnostic;
        var sb = new StringBuilder();
        sb.AppendLine($"项目名称：{project.ProjectName}");
        sb.AppendLine($"项目路径：{project.ProjectPath}");
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

    internal static EnvironmentItemRow FormatRow(WorkflowCheckItem item)
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
            var remedy = !string.IsNullOrWhiteSpace(item.Remedy)
                ? "下一步：" + item.Remedy
                : "下一步：" + item.Detail;
            return new EnvironmentItemRow(item.Name, label, badge, summary, remedy, item.Detail);
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
            ? "参考说明：" + item.Remedy
            : "";
        return new EnvironmentItemRow(item.Name, label, pendingBadge, pendingSummary, pendingRemedy, item.Detail);
    }

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

    private static string BuildDetailedReport(ProjectFolderInfo project, EnvironmentCheckExecution execution)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"项目名称：{project.ProjectName}");
        sb.AppendLine($"项目路径：{project.ProjectPath}");
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

    private void ClearOutcomeCore()
    {
        ActionItems.Clear();
        PendingItems.Clear();
        PassedItems.Clear();
        outcome = EnvironmentOutcomeKind.None;
        headline = "";
        outcomeNote = "";
        errorDetail = "";
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
        OnPropertyChanged(nameof(ErrorDetail));
        OnPropertyChanged(nameof(HasActionItems));
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
