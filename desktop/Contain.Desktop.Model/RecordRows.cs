using System.Globalization;
using System.Text.Json;
using Contain.Viewer;

namespace Contain.Desktop.Model;

public sealed record SessionRow(string Id, string Name, string StartedAt, string DisplayTime, string State, string Coverage, JsonElement Data)
{
    public string Identity => "session:" + Id;
    // The stable suffix distinguishes equal names/times without showing a full UUID by default.
    public string ShortIdentity => Id.Length <= 8 ? Id : Id[^8..];
    public string Disambiguation { get; init; } = "";
    internal static SessionRow From(JsonElement item)
    {
        var started = RecordText.Value(item, "started_at");
        return new(RecordText.Value(item, "id"), RecordText.Value(item, "name"), started,
            DateTimeOffset.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) : started,
            RecordText.State(RecordText.Value(item, "capture_state")), "覆盖范围与来源请查看详情", item.Clone());
    }
}

public sealed record EventRow(string Id, string Time, string Operation, string OperationLabel, string Resource, string Confidence, JsonElement Data)
{
    public string Identity => "event:" + Id;
    internal static EventRow From(JsonElement item) => new(RecordText.Value(item, "id"), RecordText.Value(item, "timestamp"),
        RecordText.Value(item, "operation"), RecordText.Operation(RecordText.Value(item, "operation")), RecordText.Value(item, "resource"),
        RecordText.Confidence(RecordText.Value(item, "confidence")), item.Clone());
}

public sealed record ChangeRow(string Kind, string Operation, string Resource, string Confidence, string Reason, JsonElement Data)
{
    internal static ChangeRow From(string kind, JsonElement item) => new(kind, RecordText.Value(item, "operation"),
        kind == "文件" ? RecordText.Value(item, "path") : kind == "注册表" ? RecordText.Value(item, "key") + "\\" + RecordText.Value(item, "name") : RecordText.Value(item, "name"),
        RecordText.Confidence(RecordText.Value(item, "confidence")), RecordText.Value(item, "reason"), item.Clone());
}

/// <summary>Captured before a save dialog's nested message loop; query, identity and page stay fixed.</summary>
public sealed record RecordExportContext(string Database, string SessionId, string? SelectedEventId, long Offset,
    string? Search, string? Fingerprint, long? MatchCount, long ViewVersion, string RecordName = "", string StartedAt = "")
{
    public ReportExportIdentity Identity => new(SessionId, RecordName, StartedAt);
    public string DefaultFileName => ExportFileName.Create(RecordName, StartedAt, SessionId, Search is not null);
}

internal static class RecordText
{
    internal static string Value(JsonElement value, string name) => JsonView.Text(JsonView.Field(value, name), "未提供");
    internal static string State(string value) => value switch
    {
        "finished" => "已结束", "capturing" => "未结束 · 采集中", "finalizing" => "未结束 · 整理中",
        "failed" => "中断 / 失败", "未提供" => "结束状态未提供", _ => "未知状态：" + value
    };
    internal static string Confidence(string value) => value == "Unknown" ? "Unknown · 证据不足" : value;
    internal static string Operation(string value) => value switch
    {
        "write_requested" => "文件写入请求", "read_requested" => "文件读取请求", "create_requested" => "文件创建 / 打开请求",
        "open_requested" => "文件打开请求", "create_new_file" => "观测到新建文件",
        "delete_requested" => "删除请求", "rename_requested" => "重命名请求", "close" => "关闭文件",
        "process_start" => "进程启动", "process_stop" or "process_end" => "进程退出", "file_created" => "观测到文件新增",
        "file_modified" => "观测到文件变化", "file_deleted" => "观测到文件消失", _ => value
    };
}
