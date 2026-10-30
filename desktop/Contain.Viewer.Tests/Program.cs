using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // This executable is only a negative version fixture when the real bridge
        // probes --version; it never supplies successful query data.
        if (args.SequenceEqual(new[] { "--version" })) { Console.WriteLine("contain 99.0.0"); return 0; }
        ApplicationConfiguration.Initialize();
        var options = Options.Parse(args);
        var runner = new Runner(options);
        using var form = new ViewerForm(runner.Client, runner.Dialogs);
        form.Shown += async (_, _) =>
        {
            try { await runner.RunAsync(form); }
            catch (Exception error) { runner.RecordUnhandled(error); }
            finally { runner.Finish(); form.Close(); }
        };
        Application.Run(form);
        return runner.Success ? 0 : 1;
    }
}

internal sealed record Options(string Package, string Fixtures, string Output, string PrivateOutput, string Source)
{
    public static Options Parse(string[] args)
    {
        if (args.Length != 10) throw new ArgumentException("Expected --package --fixtures --output --private-output --source.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2) values.Add(args[index], args[index + 1]);
        return new(Path.GetFullPath(values["--package"]), Path.GetFullPath(values["--fixtures"]),
            Path.GetFullPath(values["--output"]), Path.GetFullPath(values["--private-output"]), values["--source"]);
    }
}

internal sealed class TestDialogs : IViewerDialogs
{
    public string? Database { get; set; }
    public string? Report { get; set; }
    public int DatabaseCalls { get; private set; }
    public int ReportCalls { get; private set; }
    public string? ChooseDatabase(IWin32Window owner) { DatabaseCalls++; return Database; }
    public string? ChooseReport(IWin32Window owner, string suggestedName) { ReportCalls++; return Report; }
}

internal sealed record CheckResult(string Name, string Status, string? ErrorType = null, string? SafeReason = null);
internal sealed class TestFailureException(string message) : Exception(message);
internal sealed record ImageResult(string Name, string Sha256, int Width, int Height);
internal sealed record UiObservation(string Phase, bool IsBusy, bool NextEnabled, bool PendingOperationCompleted);

internal sealed class Runner
{
    private static readonly string[] PlannedChecks =
    [
        "frozen_package_and_actual_ui_assembly_identity",
        "synthetic_inputs_and_special_character_paths",
        "real_cli_records_summary_and_exact_time_strings",
        "real_cli_second_page_and_explanation_identity",
        "real_cli_diff_precise_integer_and_unknown_state",
        "strict_protocol_rejects_invalid_schema_kind_and_shape",
        "missing_null_unknown_enums_and_json_precision",
        "real_cli_database_and_process_failures_are_errors",
        "doctor_rejected_without_starting_capture",
        "native_window_empty_state",
        "native_production_executable_launch",
        "ui_open_finished_record_and_overview",
        "ui_state_changes_filter_and_reason",
        "ui_page_then_select_and_explain_same_event",
        "ui_filter_explicitly_applies_to_current_page",
        "ui_local_report_matches_current_record_page_and_event",
        "ui_historical_name_and_unknown_evidence",
        "ui_unfinished_and_unavailable_metrics",
        "ui_packaged_three_record_sample_switching",
        "ui_valid_empty_database_and_cancel",
        "ui_errors_are_visible_and_do_not_create_records",
        "all_source_and_viewed_databases_unchanged"
    ];
    private readonly Options options;
    private readonly List<CheckResult> results = [];
    private readonly List<ImageResult> images = [];
    private readonly List<UiObservation> uiObservations = [];
    private readonly Dictionary<string, string> initialHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> databases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoreResponse> summaries = new(StringComparer.Ordinal);
    private int assertionsPassed;
    private int assertionsFailed;
    private string? selectedEvent;
    private string? finishedId;
    private string? reportPath;
    private string? publicReportPath;
    private bool inputsReady;
    private bool finishedOpen;
    private bool eventPageReady;
    private bool actualCliExecuted;
    private bool completed;
    private readonly StringBuilder privateLog = new();
    public TestDialogs Dialogs { get; } = new();
    public CoreClient Client { get; }
    public bool Success => results.Count > 0 && results.All(result => result.Status == "passed");

    public Runner(Options options)
    {
        this.options = options;
        Directory.CreateDirectory(options.Output);
        Directory.CreateDirectory(options.PrivateOutput);
        Client = new CoreClient(Path.Combine(options.Package, "contain.exe"));
    }

