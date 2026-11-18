using System.Collections.ObjectModel;
using System.Diagnostics;
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

public enum BuildOutcomeKind
{
    None,
    Succeeded,
    Failed
}

public sealed record EnvironmentItemRow(
    string RawName,
    string DisplayTitle,
    string StatusBadge,
    string SummaryLine,
    string RemedyLine,
    string RawDetail,
    string OfficialGuidanceLine = "");

public sealed record BuildStepResultRow(
    string Phase,
    string DisplayTitle,
    string Status,
    string StatusBadge,
    string SummaryLine);

/// <summary>
/// Drives the "检查项目环境" page from folder selection and read-only environment checks
/// through in-process manifest preview, guarded build execution, and confirmed artifact access.
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
    private readonly Action<Action> uiDispatch;
    private readonly Action<string> artifactFolderOpener;
    private readonly LatestRequest requests = new();
    private readonly object operationsGate = new();
    private readonly HashSet<Task> pendingOperations = [];

    private FileSystemWatcher? manifestWatcher;
    private FileSystemWatcher? onboardingWatcher;
    private bool disposed;
    private bool checking;
    private bool building;
    private bool packaging;
    private bool applyingOnboarding;
    private string folderInput = "";
    private ProjectFolderInfo? currentProject;
    private DotnetOnboardingPreviewData? onboardingPreview;
    private bool onboardingDetailsExpanded;
    private string onboardingDetailsText = "";
    private string onboardingSavedNotice = "";
    private string onboardingChangeNotice = "";
    private CheckSnapshot? previousSnapshot;
    private BuildManifestPreview? currentPreview;
    private string checkedManifestFileSha256 = "";
    private string checkedManifestSource = "";

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

    private bool buildCommandsExpanded;
    private string buildFullCommandsText = "";
    private string buildProgressText = "";
    private BuildOutcomeKind buildOutcome = BuildOutcomeKind.None;
    private string buildHeadline = "";
    private string buildNote = "";
    private string buildCheckedTimeText = "";
    private string buildFailureErrorSummary = "";
    private string buildLogText = "";
    private bool showBuildLog;
    private string confirmedArtifactDirectory = "";
    private string lastOpenedArtifactDirectory = "";
    private string? verifiedBuildAttestationJson;
    private string verifiedBuildManifestSha256 = "";
    private string publishReadinessHintSummary = "";
    private string publishReadinessHintCommand = "";

    private ProjectInstallerReceiptData? lastValidInstaller;
    private DateTimeOffset? lastValidInstallerAt;
    private bool installerMatchesCurrentBuild;
    private string lastOpenedInstallerDirectory = "";
    private bool installerReceiptExpanded;
    private string installerFailureTitle = "";
    private string installerFailureSummary = "";
    private string installerFailureRemedy = "";
    private string installerFailureDetail = "";
    private string statusMessage = "";

    public EnvironmentCheckViewModel(
        IEnvironmentCheckService service,
        Func<DateTimeOffset>? clock = null,
        Action<Action>? dispatcher = null,
        Action<string>? artifactFolderOpener = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.clock = clock ?? (() => DateTimeOffset.Now);
        uiDispatch = dispatcher ?? (static action => action());
        this.artifactFolderOpener = artifactFolderOpener ?? OpenExplorerDirectory;
    }

    public ObservableCollection<EnvironmentItemRow> ActionItems { get; } = [];
    public ObservableCollection<EnvironmentItemRow> ResolvedItems { get; } = [];
    public ObservableCollection<EnvironmentItemRow> PendingItems { get; } = [];
    public ObservableCollection<EnvironmentItemRow> PassedItems { get; } = [];
    public ObservableCollection<ManifestStepPreview> BuildPreviewSteps { get; } = [];
    public ObservableCollection<BuildStepResultRow> BuildStepRows { get; } = [];

    public bool IsChecking => checking;
    public bool IsBuilding => building;
    public bool IsPackaging => packaging;
    public bool IsApplyingOnboarding => applyingOnboarding;
    public bool CanEditProjectSelection => !disposed && !building && !packaging && !applyingOnboarding;
    public bool HasProject => currentProject is not null;
    public string ProjectName => currentProject?.ProjectName ?? "尚未选择项目文件夹";
    public string ProjectPath => currentProject?.ProjectPath ?? "";
    public string FolderInput
    {
        get => folderInput;
        set => ApplyFolderInput(value ?? "");
    }

    public string SelectionScopeNote => currentProject is null
        ? ""
        : IsOnboardingReadyToConfirm
            ? $"未找到 contain.environment.json；已只读识别根目录 {onboardingPreview!.CsprojFile}（选择与预览期间未运行项目代码或 MSBuild，也未写入任何文件）。"
            : IsOnboardingUnsupported
                ? "未找到 contain.environment.json；已只读检查项目目录结构（未运行任何项目代码，也未写入任何文件）。"
                : currentProject.ManifestKind switch
                {
                    ProjectManifestKind.EnvironmentManifest when !string.IsNullOrWhiteSpace(currentProject.ExistingInstallerAppId) =>
                        $"已识别 contain.environment.json，并复用已有安装身份 contain.installer.json（{{{currentProject.ExistingInstallerAppId}}}）。点击“开始检查”后只运行首版受支持的只读探测，不会自动安装依赖或自动启动构建。",
                    ProjectManifestKind.EnvironmentManifest =>
                        "已识别 contain.environment.json。点击“开始检查”后只运行首版受支持的只读工具与 Windows 工具链探测；不会自动安装依赖或自动启动构建。",
                    ProjectManifestKind.DeclaredProjectFiles =>
                        $"未找到 contain.environment.json；检测到 {string.Join("、", currentProject.DetectedFiles)}。点击“开始检查”将复用 infer 提取已声明的精确工具版本，无法从现有文件可靠判断的 MSVC/SDK、环境变量与构建项将标为“待确认”。",
                    _ =>
                        "未找到 contain.environment.json，也未发现受支持的根目录声明文件（Cargo.toml、rust-toolchain.toml、global.json）。点击“开始检查”只能核验本机 Windows x64 平台，项目依赖将标为“待确认”。"
                };

    public bool IsOnboardingReadyToConfirm => onboardingPreview is { Status: "ready_to_confirm" };
    public bool IsOnboardingUnsupported => onboardingPreview is { Status: "unsupported" };
    public bool CanConfirmOnboarding =>
        !disposed
        && !checking
        && !building
        && !packaging
        && !applyingOnboarding
        && IsOnboardingReadyToConfirm;
    public bool CanCancelOnboarding =>
        !disposed
        && !building
        && !packaging
        && !applyingOnboarding
        && (IsOnboardingReadyToConfirm || IsOnboardingUnsupported);

    public string OnboardingHeadline => onboardingPreview is { Status: "ready_to_confirm" } p
        ? $"已识别 {p.AppName} · 控制台应用"
        : "";
    public string OnboardingSubtitle => onboardingPreview is { Status: "ready_to_confirm" }
        ? "将生成可独立安装的 Windows x64 版本（自包含、多文件发布，接收者无需安装 .NET）"
        : "";
    public string OnboardingProvenanceSummary => BuildOnboardingProvenanceSummary();
    public string OnboardingSaveTargetNote => onboardingPreview is { Status: "ready_to_confirm" } p && currentProject is not null
        ? $"点击“使用此配置”后，将在项目目录保存可复用的 {Path.Combine(currentProject.ProjectPath, "contain.environment.json")}，发布产物输出到独立目录 {p.PublishDir}；全程不修改 {p.CsprojFile}，也不覆盖原有 bin/ 或 obj/ 输出。点击“取消”不写入任何文件。"
        : "";
    public bool HasOnboardingChangeNotice => onboardingChangeNotice.Length > 0 && IsOnboardingReadyToConfirm;
    public string OnboardingChangeNotice => onboardingChangeNotice;
    public bool OnboardingDetailsExpanded
    {
        get => onboardingDetailsExpanded;
        set => SetProperty(ref onboardingDetailsExpanded, value);
    }
    public string OnboardingDetailsText => onboardingDetailsText;
    public bool HasOnboardingSavedNotice => onboardingSavedNotice.Length > 0;
    public string OnboardingSavedNotice => onboardingSavedNotice;
    public string OnboardingUnsupportedTitle => onboardingPreview?.UnsupportedTitle ?? "";
    public string OnboardingUnsupportedReason => onboardingPreview?.UnsupportedReason ?? "";
    public string OnboardingUnsupportedNextStep => onboardingPreview?.ActionableNextStep ?? "";

    public bool CanStartCheck =>
        !disposed
        && !checking
        && !building
        && !packaging
        && !applyingOnboarding
        && currentProject is not null
        && !IsOnboardingReadyToConfirm
        && !IsOnboardingUnsupported;
    public bool CanRecheck => !disposed && !checking && !building && !packaging && !applyingOnboarding && currentProject is not null && HasOutcome;
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

    // --- Project Build Surface Properties ---
    public bool ShowProjectBuildSection => HasOutcome && currentProject is not null;

    public bool IsManifestChangedSinceCheck =>
        HasOutcome
        && !string.IsNullOrEmpty(checkedManifestFileSha256)
        && (currentPreview is null
            || currentPreview.Status != ManifestPreviewStatus.Valid
            || !string.Equals(currentPreview.RawFileSha256, checkedManifestFileSha256, StringComparison.Ordinal));

    public bool CanShowStartBuild =>
        HasOutcome
        && outcome == EnvironmentOutcomeKind.Ready
        && !HasRecheckFailure
        && string.Equals(checkedManifestSource, "contain.environment.json", StringComparison.Ordinal)
        && currentPreview is { Status: ManifestPreviewStatus.Valid }
        && currentPreview.PendingItems.Count == 0
        && !currentPreview.HasUnsupportedSafeProbes
        && currentPreview.BuildSteps.Count > 0
        && !string.IsNullOrEmpty(checkedManifestFileSha256)
        && string.Equals(currentPreview.RawFileSha256, checkedManifestFileSha256, StringComparison.Ordinal);

    public bool CanStartBuild => !disposed && !checking && !building && !packaging && CanShowStartBuild;
    public string StartBuildButtonText => buildOutcome == BuildOutcomeKind.None ? "开始构建" : "再次构建";

    public bool HasBuildPrerequisiteBlocker => ShowProjectBuildSection && !CanShowStartBuild;
    public string BuildPrerequisiteMessage => ComputeBuildPrerequisiteMessage();

    public bool ShowBuildStepsPreview =>
        ShowProjectBuildSection
        && currentPreview is { Status: ManifestPreviewStatus.Valid }
        && currentPreview.AllSteps.Count > 0
        && (CanShowStartBuild || IsManifestChangedSinceCheck);

    public string BuildPreviewProjectName => currentPreview?.ProjectName ?? ProjectName;
    public string BuildPreviewWorkingDirectory => currentPreview?.WorkingDirectory ?? ProjectPath;
    public string BuildPreviewStepsSummary => currentPreview is not { Status: ManifestPreviewStatus.Valid }
        ? ""
        : currentPreview.VerifySteps.Count > 0
            ? $"共 {currentPreview.AllSteps.Count} 步（{currentPreview.BuildSteps.Count} 项构建步骤，{currentPreview.VerifySteps.Count} 项构建后验证步骤）"
            : $"共 {currentPreview.BuildSteps.Count} 项构建步骤（未声明额外验证步骤）";

    public string BuildPreviewArtifactHint => currentPreview is not { Status: ManifestPreviewStatus.Valid }
        ? ""
        : !string.IsNullOrWhiteSpace(currentPreview.ArtifactDir) && currentPreview.Artifacts.Count > 0
            ? $"声明产物目录：{currentPreview.ArtifactDir}（预期产物：{string.Join("、", currentPreview.Artifacts)}）"
            : !string.IsNullOrWhiteSpace(currentPreview.ArtifactDir)
                ? $"声明产物目录：{currentPreview.ArtifactDir}"
                : currentPreview.Artifacts.Count > 0
                    ? $"声明预期产物：{string.Join("、", currentPreview.Artifacts)}"
                    : "";

    public bool BuildCommandsExpanded
    {
        get => buildCommandsExpanded;
        set => SetProperty(ref buildCommandsExpanded, value);
    }
    public string BuildFullCommandsText => buildFullCommandsText;

    public string BuildProgressText => buildProgressText;
    public bool HasBuildOutcome => buildOutcome != BuildOutcomeKind.None;
    public BuildOutcomeKind BuildOutcome => buildOutcome;
    public bool IsBuildSucceeded => buildOutcome == BuildOutcomeKind.Succeeded;
    public bool IsBuildFailed => buildOutcome == BuildOutcomeKind.Failed;
    public string BuildOutcomeBadge => buildOutcome switch
    {
        BuildOutcomeKind.Succeeded => "构建通过",
        BuildOutcomeKind.Failed => "构建未通过",
        _ => ""
    };
    public string BuildHeadline => buildHeadline;
    public string BuildNote => buildNote;
    public string BuildCheckedTimeText => buildCheckedTimeText;
    public string BuildFailureErrorSummary => buildFailureErrorSummary;
    public bool HasBuildStepRows => BuildStepRows.Count > 0;

    public string ConfirmedArtifactDirectory => confirmedArtifactDirectory;
    public bool CanOpenArtifactFolder =>
        !disposed
        && !building
        && !packaging
        && buildOutcome == BuildOutcomeKind.Succeeded
        && confirmedArtifactDirectory.Length > 0
        && Directory.Exists(confirmedArtifactDirectory);
    public string LastOpenedArtifactDirectory => lastOpenedArtifactDirectory;

    // --- Project Installer Packaging Surface Properties ---
    public bool ShowGenerateInstallerButton => buildOutcome == BuildOutcomeKind.Succeeded;
    public bool CanGenerateInstaller =>
        !disposed
        && !checking
        && !building
        && !packaging
        && buildOutcome == BuildOutcomeKind.Succeeded;
    public string PackagingProgressText => currentProject is null
        ? "正在生成 Windows 安装包…"
        : $"正在验证自包含发布产物并为“{currentProject.ProjectName}”生成 Windows 安装包…";

    public bool HasPublishReadinessHint =>
        buildOutcome == BuildOutcomeKind.Succeeded
        && publishReadinessHintSummary.Length > 0
        && !HasInstallerFailure;
    public string PublishReadinessHintSummary => publishReadinessHintSummary;
    public string PublishReadinessHintCommand => publishReadinessHintCommand;

    public bool HasValidInstaller =>
        buildOutcome == BuildOutcomeKind.Succeeded
        && lastValidInstaller is not null
        && (installerMatchesCurrentBuild || HasInstallerFailure)
        && File.Exists(lastValidInstaller.InstallerFullPath);
    public string GeneratedInstallerBadge => (!installerMatchesCurrentBuild || HasInstallerFailure) && lastValidInstaller is not null
        ? $"上次已生成的可用安装包（{FormatTimestamp(lastValidInstallerAt ?? clock())}）"
        : lastValidInstallerAt is { } ts
            ? $"安装包已生成 · {FormatTimestamp(ts)}"
            : "安装包已生成";
    public string GeneratedInstallerFileName => lastValidInstaller?.InstallerFile ?? "";
    public string GeneratedInstallerFullPath => lastValidInstaller?.InstallerFullPath ?? "";
    public string GeneratedInstallerDirectory => lastValidInstaller?.InstallerDirectory ?? "";
    public string GeneratedInstallerSummaryLine => lastValidInstaller is null
        ? ""
        : $"版本 {lastValidInstaller.AppVersion} · 启动入口 {lastValidInstaller.Entry} · 产物来源 {lastValidInstaller.PublishRelDir}（共 {lastValidInstaller.PayloadFileCount} 个文件） · 签名状态 {(lastValidInstaller.Signed ? $"已签名 ({lastValidInstaller.AuthenticodeStatus})" : $"未签名 ({lastValidInstaller.AuthenticodeStatus})")}";
    public string GeneratedInstallerIdentityLine => lastValidInstaller is null
        ? ""
        : $"安装身份 {{{lastValidInstaller.AppId}}} · 默认目录 {lastValidInstaller.DefaultInstallDir} · SHA-256 {lastValidInstaller.InstallerSha256}";
    public string GeneratedInstallerReceiptText => lastValidInstaller is null
        ? ""
        : !string.IsNullOrWhiteSpace(lastValidInstaller.SummaryMarkdown)
            ? lastValidInstaller.SummaryMarkdown.TrimEnd()
            : $"{GeneratedInstallerSummaryLine}\n{GeneratedInstallerIdentityLine}";
    public bool InstallerReceiptExpanded
    {
        get => installerReceiptExpanded;
        set => SetProperty(ref installerReceiptExpanded, value);
    }
    public bool CanOpenInstallerFolder =>
        !disposed
        && !building
        && !packaging
        && HasValidInstaller;
    public string LastOpenedInstallerDirectory => lastOpenedInstallerDirectory;

    public bool HasInstallerFailure => installerFailureTitle.Length > 0;
    public string InstallerFailureTitle => installerFailureTitle;
    public string InstallerFailureSummary => installerFailureSummary;
    public string InstallerFailureRemedy => installerFailureRemedy;
    public string InstallerFailureDetail => installerFailureDetail;

    public bool CanToggleBuildLog => HasBuildOutcome && buildLogText.Length > 0;
    public bool ShowBuildLog
    {
        get => showBuildLog;
        set
        {
            if (SetProperty(ref showBuildLog, value))
                OnPropertyChanged(nameof(BuildLogButtonText));
        }
    }
    public string BuildLogButtonText => showBuildLog ? "收起构建日志" : "查看构建日志";
    public string BuildLogText => buildLogText;

    public string StatusMessage => statusMessage;
    public string CheckingBannerText => currentProject is null
        ? "正在检查项目环境…"
        : $"正在检查“{currentProject.ProjectName}”的本机开发环境，窗口可正常切换或浏览其他页面…";

    /// <summary>Cancelling the folder picker leaves the current project, results, and any active check untouched.</summary>
    public void CancelSelection() { }

    public bool SelectProject(string path)
    {
        if (disposed || building || packaging || applyingOnboarding) return false;
        requests.Invalidate();
        checking = false;
        DisposeManifestWatcher();
        ResetProjectSession();

        try
        {
            var info = service.InspectProject(path);
            currentProject = info;
            folderInput = info.ProjectPath;

            if (info.ManifestKind != ProjectManifestKind.EnvironmentManifest)
            {
                var onboarding = service.InspectDotnetOnboarding(info.ProjectPath);
                if (onboarding is not null)
                {
                    ApplyOnboardingPreview(onboarding, info.ProjectPath);
                    StartOnboardingDirectoryWatcher(info.ProjectPath);
                    statusMessage = onboarding.Status == "ready_to_confirm"
                        ? $"已只读识别控制台应用“{onboarding.AppName}”，请核对下方配置并点击“使用此配置”。"
                        : onboarding.UnsupportedTitle ?? "当前文件夹暂不能自动生成单项目控制台发布配置。";
                    NotifyAll();
                    return true;
                }
            }

            ApplyManifestPreview(service.InspectBuildPreview(info.ProjectPath));
            StartManifestWatcher(info.ProjectPath);
            statusMessage = !string.IsNullOrWhiteSpace(info.ExistingInstallerAppId)
                ? $"已选择项目“{info.ProjectName}”（已复用现有配置与安装身份），点击“开始检查”运行探测。"
                : $"已选择项目“{info.ProjectName}”，点击“开始检查”运行探测。";
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

    public void CancelOnboarding()
    {
        if (disposed || building || packaging || applyingOnboarding) return;
        requests.Invalidate();
        checking = false;
        DisposeManifestWatcher();
        currentProject = null;
        folderInput = "";
        ResetProjectSession();
        statusMessage = "已取消配置预览，未向项目文件夹写入任何文件。";
        NotifyAll();
    }

    public Task<bool> ConfirmOnboardingAsync() => Track(() => ConfirmOnboardingCoreAsync());

    private async Task<bool> ConfirmOnboardingCoreAsync()
    {
        if (!CanConfirmOnboarding || currentProject is null || onboardingPreview is null)
            return false;

        var targetProject = currentProject;
        var expectedSha = onboardingPreview.SourceFingerprintSha256;
        var ticket = requests.Begin();
        applyingOnboarding = true;
        onboardingChangeNotice = "";
        statusMessage = $"正在保存“{targetProject.ProjectName}”的发布配置文件…";
        NotifyAll();

        try
        {
            var applyOutcome = await service.ApplyDotnetOnboardingAsync(
                targetProject.ProjectPath,
                expectedSha,
                ticket.Token).ConfigureAwait(true);

            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            applyingOnboarding = false;

            switch (applyOutcome.Status)
            {
                case "applied":
                {
                    DisposeOnboardingWatcher();
                    ClearOnboardingStateCore();
                    var savedName = applyOutcome.SavedManifestFilename ?? "contain.environment.json";
                    var savedFullPath = Path.Combine(targetProject.ProjectPath, savedName);
                    var csprojName = applyOutcome.Inspection.CsprojFile ?? ".csproj";
                    var refreshedInfo = service.InspectProject(targetProject.ProjectPath);
                    currentProject = refreshedInfo;
                    ApplyManifestPreview(service.InspectBuildPreview(refreshedInfo.ProjectPath));
                    StartManifestWatcher(refreshedInfo.ProjectPath);
                    onboardingSavedNotice = $"配置已保存至：{savedFullPath}（未修改 {csprojName}，也未覆盖原有 bin/ 或 obj/ 目录）。现在可直接点击“开始检查” → “开始构建” → “生成安装包”。";
                    statusMessage = $"已保存 {savedName}，请点击“开始检查”继续。";
                    NotifyAll();
                    return true;
                }
                case "preserved_existing_manifest":
                {
                    DisposeOnboardingWatcher();
                    ClearOnboardingStateCore();
                    var existingFullPath = Path.Combine(targetProject.ProjectPath, "contain.environment.json");
                    var refreshedInfo = service.InspectProject(targetProject.ProjectPath);
                    currentProject = refreshedInfo;
                    ApplyManifestPreview(service.InspectBuildPreview(refreshedInfo.ProjectPath));
                    StartManifestWatcher(refreshedInfo.ProjectPath);
                    onboardingSavedNotice = $"检测到项目目录中已存在 {existingFullPath}，已直接保留并使用该配置文件（未覆盖原文件）。现在可点击“开始检查”。";
                    statusMessage = "检测到已有 contain.environment.json，已保留并加载现有配置。";
                    NotifyAll();
                    return true;
                }
                case "source_changed_refresh_required":
                {
                    ApplyOnboardingPreview(applyOutcome.Inspection, targetProject.ProjectPath);
                    if (applyOutcome.Inspection.Status == "ready_to_confirm")
                    {
                        onboardingChangeNotice = "预览期间检测到项目文件已变更，尚未写入配置文件；已按最新项目文件刷新下方预览，请核对后再次点击“使用此配置”。";
                        statusMessage = "项目文件已变更，已重新读取配置预览，请再次确认。";
                    }
                    else
                    {
                        statusMessage = applyOutcome.Inspection.UnsupportedTitle ?? "项目文件变更后暂不支持自动生成配置。";
                    }
                    NotifyAll();
                    return false;
                }
                default:
                {
                    ApplyOnboardingPreview(applyOutcome.Inspection, targetProject.ProjectPath);
                    statusMessage = applyOutcome.Inspection.UnsupportedTitle ?? "当前项目暂不能自动生成发布配置。";
                    NotifyAll();
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (!requests.IsCurrent(ticket))
        {
            applyingOnboarding = false;
            return false;
        }
        catch (EnvironmentCheckException error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            applyingOnboarding = false;
            outcome = EnvironmentOutcomeKind.CheckerError;
            headline = "未能保存项目发布配置";
            outcomeNote = error.Message;
            errorDetail = error.Diagnostic;
            statusMessage = error.Message;
            NotifyAll();
            return false;
        }
        catch (Exception error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            applyingOnboarding = false;
            outcome = EnvironmentOutcomeKind.CheckerError;
            headline = "保存项目发布配置时发生异常";
            outcomeNote = "调用配置写入程序时出现未预期错误。";
            errorDetail = $"{error.GetType().Name}: {error.Message}";
            statusMessage = "保存发布配置时发生异常。";
            NotifyAll();
            return false;
        }
    }

    public void RefreshOnboardingPreviewFromDisk()
    {
        if (disposed || applyingOnboarding || currentProject is null || onboardingPreview is null)
            return;

        var projectDir = currentProject.ProjectPath;
        var manifestPath = Path.Combine(projectDir, "contain.environment.json");
        if (File.Exists(manifestPath))
        {
            DisposeOnboardingWatcher();
            ClearOnboardingStateCore();
            currentProject = service.InspectProject(projectDir);
            ApplyManifestPreview(service.InspectBuildPreview(projectDir));
            StartManifestWatcher(projectDir);
            onboardingSavedNotice = $"检测到项目目录中新建了 {manifestPath}，已直接保留并使用该配置文件（未覆盖原文件）。";
            statusMessage = "检测到新建的 contain.environment.json，已保留并加载现有配置。";
            NotifyAll();
            return;
        }

        try
        {
            var fresh = service.InspectDotnetOnboarding(projectDir);
            if (fresh is null)
            {
                DisposeOnboardingWatcher();
                ClearOnboardingStateCore();
                currentProject = service.InspectProject(projectDir);
                ApplyManifestPreview(service.InspectBuildPreview(projectDir));
                NotifyAll();
                return;
            }

            if (!string.Equals(fresh.Status, onboardingPreview.Status, StringComparison.Ordinal)
                || !string.Equals(fresh.SourceFingerprintSha256, onboardingPreview.SourceFingerprintSha256, StringComparison.Ordinal))
            {
                ApplyOnboardingPreview(fresh, projectDir);
                if (fresh.Status == "ready_to_confirm")
                {
                    onboardingChangeNotice = "检测到项目文件已更新，已重新读取只读预览（未写入任何文件）。";
                    statusMessage = $"检测到“{currentProject.ProjectName}”项目文件已更新，已刷新配置预览。";
                }
                else
                {
                    statusMessage = fresh.UnsupportedTitle ?? "项目文件变更后暂不支持自动生成配置。";
                }
                NotifyAll();
            }
        }
        catch (EnvironmentCheckException)
        {
            // Transient file write in progress; next file event or confirmation re-validates deterministically.
        }
    }

    public void RefreshBuildPreview()
    {
        if (disposed || currentProject is null) return;
        var previousSha = currentPreview?.RawFileSha256 ?? "";
        var previousStatus = currentPreview?.Status;
        var fresh = service.InspectBuildPreview(currentProject.ProjectPath);
        ApplyManifestPreview(fresh);
        if (previousStatus != fresh.Status || !string.Equals(previousSha, fresh.RawFileSha256, StringComparison.Ordinal))
        {
            if (!building && HasOutcome && IsManifestChangedSinceCheck)
            {
                statusMessage = $"检测到“{currentProject.ProjectName}”的 contain.environment.json 已变更，已刷新构建步骤预览。";
            }
            NotifyAll();
        }
    }

    private void ApplyFolderInput(string rawValue)
    {
        if (disposed || building || packaging || applyingOnboarding || string.Equals(folderInput, rawValue, StringComparison.Ordinal)) return;
        folderInput = rawValue;
        OnPropertyChanged(nameof(FolderInput));

        var trimmed = rawValue.Trim();
        if (trimmed.Length == 0)
        {
            requests.Invalidate();
            checking = false;
            DisposeManifestWatcher();
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
        DisposeManifestWatcher();
        currentProject = null;
        ResetProjectSession();
        statusMessage = "输入的文件夹路径尚不存在；可继续输入完整路径或点击“选择文件夹”。";
        NotifyAll();
    }

    public Task<bool> CheckAsync() => Track(() => CheckCoreAsync());

    private async Task<bool> CheckCoreAsync()
    {
        if (disposed || building || packaging || currentProject is null) return false;
        var targetProject = currentProject;
        var previewBeforeCheck = service.InspectBuildPreview(targetProject.ProjectPath);
        var hasValidPriorForProject = previousSnapshot is not null
            && string.Equals(previousSnapshot.ProjectPath, targetProject.ProjectPath, StringComparison.OrdinalIgnoreCase);

        var ticket = requests.Begin();
        checking = true;
        ClearBuildResultCore();
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
            ApplyReport(targetProject, execution, previewBeforeCheck);
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

    public Task<bool> StartBuildAsync() => Track(() => StartBuildCoreAsync());

    private async Task<bool> StartBuildCoreAsync()
    {
        if (disposed || checking || building || packaging || currentProject is null || !CanShowStartBuild || currentPreview is null)
            return false;

        var targetProject = currentProject;
        var previewSnapshot = currentPreview;

        // Re-verify project and manifest consistency immediately upon explicit user click before spawning CLI.
        var latestInfo = service.InspectProject(targetProject.ProjectPath);
        var latestPreview = service.InspectBuildPreview(latestInfo.ProjectPath);
        if (!string.Equals(latestInfo.ProjectPath, targetProject.ProjectPath, StringComparison.OrdinalIgnoreCase)
            || latestPreview.Status != ManifestPreviewStatus.Valid
            || !string.Equals(latestPreview.RawFileSha256, previewSnapshot.RawFileSha256, StringComparison.Ordinal)
            || !string.Equals(latestPreview.RawFileSha256, checkedManifestFileSha256, StringComparison.Ordinal))
        {
            ApplyManifestPreview(latestPreview);
            ClearBuildResultCore();
            statusMessage = "项目构建清单已发生变化，已刷新步骤预览；请先点击“重新检查”确认后再开始构建。";
            NotifyAll();
            return false;
        }

        var ticket = requests.Begin();
        building = true;
        ClearBuildResultCore();
        buildProgressText = $"正在启动随包 contain-workflow 构建“{targetProject.ProjectName}”…";
        statusMessage = buildProgressText;
        NotifyAll();

        var progressReporter = new Progress<BuildProgressUpdate>(update =>
        {
            if (disposed || !building || !IsStillCurrent(ticket, targetProject.ProjectPath))
                return;
            buildProgressText = update.FormatDisplayText(targetProject.ProjectName);
            statusMessage = buildProgressText;
            OnPropertyChanged(nameof(BuildProgressText));
            OnPropertyChanged(nameof(StatusMessage));
        });

        try
        {
            var execution = await service.BuildAsync(
                targetProject.ProjectPath,
                latestPreview,
                progressReporter,
                ticket.Token).ConfigureAwait(true);

            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            building = false;
            buildProgressText = "";
            var succeeded = ApplyBuildExecution(targetProject, latestPreview, execution);
            NotifyAll();
            return succeeded;
        }
        catch (OperationCanceledException) when (!requests.IsCurrent(ticket))
        {
            building = false;
            buildProgressText = "";
            return false;
        }
        catch (EnvironmentCheckException error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            building = false;
            buildProgressText = "";
            if (error.Code == "manifest_changed")
            {
                ApplyManifestPreview(service.InspectBuildPreview(targetProject.ProjectPath));
                statusMessage = error.Message;
                NotifyAll();
                return false;
            }
            ApplyBuildCheckerFailure(targetProject, error.Message, error.Diagnostic);
            NotifyAll();
            return false;
        }
        catch (Exception error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            building = false;
            buildProgressText = "";
            ApplyBuildCheckerFailure(targetProject, "构建执行过程中发生异常。", $"{error.GetType().Name}: {error.Message}");
            NotifyAll();
            return false;
        }
    }

    public bool OpenArtifactFolder()
    {
        if (disposed || building || packaging || buildOutcome != BuildOutcomeKind.Succeeded || string.IsNullOrWhiteSpace(confirmedArtifactDirectory))
            return false;

        if (!Directory.Exists(confirmedArtifactDirectory))
        {
            confirmedArtifactDirectory = "";
            buildNote = "原构建产物文件夹已不存在（可能已被移动或删除），请再次构建以生成新产物。";
            statusMessage = buildNote;
            NotifyAll();
            return false;
        }

        artifactFolderOpener(confirmedArtifactDirectory);
        lastOpenedArtifactDirectory = confirmedArtifactDirectory;
        statusMessage = $"已打开产物文件夹：{confirmedArtifactDirectory}";
        OnPropertyChanged(nameof(LastOpenedArtifactDirectory));
        OnPropertyChanged(nameof(StatusMessage));
        return true;
    }

    public Task<bool> GenerateInstallerAsync() => Track(() => GenerateInstallerCoreAsync());

    private async Task<bool> GenerateInstallerCoreAsync()
    {
        if (!CanGenerateInstaller || currentProject is null)
            return false;

        var targetProject = currentProject;
        if (lastValidInstaller is not null && !File.Exists(lastValidInstaller.InstallerFullPath))
        {
            lastValidInstaller = null;
            lastValidInstallerAt = null;
        }

        var latestPreview = service.InspectBuildPreview(targetProject.ProjectPath);
        if (latestPreview.Status != ManifestPreviewStatus.Valid
            || !string.Equals(latestPreview.RawFileSha256, verifiedBuildManifestSha256, StringComparison.Ordinal))
        {
            ApplyManifestPreview(latestPreview);
            ApplyInstallerFailureBanner(
                "项目构建清单已变更，请先重新检查并构建",
                "检测到 contain.environment.json 在上次成功构建后已修改，不能将旧产物按新清单直接打包。",
                "请先点击上方“重新检查”，再点击“再次构建”验证最新发布产物后生成安装包。",
                "");
            statusMessage = $"“{targetProject.ProjectName}”清单已变更，请重新检查并构建后再生成安装包。";
            NotifyAll();
            return false;
        }

        if (string.IsNullOrWhiteSpace(verifiedBuildAttestationJson))
        {
            ApplyInstallerFailureBanner(
                "缺少本次构建的已核实发布产物记录",
                "未能确认当前项目的独立发布输出目录与文件摘要，无法直接生成安装包。",
                "请在 contain.environment.json 中配置独立的发布输出子目录（如 artifact_dir: \"artifacts/publish\"）与自包含发布命令，点击“再次构建”后重试。",
                "");
            statusMessage = $"“{targetProject.ProjectName}”缺少已核实的发布产物记录。";
            NotifyAll();
            return false;
        }

        var attestationSnapshot = verifiedBuildAttestationJson;
        var ticket = requests.Begin();
        packaging = true;
        ClearInstallerFailureCore();
        statusMessage = PackagingProgressText;
        NotifyAll();

        try
        {
            var execution = await service.PackageInstallerAsync(
                targetProject.ProjectPath,
                attestationSnapshot,
                ticket.Token).ConfigureAwait(true);

            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            packaging = false;
            var succeeded = ApplyInstallerExecution(targetProject, execution);
            NotifyAll();
            return succeeded;
        }
        catch (OperationCanceledException) when (!requests.IsCurrent(ticket))
        {
            packaging = false;
            return false;
        }
        catch (EnvironmentCheckException error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            packaging = false;
            ApplyInstallerFailureBanner(
                $"生成安装包未能完成（失败时间：{FormatTimestamp(clock())}）",
                error.Message,
                "请核对项目清单语法与随包 contain-workflow.exe 完整性后重试。",
                error.Diagnostic);
            statusMessage = $"“{targetProject.ProjectName}”生成安装包未完成：{error.Message}";
            NotifyAll();
            return false;
        }
        catch (Exception error)
        {
            if (!IsStillCurrent(ticket, targetProject.ProjectPath))
                return false;

            packaging = false;
            ApplyInstallerFailureBanner(
                $"生成安装包发生异常（失败时间：{FormatTimestamp(clock())}）",
                "调用随包打包程序过程中发生未预期错误。",
                "请检查输出目录访问权限与磁盘空间后重试。",
                $"{error.GetType().Name}: {error.Message}");
            statusMessage = $"“{targetProject.ProjectName}”生成安装包发生异常。";
            NotifyAll();
            return false;
        }
    }

    public bool OpenInstallerFolder()
    {
        if (disposed || building || packaging || lastValidInstaller is null)
            return false;

        if (!File.Exists(lastValidInstaller.InstallerFullPath) || !Directory.Exists(lastValidInstaller.InstallerDirectory))
        {
            lastValidInstaller = null;
            lastValidInstallerAt = null;
            ApplyInstallerFailureBanner(
                "原安装包文件已不存在",
                "此前生成的安装包文件或所在文件夹已被移动或删除。",
                "可再次点击“生成安装包”输出新的安装包文件。",
                "");
            statusMessage = "原安装包文件已不存在，请重新点击“生成安装包”。";
            NotifyAll();
            return false;
        }

        artifactFolderOpener(lastValidInstaller.InstallerDirectory);
        lastOpenedInstallerDirectory = lastValidInstaller.InstallerDirectory;
        statusMessage = $"已打开安装包所在文件夹：{lastValidInstaller.InstallerDirectory}";
        OnPropertyChanged(nameof(LastOpenedInstallerDirectory));
        OnPropertyChanged(nameof(StatusMessage));
        return true;
    }

    public void ToggleDetailedReport()
    {
        if (!CanToggleDetailedReport) return;
        ShowDetailedReport = !ShowDetailedReport;
    }

    public void ToggleBuildLog()
    {
        if (!CanToggleBuildLog) return;
        ShowBuildLog = !ShowBuildLog;
    }

    public async Task CloseAsync()
    {
        DisposeManifestWatcher();
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
        DisposeManifestWatcher();
        requests.Dispose();
    }

    private bool IsStillCurrent(LatestRequest.Ticket ticket, string expectedProjectPath) =>
        requests.IsCurrent(ticket)
        && currentProject is not null
        && string.Equals(currentProject.ProjectPath, expectedProjectPath, StringComparison.OrdinalIgnoreCase);

    private void ApplyReport(
        ProjectFolderInfo project,
        EnvironmentCheckExecution execution,
        BuildManifestPreview previewBeforeCheck)
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

        var freshPreview = service.InspectBuildPreview(project.ProjectPath);
        ApplyManifestPreview(freshPreview);

        checkedManifestSource = report.ManifestSource ?? (freshPreview.Status == ManifestPreviewStatus.Valid ? "contain.environment.json" : "");
        if (string.Equals(checkedManifestSource, "contain.environment.json", StringComparison.Ordinal)
            && previewBeforeCheck.Status == ManifestPreviewStatus.Valid
            && freshPreview.Status == ManifestPreviewStatus.Valid
            && string.Equals(previewBeforeCheck.RawFileSha256, freshPreview.RawFileSha256, StringComparison.Ordinal))
        {
            checkedManifestFileSha256 = freshPreview.RawFileSha256;
        }
        else
        {
            checkedManifestFileSha256 = "";
        }

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
                outcomeNote = "本机已具备该项目清单声明的平台、工具版本与 Windows 编译工具链。可在下方查看构建步骤并开始构建。";
                statusMessage = $"已完成“{project.ProjectName}”环境检查：全部 {PassedItems.Count} 项满足。";
            }
        }
    }

    private void ApplyCheckerError(ProjectFolderInfo project, string message, string diagnostic)
    {
        var failedAt = clock();
        ApplyManifestPreview(service.InspectBuildPreview(project.ProjectPath));
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

    private bool ApplyBuildExecution(
        ProjectFolderInfo project,
        BuildManifestPreview preview,
        ProjectBuildExecution execution)
    {
        var finishedAt = clock();
        buildCheckedTimeText = $"构建时间：{FormatTimestamp(finishedAt)}";
        buildLogText = string.IsNullOrWhiteSpace(execution.CombinedLogText)
            ? execution.ReportMarkdown
            : execution.CombinedLogText;
        showBuildLog = false;

        PopulateBuildStepRows(preview, execution);

        var allPassed = execution.ExitCode == 0
            && execution.Report.Checks.All(static c => c.Status == "passed");

        if (allPassed)
        {
            buildOutcome = BuildOutcomeKind.Succeeded;
            buildFailureErrorSummary = "";
            confirmedArtifactDirectory = !string.IsNullOrWhiteSpace(execution.ConfirmedArtifactDirectory)
                && Directory.Exists(execution.ConfirmedArtifactDirectory)
                    ? execution.ConfirmedArtifactDirectory
                    : "";
            verifiedBuildAttestationJson = execution.BuildAttestationJson;
            verifiedBuildManifestSha256 = preview.RawFileSha256;

            var readinessHint = ProjectPublishInspector.Inspect(project.ProjectPath, preview, confirmedArtifactDirectory);
            publishReadinessHintSummary = readinessHint.NeedsSelfContainedPublish ? readinessHint.SummaryText : "";
            publishReadinessHintCommand = readinessHint.NeedsSelfContainedPublish ? readinessHint.SuggestedCommandText : "";

            if (confirmedArtifactDirectory.Length > 0)
            {
                buildHeadline = $"构建成功：“{project.ProjectName}”已完成全部构建与验证步骤";
                buildNote = $"已确认本次构建产物位于：{confirmedArtifactDirectory}";
                statusMessage = readinessHint.NeedsSelfContainedPublish
                    ? $"“{project.ProjectName}”构建通过；当前为依赖运行时的编译产物，可按下方指引改为 win-x64 自包含发布后生成安装包。"
                    : $"“{project.ProjectName}”构建成功，可打开产物文件夹或点击“生成安装包”。";
            }
            else
            {
                buildHeadline = $"构建与验证已完成：“{project.ProjectName}”全部步骤执行通过";
                buildNote = "全部命令均已通过，但未能确认独立产物输出目录（不展示“打开产物文件夹”入口）。";
                statusMessage = $"“{project.ProjectName}”构建命令已执行通过。";
            }
            return true;
        }

        buildOutcome = BuildOutcomeKind.Failed;
        confirmedArtifactDirectory = "";

        var failedStep = execution.StepResults.FirstOrDefault(static s => s.Status == "failed");
        var unrunVerifyCount = BuildStepRows.Count(static r => r.Phase == "verify" && r.Status == "not_run");
        var unrunBuildCount = BuildStepRows.Count(static r => r.Phase == "build" && r.Status == "not_run");

        if (failedStep is not null)
        {
            var phaseTitle = failedStep.Phase == "verify" ? "验证步骤" : "构建步骤";
            buildHeadline = $"{phaseTitle}失败：{phaseTitle} {failedStep.Index + 1}“{failedStep.Name}”未通过";
            buildFailureErrorSummary = ExtractStepErrorSummary(failedStep);

            var skippedParts = new List<string>();
            if (unrunBuildCount > 0) skippedParts.Add($"{unrunBuildCount} 项后续构建步骤未执行");
            if (unrunVerifyCount > 0) skippedParts.Add($"{unrunVerifyCount} 项后续验证步骤未执行");
            var skippedText = skippedParts.Count > 0 ? string.Join("，", skippedParts) + "。" : "";
            buildNote = $"{skippedText}旧产物不会计为本次成功。修正问题后可直接在本页点击“再次构建”。";
        }
        else
        {
            var failedCheck = execution.Report.Checks.FirstOrDefault(static c => c.Status != "passed");
            if (failedCheck?.Name == "build_artifacts")
            {
                buildHeadline = "构建未通过：命令已退出，但未找到清单声明的预期产物";
                buildFailureErrorSummary = failedCheck.Detail;
                buildNote = "未确认到本次构建生成的有效产物文件，请查看构建日志核对输出路径。";
            }
            else if (failedCheck is not null)
            {
                buildHeadline = $"构建未启动：构建前重新核验环境未通过（{FriendlyCheckTitle(failedCheck.Name)}）";
                buildFailureErrorSummary = failedCheck.Detail;
                buildNote = "由于构建前安全探测未全部通过，所有构建与验证步骤均未执行。请先点击上方“重新检查”。";
            }
            else
            {
                buildHeadline = $"构建未通过（退出码 {execution.ExitCode}）";
                buildFailureErrorSummary = "请点击“查看构建日志”了解详细错误信息。";
                buildNote = "修复问题后可在本页重新尝试构建。";
            }
        }

        statusMessage = $"“{project.ProjectName}”{buildHeadline}。";
        return false;
    }

    private void ApplyBuildCheckerFailure(ProjectFolderInfo project, string message, string diagnostic)
    {
        var failedAt = clock();
        buildOutcome = BuildOutcomeKind.Failed;
        confirmedArtifactDirectory = "";
        buildCheckedTimeText = $"失败时间：{FormatTimestamp(failedAt)}";
        buildHeadline = "构建未能完成：无法执行随包构建程序";
        buildNote = message;
        buildFailureErrorSummary = diagnostic;
        buildLogText = string.IsNullOrWhiteSpace(diagnostic) ? message : $"{message}\n\n{diagnostic}";
        showBuildLog = false;
        statusMessage = $"“{project.ProjectName}”构建启动失败。";
    }

    private void PopulateBuildStepRows(BuildManifestPreview preview, ProjectBuildExecution execution)
    {
        BuildStepRows.Clear();
        if (execution.StepResults.Count > 0)
        {
            foreach (var sr in execution.StepResults)
            {
                var phaseLabel = sr.Phase == "verify" ? "验证步骤" : "构建步骤";
                var title = $"{phaseLabel} {sr.Index + 1}：{sr.Name}";
                var (badge, summary) = sr.Status switch
                {
                    "passed" => ("通过", $"已通过（退出码 {sr.ObservedExit ?? sr.ExpectedExit}）"),
                    "failed" when sr.TimedOut => ("失败", "执行超时，已终止子进程"),
                    "failed" when !string.IsNullOrWhiteSpace(sr.LaunchError) => ("失败", $"无法启动程序：{sr.LaunchError}"),
                    "failed" => ("失败", $"退出码或输出校验不匹配（预期退出码 {sr.ExpectedExit}，实际 {(sr.ObservedExit?.ToString(CultureInfo.InvariantCulture) ?? "无")}）"),
                    _ => ("未执行", sr.Phase == "verify" ? "未执行（因前置构建或环境步骤未通过而跳过）" : "未执行（因前置步骤未通过而跳过）")
                };
                BuildStepRows.Add(new BuildStepResultRow(sr.Phase, title, sr.Status, badge, summary));
            }
            return;
        }

        // Fallback to matching step checks from structured report.json if steps.json was absent (e.g. unit test mocks).
        foreach (var step in preview.AllSteps)
        {
            var check = execution.Report.Checks.FirstOrDefault(c => string.Equals(c.Name, step.Name, StringComparison.Ordinal));
            var status = check?.Status ?? "not_run";
            var phaseLabel = step.PhaseKey == "verify" ? "验证步骤" : "构建步骤";
            var idxInPhase = step.PhaseKey == "verify"
                ? step.StepNumber - preview.BuildSteps.Count
                : step.StepNumber;
            var badge = status switch
            {
                "passed" => "通过",
                "failed" => "失败",
                _ => "未执行"
            };
            var summary = status switch
            {
                "passed" => "已通过",
                "failed" => check?.Detail ?? "步骤执行失败",
                _ => "未执行（前置条件或构建步骤未通过）"
            };
            BuildStepRows.Add(new BuildStepResultRow(step.PhaseKey, $"{phaseLabel} {idxInPhase}：{step.Name}", status, badge, summary));
        }
    }

    private static string ExtractStepErrorSummary(BuildStepExecutionResult failedStep)
    {
        if (failedStep.TimedOut)
            return $"步骤“{failedStep.Name}”超过声明的超时时间上限，进程已被终止。";
        if (!string.IsNullOrWhiteSpace(failedStep.LaunchError))
            return $"无法启动步骤程序：{failedStep.LaunchError}";

        var combined = new List<string>();
        AppendLines(combined, failedStep.StderrText);
        AppendLines(combined, failedStep.StdoutText);

        var prioritized = combined
            .Where(static line =>
                line.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error CS", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error MSB", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fatal:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Exception", StringComparison.Ordinal))
            .Take(12)
            .ToList();

        var chosen = prioritized.Count > 0
            ? prioritized
            : combined.TakeLast(12).ToList();

        var exitHeader = $"预期退出码 {failedStep.ExpectedExit}，实际退出码 {(failedStep.ObservedExit?.ToString(CultureInfo.InvariantCulture) ?? "无")}。";
        return chosen.Count == 0
            ? exitHeader
            : exitHeader + Environment.NewLine + string.Join(Environment.NewLine, chosen);

        static void AppendLines(List<string> target, string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            foreach (var line in source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Length > 0)
                    target.Add(line);
            }
        }
    }

    private string ComputeBuildPrerequisiteMessage()
    {
        if (!ShowProjectBuildSection)
            return "";

        if (HasRecheckFailure)
        {
            return outcome == EnvironmentOutcomeKind.Ready
                ? "上方保留的“环境已满足”结果仅供查看：最近一次重新检查未能完成，请先修复复查错误并点击“重新检查”确认通过后再开始构建。"
                : "最近一次重新检查未能完成（上方历史结果仅供查看）：请先解决复查错误并补齐所需依赖后再开始构建。";
        }

        if (outcome == EnvironmentOutcomeKind.CheckerError)
            return "当前暂不能开始构建：环境检查未完成，请先修复项目配置文件或检查器错误并点击“重新检查”。";

        if (outcome == EnvironmentOutcomeKind.ActionRequired)
            return $"当前暂不能开始构建：请先按上方指引补齐 {ActionItems.Count} 项缺失或版本不匹配的依赖，并点击“重新检查”确认环境就绪。";

        if (string.Equals(checkedManifestSource, "inferred_from_project_files", StringComparison.Ordinal)
            || currentPreview is null or { Status: ManifestPreviewStatus.Missing })
        {
            return "当前暂不能开始构建：该项目仅有自动推断结果，缺少明确的 contain.environment.json 构建清单；请先补充已审阅的构建步骤并重新检查。";
        }

        if (currentPreview.Status == ManifestPreviewStatus.Invalid)
            return $"当前暂不能开始构建：contain.environment.json 校验未通过（{currentPreview.ValidationError}），请修正后点击“重新检查”。";

        if (IsManifestChangedSinceCheck)
            return "当前暂不能开始构建：检测到 contain.environment.json 在上次检查后已修改（下方预览已刷新），请先点击上方“重新检查”确认新清单环境就绪。";

        if (outcome == EnvironmentOutcomeKind.InsufficientInfo
            || currentPreview.PendingItems.Count > 0
            || currentPreview.HasUnsupportedSafeProbes)
        {
            return "当前暂不能开始构建：项目清单仍含待确认项或超范围安全探测命令，请先确认并消除待确认项后重新检查。";
        }

        if (currentPreview.BuildSteps.Count == 0)
            return "当前暂不能开始构建：contain.environment.json 未声明任何构建步骤（build 为空）。";

        return "当前暂不能开始构建：请先点击“重新检查”确认环境与构建清单一致。";
    }

    private void ApplyManifestPreview(BuildManifestPreview preview)
    {
        currentPreview = preview;
        BuildPreviewSteps.Clear();
        buildCommandsExpanded = false;

        if (preview.Status != ManifestPreviewStatus.Valid || preview.AllSteps.Count == 0)
        {
            buildFullCommandsText = "";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"工作目录：{preview.WorkingDirectory}");
        if (!string.IsNullOrWhiteSpace(preview.ArtifactDir))
            sb.AppendLine($"声明产物目录：{preview.ArtifactDir}");
        if (preview.Artifacts.Count > 0)
            sb.AppendLine($"预期产物文件：{string.Join("、", preview.Artifacts)}");
        sb.AppendLine();

        foreach (var step in preview.AllSteps)
        {
            BuildPreviewSteps.Add(step);
            sb.AppendLine($"[{step.StepNumber}] {step.PhaseBadge}：{step.Name}");
            sb.AppendLine($"    命令：{step.CommandLineText}");
            sb.AppendLine($"    校验：{step.ExpectationText}");
        }
        buildFullCommandsText = sb.ToString().TrimEnd();
    }

    private void ApplyOnboardingPreview(DotnetOnboardingPreviewData preview, string projectPath)
    {
        onboardingPreview = preview;
        onboardingDetailsExpanded = false;
        onboardingChangeNotice = "";

        if (preview.Status != "ready_to_confirm")
        {
            onboardingDetailsText = "";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"发布命令：{preview.PublishCommand}");
        sb.AppendLine($"隔离发布输出目录：{preview.PublishDir}（位于 {projectPath} 下）");
        sb.AppendLine($"启动与验证入口：{preview.EntryExecutable}");
        sb.AppendLine($"配置文件保存位置：{Path.Combine(projectPath, "contain.environment.json")}");
        sb.AppendLine();
        sb.AppendLine("--- 识别依据（区分项目文件声明与建议配置） ---");
        foreach (var row in preview.EvidenceFields)
        {
            sb.AppendLine($"- {row.Label}：{row.Value}  [来源：{row.Source}]");
        }
        if (!string.IsNullOrWhiteSpace(preview.ProposedManifestJson))
        {
            sb.AppendLine();
            sb.AppendLine("--- 待写入的 contain.environment.json ---");
            sb.AppendLine(preview.ProposedManifestJson);
        }
        onboardingDetailsText = sb.ToString().TrimEnd();
    }

    private string BuildOnboardingProvenanceSummary()
    {
        if (onboardingPreview is not { Status: "ready_to_confirm" } p)
            return "";

        var entryDesc = !string.IsNullOrWhiteSpace(p.CsprojFile)
            && !string.IsNullOrWhiteSpace(p.AssemblyName)
            && !string.Equals(p.CsprojFile, p.AssemblyName + ".csproj", StringComparison.OrdinalIgnoreCase)
                ? $"启动入口 {p.EntryExecutable}（按 .csproj 声明的 AssemblyName={p.AssemblyName}，非项目文件名）"
                : $"启动入口 {p.EntryExecutable}";

        var hostSdkNote = p.HostDetectedCompatibleSdks.Count > 0
            ? $"（本机已检测到兼容 SDK：{string.Join("、", p.HostDetectedCompatibleSdks)}）"
            : "（本机当前尚未检测到兼容 SDK，将在下一步检查中提示补齐）";

        var sb = new StringBuilder();
        sb.AppendLine($"项目声明：{p.CsprojFile} · 目标框架 {p.TargetFramework} · {entryDesc}");
        sb.Append($"环境与建议项：{p.SdkRequirementSummary}［来源：{p.SdkSource}］{hostSdkNote}；安装包版本 {p.AppVersion}［来源：{p.VersionSource}］");
        if (p.HasExistingInstallerIdentity && !string.IsNullOrWhiteSpace(p.ExistingAppId))
        {
            sb.AppendLine();
            sb.Append($"已有安装身份：检测到 contain.installer.json，生成安装包时将继续复用 AppId {{{p.ExistingAppId}}}");
        }
        return sb.ToString();
    }

    private void ClearOnboardingStateCore()
    {
        onboardingPreview = null;
        onboardingDetailsExpanded = false;
        onboardingDetailsText = "";
        onboardingChangeNotice = "";
    }

    private void StartOnboardingDirectoryWatcher(string projectDir)
    {
        DisposeOnboardingWatcher();
        try
        {
            var watcher = new FileSystemWatcher(projectDir, "*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };
            void OnEvent(object sender, FileSystemEventArgs e)
            {
                if (e.Name is not null && e.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    return;
                DispatchOnboardingRefresh(projectDir);
            }
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += (_, e) => OnEvent(watcher, e);
            watcher.EnableRaisingEvents = true;
            onboardingWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            onboardingWatcher = null;
        }
    }

    private void DispatchOnboardingRefresh(string watchedDir)
    {
        if (disposed) return;
        uiDispatch(() =>
        {
            if (disposed || currentProject is null || onboardingPreview is null) return;
            if (!string.Equals(currentProject.ProjectPath, watchedDir, StringComparison.OrdinalIgnoreCase))
                return;
            RefreshOnboardingPreviewFromDisk();
        });
    }

    private void DisposeOnboardingWatcher()
    {
        var w = Interlocked.Exchange(ref onboardingWatcher, null);
        if (w is not null)
        {
            try { w.EnableRaisingEvents = false; } catch { }
            w.Dispose();
        }
    }

    private void StartManifestWatcher(string projectDir)
    {
        try
        {
            var watcher = new FileSystemWatcher(projectDir, "contain.environment.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            FileSystemEventHandler onChanged = (_, _) => DispatchPreviewRefresh(projectDir);
            RenamedEventHandler onRenamed = (_, _) => DispatchPreviewRefresh(projectDir);
            watcher.Changed += onChanged;
            watcher.Created += onCreatedHandler;
            watcher.Deleted += onChanged;
            watcher.Renamed += onRenamed;
            watcher.EnableRaisingEvents = true;
            manifestWatcher = watcher;

            void onCreatedHandler(object sender, FileSystemEventArgs e) => DispatchPreviewRefresh(projectDir);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            manifestWatcher = null;
        }
    }

    private void DispatchPreviewRefresh(string watchedDir)
    {
        if (disposed) return;
        uiDispatch(() =>
        {
            if (disposed || currentProject is null) return;
            if (!string.Equals(currentProject.ProjectPath, watchedDir, StringComparison.OrdinalIgnoreCase))
                return;
            RefreshBuildPreview();
        });
    }

    private void DisposeManifestWatcher()
    {
        DisposeOnboardingWatcher();
        var w = Interlocked.Exchange(ref manifestWatcher, null);
        if (w is not null)
        {
            try { w.EnableRaisingEvents = false; } catch { }
            w.Dispose();
        }
    }

    private static void OpenExplorerDirectory(string fullDirectoryPath)
    {
        var psi = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false
        };
        psi.ArgumentList.Add(fullDirectoryPath);
        using var _ = Process.Start(psi);
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
        if (parts.Length == 2
            && parts.All(static p => p.Length > 0 && p.All(char.IsAsciiDigit))
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var majorBand)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minorBand)
            && majorBand is 8 or 9 or 10
            && minorBand == 0)
        {
            var url = $"https://dotnet.microsoft.com/download/dotnet/{majorBand}.0";
            return (
                true,
                $"官方获取入口：前往微软 .NET {majorBand}.0 下载页 {url} 获取任意正式版 .NET {majorBand}.0 SDK（Windows x64，可与本机现有 SDK 并行安装；安装后可用 dotnet --list-sdks 核对，是否安装由你决定）。");
        }

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
        "build_artifacts" => "声明产物核验",
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

    private bool ApplyInstallerExecution(ProjectFolderInfo project, ProjectInstallerExecution execution)
    {
        var now = clock();
        var allPassed = execution.ExitCode == 0
            && execution.Receipt is not null
            && execution.Report.Checks.All(static c => c.Status == "passed");

        if (allPassed && execution.Receipt is { } receipt)
        {
            lastValidInstaller = receipt;
            lastValidInstallerAt = now;
            installerMatchesCurrentBuild = true;
            installerReceiptExpanded = false;
            publishReadinessHintSummary = "";
            publishReadinessHintCommand = "";
            ClearInstallerFailureCore();
            statusMessage = $"已为“{project.ProjectName}”生成安装包：{receipt.InstallerFile}";
            return true;
        }

        var failedChecks = execution.Report.Checks.Where(static c => c.Status == "failed").ToList();
        var primary = failedChecks.FirstOrDefault();
        var baseTitle = primary?.Outcome switch
        {
            "framework_dependent_output" => "尚不能打包：当前构建产物依赖本机 .NET 运行时",
            "missing_publish_dir" or "not_dotnet_publish_output" or "single_file_or_unsupported_stack" or "unsupported_publish_kind" or "wrong_publish_rid" or "missing_self_contained_runtime" or "incomplete_dotnet_publish" =>
                "尚不能打包：请使用受支持的 Windows x64 .NET 自包含多文件发布",
            "ambiguous_entry" => "尚不能打包：启动入口有歧义",
            "missing_entry" or "invalid_entry_pe" => "尚不能打包：缺少有效的 Windows x64 启动入口",
            "missing_version" or "conflicting_version" or "invalid_version" => "尚不能打包：缺少或需要修正应用版本号",
            "inno_setup_missing" => "尚不能打包：本机缺少 Inno Setup 6 打包工具（接收者无需安装）",
            "artifacts_modified_or_stale" or "manifest_changed_since_build" => "无法使用过期或被改动的产物打包，请先重新构建",
            "forbidden_payload_member" => "发布目录包含不应随包分发的文件",
            "forbidden_contain_identity" or "conflicting_app_id" or "invalid_app_id" or "invalid_identity_file" => "项目安装身份（AppId）配置有误或冲突",
            "iscc_compile_failed" => "Inno Setup 编译安装包失败",
            _ => "未能生成 Windows 安装包"
        };

        var titleWithTime = $"{baseTitle}（{FormatTimestamp(now)}）";
        var details = failedChecks.Select(static c => c.Detail).Where(static s => !string.IsNullOrWhiteSpace(s)).ToList();
        var remedies = failedChecks
            .Select(static c => c.Remedy)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var summary = details.Count > 0
            ? string.Join(Environment.NewLine, details)
            : "未能通过安装包生成前检查。";
        if (lastValidInstaller is not null && File.Exists(lastValidInstaller.InstallerFullPath))
        {
            summary += Environment.NewLine + "本次生成未完成；下方继续保留上次已生成的可用安装包。";
        }

        var remedyText = remedies.Count > 0
            ? string.Join(Environment.NewLine + Environment.NewLine, remedies!)
            : "请按提示补齐配置并重新构建后再点击“生成安装包”。";

        ApplyInstallerFailureBanner(titleWithTime, summary, remedyText, "");
        statusMessage = $"“{project.ProjectName}”{baseTitle}。";
        return false;
    }

    private void ApplyInstallerFailureBanner(string title, string summary, string remedy, string detail)
    {
        installerFailureTitle = title;
        installerFailureSummary = summary;
        installerFailureRemedy = remedy;
        installerFailureDetail = detail;
    }

    private void ClearInstallerFailureCore()
    {
        installerFailureTitle = "";
        installerFailureSummary = "";
        installerFailureRemedy = "";
        installerFailureDetail = "";
    }

    private void ResetProjectSession()
    {
        previousSnapshot = null;
        currentPreview = null;
        checkedManifestFileSha256 = "";
        checkedManifestSource = "";
        BuildPreviewSteps.Clear();
        buildFullCommandsText = "";
        buildCommandsExpanded = false;
        lastValidInstaller = null;
        lastValidInstallerAt = null;
        installerMatchesCurrentBuild = false;
        lastOpenedInstallerDirectory = "";
        installerReceiptExpanded = false;
        ClearOnboardingStateCore();
        onboardingSavedNotice = "";
        ClearOutcomeCore();
        ClearBuildResultCore();
    }

    private void ClearRecheckFailureBanner()
    {
        recheckFailureTitle = "";
        recheckFailureText = "";
        recheckFailureDetail = "";
    }

    private void ClearBuildResultCore()
    {
        BuildStepRows.Clear();
        buildProgressText = "";
        buildOutcome = BuildOutcomeKind.None;
        buildHeadline = "";
        buildNote = "";
        buildCheckedTimeText = "";
        buildFailureErrorSummary = "";
        buildLogText = "";
        showBuildLog = false;
        confirmedArtifactDirectory = "";
        verifiedBuildAttestationJson = null;
        verifiedBuildManifestSha256 = "";
        publishReadinessHintSummary = "";
        publishReadinessHintCommand = "";
        installerMatchesCurrentBuild = false;
        installerReceiptExpanded = false;
        ClearInstallerFailureCore();
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
        OnPropertyChanged(nameof(IsBuilding));
        OnPropertyChanged(nameof(IsPackaging));
        OnPropertyChanged(nameof(IsApplyingOnboarding));
        OnPropertyChanged(nameof(CanEditProjectSelection));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectPath));
        OnPropertyChanged(nameof(FolderInput));
        OnPropertyChanged(nameof(SelectionScopeNote));
        OnPropertyChanged(nameof(IsOnboardingReadyToConfirm));
        OnPropertyChanged(nameof(IsOnboardingUnsupported));
        OnPropertyChanged(nameof(CanConfirmOnboarding));
        OnPropertyChanged(nameof(CanCancelOnboarding));
        OnPropertyChanged(nameof(OnboardingHeadline));
        OnPropertyChanged(nameof(OnboardingSubtitle));
        OnPropertyChanged(nameof(OnboardingProvenanceSummary));
        OnPropertyChanged(nameof(OnboardingSaveTargetNote));
        OnPropertyChanged(nameof(HasOnboardingChangeNotice));
        OnPropertyChanged(nameof(OnboardingChangeNotice));
        OnPropertyChanged(nameof(OnboardingDetailsExpanded));
        OnPropertyChanged(nameof(OnboardingDetailsText));
        OnPropertyChanged(nameof(HasOnboardingSavedNotice));
        OnPropertyChanged(nameof(OnboardingSavedNotice));
        OnPropertyChanged(nameof(OnboardingUnsupportedTitle));
        OnPropertyChanged(nameof(OnboardingUnsupportedReason));
        OnPropertyChanged(nameof(OnboardingUnsupportedNextStep));
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
        OnPropertyChanged(nameof(ShowProjectBuildSection));
        OnPropertyChanged(nameof(IsManifestChangedSinceCheck));
        OnPropertyChanged(nameof(CanShowStartBuild));
        OnPropertyChanged(nameof(CanStartBuild));
        OnPropertyChanged(nameof(StartBuildButtonText));
        OnPropertyChanged(nameof(HasBuildPrerequisiteBlocker));
        OnPropertyChanged(nameof(BuildPrerequisiteMessage));
        OnPropertyChanged(nameof(ShowBuildStepsPreview));
        OnPropertyChanged(nameof(BuildPreviewProjectName));
        OnPropertyChanged(nameof(BuildPreviewWorkingDirectory));
        OnPropertyChanged(nameof(BuildPreviewStepsSummary));
        OnPropertyChanged(nameof(BuildPreviewArtifactHint));
        OnPropertyChanged(nameof(BuildCommandsExpanded));
        OnPropertyChanged(nameof(BuildFullCommandsText));
        OnPropertyChanged(nameof(BuildProgressText));
        OnPropertyChanged(nameof(HasBuildOutcome));
        OnPropertyChanged(nameof(BuildOutcome));
        OnPropertyChanged(nameof(IsBuildSucceeded));
        OnPropertyChanged(nameof(IsBuildFailed));
        OnPropertyChanged(nameof(BuildOutcomeBadge));
        OnPropertyChanged(nameof(BuildHeadline));
        OnPropertyChanged(nameof(BuildNote));
        OnPropertyChanged(nameof(BuildCheckedTimeText));
        OnPropertyChanged(nameof(BuildFailureErrorSummary));
        OnPropertyChanged(nameof(HasBuildStepRows));
        OnPropertyChanged(nameof(ConfirmedArtifactDirectory));
        OnPropertyChanged(nameof(CanOpenArtifactFolder));
        OnPropertyChanged(nameof(LastOpenedArtifactDirectory));
        OnPropertyChanged(nameof(ShowGenerateInstallerButton));
        OnPropertyChanged(nameof(CanGenerateInstaller));
        OnPropertyChanged(nameof(PackagingProgressText));
        OnPropertyChanged(nameof(HasPublishReadinessHint));
        OnPropertyChanged(nameof(PublishReadinessHintSummary));
        OnPropertyChanged(nameof(PublishReadinessHintCommand));
        OnPropertyChanged(nameof(HasValidInstaller));
        OnPropertyChanged(nameof(GeneratedInstallerBadge));
        OnPropertyChanged(nameof(GeneratedInstallerFileName));
        OnPropertyChanged(nameof(GeneratedInstallerFullPath));
        OnPropertyChanged(nameof(GeneratedInstallerDirectory));
        OnPropertyChanged(nameof(GeneratedInstallerSummaryLine));
        OnPropertyChanged(nameof(GeneratedInstallerIdentityLine));
        OnPropertyChanged(nameof(GeneratedInstallerReceiptText));
        OnPropertyChanged(nameof(InstallerReceiptExpanded));
        OnPropertyChanged(nameof(CanOpenInstallerFolder));
        OnPropertyChanged(nameof(LastOpenedInstallerDirectory));
        OnPropertyChanged(nameof(HasInstallerFailure));
        OnPropertyChanged(nameof(InstallerFailureTitle));
        OnPropertyChanged(nameof(InstallerFailureSummary));
        OnPropertyChanged(nameof(InstallerFailureRemedy));
        OnPropertyChanged(nameof(InstallerFailureDetail));
        OnPropertyChanged(nameof(CanToggleBuildLog));
        OnPropertyChanged(nameof(ShowBuildLog));
        OnPropertyChanged(nameof(BuildLogButtonText));
        OnPropertyChanged(nameof(BuildLogText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(CheckingBannerText));
    }
}
