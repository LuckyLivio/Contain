using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Contain.Viewer;

namespace Contain.Desktop.Model;

/// <summary>The existing process bridge remains the only installation writer.</summary>
public interface IInstallService
{
    string PersonalDatabase { get; }
    bool CollectorStarted { get; }
    bool DatabaseExists(string database);
    Task<InstallRunResult> RunAsync(InstallRequest request, Action<InstallStatus> progress);
    Task<JsonElement> CheckEnvironmentAsync(string database);
}

public sealed class InstallService(InstallClient client, PersonalRecords records) : IInstallService
{
    public string PersonalDatabase => records.DatabasePath;
    public bool CollectorStarted => client.CollectorStarted;
    public bool DatabaseExists(string database) => File.Exists(database);
    public Task<InstallRunResult> RunAsync(InstallRequest request, Action<InstallStatus> progress) =>
        client.RunAsync(request, progress, records);
    public Task<JsonElement> CheckEnvironmentAsync(string database) => client.CheckEnvironmentAsync(database);
}

/// <summary>
/// UI-neutral task state. Configuration and recording are separate: selecting a
/// path cannot launch a process, and no cancellation API kills an installer.
/// </summary>
public sealed class InstallViewModel : ObservableObject
{
    private readonly IInstallService service;
    private readonly Action<Action> dispatch;
    private readonly Stopwatch taskClock = new();
    private readonly object progressGate = new();
    private (InstallStatus Status, long ReceivedAt)? pendingProgress;
    private InstallProgressClock progressClock = new();
    private bool dispatchScheduled;
    private int generation;
    private int busyGate;
    private bool active;
    private bool checking;
    private bool consumed;
    private bool nameEdited;
    private long receivedAt;
    private string localFailureDetails = "";
    private InstallRequest? runningRequest;

    private string installerPath = "";
    private string name = "";
    private bool followInstaller = true;
    private string watchRoot = "";
    private bool customDatabase;
    private string databasePath;
    private string argumentsText = "";
    private string registryKey = "";
    private bool snapshotOnly;
    private ulong settleMilliseconds = 1500;
    private ulong maxDrainMilliseconds = 10000;
    private string statusTitle = "准备记录一次安装";
    private string statusMessage = "选择 EXE，然后点击开始记录。选择文件本身不会启动安装器。";
    private string error = "";
    private string elapsed = "00:00:00";
    private string measurements = "尚无实际进展测量。";
    private string environmentDetails = "";

    public InstallViewModel(IInstallService service, Action<Action>? dispatch = null,
        Func<string, string, Task>? openSession = null)
    {
        this.service = service;
        databasePath = service.PersonalDatabase;
        var context = SynchronizationContext.Current;
        this.dispatch = dispatch ?? (context is null ? action => action() : action => context.Post(_ => action(), null));
        OpenSessionRequested = openSession;
    }

    public Func<string, string, Task>? OpenSessionRequested { get; set; }
    public string InstallerPath => installerPath;
    public string InstallerFileName => string.IsNullOrEmpty(installerPath) ? "选择要运行的安装器" : Path.GetFileName(installerPath);
    public string Name { get => name; set { if (Configure(ref name, value)) nameEdited = true; } }
    public bool FollowInstaller { get => followInstaller; set => Configure(ref followInstaller, value); }
    public string WatchRoot { get => watchRoot; set => Configure(ref watchRoot, value); }
    public bool CustomDatabase { get => customDatabase; set => Configure(ref customDatabase, value); }
    public string DatabasePath { get => databasePath; set => Configure(ref databasePath, value); }
    public string ArgumentsText { get => argumentsText; set => Configure(ref argumentsText, value); }
    public string RegistryKey { get => registryKey; set => Configure(ref registryKey, value); }
    public bool SnapshotOnly { get => snapshotOnly; set => Configure(ref snapshotOnly, value); }
    public ulong SettleMilliseconds { get => settleMilliseconds; set => Configure(ref settleMilliseconds, value); }
    public ulong MaxDrainMilliseconds { get => maxDrainMilliseconds; set => Configure(ref maxDrainMilliseconds, value); }

