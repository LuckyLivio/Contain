using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Contain.Viewer;

public static partial class ReportExporter
{
    // The pipe already refuses responses above 64 MiB; escaped HTML is bounded
    // independently so expansion cannot turn a bounded response into an unbounded file.
    private const long MaxSearchReportBytes = 64L * 1024 * 1024;

    public static async Task<long> ExportSearchAsync(CoreClient client, string database, string recordId,
        string search, string expectedFingerprint, long expectedCount, string outputPath,
        CancellationToken cancellationToken = default, ReportExportIdentity? identity = null)
    {
        var target = ValidateTarget(client, database, outputPath);
        if (expectedCount <= 0)
            throw new CoreClientException("export_empty", "没有匹配项可导出，请先完成有效搜索。");
        if (expectedFingerprint.Length != 64 || !expectedFingerprint.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new CoreClientException("export_search_identity", "搜索结果标识无效，请刷新搜索后再导出。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var response = await client.SearchMatchesAsync(database, recordId, search, token).ConfigureAwait(false);
            var data = response.Data;
            identity?.ValidateSearch(data);
            if (data.GetProperty("matched_total").GetInt64() != expectedCount ||
                data.GetProperty("search_fingerprint").GetString() != expectedFingerprint)
                throw new CoreClientException("export_record_changed", "搜索结果已变化，未导出报告。请刷新搜索后再导出。");

            // The CLI supplies the name, complete ordered events and count from
            // one read snapshot. Do not mix in a second summary/explain invocation.
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".contain-report-" + Guid.NewGuid().ToString("N") + ".tmp");
            var created = false;
            try
            {
                long written;
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    8192, FileOptions.Asynchronous))
                {
                    created = true;
                    // Even when small buffered writes complete synchronously,
                    // escaping a large result must not occupy the UI thread.
                    written = await Task.Run(() => WriteSearchHtmlWithIdentityAsync(stream, data, token, identity), token).ConfigureAwait(false);
                    if (written != expectedCount)
                        throw new CoreClientException("export_count", "报告事件序列不完整，未提交文件。请刷新搜索后重试。");
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite: false);
                return written;
            }
            finally
            {
                if (created && File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoreClientException("export_timeout", "导出超时，未提交不完整报告。请稍后重试。");
        }
    }

    private static Task<long> WriteSearchHtmlAsync(Stream stream, JsonElement data, CancellationToken token) =>
        WriteSearchHtmlWithIdentityAsync(stream, data, token, null);

    private static async Task<long> WriteSearchHtmlWithIdentityAsync(Stream stream, JsonElement data, CancellationToken token, ReportExportIdentity? identity)
    {
        var utf8 = new UTF8Encoding(false);
        long bytesWritten = 0;
        async Task Write(string text)
        {
            token.ThrowIfCancellationRequested();
            var size = utf8.GetByteCount(text);
            if (size > MaxSearchReportBytes - bytesWritten)
                throw new CoreClientException("export_limit", "匹配结果报告超过 64 MiB 限额，未导出截断文件。请缩小搜索范围。");
            await stream.WriteAsync(utf8.GetBytes(text), token).ConfigureAwait(false);
            bytesWritten += size;
        }
        async Task Encoded(string text)
        {
            // Bound HTML escaping allocations, retaining UTF-16 surrogate pairs.
            for (var offset = 0; offset < text.Length;)
            {
                var count = Math.Min(4096, text.Length - offset);
                if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count])) count--;
                await Write(WebUtility.HtmlEncode(text.Substring(offset, count))).ConfigureAwait(false);
                offset += count;
            }
        }
        async Task Pair(string label, string value)
        {
            await Write("<dt>").ConfigureAwait(false); await Encoded(label).ConfigureAwait(false);
            await Write("</dt><dd>").ConfigureAwait(false); await Encoded(value).ConfigureAwait(false);
            await Write("</dd>\n").ConfigureAwait(false);
        }

        await Write("<!doctype html>\n<html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>").ConfigureAwait(false);
        await Encoded(data.GetProperty("name").GetString()!).ConfigureAwait(false);
        await Write(" · Contain 搜索匹配报告</title><style>body{font-family:'Segoe UI','Microsoft YaHei',sans-serif;background:#f4f6f8;color:#203044;margin:0;line-height:1.6}main{max-width:1200px;margin:auto;padding:28px}header,section{background:white;border:1px solid #dce3ea;border-radius:10px;padding:22px;margin-bottom:18px}h1{margin:4px 0 12px}dl{display:grid;grid-template-columns:150px minmax(0,1fr);gap:6px 16px}dt{color:#5b6d7d}dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}table{border-collapse:collapse;width:100%;font-size:14px}th,td{text-align:left;vertical-align:top;border-bottom:1px solid #e2e8ee;padding:9px;white-space:pre-wrap;overflow-wrap:anywhere}th{background:#f0f4f8}.scroll{overflow:auto}.notice{background:#eef3f8;padding:12px}@media print{body{background:white}main{padding:0}tr{break-inside:avoid}}</style></head><body><main><header><p>").ConfigureAwait(false);
        await Encoded(ProductVersion.Display).ConfigureAwait(false);
        await Write(" · 本地搜索匹配报告</p><h1>").ConfigureAwait(false);
        await Encoded(data.GetProperty("name").GetString()!).ConfigureAwait(false);
        await Write("</h1><dl>").ConfigureAwait(false);
        await Pair("会话 UUID", data.GetProperty("app_id").GetString()!).ConfigureAwait(false);
        if (identity is not null) await Pair("记录开始时间", identity.StartedAt.Length == 0 ? "未提供" : identity.StartedAt).ConfigureAwait(false);
        await Pair("搜索词", data.GetProperty("search").GetString()!).ConfigureAwait(false);
        await Pair("导出范围", "本次会话全部匹配事件，按搜索时间线顺序；不受界面当前页或所选事件限制。").ConfigureAwait(false);
        await Pair("实际事件条数", data.GetProperty("events").GetArrayLength().ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await Write("</dl><p class=\"notice\">ASCII 字母忽略大小写；中文、空格、%、_ 按普通文本包含匹配。Unknown 表示证据不足；High 不代表最终归属或安全删除。记录名称为历史记录值。</p></header><section><h2>全部匹配事件</h2><div class=\"scroll\"><table id=\"matched-events\"><thead><tr><th>事件 ID</th><th>时间</th><th>精确 FILETIME</th><th>操作</th><th>记录名称 / 资源</th><th>置信标签</th></tr></thead><tbody>\n").ConfigureAwait(false);
        long count = 0;
        foreach (var item in data.GetProperty("events").EnumerateArray())
        {
            await Write("<tr data-event-id=\"").ConfigureAwait(false);
            await Encoded(item.GetProperty("id").GetString()!).ConfigureAwait(false);
            await Write("\">").ConfigureAwait(false);
            foreach (var field in new[] { "id", "timestamp", "timestamp_ticks", "operation", "resource", "confidence" })
            {
                await Write("<td>").ConfigureAwait(false);
                await Encoded(T(item, field)).ConfigureAwait(false);
                await Write("</td>").ConfigureAwait(false);
            }
            await Write("</tr>\n").ConfigureAwait(false);
            count++;
        }
        await Write("</tbody></table></div></section></main></body></html>\n").ConfigureAwait(false);
        return count;
    }
}
