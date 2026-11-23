using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

/// <summary>
/// State machine for Contain's "隔离试用" (Isolated Trial) page.
/// Separates read-only local EXE inspection from isolated sandbox execution, tracks
/// SandboxInstanceState and TrialStagingState independently without false "ended" claims,
/// and supports safe, bounded startup recovery of an unfinished session after abnormal exit.
/// </summary>
public sealed class IsolatedTrialViewModel : ObservableObject, IDisposable
{
    private readonly IIsolatedTrialService service;
    private readonly Action<Action> dispatch;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan recoveryQueryTimeout;
    private readonly object opsGate = new();
    private readonly HashSet<Task> pendingOps = [];

    private bool disposed;
    private bool capabilityCheckedOnce;
    private bool checkingCapability;
    private bool pendingCheckStarted;
    private bool operationBusy;
    private SandboxCapabilityReport capability = new(
        SandboxCapabilityStatus.Unknown,
        "尚未检测 Windows 沙盒能力",
        "进入“隔离试用”时自动检测本机是否支持按实例运行 Windows 沙盒；无论本机是否支持沙盒，本页均可只读查看本地 .exe 信息并计算 SHA-256。",
        "",
        SandboxCapabilityPrimaryAction.Recheck,
        "重新检测");

    private string executablePathInput = "";
    private TrialExecutableInfo? selectedExecutable;
    private string selectionError = "";
    private long selectionEpoch;

    // On-demand async SHA-256 state (isolated per selected file via selectionEpoch)
    private TrialHashState hashState = TrialHashState.Idle;
    private string computedSha256 = "";
    private string hashErrorMessage = "";
    private CancellationTokenSource? hashCts;

    private IsolatedTrialPhase phase = IsolatedTrialPhase.Idle;
    private TrialEndReason endReason = TrialEndReason.None;
    private SandboxInstanceState instanceState = SandboxInstanceState.NotCreated;
    private TrialStagingState stagingState = TrialStagingState.NotCreated;
    private SandboxStartResult? activeSession;
    private GuestProgramLaunchResult? lastLaunchResult;
    private bool observedOwnedWindow;
    private bool isRecoveredContext;
    private CancellationTokenSource? activeMonitorCts;
    private CancellationTokenSource? startCts;

    private string lastSessionId = "";
    private string lastInstanceId = "";
    private string lastStagingPath = "";
    private string lastSessionFileName = "";
    private string sessionHeadline = "";
    private string sessionSummary = "";
    private string cleanupStatusText = "";
    private bool isCleanupWarning;
    private string failureNextStep = "";
    private string failureDiagnostic = "";
    private string statusMessage = "";
    private bool technicalDetailsExpanded;
    private string technicalDetailsText = "";