    public async Task RunAsync(ViewerForm form)
    {
        await Check("frozen_package_and_actual_ui_assembly_identity", () =>
        {
            Require(options.Source.Length == 40 && options.Source.All(Uri.IsHexDigit), "source SHA");
            Require(File.Exists(Client.ExecutablePath), "packaged core exists");
            Require(File.Exists(Path.Combine(options.Package, "Contain.Viewer.exe")), "packaged launcher exists");
            Require(Hash(typeof(ViewerForm).Assembly.Location) == Hash(Path.Combine(options.Package, "Contain.Viewer.dll")),
                "tested production UI assembly equals delivered UI assembly");
            return Task.CompletedTask;
        });
        await Check("synthetic_inputs_and_special_character_paths", () =>
        {
            // A fixed, publicly reviewable synthetic path keeps host/user paths out of screenshots.
            // Refuse to overwrite anything; the CI runner is disposable and this is never a user DB.
            var working = Path.Combine(Path.GetPathRoot(options.Fixtures)!, "Contain Viewer Synthetic", options.Source[..12], "数据库 中文 & $ ; [x]");
            Require(!Directory.Exists(working), "new isolated synthetic directory");
            Directory.CreateDirectory(working);
            foreach (var name in new[] { "finished", "historical", "interrupted", "empty", "corrupt", "future", "unrelated" })
            {
                var source = Path.Combine(options.Fixtures, name + ".db");
                Require(File.Exists(source), "fixture exists: " + name);
                var copy = Path.Combine(working, name + " 记录.db");
                File.Copy(source, copy, false);
                databases[name] = copy;
                initialHashes[source] = Hash(source);
                initialHashes[copy] = Hash(copy);
            }
            databases["missing"] = Path.Combine(working, "missing 未创建.db");
            Require(!File.Exists(databases["missing"]), "missing input does not exist");
            inputsReady = true;
            return Task.CompletedTask;
        });
        await Check("real_cli_records_summary_and_exact_time_strings", async () =>
        {
            foreach (var scenario in new[] { "finished", "historical", "interrupted" })
            {
                actualCliExecuted = true;
                var listed = await Client.ListAsync(databases[scenario]);
                Require(listed.Data.ValueKind == JsonValueKind.Array && listed.Data.GetArrayLength() == 1, "one real CLI record");
                var id = listed.Data[0].GetProperty("id").GetString()!;
                Require(id == "synthetic-" + scenario, "explicit synthetic identity");
                var summary = await Client.SummaryAsync(databases[scenario], id);
                summaries[scenario] = summary;
                Require(summary.Data.GetProperty("id").GetString() == id, "summary identity");
                Require(summary.Data.GetProperty("events").GetArrayLength() == 0, "summary excludes full event history");
                Require(summary.Data.GetProperty("view_scope").GetString() == "summary", "summary scope explicit");
                var page = await Client.HistoryAsync(databases[scenario], id, 2, 0);
                Require(page.Data.GetProperty("events").GetArrayLength() == 2, "actual bounded query");
                foreach (var item in page.Data.GetProperty("events").EnumerateArray())
                {
                    Require(item.GetProperty("timestamp_ticks").ValueKind == JsonValueKind.String, "FILETIME stays a string");
                    Require(ulong.Parse(item.GetProperty("timestamp_ticks").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 9007199254740992UL,
                        "FILETIME fixture exceeds JavaScript exact integer range");
                }
                Require(summary.Data.GetProperty("started_at").GetString()!.EndsWith(".120+00:00", StringComparison.Ordinal), "actual CLI start-time spelling retained");
                Require(page.Data.GetProperty("events")[0].GetProperty("timestamp").GetString()!.EndsWith(".123400+00:00", StringComparison.Ordinal), "actual CLI event-time spelling retained");
                if (scenario == "finished") finishedId = id;
            }
            Require(summaries["finished"].Data.GetProperty("capture_state").GetString() == "finished", "finished state");
            Require(summaries["interrupted"].Data.GetProperty("capture_state").GetString() != "finished", "unfinished state remains unfinished");
        }, () => inputsReady);
        await Check("real_cli_second_page_and_explanation_identity", async () =>
        {
            var first = await Client.HistoryAsync(databases["finished"], finishedId!, 50, 0);
            var next = await Client.HistoryAsync(databases["finished"], finishedId!, 50, 50);
            var firstEvents = first.Data.GetProperty("events").EnumerateArray().ToArray();
            var nextEvents = next.Data.GetProperty("events").EnumerateArray().ToArray();
            Require(firstEvents.Length == 50 && nextEvents.Length == 10, "real 60-event fixture pages 50 and 10");
            Require(!firstEvents.Select(Id).Intersect(nextEvents.Select(Id)).Any(), "page IDs do not overlap");
            selectedEvent = Id(nextEvents[0]);
            var explanation = await Client.ExplainAsync(databases["finished"], selectedEvent);
            Require(explanation.Data.GetProperty("event").GetProperty("id").GetString() == selectedEvent, "explain same paged event");
            Require(JsonView.JsonEqual(explanation.Data.GetProperty("event"), nextEvents[0]), "event fields equal between views");
        }, () => finishedId is not null);
        await Check("real_cli_diff_precise_integer_and_unknown_state", async () =>
        {
            var diff = await Client.DiffAsync(databases["finished"], finishedId!);
            Require(diff.Data.GetProperty("view_scope").GetString() == "state_only", "diff scope explicit");
            Require(diff.Data.GetProperty("operations").GetArrayLength() == 0, "diff avoids source history");
            var files = diff.Data.GetProperty("files").EnumerateArray().ToArray();
            Require(files.Any(file => file.TryGetProperty("after_size", out var size) && size.GetRawText() == "9007199254740993"), "exact integer is retained");
            Require(files.All(file => file.GetProperty("confidence").GetString() == "Unknown"), "state Unknown not promoted");
        }, () => finishedId is not null);
        await Check("strict_protocol_rejects_invalid_schema_kind_and_shape", async () =>
        {
            var good = summaries["finished"].RawJson;
            RejectParse("not JSON", "inspect_summary");
            RejectParse(good + "\n{}", "inspect_summary");
            RejectParse(Mutate(good, node => node["schema_version"] = 999), "inspect_summary");
            RejectParse(Mutate(good, node => node["schema_version"] = "3"), "inspect_summary");
            RejectParse("{\"schema_version\":3," + good.TrimStart()[1..], "inspect_summary");
            RejectParse(Mutate(good, node => node["kind"] = "history"), "inspect_summary");
            RejectParse(Mutate(good, node => node["data"] = null), "inspect_summary");
            RejectParse(Mutate(good, node => node["data"] = "not an object"), "inspect_summary");
            RejectParse(Mutate(good, node => node["data"]!["processes"]![0] = "not an object"), "inspect_summary");
            var history = await Client.HistoryAsync(databases["finished"], finishedId!, 2, 0);
            RejectParse(Mutate(history.RawJson, node => node["data"]!["events"]![0]!["timestamp_ticks"] = 134350000000000011L), "history");
            RejectParse(Mutate(history.RawJson, node => node["data"]!["events"]![0]!["evidence"] = "not an object"), "history");
            RejectParse(Mutate(history.RawJson, node => node["data"]!["events"]![0]!["raw"]!["file_name"] = "not an object"), "history");
        }, () => summaries.ContainsKey("finished"));
        await Check("missing_null_unknown_enums_and_json_precision", async () =>
        {
            var history = await Client.HistoryAsync(databases["historical"], "synthetic-historical", 3, 0);
            var events = history.Data.GetProperty("events");
            Require(events[1].GetProperty("confidence").GetString() == "Unknown", "unknown retained");
            Require(events[1].GetProperty("success").ValueKind == JsonValueKind.Null, "unknown result remains null");
            var interrupted = summaries["interrupted"].Data.GetProperty("backend");
            Require(interrupted.GetProperty("etw_events_lost").ValueKind == JsonValueKind.Null, "unmeasured loss is null");
            var summaryCounts = summaries["interrupted"].Data.GetProperty("summary_counts");
            foreach (var metric in new[] { "events_normalized", "application_records_dropped", "decode_errors", "snapshot_gaps", "notification_gaps", "drain_timed_out" })
                Require(summaryCounts.GetProperty(metric).ValueKind == JsonValueKind.Null, "unmeasured summary metric is null: " + metric);
            Require(summaryCounts.GetProperty("events_retained").GetInt64() == 60, "retained count remains measured");
            Require(summaryCounts.GetProperty("unknown_actor_events").GetInt64() == 60, "provisional unknown count remains measured");
            var changed = Mutate(history.RawJson, node => node["data"]!["events"]![0]!["operation"] = "future_operation_label");
            var accepted = CoreResponse.Parse(changed, "history");
            Require(accepted.Data.GetProperty("events")[0].GetProperty("operation").GetString() == "future_operation_label", "unknown additive enum accepted");
            var futureConfidence = CoreResponse.Parse(Mutate(history.RawJson, node => node["data"]!["events"]![0]!["confidence"] = "FutureConfidence"), "history");
            Require(futureConfidence.Data.GetProperty("events")[0].GetProperty("confidence").GetString() == "FutureConfidence", "unknown confidence retained without promotion");
            Require(JsonView.Text(JsonView.Field(interrupted, "missing_test_metric"), "unavailable") == "unavailable", "missing stays unavailable");
            Require(JsonView.Text(interrupted.GetProperty("etw_events_lost"), "unavailable") == "unavailable", "null stays unavailable");
            using var exact = JsonDocument.Parse("{\"time\":\"2026-10-01T00:00:01.123400+00:00\",\"integer\":9007199254740993,\"null\":null,\"nested\":[{\"a\":1}]}");
            Require(exact.RootElement.GetProperty("time").GetString()!.EndsWith(".123400+00:00", StringComparison.Ordinal), "time spelling retained");
            Require(exact.RootElement.GetProperty("integer").GetRawText() == "9007199254740993", "raw integer retained");
            using var different = JsonDocument.Parse("{\"time\":\"2026-10-01T00:00:01.1234+00:00\",\"integer\":9007199254740993,\"null\":null,\"nested\":[{\"a\":1}]}");
            Require(!JsonView.JsonEqual(exact.RootElement, different.RootElement), "timestamp spelling is not normalized away");
        }, () => summaries.ContainsKey("interrupted"));
        await Check("real_cli_database_and_process_failures_are_errors", async () =>
        {
            foreach (var scenario in new[] { "missing", "corrupt", "future", "unrelated" })
                await RejectAsync(() => Client.ListAsync(databases[scenario]), "refused " + scenario);
            Require(!File.Exists(databases["missing"]), "missing database not created");
            try
            {
                await Client.ExplainAsync(databases["finished"], "synthetic-event-not-present");
                Require(false, "missing event must produce nonzero CLI result");
            }
            catch (CoreClientException error)
            {
                Require(error.Code == "cli_exit" && error.ExitCode == 1, "actual CLI nonzero exit and code retained");
            }
            var absent = new CoreClient(Path.Combine(options.PrivateOutput, "absent-contain.exe"));
            await RejectAsync(() => absent.ListAsync(databases["finished"]), "missing executable refused");
            var mismatched = new CoreClient(Environment.ProcessPath!);
            try
            {
                await mismatched.ListAsync(databases["finished"]);
                Require(false, "incompatible CLI version must fail");
            }
            catch (CoreClientException error) { Require(error.Code == "cli_version", "version failure is distinguished"); }
            var empty = await Client.ListAsync(databases["empty"]);
            Require(empty.Data.GetArrayLength() == 0, "valid empty database has no demo insertion");
        }, () => inputsReady);
        await Check("doctor_rejected_without_starting_capture", async () =>
        {
            await RejectAsync(() => Client.DoctorAsync(databases["finished"]), "read-only doctor refused");
        }, () => inputsReady);
        await Check("native_window_empty_state", async () =>
        {
            await WaitIdle(form);
            Require(Control<Button>(form, "OpenDatabase").Enabled, "open entry is usable");
            Require(Control<DataGridView>(form, "Sessions").Rows.Count == 0, "initial no records");
            Require(!Control<Button>(form, "ExportReport").Enabled, "no export before record exists");
            Screenshot(form, "empty-state.png");
        });
        await Check("native_production_executable_launch", LaunchPackagedWindow);
        await Check("ui_open_finished_record_and_overview", async () =>
        {
            await Open(form, databases["finished"]);
            Require(Control<DataGridView>(form, "Sessions").Rows.Count == 1, "one record listed through actual UI");
            Require(AllText(form).Contains("Synthetic finished", StringComparison.Ordinal), "real record name visible");
            Require(AllText(form).Contains("Degraded", StringComparison.Ordinal), "coverage remains Degraded");
            var databaseLabel = Control<Label>(form, "DatabasePath");
            Require(databaseLabel.Text == "当前数据库：" + databases["finished"], "exact selected database path shown");
            Require(databaseLabel.Text.Contains('&'), "special-character fixture includes literal ampersand");
            foreach (var name in new[] { "DatabasePath", "SessionTitle", "Status" })
                Require(!Control<Label>(form, name).UseMnemonic, "dynamic label preserves literal ampersands: " + name);
            Require(Control<Button>(form, "ExportReport").Enabled, "report available after record open");
            SelectTab(form, 0);
            Screenshot(form, "finished-overview.png");
            finishedOpen = true;
        }, () => inputsReady && finishedId is not null);
        await Check("ui_state_changes_filter_and_reason", async () =>
        {
            SelectTab(form, 1);
            var grid = Control<DataGridView>(form, "Changes");
            var rows = grid.Rows.Count;
            Require(rows >= 2, "actual file and registry differences rendered");
            Require(grid.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, "文件")), "file differences separated");
            Require(grid.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, "注册表")), "registry differences separated");
            grid.ClearSelection(); grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[0];
            await WaitIdle(form);
            var item = (JsonElement)grid.Rows[0].Tag!;
            Require(Control<TextBox>(form, "ChangeDetails").Text.Contains(item.GetProperty("reason").GetString()!, StringComparison.Ordinal), "selected state-change reason retained");
            Require(item.GetProperty("confidence").GetString() == "Unknown" && Equals(grid.Rows[0].Cells[3].Value, "Unknown · 未确定"), "unknown state cell is not promoted");
            var filter = Control<TextBox>(form, "ChangeFilter");
            filter.Text = "no-such-synthetic-state-unique";
            await WaitIdle(form);
            Require(grid.Rows.Count == 0, "state filter empty result");
            Require(Control<TextBox>(form, "ChangeDetails").Text.Contains("没有符合", StringComparison.Ordinal), "empty state filter explained");
            filter.Text = string.Empty;
            await WaitIdle(form);
            Require(grid.Rows.Count == rows, "clearing state filter restores records");
            grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[0];
            Screenshot(form, "state-changes.png");
        }, () => finishedOpen);
        await Check("ui_page_then_select_and_explain_same_event", async () =>
        {
            SelectTab(form, 2);
            // Activating the real grid can select its first row and start the
            // production asynchronous explain handler. Assert navigation only
            // after that operation settles; keep both states as safe evidence.
            ObserveEventUi(form, "event_tab_selected");
            await WaitIdle(form);
            ObserveEventUi(form, "event_tab_idle");
            Require(!form.IsBusy && form.PendingOperation.IsCompleted, "event tab work completed before navigation assertion");
            var grid = Control<DataGridView>(form, "Events");
            Require(grid.Rows.Count == 50, "first UI page bounded at fifty");
            Require(Control<Button>(form, "NextEvents").Enabled, "next enabled on first page");
            Control<Button>(form, "NextEvents").PerformClick();
            await WaitIdle(form);
            Require(grid.Rows.Count == 10, "second UI page contains ten");
            Require(!Control<Button>(form, "NextEvents").Enabled, "last page cannot advance");
            grid.ClearSelection(); grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[0];
            await WaitIdle(form);
            Require(grid.Rows[0].Tag is JsonElement item && Id(item) == selectedEvent, "UI selected same actual CLI page event");
            Require(Control<TextBox>(form, "EventDetails").Text.Contains(selectedEvent!, StringComparison.Ordinal), "detail retains selected event ID");
            Require(Control<Button>(form, "CopyEventId").Enabled, "copy event action available");
            Control<Button>(form, "CopyEventId").PerformClick();
            Require(Clipboard.GetText() == selectedEvent, "actual clipboard contains exact event ID");
            Control<CheckBox>(form, "RawToggle").Checked = true;
            var raw = Control<TextBox>(form, "RawDetails");
            Require(raw.Visible && raw.Text.Contains(selectedEvent!, StringComparison.Ordinal), "expanded raw details from real explanation");
            var displayed = CoreResponse.Parse(raw.Text, "explain");
            Require(JsonView.JsonEqual(displayed.Data.GetProperty("event"), (JsonElement)grid.Rows[0].Tag!), "raw detail fields match selected page row exactly");
            Require(Control<TextBox>(form, "EventDetails").Text.Contains("应用关联：High", StringComparison.Ordinal), "known source attribution visible without deleting permission");
            Control<CheckBox>(form, "RawToggle").Checked = false;
            Screenshot(form, "event-explanation.png");
            eventPageReady = true;
        }, () => finishedOpen && selectedEvent is not null);
        await Check("ui_filter_explicitly_applies_to_current_page", async () =>
        {
            var filter = Control<TextBox>(form, "EventFilter");
            filter.Text = "no-such-synthetic-event-unique";
            await WaitIdle(form);
            Require(Control<DataGridView>(form, "Events").Rows.Count == 0, "filter empty result");
            Require(AllText(form).Contains("本页", StringComparison.Ordinal), "current-page filter scope visible");
            filter.Text = string.Empty;
            await WaitIdle(form);
            var grid = Control<DataGridView>(form, "Events");
            Require(grid.Rows.Count == 10, "clearing filter restores same page");
            grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[0];
            await WaitIdle(form);
        }, () => eventPageReady);
        await Check("ui_local_report_matches_current_record_page_and_event", async () =>
        {
            reportPath = Path.Combine(options.PrivateOutput, "synthetic-viewer-report.html");
            Require(!File.Exists(reportPath), "new report path");
            Dialogs.Report = reportPath;
            var calls = Dialogs.ReportCalls;
            Control<Button>(form, "ExportReport").PerformClick();
            await WaitIdle(form);
            Require(Dialogs.ReportCalls == calls + 1, "production export button invokes save dialog");
            Require(File.Exists(reportPath), "actual report written locally");
            var report = WebUtility.HtmlDecode(await File.ReadAllTextAsync(reportPath, Encoding.UTF8));
            Require(report.Contains(finishedId!, StringComparison.Ordinal), "report same record");
            Require(report.Contains(selectedEvent!, StringComparison.Ordinal), "report selected event");
            Require(report.Contains("9007199254740993", StringComparison.Ordinal), "report exact integer");
            Require(!report.Contains("synthetic-historical", StringComparison.Ordinal), "report not a different session");
            Require(!report.Contains("<script", StringComparison.OrdinalIgnoreCase), "report has no active scripts");
            await RejectAsync(() => ReportExporter.ExportAsync(Client, databases["finished"], finishedId!, selectedEvent, 50, 50, databases["finished"]), "cannot export over database");
            await RejectAsync(() => ReportExporter.ExportAsync(Client, databases["finished"], finishedId!, selectedEvent, 50, 50, reportPath), "cannot overwrite existing report");
            var wrongPage = Path.Combine(options.PrivateOutput, "refused-wrong-page.html");
            await RejectAsync(() => ReportExporter.ExportAsync(Client, databases["finished"], finishedId!, selectedEvent, 50, 0, wrongPage), "cannot export an event outside the current page");
            Require(!File.Exists(wrongPage), "rejected report not created");
            await RejectAsync(() => ReportExporter.ExportAsync(Client, databases["finished"], finishedId!, selectedEvent, 50, 50, databases["finished"] + ":report.html"), "Windows alternate data stream destination refused");
            foreach (var privateRoot in new[] { options.PrivateOutput, options.Package, options.Fixtures })
                Require(!report.Contains(privateRoot, StringComparison.OrdinalIgnoreCase), "public synthetic report excludes private working directories");
            Require(report.Contains("SYNTHETIC", StringComparison.Ordinal) && report.Contains("Contain Viewer Synthetic", StringComparison.Ordinal), "public report is explicitly synthetic with a reviewed fixed database path");
            Require(!report.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) && !report.Contains("S-1-5-21-", StringComparison.Ordinal), "public report excludes user profiles and machine SIDs");
            var publicDirectory = Path.Combine(options.Output, "reports");
            Directory.CreateDirectory(publicDirectory);
            publicReportPath = Path.Combine(publicDirectory, "synthetic-viewer-report.html");
            File.Copy(reportPath, publicReportPath, false);
            Require(Hash(reportPath) == Hash(publicReportPath), "public synthetic HTML is the actual export without rewriting");
        }, () => eventPageReady);
        await Check("ui_historical_name_and_unknown_evidence", async () =>
        {
            await Open(form, databases["historical"]);
            SelectTab(form, 2);
            var grid = Control<DataGridView>(form, "Events");
            Require(grid.Rows.Count == 50, "historical page bounded");
            grid.ClearSelection(); grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[0];
            await WaitIdle(form);
            var details = Control<TextBox>(form, "EventDetails").Text;
            var historical = (JsonElement)grid.Rows[0].Tag!;
            Require(historical.GetProperty("raw").GetProperty("file_name").GetProperty("path_source").GetString() == "file_object_cache", "selected actual record is historical cache name");
            Require(details.Contains("名称来源：file_object_cache", StringComparison.Ordinal), "historical source disclosed in the actual field");
            Require(details.Contains("名称边界：历史缓存名称，不是已验证的当前路径。", StringComparison.Ordinal), "historical name cannot become current path");
            Screenshot(form, "historical-name.png");
            grid.ClearSelection(); grid.Rows[1].Selected = true; grid.CurrentCell = grid.Rows[1].Cells[0];
            await WaitIdle(form);
            Require(((JsonElement)grid.Rows[1].Tag!).GetProperty("confidence").GetString() == "Unknown", "selected actual record attribution Unknown");
            Require(Control<TextBox>(form, "EventDetails").Text.Contains("应用关联：Unknown · 未确定", StringComparison.Ordinal), "Unknown field shown without promotion");
            Require(Control<TextBox>(form, "EventDetails").Text.Contains("请求结果：未提供", StringComparison.Ordinal), "unknown result not displayed as false or zero");
            Screenshot(form, "unknown-evidence.png");
        }, () => inputsReady);
        await Check("ui_unfinished_and_unavailable_metrics", async () =>
        {
            await Open(form, databases["interrupted"]);
            SelectTab(form, 0);
            var overview = Control<TextBox>(form, "Overview").Text;
            var currentSummary = await Client.SummaryAsync(databases["interrupted"], "synthetic-interrupted");
            var actualState = currentSummary.Data.GetProperty("capture_state").GetString();
            Require(actualState == "finalizing", "interrupted fixture stopped after raw drain and before derived finalization");
            Require(overview.Contains("记录状态：未结束（" + actualState + "）", StringComparison.Ordinal), "unfinished actual capture state indicated");
            Require(overview.Contains("覆盖质量：Incomplete · 证据不完整", StringComparison.Ordinal), "unfinished quality is not finished or Complete");
            Require(overview.Contains("ETW 丢失事件：未提供", StringComparison.Ordinal), "missing ETW measurements not zero");
            Require(overview.Contains("规范化源操作：未提供", StringComparison.Ordinal), "unmeasured normalized operations visibly unavailable");
            Require(overview.Contains("快照缺口：未提供", StringComparison.Ordinal), "unmeasured snapshot gaps visibly unavailable");
            Require(overview.Contains("通知缺口：未提供", StringComparison.Ordinal), "unmeasured notification gaps visibly unavailable");
            Require(overview.Contains("应用丢弃：未提供", StringComparison.Ordinal), "unmeasured application loss visibly unavailable");
            Require(overview.Contains("时间线观测：60", StringComparison.Ordinal), "retained observations visibly measured");
            Screenshot(form, "interrupted-record.png");
        }, () => inputsReady);
        await Check("ui_packaged_three_record_sample_switching", async () =>
        {
            var packaged = Path.Combine(options.Package, "samples", "viewer-demo.db");
            Require(File.Exists(packaged), "packaged synthetic database exists");
            var copy = Path.Combine(Path.GetDirectoryName(databases["finished"])!, "viewer-demo 三类记录.db");
            File.Copy(packaged, copy, false);
            initialHashes[packaged] = Hash(packaged);
            initialHashes[copy] = Hash(copy);
            Require(initialHashes[packaged] == initialHashes[copy], "exact packaged sample used");
            await Open(form, copy);
            var grid = Control<DataGridView>(form, "Sessions");
            Require(grid.Rows.Count == 3, "actual packaged sample lists three records");
            for (var index = 0; index < grid.Rows.Count; index++)
            {
                var record = (JsonElement)grid.Rows[index].Tag!;
                grid.ClearSelection(); grid.Rows[index].Selected = true; grid.CurrentCell = grid.Rows[index].Cells[0];
                await WaitIdle(form);
                Require(Control<Label>(form, "SessionTitle").Text == record.GetProperty("name").GetString(), "record switching updates title");
                var expected = await Client.HistoryAsync(copy, record.GetProperty("id").GetString()!, 50, 0);
                var eventGrid = Control<DataGridView>(form, "Events");
                Require(eventGrid.Rows.Count == 50, "each selected record loads only its first page");
                Require(JsonView.JsonEqual((JsonElement)eventGrid.Rows[0].Tag!, expected.Data.GetProperty("events")[0]), "record switch keeps event identity consistent");
            }
            SelectTab(form, 0);
            Screenshot(form, "packaged-three-records.png");
        }, () => inputsReady);
        await Check("ui_valid_empty_database_and_cancel", async () =>
        {
            await Open(form, databases["empty"]);
            Require(Control<DataGridView>(form, "Sessions").Rows.Count == 0, "empty database has no synthesized records");
            Require(!Control<Button>(form, "ExportReport").Enabled, "empty DB cannot export a nonexistent record");
            Dialogs.Database = null;
            Control<Button>(form, "OpenDatabase").PerformClick();
            await WaitIdle(form);
            Require(Control<DataGridView>(form, "Sessions").Rows.Count == 0, "cancel leaves empty view stable");
            Screenshot(form, "empty-database.png");
        }, () => inputsReady);
        await Check("ui_errors_are_visible_and_do_not_create_records", async () =>
        {
            foreach (var scenario in new[] { "missing", "corrupt", "future", "unrelated" })
            {
                await Open(form, databases[scenario]);
                Require(Control<Label>(form, "Status").Text.StartsWith("未完成", StringComparison.Ordinal), "visible error status " + scenario);
                Require(Control<DataGridView>(form, "Sessions").Rows.Count == 0, "no records after invalid DB " + scenario);
                Require(!Control<Button>(form, "ExportReport").Enabled, "invalid DB export disabled " + scenario);
            }
            Require(!File.Exists(databases["missing"]), "UI missing DB never created");
            Screenshot(form, "database-error.png");
            var failureDialogs = new TestDialogs { Database = databases["finished"] };
            using var failedCoreForm = new ViewerForm(new CoreClient(Path.Combine(options.PrivateOutput, "absent-contain.exe")), failureDialogs);
            failedCoreForm.Show();
            Control<Button>(failedCoreForm, "OpenDatabase").PerformClick();
            await WaitIdle(failedCoreForm);
            Require(Control<Label>(failedCoreForm, "Status").Text.Contains("contain.exe", StringComparison.Ordinal), "missing core error actionable in UI");
            Require(Control<DataGridView>(failedCoreForm, "Sessions").Rows.Count == 0, "missing core yields no invented records");
            failedCoreForm.Close();
        }, () => inputsReady);
        await Check("all_source_and_viewed_databases_unchanged", () =>
        {
            Require(initialHashes.Count >= 14, "all input originals and copies fingerprinted");
            foreach (var entry in initialHashes) Require(File.Exists(entry.Key) && Hash(entry.Key) == entry.Value, "database bytes unchanged");
            Require(!File.Exists(databases["missing"]), "missing database still absent");
            return Task.CompletedTask;
        }, () => inputsReady);
        completed = true;
    }

    private async Task Check(string name, Func<Task> action, Func<bool>? prerequisite = null)
    {
        if (prerequisite is not null && !prerequisite()) { results.Add(new(name, "not_run", "prerequisite_unavailable")); return; }
        try { await action(); results.Add(new(name, "passed")); }
        catch (Exception error) { results.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : error is CoreClientException core ? core.Code : null)); privateLog.AppendLine(name).AppendLine(error.ToString()); }
    }

    private void Require(bool condition, string reason)
    {
        if (condition) { assertionsPassed++; return; }
        assertionsFailed++;
        throw new TestFailureException(reason);
    }

    private void RejectParse(string raw, string kind)
    {
        var rejected = false;
        try { CoreResponse.Parse(raw, kind); }
        catch (CoreClientException) { rejected = true; }
        Require(rejected, "invalid protocol rejected");
    }

    private async Task RejectAsync(Func<Task> action, string reason)
    {
        var rejected = false;
        try { await action(); }
        catch (CoreClientException) { rejected = true; }
        catch (IOException) { rejected = true; }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, reason);
    }

    private async Task Open(ViewerForm form, string database)
    {
        Dialogs.Database = database;
        var calls = Dialogs.DatabaseCalls;
        Control<Button>(form, "OpenDatabase").PerformClick();
        await WaitIdle(form);
        Require(Dialogs.DatabaseCalls == calls + 1, "production open button invoked file selection");
        var sessions = Control<DataGridView>(form, "Sessions");
        if (sessions.Rows.Count > 0)
        {
            sessions.ClearSelection();
            sessions.Rows[0].Selected = true;
            sessions.CurrentCell = sessions.Rows[0].Cells[0];
            await WaitIdle(form);
        }
    }

    private static async Task WaitIdle(ViewerForm form)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            await Task.Delay(50);
            await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(90));
            if (!form.IsBusy) { await Task.Delay(100); if (!form.IsBusy) return; }
        } while (deadline.Elapsed < TimeSpan.FromSeconds(100));
        throw new TimeoutException("Desktop operation did not settle.");
    }

    private static T Control<T>(System.Windows.Forms.Control parent, string name) where T : System.Windows.Forms.Control
    {
        var control = parent.Controls.Find(name, true).SingleOrDefault();
        return control as T ?? throw new InvalidOperationException("Control unavailable: " + name);
    }

    private static string AllText(System.Windows.Forms.Control parent) => parent.Text + "\n" +
        string.Join("\n", parent.Controls.Cast<System.Windows.Forms.Control>().Select(AllText));

    private static void SelectTab(ViewerForm form, int index) => Control<TabControl>(form, "Tabs").SelectedIndex = index;

    private void ObserveEventUi(ViewerForm form, string phase) => uiObservations.Add(new(
        phase, form.IsBusy, Control<Button>(form, "NextEvents").Enabled, form.PendingOperation.IsCompleted));

    private void Screenshot(ViewerForm form, string name)
    {
        form.Refresh();
        var directory = Path.Combine(options.Output, "screenshots");
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var path = Path.Combine(directory, name);
        bitmap.Save(path, ImageFormat.Png);
        images.Add(new(name, Hash(path), bitmap.Width, bitmap.Height));
    }

    private async Task LaunchPackagedWindow()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(options.Package, "Contain.Viewer.exe"),
            WorkingDirectory = options.Package,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not launch packaged desktop executable.");
        try
        {
            var watch = Stopwatch.StartNew();
            while (!process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) break;
                await Task.Delay(100);
            }
            Require(!process.HasExited, "packaged desktop process remains running");
            Require(process.MainWindowHandle != IntPtr.Zero, "actual Windows main window exists");
            Require(process.CloseMainWindow(), "normal desktop close accepted");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(process.ExitCode == 0, "normal desktop close exits successfully");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    public void RecordUnhandled(Exception error)
    {
        results.Add(new("harness_execution", "failed", error.GetType().Name));
        privateLog.AppendLine(error.ToString());
    }

    public void Finish()
    {
        foreach (var name in PlannedChecks.Where(name => !results.Any(result => result.Name == name)))
            results.Add(new(name, "not_run", "harness_did_not_reach_check"));
        File.WriteAllText(Path.Combine(options.PrivateOutput, "details.log"), privateLog.ToString(), new UTF8Encoding(false));
        var summary = new
        {
            schema_version = 1,
            protocol_revision = "contain-desktop-verification-1",
            scope = "synthetic_actual_windows_desktop_and_packaged_cli",
            source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(),
            actual_cli = actualCliExecuted,
            completed,
            planned_checks = PlannedChecks,
            package_manifest_sha256 = File.Exists(Path.Combine(options.Package, "BUILD.json")) ? Hash(Path.Combine(options.Package, "BUILD.json")) : null,
            synthetic = true,
            real_etw_executed = false,
            viewer_assembly_sha256 = File.Exists(Path.Combine(options.Package, "Contain.Viewer.dll")) ? Hash(Path.Combine(options.Package, "Contain.Viewer.dll")) : null,
            viewer_launcher_sha256 = File.Exists(Path.Combine(options.Package, "Contain.Viewer.exe")) ? Hash(Path.Combine(options.Package, "Contain.Viewer.exe")) : null,
            contain_sha256 = File.Exists(Client.ExecutablePath) ? Hash(Client.ExecutablePath) : null,
            passed = results.Count(result => result.Status == "passed"),
            failed = results.Count(result => result.Status == "failed"),
            skipped = 0,
            not_run = results.Count(result => result.Status == "not_run"),
            assertions_passed = assertionsPassed,
            assertions_failed = assertionsFailed,
            accepted = Success && completed && OperatingSystem.IsWindows(),
            checks = results,
            screenshots = images,
            ui_observations = uiObservations,
            screenshot_method = "Actual production WinForms controls rendered by Windows DrawToBitmap after real button/selection actions; synthetic data only.",
            report_generated = reportPath is not null && File.Exists(reportPath),
            report_sha256 = reportPath is not null && File.Exists(reportPath) ? Hash(reportPath) : null,
            report_scope = "Actual locally exported HTML from synthetic model data; public byte-identical copy only after reviewed-path checks. No user database or full CLI diagnostic log published.",
            report_publication = publicReportPath is not null && File.Exists(publicReportPath) ? new
            {
                path = "reports/synthetic-viewer-report.html",
                sha256 = Hash(publicReportPath),
                synthetic = true,
                byte_identical_to_local_export = Hash(publicReportPath) == Hash(reportPath!)
            } : null,
            time_precision = "JSON string FILETIME and unrounded raw integer tokens; no JavaScript Number conversion.",
            private_error_details_retained = privateLog.Length > 0
        };
        File.WriteAllText(Path.Combine(options.Output, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"Desktop native checks: {summary.passed} passed, {summary.failed} failed, {summary.not_run} not run; synthetic=true.");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Id(JsonElement item) => item.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing event ID.");
    private static string Mutate(string raw, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(raw)!.AsObject(); change(node); return node.ToJsonString();
    }
}
