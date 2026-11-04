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
        FormClosing += (_, args) =>
        {
            if (!IsActive && !checking) return;
            args.Cancel = true;
            status.AppendText("\r\n窗口保持打开：任务仍在运行。没有可靠的协作停止接口，不会强制终止安装器。请等待任务退出。\r\n");
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
        actions.Controls.Add(check); actions.Controls.Add(start); actions.Controls.Add(view);
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
        try
        {
            var request = Request();
            status.Text = "正在检查环境。此检查会主动探测 ETW，但不会启动安装器，也不保证随后采集成功。";
            var report = await client.CheckEnvironmentAsync(request.Database);
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
        catch (Exception error) { status.Text = "环境检查未完成：" + error.Message + "\r\n安装器未启动。"; }
        finally { checking = false; UpdateEnabled(); }
    }

    private async Task StartAsync()
    {
        IsActive = true; UpdateEnabled();
        try
        {
            var request = Request();
            consumed = true;
            status.Text = "准备中。等待后端确认会话与被管理进程；本窗口将保留至采集进程退出。";
            Result = await client.RunAsync(request, progress => { LastStatus = progress; RenderStatus(progress); });
            var result = Result;
            if (result.LastStatus is not null) { LastStatus = result.LastStatus; RenderStatus(result.LastStatus, result.Succeeded); }
            else status.Text = "失败 / 未完成\r\n尚未收到可确认会话的结构化结果；安装器启动与数据保存均未确认。\r\n";
            if (!result.Succeeded) status.AppendText("\r\n失败或未完成：" + (result.ProtocolError ?? result.LastStatus?.Error ?? "采集进程返回失败，未收到完整成功结果。") + $"\r\n最后确认阶段：{result.LastConfirmedStage ?? result.LastStatus?.Stage ?? "尚未收到状态"}；采集进程 PID：{result.CollectorPid?.ToString() ?? "未确认"}；退出码：{result.CollectorExitCode}。\r\n既有数据库与已经保存的证据未删除，可查看本次记录。\r\n高级错误诊断{(result.DiagnosticsTruncated ? "（长度超限，保留前段）" : "")}：\r\n" + result.Diagnostics);
            else status.AppendText("\r\n采集进程已退出，记录保存已确认。安装器退出结果和覆盖质量请分别查看。\r\n");
            if (result.ProtocolError is not null || LastStatus is { InstallerStarted: true, InstallerExited: false })
                status.AppendText("\r\n安装器退出结果未确认，安装器可能仍在运行。请自行确认其状态；本程序没有强制终止它。\r\n");
            // Exact backend-issued ID is queried even for an unfinished saved journal.
            if (LastStatus is not null && File.Exists(request.Database))
            {
                try { await openSession(request.Database, LastStatus.SessionId); Hide(); }
                catch (Exception error) { status.AppendText("\r\n自动打开本次会话未完成：" + error.Message + "。可用原有打开数据库功能检查已保存记录。"); }
            }
            RecordingEnded?.Invoke(result);
        }
        catch (Exception error) { status.AppendText("\r\n启动或记录失败：" + error.Message + "\r\n不会将失败当作完成，也不会删除已有记录。"); }
        finally { IsActive = false; UpdateEnabled(); }
    }

    private async Task OpenAsync()
    {
        if (LastStatus is null) return;
        try { await openSession(LastStatus.Database, LastStatus.SessionId); }
        catch (Exception error) { status.AppendText("\r\n无法读取本次会话：" + error.Message); }
    }

    private void RenderStatus(InstallStatus value, bool? completed = null)
    {
        status.Text = FormatStatus(value, completed);
    }

    internal static string FormatStatus(InstallStatus value, bool? completed = null)
    {
        var phase = completed == false ? "失败 / 未完成" : value.Stage switch { "preparing" => "准备中", "capturing" => "记录中", "finalizing" => "整理中", "finished" => completed == true ? "已结束" : "最终结果已保存，等待采集进程退出", "failed" => "失败 / 未完成", _ => "未确认" };
        var exit = !value.InstallerStarted ? "尚未确认启动" : !value.InstallerExited ? "已启动，尚未确认退出" : value.InstallerExitCode is null ? "已退出，未提供退出码" : value.InstallerExitCode == 0 ? "退出码 0（安装是否达到预期仍需核对）" : $"退出码 {value.InstallerExitCode}（非零；具体原因由安装器决定）";
        var quality = value.QualityLevel switch { "Degraded" => "Degraded · 覆盖受限", "Complete" => "Complete（仍限于配置范围与来源）", null => "整理完成后确认", _ => value.QualityLevel };
        return $"{phase}\r\n安装器结果：{exit}\r\n记录：{(completed == true ? "已完成" : "尚未确认完整结束")}；数据保存：{(value.DataSaved ? completed == true ? "已保存记录" : "已保存证据（可能仍不完整）" : "尚未确认")}\r\n本次变化：{(completed == true ? "请在本次记录的“状态变化”和“事件与依据”中查看。" : "采集或整理尚未完整确认，已有证据仍可查看。")}\r\n覆盖限制：{quality}；仅观察所选范围，不证明完整软件足迹或最终文件归属。\r\n{(string.IsNullOrWhiteSpace(value.Error) ? "" : "失败原因：" + value.Error + "\r\n")}\r\n高级诊断\r\n后端阶段：{value.Stage}\r\n会话：{value.SessionId}\r\n数据库：{value.Database}\r\n执行进程关联：{(value.InstallerStarted ? $"PID {value.InstallerPid}，创建时间 {value.InstallerCreationTime ?? "不可用（身份关联受限）"}" : "尚未确认启动")}（进程关联不等于最终文件归属）\r\n采集来源已停止：{value.CaptureFinished}；采集进程退出：{(completed is null ? "尚未确认" : "已退出")}\r\n数据已保存：{value.DataSaved}（保存过证据不代表安装成功或完整结束）\r\n请求模式：{(value.EtwRequested ? "ETW（启动时要求来源可用）" : "用户明确选择的快照模式")}\r\n本次 ETW 实际来源：进程 {value.EtwProcess ?? "尚未确认"} / 文件 {value.EtwFile ?? "尚未确认"} / 注册表 {value.EtwRegistry ?? "尚未确认"}\r\n{string.Join("\r\n", value.QualityReasons)}";
    }

    private void UpdateEnabled()
    {
        configuration.Enabled = !IsActive && !checking && !consumed;
        start.Enabled = !IsActive && !checking && !consumed;
        check.Enabled = !IsActive && !checking && !consumed;
        view.Enabled = !IsActive && !checking && LastStatus is not null && File.Exists(LastStatus.Database);
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
