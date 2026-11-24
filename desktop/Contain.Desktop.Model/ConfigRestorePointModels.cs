using System.Globalization;
using System.Text.Json.Serialization;

namespace Contain.Desktop.Model;

public enum ConfigDiffDirection
{
    BackupToCurrent,
    CurrentToBackup
}

public enum ConfigComparisonStatus
{
    Identical,
    Changed,
    Missing,
    ReadFailed,
    BackupCorrupted
}

public enum ConfigDiffLineKind
{
    Context,
    Removed,
    Added,
    Ellipsis
}

public enum ConfigRestoreOutcomeKind
{
    Succeeded,
    RefreshedDueToExternalEdit,
    VerificationFailedBackupPreserved,
    BusyInAnotherWindow,
    Failed
}

public enum ConfigUndoPreviewStatus
{
    ReadyToRestorePreviousBytes,
    ReadyToRemoveRecreatedFile,
    TargetModifiedConflict,
    TargetDeletedConflict,
    TargetReadFailed,
    BusyInAnotherWindow,
    UndoSnapshotInvalid
}

public enum ConfigUndoOutcomeKind
{
    Succeeded,
    SucceededMetadataPending,
    ExternalEditConflict,
    TargetDeletedConflict,
    BusyInAnotherWindow,
    StaleAlreadyHandled,
    Failed
}

public enum ConfigRecoveryStatus
{
    None,
    ActiveInAnotherWindow,
    RestoreNotApplied,
    RestoreAppliedNeedsReview,
    UndoAlreadyApplied,
    UndoNotApplied,
    ExternalVersionCDetected,
    TargetMissingAfterInterruption,
    NeedsVerification
}

public static class ConfigOperationStates
{
    public const string RestorePrepared = "restore_prepared";
    public const string RestoreCompleted = "restore_completed";
    public const string RestoreAborted = "restore_aborted";
    public const string UndoPrepared = "undo_prepared";
    public const string UndoCompleted = "undo_completed";
    public const string ArchivedAfterConflict = "archived_after_conflict";
}

public sealed record ConfigRestorePointRecord
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("point_id")]
    public string PointId { get; init; } = "";

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("note")]
    public string Note { get; init; } = "";

    [JsonPropertyName("target_path")]
    public string TargetPath { get; init; } = "";

    [JsonPropertyName("target_file_name")]
    public string TargetFileName { get; init; } = "";

    [JsonPropertyName("target_directory")]
    public string TargetDirectory { get; init; } = "";

    [JsonPropertyName("parent_folder_name")]
    public string ParentFolderName { get; init; } = "";

    [JsonPropertyName("byte_length")]
    public long ByteLength { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";

    [JsonPropertyName("backup_blob_relative_path")]
    public string BackupBlobRelativePath { get; init; } = "";

    [JsonPropertyName("encoding_label")]
    public string EncodingLabel { get; init; } = "";

    [JsonPropertyName("line_ending_label")]
    public string LineEndingLabel { get; init; } = "";

    [JsonIgnore]
    public string AutomationId => "restore-point:" + PointId;

    [JsonIgnore]
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    [JsonIgnore]
    public string DisplayNote => HasNote ? Note : "无备注";

    [JsonIgnore]
    public string DisplayParentBadge => string.IsNullOrWhiteSpace(ParentFolderName) ? TargetDirectory : ParentFolderName;

    [JsonIgnore]
    public string DisplayScopedName => string.IsNullOrWhiteSpace(ParentFolderName)
        ? TargetFileName
        : $"{ParentFolderName}\\{TargetFileName}";

    [JsonIgnore]
    public string DisplayDirectory => TargetDirectory;

    [JsonIgnore]
    public string DisplayCreatedTime => CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string DisplayShortSha256 => Sha256.Length >= 12 ? Sha256[..12] + "…" : Sha256;

    [JsonIgnore]
    public string DisplaySize => ConfigFormatting.FormatByteSize(ByteLength);

    [JsonIgnore]
    public string DisplayFormatSummary => string.IsNullOrWhiteSpace(LineEndingLabel) || LineEndingLabel == "不适用"
        ? EncodingLabel
        : $"{EncodingLabel} · {LineEndingLabel}";

    [JsonIgnore]
    public string DisplayListMeta => $"{DisplayCreatedTime} · {DisplaySize} · {DisplayShortSha256}";
}

