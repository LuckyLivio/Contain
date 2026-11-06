using System.Globalization;
using System.Text.Json;

namespace Contain.Viewer;

/// <summary>A projection of this session's read-only queries, never of task memory.</summary>
internal sealed record OverviewPresentation(string Installer, string InstallerNote, string Record,
    string RecordNote, string Scope, string RegistryScope, string Coverage, string Changes, string ChangesNote,
    string Alert, bool IsUnfinished)
{
    internal static OverviewPresentation Build(JsonElement summary, JsonElement diff)
    {
        var state = Text(summary, "capture_state");
        var finished = state == "finished";
        var exit = Field(summary, "exit_code");
        var hasExit = exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out _);
        var exitCode = hasExit ? exit.GetInt32() : 0;
        var installer = hasExit ? $"已记录退出码 {exitCode.ToString(CultureInfo.InvariantCulture)}" : "退出结果未记录";
        var installerNote = hasExit
            ? exitCode == 0 ? "退出码为 0；最终安装状态未验证。" : "非零退出；具体含义由安装器定义。"
            : "没有可靠退出码，不能判断安装器结果。";
        var record = finished ? "会话已结束" : state is "capturing" or "finalizing" or "failed"
            ? "会话未完成" : "会话结束状态未确认";
        var recordNote = "已从数据库读取保存的记录。" +
            (finished ? "" : "\n采集进程当前状态未确认。");
        var roots = Field(summary, "watch_roots");
        var directories = roots.ValueKind == JsonValueKind.Array && roots.GetArrayLength() != 0
            ? string.Join("\r\n", roots.EnumerateArray().Select(Value)) : "未记录 / 不可用";
        var registry = Text(summary, "registry_key");
        var follow = RecordingScope.IsFollowInstaller(summary);
        var scope = follow ? "跟随安装器记录\r\n本次安装器及已验证直接子进程的文件事件；未预设观察目录。" : "观察目录：\r\n" + directories;
        var registryScope = "注册表范围：" + (registry is null ? "未选择 / 未记录" : "HKEY_CURRENT_USER\\" + registry);
        var quality = Field(summary, "quality");
        var coverage = Text(quality, "level") switch
        {
            "Degraded" => "覆盖受限（Degraded）",
            "Incomplete" => "记录证据不完整（Incomplete）",
            "Complete" => "覆盖标签：Complete（不表示最终所有权）",
            { } value => "覆盖标签未识别：" + value,
            _ => "覆盖质量未记录 / 不可用"
        };
        var changes = follow ? "文件状态对比：未采集安装前基线\n请在事件与依据查看文件操作。" : $"文件 {Count(diff, "files")} · 注册表值 {Count(diff, "registry")}\n其他配置 {Count(diff, "inventory")}";
        var changesNote = follow ? "事件说明执行进程关联；未建立文件新增、当前路径或最终归属结论。" : finished
            ? "已保存的状态变化条目；0 项不等于没有发生变化。"
            : "仅列已保存的条目；最终状态差异尚未确认。";
        var backend = Field(summary, "backend");
        var diagnostics = Diagnostics(summary).ToArray();
        var streamError = Text(Field(backend, "stream"), "error");
        var alerts = new List<string>();
        if (finished && HasRecordedLoss(summary)) alerts.Add("记录已保存；采集期间丢失了部分记录。");
        if (streamError is not null) alerts.Add("记录来源报告错误；完整原因可在技术详情中查看或复制。");
        if (diagnostics.Any(text => text.Contains("Legacy capture:", StringComparison.Ordinal)))
            alerts.Add("旧记录未保存完整覆盖元数据；缺失统计不可按 0 解读。");
        var losses = new List<string>();
        var counts = Field(summary, "summary_counts");
        AddNonzero(losses, backend, "etw_events_lost", "ETW 丢失事件");
        AddNonzero(losses, backend, "etw_buffers_lost", "丢失缓冲区");
        AddNonzero(losses, counts, "application_records_dropped", "丢弃记录");
        AddNonzero(losses, counts, "decode_errors", "解码错误");
        AddNonzero(losses, counts, "snapshot_gaps", "快照缺口");
        AddNonzero(losses, counts, "notification_gaps", "通知缺口");
        if (Field(counts, "drain_timed_out").ValueKind == JsonValueKind.True) losses.Add("排空超时");
        if (losses.Count > 0) alerts.Add("已记录的缺口：" + string.Join("；", losses) + "。");
        if (streamError is null && diagnostics.Length > 0)
        {
            var important = diagnostics.FirstOrDefault(text => new[] { "error", "fail", "missing", "lost", "gap", "timeout", "incomplete" }
                .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))) ?? diagnostics[0];
            alerts.Add(important.StartsWith("Scoped capture cannot prove a complete machine footprint", StringComparison.Ordinal)
                ? "仅覆盖所选范围；无法证明完整软件足迹，仍可能遗漏不支持的路径与事件。"
                : "记录限制：" + Brief(important));
        }
        if (diagnostics.Length > 1) alerts.Add($"共 {diagnostics.Length} 条诊断，技术详情保留完整文字。");
        if (!finished) alerts.Insert(0, "记录未确认完整结束；已保存条目不代表完整证据。可展开技术详情检查已记录的阶段。");
        return new(installer, installerNote, record, recordNote, scope, registryScope, coverage, changes, changesNote,
            string.Join("\r\n", alerts), !finished);
    }

    internal static bool HasRecordedLoss(JsonElement summary)
    {
        var backend = Field(summary, "backend");
        var counts = Field(summary, "summary_counts");
        var pipeline = Field(backend, "pipeline");
        var handoff = Field(backend, "handoff");
        var stream = Field(backend, "stream");
        return Positive(counts, "application_records_dropped", "decode_errors")
            || Positive(backend, "dropped_events", "etw_events_lost", "etw_buffers_lost", "decode_errors", "context_losses")
            || Positive(pipeline, "queue_overflow", "enqueue_disconnected", "retention_dropped")
            || Positive(handoff, "overflow", "oversized", "disconnected", "serialize_failed")
            || Positive(stream, "quota_dropped", "failed")
            || Positive(Field(backend, "follow_installer"), "candidate_dropped");
    }

    // These are saved, overlapping diagnostic counters. Never add them to the
    // application total: retention_dropped aliases stream.quota_dropped, and
    // follow candidate loss is already represented in context_losses.
    internal static string LossDetails(JsonElement summary)
    {
        var backend = Field(summary, "backend");
        var pipeline = Field(backend, "pipeline");
        var handoff = Field(backend, "handoff");
        var stream = Field(backend, "stream");
        var follow = Field(backend, "follow_installer");
        // Old readback fills absent backend scalars with default zero. Without
        // pipeline accounting that zero does not prove measured context loss.
        var context = Number(backend, "context_losses");
        if (context == "0" && pipeline.ValueKind != JsonValueKind.Object) context = "未提供";
        return "丢失环节（保存的分项，不重复求和）\r\n"
            + $"接收队列：溢出 {Number(pipeline, "queue_overflow")} · 断开 {Number(pipeline, "enqueue_disconnected")} · 待取出 {Number(pipeline, "queue_pending")}\r\n"
            + $"线程间交付：溢出 {Number(handoff, "overflow")} · 超大记录 {Number(handoff, "oversized")} · 断开 {Number(handoff, "disconnected")} · 序列化失败 {Number(handoff, "serialize_failed")} · 待交付 {Number(handoff, "pending")}\r\n"
            + $"原始记录保存：配额丢弃 {Number(stream, "quota_dropped")} · 保存失败 {Number(stream, "failed")}\r\n"
            + $"跟随候选丢失：{Number(follow, "candidate_dropped")} · 上下文丢失总数：{context}（不同于应用原始记录丢弃总数）\r\n"
            + $"正常范围筛除（不计丢失）：主动筛除 {Number(pipeline, "deliberately_filtered")} · 不支持记录 {Number(pipeline, "unsupported_records")} · 无关进程 {Number(follow, "excluded_other")} · 自身 I/O {Number(follow, "excluded_self_io")}\r\n"
            + $"跟随身份未解析：{Number(follow, "unresolved")}（不作为已验证目标写入）";
    }

    private static bool Positive(JsonElement data, params string[] keys) => keys.Any(key =>
        Field(data, key) is var field && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var value) && value > 0);

    private static string Number(JsonElement data, string key) => Field(data, key) is var field
        && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var value)
            ? value.ToString(CultureInfo.InvariantCulture) : "未提供";

    internal static IEnumerable<string> Diagnostics(JsonElement summary)
    {
        var backend = Field(summary, "backend");
        var values = new List<string>();
        foreach (var array in new[] { Field(Field(summary, "quality"), "reasons"), Field(summary, "warnings"), Field(backend, "warnings") })
            if (array.ValueKind == JsonValueKind.Array)
                values.AddRange(array.EnumerateArray().Select(Value).Where(text => text != "未提供"));
        if (Text(Field(backend, "stream"), "error") is { } error) values.Insert(0, error);
        return values.Distinct(StringComparer.Ordinal);
    }

    private static string Count(JsonElement diff, string key)
    {
        var value = Field(diff, key);
        return value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength().ToString(CultureInfo.InvariantCulture) + " 项" : "未提供 / 不可用";
    }
    private static void AddNonzero(List<string> values, JsonElement data, string key, string label)
    {
        var count = Field(data, key);
        if (count.ValueKind == JsonValueKind.Number && count.TryGetUInt64(out var value) && value > 0)
            values.Add(label + " " + value.ToString(CultureInfo.InvariantCulture));
    }
    private static string Brief(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ');
        return line.Length > 140 ? line[..140] + "…（完整内容见技术详情）" : line;
    }
    private static JsonElement Field(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var field) ? field : default;
    private static string? Text(JsonElement data, string name) => Field(data, name) is var field && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString() : null;
    private static string Value(JsonElement value) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : "未提供";
}
