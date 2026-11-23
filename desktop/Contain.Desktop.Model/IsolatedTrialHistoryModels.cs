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
    [property: JsonPropertyName("local_hints")] TrialReplayHints LocalHints)
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