public sealed record ConfigUndoSnapshot
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("undo_id")]
    public string UndoId { get; init; } = "";

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("restored_point_id")]
    public string RestoredPointId { get; init; } = "";

    [JsonPropertyName("restored_point_note")]
    public string RestoredPointNote { get; init; } = "";

    [JsonPropertyName("target_path")]
    public string TargetPath { get; init; } = "";

    [JsonPropertyName("target_existed_before_restore")]
    public bool TargetExistedBeforeRestore { get; init; }

    [JsonPropertyName("pre_restore_byte_length")]
    public long PreRestoreByteLength { get; init; }

    [JsonPropertyName("pre_restore_sha256")]
    public string PreRestoreSha256 { get; init; } = "";

    [JsonPropertyName("pre_restore_blob_relative_path")]
    public string PreRestoreBlobRelativePath { get; init; } = "";

    [JsonPropertyName("pre_restore_encoding_label")]
    public string PreRestoreEncodingLabel { get; init; } = "";

    [JsonPropertyName("pre_restore_line_ending_label")]
    public string PreRestoreLineEndingLabel { get; init; } = "";

    [JsonPropertyName("restored_backup_byte_length")]
    public long RestoredBackupByteLength { get; init; } = -1;

    [JsonPropertyName("restored_backup_sha256")]
    public string RestoredBackupSha256 { get; init; } = "";

    [JsonPropertyName("operation_state")]
    public string OperationState { get; init; } = "";

    [JsonPropertyName("undo_started_at_utc")]
    public DateTimeOffset? UndoStartedAtUtc { get; init; }

    [JsonPropertyName("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; init; }

    [JsonPropertyName("reconciled_at_utc")]
    public DateTimeOffset? ReconciledAtUtc { get; init; }

    [JsonPropertyName("reconciled_note")]
    public string ReconciledNote { get; init; } = "";

    [JsonPropertyName("owner_pid")]
    public int OwnerPid { get; init; }

    [JsonPropertyName("owner_process_start_utc_ticks")]
    public long OwnerProcessStartUtcTicks { get; init; }

    [JsonPropertyName("lock_file_relative_path")]
    public string LockFileRelativePath { get; init; } = "";

    [JsonPropertyName("consumed")]
    public bool Consumed { get; init; }

    [JsonIgnore]
    public bool IsLegacyRecord => string.IsNullOrWhiteSpace(OperationState);

    [JsonIgnore]
    public bool IsPendingInterruption => OperationState is ConfigOperationStates.RestorePrepared or ConfigOperationStates.UndoPrepared;

    [JsonIgnore]
    public string DisplayPreRestoreShortSha => PreRestoreSha256.Length >= 12 ? PreRestoreSha256[..12] + "…" : PreRestoreSha256;

    [JsonIgnore]
    public string DisplayRestoredShortSha => RestoredBackupSha256.Length >= 12 ? RestoredBackupSha256[..12] + "…" : RestoredBackupSha256;

    [JsonIgnore]
    public string DisplayTargetFileName => string.IsNullOrWhiteSpace(TargetPath) ? "" : Path.GetFileName(TargetPath);

    [JsonIgnore]
    public string DisplayTargetScopedName => ConfigFormatting.FormatScopedName(TargetPath);
}

public sealed record ConfigTextInspection(
    bool IsRecognizedText,
    string EncodingLabel,
    bool HasUtf8Bom,
    string LineEndingLabel,
    IReadOnlyList<string> Lines,
    string UnrecognizedReason = "");

public sealed record ConfigFilePreview(
    bool IsValid,
    string FullPath,
    string FileName,
    string DirectoryPath,
    string ParentFolderName,
    long ByteLength,
    string Sha256,
    string EncodingLabel,
    string LineEndingLabel,
    string ErrorMessage = "")
{
    public string DisplaySize => ConfigFormatting.FormatByteSize(ByteLength);
    public string DisplayFormatSummary => string.IsNullOrWhiteSpace(LineEndingLabel) || LineEndingLabel == "不适用"
        ? EncodingLabel
        : $"{EncodingLabel} · {LineEndingLabel}";
}

public sealed record ConfigLiveFileSnapshot(
    bool Exists,
    bool IsReadable,
    long ByteLength,
    string Sha256,
    long LastWriteTimeUtcTicks,
    string EncodingLabel,
    string LineEndingLabel,
    string ReadError = "")
{
    public string DisplaySize => Exists && IsReadable ? ConfigFormatting.FormatByteSize(ByteLength) : "—";
    public string DisplayShortSha256 => Sha256.Length >= 12 ? Sha256[..12] + "…" : (string.IsNullOrEmpty(Sha256) ? "—" : Sha256);
    public string DisplayFormatSummary => !Exists || !IsReadable
        ? "—"
        : (string.IsNullOrWhiteSpace(LineEndingLabel) || LineEndingLabel == "不适用"
            ? EncodingLabel
            : $"{EncodingLabel} · {LineEndingLabel}");

    public bool MatchesContentOf(ConfigLiveFileSnapshot other) =>
        Exists == other.Exists &&
        IsReadable == other.IsReadable &&
        ByteLength == other.ByteLength &&
        string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase);
}

