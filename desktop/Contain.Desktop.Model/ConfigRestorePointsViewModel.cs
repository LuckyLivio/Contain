using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Contain.Desktop.Model;

/// <summary>
/// ViewModel for the "配置还原点" single-file config backup, directional diff, restore/undo preview,
/// and crash-interruption recovery workflow.
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

    // Interrupted operation reconciliation & original-copy recovery state
    private ConfigRecoveryReport? activeRecoveryReport;
    private bool isViewingOriginalCopy;
    private ConfigOriginalCopyPreview? originalCopyPreview;
    private ConfigUndoSnapshot? originalCopySource;

    // Portable restore point export ("导出还原点…") state
    private bool isExportingPackage;
    private string exportPackagePathInput = "";

    // Portable restore point import ("导入还原点…") state
    private bool isImportingPackage;
    private string importPackagePathInput = "";
    private string importTargetPathInput = "";
    private ConfigPortablePackageInspection? importedPackage;
    private ConfigImportPreviewResult? importPreview;
    private ConfigLiveFileSnapshot? confirmedImportLiveSnapshot;
    private string importMidConfirmWarning = "";

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
    public ObservableCollection<ConfigCopyPreviewLine> OriginalCopyLines { get; } = [];
    public ObservableCollection<ConfigDiffLine> ImportDiffLines { get; } = [];

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
    public bool ShowCreateCard => !isImportingPackage;
    public bool HasNoSelectedPoint => !HasSelectedPoint && !IsPreviewingUndo && !IsViewingOriginalCopy && !HasRecoveryCard && !isImportingPackage;
    public bool ShowSelectedPointPanels => HasSelectedPoint && !IsPreviewingUndo && !IsViewingOriginalCopy && !isImportingPackage;
    public bool ShowDetailHeaderCard => HasSelectedPoint && !isConfirmingRestore && !isExportingPackage && !IsPreviewingUndo && !IsViewingOriginalCopy && !isImportingPackage;
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
        !isExportingPackage &&
        !isPreviewingUndo &&
        !isViewingOriginalCopy &&
        !isImportingPackage &&
        !IsSelectedTargetBusyInAnotherWindow &&
        comparison is { BackupIntegrityValid: true } c &&
        c.Status is ConfigComparisonStatus.Changed or ConfigComparisonStatus.Missing;

    // Portable Restore Point Export ("导出还原点…" — exports saved backup A, distinct from "另存原副本…" for pre-restore B)
    public bool CanBeginExportRestorePoint =>
        !busy &&
        !isConfirmingRestore &&
        !isExportingPackage &&
        !isPreviewingUndo &&
        !isViewingOriginalCopy &&
        !isImportingPackage &&
        selectedPoint is not null &&
        comparison is { BackupIntegrityValid: true };

    public bool IsExportingPackage => isExportingPackage && selectedPoint is not null;
    public bool ShowExportPackageCard => ShowSelectedPointPanels && IsExportingPackage;
    public string ExportPackagePathInput
    {
        get => exportPackagePathInput;
        set
        {
            var next = value ?? "";
            if (string.Equals(exportPackagePathInput, next, StringComparison.Ordinal)) return;
            exportPackagePathInput = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmExportPackage));
        }
    }
    public bool CanConfirmExportPackage => !busy && IsExportingPackage && !string.IsNullOrWhiteSpace(exportPackagePathInput);
    public string SuggestedPortablePackageFileName => ConfigFormatting.BuildSuggestedPortablePackageFileName(selectedPoint?.TargetFileName);
    public string ExportPackageTitle => selectedPoint is null ? "" : $"导出还原点迁移包：{selectedPoint.DisplayScopedName}";
    public string ExportPackageVersionSummary
    {
        get
        {
            if (selectedPoint is null) return "";
            if (selectedPoint.ByteLength > ConfigPortablePackageConstants.MaxRawPayloadBytes)
            {
                return $"导出版本：{selectedPoint.TargetFileName} · {selectedPoint.DisplaySize} · SHA-256：{selectedPoint.DisplayShortSha256}（提示：这份还原点仍可在本机恢复，超过 32 MiB 的配置暂不支持迁移）";
            }
            var diskNote = comparison?.Status switch
            {
                ConfigComparisonStatus.Changed => "（当前磁盘文件已有修改，导出仍使用建点时保存的备份版本）",
                ConfigComparisonStatus.Missing => "（当前磁盘文件已缺失，导出仍使用建点时保存的备份版本）",
                ConfigComparisonStatus.ReadFailed => "（当前磁盘文件不可读，导出仍使用建点时保存的备份版本）",
                _ => "（导出的是所选还原点保存的原始字节备份，并非恢复冲突时的「覆盖前原副本」）"
            };
            return $"导出版本：{selectedPoint.TargetFileName} · {selectedPoint.DisplaySize} · {selectedPoint.DisplayFormatSummary} · SHA-256：{selectedPoint.DisplayShortSha256} {diskNote}";
        }
    }
    public string ExportPackagePrivacyNotice =>
        "隐私提醒：配置文件可能包含令牌、账号、密钥或私人路径。Contain 会原样保留文件原始字节以确保恢复逐字节一致；迁移包不包含源机目录路径或撤销记录，传输前请妥善保管。";

    // Portable Restore Point Import ("导入还原点…")
    public bool CanOpenImportWorkspace => !busy;
    public bool IsImportingPackage => isImportingPackage;
    public bool ShowImportPanel => isImportingPackage;
    public string ImportPackagePathInput
    {
        get => importPackagePathInput;
        set
        {
            var next = value ?? "";
            if (string.Equals(importPackagePathInput, next, StringComparison.Ordinal)) return;
            importPackagePathInput = next;
            OnPropertyChanged();
            InspectImportedPackageInput();
        }
    }
    public string ImportTargetPathInput
    {
        get => importTargetPathInput;
        set
        {
            var next = value ?? "";
            if (string.Equals(importTargetPathInput, next, StringComparison.Ordinal)) return;
            importTargetPathInput = next;
            OnPropertyChanged();
            RefreshImportTargetPreviewCore();
        }
    }
    public ConfigPortablePackageInspection? ImportedPackage => importedPackage;
    public ConfigImportPreviewResult? ImportPreview => importPreview;
    public bool HasValidImportedPackage => importedPackage is { IsValid: true };
    public bool HasInvalidImportedPackage => importedPackage is { IsValid: false } && !string.IsNullOrWhiteSpace(importedPackage.ErrorMessage);
    public string ImportedPackageErrorMessage => importedPackage?.ErrorMessage ?? "";
    public string ImportedPackageDisplayFileName => importedPackage is { IsValid: true } p ? p.DisplayFileName : "";
    public string ImportedPackageVerifiedHeadline => importedPackage is { IsValid: true } p
        ? $"校验通过（v{p.SchemaVersion}）· {p.DisplayFileName} · {p.DisplaySize} · {p.DisplayFormatSummary} · SHA-256：{p.DisplayShortSha256}"
        : "";
    public string ImportedPackageVerifiedMeta => importedPackage is { IsValid: true } p
        ? $"备注：{p.DisplayNote} · 包内文件名仅作展示，写入位置由下方本机目标决定"
        : "";

    public bool HasImportPreview => HasValidImportedPackage && importPreview is not null;
    public bool IsImportReadyToCreate => importPreview?.Status == ConfigImportTargetStatus.ReadyToCreate;
    public bool IsImportReadyToOverwrite => importPreview?.Status == ConfigImportTargetStatus.ReadyToOverwrite;
    public bool IsImportIdentical => importPreview?.Status == ConfigImportTargetStatus.IdenticalToTarget;
    public bool IsImportPreviewError => importPreview?.IsErrorOrBlocked == true;
    public string ImportPreviewTitle => importedPackage is { IsValid: true } p && importPreview is not null
        ? $"导入预览：{ConfigFormatting.FormatScopedName(importPreview.TargetPath)}"
        : "导入预览";
    public string ImportPreviewDirectionLabel => importPreview?.DirectionLabel ?? "本机当前 → 恢复后";
    public string ImportPreviewTargetPath => importPreview?.TargetPath ?? "";
    public string ImportPreviewHeadline => importPreview?.Headline ?? "";
    public string ImportPreviewFormatTransition
    {
        get
        {
            if (importedPackage is not { IsValid: true } pkg || importPreview is null) return "";
            if (importPreview.Status == ConfigImportTargetStatus.ReadyToCreate)
                return $"状态：当前缺失（将新建）→ 恢复后 {pkg.DisplaySize}（{pkg.DisplayFormatSummary}，{pkg.DisplayShortSha256}）";
            if (importPreview.Status == ConfigImportTargetStatus.IdenticalToTarget)
                return $"校验：本机当前与迁移包逐字节一致（{pkg.DisplaySize} · {pkg.DisplayFormatSummary} · {pkg.DisplayShortSha256}）";
            if (importPreview.IsErrorOrBlocked)
                return importPreview.Detail;
            return $"格式与哈希：{importPreview.LiveSnapshot.DisplaySize}（{importPreview.LiveSnapshot.DisplayFormatSummary}，{importPreview.LiveSnapshot.DisplayShortSha256}）→ {pkg.DisplaySize}（{pkg.DisplayFormatSummary}，{pkg.DisplayShortSha256}）";
        }
    }
    public string ImportPreviewActionSummary => importPreview?.ActionSummary ?? "";
    public bool HasImportMidConfirmWarning => !string.IsNullOrWhiteSpace(importMidConfirmWarning);
    public string ImportMidConfirmWarningText => importMidConfirmWarning;
    public bool CanConfirmImportRestore => !busy && HasValidImportedPackage && importPreview is { CanConfirm: true };
    public string ConfirmImportRestoreButtonText => IsImportReadyToCreate
        ? "确认恢复（新建目标文件）"
        : "确认恢复（覆盖并保留本机撤销副本）";
    public bool HasImportTextDiff => importPreview is { IsTextDiffAvailable: true } && ImportDiffLines.Count > 0;
    public bool ShowImportDiffFallback => importPreview is not null && !HasImportTextDiff && !string.IsNullOrWhiteSpace(importPreview.DiffFallbackNote);
    public string ImportDiffFallbackNote => importPreview?.DiffFallbackNote ?? "";
    public string ImportDiffHeaderSummary => importPreview switch
    {
        { IsTextDiffAvailable: true } p => $"逐行变更预览：本机当前 → 恢复后（- 移除本机 / + 写入迁移包 · 共 -{p.RemovedLineCount} / +{p.AddedLineCount} 行）",
        { Status: ConfigImportTargetStatus.ReadyToCreate } => "新建目标文件预览（本机当前缺失 → 恢复后新建）",
        _ => "导入对比说明"
    };
    public bool HasImportDiffTruncationNote => importPreview is { IsTextDiffAvailable: true, IsDiffTruncated: true } && !string.IsNullOrWhiteSpace(importPreview.DiffFallbackNote);

    public bool IsSelectedTargetBusyInAnotherWindow =>
        selectedPoint is not null &&
        (activeRecoveryReport is { Status: ConfigRecoveryStatus.ActiveInAnotherWindow } r &&
         string.Equals(r.OperationRecord.TargetPath, selectedPoint.TargetPath, StringComparison.OrdinalIgnoreCase)
         || service.IsTargetBusyInAnotherWindow(selectedPoint.TargetPath, out _));

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
                return "将按备份原始字节重建文件（保留原 BOM 与换行）；之后可经撤销预览移除。";
            return "将用备份原始字节覆盖目标（保留原 BOM 与换行）；覆盖前自动保存撤销副本。";
        }
    }
    public bool HasMidConfirmWarning => !string.IsNullOrWhiteSpace(midConfirmWarning);
    public string MidConfirmWarningText => midConfirmWarning;
    public bool CanConfirmRestore => !busy && isConfirmingRestore && comparison is { BackupIntegrityValid: true } && comparison.Status is ConfigComparisonStatus.Changed or ConfigComparisonStatus.Missing;

    // Interruption recovery card & shell banner properties
    public ConfigRecoveryReport? ActiveRecoveryReport => activeRecoveryReport;
    public bool HasRecoveryCard => activeRecoveryReport is not null && activeRecoveryReport.Status != ConfigRecoveryStatus.None;
    public bool ShowRecoveryCard => HasRecoveryCard && !IsPreviewingUndo && !IsViewingOriginalCopy && !isImportingPackage;
    public bool HasPendingRecoveryShellBanner => HasRecoveryCard;
    public string PendingRecoveryShellBannerText => activeRecoveryReport is null
        ? ""
        : $"{activeRecoveryReport.StatusBadge}：{activeRecoveryReport.Headline}（启动检查未修改目标文件）";

    public ConfigRecoveryStatus RecoveryStatus => activeRecoveryReport?.Status ?? ConfigRecoveryStatus.None;
    public string RecoveryStatusBadge => activeRecoveryReport?.StatusBadge ?? "";
    public string RecoveryHeadline => activeRecoveryReport?.Headline ?? "";
    public string RecoveryDetail => activeRecoveryReport?.Detail ?? "";
    public string RecoveryTargetPath => activeRecoveryReport?.OperationRecord.TargetPath ?? "";
    public string RecoveryDiskStateSummary => activeRecoveryReport?.DiskStateSummary ?? "";
    public string RecoveryOriginalCopySummary => activeRecoveryReport?.OriginalCopySummary ?? "";
    public bool IsRecoveryWarning => activeRecoveryReport?.Status is ConfigRecoveryStatus.ActiveInAnotherWindow or ConfigRecoveryStatus.ExternalVersionCDetected or ConfigRecoveryStatus.TargetMissingAfterInterruption or ConfigRecoveryStatus.NeedsVerification;
    public bool IsRecoveryInfo => activeRecoveryReport?.Status is ConfigRecoveryStatus.RestoreAppliedNeedsReview or ConfigRecoveryStatus.UndoNotApplied;
    public bool IsRecoveryResolved => activeRecoveryReport?.Status is ConfigRecoveryStatus.RestoreNotApplied or ConfigRecoveryStatus.UndoAlreadyApplied;
    public bool CanUndoFromRecovery => !busy && activeRecoveryReport is { CanOfferUndoPreview: true };
    public bool CanViewOriginalCopyFromRecovery => !busy && activeRecoveryReport is { HasValidOriginalCopy: true };
    public bool CanExportOriginalCopyFromRecovery => !busy && activeRecoveryReport is { HasValidOriginalCopy: true };
    public bool CanRecheckRecovery => !busy && activeRecoveryReport is { RequiresExplicitArchive: true } or { Status: ConfigRecoveryStatus.ActiveInAnotherWindow };
    public string DismissRecoveryButtonText => activeRecoveryReport switch
    {
        { RequiresExplicitArchive: true } => "已知悉并归档",
        { Status: ConfigRecoveryStatus.ActiveInAnotherWindow } => "收起提示",
        _ => "确认并收起"
    };

    // Inline original copy preview ("查看原副本")
    public bool IsViewingOriginalCopy => isViewingOriginalCopy && originalCopyPreview is not null;
    public ConfigOriginalCopyPreview? OriginalCopyPreview => originalCopyPreview;
    public string OriginalCopyPreviewTitle => originalCopyPreview is null
        ? ""
        : $"覆盖前原副本：{originalCopyPreview.UndoSnapshot.DisplayTargetScopedName}";
    public string OriginalCopyTargetPath => originalCopyPreview?.UndoSnapshot.TargetPath ?? "";
    public string OriginalCopyMetaSummary => originalCopyPreview is { IsValid: true } p
        ? $"大小与编码：{p.DisplaySize} · {p.DisplayFormatSummary} · SHA-256：{p.Sha256}"
        : (originalCopyPreview?.ErrorMessage ?? "");
    public bool HasOriginalCopyText => originalCopyPreview is { IsValid: true, IsTextAvailable: true } && OriginalCopyLines.Count > 0;
    public bool ShowOriginalCopyFallback => originalCopyPreview is not null && !HasOriginalCopyText && !string.IsNullOrWhiteSpace(originalCopyPreview.FallbackNote);
    public string OriginalCopyFallbackNote => originalCopyPreview?.FallbackNote ?? "";
    public bool CanExportFromOriginalCopyPreview => !busy && originalCopyPreview is { IsValid: true };
    public string SuggestedOriginalCopyFileName
    {
        get
        {
            var src = ResolveActiveOriginalCopySource();
            return ConfigFormatting.BuildSuggestedOriginalCopyFileName(src?.TargetPath);
        }
    }

    // Undo bar & lightweight Undo Preview properties
    public ConfigUndoSnapshot? LatestUndo => latestUndo;
    public bool CanUndoLastRestore => !busy && latestUndo is { Consumed: false };
    public bool ShowUndoBar => CanUndoLastRestore && !IsPreviewingUndo && !IsViewingOriginalCopy && !isImportingPackage && !(ShowRecoveryCard && CanUndoFromRecovery);
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
    public bool ShowUndoPreviewPanel => IsPreviewingUndo && !IsViewingOriginalCopy && !isImportingPackage;
    public string UndoPreviewTitle => undoPreview is null ? "" : $"撤销预览：{undoPreview.UndoSnapshot.DisplayTargetScopedName}";
    public string UndoPreviewDirectionLabel => undoPreview?.DirectionLabel ?? "当前文件 → 撤销后";
    public string UndoPreviewTargetPath => undoPreview?.UndoSnapshot.TargetPath ?? "";
    public string UndoPreviewHeadline => undoPreview?.Headline ?? "";
    public string UndoPreviewDetail => undoPreview?.Detail ?? "";
    public string UndoPreviewActionSummary => undoPreview?.ActionSummary ?? "";
    public bool IsUndoPreviewConflict => undoPreview?.IsConflictOrError == true;
    public bool IsUndoPreviewRemoveRecreated => undoPreview?.Status == ConfigUndoPreviewStatus.ReadyToRemoveRecreatedFile;
    public bool CanConfirmUndo => !busy && undoPreview is { CanConfirm: true };
    public bool CanViewOriginalCopyFromUndoPreview => !busy && undoPreview is { IsConflictOrError: true, HasValidOriginalCopy: true };
    public bool CanExportOriginalCopyFromUndoPreview => !busy && undoPreview is { IsConflictOrError: true, HasValidOriginalCopy: true };
    public string ConfirmUndoButtonText => IsUndoPreviewRemoveRecreated
        ? "确认撤销（移除重建文件）"
        : "确认撤销（写回覆盖前版本）";
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

    /// <summary>
    /// Runs at Contain startup. Strictly read-only against user target files; reconciles any
    /// interrupted restore/undo records and surfaces a shell banner if attention is needed.
    /// </summary>
    public void CheckInterruptedOperationOnStartup()
    {
        try
        {
            var report = service.ReconcileLatestOperation();
            if (report is not null)
                activeRecoveryReport = report;
            else if (activeRecoveryReport is { Status: ConfigRecoveryStatus.ActiveInAnotherWindow })
                activeRecoveryReport = null;
            latestUndo = service.LoadLatestActiveUndo();
            NotifyAll();
        }
        catch
        {
            // Non-fatal startup probe; full Refresh surfaces any storage errors.
        }
    }

    public void Refresh()
    {
        errorMessage = "";
        try
        {
            var freshReport = service.ReconcileLatestOperation();
            if (freshReport is not null)
                activeRecoveryReport = freshReport;
            else if (activeRecoveryReport is { RequiresExplicitArchive: true } or { Status: ConfigRecoveryStatus.ActiveInAnotherWindow })
                activeRecoveryReport = null;

            var points = service.LoadAllPoints();
            allPoints.Clear();
            allPoints.AddRange(points);

            latestUndo = service.LoadLatestActiveUndo();
            ClearUndoPreviewCore();
            ClearOriginalCopyPreviewCore();
            ClearExportPackageCore();
            ClearImportWorkspaceCore();

            if (selectedPoint is not null)
                selectedPoint = allPoints.FirstOrDefault(p => string.Equals(p.PointId, selectedPoint.PointId, StringComparison.OrdinalIgnoreCase));

            if (selectedPoint is null && activeRecoveryReport is not null)
            {
                selectedPoint = allPoints.FirstOrDefault(p =>
                    string.Equals(p.PointId, activeRecoveryReport.OperationRecord.RestoredPointId, StringComparison.OrdinalIgnoreCase))
                    ?? allPoints.FirstOrDefault(p =>
                        string.Equals(p.TargetPath, activeRecoveryReport.OperationRecord.TargetPath, StringComparison.OrdinalIgnoreCase));
            }

            ApplyFilter();

            if (selectedPoint is null && FilteredPoints.Count > 0)
                selectedPoint = FilteredPoints[0];

            if (selectedPoint is not null)
                RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
            else
                ClearComparisonCore();

            statusMessage = activeRecoveryReport is not null
                ? activeRecoveryReport.Headline
                : (allPoints.Count == 0
                    ? "还没有配置还原点。选择一个本地配置文件即可创建第一份原始字节备份。"
                    : $"已加载 {allPoints.Count} 个配置还原点。");
            NotifyAll();
        }
        catch (Exception ex)
        {
            errorMessage = $"加载配置还原点失败：{ex.Message}";
            statusMessage = errorMessage;
            NotifyAll();
        }
    }

    public void RecheckRecovery()
    {
        if (busy) return;
        errorMessage = "";
        ClearOperationBanner();
        ClearOriginalCopyPreviewCore();
        try
        {
            var fresh = service.ReconcileLatestOperation();
            activeRecoveryReport = fresh;
            latestUndo = service.LoadLatestActiveUndo();
            if (selectedPoint is not null)
                RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
            statusMessage = fresh is not null
                ? $"已重新核对：{fresh.StatusBadge}。"
                : "已重新核对，当前没有待处理的中断操作。";
        }
        catch (Exception ex)
        {
            SetOperationBanner("重新核对失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
        }
        finally
        {
            NotifyAll();
        }
    }

    public void BeginUndoPreviewFromRecovery()
    {
        if (busy || activeRecoveryReport is not { CanOfferUndoPreview: true }) return;
        var targetUndo = latestUndo ?? activeRecoveryReport.OperationRecord;
        latestUndo = targetUndo;
        BeginUndoPreview();
    }

    public void DismissOrArchiveRecovery()
    {
        if (busy || activeRecoveryReport is null) return;
        errorMessage = "";
        ClearOriginalCopyPreviewCore();
        try
        {
            if (activeRecoveryReport.RequiresExplicitArchive)
            {
                service.ArchiveRecoveryRecord(activeRecoveryReport.OperationRecord, $"用户确认并归档（{activeRecoveryReport.StatusBadge}）。");
                SetOperationBanner(
                    "已归档该中断记录",
                    "已收起核对提示；你的当前配置文件与本地覆盖前原副本均未被删除或修改。",
                    isError: false,
                    isWarning: false);
            }
            activeRecoveryReport = null;
            latestUndo = service.LoadLatestActiveUndo();
            statusMessage = "已收起操作中断核对提示。";
        }
        catch (Exception ex)
        {
            SetOperationBanner("归档中断记录失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
        }
        finally
        {
            NotifyAll();
        }
    }

    public void BeginViewOriginalCopy(ConfigUndoSnapshot? explicitSource = null)
    {
        if (busy) return;
        var source = explicitSource ?? ResolveActiveOriginalCopySource();
        if (source is null) return;

        errorMessage = "";
        ClearOperationBanner();
        try
        {
            var preview = service.InspectOriginalCopy(source);
            originalCopySource = source;
            originalCopyPreview = preview;
            isViewingOriginalCopy = true;
            OriginalCopyLines.Clear();
            foreach (var line in preview.Lines)
                OriginalCopyLines.Add(line);
            statusMessage = preview.IsValid
                ? $"正在查看覆盖前原副本“{source.DisplayTargetScopedName}”（SHA-256：{preview.DisplayShortSha256}，已校验完整）。"
                : preview.ErrorMessage;
        }
        catch (Exception ex)
        {
            ClearOriginalCopyPreviewCore();
            SetOperationBanner("查看原副本失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
        }
        finally
        {
            NotifyAll();
        }
    }

    public void CloseOriginalCopyPreview()
    {
        if (!isViewingOriginalCopy) return;
        ClearOriginalCopyPreviewCore();
        statusMessage = "已关闭原副本预览。";
        NotifyAll();
    }

    public ConfigOriginalCopyExportResult? ExportOriginalCopy(string destinationPath, ConfigUndoSnapshot? explicitSource = null)
    {
        if (busy) return null;
        var source = explicitSource ?? ResolveActiveOriginalCopySource();
        if (source is null) return null;

        busy = true;
        errorMessage = "";
        NotifyAll();
        try
        {
            var result = service.ExportOriginalCopy(source, destinationPath);
            SetOperationBanner(result.Headline, result.Detail, isError: !result.Succeeded, isWarning: false);
            if (!result.Succeeded)
                errorMessage = result.Detail;
            statusMessage = result.Headline;
            return result;
        }
        catch (Exception ex)
        {
            SetOperationBanner("另存原副本失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
            return null;
        }
        finally
        {
            busy = false;
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
        ClearOriginalCopyPreviewCore();
        ClearExportPackageCore();
        ClearImportWorkspaceCore();
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
        if (ReferenceEquals(selectedPoint, point) && !isImportingPackage && !isExportingPackage) return;
        selectedPoint = point;
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";
        ClearUndoPreviewCore();
        ClearOriginalCopyPreviewCore();
        ClearExportPackageCore();
        ClearImportWorkspaceCore();
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
        ClearOriginalCopyPreviewCore();
        ClearExportPackageCore();
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
        ClearOriginalCopyPreviewCore();
        ClearExportPackageCore();
        ClearImportWorkspaceCore();
        midConfirmWarning = "";

        if (service.IsTargetBusyInAnotherWindow(selectedPoint.TargetPath, out var busyLock))
        {
            var pidNote = busyLock is { OwnerPid: > 0 } ? $"（PID {busyLock.OwnerPid}）" : "";
            activeRecoveryReport = service.ReconcileLatestOperation() ?? activeRecoveryReport;
            SetOperationBanner(
                "操作已暂缓：另一个 Contain 窗口正在操作此文件",
                $"检测到另一个 Contain 窗口{pidNote}正在写入该目标文件。请等待对方完成后点击「刷新」再尝试恢复。",
                isError: false,
                isWarning: true);
            statusMessage = "另一个 Contain 窗口正在操作此文件，当前仅支持只读浏览。";
            NotifyAll();
            return;
        }

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
                    activeRecoveryReport = null;
                    latestUndo = result.UndoSnapshot;
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: result.RefreshFailed);
                    statusMessage = $"{result.Headline}（SHA-256：{selectedPoint.DisplayShortSha256}）。";
                    break;

                case ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit:
                    activeRecoveryReport = service.ReconcileLatestOperation();
                    latestUndo = service.LoadLatestActiveUndo();
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

                case ConfigRestoreOutcomeKind.BusyInAnotherWindow:
                    isConfirmingRestore = false;
                    confirmedLiveSnapshot = null;
                    activeRecoveryReport = service.ReconcileLatestOperation();
                    latestUndo = service.LoadLatestActiveUndo();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    break;

                case ConfigRestoreOutcomeKind.VerificationFailedBackupPreserved:
                    isConfirmingRestore = false;
                    confirmedLiveSnapshot = null;
                    // Run read-only reconciliation immediately so user gets full recovery options & original copy access!
                    activeRecoveryReport = service.ReconcileLatestOperation();
                    latestUndo = service.LoadLatestActiveUndo();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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
        ClearOriginalCopyPreviewCore();
        ClearExportPackageCore();
        ClearImportWorkspaceCore();
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
        ClearOriginalCopyPreviewCore();
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
                    ClearOriginalCopyPreviewCore();
                    activeRecoveryReport = null;
                    latestUndo = service.LoadLatestActiveUndo();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: false);
                    statusMessage = result.Headline;
                    if (selectedPoint is not null)
                        RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
                    break;

                case ConfigUndoOutcomeKind.SucceededMetadataPending:
                    ClearUndoPreviewCore();
                    ClearOriginalCopyPreviewCore();
                    activeRecoveryReport = null;
                    // Do NOT show a stale Undo bar! LoadLatestActiveUndo excludes undo_prepared records.
                    latestUndo = service.LoadLatestActiveUndo();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    if (selectedPoint is not null)
                        RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
                    break;

                case ConfigUndoOutcomeKind.BusyInAnotherWindow:
                    if (result.RefreshedUndoPreview is not null)
                        ApplyUndoPreviewCore(result.RefreshedUndoPreview);
                    activeRecoveryReport = service.ReconcileLatestOperation();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    break;

                case ConfigUndoOutcomeKind.StaleAlreadyHandled:
                    ClearUndoPreviewCore();
                    ClearOriginalCopyPreviewCore();
                    activeRecoveryReport = service.ReconcileLatestOperation();
                    latestUndo = service.LoadLatestActiveUndo();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    // --- Portable Restore Point Export ("导出还原点…") ---

    public void BeginExportRestorePoint()
    {
        if (!CanBeginExportRestorePoint || selectedPoint is null) return;
        errorMessage = "";
        ClearOperationBanner();
        ClearUndoPreviewCore();
        ClearOriginalCopyPreviewCore();
        ClearImportWorkspaceCore();
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";

        isExportingPackage = true;
        exportPackagePathInput = "";
        OnPropertyChanged(nameof(ExportPackagePathInput));
        statusMessage = $"准备导出还原点“{selectedPoint.DisplayScopedName}”的保存备份（SHA-256：{selectedPoint.DisplayShortSha256}）。";
        NotifyAll();
    }

    public void SelectExportPackagePath(string path)
    {
        if (!isExportingPackage) return;
        exportPackagePathInput = path ?? "";
        OnPropertyChanged(nameof(ExportPackagePathInput));
        OnPropertyChanged(nameof(CanConfirmExportPackage));
    }

    public void CancelExportRestorePoint()
    {
        if (!isExportingPackage) return;
        ClearExportPackageCore();
        statusMessage = "已取消导出还原点迁移包。";
        NotifyAll();
    }

    public ConfigPortablePackageExportResult? ConfirmExportRestorePoint()
    {
        if (!CanConfirmExportPackage || selectedPoint is null) return null;
        return ExportRestorePointPackage(exportPackagePathInput);
    }

    public ConfigPortablePackageExportResult? ExportRestorePointPackage(string destinationPath)
    {
        if (busy || selectedPoint is null) return null;
        busy = true;
        errorMessage = "";
        NotifyAll();

        try
        {
            var result = service.ExportRestorePointPackage(selectedPoint, destinationPath);
            SetOperationBanner(result.Headline, result.Detail, isError: !result.Succeeded, isWarning: false);
            if (result.Succeeded)
            {
                ClearExportPackageCore();
            }
            else
            {
                errorMessage = result.Detail;
            }
            statusMessage = result.Headline;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetOperationBanner("导出还原点失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
            return null;
        }
        finally
        {
            busy = false;
            NotifyAll();
        }
    }

    // --- Portable Restore Point Import ("导入还原点…") ---

    public void OpenImportWorkspace()
    {
        if (busy) return;
        errorMessage = "";
        ClearOperationBanner();
        ClearUndoPreviewCore();
        ClearOriginalCopyPreviewCore();
        ClearExportPackageCore();
        isConfirmingRestore = false;
        confirmedLiveSnapshot = null;
        midConfirmWarning = "";

        isImportingPackage = true;
        importPackagePathInput = "";
        importTargetPathInput = "";
        importedPackage = null;
        importPreview = null;
        confirmedImportLiveSnapshot = null;
        importMidConfirmWarning = "";
        ImportDiffLines.Clear();
        OnPropertyChanged(nameof(ImportPackagePathInput));
        OnPropertyChanged(nameof(ImportTargetPathInput));
        statusMessage = "请选择要导入的配置还原点迁移包（.contain-config-point）。打开与预览均不会写入本机文件。";
        NotifyAll();
    }

    public void OpenImportPackage(string packagePath)
    {
        if (busy) return;
        if (!isImportingPackage)
        {
            ClearOperationBanner();
            ClearUndoPreviewCore();
            ClearOriginalCopyPreviewCore();
            ClearExportPackageCore();
            isConfirmingRestore = false;
            confirmedLiveSnapshot = null;
            midConfirmWarning = "";
            isImportingPackage = true;
        }
        importPackagePathInput = packagePath ?? "";
        OnPropertyChanged(nameof(ImportPackagePathInput));
        InspectImportedPackageInput();
    }

    public void SelectImportTargetPath(string targetPath)
    {
        if (busy || !isImportingPackage) return;
        importTargetPathInput = targetPath ?? "";
        OnPropertyChanged(nameof(ImportTargetPathInput));
        RefreshImportTargetPreviewCore();
    }

    public void RecheckImportTarget()
    {
        if (busy || !isImportingPackage) return;
        errorMessage = "";
        importMidConfirmWarning = "";
        ClearOperationBanner();
        RefreshImportTargetPreviewCore();
    }

    public void CancelImport()
    {
        if (!isImportingPackage) return;
        ClearImportWorkspaceCore();
        errorMessage = "";
        if (selectedPoint is not null)
            RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
        statusMessage = "已取消导入还原点，未写入本机目标文件。";
        NotifyAll();
    }

    public ConfigImportRestoreResult? ConfirmImportRestore()
    {
        if (busy || !isImportingPackage || importedPackage is not { IsValid: true } || importPreview is null || !importPreview.CanConfirm || confirmedImportLiveSnapshot is null)
            return null;

        busy = true;
        errorMessage = "";
        importMidConfirmWarning = "";
        NotifyAll();

        try
        {
            var result = service.ImportAndRestorePackage(importedPackage, importTargetPathInput, confirmedImportLiveSnapshot);
            switch (result.Outcome)
            {
                case ConfigRestoreOutcomeKind.Succeeded:
                    if (result.ImportedPoint is not null)
                    {
                        allPoints.Insert(0, result.ImportedPoint);
                        ApplyFilter();
                        selectedPoint = result.ImportedPoint;
                    }
                    ClearImportWorkspaceCore();
                    activeRecoveryReport = null;
                    latestUndo = result.UndoSnapshot;
                    if (selectedPoint is not null)
                        RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: result.RefreshFailed);
                    statusMessage = result.Headline;
                    break;

                case ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit:
                    if (result.RefreshedImportPreview is not null)
                        ApplyImportPreviewCore(result.RefreshedImportPreview);
                    importMidConfirmWarning = $"{result.Headline}：{result.Detail}";
                    if (importPreview is not { CanConfirm: true })
                        SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    break;

                case ConfigRestoreOutcomeKind.BusyInAnotherWindow:
                    if (result.RefreshedImportPreview is not null)
                        ApplyImportPreviewCore(result.RefreshedImportPreview);
                    activeRecoveryReport = service.ReconcileLatestOperation();
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    break;

                case ConfigRestoreOutcomeKind.VerificationFailedBackupPreserved:
                    if (result.ImportedPoint is not null)
                    {
                        allPoints.Insert(0, result.ImportedPoint);
                        ApplyFilter();
                        selectedPoint = result.ImportedPoint;
                    }
                    ClearImportWorkspaceCore();
                    activeRecoveryReport = service.ReconcileLatestOperation();
                    latestUndo = service.LoadLatestActiveUndo();
                    if (selectedPoint is not null)
                        RefreshSelectedComparisonCore(ConfigDiffDirection.BackupToCurrent, preserveConfirmation: false);
                    SetOperationBanner(result.Headline, result.Detail, isError: false, isWarning: true);
                    statusMessage = result.Headline;
                    break;

                case ConfigRestoreOutcomeKind.Failed:
                default:
                    if (result.RefreshedImportPreview is not null)
                        ApplyImportPreviewCore(result.RefreshedImportPreview);
                    SetOperationBanner(result.Headline, result.Detail, isError: true, isWarning: false);
                    errorMessage = result.Detail;
                    statusMessage = result.Headline;
                    break;
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetOperationBanner("导入恢复失败", ex.Message, isError: true, isWarning: false);
            errorMessage = ex.Message;
            statusMessage = errorMessage;
            return null;
        }
        finally
        {
            busy = false;
            NotifyAll();
        }
    }

    private void InspectImportedPackageInput()
    {
        errorMessage = "";
        importMidConfirmWarning = "";
        ClearOperationBanner();
        if (string.IsNullOrWhiteSpace(importPackagePathInput))
        {
            importedPackage = null;
            ClearImportPreviewOnlyCore();
            statusMessage = "请选择要导入的配置还原点迁移包（.contain-config-point）。";
            NotifyAll();
            return;
        }

        importedPackage = service.InspectPortablePackage(importPackagePathInput);
        if (!importedPackage.IsValid)
        {
            ClearImportPreviewOnlyCore();
            statusMessage = importedPackage.ErrorMessage;
            NotifyAll();
            return;
        }

        if (!string.IsNullOrWhiteSpace(importTargetPathInput))
        {
            var preview = service.PreviewImportedPackageTarget(importedPackage, importTargetPathInput);
            ApplyImportPreviewCore(preview);
            statusMessage = $"迁移包校验通过，已预览「{preview.DirectionLabel}」。";
        }
        else
        {
            ClearImportPreviewOnlyCore();
            statusMessage = $"迁移包校验通过（{importedPackage.DisplayFileName}，{importedPackage.DisplaySize}）。请选择本机目标配置文件。";
        }
        NotifyAll();
    }

    private void RefreshImportTargetPreviewCore()
    {
        importMidConfirmWarning = "";
        if (importedPackage is not { IsValid: true } || string.IsNullOrWhiteSpace(importTargetPathInput))
        {
            ClearImportPreviewOnlyCore();
            NotifyAll();
            return;
        }

        var preview = service.PreviewImportedPackageTarget(importedPackage, importTargetPathInput);
        ApplyImportPreviewCore(preview);
        statusMessage = preview.CanConfirm
            ? $"{preview.DirectionLabel}：请核对本机目标路径与差异后确认恢复。"
            : preview.Headline;
        NotifyAll();
    }

    private void ApplyImportPreviewCore(ConfigImportPreviewResult preview)
    {
        importPreview = preview;
        confirmedImportLiveSnapshot = preview.LiveSnapshot;
        ImportDiffLines.Clear();
        foreach (var line in preview.DiffLines)
            ImportDiffLines.Add(line);
    }

    private void ClearImportPreviewOnlyCore()
    {
        importPreview = null;
        confirmedImportLiveSnapshot = null;
        importMidConfirmWarning = "";
        ImportDiffLines.Clear();
    }

    private void ClearExportPackageCore()
    {
        isExportingPackage = false;
        exportPackagePathInput = "";
    }

    private void ClearImportWorkspaceCore()
    {
        isImportingPackage = false;
        importPackagePathInput = "";
        importTargetPathInput = "";
        importedPackage = null;
        ClearImportPreviewOnlyCore();
    }

    private ConfigUndoSnapshot? ResolveActiveOriginalCopySource()
    {
        if (originalCopySource is not null && isViewingOriginalCopy)
            return originalCopySource;
        if (isPreviewingUndo && undoPreview is not null)
            return undoPreview.UndoSnapshot;
        if (activeRecoveryReport is not null)
            return activeRecoveryReport.OperationRecord;
        return latestUndo;
    }

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

    private void ClearOriginalCopyPreviewCore()
    {
        isViewingOriginalCopy = false;
        originalCopyPreview = null;
        originalCopySource = null;
        OriginalCopyLines.Clear();
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
        OnPropertyChanged(nameof(ShowCreateCard));
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
        OnPropertyChanged(nameof(CanBeginExportRestorePoint));
        OnPropertyChanged(nameof(IsExportingPackage));
        OnPropertyChanged(nameof(ShowExportPackageCard));
        OnPropertyChanged(nameof(ExportPackagePathInput));
        OnPropertyChanged(nameof(CanConfirmExportPackage));
        OnPropertyChanged(nameof(SuggestedPortablePackageFileName));
        OnPropertyChanged(nameof(ExportPackageTitle));
        OnPropertyChanged(nameof(ExportPackageVersionSummary));
        OnPropertyChanged(nameof(ExportPackagePrivacyNotice));
        OnPropertyChanged(nameof(CanOpenImportWorkspace));
        OnPropertyChanged(nameof(IsImportingPackage));
        OnPropertyChanged(nameof(ShowImportPanel));
        OnPropertyChanged(nameof(ImportPackagePathInput));
        OnPropertyChanged(nameof(ImportTargetPathInput));
        OnPropertyChanged(nameof(ImportedPackage));
        OnPropertyChanged(nameof(ImportPreview));
        OnPropertyChanged(nameof(HasValidImportedPackage));
        OnPropertyChanged(nameof(HasInvalidImportedPackage));
        OnPropertyChanged(nameof(ImportedPackageErrorMessage));
        OnPropertyChanged(nameof(ImportedPackageDisplayFileName));
        OnPropertyChanged(nameof(ImportedPackageVerifiedHeadline));
        OnPropertyChanged(nameof(ImportedPackageVerifiedMeta));
        OnPropertyChanged(nameof(HasImportPreview));
        OnPropertyChanged(nameof(IsImportReadyToCreate));
        OnPropertyChanged(nameof(IsImportReadyToOverwrite));
        OnPropertyChanged(nameof(IsImportIdentical));
        OnPropertyChanged(nameof(IsImportPreviewError));
        OnPropertyChanged(nameof(ImportPreviewTitle));
        OnPropertyChanged(nameof(ImportPreviewDirectionLabel));
        OnPropertyChanged(nameof(ImportPreviewTargetPath));
        OnPropertyChanged(nameof(ImportPreviewHeadline));
        OnPropertyChanged(nameof(ImportPreviewFormatTransition));
        OnPropertyChanged(nameof(ImportPreviewActionSummary));
        OnPropertyChanged(nameof(HasImportMidConfirmWarning));
        OnPropertyChanged(nameof(ImportMidConfirmWarningText));
        OnPropertyChanged(nameof(CanConfirmImportRestore));
        OnPropertyChanged(nameof(ConfirmImportRestoreButtonText));
        OnPropertyChanged(nameof(HasImportTextDiff));
        OnPropertyChanged(nameof(ShowImportDiffFallback));
        OnPropertyChanged(nameof(ImportDiffFallbackNote));
        OnPropertyChanged(nameof(ImportDiffHeaderSummary));
        OnPropertyChanged(nameof(HasImportDiffTruncationNote));
        OnPropertyChanged(nameof(IsSelectedTargetBusyInAnotherWindow));
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
        OnPropertyChanged(nameof(ActiveRecoveryReport));
        OnPropertyChanged(nameof(HasRecoveryCard));
        OnPropertyChanged(nameof(ShowRecoveryCard));
        OnPropertyChanged(nameof(HasPendingRecoveryShellBanner));
        OnPropertyChanged(nameof(PendingRecoveryShellBannerText));
        OnPropertyChanged(nameof(RecoveryStatus));
        OnPropertyChanged(nameof(RecoveryStatusBadge));
        OnPropertyChanged(nameof(RecoveryHeadline));
        OnPropertyChanged(nameof(RecoveryDetail));
        OnPropertyChanged(nameof(RecoveryTargetPath));
        OnPropertyChanged(nameof(RecoveryDiskStateSummary));
        OnPropertyChanged(nameof(RecoveryOriginalCopySummary));
        OnPropertyChanged(nameof(IsRecoveryWarning));
        OnPropertyChanged(nameof(IsRecoveryInfo));
        OnPropertyChanged(nameof(IsRecoveryResolved));
        OnPropertyChanged(nameof(CanUndoFromRecovery));
        OnPropertyChanged(nameof(CanViewOriginalCopyFromRecovery));
        OnPropertyChanged(nameof(CanExportOriginalCopyFromRecovery));
        OnPropertyChanged(nameof(CanRecheckRecovery));
        OnPropertyChanged(nameof(DismissRecoveryButtonText));
        OnPropertyChanged(nameof(IsViewingOriginalCopy));
        OnPropertyChanged(nameof(OriginalCopyPreview));
        OnPropertyChanged(nameof(OriginalCopyPreviewTitle));
        OnPropertyChanged(nameof(OriginalCopyTargetPath));
        OnPropertyChanged(nameof(OriginalCopyMetaSummary));
        OnPropertyChanged(nameof(HasOriginalCopyText));
        OnPropertyChanged(nameof(ShowOriginalCopyFallback));
        OnPropertyChanged(nameof(OriginalCopyFallbackNote));
        OnPropertyChanged(nameof(CanExportFromOriginalCopyPreview));
        OnPropertyChanged(nameof(SuggestedOriginalCopyFileName));
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
        OnPropertyChanged(nameof(ShowUndoPreviewPanel));
        OnPropertyChanged(nameof(UndoPreviewTitle));
        OnPropertyChanged(nameof(UndoPreviewDirectionLabel));
        OnPropertyChanged(nameof(UndoPreviewTargetPath));
        OnPropertyChanged(nameof(UndoPreviewHeadline));
        OnPropertyChanged(nameof(UndoPreviewDetail));
        OnPropertyChanged(nameof(UndoPreviewActionSummary));
        OnPropertyChanged(nameof(IsUndoPreviewConflict));
        OnPropertyChanged(nameof(IsUndoPreviewRemoveRecreated));
        OnPropertyChanged(nameof(CanConfirmUndo));
        OnPropertyChanged(nameof(CanViewOriginalCopyFromUndoPreview));
        OnPropertyChanged(nameof(CanExportOriginalCopyFromUndoPreview));
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
