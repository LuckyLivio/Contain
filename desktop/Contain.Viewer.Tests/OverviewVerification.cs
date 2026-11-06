using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

/// <summary>Read-only result views on isolated, explicitly synthetic persisted records.</summary>
internal sealed class OverviewVerification(Options options, bool viewerChecksPassed)
{
    private static readonly string[] Plan =
    [
        "frozen_overview_package_identity", "persisted_three_outcomes", "missing_statistics_distinct_from_zero",
        "same_name_uuid_switch_and_details_navigation", "external_legacy_and_default_library",
        "query_failure_clears_previous_summary", "packaged_overview_three_outcomes",
        "packaged_overview_cold_restart_exact_session_event", "overview_expand_resize_actions_and_long_text",
        "overview_databases_unchanged"
    ];
    private static readonly string[] Sessions = ["synthetic-finished", "synthetic-historical", "synthetic-interrupted"];
    private readonly CoreClient client = new(Path.Combine(options.Package, "contain.exe"));
    private readonly List<CheckResult> checks = [];
    private readonly List<ImageResult> screenshots = [];
    private readonly StringBuilder errors = new();
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CoreResponse> summaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CoreResponse> diffs = new(StringComparer.Ordinal);
    private int assertions;
    private bool actualExecutable;
    private bool restarted;
    private int? firstPid;
    private int? restartPid;
    private int? firstExit;
    private uint? actualDpi;
    private string? library;
    private string? eventId;
    private string? initialCards;
    private const string SameName = "SYNTHETIC 同名安装记录";
    private const string LongRoot = @"C:\synthetic\观察目录 中文 & [x]\第一层很长的目录名称\第二层很长的目录名称\第三层很长的目录名称\第四层完整路径末尾";
    private static readonly string LongError = "SYNTHETIC：受控记录来源错误。" + string.Concat(Enumerable.Repeat("这是用于检查换行及完整查看的较长中文诊断；不是真实系统故障。", 12)) + "完整诊断末尾。";

