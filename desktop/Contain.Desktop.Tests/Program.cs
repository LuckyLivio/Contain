using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Contain.Desktop.Tests;

internal sealed record Options(string Package, string Fixtures, string Output, string PrivateOutput, string Source, string? InstallFixture)
{
    internal static Options Parse(string[] args)
    {
        if (args.Length is not (10 or 12)) throw new ArgumentException("Expected --package --fixtures --output --private-output --source [--install-fixture].");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2) values.Add(args[i], args[i + 1]);
        return new(Path.GetFullPath(values["--package"]), Path.GetFullPath(values["--fixtures"]), Path.GetFullPath(values["--output"]),
            Path.GetFullPath(values["--private-output"]), values["--source"], values.TryGetValue("--install-fixture", out var fixture) ? Path.GetFullPath(fixture) : null);
    }
}
internal sealed record Check(string Name, string Status, string? ErrorType = null, string? Stage = null, string? Control = null, string? SafeReason = null, string[]? CallSites = null);
internal sealed class AssertionException(string reason) : Exception(reason);

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This harness requires native Windows.");
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        return new NativeSuite(Options.Parse(args)).RunAsync().GetAwaiter().GetResult();
    }
}

internal sealed class NativeSuite(Options options)
{
    internal static readonly string[] Plan =
    [
        "frozen_package_identity", "empty_personal_library_and_native_picker_cancel", "saved_records_exact_identity_and_outcomes",
        "search_130_events_53_matches_exact_order", "search_copy_explain_and_export_all", "external_legacy_readonly_and_return",
        "same_name_session_switch_and_clear", "compact_visible_keyboard_actions", "saved_records_cold_restart",
        "gui_default_configuration_no_implicit_install", "gui_single_start_close_guard_and_saved_uuid",
        "real_etw_parent_child_independent_oracle", "real_event_explain_export_and_cold_restart", "source_databases_and_package_unchanged"
    ];
    private readonly List<Check> checks = [];
    private readonly List<Screenshot> screenshots = [];
    private readonly List<object> launches = [];
    private readonly List<object> failureScreenshots = [];
    private readonly List<object> navigationDiagnostics = [];
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonElement> earlierLiveRecords = new(StringComparer.Ordinal);
    private readonly string[] expected = Enumerable.Range(70, 53).Select(i => $"synthetic-search-event-{i:000}").ToArray();
    private readonly StringBuilder privateErrors = new();
    private DesktopDriver? desktop;
    private int assertions;
    private string? selectedId, liveSession, liveEvent;
    private JsonElement? followOracle, liveAccounting;
    private string[] actualFirst = [], actualSecond = [], exportedIds = [];
    private string? currentRoot;
    private string? liveRoot;
    private string? truthRoot;
    private string? liveDatabase;
    private Process? unrelated;
    private Task<string>? unrelatedOutput, unrelatedError;
    private bool actualEtw, coldRestart;
    private int? collectorExit;
    private string Core => Path.Combine(options.Package, "contain.exe");

    internal async Task<int> RunAsync()
    {
        Directory.CreateDirectory(options.Output); Directory.CreateDirectory(options.PrivateOutput);
        await Write("modern-desktop-plan.json", new
        {
            schema_version = 1, protocol = "contain-wpf-native-plan-1", source_sha = options.Source,
            declared_at_utc = DateTimeOffset.UtcNow, planned_checks = Plan, retries = 0,
            maximum_native_captures = options.InstallFixture is null ? 0 : 1,
            native_install_requested = options.InstallFixture is not null,
            deadlines_ms = new { ready = 40000, uia = 20000, transition = 30000, installation = 180000, close = 20000 },
            fixture = new { event_count = 130, matches = 53, first_page = 50, second_page = 3, selected_event = "synthetic-search-event-120" },
            product_process_only = true, real_user_records_accessed = false,
            failure_policy = "Stop the dependent stage on its first failure; retain failed and unexecuted checks. Never retry a launch to obtain a pass."
        });
        try
        {
            await Step(Plan[0], VerifyIdentity);
            await Step(Plan[1], EmptyAndChooser);
            await Step(Plan[2], SavedOutcomes);
            await Step(Plan[3], SearchPages);
            await Step(Plan[4], SearchExplainExport);
            await Step(Plan[5], ExternalReadOnly);
            await Step(Plan[6], SameNames);
            await Step(Plan[7], Compact);
            await Step(Plan[8], SavedColdRestart);
            if (options.InstallFixture is not null)
            {
                await Step(Plan[9], ConfigureInstall);
                await Step(Plan[10], StartAndSave);
                await Step(Plan[11], VerifyLiveOracle);
                await Step(Plan[12], LiveExplainRestart);
            }
            await Step(Plan[13], CheckUnchanged);
        }
        catch (Exception error) { privateErrors.AppendLine(error.ToString()); }
        finally
        {
            PreserveLaunch(); desktop?.Dispose(); desktop = null;
            foreach (var name in Plan.Where(name => checks.All(check => check.Name != name)))
                checks.Add(new(name, "not_run", SafeReason: options.InstallFixture is null && Array.IndexOf(Plan, name) is >= 9 and <= 12 ? "native_install_not_requested_preview_only" : "prerequisite_failed"));
            await File.WriteAllTextAsync(Path.Combine(options.PrivateOutput, "modern-desktop-private-errors.log"), privateErrors.ToString());
            await Write("modern-desktop-summary.json", new
            {
                schema_version = 1, protocol = "contain-wpf-native-verification-1", source_sha = options.Source,
                native_windows = true, windows_version = Environment.OSVersion.Version.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                framework = "WPF + WPF UI", product_process_only = true, real_user_records_accessed = false,
                accepted = checks.Count == Plan.Length && checks.All(check => check.Status == "passed") && actualEtw && coldRestart,
                preview_checks_passed = checks.All(check => check.Status == "passed" || check.SafeReason == "native_install_not_requested_preview_only"),
                native_install_requested = options.InstallFixture is not null, actual_etw = actualEtw, cold_restart_verified = coldRestart,
                viewer_exe_sha256 = HashIfPresent(Path.Combine(options.Package, "Contain.Viewer.exe")), viewer_dll_sha256 = HashIfPresent(Path.Combine(options.Package, "Contain.Viewer.dll")),
                core_sha256 = HashIfPresent(Core), package_manifest_sha256 = HashIfPresent(Path.Combine(options.Package, "BUILD.json")),
                planned_checks = Plan, checks, assertions, passed = checks.Count(c => c.Status == "passed"), failed = checks.Count(c => c.Status == "failed"),
                not_run = checks.Count(c => c.Status == "not_run"), retries = 0, screenshots, launches, failure_screenshots = failureScreenshots,
                navigation_diagnostics = navigationDiagnostics,
                search = new { expected_ids = expected, first_page_ids = actualFirst, second_page_ids = actualSecond, exported_ids = exportedIds, selected_event_id = selectedId },
                live = new { session_id = liveSession, selected_event_id = liveEvent, accounting = liveAccounting, follow_oracle = followOracle },
                scope = "One native WPF package, isolated synthetic stores and at most one real follow-installer capture. Native DPI is measured per screenshot. No simulated scaling is reported as physical DPI."
            });
        }
        return checks.All(check => check.Status == "passed" || check.SafeReason == "native_install_not_requested_preview_only") ? 0 : 1;
    }

