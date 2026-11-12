using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Contain.Viewer;

namespace Contain.Desktop.Model;

/// <summary>One exact selection owns all displayed data. UI-framework independent and testable with controlled queries.</summary>
public sealed class RecordsViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 50;
    private readonly IRecordService service;
    private readonly string personalDatabase;
    private readonly Func<bool> personalMissing;
    private readonly LatestRequest requests = new();
    private readonly object operationsGate = new();
    private readonly HashSet<Task> pendingOperations = [];
    private CancellationTokenSource? exportCancellation;
    private bool disposed;
    private bool busy;
    private bool exporting;
    private string? database;
    private string? recordId;
    private string sessionTitle = "选择一条记录";
    private string sessionStartedAt = "";
    private string sessionExportName = "";
    private string search = "";
    private string searchDraft = "";
    private bool searchCommitted;
    private string changeFilter = "";
    private string statusMessage = "";
    private string errorMessage = "";
    private Exception? lastError;
    private string technical = "";
    private string eventExplanation = "选择一条事件，查看其类型、进程关联和依据。";
    private string rawExplanation = "";
    private string? selectedEventId;
    private string changeExplanation = "选择一项状态变化查看记录的依据。";
    private bool follow;
    private bool personal = true;
    private bool moreEvents;
    private long eventOffset;
    private int changeOffset;
    private long? matched;
    private string? fingerprint;
    private OverviewPresentation? overview;
    private List<ChangeRow> allChanges = [];

    public RecordsViewModel(IRecordService service, string personalDatabase, Func<bool> personalMissing)
    {
        this.service = service;
        this.personalDatabase = Path.GetFullPath(personalDatabase);
        this.personalMissing = personalMissing;
    }

    public ObservableCollection<SessionRow> Sessions { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<ChangeRow> Changes { get; } = [];
    public bool IsBusy => busy;
    public bool IsExporting => exporting;
    public bool IsPersonalLibrary => personal;
    public string DatabasePath => database ?? (personal ? personalDatabase : "");
    public string SourceTitle => personal ? "我的记录" : "外部数据库 · 只读";
    public string SourceDescription => personal ? "保存在这台电脑，关闭后仍可找回。" : "只读取所选数据库；不会修改原记录，也不会改变新建记录的默认保存位置。";
    public bool IsEmpty => !busy && Sessions.Count == 0 && errorMessage.Length == 0;
    public bool HasRecord => recordId is not null;
    public string? ActiveRecordId => recordId;
    public string SessionTitle => sessionTitle;
    public string SessionIdentity => recordId is null ? "" : "session:" + recordId;
    public bool FollowInstaller => follow;
    public string StatusMessage => statusMessage;
    public string ErrorMessage => errorMessage;
    public string ErrorKind => lastError is CoreClientException core ? core.Code : lastError?.GetType().Name ?? "";
    public Exception? LastError => lastError;
    public string OverviewInstaller => overview?.Installer ?? "退出结果未记录";
    public string OverviewInstallerNote => overview?.InstallerNote ?? "";
    public string OverviewRecord => overview?.Record ?? "记录尚未打开";
    public string OverviewRecordNote => overview?.RecordNote ?? "";
    public string OverviewScope => overview?.Scope ?? "";
    public string OverviewRegistryScope => overview?.RegistryScope ?? "";
    public string OverviewCoverage => overview?.Coverage ?? "覆盖质量未提供";
    public string OverviewChanges => overview?.Changes ?? "";
    public string OverviewChangesNote => overview?.ChangesNote ?? "";
    public string OverviewAlert => overview is null ? "" : RecordPresentation.OverviewAlert(overview.Alert);
    public string TechnicalDetails => technical;
    public string DiagnosticSummary => technical + (lastError is null ? "" : $"\n查询 / 导出错误\n数据库：{DatabasePath}\n会话：{recordId ?? "尚未确认"}\n错误类型：{ErrorKind}\n{lastError}\n"
        + (lastError is CoreClientException core ? core.Diagnostic : ""));
    public string EventExplanation => eventExplanation;
    public string RawExplanation => rawExplanation;
    public string? SelectedEventId => selectedEventId;
    public string SelectedEventResource => Events.FirstOrDefault(row => row.Id == selectedEventId)?.Resource ?? "";
    public string EventIdentity => selectedEventId is null ? "" : "event:" + selectedEventId;
    public bool CanCopyEvent => !busy && selectedEventId is not null;
    public string SearchText => search;
    public string SearchDraft
    {
        get => searchDraft;
        set
        {
            value ??= "";
            if (disposed || searchDraft == value) return;
            searchDraft = value;
            // An edited query invalidates results immediately, even before Enter.
            // Avoid starting a CLI process for every keystroke or retaining rows
            // whose search context no longer matches the editable field.
            requests.Invalidate(); busy = false; searchCommitted = false;
            Events.Clear(); matched = null; fingerprint = null; moreEvents = false; eventOffset = 0;
            lastError = null; errorMessage = "";
            ClearExplanation();
            SetStatus("搜索条件已更改，点击搜索或按 Enter 查看当前会话的匹配项。");
            NotifyState();
        }
    }
    public long? MatchedCount => matched;
    public long EventOffset => eventOffset;
    public string EventPageText => !HasRecord ? "尚未加载事件" : busy && Events.Count == 0 ? "正在查询…" : !searchCommitted ? "搜索条件已更改，等待执行"
        : matched is { } total ? total == 0 ? "共 0 条，没有匹配项" : Events.Count == 0 ? $"共 {total} 条，此页没有匹配项"
            : $"共 {total} 条 · 第 {eventOffset + 1}–{eventOffset + Events.Count} 条"
        : Events.Count == 0 ? "此页没有事件" : $"第 {eventOffset + 1}–{eventOffset + Events.Count} 条 · 每页最多 {PageSize} 条";
    public bool CanPreviousEvents => !busy && HasRecord && searchCommitted && eventOffset > 0;
    public bool CanNextEvents => !busy && HasRecord && searchCommitted && moreEvents;
    public bool CanExportPage => !busy && !exporting && HasRecord && searchCommitted && search.Length == 0;
    public bool CanExportMatches => !busy && !exporting && HasRecord && searchCommitted && search.Length != 0 && matched is > 0 && fingerprint is not null;
    public string MatchExportLabel => matched is > 0 ? $"导出全部 {matched} 条匹配结果" : "导出全部匹配结果";
    public string ChangeFilter => changeFilter;
    public string ChangePageText => !HasRecord ? "尚未加载变化" : $"全部 {allChanges.Count} 项 · 筛选后 {FilteredChanges().Count} 项 · 本页 {Changes.Count} 项";
    public bool CanPreviousChanges => !busy && HasRecord && changeOffset > 0;
    public bool CanNextChanges => !busy && HasRecord && changeOffset + PageSize < FilteredChanges().Count;
    public string ChangeExplanation => changeExplanation;

    public Task<bool> LoadPersonalAsync() => RunAsync(async ticket =>
    {
        PrepareLibrary(true);
        // The resolver is read-only. In particular, empty first use creates nothing.
        if (personalMissing()) { SetStatus("还没有安装记录。选择 EXE 并开始，即可创建第一条记录。"); return; }
        await LoadLibraryAsync(personalDatabase, ticket);
    }, "正在读取我的记录…");

    public Task<bool> OpenDatabaseAsync(string path) => RunAsync(async ticket =>
    {
        var full = Path.GetFullPath(path);
        PrepareLibrary(string.Equals(full, personalDatabase, StringComparison.OrdinalIgnoreCase));
        await LoadLibraryAsync(full, ticket);
    }, "正在打开记录库…");

    public Task<bool> OpenSessionAsync(string id) => RunAsync(async ticket =>
    {
        var selectedDatabase = database ?? throw new InvalidOperationException("请先打开已有记录库。");
        var row = Sessions.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidDataException("未找到所选准确会话；未使用同名或最新记录替代。");
        ClearRecord();
        sessionTitle = "正在读取：" + row.Name;
        NotifyRecord();
        await LoadRecordAsync(selectedDatabase, row.Id, ticket);
    }, "正在读取所选记录…");

    public async Task OpenCapturedSessionAsync(string path, string id)
    {
        Exception? failure = null;
        var success = await RunAsync(async ticket =>
        {
            try
            {
                var full = Path.GetFullPath(path);
                PrepareLibrary(string.Equals(full, personalDatabase, StringComparison.OrdinalIgnoreCase));
                await LoadLibraryAsync(full, ticket);
                if (Sessions.Count(item => item.Id == id) != 1)
                    throw new InvalidDataException("数据库中未找到本次启动的准确会话；未选择其他记录替代。已有证据保持不变。");
                await LoadRecordAsync(full, id, ticket);
                SetStatus("记录已保存，可以查看事件与依据。");
            }
            catch (Exception error) { failure = error; throw; }
        }, "正在按准确会话读回保存结果…");
        if (!success)
        {
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            throw new OperationCanceledException("本次会话读回被另一次选择替代，未确认自动打开成功。");
        }
    }

    public Task<bool> SearchAsync(string text) => LoadEventsAsync(0, text);
    public Task<bool> ClearSearchAsync() => SearchAsync("");
    public Task<bool> PreviousEventsAsync() => LoadEventsAsync(Math.Max(0, eventOffset - PageSize), search);
    public Task<bool> NextEventsAsync() => LoadEventsAsync(checked(eventOffset + PageSize), search);

    public Task<bool> SelectEventAsync(EventRow row) => RunAsync(async ticket =>
    {
        var db = database ?? throw new InvalidOperationException("请先打开记录。");
        var id = recordId ?? throw new InvalidOperationException("请先选择记录。");
        var actual = Events.SingleOrDefault(item => item.Id == row.Id);
        if (actual is null || !JsonView.JsonEqual(actual.Data, row.Data))
            throw new InvalidDataException("事件不属于当前显示页，未查询或关联其他记录。");
        ClearExplanation();
        eventExplanation = "正在读取所选事件的依据…";
        NotifyExplanation();
        var explanation = await service.ExplainAsync(db, id, row.Id, ticket.Token);
        RequireCurrent(ticket);
        ValidateExplanation(db, id, row, explanation);
        CommitExplanation(row, explanation);
        SetStatus("已读取同一事件的完整依据。High 不表示安全删除。");
    }, "正在读取事件依据…");

    public void SetChangeFilter(string value)
    {
        changeFilter = value;
        changeOffset = 0;
        RenderChanges();
    }
    public void PreviousChanges() { changeOffset = Math.Max(0, changeOffset - PageSize); RenderChanges(); }
    public void NextChanges() { changeOffset = checked(changeOffset + PageSize); RenderChanges(); }
    public void SelectChange(ChangeRow row)
    {
        if (!Changes.Contains(row)) return;
        changeExplanation = $"依据：{row.Reason}\n置信标签：{row.Confidence}\n完整资源：{row.Resource}\n状态变化不建立最终所有权，也不是删除许可。";
        OnPropertyChanged(nameof(ChangeExplanation));
    }

    public Task<bool> ExportPageAsync(string target, CancellationToken token = default)
    {
        var context = CaptureExportContext(false);
        return context is null ? Task.FromResult(false) : ExportAsync(context, target, token);
    }

    public Task<bool> ExportMatchesAsync(string target, CancellationToken token = default)
    {
        var context = CaptureExportContext(true);
        return context is null ? Task.FromResult(false) : ExportAsync(context, target, token);
    }

    public RecordExportContext? CaptureExportContext(bool matches)
    {
        if (matches ? !CanExportMatches : !CanExportPage) return null;
        return new(database!, recordId!, selectedEventId, eventOffset, matches ? search : null,
            matches ? fingerprint : null, matches ? matched : null, requests.Version, sessionExportName, sessionStartedAt);
    }

    public Task<bool> ExportAsync(RecordExportContext context, string target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Search is null) return ExportAsync(async cancellation =>
        {
            await service.ExportPageAsync(context.Database, context.SessionId, context.SelectedEventId, PageSize, context.Offset, target, context.Identity, cancellation);
            return "已导出本页报告：当前未筛选事件页、所选解释及本记录状态变化。";
        }, token, viewVersion: context.ViewVersion);
        return ExportAsync(async cancellation =>
        {
            if (context.Fingerprint is null || context.MatchCount is not > 0)
                throw new InvalidDataException("导出上下文缺少已完成搜索的内容标识或数量。");
            var actual = await service.ExportMatchesAsync(context.Database, context.SessionId, context.Search, context.Fingerprint, context.MatchCount.Value, target, context.Identity, cancellation);
            if (actual != context.MatchCount.Value) throw new InvalidDataException("导出结果数量与开始时的完整搜索快照不一致。");
            return $"已导出全部 {actual} 条匹配结果；顺序与搜索一致。";
        }, token, () => { if (database == context.Database && recordId == context.SessionId && search == context.Search) fingerprint = null; }, context.ViewVersion);
    }

    public void CancelExport() => exportCancellation?.Cancel();

    private Task<bool> ExportAsync(Func<CancellationToken, Task<string>> export, CancellationToken token, Action? changed = null, long? viewVersion = null) =>
        Track(() => ExportCoreAsync(export, token, changed, viewVersion));

    private async Task<bool> ExportCoreAsync(Func<CancellationToken, Task<string>> export, CancellationToken token, Action? changed, long? viewVersion)
    {
        if (disposed || exporting) return false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        exportCancellation = cancellation;
        exporting = true;
        var version = viewVersion ?? requests.Version;
        NotifyState();
        try
        {
            var message = await export(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!disposed && version == requests.Version) SetStatus(message);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!disposed && version == requests.Version) SetStatus("已取消导出；未提交不完整报告，当前选择保持不变。");
            return false;
        }
        catch (Exception error)
        {
            if (!disposed && version == requests.Version)
            {
                if (error is CoreClientException { Code: "export_record_changed" }) changed?.Invoke();
                SetError(error);
            }
            return false;
        }
        finally
        {
            exportCancellation = null;
            exporting = false;
            if (!disposed) NotifyState();
        }
    }

    private Task<bool> LoadEventsAsync(long offset, string text) => RunAsync(async ticket =>
    {
        var db = database ?? throw new InvalidOperationException("请先打开记录。");
        var id = recordId ?? throw new InvalidOperationException("请先选择记录。");
        search = text; searchDraft = text; searchCommitted = false;
        eventOffset = offset;
        Events.Clear(); matched = null; fingerprint = null; moreEvents = false;
        ClearExplanation(); NotifyState();
        var page = await service.HistoryAsync(db, id, text.Length == 0 ? PageSize + 1 : PageSize, offset, text.Length == 0 ? null : text, ticket.Token);
        RequireCurrent(ticket);
        EnsureIdentity(page, "app_id", id);
        if (database != db || recordId != id) throw new InvalidDataException("事件页与当前会话不一致。");
        CommitEvents(page, offset);
        SetStatus(text.Length == 0 ? "已加载事件页，选择一项查看依据。" : "搜索覆盖当前会话全部事件。清除搜索可返回普通浏览。");
    }, "正在查询本次记录的事件…");

    private async Task LoadLibraryAsync(string path, LatestRequest.Ticket ticket)
    {
        var response = await service.ListAsync(path, ticket.Token);
        RequireCurrent(ticket);
        var rows = response.Data.EnumerateArray().Select(SessionRow.From).ToArray();
        if (rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new InvalidDataException("记录列表包含重复会话身份，未显示歧义结果。");
        var duplicates = rows.GroupBy(row => row.Name, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .SelectMany(group => group.Select((row, index) => (row.Id, Label: $"同名记录 {index + 1} / {group.Count()} · 标识末尾 {DistinctSuffix(row.Id, group.Select(item => item.Id).ToArray())}")))
            .ToDictionary(item => item.Id, item => item.Label, StringComparer.Ordinal);
        database = Path.GetFullPath(path);
        foreach (var row in rows) Sessions.Add(duplicates.TryGetValue(row.Id, out var label) ? row with { Disambiguation = label } : row);
        SetStatus(rows.Length == 0 ? "暂无记录。可以创建第一条安装记录。" : $"已找到 {rows.Length} 条记录，选择一条查看。");
        NotifyState();
    }

    private async Task LoadRecordAsync(string db, string id, LatestRequest.Ticket ticket)
    {
        var summary = await service.SummaryAsync(db, id, ticket.Token);
        RequireCurrent(ticket); EnsureIdentity(summary, "id", id);
        var diff = await service.DiffAsync(db, id, ticket.Token);
        RequireCurrent(ticket); EnsureIdentity(diff, "app_id", id);
        var isFollow = RecordingScope.IsFollowInstaller(summary.Data);
        var query = isFollow ? "write_requested" : null;
        var page = await service.HistoryAsync(db, id, query is null ? PageSize + 1 : PageSize, 0, query, ticket.Token);
        RequireCurrent(ticket); EnsureIdentity(page, "app_id", id);
        var changes = new List<ChangeRow>();
        foreach (var pair in new[] { ("文件", "files"), ("注册表", "registry"), ("配置清单", "inventory") })
            changes.AddRange(JsonView.Items(JsonView.Field(diff.Data, pair.Item2)).Select(item => ChangeRow.From(pair.Item1, item)));
        // All required initial reads commit together. No old session data survives a failed load.
        database = db; recordId = id; follow = isFollow; sessionTitle = RecordText.Value(summary.Data, "name");
        sessionStartedAt = JsonView.Text(JsonView.Field(summary.Data, "started_at"), "");
        sessionExportName = JsonView.Text(JsonView.Field(summary.Data, "name"), "");
        overview = OverviewPresentation.Build(summary.Data, diff.Data);
        technical = RecordPresentation.Technical(summary.Data, db);
        allChanges = changes; search = query ?? ""; searchDraft = search;
        CommitEvents(page, 0);
        RenderChanges(); NotifyRecord();
        SetStatus("记录已从数据库读回。保存与覆盖完整是不同事实。");
        if (isFollow)
        {
            var candidate = Events.FirstOrDefault(item => RecordText.Value(item.Data, "event_type") == "file"
                && item.Operation == "write_requested" && RecordText.Value(item.Data, "confidence") == "High"
                && RecordText.Value(JsonView.Field(item.Data, "evidence"), "source") == "EtwFile"
                && RecordText.Value(JsonView.Field(item.Data, "evidence"), "rule") is "InstallerPid" or "DescendantProcess");
            if (candidate is not null)
            {
                var explanation = await service.ExplainAsync(db, id, candidate.Id, ticket.Token);
                RequireCurrent(ticket);
                ValidateExplanation(db, id, candidate, explanation);
                CommitExplanation(candidate, explanation);
            }
        }
    }

    private void CommitEvents(CoreResponse response, long offset)
    {
        var rows = JsonView.Items(JsonView.Field(response.Data, "events")).Select(EventRow.From).ToArray();
        eventOffset = offset;
        searchCommitted = true;
        matched = response.Data.TryGetProperty("matched_total", out var count) ? count.GetInt64() : null;
        fingerprint = response.Data.TryGetProperty("search_fingerprint", out var identity) ? identity.GetString() : null;
        moreEvents = matched is { } total ? offset + rows.Length < total : rows.Length > PageSize;
        Events.Clear();
        foreach (var row in rows.Take(PageSize)) Events.Add(row);
        ClearExplanation();
        if (matched == 0) eventExplanation = "没有匹配项。可以修改搜索条件或清除搜索。";
        NotifyExplanation(); NotifyState();
    }

    private void ValidateExplanation(string db, string id, EventRow row, CoreResponse explanation)
    {
        var explained = JsonView.Field(explanation.Data, "event");
        if (database != db || recordId != id || RecordText.Value(explanation.Data, "app_id") != id
            || RecordText.Value(explained, "id") != row.Id || !JsonView.JsonEqual(row.Data, explained))
            throw new InvalidDataException("事件解释与当前会话、事件或显示内容不一致，未显示可能误关联的依据。");
    }

    private void CommitExplanation(EventRow row, CoreResponse explanation)
    {
        selectedEventId = row.Id;
        eventExplanation = RecordPresentation.Explain(explanation.Data, follow);
        rawExplanation = JsonSerializer.Serialize(explanation.Envelope, new JsonSerializerOptions { WriteIndented = true });
        NotifyExplanation();
    }

    private List<ChangeRow> FilteredChanges() => allChanges.Where(row => changeFilter.Length == 0
        || new[] { row.Kind, row.Operation, row.Resource, row.Confidence, row.Reason }
            .Any(value => value.Contains(changeFilter, StringComparison.OrdinalIgnoreCase))).ToList();

    private void RenderChanges()
    {
        var filtered = FilteredChanges();
        changeOffset = Math.Min(changeOffset, filtered.Count == 0 ? 0 : (filtered.Count - 1) / PageSize * PageSize);
        Changes.Clear();
        foreach (var row in filtered.Skip(changeOffset).Take(PageSize)) Changes.Add(row);
        changeExplanation = follow ? "没有安装前文件基线，请到“事件与依据”查看来源事件。空状态差异不表示没有文件操作。"
            : filtered.Count == 0 ? "没有符合当前筛选的状态变化；未观察到不等于没有发生。" : "选择一项状态变化查看记录的依据。";
        NotifyState(); OnPropertyChanged(nameof(ChangeExplanation));
    }

    private void PrepareLibrary(bool isPersonal)
    {
        personal = isPersonal; database = null; Sessions.Clear(); ClearRecord(); NotifyState();
    }

    private void ClearRecord()
    {
        recordId = null; sessionTitle = "选择一条记录"; overview = null; technical = ""; follow = false;
        sessionStartedAt = "";
        sessionExportName = "";
        search = ""; searchDraft = ""; searchCommitted = false; changeFilter = ""; eventOffset = 0; changeOffset = 0; matched = null; fingerprint = null; moreEvents = false;
        allChanges = []; Events.Clear(); Changes.Clear(); ClearExplanation(); NotifyRecord(); NotifyState();
    }

    private void ClearExplanation()
    {
        selectedEventId = null; rawExplanation = "";
        eventExplanation = "选择一条事件，查看其类型、进程关联和依据。Unknown 表示证据不足，不表示危险。";
        NotifyExplanation();
    }

    private Task<bool> RunAsync(Func<LatestRequest.Ticket, Task> action, string message) => Track(() => RunCoreAsync(action, message));

    private async Task<bool> RunCoreAsync(Func<LatestRequest.Ticket, Task> action, string message)
    {
        if (disposed) return false;
        var ticket = requests.Begin();
        busy = true; lastError = null; errorMessage = ""; statusMessage = message; NotifyState();
        try
        {
            await action(ticket);
            return requests.IsCurrent(ticket);
        }
        catch (OperationCanceledException) when (!requests.IsCurrent(ticket)) { return false; }
        catch (Exception error)
        {
            if (requests.IsCurrent(ticket)) SetError(error);
            return false;
        }
        finally
        {
            if (requests.IsCurrent(ticket)) { busy = false; NotifyState(); }
        }
    }

    private void RequireCurrent(LatestRequest.Ticket ticket)
    {
        if (!requests.IsCurrent(ticket)) throw new OperationCanceledException(ticket.Token);
    }

    private static void EnsureIdentity(CoreResponse response, string field, string id)
    {
        if (RecordText.Value(response.Data, field) != id) throw new InvalidDataException("返回会话与选择的准确身份不一致。");
    }

    private static string DistinctSuffix(string id, IReadOnlyList<string> group)
    {
        for (var length = Math.Min(8, id.Length); length < id.Length; length++)
        {
            var suffix = id[^length..];
            if (group.Count(value => value.EndsWith(suffix, StringComparison.Ordinal)) == 1) return suffix;
        }
        return id;
    }

    private void SetError(Exception error)
    {
        lastError = error; errorMessage = error.Message; statusMessage = "操作未完成；已有记录保持不变。"; NotifyState();
    }

    private void SetStatus(string message) { statusMessage = message; OnPropertyChanged(nameof(StatusMessage)); }

    private void NotifyRecord()
    {
        foreach (var property in new[] { nameof(HasRecord), nameof(ActiveRecordId), nameof(SessionTitle), nameof(SessionIdentity), nameof(FollowInstaller),
            nameof(OverviewInstaller), nameof(OverviewInstallerNote), nameof(OverviewRecord), nameof(OverviewRecordNote), nameof(OverviewScope),
            nameof(OverviewRegistryScope), nameof(OverviewCoverage), nameof(OverviewChanges), nameof(OverviewChangesNote), nameof(OverviewAlert), nameof(TechnicalDetails), nameof(DiagnosticSummary) })
            OnPropertyChanged(property);
    }

    private void NotifyExplanation()
    {
        foreach (var property in new[] { nameof(EventExplanation), nameof(RawExplanation), nameof(SelectedEventId), nameof(SelectedEventResource), nameof(EventIdentity), nameof(CanCopyEvent) }) OnPropertyChanged(property);
    }

    private void NotifyState()
    {
        foreach (var property in new[] { nameof(IsBusy), nameof(IsExporting), nameof(IsPersonalLibrary), nameof(IsEmpty), nameof(DatabasePath), nameof(SourceTitle),
            nameof(SourceDescription), nameof(StatusMessage), nameof(ErrorMessage), nameof(ErrorKind), nameof(LastError), nameof(SearchText), nameof(SearchDraft), nameof(MatchedCount), nameof(DiagnosticSummary),
            nameof(EventOffset), nameof(EventPageText), nameof(CanPreviousEvents), nameof(CanNextEvents), nameof(CanExportPage), nameof(CanExportMatches),
            nameof(MatchExportLabel), nameof(ChangeFilter), nameof(ChangePageText), nameof(CanPreviousChanges), nameof(CanNextChanges), nameof(CanCopyEvent) }) OnPropertyChanged(property);
    }

    private Task<T> Track<T>(Func<Task<T>> operation)
    {
        // Register before calling the operation: synchronous property-change
        // handlers may start closing before the first asynchronous suspension.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (operationsGate) pendingOperations.Add(completed.Task);
        return RunTrackedAsync();

        async Task<T> RunTrackedAsync()
        {
            try { return await operation(); }
            finally
            {
                lock (operationsGate) pendingOperations.Remove(completed.Task);
                completed.SetResult();
            }
        }
    }

    /// <summary>Cancel read-only work, then wait for pipe and temporary-export cleanup before the application exits.</summary>
    public async Task CloseAsync()
    {
        Dispose();
        Task[] pending;
        lock (operationsGate) pending = pendingOperations.ToArray();
        if (pending.Length == 0) return;
        // The existing CLI/read/export bounds remain 60 seconds. A stuck cleanup
        // is a visible close failure, not permission to abandon a child or file;
        // the window must remain open if this deadline is reached.
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(60));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; requests.Dispose(); exportCancellation?.Cancel();
    }
}
