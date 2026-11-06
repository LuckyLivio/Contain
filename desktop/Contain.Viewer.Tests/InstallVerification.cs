using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

/// <summary>One bounded capture, started exclusively by the production form's Start button.</summary>
internal sealed class InstallVerification(Options options, bool viewerChecksPassed)
{
    private static readonly string[] Plan =
    [
        "install_protocol_and_arguments", "install_configuration_without_launch",
        "gui_real_etw_start_and_duplicate_guard", "gui_runtime_stages_and_close_guard",
        "gui_exact_session_autoopen", "gui_real_source_event_explain_and_export"
    ];
    private readonly List<CheckResult> checks = [];
    private readonly List<CheckResult> interactionChecks = [];
    private readonly List<CheckResult> offlineChecks = [];
    private readonly List<ImageResult> screenshots = [];
    private readonly List<string> observedStages = [];
    private readonly StringBuilder privateLog = new();
    private readonly TestDialogs viewerDialogs = new();
    private readonly CoreClient client = new(Path.Combine(options.Package, "contain.exe"));
    private ViewerForm? viewer;
    private InstallStatus? finalStatus;
    private string? root;
    private string? truthRoot;
    private string? database;
    private string? session;
    private string? selectedEvent;
    private string? report;
    private bool publicReport;
    private JsonElement? sourceEvent;
    private JsonElement? accounting;
    private int sourceEventCount;
    private bool guiInitiated;
    private bool independentTruthVerified;
    private bool providersActive;
    private bool configured;
    private string recordName = "";
    private readonly Dictionary<string, JsonElement> earlierSummaries = [];
    private int assertions;
    private IDisposable? liveIsolation;
    private PackagedDesktop? actualDesktop;
    private string? initialWorkingDirectory;
    private int? actualFirstPid;
    private int? actualFirstExitCode;
    private int? actualRestartPid;
    private int? actualCollectorPid;
    private bool actualCapture;
    private bool defaultSaveWithoutPathInteraction;
    private bool coldRestartVerified;
    private string? restartedSession;
    private string? restartedEvent;
    private string? initialOutcomeText;
    private readonly List<ImageResult> firstUseScreenshots = [];
    private uint? firstUseDpi;
    private bool cancelPreservedSelection, configurationCreatedNoSession;
    private bool defaultFollow, noWatchArgument, followModePersisted;
    private JsonElement? followVerification;
    private string? copiedEvent;
    private Process? unrelatedProcess;
    private string? previousFixtureFollow;
    private bool keyboardFlowVerified, defaultNameMatchesExe, activeEntryPreserved, duplicateCreatedOne;
    private int savedScopeCount;
    private readonly Stopwatch progressClock = Stopwatch.StartNew();
    private readonly List<object> progressEntries = [];
    private string currentPhase = "setup";
    private string? previousFixtureRoot;
    private string? previousFixtureTruth;

    public async Task<bool> RunAsync()
    {
        previousFixtureFollow = Environment.GetEnvironmentVariable("CONTAIN_FIXTURE_FOLLOW");
        previousFixtureRoot = Environment.GetEnvironmentVariable("CONTAIN_FIXTURE_ROOT");
        previousFixtureTruth = Environment.GetEnvironmentVariable("CONTAIN_FIXTURE_TRUTH");
        try
        {
            await Check(Plan[0], TestProtocol);
            await Check(Plan[1], Configure, () => viewerChecksPassed && Passed(Plan[0]));
            // Offline read-only and protocol/configuration checks gate the single live attempt.
            await Check(Plan[2], Start, () => viewerChecksPassed && configured && Passed(Plan[0]) && Passed(Plan[1]));
            await Check(Plan[3], ObserveRuntime, () => guiInitiated);
            await Check(Plan[4], VerifyAutomaticAssociation, () => finalStatus is { Stage: "finished" });
            await Check(Plan[5], ExplainAndExport, () => Passed(Plan[4]));
        }
        catch (Exception error) { privateLog.AppendLine(error.ToString()); }
        finally
        {
            viewer?.Close(); viewer?.Dispose();
            if (actualDesktop is not null)
            {
                try { if (!actualDesktop.HasExited && session is not null) await actualDesktop.CloseAsync(); }
                catch (Exception error) { privateLog.AppendLine("Actual desktop retained: " + error); }
                actualDesktop.Dispose();
            }
            unrelatedProcess?.Dispose();
            Environment.SetEnvironmentVariable("CONTAIN_FIXTURE_FOLLOW", previousFixtureFollow);
            Environment.SetEnvironmentVariable("CONTAIN_FIXTURE_TRUTH", previousFixtureTruth);
            Environment.SetEnvironmentVariable("CONTAIN_FIXTURE_ROOT", previousFixtureRoot);
            WriteSummary();
            liveIsolation?.Dispose();
        }
        return checks.Count == Plan.Length && checks.All(check => check.Status == "passed")
            && interactionChecks.All(check => check.Status != "failed");
    }