    public bool Busy => Volatile.Read(ref busyGate) != 0;
    public bool IsActive => active;
    public bool IsChecking => checking;
    public bool ConfigurationEnabled => !Busy && !consumed;
    public bool CanStart => ConfigurationEnabled && !string.IsNullOrWhiteSpace(InstallerPath)
        && (FollowInstaller || !string.IsNullOrWhiteSpace(WatchRoot));
    public bool CanCheckEnvironment => ConfigurationEnabled;
    public bool CanOpenSession => !Busy && LastStatus is { } value && service.DatabaseExists(value.Database);
    public bool HasTask => active || checking || consumed || LastStatus is not null || HasError || environmentDetails.Length != 0;
    public bool HasError => Error.Length != 0;
    public string StatusTitle { get => statusTitle; private set => SetProperty(ref statusTitle, value); }
    public string StatusMessage { get => statusMessage; private set => SetProperty(ref statusMessage, value); }
    public string Error { get => error; private set { if (SetProperty(ref error, value)) OnPropertyChanged(nameof(HasError)); } }
    public string Elapsed { get => elapsed; private set => SetProperty(ref elapsed, value); }
    public string Measurements { get => measurements; private set => SetProperty(ref measurements, value); }
    public InstallStatus? LastStatus { get; private set; }
    public InstallRunResult? Result { get; private set; }
    public string? SessionId => LastStatus?.SessionId;
    public string Step => StepLabel(LastStatus?.Step);
    public string InstallerOutcome => LastStatus is not { } value ? "尚未确认启动"
        : !value.InstallerStarted ? "尚未确认启动"
        : !value.InstallerExited ? "已启动，尚未确认退出"
        : value.InstallerExitCode is null ? "已退出，未提供退出码"
        : value.InstallerExitCode == 0 ? "退出码 0 · 最终安装状态未验证"
        : $"退出码 {value.InstallerExitCode} · 非零，含义由安装器定义";
    public string SavedState => Result?.Succeeded == true ? "记录已保存，可以查看文件操作"
        : LastStatus?.DataSaved == true ? "已有证据保存，尚未确认完整结束" : "保存尚未确认";
    public string Coverage => LastStatus?.QualityLevel switch
    {
        "Complete" => "Complete · 仍限于配置范围和来源",
        "Degraded" => "Degraded · 覆盖受限",
        "Incomplete" => "Incomplete · 记录证据不完整",
        null => "覆盖质量尚未确认",
        var value => value
    };
    public string ConfigurationSummary
    {
        get
        {
            var options = new List<string> { FollowInstaller ? "跟随安装器及已验证直接子进程" : "指定文件夹观察",
                CustomDatabase ? "自定义保存位置" : "保存到我的记录" };
            if (SnapshotOnly) options.Add("快照模式 · 不启用 ETW");
            if (!string.IsNullOrWhiteSpace(RegistryKey)) options.Add("请求 HKCU 注册表观察");
            if (ArgumentsText.Length != 0) options.Add($"{ArgumentLines().Length} 项字面参数");
            if (SettleMilliseconds != 1500 || MaxDrainMilliseconds != 10000)
                options.Add($"静默 {SettleMilliseconds} / 排空 {MaxDrainMilliseconds} ms");
            return string.Join(" · ", options);
        }
    }

