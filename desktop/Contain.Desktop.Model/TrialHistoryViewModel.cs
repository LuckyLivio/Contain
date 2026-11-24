using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

/// <summary>
/// ViewModel for the "我的记录 → 隔离试用" history category and single-trial detail/export view.
/// Completely independent of Windows Sandbox availability: machines without Sandbox can still browse
/// historical sessions, search by software name or entry path, and export versioned JSON reports.
/// </summary>
public sealed class TrialHistoryViewModel : ObservableObject
{
    private readonly ITrialHistoryStore store;
    private readonly List<TrialHistoryRecord> allRecords = [];
    private string searchQuery = "";
    private TrialHistoryRecord? selectedRecord;
    private string statusMessage = "";
    private string errorMessage = "";

    public TrialHistoryViewModel(ITrialHistoryStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ObservableCollection<TrialHistoryRecord> FilteredRecords { get; } = [];
    public ObservableCollection<TrialTimelineEventReport> SelectedTimeline { get; } = [];

    public int TotalCount => allRecords.Count;
    public string TabHeaderText => allRecords.Count > 0 ? $"隔离试用（{allRecords.Count}）" : "隔离试用";
    public bool IsEmpty => allRecords.Count == 0;
    public bool IsSearchEmpty => allRecords.Count > 0 && FilteredRecords.Count == 0;
    public bool HasSelectedRecord => selectedRecord is not null;
    public TrialHistoryRecord? SelectedRecord => selectedRecord;
    public TrialSessionReport? SelectedReport => selectedRecord?.Report;

    public string SearchQuery
    {
        get => searchQuery;
        set
        {
            var next = value ?? "";
            if (string.Equals(searchQuery, next, StringComparison.Ordinal)) return;
            searchQuery = next;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string StatusMessage => statusMessage;
    public string ErrorMessage => errorMessage;
    public bool HasError => errorMessage.Length > 0;

    // Convenience display properties for Detail view (all sourced directly from SelectedReport)
    public string DetailTitle => SelectedReport?.SoftwareName ?? "";
    public string DetailSubtitle => SelectedReport is null
        ? ""
        : $"{SelectedReport.InputKindLabel} · 版本：{SelectedReport.DisplayVersion} · 会话 ID：{SelectedReport.SessionId}";
    public string DetailStartedTime => SelectedReport?.DisplayStartedTime ?? "";
    public string DetailEndedTime => SelectedReport?.DisplayEndedTime ?? "";
    public string DetailInputKindLabel => SelectedReport?.InputKindLabel ?? "";
    public string DetailEntryRelativePath => SelectedReport?.EntryRelativePath ?? "";
    public string DetailEntryArchitecture => SelectedReport?.EntryArchitecture ?? "";
    public string DetailEntryVersion => SelectedReport?.DisplayVersion ?? "未提供";
    public string DetailEntrySha256 => SelectedReport?.DisplayEntrySha256 ?? "未记录";
    public bool DetailHasEntrySha256 => SelectedReport?.EntrySha256Recorded == true && !string.IsNullOrWhiteSpace(SelectedReport.EntrySha256);
    public bool DetailIsFolderMode => SelectedReport?.IsFolderMode == true;
    public string DetailFolderScopeText => SelectedReport?.DisplayFolderScope ?? "";
    public string DetailLaunchOutcomeLabel => SelectedReport?.LaunchOutcomeLabel ?? "";
    public string DetailLaunchSummary => SelectedReport?.LaunchSummary ?? "";
    public string DetailSandboxStateLabel => SelectedReport?.SandboxInstanceStateLabel ?? "";
    public string DetailStagingStateLabel => SelectedReport?.HostStagingStateLabel ?? "";
    public string DetailOverallResultLabel => SelectedReport?.OverallResultLabel ?? "";
    public string DetailEvidenceScopeNote => SelectedReport?.EvidenceScopeNote ?? TrialSessionReport.DefaultEvidenceScopeNote;

    public bool DetailSandboxIsWarning => SelectedReport?.SandboxInstanceState is "Unknown" or "Running";
    public bool DetailStagingIsWarning => SelectedReport?.HostStagingState is "Remaining" or "Unconfirmed";

    public void Refresh()
    {
        errorMessage = "";
        try
        {
            var loaded = store.LoadAll();
            allRecords.Clear();
            allRecords.AddRange(loaded);

            if (selectedRecord is not null)
            {
                var updated = allRecords.FirstOrDefault(r => string.Equals(r.SessionId, selectedRecord.SessionId, StringComparison.OrdinalIgnoreCase));
                if (updated is not null)
                    SetSelectedRecordCore(updated);
            }

            ApplyFilter();
            statusMessage = allRecords.Count == 0
                ? "还没有隔离试用历史记录。"
                : $"共 {allRecords.Count} 条隔离试用历史记录（按最近时间排列）。";
            NotifyAll();
        }
        catch (Exception ex)
        {
            errorMessage = $"读取隔离试用历史失败：{ex.Message}";
            statusMessage = errorMessage;
            NotifyAll();
        }
    }

    /// <summary>
    /// Updates the in-memory list immediately when IsolatedTrialViewModel persists a session change.
    /// </summary>
    public void UpsertInMemory(TrialHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var idx = allRecords.FindIndex(r => string.Equals(r.SessionId, record.SessionId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            allRecords[idx] = record;
        else
            allRecords.Add(record);

        allRecords.Sort((a, b) =>
        {
            var cmp = b.Report.StartedAtUtc.CompareTo(a.Report.StartedAtUtc);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(b.SessionId, a.SessionId);
        });

        if (selectedRecord is not null && string.Equals(selectedRecord.SessionId, record.SessionId, StringComparison.OrdinalIgnoreCase))
            SetSelectedRecordCore(record);

        ApplyFilter();
        NotifyAll();
    }

    public bool OpenRecord(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var match = allRecords.FirstOrDefault(r => string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            ?? store.LoadBySessionId(sessionId);
        if (match is null)
        {
            errorMessage = "未找到对应的隔离试用记录。";
            statusMessage = errorMessage;
            NotifyAll();
            return false;
        }

        if (!allRecords.Any(r => string.Equals(r.SessionId, match.SessionId, StringComparison.OrdinalIgnoreCase)))
            UpsertInMemory(match);

        errorMessage = "";
        SetSelectedRecordCore(match);
        statusMessage = $"正在查看“{match.SoftwareName}”的隔离试用详情（会话 {match.SessionId}）。";
        NotifyAll();
        return true;
    }

    public void ClearSearch()
    {
        SearchQuery = "";
    }

    public bool ExportSelectedReport(string destinationPath)
    {
        if (selectedRecord is null)
        {
            errorMessage = "请先选择一条隔离试用记录后再导出报告。";
            statusMessage = errorMessage;
            NotifyAll();
            return false;
        }

        try
        {
            store.ExportReport(selectedRecord.Report, destinationPath);
            errorMessage = "";
            statusMessage = $"已导出本次报告（版本化 JSON，不含宿主机绝对路径与用户名）：{Path.GetFileName(destinationPath)}";
            NotifyAll();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"导出隔离试用报告失败：{ex.Message}";
            statusMessage = errorMessage;
            NotifyAll();
            return false;
        }
    }

    public string SuggestedExportFileName => SelectedReport is null
        ? "Contain-隔离试用报告.json"
        : JsonTrialHistoryStore.BuildSuggestedExportFileName(SelectedReport);

    private void SetSelectedRecordCore(TrialHistoryRecord record)
    {
        selectedRecord = record;
        SelectedTimeline.Clear();
        foreach (var item in record.Report.Timeline)
            SelectedTimeline.Add(item);
    }

    private void ApplyFilter()
    {
        var q = searchQuery.Trim();
        FilteredRecords.Clear();
        foreach (var rec in allRecords)
        {
            if (q.Length == 0
                || rec.Report.SoftwareName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || rec.Report.EntryRelativePath.Contains(q, StringComparison.OrdinalIgnoreCase)
                || rec.Report.EntryFileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || rec.Report.Version.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredRecords.Add(rec);
            }
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsSearchEmpty));
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TabHeaderText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsSearchEmpty));
        OnPropertyChanged(nameof(HasSelectedRecord));
        OnPropertyChanged(nameof(SelectedRecord));
        OnPropertyChanged(nameof(SelectedReport));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailSubtitle));
        OnPropertyChanged(nameof(DetailStartedTime));
        OnPropertyChanged(nameof(DetailEndedTime));
        OnPropertyChanged(nameof(DetailInputKindLabel));
        OnPropertyChanged(nameof(DetailEntryRelativePath));
        OnPropertyChanged(nameof(DetailEntryArchitecture));
        OnPropertyChanged(nameof(DetailEntryVersion));
        OnPropertyChanged(nameof(DetailEntrySha256));
        OnPropertyChanged(nameof(DetailHasEntrySha256));
        OnPropertyChanged(nameof(DetailIsFolderMode));
        OnPropertyChanged(nameof(DetailFolderScopeText));
        OnPropertyChanged(nameof(DetailLaunchOutcomeLabel));
        OnPropertyChanged(nameof(DetailLaunchSummary));
        OnPropertyChanged(nameof(DetailSandboxStateLabel));
        OnPropertyChanged(nameof(DetailStagingStateLabel));
        OnPropertyChanged(nameof(DetailOverallResultLabel));
        OnPropertyChanged(nameof(DetailEvidenceScopeNote));
        OnPropertyChanged(nameof(DetailSandboxIsWarning));
        OnPropertyChanged(nameof(DetailStagingIsWarning));
    }
}
