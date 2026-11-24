using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

/// <summary>
/// ViewModel for the "配置还原点" single-file config backup, diff, confirmed restore, and undo workflow.
/// Runs completely locally with normal user privileges and requires NO Windows Sandbox.
/// </summary>
public sealed class ConfigRestorePointsViewModel : ObservableObject
{
    private readonly IConfigRestorePointService service;
    private readonly List<ConfigRestorePointRecord> allPoints = [];

    private string targetFilePathInput = "";
    private string noteInput = "";
    private string searchQuery = "";
    private ConfigFilePreview? candidatePreview;
    private ConfigRestorePointRecord? selectedPoint;
    private ConfigComparisonResult? comparison;
    private bool isConfirmingRestore;
    private ConfigLiveFileSnapshot? confirmedLiveSnapshot;
    private string midConfirmWarning = "";
    private ConfigUndoSnapshot? latestUndo;
    private bool hasOperationBanner;
    private bool isOperationBannerError;
    private bool isOperationBannerWarning;
    private string operationBannerHeadline = "";
    private string operationBannerDetail = "";
    private string statusMessage = "";
    private string errorMessage = "";
    private bool busy;

    public ConfigRestorePointsViewModel(IConfigRestorePointService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public ObservableCollection<ConfigRestorePointRecord> FilteredPoints { get; } = [];
    public ObservableCollection<ConfigDiffLine> DiffLines { get; } = [];

    public string TargetFilePathInput
    {
        get => targetFilePathInput;
        set
        {
            var next = value ?? "";
            if (string.Equals(targetFilePathInput, next, StringComparison.Ordinal)) return;
            targetFilePathInput = next;
            OnPropertyChanged();
            InspectCandidateInput();
        }
    }

    public string NoteInput
    {
        get => noteInput;
        set
        {
            var next = value ?? "";
            if (string.Equals(noteInput, next, StringComparison.Ordinal)) return;
            noteInput = next;
            OnPropertyChanged();
        }
    }

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

    public bool IsBusy => busy;
    public int TotalCount => allPoints.Count;
    public bool IsListEmpty => allPoints.Count == 0;
    public bool IsSearchEmpty => allPoints.Count > 0 && FilteredPoints.Count == 0;
    public bool HasCandidatePreview => candidatePreview is not null && candidatePreview.IsValid;
    public bool HasCandidateError => candidatePreview is not null && !candidatePreview.IsValid && !string.IsNullOrWhiteSpace(candidatePreview.ErrorMessage);
    public string CandidateErrorMessage => candidatePreview?.ErrorMessage ?? "";
    public string CandidateSummaryTitle => candidatePreview is { IsValid: true } p
        ? $"{p.FileName}（目录：{p.ParentFolderName}）"
        : "尚未选择配置文件";
    public string CandidateSummaryMeta => candidatePreview is { IsValid: true } p
        ? $"{p.DisplaySize} · {p.DisplayFormatSummary} · SHA-256：{p.Sha256[..12]}…"
        : "";
    public string CandidateFullPath => candidatePreview?.FullPath ?? "";
    public bool CanCreateRestorePoint => !busy && HasCandidatePreview;

    public ConfigRestorePointRecord? SelectedPoint
    {
        get => selectedPoint;
        set => SelectPoint(value);
    }

    public bool HasSelectedPoint => selectedPoint is not null && comparison is not null;
    public bool HasNoSelectedPoint => !HasSelectedPoint;
    public ConfigComparisonResult? Comparison => comparison;

    public string SelectedFileName => selectedPoint?.TargetFileName ?? "";
    public string SelectedScopedName => selectedPoint?.DisplayScopedName ?? "";
    public string SelectedTargetPath => selectedPoint?.TargetPath ?? "";
    public string SelectedNoteText => selectedPoint?.DisplayNote ?? "";
    public string SelectedCreatedTime => selectedPoint?.DisplayCreatedTime ?? "";
    public string SelectedPointId => selectedPoint?.PointId ?? "";

    public ConfigComparisonStatus ComparisonStatus => comparison?.Status ?? ConfigComparisonStatus.Identical;
    public string ComparisonStatusBadge => comparison?.Status switch
    {
        ConfigComparisonStatus.Identical => "与备份一致",
        ConfigComparisonStatus.Changed => "已变化",
        ConfigComparisonStatus.Missing => "已缺失",
        ConfigComparisonStatus.ReadFailed => "读取失败",
        ConfigComparisonStatus.BackupCorrupted => "备份损坏",
        _ => ""
    };
    public bool IsStatusIdentical => comparison?.Status == ConfigComparisonStatus.Identical;
    public bool IsStatusChanged => comparison?.Status == ConfigComparisonStatus.Changed;
    public bool IsStatusMissing => comparison?.Status == ConfigComparisonStatus.Missing;
    public bool IsStatusError => comparison?.Status is ConfigComparisonStatus.ReadFailed or ConfigComparisonStatus.BackupCorrupted;
    public string ComparisonHeadline => comparison?.StatusHeadline ?? "";
    public string ComparisonDetail => comparison?.StatusDetail ?? "";

    public string BackupSizeText => selectedPoint?.DisplaySize ?? "—";
    public string BackupFormatText => selectedPoint?.DisplayFormatSummary ?? "—";
    public string BackupSha256Text => selectedPoint?.Sha256 ?? "";

    public string LiveSizeText => comparison?.LiveSnapshot.DisplaySize ?? "—";
    public string LiveFormatText => comparison?.LiveSnapshot.DisplayFormatSummary ?? "—";
    public string LiveSha256Text => comparison?.LiveSnapshot is { Exists: true, IsReadable: true } snap ? snap.Sha256 : (comparison?.Status == ConfigComparisonStatus.Missing ? "目标文件不存在" : "无法读取");

    public bool HasTextDiff => comparison?.IsTextDiffAvailable == true && DiffLines.Count > 0;
    public bool ShowDiffFallback => comparison is not null && !HasTextDiff && !string.IsNullOrWhiteSpace(comparison.DiffFallbackNote);
    public string DiffFallbackNote => comparison?.DiffFallbackNote ?? "";
    public string DiffHeaderSummary => comparison is { IsTextDiffAvailable: true } c
        ? $"逐行差异（- 备份中原有 / + 当前文件现有 · 共 -{c.RemovedLineCount} / +{c.AddedLineCount} 行）"
        : "文件对比说明";
    public bool HasDiffTruncationNote => comparison is { IsTextDiffAvailable: true, IsDiffTruncated: true } && !string.IsNullOrWhiteSpace(comparison.DiffFallbackNote);

    public bool CanBeginRestore =>
        !busy &&
        !isConfirmingRestore &&
        comparison is { BackupIntegrityValid: true } c &&
        c.Status is ConfigComparisonStatus.Changed or ConfigComparisonStatus.Missing;

    public bool IsConfirmingRestore => isConfirmingRestore;
    public bool IsNotConfirmingRestore => !isConfirmingRestore;
    public string ConfirmRestoreTitle => selectedPoint is null ? "" : $"恢复前确认：{selectedPoint.DisplayScopedName}";
    public string ConfirmRestoreTargetPath => selectedPoint?.TargetPath ?? "";
    public string ConfirmRestoreOperationText
    {
        get
        {
            if (selectedPoint is null || comparison is null) return "";
            if (comparison.Status == ConfigComparisonStatus.Missing)
            {
                return $"即将在目标路径按备份原始字节重新创建文件（{selectedPoint.DisplaySize}，{selectedPoint.DisplayFormatSummary}，SHA-256：{selectedPoint.DisplayShortSha256}）。恢复后可点击「撤销本次恢复」移除重建文件。";
            }
            return $"即将用备份的原始字节覆盖目标文件（{selectedPoint.DisplaySize}，{selectedPoint.DisplayFormatSummary}，SHA-256：{selectedPoint.DisplayShortSha256}），严格保留原始 BOM 与换行格式。覆盖前会先保存当前版本（{comparison.LiveSnapshot.DisplaySize}，SHA-256：{comparison.LiveSnapshot.DisplayShortSha256}），恢复后可随时点击「撤销本次恢复」。";
        }
    }
    public bool HasMidConfirmWarning => !string.IsNullOrWhiteSpace(midConfirmWarning);
    public string MidConfirmWarningText => midConfirmWarning;
    public bool CanConfirmRestore => !busy && isConfirmingRestore && comparison is { BackupIntegrityValid: true } && comparison.Status is ConfigComparisonStatus.Changed or ConfigComparisonStatus.Missing;

    public bool HasOperationBanner => hasOperationBanner;
    public bool IsOperationBannerError => isOperationBannerError;
    public bool IsOperationBannerWarning => isOperationBannerWarning;
    public string OperationBannerHeadline => operationBannerHeadline;
    public string OperationBannerDetail => operationBannerDetail;

    public bool CanUndoLastRestore => !busy && latestUndo is { Consumed: false };
    public string UndoButtonText => latestUndo is { Consumed: false } u
        ? $"撤销本次恢复（回到 {(u.TargetExistedBeforeRestore ? u.DisplayPreRestoreShortSha : "恢复前缺失状态")}）"
        : "撤销本次恢复";
    public string UndoContextText => latestUndo is { Consumed: false } u
        ? $"可撤销目标：{u.TargetPath}"
        : "";

    public string StatusMessage => statusMessage;
    public string ErrorMessage => errorMessage;
    public bool HasError => errorMessage.Length > 0;

    public void Refresh()
    {
        errorMessage = "";
        try
        {
            var points = service.LoadAllPoints();
            allPoints.Clear();
            allPoints.AddRange(points);

            latestUndo = service.LoadLatestActiveUndo();

            if (selectedPoint is not null)
            {
                var updated = allPoints.FirstOrDefault(p => string.Equals(p.PointId, selectedPoint.PointId, StringComparison.OrdinalIgnoreCase));
                selectedPoint = updated;
            }

            ApplyFilter();

            if (selectedPoint is null && FilteredPoints.Count > 0)
                selectedPoint = FilteredPoints[0];

            if (selectedPoint is not null)
                RefreshSelectedComparisonCore(preserveConfirmation: false);
            else
                ClearComparisonCore();

            statusMessage = allPoints.Count == 0
                ? "还没有配置还原点。选择一个本地配置文件即可创建第一份原始字节备份。"
                : $"已加载 {allPoints.Count} 个配置还原点。";
            NotifyAll();
        }
        catch (Exception ex)
        {
            errorMessage = $"加载配置还原点失败：{ex.Message}";
            statusMessage = errorMessage;
            NotifyAll();
        }
    }

    public void SelectCandidateFile(string path)
    {
        targetFilePathInput = path ?? "";
        OnPropertyChanged(nameof(TargetFilePathInput));
        InspectCandidateInput();
    }

    public void CreateRestorePoint()
    {
        if (!CanCreateRestorePoint) return;
        busy = true;
        errorMessage = "";
        ClearOperationBanner();
        NotifyAll();

        try
        {
            var created = service.CreateRestorePoint(targetFilePathInput, noteInput);
            allPoints.Insert(0, created);
            noteInput = "";
            OnPropertyChanged(nameof(NoteInput));

            ApplyFilter();
            selectedPoint = created;
            isConfirmingRestore = false;
            confirmedLiveSnapshot = null;
            midConfirmWarning = "";
            RefreshSelectedComparisonCore(preserveConfirmation: false);

            SetOperationBanner(
                "已创建配置还原点",
                $"已保存“{created.DisplayScopedName}”的原始字节备份（{created.DisplaySize}，{created.DisplayFormatSummary}，SHA-256：{created.DisplayShortSha256}）。",
                isError: false,
                isWarning: false);
            statusMessage = $"已为 {created.TargetFileName} 创建还原点（{created.PointId}）。";
        }
        catch (Exception ex)
        {
            SetOperationBanner("创建还原点失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
        }
        finally
        {
            busy = false;
            NotifyAll();
        }
    }

    public void SelectPoint(ConfigRestorePointRecord? point)
    {
        if (ReferenceEquals(selectedPoint, point)) return;
        selectedPoint = point;
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";
        errorMessage = "";
        if (selectedPoint is not null)
        {
            RefreshSelectedComparisonCore(preserveConfirmation: false);
            statusMessage = $"已核对“{selectedPoint.DisplayScopedName}”：{comparison?.StatusHeadline}";
        }
        else
        {
            ClearComparisonCore();
        }
        NotifyAll();
    }

    public void RefreshSelectedComparison()
    {
        if (selectedPoint is null) return;
        errorMessage = "";
        RefreshSelectedComparisonCore(preserveConfirmation: false);
        statusMessage = $"已重新核对“{selectedPoint.DisplayScopedName}”：{comparison?.StatusHeadline}";
        NotifyAll();
    }

    public void BeginRestoreConfirmation()
    {
        if (selectedPoint is null || busy) return;
        errorMessage = "";
        ClearOperationBanner();
        midConfirmWarning = "";

        // Always refresh live file state right when user clicks "恢复" so confirmation shows fresh facts!
        RefreshSelectedComparisonCore(preserveConfirmation: false);
        if (comparison is null || !comparison.BackupIntegrityValid)
        {
            SetOperationBanner(
                "无法开始恢复：备份校验未通过",
                comparison?.BackupIntegrityError ?? "备份文件不可用，现有文件未被修改。",
                isError: true,
                isWarning: false);
            NotifyAll();
            return;
        }

        if (comparison.Status == ConfigComparisonStatus.Identical)
        {
            SetOperationBanner("当前文件与备份一致", "文件内容已与所选还原点逐字节一致，无需恢复。", isError: false, isWarning: false);
            NotifyAll();
            return;
        }

        if (comparison.Status == ConfigComparisonStatus.ReadFailed)
        {
            SetOperationBanner("无法开始恢复：当前文件读取失败", comparison.StatusDetail, isError: true, isWarning: false);
            NotifyAll();
            return;
        }

        isConfirmingRestore = true;
        confirmedLiveSnapshot = comparison.LiveSnapshot;
        statusMessage = $"请核对目标路径与差异后确认恢复“{selectedPoint.DisplayScopedName}”。";
        NotifyAll();
    }

    public void CancelRestoreConfirmation()
    {
        if (!isConfirmingRestore) return;
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";
        statusMessage = "已取消恢复，未修改目标文件。";
        NotifyAll();
    }

    public void ConfirmRestore()
    {
        if (busy || !isConfirmingRestore || selectedPoint is null || confirmedLiveSnapshot is null) return;
        busy = true;
        errorMessage = "";
        midConfirmWarning = "";
        NotifyAll();

        try
        {
            var result = service.RestoreFromPoint(selectedPoint, confirmedLiveSnapshot);

            if (result.RefreshedComparison is not null)
                ApplyComparisonCore(result.RefreshedComparison);

            switch (result.Outcome)
            {
                case ConfigRestoreOutcomeKind.Succeeded:
                    isConfirmingRestore = false;
                    confirmedLiveSnapshot = null;
                    latestUndo = result.UndoSnapshot;
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: false);
                    statusMessage = $"{result.Headline}（SHA-256：{selectedPoint.DisplayShortSha256}）。";
                    break;

                case ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit:
                    // Stay in confirmation mode if still restorable; update expected snapshot so user must explicitly re-confirm!
                    if (comparison is { BackupIntegrityValid: true } c &&
                        c.Status is ConfigComparisonStatus.Changed or ConfigComparisonStatus.Missing)
                    {
                        isConfirmingRestore = true;
                        confirmedLiveSnapshot = c.LiveSnapshot;
                        midConfirmWarning = $"{result.Headline}：{result.Detail}";
                    }
                    else
                    {
                        isConfirmingRestore = false;
                        confirmedLiveSnapshot = null;
                        SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    }
                    statusMessage = result.Headline;
                    break;

                case ConfigRestoreOutcomeKind.Failed:
                default:
                    isConfirmingRestore = false;
                    confirmedLiveSnapshot = null;
                    SetOperationBanner(result.Headline, result.Detail, isError: true, isWarning: false);
                    errorMessage = result.Detail;
                    statusMessage = result.Headline;
                    break;
            }
        }
        catch (Exception ex)
        {
            isConfirmingRestore = false;
            confirmedLiveSnapshot = null;
            SetOperationBanner("恢复失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
        }
        finally
        {
            busy = false;
            NotifyAll();
        }
    }

    public void UndoLastRestore()
    {
        if (!CanUndoLastRestore || latestUndo is null) return;
        busy = true;
        errorMessage = "";
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";
        NotifyAll();

        try
        {
            var result = service.UndoRestore(latestUndo);
            switch (result.Outcome)
            {
                case ConfigUndoOutcomeKind.Succeeded:
                    latestUndo = service.LoadLatestActiveUndo();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: false);
                    statusMessage = result.Headline;
                    break;

                case ConfigUndoOutcomeKind.ExternalEditConflict:
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    break;

                case ConfigUndoOutcomeKind.Failed:
                default:
                    SetOperationBanner(result.Headline, result.Detail, isError: true, isWarning: false);
                    errorMessage = result.Detail;
                    statusMessage = result.Headline;
                    break;
            }

            if (selectedPoint is not null)
                RefreshSelectedComparisonCore(preserveConfirmation: false);
        }
        catch (Exception ex)
        {
            SetOperationBanner("撤销失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
        }
        finally
        {
            busy = false;
            NotifyAll();
        }
    }

    public void ClearSearch() => SearchQuery = "";

    private void InspectCandidateInput()
    {
        if (string.IsNullOrWhiteSpace(targetFilePathInput))
        {
            candidatePreview = null;
            NotifyCandidateProperties();
            return;
        }

        candidatePreview = service.InspectCandidateFile(targetFilePathInput);
        NotifyCandidateProperties();
    }

    private void ApplyFilter()
    {
        var query = searchQuery.Trim();
        FilteredPoints.Clear();
        foreach (var point in allPoints)
        {
            if (query.Length == 0 ||
                point.TargetFileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                point.TargetPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                point.Note.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                FilteredPoints.Add(point);
            }
        }
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(IsSearchEmpty));
    }

    private void RefreshSelectedComparisonCore(bool preserveConfirmation)
    {
        if (selectedPoint is null)
        {
            ClearComparisonCore();
            return;
        }

        var result = service.CompareRestorePoint(selectedPoint);
        ApplyComparisonCore(result);
        if (!preserveConfirmation)
        {
            isConfirmingRestore = false;
            confirmedLiveSnapshot = null;
            midConfirmWarning = "";
        }
    }

    private void ApplyComparisonCore(ConfigComparisonResult result)
    {
        comparison = result;
        DiffLines.Clear();
        foreach (var line in result.DiffLines)
            DiffLines.Add(line);
    }

    private void ClearComparisonCore()
    {
        comparison = null;
        DiffLines.Clear();
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";
    }

    private void SetOperationBanner(string headline, string detail, bool isError, bool isWarning)
    {
        hasOperationBanner = true;
        operationBannerHeadline = headline;
        operationBannerDetail = detail;
        isOperationBannerError = isError;
        isOperationBannerWarning = isWarning;
    }

    private void ClearOperationBanner()
    {
        hasOperationBanner = false;
        operationBannerHeadline = "";
        operationBannerDetail = "";
        isOperationBannerError = false;
        isOperationBannerWarning = false;
    }

    private void NotifyCandidateProperties()
    {
        OnPropertyChanged(nameof(HasCandidatePreview));
        OnPropertyChanged(nameof(HasCandidateError));
        OnPropertyChanged(nameof(CandidateErrorMessage));
        OnPropertyChanged(nameof(CandidateSummaryTitle));
        OnPropertyChanged(nameof(CandidateSummaryMeta));
        OnPropertyChanged(nameof(CandidateFullPath));
        OnPropertyChanged(nameof(CanCreateRestorePoint));
    }

    private void NotifyAll()
    {
        NotifyCandidateProperties();
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(IsSearchEmpty));
        OnPropertyChanged(nameof(SelectedPoint));
        OnPropertyChanged(nameof(HasSelectedPoint));
        OnPropertyChanged(nameof(HasNoSelectedPoint));
        OnPropertyChanged(nameof(Comparison));
        OnPropertyChanged(nameof(SelectedFileName));
        OnPropertyChanged(nameof(SelectedScopedName));
        OnPropertyChanged(nameof(SelectedTargetPath));
        OnPropertyChanged(nameof(SelectedNoteText));
        OnPropertyChanged(nameof(SelectedCreatedTime));
        OnPropertyChanged(nameof(SelectedPointId));
        OnPropertyChanged(nameof(ComparisonStatus));
        OnPropertyChanged(nameof(ComparisonStatusBadge));
        OnPropertyChanged(nameof(IsStatusIdentical));
        OnPropertyChanged(nameof(IsStatusChanged));
        OnPropertyChanged(nameof(IsStatusMissing));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(ComparisonHeadline));
        OnPropertyChanged(nameof(ComparisonDetail));
        OnPropertyChanged(nameof(BackupSizeText));
        OnPropertyChanged(nameof(BackupFormatText));
        OnPropertyChanged(nameof(BackupSha256Text));
        OnPropertyChanged(nameof(LiveSizeText));
        OnPropertyChanged(nameof(LiveFormatText));
        OnPropertyChanged(nameof(LiveSha256Text));
        OnPropertyChanged(nameof(HasTextDiff));
        OnPropertyChanged(nameof(ShowDiffFallback));
        OnPropertyChanged(nameof(DiffFallbackNote));
        OnPropertyChanged(nameof(DiffHeaderSummary));
        OnPropertyChanged(nameof(HasDiffTruncationNote));
        OnPropertyChanged(nameof(CanBeginRestore));
        OnPropertyChanged(nameof(IsConfirmingRestore));
        OnPropertyChanged(nameof(IsNotConfirmingRestore));
        OnPropertyChanged(nameof(ConfirmRestoreTitle));
        OnPropertyChanged(nameof(ConfirmRestoreTargetPath));
        OnPropertyChanged(nameof(ConfirmRestoreOperationText));
        OnPropertyChanged(nameof(HasMidConfirmWarning));
        OnPropertyChanged(nameof(MidConfirmWarningText));
        OnPropertyChanged(nameof(CanConfirmRestore));
        OnPropertyChanged(nameof(HasOperationBanner));
        OnPropertyChanged(nameof(IsOperationBannerError));
        OnPropertyChanged(nameof(IsOperationBannerWarning));
        OnPropertyChanged(nameof(OperationBannerHeadline));
        OnPropertyChanged(nameof(OperationBannerDetail));
        OnPropertyChanged(nameof(CanUndoLastRestore));
        OnPropertyChanged(nameof(UndoButtonText));
        OnPropertyChanged(nameof(UndoContextText));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }
}