    public string TaskSummary => new StringBuilder()
        .AppendLine(ProductVersion.Display)
        .AppendLine(StatusTitle + " · " + Step).AppendLine(StatusMessage)
        .AppendLine("安装器：" + InstallerOutcome).AppendLine("记录：" + SavedState)
        .AppendLine("覆盖：" + Coverage).AppendLine("耗时：" + Elapsed).AppendLine(Measurements)
        .AppendLine($"会话 ID：{SessionId ?? "未确认"}")
        .AppendLine($"数据库：{LastStatus?.Database ?? runningRequest?.Database ?? SelectedDatabase}")
        .AppendLine($"最后确认阶段：{Result?.LastConfirmedStage ?? LastStatus?.Stage ?? "未取得结构化状态"}")
        .AppendLine($"采集进程 PID：{Result?.CollectorPid?.ToString(CultureInfo.InvariantCulture) ?? "未确认"}；退出码：{Result?.CollectorExitCode.ToString(CultureInfo.InvariantCulture) ?? "尚未确认"}")
        .AppendLine("失败摘要：" + (HasError ? Error : "未报告"))
        .AppendLine("仅复制到本机，请审阅路径后再分享。").ToString();

    public string Diagnostics => new StringBuilder(TaskSummary)
        .AppendLine().AppendLine("技术详情 · 原始结果")
        .AppendLine($"安装器 PID / 创建时间：{LastStatus?.InstallerPid?.ToString(CultureInfo.InvariantCulture) ?? "未确认"} / {LastStatus?.InstallerCreationTime ?? "未提供"}")
        .AppendLine($"实际 ETW 来源：进程 {LastStatus?.EtwProcess ?? "未确认"} / 文件 {LastStatus?.EtwFile ?? "未确认"} / 注册表 {LastStatus?.EtwRegistry ?? "未确认"}")
        .AppendLine($"后端原始阶段 / 步骤：{LastStatus?.Stage ?? "未确认"} / {LastStatus?.Step ?? "未提供"}")
        .AppendLine($"采集停止：{LastStatus?.CaptureFinished.ToString() ?? "未确认"}；数据保存：{LastStatus?.DataSaved.ToString() ?? "未确认"}")
        .AppendLine("协议错误：" + (Result?.ProtocolError ?? "未报告"))
        .AppendLine("错误原文：" + (localFailureDetails.Length != 0 ? localFailureDetails : LastStatus?.Error ?? "未报告"))
        .AppendLine(string.Join(Environment.NewLine, LastStatus?.QualityReasons ?? []))
        .AppendLine(environmentDetails)
        .AppendLine($"采集进程诊断{(Result?.DiagnosticsTruncated == true ? "（达到长度上限，仅保留前段）" : "")}：")
        .AppendLine(Result?.Diagnostics ?? "未提供").ToString();

    private string SelectedDatabase => CustomDatabase ? DatabasePath : service.PersonalDatabase;

    public void SelectInstaller(string? path)
    {
        if (path is null || !ConfigurationEnabled) return;
        installerPath = path;
        if (!nameEdited) { name = Path.GetFileNameWithoutExtension(path); OnPropertyChanged(nameof(Name)); }
        OnPropertyChanged(nameof(InstallerPath));
        OnPropertyChanged(nameof(InstallerFileName));
        NotifyConfiguration();
    }

    /// <summary>An explicit new task, never invoked merely when navigating away.</summary>
    public void Reset()
    {
        if (Busy) return;
        ++generation;
        lock (progressGate) { pendingProgress = null; dispatchScheduled = false; }
        consumed = false; nameEdited = false; runningRequest = null; receivedAt = 0;
        installerPath = name = watchRoot = argumentsText = registryKey = "";
        followInstaller = true; customDatabase = snapshotOnly = false;
        databasePath = service.PersonalDatabase; settleMilliseconds = 1500; maxDrainMilliseconds = 10000;
        LastStatus = null; Result = null; Error = localFailureDetails = environmentDetails = "";
        progressClock = new(); taskClock.Reset();
        StatusTitle = "准备记录一次安装";
        StatusMessage = "选择 EXE，然后点击开始记录。选择文件本身不会启动安装器。";
        Elapsed = "00:00:00"; Measurements = "尚无实际进展测量。";
        OnPropertyChanged(string.Empty);
    }

