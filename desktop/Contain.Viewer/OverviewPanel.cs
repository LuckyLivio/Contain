namespace Contain.Viewer;

/// <summary>Compact saved-record results with a separately scrollable diagnostic view.</summary>
internal sealed class OverviewPanel : TableLayoutPanel
{
    private static readonly Color Ink = Color.FromArgb(28, 43, 60);
    private static readonly Color Muted = Color.FromArgb(92, 108, 123);
    private static readonly Color Warning = Color.FromArgb(148, 67, 18);
    private readonly TableLayoutPanel cards;
    private readonly TableLayoutPanel document;
    private readonly Panel viewport;
    private readonly TextBox details;
    private readonly Label installer = TextLabel("OverviewInstaller", true);
    private readonly Label installerNote = TextLabel("OverviewInstallerNote");
    private readonly Label record = TextLabel("OverviewRecord", true);
    private readonly Label recordNote = TextLabel("OverviewRecordNote");
    private readonly TextBox scope = new()
    {
        Name = "OverviewScope", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ink,
        ScrollBars = ScrollBars.Vertical, WordWrap = true
    };
    private readonly Label coverage = TextLabel("OverviewCoverage");
    private readonly Label changes = TextLabel("OverviewChanges", true);
    private readonly Label changesNote = TextLabel("OverviewChangesNote");
    private readonly TextBox alert = new()
    {
        Name = "OverviewAlert", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(255, 248, 234),
        ForeColor = Warning, ScrollBars = ScrollBars.Vertical, WordWrap = true,
        Margin = new Padding(3, 5, 3, 5)
    };
    private readonly FlowLayoutPanel actions;
    private readonly Button viewChanges = Action("ViewChanges", "查看变化");
    private readonly Button viewEvents = Action("ViewEvents", "查看事件与依据");
    private readonly Button toggleTechnical = Action("ToggleTechnical", "技术详情");
    private readonly Button copyScope = Action("CopyOverviewScope", "复制范围");
    private bool hasRecord;
    private bool expanded;
    private bool sizing;

