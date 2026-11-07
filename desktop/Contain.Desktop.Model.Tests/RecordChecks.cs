using System.Text.Json;
using Contain.Desktop.Model;
using Contain.Viewer;

namespace Contain.Desktop.Model.Tests;

/// <summary>Controlled completions exercise the production view model, including services that ignore cancellation.</summary>
public static class RecordChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();
        async Task Check(string name, Func<Task> test) { await test().WaitAsync(TimeSpan.FromSeconds(10)); passed.Add(name); }

        await Check("empty personal library performs no CLI read or creation", async () =>
        {
            var service = new FakeService();
            using var vm = New(service, true);
            Require(await vm.LoadPersonalAsync() && vm.IsEmpty && service.ListCalls == 0 && !vm.HasRecord, "empty personal state");
        });

        await Check("same-name same-time rows have distinct visible labels and exact identities", async () =>
        {
            using var vm = await Ready(new FakeService());
            Require(vm.Sessions.Select(row => row.Name).Distinct().Count() == 1
                && vm.Sessions.Select(row => row.DisplayTime).Distinct().Count() == 1
                && vm.Sessions.All(row => row.Disambiguation.Contains("同名记录", StringComparison.Ordinal))
                && vm.Sessions.Select(row => row.Disambiguation).Distinct().Count() == 2
                && vm.Sessions.Select(row => row.Identity).SequenceEqual(new[] { "session:A", "session:B" }), "same-name distinguishability");
        });

        await Check("same-name late summary cannot replace exact newer session", async () =>
        {
            var gate = NewGate<CoreResponse>();
            var service = new FakeService { SummaryOverride = (id, _) => id == "A" ? gate.Task : Task.FromResult(Summary(id)) };
            using var vm = await Ready(service);
            var old = vm.OpenSessionAsync("A");
            Require(vm.IsBusy && !vm.HasRecord, "old data clears before await");
            Require(await vm.OpenSessionAsync("B"), "new session loaded");
            gate.SetResult(Summary("A"));
            Require(!await old && vm.ActiveRecordId == "B" && vm.SessionIdentity == "session:B" && vm.Events.All(row => row.Id.StartsWith("B-", StringComparison.Ordinal)), "late results isolated");
        });

        await Check("cancelled query late error cannot replace newer success", async () =>
        {
            var gate = NewGate<CoreResponse>();
            var service = new FakeService { SummaryOverride = (id, _) => id == "A" ? gate.Task : Task.FromResult(Summary(id)) };
            using var vm = await Ready(service);
            var old = vm.OpenSessionAsync("A");
            await vm.OpenSessionAsync("B");
            gate.SetException(new CoreClientException("cli_exit", "old read failed"));
            Require(!await old && vm.ActiveRecordId == "B" && vm.ErrorMessage.Length == 0 && !vm.IsBusy, "stale error suppressed");
        });

        await Check("late search cannot mix events into another same-name session", async () =>
        {
            var gate = NewGate<CoreResponse>();
            var service = new FakeService();
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A");
            service.HistoryOverride = (id, limit, offset, query, _) => query == "old" ? gate.Task : Task.FromResult(History(id, limit, offset, query));
            var old = vm.SearchAsync("old");
            Require(vm.Events.Count == 0 && vm.SelectedEventId is null && !vm.CanExportMatches, "old page unavailable while querying");
            await vm.OpenSessionAsync("B");
            gate.SetResult(History("A", 50, 0, "old"));
            Require(!await old && vm.ActiveRecordId == "B" && vm.SearchText.Length == 0 && vm.Events[0].Id == "B-000", "stale search isolated");
        });

        await Check("editing search draft invalidates pending results before explicit search", async () =>
        {
            var gate = NewGate<CoreResponse>();
            var service = new FakeService();
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A");
            await vm.SelectEventAsync(vm.Events[0]);
            service.HistoryOverride = (id, limit, offset, query, _) => query == "old" ? gate.Task : Task.FromResult(History(id, limit, offset, query));
            var old = vm.SearchAsync("old");
            var queries = service.HistoryCalls;
            vm.SearchDraft = "new draft";
            gate.SetResult(History("A", 50, 0, "old"));
            Require(!await old && vm.SearchDraft == "new draft" && vm.Events.Count == 0 && vm.SelectedEventId is null
                && !vm.IsBusy && !vm.CanExportMatches && !vm.CanExportPage && service.HistoryCalls == queries, "draft edit cancels without launching another query");
            Require(await vm.SearchAsync(vm.SearchDraft) && vm.SearchText == "new draft" && vm.SearchDraft == "new draft" && vm.Events.Count == 50, "explicit search commits matching draft");
        });

        await Check("late explanation cannot change copy identity", async () =>
        {
            var gate = NewGate<CoreResponse>();
            var service = new FakeService { ExplainOverride = (id, eventId, _) => eventId.EndsWith("000", StringComparison.Ordinal) ? gate.Task : Task.FromResult(Explain(id, eventId)) };
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A");
            var first = vm.Events[0]; var second = vm.Events[1];
            var old = vm.SelectEventAsync(first);
            await vm.SelectEventAsync(second);
            gate.SetResult(Explain("A", first.Id));
            Require(!await old && vm.SelectedEventId == second.Id && vm.EventIdentity == "event:" + second.Id && vm.EventExplanation.Contains(second.Id, StringComparison.Ordinal), "selected explanation remains exact");
        });

        await Check("mismatched event payload refuses explain and copy", async () =>
        {
            var service = new FakeService { ExplainOverride = (id, eventId, _) => Task.FromResult(Explain(id, eventId, "changed resource")) };
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A");
            Require(!await vm.SelectEventAsync(vm.Events[0]) && vm.ErrorKind == nameof(InvalidDataException) && vm.SelectedEventId is null && !vm.CanCopyEvent, "payload mismatch is failure");
        });

        await Check("130 event and 53 match pagination uses exact ordered IDs", async () =>
        {
            var service = new FakeService();
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A");
            Require(vm.Events.Count == 50 && vm.CanNextEvents && !vm.CanPreviousEvents, "initial page");
            await vm.SearchAsync("match");
            var first = vm.Events.Select(row => row.Id).ToArray();
            Require(vm.MatchedCount == 53 && first.Length == 50 && vm.CanExportMatches, "first matched page");
            await vm.NextEventsAsync();
            var all = first.Concat(vm.Events.Select(row => row.Id)).ToArray();
            Require(all.SequenceEqual(Enumerable.Range(0, 53).Select(index => $"A-{index:D3}")) && vm.Events.Count == 3 && !vm.CanNextEvents && vm.CanPreviousEvents, "all matches exact once in order");
            await vm.ClearSearchAsync();
            Require(vm.EventOffset == 0 && vm.SearchText.Length == 0 && vm.MatchedCount is null && vm.Events.Count == 50 && vm.CanExportPage, "clear returns ordinary page one");
            await vm.NextEventsAsync(); await vm.NextEventsAsync();
            Require(vm.Events.Count == 30 && vm.Events[0].Id == "A-100" && !vm.CanNextEvents, "ordinary final page");
        });

        await Check("zero search results preserve measured zero and disable match export", async () =>
        {
            using var vm = await Ready(new FakeService());
            await vm.OpenSessionAsync("A"); await vm.SearchAsync("none");
            Require(vm.MatchedCount == 0 && vm.Events.Count == 0 && vm.EventPageText.Contains("共 0 条", StringComparison.Ordinal) && !vm.CanExportMatches, "zero is available measurement");
        });

        await Check("export snapshot survives search and session changes inside save dialog", async () =>
        {
            var service = new FakeService();
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A"); await vm.SearchAsync("match"); await vm.NextEventsAsync();
            var context = vm.CaptureExportContext(true)!;
            await vm.OpenSessionAsync("B"); await vm.SearchAsync("none");
            var currentMessage = vm.StatusMessage;
            Require(await vm.ExportAsync(context, Path.GetFullPath("unused-report.html")), "captured export succeeds");
            Require(service.LastExport is { SessionId: "A", Search: "match", MatchCount: 53, Offset: 0 } && vm.ActiveRecordId == "B" && vm.SearchText == "none" && vm.StatusMessage == currentMessage, "old export never mixes new view or overwrites new status");
        });

        await Check("page export freezes selected event and offset", async () =>
        {
            var service = new FakeService();
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A"); await vm.NextEventsAsync(); await vm.SelectEventAsync(vm.Events[3]);
            var context = vm.CaptureExportContext(false)!;
            await vm.OpenSessionAsync("B");
            Require(await vm.ExportAsync(context, Path.GetFullPath("unused-page.html")), "page export");
            Require(service.LastExport is { SessionId: "A", SelectedEventId: "A-053", Offset: 50, Search: null }, "frozen page identity");
        });

        await Check("export cancellation preserves selection and does not become error", async () =>
        {
            var entered = NewGate<bool>();
            var service = new FakeService { ExportOverride = async token => { entered.SetResult(true); await Task.Delay(Timeout.Infinite, token); return 53; } };
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A"); await vm.SearchAsync("match"); await vm.NextEventsAsync(); await vm.SelectEventAsync(vm.Events[1]);
            var exporting = vm.ExportMatchesAsync(Path.GetFullPath("unused-cancel.html"));
            await entered.Task;
            Require(vm.IsExporting && !vm.CanExportMatches, "concurrent export disabled");
            vm.CancelExport();
            Require(!await exporting && !vm.IsExporting && vm.SelectedEventId == "A-051" && vm.EventOffset == 50 && vm.SearchText == "match" && vm.ErrorMessage.Length == 0, "cancel preserves view");
        });

        await Check("export existing target and IO errors keep view and error kind", async () =>
        {
            var service = new FakeService { ExportOverride = _ => Task.FromException<long>(new CoreClientException("export_exists", "target exists")) };
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A"); await vm.SearchAsync("match"); await vm.SelectEventAsync(vm.Events[2]);
            Require(!await vm.ExportMatchesAsync(Path.GetFullPath("unused-existing.html")) && vm.ErrorKind == "export_exists" && vm.SelectedEventId == "A-002" && vm.CanExportMatches, "existing target failure retained");
            service.ExportOverride = _ => Task.FromException<long>(new IOException("write failed"));
            Require(!await vm.ExportMatchesAsync(Path.GetFullPath("unused-io.html")) && vm.ErrorKind == nameof(IOException) && vm.Events.Count == 50, "IO failure retained");
        });

        await Check("changed search snapshot invalidates only current export authorization", async () =>
        {
            var service = new FakeService { ExportOverride = _ => Task.FromException<long>(new CoreClientException("export_record_changed", "changed")) };
            using var vm = await Ready(service);
            await vm.OpenSessionAsync("A"); await vm.SearchAsync("match");
            Require(!await vm.ExportMatchesAsync(Path.GetFullPath("unused-changed.html")) && !vm.CanExportMatches && vm.SearchText == "match" && vm.Events.Count == 50, "requires refresh without losing page");
        });

        await Check("accurate captured session cannot fall back to same name", async () =>
        {
            using var vm = New(new FakeService());
            try { await vm.OpenCapturedSessionAsync(Path.GetFullPath("isolated.db"), "missing-uuid"); throw new Exception("missing exact session accepted"); }
            catch (InvalidDataException) { Require(!vm.HasRecord && vm.Sessions.Count == 2 && vm.ErrorMessage.Contains("准确会话", StringComparison.Ordinal), "same-name records remain unselected"); }
        });

        await Check("wrong session summary and CLI failure are not successful empty pages", async () =>
        {
            var service = new FakeService { SummaryOverride = (_, _) => Task.FromResult(Summary("B")) };
            using var vm = await Ready(service);
            Require(!await vm.OpenSessionAsync("A") && !vm.HasRecord && vm.ErrorKind == nameof(InvalidDataException), "wrong UUID rejected");
            service.SummaryOverride = (_, _) => Task.FromException<CoreResponse>(new CoreClientException("cli_exit", "collector query exited", 7));
            Require(!await vm.OpenSessionAsync("A") && vm.ErrorKind == "cli_exit" && vm.LastError is CoreClientException { ExitCode: 7 }, "original failure retained");
        });

        await Check("finished Incomplete keeps saved loss message and missing counts", async () =>
        {
            var service = new FakeService { SummaryOverride = (id, _) => Task.FromResult(Summary(id, "finished", 9, "Incomplete", true)) };
            using var vm = await Ready(service); await vm.OpenSessionAsync("A");
            Require(vm.OverviewRecord == "会话已结束" && vm.OverviewInstaller.Contains("9", StringComparison.Ordinal)
                && vm.OverviewInstallerNote.Contains("非零", StringComparison.Ordinal) && vm.OverviewAlert.Contains("记录已保存；采集期间丢失了部分记录", StringComparison.Ordinal)
                && vm.TechnicalDetails.Contains("溢出 未提供", StringComparison.Ordinal), "separate saved exit coverage and missing counters");
            const string rawError = "Raw journal writer failed with retained diagnostic details.";
            service.SummaryOverride = (id, _) => Task.FromResult(Summary(id, "finished", 9, "Incomplete", true, diagnostics: [rawError], streamError: rawError));
            await vm.OpenSessionAsync("A");
            Require(vm.OverviewAlert.Contains("记录来源报告错误", StringComparison.Ordinal)
                && vm.OverviewAlert.Contains("丢弃记录 3", StringComparison.Ordinal)
                && vm.OverviewCoverage.Contains("Incomplete", StringComparison.Ordinal)
                && !vm.OverviewAlert.Contains(rawError, StringComparison.Ordinal)
                && vm.TechnicalDetails.Contains(rawError, StringComparison.Ordinal), "source error and numeric loss remain visible with full raw diagnostic in technical details");
            service.SummaryOverride = (id, _) => Task.FromResult(Summary(id, "finished", 9, "Incomplete", true, diagnostics: [rawError]));
            await vm.OpenSessionAsync("A");
            Require(vm.OverviewAlert.Contains("限制或异常", StringComparison.Ordinal)
                && vm.OverviewAlert.Contains("记录已保存；采集期间丢失了部分记录", StringComparison.Ordinal)
                && vm.TechnicalDetails.Contains(rawError, StringComparison.Ordinal), "unknown diagnostic is summarized without concealing loss or its raw cause");
        });

        await Check("unfinished missing exit and no-baseline follow state stay distinct", async () =>
        {
            var service = new FakeService { SummaryOverride = (id, _) => Task.FromResult(Summary(id, "finalizing", null, "Degraded", false, true)) };
            using var vm = await Ready(service); await vm.OpenSessionAsync("A");
            Require(vm.OverviewInstaller == "退出结果未记录" && vm.OverviewRecord == "会话未完成" && vm.FollowInstaller
                && vm.OverviewChanges.Contains("未采集安装前基线", StringComparison.Ordinal) && !vm.OverviewChanges.Contains("文件 0", StringComparison.Ordinal), "missing values and no baseline are not zero");
            // This is the fixed core warning visible in the M10 native overview;
            // it describes an evidence boundary, not a newly observed loss.
            const string readBoundary = "File observations use bounded temporary read leases where available; they describe observed hashes and identities, not complete mutation coverage or ownership. Final file and registry state attribution remains Unknown where mutation coverage or read/clock proof is incomplete; verified source actors remain separate.";
            service.SummaryOverride = (id, _) => Task.FromResult(Summary(id, "finished", 0, "Degraded", false, true, [readBoundary]));
            await vm.OpenSessionAsync("A");
            Require(vm.OverviewAlert.Contains("观测到的内容与文件身份", StringComparison.Ordinal)
                && vm.OverviewAlert.Contains("Unknown", StringComparison.Ordinal)
                && !vm.OverviewAlert.Contains("File observations", StringComparison.Ordinal)
                && !vm.OverviewAlert.Contains("丢失了部分记录", StringComparison.Ordinal)
                && vm.OverviewCoverage.Contains("Degraded", StringComparison.Ordinal)
                && vm.TechnicalDetails.Contains(readBoundary, StringComparison.Ordinal), "native read-boundary warning is concise Chinese without losing original evidence or manufacturing loss");
        });

        await Check("external library remains distinct from personal destination", async () =>
        {
            var service = new FakeService();
            using var vm = New(service);
            await vm.OpenDatabaseAsync(Path.GetFullPath("external-read-only.db"));
            Require(!vm.IsPersonalLibrary && vm.SourceTitle.Contains("只读", StringComparison.Ordinal), "external source clear");
            await vm.LoadPersonalAsync();
            Require(vm.IsPersonalLibrary && vm.DatabasePath == Path.GetFullPath("personal.db") && service.ListCalls == 2, "returns original personal library");
        });

        await Check("disposed model rejects late query and leaves no active selection", async () =>
        {
            var gate = NewGate<CoreResponse>();
            var service = new FakeService { SummaryOverride = (_, _) => gate.Task };
            var vm = await Ready(service);
            var query = vm.OpenSessionAsync("A"); vm.Dispose(); gate.SetResult(Summary("A"));
            Require(!await query && !vm.HasRecord && !await vm.OpenSessionAsync("B"), "disposal does not publish late result");
        });

        await Check("close cancels pending query and export then awaits both cleanup paths", async () =>
        {
            var readEntered = NewGate<bool>(); var exportEntered = NewGate<bool>();
            var readCleanupStarted = NewGate<bool>(); var exportCleanupStarted = NewGate<bool>();
            var releaseReadCleanup = NewGate<bool>(); var releaseExportCleanup = NewGate<bool>();
            var service = new FakeService
            {
                ExportOverride = async token =>
                {
                    exportEntered.SetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { exportCleanupStarted.SetResult(true); await releaseExportCleanup.Task; }
                    return 53;
                },
                HistoryOverride = async (id, limit, offset, query, token) =>
                {
                    if (query != "pending") return History(id, limit, offset, query);
                    readEntered.SetResult(true);
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { readCleanupStarted.SetResult(true); await releaseReadCleanup.Task; }
                    return History(id, limit, offset, query);
                }
            };
            var vm = await Ready(service);
            await vm.OpenSessionAsync("A"); await vm.SearchAsync("match");
            var export = vm.ExportMatchesAsync(Path.GetFullPath("unused-closing.html")); await exportEntered.Task;
            var query = vm.SearchAsync("pending"); await readEntered.Task;
            var closing = vm.CloseAsync();
            await Task.WhenAll(readCleanupStarted.Task, exportCleanupStarted.Task);
            Require(!closing.IsCompleted, "cancellation alone does not finish close");
            releaseExportCleanup.SetResult(true);
            Require(!await export && !closing.IsCompleted, "read cleanup still awaited after exporter finishes");
            releaseReadCleanup.SetResult(true);
            Require(!await query, "cancelled read fails instead of committing");
            await closing;
            Require(!vm.IsExporting && !await vm.LoadPersonalAsync(), "closed model owns no later query");
        });

        await Check("close also awaits superseded query that ignores cancellation", async () =>
        {
            var oldResult = NewGate<CoreResponse>();
            var service = new FakeService { SummaryOverride = (id, _) => id == "A" ? oldResult.Task : Task.FromResult(Summary(id)) };
            var vm = await Ready(service);
            var old = vm.OpenSessionAsync("A");
            await vm.OpenSessionAsync("B");
            var closing = vm.CloseAsync();
            Require(!closing.IsCompleted, "superseded query remains owned until cleanup");
            oldResult.SetResult(Summary("A"));
            Require(!await old, "late superseded result rejected");
            await closing;
            Require(vm.ActiveRecordId == "B", "close cannot publish old session");
        });
        return passed;
    }

    private static RecordsViewModel New(FakeService service, bool missing = false) => new(service, Path.GetFullPath("personal.db"), () => missing);
    private static async Task<RecordsViewModel> Ready(FakeService service) { var vm = New(service); Require(await vm.LoadPersonalAsync(), "load fixture list"); return vm; }
    private static TaskCompletionSource<T> NewGate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CoreResponse Response(string kind, object data) => CoreResponse.Parse(JsonSerializer.Serialize(new { schema_version = 3, kind, data }), kind);
    private static CoreResponse Summary(string id, string state = "finished", int? exit = 0, string quality = "Degraded", bool loss = false, bool follow = false,
        string[]? diagnostics = null, string? streamError = null) => Response("inspect_summary", new
    {
        id, name = "same name", started_at = "2026-11-07T01:00:00+00:00", finished_at = state == "finished" ? "2026-11-07T01:01:00+00:00" : null,
        capture_state = state, exit_code = exit, events = Array.Empty<object>(), watch_roots = Array.Empty<string>(), files = Array.Empty<object>(),
        registry = Array.Empty<object>(), inventory = Array.Empty<object>(), processes = Array.Empty<object>(),
        backend = new { follow_installer = follow ? new { direct_children_only = true, baseline_available = false } : null, stream = new { error = streamError } },
        warnings = diagnostics ?? Array.Empty<string>(),
        quality = new { level = quality, reasons = Array.Empty<string>() }, summary_counts = new { application_records_dropped = loss ? (long?)3 : null }
    });
    private static Dictionary<string, object?> Event(string id, string? resource = null) => new()
    {
        ["id"] = id, ["timestamp"] = "2026-11-07T01:00:00+00:00", ["timestamp_ticks"] = "134385000000000001",
        ["operation"] = "write_requested", ["resource"] = resource ?? "sample/" + id + ".txt", ["confidence"] = "Unknown", ["success"] = null,
        ["event_type"] = "file", ["reason"] = "synthetic insufficient actor evidence"
    };
    private static CoreResponse History(string id, int limit, long offset, string? query)
    {
        var total = query == "none" ? 0 : query is null ? 130 : 53;
        var data = new Dictionary<string, object?>
        {
            ["app_id"] = id, ["events"] = Enumerable.Range(0, total).Skip(checked((int)offset)).Take(limit).Select(index => Event($"{id}-{index:D3}")).ToArray()
        };
        if (query is not null)
        {
            data["search"] = query; data["matched_total"] = total; data["search_fingerprint"] = new string('a', 64);
            data["offset"] = offset; data["limit"] = limit; data["name"] = "same name";
        }
        return Response("history", data);
    }
    private static CoreResponse Explain(string id, string eventId, string? resource = null) => Response("explain", new
    {
        app_id = id, @event = Event(eventId, resource), edges = Array.Empty<object>(), operations = Array.Empty<object>()
    });

    private sealed class FakeService : IRecordService
    {
        internal int ListCalls;
        internal int HistoryCalls;
        internal Func<string, CancellationToken, Task<CoreResponse>>? SummaryOverride;
        internal Func<string, int, long, string?, CancellationToken, Task<CoreResponse>>? HistoryOverride;
        internal Func<string, string, CancellationToken, Task<CoreResponse>>? ExplainOverride;
        internal Func<CancellationToken, Task<long>>? ExportOverride;
        internal RecordExportContext? LastExport;
        public Task<CoreResponse> ListAsync(string database, CancellationToken token)
        {
            ++ListCalls;
            return Task.FromResult(Response("list_details", new[] { "A", "B" }.Select(id => new { id, name = "same name", started_at = "2026-11-07T01:00:00+00:00", capture_state = "finished" })));
        }
        public Task<CoreResponse> SummaryAsync(string database, string id, CancellationToken token) => SummaryOverride?.Invoke(id, token) ?? Task.FromResult(Summary(id));
        public Task<CoreResponse> DiffAsync(string database, string id, CancellationToken token) => Task.FromResult(Response("diff", new { app_id = id, files = Array.Empty<object>(), registry = Array.Empty<object>(), inventory = Array.Empty<object>() }));
        public Task<CoreResponse> HistoryAsync(string database, string id, int limit, long offset, string? query, CancellationToken token)
        {
            ++HistoryCalls;
            return HistoryOverride?.Invoke(id, limit, offset, query, token) ?? Task.FromResult(History(id, limit, offset, query));
        }
        public Task<CoreResponse> ExplainAsync(string database, string id, string eventId, CancellationToken token) => ExplainOverride?.Invoke(id, eventId, token) ?? Task.FromResult(Explain(id, eventId));
        public Task ExportPageAsync(string database, string id, string? eventId, int pageSize, long offset, string target, CancellationToken token)
        {
            LastExport = new(database, id, eventId, offset, null, null, null, 0);
            return Task.CompletedTask;
        }
        public Task<long> ExportMatchesAsync(string database, string id, string query, string fingerprint, long count, string target, CancellationToken token)
        {
            LastExport = new(database, id, null, 0, query, fingerprint, count, 0);
            return ExportOverride?.Invoke(token) ?? Task.FromResult(count);
        }
    }
}