    public async Task StartAsync()
    {
        if (!CanStart || Interlocked.CompareExchange(ref busyGate, 1, 0) != 0) return;
        active = true;
        var taskGeneration = ++generation;
        Error = localFailureDetails = environmentDetails = "";
        LastStatus = null; Result = null; receivedAt = 0; progressClock = new();
        StatusTitle = "准备中";
        StatusMessage = "正在检查保存位置与实际采集来源；检查通过后才会启动安装器。";
        Elapsed = "0 毫秒（本地计时）";
        Measurements = "尚未收到实际进展测量。";
        taskClock.Restart(); NotifyState();
        try
        {
            // This immutable snapshot also binds later diagnostics and readback.
            var request = BuildRequest();
            runningRequest = request;
            consumed = true;
            Result = await service.RunAsync(request, value => QueueStatus(value, taskGeneration));
            taskClock.Stop();
            consumed = !Result.CanRetryBeforeInstaller;
            LastStatus = Result.LastStatus;
            if (!Result.Succeeded)
            {
                Error = Result.ProtocolError ?? LastStatus?.Error ?? "采集进程未确认完整结束。";
                localFailureDetails = Error;
            }
            RenderStatus();
            // A result is opened only by the exact backend-issued UUID. Partial
            // saved evidence remains available through the explicit view action.
            if (Result.Succeeded && LastStatus is { } saved && OpenSessionRequested is not null)
            {
                try { await OpenSessionRequested(saved.Database, saved.SessionId); }
                catch (Exception failure)
                {
                    Error = "记录已保存，但自动打开本次会话未完成。请点击查看本次记录。";
                    localFailureDetails = failure.ToString();
                }
            }
        }
        catch (Exception failure)
        {
            if (!service.CollectorStarted) consumed = false;
            Error = failure.Message;
            localFailureDetails = failure.ToString();
            StatusTitle = service.CollectorStarted ? "记录失败或未确认结束" : "未能开始记录";
            StatusMessage = service.CollectorStarted
                ? "已有证据保留。请查看技术详情；安装器退出结果尚未确认时，需自行核对其状态。"
                : "安装器未启动。请核对安装器、保存位置和高级配置后重试；已有记录保持不变。";
        }
        finally
        {
            active = false; taskClock.Stop();
            ++generation;
            lock (progressGate) { pendingProgress = null; dispatchScheduled = false; }
            Volatile.Write(ref busyGate, 0);
            Tick(); NotifyState();
        }
    }

    public async Task CheckEnvironmentAsync()
    {
        if (!CanCheckEnvironment || Interlocked.CompareExchange(ref busyGate, 1, 0) != 0) return;
        checking = true; Error = localFailureDetails = "";
        StatusTitle = "正在检查环境";
        StatusMessage = "主动探测当前 ETW 能力；不会启动安装器，也不保证随后采集成功。";
        NotifyState();
        try
        {
            var report = await service.CheckEnvironmentAsync(SelectedDatabase);
            environmentDetails = report.GetRawText();
            var required = string.IsNullOrWhiteSpace(RegistryKey)
                ? new[] { "etw_process", "etw_file" } : new[] { "etw_process", "etw_file", "etw_registry" };
            var unavailable = required.Any(source => report.GetProperty(source).GetString() != "active");
            StatusTitle = "环境检查结束";
            StatusMessage = SnapshotOnly ? "已明确选择快照模式；记录不会启用 ETW，覆盖范围受限。"
                : unavailable ? "所需 ETW 来源当前不可用。请查看详情修正环境；程序不会自动提权或切换模式。"
                : "当前 ETW 来源可用。开始时仍会检查实际来源；安装器尚未启动。";
            if (!SnapshotOnly && unavailable) Error = "所需 ETW 来源当前不可用，安装器未启动。";
        }
        catch (Exception failure)
        {
            Error = failure.Message; localFailureDetails = failure.ToString();
            StatusTitle = "环境检查未完成"; StatusMessage = "安装器未启动，请查看技术详情并修正环境后重试。";
        }
        finally { checking = false; Volatile.Write(ref busyGate, 0); NotifyState(); }
    }