    private async Task Step(string name, Func<Task> action)
    {
        try { await action(); checks.Add(new(name, "passed")); }
        catch (Exception error)
        {
            checks.Add(new(name, "failed", error.GetType().Name, desktop?.Stage, desktop?.LastControl,
                error is AssertionException ? error.Message : null, SafeCallSites(error)));
            privateErrors.AppendLine(name).AppendLine(error.ToString());
            if (desktop is not null && !desktop.HasExited && desktop.LastControl is "NewInstall" or "MyRecords" or "CurrentTask" or "OpenDatabase" or "About")
            {
                try { navigationDiagnostics.Add(await desktop.NavigationDiagnosticsAsync()); }
                catch (Exception diagnosticError) { navigationDiagnostics.Add(new { captured = false, error_type = diagnosticError.GetType().Name }); }
            }
            // Preserve the original check failure before this one optional,
            // bounded screenshot. It never retries the action or changes its result.
            if (desktop is not null && !desktop.HasExited)
            {
                try
                {
                    await Shot("failed-" + name + ".png", "failed-isolated-native-stage");
                    failureScreenshots.Add(new { check = name, captured = true, error_type = (string?)null });
                }
                catch (Exception screenshotError)
                { failureScreenshots.Add(new { check = name, captured = false, error_type = screenshotError.GetType().Name }); }
            }
            throw;
        }
        finally { await Write("modern-desktop-progress.json", new { checks, current_stage = name, retries = 0 }); }
    }
    private async Task VerifyIdentity()
    {
        await ObservedWait.VerifyContractAsync();
        Require(options.Source.Length == 40 && options.Source.All(Uri.IsHexDigit), "source SHA shape");
        foreach (var file in new[] { "Contain.Viewer.exe", "Contain.Viewer.dll", "contain.exe", "BUILD.json" }) Track(Path.Combine(options.Package, file));
        using var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.Package, "BUILD.json")));
        Require(build.RootElement.GetProperty("source_sha").GetString() == options.Source, "frozen UI source identity");
        foreach (var file in Directory.EnumerateFiles(options.Package, "*", SearchOption.AllDirectories)) Track(file);
        foreach (var file in Directory.EnumerateFiles(options.Fixtures, "*.db")) Track(file);
    }
    private async Task EmptyAndChooser()
    {
        currentRoot = Root("empty"); Start(currentRoot, "working-empty"); await D.ReadyAsync();
        Require(!File.Exists(Database(currentRoot)), "empty library does not create database");
        await D.UntilAsync("empty_records_ready", "EmptyRecordsCTA", () => D.VisibleAsync("EmptyRecordsCTA"));
        await Shot("records-empty.png", "isolated-empty");
        await D.InvokeAsync("NewInstall"); await D.UntilAsync("new_config", "PickInstaller", () => D.VisibleAsync("PickInstaller"));
        await D.ChooseFileAsync("PickInstaller", null);
        Require(!await D.EnabledAsync("StartRecording") && !File.Exists(Database(currentRoot)), "picker cancellation creates no session and cannot start");
        await D.InvokeAsync("MyRecords");
        await D.UntilAsync("back_to_empty_records", "EmptyRecordsCTA", () => D.VisibleAsync("EmptyRecordsCTA"));
        await D.KeyboardActivateAsync("NewInstall");
        await D.UntilAsync("keyboard_new_record_navigation", "PickInstaller", () => D.VisibleAsync("PickInstaller"));
        await D.CloseAsync(); PreserveLaunch(); D.Dispose(); desktop = null;
    }
    private async Task SavedOutcomes()
    {
        currentRoot = Root("saved"); Seed(currentRoot, "viewer-demo.db", prepareOutcomes: true); Start(currentRoot, "working-saved"); await D.ReadyAsync();
        await D.UntilAsync("saved_library_ready", "record:synthetic-finished", () => D.ExistsAsync("record:synthetic-finished"));
        await Shot("records-sample.png", "synthetic-viewer-fixtures");
        foreach (var id in new[] { "synthetic-finished", "synthetic-historical", "synthetic-interrupted" })
        {
            await OpenRecord(id);
            await D.InvokeAsync("OverviewTab");
            var summary = await Cli(Database(currentRoot), "inspect", "--summary", "--json", "--", id);
            Require(summary.GetProperty("id").GetString() == id, "exact CLI summary identity");
            var text = await D.TextAsync("RecordOutcome");
            Require((await D.TextAsync("RecordingScope")).Contains(SyntheticSql.LongRoot, StringComparison.Ordinal), "complete long scope path remains inspectable");
            if (id == "synthetic-interrupted")
            {
                Require(text.Contains("未完成", StringComparison.Ordinal) || text.Contains("未确认", StringComparison.Ordinal), "unfinished record not rendered finished");
                Require((await D.TextAsync("InstallerOutcome")).Contains("未记录", StringComparison.Ordinal), "missing installer exit is not shown as zero");
                Require((await D.TextAsync("CoverageStatus")).Contains("Incomplete", StringComparison.Ordinal), "incomplete retained distinctly from saved state");
            }
            else
            {
                Require(text.Contains("已结束", StringComparison.Ordinal) || text.Contains("已保存", StringComparison.Ordinal), "finished database record has completed wording");
                var expectedCode = id == "synthetic-historical" ? 1603 : 0;
                Require(summary.GetProperty("exit_code").GetInt32() == expectedCode && await D.TextAsync("InstallerOutcome") == "已记录退出码 " + expectedCode, "persisted zero or nonzero exit shown exactly");
                Require((await D.TextAsync("CoverageStatus")).Contains("Degraded", StringComparison.Ordinal), "coverage-limited finished record not displayed complete");
            }
            await Shot("overview-" + id[10..] + ".png", "synthetic-persisted-outcome");
            await D.InvokeAsync("TechnicalTab");
            var detail = await D.TextAsync("TechnicalDetails");
            Require(detail.Contains("ETW 丢失事件：" + (id == "synthetic-interrupted" ? "未提供" : "0"), StringComparison.Ordinal), "native technical detail distinguishes unmeasured loss from measured zero");
            Require(detail.Contains("时间线观测：60", StringComparison.Ordinal), "native measured timeline denominator remains 60");
            if (id == "synthetic-interrupted")
                Require(detail.Contains("规范化源操作：未提供", StringComparison.Ordinal) && detail.Contains("完整诊断末尾。", StringComparison.Ordinal), "missing normalization and full original error remain available");
            await D.InvokeAsync("MyRecords");
        }
        await D.CloseAsync(); PreserveLaunch(); D.Dispose(); desktop = null;
        currentRoot = Root("search"); Seed(currentRoot, "search.db"); Start(currentRoot, "working-search"); await D.ReadyAsync();
    }
    private async Task SearchPages()
    {
        await OpenRecord("synthetic-search"); await Events();
        await Search("needle", 53, 1);
        actualFirst = await D.RowIdsAsync("EventList", "event:");
        Require(actualFirst.SequenceEqual(expected.Take(50)), "first 50 matching event IDs and order");
        await D.InvokeAsync("NextEvents");
        await D.UntilAsync("search_second_page", "EventPageLabel", async () => (await D.RowIdsAsync("EventList", "event:")).SequenceEqual(expected.Skip(50)));
        actualSecond = await D.RowIdsAsync("EventList", "event:");
        Require(actualSecond.SequenceEqual(expected.Skip(50)) && !await D.EnabledAsync("NextEvents"), "three final exact IDs and bounded page");
        Require(actualFirst.Concat(actualSecond).SequenceEqual(expected), "no duplicates missing or foreign events");
    }
    private async Task SearchExplainExport()
    {
        selectedId = "synthetic-search-event-120"; await Explain(selectedId, "synthetic-search");
        await CopyEvent(selectedId);
        await Shot("events-search-explain.png", "synthetic-130-events-53-matches");
        var report = Path.Combine(options.PrivateOutput, "all-matches.html");
        await D.ChooseFileAsync("ExportMatches", report, save: true);
        await D.UntilAsync("full_match_export", "ExportMatches", async () => File.Exists(report) && await D.EnabledAsync("ExportMatches"));
        var html = WebUtility.HtmlDecode(await File.ReadAllTextAsync(report));
        exportedIds = Regex.Matches(html, "data-event-id=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToArray();
        Require(exportedIds.SequenceEqual(expected), "all 53 exported IDs match independent ordered oracle");
        Require(html.Contains("synthetic-search", StringComparison.Ordinal) && !html.Contains("<script", StringComparison.OrdinalIgnoreCase), "report exact session and passive HTML");
        await D.ChooseFileAsync("ExportMatches", null, save: true);
        Require(await D.HelpAsync("EventDetails") == "event:" + selectedId && (await D.RowIdsAsync("EventList", "event:")).SequenceEqual(expected.Skip(50)), "cancel preserves page and selected evidence");
        var protectedPath = Path.Combine(options.PrivateOutput, "existing-protected-report.html");
        await File.WriteAllTextAsync(protectedPath, "SYNTHETIC original destination must not be overwritten"); var protectedHash = Hash(protectedPath);
        await D.ChooseFileAsync("ExportMatches", protectedPath, save: true, existingRefusal: true);
        await D.UntilAsync("existing_export_refusal", "ShellError", async () => !string.IsNullOrWhiteSpace(await D.TryTextAsync("ShellError")));
        Require(Hash(protectedPath) == protectedHash && await D.HelpAsync("EventDetails") == "event:" + selectedId, "existing export target bytes and selected event preserved after visible error");
        await D.InvokeAsync("ClearSearch");
        await D.UntilAsync("clear_search", "EventList", async () => (await D.RowIdsAsync("EventList", "event:")).FirstOrDefault() == "synthetic-search-event-000");
        var pageReport = Path.Combine(options.PrivateOutput, "current-page.html");
        await D.ChooseFileAsync("ExportPage", pageReport, save: true);
        await D.UntilAsync("page_export", "ExportPage", async () => File.Exists(pageReport) && await D.EnabledAsync("ExportPage"));
        Require((await File.ReadAllTextAsync(pageReport)).Contains("synthetic-search-event-000", StringComparison.Ordinal), "current-page report includes current first event");
    }
    private async Task ExternalReadOnly()
    {
        var external = Path.Combine(options.PrivateOutput, "external-legacy.db"); File.Copy(Path.Combine(options.Fixtures, "search-legacy.db"), external, false); Track(external);
        await D.ChooseFileAsync("OpenDatabase", external); await OpenRecord("synthetic-search-legacy"); await Events(); await Search("100%_legacy", 1, 1);
        Require((await D.RowIdsAsync("EventList", "event:")).SequenceEqual(new[] { "synthetic-search-legacy-event-0" }), "schema3 literal query exact event");
        await D.InvokeAsync("MyRecords"); await OpenRecord("synthetic-search");
        Require(Hash(external) == hashes[external], "external original bytes unchanged");
    }
    private async Task SameNames()
    {
        await D.InvokeAsync("MyRecords"); await OpenRecord("synthetic-search-other");
        await Events(); await Search("needle", 53, 1);
        Require((await D.RowIdsAsync("EventList", "event:")).All(id => id.StartsWith("synthetic-search-other-event-", StringComparison.Ordinal)), "same name retains other exact UUID");
        await D.InvokeAsync("MyRecords"); await OpenRecord("synthetic-search"); await Events();
        await D.SetTextAsync("SearchText", "needle"); await D.InvokeAsync("SearchEvents");
        await D.SetTextAsync("SearchText", "中文文件"); await D.InvokeAsync("SearchEvents");
        await D.UntilAsync("newest_search", "EventList", async () => (await D.RowIdsAsync("EventList", "event:")).SequenceEqual(new[] { "synthetic-search-event-080" }));
        await Search("%_", 1, 1);
        Require((await D.RowIdsAsync("EventList", "event:")).SequenceEqual(new[] { "synthetic-search-event-080" }), "literal percent and underscore query does not become a wildcard");
        await Explain("synthetic-search-event-080", "synthetic-search");
        await DesktopDriver.SetClipboardAsync("SYNTHETIC pending explicit full path copy"); await D.InvokeAsync("CopyEventPath");
        const string fullPath = @"C:\synthetic\含 空格\Needle 中文文件 100%_done.txt";
        await D.UntilAsync("clipboard_full_path", "CopyEventPath", async () => await DesktopDriver.ClipboardAsync() == fullPath, TimeSpan.FromSeconds(5));
        await Search("never-present-synthetic", 0, 0);
        Require(!await D.EnabledAsync("CopyEventId") && !await D.EnabledAsync("ExportMatches"), "empty query cannot export or copy stale evidence");
        await D.InvokeAsync("ClearSearch");
        await D.UntilAsync("clear_latest", "EventList", async () => (await D.RowIdsAsync("EventList", "event:")).FirstOrDefault() == "synthetic-search-event-000");
    }
    private async Task Compact()
    {
        await D.ResizeAsync(820, 640);
        foreach (var id in new[] { "MyRecords", "NewInstall", "OpenDatabase", "About" })
        {
            await D.UntilAsync("compact_navigation_visible", id, () => D.VisibleAsync(id), TimeSpan.FromSeconds(10));
            Require(await D.EnabledAsync(id) && await D.SupportsInvokeAsync(id), "compact primary and footer navigation retain enabled Invoke actions");
        }
        await D.KeyboardReachAsync("OpenDatabase");
        await D.ChooseFileAsync("OpenDatabase", null);
        Require(await D.HelpAsync("SessionTitle") == "session:synthetic-search", "compact external-picker cancel preserves exact current session");
        foreach (var id in new[] { "SearchText", "SearchEvents", "ClearSearch", "NextEvents" })
            await D.UntilAsync("compact_visible", id, () => D.VisibleAsync(id), TimeSpan.FromSeconds(10));
        await D.KeyboardReachAsync("SearchText");
        await Shot("compact-events.png", "synthetic-viewer-fixtures");
        await D.InvokeAsync("NewInstall"); await D.UntilAsync("compact_config", "PickInstaller", () => D.VisibleAsync("PickInstaller"));
        Require(await D.VisibleAsync("StartRecording"), "compact configuration primary action visible");
        await Shot("compact-new-record.png", "isolated-configuration-no-install");
    }
    private async Task SavedColdRestart()
    {
        var first = D.ProcessId; await D.CloseAsync(); PreserveLaunch(); D.Dispose(); desktop = null;
        Start(currentRoot!, "different-cold-working"); await D.ReadyAsync(); Require(D.ProcessId != first, "distinct cold process");
        await OpenRecord("synthetic-search"); await Events(); await Search("needle", 53, 1); await D.InvokeAsync("NextEvents");
        await D.UntilAsync("cold_second_page", "EventList", async () => (await D.RowIdsAsync("EventList", "event:")).SequenceEqual(expected.Skip(50)));
        await Explain("synthetic-search-event-120", "synthetic-search");
        await D.CloseAsync(); PreserveLaunch(); D.Dispose(); desktop = null;
    }

    private async Task ConfigureInstall()
    {
        Require(File.Exists(options.InstallFixture), "controlled installer fixture exists");
        currentRoot = Root("live"); liveDatabase = Database(currentRoot); Seed(currentRoot, "viewer-demo.db");
        var earlier = await Cli(liveDatabase, "list", "--details", "--json");
        foreach (var record in earlier.EnumerateArray())
        {
            var id = record.GetProperty("id").GetString()!;
            earlierLiveRecords.Add(id, await Cli(liveDatabase, "inspect", "--summary", "--json", "--", id));
        }
        // The unchanged fixture authorizes only direct TEMP children with its guarded prefix.
        liveRoot = Path.Combine(Path.GetTempPath(), "contain-demo-modern-" + Guid.NewGuid().ToString("N")); truthRoot = liveRoot + "-truth";
        Directory.CreateDirectory(liveRoot); Directory.CreateDirectory(truthRoot); Directory.CreateDirectory(Path.Combine(liveRoot, "A"));
        foreach (var dir in new[] { liveRoot, truthRoot }) await File.WriteAllTextAsync(Path.Combine(dir, ".contain-demo-marker"), "Contain test fixture");
        var environment = new Dictionary<string, string> { ["CONTAIN_FIXTURE_TRUTH"] = truthRoot, ["CONTAIN_FIXTURE_ROOT"] = liveRoot, ["CONTAIN_FIXTURE_FOLLOW"] = "1" };
        Start(currentRoot, "live-first-working", environment); await D.ReadyAsync();
        var initial = Hash(liveDatabase); await D.InvokeAsync("NewInstall");
        await D.UntilAsync("live_config", "PickInstaller", () => D.VisibleAsync("PickInstaller"));
        await D.ChooseFileAsync("PickInstaller", options.InstallFixture);
        await D.UntilAsync("selected_installer", "StartRecording", () => D.EnabledAsync("StartRecording"));
        Require(await D.TextAsync("InstallName") == Path.GetFileNameWithoutExtension(options.InstallFixture), "default name follows selected EXE");
        Require(Hash(liveDatabase) == initial && !File.Exists(Path.Combine(liveRoot, ".follow-go")), "choosing EXE neither creates session nor launches installer");
        await Shot("new-record-configured.png", "real-controlled-fixture-before-explicit-start");
    }
    private async Task StartAndSave()
    {
        var independent = new ProcessStartInfo(options.InstallFixture!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--role", "follow-unrelated", "--root", liveRoot! }) independent.ArgumentList.Add(arg);
        independent.Environment["CONTAIN_FIXTURE_TRUTH"] = truthRoot; unrelated = Process.Start(independent) ?? throw new IOException("Independent writer did not start.");
        unrelatedOutput = unrelated.StandardOutput.ReadToEndAsync(); unrelatedError = unrelated.StandardError.ReadToEndAsync();
        await D.UntilAsync("independent_ready", "StartRecording", () => Task.FromResult(File.Exists(Path.Combine(liveRoot!, ".follow-unrelated-ready"))), TimeSpan.FromSeconds(10));
        await D.InvokeAsync("StartRecording"); D.LiveInstallationStarted = true;
        await D.UntilAsync("start_disabled", "StartRecording", async () => !await D.EnabledAsync("StartRecording"), TimeSpan.FromSeconds(5));
        var duplicateRefused = false;
        try { await D.InvokeAsync("StartRecording"); } catch (System.Windows.Automation.ElementNotEnabledException) { duplicateRefused = true; }
        Require(duplicateRefused, "disabled start cannot invoke second task");
        await D.RequestCloseAsync(); Require(!D.HasExited, "close request preserves active recording");
        var capturedRunning = false;
        await D.UntilAsync("installation_saved_exact_uuid", "SessionTitle", async () =>
        {
            var identity = await D.TryHelpAsync("SessionTitle");
            if (identity is not null && identity.StartsWith("session:", StringComparison.Ordinal) && Guid.TryParse(identity[8..], out _))
            { liveSession = identity[8..]; return true; }
            var status = await D.TryHelpAsync("TaskStatus");
            if (!capturedRunning && status == "stage:capturing")
            { await Shot("task-real-recording.png", "real-controlled-etw-capture"); capturedRunning = true; }
            return false;
        }, TimeSpan.FromSeconds(180));
        Require(capturedRunning, "actual running stage screenshot captured");
        var summary = await Cli(liveDatabase!, "inspect", "--summary", "--json", "--", liveSession!);
        Require(summary.GetProperty("id").GetString() == liveSession && summary.GetProperty("capture_state").GetString() == "finished", "exact GUI UUID is finished and read back");
        Require(summary.GetProperty("exit_code").GetInt32() == 0, "installer exit separately measured");
        var list = await Cli(liveDatabase!, "list", "--details", "--json"); Require(list.GetArrayLength() == 4, "duplicate start created exactly one session");
        foreach (var earlier in earlierLiveRecords)
            Require(JsonElement.DeepEquals(earlier.Value, await Cli(liveDatabase!, "inspect", "--summary", "--json", "--", earlier.Key)), "older exact records remain unchanged after saving");
        Require(summary.GetProperty("watch_roots").GetArrayLength() == 0 && summary.GetProperty("backend").TryGetProperty("follow_installer", out _), "default no-watch follow mode persisted");
        await D.InvokeAsync("OverviewTab"); await Shot("real-saved-overview.png", "real-controlled-etw-saved");
        await D.InvokeAsync("CurrentTask"); await D.ExpandAsync("TaskDetails");
        var task = await D.TextAsync("TaskDiagnostics");
        Require(task.Contains("会话 ID：" + liveSession, StringComparison.Ordinal) && task.Contains("记录已保存", StringComparison.Ordinal), "task result independently reports exact saved UUID");
        Require(task.Contains("采集停止：True；数据保存：True", StringComparison.Ordinal), "task confirms capture stopped and data saved as separate facts");
        var collector = Regex.Match(task, @"采集进程 PID：(\d+)；退出码：(-?\d+)");
        Require(collector.Success && int.Parse(collector.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0, "collector process identity and terminal exit measured");
        collectorExit = int.Parse(collector.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        Require(collectorExit == 0, "collector exit measured separately as zero");
        await D.InvokeAsync("OpenCapturedSession");
        await D.UntilAsync("return_exact_saved", "SessionTitle", async () => await D.TryHelpAsync("SessionTitle") == "session:" + liveSession);
    }
    private async Task VerifyLiveOracle()
    {
        var capture = await Cli(liveDatabase!, "inspect", "--json", "--", liveSession!);
        var backend = capture.GetProperty("backend");
        Require(backend.GetProperty("etw_process").GetString() == "active" && backend.GetProperty("etw_file").GetString() == "active", "real process and file ETW active");
        foreach (var name in new[] { "etw_events_lost", "etw_buffers_lost", "dropped_events", "context_losses", "decode_errors" }) Require(backend.GetProperty(name).GetUInt64() == 0, "zero measured live source loss: " + name);
        var pipeline = backend.GetProperty("pipeline"); var handoff = backend.GetProperty("handoff"); var stream = backend.GetProperty("stream");
        foreach (var name in new[] { "queue_overflow", "enqueue_disconnected", "queue_pending" }) Require(pipeline.GetProperty(name).GetUInt64() == 0, "receiver loss or pending record: " + name);
        foreach (var name in new[] { "overflow", "oversized", "disconnected", "serialize_failed", "pending" }) Require(handoff.GetProperty(name).GetUInt64() == 0, "handoff loss or pending record: " + name);
        Require(!capture.GetProperty("stats").GetProperty("drain_timed_out").GetBoolean(), "bounded drain completed");
        var stats = capture.GetProperty("stats");
        liveAccounting = JsonSerializer.SerializeToElement(new
        {
            events_received = stats.GetProperty("events_received"), events_retained = stats.GetProperty("events_retained"),
            etw_events_lost = backend.GetProperty("etw_events_lost"), etw_buffers_lost = backend.GetProperty("etw_buffers_lost"),
            dropped_events = backend.GetProperty("dropped_events"), context_losses = backend.GetProperty("context_losses"), decode_errors = backend.GetProperty("decode_errors"),
            receiver = NumericFields(pipeline, "queue_overflow", "enqueue_disconnected", "queue_pending", "deliberately_filtered", "unsupported_records"),
            handoff = NumericFields(handoff, "overflow", "oversized", "disconnected", "serialize_failed", "pending"),
            raw_storage = NumericFields(stream, "persisted", "failed", "quota_dropped", "quota_bytes", "committed_bytes"),
            drain_timed_out = stats.GetProperty("drain_timed_out")
        });
        // Full capture and paths are private. Only the independent oracle's reviewed aggregates are published.
        var capturePath = Path.Combine(options.PrivateOutput, "live-capture.json");
        await File.WriteAllTextAsync(capturePath, JsonSerializer.Serialize(new { schema_version = 3, kind = "inspect", data = capture }));
        await unrelated!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await unrelatedOutput!; await unrelatedError!;
        var targets = new[]
        {
            new { label = "parent", role = "followparent", path = Path.Combine(liveRoot!, "A", "parent-target.txt"), content = "Contain follow experiment: exact parent write\n" },
            new { label = "child", role = "followchild", path = Path.Combine(liveRoot!, "B", "child-target.txt"), content = "Contain follow experiment: exact direct child write\n" },
            new { label = "unrelated", role = "followunrelated", path = Path.Combine(liveRoot!, "A", "unrelated-target.txt"), content = "Contain follow experiment: independent writer, never installer\n" }
        };
        var oracleDir = Path.Combine(options.PrivateOutput, "native-oracle"); Directory.CreateDirectory(oracleDir);
        var expectedPath = Path.Combine(oracleDir, "expected.json");
        await File.WriteAllTextAsync(expectedPath, JsonSerializer.Serialize(new
        {
            source_sha = options.Source, capture_exit_code = collectorExit, independent_exit_code = unrelated.ExitCode, directory_b_existed_before = false,
            product_watch_arguments = Array.Empty<string>(), excluded_io_paths = new[] { liveDatabase }, excluded_io_directories = new[] { options.PrivateOutput },
            targets = targets.Select(t => new { t.label, t.role, t.path, content_sha256 = HashBytes(Encoding.UTF8.GetBytes(t.content)), actual_content_sha256 = File.Exists(t.path) ? Hash(t.path) : null })
        }));
        var explanations = Path.Combine(oracleDir, "explanations"); Directory.CreateDirectory(explanations);
        foreach (var target in targets)
        {
            var candidates = capture.GetProperty("events").EnumerateArray().Where(e => e.GetProperty("resource").GetString()?.Equals(target.path, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            Require(candidates.Length <= 32, "bounded independent target selection");
            foreach (var item in candidates)
            {
                var id = item.GetProperty("id").GetString()!;
                var result = await Cli(liveDatabase!, "explain", "--json", "--", id);
                await File.WriteAllTextAsync(Path.Combine(explanations, id + ".json"), JsonSerializer.Serialize(new { schema_version = 3, kind = "explain", data = result }));
            }
        }
        var summaryPath = Path.Combine(oracleDir, "summary.json");
        await RunProcess("python", [Path.GetFullPath("scripts/verify-follow-installer-experiment.py"), "--capture", capturePath, "--truth", truthRoot!, "--expected", expectedPath, "--explanations", explanations, "--output", summaryPath, "--source", options.Source], TimeSpan.FromSeconds(30));
        using var oracle = JsonDocument.Parse(await File.ReadAllTextAsync(summaryPath)); followOracle = oracle.RootElement.Clone();
        Require(followOracle.Value.GetProperty("accepted").GetBoolean(), "unchanged independent oracle accepts parent child unrelated and resource gates");
        liveEvent = followOracle.Value.GetProperty("observations").EnumerateArray().Single(row => row.GetProperty("label").GetString() == "child").GetProperty("event_id").GetString();
        actualEtw = true;
    }
    private async Task LiveExplainRestart()
    {
        await Events(); await Search("child-target.txt", null, null); await Explain(liveEvent!, liveSession!);
        await CopyEvent(liveEvent!);
        await Shot("real-event-explanation.png", "real-controlled-etw-saved");
        await SelectTimelineEvent(liveEvent!);
        var report = Path.Combine(options.PrivateOutput, "real-current-page.html"); await D.ChooseFileAsync("ExportPage", report, save: true);
        await D.UntilAsync("real_report", "ExportPage", async () => File.Exists(report) && await D.EnabledAsync("ExportPage"));
        var html = WebUtility.HtmlDecode(await File.ReadAllTextAsync(report)); Require(html.Contains(liveSession!, StringComparison.Ordinal) && html.Contains(liveEvent!, StringComparison.Ordinal), "real report exact session and selected event");
        var firstPid = D.ProcessId; await D.CloseAsync(); PreserveLaunch(); D.Dispose(); desktop = null;
        Start(currentRoot!, "live-different-cold-working"); await D.ReadyAsync(); Require(D.ProcessId != firstPid, "real cold start new process and changed cwd");
        await OpenRecord(liveSession!); await Events(); await Search("child-target.txt", null, null); await Explain(liveEvent!, liveSession!);
        await CopyEvent(liveEvent!);
        await Shot("real-cold-restart.png", "real-controlled-etw-saved-cold-restart");
        await D.CloseAsync(); coldRestart = true;
    }
    private Task CheckUnchanged()
    {
        foreach (var pair in hashes) Require(Hash(pair.Key) == pair.Value, "original synthetic and package bytes remain unchanged");
        return Task.CompletedTask;
    }
    private async Task SelectTimelineEvent(string id)
    {
        var full = await Cli(liveDatabase!, "inspect", "--json", "--", liveSession!);
        var ids = full.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToArray();
        var index = Array.IndexOf(ids, id); Require(index >= 0, "target event exists in persisted full timeline");
        await D.InvokeAsync("ClearSearch");
        await D.UntilAsync("real_timeline_first_page", "EventList", async () => (await D.RowIdsAsync("EventList", "event:")).FirstOrDefault() == ids[0]);
        for (var page = 0; page < index / 50; page++)
        {
            await D.InvokeAsync("NextEvents"); var first = ids[(page + 1) * 50];
            await D.UntilAsync("real_timeline_page", "EventList", async () => (await D.RowIdsAsync("EventList", "event:")).FirstOrDefault() == first);
        }
        await Explain(id, liveSession!);
    }
    private async Task OpenRecord(string id)
    {
        await D.UntilAsync("find_exact_record", "record:" + id, () => D.ExistsAsync("record:" + id));
        await D.SelectAsync("record:" + id);
        await D.UntilAsync("load_exact_record", "SessionTitle", async () => await D.TryHelpAsync("SessionTitle") == "session:" + id);
    }
    private async Task Events()
    {
        await D.InvokeAsync("EventsTab"); await D.UntilAsync("events_visible", "SearchText", () => D.VisibleAsync("SearchText"));
    }
    private async Task Search(string query, int? total, int? page)
    {
        await D.SetTextAsync("SearchText", query); await D.InvokeAsync("SearchEvents");
        await D.UntilAsync("search_results", "EventList", async () =>
        {
            var rows = await D.RowIdsAsync("EventList", "event:");
            if (total is null) return rows.Length > 0 && rows.Contains(liveEvent);
            var label = await D.TextAsync("EventPageLabel");
            return rows.Length == Math.Min(50, total.Value) && Regex.IsMatch(label, @"(?<!\d)" + total.Value + @"(?!\d)");
        });
    }
    private async Task Explain(string eventId, string session)
    {
        await D.SelectAsync("event:" + eventId);
        await D.UntilAsync("exact_explanation", "EventDetails", async () => await D.TryHelpAsync("EventDetails") == "event:" + eventId);
        await D.ExpandAsync("RawExplanationExpander");
        var raw = await D.TextAsync("EventRawJson"); using var parsed = JsonDocument.Parse(raw);
        var data = parsed.RootElement.GetProperty("data");
        Require(data.GetProperty("app_id").GetString() == session && data.GetProperty("event").GetProperty("id").GetString() == eventId, "raw explanation exact session and event");
        var expectedEvent = (await Cli(currentRoot == null ? "" : Database(currentRoot), "explain", "--json", "--", eventId)).GetProperty("event");
        Require(JsonElement.DeepEquals(data.GetProperty("event"), expectedEvent), "UI explanation equals independent persisted CLI event");
        await D.CollapseAsync("RawExplanationExpander");
    }
    private async Task<JsonElement> Cli(string database, params string[] args)
    {
        var text = await RunProcess(Core, ["--read-only", "--db", database, .. args], TimeSpan.FromSeconds(90));
        using var doc = JsonDocument.Parse(text); return doc.RootElement.GetProperty("data").Clone();
    }
    private static async Task<string> RunProcess(string file, string[] args, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Verification subprocess failed to start.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout); var text = await output; var errors = await error;
        if (process.ExitCode != 0) throw new IOException("Verification subprocess exit=" + process.ExitCode + "; " + errors);
        if (Encoding.UTF8.GetByteCount(text) > 64 * 1024 * 1024) throw new IOException("Verification output exceeded established bound.");
        return text;
    }
    private void Start(string root, string working, IReadOnlyDictionary<string, string>? environment = null) => desktop = new(options.Package, Path.Combine(root, working), root, environment);
    private string Root(string label)
    {
        var path = Path.Combine(options.PrivateOutput, "records-" + label);
        if (Directory.Exists(path)) throw new IOException("Isolated scenario already exists; attempts are not overwritten.");
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, ".contain-records-test-root"), "Contain isolated record library test data"); return path;
    }
    private void Seed(string root, string filename, bool prepareOutcomes = false)
    {
        var db = Database(root); Directory.CreateDirectory(Path.GetDirectoryName(db)!); File.Copy(Path.Combine(options.Fixtures, filename), db, false);
        if (prepareOutcomes) SyntheticSql.PrepareOutcomes(root, db);
        if (!root.EndsWith("records-live", StringComparison.Ordinal)) Track(db);
    }
    private static string Database(string root) => Path.Combine(root, "Contain", "Records", "contain.db");
    private DesktopDriver D => desktop ?? throw new InvalidOperationException("Required packaged desktop is not running.");
    private async Task CopyEvent(string id)
    {
        await DesktopDriver.SetClipboardAsync("SYNTHETIC pending explicit event copy");
        await D.InvokeAsync("CopyEventId");
        await D.UntilAsync("clipboard_exact_event", "CopyEventId", async () => await DesktopDriver.ClipboardAsync() == id, TimeSpan.FromSeconds(5));
        Require(await DesktopDriver.ClipboardAsync() == id, "copied ID equals currently explained original event");
    }
    private void PreserveLaunch()
    {
        if (desktop is null) return;
        launches.Add(new { process_id = desktop.ProcessId, probes = desktop.Probes.ToArray(), actions = desktop.Actions.ToArray(), last_stage = desktop.Stage, last_control = desktop.LastControl,
            last_error_type = desktop.LastErrorType, process_exited = desktop.HasExited, normal_close = desktop.CloseObservation });
    }
    private async Task Shot(string name, string kind) => screenshots.Add(await D.CaptureAsync(Path.Combine(options.Output, "screenshots"), name, kind));
    private void Track(string path) { Require(File.Exists(path), "identity file exists"); hashes.TryAdd(path, Hash(path)); }
    private void Require(bool value, string reason) { if (!value) throw new AssertionException(reason); assertions++; }
    private static string Hash(string path) => HashBytes(File.ReadAllBytes(path));
    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string? HashIfPresent(string path) => File.Exists(path) ? Hash(path) : null;
    private static string[] SafeCallSites(Exception error) => (new StackTrace(error, false).GetFrames() ?? [])
        .Select(frame => frame.GetMethod()).Where(method => method?.DeclaringType?.Namespace == "Contain.Desktop.Tests")
        .Select(method => method!.DeclaringType!.Name + "." + method.Name).Take(8).ToArray();
    private static Dictionary<string, ulong?> NumericFields(JsonElement value, params string[] names) => names.ToDictionary(name => name,
        name => value.TryGetProperty(name, out var number) && number.TryGetUInt64(out var result) ? (ulong?)result : null);
    private Task Write(string name, object value) => File.WriteAllTextAsync(Path.Combine(options.Output, name), JsonSerializer.Serialize(value,
        new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
}