public sealed record ConfigDiffLine(
    ConfigDiffLineKind Kind,
    int? LeftLineNumber,
    int? RightLineNumber,
    string Text)
{
    public string Prefix => Kind switch
    {
        ConfigDiffLineKind.Removed => "-",
        ConfigDiffLineKind.Added => "+",
        ConfigDiffLineKind.Ellipsis => "…",
        _ => " "
    };

    public string LineNumbersDisplay => Kind == ConfigDiffLineKind.Ellipsis
        ? "   … |    …"
        : $"{(LeftLineNumber.HasValue ? LeftLineNumber.Value.ToString(CultureInfo.InvariantCulture).PadLeft(4) : "    ")} | {(RightLineNumber.HasValue ? RightLineNumber.Value.ToString(CultureInfo.InvariantCulture).PadLeft(4) : "    ")}";

    public string DisplayLine => Kind == ConfigDiffLineKind.Ellipsis
        ? Text
        : $"{Prefix} {Text}";
}

public sealed record ConfigCopyPreviewLine(
    int? LineNumber,
    string Text)
{
    public string LineNumberDisplay => LineNumber.HasValue
        ? LineNumber.Value.ToString(CultureInfo.InvariantCulture).PadLeft(4)
        : "   …";
}

public sealed record ConfigComparisonResult(
    ConfigRestorePointRecord Point,
    ConfigDiffDirection Direction,
    ConfigComparisonStatus Status,
    bool BackupIntegrityValid,
    string BackupIntegrityError,
    ConfigLiveFileSnapshot LiveSnapshot,
    string StatusHeadline,
    string StatusDetail,
    bool IsTextDiffAvailable,
    bool HasFormatOnlyDifference,
    string DiffFallbackNote,
    int RemovedLineCount,
    int AddedLineCount,
    bool IsDiffTruncated,
    IReadOnlyList<ConfigDiffLine> DiffLines);

public sealed record ConfigRestoreResult(
    ConfigRestoreOutcomeKind Outcome,
    string Headline,
    string Detail,
    string TargetPath,
    string ExpectedSha256,
    string ActualSha256,
    bool ExistingFilePreserved,
    ConfigUndoSnapshot? UndoSnapshot,
    ConfigComparisonResult? RefreshedComparison,
    bool OriginalCopyPreserved = false,
    bool RefreshFailed = false);

public sealed record ConfigUndoPreviewResult(
    ConfigUndoSnapshot UndoSnapshot,
    ConfigUndoPreviewStatus Status,
    ConfigLiveFileSnapshot LiveSnapshot,
    string DirectionLabel,
    string Headline,
    string Detail,
    string ActionSummary,
    string PreRestoreEncodingLabel,
    string PreRestoreLineEndingLabel,
    long PreRestoreByteLength,
    string PreRestoreSha256,
    bool IsTextDiffAvailable,
    bool HasFormatOnlyDifference,
    string DiffFallbackNote,
    int RemovedLineCount,
    int AddedLineCount,
    bool IsDiffTruncated,
    IReadOnlyList<ConfigDiffLine> DiffLines,
    bool HasValidOriginalCopy = false)
{
    public bool CanConfirm => Status is ConfigUndoPreviewStatus.ReadyToRestorePreviousBytes or ConfigUndoPreviewStatus.ReadyToRemoveRecreatedFile;
    public bool IsConflictOrError => !CanConfirm;
    public string PreRestoreDisplaySize => ConfigFormatting.FormatByteSize(PreRestoreByteLength);
    public string PreRestoreDisplayFormatSummary => string.IsNullOrWhiteSpace(PreRestoreLineEndingLabel) || PreRestoreLineEndingLabel == "不适用"
        ? PreRestoreEncodingLabel
        : $"{PreRestoreEncodingLabel} · {PreRestoreLineEndingLabel}";
}