    internal static int RunStandalone(string package, string fixtures, string output)
    {
        Directory.CreateDirectory(output);
        using var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetFullPath(package), "BUILD.json")));
        var source = build.RootElement.GetProperty("source_sha").GetString();
        if (source is null || source.Length != 40 || !source.All(Uri.IsHexDigit))
            throw new InvalidDataException("The overview preflight requires a frozen package source SHA.");
        var options = new Options(Path.GetFullPath(package), Path.GetFullPath(fixtures), Path.GetFullPath(output),
            Path.Combine(Path.GetFullPath(output), "private"), source, null);
        var passed = false;
        using var owner = new Form { Text = "Contain isolated overview checks", ShowInTaskbar = false };
        owner.Shown += async (_, _) => { try { passed = await new OverviewVerification(options, true).RunAsync(owner); } finally { owner.Close(); } };
        Application.Run(owner); return passed ? 0 : 1;
    }

    internal async Task<bool> RunAsync(Form owner)
    {
        Directory.CreateDirectory(options.Output); Directory.CreateDirectory(options.PrivateOutput);
        var root = TestRecordIsolation.Create("overview");
        using var isolation = TestRecordIsolation.Use(root);
        try
        {
            await Check(Plan[0], async () =>
            {
                Require(OperatingSystem.IsWindows(), "native Windows execution required");
                Require(Hash(typeof(ViewerForm).Assembly.Location) == Hash(Path.Combine(options.Package, "Contain.Viewer.dll")), "tested and packaged assembly bytes match");
                using var build = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.Package, "BUILD.json")));
                Require(build.RootElement.GetProperty("version").GetString() == ProductVersion.Version, "overview and package version match");
                Require(build.RootElement.GetProperty("source_sha").GetString() == options.Source, "overview source equals frozen package");
                library = PersonalRecords.ForCurrentUser().DatabasePath;
                Directory.CreateDirectory(Path.GetDirectoryName(library)!);
                var original = Path.Combine(options.Package, "samples", "viewer-demo.db");
                hashes.Add(original, Hash(original));
                File.Copy(original, library, false);
                // Explicit synthetic copies only. Production storage/CLI remain untouched.
                SyntheticSql.Execute(library, "UPDATE applications SET name='" + SameName + "';"
                    + "UPDATE installation_sessions SET exit_code=1603 WHERE id='synthetic-historical';"
                    + "UPDATE capture_runs SET error='" + LongError + "' WHERE session_id='synthetic-interrupted';"
                    + "UPDATE installation_sessions SET watch_roots_json='" + JsonSerializer.Serialize(new[] { LongRoot }).Replace("'", "''", StringComparison.Ordinal) + "';");
                hashes.Add(library, Hash(library));
                foreach (var id in Sessions)
                {
                    summaries.Add(id, await client.SummaryAsync(library, id));
                    diffs.Add(id, await client.DiffAsync(library, id));
                }
            });
            await Check(Plan[1], async () =>
            {
                foreach (var id in Sessions)
                {
                    var summary = summaries[id].Data;
                    var model = OverviewPresentation.Build(summary, diffs[id].Data);
                    Require(summary.GetProperty("name").GetString() == SameName, "same names retained with distinct IDs");
                    Require(model.RecordNote.Contains("读取", StringComparison.Ordinal), "saved record read separately from completion");
                    Require(model.Scope.Contains(LongRoot, StringComparison.Ordinal), "complete persisted long scope retained");
                    if (id == Sessions[2])
                    {
                        Require(model.IsUnfinished && model.Record.Contains("未", StringComparison.Ordinal), "partial session is unfinished");
                        Require(model.Installer.Contains("未", StringComparison.Ordinal), "missing installer exit is unknown");
                        Require(model.Coverage.Contains("不完整", StringComparison.Ordinal), "Incomplete explained");
                        Require(!model.Record.Contains("正在", StringComparison.Ordinal), "historical finalizing cannot assert live process");
                    }
                    else
                    {
                        var code = id == Sessions[0] ? "0" : "1603";
                        Require(model.Installer == "已记录退出码 " + code, "exact persisted exit code shown");
                        Require(!model.IsUnfinished && model.Record.Contains("结束", StringComparison.Ordinal), "completed session is distinct from exit result");
                        Require(model.Coverage.Contains("受限", StringComparison.Ordinal), "Degraded explained");
                    }
                    var counts = diffs[id].Data;
                    var expectedCounts = $"文件 {counts.GetProperty("files").GetArrayLength()} 项 · 注册表值 {counts.GetProperty("registry").GetArrayLength()} 项\n其他配置 {counts.GetProperty("inventory").GetArrayLength()} 项";
                    Require(model.Changes == expectedCounts, "each labeled category equals its exact state-only backend array count");
                }
                await Task.CompletedTask;
            }, () => summaries.Count == 3);
            await Check(Plan[2], () =>
            {
                var original = summaries[Sessions[0]].Data;
                var changed = JsonNode.Parse(original.GetRawText())!.AsObject();
                changed["exit_code"] = null; changed["capture_state"] = null;
                changed["summary_counts"] = null; changed["quality"] = null; changed["registry_key"] = null;
                using var missing = JsonDocument.Parse(changed.ToJsonString());
                var model = OverviewPresentation.Build(missing.RootElement, diffs[Sessions[0]].Data);
                Require(model.Installer.Contains("未", StringComparison.Ordinal), "null exit cannot become zero");
                Require(model.IsUnfinished, "legacy missing capture state not promoted by finished_at");
                Require(model.Coverage.Contains("未", StringComparison.Ordinal) || model.Coverage.Contains("未知", StringComparison.Ordinal), "missing quality unavailable");
                var zero = OverviewPresentation.Build(summaries[Sessions[2]].Data, diffs[Sessions[2]].Data);
                Require(zero.Changes == "文件 0 项 · 注册表值 0 项\n其他配置 0 项", "actual empty persisted state arrays are measured zero in each category");
                var missingDiff = JsonNode.Parse(diffs[Sessions[2]].Data.GetRawText())!.AsObject(); missingDiff.Remove("files");
                using var unavailable = JsonDocument.Parse(missingDiff.ToJsonString());
                var unknownCounts = OverviewPresentation.Build(summaries[Sessions[2]].Data, unavailable.RootElement);
                Require(unknownCounts.Changes.Contains("文件 未提供", StringComparison.Ordinal), "missing state count cannot silently become measured zero");
                Require(summaries[Sessions[2]].Data.GetProperty("summary_counts").GetProperty("events_normalized").ValueKind == JsonValueKind.Null,
                    "missing source operation measurement remains null independently of zero state changes");
                return Task.CompletedTask;
            }, () => summaries.Count == 3);

            if (library is not null && summaries.Count == 3)
            {
                var dialogs = new TestDialogs();
                using var form = new ViewerForm(client, dialogs); form.Show(owner); await Idle(form);
                await Check(Plan[3], async () =>
                {
                    foreach (var id in Sessions)
                    {
                        await Select(form, id);
                        Require(C<Label>(form, "SessionTitle").AccessibleDescription == "session:" + id, "exact selected UUID bound to overview");
                        Require(C<Label>(form, "SessionTitle").Text == SameName, "identical name does not replace UUID identity");
                        C<Button>(form, "ViewChanges").PerformClick(); await Idle(form);
                        Require(C<TabControl>(form, "Tabs").SelectedIndex == 1, "changes entry selects existing current-session view");
                        var count = new[] { "files", "registry", "inventory" }.Sum(key => diffs[id].Data.GetProperty(key).GetArrayLength());
                        Require(C<DataGridView>(form, "Changes").Rows.Count == count, "current session state count equals summary backend data");
                        C<TabControl>(form, "Tabs").SelectedIndex = 0;
                        C<Button>(form, "ViewEvents").PerformClick(); await Idle(form);
                        Require(C<TabControl>(form, "Tabs").SelectedIndex == 2, "events entry selects current-session view");
                        Require(((JsonElement)C<DataGridView>(form, "Events").Rows[0].Tag!).GetProperty("id").GetString() == id + "-event-0", "current session event identity");
                        C<TabControl>(form, "Tabs").SelectedIndex = 0;
                        C<Button>(form, "ToggleTechnical").PerformClick();
                        Require(C<TextBox>(form, "Overview").Visible && C<TextBox>(form, "Overview").Text.Contains(id, StringComparison.Ordinal), "technical detail identifies selected UUID");
                        Require(C<TextBox>(form, "Overview").Text.Contains("ETW 丢失事件：" + (id == Sessions[2] ? "未提供" : "0"), StringComparison.Ordinal),
                            "technical detail distinguishes unmeasured source loss from measured zero");
                        Require(C<TextBox>(form, "Overview").Text.Contains("时间线观测：60", StringComparison.Ordinal), "timeline count remains its own measured denominator");
                        C<Button>(form, "ToggleTechnical").PerformClick();
                        Require(!C<TextBox>(form, "Overview").Visible, "technical detail collapses");
                    }
                });
                await Check(Plan[4], async () =>
                {
                    var old = Path.Combine(root, "external-historical.db");
                    File.Copy(Path.Combine(options.Fixtures, "historical.db"), old, false); hashes.Add(old, Hash(old));
                    dialogs.Database = old; C<Button>(form, "OpenDatabase").PerformClick(); await Idle(form);
                    await Select(form, Sessions[1]);
                    Require(C<Label>(form, "DatabasePath").Text.StartsWith("外部数据库（只读）", StringComparison.Ordinal), "manual existing external database remains readonly");
                    var externalSummary = await client.SummaryAsync(old, Sessions[1]);
                    var externalDiff = await client.DiffAsync(old, Sessions[1]);
                    Require(C<Label>(form, "OverviewInstaller").Text == OverviewPresentation.Build(externalSummary.Data, externalDiff.Data).Installer
                        && C<Label>(form, "OverviewInstaller").Text == "已记录退出码 0", "external same UUID uses its database's exact exit code, not personal 1603");
                    C<Button>(form, "MyRecords").PerformClick(); await Idle(form); await Select(form, Sessions[1]);
                    Require(C<Label>(form, "OverviewInstaller").Text == OverviewPresentation.Build(summaries[Sessions[1]].Data, diffs[Sessions[1]].Data).Installer
                        && C<Label>(form, "OverviewInstaller").Text == "已记录退出码 1603", "default same UUID restores its own exact persisted result");
                    Require(C<Label>(form, "DatabasePath").Text == "我的记录", "explicit personal library navigation");
                });
                await Check(Plan[5], async () =>
                {
                    await Select(form, Sessions[0]);
                    using (var denied = new FileStream(library, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        var grid = C<DataGridView>(form, "Sessions");
                        var row = grid.Rows.Cast<DataGridViewRow>().Single(row => ((JsonElement)row.Tag!).GetProperty("id").GetString() == Sessions[1]);
                        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0];
                        Require(C<Label>(form, "OverviewInstaller").Text.Length == 0, "loading clears prior installer result synchronously");
                        Require(C<Label>(form, "SessionTitle").AccessibleDescription is null, "loading clears prior session identity synchronously");
                        await Idle(form);
                        Require(C<Label>(form, "Status").Text.StartsWith("未完成", StringComparison.Ordinal), "query failure explicit");
                        Require(!C<Button>(form, "ViewChanges").Enabled && !C<Button>(form, "ViewEvents").Enabled, "failed selected record cannot navigate stale results");
                        Require(C<DataGridView>(form, "Events").Rows.Count == 0 && C<DataGridView>(form, "Changes").Rows.Count == 0, "failed query clears prior evidence grids");
                        Require(C<Label>(form, "SessionTitle").AccessibleDescription != "session:" + Sessions[0], "previous session identity not presented after failure");
                    }
                    await Select(form, Sessions[2]);
                    Require(C<Label>(form, "OverviewRecord").Text.Contains("未", StringComparison.Ordinal), "successful subsequent query recovers selected result");
                    // Force a generic read error after cancellation but before a newer
                    // operation version exists. A stale IOException must not replace
                    // the selected record with an error during the queued-selection gap.
                    var completion = new TaskCompletionSource();
                    var threwAfterCancellation = false;
                    Func<CancellationToken, Task> cancelledRead = async _ =>
                    { await completion.Task; threwAfterCancellation = true; throw new IOException("SYNTHETIC superseded read failure"); };
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    typeof(ViewerForm).GetMethod("StartOperation", flags)!.Invoke(form, [cancelledRead, true, false]);
                    var operationVersion = typeof(ViewerForm).GetField("_operationVersion", flags)!.GetValue(form);
                    var cancellation = (CancellationTokenSource)typeof(ViewerForm).GetField("_operationCancellation", flags)!.GetValue(form)!;
                    cancellation.Cancel(); completion.SetResult(); await form.PendingOperation;
                    Require(threwAfterCancellation && cancellation.IsCancellationRequested
                        && Equals(operationVersion, typeof(ViewerForm).GetField("_operationVersion", flags)!.GetValue(form)),
                        "generic failure occurs after cancellation with no newer operation version");
                    Require(!C<Label>(form, "Status").Text.Contains("SYNTHETIC superseded read failure", StringComparison.Ordinal),
                        "cancelled generic error cannot overwrite current status before next operation starts");
                    Require(C<Label>(form, "SessionTitle").AccessibleDescription == "session:" + Sessions[2],
                        "cancelled read failure preserves current persisted identity");
                });
                form.Close();

                var cwd = Path.Combine(root, "working-a"); Directory.CreateDirectory(cwd);
                await Check(Plan[6], async () =>
                {
                    using var desktop = new PackagedDesktop(options.Package, cwd); firstPid = desktop.ProcessId;
                    await desktop.ReadyAsync(); actualExecutable = true; actualDpi = await desktop.DeviceDpiAsync();
                    Require(actualDpi > 0, "actual packaged process reports Windows DPI");
                    await VerifyOptionalStatusSampling(desktop);
                    foreach (var (id, imageName) in Sessions.Zip(new[] { "success", "nonzero", "unfinished" }))
                    {
                        await SelectPackaged(desktop, id);
                        var expected = OverviewPresentation.Build(summaries[id].Data, diffs[id].Data);
                        Require(await desktop.TextAsync("OverviewInstaller") == expected.Installer, "actual packaged installer summary equals persisted result");
                        Require(await desktop.TextAsync("OverviewRecord") == expected.Record, "actual packaged session summary equals persisted result");
                        Require(await desktop.TextAsync("OverviewChanges") == expected.Changes, "actual packaged changes summary equals backend counts");
                        Require(await desktop.TextAsync("OverviewRegistryScope") == expected.RegistryScope, "actual registry scope bound to this saved record");
                        Require(await desktop.OnscreenAsync("OverviewRegistryScope"), "registry range visible separately from long file paths");
                        screenshots.Add(await desktop.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "overview-packaged-" + imageName + ".png"));
                    }
                    await SelectPackaged(desktop, Sessions[0]); initialCards = await Cards(desktop);
                    eventId = Sessions[0] + "-event-0";
                    await PersonalRecordsVerification.VerifyExactUi(desktop, Sessions[0], eventId);
                    await desktop.CloseAsync(); firstExit = desktop.ExitCode;
                    Require(desktop.HasExited && firstExit == 0, "first packaged process fully exits normally");
                });
                await Check(Plan[7], async () =>
                {
                    var differentCwd = Path.Combine(root, "working-b"); Directory.CreateDirectory(differentCwd);
                    using var desktop = new PackagedDesktop(options.Package, differentCwd); restartPid = desktop.ProcessId;
                    await desktop.ReadyAsync(); await SelectPackaged(desktop, Sessions[0]);
                    Require(await Cards(desktop) == initialCards, "cold restart overview equals same persisted session");
                    await PersonalRecordsVerification.VerifyExactUi(desktop, Sessions[0], eventId!);
                    Require(desktop.WorkingDirectory != cwd && restartPid != firstPid, "new process and new working directory");
                    await desktop.CloseAsync(); restarted = true;
                }, () => firstExit == 0 && eventId is not null);
                await Check(Plan[8], async () =>
                {
                    using var desktop = new PackagedDesktop(options.Package, cwd); await desktop.ReadyAsync();
                    await SelectPackaged(desktop, Sessions[2]);
                    Require((await desktop.TextAsync("OverviewScope")).Contains(LongRoot, StringComparison.Ordinal), "actual scope text contains complete long Chinese path");
                    await desktop.InvokeAsync("CopyOverviewScope");
                    Require(Clipboard.GetText().Contains(LongRoot, StringComparison.Ordinal), "actual copy retains complete scope");
                    Require(Clipboard.GetText().Contains(OverviewPresentation.Build(summaries[Sessions[2]].Data, diffs[Sessions[2]].Data).RegistryScope, StringComparison.Ordinal),
                        "scope copy includes separately visible registry range");
                    foreach (var size in new[] { new Size(1120, 780), new Size(1400, 980) })
                    {
                        await desktop.ResizeAsync(size.Width, size.Height);
                        foreach (var expanded in new[] { true, false })
                        {
                            await desktop.InvokeAsync("ToggleTechnical");
                            await PackagedDesktop.WaitAsync(async () => (await desktop.TextAsync("ToggleTechnical")) == (expanded ? "收起技术详情" : "技术详情"), TimeSpan.FromSeconds(10));
                            foreach (var action in new[] { "ViewChanges", "ViewEvents", "ToggleTechnical" })
                                Require(await desktop.OnscreenAsync(action), "main overview actions visible after expansion and resize");
                            if (expanded)
                            {
                                var detail = await desktop.TextAsync("Overview");
                                Require(detail.Contains(Sessions[2], StringComparison.Ordinal), "expanded full technical content retains current UUID");
                                Require(detail.Contains(LongError, StringComparison.Ordinal), "long persisted source error fully inspectable");
                            }
                        }
                    }
                    await desktop.InvokeAsync("ViewChanges");
                    Require((await desktop.TextAsync("ChangesPage")).Contains("全部", StringComparison.Ordinal), "actual changes entry exposes current state view");
                    await desktop.SelectTabAsync("记录概览"); await desktop.InvokeAsync("ViewEvents");
                    Require((await desktop.NamesAsync("Events")).Any(name => name.EndsWith("event:" + Sessions[2] + "-event-0", StringComparison.Ordinal)), "actual event entry belongs to current UUID");
                    await desktop.CloseAsync();
                    // The accessibility Invoke path above is a distinct regression.
                    // Also validate the ordinary first mouse click in a new process.
                    using var mouse = new PackagedDesktop(options.Package, cwd); await mouse.ReadyAsync();
                    await mouse.ClickIdentityAsync("session:" + Sessions[2]);
                    await PackagedDesktop.WaitAsync(async () => (await mouse.DescriptionAsync("SessionTitle")) == "session:" + Sessions[2], TimeSpan.FromSeconds(30));
                    await PackagedDesktop.WaitAsync(() => mouse.EnabledAsync("ViewChanges"), TimeSpan.FromSeconds(30));
                    var mouseModel = OverviewPresentation.Build(summaries[Sessions[2]].Data, diffs[Sessions[2]].Data);
                    Require(await mouse.TextAsync("OverviewInstaller") == mouseModel.Installer
                        && await mouse.TextAsync("OverviewRecord") == mouseModel.Record, "fresh native mouse click selects the intended nonfirst session");
                    await mouse.CloseAsync();
                });
            }
            await Check(Plan[9], () =>
            {
                Require(hashes.Count >= 3, "source, default copy and external copy fingerprinted");
                foreach (var pair in hashes) Require(Hash(pair.Key) == pair.Value, "all viewed DB bytes unchanged after fixture setup");
                return Task.CompletedTask;
            });
        }
        catch (Exception error) { errors.AppendLine(error.ToString()); }
        foreach (var name in Plan.Where(name => checks.All(check => check.Name != name))) checks.Add(new(name, "not_run", "overview_prerequisite_unavailable"));
        var accepted = viewerChecksPassed && checks.Count == Plan.Length && checks.All(check => check.Status == "passed");
        File.WriteAllText(Path.Combine(options.PrivateOutput, "overview-details.log"), errors.ToString());
        File.WriteAllText(Path.Combine(options.Output, "overview-summary.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1, protocol_revision = "contain-desktop-overview-verification-1", source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(), architecture = RuntimeInformation.OSArchitecture.ToString(),
            windows_version = Environment.OSVersion.Version.ToString(), device_dpi = actualDpi,
            synthetic = true, real_etw_executed = false, real_user_records_accessed = false,
            actual_packaged_executable = actualExecutable, cold_restart_verified = restarted,
            first_process_id = firstPid, restarted_process_id = restartPid, first_process_exit_code = firstExit,
            initial_session_id = Sessions[0], restart_session_id = restarted ? Sessions[0] : null,
            initial_event_id = eventId, restart_event_id = restarted ? eventId : null, working_directory_changed = restarted,
            package_manifest_sha256 = Hash(Path.Combine(options.Package, "BUILD.json")),
            viewer_assembly_sha256 = Hash(typeof(ViewerForm).Assembly.Location), viewer_launcher_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.exe")),
            contain_sha256 = Hash(client.ExecutablePath), planned_checks = Plan, checks, assertions, screenshots,
            passed = checks.Count(check => check.Status == "passed"), failed = checks.Count(check => check.Status == "failed"),
            skipped = 0, not_run = checks.Count(check => check.Status == "not_run"), completed = checks.All(check => check.Status != "not_run"), accepted, retries = 0,
            scope = "Actual packaged EXE and production controls using guarded isolated synthetic persisted records. No capture; native DPI distinct from separate simulated layout checks."
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return accepted;
    }

    private async Task VerifyOptionalStatusSampling(PackagedDesktop desktop)
    {
        Require(await desktop.TryTextAsync("InstallStatus") is null,
            "optional status sample is absent when the actual saved-record viewer has no install window");
        var requiredMissing = false;
        try { await desktop.TextAsync("InstallStatus"); }
        catch (PackagedDesktop.RequiredProductControlMissingException) { requiredMissing = true; }
        Require(requiredMissing, "required missing control still fails with a safe fixed failure classification");
        var expected = await desktop.TextAsync("NewInstall");
        Require(!string.IsNullOrWhiteSpace(expected) && await desktop.TryTextAsync("NewInstall") == expected,
            "optional read retains actual existing packaged-control text");
        Require(PackagedDesktop.TryText(() => throw new System.Windows.Automation.ElementNotAvailableException()) is null,
            "a status element disappearing during the atomic UIA operation produces only an absent sample");
        var unexpected = new InvalidOperationException("SYNTHETIC non-lifetime UIA failure");
        var propagated = false;
        try { PackagedDesktop.TryText(() => throw unexpected); }
        catch (InvalidOperationException error) when (ReferenceEquals(error, unexpected)) { propagated = true; }
        Require(propagated, "optional sampling does not suppress ordinary InvalidOperationException failures");
    }

    private async Task Select(ViewerForm form, string id)
    {
        C<TabControl>(form, "Tabs").SelectedIndex = 0; await Idle(form);
        var grid = C<DataGridView>(form, "Sessions");
        var row = grid.Rows.Cast<DataGridViewRow>().Single(row => ((JsonElement)row.Tag!).GetProperty("id").GetString() == id);
        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0]; await Idle(form);
    }
    private async Task SelectPackaged(PackagedDesktop desktop, string id)
    {
        try
        {
            await desktop.SelectTabAsync("记录概览");
            await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(30));
            await desktop.SelectIdentityAsync("session:" + id);
            await PackagedDesktop.WaitAsync(async () => (await desktop.DescriptionAsync("SessionTitle")) == "session:" + id, TimeSpan.FromSeconds(30));
            await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("ViewChanges"), TimeSpan.FromSeconds(30));
        }
        catch
        {
            // Preserve the actual failed selection; never retry or switch to a different row.
            var failure = "overview-selection-" + desktop.ProcessId + "-" + id;
            try
            {
                File.WriteAllText(Path.Combine(options.PrivateOutput, failure + ".txt"),
                    await desktop.SelectionStateAsync("Sessions") + "\r\n" + await desktop.DumpAsync());
                await desktop.ScreenshotAsync(options.PrivateOutput, failure + ".png");
            }
            catch (Exception diagnostic) { errors.AppendLine("Selection diagnostic unavailable: " + diagnostic); }
            throw;
        }
    }
    private static async Task<string> Cards(PackagedDesktop desktop) => string.Join("\n", await Task.WhenAll(
        new[] { "OverviewInstaller", "OverviewRecord", "OverviewScope", "OverviewRegistryScope", "OverviewCoverage", "OverviewChanges" }.Select(desktop.TextAsync)));
    private static T C<T>(Control parent, string name) where T : Control => parent.Controls.Find(name, true).Single() as T ?? throw new InvalidOperationException(name);
    private static async Task Idle(ViewerForm form) { await Task.Delay(40); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(90)); await Task.Delay(80); }
    private async Task Check(string name, Func<Task> action, Func<bool>? prerequisite = null)
    {
        if (prerequisite is not null && !prerequisite()) { checks.Add(new(name, "not_run", "overview_prerequisite_unavailable")); return; }
        try { await action(); checks.Add(new(name, "passed")); }
        catch (Exception error) { checks.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : "overview verification failed")); errors.AppendLine(name).AppendLine(error.ToString()); }
    }
    private void Require(bool condition, string reason) { if (!condition) throw new TestFailureException(reason); assertions++; }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static class SyntheticSql
    {
        internal static void Execute(string database, string sql)
        {
            var root = Path.GetFullPath(TestRecordIsolation.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(database).StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(database))
                throw new InvalidOperationException("Synthetic SQLite mutations must remain inside marked test data.");
            if (sqlite3_open_v2(database, out var connection, 2, IntPtr.Zero) != 0) throw new IOException("Could not open synthetic fixture copy.");
            try
            {
                var result = sqlite3_exec(connection, sql, IntPtr.Zero, IntPtr.Zero, out var error);
                try { if (result != 0) throw new IOException("Synthetic fixture setup failed: " + Marshal.PtrToStringUTF8(error)); }
                finally { if (error != IntPtr.Zero) sqlite3_free(error); }
            }
            finally { sqlite3_close(connection); }
        }
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, out IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);
    }
}
