using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Contain.Viewer;

/// <summary>A local report from the same read-only CLI contracts used by the viewer.</summary>
public static class ReportExporter
{
    public static async Task ExportAsync(CoreClient client, string database, string recordId,
        string? eventId, int pageSize, long offset, string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var target = Path.GetFullPath(outputPath);
        var db = Path.GetFullPath(database);
        // A Windows alternate data stream is not a separate report file.
        var root = Path.GetPathRoot(target) ?? string.Empty;
        if (target[root.Length..].Contains(':'))
            throw new CoreClientException("export_path", "报告必须是独立的普通文件，不能写入备用数据流。");
        if (File.Exists(target) || Directory.Exists(target))
            throw new CoreClientException("export_exists", "报告目标已存在；请选择新的文件名。不会覆盖数据库或其他文件。");
        if (!string.Equals(Path.GetExtension(target), ".html", StringComparison.OrdinalIgnoreCase))
            throw new CoreClientException("export_extension", "请选择 .html 本地报告文件。");
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent))
            throw new CoreClientException("export_directory", "报告目录不存在。");
        var protectedPaths = new[] { db, db + "-wal", db + "-shm", db + "-journal", client.ExecutablePath };
        if (protectedPaths.Any(path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase)))
            throw new CoreClientException("export_protected", "不能将报告写入数据库或程序文件。");

        var summary = await client.SummaryAsync(db, recordId, cancellationToken);
        var changes = await client.DiffAsync(db, recordId, cancellationToken);
        var history = await client.HistoryAsync(db, recordId, pageSize, offset, cancellationToken);
        CoreResponse? explanation = null;
        if (!string.IsNullOrEmpty(eventId))
        {
            var matching = history.Data.GetProperty("events").EnumerateArray()
                .Where(item => item.GetProperty("id").GetString() == eventId).ToArray();
            if (matching.Length != 1)
                throw new CoreClientException("export_event_page", "所选事件不在当前读回页中；请刷新并重新选择事件。");
            explanation = await client.ExplainAsync(db, eventId, cancellationToken);
            if (explanation.Data.GetProperty("app_id").GetString() != recordId ||
                !JsonView.JsonEqual(matching[0], explanation.Data.GetProperty("event")))
                throw new CoreClientException("export_event_mismatch", "事件解释与当前记录页不一致，未导出报告。");
        }
        // A database still being written can change while separate CLI commands
        // run. Do not present a mixed summary/state/page as one settled report.
        var checkedSummary = await client.SummaryAsync(db, recordId, cancellationToken);
        if (!JsonView.JsonEqual(summary.Envelope, checkedSummary.Envelope) ||
            !JsonView.JsonEqual(JsonView.Field(summary.Data, "files"), JsonView.Field(changes.Data, "files")) ||
            !JsonView.JsonEqual(JsonView.Field(summary.Data, "registry"), JsonView.Field(changes.Data, "registry")) ||
            !JsonView.JsonEqual(JsonView.Field(summary.Data, "inventory"), JsonView.Field(changes.Data, "inventory")))
            throw new CoreClientException("export_record_changed", "记录在读取期间发生变化；未导出混合结果，请稍后刷新重试。");

        var binaryHash = await HashFileAsync(client.ExecutablePath, cancellationToken);
        var html = BuildHtml(db, summary, changes, history, explanation, pageSize, offset, binaryHash);
        var temporary = Path.Combine(parent, ".contain-report-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous))
            {
                var bytes = new UTF8Encoding(false).GetBytes(html);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string BuildHtml(string database, CoreResponse summary, CoreResponse changes,
        CoreResponse history, CoreResponse? explanation, int pageSize, long offset, string binaryHash)
    {
        var data = summary.Data;
        var html = new StringBuilder("<!doctype html>\n<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        html.Append("<title>").Append(E(JsonView.Text(JsonView.Field(data, "name")))).Append(" · Contain 本地报告</title>");
        html.Append("<style>body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;background:#f4f6f8;color:#203044;margin:0;line-height:1.6}main{max-width:1120px;margin:auto;padding:36px}header,section{background:white;border:1px solid #dce3ea;border-radius:10px;padding:24px;margin-bottom:20px}h1{margin:4px 0 12px}h2{font-size:21px;margin:0 0 16px}h3{font-size:17px}p{margin:8px 0}.muted{color:#5b6d7d}.notice{background:#eef3f8;border-left:3px solid #496d91;padding:12px}.badge{display:inline-block;background:#edf2f6;border-radius:5px;padding:3px 9px}dl{display:grid;grid-template-columns:150px minmax(0,1fr);gap:6px 16px}dt{color:#5b6d7d}dd{margin:0;overflow-wrap:anywhere}.scroll{overflow:auto}table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;vertical-align:top;border-bottom:1px solid #e2e8ee;padding:9px 10px;overflow-wrap:anywhere}th{background:#f0f4f8}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f7f9fb;padding:14px;font-size:12px}details{margin-top:16px}summary{cursor:pointer;color:#355f85}code{overflow-wrap:anywhere}li{margin:5px 0}@media print{body{background:white}main{padding:0}section{break-inside:avoid}}</style></head><body><main>");
        html.Append("<header><div class=\"muted\">Contain 0.3.1 · 本地记录报告</div><h1>")
            .Append(E(JsonView.Text(JsonView.Field(data, "name")))).Append("</h1>");
        html.Append("<p>此报告由真实 CLI 查询生成，保存在本地，未上传。它包含记录的原始路径与证据字段。</p>");
        html.Append("<p class=\"notice\">状态变化不等于最终所有权。Unknown 表示证据不足，不表示危险；High 也不代表可以安全删除。记录结束与观察完整是两个不同判断。</p><dl>");
        Pair(html, "记录 ID", JsonView.Text(JsonView.Field(data, "id")));
        Pair(html, "数据库", database);
        Pair(html, "记录开始", JsonView.Text(JsonView.Field(data, "started_at")));
        Pair(html, "记录结束时间", JsonView.Text(JsonView.Field(data, "finished_at")));
        Pair(html, "记录状态", JsonView.Text(JsonView.Field(data, "capture_state"), "未记录（旧记录）"));
        Pair(html, "覆盖质量", JsonView.Text(JsonView.Field(JsonView.Field(data, "quality"), "level")));
        Pair(html, "核心文件 SHA-256（导出时）", binaryHash);
        html.Append("</dl></header>");

        html.Append("<section><h2>观察范围与限制</h2><dl>");
        Pair(html, "程序", JsonView.Text(JsonView.Field(data, "installer")));
        Pair(html, "观察目录", string.Join("\n", JsonView.Items(JsonView.Field(data, "watch_roots")).Select(v => JsonView.Text(v))));
        Pair(html, "注册表范围", JsonView.Text(JsonView.Field(data, "registry_key"), "未选择 / 未记录"));
        var backend = JsonView.Field(data, "backend");
        Pair(html, "进程源", JsonView.Text(JsonView.Field(backend, "etw_process")));
        Pair(html, "文件源", JsonView.Text(JsonView.Field(backend, "etw_file")));
        Pair(html, "注册表源", JsonView.Text(JsonView.Field(backend, "etw_registry")));
        Pair(html, "ETW 事件丢失", JsonView.Text(JsonView.Field(backend, "etw_events_lost"), "不可用"));
        Pair(html, "ETW 缓冲区丢失", JsonView.Text(JsonView.Field(backend, "etw_buffers_lost"), "不可用"));
        // Availability comes from the same Rust production summary renderer,
        // rather than interpreting legacy/default raw statistics in the UI.
        var counts = JsonView.Field(data, "summary_counts");
        Pair(html, "时间线观察数", JsonView.Text(JsonView.Field(counts, "events_retained"), "未测 / 不可用"));
        Pair(html, "归一化源操作数", JsonView.Text(JsonView.Field(counts, "events_normalized"), "未测 / 不可用"));
        Pair(html, "已知执行进程的源观察", JsonView.Text(JsonView.Field(counts, "known_actor_events"), "未测 / 不可用"));
        Pair(html, "未知执行进程的源观察", JsonView.Text(JsonView.Field(counts, "unknown_actor_events"), "未测 / 不可用"));
        Pair(html, "未解析名称 / 资源", JsonView.Text(JsonView.Field(counts, "unknown_resource_events"), "未测 / 不可用"));
        Pair(html, "应用记录丢失", JsonView.Text(JsonView.Field(counts, "application_records_dropped"), "未测 / 不可用"));
        Pair(html, "解码错误", JsonView.Text(JsonView.Field(counts, "decode_errors"), "未测 / 不可用"));
        Pair(html, "快照缺口", JsonView.Text(JsonView.Field(counts, "snapshot_gaps"), "未测 / 不可用"));
        Pair(html, "通知缺口", JsonView.Text(JsonView.Field(counts, "notification_gaps"), "未测 / 不可用"));
        Pair(html, "排空超时", JsonView.Text(JsonView.Field(counts, "drain_timed_out"), "未测 / 不可用"));
        html.Append("</dl><ul>");
        var limitations = JsonView.Items(JsonView.Field(JsonView.Field(data, "quality"), "reasons"))
            .Concat(JsonView.Items(JsonView.Field(data, "warnings")))
            .Concat(JsonView.Items(JsonView.Field(backend, "warnings")))
            .Select(v => JsonView.Text(v)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var limitation in limitations) html.Append("<li>").Append(E(limitation)).Append("</li>");
        if (limitations.Length == 0) html.Append("<li>记录未提供额外限制说明；这不代表已证明覆盖完整。</li>");
        html.Append("</ul></section>");

        html.Append("<section><h2>已记录的执行进程</h2>");
        Table(html, ["PID", "出生时间标识（精确 FILETIME）", "程序", "关联依据", "原因"],
            JsonView.Items(JsonView.Field(data, "processes")).Select(p => new[]
            {
                T(p, "pid"), T(p, "creation_time"), T(p, "image"), T(p, "confidence"), T(p, "reason")
            }));
        html.Append("</section><section><h2>前后状态变化</h2><p class=\"muted\">下列是状态差异，不能与源事件相加视为独立操作数，也不能据此推定所有权。</p><h3>文件</h3>");
        Table(html, ["操作", "记录路径", "归属证据", "原因"],
            JsonView.Items(JsonView.Field(changes.Data, "files")).Select(p => new[] { T(p, "operation"), T(p, "path"), T(p, "confidence"), T(p, "reason") }));
        html.Append("<h3>注册表</h3>");
        Table(html, ["操作", "键 / 值", "归属证据", "原因"],
            JsonView.Items(JsonView.Field(changes.Data, "registry")).Select(p => new[] { T(p, "operation"), T(p, "key") + "\\" + T(p, "name"), T(p, "confidence"), T(p, "reason") }));
        html.Append("<h3>其他配置记录</h3>");
        Table(html, ["类别", "操作", "名称", "归属证据", "原因"],
            JsonView.Items(JsonView.Field(changes.Data, "inventory")).Select(p => new[] { T(p, "kind"), T(p, "operation"), T(p, "name"), T(p, "confidence"), T(p, "reason") }));
        html.Append("</section><section><h2>源操作与观察事件 · 当前页</h2><p class=\"notice\">仅包含当前事件页，未应用界面中的当前页文字筛选；不是全部事件导出。偏移 ")
            .Append(offset.ToString(CultureInfo.InvariantCulture)).Append("，页大小 ").Append(pageSize.ToString(CultureInfo.InvariantCulture)).Append("。</p>");
        Table(html, ["事件 ID", "时间", "操作", "记录名称", "应用归因"],
            JsonView.Items(JsonView.Field(history.Data, "events")).Select(p => new[] { T(p, "id"), T(p, "timestamp"), T(p, "operation"), T(p, "resource"), T(p, "confidence") }));
        html.Append("</section><section><h2>所选事件依据</h2>");
        if (explanation is null)
            html.Append("<p>导出时未选择事件。此报告未将任何事件解释为所选依据。</p>");
        else
        {
            var selected = explanation.Data.GetProperty("event");
            var evidence = JsonView.Field(selected, "evidence");
            var raw = JsonView.Field(selected, "raw");
            var name = JsonView.Field(raw, "file_name");
            var diagnosticName = JsonView.Field(raw, "file_diagnostics");
            var dimensions = JsonView.Field(selected, "dimensions");
            var nameSource = JsonView.Field(name, "path_source");
            if (nameSource.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                nameSource = JsonView.Field(diagnosticName, "path_source");
            var nameTime = JsonView.Field(name, "observed_ticks");
            if (nameTime.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                nameTime = JsonView.Field(JsonView.Field(diagnosticName, "cache_origin"), "event_timestamp");
            html.Append("<dl>");
            Pair(html, "事件 ID", T(selected, "id"));
            Pair(html, "源操作", T(selected, "operation"));
            Pair(html, "时间", T(selected, "timestamp"));
            Pair(html, "精确 FILETIME", T(selected, "timestamp_ticks"));
            Pair(html, "记录名称", T(selected, "resource"));
            Pair(html, "执行进程", T(evidence, "process_image") + " · PID " + T(evidence, "pid"));
            Pair(html, "进程出生标识", T(evidence, "process_creation_time"));
            Pair(html, "实际数据源", T(evidence, "source"));
            Pair(html, "应用归因", T(selected, "confidence"));
            Pair(html, "执行进程身份依据", T(dimensions, "actor"));
            Pair(html, "记录名称解析依据", T(dimensions, "resource"));
            Pair(html, "操作结果依据", T(dimensions, "operation"));
            Pair(html, "依据 / 未知原因", T(selected, "reason"));
            Pair(html, "名称来源", JsonView.Text(nameSource, "未记录；可查看原始字段"));
            Pair(html, "名称观察时间", JsonView.Text(nameTime, "不可用"));
            Pair(html, "请求结果", JsonView.Text(JsonView.Field(selected, "success"), "未确认"));
            html.Append("</dl><p class=\"notice\">名称可能是历史缓存；没有当前路径验证时，不应将它当作已核实的当前名称。原生操作也不证明应用显式调用了同名 API，且不证明最终所有权。</p>");
            Table(html, ["关系来源", "关系目标", "依据等级", "原因"],
                JsonView.Items(JsonView.Field(explanation.Data, "edges")).Select(p => new[] { T(p, "from"), T(p, "to"), T(p, "confidence"), T(p, "reason") }));
        }
        html.Append("</section><section><h2>高级：原始 CLI JSON</h2><p>以下保留 CLI 字段表达、精确整数、时间字符串与 null；没有重算归因或补全未知字段。</p>");
        foreach (var response in new[] { summary, changes, history, explanation })
        {
            if (response is null) continue;
            var kind = response.Envelope.GetProperty("kind").GetString()!;
            html.Append("<details><summary>").Append(E(kind)).Append("</summary><pre data-kind=\"").Append(E(kind)).Append("\">")
                .Append(E(response.RawJson)).Append("</pre></details>");
        }
        html.Append("</section></main></body></html>\n");
        return html.ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string T(JsonElement value, string name) => JsonView.Text(JsonView.Field(value, name));
    private static void Pair(StringBuilder output, string label, string value) =>
        output.Append("<dt>").Append(E(label)).Append("</dt><dd>").Append(E(string.IsNullOrEmpty(value) ? "未记录" : value)).Append("</dd>");

    private static void Table(StringBuilder output, IReadOnlyList<string> headings, IEnumerable<string[]> rows)
    {
        output.Append("<div class=\"scroll\"><table><thead><tr>");
        foreach (var heading in headings) output.Append("<th>").Append(E(heading)).Append("</th>");
        output.Append("</tr></thead><tbody>");
        var count = 0;
        foreach (var row in rows)
        {
            count++;
            output.Append("<tr>");
            foreach (var value in row) output.Append("<td>").Append(E(value)).Append("</td>");
            output.Append("</tr>");
        }
        if (count == 0) output.Append("<tr><td colspan=\"").Append(headings.Count).Append("\">此范围没有记录。</td></tr>");
        output.Append("</tbody></table></div>");
    }
}
