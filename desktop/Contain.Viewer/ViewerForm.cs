using System.Text;
using System.Text.Json;

namespace Contain.Viewer;

/// <summary>Read-only record views with an explicit, isolated installation workflow.</summary>
public sealed class ViewerForm : Form
{
    private static readonly Color Ink = Color.FromArgb(28, 43, 60);
    private static readonly Color Muted = Color.FromArgb(92, 108, 123);
    private static readonly Color Accent = Color.FromArgb(22, 105, 117);
    private static readonly Color Canvas = Color.FromArgb(245, 248, 250);
    private const string Missing = "未提供";
    private readonly CoreClient _client;
    private readonly IViewerDialogs _dialogs;
    private readonly int _pageSize;
    private readonly IInstallDialogs? _installDialogs;
    private readonly Button _newInstall = Button("NewInstall", "新建安装记录", true);
    private readonly Button _open = Button("OpenDatabase", "打开记录数据库", true);
    private readonly Button _export = Button("ExportReport", "导出本页报告");
    private readonly Label _databaseLabel = Label("DatabasePath", "尚未打开数据库", Muted);
    private readonly LinkLabel _captureSummary = new()
    {
        Name = "CaptureOutcome", Text = "新建记录会显式启动安装器；打开已有数据库始终只读。",
        UseMnemonic = false, ForeColor = Muted, LinkColor = Muted, ActiveLinkColor = Accent,
        LinkBehavior = LinkBehavior.HoverUnderline, LinkArea = new LinkArea(0, 0),
        Dock = DockStyle.Fill, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Label _status = Label("Status", "选择已有数据库开始查看。数据仅保留在本机，不启动采集。", Muted);
    private readonly Label _sessionTitle = Label("SessionTitle", "打开一份记录，了解它留下的证据", Ink);
    private readonly Label _summaryStatus = Label("SummaryStatus", "只读查看 · 不修改原数据库 · 不判断安全删除", Muted);
    private readonly DataGridView _sessions = Grid("Sessions");
    private readonly DataGridView _changes = Grid("Changes");
    private readonly DataGridView _events = Grid("Events");
    private readonly TextBox _overview = ReadText("Overview");
    private readonly TextBox _eventDetails = ReadText("EventDetails");
    private readonly TextBox _rawDetails = ReadText("RawDetails");
    private readonly TextBox _changeDetails = ReadText("ChangeDetails");
    private readonly TextBox _eventFilter = new() { Name = "EventFilter", Dock = DockStyle.Fill, PlaceholderText = "筛选当前页的操作、名称或置信标签" };
    private readonly TextBox _changeFilter = new() { Name = "ChangeFilter", Dock = DockStyle.Fill, PlaceholderText = "筛选本记录全部状态变化" };
    private readonly Button _previousEvents = Button("PreviousEvents", "上一页");
    private readonly Button _nextEvents = Button("NextEvents", "下一页");
    private readonly Button _previousChanges = Button("PreviousChanges", "上一页");
    private readonly Button _nextChanges = Button("NextChanges", "下一页");
    private readonly Label _eventsPage = Label("EventsPage", "尚未加载事件", Muted);
    private readonly Label _changesPage = Label("ChangesPage", "尚未加载变化", Muted);
    private readonly Button _copyEvent = Button("CopyEventId", "复制事件 ID");
    private readonly CheckBox _rawToggle = new() { Name = "RawToggle", Text = "显示高级原始字段（JSON）", AutoSize = true, ForeColor = Muted };
    private readonly TabControl _tabs = new() { Name = "Tabs", Dock = DockStyle.Fill, Padding = new Point(18, 8) };
    private readonly TableLayoutPanel _detailHost = new() { Name = "EventDetailHost", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
    private readonly ToolTip _tips = new();
    private CancellationTokenSource? _operationCancellation;
    private long _operationVersion;
    private bool _rendering;
    private string? _database;
    private string? _recordId;
    private string? _selectedEventId;
    private List<JsonElement> _pageEvents = [];
    private List<(string Kind, JsonElement Data)> _allChanges = [];
    private long _eventOffset;
    private int _changeOffset;
    private bool _moreEvents;
    private bool _moreChanges;
    private InstallRunResult? _lastRun;

    public bool IsBusy { get; private set; }
    public Task PendingOperation { get; private set; } = Task.CompletedTask;
    public InstallForm? ActiveInstallForm { get; private set; }

    public ViewerForm(CoreClient client, IViewerDialogs? dialogs = null, int pageSize = 50, IInstallDialogs? installDialogs = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        _client = client;
        _dialogs = dialogs ?? new ViewerDialogs();
        _pageSize = pageSize;
        _installDialogs = installDialogs;
        Name = "ContainViewer";
        Text = Contain.Viewer.ProductVersion.Display + " · 安装记录";
        // These programmatic dimensions are logical pixels at 96 DPI. Without
        // this baseline Windows can scale the font while fixed layout rows keep
        // their original height on the first high-DPI window.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        ForeColor = Ink;
        BackColor = Canvas;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 850);
        MinimumSize = new Size(1040, 720);
        BuildLayout();
        WireEvents();
        ResetRecord();
        UpdateEnabled();
    }

    private void BuildLayout()
    {
        var viewport = new Panel { Name = "ViewerViewport", Dock = DockStyle.Fill, AutoScroll = true };
        var root = new TableLayoutPanel
        {
            Name = "ViewerLayout", Padding = new Padding(22, 16, 22, 12), ColumnCount = 1, RowCount = 5,
            MinimumSize = new Size(960, 640)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(viewport);
        AddScrollableContent(viewport, root);
        var header = new TableLayoutPanel { Name = "ViewerHeader", Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var brand = Label("Brand", "Contain  /  安装记录", Ink);
        brand.Font = new Font(Font.FontFamily, 21F, FontStyle.Bold);
        header.Controls.Add(brand, 0, 0);
        header.Controls.Add(_newInstall, 1, 0);
        header.Controls.Add(_open, 2, 0);
        header.Controls.Add(_export, 3, 0);
        root.Controls.Add(header, 0, 0);
        _databaseLabel.AutoEllipsis = true;
        _databaseLabel.Padding = new Padding(4, 0, 4, 8);
        root.Controls.Add(_databaseLabel, 0, 1);
        _captureSummary.Padding = new Padding(4, 2, 4, 8);
        root.Controls.Add(_captureSummary, 0, 2);
        var body = new TableLayoutPanel { Name = "ViewerBody", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        // The implicit row is AutoSize and can retain the children's preferred
        // height after a DPI/font change, overflowing the allocated body cell.
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 3);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 0, 16, 0), Margin = Padding.Empty };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var listHeading = Label("SessionsHeading", "已保存记录", Ink);
        listHeading.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        left.Controls.Add(listHeading, 0, 0);
        _sessions.Columns.Add(Column("Name", "记录", 150));
        _sessions.Columns.Add(Column("State", "状态", 90));
        _sessions.Columns.Add(Column("Started", "开始时间", 120));
        _sessions.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        left.Controls.Add(_sessions, 0, 1);
        var listNote = Label("SessionsNote", "选择一条记录查看详情。\nUnknown 表示证据不足，不等于危险。", Muted);
        listNote.Padding = new Padding(2, 12, 0, 0);
        left.Controls.Add(listNote, 0, 2);
        body.Controls.Add(left, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _sessionTitle.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
        _sessionTitle.AutoEllipsis = true;
        _sessionTitle.Padding = new Padding(0, 0, 0, 4);
        _summaryStatus.Padding = new Padding(0, 0, 0, 8);
        right.Controls.Add(_sessionTitle, 0, 0);
        right.Controls.Add(_summaryStatus, 0, 1);
        right.Controls.Add(_tabs, 0, 2);
        body.Controls.Add(right, 1, 0);
        var summary = new TabPage("记录概览") { Name = "OverviewTab", BackColor = Color.White, Padding = new Padding(16) };
        summary.Controls.Add(_overview);
        _tabs.TabPages.Add(summary);
        var changeTab = new TabPage("状态变化") { Name = "ChangesTab", BackColor = Color.White, Padding = new Padding(12), AutoScroll = true };
        var changeLayout = Rows(0, 0, -75, -25, 0);
        changeLayout.Name = "ChangesLayout";
        changeLayout.MinimumSize = new Size(0, 340);
        changeLayout.Controls.Add(_changeFilter, 0, 0);
        changeLayout.Controls.Add(Label("ChangesScope", "筛选本记录全部状态变化，按页显示。状态差异与源事件数量不同；变化不自动属于目标程序。", Muted), 0, 1);
        _changes.Columns.Add(Column("Kind", "类别", 90));
        _changes.Columns.Add(Column("Operation", "变化", 100));
        _changes.Columns.Add(Column("Resource", "记录名称 / 位置", 320));
        _changes.Columns.Add(Column("Confidence", "归属证据", 110));
        changeLayout.Controls.Add(_changes, 0, 2);
        _changeDetails.Text = "选择一项变化查看记录的依据。";
        changeLayout.Controls.Add(_changeDetails, 0, 3);
        changeLayout.Controls.Add(Pager(_previousChanges, _changesPage, _nextChanges), 0, 4);
        AddScrollableContent(changeTab, changeLayout);
        _tabs.TabPages.Add(changeTab);
        var eventsTab = new TabPage("事件与依据") { Name = "EventsTab", BackColor = Color.White, Padding = new Padding(12), AutoScroll = true };
        var eventLayout = Rows(0, 0, -45, 0, 0, -55);
        eventLayout.Name = "EventsLayout";
        eventLayout.MinimumSize = new Size(0, 400);
        eventLayout.Controls.Add(_eventFilter, 0, 0);
        eventLayout.Controls.Add(Label("EventsScope", $"仅筛选当前页（最多 {_pageSize} 条），不会搜索其他页。报告包含：本记录全部状态变化、当前未筛选事件页、所选事件解释。", Muted), 0, 1);
        _events.Columns.Add(Column("Time", "观测时间", 175));
        _events.Columns.Add(Column("Operation", "操作", 145));
        _events.Columns.Add(Column("Resource", "记录名称 / 资源", 250));
        _events.Columns.Add(Column("Confidence", "应用关联", 90));
        eventLayout.Controls.Add(_events, 0, 2);
        eventLayout.Controls.Add(Pager(_previousEvents, _eventsPage, _nextEvents), 0, 3);
        var detailTools = new FlowLayoutPanel { Name = "EventDetailTools", Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = Padding.Empty };
        _copyEvent.Dock = DockStyle.None;
        _copyEvent.Margin = new Padding(0, 3, 12, 3);
        _rawToggle.Margin = new Padding(0, 8, 0, 3);
        detailTools.Controls.Add(_copyEvent);
        detailTools.Controls.Add(_rawToggle);
        eventLayout.Controls.Add(detailTools, 0, 4);
        _detailHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _detailHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _detailHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _rawDetails.Font = new Font("Consolas", 9F);
        _detailHost.Controls.Add(_eventDetails, 0, 0);
        _detailHost.Controls.Add(_rawDetails, 0, 1);
        ShowEventDetails(false);
        eventLayout.Controls.Add(_detailHost, 0, 5);
        // Fixed logical minima do not account for wrapped native grid headers or
        // preferred-height action rows at a real monitor DPI. Reserve enough of
        // the 45% grid row for a complete header and readable data row; let the
        // existing tab viewport scroll the resulting content extent.
        var sizingEvents = false;
        eventLayout.Layout += (_, _) =>
        {
            if (sizingEvents) return;
            sizingEvents = true;
            try
            {
                var heights = eventLayout.GetRowHeights();
                if (heights.Length != 6) return;
                var automaticRows = heights[0] + heights[1] + heights[3] + heights[4];
                var gridRow = _events.ColumnHeadersHeight + Math.Max(_events.Font.Height * 2,
                    _events.RowTemplate.Height + _events.DefaultCellStyle.Padding.Vertical) + _events.Margin.Vertical;
                var explanation = _eventDetails.Font.Height * 3 + _detailHost.Margin.Vertical;
                var remaining = (int)Math.Ceiling(Math.Max(gridRow / 0.45, explanation / 0.55)) + 8;
                var required = automaticRows + remaining + eventLayout.Padding.Vertical;
                if (eventLayout.MinimumSize.Height < required)
                    eventLayout.MinimumSize = new Size(eventLayout.MinimumSize.Width, required);
            }
            finally { sizingEvents = false; }
        };
        _events.ColumnHeadersHeightChanged += (_, _) => eventLayout.PerformLayout();
        AddScrollableContent(eventsTab, eventLayout);
        _tabs.TabPages.Add(eventsTab);
        _status.AutoEllipsis = true;
        _status.Padding = new Padding(4, 6, 4, 0);
        root.Controls.Add(_status, 0, 4);
        _tips.SetToolTip(_export, "导出本记录全部状态变化、当前未筛选的事件页和所选事件解释。筛选框不会改变报告范围。报告不是全部事件历史。");
    }

    private void WireEvents()
    {
        Shown += (_, _) => FitWorkingArea();
        DpiChanged += (_, _) => FitWorkingArea();
        Resize += (_, _) => { if (IsHandleCreated) FitWorkingArea(); };
        _newInstall.Click += (_, _) =>
        {
            if (ActiveInstallForm is { IsDisposed: false })
            {
                if (!ActiveInstallForm.Visible) ActiveInstallForm.Show(this);
                ActiveInstallForm.Activate();
                return;
            }
            ActiveInstallForm = new InstallForm(new InstallClient(_client.ExecutablePath), OpenCapturedSessionAsync, _installDialogs);
            ActiveInstallForm.RecordingEnded += result =>
            {
                var state = result.LastStatus;
                _lastRun = result;
                _captureSummary.Text = $"最近任务：安装器退出 {(state?.InstallerExited == true ? state.InstallerExitCode?.ToString() ?? "未知" : "未确认")} · 记录{(result.Succeeded ? "已完成" : "失败或未完成")} · 保存{(state?.DataSaved == true ? "已确认" : "未确认")} · 覆盖 {state?.QualityLevel ?? "未确认"}。点击查看任务详情。";
                _captureSummary.LinkArea = new LinkArea(0, _captureSummary.Text.Length);
                _tips.SetToolTip(_captureSummary, "任务详情保留最后确认阶段、采集进程退出结果和诊断。安装器退出结果与记录完成分别判断。");
                UpdateEnabled();
            };
            ActiveInstallForm.FormClosed += (_, _) => { ActiveInstallForm = null; UpdateEnabled(); };
            ActiveInstallForm.Show(this);
            UpdateEnabled();
        };
        _captureSummary.LinkClicked += (_, _) => ShowLastTaskDetails();
        _open.Click += (_, _) =>
        {
            var path = _dialogs.ChooseDatabase(this);
            if (path is not null) StartOperation(token => OpenDatabaseAsync(path, token));
        };
        _sessions.SelectionChanged += (_, _) =>
        {
            if (_rendering || IsBusy || _sessions.SelectedRows.Count == 0) return;
            var record = (JsonElement)_sessions.SelectedRows[0].Tag!;
            StartOperation(token => OpenRecordAsync(record, token));
        };
        _events.SelectionChanged += (_, _) =>
        {
            if (_rendering || IsBusy || _events.SelectedRows.Count == 0) return;
            var item = (JsonElement)_events.SelectedRows[0].Tag!;
            StartOperation(token => ExplainAsync(item, token));
        };
        _changes.SelectionChanged += (_, _) =>
        {
            if (_rendering || _changes.SelectedRows.Count == 0) return;
            var change = (JsonElement)_changes.SelectedRows[0].Tag!;
            _changeDetails.Text = $"依据：{FieldText(change, "reason")}\r\n归属：{Confidence(FieldText(change, "confidence"))}\r\n状态变化不建立最终所有权，也不是删除许可。";
        };
        _previousEvents.Click += (_, _) => ChangeEventPage(Math.Max(0, _eventOffset - _pageSize));
        _nextEvents.Click += (_, _) => ChangeEventPage(checked(_eventOffset + _pageSize));
        _eventFilter.TextChanged += (_, _) => { if (!_rendering) RenderEvents(); };
        _changeFilter.TextChanged += (_, _) => { _changeOffset = 0; RenderChanges(); };
        _previousChanges.Click += (_, _) => { _changeOffset = Math.Max(0, _changeOffset - _pageSize); RenderChanges(); };
        _nextChanges.Click += (_, _) => { _changeOffset += _pageSize; RenderChanges(); };
        _rawToggle.CheckedChanged += (_, _) => ShowEventDetails(_rawToggle.Checked);
        _copyEvent.Click += (_, _) =>
        {
            if (_selectedEventId is null) return;
            try { Clipboard.SetText(_selectedEventId); SetStatus("事件 ID 已复制。", false); }
            catch (System.Runtime.InteropServices.ExternalException) { SetStatus("剪贴板正忙，请稍后重试。事件 ID 仍显示在详情中。", true); }
        };
        _export.Click += (_, _) =>
        {
            if (_database is null || _recordId is null) return;
            var path = _dialogs.ChooseReport(this, $"Contain-report-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            if (path is null) return;
            var db = _database;
            var id = _recordId;
            var eventId = _selectedEventId;
            var offset = _eventOffset;
            StartOperation(async token =>
            {
                await ReportExporter.ExportAsync(_client, db, id, eventId, _pageSize, offset, path, token);
                token.ThrowIfCancellationRequested();
                SetStatus($"已导出本地报告：{path}。范围：本记录全部状态变化、当前未筛选事件页、所选事件解释；不包含全部事件历史。", false);
            });
        };
        FormClosed += (_, _) => { _operationCancellation?.Cancel(); _tips.Dispose(); };
        FormClosing += (_, args) =>
        {
            if (ActiveInstallForm is not { IsActive: true } && ActiveInstallForm is not { IsChecking: true }) return;
            args.Cancel = true;
            ActiveInstallForm.Activate();
            SetStatus("记录任务仍在运行，窗口保持打开。请等待采集进程退出；不会强制终止安装器。", true);
        };
    }

    public async Task OpenCapturedSessionAsync(string database, string sessionId)
    {
        Exception? failure = null;
        StartOperation(async token =>
        {
            try
            {
                await OpenDatabaseAsync(database, token);
                var matches = _sessions.Rows.Cast<DataGridViewRow>().Where(row => row.Tag is JsonElement value && FieldText(value, "id") == sessionId).ToList();
                if (matches.Count != 1) throw new InvalidDataException("数据库中未找到本次启动的准确会话；未选择其他记录替代。已保存的记录仍可从列表检查。");
                await OpenRecordAsync((JsonElement)matches[0].Tag!, token);
                _rendering = true;
                try { matches[0].Selected = true; }
                finally { _rendering = false; }
            }
            catch (Exception error) { failure = error; throw; }
        });
        await PendingOperation;
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task OpenDatabaseAsync(string path, CancellationToken token)
    {
        _database = null;
        ResetRecord();
        _rendering = true;
        try { _sessions.Rows.Clear(); }
        finally { _rendering = false; }
        _databaseLabel.Text = "正在打开：" + path;
        _tips.SetToolTip(_databaseLabel, path);
        var result = await _client.ListAsync(path, token);
        token.ThrowIfCancellationRequested();
        _database = Path.GetFullPath(path);
        _databaseLabel.Text = "当前数据库：" + _database;
        _tips.SetToolTip(_databaseLabel, _database);
        _rendering = true;
        try
        {
            foreach (var record in Array(result.Data))
            {
                var index = _sessions.Rows.Add(FieldText(record, "name"), State(FieldText(record, "capture_state")), FieldText(record, "started_at"));
                _sessions.Rows[index].Tag = record.Clone();
            }
            _sessions.ClearSelection();
            _sessions.CurrentCell = null;
        }
        finally { _rendering = false; }
        _overview.Text = _sessions.Rows.Count == 0
            ? "这是一个有效的空数据库。\r\n\r\n没有已保存的记录。本查看器不会自动添加演示数据，也不会启动采集。"
            : "请从左侧选择一条已保存记录。\r\n\r\n查看器只读取数据库，不启动安装器或采集，也不执行删除。\r\n\r\n导出报告仅写入您选择的新本地文件。";
        SetStatus($"数据库已打开 · {_sessions.Rows.Count} 条记录 · 原数据库未迁移。", false);
    }

    private async Task OpenRecordAsync(JsonElement record, CancellationToken token)
    {
        var db = _database ?? throw new InvalidOperationException("请先打开数据库。");
        ResetRecord();
        var id = FieldText(record, "id");
        var summary = await _client.SummaryAsync(db, id, token);
        var changes = await _client.DiffAsync(db, id, token);
        token.ThrowIfCancellationRequested();
        _recordId = id;
        _sessionTitle.Text = FieldText(summary.Data, "name");
        _summaryStatus.Text = $"安装器退出：{FieldText(summary.Data, "exit_code")}  ·  记录：{State(FieldText(summary.Data, "capture_state"))}  ·  保存：已读取  ·  覆盖：{Quality(FieldText(Field(summary.Data, "quality"), "level"))}";
        _overview.Text = OverviewText(summary.Data);
        _allChanges = [];
        foreach (var item in Array(Field(changes.Data, "files"))) _allChanges.Add(("文件", item.Clone()));
        foreach (var item in Array(Field(changes.Data, "registry"))) _allChanges.Add(("注册表", item.Clone()));
        foreach (var item in Array(Field(changes.Data, "inventory"))) _allChanges.Add(("配置清单", item.Clone()));
        RenderChanges();
        await LoadEventsAsync(0, token);
        token.ThrowIfCancellationRequested();
        SetStatus("记录已加载。状态差异、源操作和最终所有权是不同判断；可在“事件与依据”中选择一条事件。", false);
    }

    private void ChangeEventPage(long offset)
    {
        if (_recordId is null || _database is null || IsBusy) return;
        StartOperation(async token =>
        {
            await LoadEventsAsync(offset, token);
            token.ThrowIfCancellationRequested();
            SetStatus("事件页已加载。筛选仅作用于当前页，点击事件查看依据。", false);
        });
    }

    private async Task LoadEventsAsync(long offset, CancellationToken token)
    {
        var history = await _client.HistoryAsync(_database!, _recordId!, _pageSize + 1, offset, token);
        token.ThrowIfCancellationRequested();
        var rows = Array(Field(history.Data, "events")).ToList();
        _eventOffset = offset;
        _moreEvents = rows.Count > _pageSize;
        _pageEvents = rows.Take(_pageSize).Select(row => row.Clone()).ToList();
        _rendering = true;
        try { _eventFilter.Clear(); }
        finally { _rendering = false; }
        RenderEvents();
    }

    private async Task ExplainAsync(JsonElement item, CancellationToken token)
    {
        ClearExplanation();
        var eventId = FieldText(item, "id");
        var explanation = await _client.ExplainAsync(_database!, eventId, token);
        token.ThrowIfCancellationRequested();
        var explained = Field(explanation.Data, "event");
        if (FieldText(explanation.Data, "app_id") != _recordId || FieldText(explained, "id") != eventId || !JsonView.JsonEqual(item, explained))
            throw new InvalidDataException("事件详情与当前记录或当前页不一致；未展示可能误关联的依据。请重新打开记录。");
        _selectedEventId = eventId;
        _eventDetails.Text = ExplainText(explanation.Data);
        _rawDetails.Text = Pretty(explanation.Envelope);
        SetStatus("已读取此事件的生产 explain 结果。High 表示源证据关联，不表示安全删除。", false);
    }

    private void RenderEvents()
    {
        var filter = _eventFilter.Text.Trim();
        _rendering = true;
        try
        {
            _events.Rows.Clear();
            foreach (var item in _pageEvents.Where(item => Matches(item, filter, "operation", "resource", "confidence", "id", "event_type")))
            {
                var index = _events.Rows.Add(FieldText(item, "timestamp"), FieldText(item, "operation"), FieldText(item, "resource"), Confidence(FieldText(item, "confidence")));
                _events.Rows[index].Tag = item;
            }
            _events.ClearSelection();
            _events.CurrentCell = null;
        }
        finally { _rendering = false; }
        ClearExplanation();
        _eventsPage.Text = _recordId is null ? "尚未加载事件" : _pageEvents.Count == 0
            ? $"偏移 {_eventOffset} · 此页没有事件"
            : $"偏移 {_eventOffset} · 本页 {_pageEvents.Count} 条 / 筛选后 {_events.Rows.Count} 条";
        if (_events.Rows.Count == 0 && _pageEvents.Count > 0) _eventDetails.Text = "当前页没有符合筛选的事件。可清空筛选或翻页；尚未搜索其他页面。";
        UpdateEnabled();
    }

    private void RenderChanges()
    {
        var filter = _changeFilter.Text.Trim();
        var selected = _allChanges.Where(row => Matches(row.Data, filter, "operation", "path", "key", "name", "confidence") || (!string.IsNullOrEmpty(filter) && row.Kind.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        _changeOffset = Math.Min(_changeOffset, selected.Count == 0 ? 0 : ((selected.Count - 1) / _pageSize) * _pageSize);
        _moreChanges = selected.Count > _changeOffset + _pageSize;
        _rendering = true;
        try
        {
            _changes.Rows.Clear();
            foreach (var row in selected.Skip(_changeOffset).Take(_pageSize))
            {
                var resource = row.Kind == "文件" ? FieldText(row.Data, "path") : row.Kind == "注册表" ? FieldText(row.Data, "key") + "\\" + FieldText(row.Data, "name") : FieldText(row.Data, "name");
                var index = _changes.Rows.Add(row.Kind, FieldText(row.Data, "operation"), resource, Confidence(FieldText(row.Data, "confidence")));
                _changes.Rows[index].Tag = row.Data;
            }
            _changes.ClearSelection();
            _changes.CurrentCell = null;
        }
        finally { _rendering = false; }
        _changesPage.Text = _recordId is null ? "尚未加载变化" : $"全部 {_allChanges.Count} 项 · 筛选后 {selected.Count} 项 · 本页 {_changes.Rows.Count} 项";
        _changeDetails.Text = selected.Count == 0 ? "没有符合当前筛选的状态变化。未观察到不等于没有发生。" : "选择一项变化查看记录的依据。前后状态变化不自动建立归属。";
        UpdateEnabled();
    }

    private void ResetRecord()
    {
        _recordId = null;
        _eventOffset = 0;
        _changeOffset = 0;
        _moreEvents = false;
        _moreChanges = false;
        _pageEvents = [];
        _allChanges = [];
        _sessionTitle.Text = "打开一份记录，了解它留下的证据";
        _summaryStatus.Text = "只读查看 · 不修改原数据库 · 不判断安全删除";
        _overview.Text = "打开已有 Contain 数据库，然后选择一条记录。\r\n\r\n记录列表、变化和事件均来自实际 CLI 查询。没有数据库时不会生成演示记录。";
        _rendering = true;
        try { _eventFilter.Clear(); _changeFilter.Clear(); }
        finally { _rendering = false; }
        RenderChanges();
        RenderEvents();
    }

    private void ClearExplanation()
    {
        _selectedEventId = null;
        _eventDetails.Text = "选择当前页中的一条事件，查看操作、执行进程和名称来源的证据。\r\n\r\n未知不等于危险；High 不代表最终所有权或安全删除。";
        _rawDetails.Clear();
        _rawToggle.Checked = false;
    }

    private void StartOperation(Func<CancellationToken, Task> operation)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var version = ++_operationVersion;
        PendingOperation = RunOperationAsync(operation, _operationCancellation.Token, version);
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, CancellationToken token, long version)
    {
        IsBusy = true;
        UpdateEnabled();
        SetStatus("正在读取本地记录…", false);
        try { await operation(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version == _operationVersion && !IsDisposed)
            {
                if (_database is null) _databaseLabel.Text = "未打开数据库：" + _tips.GetToolTip(_databaseLabel);
                SetStatus("未完成：" + error.Message, true);
            }
        }
        finally
        {
            if (version == _operationVersion && !IsDisposed)
            {
                IsBusy = false;
                UpdateEnabled();
            }
        }
    }

    private void UpdateEnabled()
    {
        _newInstall.Enabled = !IsBusy;
        _newInstall.Text = ActiveInstallForm is null ? "新建安装记录" : "本次任务状态";
        _sessions.Enabled = !IsBusy && _database is not null;
        // Keep tab-page ownership and visibility stable during read-only work.
        // Disabling the entire tab tree also disables inactive page windows;
        // only the controls that can start another query need to be gated.
        _events.Enabled = !IsBusy;
        _changes.Enabled = !IsBusy;
        _eventFilter.Enabled = !IsBusy;
        _changeFilter.Enabled = !IsBusy;
        _export.Enabled = !IsBusy && _database is not null && _recordId is not null;
        _previousEvents.Enabled = !IsBusy && _recordId is not null && _eventOffset > 0;
        _nextEvents.Enabled = !IsBusy && _recordId is not null && _moreEvents;
        _previousChanges.Enabled = !IsBusy && _recordId is not null && _changeOffset > 0;
        _nextChanges.Enabled = !IsBusy && _recordId is not null && _moreChanges;
        _copyEvent.Enabled = !IsBusy && _selectedEventId is not null;
        _rawToggle.Enabled = !IsBusy && _selectedEventId is not null;
    }

    private void SetStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? Color.FromArgb(164, 43, 35) : Muted;
        _tips.SetToolTip(_status, message);
    }

    private void FitWorkingArea()
    {
        var area = Screen.FromControl(this).WorkingArea;
        var minimum = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        if (MinimumSize != minimum) MinimumSize = minimum;
        if (WindowState == FormWindowState.Normal && (Width > area.Width || Height > area.Height))
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
    }

    private void ShowEventDetails(bool raw)
    {
        // Each detail view owns a separate row. The inactive row is collapsed,
        // rather than stacking two Dock.Fill native text windows over one another.
        _detailHost.SuspendLayout();
        try
        {
            _detailHost.RowStyles[0].SizeType = raw ? SizeType.Absolute : SizeType.Percent;
            _detailHost.RowStyles[0].Height = raw ? 0 : 100;
            _detailHost.RowStyles[1].SizeType = raw ? SizeType.Percent : SizeType.Absolute;
            _detailHost.RowStyles[1].Height = raw ? 100 : 0;
            _eventDetails.Visible = !raw;
            _rawDetails.Visible = raw;
        }
        finally { _detailHost.ResumeLayout(true); }
    }

    private void ShowLastTaskDetails()
    {
        if (_lastRun is not { } result) return;
        var state = result.LastStatus;
        var details = new StringBuilder()
            .AppendLine($"安装器退出结果：{(state?.InstallerExited == true ? state.InstallerExitCode?.ToString() ?? "未提供退出码" : "未确认退出")}")
            .AppendLine("非零退出码的含义由安装器定义；不会自动判断为用户取消。")
            .AppendLine($"记录完成：{(result.Succeeded ? "已确认" : "失败或未完成")}")
            .AppendLine($"数据保存：{(state?.DataSaved == true ? "已确认保存过证据；不代表记录完整或安装成功" : "未确认；不会删除已有记录")}")
            .AppendLine($"覆盖质量：{state?.QualityLevel ?? "未确认"}")
            .AppendLine($"最后确认阶段：{result.LastConfirmedStage ?? state?.Stage ?? "未取得结构化状态"}")
            .AppendLine($"后端最终状态：{state?.Stage ?? "未取得结构化状态"}")
            .AppendLine($"会话 ID：{state?.SessionId ?? "未确认"}")
            .AppendLine($"数据库：{state?.Database ?? "未取得结构化状态"}")
            .AppendLine().AppendLine("高级诊断（保留在本机）")
            .AppendLine($"采集进程 PID：{result.CollectorPid?.ToString() ?? "未确认"}")
            .AppendLine($"采集进程退出码：{result.CollectorExitCode}")
            .AppendLine($"采集来源已停止：{state?.CaptureFinished.ToString() ?? "未确认"}")
            .AppendLine($"实际 ETW 来源：进程 {state?.EtwProcess ?? "未确认"} / 文件 {state?.EtwFile ?? "未确认"} / 注册表 {state?.EtwRegistry ?? "未确认"}")
            .AppendLine($"结构化状态错误：{result.ProtocolError ?? "未报告"}")
            .AppendLine($"后端错误：{state?.Error ?? "未报告"}");
        foreach (var reason in state?.QualityReasons ?? []) details.AppendLine("• " + reason);
        details.AppendLine().AppendLine("采集进程诊断输出：").AppendLine(string.IsNullOrEmpty(result.Diagnostics) ? "未提供" : result.Diagnostics);
        if (result.DiagnosticsTruncated) details.AppendLine("诊断过长，仅保留有界输出；此标记不表示记录成功。");
        using var window = new Form
        {
            Name = "LastTaskDetails", Text = "Contain · 最近任务详情", Font = Font,
            AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(760, 560), MinimizeBox = false, StartPosition = FormStartPosition.CenterParent
        };
        var content = ReadText("LastTaskDiagnostics");
        content.Text = details.ToString();
        content.Padding = new Padding(12);
        window.Controls.Add(content);
        window.Shown += (_, _) =>
        {
            var area = Screen.FromControl(window).WorkingArea;
            window.Size = new Size(Math.Min(window.Width, area.Width), Math.Min(window.Height, area.Height));
            window.Location = new Point(area.Left + (area.Width - window.Width) / 2, area.Top + (area.Height - window.Height) / 2);
        };
        window.ShowDialog(this);
    }

    private static string OverviewText(JsonElement data)
    {
        var text = new StringBuilder();
        var counts = Field(data, "summary_counts");
        var backend = Field(data, "backend");
        var quality = Field(data, "quality");
        var captureState = FieldText(data, "capture_state");
        var exitCode = Field(data, "exit_code");
        var exitResult = exitCode.ValueKind == JsonValueKind.Number && exitCode.TryGetInt32(out var code)
            ? code == 0 ? "退出码 0（安装器报告成功，未验证最终安装状态）" : $"退出码 {code}（非零；具体含义由安装器定义）"
            : "未确认（记录没有提供退出码）";
        text.AppendLine("本次记录结果")
            .AppendLine($"安装器退出结果：{exitResult}")
            .AppendLine($"记录完成：{(captureState == "finished" ? "已确认结束" : "未确认完整结束")}；数据保存：已从数据库读到记录。保存不代表安装成功或记录完整。")
            .AppendLine($"观察到的状态变化：文件 {Count(Field(data, "files"))} 项 / 注册表值 {Count(Field(data, "registry"))} 项 / 配置清单 {Count(Field(data, "inventory"))} 项。")
            .AppendLine($"覆盖质量：{Quality(FieldText(quality, "level"))}")
            .AppendLine(FieldText(quality, "level") == "Incomplete" ? "覆盖限制：证据不完整。数据库未确认结束时，不能仅凭当前阶段确定中断根因。" : "覆盖限制：仅观察所选范围；实际来源启用也不能证明完整软件足迹或最终文件归属。")
            .AppendLine().AppendLine("观察对象与范围").AppendLine($"程序：{FieldText(data, "name")}")
            .AppendLine($"安装程序：{FieldText(data, "installer")}")
            .AppendLine($"开始：{FieldText(data, "started_at")}")
            .AppendLine($"记录结束时间：{FieldText(data, "finished_at")}")
            .AppendLine($"记录状态：{State(captureState)}")
            .AppendLine($"观察目录：{Join(Field(data, "watch_roots"))}")
            .AppendLine($"注册表范围：{FieldText(data, "registry_key")}").AppendLine()
            .AppendLine("高级诊断与原始依据")
            .AppendLine("实际来源（active 表示启用，不证明覆盖完整）")
            .AppendLine($"ETW 进程：{FieldText(backend, "etw_process")}  ·  文件：{FieldText(backend, "etw_file")}  ·  注册表：{FieldText(backend, "etw_registry")}").AppendLine()
            .AppendLine("记录内容（不同口径分别统计）")
            .AppendLine($"时间线观测：{FieldText(counts, "events_retained")}  ·  规范化源操作：{FieldText(counts, "events_normalized")}")
            .AppendLine($"源进程已知：{FieldText(counts, "known_actor_events")}  ·  未知：{FieldText(counts, "unknown_actor_events")}")
            .AppendLine($"状态差异：文件 {Count(Field(data, "files"))} 项 / 注册表值 {Count(Field(data, "registry"))} 项 / 配置清单 {Count(Field(data, "inventory"))} 项").AppendLine()
            .AppendLine("覆盖与缺失")
            .AppendLine($"覆盖质量：{Quality(FieldText(quality, "level"))}")
            .AppendLine($"ETW 丢失事件：{FieldText(backend, "etw_events_lost")}  ·  丢失缓冲区：{FieldText(backend, "etw_buffers_lost")}")
            .AppendLine($"应用丢弃：{FieldText(counts, "application_records_dropped")}  ·  解码错误：{FieldText(counts, "decode_errors")}")
            .AppendLine($"快照缺口：{FieldText(counts, "snapshot_gaps")}  ·  通知缺口：{FieldText(counts, "notification_gaps")}  ·  排空超时：{FieldText(counts, "drain_timed_out")}");
        foreach (var reason in Array(Field(quality, "reasons")).Concat(Array(Field(data, "warnings"))).Concat(Array(Field(backend, "warnings"))).Select(Value).Distinct()) text.AppendLine("• " + reason);
        text.AppendLine().AppendLine("进程证据（身份由 PID 与创建时间共同标识）");
        var processes = Array(Field(data, "processes")).ToList();
        foreach (var process in processes.Take(20)) text.AppendLine($"• {FieldText(process, "image")} · PID {FieldText(process, "pid")} · 创建时间 {FieldText(process, "creation_time")} · {Confidence(FieldText(process, "confidence"))}");
        if (processes.Count == 0) text.AppendLine("没有可用的进程记录。");
        if (processes.Count > 20) text.AppendLine($"另有 {processes.Count - 20} 条进程记录；完整原始概览包含在导出的本地报告中。");
        text.AppendLine().AppendLine("会话结束不等于覆盖完整。状态变化不建立最终所有权；历史名称也不等于当前路径。");
        return text.ToString();
    }

    private static string ExplainText(JsonElement data)
    {
        var item = Field(data, "event");
        var evidence = Field(item, "evidence");
        var name = Field(Field(item, "raw"), "file_name");
        var source = FieldText(name, "path_source");
        var text = new StringBuilder();
        text.AppendLine($"事件 ID：{FieldText(item, "id")}")
            .AppendLine($"操作：{FieldText(item, "operation")}  ·  来源：{FieldText(evidence, "source")}")
            .AppendLine($"观测时间：{FieldText(item, "timestamp")}")
            .AppendLine($"记录名称 / 资源：{FieldText(item, "resource")}")
            .AppendLine($"执行进程：{FieldText(evidence, "process_image")}  ·  PID {FieldText(evidence, "pid")}")
            .AppendLine($"进程创建时间（精确 FILETIME）：{FieldText(evidence, "process_creation_time")}")
            .AppendLine($"应用关联：{Confidence(FieldText(item, "confidence"))}  ·  请求结果：{FieldText(item, "success")}")
            .AppendLine("执行进程关联描述谁发出了这次请求；它不判断最终文件归属。请求完成也不自动验证前后状态变化。")
            .AppendLine($"名称来源：{source}");
        text.AppendLine(source == "file_object_cache" ? "名称边界：历史缓存名称，不是已验证的当前路径。" : "名称边界：保留所记录的名称；不能推导现在的路径或最终所有权。");
        text.AppendLine($"名称观察时间（精确 FILETIME）：{FieldText(name, "observed_ticks")}")
            .AppendLine($"依据 / 未知原因：{FieldText(item, "reason")}").AppendLine();
        foreach (var edge in Array(Field(data, "edges"))) text.AppendLine($"• {FieldText(edge, "relation")} [{FieldText(edge, "confidence")}]：{FieldText(edge, "reason")}");
        text.AppendLine().AppendLine("原生请求不等于显式应用 API 调用。High 不是删除许可；Unknown 表示当前证据无法确定。");
        return text.ToString();
    }

    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string FieldText(JsonElement value, string name) => Value(Field(value, name));
    private static string Value(JsonElement value) => value.ValueKind switch { JsonValueKind.String => string.IsNullOrEmpty(value.GetString()) ? Missing : value.GetString()!, JsonValueKind.Null or JsonValueKind.Undefined => Missing, _ => value.GetRawText() };
    private static string Count(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture) : Missing;
    private static string Join(JsonElement value) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 ? string.Join("；", value.EnumerateArray().Select(Value)) : Missing;
    private static bool Matches(JsonElement value, string filter, params string[] fields) => string.IsNullOrEmpty(filter) || fields.Any(field => FieldText(value, field).Contains(filter, StringComparison.OrdinalIgnoreCase));
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    private static string Confidence(string value) => value == "Unknown" ? "Unknown · 未确定" : value;
    private static string State(string value) => value switch { "finished" => "已结束", "capturing" => "未结束（capturing）", "finalizing" => "未结束（finalizing）", "failed" => "中断 / 失败（failed）", Missing => "未知（旧记录未提供）", _ => "未知状态：" + value };
    private static string Quality(string value) => value switch { "Degraded" => "Degraded · 覆盖受限", "Incomplete" => "Incomplete · 证据不完整", "Complete" => "Complete（记录的标签，不是所有权保证）", _ => value };

    private static TableLayoutPanel Rows(params float[] sizes)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = sizes.Length };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var size in sizes) panel.RowStyles.Add(size == 0 ? new RowStyle(SizeType.AutoSize) : new RowStyle(size < 0 ? SizeType.Percent : SizeType.Absolute, Math.Abs(size)));
        return panel;
    }
    private static void AddScrollableContent(ScrollableControl viewport, Control content)
    {
        // A real content extent gives the viewport ownership of scrolling.
        // Dock.Fill can overflow at a scaled minimum without extending scroll
        // bounds, and AutoScrollMinSize alone is not scaled by Control.Scale.
        content.Dock = DockStyle.None;
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        content.Margin = Padding.Empty;
        content.Location = new Point(viewport.Padding.Left, viewport.Padding.Top);
        viewport.Controls.Add(content);
        var sizing = false;
        void FitContent()
        {
            if (sizing) return;
            sizing = true;
            try
            {
                var padding = viewport.Padding;
                var minimum = content.MinimumSize;
                var extent = new Size(minimum.Width + padding.Horizontal, minimum.Height + padding.Vertical);
                if (viewport.AutoScrollMinSize != extent) viewport.AutoScrollMinSize = extent;
                var size = new Size(Math.Max(minimum.Width, viewport.ClientSize.Width - padding.Horizontal),
                    Math.Max(minimum.Height, viewport.ClientSize.Height - padding.Vertical));
                if (content.Size != size) content.Size = size;
            }
            finally { sizing = false; }
        }
        viewport.ClientSizeChanged += (_, _) => FitContent();
        viewport.Layout += (_, _) => FitContent();
        FitContent();
    }
    private static Control Pager(Button previous, Label page, Button next)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        page.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(previous, 0, 0); layout.Controls.Add(page, 1, 0); layout.Controls.Add(next, 2, 0);
        return layout;
    }
    private static Label Label(string name, string text, Color color) => new() { Name = name, Text = text, UseMnemonic = false, ForeColor = color, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true };
    private static Button Button(string name, string text, bool primary = false) => new()
    {
        Name = name, Text = text, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(10, 6, 10, 6), Margin = new Padding(4, 3, 4, 3),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White,
        ForeColor = primary ? Color.White : Ink, UseVisualStyleBackColor = false
    };
    private static TextBox ReadText(string name) => new()
    {
        Name = name, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
        BackColor = Color.White, ForeColor = Ink, WordWrap = true
    };
    private static DataGridViewTextBoxColumn Column(string name, string title, int width) => new() { Name = name, HeaderText = title, Width = width, FillWeight = width, SortMode = DataGridViewColumnSortMode.NotSortable };
    private static DataGridView Grid(string name)
    {
        var grid = new DataGridView
        {
            Name = name, Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AutoGenerateColumns = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Color.FromArgb(234, 239, 243),
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false
        };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 241, 245);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 6, 4, 6);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 236, 239);
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.DefaultCellStyle.Padding = new Padding(4, 4, 4, 4);
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        return grid;
    }
}
