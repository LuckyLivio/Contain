using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

/// <summary>
/// ViewModel for the "配置还原点" single-file config backup, directional diff, restore preview, and undo preview workflow.
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

    // Restore preview / confirmation state ("当前文件 → 恢复后")
    private bool isConfirmingRestore;
    private ConfigLiveFileSnapshot? confirmedLiveSnapshot;
    private string midConfirmWarning = "";

    // Undo preview / confirmation state ("当前文件 → 撤销后")
    private ConfigUndoSnapshot? latestUndo;
    private bool isPreviewingUndo;
    private ConfigUndoPreviewResult? undoPreview;
    private ConfigLiveFileSnapshot? confirmedUndoLiveSnapshot;

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
    public ObservableCollection<ConfigDiffLine> UndoDiffLines { get; } = [];

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
    public bool HasNoSelectedPoint => !HasSelectedPoint && !isPreviewingUndo;
    public bool ShowSelectedPointPanels => HasSelectedPoint && !isPreviewingUndo;
    public bool ShowDetailHeaderCard => HasSelectedPoint && !isConfirmingRestore && !isPreviewingUndo;
    public ConfigComparisonResult? Comparison => comparison;
    public ConfigUndoPreviewResult? UndoPreview => undoPreview;

    public string SelectedFileName => selectedPoint?.TargetFileName ?? "";
    public string SelectedScopedName => selectedPoint?.DisplayScopedName ?? "";
    public string SelectedTargetPath => selectedPoint?.TargetPath ?? "";
    public string SelectedNoteText => selectedPoint?.DisplayNote ?? "";
    public string SelectedCreatedTime => selectedPoint?.DisplayCreatedTime ?? "";
    public string SelectedPointId => selectedPoint?.PointId ?? "";

    public ConfigComparisonStatus ComparisonStatus => comparison?.Status ?? ConfigComparisonStatus.Identical;
    public ConfigDiffDirection ActiveDiffDirection => comparison?.Direction ?? ConfigDiffDirection.BackupToCurrent;
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
    public string LiveSha256Text => comparison?.LiveSnapshot is { Exists: true, IsReadable: true } snap
        ? snap.Sha256
        : (comparison?.Status == ConfigComparisonStatus.Missing ? "目标文件不存在" : "无法读取");

    public bool HasTextDiff => comparison?.IsTextDiffAvailable == true && DiffLines.Count > 0;
    public bool ShowDiffFallback => comparison is not null && !HasTextDiff && !string.IsNullOrWhiteSpace(comparison.DiffFallbackNote);
    public string DiffFallbackNote => comparison?.DiffFallbackNote ?? "";
    public string DiffDirectionBadgeText => comparison?.Direction == ConfigDiffDirection.CurrentToBackup
        ? "当前文件 → 恢复后"
        : "从备份到现在";
    public string DiffHeaderSummary
    {
        get
        {
            if (comparison is null) return "文件对比说明";
            if (!comparison.IsTextDiffAvailable)
                return comparison.Direction == ConfigDiffDirection.CurrentToBackup ? "恢复预览（当前文件 → 恢复后）" : "文件对比说明";
            return comparison.Direction == ConfigDiffDirection.CurrentToBackup
                ? $"逐行变更预览：当前文件 → 恢复后（- 移除当前 / + 写入备份 · 共 -{comparison.RemovedLineCount} / +{comparison.AddedLineCount} 行）"
                : $"逐行变化：从备份到现在（- 备份中原有 / + 当前文件现有 · 共 -{comparison.RemovedLineCount} / +{comparison.AddedLineCount} 行）";
        }
    }
    public bool HasDiffTruncationNote => comparison is { IsTextDiffAvailable: true, IsDiffTruncated: true } && !string.IsNullOrWhiteSpace(comparison.DiffFallbackNote);

    public bool CanBeginRestore =>
        !busy &&
        !isConfirmingRestore &&
        !isPreviewingUndo &&
        comparison is { BackupIntegrityValid: true } c &&
        c.Status is ConfigComparisonStatus.Changed or ConfigComparisonStatus.Missing;

    public bool IsConfirmingRestore => isConfirmingRestore;
    public bool IsNotConfirmingRestore => !isConfirmingRestore;
    public string ConfirmRestoreDirectionLabel => "当前文件 → 恢复后";
    public string ConfirmRestoreTitle => selectedPoint is null ? "" : $"恢复预览：{selectedPoint.DisplayScopedName}";
    public string ConfirmRestoreTargetPath => selectedPoint?.TargetPath ?? "";
    public string ConfirmRestoreFormatTransition
    {
        get
        {
            if (selectedPoint is null || comparison is null) return "";
            if (comparison.Status == ConfigComparisonStatus.Missing)
                return $"状态与编码：当前缺失 → 恢复后重建（{selectedPoint.DisplaySize} · {selectedPoint.DisplayFormatSummary} · SHA-256：{selectedPoint.DisplayShortSha256}）";
            return $"编码与哈希：当前 {comparison.LiveSnapshot.DisplaySize}（{comparison.LiveSnapshot.DisplayFormatSummary}，{comparison.LiveSnapshot.DisplayShortSha256}）→ 恢复后 {selectedPoint.DisplaySize}（{selectedPoint.DisplayFormatSummary}，{selectedPoint.DisplayShortSha256}）";
        }
    }
    public string ConfirmRestoreOperationText
    {
        get
        {
            if (selectedPoint is null || comparison is null) return "";
            if (comparison.Status == ConfigComparisonStatus.Missing)
                return "确认后将按备份原始字节重建该文件（保留原始 BOM 与换行格式）；之后可点击撤销预览移除本次重建的文件。";
            return "确认后将用备份原始字节覆盖目标文件（保留原始 BOM 与换行格式）；覆盖前先保存当前版本，随时可撤销。";
        }
    }
    public bool HasMidConfirmWarning => !string.IsNullOrWhiteSpace(midConfirmWarning);
    public string MidConfirmWarningText => midConfirmWarning;
    public bool CanConfirmRestore => !busy && isConfirmingRestore && comparison is { BackupIntegrityValid: true } && comparison.Status is ConfigComparisonStatus.Changed or ConfigComparisonStatus.Missing;

    // Undo bar & lightweight Undo Preview properties
    public ConfigUndoSnapshot? LatestUndo => latestUndo;
    public bool CanUndoLastRestore => !busy && latestUndo is { Consumed: false };
    public bool ShowUndoBar => CanUndoLastRestore && !isPreviewingUndo;
    public string UndoTargetScopedName => latestUndo is { Consumed: false } u ? u.DisplayTargetScopedName : "";
    public string UndoTargetFullPath => latestUndo is { Consumed: false } u ? u.TargetPath : "";
    public bool IsUndoTargetDifferentFromSelected =>
        latestUndo is { Consumed: false } u &&
        selectedPoint is not null &&
        !string.Equals(u.TargetPath, selectedPoint.TargetPath, StringComparison.OrdinalIgnoreCase);
    public string UndoCrossTargetNotice => IsUndoTargetDifferentFromSelected && latestUndo is not null && selectedPoint is not null
        ? $"注意：最近一次可撤销操作指向「{latestUndo.DisplayTargetScopedName}」，与当前左侧选中的「{selectedPoint.DisplayScopedName}」不是同一个文件。"
        : "";
    public string UndoBarHeadline => latestUndo is { Consumed: false } u
        ? (u.TargetExistedBeforeRestore
            ? $"可撤销对「{u.DisplayTargetScopedName}」的上次恢复（覆盖前版本已保留）"
            : $"可撤销对「{u.DisplayTargetScopedName}」的上次恢复（原本缺失，恢复时重建）")
        : "";
    public string UndoButtonText => latestUndo is { Consumed: false } u
        ? $"撤销对 {u.DisplayTargetScopedName} 的恢复…"
        : "撤销本次恢复…";
    public string UndoContextText => latestUndo is { Consumed: false } u
        ? $"实际操作目标：{u.TargetPath}"
        : "";

    public bool IsPreviewingUndo => isPreviewingUndo && undoPreview is not null;
    public string UndoPreviewTitle => undoPreview is null ? "" : $"撤销预览：{undoPreview.UndoSnapshot.DisplayTargetScopedName}";
    public string UndoPreviewDirectionLabel => undoPreview?.DirectionLabel ?? "当前文件 → 撤销后";
    public string UndoPreviewTargetPath => undoPreview?.UndoSnapshot.TargetPath ?? "";
    public string UndoPreviewHeadline => undoPreview?.Headline ?? "";
    public string UndoPreviewDetail => undoPreview?.Detail ?? "";
    public string UndoPreviewActionSummary => undoPreview?.ActionSummary ?? "";
    public bool IsUndoPreviewConflict => undoPreview?.IsConflictOrError == true;
    public bool IsUndoPreviewRemoveRecreated => undoPreview?.Status == ConfigUndoPreviewStatus.ReadyToRemoveRecreatedFile;
    public bool CanConfirmUndo => !busy && undoPreview is { CanConfirm: true };
    public string ConfirmUndoButtonText => IsUndoPreviewRemoveRecreated
        ? "确认撤销（移除本次重建的文件）"
        : "确认撤销（写回覆盖前原始字节）";
    public string UndoPreviewFormatTransition
    {
        get
        {
            if (undoPreview is null) return "";
            if (undoPreview.Status == ConfigUndoPreviewStatus.ReadyToRemoveRecreatedFile)
                return $"状态与哈希：当前已重建文件（{undoPreview.LiveSnapshot.DisplaySize}，{undoPreview.LiveSnapshot.DisplayFormatSummary}，{undoPreview.LiveSnapshot.DisplayShortSha256}）→ 撤销后移除";
            if (undoPreview.IsConflictOrError)
                return $"恢复写入版本：{undoPreview.UndoSnapshot.DisplayRestoredShortSha} · 当前状态：{(undoPreview.LiveSnapshot.Exists ? $"{undoPreview.LiveSnapshot.DisplaySize}（{undoPreview.LiveSnapshot.DisplayShortSha256}）" : "文件已不存在")}";
            return $"编码与哈希：当前 {undoPreview.LiveSnapshot.DisplaySize}（{undoPreview.LiveSnapshot.DisplayFormatSummary}，{undoPreview.LiveSnapshot.DisplayShortSha256}）→ 撤销后 {undoPreview.PreRestoreDisplaySize}（{undoPreview.PreRestoreDisplayFormatSummary}，{undoPreview.UndoSnapshot.DisplayPreRestoreShortSha}）";
        }
    }
    public bool HasUndoTextDiff => undoPreview?.IsTextDiffAvailable == true && UndoDiffLines.Count > 0;
    public bool ShowUndoDiffFallback => undoPreview is not null && !HasUndoTextDiff && !string.IsNullOrWhiteSpace(undoPreview.DiffFallbackNote);
    public string UndoDiffFallbackNote => undoPreview?.DiffFallbackNote ?? "";
    public string UndoDiffHeaderSummary => undoPreview is { IsTextDiffAvailable: true } p
        ? $"逐行变更预览：当前文件 → 撤销后（- 移除当前 / + 写回覆盖前 · 共 -{p.RemovedLineCount} / +{p.AddedLineCount} 行）"
        : (IsUndoPreviewConflict ? "冲突说明（已保住用户最新状态）" : "撤销变更说明");
    public bool HasUndoDiffTruncationNote => undoPreview is { IsTextDiffAvailable: true, IsDiffTruncated: true } && !string.IsNullOrWhiteSpace(undoPreview.DiffFallbackNote);

    public bool HasOperationBanner => hasOperationBanner;
    public bool IsOperationBannerError => isOperationBannerError;
    public bool IsOperationBannerWarning => isOperationBannerWarning;
    public string OperationBannerHeadline => operationBannerHeadline;
    public string OperationBannerDetail => operationBannerDetail;

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
            ClearUndoPreviewCore();

            if (selectedPoint is not null)
                selectedPoint = allPoints.FirstOrDefault(p => string.Equals(p.PointId, selectedPoint.PointId, StringComparison.OrdinalIgnoreCase));

            ApplyFilter();

            if (selectedPoint is null && FilteredPoints.Count > 0)
                selectedPoint = FilteredPoints[0];

            if (selectedPoint is not null)
                RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
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
        ClearUndoPreviewCore();
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
            RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);

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
        ClearUndoPreviewCore();
        errorMessage = "";
        if (selectedPoint is not null)
        {
            RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
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
        ClearUndoPreviewCore();
        RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
        statusMessage = $"已重新核对“{selectedPoint.DisplayScopedName}”：{comparison?.StatusHeadline}";
        NotifyAll();
    }

    public void BeginRestoreConfirmation()
    {
        if (selectedPoint is null || busy) return;
        errorMessage = "";
        ClearOperationBanner();
        ClearUndoPreviewCore();
        midConfirmWarning = "";

        // Entering restore confirmation flips diff direction to "当前文件 → 恢复后"!
        RefreshSelectedComparisonCore(ConfigDiffDirection.CurrentToBackup, preserveConfirmation: false);
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
            RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
            SetOperationBanner("当前文件与备份一致", "文件内容已与所选还原点逐字节一致，无需恢复。", isError: false, isWarning: false);
            NotifyAll();
            return;
        }

        if (comparison.Status == ConfigComparisonStatus.ReadFailed)
        {
            RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
            SetOperationBanner("无法开始恢复：当前文件读取失败", comparison.StatusDetail, isError: true, isWarning: false);
            NotifyAll();
            return;
        }

        isConfirmingRestore = true;
        confirmedLiveSnapshot = comparison.LiveSnapshot;
        statusMessage = $"当前文件 → 恢复后：请核对目标路径与下方差异后确认恢复“{selectedPoint.DisplayScopedName}”。";
        NotifyAll();
    }

    public void CancelRestoreConfirmation()
    {
        if (!isConfirmingRestore) return;
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";
        if (selectedPoint is not null)
            RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
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

    public void BeginUndoPreview()
    {
        if (!CanUndoLastRestore || latestUndo is null) return;
        errorMessage = "";
        ClearOperationBanner();
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";

        try
        {
            var preview = service.InspectUndoPreview(latestUndo);
            ApplyUndoPreviewCore(preview);
            statusMessage = preview.CanConfirm
                ? $"当前文件 → 撤销后：请核对实际目标“{preview.UndoSnapshot.DisplayTargetScopedName}”后确认撤销。"
                : preview.Headline;
        }
        catch (Exception ex)
        {
            ClearUndoPreviewCore();
            SetOperationBanner("加载撤销预览失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
        }
        finally
        {
            NotifyAll();
        }
    }

    public void CancelUndoPreview()
    {
        if (!isPreviewingUndo) return;
        ClearUndoPreviewCore();
        if (selectedPoint is not null)
            RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
        statusMessage = "已取消撤销预览，未写入磁盘，撤销记录已保留。";
        NotifyAll();
    }

    public void ConfirmUndoRestore()
    {
        if (busy || !isPreviewingUndo || latestUndo is null || undoPreview is null || !undoPreview.CanConfirm) return;
        busy = true;
        errorMessage = "";
        NotifyAll();

        try
        {
            var result = service.UndoRestore(latestUndo, confirmedUndoLiveSnapshot);
            switch (result.Outcome)
            {
                case ConfigUndoOutcomeKind.Succeeded:
                    ClearUndoPreviewCore();
                    latestUndo = service.LoadLatestActiveUndo();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: false);
                    statusMessage = result.Headline;
                    if (selectedPoint is not null)
                        RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
                    break;

                case ConfigUndoOutcomeKind.ExternalEditConflict:
                case ConfigUndoOutcomeKind.TargetDeletedConflict:
                    // Keep Undo Preview open with refreshed conflict facts; preserve user's latest state and do NOT consume undo record!
                    if (result.RefreshedUndoPreview is not null)
                        ApplyUndoPreviewCore(result.RefreshedUndoPreview);
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    break;

                case ConfigUndoOutcomeKind.Failed:
                default:
                    if (result.RefreshedUndoPreview is not null)
                        ApplyUndoPreviewCore(result.RefreshedUndoPreview);
                    SetOperationBanner(result.Headline, result.Detail, isError: true, isWarning: false);
                    errorMessage = result.Detail;
                    statusMessage = result.Headline;
                    break;
            }
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

    private void RefreshSelectedComparisonCore(ConfigDiffDirection direction, bool preserveConfirmation)
    {
        if (selectedPoint is null)
        {
            ClearComparisonCore();
            return;
        }

        var result = service.CompareRestorePoint(selectedPoint, direction);
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

    private void ApplyUndoPreviewCore(ConfigUndoPreviewResult preview)
    {
        undoPreview = preview;
        isPreviewingUndo = true;
        confirmedUndoLiveSnapshot = preview.LiveSnapshot;
        UndoDiffLines.Clear();
        foreach (var line in preview.DiffLines)
            UndoDiffLines.Add(line);
    }

    private void ClearUndoPreviewCore()
    {
        isPreviewingUndo = false;
        undoPreview = null;
        confirmedUndoLiveSnapshot = null;
        UndoDiffLines.Clear();
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
        OnPropertyChanged(nameof(ShowSelectedPointPanels));
        OnPropertyChanged(nameof(ShowDetailHeaderCard));
        OnPropertyChanged(nameof(Comparison));
        OnPropertyChanged(nameof(UndoPreview));
        OnPropertyChanged(nameof(SelectedFileName));
        OnPropertyChanged(nameof(SelectedScopedName));
        OnPropertyChanged(nameof(SelectedTargetPath));
        OnPropertyChanged(nameof(SelectedNoteText));
        OnPropertyChanged(nameof(SelectedCreatedTime));
        OnPropertyChanged(nameof(SelectedPointId));
        OnPropertyChanged(nameof(ComparisonStatus));
        OnPropertyChanged(nameof(ActiveDiffDirection));
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
        OnPropertyChanged(nameof(DiffDirectionBadgeText));
        OnPropertyChanged(nameof(DiffHeaderSummary));
        OnPropertyChanged(nameof(HasDiffTruncationNote));
        OnPropertyChanged(nameof(CanBeginRestore));
        OnPropertyChanged(nameof(IsConfirmingRestore));
        OnPropertyChanged(nameof(IsNotConfirmingRestore));
        OnPropertyChanged(nameof(ConfirmRestoreDirectionLabel));
        OnPropertyChanged(nameof(ConfirmRestoreTitle));
        OnPropertyChanged(nameof(ConfirmRestoreTargetPath));
        OnPropertyChanged(nameof(ConfirmRestoreFormatTransition));
        OnPropertyChanged(nameof(ConfirmRestoreOperationText));
        OnPropertyChanged(nameof(HasMidConfirmWarning));
        OnPropertyChanged(nameof(MidConfirmWarningText));
        OnPropertyChanged(nameof(CanConfirmRestore));
        OnPropertyChanged(nameof(LatestUndo));
        OnPropertyChanged(nameof(CanUndoLastRestore));
        OnPropertyChanged(nameof(ShowUndoBar));
        OnPropertyChanged(nameof(UndoTargetScopedName));
        OnPropertyChanged(nameof(UndoTargetFullPath));
        OnPropertyChanged(nameof(IsUndoTargetDifferentFromSelected));
        OnPropertyChanged(nameof(UndoCrossTargetNotice));
        OnPropertyChanged(nameof(UndoBarHeadline));
        OnPropertyChanged(nameof(UndoButtonText));
        OnPropertyChanged(nameof(UndoContextText));
        OnPropertyChanged(nameof(IsPreviewingUndo));
        OnPropertyChanged(nameof(UndoPreviewTitle));
        OnPropertyChanged(nameof(UndoPreviewDirectionLabel));
        OnPropertyChanged(nameof(UndoPreviewTargetPath));
        OnPropertyChanged(nameof(UndoPreviewHeadline));
        OnPropertyChanged(nameof(UndoPreviewDetail));
        OnPropertyChanged(nameof(UndoPreviewActionSummary));
        OnPropertyChanged(nameof(IsUndoPreviewConflict));
        OnPropertyChanged(nameof(IsUndoPreviewRemoveRecreated));
        OnPropertyChanged(nameof(CanConfirmUndo));
        OnPropertyChanged(nameof(ConfirmUndoButtonText));
        OnPropertyChanged(nameof(UndoPreviewFormatTransition));
        OnPropertyChanged(nameof(HasUndoTextDiff));
        OnPropertyChanged(nameof(ShowUndoDiffFallback));
        OnPropertyChanged(nameof(UndoDiffFallbackNote));
        OnPropertyChanged(nameof(UndoDiffHeaderSummary));
        OnPropertyChanged(nameof(HasUndoDiffTruncationNote));
        OnPropertyChanged(nameof(HasOperationBanner));
        OnPropertyChanged(nameof(IsOperationBannerError));
        OnPropertyChanged(nameof(IsOperationBannerWarning));
        OnPropertyChanged(nameof(OperationBannerHeadline));
        OnPropertyChanged(nameof(OperationBannerDetail));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }
}