    internal OverviewPanel(TextBox technicalDetails, Action changesAction, Action eventsAction, Action<string, bool> status)
    {
        Name = "OverviewPanel";
        Dock = DockStyle.Fill;
        ColumnCount = 1;
        RowCount = 2;
        Margin = Padding.Empty;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details = technicalDetails;
        details.Margin = new Padding(6, 6, 6, 6);
        viewport = new Panel { Name = "OverviewViewport", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        document = new TableLayoutPanel
        {
            Name = "OverviewDocument", ColumnCount = 1, RowCount = 3, Margin = Padding.Empty,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        document.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        document.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        document.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        document.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        cards = new TableLayoutPanel
        {
            Name = "OverviewCards", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Margin = Padding.Empty
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        cards.Controls.Add(Card("InstallerCard", "安装器结果", installer, installerNote), 0, 0);
        cards.Controls.Add(Card("RecordCard", "记录状态", record, recordNote), 1, 0);
        var scopeCard = Card("ScopeCard", "观察范围与覆盖", coverage, scope);
        scopeCard.RowCount = 4;
        scopeCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        copyScope.Dock = DockStyle.None;
        copyScope.Anchor = AnchorStyles.Left;
        copyScope.Padding = new Padding(5, 1, 5, 1);
        scopeCard.Controls.Add(copyScope, 0, 3);
        cards.Controls.Add(scopeCard, 0, 1);
        cards.Controls.Add(Card("ChangesCard", "主要变化", changes, changesNote), 1, 1);
        actions = new FlowLayoutPanel
        {
            Name = "OverviewActions", Dock = DockStyle.Fill, AutoSize = true,
            WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty
        };
        actions.Controls.Add(viewChanges);
        actions.Controls.Add(viewEvents);
        actions.Controls.Add(toggleTechnical);
        document.Controls.Add(cards, 0, 0);
        document.Controls.Add(alert, 0, 1);
        document.Controls.Add(details, 0, 2);
        viewport.Controls.Add(document);
        Controls.Add(viewport, 0, 0);
        Controls.Add(actions, 0, 1);
        viewChanges.Click += (_, _) => changesAction();
        viewEvents.Click += (_, _) => eventsAction();
        toggleTechnical.Click += (_, _) =>
        {
            expanded = !expanded;
            UpdateLayout();
            if (expanded) viewport.ScrollControlIntoView(details);
            else viewport.AutoScrollPosition = Point.Empty;
        };
        copyScope.Click += (_, _) =>
        {
            if (!hasRecord) return;
            try { Clipboard.SetText(scope.Text); status("完整观察范围已复制。", false); }
            catch (System.Runtime.InteropServices.ExternalException) { status("剪贴板正忙，可在观察范围中选中文字复制。", true); }
        };
        FontChanged += (_, _) => UpdateLayout();
        viewport.ClientSizeChanged += (_, _) => FitDocument();
        viewport.Layout += (_, _) => FitDocument();
        SetMessage();
    }

    internal void SetMessage()
    {
        hasRecord = false;
        expanded = false;
        viewport.AutoScrollPosition = Point.Empty;
        AccessibleDescription = null;
        installer.Text = installerNote.Text = record.Text = recordNote.Text = scope.Text = coverage.Text = changes.Text = changesNote.Text = alert.Text = "";
        UpdateLayout();
    }

    internal void SetRecord(OverviewPresentation model, string identity)
    {
        hasRecord = true;
        expanded = false;
        viewport.AutoScrollPosition = Point.Empty;
        AccessibleDescription = "session:" + identity;
        installer.Text = model.Installer;
        installerNote.Text = model.InstallerNote;
        record.Text = model.Record;
        record.ForeColor = model.IsUnfinished ? Warning : Ink;
        recordNote.Text = model.RecordNote;
        scope.Text = model.Scope;
        scope.SelectionStart = 0;
        coverage.Text = model.Coverage;
        changes.Text = model.Changes;
        changesNote.Text = model.ChangesNote;
        alert.Text = model.Alert;
        UpdateLayout();
    }

    internal void SetQueryEnabled(bool enabled)
    {
        viewChanges.Enabled = viewEvents.Enabled = hasRecord && enabled;
        toggleTechnical.Enabled = copyScope.Enabled = hasRecord;
    }

    private void UpdateLayout()
    {
        if (cards is null || actions is null) return;
        SuspendLayout();
        try
        {
            cards.Visible = hasRecord;
            actions.Visible = hasRecord;
            alert.Visible = hasRecord && alert.Text.Length > 0;
            details.Visible = !hasRecord || expanded;
            document.RowStyles[0].SizeType = hasRecord ? expanded ? SizeType.Absolute : SizeType.Percent : SizeType.Absolute;
            document.RowStyles[0].Height = hasRecord ? 100 : 0;
            document.RowStyles[1].Height = alert.Visible ? Font.Height * 3 + 12 : 0;
            document.RowStyles[2].SizeType = !hasRecord || expanded ? SizeType.Percent : SizeType.Absolute;
            document.RowStyles[2].Height = !hasRecord || expanded ? 100 : 0;
            toggleTechnical.Text = expanded ? "收起技术详情" : "技术详情";
            toggleTechnical.AccessibleDescription = expanded ? "已展开" : "已收起";
        }
        finally { ResumeLayout(true); }
        FitDocument();
    }

    private void FitDocument()
    {
        if (sizing || document is null || cards is null) return;
        sizing = true;
        try
        {
            var width = Math.Max(1, viewport.ClientSize.Width);
            var cardWidth = Math.Max(100, width / 2 - 30);
            int Lines(Control value) => value.GetPreferredSize(new Size(cardWidth, 0)).Height + value.Margin.Vertical;
            var heading = Font.Height + 14;
            var firstRow = Math.Max(Lines(installer) + Lines(installerNote), Lines(record) + Lines(recordNote)) + heading + 20;
            var secondRow = Math.Max(Lines(changes) + Lines(changesNote), Lines(coverage) + Font.Height * 4 + copyScope.GetPreferredSize(Size.Empty).Height) + heading + 24;
            var cardHeight = firstRow + secondRow;
            cards.RowStyles[0].SizeType = SizeType.Absolute;
            cards.RowStyles[0].Height = firstRow;
            cards.RowStyles[1].SizeType = SizeType.Percent;
            cards.RowStyles[1].Height = 100;
            if (hasRecord && expanded) document.RowStyles[0].Height = cardHeight;
            var minimum = hasRecord ? cardHeight + (int)document.RowStyles[1].Height + (expanded ? Font.Height * 12 : 0) : 0;
            var extent = new Size(0, minimum);
            if (viewport.AutoScrollMinSize != extent) viewport.AutoScrollMinSize = extent;
            var target = new Size(width, Math.Max(minimum, viewport.ClientSize.Height));
            if (document.Size != target) document.Size = target;
        }
        finally { sizing = false; }
    }

    private static TableLayoutPanel Card(string name, string title, Control primary, Control secondary)
    {
        var card = new TableLayoutPanel
        {
            Name = name, Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(10, 6, 10, 6), Margin = new Padding(3),
            BackColor = Color.White, CellBorderStyle = TableLayoutPanelCellBorderStyle.None
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = TextLabel(name + "Heading");
        heading.Text = title;
        heading.ForeColor = Color.FromArgb(22, 105, 117);
        heading.Margin = new Padding(0, 0, 0, 7);
        card.Controls.Add(heading, 0, 0);
        card.Controls.Add(primary, 0, 1);
        card.Controls.Add(secondary, 0, 2);
        return card;
    }

    private static Label TextLabel(string name, bool strong = false) => new()
    {
        Name = name, Dock = DockStyle.Fill, AutoSize = true, UseMnemonic = false,
        ForeColor = strong ? Ink : Muted, Margin = new Padding(0, 0, 0, 5),
        Font = new Font("Microsoft YaHei UI", strong ? 11F : 9.5F, strong ? FontStyle.Bold : FontStyle.Regular)
    };
    private static Button Action(string name, string text) => new()
    {
        Name = name, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(9, 5, 9, 5), Margin = new Padding(3, 5, 6, 2),
        FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, UseVisualStyleBackColor = false
    };
}
