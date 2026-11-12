using System.Text.Json;
using System.Text.Json.Nodes;
using Contain.Viewer;

// Pure projections of synthetic saved summaries. These tests use the production
// presentation code; they do not claim to validate WinForms layout or live ETW.
using var diff = JsonDocument.Parse("{\"files\":[],\"registry\":[],\"inventory\":[]}");
var summary = JsonNode.Parse("""
{
  "capture_state":"finished", "exit_code":0,
  "quality":{"level":"Incomplete"},
  "summary_counts":{"application_records_dropped":57767,"decode_errors":0},
  "backend":{
    "etw_events_lost":0,"etw_buffers_lost":0,"context_losses":0,
    "pipeline":{"queue_overflow":17,"enqueue_disconnected":0,"queue_pending":0,"retention_dropped":0,
      "deliberately_filtered":100,"unsupported_records":3},
    "handoff":{"overflow":57750,"oversized":0,"disconnected":0,"serialize_failed":0,"pending":0},
    "stream":{"quota_dropped":0,"failed":0},
    "follow_installer":{"candidate_dropped":0,"excluded_other":900,"excluded_self_io":50,"unresolved":0}
  }
}
""")!.AsObject();
var checks = 0;
void Require(bool value, string message)
{
    if (!value) throw new InvalidDataException(message);
    checks++;
}
OverviewPresentation Model(JsonNode node)
{
    using var document = JsonDocument.Parse(node.ToJsonString());
    return OverviewPresentation.Build(document.RootElement, diff.RootElement);
}
string Details(JsonNode node)
{
    using var document = JsonDocument.Parse(node.ToJsonString());
    return OverviewPresentation.LossDetails(document.RootElement);
}

var saved = Model(summary);
Require(!saved.IsUnfinished && saved.Record == "会话已结束", "saved loss does not change completion state");
Require(saved.Alert.Contains("记录已保存；采集期间丢失了部分记录。", StringComparison.Ordinal), "saved loss is explicit");
Require(!saved.Alert.Contains("未确认完整结束", StringComparison.Ordinal), "finished loss is not unfinished");
Require(saved.Installer == "已记录退出码 0", "loss does not rewrite installer result");
var details = Details(summary);
Require(details.Contains("接收队列：溢出 17", StringComparison.Ordinal), "receiver loss is separate");
Require(details.Contains("线程间交付：溢出 57750", StringComparison.Ordinal), "handoff loss is separate");
Require(details.Contains("原始记录保存：配额丢弃 0 · 保存失败 0", StringComparison.Ordinal), "measured raw persistence zero is shown");
Require(details.Contains("跟随候选丢失：0 · 上下文丢失总数：0", StringComparison.Ordinal), "candidate and context counters retain separate meanings");
Require(details.Contains("正常范围筛除（不计丢失）：主动筛除 100", StringComparison.Ordinal), "scope filtering has its own label");

var unfinished = summary.DeepClone().AsObject(); unfinished["capture_state"] = "finalizing";
var partial = Model(unfinished);
Require(partial.IsUnfinished && partial.Alert.StartsWith("记录未确认完整结束", StringComparison.Ordinal), "unfinished warning survives additional loss alerts");
Require(!partial.Alert.Contains("记录已保存；采集期间", StringComparison.Ordinal), "unfinished session cannot receive completed notice");

var historical = summary.DeepClone().AsObject(); historical["backend"] = new JsonObject();
var historicalDetails = Details(historical);
Require(Model(historical).Alert.Contains("记录已保存；采集期间丢失了部分记录。", StringComparison.Ordinal), "historical total is enough to report loss");
Require(historicalDetails.Contains("接收队列：溢出 未提供", StringComparison.Ordinal)
    && historicalDetails.Contains("线程间交付：溢出 未提供", StringComparison.Ordinal)
    && historicalDetails.Contains("原始记录保存：配额丢弃 未提供", StringComparison.Ordinal), "missing historical breakdown is never measured zero");

var filtered = summary.DeepClone().AsObject();
filtered["summary_counts"]!["application_records_dropped"] = 0;
filtered["backend"]!["pipeline"]!["queue_overflow"] = 0;
filtered["backend"]!["handoff"]!["overflow"] = 0;
Require(!Model(filtered).Alert.Contains("丢失了部分记录", StringComparison.Ordinal), "normal scope filtering cannot manufacture loss");
filtered["backend"]!["follow_installer"]!["candidate_dropped"] = 12;
Require(Model(filtered).Alert.Contains("记录已保存；采集期间丢失了部分记录。", StringComparison.Ordinal)
    && Details(filtered).Contains("跟随候选丢失：12", StringComparison.Ordinal), "candidate loss remains visible independently of raw loss total");
// Production readback of legacy metadata can supply a default scalar zero.
historical["backend"] = new JsonObject { ["context_losses"] = 0 };
Require(Details(historical).Contains("上下文丢失总数：未提供", StringComparison.Ordinal), "legacy default zero is not a measured context counter");
historical["backend"]!["context_losses"] = 3;
Require(Details(historical).Contains("上下文丢失总数：3", StringComparison.Ordinal), "positive legacy context loss remains visible");

var etwLoss = filtered.DeepClone().AsObject();
etwLoss["backend"]!["follow_installer"]!["candidate_dropped"] = 0;
etwLoss["backend"]!["etw_events_lost"] = 192055;
etwLoss["summary_counts"]!["drain_timed_out"] = true;
var etwOutcome = Model(etwLoss);
Require(etwOutcome.Alert.Contains("记录已保存，但采集期间丢失部分 ETW 事件，且收尾等待超时。", StringComparison.Ordinal), "ETW source loss and drain deadline are explicit independent gaps");
Require(etwOutcome.Alert.Contains("ETW 丢失事件 192055", StringComparison.Ordinal) && etwOutcome.Coverage.Contains("Incomplete", StringComparison.Ordinal), "exact ETW counter and incomplete level retained");
etwLoss["backend"]!["etw_events_lost"] = 0;
var timeoutOnly = Model(etwLoss);
Require(timeoutOnly.Alert.Contains("收尾等待超时，后续操作可能未被记录", StringComparison.Ordinal)
    && !timeoutOnly.Alert.Contains("丢失部分 ETW", StringComparison.Ordinal), "deadline alone does not manufacture ETW loss");
etwLoss["summary_counts"]!.AsObject().Remove("drain_timed_out");
Require(!Model(etwLoss).Alert.Contains("超时", StringComparison.Ordinal), "missing deadline measurement is not true");

Console.WriteLine($"Saved loss presentation: {checks} passed, 0 failed (synthetic projection only).");
ReadyDiagnosticsChecks.Run();
RequiredLookupDiagnosticsChecks.Run();