    private async Task TestProtocol()
    {
        var db = Path.Combine(Path.GetTempPath(), "参数 中文 & $ ; [x].db");
        var id = Guid.NewGuid().ToString();
        var preparing = StatusDocument(db, id, 1, "preparing");
        Reject(() => InstallStatus.Parse("not JSON"));
        Reject(() => InstallStatus.Parse("{}"));
        Reject(() => InstallStatus.Parse(preparing + "\n{}"));
        Reject(() => InstallStatus.Parse(Change(preparing, node => node["data"]!["stage"] = "future_finished")));
        Reject(() => InstallStatus.Parse(Change(preparing, node => node["data"]!["stage"] = "finished")));
        Reject(() => InstallStatus.Parse(Change(preparing, node => node["data"]!["installer_creation_time"] = 134350000000000001L)));
        Reject(() => InstallStatus.Parse(Change(preparing, node => node["data"]!["sequence"] = -1)));
        var stream = new InstallStatusSequence(db);
        var first = stream.Accept(preparing);
        Require(!first.InstallerStarted && !first.CaptureFinished && !first.DataSaved, "preparing is not success");
        Reject(() => stream.Accept(StatusDocument(db, Guid.NewGuid().ToString(), 2, "capturing", true)));
        Reject(() => stream.Accept(StatusDocument(db + ".other", id, 2, "capturing", true)));
        Reject(() => stream.Accept(StatusDocument(db, id, 3, "finalizing", true, true, true)));
        var active = stream.Accept(StatusDocument(db, id, 2, "capturing", true));
        Require(active.InstallerStarted && !active.InstallerExited && !active.CaptureFinished, "capture and installer outcome are separate");
        var exited = stream.Accept(StatusDocument(db, id, 3, "capturing", true, true));
        Require(exited.InstallerExited && exited.InstallerExitCode == 0 && !exited.CaptureFinished && !exited.DataSaved,
            "installer exit zero cannot complete finalization");
        Reject(() => stream.Accept(StatusDocument(db, id, 4, "preparing")));
        var finalizing = stream.Accept(Change(StatusDocument(db, id, 4, "finalizing", true, true, true), node => node["data"]!["data_saved"] = false));
        Require(finalizing.CaptureFinished && !finalizing.DataSaved, "source stop and derived evidence save are separate");
        Require(stream.TryAccept(StatusDocument(db, id, 4, "finalizing", true, true, true), out _) == false,
            "duplicate progress does not replace accepted state");
        Reject(() => stream.Accept(Change(StatusDocument(db, id, 5, "finished", true, true, true), node => node["data"]!["data_saved"] = false)));
        var finished = stream.Accept(StatusDocument(db, id, 5, "finished", true, true, true));
        Require(finished.CaptureFinished && finished.DataSaved && finished.QualityLevel == "Degraded", "finished independently confirms persistence and quality");
        var nonzeroInstaller = finished with { InstallerExitCode = 1 };
        Require(new InstallRunResult(nonzeroInstaller, 0, null, "").Succeeded && nonzeroInstaller.InstallerExitCode == 1,
            "successful collection retains nonzero installer outcome separately and does not infer cancellation");
        var nonzeroText = InstallForm.FormatStatus(nonzeroInstaller, true);
        Require(nonzeroText.Contains("退出码 1（非零；具体原因由安装器决定）", StringComparison.Ordinal)
            && !nonzeroText.Contains("用户取消", StringComparison.Ordinal)
            && nonzeroText.Contains("记录：已完成；数据保存：已保存记录", StringComparison.Ordinal),
            "synthetic nonzero installer result remains distinct from saved complete collection and is not guessed as cancellation");
        var unfinishedText = InstallForm.FormatStatus(finalizing with { DataSaved = true }, false);
        Require(unfinishedText.Contains("失败 / 未完成", StringComparison.Ordinal)
            && unfinishedText.Contains("已保存证据（可能仍不完整）", StringComparison.Ordinal)
            && !unfinishedText.Contains("记录：已完成", StringComparison.Ordinal),
            "synthetic saved finalizing evidence is not formatted as a complete record");
        Require(InstallForm.FormatStatus(finished with { InstallerExitCode = null }, true).Contains("已退出，未提供退出码", StringComparison.Ordinal),
            "synthetic absent installer exit code remains unknown");
        Reject(() => stream.Accept(StatusDocument(db, id, 6, "finished", true, true, true)));
        Require(!new InstallRunResult(null, 0, null, "").Succeeded, "missing result never succeeds");
        Require(!new InstallRunResult(finalizing, 0, null, "").Succeeded, "process exit while finalizing never succeeds");
        Require(!new InstallRunResult(finished, 1, null, "").Succeeded, "collector nonzero cannot become capture success");
        Require(!new InstallRunResult(finished, 0, "invalid JSON", "").Succeeded, "protocol failure cannot become success");
        var failed = new InstallStatusSequence(db);
        failed.Accept(preparing);
        var failedSaved = failed.Accept(Change(StatusDocument(db, id, 2, "failed"), node => { node["data"]!["data_saved"] = true; node["data"]!["error"] = "SYNTHETIC injected failure"; }));
        Require(failedSaved.Stage == "failed" && failedSaved.DataSaved && !failedSaved.CaptureFinished, "failure preserves saved evidence without success");
        var scope = Path.Combine(Path.GetTempPath(), "contain-argument-test-中文 & [x]-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scope);
        var installer = Path.Combine(scope, "fixture 中文 & $ [x].exe");
        File.Copy(options.InstallFixture!, installer, false);
        var literalArgs = new[] { "spaces 中文", "\"literal quoted\"", "$ ; & | < > (x) [y]", "", "--flag=value" };
        var request = new InstallRequest(installer, "同名 \"quotes\" & $ ;", scope, db, literalArgs, "Software\\Contain\\参数 &", true);
        var args = InstallClient.BuildArguments(request).ToArray();
        Require(args.TakeLast(literalArgs.Length).SequenceEqual(literalArgs), "each installer argument preserves its exact literal boundary");
        Require(args.Contains(installer) && args.Contains("--name=" + request.Name) && args.Contains("--no-etw") && !args.Contains("--require-etw"), "literal paths, record name and explicitly selected mode");
        Reject(() => (request with { Database = Path.Combine(scope, "inside.db") }).Validate());
        var absentRoot = scope + "-absent";
        Reject(() => (request with { WatchRoot = absentRoot }).Validate());
        Require(!Directory.Exists(absentRoot), "watch validation never creates a software target directory");
        Reject(() => (request with { Arguments = ["bad\0argument"] }).Validate());
        Reject(() => (request with { Installer = installer + ".msi" }).Validate());
        await InstallDiagnosticsChecks.Run(Require);
    }

    private async Task Configure()
    {
        Phase("configure_isolated_follow_fixture");
        Require(File.Exists(options.InstallFixture), "controlled installer exists");
        Require(Hash(options.InstallFixture!) == Hash(Path.Combine(options.Package, "contain-test-installer.exe")), "exact frozen installer fixture");
        var name = "contain-demo-desktop-" + options.Source[..12] + "-" + Guid.NewGuid().ToString("N")[..8];
        root = Path.Combine(Path.GetTempPath(), name);
        truthRoot = Path.Combine(Path.GetTempPath(), name + "-truth");
        var personalRoot = TestRecordIsolation.Create("one-live-default-follow-record");
        liveIsolation = TestRecordIsolation.Use(personalRoot);
        database = PersonalRecords.ForCurrentUser().DatabasePath;
        Require(!Directory.Exists(root) && !Directory.Exists(truthRoot) && !File.Exists(database), "new disposable target and database");
        Directory.CreateDirectory(root); Directory.CreateDirectory(truthRoot); Directory.CreateDirectory(Path.Combine(root, "A"));
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        await File.WriteAllTextAsync(Path.Combine(truthRoot, ".contain-demo-marker"), "Contain test fixture");
        await File.WriteAllTextAsync(Path.Combine(root, ".contain-demo-marker"), "Contain test fixture");
        Require(!Directory.Exists(Path.Combine(root, "B")), "direct-child target does not exist before capture");
        File.Copy(Path.Combine(options.Package, "samples", "viewer-demo.db"), database, false);
        recordName = Path.GetFileNameWithoutExtension(options.InstallFixture!);
        Require(recordName == "contain-test-installer", "fixed native fixture filename oracle");
        RenameSyntheticHistory(database);
        var earlier = await client.ListAsync(database);
        foreach (var item in earlier.Data.EnumerateArray())
        {
            var id = Text(item, "id"); earlierSummaries.Add(id, (await client.SummaryAsync(database, id)).Data.Clone());
        }
        Require(earlierSummaries.Count == 3, "old and same-name records seeded before this launch");
        await VerifyOfflineFailure();
        Environment.SetEnvironmentVariable("CONTAIN_FIXTURE_TRUTH", truthRoot);
        Environment.SetEnvironmentVariable("CONTAIN_FIXTURE_ROOT", root);
        Environment.SetEnvironmentVariable("CONTAIN_FIXTURE_FOLLOW", "1");
        initialWorkingDirectory = Path.Combine(personalRoot, "initial-working-directory"); Directory.CreateDirectory(initialWorkingDirectory);
        actualDesktop = new PackagedDesktop(options.Package, initialWorkingDirectory); actualFirstPid = actualDesktop.ProcessId;
        actualDesktop.ConfigurationProgress = () => Progress("native_dialog");
        await actualDesktop.ReadyAsync();
        initialOutcomeText = await actualDesktop.TextAsync("CaptureOutcome");
        await PersonalRecordsVerification.VerifyExactUi(actualDesktop, "synthetic-interrupted", "synthetic-interrupted-event-2", 0);
        var originalDatabaseHash = Hash(database);
        Phase("native_initial_picker_cancel");
        await actualDesktop.ChooseInstallerAsync(null);
        await PackagedDesktop.WaitAsync(() => actualDesktop.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(10));
        Require(!await actualDesktop.ExistsAsync("StartRecording"), "cancelled initial picker returns without a configuration window");
        Require(await actualDesktop.DescriptionAsync("SessionTitle") == "session:synthetic-interrupted"
            && await actualDesktop.DescriptionAsync("EventDetails") == "event:synthetic-interrupted-event-2", "cancel preserves exact historical selection and explanation");
        Require(Hash(database) == originalDatabaseHash && !File.Exists(Path.Combine(root, ".follow-go")), "cancel launches no installer and changes no session");
        cancelPreservedSelection = true;
        Phase("native_initial_picker_select");
        await actualDesktop.ChooseInstallerAsync(options.InstallFixture!);
        await PackagedDesktop.WaitAsync(() => actualDesktop.ExistsAsync("StartRecording"), TimeSpan.FromSeconds(10));
        Require(await actualDesktop.FocusedAsync("StartRecording"), "EXE selection focuses the directly available Start action");
        Require(await actualDesktop.IsCheckedAsync("FollowInstallerMode") && !await actualDesktop.IsCheckedAsync("ManualWatchMode"), "ordinary page defaults to follow installer");
        Require(!await actualDesktop.IsCheckedAsync("AdvancedToggle") && await actualDesktop.EnabledAsync("StartRecording"), "one selected EXE can start with advanced settings collapsed");
        Require((await actualDesktop.NativeVisibleTextAsync("InstallerFileName")).Contains(Path.GetFileName(options.InstallFixture!), StringComparison.Ordinal), "selected filename is visible");
        await actualDesktop.ToggleAsync("ShowInstallerPath", true);
        Require(Normalize(await actualDesktop.TextAsync("InstallerPath")) == Normalize(options.InstallFixture!), "native picker selected the exact frozen EXE");
        await actualDesktop.ToggleAsync("ShowInstallerPath", false);
        await actualDesktop.ResizeControlWindowAsync("StartRecording", 740, 620);
        firstUseDpi = await actualDesktop.ControlWindowDpiAsync("StartRecording");
        await actualDesktop.TabToAsync("StartRecording"); keyboardFlowVerified = true;
        Require(await actualDesktop.FullyVisibleAsync("StartRecording"), "primary action is reachable in the actual compact viewport");
        firstUseScreenshots.Add(await actualDesktop.ScreenshotControlWindowAsync("StartRecording", Path.Combine(options.Output, "screenshots"), "install-follow-configured.png"));
        screenshots.AddRange(firstUseScreenshots);
        Require(actualDesktop.WatchPickerSelections == 0 && actualDesktop.AdvancedInteractions == 0
            && actualDesktop.EnvironmentCheckInteractions == 0 && actualDesktop.DatabasePathInteractions == 0, "ordinary follow flow makes no folder, advanced, Doctor or database selection");
        Require(Hash(database) == originalDatabaseHash && !File.Exists(Path.Combine(root, ".follow-go")), "configuration creates no session and launches no installer");
        defaultFollow = noWatchArgument = configurationCreatedNoSession = true;
        interactionChecks.Add(new("native_picker_navigation_and_cancel", "passed"));
        configured = true; Phase("ordinary_follow_configuration_ready");
    }

    private async Task Start()
    {
        Phase("ordinary_start_once");
        var independent = new ProcessStartInfo(options.InstallFixture!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--role", "follow-unrelated", "--root", root! }) independent.ArgumentList.Add(argument);
        unrelatedProcess = Process.Start(independent) ?? throw new IOException("Independent fixture did not start.");
        await PackagedDesktop.WaitAsync(() => Task.FromResult(File.Exists(Path.Combine(root!, ".follow-unrelated-ready"))), TimeSpan.FromSeconds(10));
        Require(actualDesktop is not null, "configured actual packaged process");
        Require(await actualDesktop!.EnabledAsync("StartRecording"), "actual start action available without an environment-check detour");
        await actualDesktop.TabToAsync("StartRecording");
        await actualDesktop.KeyboardActivateAsync("StartRecording", focus: false);
        guiInitiated = true;
        await PackagedDesktop.WaitAsync(async () => !await actualDesktop.EnabledAsync("StartRecording"), TimeSpan.FromSeconds(5));
        Require(!await actualDesktop.EnabledAsync("StartRecording"), "actual start disables before asynchronous work");
        await actualDesktop.ClickTwiceAsync("StartRecording");
        var duplicateRefused = false;
        try { await actualDesktop.InvokeAsync("StartRecording"); }
        catch (System.Windows.Automation.ElementNotEnabledException) { duplicateRefused = true; }
        Require(duplicateRefused || !await actualDesktop.EnabledAsync("StartRecording"), "actual disabled start refuses a second launch");
        await actualDesktop.InvokeAsync("NewInstall");
        Require(!await actualDesktop.EnabledAsync("StartRecording"), "main entry preserves the same active recording");
        Require(actualDesktop.InstallerPickerSelections == 1 && actualDesktop.InstallerPickerCancels == 1,
            "active task entry does not reopen the installer picker");
        activeEntryPreserved = true;
        actualCapture = true;
        defaultSaveWithoutPathInteraction = actualDesktop.DatabasePathInteractions == 0;
    }

    private async Task ObserveRuntime()
    {
        Phase("observe_one_recording");
        Require(actualDesktop is not null, "actual record process exists");
        var clock = Stopwatch.StartNew();
        var closeChecked = false;
        var runningScreenshot = false;
        while (clock.Elapsed < TimeSpan.FromMinutes(3))
        {
            Require(!actualDesktop!.HasExited, "actual product remains running until a result is shown");
            var selected = await actualDesktop.DescriptionAsync("SessionTitle");
            if (selected.StartsWith("session:", StringComparison.Ordinal)
                && Guid.TryParse(selected["session:".Length..], out _)) { session = selected["session:".Length..]; break; }
            if (!closeChecked)
            {
                await actualDesktop.RequestCloseAsync(); await Task.Delay(150);
                Require(!actualDesktop.HasExited && !await actualDesktop.EnabledAsync("StartRecording"), "actual main-window close preserves active task");
                closeChecked = true;
            }
            if (await actualDesktop.ExistsAsync("InstallStatus"))
            {
                var text = await actualDesktop.TextAsync("InstallStatus");
                foreach (var (caption, stage) in new[] { ("准备中", "preparing"), ("记录中", "capturing"), ("整理中", "finalizing") })
                    if (text.Contains(caption, StringComparison.Ordinal) && !observedStages.Contains(stage)) observedStages.Add(stage);
                if (text.Contains("记录中", StringComparison.Ordinal) && !runningScreenshot)
                { screenshots.Add(await actualDesktop.ScreenshotControlWindowAsync("InstallStatus", Path.Combine(options.Output, "screenshots"), "install-packaged-recording.png")); runningScreenshot = true; }
            }
            await Task.Delay(50);
        }
        Require(session is not null, "actual GUI returned an exact UUID; no name/latest lookup");
        Require(closeChecked, "active close gesture checked");
        await PackagedDesktop.WaitAsync(async () => await actualDesktop!.TextAsync("CaptureOutcome") != initialOutcomeText,
            TimeSpan.FromSeconds(30));
        await PackagedDesktop.WaitAsync(() => actualDesktop!.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(30));
        await actualDesktop!.InvokeAsync("NewInstall");
        await actualDesktop.InvokeAsync("TaskDetails");
        var taskDetails = await actualDesktop.TextAsync("InstallTaskDiagnostics");
        Require(taskDetails.Contains("记录：已完成；数据保存：已保存记录", StringComparison.Ordinal)
            && taskDetails.Contains("会话 ID：" + session, StringComparison.Ordinal), "actual GUI independently confirms complete save for the exact returned UUID");
        var collector = Regex.Match(taskDetails, @"采集进程 PID：(\d+)；退出码：(\d+)");
        Require(collector.Success && collector.Groups[2].Value == "0", "actual collector exit is independently confirmed zero in task details");
        actualCollectorPid = int.Parse(collector.Groups[1].Value, CultureInfo.InvariantCulture);
        var full = await ReadFullCapture();
        Require(Text(full, "capture_state") == "finished", "persisted exact session is finished independently of GUI text");
        var installer = full.GetProperty("processes").EnumerateArray().Single(item => Text(item, "confidence") == "Certain"
            && Normalize(Text(item, "image")) == Normalize(options.InstallFixture!));
        var backend = full.GetProperty("backend"); var quality = full.GetProperty("quality");
        finalStatus = new(session!, database!, 0, "finished", true, installer.GetProperty("pid").GetUInt32(), Text(installer, "creation_time"),
            true, full.GetProperty("exit_code").GetInt32(), true, true, true, Text(backend, "etw_process"), Text(backend, "etw_file"),
            Text(backend, "etw_registry"), Text(quality, "level"), quality.GetProperty("reasons").EnumerateArray().Select(item => item.GetString()!).ToArray(), null);
        Require(finalStatus.InstallerPid is > 0 && finalStatus.InstallerCreationTime is { Length: > 0 }, "stored managed installer identity retains PID plus exact birth");
        Require(finalStatus.InstallerExitCode == 0, "installer exit remains separate from collector and save outcome");
        Require(finalStatus.EtwProcess == "active" && finalStatus.EtwFile == "active" && finalStatus.EtwRegistry == "not_requested",
            "ordinary recording activates real process/file ETW without silently requesting registry observation or falling back");
        providersActive = true;
        screenshots.Add(await actualDesktop.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "install-packaged-result.png"));
        await actualDesktop.CloseAsync(); actualFirstExitCode = actualDesktop.ExitCode;
        Require(actualDesktop.HasExited, "actual recorded product process closes fully before reopening");
        viewer = new ViewerForm(client, viewerDialogs); viewer.Show(); await WaitIdle(viewer);
        await viewer.OpenCapturedSessionAsync(database!, session!); await WaitIdle(viewer);
    }

    private async Task VerifyAutomaticAssociation()
    {
        Require(viewer is not null && finalStatus is not null && database is not null, "actual result available");
        await WaitIdle(viewer!);
        session = finalStatus!.SessionId;
        Require(string.Equals(Path.GetFullPath(finalStatus.Database), Path.GetFullPath(database!), StringComparison.OrdinalIgnoreCase), "exact database from this launch");
        var sessions = C<DataGridView>(viewer!, "Sessions");
        Require(sessions.SelectedRows.Count == 1, "record automatically selected after result");
        Require(((JsonElement)sessions.SelectedRows[0].Tag!).GetProperty("id").GetString() == session, "automatic selection equals bound session ID");
        var summary = await client.SummaryAsync(database!, session!);
        Require(summary.Data.GetProperty("id").GetString() == session && summary.Data.GetProperty("capture_state").GetString() == "finished", "read-only view confirms actual session persisted");
        Require(summary.Data.GetProperty("name").GetString() == recordName, "same name as an older record is preserved literally");
        defaultNameMatchesExe = recordName == Path.GetFileNameWithoutExtension(options.InstallFixture!);
        var listed = await client.ListAsync(database!);
        Require(listed.Data.GetArrayLength() == earlierSummaries.Count + 1, "duplicate click launched only one additional session");
        duplicateCreatedOne = true;
        Require(listed.Data.EnumerateArray().Count(item => Text(item, "name") == recordName) >= 2, "same-name records coexist without association by name");
        foreach (var earlier in earlierSummaries)
            Require(JsonView.JsonEqual(earlier.Value, (await client.SummaryAsync(database!, earlier.Key)).Data), "earlier finished and unfinished records remain unchanged");
        savedScopeCount = summary.Data.GetProperty("watch_roots").GetArrayLength();
        Require(savedScopeCount == 0 && summary.Data.GetProperty("backend").TryGetProperty("follow_installer", out _), "saved follow marker has no preset observation directory");
        followModePersisted = true;
        Require(summary.Data.GetProperty("backend").GetProperty("etw_file").GetString() == "active", "stored backend is real ETW");
        Screenshot(viewer!, "install-autoopened-session.png");
    }

    private async Task ExplainAndExport()
    {
        var full = await ReadFullCapture();
        var backend = full.GetProperty("backend");
        var stats = full.GetProperty("stats");
        accounting = JsonSerializer.SerializeToElement(new
        {
            etw_events_lost = backend.GetProperty("etw_events_lost").Clone(),
            etw_buffers_lost = backend.GetProperty("etw_buffers_lost").Clone(),
            dropped_events = backend.GetProperty("dropped_events").Clone(),
            context_losses = backend.GetProperty("context_losses").Clone(),
            decode_errors = backend.GetProperty("decode_errors").Clone(),
            events_received = stats.GetProperty("events_received").Clone(),
            events_retained = stats.GetProperty("events_retained").Clone(),
            snapshot_gaps = stats.GetProperty("snapshot_gaps").Clone(),
            notification_gaps = stats.GetProperty("notification_gaps").Clone(),
            drain_timed_out = stats.GetProperty("drain_timed_out").Clone()
        });
        Require(backend.GetProperty("etw_events_lost").ValueKind == JsonValueKind.Number && backend.GetProperty("etw_events_lost").GetUInt64() == 0,
            "ETW event loss is actually measured zero");
        Require(backend.GetProperty("etw_buffers_lost").ValueKind == JsonValueKind.Number && backend.GetProperty("etw_buffers_lost").GetUInt64() == 0,
            "realtime ETW buffer loss is actually measured zero");
        Require(backend.GetProperty("dropped_events").GetUInt64() == 0 && backend.GetProperty("context_losses").GetUInt64() == 0 && backend.GetProperty("decode_errors").GetUInt64() == 0,
            "source/accounting losses remain explicit and absent in accepted fixture");
        var events = full.GetProperty("events").EnumerateArray().Select(item => item.Clone()).ToArray();
        var sourceEvents = events.Where(item => Text(item, "event_type") is "file" or "registry" && Text(item.GetProperty("evidence"), "source").StartsWith("Etw", StringComparison.Ordinal)).ToArray();
        sourceEventCount = sourceEvents.Length;
        Require(sourceEventCount > 0, "actual source observations persisted");
        var truth = Directory.EnumerateFiles(truthRoot!, "ground-truth-*.jsonl")
            .SelectMany(File.ReadAllLines).Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Require(truth.Length > 0, "existing fixture independent syscall oracle produced evidence");
        await File.WriteAllTextAsync(Path.Combine(options.PrivateOutput, "install-ground-truth.json"), JsonSerializer.Serialize(truth));
        await VerifyFollowOracle(full);
        var observedChild = followVerification!.Value.GetProperty("observations").EnumerateArray().Single(item => Text(item, "label") == "child");
        selectedEvent = Text(observedChild, "event_id");
        sourceEvent = events.Single(item => Text(item, "id") == selectedEvent);
        Require(truth.Any(row => Text(row, "role") == "followchild" && MatchesTruth(sourceEvent.Value, row)),
            "the demonstration selects the direct child's independently verified source write");
        independentTruthVerified = true;
        var index = Array.FindIndex(events, item => Text(item, "id") == selectedEvent);
        C<TabControl>(viewer!, "Tabs").SelectedIndex = 2;
        await WaitIdle(viewer!);
        if (C<TextBox>(viewer!, "EventFilter").Text.Length != 0)
        {
            C<Button>(viewer!, "ClearEventSearch").PerformClick(); await WaitIdle(viewer!);
        }
        for (var page = 0; page < index / 50; page++)
        {
            var next = C<Button>(viewer!, "NextEvents");
            Require(next.Enabled, "actual event page navigation available");
            next.PerformClick(); await WaitIdle(viewer!);
        }
        var grid = C<DataGridView>(viewer!, "Events");
        var row = grid.Rows.Cast<DataGridViewRow>().Single(item => Text((JsonElement)item.Tag!, "id") == selectedEvent);
        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells[0];
        await WaitIdle(viewer!);
        Require(C<TextBox>(viewer!, "EventDetails").Text.Contains(selectedEvent!, StringComparison.Ordinal), "actual UI explain uses selected real source event");
        C<CheckBox>(viewer!, "RawToggle").Checked = true;
        var explanation = CoreResponse.Parse(C<TextBox>(viewer!, "RawDetails").Text, "explain");
        Require(Text(explanation.Data, "app_id") == session && JsonView.JsonEqual(explanation.Data.GetProperty("event"), sourceEvent.Value), "explanation exact session and event identity");
        C<CheckBox>(viewer!, "RawToggle").Checked = false;
        C<Button>(viewer!, "CopyEventId").PerformClick();
        Require(Clipboard.GetText() == selectedEvent, "real source row and copied ID match the same explanation");
        Screenshot(viewer!, "install-real-event-explain.png");
        report = Path.Combine(Path.GetTempPath(), "contain-desktop-report-" + session + ".html");
        viewerDialogs.Report = report;
        C<Button>(viewer!, "ExportReport").PerformClick(); await WaitIdle(viewer!);
        Require(File.Exists(report), "product exported report after capture");
        var html = WebUtility.HtmlDecode(await File.ReadAllTextAsync(report));
        Require(html.Contains(session!, StringComparison.Ordinal) && html.Contains(selectedEvent!, StringComparison.Ordinal), "report belongs to exact session and selected event");
        Require(!html.Contains("<script", StringComparison.OrdinalIgnoreCase), "report stays passive");
        var destination = Path.Combine(options.Output, "reports", "controlled-real-etw-report.html");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Actual registry source paths can contain the runner's SID. The complete
        // local report is the user's product result, but it is not a public fixture.
        if (!html.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) && !html.Contains("S-1-5-21-", StringComparison.Ordinal))
        {
            File.Copy(report, destination, false);
            Require(Hash(report) == Hash(destination), "public report, when safe to publish, is the exact actual export");
            publicReport = true;
        }
        var restartWorking = Path.Combine(Path.GetDirectoryName(database!)!, "restart-working-directory"); Directory.CreateDirectory(restartWorking);
        using var restarted = new PackagedDesktop(options.Package, restartWorking);
        actualRestartPid = restarted.ProcessId; await restarted.ReadyAsync();
        Require(actualFirstExitCode == 0 && actualFirstPid is > 0 && actualRestartPid != actualFirstPid,
            "the recorded EXE process ended before a distinct process cold-starts");
        var restartedExplanation = await PersonalRecordsVerification.VerifyExactUi(restarted, session!, selectedEvent!, index / 50);
        Require(JsonView.JsonEqual(restartedExplanation.Data.GetProperty("event"), sourceEvent.Value),
            "cold-restarted actual EXE explains the exact original real source event");
        await restarted.InvokeAsync("CopyEventId");
        copiedEvent = Clipboard.GetText();
        Require(copiedEvent == selectedEvent, "cold-restarted actual EXE copies the same child event ID as its explanation");
        Require(restarted.WorkingDirectory != initialWorkingDirectory, "actual cold restart changes working directory");
        restartedSession = restartedExplanation.Data.GetProperty("app_id").GetString();
        restartedEvent = restartedExplanation.Data.GetProperty("event").GetProperty("id").GetString();
        screenshots.Add(await restarted.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "install-packaged-cold-restart-exact-event.png"));
        await restarted.CloseAsync(); coldRestartVerified = true;
        var verified = JsonNode.Parse(followVerification!.Value.GetRawText())!.AsObject();
        verified["selected_event_id"] = selectedEvent; verified["copied_event_id"] = copiedEvent;
        verified["explain_event_id"] = Text(explanation.Data.GetProperty("event"), "id");
        verified["restart_event_id"] = restartedEvent; verified["returned_session_id"] = finalStatus!.SessionId;
        verified["autoopened_session_id"] = session; verified["restart_session_id"] = restartedSession;
        followVerification = JsonSerializer.SerializeToElement(verified);
    }

    private async Task VerifyFollowOracle(JsonElement full)
    {
        Require(unrelatedProcess is not null, "independent writer was launched outside the installer tree");
        await unrelatedProcess!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        privateLog.AppendLine(await unrelatedProcess.StandardError.ReadToEndAsync());
        _ = await unrelatedProcess.StandardOutput.ReadToEndAsync();
        var expectedTargets = new[]
        {
            new { label = "parent", role = "followparent", path = Path.Combine(root!, "A", "parent-target.txt"), content = "Contain follow experiment: exact parent write\n" },
            new { label = "child", role = "followchild", path = Path.Combine(root!, "B", "child-target.txt"), content = "Contain follow experiment: exact direct child write\n" },
            new { label = "unrelated", role = "followunrelated", path = Path.Combine(root!, "A", "unrelated-target.txt"), content = "Contain follow experiment: independent writer, never installer\n" }
        };
        var privateOracle = Path.Combine(options.PrivateOutput, "follow-native-oracle"); Directory.CreateDirectory(privateOracle);
        var expectedPath = Path.Combine(privateOracle, "expected.json");
        var expected = new
        {
            source_sha = options.Source, capture_exit_code = 0, independent_exit_code = unrelatedProcess.ExitCode,
            directory_b_existed_before = false, product_watch_arguments = Array.Empty<string>(),
            excluded_io_paths = new[] { database! }, excluded_io_directories = new[] { options.PrivateOutput },
            targets = expectedTargets.Select(item => new { item.label, item.role, item.path,
                content_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.content))).ToLowerInvariant(),
                actual_content_sha256 = File.Exists(item.path) ? Hash(item.path) : null })
        };
        await File.WriteAllTextAsync(expectedPath, JsonSerializer.Serialize(expected));
        var explanationDirectory = Path.Combine(privateOracle, "explanations"); Directory.CreateDirectory(explanationDirectory);
        foreach (var target in expectedTargets)
        {
            var candidates = full.GetProperty("events").EnumerateArray().Where(item => Text(item, "event_type") == "file"
                && Text(item.GetProperty("evidence"), "source") == "EtwFile" && Normalize(Text(item, "resource")) == Normalize(target.path)).ToArray();
            Require(candidates.Length <= 32, "independent target selection respects its fixed candidate bound");
            foreach (var item in candidates)
            {
                var id = Text(item, "id"); Require(Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,100}$"), "safe exact event ID");
                var explanation = await client.ExplainAsync(database!, session!, id);
                await File.WriteAllTextAsync(Path.Combine(explanationDirectory, id + ".json"), explanation.RawJson);
            }
        }
        var summaryPath = Path.Combine(privateOracle, "follow-native-summary.json");
        var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false, true) };
        foreach (var argument in new[] { Path.GetFullPath("scripts/verify-follow-installer-experiment.py"),
            "--capture", Path.Combine(options.PrivateOutput, "install-capture.json"), "--truth", truthRoot!, "--expected", expectedPath,
            "--explanations", explanationDirectory, "--output", summaryPath, "--source", options.Source }) start.ArgumentList.Add(argument);
        using var oracle = Process.Start(start) ?? throw new IOException("Independent native oracle did not start.");
        var stdout = oracle.StandardOutput.ReadToEndAsync(); var stderr = oracle.StandardError.ReadToEndAsync();
        await oracle.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        privateLog.AppendLine(await stdout).AppendLine(await stderr);
        if (File.Exists(summaryPath))
        {
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(summaryPath)); followVerification = result.RootElement.Clone();
        }
        Require(oracle.ExitCode == 0 && followVerification is { } verified && verified.GetProperty("accepted").GetBoolean(),
            "all fixed root, direct-child, unrelated, baseline, bounded-storage and reopened-explanation oracle checks passed");
    }

    private async Task<JsonElement> ReadFullCapture()
    {
        var start = new ProcessStartInfo(client.ExecutablePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false, true) };
        foreach (var arg in new[] { "--db", database!, "--read-only", "inspect", "--json", "--", session! }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not read actual result.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        var raw = await output; privateLog.AppendLine(await error);
        Require(process.ExitCode == 0 && Encoding.UTF8.GetByteCount(raw) <= 64 * 1024 * 1024,
            "actual full read-only capture export succeeds within the existing output bound");
        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data").Clone();
        Require(Text(data, "id") == session, "full result is exact session");
        await File.WriteAllTextAsync(Path.Combine(options.PrivateOutput, "install-capture.json"), raw);
        return data;
    }

    private async Task VerifyOfflineFailure()
    {
        const string name = "synthetic_invalid_exe_preserves_saved_session_and_old_records";
        try
        {
            var failureDatabase = database + ".synthetic-failure.db";
            File.Copy(database!, failureDatabase, false);
            var invalidInstaller = Path.Combine(Path.GetTempPath(), "contain-invalid-executable-" + Guid.NewGuid().ToString("N") + ".exe");
            await File.WriteAllTextAsync(invalidInstaller, "SYNTHETIC invalid EXE: the operating system must reject this fixture before launch.");
            using var failureViewer = new ViewerForm(client, viewerDialogs, installDialogs: new FixtureInstallDialogs(invalidInstaller));
            failureViewer.Show();
            var lockedDatabase = database + ".synthetic-readonly.db";
            File.Copy(database!, lockedDatabase, false);
            var lockedHash = Hash(lockedDatabase);
            File.SetAttributes(lockedDatabase, File.GetAttributes(lockedDatabase) | FileAttributes.ReadOnly);
            try
            {
                using var lockedForm = new InstallForm(new InstallClient(client.ExecutablePath), failureViewer.OpenCapturedSessionAsync);
                lockedForm.Show(failureViewer);
                TestRecordIsolation.SelectManualObservation(lockedForm);
                C<TextBox>(lockedForm, "InstallerPath").Text = invalidInstaller;
                C<TextBox>(lockedForm, "RecordName").Text = recordName;
                C<TextBox>(lockedForm, "WatchRoot").Text = root!;
                TestRecordIsolation.SelectCustomDatabase(lockedForm, lockedDatabase);
                C<CheckBox>(lockedForm, "SnapshotOnly").Checked = true;
                C<Button>(lockedForm, "StartRecording").PerformClick();
                await lockedForm.PendingOperation.WaitAsync(TimeSpan.FromSeconds(90));
                Require(lockedForm.Result is { Succeeded: false } && lockedForm.LastStatus is { Stage: "failed", InstallerStarted: false }, "unwritable database fails before installer launch");
                Require(Hash(lockedDatabase) == lockedHash, "unwritable existing database bytes remain unchanged");
                lockedForm.Close();
            }
            finally { File.SetAttributes(lockedDatabase, File.GetAttributes(lockedDatabase) & ~FileAttributes.ReadOnly); }
            C<Button>(failureViewer, "NewInstall").PerformClick();
            using var failedForm = failureViewer.ActiveInstallForm ?? throw new InvalidOperationException("Failure fixture did not open the production install entry.");
            TestRecordIsolation.SelectManualObservation(failedForm);
            C<TextBox>(failedForm, "InstallerPath").Text = invalidInstaller;
            C<TextBox>(failedForm, "RecordName").Text = recordName;
            C<TextBox>(failedForm, "WatchRoot").Text = root!;
            TestRecordIsolation.SelectCustomDatabase(failedForm, Path.Combine(root!, "invalid-inside-root.db"));
            C<CheckBox>(failedForm, "SnapshotOnly").Checked = true;
            C<Button>(failedForm, "StartRecording").PerformClick();
            await failedForm.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
            Require(failedForm.LastStatus is null && !failedForm.IsActive && !File.Exists(Path.Combine(root!, "invalid-inside-root.db")), "invalid UI scope fails before collector launch and creates no database");
            C<TextBox>(failedForm, "WatchRoot").Text = root + "-missing";
            TestRecordIsolation.SelectCustomDatabase(failedForm, failureDatabase);
            C<Button>(failedForm, "StartRecording").PerformClick();
            await failedForm.PendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
            Require(failedForm.LastStatus is null && !Directory.Exists(root + "-missing"), "UI does not create nonexistent watch directory to pass validation");
            C<TextBox>(failedForm, "WatchRoot").Text = root!;
            C<Button>(failedForm, "StartRecording").PerformClick();
            await failedForm.PendingOperation.WaitAsync(TimeSpan.FromSeconds(100));
            Require(failedForm.Result is { Succeeded: false } && failedForm.LastStatus is { Stage: "failed", InstallerStarted: false, DataSaved: true }, "OS-invalid EXE failure saves unfinished evidence without reporting success");
            Require(failedForm.Result is { CollectorPid: > 0, LastConfirmedStage: "preparing" },
                "real child process failure retains the managed collector PID and last confirmed work phase");
            var failureText = failedForm.TaskDetailsText;
            Require(failureText.Contains("最后确认阶段：preparing", StringComparison.Ordinal)
                && failureText.Contains("采集进程 PID：" + failedForm.Result!.CollectorPid, StringComparison.Ordinal)
                && failureText.Contains("失败", StringComparison.Ordinal),
                "visible failed-task details retain the confirmed phase and collector identity");
            var failureId = failedForm.LastStatus!.SessionId;
            var saved = await client.SummaryAsync(failureDatabase, failureId);
            Require(Text(saved.Data, "capture_state") != "finished", "failed launch remains unfinished in existing viewer");
            await WaitIdle(failureViewer);
            Require(C<Button>(failedForm, "ViewRecordedSession").Enabled, "saved failed attempt remains available to inspect");
            if (!failedForm.Visible) failedForm.Show(failureViewer);
            var previousOperation = failedForm.PendingOperation;
            C<Button>(failedForm, "ViewRecordedSession").PerformClick();
            Require(!ReferenceEquals(previousOperation, failedForm.PendingOperation), "visible failed-result action invokes production open handler");
            await failedForm.PendingOperation.WaitAsync(TimeSpan.FromSeconds(90));
            var selected = C<DataGridView>(failureViewer, "Sessions").SelectedRows;
            Require(selected.Count == 1 && Text((JsonElement)selected[0].Tag!, "id") == failureId, "failed saved attempt opens exact backend-issued session");
            foreach (var earlier in earlierSummaries)
                Require(JsonView.JsonEqual(earlier.Value, (await client.SummaryAsync(failureDatabase, earlier.Key)).Data), "failure preserves earlier database records");
            failedForm.Close();
            await VerifyRetainedTaskDetails(failureViewer, failureId, failedForm.Result!.CollectorPid!.Value);
            failureViewer.Close();
            offlineChecks.Add(new(name, "passed"));
        }
        catch (Exception error)
        {
            offlineChecks.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : null));
            privateLog.AppendLine(name).AppendLine(error.ToString());
            throw;
        }
    }

    private async Task VerifyRetainedTaskDetails(ViewerForm form, string failureId, int collectorPid)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        var watch = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            var details = Application.OpenForms.Cast<Form>().SingleOrDefault(window => window.Name == "LastTaskDetails");
            if (details is null)
            {
                if (watch.Elapsed < TimeSpan.FromSeconds(5)) return;
                timer.Stop(); completion.TrySetException(new TimeoutException("Retained task details did not open.")); return;
            }
            timer.Stop();
            try
            {
                var text = C<TextBox>(details, "LastTaskDiagnostics").Text;
                Require(text.Contains(failureId, StringComparison.Ordinal) && text.Contains("最后确认阶段：preparing", StringComparison.Ordinal)
                    && text.Contains("采集进程 PID：" + collectorPid, StringComparison.Ordinal),
                    "task details remain accessible with exact failed session, work phase and collector PID after recording window closes");
                Require(text.Contains("失败或未完成", StringComparison.Ordinal) && text.Contains("不代表记录完整或安装成功", StringComparison.Ordinal),
                    "retained failure details never promote saved evidence to capture or installation success");
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { details.Close(); }
        };
        timer.Start();
        // A real production LinkLabel accessibility action opens its actual modal;
        // this is a control interaction, not a claim about native file-dialog navigation.
        var taskLink = C<LinkLabel>(form, "CaptureOutcome").AccessibilityObject.GetChild(0)
            ?? throw new InvalidOperationException("Retained task has no accessible link.");
        taskLink.DoDefaultAction();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    private static bool MatchesTruth(JsonElement item, JsonElement row)
    {
        var evidence = item.GetProperty("evidence");
        var time = ulong.Parse(Text(item, "timestamp_ticks"), CultureInfo.InvariantCulture);
        return row.GetProperty("success").GetBoolean() && Text(row, "role") != "unrelated"
            && Normalize(Text(item, "resource")) == Normalize(Text(row, "resource"))
            && Text(item, "operation") == Text(row, "operation")
            && evidence.GetProperty("pid").GetUInt32() == row.GetProperty("pid").GetUInt32()
            && Text(evidence, "process_creation_time") == Text(row, "creation_time")
            && time >= ulong.Parse(Text(row, "start_ticks"), CultureInfo.InvariantCulture)
            && time <= ulong.Parse(Text(row, "end_ticks"), CultureInfo.InvariantCulture);
    }

    private async Task Check(string name, Func<Task> action, Func<bool>? ready = null)
    {
        if (ready is not null && !ready()) { checks.Add(new(name, "not_run", "offline_or_runtime_prerequisite_failed")); return; }
        Phase(name);
        try { await action(); checks.Add(new(name, "passed")); Progress("passed"); }
        catch (Exception error) { checks.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : "phase=" + currentPhase)); privateLog.AppendLine(name).AppendLine(error.ToString()); Progress("failed", error.GetType().Name); }
    }
    private void Phase(string name) { currentPhase = name; Progress("started"); }
    private void Progress(string status, string? errorType = null)
    {
        progressEntries.Add(new { elapsed_ms = progressClock.ElapsedMilliseconds, phase = currentPhase, status, error_type = errorType,
            dialog_stage = actualDesktop?.ConfigurationDialogStage, installer_selections = actualDesktop?.InstallerPickerSelections,
            installer_cancels = actualDesktop?.InstallerPickerCancels, folder_selections = actualDesktop?.WatchPickerSelections });
        Directory.CreateDirectory(options.Output);
        File.WriteAllText(Path.Combine(options.Output, "install-progress.json"), JsonSerializer.Serialize(new
        { schema_version = 1, source_sha = options.Source, entries = progressEntries }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private bool Passed(string name) => checks.Any(check => check.Name == name && check.Status == "passed");
    private void Require(bool value, string reason) { if (!value) throw new TestFailureException(reason); assertions++; }
    private void Reject(Action action)
    {
        var rejected = false; try { action(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "malformed or contradictory status refused");
    }
    private static T C<T>(Control owner, string name) where T : Control => owner.Controls.Find(name, true).SingleOrDefault() as T ?? throw new InvalidOperationException("Missing product control: " + name);
    private static async Task WaitIdle(ViewerForm form) { await Task.Delay(50); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(100)); await Task.Delay(150); if (form.IsBusy) await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(100)); }
    private void Screenshot(Form form, string name)
    {
        form.Refresh(); var directory = Path.Combine(options.Output, "screenshots"); Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var file = Path.Combine(directory, name); bitmap.Save(file, ImageFormat.Png); screenshots.Add(new(name, Hash(file), bitmap.Width, bitmap.Height));
    }
    private static string Text(JsonElement value, string property) => value.TryGetProperty(property, out var field) ? field.ValueKind == JsonValueKind.String ? field.GetString()! : field.GetRawText() : "";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Normalize(string path) => path.Replace('/', '\\').Replace(@"\\?\", "", StringComparison.Ordinal).ToLowerInvariant();
    private static string ScopeHash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))))).ToLowerInvariant();

    private sealed class FixtureInstallDialogs(string installer) : IInstallDialogs
    {
        public string? ChooseInstaller(IWin32Window owner) => installer;
        public string? ChooseWatchRoot(IWin32Window owner) => null;
        public string? ChooseSaveDatabase(IWin32Window owner) => null;
    }

    private static void RenameSyntheticHistory(string database)
    {
        var safeRoot = Path.GetFullPath(TestRecordIsolation.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(database).StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(database))
            throw new InvalidOperationException("Historical controls must remain inside marked test data.");
        if (sqlite3_open_v2(database, out var connection, 2, IntPtr.Zero) != 0) throw new IOException("Could not open synthetic history controls.");
        try
        {
            var result = sqlite3_exec(connection, "UPDATE applications SET name = 'contain-test-installer'", IntPtr.Zero, IntPtr.Zero, out var error);
            try { if (result != 0) throw new IOException("Could not prepare same-name synthetic historical controls."); }
            finally { if (error != IntPtr.Zero) sqlite3_free(error); }
        }
        finally { sqlite3_close(connection); }
    }
    private static string Change(string raw, Action<JsonObject> action) { var node = JsonNode.Parse(raw)!.AsObject(); action(node); return node.ToJsonString(); }
    private static string StatusDocument(string db, string id, int sequence, string stage, bool started = false, bool exited = false, bool finished = false) => JsonSerializer.Serialize(new
    {
        schema_version = 3, kind = "install_status", data = new
        {
            session_id = id, database = db, sequence, stage, installer_started = started,
            installer_pid = started ? (uint?)123 : null, installer_creation_time = started ? "134350000000000001" : null,
            installer_exited = exited, installer_exit_code = exited ? (int?)0 : null,
            capture_finished = finished, data_saved = finished, etw_requested = true,
            etw_process = started ? "active" : null, etw_file = started ? "active" : null, etw_registry = started ? "active" : null,
            quality = finished ? new { level = "Degraded", reasons = new[] { "SYNTHETIC protocol test" } } : null, error = (string?)null
        }
    });

    private void WriteSummary()
    {
        foreach (var name in Plan.Where(name => checks.All(check => check.Name != name))) checks.Add(new(name, "not_run", "harness_did_not_reach_check"));
        File.WriteAllText(Path.Combine(options.PrivateOutput, "install-details.log"), privateLog.ToString());
        var accepted = viewerChecksPassed && checks.All(check => check.Status == "passed");
        var summary = new
        {
            schema_version = 1, protocol_revision = "contain-desktop-install-verification-1", source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(), gui_initiated = guiInitiated,
            real_etw_executed = guiInitiated && providersActive && sourceEventCount > 0,
            synthetic = false, controlled_fixture = true, maximum_native_captures = 1, retries = 0,
            planned_checks = Plan, checks, passed = checks.Count(check => check.Status == "passed"),
            failed = checks.Count(check => check.Status == "failed"), not_run = checks.Count(check => check.Status == "not_run"), skipped = 0,
            completed = checks.Count == Plan.Length && checks.All(check => check.Status != "not_run"),
            accepted, automatic_checks_accepted = accepted,
            executed_interaction_failed = interactionChecks.Any(check => check.Status == "failed"),
            assertions_passed = assertions, offline_viewer_checks_passed = viewerChecksPassed,
            package_manifest_sha256 = Hash(Path.Combine(options.Package, "BUILD.json")),
            actual_packaged_executable_capture = actualCapture,
            default_database_used_without_path_interaction = defaultSaveWithoutPathInteraction,
            cold_restart_verified = coldRestartVerified, working_directory_changed = coldRestartVerified,
            first_use = new
            {
                protocol_revision = "contain-follow-install-flow-1", actual_packaged_executable = actualCapture,
                windows_version = Environment.OSVersion.Version.ToString(), actual_device_dpi = firstUseDpi,
                installer_picker_cancels = actualDesktop?.InstallerPickerCancels ?? 0,
                installer_picker_selections = actualDesktop?.InstallerPickerSelections ?? 0,
                observation_picker_selections = actualDesktop?.WatchPickerSelections ?? 0,
                cancel_preserved_record_selection = cancelPreservedSelection,
                configuration_created_no_session = configurationCreatedNoSession,
                default_follow = defaultFollow, no_watch_argument = noWatchArgument, follow_mode_persisted = followModePersisted,
                ordinary_input_count = 1, advanced_interactions = actualDesktop?.AdvancedInteractions ?? 0,
                preexisting_session_count = earlierSummaries.Count,
                environment_check_interactions = actualDesktop?.EnvironmentCheckInteractions ?? 0,
                database_path_interactions = actualDesktop?.DatabasePathInteractions ?? 0,
                keyboard_flow_verified = keyboardFlowVerified, default_name_matches_exe = defaultNameMatchesExe,
                default_database = defaultSaveWithoutPathInteraction,
                saved_scope_count = savedScopeCount, session_id = session,
                main_entry_kept_active_task = activeEntryPreserved, duplicate_start_created_one_session = duplicateCreatedOne,
                screenshots = firstUseScreenshots
            },
            follow_verification = followVerification,
            first_process_id = actualFirstPid, restarted_process_id = actualRestartPid, first_process_exit_code = actualFirstExitCode,
            restart_session_id = restartedSession, restart_event_id = restartedEvent, collector_process_id = actualCollectorPid,
            viewer_assembly_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.dll")),
            viewer_launcher_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.exe")),
            contain_sha256 = Hash(client.ExecutablePath), fixture_sha256 = File.Exists(options.InstallFixture) ? Hash(options.InstallFixture!) : null,
            session_id = session ?? finalStatus?.SessionId, event_id = selectedEvent,
            installer_started = finalStatus?.InstallerStarted ?? false, installer_pid = finalStatus?.InstallerPid,
            installer_creation_time = finalStatus?.InstallerCreationTime, installer_exit_code = finalStatus?.InstallerExitCode,
            collection_finished = finalStatus?.CaptureFinished ?? false, data_saved = finalStatus?.DataSaved ?? false,
            capture_state = finalStatus?.Stage, quality_level = finalStatus?.QualityLevel,
            quality_reasons = finalStatus?.QualityReasons, providers_active = providersActive,
            real_source_event_count = sourceEventCount, independent_truth_verified = independentTruthVerified,
            capture_accounting = accounting,
            observed_stages = observedStages, real_event = sourceEvent is { ValueKind: JsonValueKind.Object } item ? new
            {
                id = Text(item, "id"), operation = Text(item, "operation"), confidence = Text(item, "confidence"),
                source = Text(item.GetProperty("evidence"), "source"), timestamp_ticks = Text(item, "timestamp_ticks"),
                pid = Text(item.GetProperty("evidence"), "pid"), creation_time = Text(item.GetProperty("evidence"), "process_creation_time")
            } : null,
            report_generated = report is not null && File.Exists(report), report_sha256 = report is not null && File.Exists(report) ? Hash(report) : null,
            report_publication = publicReport ? "byte_identical_controlled_fixture_export" : "local_only_full_report_may_include_host_registry_identity",
            screenshots, manual_checks = interactionChecks, offline_checks = offlineChecks,
            fault_injection = "Explicit synthetic read-only-database and invalid-EXE starts in selected snapshot mode; no successful installer process and no ETW acceptance credit.",
            screenshot_method = "Actual production WinForms controls rendered on Windows after product selection/start/status/result/explain actions. Controlled fixture; real ETW source records are not synthetic.",
            interaction_boundary = "Product controls/start logic executed. Native picker navigation and cancel separately reported; injected configuration is never counted as native picker navigation. No third-party installer, scale experiment or retry.",
            private_evidence_retained = true
        };
        File.WriteAllText(Path.Combine(options.Output, "install-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + "\n");
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);
}
