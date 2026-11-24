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
    Failed
}

public enum ConfigUndoPreviewStatus
{
    ReadyToRestorePreviousBytes,
    ReadyToRemoveRecreatedFile,
    TargetModifiedConflict,
    TargetDeletedConflict,
    TargetReadFailed,
    UndoSnapshotInvalid
}

public enum ConfigUndoOutcomeKind
{
    Succeeded,
    ExternalEditConflict,
    TargetDeletedConflict,
    Failed
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

    [JsonPropertyName("restored_backup_sha256")]
    public string RestoredBackupSha256 { get; init; } = "";

    [JsonPropertyName("consumed")]
    public bool Consumed { get; init; }

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
    ConfigComparisonResult? RefreshedComparison);

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
    IReadOnlyList<ConfigDiffLine> DiffLines)
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
    ConfigUndoPreviewResult? RefreshedUndoPreview = null);

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
}
