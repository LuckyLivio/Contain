using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contain.Desktop.Model;

public sealed record TrialFolderScopeReport(
    [property: JsonPropertyName("file_count")] int FileCount,
    [property: JsonPropertyName("directory_count")] int DirectoryCount,
    [property: JsonPropertyName("total_bytes")] long TotalBytes,
    [property: JsonPropertyName("formatted_total_size")] string FormattedTotalSize);

public sealed record TrialIsolationPolicyReport(
    [property: JsonPropertyName("networking")] string Networking,
    [property: JsonPropertyName("clipboard_redirection")] string ClipboardRedirection,
    [property: JsonPropertyName("read_only_host_mount")] bool ReadOnlyHostMount,
    [property: JsonPropertyName("discard_guest_changes_on_close")] bool DiscardGuestChangesOnClose);

public sealed record TrialTimelineEventReport(
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("display_time")] string DisplayTime,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// Versioned single-session trial report shared as the single source of truth by BOTH the
/// in-app trial detail view AND "导出本次报告" JSON export.
/// Intentionally omits host absolute paths, Windows usernames, and raw diagnostic commands.
/// </summary>
public sealed record TrialSessionReport(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("started_at_utc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("ended_at_utc")] DateTimeOffset? EndedAtUtc,
    [property: JsonPropertyName("software_name")] string SoftwareName,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("input_kind")] string InputKind,
    [property: JsonPropertyName("input_kind_label")] string InputKindLabel,
    [property: JsonPropertyName("entry_relative_path")] string EntryRelativePath,
    [property: JsonPropertyName("entry_file_name")] string EntryFileName,
    [property: JsonPropertyName("entry_architecture")] string EntryArchitecture,
    [property: JsonPropertyName("entry_size_bytes")] long? EntrySizeBytes,
    [property: JsonPropertyName("entry_sha256")] string? EntrySha256,
    [property: JsonPropertyName("entry_sha256_recorded")] bool EntrySha256Recorded,
    [property: JsonPropertyName("folder_scope")] TrialFolderScopeReport? FolderScope,
    [property: JsonPropertyName("isolation_policy")] TrialIsolationPolicyReport IsolationPolicy,
    [property: JsonPropertyName("launch_outcome")] string LaunchOutcome,
    [property: JsonPropertyName("launch_outcome_label")] string LaunchOutcomeLabel,
    [property: JsonPropertyName("launch_summary")] string LaunchSummary,
    [property: JsonPropertyName("sandbox_instance_state")] string SandboxInstanceState,
    [property: JsonPropertyName("sandbox_instance_state_label")] string SandboxInstanceStateLabel,
    [property: JsonPropertyName("host_staging_state")] string HostStagingState,
    [property: JsonPropertyName("host_staging_state_label")] string HostStagingStateLabel,
    [property: JsonPropertyName("overall_result_label")] string OverallResultLabel,
    [property: JsonPropertyName("evidence_scope_note")] string EvidenceScopeNote,
    [property: JsonPropertyName("timeline")] IReadOnlyList<TrialTimelineEventReport> Timeline)
{
    public const int CurrentSchemaVersion = 1;
    public const string ProtocolName = "contain-isolated-trial-report-1";
    public const string DefaultEvidenceScopeNote =
        "本报告仅以 Contain 在宿主机与启动阶段实际观察到的证据为限，不含来宾内部完整行为审计；时间线中的“已启动入口”仅表示当时观察到入口进程启动。";

    [JsonIgnore]
    public bool IsFolderMode => string.Equals(InputKind, "folder", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayStartedTime => StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string DisplayEndedTime => EndedAtUtc is DateTimeOffset ended
        ? ended.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : "尚未确认结束";

    [JsonIgnore]
    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "未提供" : Version;

    [JsonIgnore]
    public bool HasVersion => !string.IsNullOrWhiteSpace(Version) && !string.Equals(Version, "未提供", StringComparison.Ordinal);

    [JsonIgnore]
    public string DisplayEntrySha256 => EntrySha256Recorded && !string.IsNullOrWhiteSpace(EntrySha256)
        ? EntrySha256!
        : "未记录";

    [JsonIgnore]
    public string DisplayEntryShortSha256 => EntrySha256Recorded && !string.IsNullOrWhiteSpace(EntrySha256) && EntrySha256.Length >= 12
        ? EntrySha256[..12] + "…"
        : "未记录";

    [JsonIgnore]
    public string DisplayFolderScope => FolderScope is null
        ? ""
        : $"{FolderScope.FileCount:N0} 个文件 · {FolderScope.FormattedTotalSize} · {FolderScope.DirectoryCount:N0} 个子目录";

    [JsonIgnore]
    public string DisplayEntrySummary => IsFolderMode
        ? $"入口：{EntryRelativePath}" + (FolderScope is not null ? $"（{FolderScope.FileCount:N0} 个文件 · {FolderScope.FormattedTotalSize}）" : "")
        : $"入口：{EntryFileName}" + (EntrySha256Recorded && !string.IsNullOrWhiteSpace(EntrySha256) && EntrySha256.Length >= 8 ? $" · SHA-256 {EntrySha256[..8]}…" : "");
}

/// <summary>
/// Per-file staged snapshot entry recorded during folder snapshot copying (exact bytes staged into Sandbox).
/// Persisted ONLY in local user history to power "再次试用前的文件夹变化对照"; NEVER included in default exported JSON.
/// </summary>
public sealed record TrialStagedFileEntry(
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// Local-only complete folder file manifest persisted alongside TrialHistoryRecord.
/// Omitted in Beta.5 records (null) so legacy records remain untouched and cleanly detected.
/// NEVER included in exported TrialSessionReport JSON.
/// </summary>
public sealed record TrialLocalFolderManifest(
    [property: JsonPropertyName("manifest_schema_version")] int ManifestSchemaVersion,
    [property: JsonPropertyName("is_complete")] bool IsComplete,
    [property: JsonPropertyName("files")] IReadOnlyList<TrialStagedFileEntry> Files)
{
    public const int CurrentManifestSchemaVersion = 1;
}

public enum TrialFolderDiffKind
{
    Modified,
    Added,
    Removed
}

public sealed record TrialFolderDiffItem(
    TrialFolderDiffKind Kind,
    string RelativePath,
    long? PreviousSizeBytes,
    long? CurrentSizeBytes,
    string? PreviousSha256,
    string? CurrentSha256,
    bool IsSelectedEntry = false)
{
    public string KindBadge => Kind switch
    {
        TrialFolderDiffKind.Modified => "内容变化",
        TrialFolderDiffKind.Added => "新增",
        TrialFolderDiffKind.Removed => "已移除",
        _ => "变化"
    };

    public string DetailSummary => Kind switch
    {
        TrialFolderDiffKind.Modified =>
            $"原 {FormatSize(PreviousSizeBytes)}（{ShortHash(PreviousSha256)}）→ 现 {FormatSize(CurrentSizeBytes)}（{ShortHash(CurrentSha256)}）"
            + (IsSelectedEntry ? " · 入口文件" : ""),
        TrialFolderDiffKind.Added =>
            $"新增文件 · {FormatSize(CurrentSizeBytes)}（{ShortHash(CurrentSha256)}）"
            + (IsSelectedEntry ? " · 入口文件" : ""),
        TrialFolderDiffKind.Removed =>
            $"已移除 · 原 {FormatSize(PreviousSizeBytes)}（{ShortHash(PreviousSha256)}）"
            + (IsSelectedEntry ? " · 原入口文件" : ""),
        _ => ""
    };

    private static string FormatSize(long? bytes) =>
        bytes is long b ? WindowsSandboxTrialService.FormatByteSize(b) : "未知大小";

    private static string ShortHash(string? sha) =>
        !string.IsNullOrWhiteSpace(sha) && sha.Length >= 10 ? sha[..10] + "…" : "未记录";
}

public enum TrialFolderComparisonStatus
{
    None,
    Preparing,
    Unchanged,
    Changed,
    LegacyEntryOnly,
    IncompleteManifest,
    Cancelled,
    Failed
}

public sealed record TrialFolderComparisonResult(
    TrialFolderComparisonStatus Status,
    string Headline,
    string Detail,
    int ModifiedCount,
    int AddedCount,
    int RemovedCount,
    int TotalCurrentFiles,
    long TotalCurrentBytes,
    bool EntryChanged,
    bool EntryRemoved,
    IReadOnlyList<TrialFolderDiffItem> Items)
{
    public bool IsWarning => Status is TrialFolderComparisonStatus.Changed
        or TrialFolderComparisonStatus.IncompleteManifest
        or TrialFolderComparisonStatus.Cancelled
        or TrialFolderComparisonStatus.Failed
        || (Status == TrialFolderComparisonStatus.LegacyEntryOnly && (EntryChanged || EntryRemoved));

    public bool HasDiffItems => Items.Count > 0;

    public string ExpanderHeaderText => Items.Count > 0
        ? $"查看变化文件明细（共 {Items.Count:N0} 项相对路径）"
        : "";
}

public static class TrialFolderDiffEngine
{
    public static bool IsValidSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    public static bool TryValidateCompleteManifest(
        TrialLocalFolderManifest? manifest,
        TrialFolderScopeReport? expectedScope,
        out Dictionary<string, TrialStagedFileEntry> map,
        out string reason)
    {
        map = new Dictionary<string, TrialStagedFileEntry>(StringComparer.OrdinalIgnoreCase);
        reason = "";
        if (manifest is null)
        {
            reason = "missing_manifest";
            return false;
        }
        if (manifest.ManifestSchemaVersion != TrialLocalFolderManifest.CurrentManifestSchemaVersion || !manifest.IsComplete)
        {
            reason = "manifest_marked_incomplete";
            return false;
        }
        if (manifest.Files is null)
        {
            reason = "manifest_files_null";
            return false;
        }
        if (expectedScope is not null && manifest.Files.Count != expectedScope.FileCount)
        {
            reason = $"manifest_count_mismatch({manifest.Files.Count}!={expectedScope.FileCount})";
            return false;
        }

        foreach (var entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.RelativePath) || entry.SizeBytes < 0 || !IsValidSha256Hex(entry.Sha256))
            {
                reason = "manifest_entry_invalid";
                return false;
            }
            var norm = NormalizeRelativePath(entry.RelativePath);
            if (!map.TryAdd(norm, entry with { RelativePath = norm }))
            {
                reason = "manifest_duplicate_path";
                return false;
            }
        }
        return true;
    }

    public static bool ManifestsEqual(
        IReadOnlyList<TrialStagedFileEntry>? first,
        IReadOnlyList<TrialStagedFileEntry>? second)
    {
        if (first is null || second is null || first.Count != second.Count) return false;
        var map = new Dictionary<string, TrialStagedFileEntry>(first.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in first)
        {
            var key = NormalizeRelativePath(item.RelativePath);
            if (!map.TryAdd(key, item)) return false;
        }
        foreach (var item in second)
        {
            var key = NormalizeRelativePath(item.RelativePath);
            if (!map.TryGetValue(key, out var other)) return false;
            if (other.SizeBytes != item.SizeBytes
                || !string.Equals(other.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public static TrialFolderComparisonResult Compare(
        TrialHistoryRecord previous,
        IReadOnlyList<TrialStagedFileEntry>? currentFiles,
        string? selectedEntryRelPath,
        bool currentSnapshotComplete)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var normalizedEntry = string.IsNullOrWhiteSpace(selectedEntryRelPath)
            ? NormalizeRelativePath(previous.Report.EntryRelativePath)
            : NormalizeRelativePath(selectedEntryRelPath);

        if (!currentSnapshotComplete || currentFiles is null)
        {
            return new TrialFolderComparisonResult(
                TrialFolderComparisonStatus.Failed,
                "文件夹变化对照未完成",
                "当前文件夹的快照与哈希核对尚未完成，无法确认文件内容是否与上次一致。",
                0, 0, 0,
                currentFiles?.Count ?? 0,
                currentFiles?.Sum(f => f.SizeBytes) ?? 0,
                EntryChanged: false,
                EntryRemoved: false,
                Items: []);
        }

        var currentMap = new Dictionary<string, TrialStagedFileEntry>(currentFiles.Count, StringComparer.OrdinalIgnoreCase);
        long currentTotalBytes = 0;
        foreach (var f in currentFiles)
        {
            if (f is null || string.IsNullOrWhiteSpace(f.RelativePath) || !IsValidSha256Hex(f.Sha256))
            {
                return new TrialFolderComparisonResult(
                    TrialFolderComparisonStatus.Failed,
                    "文件夹变化对照未完整完成",
                    "当前快照中有文件未能完成哈希校验，因此不能确认文件内容与上次一致。",
                    0, 0, 0, currentFiles.Count, currentTotalBytes, false, false, []);
            }
            var norm = NormalizeRelativePath(f.RelativePath);
            currentMap[norm] = f with { RelativePath = norm };
            currentTotalBytes += f.SizeBytes;
        }

        // Check current entry status
        var hasCurrentEntry = currentMap.TryGetValue(normalizedEntry, out var currentEntryFile);
        var hasPreviousEntrySha = previous.Report.EntrySha256Recorded && IsValidSha256Hex(previous.Report.EntrySha256);
        var entryChangedByHash = hasCurrentEntry && hasPreviousEntrySha
            && !string.Equals(currentEntryFile!.Sha256, previous.Report.EntrySha256, StringComparison.OrdinalIgnoreCase);
        var entryRemoved = !hasCurrentEntry;

        // 1. Beta.5 legacy record (no folder manifest at all): preserve legacy record untouched & explain cleanly!
        if (previous.LocalFolderManifest is null)
        {
            var entryCheckSummary = BuildEntryCheckSummary(previous.Report, normalizedEntry, currentEntryFile, entryChangedByHash, entryRemoved);
            return new TrialFolderComparisonResult(
                TrialFolderComparisonStatus.LegacyEntryOnly,
                "上次仅记录了入口文件，可核对入口；完成本次试用后可比较整个文件夹。",
                entryCheckSummary,
                ModifiedCount: entryChangedByHash ? 1 : 0,
                AddedCount: 0,
                RemovedCount: entryRemoved ? 1 : 0,
                TotalCurrentFiles: currentMap.Count,
                TotalCurrentBytes: currentTotalBytes,
                EntryChanged: entryChangedByHash,
                EntryRemoved: entryRemoved,
                Items: []);
        }

        // 2. Incomplete / corrupted historical manifest: honestly report up to what can be confirmed!
        if (!TryValidateCompleteManifest(previous.LocalFolderManifest, previous.Report.FolderScope, out var previousMap, out _))
        {
            var entryCheckSummary = BuildEntryCheckSummary(previous.Report, normalizedEntry, currentEntryFile, entryChangedByHash, entryRemoved);
            return new TrialFolderComparisonResult(
                TrialFolderComparisonStatus.IncompleteManifest,
                "上次记录的文件夹清单不完整，仅能核对入口文件，无法确认整个文件夹是否完全一致。",
                $"{entryCheckSummary} 完成本次试用后将重新保存完整文件清单。",
                ModifiedCount: entryChangedByHash ? 1 : 0,
                AddedCount: 0,
                RemovedCount: entryRemoved ? 1 : 0,
                TotalCurrentFiles: currentMap.Count,
                TotalCurrentBytes: currentTotalBytes,
                EntryChanged: entryChangedByHash,
                EntryRemoved: entryRemoved,
                Items: []);
        }

        // 3. Full per-file SHA-256 comparison across every relative path!
        var modified = new List<TrialFolderDiffItem>();
        var added = new List<TrialFolderDiffItem>();
        var removed = new List<TrialFolderDiffItem>();

        foreach (var (relPath, cur) in currentMap)
        {
            var isEntry = string.Equals(relPath, normalizedEntry, StringComparison.OrdinalIgnoreCase);
            if (previousMap.TryGetValue(relPath, out var prev))
            {
                if (!string.Equals(prev.Sha256, cur.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    modified.Add(new TrialFolderDiffItem(
                        TrialFolderDiffKind.Modified,
                        cur.RelativePath,
                        prev.SizeBytes,
                        cur.SizeBytes,
                        prev.Sha256,
                        cur.Sha256,
                        IsSelectedEntry: isEntry));
                }
            }
            else
            {
                added.Add(new TrialFolderDiffItem(
                    TrialFolderDiffKind.Added,
                    cur.RelativePath,
                    null,
                    cur.SizeBytes,
                    null,
                    cur.Sha256,
                    IsSelectedEntry: isEntry));
            }
        }

        var prevEntryRel = NormalizeRelativePath(previous.Report.EntryRelativePath);
        foreach (var (relPath, prev) in previousMap)
        {
            if (!currentMap.ContainsKey(relPath))
            {
                var wasEntry = string.Equals(relPath, prevEntryRel, StringComparison.OrdinalIgnoreCase);
                removed.Add(new TrialFolderDiffItem(
                    TrialFolderDiffKind.Removed,
                    prev.RelativePath,
                    prev.SizeBytes,
                    null,
                    prev.Sha256,
                    null,
                    IsSelectedEntry: wasEntry));
            }
        }

        modified.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        added.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        removed.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        var allItems = new List<TrialFolderDiffItem>(modified.Count + added.Count + removed.Count);
        allItems.AddRange(modified);
        allItems.AddRange(added);
        allItems.AddRange(removed);

        var entryModifiedInDiff = modified.Any(m => string.Equals(m.RelativePath, normalizedEntry, StringComparison.OrdinalIgnoreCase));
        var entryRemovedInDiff = !currentMap.ContainsKey(prevEntryRel);

        if (allItems.Count == 0)
        {
            return new TrialFolderComparisonResult(
                TrialFolderComparisonStatus.Unchanged,
                "文件内容与上次一致",
                $"已完整核对本次快照全部 {currentMap.Count:N0} 个文件（共 {WindowsSandboxTrialService.FormatByteSize(currentTotalBytes)}），所有相对路径与 SHA-256 均与上次一致。确认后点击“开始隔离试用”将直接复用本次已准备快照并生成新记录（不会自动启动沙箱）。",
                0, 0, 0,
                currentMap.Count,
                currentTotalBytes,
                EntryChanged: false,
                EntryRemoved: false,
                Items: []);
        }

        var summaryParts = new List<string>(3);
        if (modified.Count > 0) summaryParts.Add($"{modified.Count} 个文件内容变化");
        if (added.Count > 0) summaryParts.Add($"{added.Count} 个新增");
        if (removed.Count > 0) summaryParts.Add($"{removed.Count} 个移除");
        var headline = $"与上次相比，{string.Join("，", summaryParts)}";

        string detail;
        if (entryRemovedInDiff)
        {
            detail = $"原入口“{previous.Report.EntryRelativePath}”已不在当前目录中。点开下方明细可查看全部相对路径，请重新确认入口后再决定是否启动；原历史记录保持不变。";
        }
        else if (entryModifiedInDiff)
        {
            detail = $"入口“{normalizedEntry}”及目录内容已发生变化。点开下方明细可查看全部相对路径，确认后点击“开始隔离试用”将使用当前快照并生成新记录，原历史记录保持不变。";
        }
        else
        {
            detail = $"入口“{normalizedEntry}”本身未变化，但目录内其他文件已变化。点开下方明细可查看具体相对路径，确认后点击“开始隔离试用”将使用当前快照并生成新记录，原历史记录保持不变。";
        }

        return new TrialFolderComparisonResult(
            TrialFolderComparisonStatus.Changed,
            headline,
            detail,
            modified.Count,
            added.Count,
            removed.Count,
            currentMap.Count,
            currentTotalBytes,
            EntryChanged: entryModifiedInDiff,
            EntryRemoved: entryRemovedInDiff,
            Items: allItems);
    }

    private static string BuildEntryCheckSummary(
        TrialSessionReport prevReport,
        string normalizedEntry,
        TrialStagedFileEntry? currentEntryFile,
        bool entryChanged,
        bool entryRemoved)
    {
        if (entryRemoved)
            return $"当前已确认：原入口“{prevReport.EntryRelativePath}”已不存在，请在下方重新选择入口后再启动（原历史记录保持不变）。";
        if (!prevReport.EntrySha256Recorded || string.IsNullOrWhiteSpace(prevReport.EntrySha256))
            return $"当前已确认：目录已重新准备快照并找到入口“{normalizedEntry}”（原历史未记录入口 SHA-256，原历史记录保持不变）。";
        if (entryChanged && currentEntryFile is not null)
        {
            var currShort = currentEntryFile.Sha256.Length >= 12 ? currentEntryFile.Sha256[..12] + "…" : currentEntryFile.Sha256;
            return $"当前已核对入口“{normalizedEntry}”：文件已变化（历史：{prevReport.DisplayEntryShortSha256}，当前：{currShort}）。";
        }
        return $"当前已核对入口“{normalizedEntry}”：入口 SHA-256 与上次一致（其余文件尚无历史对照基线，原历史记录保持不变）。";
    }

    public static string NormalizeRelativePath(string path) =>
        (path ?? "").Replace('/', '\\').Trim().TrimStart('\\');
}

/// <summary>
/// Local-only hints persisted alongside the report in user app data to support "再次试用" replay
/// and crash-recovery correlation. NEVER included in exported shareable JSON reports.
/// </summary>
public sealed record TrialReplayHints(
    [property: JsonPropertyName("source_path_hint")] string SourcePathHint,
    [property: JsonPropertyName("entry_path_hint")] string EntryPathHint,
    [property: JsonPropertyName("instance_id")] string? InstanceId = null);

public sealed record TrialHistoryRecord(
    [property: JsonPropertyName("record_schema_version")] int RecordSchemaVersion,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc,
    [property: JsonPropertyName("report")] TrialSessionReport Report,
    [property: JsonPropertyName("local_hints")] TrialReplayHints LocalHints,
    [property: JsonPropertyName("local_folder_manifest")] TrialLocalFolderManifest? LocalFolderManifest = null)
{
    public const int CurrentRecordSchemaVersion = 1;

    [JsonIgnore] public string SessionId => Report.SessionId;
    [JsonIgnore] public string SoftwareName => Report.SoftwareName;
    [JsonIgnore] public string DisplayVersion => Report.DisplayVersion;
    [JsonIgnore] public bool HasVersion => Report.HasVersion;
    [JsonIgnore] public string InputKindLabel => Report.InputKindLabel;
    [JsonIgnore] public string EntryRelativePath => Report.EntryRelativePath;
    [JsonIgnore] public string DisplayStartedTime => Report.DisplayStartedTime;
    [JsonIgnore] public string OverallResultLabel => Report.OverallResultLabel;
    [JsonIgnore] public string DisplayEntrySummary => Report.DisplayEntrySummary;
}

public interface ITrialHistoryStore
{
    string HistoryDirectoryPath { get; }
    IReadOnlyList<TrialHistoryRecord> LoadAll();
    TrialHistoryRecord? LoadBySessionId(string sessionId);
    void Save(TrialHistoryRecord record);
    void ExportReport(TrialSessionReport report, string destinationPath);
}

public sealed class JsonTrialHistoryStore : ITrialHistoryStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object fileGate = new();
    public string HistoryDirectoryPath { get; }

    public JsonTrialHistoryStore(string? historyDirectoryPath = null)
    {
        if (string.IsNullOrWhiteSpace(historyDirectoryPath))
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData)) localData = Path.GetTempPath();
            HistoryDirectoryPath = Path.Combine(localData, "Contain", "IsolatedTrial", "History");
        }
        else
        {
            HistoryDirectoryPath = Path.GetFullPath(historyDirectoryPath);
        }
    }

    public IReadOnlyList<TrialHistoryRecord> LoadAll()
    {
        lock (fileGate)
        {
            // Read-only: never create directory on empty first use
            if (!Directory.Exists(HistoryDirectoryPath))
                return [];

            var list = new List<TrialHistoryRecord>();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(HistoryDirectoryPath, "*.json", SearchOption.TopDirectoryOnly).ToList(); }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }

            foreach (var file in files)
            {
                var record = TryLoadFile(file);
                if (record is not null)
                    list.Add(record);
            }

            list.Sort((a, b) =>
            {
                var cmp = b.Report.StartedAtUtc.CompareTo(a.Report.StartedAtUtc);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(b.SessionId, a.SessionId);
            });
            return list;
        }
    }

    public TrialHistoryRecord? LoadBySessionId(string sessionId)
    {
        if (!IsValidSessionId(sessionId)) return null;
        lock (fileGate)
        {
            var path = Path.Combine(HistoryDirectoryPath, sessionId + ".json");
            return File.Exists(path) ? TryLoadFile(path) : null;
        }
    }

    public void Save(TrialHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsValidSessionId(record.SessionId))
            throw new ArgumentException("Invalid trial history session_id.", nameof(record));

        lock (fileGate)
        {
            Directory.CreateDirectory(HistoryDirectoryPath);
            var targetPath = Path.Combine(HistoryDirectoryPath, record.SessionId + ".json");
            var tempPath = Path.Combine(HistoryDirectoryPath, $"{record.SessionId}.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(record, SerializerOptions);
            File.WriteAllText(tempPath, json, Utf8NoBom);
            File.Move(tempPath, targetPath, overwrite: true);
        }
    }

    public void ExportReport(TrialSessionReport report, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(destinationPath) || destinationPath.Contains('\0'))
            throw new ArgumentException("导出路径无效。", nameof(destinationPath));

        var full = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var json = SerializeReportJson(report);
        File.WriteAllText(full, json, Utf8NoBom);
    }

    public static string SerializeReportJson(TrialSessionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, SerializerOptions);
    }

    public static TrialSessionReport? DeserializeReportJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<TrialSessionReport>(json, SerializerOptions);
    }

    public static bool IsValidSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length < 6 || sessionId.Length > 40)
            return false;
        foreach (var ch in sessionId)
        {
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch == '-' || ch == '_'))
                return false;
        }
        return true;
    }

    public static string BuildSuggestedExportFileName(TrialSessionReport report)
    {
        var rawName = string.IsNullOrWhiteSpace(report.SoftwareName) ? "trial" : report.SoftwareName;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new StringBuilder(rawName.Length);
        foreach (var ch in rawName)
        {
            clean.Append(invalid.Contains(ch) ? '_' : ch);
        }
        var stem = clean.ToString().Trim();
        if (stem.Length > 36) stem = stem[..36].Trim();
        if (stem.Length == 0) stem = "trial";
        return $"Contain-隔离试用报告-{stem}-{report.SessionId}.json";
    }

    private static TrialHistoryRecord? TryLoadFile(string filePath)
    {
        try
        {
            if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                return null;
            var raw = File.ReadAllText(filePath, Utf8NoBom);
            var parsed = JsonSerializer.Deserialize<TrialHistoryRecord>(raw, SerializerOptions);
            if (parsed is null
                || parsed.Report is null
                || parsed.LocalHints is null
                || parsed.RecordSchemaVersion != TrialHistoryRecord.CurrentRecordSchemaVersion
                || !IsValidSessionId(parsed.Report.SessionId))
            {
                return null;
            }
            return parsed;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}
