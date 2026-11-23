using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

/// <summary>
/// State machine for Contain's "隔离试用" (Isolated Trial) page.
/// Separates read-only local EXE inspection (always available even without Windows Sandbox)
/// from isolated sandbox execution, enforces single-session concurrency, distinguishes
/// "沙箱已打开" from "程序已启动", surfaces concise host results, and tucks technical
/// UUID/PID/timeline metadata into the collapsible details section.
/// </summary>
public sealed class IsolatedTrialViewModel : ObservableObject, IDisposable
{
    private readonly IIsolatedTrialService service;
    private readonly Action<Action> dispatch;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan pollInterval;
    private readonly object opsGate = new();
    private readonly HashSet<Task> pendingOps = [];

    private bool disposed;
    private bool capabilityCheckedOnce;
    private bool checkingCapability;
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
    private SandboxStartResult? activeSession;
    private GuestProgramLaunchResult? lastLaunchResult;
    private bool observedOwnedWindow;
    private CancellationTokenSource? activeMonitorCts;
    private CancellationTokenSource? startCts;

    private string lastInstanceId = "";
    private string lastStagingPath = "";
    private bool? lastInstanceStopped;
    private bool? lastStagingCleaned;
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
        TimeSpan? pollInterval = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.dispatch = dispatch ?? (action => action());
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1500);
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

    // Sandbox session state
    public IsolatedTrialPhase Phase => phase;
    public TrialEndReason EndReason => endReason;
    public bool IsStarting => phase == IsolatedTrialPhase.StartingSandbox;
    public bool IsStopping => phase == IsolatedTrialPhase.Stopping;
    public bool IsSessionActive =>
        activeSession is not null && phase is IsolatedTrialPhase.SandboxOpened or IsolatedTrialPhase.ProgramStarted or IsolatedTrialPhase.ProgramExitedAfterStart;
    public bool IsSessionEnded => phase == IsolatedTrialPhase.Ended;
    public bool IsSessionFailed => phase == IsolatedTrialPhase.Failed;
    public bool HasSessionCard => phase != IsolatedTrialPhase.Idle;
    public bool HasEvidenceItems => EvidenceItems.Count > 0;

    // Decoupled capabilities: viewing files is allowed whenever no sandbox session is actively running
    public bool CanRecheckCapability => !disposed && !checkingCapability && !IsStarting && !IsStopping && !IsSessionActive;
    public bool CanSelectExecutable => !disposed && !IsStarting && !IsStopping && !IsSessionActive;
    public bool CanStartTrial =>
        !disposed
        && !checkingCapability
        && capability.IsReady
        && selectedExecutable is not null
        && selectedExecutable.IsRunnableInX64Sandbox
        && !IsStarting
        && !IsStopping
        && !IsSessionActive;
    public bool CanEndTrial => !disposed && IsSessionActive && !IsStopping;
    public bool CanRetryTrial => CanStartTrial && (IsSessionEnded || IsSessionFailed);

    public string SandboxRunAvailabilityHint =>
        capability.IsReady
            ? ""
            : checkingCapability
                ? "正在检测本机 Windows 沙盒能力；期间已可查看文件信息与计算 SHA-256。"
                : "本机当前无法启动 Windows 沙盒，仅禁用“开始隔离试用”；上方文件信息查看与 SHA-256 计算均可正常使用。";

    public string SandboxStageBadge => phase switch
    {
        IsolatedTrialPhase.Idle => "未启动",
        IsolatedTrialPhase.StartingSandbox => "正在打开沙箱…",
        IsolatedTrialPhase.SandboxOpened or IsolatedTrialPhase.ProgramStarted or IsolatedTrialPhase.ProgramExitedAfterStart => "沙箱已打开",
        IsolatedTrialPhase.Stopping => "正在结束并清理…",
        IsolatedTrialPhase.Ended => "试用已结束",
        IsolatedTrialPhase.Failed => "启动失败",
        _ => "未知"
    };

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
        IsolatedTrialPhase.Stopping => "正在结束会话",
        IsolatedTrialPhase.Ended => lastStagingCleaned == true ? "已结束（改动已丢弃）" : "已结束",
        IsolatedTrialPhase.Failed => "未启动",
        _ => "未知"
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
    public bool LastStagingCleaned => lastStagingCleaned == true;
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

    public Task EnsureCapabilityCheckedAsync()
    {
        if (capabilityCheckedOnce || checkingCapability || disposed)
            return Task.CompletedTask;
        return RefreshCapabilityAsync();
    }

    public Task<SandboxCapabilityReport> RefreshCapabilityAsync() => Track(RefreshCapabilityCoreAsync);

    private async Task<SandboxCapabilityReport> RefreshCapabilityCoreAsync()
    {
        if (disposed || checkingCapability || IsStarting || IsStopping || IsSessionActive)
            return capability;

        checkingCapability = true;
        capabilityCheckedOnce = true;
        statusMessage = "正在以普通用户权限检测本机 Windows 沙盒能力…";
        NotifyAll();

        try
        {
            var report = await service.ProbeCapabilityAsync().ConfigureAwait(true);
            if (disposed) return report;
            capability = report;
            checkingCapability = false;
            statusMessage = report.IsReady
                ? $"Windows 沙盒已就绪（wsb {report.WsbVersion}）。"
                : $"{report.Headline}（仍可查看本地 .exe 信息与计算 SHA-256）。";
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
            if (phase is IsolatedTrialPhase.Ended or IsolatedTrialPhase.Failed)
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
            // Discard stale result if user switched or cleared the file while hashing
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
        phase = IsolatedTrialPhase.StartingSandbox;
        endReason = TrialEndReason.None;
        activeSession = null;
        lastLaunchResult = null;
        observedOwnedWindow = false;
        lastInstanceId = "";
        lastStagingPath = "";
        lastInstanceStopped = null;
        lastStagingCleaned = null;
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
            // Step 1: Create sandbox, launch interactive window detached, wait for ExistingLogin readiness
            var session = await service.StartSandboxAsync(target, token).ConfigureAwait(true);
            if (disposed)
            {
                await service.StopAndCleanupAsync(session.InstanceId, session.StagingRootPath).ConfigureAwait(false);
                return false;
            }

            activeSession = session;
            lastInstanceId = session.InstanceId;
            lastStagingPath = session.StagingRootPath;
            phase = IsolatedTrialPhase.SandboxOpened;
            sessionHeadline = "沙箱已打开，正在沙箱内启动程序…";
            sessionSummary = $"正在将“{target.FileName}”复制到沙箱本地并启动，请稍候。";
            statusMessage = sessionHeadline;
            AddEvidence("沙箱已打开", $"实例 {session.InstanceId} 已创建并响应 ExistingLogin 就绪检测（只读目录 C:\\ContainTrialInput 已挂载，连接进程 PID {(session.ConnectProcessId?.ToString(CultureInfo.InvariantCulture) ?? "已启动")}）。");
            RebuildTechnicalDetails();
            NotifyAll();

            // Step 2: Launch selected EXE inside guest local C:\ContainTrialRun (data-only JSON handoff)
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

            // Step 3: Start monitoring ONLY this exact instance ID & its owned remote-session window
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
            phase = IsolatedTrialPhase.Failed;
            activeSession = null;
            lastInstanceStopped = true;
            lastStagingCleaned = true;
            cleanupStatusText = "已回滚清理本次临时暂存文件，宿主机原始文件未受影响。";
            isCleanupWarning = false;
            sessionHeadline = $"无法开始隔离试用：{error.Message}";
            sessionSummary = "本次未能建立隔离试用会话，所选程序未在宿主机上运行。";
            failureNextStep = string.IsNullOrWhiteSpace(error.NextStep)
                ? "请排查上述原因后点击“重新检测”或再次点击“开始隔离试用”。"
                : error.NextStep;
            failureDiagnostic = error.Diagnostic;
            statusMessage = sessionHeadline;
            AddEvidence("启动失败", $"{error.Message}（错误码：{error.Code}）");
            RebuildTechnicalDetails();
            NotifyAll();
            return false;
        }
        catch (Exception error)
        {
            if (disposed) return false;
            phase = IsolatedTrialPhase.Failed;
            activeSession = null;
            lastInstanceStopped = null;
            lastStagingCleaned = null;
            cleanupStatusText = "清理状态尚未确认，请展开“技术详情”核对。";
            isCleanupWarning = true;
            sessionHeadline = "启动隔离试用时发生未预期错误";
            sessionSummary = $"{error.Message}（所选程序未在宿主机上运行。）";
            failureNextStep = "请点击“重新检测”确认本机 Windows 沙盒状态，再重新尝试启动。";
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

        return EndSessionCoreAsync(activeSession, TrialEndReason.EndedByContain);
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

                if (!poll.InstancePresentInList)
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
        catch { /* Transient poll failure does not tear down the active UI. */ }
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
        if (IsStopping) return Task.FromResult(false);

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
                if (disposed) return result.InstanceStopped;

                activeSession = null;
                observedOwnedWindow = false;
                endReason = reason;
                lastInstanceStopped = result.InstanceStopped;
                lastStagingCleaned = result.StagingCleaned;
                phase = IsolatedTrialPhase.Ended;

                sessionHeadline = "试用已结束";
                sessionSummary = reason switch
                {
                    TrialEndReason.SandboxWindowClosed => "沙箱窗口已关闭，来宾内所有改动已丢弃，宿主机原文件未受影响。",
                    TrialEndReason.SandboxInstanceExited => "沙箱实例已关闭，来宾内所有改动已丢弃，宿主机原文件未受影响。",
                    _ => "来宾内所有改动已丢弃，宿主机原文件未受影响。"
                };

                (cleanupStatusText, isCleanupWarning) = (result.InstanceStopped, result.StagingCleaned) switch
                {
                    (true, true) => ("本次临时文件已清理", false),
                    (true, false) => ("沙箱已结束，但本次临时文件尚未完全清理（请展开“技术详情”查看路径）", true),
                    (false, true) => ("本次临时文件已清理，但沙箱实例停止状态尚未确认（请展开“技术详情”查看）", true),
                    _ => ("沙箱停止与临时文件清理均未完全确认，请展开“技术详情”查看诊断信息", true)
                };

                failureDiagnostic = result.Diagnostic;
                statusMessage = result.InstanceStopped && result.StagingCleaned
                    ? "试用已结束，本次临时文件已清理。"
                    : $"试用已结束：{cleanupStatusText}。";
                AddEvidence("试用已结束", result.Summary);
                RebuildTechnicalDetails();
                NotifyAll();
                return result.InstanceStopped && result.StagingCleaned;
            }
            catch (Exception error)
            {
                if (disposed) return false;
                activeSession = null;
                lastInstanceStopped = null;
                lastStagingCleaned = null;
                phase = IsolatedTrialPhase.Failed;
                sessionHeadline = "结束试用时发生异常";
                sessionSummary = error.Message;
                cleanupStatusText = "临时文件与沙箱清理状态尚未确认，请展开“技术详情”核对。";
                isCleanupWarning = true;
                failureNextStep = "请核对本机沙箱状态后重试。";
                failureDiagnostic = $"{error.GetType().Name}: {error.Message}";
                statusMessage = sessionHeadline;
                AddEvidence("结束异常", error.Message);
                RebuildTechnicalDetails();
                NotifyAll();
                return false;
            }
        });
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
                await service.StopAndCleanupAsync(sessionToClean.InstanceId, sessionToClean.StagingRootPath, shutdownCts.Token).ConfigureAwait(false);
            }
            catch { }
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
        phase = IsolatedTrialPhase.Idle;
        endReason = TrialEndReason.None;
        lastLaunchResult = null;
        lastInstanceId = "";
        lastStagingPath = "";
        lastInstanceStopped = null;
        lastStagingCleaned = null;
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
        {
            var cleanedText = lastStagingCleaned switch
            {
                true => "是",
                false => "否（仍有残留）",
                null => "进行中/未确认"
            };
            sb.AppendLine($"本次暂存目录：{lastStagingPath}（已清理：{cleanedText}）");
        }
        if (phase != IsolatedTrialPhase.Idle)
            sb.AppendLine($"状态摘要：沙箱={SandboxStageBadge} | 程序={ProgramStageBadge}");

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
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(EndReason));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(IsStopping));
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(IsSessionEnded));
        OnPropertyChanged(nameof(IsSessionFailed));
        OnPropertyChanged(nameof(HasSessionCard));
        OnPropertyChanged(nameof(HasEvidenceItems));
        OnPropertyChanged(nameof(CanRecheckCapability));
        OnPropertyChanged(nameof(CanSelectExecutable));
        OnPropertyChanged(nameof(CanStartTrial));
        OnPropertyChanged(nameof(CanEndTrial));
        OnPropertyChanged(nameof(CanRetryTrial));
        OnPropertyChanged(nameof(SandboxRunAvailabilityHint));
        OnPropertyChanged(nameof(SandboxStageBadge));
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
