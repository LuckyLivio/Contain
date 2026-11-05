using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

internal sealed class PersonalRecordsVerification(Options options, bool viewerChecksPassed)
{
    private static readonly string[] Plan = ["guarded_test_root_and_missing_library", "default_path_and_name_suggestion",
        "manual_name_and_advanced_custom_location", "external_database_read_only_new_record_default",
        "scope_conflict_before_library_creation", "future_corrupt_unwritable_records_preserved",
        "packaged_executable_default_history", "packaged_executable_cold_restart_exact_session_event"];
    private readonly List<CheckResult> checks = [];
    private readonly List<ImageResult> screenshots = [];
    private readonly StringBuilder errors = new();
    private readonly CoreClient client = new(Path.Combine(options.Package, "contain.exe"));
    private int assertions;
    private bool actualExecutable;
    private bool restarted;
    private string? restartedSession;
    private string? restartedEvent;
    private int? firstPid;
    private int? restartPid;
    private int? firstExitCode;

    internal static int RunStandalone(string package, string fixtures, string output)
    {
        Directory.CreateDirectory(output); var privateOutput = Path.Combine(output, "private"); Directory.CreateDirectory(privateOutput);
        var options = new Options(Path.GetFullPath(package), Path.GetFullPath(fixtures), Path.GetFullPath(output),
            Path.GetFullPath(privateOutput), "working-tree", null);
        var passed = false;
        using var owner = new Form { Text = "Contain isolated personal-record checks", ShowInTaskbar = false };
        owner.Shown += async (_, _) => { try { passed = await new PersonalRecordsVerification(options, true).RunAsync(owner); } finally { owner.Close(); } };
        Application.Run(owner); return passed ? 0 : 1;
    }

