using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

/// <summary>
/// State machine for Contain's "隔离试用" (Isolated Trial) page.
/// Enforces single-session concurrency, distinguishes "沙箱已打开" from "程序已启动"
/// using real per-instance observable evidence, monitors external sandbox window close,
/// and scopes stop/cleanup strictly to the active session's instance ID and staging folder.
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
        "点击“开始隔离试用”或进入页面时将自动检测本机是否支持按实例创建、连接与结束 Windows 沙盒。",
        "");

    private string executablePathInput = "";
    private TrialExecutableInfo? selectedExecutable;
    private string selectionError = "";

    private IsolatedTrialPhase phase = IsolatedTrialPhase.Idle;
    private TrialEndReason endReason = TrialEndReason.None;
    private SandboxStartResult? activeSession;
    private GuestProgramLaunchResult? lastLaunchResult;
    private bool observedOwnedWindow;
    private CancellationTokenSource? activeMonitorCts;
    private CancellationTokenSource? startCts;

    private string lastInstanceId = "";
    private string lastStagingPath = "";
    private bool lastStagingCleaned;
    private string sessionHeadline = "";
    private string sessionSummary = "";
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

    public SandboxCapabilityReport Capability => capability;
    public bool IsCheckingCapability => checkingCapability;
    public bool IsCapabilityReady => capability.IsReady;
    public bool IsCapabilityBlocked =>
        !checkingCapability && capability.Status is not (SandboxCapabilityStatus.Unknown or SandboxCapabilityStatus.Checking or SandboxCapabilityStatus.Ready);

    public string CapabilityBadgeText => checkingCapability
        ? "正在检测…"
        : capability.IsReady
            ? (string.IsNullOrWhiteSpace(capability.WsbVersion) ? "Windows 沙盒可用" : $"Windows 沙盒可用 · wsb {capability.WsbVersion}")
            : "Windows 沙盒不可用";

    public string ExecutablePathInput
    {
        get => executablePathInput;
        set => ApplyExecutablePathInput(value ?? "");
    }

    public bool HasExecutable => selectedExecutable is not null;
    public TrialExecutableInfo? SelectedExecutable => selectedExecutable;
    public string ExecutableFileName => selectedExecutable?.FileName ?? "尚未选择本地 .exe 程序";
    public string ExecutableSizeText => selectedExecutable is null ? "" : $"文件大小：{selectedExecutable.FormattedSize}（{selectedExecutable.SizeBytes:N0} 字节）";
    public string ExecutableArchitectureText => selectedExecutable is null ? "" : $"检测架构：{selectedExecutable.ArchitectureLabel}";
    public string SelectionError => selectionError;
    public bool HasSiblingFilesNotice => selectedExecutable?.HasSiblingFiles == true;
    public string SiblingFilesNoticeText => selectedExecutable?.HasSiblingFiles == true
        ? $"提示：原目录中另有其他文件，但本次只会复制所选的单个“{selectedExecutable.FileName}”并只读提供给沙箱，不会带入同目录其他文件。"
        : "";

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

    public bool CanRecheckCapability => !disposed && !checkingCapability && !IsStarting && !IsStopping && !IsSessionActive;
    public bool CanSelectExecutable => !disposed && !IsStarting && !IsStopping && !IsSessionActive;
    public bool CanStartTrial =>
        !disposed && !checkingCapability && capability.IsReady && selectedExecutable is not null && !IsStarting && !IsStopping && !IsSessionActive;
    public bool CanEndTrial => !disposed && IsSessionActive && !IsStopping;

    public string SandboxStageBadge => phase switch
    {
        IsolatedTrialPhase.Idle => "未启动",
        IsolatedTrialPhase.StartingSandbox => "正在创建并连接沙箱…",
        IsolatedTrialPhase.SandboxOpened or IsolatedTrialPhase.ProgramStarted or IsolatedTrialPhase.ProgramExitedAfterStart => "沙箱已打开",
        IsolatedTrialPhase.Stopping => "正在结束并清理…",
        IsolatedTrialPhase.Ended => "会话已结束",
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
        IsolatedTrialPhase.Ended => "已结束（来宾改动已丢弃）",
        IsolatedTrialPhase.Failed => "未启动",
        _ => "未知"
    };

    public string ActiveInstanceIdText => string.IsNullOrEmpty(lastInstanceId) ? "" : $"实例 ID：{lastInstanceId}";
    public string SessionHeadline => sessionHeadline;
    public string SessionSummary => sessionSummary;
    public string FailureNextStep => failureNextStep;
    public string FailureDiagnostic => failureDiagnostic;
    public string StatusMessage => statusMessage;
    public bool LastStagingCleaned => lastStagingCleaned;
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
        statusMessage = "正在检测本机 Windows 沙盒能力（创建、连接、来宾执行与按实例结束）…";
        NotifyAll();

        try
        {
            var report = await service.ProbeCapabilityAsync().ConfigureAwait(true);
            if (disposed) return report;
            capability = report;
            checkingCapability = false;
            statusMessage = report.IsReady
                ? $"Windows 沙盒已就绪（wsb {report.WsbVersion}）。"
                : report.Headline;
            RebuildTechnicalDetails();
            NotifyAll();
            return report;
        }
        catch (Exception error)
        {
            if (disposed) return capability;
            capability = new SandboxCapabilityReport(
                SandboxCapabilityStatus.PolicyOrRuntimeBlocked,
                "检测 Windows 沙盒能力时出错",
                error.Message,
                "请确认本机 Windows 沙盒组件完整且未被策略阻止后点击“重新检测”。",
                Diagnostic: $"{error.GetType().Name}: {error.Message}");
            checkingCapability = false;
            statusMessage = capability.Headline;
            RebuildTechnicalDetails();
            NotifyAll();
            return capability;
        }
    }

    public bool SelectExecutable(string path)
    {
        if (!CanSelectExecutable) return false;

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
            statusMessage = $"已选择“{info.FileName}”（{info.FormattedSize}）；点击“开始隔离试用”将在断网、禁剪贴板的临时沙箱中运行。";
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
            NotifyAll();
            return false;
        }
    }

    private void ApplyExecutablePathInput(string rawValue)
    {
        if (disposed || !CanSelectExecutable || string.Equals(executablePathInput, rawValue, StringComparison.Ordinal))
            return;

        executablePathInput = rawValue;
        OnPropertyChanged(nameof(ExecutablePathInput));

        var trimmed = rawValue.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            selectedExecutable = null;
            selectionError = "";
            statusMessage = "";
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
        NotifyAll();
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
        lastStagingCleaned = false;
        failureNextStep = "";
        failureDiagnostic = "";
        EvidenceItems.Clear();

        sessionHeadline = $"正在为“{target.FileName}”启动隔离沙箱…";
        sessionSummary = "正在准备独立只读暂存目录、创建断网且禁用剪贴板共享的 Windows 沙箱并等待交互桌面就绪（最多 60 秒）。";
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
            // Step 1: Create sandbox, launch interactive window, wait for ExistingLogin readiness
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
            sessionSummary = $"实例 {session.InstanceId} 的交互窗口已连接且只读输入目录已挂载，正在把“{target.FileName}”复制到来宾本地目录并启动。";
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
                    sessionSummary = "请切换到已打开的 Windows 沙箱窗口中操作该程序。试用结束后可直接关闭沙箱窗口，或回到这里点击“结束试用”，所有来宾改动都会被丢弃。";
                    AddEvidence("程序已启动", launch.Summary);
                    break;

                case GuestProgramLaunchOutcome.StartedAndExitedZero:
                    phase = IsolatedTrialPhase.ProgramExitedAfterStart;
                    sessionHeadline = $"程序已在沙箱中运行并退出：“{target.FileName}”";
                    sessionSummary = "沙箱窗口仍然保持打开，你可以在沙箱内查看结果；完成后可直接关闭沙箱窗口或点击“结束试用”。";
                    AddEvidence("程序已启动并退出", launch.Summary);
                    break;

                case GuestProgramLaunchOutcome.StartedAndExitedNonZero:
                    phase = IsolatedTrialPhase.ProgramExitedAfterStart;
                    sessionHeadline = $"沙箱已打开，但“{target.FileName}”启动后异常退出";
                    sessionSummary = launch.Summary + " 沙箱仍处于打开状态，你可以点击“结束试用”或直接关闭沙箱窗口。";
                    AddEvidence("程序启动后异常退出", launch.Summary);
                    break;

                default:
                    phase = IsolatedTrialPhase.SandboxOpened;
                    sessionHeadline = $"沙箱已打开，但未能启动“{target.FileName}”";
                    sessionSummary = launch.Summary;
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
            lastStagingCleaned = true;
            sessionHeadline = $"无法开始隔离试用：{error.Message}";
            sessionSummary = "本次启动未建立有效试用会话；若已创建临时实例或暂存目录，已自动回滚清理，宿主机原始文件未受影响。";
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
            lastStagingCleaned = true;
            sessionHeadline = "启动隔离试用时发生未预期错误";
            sessionSummary = error.Message;
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
        sessionHeadline = reason switch
        {
            TrialEndReason.SandboxWindowClosed => "检测到沙箱窗口已关闭，正在结束实例并清理暂存目录…",
            TrialEndReason.SandboxInstanceExited => "检测到沙箱实例已退出，正在清理本次暂存目录…",
            _ => "正在按实例 ID 结束本次沙箱并清理暂存目录…"
        };
        sessionSummary = $"仅针对实例 {session.InstanceId} 执行停止与清理，不会影响宿主机上的其他沙箱实例。";
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
                lastStagingCleaned = result.StagingCleaned;
                phase = IsolatedTrialPhase.Ended;

                sessionHeadline = reason switch
                {
                    TrialEndReason.SandboxWindowClosed => "会话已结束（沙箱窗口已关闭）",
                    TrialEndReason.SandboxInstanceExited => "会话已结束（沙箱实例已关闭）",
                    _ => "会话已结束"
                };
                sessionSummary = result.Summary + " 可再次点击“开始隔离试用”开启一次全新的干净沙箱。";
                failureDiagnostic = result.Diagnostic;
                statusMessage = sessionHeadline + "，来宾改动已丢弃。";
                AddEvidence("会话已结束", result.Summary);
                RebuildTechnicalDetails();
                NotifyAll();
                return result.InstanceStopped && result.StagingCleaned;
            }
            catch (Exception error)
            {
                if (disposed) return false;
                activeSession = null;
                phase = IsolatedTrialPhase.Failed;
                sessionHeadline = "结束隔离试用时发生异常";
                sessionSummary = error.Message;
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
        try { startCts?.Cancel(); } catch { }
        startCts?.Dispose();
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
        lastStagingCleaned = false;
        sessionHeadline = "";
        sessionSummary = "";
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
        if (!string.IsNullOrWhiteSpace(capability.WsbVersion))
            sb.AppendLine($"wsb 版本：{capability.WsbVersion}");
        if (selectedExecutable is not null)
        {
            sb.AppendLine($"所选程序：{selectedExecutable.FullPath}");
            sb.AppendLine($"文件大小：{selectedExecutable.SizeBytes:N0} 字节 · {selectedExecutable.ArchitectureLabel}");
        }
        sb.AppendLine("隔离策略：Networking=Disable · ClipboardRedirection=Disable · MappedFolder ReadOnly=true · 仅复制所选单个 EXE · 关闭后丢弃来宾改动");
        if (!string.IsNullOrEmpty(lastInstanceId))
            sb.AppendLine($"本次实例 ID：{lastInstanceId}");
        if (!string.IsNullOrEmpty(lastStagingPath))
            sb.AppendLine($"本次暂存目录：{lastStagingPath}（已清理：{(lastStagingCleaned ? "是" : "否/进行中")}）");
        sb.AppendLine($"沙箱状态：{SandboxStageBadge} | 程序状态：{ProgramStageBadge}");

        if (EvidenceItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("可观察状态依据：");
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
        OnPropertyChanged(nameof(ExecutablePathInput));
        OnPropertyChanged(nameof(HasExecutable));
        OnPropertyChanged(nameof(SelectedExecutable));
        OnPropertyChanged(nameof(ExecutableFileName));
        OnPropertyChanged(nameof(ExecutableSizeText));
        OnPropertyChanged(nameof(ExecutableArchitectureText));
        OnPropertyChanged(nameof(SelectionError));
        OnPropertyChanged(nameof(HasSiblingFilesNotice));
        OnPropertyChanged(nameof(SiblingFilesNoticeText));
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
        OnPropertyChanged(nameof(SandboxStageBadge));
        OnPropertyChanged(nameof(ProgramStageBadge));
        OnPropertyChanged(nameof(ActiveInstanceIdText));
        OnPropertyChanged(nameof(SessionHeadline));
        OnPropertyChanged(nameof(SessionSummary));
        OnPropertyChanged(nameof(FailureNextStep));
        OnPropertyChanged(nameof(FailureDiagnostic));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(LastStagingCleaned));
        OnPropertyChanged(nameof(CanShowTechnicalDetails));
        OnPropertyChanged(nameof(TechnicalDetailsText));
    }
}