public sealed record ConfigUndoResult(
    ConfigUndoOutcomeKind Outcome,
    string Headline,
    string Detail,
    string TargetPath,
    string ExpectedSha256,
    string ActualSha256,
    bool ExistingFilePreserved,
    ConfigUndoPreviewResult? RefreshedUndoPreview = null,
    bool MetadataPersisted = true);

public sealed record ConfigRecoveryReport(
    ConfigRecoveryStatus Status,
    ConfigUndoSnapshot OperationRecord,
    ConfigLiveFileSnapshot LiveSnapshot,
    string StatusBadge,
    string Headline,
    string Detail,
    string DiskStateSummary,
    string OriginalCopySummary,
    bool HasValidOriginalCopy,
    bool CanOfferUndoPreview,
    bool RequiresExplicitArchive,
    bool LocalMetadataUpdated);

public sealed record ConfigOriginalCopyPreview(
    ConfigUndoSnapshot UndoSnapshot,
    bool IsValid,
    string ErrorMessage,
    long ByteLength,
    string Sha256,
    string EncodingLabel,
    string LineEndingLabel,
    bool IsTextAvailable,
    string FallbackNote,
    bool IsTruncated,
    IReadOnlyList<ConfigCopyPreviewLine> Lines)
{
    public string DisplaySize => ConfigFormatting.FormatByteSize(ByteLength);
    public string DisplayShortSha256 => Sha256.Length >= 12 ? Sha256[..12] + "…" : Sha256;
    public string DisplayFormatSummary => string.IsNullOrWhiteSpace(LineEndingLabel) || LineEndingLabel == "不适用"
        ? EncodingLabel
        : $"{EncodingLabel} · {LineEndingLabel}";
}

public sealed record ConfigOriginalCopyExportResult(
    bool Succeeded,
    string DestinationPath,
    long ByteLength,
    string Sha256,
    string Headline,
    string Detail);

public static class ConfigPortablePackageConstants
{
    public const int CurrentSchemaVersion = 1;
    public const string FormatId = "contain-config-restore-point-package";
    public const string FileExtension = ".contain-config-point";
    public const long MaxRawPayloadBytes = 32L * 1024 * 1024;
    public const long MaxPackageFileBytes = 48L * 1024 * 1024;
}

/// <summary>
/// Single-file versioned portable migration package for a saved config restore point.
/// Deliberately excludes source-machine paths, undo history, and host identifiers.
/// </summary>
public sealed record ConfigPortablePackageFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = ConfigPortablePackageConstants.CurrentSchemaVersion;

    [JsonPropertyName("format")]
    public string Format { get; init; } = ConfigPortablePackageConstants.FormatId;

    [JsonPropertyName("exported_at_utc")]
    public DateTimeOffset ExportedAtUtc { get; init; }

    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("display_file_name")]
    public string DisplayFileName { get; init; } = "";

    [JsonPropertyName("note")]
    public string Note { get; init; } = "";

    [JsonPropertyName("byte_length")]
    public long ByteLength { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = "";

    [JsonPropertyName("encoding_label")]
    public string EncodingLabel { get; init; } = "";

    [JsonPropertyName("line_ending_label")]
    public string LineEndingLabel { get; init; } = "";

    [JsonPropertyName("payload_base64")]
    public string PayloadBase64 { get; init; } = "";
}

public sealed record ConfigPortablePackageExportResult(
    bool Succeeded,
    string DestinationPath,
    string DisplayFileName,
    long ByteLength,
    string Sha256,
    string Headline,
    string Detail);

public sealed record ConfigPortablePackageInspection(
    bool IsValid,
    string PackagePath,
    int SchemaVersion,
    string Format,
    DateTimeOffset ExportedAtUtc,
    DateTimeOffset CreatedAtUtc,
    string DisplayFileName,
    string Note,
    long ByteLength,
    string Sha256,
    string EncodingLabel,
    string LineEndingLabel,
    byte[] RawBytes,
    string ErrorMessage = "")
{
    public string DisplaySize => ConfigFormatting.FormatByteSize(ByteLength);
    public string DisplayShortSha256 => Sha256.Length >= 12 ? Sha256[..12] + "…" : Sha256;
    public string DisplayFormatSummary => string.IsNullOrWhiteSpace(LineEndingLabel) || LineEndingLabel == "不适用"
        ? EncodingLabel
        : $"{EncodingLabel} · {LineEndingLabel}";
    public string DisplayCreatedTime => CreatedAtUtc == default
        ? "未知"
        : CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string DisplayNote => string.IsNullOrWhiteSpace(Note) ? "无备注" : Note;
}

