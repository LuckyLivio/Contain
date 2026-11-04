using System.Text.Json;

namespace Contain.Viewer;

/// <summary>The CLI document is kept as JSON, without floating-point or date conversion.</summary>
public sealed class CoreResponse
{
    public string RawJson { get; }
    public JsonElement Envelope { get; }
    public JsonElement Data => Envelope.GetProperty("data");

    private CoreResponse(string rawJson, JsonElement envelope)
    {
        RawJson = rawJson;
        Envelope = envelope;
    }

    public static CoreResponse Parse(string rawJson, string expectedKind)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 128
            });
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, "JSON 顶层必须是对象。");
            RejectDuplicateFields(root);
            Require(root.TryGetProperty("schema_version", out var schema) &&
                schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version == 3,
                "不支持此 CLI JSON 版本；查看器需要 schema_version 3。");
            Require(String(root, "kind") == expectedKind, "CLI 返回的文档类型与请求不一致。");
            Require(root.TryGetProperty("data", out var data), "CLI 文档缺少 data。");
            ValidateData(data, expectedKind);
            return new CoreResponse(rawJson, root.Clone());
        }
        catch (JsonException error)
        {
            throw new CoreClientException("invalid_json", "CLI 未返回有效的单个 JSON 文档。", diagnostic: error.Message, inner: error);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException)
        {
            throw new CoreClientException("invalid_contract", "CLI 嵌套字段缺失或类型不正确。", diagnostic: error.Message, inner: error);
        }
    }

    private static void ValidateData(JsonElement data, string kind)
    {
        if (kind == "list_details")
        {
            Require(data.ValueKind == JsonValueKind.Array, "记录列表必须是数组。");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in data.EnumerateArray())
            {
                var id = String(row, "id");
                Require(id.Length != 0 && ids.Add(id), "记录列表含缺失或重复的 ID。");
                String(row, "name");
                String(row, "started_at");
                OptionalString(row, "finished_at");
                OptionalString(row, "capture_state");
            }
            return;
        }

        Require(data.ValueKind == JsonValueKind.Object, "CLI data 必须是对象。");
        switch (kind)
        {
            case "inspect_summary":
                String(data, "id");
                String(data, "name");
                String(data, "started_at");
                OptionalString(data, "finished_at");
                OptionalString(data, "capture_state");
                Require(Array(data, "events").GetArrayLength() == 0, "摘要意外包含完整事件历史。");
                Array(data, "watch_roots");
                Array(data, "files");
                Array(data, "registry");
                Array(data, "inventory");
                OptionalObject(data, "backend");
                OptionalObject(data, "stats");
                OptionalObject(data, "quality");
                var counts = OptionalObject(data, "summary_counts");
                if (counts.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "events_retained", "events_normalized", "known_actor_events",
                        "unknown_actor_events", "unknown_resource_events", "application_records_dropped",
                        "decode_errors", "snapshot_gaps", "notification_gaps" })
                    {
                        if (counts.TryGetProperty(key, out var count) && count.ValueKind != JsonValueKind.Null)
                            Require(count.ValueKind == JsonValueKind.Number && count.TryGetUInt64(out _), $"CLI 计数 {key} 必须是精确非负整数或 null。");
                    }
                    if (counts.TryGetProperty("drain_timed_out", out var timeout))
                        Require(timeout.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null,
                            "排空超时指标必须是布尔值或 null。");
                }
                foreach (var process in Array(data, "processes").EnumerateArray())
                {
                    Require(process.ValueKind == JsonValueKind.Object, "进程记录必须是对象。");
                    DecimalString(process, "creation_time");
                    DecimalString(process, "parent_creation_time");
                    DecimalString(process, "ended_at");
                }
                break;
            case "diff":
                String(data, "app_id");
                Array(data, "files");
                Array(data, "registry");
                Array(data, "inventory");
                break;
            case "history":
                String(data, "app_id");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in Array(data, "events").EnumerateArray())
                {
                    ValidateEvent(item);
                    Require(ids.Add(String(item, "id")), "当前事件页含重复 ID。");
                }
                break;
            case "explain":
                String(data, "app_id");
                Require(data.TryGetProperty("event", out var selected), "解释缺少事件。");
                ValidateEvent(selected);
                Array(data, "edges");
                Array(data, "operations");
                break;
            default:
                throw new CoreClientException("unsupported_document", "查看器不支持此 CLI 文档类型。");
        }
    }

    private static void ValidateEvent(JsonElement item)
    {
        Require(String(item, "id").Length != 0, "事件缺少 ID。");
        String(item, "timestamp");
        String(item, "operation");
        String(item, "resource");
        // Unknown/new confidence and operation strings are preserved, not promoted.
        OptionalString(item, "confidence");
        DecimalString(item, "timestamp_ticks");
        var evidence = OptionalObject(item, "evidence");
        if (evidence.ValueKind == JsonValueKind.Object)
            DecimalString(evidence, "process_creation_time");
        var raw = OptionalObject(item, "raw");
        if (raw.ValueKind == JsonValueKind.Object)
        {
            var name = OptionalObject(raw, "file_name");
            if (name.ValueKind == JsonValueKind.Object) DecimalString(name, "observed_ticks");
        }
    }

    private static void DecimalString(JsonElement item, string key)
    {
        Require(item.ValueKind == JsonValueKind.Object, "时间标识所在的记录必须是对象。");
        if (!item.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return;
        Require(value.ValueKind == JsonValueKind.String, $"{key} 必须保留为精确十进制字符串。");
        var text = value.GetString()!;
        Require(text.Length > 0 && text.All(c => c is >= '0' and <= '9'), $"{key} 不是十进制时间标识。");
    }

    private static string String(JsonElement item, string key)
    {
        Require(item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) &&
            value.ValueKind == JsonValueKind.String, $"CLI 字段 {key} 缺失或类型不正确。");
        return item.GetProperty(key).GetString()!;
    }

    private static void OptionalString(JsonElement item, string key)
    {
        if (item.TryGetProperty(key, out var value))
            Require(value.ValueKind is JsonValueKind.String or JsonValueKind.Null, $"CLI 字段 {key} 类型不正确。");
    }

    private static JsonElement OptionalObject(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out var value)) return default;
        Require(value.ValueKind is JsonValueKind.Object or JsonValueKind.Null, $"CLI 字段 {key} 必须是对象或 null。");
        return value;
    }

    private static JsonElement Array(JsonElement item, string key)
    {
        Require(item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array,
            $"CLI 字段 {key} 必须是数组。");
        return item.GetProperty(key);
    }

    private static void RejectDuplicateFields(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                Require(names.Add(field.Name), "CLI JSON 含重复字段，无法确定解释。");
                RejectDuplicateFields(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new CoreClientException("invalid_contract", message);
    }
}

public sealed class CoreClientException : Exception
{
    public string Code { get; }
    public int? ExitCode { get; }
    public string Diagnostic { get; }

    public CoreClientException(string code, string message, int? exitCode = null,
        string diagnostic = "", Exception? inner = null) : base(message, inner)
    {
        Code = code;
        ExitCode = exitCode;
        Diagnostic = diagnostic;
    }
}