    public IsolatedTrialViewModel(
        IIsolatedTrialService service,
        Action<Action>? dispatch = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? pollInterval = null,
        TimeSpan? recoveryQueryTimeout = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.dispatch = dispatch ?? (action => action());
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1500);
        this.recoveryQueryTimeout = recoveryQueryTimeout ?? TimeSpan.FromSeconds(10);
    }

    public ObservableCollection<TrialEvidenceEntry> EvidenceItems { get; } = [];

    // Capability state & tailored primary actions
    public SandboxCapabilityReport Capability => capability;
    public bool IsCheckingCapability => checkingCapability;
    public bool IsCapabilityReady => capability.IsReady;
    public bool IsCapabilityBlocked =>
        !checkingCapability && capability.Status is not (SandboxCapabilityStatus.Unknown or SandboxCapabilityStatus.Checking or SandboxCapabilityStatus.Ready);

    public string CapabilityBadgeText => checkingCapability
        ? "正在检测…"
        : capability.Status switch
        {
            SandboxCapabilityStatus.Ready => string.IsNullOrWhiteSpace(capability.WsbVersion)
                ? "已就绪"
                : $"已就绪 · wsb {capability.WsbVersion}",
            SandboxCapabilityStatus.SupportedFeatureNotEnabled => "受支持但功能未启用",
            SandboxCapabilityStatus.HomeEditionUnsupported => "家庭版不支持",
            SandboxCapabilityStatus.CliUnavailable => "缺少所需 wsb 命令",
            SandboxCapabilityStatus.VirtualizationDisabled => "硬件虚拟化未开启",
            SandboxCapabilityStatus.PolicyBlocked => "系统策略阻止",
            SandboxCapabilityStatus.RebootPending => "等待重启生效",
            SandboxCapabilityStatus.UnsupportedPlatform => "非 Windows x64 平台",
            SandboxCapabilityStatus.Undetermined => "原因尚未确定",
            _ => "尚未检测"
        };

    public string CapabilityPermissionNote => capability.PermissionNote ?? "";
    public bool HasCapabilityPermissionNote => !string.IsNullOrWhiteSpace(capability.PermissionNote);
    public bool ShowOpenWindowsFeaturesButton =>
        IsCapabilityBlocked && capability.PrimaryAction == SandboxCapabilityPrimaryAction.OpenWindowsFeatures;
    public bool ShowGoToPackagingButton =>
        IsCapabilityBlocked && capability.PrimaryAction == SandboxCapabilityPrimaryAction.GoToProjectPackaging;
    public bool ShowPrimaryRecheckButton =>
        IsCapabilityBlocked && capability.PrimaryAction == SandboxCapabilityPrimaryAction.Recheck;

    // File inspection state (always available even when Sandbox is unavailable)
    public string ExecutablePathInput
    {
        get => executablePathInput;
        set => ApplyExecutablePathInput(value ?? "");
    }

    public bool HasExecutable => selectedExecutable is not null;
    public TrialExecutableInfo? SelectedExecutable => selectedExecutable;
    public string ExecutableFileName => selectedExecutable?.FileName ?? "尚未选择本地 .exe 程序";
    public string ExecutableSizeText => selectedExecutable is null ? "" : $"文件大小：{selectedExecutable.FormattedSize}（{selectedExecutable.SizeBytes:N0} 字节）";
    public string ExecutableArchitectureText => selectedExecutable is null ? "" : $"PE 架构：{selectedExecutable.ArchitectureLabel}";
    public string ExecutableProductVersionText => selectedExecutable is null ? "" : $"产品版本：{selectedExecutable.DisplayVersion}";
    public string SelectionError => selectionError;

    // Calm neutral sibling note (NOT a warning banner)
    public bool HasSiblingFilesNotice => selectedExecutable?.HasSiblingFiles == true;
    public string SiblingFilesNoticeText => selectedExecutable?.HasSiblingFiles == true
        ? "仅带入所选文件：原目录另有其他文件，启动沙箱时不会一并带入。"
        : "";

    // Real compatibility warning ONLY when selected PE cannot run in x64 Sandbox (e.g., ARM64)
    public bool HasRunCompatibilityWarning => selectedExecutable is not null && !selectedExecutable.IsRunnableInX64Sandbox;
    public string RunCompatibilityWarningText => HasRunCompatibilityWarning
        ? $"检测到 {selectedExecutable!.ArchitectureLabel}；当前仍可查看该文件信息与计算 SHA-256，但不能在 Windows x64 沙箱中运行。"
        : "";

    // On-demand read-only SHA-256 state
    public TrialHashState HashState => hashState;
    public string ComputedSha256 => computedSha256;
    public string HashErrorMessage => hashErrorMessage;
    public bool IsComputingSha256 => hashState == TrialHashState.Computing;
    public bool HasComputedSha256 => hashState == TrialHashState.Computed && computedSha256.Length > 0;
    public bool CanComputeSha256 => !disposed && selectedExecutable is not null && !IsComputingSha256;
    public bool CanCopySha256 => !disposed && HasComputedSha256;
    public string ComputeSha256ButtonText => IsComputingSha256
        ? "正在计算 SHA-256…"
        : HasComputedSha256 ? "重新计算 SHA-256" : "计算 SHA-256";

    // Two authoritative independent facts: Sandbox instance state & Host staging state
    public SandboxInstanceState InstanceState => instanceState;
    public TrialStagingState StagingState => stagingState;
    public IsolatedTrialPhase Phase => phase;
    public TrialEndReason EndReason => endReason;
    public bool IsRecovering => phase == IsolatedTrialPhase.Recovering;
    public bool IsStarting => phase == IsolatedTrialPhase.StartingSandbox;
    public bool IsStopping => phase == IsolatedTrialPhase.Stopping;
    public bool IsUnconfirmed => phase == IsolatedTrialPhase.Unconfirmed;
    public bool IsRecoveredRunning => phase == IsolatedTrialPhase.RecoveredRunning;
    public bool IsSessionActive =>
        activeSession is not null && phase is IsolatedTrialPhase.SandboxOpened or IsolatedTrialPhase.ProgramStarted or IsolatedTrialPhase.ProgramExitedAfterStart or IsolatedTrialPhase.RecoveredRunning;
    public bool IsSessionEnded => phase == IsolatedTrialPhase.Ended;
    public bool IsSessionFailed => phase == IsolatedTrialPhase.Failed;
    public bool HasSessionCard => phase != IsolatedTrialPhase.Idle;
    public bool HasEvidenceItems => EvidenceItems.Count > 0;

    // Non-blocking banner for MainWindow when an unresolved session was discovered on startup
    public bool HasPendingRecoveryBanner =>
        phase is IsolatedTrialPhase.RecoveredRunning or IsolatedTrialPhase.Unconfirmed
        || (phase == IsolatedTrialPhase.Ended && stagingState is TrialStagingState.Remaining or TrialStagingState.Unconfirmed);

    public string PendingRecoveryBannerText => phase switch
    {
        IsolatedTrialPhase.RecoveredRunning => "检测到上次隔离试用仍在运行，点击前往「隔离试用」结束这次试用。",
        IsolatedTrialPhase.Unconfirmed => "暂时无法确认上次隔离试用状态，点击前往「隔离试用」重新检查。",
        IsolatedTrialPhase.Ended when stagingState is TrialStagingState.Remaining or TrialStagingState.Unconfirmed
            => "上次隔离试用已结束，但临时文件尚未完全清理，点击前往「隔离试用」重试清理。",
        _ => ""
    };

    // Guarded action availability
    private bool HasBlockingSessionState =>
        IsStarting || IsStopping || IsRecovering || IsSessionActive || IsUnconfirmed || operationBusy;

    public bool CanRecheckCapability => !disposed && !checkingCapability && !IsStarting && !IsStopping;
    public bool CanSelectExecutable => !disposed && !IsStarting && !IsStopping && !IsSessionActive;
    public bool CanStartTrial =>
        !disposed
        && !checkingCapability
        && capability.IsReady
        && selectedExecutable is not null
        && selectedExecutable.IsRunnableInX64Sandbox
        && !HasBlockingSessionState
        && stagingState is not TrialStagingState.Remaining;

    public bool CanEndTrial => !disposed && IsSessionActive && !IsStopping && !operationBusy;
    public string EndTrialButtonText => phase == IsolatedTrialPhase.RecoveredRunning ? "结束这次试用" : "结束试用";

    public bool CanRecheckPendingSession =>
        !disposed && !operationBusy && !IsStopping && !IsRecovering
        && phase == IsolatedTrialPhase.Unconfirmed
        && !string.IsNullOrEmpty(lastInstanceId);

    public bool CanRetryEndPendingSession =>
        !disposed && !operationBusy && !IsStopping && !IsRecovering
        && phase == IsolatedTrialPhase.Unconfirmed
        && !string.IsNullOrEmpty(lastInstanceId);

    public string RetryEndPendingButtonText => isRecoveredContext ? "结束这次试用" : "重试结束";

    public bool CanRetryStagingCleanup =>
        !disposed && !operationBusy && !IsStopping && !IsRecovering
        && instanceState is SandboxInstanceState.ConfirmedEnded or SandboxInstanceState.NotCreated
        && stagingState is TrialStagingState.Remaining or TrialStagingState.Unconfirmed
        && !string.IsNullOrEmpty(lastStagingPath);

    public bool CanRetryTrial =>
        CanStartTrial
        && phase is IsolatedTrialPhase.Ended or IsolatedTrialPhase.Failed
        && instanceState is SandboxInstanceState.NotCreated or SandboxInstanceState.ConfirmedEnded
        && stagingState is TrialStagingState.NotCreated or TrialStagingState.Cleaned;

    public string SandboxRunAvailabilityHint =>
        phase == IsolatedTrialPhase.RecoveredRunning
            ? "上次隔离试用仍在运行，请先结束当前实例后再开启新的试用。"
            : phase == IsolatedTrialPhase.Unconfirmed
                ? "当前尚无法确认上次试用实例状态，请先“重新检查”或“重试结束”。"
                : stagingState == TrialStagingState.Remaining && phase is IsolatedTrialPhase.Ended or IsolatedTrialPhase.Failed
                    ? "上次临时文件仍有残留，请先点击上方“重试清理”。"
                    : capability.IsReady
                        ? ""
                        : checkingCapability
                            ? "正在检测本机 Windows 沙盒能力；期间已可查看文件信息与计算 SHA-256。"
                            : "本机当前无法启动 Windows 沙盒，仅禁用“开始隔离试用”；上方文件信息查看与 SHA-256 计算均可正常使用。";

    // Consistent badges: separate Sandbox instance state from Program / Staging state
    public string SandboxStageBadge => phase switch
    {
        IsolatedTrialPhase.Idle => "未启动",
        IsolatedTrialPhase.Recovering => "正在检查上次试用…",
        IsolatedTrialPhase.StartingSandbox => "正在打开沙箱…",
        IsolatedTrialPhase.SandboxOpened or IsolatedTrialPhase.ProgramStarted or IsolatedTrialPhase.ProgramExitedAfterStart => "沙箱已打开",
        IsolatedTrialPhase.RecoveredRunning => "仍在运行",
        IsolatedTrialPhase.Stopping => "正在结束并清理…",
        IsolatedTrialPhase.Ended => "已确认结束",
        IsolatedTrialPhase.Unconfirmed => instanceState == SandboxInstanceState.Running ? "仍在运行" : "状态未知",
        IsolatedTrialPhase.Failed => FormatInstanceStateLabel(instanceState),
        _ => "状态未知"
    };

    public string StagingStageBadge => FormatStagingStateLabel(stagingState);

    public bool ShowProgramStageBadge =>
        phase is IsolatedTrialPhase.StartingSandbox or IsolatedTrialPhase.SandboxOpened or IsolatedTrialPhase.ProgramStarted or IsolatedTrialPhase.ProgramExitedAfterStart;

    public bool ShowStagingStageBadge =>
        phase is IsolatedTrialPhase.RecoveredRunning or IsolatedTrialPhase.Stopping or IsolatedTrialPhase.Ended or IsolatedTrialPhase.Unconfirmed or IsolatedTrialPhase.Failed;

    public string ProgramStageBadge => phase switch
    {
        IsolatedTrialPhase.Idle => "未启动",
        IsolatedTrialPhase.StartingSandbox => "等待沙箱就绪",
        IsolatedTrialPhase.SandboxOpened => lastLaunchResult is null
            ? "正在沙箱内启动程序…"
            : "程序未成功启动",
        IsolatedTrialPhase.ProgramStarted => "程序已启动",
        IsolatedTrialPhase.ProgramExitedAfterStart => lastLaunchResult?.Outcome == GuestProgramLaunchOutcome.StartedAndExitedZero
            ? "程序已启动并正常退出"
            : "程序启动后异常退出",
        IsolatedTrialPhase.RecoveredRunning => "上次试用仍在运行",
        IsolatedTrialPhase.Stopping => "正在结束会话",
        IsolatedTrialPhase.Ended => stagingState is TrialStagingState.Cleaned or TrialStagingState.NotCreated ? "已确认结束" : "已结束（暂存待清理）",
        IsolatedTrialPhase.Unconfirmed => "状态尚未确认",
        IsolatedTrialPhase.Failed => "未启动",
        _ => "状态未知"
    };

    public string ActiveInstanceIdText => string.IsNullOrEmpty(lastInstanceId) ? "" : $"实例 ID：{lastInstanceId}";
    public string SessionHeadline => sessionHeadline;
    public string SessionSummary => sessionSummary;
    public string CleanupStatusText => cleanupStatusText;
    public bool HasCleanupStatus => !string.IsNullOrWhiteSpace(cleanupStatusText);
    public bool IsCleanupWarning => isCleanupWarning;
    public string FailureNextStep => failureNextStep;
    public string FailureDiagnostic => failureDiagnostic;
    public string StatusMessage => statusMessage;
    public bool LastStagingCleaned => stagingState is TrialStagingState.Cleaned or TrialStagingState.NotCreated;
    public string LastInstanceId => lastInstanceId;
    public string LastStagingPath => lastStagingPath;

    public bool CanShowTechnicalDetails => technicalDetailsText.Length > 0;
    public bool TechnicalDetailsExpanded
    {
        get => technicalDetailsExpanded;
        set => SetProperty(ref technicalDetailsExpanded, value);
    }
    public string TechnicalDetailsText => technicalDetailsText;

    public void CancelSelection() { }

    /// <summary>
    /// Bounded non-blocking startup check for any pending session left behind by an abnormal Contain exit.
    /// Does not block MainWindow or other pages, and returns immediately when no pending file exists.
    /// </summary>
    public Task CheckPendingSessionOnStartupAsync()
    {
        if (disposed || pendingCheckStarted || IsStarting || IsSessionActive)
            return Task.CompletedTask;
        pendingCheckStarted = true;

        var loaded = service.LoadPendingSession();
        if (loaded.Status == PendingSessionLoadStatus.None)
            return Task.CompletedTask;

        if (loaded.Status == PendingSessionLoadStatus.OwnedByAnotherLiveProcess)
        {
            AddEvidence("多窗口保护", $"检测到另一个正在运行的 Contain 窗口持有会话（{loaded.Diagnostic}），本窗口不介入该实例或暂存目录。");
            RebuildTechnicalDetails();
            NotifyAll();
            return Task.CompletedTask;
        }

        if (loaded.Status == PendingSessionLoadStatus.Corrupt || loaded.Record is null)
        {
            AddEvidence("恢复记录已忽略", $"发现损坏或未通过路径核验的待恢复记录，已安全忽略且未操作任何沙箱或目录（{loaded.Diagnostic}）。");
            RebuildTechnicalDetails();
            NotifyAll();
            return Task.CompletedTask;
        }

        return Track(() => RecoverPendingRecordCoreAsync(loaded.Record));
    }

    private async Task RecoverPendingRecordCoreAsync(PendingTrialSessionRecord record)
    {
        if (disposed || operationBusy) return;
        operationBusy = true;
        isRecoveredContext = true;
        lastSessionId = record.SessionId;
        lastInstanceId = record.InstanceId;
        lastStagingPath = record.StagingRootPath;
        lastSessionFileName = record.FileName;
        phase = IsolatedTrialPhase.Recovering;
        instanceState = SandboxInstanceState.Unknown;
        stagingState = TrialStagingState.Unconfirmed;
        sessionHeadline = "正在检查上次未结束的隔离试用…";
        sessionSummary = "后台限时查询上次沙箱实例状态，不影响你使用 Contain 其他页面。";
        cleanupStatusText = "";
        isCleanupWarning = false;
        failureNextStep = "";
        failureDiagnostic = "";
        AddEvidence("启动恢复检查", $"读取到上次未结束会话记录（目标：{record.FileName}，实例：{record.InstanceId}），正在限时查询该确切实例状态。");
        RebuildTechnicalDetails();
        NotifyAll();

        try
        {
            using var timeoutCts = new CancellationTokenSource(recoveryQueryTimeout);
            var poll = await service.QueryInstanceStateAsync(record.InstanceId, record.StagingRootPath, timeoutCts.Token).ConfigureAwait(true);
            if (disposed) return;

            await ApplyPendingQueryResultAsync(record.SessionId, record.InstanceId, record.StagingRootPath, record.FileName, poll).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (disposed) return;
            ApplyUnconfirmedState(
                isRecovery: true,
                instance: SandboxInstanceState.Unknown,
                staging: TrialStagingState.Unconfirmed,
                diagnostic: $"{ex.GetType().Name}: {ex.Message}",
                evidenceStage: "恢复查询异常",
                evidenceDetail: $"限时查询上次实例状态失败：{ex.Message}");
        }
        finally
        {
            operationBusy = false;
            NotifyAll();
        }
    }

    public Task<bool> RecheckPendingSessionAsync()
    {
        if (!CanRecheckPendingSession) return Task.FromResult(false);
        operationBusy = true;
        var sessionId = lastSessionId;
        var instanceId = lastInstanceId;
        var stagingPath = lastStagingPath;
        var fileName = lastSessionFileName;
        var wasRecovery = isRecoveredContext;
        phase = IsolatedTrialPhase.Recovering;
        sessionHeadline = wasRecovery ? "正在重新检查上次试用状态…" : "正在重新确认本次试用状态…";
        sessionSummary = "正在限时查询该确切沙箱实例和暂存目录状态…";
        statusMessage = sessionHeadline;
        NotifyAll();

        return Track(async () =>
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(recoveryQueryTimeout);
                var poll = await service.QueryInstanceStateAsync(instanceId, stagingPath, timeoutCts.Token).ConfigureAwait(true);
                if (disposed) return false;
                await ApplyPendingQueryResultAsync(sessionId, instanceId, stagingPath, fileName, poll).ConfigureAwait(true);
                return instanceState != SandboxInstanceState.Unknown;
            }
            catch (Exception ex)
            {
                if (disposed) return false;
                ApplyUnconfirmedState(
                    isRecovery: wasRecovery,
                    instance: SandboxInstanceState.Unknown,
                    staging: stagingState,
                    diagnostic: $"{ex.GetType().Name}: {ex.Message}",
                    evidenceStage: "重新检查失败",
                    evidenceDetail: $"重新查询实例状态出错：{ex.Message}");
                return false;
            }
            finally
            {
                operationBusy = false;
                NotifyAll();
            }
        });
    }

    private async Task ApplyPendingQueryResultAsync(
        string sessionId,
        string instanceId,
        string stagingPath,
        string fileName,
        SandboxInstancePollResult poll)
    {
        instanceState = poll.InstanceState;
        stagingState = poll.InstanceState == SandboxInstanceState.Running && poll.StagingState == TrialStagingState.Remaining
            ? TrialStagingState.Active
            : poll.StagingState;
        var displayTarget = string.IsNullOrWhiteSpace(fileName) ? "上次程序" : $"“{fileName}”";

        switch (poll.InstanceState)
        {
            case SandboxInstanceState.Running:
                activeSession = new SandboxStartResult(
                    sessionId,
                    instanceId,
                    stagingPath,
                    Path.Combine(stagingPath, "input"),
                    null,
                    clock());
                observedOwnedWindow = poll.OwnedWindowRunning == true;
                phase = IsolatedTrialPhase.RecoveredRunning;
                sessionHeadline = "上次试用仍在运行";
                sessionSummary = $"上次隔离试用（{displayTarget}）的沙箱实例仍在运行。可点击下方“结束这次试用”立即停止并清理临时文件。";
                cleanupStatusText = stagingState == TrialStagingState.Active
                    ? "本次宿主暂存目录仍在使用中，待沙箱结束后自动清理。"
                    : $"本次宿主暂存目录：{FormatStagingStateLabel(stagingState)}";
                isCleanupWarning = false;
                failureNextStep = "";
                failureDiagnostic = "";
                statusMessage = "检测到上次隔离试用仍在运行，可点击“结束这次试用”。";
                AddEvidence("恢复确认：仍在运行", $"可靠查询确认实例 {instanceId} 仍在运行（暂存目录：{FormatStagingStateLabel(stagingState)}）。");
                RebuildTechnicalDetails();
                NotifyAll();
                StartSessionMonitor(activeSession);
                break;

            case SandboxInstanceState.ConfirmedEnded:
                activeSession = null;
                if (stagingState == TrialStagingState.Remaining && !string.IsNullOrEmpty(stagingPath))
                {
                    // Instance is confirmed ended: attempt verified staging cleanup immediately
                    var cleanRes = await service.CleanupStagingOnlyAsync(stagingPath).ConfigureAwait(true);
                    if (disposed) return;
                    stagingState = cleanRes.StagingState;
                    failureDiagnostic = cleanRes.Diagnostic;
                }

                if (stagingState is TrialStagingState.Cleaned or TrialStagingState.NotCreated)
                {
                    service.ClearPendingSession(instanceId, sessionId);
                    endReason = TrialEndReason.RecoveredAlreadyExited;
                    phase = IsolatedTrialPhase.Ended;
                    sessionHeadline = "试用已结束";
                    sessionSummary = $"已确认上次隔离试用（{displayTarget}）的沙箱实例已结束，来宾内改动已丢弃。";
                    cleanupStatusText = "本次临时文件已清理";
                    isCleanupWarning = false;
                    failureNextStep = "";
                    statusMessage = "上次试用已确认结束，本次临时文件已清理。";
                    AddEvidence("恢复确认：已结束并清理", $"可靠查询确认实例 {instanceId} 已不在运行列表中，暂存目录已清理，待恢复记录已移除。");
                }
                else
                {
                    endReason = TrialEndReason.RecoveredAlreadyExited;
                    phase = IsolatedTrialPhase.Ended;
                    sessionHeadline = "试用已结束，但临时文件仍有残留";
                    sessionSummary = $"已确认上次隔离试用（{displayTarget}）的沙箱实例已结束，但宿主机暂存目录尚未完全删除。";
                    cleanupStatusText = stagingState == TrialStagingState.Remaining
                        ? "本次临时文件仍有残留，请点击“重试清理”（处理完成后再移除恢复记录）。"
                        : "本次临时文件清理状态尚未确认，请展开“技术详情”核对或点击“重试清理”。";
                    isCleanupWarning = true;
                    failureNextStep = "请确认没有其他程序占用临时目录后点击“重试清理”。";
                    statusMessage = "上次试用已结束，但临时文件仍有残留。";
                    AddEvidence("恢复确认：暂存待清理", $"实例 {instanceId} 已确认结束，但暂存目录状态为：{FormatStagingStateLabel(stagingState)}。");
                }
                RebuildTechnicalDetails();
                NotifyAll();
                break;

            default:
                ApplyUnconfirmedState(
                    isRecovery: isRecoveredContext,
                    instance: SandboxInstanceState.Unknown,
                    staging: poll.StagingState,
                    diagnostic: poll.Diagnostic,
                    evidenceStage: "状态尚未确认",
                    evidenceDetail: $"未能取得有效的实例列表以确认 {instanceId} 的状态（{poll.Diagnostic}），已保留恢复记录。");
                break;
        }
    }

    public Task<bool> RetryEndPendingSessionAsync()
    {
        if (!CanRetryEndPendingSession) return Task.FromResult(false);
        var synthetic = new SandboxStartResult(
            lastSessionId,
            lastInstanceId,
            lastStagingPath,
            Path.Combine(lastStagingPath, "input"),
            null,
            clock());
        return EndSessionCoreAsync(synthetic, isRecoveredContext ? TrialEndReason.RecoveredAndEnded : TrialEndReason.EndedByContain);
    }

    public Task<bool> RetryStagingCleanupAsync()
    {
        if (!CanRetryStagingCleanup) return Task.FromResult(false);
        operationBusy = true;
        var instanceId = lastInstanceId;
        var sessionId = lastSessionId;
        var stagingPath = lastStagingPath;
        statusMessage = "正在重试清理本次宿主暂存目录…";
        NotifyAll();

        return Track(async () =>
        {
            try
            {
                var res = await service.CleanupStagingOnlyAsync(stagingPath).ConfigureAwait(true);
                if (disposed) return false;
                stagingState = res.StagingState;
                failureDiagnostic = res.Diagnostic;

                if (stagingState is TrialStagingState.Cleaned or TrialStagingState.NotCreated)
                {
                    service.ClearPendingSession(instanceId, sessionId);
                    if (phase == IsolatedTrialPhase.Ended)
                    {
                        sessionHeadline = "试用已结束";
                        sessionSummary = "来宾内所有改动已丢弃，宿主机原文件未受影响。";
                    }
                    else if (phase == IsolatedTrialPhase.Failed)
                    {
                        sessionSummary = "本次未能建立隔离试用会话，所选程序未在宿主机上运行。";
                    }
                    cleanupStatusText = "本次临时文件已清理";
                    isCleanupWarning = false;
                    failureNextStep = "";
                    statusMessage = "本次临时文件已清理，待恢复记录已移除。";
                    AddEvidence("重试清理成功", "经核对的本次宿主暂存目录已清理，待恢复记录已移除。");
                    RebuildTechnicalDetails();
                    NotifyAll();
                    return true;
                }

                cleanupStatusText = stagingState == TrialStagingState.Remaining
                    ? "本次临时文件仍有残留，请确认无占用后再次点击“重试清理”。"
                    : "本次临时文件清理状态尚未确认（路径未通过核验或无法访问），未删除目录。";
                isCleanupWarning = true;
                statusMessage = cleanupStatusText;
                AddEvidence("重试清理未完成", $"暂存目录状态：{FormatStagingStateLabel(stagingState)}（{res.Diagnostic}）。");
                RebuildTechnicalDetails();
                NotifyAll();
                return false;
            }
            finally
            {
                operationBusy = false;
                NotifyAll();
            }
        });
    }

    public Task EnsureCapabilityCheckedAsync()
    {
        if (capabilityCheckedOnce || checkingCapability || disposed)
            return Task.CompletedTask;
        return RefreshCapabilityAsync();
    }

    public Task<SandboxCapabilityReport> RefreshCapabilityAsync() => Track(RefreshCapabilityCoreAsync);

    private async Task<SandboxCapabilityReport> RefreshCapabilityCoreAsync()
    {
        if (disposed || checkingCapability || IsStarting || IsStopping)
            return capability;

        checkingCapability = true;
        capabilityCheckedOnce = true;
        if (phase is IsolatedTrialPhase.Idle or IsolatedTrialPhase.Ended or IsolatedTrialPhase.Failed)
            statusMessage = "正在以普通用户权限检测本机 Windows 沙盒能力…";
        NotifyAll();

        try
        {
            var report = await service.ProbeCapabilityAsync().ConfigureAwait(true);
            if (disposed) return report;
            capability = report;
            checkingCapability = false;
            if (phase is IsolatedTrialPhase.Idle or IsolatedTrialPhase.Ended or IsolatedTrialPhase.Failed)
            {
                statusMessage = report.IsReady
                    ? $"Windows 沙盒已就绪（wsb {report.WsbVersion}）。"
                    : $"{report.Headline}（仍可查看本地 .exe 信息与计算 SHA-256）。";
            }
            RebuildTechnicalDetails();
            NotifyAll();
            return report;
        }
        catch (Exception error)
        {
            if (disposed) return capability;
            capability = new SandboxCapabilityReport(
                SandboxCapabilityStatus.Undetermined,
                "检测 Windows 沙盒能力时出错（原因尚未确定）",
                "检测过程返回未预期错误，Contain 未修改任何系统设置。你仍可在本页查看本地 .exe 信息与计算 SHA-256。",
                "请展开“技术详情”查看原始错误，确认环境后点击“重新检测”。",
                SandboxCapabilityPrimaryAction.Recheck,
                "重新检测",
                Diagnostic: $"{error.GetType().Name}: {error.Message}");
            checkingCapability = false;
            if (phase is IsolatedTrialPhase.Idle or IsolatedTrialPhase.Ended or IsolatedTrialPhase.Failed)
                statusMessage = capability.Headline;
            RebuildTechnicalDetails();
            NotifyAll();
            return capability;
        }
    }

    public void OpenWindowsFeatures()
    {
        if (disposed) return;
        service.OpenWindowsFeatures();
        statusMessage = "已打开“启用或关闭 Windows 功能”。如需开启 Windows 沙盒，请自行勾选并按需重启，完成后点击“重新检测”。";
        NotifyAll();
    }

    public bool SelectExecutable(string path)
    {
        if (!CanSelectExecutable) return false;

        CancelActiveHash();
        selectionEpoch++;
        ResetHashStateOnly();
        selectionError = "";

        try
        {
            var info = service.InspectExecutable(path);
            selectedExecutable = info;
            executablePathInput = info.FullPath;
            if (phase is IsolatedTrialPhase.Ended or IsolatedTrialPhase.Failed
                && instanceState is SandboxInstanceState.NotCreated or SandboxInstanceState.ConfirmedEnded
                && stagingState is TrialStagingState.NotCreated or TrialStagingState.Cleaned)
            {
                ResetSessionDisplayForNewSelection();
            }
            statusMessage = capability.IsReady && info.IsRunnableInX64Sandbox
                ? $"已选择“{info.FileName}”（{info.FormattedSize}）；可查看文件信息、按需计算 SHA-256，或点击“开始隔离试用”。"
                : $"已只读读取“{info.FileName}”（{info.FormattedSize}，{info.ArchitectureLabel}）；可按需计算 SHA-256。";
            RebuildTechnicalDetails();
            NotifyAll();
            return true;
        }
        catch (IsolatedTrialException error)
        {
            selectedExecutable = null;
            executablePathInput = path ?? "";
            selectionError = error.Message;
            statusMessage = error.Message;
            RebuildTechnicalDetails();
            NotifyAll();
            return false;
        }
    }

    private void ApplyExecutablePathInput(string rawValue)
    {
        if (disposed || !CanSelectExecutable || string.Equals(executablePathInput, rawValue, StringComparison.Ordinal))
            return;

        CancelActiveHash();
        selectionEpoch++;
        ResetHashStateOnly();

        executablePathInput = rawValue;
        OnPropertyChanged(nameof(ExecutablePathInput));

        var trimmed = rawValue.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            selectedExecutable = null;
            selectionError = "";
            statusMessage = "";
            RebuildTechnicalDetails();
            NotifyAll();
            return;
        }

        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
        {
            SelectExecutable(trimmed);
            return;
        }

        selectedExecutable = null;
        selectionError = "";
        statusMessage = "输入的 .exe 路径尚不存在；可继续输入完整路径或点击“选择 .exe 文件”。";
        RebuildTechnicalDetails();
        NotifyAll();
    }

    public Task<string?> ComputeSha256Async()
    {
        if (!CanComputeSha256 || selectedExecutable is null)
            return Task.FromResult<string?>(null);

        CancelActiveHash();
        var epoch = selectionEpoch;
        var target = selectedExecutable;
        var cts = new CancellationTokenSource();
        hashCts = cts;
        hashState = TrialHashState.Computing;
        computedSha256 = "";
        hashErrorMessage = "";
        statusMessage = $"正在只读计算“{target.FileName}”的 SHA-256，界面保持可用…";
        NotifyAll();

        return Track(() => ComputeSha256CoreAsync(target, epoch, cts.Token));
    }

    private async Task<string?> ComputeSha256CoreAsync(TrialExecutableInfo target, long epoch, CancellationToken token)
    {
        try
        {
            var digest = await service.ComputeSha256Async(target.FullPath, token).ConfigureAwait(true);
            if (disposed
                || epoch != selectionEpoch
                || selectedExecutable is null
                || !string.Equals(selectedExecutable.FullPath, target.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            hashState = TrialHashState.Computed;
            computedSha256 = digest;
            hashErrorMessage = "";
            statusMessage = $"已完成“{target.FileName}”的 SHA-256 计算（仅本地只读校验，不代表安全评分）。";
            RebuildTechnicalDetails();
            NotifyAll();
            return digest;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception error)
        {
            if (disposed || epoch != selectionEpoch) return null;
            hashState = TrialHashState.Failed;
            computedSha256 = "";
            hashErrorMessage = error.Message;
            statusMessage = error.Message;
            RebuildTechnicalDetails();
            NotifyAll();
            return null;
        }
    }

    public Task<bool> StartTrialAsync()
    {
        // Synchronous concurrency guard before any await prevents duplicate sessions on rapid clicks
        if (!CanStartTrial || selectedExecutable is null)
            return Task.FromResult(false);

        var target = selectedExecutable;
        isRecoveredContext = false;
        phase = IsolatedTrialPhase.StartingSandbox;
        endReason = TrialEndReason.None;
        instanceState = SandboxInstanceState.NotCreated;
        stagingState = TrialStagingState.NotCreated;
        activeSession = null;
        lastLaunchResult = null;
        observedOwnedWindow = false;
        lastSessionId = "";
        lastInstanceId = "";
        lastStagingPath = "";
        lastSessionFileName = target.FileName;
        cleanupStatusText = "";
        isCleanupWarning = false;
        failureNextStep = "";
        failureDiagnostic = "";
        EvidenceItems.Clear();

        sessionHeadline = $"正在打开隔离沙箱并启动“{target.FileName}”…";
        sessionSummary = "正在准备只读输入目录、启动离线且禁用剪贴板共享的 Windows 沙箱并等待就绪（最多 60 秒）。";
        statusMessage = sessionHeadline;
        AddEvidence("启动准备", $"已锁定单文件目标“{target.FileName}”（{target.FormattedSize}），配置关闭网络与剪贴板共享、只读映射输入目录。");

        startCts?.Dispose();
        startCts = new CancellationTokenSource();
        var token = startCts.Token;
        NotifyAll();

        return Track(() => StartTrialCoreAsync(target, token));
    }

    private async Task<bool> StartTrialCoreAsync(TrialExecutableInfo target, CancellationToken token)
    {
        try
        {
            var session = await service.StartSandboxAsync(target, token).ConfigureAwait(true);
            if (disposed)
            {
                await service.StopAndCleanupAsync(session.InstanceId, session.StagingRootPath).ConfigureAwait(false);
                return false;
            }

            activeSession = session;
            lastSessionId = session.SessionId;
            lastInstanceId = session.InstanceId;
            lastStagingPath = session.StagingRootPath;
            instanceState = SandboxInstanceState.Running;
            stagingState = TrialStagingState.Active;
            phase = IsolatedTrialPhase.SandboxOpened;
            sessionHeadline = "沙箱已打开，正在沙箱内启动程序…";
            sessionSummary = $"正在将“{target.FileName}”复制到沙箱本地并启动，请稍候。";
            statusMessage = sessionHeadline;
            AddEvidence("沙箱已打开", $"实例 {session.InstanceId} 已创建并响应 ExistingLogin 就绪检测（只读目录 C:\\ContainTrialInput 已挂载，连接进程 PID {(session.ConnectProcessId?.ToString(CultureInfo.InvariantCulture) ?? "已启动")}）。");
            RebuildTechnicalDetails();
            NotifyAll();

            var launch = await service.LaunchProgramInSandboxAsync(session, target, token).ConfigureAwait(true);
            if (disposed)
            {
                await service.StopAndCleanupAsync(session.InstanceId, session.StagingRootPath).ConfigureAwait(false);
                return false;
            }

            lastLaunchResult = launch;
            switch (launch.Outcome)
            {
                case GuestProgramLaunchOutcome.StartedAndRunning:
                    phase = IsolatedTrialPhase.ProgramStarted;
                    sessionHeadline = $"程序已在沙箱中启动：“{target.FileName}”";
                    sessionSummary = "请切换到已打开的 Windows 沙箱窗口中操作该程序。完成后直接关闭沙箱窗口，或点击下方“结束试用”。";
                    AddEvidence("程序已启动", launch.Summary);
                    break;

                case GuestProgramLaunchOutcome.StartedAndExitedZero:
                    phase = IsolatedTrialPhase.ProgramExitedAfterStart;
                    sessionHeadline = $"程序已在沙箱中运行并退出：“{target.FileName}”";
                    sessionSummary = "沙箱窗口仍保持打开，可在沙箱内查看结果；完成后直接关闭沙箱窗口或点击“结束试用”。";
                    AddEvidence("程序已启动并退出", launch.Summary);
                    break;

                case GuestProgramLaunchOutcome.StartedAndExitedNonZero:
                    phase = IsolatedTrialPhase.ProgramExitedAfterStart;
                    sessionHeadline = $"沙箱已打开，但“{target.FileName}”启动后异常退出";
                    sessionSummary = "若该程序需要联网下载或依赖原目录其他文件，在离线单文件沙箱中可能无法运行。完成后请关闭沙箱窗口或点击“结束试用”。";
                    AddEvidence("程序启动后异常退出", launch.Summary);
                    break;

                default:
                    phase = IsolatedTrialPhase.SandboxOpened;
                    sessionHeadline = $"沙箱已打开，但未能启动“{target.FileName}”";
                    sessionSummary = "可展开下方“技术详情”查看来宾返回码，或直接点击“结束试用”关闭沙箱。";
                    failureDiagnostic = launch.Diagnostic;
                    AddEvidence("程序启动受阻", launch.Summary);
                    break;
            }

            statusMessage = sessionHeadline;
            RebuildTechnicalDetails();
            NotifyAll();

            StartSessionMonitor(session);
            return true;
        }
        catch (OperationCanceledException) when (disposed)
        {
            return false;
        }
        catch (IsolatedTrialException error)
        {
            if (disposed) return false;
            activeSession = null;
            if (!string.IsNullOrEmpty(error.SessionId)) lastSessionId = error.SessionId;
            if (!string.IsNullOrEmpty(error.CreatedInstanceId)) lastInstanceId = error.CreatedInstanceId;
            if (!string.IsNullOrEmpty(error.StagingRootPath)) lastStagingPath = error.StagingRootPath;

            // Read REAL rollback result instead of hardcoding "cleaned"
            var rb = error.RollbackResult;
            instanceState = rb?.InstanceState ?? (string.IsNullOrEmpty(error.CreatedInstanceId) ? SandboxInstanceState.NotCreated : SandboxInstanceState.Unknown);
            stagingState = rb?.StagingState ?? (string.IsNullOrEmpty(error.StagingRootPath) ? TrialStagingState.NotCreated : TrialStagingState.Unconfirmed);
            failureDiagnostic = error.Diagnostic;

            if (instanceState is SandboxInstanceState.Running or SandboxInstanceState.Unknown)
            {
                phase = IsolatedTrialPhase.Unconfirmed;
                sessionHeadline = $"启动失败，且未能确认临时沙箱已结束：{error.Message}";
                sessionSummary = "本次未能建立隔离试用会话，所选程序未在宿主机上运行；已保留该临时实例关联信息，请点击“重新检查”或“重试结束”。";
                cleanupStatusText = $"沙箱实例：{FormatInstanceStateLabel(instanceState)}；宿主暂存目录：{FormatStagingStateLabel(stagingState)}。";
                isCleanupWarning = true;
                failureNextStep = "请稍后点击“重新检查”或“重试结束”，确认清理完成后再开启新试用。";
            }
            else if (stagingState is TrialStagingState.Remaining or TrialStagingState.Unconfirmed)
            {
                phase = IsolatedTrialPhase.Failed;
                sessionHeadline = $"无法开始隔离试用：{error.Message}";
                sessionSummary = "本次未能建立隔离试用会话，所选程序未在宿主机上运行；但临时暂存目录尚未完全清理。";
                cleanupStatusText = stagingState == TrialStagingState.Remaining
                    ? "本次临时文件仍有残留，请点击“重试清理”。"
                    : "本次临时文件清理状态尚未确认，请展开“技术详情”核对或点击“重试清理”。";
                isCleanupWarning = true;
                failureNextStep = string.IsNullOrWhiteSpace(error.NextStep)
                    ? "请先点击“重试清理”处理临时文件，再排查上述原因后重试。"
                    : error.NextStep;
            }
            else
            {
                phase = IsolatedTrialPhase.Failed;
                sessionHeadline = $"无法开始隔离试用：{error.Message}";
                sessionSummary = "本次未能建立隔离试用会话，所选程序未在宿主机上运行。";
                cleanupStatusText = stagingState == TrialStagingState.Cleaned
                    ? "已确认回滚并清理本次临时文件，宿主机原文件未受影响。"
                    : "未创建临时沙箱实例，宿主机原文件未受影响。";
                isCleanupWarning = false;
                failureNextStep = string.IsNullOrWhiteSpace(error.NextStep)
                    ? "请排查上述原因后点击“重新检测”或再次点击“开始隔离试用”。"
                    : error.NextStep;
            }

            statusMessage = sessionHeadline;
            AddEvidence("启动失败", $"{error.Message}（错误码：{error.Code}；回滚：实例={FormatInstanceStateLabel(instanceState)}，暂存={FormatStagingStateLabel(stagingState)}）");
            RebuildTechnicalDetails();
            NotifyAll();
            return false;
        }
        catch (Exception error)
        {
            if (disposed) return false;
            activeSession = null;
            instanceState = string.IsNullOrEmpty(lastInstanceId) ? SandboxInstanceState.NotCreated : SandboxInstanceState.Unknown;
            stagingState = TrialStagingState.Unconfirmed;
            phase = instanceState == SandboxInstanceState.Unknown ? IsolatedTrialPhase.Unconfirmed : IsolatedTrialPhase.Failed;
            sessionHeadline = instanceState == SandboxInstanceState.Unknown
                ? "启动异常，且暂时无法确认临时沙箱状态"
                : "启动隔离试用时发生未预期错误";
            sessionSummary = $"{error.Message}（所选程序未在宿主机上运行。）";
            cleanupStatusText = $"沙箱实例：{FormatInstanceStateLabel(instanceState)}；宿主暂存目录：{FormatStagingStateLabel(stagingState)}。";
            isCleanupWarning = true;
            failureNextStep = "请展开“技术详情”核对后点击“重新检查”或“重新检测”。";
            failureDiagnostic = $"{error.GetType().Name}: {error.Message}";
            statusMessage = sessionHeadline;
            AddEvidence("启动异常", $"{error.GetType().Name}: {error.Message}");
            RebuildTechnicalDetails();
            NotifyAll();
            return false;
        }
    }

    public Task<bool> EndTrialAsync()
    {
        if (!CanEndTrial || activeSession is null)
            return Task.FromResult(false);

        var reason = phase == IsolatedTrialPhase.RecoveredRunning
            ? TrialEndReason.RecoveredAndEnded
            : TrialEndReason.EndedByContain;
        return EndSessionCoreAsync(activeSession, reason);
    }

    private void StartSessionMonitor(SandboxStartResult session)
    {
        StopMonitorOnly();
        var cts = new CancellationTokenSource();
        activeMonitorCts = cts;
        _ = Track(() => MonitorLoopAsync(session, cts.Token));
    }

    private async Task MonitorLoopAsync(SandboxStartResult session, CancellationToken token)
    {
        try
        {
            var firstTick = true;
            while (!token.IsCancellationRequested && !disposed)
            {
                var delay = firstTick && pollInterval > TimeSpan.FromMilliseconds(200)
                    ? TimeSpan.FromMilliseconds(200)
                    : pollInterval;
                firstTick = false;
                await Task.Delay(delay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || disposed) return;

                var poll = await service.PollSessionAsync(session, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || disposed) return;

                if (poll.OwnedWindowRunning == true)
                {
                    observedOwnedWindow = true;
                }

                // ONLY treat explicit ConfirmedEnded (valid parsed `wsb list` omitting this ID) as external exit.
                // Transient CLI unavailability / timeout / bad JSON returns Unknown and NEVER falsely ends the session!
                if (poll.InstanceState == SandboxInstanceState.ConfirmedEnded)
                {
                    await CompleteExternalEndAsync(session, TrialEndReason.SandboxInstanceExited).ConfigureAwait(false);
                    return;
                }

                if (observedOwnedWindow && poll.OwnedWindowRunning == false)
                {
                    await CompleteExternalEndAsync(session, TrialEndReason.SandboxWindowClosed).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* Transient poll failure never falsely ends the active UI. */ }
    }

    private Task CompleteExternalEndAsync(SandboxStartResult session, TrialEndReason reason)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatch(() =>
        {
            _ = HandleExternalEndOnUiAsync(session, reason).ContinueWith(
                _ => tcs.TrySetResult(),
                TaskScheduler.Default);
        });
        return tcs.Task;
    }

    private async Task HandleExternalEndOnUiAsync(SandboxStartResult session, TrialEndReason reason)
    {
        if (disposed || activeSession is null || !string.Equals(activeSession.InstanceId, session.InstanceId, StringComparison.OrdinalIgnoreCase))
            return;
        if (IsStopping || phase == IsolatedTrialPhase.Ended)
            return;

        await EndSessionCoreAsync(session, reason).ConfigureAwait(true);
    }

    private Task<bool> EndSessionCoreAsync(SandboxStartResult session, TrialEndReason reason)
    {
        if (IsStopping || operationBusy) return Task.FromResult(false);

        operationBusy = true;
        StopMonitorOnly();
        phase = IsolatedTrialPhase.Stopping;
        cleanupStatusText = "";
        isCleanupWarning = false;
        sessionHeadline = reason switch
        {
            TrialEndReason.SandboxWindowClosed => "检测到沙箱窗口已关闭，正在结束并清理临时文件…",
            TrialEndReason.SandboxInstanceExited => "检测到沙箱已退出，正在清理临时文件…",
            _ => "正在结束本次试用并清理临时文件…"
        };
        sessionSummary = "仅针对本次试用实例执行停止与清理，不会影响宿主机上的其他沙箱。";
        statusMessage = sessionHeadline;
        NotifyAll();

        return Track(async () =>
        {
            try
            {
                var result = await service.StopAndCleanupAsync(session.InstanceId, session.StagingRootPath).ConfigureAwait(true);
                if (disposed) return result.IsFullyCleaned;

                instanceState = result.InstanceState;
                stagingState = result.StagingState;
                failureDiagnostic = result.Diagnostic;
                endReason = reason;

                // ONLY show "试用已结束" when SandboxInstanceState is authoritatively ConfirmedEnded (or NotCreated)!
                if (result.InstanceState is SandboxInstanceState.ConfirmedEnded or SandboxInstanceState.NotCreated)
                {
                    activeSession = null;
                    observedOwnedWindow = false;
                    phase = IsolatedTrialPhase.Ended;

                    if (result.StagingState is TrialStagingState.Cleaned or TrialStagingState.NotCreated)
                    {
                        service.ClearPendingSession(session.InstanceId, session.SessionId);
                        sessionHeadline = "试用已结束";
                        sessionSummary = reason switch
                        {
                            TrialEndReason.SandboxWindowClosed => "沙箱窗口已关闭，来宾内所有改动已丢弃，宿主机原文件未受影响。",
                            TrialEndReason.SandboxInstanceExited => "沙箱实例已关闭，来宾内所有改动已丢弃，宿主机原文件未受影响。",
                            _ => "来宾内所有改动已丢弃，宿主机原文件未受影响。"
                        };
                        cleanupStatusText = "本次临时文件已清理";
                        isCleanupWarning = false;
                        failureNextStep = "";
                        statusMessage = "试用已结束，本次临时文件已清理。";
                        AddEvidence("试用已结束", result.Summary);
                    }
                    else
                    {
                        sessionHeadline = "试用已结束，但临时文件仍有残留";
                        sessionSummary = "已确认本次沙箱实例结束，但宿主机暂存目录尚未完全删除；已保留恢复记录直至清理完成。";
                        cleanupStatusText = result.StagingState == TrialStagingState.Remaining
                            ? "本次临时文件仍有残留，请点击“重试清理”。"
                            : "本次临时文件清理状态尚未确认，请展开“技术详情”核对或点击“重试清理”。";
                        isCleanupWarning = true;
                        failureNextStep = "请确认没有程序占用临时文件后点击“重试清理”。";
                        statusMessage = "试用已结束，但临时文件尚未完全清理。";
                        AddEvidence("实例已结束，暂存待清理", result.Summary);
                    }
                    RebuildTechnicalDetails();
                    NotifyAll();
                    return result.IsFullyCleaned;
                }

                if (result.InstanceState == SandboxInstanceState.Running)
                {
                    // Confirmed still running! Keep activeSession & pending record so user can retry stopping it.
                    activeSession = session;
                    phase = IsolatedTrialPhase.RecoveredRunning;
                    sessionHeadline = isRecoveredContext ? "上次试用仍在运行" : "本次试用仍在运行（未能立即停止）";
                    sessionSummary = "查询确认该沙箱实例仍在运行，未清理暂存目录，也未移除恢复记录。请再次点击“结束这次试用”或直接关闭沙箱窗口。";
                    cleanupStatusText = $"沙箱实例：仍在运行；宿主暂存目录：{FormatStagingStateLabel(result.StagingState)}。";
                    isCleanupWarning = true;
                    failureNextStep = "请点击“结束这次试用”重试，或手动关闭对应的 Windows 沙箱窗口。";
                    statusMessage = sessionHeadline;
                    AddEvidence("停止未生效（仍在运行）", result.Summary);
                    RebuildTechnicalDetails();
                    NotifyAll();
                    StartSessionMonitor(session);
                    return false;
                }

                // SandboxInstanceState.Unknown -> NEVER display "试用已结束"! Retain record & offer Recheck/Retry.
                activeSession = null;
                observedOwnedWindow = false;
                ApplyUnconfirmedState(
                    isRecovery: isRecoveredContext,
                    instance: SandboxInstanceState.Unknown,
                    staging: result.StagingState,
                    diagnostic: result.Diagnostic,
                    evidenceStage: "结束状态尚未确认",
                    evidenceDetail: result.Summary);
                return false;
            }
            catch (Exception error)
            {
                if (disposed) return false;
                activeSession = null;
                ApplyUnconfirmedState(
                    isRecovery: isRecoveredContext,
                    instance: SandboxInstanceState.Unknown,
                    staging: TrialStagingState.Unconfirmed,
                    diagnostic: $"{error.GetType().Name}: {error.Message}",
                    evidenceStage: "结束过程异常",
                    evidenceDetail: $"结束试用时抛出异常：{error.Message}");
                return false;
            }
            finally
            {
                operationBusy = false;
                NotifyAll();
            }
        });
    }

    private void ApplyUnconfirmedState(
        bool isRecovery,
        SandboxInstanceState instance,
        TrialStagingState staging,
        string diagnostic,
        string evidenceStage,
        string evidenceDetail)
    {
        instanceState = instance;
        stagingState = staging;
        phase = IsolatedTrialPhase.Unconfirmed;
        sessionHeadline = isRecovery ? "暂时无法确认上次试用状态" : "暂时无法确认试用是否已结束";
        sessionSummary = "停止或查询未返回可解析的可靠结果，因此不会误报为“试用已结束”。已保留本次实例关联信息，可稍后点击“重新检查”。";
        cleanupStatusText = $"沙箱实例：{FormatInstanceStateLabel(instance)}；宿主暂存目录：{FormatStagingStateLabel(staging)}。";
        isCleanupWarning = true;
        failureNextStep = "待 Windows 沙盒命令行恢复可用后，点击“重新检查”或重试结束；处理完成前会保留恢复记录。";
        failureDiagnostic = diagnostic;
        statusMessage = sessionHeadline;
        AddEvidence(evidenceStage, evidenceDetail);
        RebuildTechnicalDetails();
        NotifyAll();
    }

    public async Task CloseAsync()
    {
        if (disposed) return;
        disposed = true;
        StopMonitorOnly();
        CancelActiveHash();
        try { startCts?.Cancel(); } catch { }

        var sessionToClean = activeSession;
        activeSession = null;

        Task[] snapshot;
        lock (opsGate) snapshot = [.. pendingOps];
        try { await Task.WhenAll(snapshot).ConfigureAwait(false); }
        catch { }

        if (sessionToClean is not null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var stopRes = await service.StopAndCleanupAsync(sessionToClean.InstanceId, sessionToClean.StagingRootPath, shutdownCts.Token).ConfigureAwait(false);
                if (stopRes.IsFullyCleaned)
                {
                    service.ClearPendingSession(sessionToClean.InstanceId, sessionToClean.SessionId);
                }
            }
            catch { /* Leave pending-session.json intact so next launch recovers it! */ }
        }
    }

    public void Dispose()
    {
        disposed = true;
        StopMonitorOnly();
        CancelActiveHash();
        try { startCts?.Cancel(); } catch { }
        startCts?.Dispose();
    }

    private static string FormatInstanceStateLabel(SandboxInstanceState state) => state switch
    {
        SandboxInstanceState.NotCreated => "未创建",
        SandboxInstanceState.Running => "仍在运行",
        SandboxInstanceState.ConfirmedEnded => "已确认结束",
        _ => "状态未知"
    };

    private static string FormatStagingStateLabel(TrialStagingState state) => state switch
    {
        TrialStagingState.NotCreated => "未创建",
        TrialStagingState.Active => "使用中",
        TrialStagingState.Cleaned => "已清理",
        TrialStagingState.Remaining => "仍有残留",
        _ => "尚未确认"
    };

    private void CancelActiveHash()
    {
        var cts = hashCts;
        hashCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private void ResetHashStateOnly()
    {
        hashState = TrialHashState.Idle;
        computedSha256 = "";
        hashErrorMessage = "";
    }

    private void StopMonitorOnly()
    {
        var cts = activeMonitorCts;
        activeMonitorCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private void ResetSessionDisplayForNewSelection()
    {
        isRecoveredContext = false;
        phase = IsolatedTrialPhase.Idle;
        endReason = TrialEndReason.None;
        instanceState = SandboxInstanceState.NotCreated;
        stagingState = TrialStagingState.NotCreated;
        lastLaunchResult = null;
        lastSessionId = "";
        lastInstanceId = "";
        lastStagingPath = "";
        lastSessionFileName = "";
        sessionHeadline = "";
        sessionSummary = "";
        cleanupStatusText = "";
        isCleanupWarning = false;
        failureNextStep = "";
        failureDiagnostic = "";
        EvidenceItems.Clear();
    }

    private void AddEvidence(string stage, string summary)
    {
        var ts = clock().ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        EvidenceItems.Add(new TrialEvidenceEntry(ts, stage, summary));
    }

    private void RebuildTechnicalDetails()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"沙盒能力状态：{capability.Status}（{capability.Headline}）");
        if (!string.IsNullOrWhiteSpace(capability.WindowsEditionSummary))
            sb.AppendLine($"系统版本证据：{capability.WindowsEditionSummary}");
        if (!string.IsNullOrWhiteSpace(capability.WsbVersion))
            sb.AppendLine($"wsb 版本：{capability.WsbVersion}");
        if (!string.IsNullOrWhiteSpace(capability.PermissionNote))
            sb.AppendLine($"权限说明：{capability.PermissionNote}");
        if (selectedExecutable is not null)
        {
            sb.AppendLine($"所选程序：{selectedExecutable.FullPath}");
            sb.AppendLine($"文件属性：{selectedExecutable.SizeBytes:N0} 字节 · {selectedExecutable.ArchitectureLabel} · 产品版本 {selectedExecutable.DisplayVersion}");
            if (HasComputedSha256)
                sb.AppendLine($"SHA-256：{computedSha256}");
        }
        sb.AppendLine("隔离策略：Networking=Disable · ClipboardRedirection=Disable · MappedFolder ReadOnly=true · 仅复制所选单个 EXE · 关闭后丢弃来宾改动");
        if (!string.IsNullOrEmpty(lastInstanceId))
            sb.AppendLine($"本次实例 ID：{lastInstanceId}");
        if (!string.IsNullOrEmpty(lastStagingPath))
            sb.AppendLine($"本次暂存目录：{lastStagingPath}");
        if (phase != IsolatedTrialPhase.Idle || instanceState != SandboxInstanceState.NotCreated || stagingState != TrialStagingState.NotCreated)
        {
            sb.AppendLine($"实例状态：{FormatInstanceStateLabel(instanceState)} | 宿主暂存目录：{FormatStagingStateLabel(stagingState)}");
        }

        if (EvidenceItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("完整时间线与可观察依据：");
            foreach (var item in EvidenceItems)
                sb.AppendLine($"[{item.Timestamp}] {item.Stage}：{item.Summary}");
        }
        if (!string.IsNullOrWhiteSpace(failureDiagnostic))
        {
            sb.AppendLine();
            sb.AppendLine("诊断详情：");
            sb.AppendLine(failureDiagnostic);
        }
        if (!string.IsNullOrWhiteSpace(capability.Diagnostic) && !capability.IsReady)
        {
            sb.AppendLine();
            sb.AppendLine("能力检测诊断：");
            sb.AppendLine(capability.Diagnostic);
        }
        technicalDetailsText = sb.ToString().TrimEnd();
    }

    private Task<T> Track<T>(Func<Task<T>> operation)
    {
        var task = operation();
        lock (opsGate)
        {
            if (!task.IsCompleted)
            {
                pendingOps.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (opsGate) pendingOps.Remove(completed);
                }, TaskScheduler.Default);
            }
        }
        return task;
    }

    private Task Track(Func<Task> operation)
    {
        var task = operation();
        lock (opsGate)
        {
            if (!task.IsCompleted)
            {
                pendingOps.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (opsGate) pendingOps.Remove(completed);
                }, TaskScheduler.Default);
            }
        }
        return task;
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Capability));
        OnPropertyChanged(nameof(IsCheckingCapability));
        OnPropertyChanged(nameof(IsCapabilityReady));
        OnPropertyChanged(nameof(IsCapabilityBlocked));
        OnPropertyChanged(nameof(CapabilityBadgeText));
        OnPropertyChanged(nameof(CapabilityPermissionNote));
        OnPropertyChanged(nameof(HasCapabilityPermissionNote));
        OnPropertyChanged(nameof(ShowOpenWindowsFeaturesButton));
        OnPropertyChanged(nameof(ShowGoToPackagingButton));
        OnPropertyChanged(nameof(ShowPrimaryRecheckButton));
        OnPropertyChanged(nameof(ExecutablePathInput));
        OnPropertyChanged(nameof(HasExecutable));
        OnPropertyChanged(nameof(SelectedExecutable));
        OnPropertyChanged(nameof(ExecutableFileName));
        OnPropertyChanged(nameof(ExecutableSizeText));
        OnPropertyChanged(nameof(ExecutableArchitectureText));
        OnPropertyChanged(nameof(ExecutableProductVersionText));
        OnPropertyChanged(nameof(SelectionError));
        OnPropertyChanged(nameof(HasSiblingFilesNotice));
        OnPropertyChanged(nameof(SiblingFilesNoticeText));
        OnPropertyChanged(nameof(HasRunCompatibilityWarning));
        OnPropertyChanged(nameof(RunCompatibilityWarningText));
        OnPropertyChanged(nameof(HashState));
        OnPropertyChanged(nameof(ComputedSha256));
        OnPropertyChanged(nameof(HashErrorMessage));
        OnPropertyChanged(nameof(IsComputingSha256));
        OnPropertyChanged(nameof(HasComputedSha256));
        OnPropertyChanged(nameof(CanComputeSha256));
        OnPropertyChanged(nameof(CanCopySha256));
        OnPropertyChanged(nameof(ComputeSha256ButtonText));
        OnPropertyChanged(nameof(InstanceState));
        OnPropertyChanged(nameof(StagingState));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(EndReason));
        OnPropertyChanged(nameof(IsRecovering));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(IsStopping));
        OnPropertyChanged(nameof(IsUnconfirmed));
        OnPropertyChanged(nameof(IsRecoveredRunning));
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(IsSessionEnded));
        OnPropertyChanged(nameof(IsSessionFailed));
        OnPropertyChanged(nameof(HasSessionCard));
        OnPropertyChanged(nameof(HasEvidenceItems));
        OnPropertyChanged(nameof(HasPendingRecoveryBanner));
        OnPropertyChanged(nameof(PendingRecoveryBannerText));
        OnPropertyChanged(nameof(CanRecheckCapability));
        OnPropertyChanged(nameof(CanSelectExecutable));
        OnPropertyChanged(nameof(CanStartTrial));
        OnPropertyChanged(nameof(CanEndTrial));
        OnPropertyChanged(nameof(EndTrialButtonText));
        OnPropertyChanged(nameof(CanRecheckPendingSession));
        OnPropertyChanged(nameof(CanRetryEndPendingSession));
        OnPropertyChanged(nameof(RetryEndPendingButtonText));
        OnPropertyChanged(nameof(CanRetryStagingCleanup));
        OnPropertyChanged(nameof(CanRetryTrial));
        OnPropertyChanged(nameof(SandboxRunAvailabilityHint));
        OnPropertyChanged(nameof(SandboxStageBadge));
        OnPropertyChanged(nameof(StagingStageBadge));
        OnPropertyChanged(nameof(ShowProgramStageBadge));
        OnPropertyChanged(nameof(ShowStagingStageBadge));
        OnPropertyChanged(nameof(ProgramStageBadge));
        OnPropertyChanged(nameof(ActiveInstanceIdText));
        OnPropertyChanged(nameof(SessionHeadline));
        OnPropertyChanged(nameof(SessionSummary));
        OnPropertyChanged(nameof(CleanupStatusText));
        OnPropertyChanged(nameof(HasCleanupStatus));
        OnPropertyChanged(nameof(IsCleanupWarning));
        OnPropertyChanged(nameof(FailureNextStep));
        OnPropertyChanged(nameof(FailureDiagnostic));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(LastStagingCleaned));
        OnPropertyChanged(nameof(CanShowTechnicalDetails));
        OnPropertyChanged(nameof(TechnicalDetailsText));
    }
}
