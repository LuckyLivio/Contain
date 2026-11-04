using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Contain.Viewer;

public sealed class InstallForm : Form
{
    private readonly InstallClient client;
    private readonly IInstallDialogs dialogs;
    private readonly Func<string, string, Task> openSession;
    private readonly TextBox installer = Input("InstallerPath");
    private readonly TextBox recordName = Input("RecordName");
    private readonly TextBox watchRoot = Input("WatchRoot");
    private readonly TextBox database = Input("DatabaseSavePath");
    private readonly TextBox arguments = new() { Name = "InstallerArguments", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox registryKey = Input("RegistryKey");
    private readonly NumericUpDown settle = new() { Name = "SettleMilliseconds", Maximum = 600000, Value = 1500, Increment = 100, Dock = DockStyle.Fill };
    private readonly NumericUpDown maxDrain = new() { Name = "MaxDrainMilliseconds", Maximum = 600000, Value = 10000, Increment = 100, Dock = DockStyle.Fill };
    private readonly CheckBox snapshot = new() { Name = "SnapshotOnly", Text = "明确选择快照模式（不启用 ETW，证据受限）", AutoSize = true };
    private readonly CheckBox advancedToggle = new() { Name = "AdvancedToggle", Text = "高级参数", AutoSize = true };
    private readonly Button check = Action("CheckEnvironment", "检查环境");
    private readonly Button start = Action("StartRecording", "开始记录");
    private readonly Button view = Action("ViewRecordedSession", "查看本次记录");
    private readonly Button details = Action("TaskDetails", "查看任务详情");
    private readonly Button copySummary = Action("CopyTaskSummary", "复制任务摘要");
    private readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 500 };
    private readonly Stopwatch taskClock = new();
    private readonly InstallProgressClock progressClock = new();
    private readonly object progressGate = new();
    private (InstallStatus Status, long ReceivedAt)? pendingProgress;
    private bool dispatchScheduled;
    private int generation;
    private long lastStatusReceivedAt;
    private string? localFailure;
    private bool closeRefused;
    private readonly TextBox status = new() { Name = "InstallStatus", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly TextBox scopePreview = new() { Name = "CaptureConfigurationPreview", Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, BackColor = SystemColors.Control, BorderStyle = BorderStyle.FixedSingle, MinimumSize = new Size(0, 100) };
    private readonly Panel configuration = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private bool checking;
    private bool consumed;

    public bool IsActive { get; private set; }
    public bool IsChecking => checking;
    public event Action<InstallRunResult>? RecordingEnded;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public InstallStatus? LastStatus { get; private set; }
    public string? SessionId => LastStatus?.SessionId;
    public string Database => Path.GetFullPath(database.Text);
    public InstallRunResult? Result { get; private set; }

    public InstallForm(InstallClient client, Func<string, string, Task> openSession, IInstallDialogs? dialogs = null)
    {
        this.client = client;
        this.openSession = openSession;
        this.dialogs = dialogs ?? new InstallDialogs();
        Name = "NewInstallForm";
        Text = Contain.Viewer.ProductVersion.Display + " · 新建安装记录";
        Font = new Font("Microsoft YaHei UI", 9.5F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 820);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterParent;
        Build();
        foreach (var input in new[] { installer, watchRoot, database, registryKey }) input.TextChanged += (_, _) => UpdateScopePreview();
        snapshot.CheckedChanged += (_, _) => UpdateScopePreview();
        UpdateScopePreview();
        status.Text = "准备配置。仅点击“开始记录”才会启动所选安装器。\r\n当前支持可直接启动的 EXE；未承诺 MSI、更新器或服务代理完整支持。";
        start.Click += (_, _) => { if (!IsActive && !checking && !consumed) PendingOperation = StartAsync(); };
        check.Click += (_, _) => { if (!IsActive && !checking && !consumed) PendingOperation = CheckAsync(); };
        view.Click += (_, _) => { if (!IsActive && LastStatus is not null) PendingOperation = OpenAsync(); };
        details.Click += (_, _) => ShowTaskDetails();
        copySummary.Click += (_, _) =>
        {
            try { Clipboard.SetText(TaskSummaryText); }
            catch (System.Runtime.InteropServices.ExternalException) { status.AppendText("\r\n剪贴板暂不可用，可在任务详情中选择并复制文字。"); }
        };
        displayTimer.Tick += (_, _) =>
        {
            if (!IsActive || IsDisposed || Disposing) return;
            if (LastStatus is not null) RenderStatus(LastStatus);
            else status.Text = $"准备中 · 任务已运行 {Duration((ulong)taskClock.ElapsedMilliseconds)}（本地计时）\r\n尚未收到可确认会话的后端状态；没有实际进展测量。\r\n请等待后端确认，不会仅因没有消息判断卡死。";
        };
        FormClosing += (_, args) =>
        {
            if (!IsActive && !checking) return;
            args.Cancel = true;
            closeRefused = true;
            if (LastStatus is not null) RenderStatus(LastStatus);
            else status.Text = "窗口保持打开：任务仍在运行。请等待任务退出；不会强制终止安装器。";
        };
        Load += (_, _) =>
        {
            // DPI-scaled preferred dimensions may exceed a small display. Keep the
            // action/status rows on screen; the configuration has its own scroll area.
            var working = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, working.Width), Math.Min(MinimumSize.Height, working.Height));
            if (Width > working.Width || Height > working.Height)
            {
                Size = new Size(Math.Min(Width, working.Width), Math.Min(Height, working.Height));
                Location = new Point(working.Left + Math.Max(0, (working.Width - Width) / 2), working.Top + Math.Max(0, (working.Height - Height) / 2));
            }
        };
        view.Enabled = false;
    }

    private void Build()
    {
        var viewport = new Panel { Name = "InstallViewport", Dock = DockStyle.Fill, AutoScroll = true };
        var root = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(14), ColumnCount = 1, RowCount = 5, MinimumSize = new Size(0, 560), Height = 560 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        Controls.Add(viewport);
        viewport.Controls.Add(root);
        viewport.ClientSizeChanged += (_, _) => root.Height = Math.Max(root.MinimumSize.Height, viewport.ClientSize.Height);
        root.Height = Math.Max(root.MinimumSize.Height, viewport.ClientSize.Height);
        var scopeNote = new Label { Name = "CaptureScopeNote", AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3, 0, 3, 10), Text = "新建安装记录 · 当前支持可直接启动的 EXE\r\n安装包位置与实际安装目标不同。请先选定一个已存在的观察目录，并在安装器中将目标设在该范围内；范围之外的变化可能无法观察。不会自动创建目标目录或扩大范围。" };
        root.Controls.Add(scopeNote, 0, 0);
        root.SizeChanged += (_, _) => scopeNote.MaximumSize = new Size(Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal - scopeNote.Margin.Horizontal), 0);
        var fields = new TableLayoutPanel { Name = "InstallConfigurationFields", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(0, 0, 6, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configuration.Controls.Add(fields);
        root.Controls.Add(configuration, 0, 1);
        AddRow(fields, "安装器 EXE", installer, Browse("ChooseInstaller", () => dialogs.ChooseInstaller(this), installer));
        AddRow(fields, "记录名称", recordName);
        AddRow(fields, "观察目录", watchRoot, Browse("ChooseWatchRoot", () => dialogs.ChooseWatchRoot(this), watchRoot));
        AddRow(fields, "数据库位置", database, Browse("ChooseSaveDatabase", () => dialogs.ChooseSaveDatabase(this), database));
        var modeRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(snapshot, 1, modeRow); fields.SetColumnSpan(snapshot, 2);
        var toggleRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(advancedToggle, 1, toggleRow);
        var advanced = new TableLayoutPanel { Name = "AdvancedParameters", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Visible = false };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var advancedRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(advanced, 0, advancedRow); fields.SetColumnSpan(advanced, 3);
        arguments.MinimumSize = new Size(0, 78);
        AddAdvanced(advanced, "逐行参数", arguments);
        var argumentNote = new Label { Name = "InstallerArgumentsNote", Dock = DockStyle.Top, Text = "每行是一个独立的原始参数；引号也是参数内容，无需 shell 转义。空白输入不传参数；中间空行表示空参数。", AutoSize = true };
        AddAdvanced(advanced, "参数说明", argumentNote);
        advanced.SizeChanged += (_, _) => argumentNote.MaximumSize = new Size(Math.Max(1, advanced.ClientSize.Width - advanced.GetColumnWidths().FirstOrDefault() - argumentNote.Margin.Horizontal), 0);
        AddAdvanced(advanced, "HKCU 注册表键", registryKey);
        AddAdvanced(advanced, "静默等待 ms", settle);
        AddAdvanced(advanced, "最长排空 ms", maxDrain);
        advancedToggle.CheckedChanged += (_, _) => advanced.Visible = advancedToggle.Checked;
        root.Controls.Add(scopePreview, 0, 2);
        var actions = new FlowLayoutPanel { Name = "InstallActions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        actions.Controls.Add(check); actions.Controls.Add(start); actions.Controls.Add(view); actions.Controls.Add(details); actions.Controls.Add(copySummary);
        root.Controls.Add(actions, 0, 3);
        root.Controls.Add(status, 0, 4);
    }

    private void UpdateScopePreview()
    {
        static string Chosen(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未选择";
            try { return Path.GetFullPath(value); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return value + "（待校验）"; }
        }
        scopePreview.Text = $"开始前核对 · 实际观察目录：{Chosen(watchRoot.Text)}\r\n数据库：{Chosen(database.Text)}（必须位于观察范围之外）\r\n注册表：{(string.IsNullOrWhiteSpace(registryKey.Text) ? "未请求" : "请求 HKCU\\" + registryKey.Text)}；模式：{(snapshot.Checked ? "明确选择快照，不尝试 ETW" : "要求 ETW 来源可用")}\r\n安装目标由您在安装器中选择；安装包所在目录不等于观察范围。";
    }

    private InstallRequest Request()
    {
        var lines = arguments.Text.Length == 0 ? Array.Empty<string>() : arguments.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return new InstallRequest(installer.Text, recordName.Text, watchRoot.Text, database.Text, lines,
            string.IsNullOrWhiteSpace(registryKey.Text) ? null : registryKey.Text, snapshot.Checked,
            (ulong)settle.Value, (ulong)maxDrain.Value).Validate();
    }

    private async Task CheckAsync()
    {
        checking = true; UpdateEnabled();
        var checkGeneration = ++generation;
        try
        {
            var request = Request();
            status.Text = "正在检查环境。此检查会主动探测 ETW，但不会启动安装器，也不保证随后采集成功。";
            var report = await client.CheckEnvironmentAsync(request.Database);
            if (IsDisposed || Disposing || checkGeneration != generation) return;
            var elevated = report.GetProperty("elevated").GetBoolean();
            var text = new StringBuilder("环境预检结束（仅报告当前可用能力；不是本次实际启用来源，也不是启动保证）\r\n");
            text.AppendLine("当前管理员令牌：" + (elevated ? "是" : "否"));
            text.AppendLine("进程身份观察：" + report.GetProperty("process_observation").GetBoolean());
            foreach (var source in new[] { "etw_process", "etw_file", "etw_registry" }) text.AppendLine("可用能力 " + source + "：" + report.GetProperty(source).GetString());
            text.AppendLine("本次注册表观察：" + (request.RegistryKey is null ? "未请求；预检可用不代表记录会启用注册表来源。" : "请求 HKCU\\" + request.RegistryKey));
            if (request.SnapshotOnly) text.AppendLine("您已明确选择快照模式：本次记录不尝试 ETW；只观察所选目录通知、前后快照和可读取的配置，无法提供完整内核源归因。");
            else if ((request.RegistryKey is null ? new[] { "etw_process", "etw_file" } : new[] { "etw_process", "etw_file", "etw_registry" }).Any(x => report.GetProperty(x).GetString() != "active"))
                text.AppendLine(elevated ? "ETW 当前不可用。请查看下方具体原因；开始时会再次检查，缺少所需来源将拒绝启动安装器。可明确选择范围受限的快照模式。" : "当前令牌下所需 ETW 来源不可用，可能缺少跟踪权限。可由您在获授权的环境中自行重新打开程序，或明确选择快照模式。本程序不会自动提权；开始时仍会检查实际来源。");
            else text.AppendLine("ETW 来源当前可用。实际记录仍可能发生来源丢失或降级，将按真实结果报告。");
            if (report.TryGetProperty("warnings", out var warnings)) foreach (var warning in warnings.EnumerateArray()) text.AppendLine("• " + warning.GetString());
            status.Text = text.ToString();
        }
        catch (Exception error) { if (!IsDisposed && !Disposing) status.Text = "环境检查未完成：" + error.Message + "\r\n安装器未启动。"; }
        finally { checking = false; if (!IsDisposed && !Disposing) UpdateEnabled(); }
    }

    private async Task StartAsync()
    {
        IsActive = true; UpdateEnabled();
        var taskGeneration = ++generation;
        localFailure = null; closeRefused = false;
        try
        {
            var request = Request();
            consumed = true;
            taskClock.Restart(); displayTimer.Start();
            status.Text = "准备中。等待后端确认会话与被管理进程；本窗口将保留至采集进程退出。";
            Result = await client.RunAsync(request, progress => QueueStatus(progress, taskGeneration));
            if (IsDisposed || Disposing || taskGeneration != generation) return;
            displayTimer.Stop(); taskClock.Stop();
            var result = Result;
            if (result.LastStatus is not null) { LastStatus = result.LastStatus; RenderStatus(result.LastStatus, result.Succeeded); }
            else status.Text = "失败 / 未完成\r\n尚未收到可确认会话的结构化结果；安装器启动与数据保存均未确认。";
            if (!result.Succeeded)
            {
                localFailure = result.ProtocolError ?? result.LastStatus?.Error ?? "采集进程返回失败，未收到完整成功结果。";
                if (LastStatus is not null) RenderStatus(LastStatus, false);
                else status.AppendText("\r\n失败原因：" + Brief(localFailure));
                status.AppendText("\r\n已保存的证据保留；错误详情和任务摘要可通过上方按钮查看、复制。");
            }
            if (result.ProtocolError is not null || LastStatus is { InstallerStarted: true, InstallerExited: false })
                status.AppendText("\r\n安装器退出结果未确认，可能仍在运行。请自行确认其状态。");
            // Exact backend-issued ID is queried even for an unfinished saved journal.
            if (LastStatus is not null && File.Exists(request.Database))
            {
                try { await openSession(request.Database, LastStatus.SessionId); if (!IsDisposed && !Disposing) Hide(); }
                catch (Exception error) { if (!IsDisposed && !Disposing) status.AppendText("\r\n自动打开本次会话未完成：" + Brief(error.Message) + "。可重新打开数据库查看。"); }
            }
            if (!IsDisposed && !Disposing) RecordingEnded?.Invoke(result);
        }
        catch (Exception error)
        {
            localFailure = error.Message;
            if (!IsDisposed && !Disposing) status.AppendText("\r\n启动或记录失败：" + Brief(error.Message) + "\r\n已有记录保留，可查看任务详情。");
        }
        finally
        {
            IsActive = false;
            ++generation;
            lock (progressGate) { pendingProgress = null; dispatchScheduled = false; }
            taskClock.Stop();
            if (!IsDisposed && !Disposing) { displayTimer.Stop(); UpdateEnabled(); }
        }
    }

    private void QueueStatus(InstallStatus value, int taskGeneration)
    {
        lock (progressGate)
        {
            if (IsDisposed || Disposing || taskGeneration != generation) return;
            pendingProgress = (value, Stopwatch.GetTimestamp());
            if (dispatchScheduled) return;
            dispatchScheduled = true;
        }
        try
        {
            BeginInvoke((Action)(() =>
            {
                (InstallStatus Status, long ReceivedAt)? latest;
                lock (progressGate) { latest = pendingProgress; pendingProgress = null; dispatchScheduled = false; }
                if (IsDisposed || Disposing || taskGeneration != generation || latest is null) return;
                LastStatus = latest.Value.Status;
                lastStatusReceivedAt = latest.Value.ReceivedAt;
                RenderStatus(LastStatus);
            }));
        }
        catch (InvalidOperationException) { lock (progressGate) { pendingProgress = null; dispatchScheduled = false; } }
    }

    private async Task OpenAsync()
    {
        if (LastStatus is null) return;
        try { await openSession(LastStatus.Database, LastStatus.SessionId); }
        catch (Exception error) { status.AppendText("\r\n无法读取本次会话：" + error.Message); }
    }

    private void RenderStatus(InstallStatus value, bool? completed = null)
    {
        var receivedAgo = lastStatusReceivedAt == 0 ? (ulong?)null : (ulong)Stopwatch.GetElapsedTime(lastStatusReceivedAt).TotalMilliseconds;
        var advance = IsActive && completed is null ? receivedAgo ?? 0 : 0;
        var display = progressClock.Observe(value, advance, (ulong)taskClock.ElapsedMilliseconds, IsActive && completed is null && value.Stage is not ("finished" or "failed"));
        status.Text = FormatStatus(value, completed, advance, receivedAgo, display);
        if (localFailure is not null && value.Error != localFailure) status.AppendText("\r\n失败原因：" + Brief(localFailure));
        if (closeRefused && IsActive) status.AppendText("\r\n窗口保持打开：任务仍在运行，请等待任务退出。");
        details.Enabled = copySummary.Enabled = true;
    }

    internal static string FormatStatus(InstallStatus value, bool? completed = null, ulong localAdvanceMilliseconds = 0, ulong? receivedAgoMilliseconds = null, InstallDisplayTimes? display = null)
    {
        var phase = completed == false ? "失败 / 未完成" : value.Stage switch { "preparing" => "准备中", "capturing" => "记录中", "finalizing" => "整理中", "finished" => completed == true ? "已结束" : "最终结果已保存，等待采集进程退出", "failed" => "失败 / 未完成", _ => "未确认" };
        var exit = !value.InstallerStarted ? "尚未确认启动" : !value.InstallerExited ? "已启动，尚未确认退出" : value.InstallerExitCode is null ? "已退出，未提供退出码" : value.InstallerExitCode == 0 ? "退出码 0（安装是否达到预期仍需核对）" : $"退出码 {value.InstallerExitCode}（非零；具体原因由安装器决定）";
        var quality = value.QualityLevel switch { "Degraded" => "Degraded · 覆盖受限", "Complete" => "Complete（仍限于配置范围与来源）", null => "整理完成后确认", _ => value.QualityLevel };
        var activeClock = completed is null && value.Stage is not ("finished" or "failed");
        var advance = activeClock ? localAdvanceMilliseconds : 0;
        var elapsed = (display?.Task ?? (value.ElapsedMilliseconds is { } taskMs ? SaturatingAdd(taskMs, advance) : (ulong?)null)) is { } taskDuration ? Duration(taskDuration) : "不可用";
        var stageElapsed = (display?.Stage ?? (value.StageElapsedMilliseconds is { } stageMs ? SaturatingAdd(stageMs, advance) : (ulong?)null)) is { } stageDuration ? Duration(stageDuration) : "不可用";
        var stepElapsed = (display?.Step ?? (value.StepElapsedMilliseconds is { } stepMs ? SaturatingAdd(stepMs, advance) : (ulong?)null)) is { } stepDuration ? Duration(stepDuration) : "不可用";
        var count = value.ProcessedCount is { } countValue ? $"{countValue:N0} {Unit(value.ProcessedUnit)}" : "不可用";
        var progressAge = value.ElapsedMilliseconds is { } measured && value.LastProgressElapsedMilliseconds is { } last
            ? Duration(display?.ProgressAge ?? SaturatingAdd(measured - last, advance)) + "前" : "不可用";
        var interaction = value.InstallerExited || value.InstallerInteraction == "not_needed" ? "无需继续操作安装器；请等待整理和保存。"
            : value.InstallerInteraction == "may_need_input" ? "安装器仍在运行，可能需要您在安装器窗口继续操作；无法判断它是否正在等待输入。"
            : value.InstallerInteraction == "not_started" ? "安装器尚未启动，请等待准备完成。" : "安装器交互状态不可用，请核对安装器窗口。";
        return $"{phase} · {StepLabel(value.Step)}\r\n任务耗时：{elapsed}；阶段耗时：{stageElapsed}；步骤耗时：{stepElapsed}；已处理：{count}\r\n最近实际进展：{progressAge}；最近状态接收：{(receivedAgoMilliseconds is { } receipt ? Duration(receipt) + "前" : "不可用")}\r\n{(activeClock ? "计时由本地续计；状态消息和界面刷新不等于实际工作进展，无消息不自动判定卡死。\r\n" : "")}下一步：{interaction}\r\n安装器结果：{exit}\r\n记录：{(completed == true ? "已完成" : "尚未确认完整结束")}；数据保存：{(value.DataSaved ? completed == true ? "已保存记录" : "已保存证据（可能仍不完整）" : "尚未确认")}\r\n采集来源：{(value.CaptureFinished ? "已停止" : "尚未确认停止")}；覆盖限制：{quality}\r\n{(string.IsNullOrWhiteSpace(value.Error) ? "" : "失败原因：" + Brief(value.Error) + "\r\n")}详情中可查看准确会话、来源、错误原文；High 不代表最终归属或安全删除许可。";
    }

    internal static string StepLabel(string? step) => step switch
    {
        "preparing" or "database_open" => "打开记录数据库", "baseline_inventory" => "读取初始配置清单",
        "provider_start" => "启动采集来源", "baseline_files" => "枚举并读取初始文件状态",
        "installer_running" => "运行安装器并记录来源", "descendant_drain" => "等待后代进程与活动安静",
        "final_files" => "枚举、读取并哈希最终文件状态", "final_registry" => "读取最终注册表状态",
        "source_stop" => "停止本次来源并排空队列", "evidence_replay" => "读回原始证据日志",
        "evidence_association" => "关联事件与状态证据", "evidence_normalization" => "规范化来源操作", "checkpoint_finalize" => "提交并关闭原始证据检查点", "final_inventory" => "读取最终配置清单",
        "sqlite_save" => "保存最终会话到 SQLite", "result_readback" => "按准确会话 ID 读回结果",
        "completed" => "最终结果已读回", null => "实际步骤不可用", _ => "后端步骤：" + Brief(step)
    };
    private static string Unit(string? unit) => unit switch { "entries" => "项（已完成的枚举/读取项，含跳过项）", "raw_events" => "条原始事件", "resources" => "项资源", "source_records" => "条已接收来源记录（不代表已保存或已归因）", null => "单位不可用", _ => unit };
    private static ulong SaturatingAdd(ulong value, ulong other) => ulong.MaxValue - value < other ? ulong.MaxValue : value + other;
    internal static string Duration(ulong milliseconds) => milliseconds < 1000 ? $"{milliseconds} 毫秒" : $"{milliseconds / 1000 / 3600:D2}:{milliseconds / 1000 / 60 % 60:D2}:{milliseconds / 1000 % 60:D2}";
    private static string Brief(string text) { var line = text.Replace('\r', ' ').Replace('\n', ' '); return line.Length <= 240 ? line : line[..240] + "…（原文见详情）"; }

    internal string TaskSummaryText => new StringBuilder()
        .AppendLine(Contain.Viewer.ProductVersion.Display).AppendLine(LastStatus is { } state ? FormatStatus(state, Result?.Succeeded) : status.Text)
        .AppendLine($"会话 ID：{LastStatus?.SessionId ?? "未确认"}")
        .AppendLine($"数据库：{LastStatus?.Database ?? database.Text}")
        .AppendLine($"最后确认阶段：{Result?.LastConfirmedStage ?? LastStatus?.Stage ?? "未取得结构化状态"}")
        .AppendLine($"实际步骤：{LastStatus?.Step ?? "不可用"}")
        .AppendLine($"采集进程 PID：{Result?.CollectorPid?.ToString() ?? "未确认"}；退出码：{Result?.CollectorExitCode.ToString() ?? "尚未确认"}")
        .AppendLine($"失败摘要：{Brief(localFailure ?? LastStatus?.Error ?? "未报告")}")
        .AppendLine("摘要仅复制到本机剪贴板，请审阅路径后再分享。 ").ToString();

    internal string TaskDetailsText => new StringBuilder(TaskSummaryText)
        .AppendLine().AppendLine("高级诊断（保留在本机）")
        .AppendLine($"安装器 PID / 创建时间：{LastStatus?.InstallerPid?.ToString() ?? "未确认"} / {LastStatus?.InstallerCreationTime ?? "不可用"}")
        .AppendLine($"ETW 实际来源：进程 {LastStatus?.EtwProcess ?? "未确认"} / 文件 {LastStatus?.EtwFile ?? "未确认"} / 注册表 {LastStatus?.EtwRegistry ?? "未确认"}")
        .AppendLine($"后端原始阶段 / 步骤：{LastStatus?.Stage ?? "未确认"} / {LastStatus?.Step ?? "不可用"}")
        .AppendLine($"结构化状态错误：{Result?.ProtocolError ?? "未报告"}")
        .AppendLine($"错误原文：{localFailure ?? LastStatus?.Error ?? "未报告"}")
        .AppendLine(string.Join("\r\n", LastStatus?.QualityReasons ?? []))
        .AppendLine($"采集进程诊断{(Result?.DiagnosticsTruncated == true ? "（长度超限，仅保留前段）" : "")}：")
        .AppendLine(Result?.Diagnostics ?? "未提供").ToString();

    private void ShowTaskDetails()
    {
        var window = new Form { Name = "InstallTaskDetails", Text = "Contain · 本次任务详情", Font = Font,
            AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(760, 520), MinimumSize = new Size(420, 280), StartPosition = FormStartPosition.CenterParent };
        window.Controls.Add(new TextBox { Name = "InstallTaskDiagnostics", Text = TaskDetailsText, Multiline = true,
            ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, BackColor = Color.White });
        window.Show(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ++generation;
            lock (progressGate) { pendingProgress = null; dispatchScheduled = false; }
            displayTimer.Stop(); displayTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void UpdateEnabled()
    {
        configuration.Enabled = !IsActive && !checking && !consumed;
        start.Enabled = !IsActive && !checking && !consumed;
        check.Enabled = !IsActive && !checking && !consumed;
        view.Enabled = !IsActive && !checking && LastStatus is not null && File.Exists(LastStatus.Database);
        details.Enabled = copySummary.Enabled = consumed || LastStatus is not null || localFailure is not null;
    }

    private static TextBox Input(string name) => new() { Name = name, Dock = DockStyle.Fill };
    private static Button Action(string name, string text) => new() { Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(4, 6, 8, 6) };
    private static Button Browse(string name, Func<string?> choose, TextBox target)
    {
        var button = Action(name, "选择…"); button.Anchor = AnchorStyles.Left;
        button.Click += (_, _) => { var path = choose(); if (path is not null) target.Text = path; };
        return button;
    }
    private static void AddRow(TableLayoutPanel layout, string title, Control field, Control? browse = null)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 6, 10, 6) }, 0, row);
        field.Dock = DockStyle.None; field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Margin = new Padding(4, 6, 4, 6); layout.Controls.Add(field, 1, row);
        if (browse is not null) layout.Controls.Add(browse, 2, row); else layout.SetColumnSpan(field, 2);
    }
    private static void AddAdvanced(TableLayoutPanel layout, string title, Control field)
    {
        var row = layout.RowCount++; layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 6, 10, 6) }, 0, row);
        field.Margin = new Padding(4, 6, 4, 6);
        field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        field.Dock = field is TextBox { Multiline: true } ? DockStyle.Fill : DockStyle.None;
        layout.Controls.Add(field, 1, row);
    }
}
