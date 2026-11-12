using System.Text;
using System.Text.Json;
using Contain.Viewer;

namespace Contain.Desktop.Model;

internal static class RecordPresentation
{
    internal static string OverviewAlert(string alert) => string.Join("\r\n", alert.Split("\r\n", StringSplitOptions.None).Select(line =>
    {
        // Keep the shared projection's saved/lost, source-error, missing-data
        // and numeric-gap statements verbatim. Only its raw diagnostic preview
        // is summarized here; Technical retains every original diagnostic.
        const string diagnostic = "记录限制：";
        if (!line.StartsWith(diagnostic, StringComparison.Ordinal)) return line;
        return line.StartsWith(diagnostic + "File observations use bounded temporary read leases", StringComparison.Ordinal)
            ? "读取结果只说明观测到的内容与文件身份，不能证明完整变化或最终归属；证据不足时状态关联仍为 Unknown。"
            : "记录报告了限制或异常；请在技术详情中查看完整原始原因。";
    }));

    internal static string Technical(JsonElement data, string database)
    {
        var backend = JsonView.Field(data, "backend");
        var counts = JsonView.Field(data, "summary_counts");
        var quality = JsonView.Field(data, "quality");
        var text = new StringBuilder()
            .AppendLine(ProductVersion.Display)
            .AppendLine("会话 ID：" + RecordText.Value(data, "id"))
            .AppendLine("数据库：" + database)
            .AppendLine("安装程序：" + RecordText.Value(data, "installer"))
            .AppendLine("开始：" + RecordText.Value(data, "started_at"))
            .AppendLine("记录结束时间：" + RecordText.Value(data, "finished_at"))
            .AppendLine("持久化状态：" + RecordText.Value(data, "capture_state") + "（不表示采集进程现在仍在运行）")
            .AppendLine("覆盖质量：" + RecordText.Value(quality, "level"))
            .AppendLine("实际来源（active 不证明覆盖完整）")
            .AppendLine($"ETW 进程：{RecordText.Value(backend, "etw_process")} · 文件：{RecordText.Value(backend, "etw_file")} · 注册表：{RecordText.Value(backend, "etw_registry")}")
            .AppendLine($"时间线观测：{RecordText.Value(counts, "events_retained")} · 规范化源操作：{RecordText.Value(counts, "events_normalized")}")
            .AppendLine($"源进程已知：{RecordText.Value(counts, "known_actor_events")} · 未知：{RecordText.Value(counts, "unknown_actor_events")}")
            .AppendLine($"ETW 丢失事件：{RecordText.Value(backend, "etw_events_lost")} · 丢失缓冲区：{RecordText.Value(backend, "etw_buffers_lost")}")
            .AppendLine($"应用丢弃：{RecordText.Value(counts, "application_records_dropped")} · 解码错误：{RecordText.Value(counts, "decode_errors")}")
            .AppendLine($"快照缺口：{RecordText.Value(counts, "snapshot_gaps")} · 通知缺口：{RecordText.Value(counts, "notification_gaps")} · 排空超时：{RecordText.Value(counts, "drain_timed_out")}")
            .AppendLine(OverviewPresentation.LossDetails(data));
        foreach (var reason in OverviewPresentation.Diagnostics(data)) text.AppendLine(reason);
        text.AppendLine().AppendLine("进程证据：PID 与创建时间共同标识进程实例。");
        var processes = JsonView.Items(JsonView.Field(data, "processes")).ToArray();
        foreach (var process in processes.Take(20))
            text.AppendLine($"{RecordText.Value(process, "image")} · PID {RecordText.Value(process, "pid")} · 创建时间 {RecordText.Value(process, "creation_time")} · {RecordText.Value(process, "confidence")}");
        if (processes.Length > 20) text.AppendLine($"另有 {processes.Length - 20} 条进程记录，完整原始概览保留在本页报告中。");
        text.AppendLine("会话结束不等于覆盖完整。历史名称不等于当前路径；High 不证明最终所有权或安全删除。");
        return text.ToString();
    }

    internal static string Explain(JsonElement data, bool follow)
    {
        var item = JsonView.Field(data, "event");
        var evidence = JsonView.Field(item, "evidence");
        var fileName = JsonView.Field(JsonView.Field(item, "raw"), "file_name");
        var operation = RecordText.Value(item, "operation");
        var confidence = RecordText.Value(item, "confidence");
        var success = JsonView.Field(item, "success");
        var processEvent = RecordText.Value(item, "event_type") == "process" || operation is "process_start" or "process_stop" or "process_end";
        var text = new StringBuilder()
            .AppendLine(RecordText.Operation(operation))
            .AppendLine(confidence == "High" ? "有依据的执行进程关联；不等于最终文件所有权。" : confidence == "Unknown"
                ? "当前证据不足以确定关联；Unknown 不表示危险。" : "保留原始置信标签：" + confidence)
            .AppendLine(processEvent ? "这是进程生命周期观测，不是文件写入请求，也不证明该进程写入了文件。"
                : success.ValueKind == JsonValueKind.True ? "请求完成结果：成功。请求成功不自动证明文件前后状态。"
                : success.ValueKind == JsonValueKind.False ? "请求完成结果：未成功。" : "请求完成结果：未提供 / 尚无可靠完成依据。")
            .AppendLine((processEvent ? "进程资源：" : "资源：") + RecordText.Value(item, "resource"))
            .AppendLine("记录中的进程映像：" + RecordText.Value(evidence, "process_image"))
            .AppendLine($"PID：{RecordText.Value(evidence, "pid")} · 创建时间（精确 FILETIME）：{RecordText.Value(evidence, "process_creation_time")}")
            .AppendLine("依据 / 未知原因：" + RecordText.Value(item, "reason"))
            .AppendLine("事件 ID：" + RecordText.Value(item, "id"))
            .AppendLine($"原始操作：{operation} · 来源：{RecordText.Value(evidence, "source")}")
            .AppendLine("观测时间：" + RecordText.Value(item, "timestamp"));
        if (!processEvent)
            text.AppendLine("名称来源：" + RecordText.Value(fileName, "path_source"))
                .AppendLine(RecordText.Value(fileName, "path_source") == "file_object_cache"
                    ? "历史缓存名称不是已验证的当前路径。" : "保留所记录名称，不能推导现在的路径或最终归属。")
                .AppendLine("名称观察时间（精确 FILETIME）：" + RecordText.Value(fileName, "observed_ticks"));
        foreach (var edge in JsonView.Items(JsonView.Field(data, "edges")))
            text.AppendLine($"{RecordText.Value(edge, "relation")} [{RecordText.Value(edge, "confidence")}]：{RecordText.Value(edge, "reason")}");
        if (follow && !processEvent) text.AppendLine("本次跟随记录没有安装前文件基线，不能据此断言文件新增。");
        text.AppendLine(processEvent ? "完整进程原始字段与关联规则可展开原始 JSON 查看。进程启动不证明文件写入。"
            : "完整请求完成依据、原始字段与关联规则可展开原始 JSON 查看。High 不是删除许可。");
        return text.ToString();
    }
}
