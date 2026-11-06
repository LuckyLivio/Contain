using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

/// <summary>Complete search export through the actual frozen executable and native save dialog.</summary>
internal sealed class SearchExportVerification(Options options, bool priorChecksPassed)
{
    private static readonly string[] Plan =
    [
        "frozen_export_package_identity", "actual_packaged_search_export_all_ids", "exported_page_selection_preserved",
        "ordinary_page_report_after_clear", "no_matches_cancel_and_failure_state", "safe_targets_and_cancelled_write",
        "literal_text_html_escaping", "unchanged_readonly_databases"
    ];
    private const string Session = "synthetic-search";
    private const string Selected = "synthetic-search-event-120";
    // Independent predetermined input positions, never the search implementation.
    private static readonly string[] Expected = Enumerable.Range(70, 53).Select(i => $"synthetic-search-event-{i:000}").ToArray();
    private readonly CoreClient client = new(Path.Combine(options.Package, "contain.exe"));
    private readonly List<CheckResult> checks = [];
    private readonly List<ImageResult> screenshots = [];
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder errors = new();
    private int assertions;
    private bool actualExecutable, nativeSaveDialog;
    private uint? actualDpi;
    private string[] reportIds = [], pageAfterIds = [];
    private string? copiedId, explainId;
    private long? beforeOffset, afterOffset;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly List<object> progress = [];
    private string currentCheck = "startup", currentStage = "setup";
    private PackagedDesktop? actualDesktop;