    public async Task OpenSessionAsync()
    {
        if (!CanOpenSession || LastStatus is not { } status || OpenSessionRequested is null) return;
        try { await OpenSessionRequested(status.Database, status.SessionId); }
        catch (Exception failure)
        {
            Error = "无法读取本次准确会话：" + failure.Message;
            localFailureDetails = failure.ToString(); NotifyState();
        }
    }

    public void RefuseClose()
    {
        if (Busy) StatusMessage = "任务仍在运行，请等待当前任务结束后关闭。窗口保持打开，不会强制终止安装器。";
    }

    public void Tick()
    {
        // The shell timer stays alive while browsing. Do not rebuild a completed
        // task's potentially large diagnostic text on every UI clock tick.
        if (!active || Result is not null) return;
        if (LastStatus is not null) RenderMeasurements();
        else Elapsed = Duration((ulong)taskClock.ElapsedMilliseconds) + "（本地计时）";
    }

    private InstallRequest BuildRequest() => new(InstallerPath, Name, WatchRoot, SelectedDatabase,
        ArgumentLines(), string.IsNullOrWhiteSpace(RegistryKey) ? null : RegistryKey, SnapshotOnly,
        SettleMilliseconds, MaxDrainMilliseconds, FollowInstaller);

    private string[] ArgumentLines() => ArgumentsText.Length == 0 ? []
        : ArgumentsText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private bool Configure<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!ConfigurationEnabled || !SetProperty(ref field, value, propertyName)) return false;
        NotifyConfiguration(); return true;
    }

    private void NotifyConfiguration()
    {
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(ConfigurationSummary));
        OnPropertyChanged(nameof(Diagnostics)); OnPropertyChanged(nameof(TaskSummary));
    }

    private void QueueStatus(InstallStatus value, int taskGeneration)
    {
        lock (progressGate)
        {
            if (taskGeneration != generation || !active || Result is not null) return;
            pendingProgress = (value, Stopwatch.GetTimestamp());
            if (dispatchScheduled) return;
            dispatchScheduled = true;
        }
        dispatch(() =>
        {
            (InstallStatus Status, long ReceivedAt)? latest;
            lock (progressGate)
            {
                if (taskGeneration != generation) return;
                latest = pendingProgress; pendingProgress = null; dispatchScheduled = false;
            }
            if (latest is null || Result is not null || !active) return;
            LastStatus = latest.Value.Status; receivedAt = latest.Value.ReceivedAt;
            RenderStatus(); NotifyState();
        });
    }

    private void RenderStatus()
    {
        if (LastStatus is not { } value)
        {
            StatusTitle = "记录失败或未确认结束";
            StatusMessage = "未取得可确认会话的结构化结果。请查看技术详情；保存与安装器退出均未确认。";
            return;
        }
        var completion = Result?.Succeeded;
        StatusTitle = completion == false ? "记录失败或未确认结束" : value.Stage switch
        {
            "preparing" => "准备中", "capturing" => "正在记录安装", "finalizing" => "正在整理与保存",
            "finished" => completion == true ? "记录已保存" : "正在确认最终结果",
            "failed" => "记录失败或未确认结束", _ => "记录状态未确认"
        };
        StatusMessage = completion == true ? "记录已保存，可以查看文件操作。"
            : completion == false || value.Stage == "failed" ? "本次记录未确认完整结束，请查看技术详情。" + (value.DataSaved ? "可打开本次记录查看已保存的证据。" : "")
            : value.Stage == "finished" ? "已收到最终状态，正在确认采集进程退出与本次结果。"
            : value.Step == "result_readback" ? "记录正在按准确会话 ID 读回，等待最终结果确认。"
            : value.CaptureFinished ? "采集已停止，请等待整理、保存与最终结果确认。"
            : value.InstallerExited ? "安装器已退出，请等待整理和保存。"
            : value.InstallerInteraction == "may_need_input" ? "安装器仍在运行，可能需要您在安装器窗口继续操作。"
            : "正在准备本次记录，安装器启动状态以实际来源为准。";
        RenderMeasurements();
    }

    private void RenderMeasurements()
    {
        if (LastStatus is not { } value) return;
        var receiptAge = receivedAt == 0 ? (ulong?)null : (ulong)Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds;
        var local = active && Result is null && value.Stage is not ("finished" or "failed");
        var times = progressClock.Observe(value, receiptAge ?? 0, (ulong)taskClock.ElapsedMilliseconds, local);
        Elapsed = times.Task is { } elapsedValue ? Duration(elapsedValue) + (local ? "（本地续计）" : "") : "未提供任务耗时";
        var count = value.ProcessedCount is { } processed ? $"{processed:N0} {Unit(value.ProcessedUnit)}" : "未提供";
        Measurements = $"阶段耗时：{Measured(times.Stage)} · 步骤耗时：{Measured(times.Step)}\n已处理：{count}\n最近实际进展：{Ago(times.ProgressAge)} · 最近状态接收：{Ago(receiptAge)}"
            + (local ? "\n计时刷新与消息接收不代表实际工作进展；未收到消息不自动判断卡死。" : "");
    }

    private void NotifyState()
    {
        foreach (var property in new[] { nameof(Busy), nameof(IsActive), nameof(IsChecking), nameof(ConfigurationEnabled),
            nameof(CanStart), nameof(CanCheckEnvironment), nameof(CanOpenSession), nameof(HasTask), nameof(LastStatus),
            nameof(Result), nameof(SessionId), nameof(Step), nameof(InstallerOutcome), nameof(SavedState), nameof(Coverage),
            nameof(TaskSummary), nameof(Diagnostics) }) OnPropertyChanged(property);
    }

    private static string Measured(ulong? value) => value is { } count ? Duration(count) : "未提供";
    private static string Ago(ulong? value) => value is { } count ? Duration(count) + "前" : "未提供";
    public static string Duration(ulong milliseconds) => milliseconds < 1000 ? $"{milliseconds} 毫秒"
        : $"{milliseconds / 1000 / 3600:D2}:{milliseconds / 1000 / 60 % 60:D2}:{milliseconds / 1000 % 60:D2}";
    private static string Unit(string? unit) => unit switch
    {
        "entries" => "项（含跳过或不可读项）", "raw_events" => "条原始事件", "resources" => "项资源",
        "source_records" => "条来源记录（不代表保存或归因）", null => "单位未提供", _ => unit
    };
    public static string StepLabel(string? step) => step switch
    {
        "preparing" or "database_open" => "检查记录保存位置", "baseline_inventory" => "读取初始配置清单",
        "provider_start" => "检查并启动采集来源", "baseline_files" => "读取初始文件状态",
        "installer_running" => "运行安装器并记录", "descendant_drain" => "等待已验证子进程活动结束",
        "final_files" => "读取最终文件状态", "final_registry" => "读取最终注册表状态",
        "source_stop" => "停止来源并排空队列", "evidence_replay" => "读回原始记录",
        "evidence_association" => "关联事件与状态依据", "evidence_normalization" => "整理来源操作",
        "checkpoint_finalize" => "提交原始记录检查点", "final_inventory" => "读取最终配置清单",
        "sqlite_save" => "保存会话", "result_readback" => "按准确会话读回结果", "completed" => "最终结果已读回",
        null => "实际步骤尚未提供", _ => "后端步骤：" + step
    };
}
