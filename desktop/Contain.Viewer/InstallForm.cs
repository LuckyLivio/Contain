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
    private readonly NumericUpDown settle = new() { Name = "SettleMilliseconds", Value = 1500, Maximum = 600000, Increment = 100, Dock = DockStyle.Fill };
    private readonly NumericUpDown maxDrain = new() { Name = "MaxDrainMilliseconds", Value = 10000, Maximum = 600000, Increment = 100, Dock = DockStyle.Fill };
    private readonly CheckBox snapshot = new() { Name = "SnapshotOnly", Text = "明确选择快照模式（不尝试 ETW，缺少内核源证据）", AutoSize = true };
    private readonly CheckBox advancedToggle = new() { Name = "AdvancedToggle", Text = "高级参数", AutoSize = true };
    private readonly Button check = Action("CheckEnvironment", "检查环境");
    private readonly Button start = Action("StartRecording", "开始记录");
    private readonly Button view = Action("ViewRecordedSession", "查看本次记录");
    private readonly TextBox status = new() { Name = "InstallStatus", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
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
        Text = "Contain · 新建安装记录";
        Font = new Font("Microsoft YaHei UI", 9.5F);
        ClientSize = new Size(880, 790);
        MinimumSize = new Size(760, 700);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Build();
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
        view.Enabled = false;
    }

    private void Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        Controls.Add(root);
        root.Controls.Add(new Label { Name = "CaptureScopeNote", Dock = DockStyle.Fill, Text = "新建安装记录\r\n仅观察指定的已存在目录及后端支持的来源；安装器所在目录不代表软件全部安装位置。不会扫描全盘、自动创建软件目标目录、自动提权或修改安全设置。已有数据库会追加本次记录，不会删除旧记录。" }, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 0, 6, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        configuration.Controls.Add(fields);
        root.Controls.Add(configuration, 0, 1);
        AddRow(fields, "安装器 EXE", installer, Browse("ChooseInstaller", () => dialogs.ChooseInstaller(this), installer));
        AddRow(fields, "记录名称", recordName);
        AddRow(fields, "观察目录", watchRoot, Browse("ChooseWatchRoot", () => dialogs.ChooseWatchRoot(this), watchRoot));
        AddRow(fields, "数据库位置", database, Browse("ChooseSaveDatabase", () => dialogs.ChooseSaveDatabase(this), database));
        var modeRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.Controls.Add(snapshot, 1, modeRow); fields.SetColumnSpan(snapshot, 2);
        var toggleRow = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fields.Controls.Add(advancedToggle, 1, toggleRow);
        var advanced = new TableLayoutPanel { Name = "AdvancedParameters", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Visible = false };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var advancedRow = fields.RowCount++;
        fields.Controls.Add(advanced, 0, advancedRow); fields.SetColumnSpan(advanced, 3);
        AddAdvanced(advanced, "逐行参数", arguments, 82);
        AddAdvanced(advanced, "参数说明", new Label { Dock = DockStyle.Fill, Text = "每行是一个独立的原始参数；引号也是参数内容。无需 shell 转义。空白输入表示不传参数；中间空行表示空参数。", AutoSize = false }, 50);
        AddAdvanced(advanced, "HKCU 注册表键", registryKey, 34);
        AddAdvanced(advanced, "静默等待 ms", settle, 34);
        AddAdvanced(advanced, "最长排空 ms", maxDrain, 34);
        advancedToggle.CheckedChanged += (_, _) => advanced.Visible = advancedToggle.Checked;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(check); actions.Controls.Add(start); actions.Controls.Add(view);
        root.Controls.Add(actions, 0, 2);
        root.Controls.Add(status, 0, 3);
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
            var text = new StringBuilder("环境检查结束（不是启动保证）\r\n");
            text.AppendLine("当前管理员令牌：" + (elevated ? "是" : "否"));
            text.AppendLine("进程身份观察：" + report.GetProperty("process_observation").GetBoolean());
            foreach (var source in new[] { "etw_process", "etw_file", "etw_registry" }) text.AppendLine(source + "：" + report.GetProperty(source).GetString());
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
            if (!result.Succeeded) status.AppendText("\r\n失败或未完成：" + (result.ProtocolError ?? result.LastStatus?.Error ?? "采集进程返回失败。") + $"\r\n采集进程退出码：{result.CollectorExitCode}。既有数据库与已经保存的证据未删除。\r\n" + result.Diagnostics);
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

    private void RenderStatus(InstallStatus value)
    {
        var phase = value.Stage switch { "preparing" => "准备中", "capturing" => "记录中", "finalizing" => "整理中", "finished" => "已结束", "failed" => "失败 / 未完成", _ => "未确认" };
        var exit = value.InstallerExited ? value.InstallerExitCode?.ToString() ?? "未提供退出码" : "尚未确认退出";
        status.Text = $"{phase}\r\n会话：{value.SessionId}\r\n数据库：{value.Database}\r\n安装器：{(value.InstallerStarted ? $"已启动，PID {value.InstallerPid}，创建时间 {value.InstallerCreationTime ?? "不可用（身份关联受限）"}" : "尚未确认启动")}\r\n安装器退出结果：{exit}\r\n采集来源已停止：{value.CaptureFinished}（整理阶段仍需保存最终结果）\r\n数据已保存：{value.DataSaved}（保存过证据不代表完整结束）\r\n请求模式：{(value.EtwRequested ? "ETW（启动时要求来源可用）" : "用户明确选择的快照模式")}\r\nETW 实际来源：进程 {value.EtwProcess ?? "尚未确认"} / 文件 {value.EtwFile ?? "尚未确认"} / 注册表 {value.EtwRegistry ?? "尚未确认"}\r\n覆盖质量：{value.QualityLevel ?? "整理完成后确认"}\r\n{string.Join("\r\n", value.QualityReasons)}\r\n{value.Error}";
    }

    private void UpdateEnabled()
    {
        configuration.Enabled = !IsActive && !checking && !consumed;
        start.Enabled = !IsActive && !checking && !consumed;
        check.Enabled = !IsActive && !checking && !consumed;
        view.Enabled = !IsActive && !checking && LastStatus is not null && File.Exists(LastStatus.Database);
    }

    private static TextBox Input(string name) => new() { Name = name, Dock = DockStyle.Fill };
    private static Button Action(string name, string text) => new() { Name = name, Text = text, AutoSize = true, Height = 36, Margin = new Padding(4, 6, 8, 6) };
    private static Button Browse(string name, Func<string?> choose, TextBox target)
    {
        var button = Action(name, "选择…"); button.Dock = DockStyle.Fill;
        button.Click += (_, _) => { var path = choose(); if (path is not null) target.Text = path; };
        return button;
    }
    private static void AddRow(TableLayoutPanel layout, string title, Control field, Control? browse = null)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        field.Margin = new Padding(4, 8, 4, 4); layout.Controls.Add(field, 1, row);
        if (browse is not null) layout.Controls.Add(browse, 2, row); else layout.SetColumnSpan(field, 2);
    }
    private static void AddAdvanced(TableLayoutPanel layout, string title, Control field, int height)
    {
        var row = layout.RowCount++; layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        layout.Controls.Add(field, 1, row);
    }
}