    internal async Task<bool> RunAsync(Form owner)
    {
        var root = TestRecordIsolation.Create("search-export");
        using var isolation = TestRecordIsolation.Use(root);
        var library = PersonalRecords.ForCurrentUser().DatabasePath;
        var reportPath = Path.Combine(options.Output, "reports", "search-export-synthetic.html");
        var original = Path.Combine(options.Fixtures, "search.db");
        Progress("started");
        try
        {
            await Check(Plan[0], async () =>
            {
                Require(OperatingSystem.IsWindows(), "native Windows required");
                Require(Hash(typeof(ViewerForm).Assembly.Location) == Hash(Path.Combine(options.Package, "Contain.Viewer.dll")), "harness loads exact frozen production assembly");
                using var build = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.Package, "BUILD.json")));
                Require(build.RootElement.GetProperty("source_sha").GetString() == options.Source && build.RootElement.GetProperty("version").GetString() == ProductVersion.Version, "frozen source and product version match");
                Directory.CreateDirectory(Path.GetDirectoryName(library)!);
                hashes.Add(original, Hash(original)); File.Copy(original, library, false); hashes.Add(library, Hash(library));
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                Require(!File.Exists(reportPath), "actual native export starts with a new output filename");
            });
            using (var desktop = new PackagedDesktop(options.Package, root, savedRecordsOnly: true))
            {
                actualDesktop = desktop;
                desktop.ReportDialogProgress = () => Progress("dialog_stage");
                Stage("packaged_viewer_start");
                await desktop.ReadyAsync(); actualExecutable = true; actualDpi = await desktop.DeviceDpiAsync();
                await Check(Plan[1], async () =>
                {
                    Require(actualDpi > 0, "native executable DPI measured");
                    await desktop.SelectIdentityAsync("session:" + Session);
                    await PackagedDesktop.WaitAsync(async () => await desktop.DescriptionAsync("SessionTitle") == "session:" + Session, TimeSpan.FromSeconds(30));
                    await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("ViewEvents"), TimeSpan.FromSeconds(30));
                    await desktop.InvokeAsync("ViewEvents");
                    await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(30));
                    await desktop.SetTextAsync("EventFilter", "needle");
                    await PackagedDesktop.WaitAsync(async () => await desktop.TextAsync("EventsPage") == "共 53 条，显示第 1–50 条", TimeSpan.FromSeconds(30));
                    await desktop.InvokeAsync("NextEvents");
                    await PackagedDesktop.WaitAsync(async () => await desktop.TextAsync("EventsPage") == "共 53 条，显示第 51–53 条", TimeSpan.FromSeconds(30));
                    Require((await PackagedIds(desktop)).SequenceEqual(Expected.Skip(50)), "actual page two is exactly IDs 120,121,122");
                    beforeOffset = 50;
                    await desktop.SelectIdentityAsync("event:" + Selected);
                    await PackagedDesktop.WaitAsync(async () => await desktop.DescriptionAsync("EventDetails") == "event:" + Selected, TimeSpan.FromSeconds(30));
                    await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("ExportReport"), TimeSpan.FromSeconds(30));
                    Require(await desktop.TextAsync("ExportReport") == "导出全部 53 条匹配结果", "search export action states its full range");
                    Require(await desktop.FullyVisibleAsync("ExportReport"), "full search-export action is inside every native client viewport");
                    screenshots.Add(await desktop.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "search-export-before-save.png"));
                    Stage("native_search_save");
                    await desktop.ExportReportAsync(reportPath);
                    nativeSaveDialog = desktop.ReportDialogInteractions == 1 && desktop.ReportDialogSaves == 1;
                    Require(nativeSaveDialog, "actual packaged EXE native save dialog operated");
                    Stage("search_report_completion");
                    await PackagedDesktop.WaitAsync(async () => File.Exists(reportPath) && await desktop.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(90));
                    Stage("search_report_all_ids");
                    var html = await File.ReadAllTextAsync(reportPath, Encoding.UTF8);
                    reportIds = EventIds(html);
                    Require(reportIds.SequenceEqual(Expected), "all 53 HTML event-row IDs exactly equal independent fixed order, with no duplicates omissions or foreign rows");
                    Require(html.Contains("id=\"matched-events\"", StringComparison.Ordinal), "matched event rows have an explicit report table");
                    Require(WebUtility.HtmlDecode(html).Contains("SYNTHETIC 同名搜索记录", StringComparison.Ordinal) && html.Contains(Session, StringComparison.Ordinal) && html.Contains("needle", StringComparison.Ordinal), "report includes synthetic session name UUID and exact query");
                    Require(!html.Contains("synthetic-search-other", StringComparison.Ordinal), "same-name other session absent from report");
                    Require(!html.Contains(root, StringComparison.OrdinalIgnoreCase) && !html.Contains(options.PrivateOutput, StringComparison.OrdinalIgnoreCase), "public synthetic export contains no harness or private database path");
                    Require(!Regex.IsMatch(html, @"<(script|link|iframe|img)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "HTML contains no scripts or external resource elements");
                    var status = await desktop.TextAsync("Status");
                    Require(status.Contains("53", StringComparison.Ordinal) && status.Contains(reportPath, StringComparison.Ordinal), "completion shows actual written count and chosen local path");
                    await desktop.InvokeAsync("CopyEventId");
                    Require(Clipboard.GetText() == Selected, "public screenshot retains exact selected ID after clearing private output-path status through the actual Copy action");
                    screenshots.Add(await desktop.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "search-export-second-page.png"));
                });
                await Check(Plan[2], async () =>
                {
                    Require(await desktop.TextAsync("EventFilter") == "needle" && await desktop.TextAsync("EventsPage") == "共 53 条，显示第 51–53 条", "export preserves query and page two");
                    pageAfterIds = await PackagedIds(desktop); afterOffset = 50;
                    Require(pageAfterIds.SequenceEqual(Expected.Skip(50)), "same three page-two result IDs remain");
                    explainId = await desktop.DescriptionAsync("EventDetails") == "event:" + Selected ? Selected : null;
                    Require(explainId == Selected && (await desktop.TextAsync("EventDetails")).Contains(Selected, StringComparison.Ordinal), "selected explanation remains exact event120 after export");
                    await desktop.InvokeAsync("CopyEventId"); copiedId = Clipboard.GetText();
                    Require(copiedId == Selected, "copied ID still matches selected event and explanation");
                    Stage("native_save_cancel");
                    await desktop.ExportReportAsync(null);
                    Require(desktop.ReportDialogInteractions == 2 && desktop.ReportDialogSaves == 1, "actual native save dialog cancellation exercised");
                    Require(await desktop.TextAsync("EventFilter") == "needle" && await desktop.TextAsync("EventsPage") == "共 53 条，显示第 51–53 条" && await desktop.DescriptionAsync("EventDetails") == "event:" + Selected, "cancel preserves query page and explanation");
                });
                await Check(Plan[3], async () =>
                {
                    await desktop.InvokeAsync("ClearEventSearch");
                    await PackagedDesktop.WaitAsync(async () => (await desktop.TextAsync("EventsPage")).StartsWith("偏移 0", StringComparison.Ordinal) && await desktop.EnabledAsync("ExportReport"), TimeSpan.FromSeconds(30));
                    Require(await desktop.TextAsync("ExportReport") == "导出本页报告", "clear restores ordinary report action");
                    var ordinaryPath = Path.Combine(root, "ordinary-after-search.html");
                    Stage("native_ordinary_save");
                    await desktop.ExportReportAsync(ordinaryPath);
                    Stage("ordinary_report_completion");
                    await PackagedDesktop.WaitAsync(async () => File.Exists(ordinaryPath) && await desktop.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(90));
                    var ordinary = await File.ReadAllTextAsync(ordinaryPath, Encoding.UTF8);
                    var history = Regex.Match(ordinary, "<pre data-kind=\"history\">(.*?)</pre>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
                    Require(history.Success, "ordinary report retains original CLI history document");
                    var response = CoreResponse.Parse(WebUtility.HtmlDecode(history.Groups[1].Value), "history");
                    var ids = response.Data.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
                    Require(ids.SequenceEqual(Enumerable.Range(0, 50).Select(i => $"synthetic-search-event-{i:000}")), "ordinary first page retains all 50 original unfiltered rows");
                    Require(!ordinary.Contains("id=\"matched-events\"", StringComparison.Ordinal), "ordinary report keeps its own scope");
                    Stage("packaged_viewer_normal_close");
                    await desktop.CloseAsync(); Require(desktop.ExitCode == 0, "actual packaged executable exits normally");
                });
            }
            Progress("packaged_viewer_disposed");

            var dialogs = new TestDialogs();
            Stage("in_process_regressions_start");
            using var form = new ViewerForm(client, dialogs); form.Show(owner); await Idle(form); await Select(form, Session);
            await Check(Plan[4], async () =>
            {
                await Search(form, "never-present-synthetic");
                Require(!C<Button>(form, "ExportReport").Enabled, "no matches cannot export");
                await Search(form, "needle");
                Require(C<Button>(form, "ExportReport").Enabled && C<DataGridView>(form, "Events").SelectedRows.Count == 0, "export is enabled without selected event");
                C<Button>(form, "NextEvents").PerformClick(); await Idle(form); SelectEvent(form, Selected); await Idle(form);
                var before = ViewState(form); var calls = dialogs.ReportCalls;
                dialogs.Report = null; C<Button>(form, "ExportReport").PerformClick(); await Idle(form);
                Require(dialogs.ReportCalls == calls + 1 && ViewState(form) == before, "cancelled save picker keeps query page and selected explanation");
                using (var denied = new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    C<TextBox>(form, "EventFilter").Text = "中文文件";
                    Require(!C<Button>(form, "ExportReport").Enabled, "new query disables prior successful export immediately");
                    await Idle(form);
                    Require(!C<Button>(form, "ExportReport").Enabled && C<Label>(form, "EventsPage").Text.Contains("查询失败", StringComparison.Ordinal), "query failure never exports stale matches");
                }
                await Search(form, "needle"); C<Button>(form, "NextEvents").PerformClick(); await Idle(form); SelectEvent(form, Selected); await Idle(form);
                before = ViewState(form);
                var replacement = Path.Combine(options.Fixtures, "search-replaced.db"); hashes.TryAdd(replacement, Hash(replacement));
                try
                {
                    File.Copy(replacement, library, true);
                    var current = await client.HistoryAsync(library, Session, 50, 0, "needle");
                    Require(current.Data.GetProperty("matched_total").GetInt64() == 53, "replacement preserves count while changing a first-page event");
                    dialogs.Report = Path.Combine(root, "same-count-stale.html");
                    C<Button>(form, "ExportReport").PerformClick(); await Idle(form);
                    Require(!File.Exists(dialogs.Report) && C<Label>(form, "Status").Text.Contains("刷新", StringComparison.Ordinal), "same-count changed content is rejected before report commit");
                    Require(ViewState(form) == before, "failed stale export preserves query page and selected explanation");
                }
                finally { File.Copy(original, library, true); }
            });
            await Check(Plan[5], async () =>
            {
                await Search(form, "中文文件"); await Search(form, "needle");
                C<Button>(form, "NextEvents").PerformClick(); await Idle(form); SelectEvent(form, Selected); await Idle(form);
                var state = ViewState(form);
                Require(C<Button>(form, "ExportReport").Enabled, "failure regressions begin with an exportable current search");
                var page = await client.HistoryAsync(library, Session, 50, 50, "needle");
                var fingerprint = page.Data.GetProperty("search_fingerprint").GetString()!;
                var sentinel = Path.Combine(root, "existing.html"); File.WriteAllText(sentinel, "SYNTHETIC existing report remains unchanged"); var sentinelHash = Hash(sentinel);
                dialogs.Report = sentinel; C<Button>(form, "ExportReport").PerformClick(); await Idle(form);
                Require(Hash(sentinel) == sentinelHash && C<Label>(form, "Status").Text.Contains("已存在", StringComparison.Ordinal), "existing target is refused and not overwritten");
                Require(ViewState(form) == state, "existing-target failure keeps search page and selected explanation");
                var failure = Path.Combine(root, new string('x', 260) + ".html");
                dialogs.Report = failure; C<Button>(form, "ExportReport").PerformClick(); await Idle(form);
                Require(!File.Exists(failure) && !Directory.EnumerateFiles(root, ".contain-report-*.tmp").Any(), "write/commit error leaves no partial target or temporary report");
                Require(ViewState(form) == state && C<Label>(form, "Status").Text.StartsWith("未完成", StringComparison.Ordinal), "write failure preserves search page and selected explanation");
                var cancelled = Path.Combine(root, "cancelled.html"); using var cancel = new CancellationTokenSource(); cancel.Cancel();
                await Reject(() => ReportExporter.ExportSearchAsync(client, library, Session, "needle", fingerprint, 53, cancelled, cancel.Token));
                Require(!File.Exists(cancelled) && Hash(sentinel) == sentinelHash, "cancelled query creates no report and damages no old file");
                var wrongIdentity = Path.Combine(root, "wrong-fingerprint.html");
                await Reject(() => ReportExporter.ExportSearchAsync(client, library, Session, "needle", new string('0', 64), 53, wrongIdentity));
                Require(!File.Exists(wrongIdentity), "mismatching result identity fails before commit");
                Require(!Directory.EnumerateFiles(root, ".contain-report-*.tmp").Any(), "all failure paths clean operation-owned temporary files");
                // Exercise the production streaming formatter independently of the
                // pipe: escaping can exceed the HTML cap even below the JSON cap.
                Stage("formatter_mid_write_cancel");
                var writer = typeof(ReportExporter).GetMethod("WriteSearchHtmlAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                Task<long> Render(Stream output, JsonElement data, CancellationToken token) => (Task<long>)writer.Invoke(null, [output, data, token])!;
                using var duringWrite = new CancellationTokenSource();
                using var interrupted = new CancelAfterWritesStream(duringWrite, 40);
                await Reject(() => Render(interrupted, page.Data, duringWrite.Token));
                Require(duringWrite.IsCancellationRequested && interrupted.BytesWritten > 0, "cancellation after actual formatter writes stops the partial stream");
                Stage("formatter_html_limit");
                var oversized = JsonNode.Parse(page.Data.GetRawText())!;
                oversized["events"] = new JsonArray(JsonNode.Parse(page.Data.GetProperty("events")[0].GetRawText()));
                oversized["events"]![0]!["resource"] = new string('&', 14 * 1024 * 1024);
                using var large = JsonDocument.Parse(oversized.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                using var formatterDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await Task.Run(() => Render(Stream.Null, large.RootElement, formatterDeadline.Token)).WaitAsync(TimeSpan.FromSeconds(35)); throw new TestFailureException("Escaped HTML limit was not enforced."); }
                catch (CoreClientException error) { Require(error.Message.Contains("64 MiB", StringComparison.Ordinal), "HTML expansion above 64 MiB fails explicitly, never reports a truncated success"); }
            });
            await Check(Plan[6], async () =>
            {
                var special = Path.Combine(options.Fixtures, "search-special.db"); hashes.Add(special, Hash(special));
                foreach (var query in new[] { "<script>", "%_", "中文", "write_requested<&>", "High" })
                {
                    var page = await client.HistoryAsync(special, "synthetic-search-special", 50, 0, query);
                    Require(page.Data.GetProperty("matched_total").GetInt64() == 1, "literal special-text search has one exact match");
                    var output = Path.Combine(root, "special-" + Guid.NewGuid().ToString("N") + ".html");
                    var count = await ReportExporter.ExportSearchAsync(client, special, "synthetic-search-special", query, page.Data.GetProperty("search_fingerprint").GetString()!, 1, output);
                    var html = await File.ReadAllTextAsync(output, Encoding.UTF8);
                    Require(count == 1 && EventIds(html).SequenceEqual(new[] { "synthetic-search-special-event-0" }), "special-text export has the original event ID");
                    Require(!html.Contains("<script", StringComparison.OrdinalIgnoreCase) && html.Contains("&lt;script&gt;", StringComparison.Ordinal), "recorded HTML-like names are text, never executable markup");
                    var decoded = WebUtility.HtmlDecode(html);
                    Require(decoded.Contains("SYNTHETIC <记录> & \"HTML\"", StringComparison.Ordinal) && decoded.Contains(query, StringComparison.Ordinal), "escaped session name and literal search round-trip");
                    Require(decoded.Contains(page.Data.GetProperty("events")[0].GetProperty("resource").GetString()!, StringComparison.Ordinal), "complete recorded resource round-trips after HTML escaping");
                }
            });
            await Check(Plan[7], () =>
            {
                Require(hashes.Count >= 4 && hashes.All(item => Hash(item.Key) == item.Value), "all original fixtures and restored isolated library remain byte-identical");
                Require(!Directory.EnumerateFiles(root, ".contain-report-*.tmp", SearchOption.AllDirectories).Any(), "no temporary report remains in isolated sample tree");
                return Task.CompletedTask;
            });
        }
        catch (Exception error) { errors.AppendLine(error.ToString()); Progress("suite_failed", error.GetType().Name); }
        foreach (var name in Plan.Where(name => checks.All(c => c.Name != name))) checks.Add(new(name, "not_run", "export_prerequisite_unavailable"));
        var accepted = priorChecksPassed && checks.All(c => c.Status == "passed");
        Progress(accepted ? "accepted" : "rejected");
        File.WriteAllText(Path.Combine(options.PrivateOutput, "search-export-details.log"), errors.ToString());
        File.WriteAllText(Path.Combine(options.Output, "export-summary.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1, protocol_revision = "contain-desktop-search-export-verification-1", source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(), architecture = RuntimeInformation.OSArchitecture.ToString(), windows_version = Environment.OSVersion.Version.ToString(), actual_device_dpi = actualDpi,
            synthetic = true, real_etw_executed = false, real_user_records_accessed = false, actual_packaged_executable = actualExecutable, native_save_dialog = nativeSaveDialog,
            native_dialog_stage = actualDesktop?.ReportDialogStage, native_dialog_diagnostic = actualDesktop?.ReportDialogDiagnostic,
            native_dialog_interactions = actualDesktop?.ReportDialogInteractions, native_dialog_saves = actualDesktop?.ReportDialogSaves,
            saved_record_viewer_cleanup = actualDesktop?.CleanupState,
            package_manifest_sha256 = Hash(Path.Combine(options.Package, "BUILD.json")), viewer_assembly_sha256 = Hash(typeof(ViewerForm).Assembly.Location),
            viewer_launcher_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.exe")), contain_sha256 = Hash(client.ExecutablePath),
            report = File.Exists(reportPath) ? new { path = "reports/search-export-synthetic.html", bytes = new FileInfo(reportPath).Length, sha256 = Hash(reportPath) } : null,
            report_event_ids = reportIds, event_count = reportIds.Length, page_before_offset = beforeOffset, page_after_offset = afterOffset, page_after_ids = pageAfterIds,
            selected_event_id = Selected, copied_event_id = copiedId, explain_event_id = explainId,
            planned_checks = Plan, checks, assertions, screenshots, passed = checks.Count(c => c.Status == "passed"), failed = checks.Count(c => c.Status == "failed"), skipped = 0,
            not_run = checks.Count(c => c.Status == "not_run"), completed = checks.All(c => c.Status != "not_run"), accepted, retries = 0,
            scope = "Actual packaged EXE and native SaveFileDialog; byte-unchanged synthetic HTML checked against every fixed expected ID. Isolated saved records only, no installer/ETW or personal data."
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return accepted;
    }

    private static string[] EventIds(string html) => Regex.Matches(html, "<tr\\b[^>]*\\bdata-event-id=\"([^\"]*)\"", RegexOptions.CultureInvariant)
        .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
    private static async Task<string[]> PackagedIds(PackagedDesktop desktop) => (await desktop.NamesAsync("Events")).Select(name => Regex.Match(name, @"event:(synthetic-search-event-\d{3})$")).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToArray();
    private static T C<T>(Control parent, string name) where T : Control => (T)parent.Controls.Find(name, true).Single();
    private static string ViewState(ViewerForm form) => string.Join("|", C<TextBox>(form, "EventFilter").Text, C<Label>(form, "EventsPage").Text, C<TextBox>(form, "EventDetails").AccessibleDescription, C<TextBox>(form, "EventDetails").Text);
    private static async Task Idle(ViewerForm form)
    {
        for (var i = 0; i < 5; i++) { await Task.Delay(80); var task = form.PendingOperation; await task.WaitAsync(TimeSpan.FromSeconds(90)); if (ReferenceEquals(task, form.PendingOperation) && !form.IsBusy) return; }
        throw new TimeoutException("Export controls did not become idle.");
    }
    private static async Task Search(ViewerForm form, string text) { C<TextBox>(form, "EventFilter").Text = text; await Idle(form); }
    private static async Task Select(ViewerForm form, string id)
    {
        var grid = C<DataGridView>(form, "Sessions"); var row = grid.Rows.Cast<DataGridViewRow>().Single(r => ((JsonElement)r.Tag!).GetProperty("id").GetString() == id);
        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0]; await Idle(form); C<TabControl>(form, "Tabs").SelectedIndex = 2; await Idle(form);
    }
    private static void SelectEvent(ViewerForm form, string id)
    {
        var grid = C<DataGridView>(form, "Events"); var row = grid.Rows.Cast<DataGridViewRow>().Single(r => ((JsonElement)r.Tag!).GetProperty("id").GetString() == id);
        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0];
    }
    private async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is CoreClientException or IOException or OperationCanceledException or UnauthorizedAccessException) { assertions++; return; }
        throw new TestFailureException("Expected export refusal or cancellation.");
    }
    private async Task Check(string name, Func<Task> action)
    {
        currentCheck = name; currentStage = "check_start"; Progress("started");
        try { await action(); checks.Add(new(name, "passed")); Progress("passed"); }
        catch (Exception error)
        {
            checks.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : "stage=" + currentStage + "; dialog=" + actualDesktop?.ReportDialogStage));
            errors.AppendLine(name).AppendLine(error.ToString()); Progress("failed", error.GetType().Name);
        }
    }
    private void Stage(string name) { currentStage = name; Progress("stage"); }
    private void Progress(string status, string? errorType = null)
    {
        // Public progress contains only declared stage names, safe type names
        // and counts. Never copy dialog values, filenames or exception messages.
        progress.Add(new
        {
            elapsed_ms = elapsed.ElapsedMilliseconds, check = currentCheck, stage = currentStage, status, error_type = errorType,
            dialog_stage = actualDesktop?.ReportDialogStage, dialog_diagnostic = actualDesktop?.ReportDialogDiagnostic,
            dialog_interactions = actualDesktop?.ReportDialogInteractions, dialog_saves = actualDesktop?.ReportDialogSaves,
            saved_record_viewer_cleanup = actualDesktop?.CleanupState
        });
        Directory.CreateDirectory(options.Output);
        File.WriteAllText(Path.Combine(options.Output, "export-progress.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1, synthetic = true, source_sha = options.Source, entries = progress
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"search-export {currentCheck} {currentStage} {status}");
        Console.Out.Flush();
    }
    private void Require(bool condition, string reason) { if (!condition) throw new TestFailureException(reason); assertions++; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class CancelAfterWritesStream(CancellationTokenSource cancellation, int maximumWrites) : Stream
    {
        private int writes;
        internal long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); BytesWritten += buffer.Length;
            if (++writes == maximumWrites) cancellation.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
