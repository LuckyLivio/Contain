using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Contain.Viewer;

public sealed class InstallForm : Form
{
    private readonly InstallClient client;
    private readonly IInstallDialogs dialogs;
    private readonly PersonalRecords records;
    private readonly Func<string, string, Task> openSession;
    private readonly TextBox installer = Input("InstallerPath");
    private readonly Label installerName = new() { Name = "InstallerFileName", Text = "尚未选择安装器", UseMnemonic = false, AutoSize = true, Dock = DockStyle.Top };
    private readonly CheckBox showInstallerPath = new() { Name = "ShowInstallerPath", Text = "查看完整路径", AutoSize = true };
    private readonly Button chooseInstaller = Action("ChooseInstaller", "更换…");
    private readonly TextBox recordName = Input("RecordName");
    private readonly TextBox watchRoot = Input("WatchRoot");
    private readonly Button chooseWatchRoot = Action("ChooseWatchRoot", "选择文件夹…");
    private readonly Button copyWatchRoot = Action("CopyWatchRoot", "复制路径");
    private readonly Label watchHint = new() { Name = "WatchRootHint", AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(122, 75, 19) };
    private readonly Label correctionHint = new() { Name = "InstallCorrectionHint", AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(150, 45, 35), Visible = false };
    private readonly TextBox database = Input("DatabaseSavePath");
    private readonly TextBox arguments = new() { Name = "InstallerArguments", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox registryKey = Input("RegistryKey");
    private readonly NumericUpDown settle = new() { Name = "SettleMilliseconds", Maximum = 600000, Value = 1500, Increment = 100, Dock = DockStyle.Fill };
    private readonly NumericUpDown maxDrain = new() { Name = "MaxDrainMilliseconds", Maximum = 600000, Value = 10000, Increment = 100, Dock = DockStyle.Fill };
    private readonly CheckBox snapshot = new() { Name = "SnapshotOnly", Text = "明确选择快照模式（不启用 ETW，证据受限）", AutoSize = true };
    private readonly CheckBox customDatabase = new() { Name = "CustomDatabaseToggle", Text = "使用自定义保存位置", AutoSize = true };
    private readonly Label saveLocation = new() { Name = "DefaultSaveLocation", Text = "保存到我的记录", AutoSize = true, Dock = DockStyle.Top };
    private readonly Button changeSaveLocation = Action("ChangeSaveLocation", "更改保存位置");
    private readonly CheckBox advancedToggle = new() { Name = "AdvancedToggle", Text = "高级设置", AutoSize = true };
    private readonly Button reviewCaptureMode = Action("ReviewCaptureMode", "查看记录方式");
    private readonly Button check = Action("CheckEnvironment", "检查环境");
    private readonly Button start = Action("StartRecording", "开始记录");
    private readonly Button view = Action("ViewRecordedSession", "查看本次记录");
    private readonly Button details = Action("TaskDetails", "查看任务详情");
    private readonly Button copySummary = Action("CopyTaskSummary", "复制任务摘要");
    private readonly System.Windows.Forms.Timer displayTimer = new() { Interval = 500 };
    private readonly Stopwatch taskClock = new();
    private InstallProgressClock progressClock = new();
    private readonly object progressGate = new();
    private (InstallStatus Status, long ReceivedAt)? pendingProgress;
    private bool dispatchScheduled;
    private int generation;
    private long lastStatusReceivedAt;
    private string? localFailure;
    private string? localFailureDetails;
    private Control? correctionTarget;
    private bool closeRefused;
    private readonly TextBox status = new() { Name = "InstallStatus", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly TextBox scopePreview = new() { Name = "CaptureConfigurationPreview", Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, BackColor = SystemColors.Control, BorderStyle = BorderStyle.None, Height = 72, Visible = false, TabStop = true };
    private readonly Panel configuration = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly ToolTip tips = new();
    private TableLayoutPanel layoutRoot = null!;
    private bool checking;
    private bool consumed;
    private bool nameEdited;
    private bool suggestingName;

    public bool IsActive { get; private set; }
    public bool IsChecking => checking;
    public event Action<InstallRunResult>? RecordingEnded;
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public InstallStatus? LastStatus { get; private set; }
    public string? SessionId => LastStatus?.SessionId;
    private string SelectedDatabase => customDatabase.Checked ? database.Text : records.DatabasePath;
    public string Database => Path.GetFullPath(SelectedDatabase);
    public InstallRunResult? Result { get; private set; }

    public InstallForm(InstallClient client, Func<string, string, Task> openSession, IInstallDialogs? dialogs = null, PersonalRecords? records = null, string? initialInstaller = null)
    {
        this.client = client;
        this.openSession = openSession;
        this.dialogs = dialogs ?? new InstallDialogs();
        this.records = records ?? PersonalRecords.ForCurrentUser();
        database.Text = this.records.DatabasePath;
        Name = "NewInstallForm";
        Text = Contain.Viewer.ProductVersion.Display + " · 新建安装记录";
        Font = new Font("Microsoft YaHei UI", 9.5F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(800, 620);
        MinimumSize = new Size(640, 520);
        StartPosition = FormStartPosition.CenterParent;
        Build();
        foreach (var input in new[] { installer, watchRoot, database, registryKey, arguments }) input.TextChanged += (_, _) => { UpdateScopePreview(); UpdateEnabled(); };
        snapshot.CheckedChanged += (_, _) => UpdateScopePreview();
        settle.ValueChanged += (_, _) => UpdateScopePreview();
        maxDrain.ValueChanged += (_, _) => UpdateScopePreview();
        recordName.TextChanged += (_, _) => { if (!suggestingName) nameEdited = true; UpdateScopePreview(); };
        installer.TextChanged += (_, _) => { SuggestRecordName(); UpdateScopePreview(); };
        watchRoot.TextChanged += (_, _) => { watchHint.Text = string.IsNullOrWhiteSpace(watchRoot.Text) ? "还需选择观察文件夹，才能确定要记录哪里的变化。" : ""; correctionHint.Visible = false; };
        customDatabase.CheckedChanged += (_, _) => { saveLocation.Text = customDatabase.Checked ? "保存到自定义位置" : "保存到我的记录"; UpdateScopePreview(); };
        changeSaveLocation.Visible = false;
        changeSaveLocation.Click += (_, _) =>
        {
            advancedToggle.Checked = true; customDatabase.Checked = true;
            configuration.ScrollControlIntoView(database); database.Focus(); database.SelectAll();
        };
        reviewCaptureMode.Visible = false;
        reviewCaptureMode.Click += (_, _) =>
        {
            advancedToggle.Checked = true;
            configuration.ScrollControlIntoView(snapshot); snapshot.Focus();
        };
        chooseInstaller.Click += (_, _) =>
        {
            var selected = this.dialogs.ChooseInstaller(this);
            if (selected is not null) { installer.Text = selected; chooseWatchRoot.Focus(); }
        };
        chooseWatchRoot.Click += (_, _) =>
        {
            var selected = this.dialogs.ChooseWatchRoot(this);
            if (selected is not null) { watchRoot.Text = selected; start.Focus(); }
        };
        copyWatchRoot.Click += (_, _) =>
        {
            try { Clipboard.SetText(watchRoot.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { watchHint.Text = "剪贴板暂不可用，可选中上方路径后复制。"; }
        };
        showInstallerPath.CheckedChanged += (_, _) =>
        {
            installer.Visible = showInstallerPath.Checked;
            if (installer.Visible) { installer.Focus(); installer.SelectAll(); }
        };
        if (initialInstaller is not null) installer.Text = initialInstaller;
        UpdateScopePreview();
        watchHint.Text = "还需选择观察文件夹，才能确定要记录哪里的变化。";
        status.Text = "仅点击“开始记录”才会启动所选安装器。";
        status.Visible = false;
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
        Shown += (_, _) => { if (!string.IsNullOrWhiteSpace(installer.Text)) chooseWatchRoot.Focus(); else chooseInstaller.Focus(); };
        view.Enabled = false;
        UpdateEnabled();
    }

    private void Build()
    {
        var viewport = new Panel { Name = "InstallViewport", Dock = DockStyle.Fill, AutoScroll = true };
        var root = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(18), ColumnCount = 1, RowCount = 5, MinimumSize = new Size(0, 480), Height = 480 };
        layoutRoot = root;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        Controls.Add(viewport);
        viewport.Controls.Add(root);
        viewport.ClientSizeChanged += (_, _) => root.Height = Math.Max(root.MinimumSize.Height, viewport.ClientSize.Height);
        root.Height = Math.Max(root.MinimumSize.Height, viewport.ClientSize.Height);
        var heading = new Label { Name = "CaptureScopeNote", Text = "新建安装记录", AutoSize = true, Dock = DockStyle.Top,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 0, 3, 14) };
        root.Controls.Add(heading, 0, 0);
        var fields = new TableLayoutPanel { Name = "InstallConfigurationFields", Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(0, 0, 6, 0), TabIndex = 0 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        configuration.Controls.Add(fields);
        configuration.TabIndex = 0;
        root.Controls.Add(configuration, 0, 1);
        var installerRow = new TableLayoutPanel { Name = "InstallerSelection", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, TabIndex = 0 };
        installerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        installerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        installerName.Margin = new Padding(3, 12, 3, 6);
        installerName.AccessibleName = "安装器文件名";
        installerRow.Controls.Add(installerName, 0, 0);
        installerRow.Controls.Add(chooseInstaller, 1, 0);
        chooseInstaller.TabIndex = 0;
        AddSection(fields, installerRow);
        showInstallerPath.TabIndex = 1;
        AddSection(fields, showInstallerPath);
        installer.ReadOnly = true; installer.Visible = false; installer.TabIndex = 2;
        installer.AccessibleName = "安装器完整路径";
        AddSection(fields, installer);
        var watchLabel = new Label { Name = "WatchRootLabel", Text = "要观察哪个文件夹？", AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3, 18, 3, 4) };
        AddSection(fields, watchLabel);
        var scopeNote = new Label { Name = "WatchRootNote", Text = "Contain 会记录这个文件夹里的变化。要查看安装目标里的文件，请选择对应的已有文件夹。",
            AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(3, 0, 3, 8) };
        AddSection(fields, scopeNote);
        watchRoot.ReadOnly = true; watchRoot.TabIndex = 3; watchRoot.PlaceholderText = "请选择一个已有文件夹";
        watchRoot.AccessibleName = "实际观察文件夹路径";
        AddSection(fields, watchRoot);
        var folderActions = new FlowLayoutPanel { Name = "WatchRootActions", AutoSize = true, Dock = DockStyle.Top, WrapContents = true, TabIndex = 4 };
        chooseWatchRoot.TabIndex = 0; copyWatchRoot.TabIndex = 1;
        folderActions.Controls.Add(chooseWatchRoot); folderActions.Controls.Add(copyWatchRoot);
        AddSection(fields, folderActions);
        watchHint.Margin = new Padding(3, 0, 3, 10);
        AddSection(fields, watchHint);
        advancedToggle.TabIndex = 5;
        AddSection(fields, advancedToggle);
        var advanced = new TableLayoutPanel { Name = "AdvancedParameters", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Visible = false, TabIndex = 6 };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSection(fields, advanced);
        AddAdvanced(advanced, "记录名称", recordName);
        AddAdvanced(advanced, "默认保存", saveLocation);
        AddAdvanced(advanced, "记录保存", customDatabase);
        var customLocation = new TableLayoutPanel { Name = "CustomDatabaseLocation", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Visible = false };
        customLocation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        customLocation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        customLocation.Controls.Add(database, 0, 0);
        customLocation.Controls.Add(Browse("ChooseSaveDatabase", () => dialogs.ChooseSaveDatabase(this), database), 1, 0);
        var customLabel = new Label { Text = "数据库位置", AutoSize = true, Anchor = AnchorStyles.Left, Visible = false };
        var customRow = advanced.RowCount++;
        advanced.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        advanced.Controls.Add(customLabel, 0, customRow); advanced.Controls.Add(customLocation, 1, customRow);
        customDatabase.CheckedChanged += (_, _) => { customLabel.Visible = customLocation.Visible = customDatabase.Checked; };
        AddAdvanced(advanced, "记录方式", snapshot);
        arguments.MinimumSize = new Size(0, 78);
        AddAdvanced(advanced, "逐行参数", arguments);
        var argumentNote = new Label { Name = "InstallerArgumentsNote", Dock = DockStyle.Top, Text = "每行一个原始参数；无需 shell 转义。中间空行表示空参数。", AutoSize = true };
        AddAdvanced(advanced, "参数说明", argumentNote);
        advanced.SizeChanged += (_, _) => argumentNote.MaximumSize = new Size(Math.Max(1, advanced.ClientSize.Width - advanced.GetColumnWidths().FirstOrDefault() - argumentNote.Margin.Horizontal), 0);
        AddAdvanced(advanced, "HKCU 注册表键", registryKey);
        AddAdvanced(advanced, "静默等待 ms", settle);
        AddAdvanced(advanced, "最长排空 ms", maxDrain);
        AddAdvanced(advanced, "可选诊断", check);
        advancedToggle.CheckedChanged += (_, _) => advanced.Visible = advancedToggle.Checked;
        var notices = new TableLayoutPanel { Name = "InstallActionNotices", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, TabIndex = 1 };
        notices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSection(notices, scopePreview); AddSection(notices, correctionHint);
        root.Controls.Add(notices, 0, 2);
        var actions = new FlowLayoutPanel { Name = "InstallActions", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = Padding.Empty, TabIndex = 2 };
        start.BackColor = Color.FromArgb(22, 105, 117); start.ForeColor = Color.White; start.FlatStyle = FlatStyle.Flat; start.TabIndex = 0;
        actions.Controls.Add(start); actions.Controls.Add(view); actions.Controls.Add(details); actions.Controls.Add(copySummary);
        actions.Controls.Add(changeSaveLocation); actions.Controls.Add(reviewCaptureMode);
        var startNote = new Label { Name = "InstallStartNote", Text = "保存到我的记录；点击“开始记录”才会运行安装器。", AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(3, 12, 3, 6) };
        actions.Controls.Add(startNote);
        customDatabase.CheckedChanged += (_, _) => startNote.Text = customDatabase.Checked
            ? "保存到自定义位置；点击“开始记录”才会运行安装器。" : "保存到我的记录；点击“开始记录”才会运行安装器。";
        root.Controls.Add(actions, 0, 3);
        status.TabIndex = 3;
        root.Controls.Add(status, 0, 4);
        fields.SizeChanged += (_, _) =>
        {
            var width = Math.Max(1, fields.ClientSize.Width - fields.Padding.Horizontal - 10);
            scopeNote.MaximumSize = watchHint.MaximumSize = new Size(width, 0);
            installerName.MaximumSize = new Size(Math.Max(1, width - chooseInstaller.PreferredSize.Width - 20), 0);
        };
        root.SizeChanged += (_, _) => correctionHint.MaximumSize = new Size(Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal - 10), 0);
    }

    private static void AddSection(TableLayoutPanel layout, Control control)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top;
        layout.Controls.Add(control, 0, row);
    }

    private void UpdateScopePreview()
    {
        string filename;
        try { filename = Path.GetFileName(installer.Text); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { filename = installer.Text; }
        installerName.Text = string.IsNullOrWhiteSpace(filename) ? "尚未选择安装器" : filename;
        tips.SetToolTip(installerName, installer.Text);
        tips.SetToolTip(watchRoot, watchRoot.Text);
        chooseWatchRoot.Text = string.IsNullOrWhiteSpace(watchRoot.Text) ? "选择文件夹…" : "更换文件夹…";
        copyWatchRoot.Enabled = !string.IsNullOrWhiteSpace(watchRoot.Text);
        copyWatchRoot.Visible = copyWatchRoot.Enabled;
        // Put the selected mode first, so long names or paths cannot conceal
        // it when advanced settings are collapsed. Exact values remain selectable.
        var selected = new List<string>();
        var values = new List<string>();
        if (snapshot.Checked) selected.Add("快照模式（不启用 ETW，证据受限）");
        if (customDatabase.Checked) { selected.Add("自定义保存位置"); values.Add("保存位置：" + database.Text); }
        if (nameEdited) { selected.Add("自定义名称"); values.Add("记录名称：" + recordName.Text); }
        if (!string.IsNullOrWhiteSpace(registryKey.Text)) { selected.Add("观察注册表"); values.Add("注册表：HKCU\\" + registryKey.Text); }
        if (arguments.Text.Length != 0)
        {
            var items = arguments.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            selected.Add($"参数 {items.Length} 项");
            values.Add("参数：" + string.Join(" · ", items.Select(value => value.Length == 0 ? "（空参数）" : value)));
        }
        if (settle.Value != 1500 || maxDrain.Value != 10000) selected.Add($"等待：静默 {settle.Value} ms / 最长排空 {maxDrain.Value} ms");
        scopePreview.Text = selected.Count == 0 ? "" : "本次生效：" + string.Join("；", selected) + (values.Count == 0 ? "" : "\r\n" + string.Join("；", values));
        scopePreview.Visible = selected.Count != 0;
        tips.SetToolTip(scopePreview, scopePreview.Text);
    }

    private void ShowStatus()
    {
        status.Visible = true;
        layoutRoot.RowStyles[1] = new RowStyle(SizeType.Percent, 45);
        layoutRoot.RowStyles[4] = new RowStyle(SizeType.Percent, 55);
    }

    private void SuggestRecordName()
    {
        if (nameEdited) return;
        string suggestion;
        try { suggestion = Path.GetFileNameWithoutExtension(installer.Text); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return; }
        suggestingName = true;
        try { recordName.Text = suggestion; }
        finally { suggestingName = false; }
    }

    private InstallRequest Request()
    {
        var lines = arguments.Text.Length == 0 ? Array.Empty<string>() : arguments.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return new InstallRequest(installer.Text, recordName.Text, watchRoot.Text, SelectedDatabase, lines,
            string.IsNullOrWhiteSpace(registryKey.Text) ? null : registryKey.Text, snapshot.Checked,
            (ulong)settle.Value, (ulong)maxDrain.Value).Validate(allowMissingDatabaseDirectory: !customDatabase.Checked);
    }

    private async Task CheckAsync()
    {
        ShowStatus();
        correctionHint.Visible = changeSaveLocation.Visible = reviewCaptureMode.Visible = false;
        correctionTarget = null;
        localFailure = localFailureDetails = null;
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
            if (!request.SnapshotOnly && (request.RegistryKey is null ? new[] { "etw_process", "etw_file" } : new[] { "etw_process", "etw_file", "etw_registry" }).Any(x => report.GetProperty(x).GetString() != "active"))
            {
                localFailure = "环境检查：所需 ETW 来源当前不可用。";
                localFailureDetails = report.GetRawText();
                ShowSourceCorrection(IsPermissionFailure(report.GetRawText()) || new[] { "etw_process", "etw_file", "etw_registry" }.Any(x => report.GetProperty(x).GetString() == "permission_denied"));
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed && !Disposing)
            {
                localFailure = error.Message; localFailureDetails = error.ToString();
                ShowCorrection(error.Message);
                status.Text = "环境检查未完成：" + error.Message + "\r\n安装器未启动。";
            }
        }
        finally { checking = false; if (!IsDisposed && !Disposing) { UpdateEnabled(); FocusCorrection(); } }
    }

    private async Task StartAsync()
    {
        ShowStatus();
        correctionHint.Visible = changeSaveLocation.Visible = reviewCaptureMode.Visible = false;
        correctionTarget = null;
        IsActive = true; UpdateEnabled();
        var taskGeneration = ++generation;
        localFailure = null; localFailureDetails = null; closeRefused = false;
        try
        {
            var request = Request();
            LastStatus = null; Result = null; lastStatusReceivedAt = 0; progressClock = new();
            changeSaveLocation.Visible = false;
            consumed = true;
            taskClock.Restart(); displayTimer.Start();
            status.Text = "准备中。等待后端确认会话与被管理进程；本窗口将保留至采集进程退出。";
            Result = await client.RunAsync(request, progress => QueueStatus(progress, taskGeneration), records);
            if (IsDisposed || Disposing || taskGeneration != generation) return;
            displayTimer.Stop(); taskClock.Stop();
            var result = Result;
            consumed = !result.CanRetryBeforeInstaller;
            if (result.LastStatus is not null) { LastStatus = result.LastStatus; RenderStatus(result.LastStatus, result.Succeeded); }
            else status.Text = "失败 / 未完成\r\n尚未收到可确认会话的结构化结果；安装器启动与数据保存均未确认。";
            if (!result.Succeeded)
            {
                localFailure = result.ProtocolError ?? result.LastStatus?.Error ?? "采集进程返回失败，未收到完整成功结果。";
                if (!consumed)
                {
                    if (result.LastStatus is { EtwRequested: true } failed &&
                        (failed.EtwProcess is not (null or "active") || failed.EtwFile is not (null or "active") || failed.EtwRegistry is not (null or "active" or "not_requested")))
                        ShowSourceCorrection(new[] { failed.EtwProcess, failed.EtwFile, failed.EtwRegistry }.Any(x => x == "permission_denied") || IsPermissionFailure(localFailure));
                    else ShowCorrection(localFailure, result.LastStatus?.Step == "database_open");
                }
                if (LastStatus is not null) RenderStatus(LastStatus, false);
                else status.AppendText("\r\n失败原因：" + Brief(localFailure));
                status.AppendText("\r\n已保存的证据保留；错误详情和任务摘要可通过上方按钮查看、复制。");
            }
            if (result.ProtocolError is not null || LastStatus is { InstallerStarted: true, InstallerExited: false })
                status.AppendText("\r\n安装器退出结果未确认，可能仍在运行。请自行确认其状态。");
            // Exact backend-issued ID is queried even for an unfinished saved journal.
            if (LastStatus is not null && File.Exists(request.Database) && !result.CanRetryBeforeInstaller)
            {
                try { await openSession(request.Database, LastStatus.SessionId); if (!IsDisposed && !Disposing) Hide(); }
                catch (Exception error) { if (!IsDisposed && !Disposing) status.AppendText("\r\n自动打开本次会话未完成：" + Brief(error.Message) + "。可重新打开数据库查看。"); }
            }
            if (!IsDisposed && !Disposing) RecordingEnded?.Invoke(result);
        }
        catch (Exception error)
        {
            if (!client.CollectorStarted) consumed = false;
            localFailure = error.Message; localFailureDetails = error.ToString();
            if (!IsDisposed && !Disposing && !consumed) ShowCorrection(error.Message);
            if (!IsDisposed && !Disposing) status.AppendText("\r\n启动或记录失败：" + Brief(error.Message) + "\r\n已有记录保留，可查看任务详情。");
        }
        finally
        {
            IsActive = false;
            ++generation;
            lock (progressGate) { pendingProgress = null; dispatchScheduled = false; }
            taskClock.Stop();
            if (!IsDisposed && !Disposing) { displayTimer.Stop(); UpdateEnabled(); FocusCorrection(); }
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
        details.Visible = copySummary.Visible = true;
    }

    private void ShowSourceCorrection(bool permissionDenied)
    {
        correctionHint.Text = permissionDenied
            ? "当前权限不足以使用所需的 ETW 来源，安装器未启动。可查看详情，或在高级设置中自行选择证据受限的快照模式。"
            : "所需的 ETW 来源当前不可用，安装器未启动。可查看详情后重试，或在高级设置中自行选择证据受限的快照模式。";
        correctionHint.Visible = reviewCaptureMode.Visible = true;
        changeSaveLocation.Visible = false;
        correctionTarget = reviewCaptureMode;
    }

    private static bool IsPermissionFailure(string text) => text.Contains("PermissionDenied", StringComparison.OrdinalIgnoreCase)
        || text.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
        || text.Contains("权限", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:Win32|code:)\s*5\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private void ShowCorrection(string error, bool storageFailure = false)
    {
        changeSaveLocation.Visible = reviewCaptureMode.Visible = false;
        correctionTarget = details;
        if (!File.Exists(installer.Text) || !string.Equals(Path.GetExtension(installer.Text), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            correctionHint.Text = "安装器已不可用，请点击“更换”重新选择已有的 EXE；安装器未启动。";
            correctionTarget = chooseInstaller;
        }
        else if (!Directory.Exists(watchRoot.Text))
        {
            watchHint.Text = "观察文件夹已不存在，请重新选择已有文件夹。";
            correctionHint.Text = "请先重新选择观察文件夹；安装器未启动。";
            correctionTarget = chooseWatchRoot;
        }
        else if (storageFailure || error.Contains("数据库", StringComparison.Ordinal) || error.Contains("保存位置", StringComparison.Ordinal) || error.Contains("记录目录", StringComparison.Ordinal) || error.Contains("WAL", StringComparison.Ordinal)
            || error.Contains("database", StringComparison.OrdinalIgnoreCase) || error.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
        {
            correctionHint.Text = "记录保存位置不可用或与观察范围冲突，请点击“更改保存位置”；已有记录保留，安装器未启动。";
            changeSaveLocation.Visible = true;
            correctionTarget = changeSaveLocation;
        }
        else if (error.Contains("记录名称", StringComparison.Ordinal))
        {
            correctionHint.Text = "请展开高级设置，填写记录名称后重试；安装器未启动。";
            correctionTarget = advancedToggle;
        }
        else if (error.Contains("等待参数", StringComparison.Ordinal) || error.Contains("参数不能", StringComparison.Ordinal))
        {
            correctionHint.Text = "请展开高级设置，修正参数后重试；安装器未启动。";
            correctionTarget = advancedToggle;
        }
        else correctionHint.Text = "本次记录未能开始，请查看任务详情后重试。";
        correctionHint.Visible = true;
    }

    private void FocusCorrection()
    {
        if (correctionTarget is null || !correctionTarget.Visible || !correctionTarget.Enabled) return;
        if (configuration.Contains(correctionTarget)) configuration.ScrollControlIntoView(correctionTarget);
        correctionTarget.Focus();
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
        .AppendLine($"数据库：{LastStatus?.Database ?? SelectedDatabase}")
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
        .AppendLine($"错误原文：{localFailureDetails ?? localFailure ?? LastStatus?.Error ?? "未报告"}")
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
            tips.Dispose();
        }
        base.Dispose(disposing);
    }

    private void UpdateEnabled()
    {
        configuration.Enabled = !IsActive && !checking && !consumed;
        start.Enabled = !IsActive && !checking && !consumed && !string.IsNullOrWhiteSpace(installer.Text) && !string.IsNullOrWhiteSpace(watchRoot.Text);
        check.Enabled = !IsActive && !checking && !consumed;
        changeSaveLocation.Enabled = reviewCaptureMode.Enabled = !IsActive && !checking && !consumed;
        view.Enabled = !IsActive && !checking && LastStatus is not null && File.Exists(LastStatus.Database);
        details.Enabled = copySummary.Enabled = consumed || LastStatus is not null || localFailure is not null;
        details.Visible = details.Enabled; copySummary.Visible = copySummary.Enabled; view.Visible = view.Enabled;
    }

    private static TextBox Input(string name) => new() { Name = name, Dock = DockStyle.Fill };
    private static Button Action(string name, string text) => new() { Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(4, 6, 8, 6) };
    private static Button Browse(string name, Func<string?> choose, TextBox target)
    {
        var button = Action(name, "选择…"); button.Anchor = AnchorStyles.Left;
        button.Click += (_, _) => { var path = choose(); if (path is not null) target.Text = path; };
        return button;
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