public enum ConfigImportTargetStatus
{
    None,
    ReadyToOverwrite,
    ReadyToCreate,
    IdenticalToTarget,
    TargetBusyInAnotherWindow,
    TargetReadFailed,
    InvalidTargetPath
}

public sealed record ConfigImportPreviewResult(
    ConfigPortablePackageInspection Package,
    string TargetPath,
    ConfigImportTargetStatus Status,
    ConfigLiveFileSnapshot LiveSnapshot,
    string DirectionLabel,
    string Headline,
    string Detail,
    string ActionSummary,
    bool IsTextDiffAvailable,
    bool HasFormatOnlyDifference,
    string DiffFallbackNote,
    int RemovedLineCount,
    int AddedLineCount,
    bool IsDiffTruncated,
    IReadOnlyList<ConfigDiffLine> DiffLines)
{
    public bool CanConfirm => Status is ConfigImportTargetStatus.ReadyToOverwrite or ConfigImportTargetStatus.ReadyToCreate;
    public bool IsErrorOrBlocked => Status is ConfigImportTargetStatus.TargetBusyInAnotherWindow or ConfigImportTargetStatus.TargetReadFailed or ConfigImportTargetStatus.InvalidTargetPath;
}

public sealed record ConfigImportRestoreResult(
    ConfigRestoreOutcomeKind Outcome,
    string Headline,
    string Detail,
    string TargetPath,
    string ExpectedSha256,
    string ActualSha256,
    bool ExistingFilePreserved,
    ConfigRestorePointRecord? ImportedPoint,
    ConfigUndoSnapshot? UndoSnapshot,
    ConfigImportPreviewResult? RefreshedImportPreview = null,
    bool OriginalCopyPreserved = false,
    bool RefreshFailed = false);

public sealed record ConfigTargetLockInfo
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("owner_pid")]
    public int OwnerPid { get; init; }

    [JsonPropertyName("owner_process_start_utc_ticks")]
    public long OwnerProcessStartUtcTicks { get; init; }

    [JsonPropertyName("instance_id")]
    public string InstanceId { get; init; } = "";

    [JsonPropertyName("operation_kind")]
    public string OperationKind { get; init; } = "";

    [JsonPropertyName("undo_id")]
    public string UndoId { get; init; } = "";

    [JsonPropertyName("target_path")]
    public string TargetPath { get; init; } = "";

    [JsonPropertyName("started_at_utc")]
    public DateTimeOffset StartedAtUtc { get; init; }
}

public static class ConfigFormatting
{
    public static string FormatByteSize(long bytes)
    {
        if (bytes < 0) return "未知";
        if (bytes < 1024) return $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B";
        if (bytes < 1024 * 1024)
            return $"{(bytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture)} KB（{bytes.ToString("N0", CultureInfo.InvariantCulture)} 字节）";
        return $"{(bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture)} MB（{bytes.ToString("N0", CultureInfo.InvariantCulture)} 字节）";
    }

    public static string FormatScopedName(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return "";
        var fileName = Path.GetFileName(fullPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(dir)) return fileName;
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(parent) ? fileName : $"{parent}\\{fileName}";
    }

    public static string BuildSuggestedOriginalCopyFileName(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return "config.before-restore.bak";
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var ext = Path.GetExtension(targetPath);
        if (string.IsNullOrWhiteSpace(name)) return "config.before-restore" + ext;
        return $"{name}.before-restore{ext}";
    }

    public static string BuildSuggestedPortablePackageFileName(string? fileName)
    {
        var leaf = string.IsNullOrWhiteSpace(fileName) ? "config" : Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(leaf)) leaf = "config";
        foreach (var bad in Path.GetInvalidFileNameChars())
            leaf = leaf.Replace(bad, '_');
        if (leaf.EndsWith(ConfigPortablePackageConstants.FileExtension, StringComparison.OrdinalIgnoreCase))
            return leaf;
        return leaf + ConfigPortablePackageConstants.FileExtension;
    }
}