    internal async Task<bool> RunAsync(Form owner)
    {
        await Check(Plan[0], async () =>
        {
            var saved = Environment.GetEnvironmentVariable(TestRecordIsolation.Variable);
            try
            {
                foreach (var invalid in new[] { " ", "relative", Path.Combine(TestRecordIsolation.Root, "missing-root") })
                {
                    Environment.SetEnvironmentVariable(TestRecordIsolation.Variable, invalid);
                    Reject(() => PersonalRecords.ForCurrentUser());
                }
                var unmarked = Path.Combine(TestRecordIsolation.Root, "unmarked-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(unmarked);
                Environment.SetEnvironmentVariable(TestRecordIsolation.Variable, unmarked);
                Reject(() => PersonalRecords.ForCurrentUser());
            }
            finally { Environment.SetEnvironmentVariable(TestRecordIsolation.Variable, saved); }
            var root = TestRecordIsolation.Create("missing");
            using var isolated = TestRecordIsolation.Use(root);
            var records = PersonalRecords.ForCurrentUser();
            Require(records.DatabasePath == Path.Combine(root, "Contain", "Records", "contain.db"), "guarded absolute default path");
            Require(records.InspectExisting() == PersonalRecordState.Missing && records.IsMissing, "missing means unavailable, not a fabricated record");
            Require(!Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "resolving missing records creates neither parent nor database");
            foreach (var component in new[] { "Contain", "Records" })
            {
                var junctionRoot = TestRecordIsolation.Create("junction-" + component);
                var target = TestRecordIsolation.Create("junction-target");
                var parent = component == "Records" ? Path.Combine(junctionRoot, "Contain") : junctionRoot;
                Directory.CreateDirectory(parent);
                var link = Path.Combine(parent, component);
                await CreateTestJunction(link, target);
                using var junctionIsolation = TestRecordIsolation.Use(junctionRoot);
                Reject(() => PersonalRecords.ForCurrentUser());
                Require((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0 && Directory.Exists(target),
                    "rejected junction fixture and isolated target remain preserved");
            }
        });
        await Check(Plan[1], async () =>
        {
            var root = TestRecordIsolation.Create("suggestion"); using var isolated = TestRecordIsolation.Use(root);
            using var form = NewForm(); form.Show(owner);
            var records = PersonalRecords.ForCurrentUser();
            Require(form.Database == records.DatabasePath && !C<CheckBox>(form, "AdvancedToggle").Checked
                && !C<CheckBox>(form, "CustomDatabaseToggle").Checked, "new recording defaults to personal records with advanced options collapsed");
            Require(!C<TextBox>(form, "DatabaseSavePath").Visible && !C<Button>(form, "ChooseSaveDatabase").Visible,
                "ordinary flow exposes no database picker");
            C<TextBox>(form, "InstallerPath").Text = client.ExecutablePath;
            Require(C<TextBox>(form, "RecordName").Text.Contains(Path.GetFileNameWithoutExtension(client.ExecutablePath), StringComparison.Ordinal), "installer gives an editable name suggestion");
            var second = Path.Combine(root, "Second Installer 中文.exe"); File.Copy(client.ExecutablePath, second, false);
            C<TextBox>(form, "InstallerPath").Text = second;
            Require(C<TextBox>(form, "RecordName").Text.Contains("Second Installer 中文", StringComparison.Ordinal), "untouched suggestion follows installer selection");
            Require(!Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "configuration does not create the personal library");
            form.Close(); await Task.CompletedTask;
        });
        await Check(Plan[2], async () =>
        {
            var root = TestRecordIsolation.Create("manual-name"); using var isolated = TestRecordIsolation.Use(root);
            using var form = NewForm(); form.Show(owner);
            C<TextBox>(form, "RecordName").Text = "手动名称 & 保留";
            C<TextBox>(form, "InstallerPath").Text = client.ExecutablePath;
            Require(C<TextBox>(form, "RecordName").Text == "手动名称 & 保留", "manual name before installer selection remains unchanged");
            var custom = Path.Combine(root, "custom.db");
            TestRecordIsolation.SelectCustomDatabase(form, custom);
            Require(form.Database == custom && C<TextBox>(form, "DatabaseSavePath").Visible, "explicit advanced custom location is available");
            C<CheckBox>(form, "CustomDatabaseToggle").Checked = false;
            Require(form.Database == PersonalRecords.ForCurrentUser().DatabasePath, "disabling custom location returns to stable default");
            Require(!File.Exists(custom), "choosing a path is not a write");
            form.Close(); await Task.CompletedTask;
        });
        await Check(Plan[3], async () =>
        {
            var root = TestRecordIsolation.Create("external-read-only"); using var isolated = TestRecordIsolation.Use(root);
            var external = Path.Combine(root, "external.db"); File.Copy(Path.Combine(options.Fixtures, "finished.db"), external, false);
            var before = Hash(external); var dialogs = new TestDialogs { Database = external };
            using var viewer = new ViewerForm(client, dialogs); viewer.Show(owner); await Idle(viewer);
            C<Button>(viewer, "OpenDatabase").PerformClick(); await Idle(viewer);
            Require(C<DataGridView>(viewer, "Sessions").Rows.Count == 1, "explicit external database still opens read-only");
            C<Button>(viewer, "NewInstall").PerformClick();
            var form = viewer.ActiveInstallForm ?? throw new TestFailureException("new install form missing");
            Require(form.Database == PersonalRecords.ForCurrentUser().DatabasePath && form.Database != external,
                "viewing external data never redirects the next default recording");
            form.Close();
            C<Button>(viewer, "MyRecords").PerformClick(); await Idle(viewer);
            Require(C<DataGridView>(viewer, "Sessions").Rows.Count == 0 && !File.Exists(PersonalRecords.ForCurrentUser().DatabasePath),
                "returning to missing personal records leaves an honest empty state");
            Require(Hash(external) == before, "external original bytes unchanged");
            var personal = PersonalRecords.ForCurrentUser().DatabasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(personal)!); File.WriteAllText(personal, "SYNTHETIC corrupt personal database");
            C<Button>(viewer, "OpenDatabase").PerformClick(); await Idle(viewer);
            Require(C<DataGridView>(viewer, "Sessions").Rows.Count == 1, "external view reopens before corrupt-personal transition");
            C<Button>(viewer, "MyRecords").PerformClick(); await Idle(viewer);
            Require(C<DataGridView>(viewer, "Sessions").Rows.Count == 0 && Hash(external) == before,
                "failed personal-library navigation clears external rows rather than relabeling them as personal records");
            viewer.Close();
        });
        await Check(Plan[4], async () =>
        {
            var root = TestRecordIsolation.Create("scope-conflict"); using var isolated = TestRecordIsolation.Use(root);
            var records = PersonalRecords.ForCurrentUser();
            var request = new InstallRequest(client.ExecutablePath, "SYNTHETIC scope refusal", root, records.DatabasePath, [], SnapshotOnly: true);
            Reject(() => records.PrepareForRecording(request));
            Require(!Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "scope refusal precedes default-directory creation");
            using var form = NewForm(); form.Show(owner);
            C<TextBox>(form, "InstallerPath").Text = client.ExecutablePath;
            C<TextBox>(form, "WatchRoot").Text = root;
            C<Button>(form, "StartRecording").PerformClick(); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(20));
            Require(!form.IsActive && form.LastStatus?.InstallerStarted != true && !Directory.Exists(Path.GetDirectoryName(records.DatabasePath)),
                "default GUI conflict never starts an installer or creates its database");
            Require(C<Button>(form, "ChangeSaveLocation").Visible, "recoverable conflict has one explicit change-location entry");
            C<Button>(form, "ChangeSaveLocation").PerformClick();
            Require(C<CheckBox>(form, "AdvancedToggle").Checked && C<CheckBox>(form, "CustomDatabaseToggle").Checked,
                "change-location opens the actual advanced custom controls");
            form.Close();
        });
        await Check(Plan[5], async () =>
        {
            var failed = new InstallStatus(Guid.NewGuid().ToString(), Path.Combine(TestRecordIsolation.Root, "not-created.db"), 2,
                "failed", false, null, null, false, null, false, false, false, null, null, null, null, [], "SYNTHETIC trusted prelaunch refusal");
            Require(new InstallRunResult(failed, 1, null, "").CanRetryBeforeInstaller, "trusted terminal prelaunch failure is retryable");
            Require(!new InstallRunResult(failed, 1, "malformed protocol", "").CanRetryBeforeInstaller
                && !new InstallRunResult(null, 0, null, "").CanRetryBeforeInstaller
                && !new InstallRunResult(failed with { InstallerStarted = true, InstallerPid = 1234 }, 1, null, "").CanRetryBeforeInstaller,
                "missing/protocol/started outcomes never grant a retry");
            foreach (var scenario in new[] { "future", "corrupt", "unrelated", "read-only", "orphan-wal" })
            {
                var root = TestRecordIsolation.Create(scenario); using var isolated = TestRecordIsolation.Use(root);
                var records = PersonalRecords.ForCurrentUser(); Directory.CreateDirectory(Path.GetDirectoryName(records.DatabasePath)!);
                var observed = Path.Combine(root, "observed"); Directory.CreateDirectory(observed);
                var evidence = records.DatabasePath;
                if (scenario == "orphan-wal") { evidence += "-wal"; File.WriteAllText(evidence, "SYNTHETIC orphan WAL evidence"); }
                else File.Copy(Path.Combine(options.Fixtures, scenario == "read-only" ? "finished.db" : scenario + ".db"), evidence, false);
                if (scenario == "read-only") File.SetAttributes(evidence, File.GetAttributes(evidence) | FileAttributes.ReadOnly);
                var before = Hash(evidence);
                try
                {
                    var started = false;
                    try
                    {
                        var result = await new InstallClient(client.ExecutablePath).RunAsync(new(client.ExecutablePath,
                            "SYNTHETIC protected library", observed, records.DatabasePath, [], SnapshotOnly: true),
                            state => started |= state.InstallerStarted, records);
                        Require(!result.Succeeded && !started, "unsafe existing record cannot be accepted or launch installer");
                    }
                    catch (Exception error) when (error is InvalidDataException or CoreClientException or IOException)
                    { Require(!started, "preflight refusal precedes installer launch"); }
                    Require(Hash(evidence) == before, "failed existing-record handling preserves exact original evidence");
                    if (scenario == "orphan-wal") Require(!File.Exists(records.DatabasePath), "orphan WAL never becomes a replacement database");
                }
                finally { if (scenario == "read-only") File.SetAttributes(evidence, File.GetAttributes(evidence) & ~FileAttributes.ReadOnly); }
            }
        });
        var actualRoot = TestRecordIsolation.Create("actual-executable");
        using (TestRecordIsolation.Use(actualRoot))
        {
            var records = PersonalRecords.ForCurrentUser();
            var workingA = Path.Combine(actualRoot, "working-a"); Directory.CreateDirectory(workingA);
            await Check(Plan[6], async () =>
            {
                using var desktop = new PackagedDesktop(options.Package, workingA);
                await desktop.ReadyAsync(); actualExecutable = true;
                await PackagedDesktop.WaitAsync(async () => (await desktop.TextAsync("Status")).Contains("记录", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
                Require(!(await desktop.NamesAsync("Sessions")).Any(name => name.StartsWith("session:", StringComparison.Ordinal)), "fresh actual EXE shows no invented history");
                Require(!Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "actual cold startup does not create an empty database");
                screenshots.Add(await desktop.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "personal-first-launch-empty.png"));
                await desktop.CloseAsync(); Require(desktop.HasExited, "first packaged process exits completely");
            });
            await Check(Plan[7], async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(records.DatabasePath)!);
                File.Copy(Path.Combine(options.Package, "samples", "viewer-demo.db"), records.DatabasePath, false);
                var fingerprint = Hash(records.DatabasePath);
                var listed = await client.ListAsync(records.DatabasePath);
                restartedSession = "synthetic-finished";
                Require(listed.Data.EnumerateArray().Count(item => item.GetProperty("id").GetString() == restartedSession) == 1, "exact seeded record identity exists");
                var history = await client.HistoryAsync(records.DatabasePath, restartedSession, 50, 0);
                restartedEvent = history.Data.GetProperty("events")[0].GetProperty("id").GetString()!;
                using (var first = new PackagedDesktop(options.Package, workingA))
                {
                    firstPid = first.ProcessId; await first.ReadyAsync();
                    await VerifyExactUi(first, restartedSession, restartedEvent);
                    await first.CloseAsync(); Require(first.HasExited, "record-view process terminated before restart"); firstExitCode = first.ExitCode;
                }
                var movedPackage = Path.Combine(actualRoot, "moved-package", "程序包 中文 & [x]"); CopyPackage(options.Package, movedPackage);
                var workingB = Path.Combine(actualRoot, "different-working-directory"); Directory.CreateDirectory(workingB);
                using var second = new PackagedDesktop(movedPackage, workingB);
                restartPid = second.ProcessId; await second.ReadyAsync();
                await VerifyExactUi(second, restartedSession, restartedEvent);
                Require(second.WorkingDirectory != workingA && second.Executable != Path.Combine(options.Package, "Contain.Viewer.exe"), "restart uses moved package and different working directory");
                screenshots.Add(await second.ScreenshotAsync(Path.Combine(options.Output, "screenshots"), "personal-cold-restart-exact-event.png"));
                await second.CloseAsync();
                Require(Hash(records.DatabasePath) == fingerprint, "both actual read-only processes preserve personal database bytes");
                restarted = true;
            });
        }
        var accepted = viewerChecksPassed && checks.Count == Plan.Length && checks.All(check => check.Status == "passed");
        File.WriteAllText(Path.Combine(options.PrivateOutput, "personal-records-details.log"), errors.ToString());
        File.WriteAllText(Path.Combine(options.Output, "personal-records-summary.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1, protocol_revision = "contain-desktop-personal-records-verification-1", source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(), synthetic = true, real_etw_executed = false,
            actual_packaged_executable = actualExecutable, cold_restart_verified = restarted,
            initial_session_id = restartedSession, initial_event_id = restartedEvent,
            restart_session_id = restartedSession, restart_event_id = restartedEvent, first_process_id = firstPid, restarted_process_id = restartPid,
            first_process_exit_code = firstExitCode, working_directory_changed = restarted,
            package_manifest_sha256 = File.Exists(Path.Combine(options.Package, "BUILD.json")) ? Hash(Path.Combine(options.Package, "BUILD.json")) : null,
            viewer_assembly_sha256 = Hash(typeof(ViewerForm).Assembly.Location), viewer_launcher_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.exe")),
            contain_sha256 = Hash(client.ExecutablePath), planned_checks = Plan, checks, assertions,
            passed = checks.Count(check => check.Status == "passed"), failed = checks.Count(check => check.Status == "failed"),
            skipped = 0, not_run = Plan.Length - checks.Count, completed = checks.Count == Plan.Length,
            accepted, screenshots, retries = 0, real_user_records_accessed = false,
            scope = "Explicit marked test-root only; actual packaged EXE operated through process-scoped UI Automation. Synthetic records and read-only cold restart are not real ETW evidence."
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return accepted;
    }

    internal static async Task<CoreResponse> VerifyExactUi(PackagedDesktop desktop, string session, string eventId, int page = 0)
    {
        await PackagedDesktop.WaitAsync(async () => (await desktop.NamesAsync("Sessions")).Any(name => name == "session:" + session || name.EndsWith("；session:" + session, StringComparison.Ordinal)), TimeSpan.FromSeconds(30));
        await desktop.SelectIdentityAsync("session:" + session);
        await PackagedDesktop.WaitAsync(async () => (await desktop.DescriptionAsync("SessionTitle")).Contains("session:" + session, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        await desktop.SelectTabAsync("事件与依据");
        for (var index = 0; index < page; index++)
        {
            await PackagedDesktop.WaitAsync(() => desktop.EnabledAsync("NextEvents"), TimeSpan.FromSeconds(30));
            await desktop.InvokeAsync("NextEvents");
            var offset = (index + 1) * 50;
            await PackagedDesktop.WaitAsync(async () => (await desktop.TextAsync("EventsPage")).Contains("偏移 " + offset, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        }
        await desktop.SetTextAsync("EventFilter", eventId);
        await PackagedDesktop.WaitAsync(async () => (await desktop.NamesAsync("Events")).Any(name => name == "event:" + eventId || name.EndsWith("；event:" + eventId, StringComparison.Ordinal)), TimeSpan.FromSeconds(30));
        await desktop.SelectIdentityAsync("event:" + eventId);
        await PackagedDesktop.WaitAsync(async () => (await desktop.TextAsync("EventDetails")).Contains(eventId, StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        await desktop.ToggleAsync("RawToggle", true);
        var explanation = CoreResponse.Parse(await desktop.TextAsync("RawDetails"), "explain");
        if (explanation.Data.GetProperty("app_id").GetString() != session || explanation.Data.GetProperty("event").GetProperty("id").GetString() != eventId)
            throw new TestFailureException("Actual cold-restarted EXE explanation has the wrong record or event identity.");
        await desktop.ToggleAsync("RawToggle", false);
        return explanation;
    }

    private InstallForm NewForm() => new(new InstallClient(client.ExecutablePath), (_, _) => Task.CompletedTask);
    private async Task Check(string name, Func<Task> action)
    {
        try { await action(); checks.Add(new(name, "passed")); }
        catch (Exception error) { checks.Add(new(name, "failed", error.GetType().Name, "isolated personal-record verification failed")); errors.AppendLine(name).AppendLine(error.ToString()); }
    }
    private void Require(bool condition, string message) { if (!condition) throw new TestFailureException(message); assertions++; }
    private void Reject(Action action) { var refused = false; try { action(); } catch (InvalidDataException) { refused = true; } Require(refused, "invalid location refused without fallback"); }
    private static T C<T>(Control form, string name) where T : Control => form.Controls.Find(name, true).Single() as T ?? throw new InvalidOperationException(name);
    private static async Task Idle(ViewerForm form) { await Task.Delay(50); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(60)); await Task.Delay(100); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void CopyPackage(string source, string destination)
    {
        if (Directory.Exists(destination)) throw new IOException("Moved-package fixture already exists.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, false);
            if (Hash(file) != Hash(target)) throw new InvalidDataException("Moved frozen package bytes differ.");
        }
    }

    private static async Task CreateTestJunction(string link, string target)
    {
        if (!Path.IsPathFullyQualified(link) || !Path.IsPathFullyQualified(target) || link.Contains('"') || target.Contains('"'))
            throw new InvalidDataException("Invalid isolated junction fixture path.");
        // Only create a junction inside marked disposable test data. No delete,
        // move, real personal-library path or system configuration is involved.
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\""
        }) ?? throw new IOException("Junction fixture setup failed.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _ = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new IOException("The explicitly isolated junction fixture could not be created: " + error);
    }
}
