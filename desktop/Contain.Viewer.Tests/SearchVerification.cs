using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

/// <summary>Search saved evidence through the production controls, CLI and packaged EXE.</summary>
internal sealed class SearchVerification(Options options, bool priorChecksPassed)
{
    private static readonly string[] Plan =
    [
        "frozen_search_package_identity", "full_session_search_fixed_oracle", "second_page_copy_explain_identity",
        "literal_text_operation_confidence", "empty_clear_failure_and_export_boundary", "superseded_queries_and_same_name_records",
        "default_library_and_legacy_readonly", "packaged_exe_cross_page_search", "search_databases_unchanged"
    ];
    private const string Session = "synthetic-search";
    private const string Selected = "synthetic-search-event-120";
    // Input positions, never the product's matching code, define the independent oracle.
    private static readonly string[] Expected = Enumerable.Range(70, 53).Select(i => $"synthetic-search-event-{i:000}").ToArray();
    private readonly CoreClient client = new(Path.Combine(options.Package, "contain.exe"));
    private readonly List<CheckResult> checks = [];
    private readonly List<ImageResult> screenshots = [];
    private readonly StringBuilder errors = new();
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private string[] firstPage = [], secondPage = [];
    private string? copiedId, explainId;
    private int assertions;
    private bool actualExecutable;
    private uint? actualDpi;

    internal async Task<bool> RunAsync(Form owner)
    {
        var root = TestRecordIsolation.Create("session-search");
        using var isolation = TestRecordIsolation.Use(root);
        var library = PersonalRecords.ForCurrentUser().DatabasePath;
        try
        {
            await Check(Plan[0], async () =>
            {
                Require(OperatingSystem.IsWindows(), "native Windows required");
                Require(Hash(typeof(ViewerForm).Assembly.Location) == Hash(Path.Combine(options.Package, "Contain.Viewer.dll")), "production assembly equals frozen package");
                using var build = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.Package, "BUILD.json")));
                Require(build.RootElement.GetProperty("source_sha").GetString() == options.Source, "exact frozen source identity");
                Require(build.RootElement.GetProperty("version").GetString() == ProductVersion.Version, "frozen version identity");
                Directory.CreateDirectory(Path.GetDirectoryName(library)!);
                var original = Path.Combine(options.Fixtures, "search.db");
                hashes.Add(original, Hash(original)); File.Copy(original, library, false); hashes.Add(library, Hash(library));
            });
            var dialogs = new TestDialogs();
            using var form = new ViewerForm(client, dialogs); form.Show(owner); await Idle(form);
            await Select(form, Session);
            await Check(Plan[1], async () =>
            {
                Require(Ids(form).Length == 50 && !Ids(form).Intersect(Expected).Any(), "original page contains none of the independently predetermined hits");
                await Search(form, "needle"); firstPage = Ids(form);
                Require(firstPage.SequenceEqual(Expected.Take(50)), "first search page equals fixed ordered oracle");
                Require(C<Label>(form, "EventsPage").Text == "共 53 条，显示第 1–50 条", "total and first range shown");
                C<Button>(form, "NextEvents").PerformClick(); await Idle(form); secondPage = Ids(form);
                Require(secondPage.SequenceEqual(Expected.Skip(50)), "second search page equals fixed ordered oracle");
                Require(C<Label>(form, "EventsPage").Text == "共 53 条，显示第 51–53 条", "second range shown");
                Require(firstPage.Concat(secondPage).SequenceEqual(Expected), "no omission duplicate or foreign-session ID");
                Require(!C<Button>(form, "NextEvents").Enabled, "last result page bounded");
                var ticks = C<DataGridView>(form, "Events").Rows.Cast<DataGridViewRow>().Select(r => ((JsonElement)r.Tag!).GetProperty("timestamp_ticks").GetString()).Distinct();
                Require(ticks.Count() == 1, "equal-time events remain separately selectable");
            });
            await Check(Plan[2], async () =>
            {
                var grid = C<DataGridView>(form, "Events"); var row = grid.Rows.Cast<DataGridViewRow>().Single(r => EventId(r) == Selected);
                grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0]; await Idle(form);
                C<Button>(form, "CopyEventId").PerformClick(); copiedId = Clipboard.GetText();
                C<CheckBox>(form, "RawToggle").Checked = true;
                var explain = CoreResponse.Parse(C<TextBox>(form, "RawDetails").Text, "explain");
                explainId = explain.Data.GetProperty("event").GetProperty("id").GetString();
                Require(copiedId == Selected && explainId == Selected, "row clipboard and explain retain same original event ID");
                Require(JsonView.JsonEqual((JsonElement)row.Tag!, explain.Data.GetProperty("event")), "all selected row and explain fields agree");
                C<CheckBox>(form, "RawToggle").Checked = false;
            });
            await Check(Plan[3], async () =>
            {
                foreach (var query in new[] { "中文文件", @"C:\synthetic\含 空格\Needle 中文文件 100%_done.txt", "%", "%_", "100%_done", "RENAME_REQUESTED" })
                {
                    await Search(form, query);
                    Require(Ids(form).SequenceEqual(new[] { query == "RENAME_REQUESTED" ? "synthetic-search-event-100" : "synthetic-search-event-080" }), "literal path Chinese operation and percent underscore query: " + query);
                }
                await Search(form, "High"); Require(C<Label>(form, "EventsPage").Text.StartsWith("共 130 条", StringComparison.Ordinal), "confidence search preserved");
                await Search(form, "_"); Require(C<Label>(form, "EventsPage").Text.StartsWith("共 130 条", StringComparison.Ordinal), "literal underscore in every operation retained");
            });
            await Check(Plan[4], async () =>
            {
                await Search(form, "never-present-synthetic");
                Require(Ids(form).Length == 0 && C<Label>(form, "EventsPage").Text.Contains("没有匹配项", StringComparison.Ordinal), "empty search explicit");
                Require(!C<Button>(form, "ExportReport").Enabled, "no-match search cannot export stale results");
                C<Button>(form, "ClearEventSearch").PerformClick(); await Idle(form);
                Require(Ids(form).Length == 50 && Ids(form)[0] == "synthetic-search-event-000", "clear restores ordinary page one");
                Require(C<Button>(form, "ExportReport").Enabled, "ordinary report restored");
                using (var denied = new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    C<TextBox>(form, "EventFilter").Text = "needle";
                    Require(C<Label>(form, "EventsPage").Text.Contains("正在查询", StringComparison.Ordinal), "loading distinct and synchronous");
                    await Idle(form);
                    Require(Ids(form).Length == 0 && C<Label>(form, "EventsPage").Text.Contains("查询失败", StringComparison.Ordinal), "failure distinct from empty");
                    Require(!C<Button>(form, "CopyEventId").Enabled && C<TextBox>(form, "EventDetails").AccessibleDescription is null && C<TextBox>(form, "RawDetails").Text.Length == 0, "failed search cannot expose stale selected evidence");
                }
                C<Button>(form, "ClearEventSearch").PerformClick(); await Idle(form);
                Require(Ids(form).Length == 50, "query recovers after failure");
            });
            await Check(Plan[5], async () =>
            {
                var filter = C<TextBox>(form, "EventFilter");
                filter.Text = "needle"; filter.Text = "not-present"; filter.Text = "中文文件";
                await Idle(form); Require(Ids(form).SequenceEqual(new[] { "synthetic-search-event-080" }), "rapid conditions retain latest result only");
                filter.Text = "needle"; filter.Clear(); await Idle(form);
                Require(Ids(form).First() == "synthetic-search-event-000", "rapid clear cannot restore obsolete matches");
                await Search(form, "needle"); C<Button>(form, "NextEvents").PerformClick();
                filter.Text = "中文文件"; await Idle(form);
                Require(Ids(form).SequenceEqual(new[] { "synthetic-search-event-080" }), "late page cannot overwrite newer search");
                var grid = C<DataGridView>(form, "Events"); grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[0];
                filter.Text = "never-present"; await Idle(form);
                Require(C<TextBox>(form, "EventDetails").AccessibleDescription is null && C<TextBox>(form, "RawDetails").Text.Length == 0 && !C<Button>(form, "CopyEventId").Enabled, "late explain cannot overwrite new empty selection");
                filter.Text = "needle"; await Select(form, "synthetic-search-other"); await Search(form, "needle");
                Require(Ids(form).Length == 50 && Ids(form).All(id => id.StartsWith("synthetic-search-other-event-", StringComparison.Ordinal)), "same display name binds exact new session");
                await Select(form, Session); await Search(form, "needle");
                Require(Ids(form).SequenceEqual(Expected.Take(50)), "switch back restores only selected UUID");
                // Deterministically complete an old failed request after a newer request.
                var release = new TaskCompletionSource(); var failedLate = false;
                Func<CancellationToken, Task> stale = async _ => { await release.Task; failedLate = true; throw new IOException("SYNTHETIC obsolete search error"); };
                var method = typeof(ViewerForm).GetMethod("StartOperation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var args = new object?[method.GetParameters().Length]; args[0] = stale;
                for (var i = 1; i < args.Length; i++) args[i] = method.GetParameters()[i].Name == "eventQuery";
                method.Invoke(form, args); var old = form.PendingOperation;
                filter.Text = "中文文件"; release.SetResult(); await old; await Idle(form);
                Require(failedLate && Ids(form).SequenceEqual(new[] { "synthetic-search-event-080" }), "deterministic late error cannot replace new results");
                Require(!C<Label>(form, "Status").Text.Contains("obsolete", StringComparison.Ordinal), "late failure not displayed");
            });
            await Check(Plan[6], async () =>
            {
                var old = Path.Combine(root, "external-legacy.db"); var original = Path.Combine(options.Fixtures, "search-legacy.db");
                hashes.Add(original, Hash(original)); File.Copy(original, old, false); hashes.Add(old, Hash(old));
                dialogs.Database = old; C<Button>(form, "OpenDatabase").PerformClick(); await Idle(form);
                await Select(form, "synthetic-search-legacy"); await Search(form, "100%_legacy");
                Require(Ids(form).Length == 1, "supported schema v3 externally searched through read-only copy migration");
                Require(C<Label>(form, "DatabasePath").Text.StartsWith("外部数据库（只读）", StringComparison.Ordinal), "legacy external view explicit");
                C<Button>(form, "MyRecords").PerformClick(); await Idle(form); await Select(form, Session); await Search(form, "needle");
                Require(C<Label>(form, "DatabasePath").Text == "我的记录" && Ids(form).SequenceEqual(Expected.Take(50)), "default library remains exact independent current store");
            });
            await Check(Plan[7], async () =>
            {
                using var desktop = new PackagedDesktop(options.Package, root); await desktop.ReadyAsync();
                actualExecutable = true; actualDpi = await desktop.DeviceDpiAsync(); Require(actualDpi > 0, "actual process native DPI");
                await desktop.SelectIdentityAsync("session:" + Session);
                await PackagedDesktop.WaitAsync(async () => await desktop.DescriptionAsync("SessionTitle") == "session:" + Session, TimeSpan.FromSeconds(30));
                await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("ViewEvents"), TimeSpan.FromSeconds(30));
                await desktop.InvokeAsync("ViewEvents");
                await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(30));
                await desktop.SetTextAsync("EventFilter", "needle");
                await PackagedDesktop.WaitAsync(async () => await desktop.TextAsync("EventsPage") == "共 53 条，显示第 1–50 条", TimeSpan.FromSeconds(30));
                Require((await PackagedIds(desktop)).SequenceEqual(Expected.Take(50)), "actual EXE first page exact independent IDs and order");
                await desktop.InvokeAsync("NextEvents");
                await PackagedDesktop.WaitAsync(async () => await desktop.TextAsync("EventsPage") == "共 53 条，显示第 51–53 条", TimeSpan.FromSeconds(30));
                Require((await PackagedIds(desktop)).SequenceEqual(Expected.Skip(50)), "actual EXE second page exact independent IDs and order");
                await desktop.SelectIdentityAsync("event:" + Selected);
                await PackagedDesktop.WaitAsync(async () => await desktop.DescriptionAsync("EventDetails") == "event:" + Selected, TimeSpan.FromSeconds(30));
                await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("CopyEventId"), TimeSpan.FromSeconds(30));
                await desktop.InvokeAsync("CopyEventId"); Require(Clipboard.GetText() == Selected, "actual packaged clipboard ID equals explained row");
                Require((await desktop.TextAsync("EventDetails")).Contains(Selected, StringComparison.Ordinal), "actual packaged explanation same ID");
                screenshots.Add(await desktop.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "search-packaged-second-page.png"));
                foreach (var id in new[] { "EventFilter", "ClearEventSearch" })
                    Require(await desktop.FullyVisibleAsync(id), "search header remains entirely inside all native client viewports after second-page selection and copy: " + id);
                await desktop.ResizeAsync(1120, 780);
                await PackagedDesktop.WaitAsync(() => desktop.FullyVisibleAsync("EventFilter"), TimeSpan.FromSeconds(10));
                screenshots.Add(await desktop.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "search-packaged-second-page-resized.png"));
                Require(await desktop.FullyVisibleAsync("ClearEventSearch"), "clear-search action remains fully visible after actual resize");
                await desktop.SetTextAsync("EventFilter", "中文文件");
                await PackagedDesktop.WaitAsync(async () => await desktop.TextAsync("EventsPage") == "共 1 条，显示第 1–1 条", TimeSpan.FromSeconds(30));
                Require((await PackagedIds(desktop)).SequenceEqual(new[] { "synthetic-search-event-080" }), "visible header can change query after reading a second-page explanation");
                await desktop.InvokeAsync("ClearEventSearch");
                await PackagedDesktop.WaitAsync(async () => (await desktop.TextAsync("EventsPage")).Contains("偏移 0", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
                Require(await desktop.FullyVisibleAsync("EventFilter"), "search input stays fully visible after actual clear action");
                await desktop.CloseAsync(); Require(desktop.ExitCode == 0, "actual EXE closes normally");
            });
            await Check(Plan[8], () => { Require(hashes.Count >= 4, "current and legacy source and copies fingerprinted"); foreach (var item in hashes) Require(Hash(item.Key) == item.Value, "queried database unchanged"); return Task.CompletedTask; });
        }
        catch (Exception error) { errors.AppendLine(error.ToString()); }
        foreach (var name in Plan.Where(name => checks.All(c => c.Name != name))) checks.Add(new(name, "not_run", "search_prerequisite_unavailable"));
        var accepted = priorChecksPassed && checks.All(c => c.Status == "passed");
        File.WriteAllText(Path.Combine(options.PrivateOutput, "search-details.log"), errors.ToString());
        File.WriteAllText(Path.Combine(options.Output, "search-summary.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1, protocol_revision = "contain-desktop-session-search-verification-1", source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(), architecture = RuntimeInformation.OSArchitecture.ToString(),
            windows_version = Environment.OSVersion.Version.ToString(), actual_device_dpi = actualDpi,
            synthetic = true, real_etw_executed = false, real_user_records_accessed = false, actual_packaged_executable = actualExecutable,
            package_manifest_sha256 = Hash(Path.Combine(options.Package, "BUILD.json")), viewer_assembly_sha256 = Hash(typeof(ViewerForm).Assembly.Location),
            viewer_launcher_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.exe")), contain_sha256 = Hash(client.ExecutablePath),
            total_matches = firstPage.Length + secondPage.Length, first_page_count = firstPage.Length, second_page_count = secondPage.Length,
            expected_ids = Expected, first_page_ids = firstPage, second_page_ids = secondPage, selected_event_id = Selected, copied_event_id = copiedId, explain_event_id = explainId,
            planned_checks = Plan, checks, assertions, screenshots, passed = checks.Count(c => c.Status == "passed"), failed = checks.Count(c => c.Status == "failed"),
            skipped = 0, not_run = checks.Count(c => c.Status == "not_run"), completed = checks.All(c => c.Status != "not_run"), accepted, retries = 0,
            scope = "Saved synthetic records only; actual packaged Windows EXE plus production forms/CLI. Native DPI recorded; simulated layout is separate. No installer or ETW."
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return accepted;
    }
    private static async Task<string[]> PackagedIds(PackagedDesktop desktop) => (await desktop.NamesAsync("Events"))
        .Select(name => Regex.Match(name, @"event:(synthetic-search-event-\d{3})$"))
        .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct().ToArray();
    private static string EventId(DataGridViewRow row) => ((JsonElement)row.Tag!).GetProperty("id").GetString()!;
    private static string[] Ids(ViewerForm form) => C<DataGridView>(form, "Events").Rows.Cast<DataGridViewRow>().Select(EventId).ToArray();
    private static T C<T>(Control parent, string name) where T : Control => (T)parent.Controls.Find(name, true).Single();
    private static async Task Idle(ViewerForm form)
    {
        for (var i = 0; i < 5; i++) { await Task.Delay(80); var task = form.PendingOperation; await task.WaitAsync(TimeSpan.FromSeconds(90)); if (ReferenceEquals(task, form.PendingOperation) && !form.IsBusy) return; }
        throw new TimeoutException("Search controls did not become idle.");
    }
    private static async Task Search(ViewerForm form, string text) { C<TextBox>(form, "EventFilter").Text = text; await Idle(form); }
    private static async Task Select(ViewerForm form, string id)
    {
        var grid = C<DataGridView>(form, "Sessions"); var row = grid.Rows.Cast<DataGridViewRow>().Single(r => EventId(r) == id);
        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0]; await Idle(form);
        C<TabControl>(form, "Tabs").SelectedIndex = 2; await Idle(form);
    }
    private async Task Check(string name, Func<Task> action)
    {
        try { await action(); checks.Add(new(name, "passed")); }
        catch (Exception error) { checks.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : "search verification failed")); errors.AppendLine(name).AppendLine(error.ToString()); }
    }
    private void Require(bool condition, string reason) { if (!condition) throw new TestFailureException(reason); assertions++; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
