using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

/// <summary>Production form controls over isolated records; synthetic permission status is never ETW evidence.</summary>
internal sealed class FirstUseConfigurationChecks(Options options, bool priorChecksPassed)
{
    private static readonly string[] Plan =
    [
        "picker_cancel_and_existing_selection", "missing_and_invalidated_observation_directory",
        "personal_library_conflict_and_correction", "advanced_options_retained_when_collapsed",
        "synthetic_permission_refusal_explicit_retry_and_single_start"
    ];
    private readonly List<CheckResult> checks = [];
    private readonly StringBuilder privateErrors = new();
    private readonly CoreClient client = new(Path.Combine(options.Package, "contain.exe"));
    private int assertions;

    internal async Task<bool> RunAsync(Form owner)
    {
        await Check(Plan[0], async () =>
        {
            Require(Hash(typeof(ViewerForm).Assembly.Location) == Hash(Path.Combine(options.Package, "Contain.Viewer.dll")),
                "configuration checks load the frozen production UI assembly");
            var root = TestRecordIsolation.Create("first-use-picker"); using var isolation = TestRecordIsolation.Use(root);
            var external = Path.Combine(root, "existing.db"); File.Copy(Path.Combine(options.Fixtures, "finished.db"), external, false);
            var original = Hash(external); var viewerDialogs = new TestDialogs { Database = external };
            var installDialogs = new ConfigurationTestDialogs();
            using var viewer = new ViewerForm(client, viewerDialogs, installDialogs: installDialogs); viewer.Show(owner); await Idle(viewer);
            C<Button>(viewer, "OpenDatabase").PerformClick(); await Idle(viewer);
            var sessions = C<DataGridView>(viewer, "Sessions");
            Require(sessions.Rows.Count == 1, "isolated history is loaded before cancelling installer choice");
            sessions.Rows[0].Selected = true; sessions.CurrentCell = sessions.Rows[0].Cells[0]; await Idle(viewer);
            var selected = C<Label>(viewer, "SessionTitle").AccessibleDescription;
            C<Button>(viewer, "NewInstall").PerformClick();
            Require(installDialogs.InstallerCalls == 1 && viewer.ActiveInstallForm is null,
                "cancelled initial picker opens no configuration or task window");
            Require(C<Label>(viewer, "SessionTitle").AccessibleDescription == selected && sessions.SelectedRows.Count == 1,
                "cancelled initial picker preserves the exact selected historical record");
            Require(!Directory.Exists(Path.GetDirectoryName(PersonalRecords.ForCurrentUser().DatabasePath)) && Hash(external) == original,
                "cancel creates no personal library or session and preserves existing evidence");
            installDialogs.Installer = client.ExecutablePath;
            C<Button>(viewer, "NewInstall").PerformClick();
            var form = viewer.ActiveInstallForm ?? throw new TestFailureException("Accepted installer selection did not open configuration.");
            Require(C<TextBox>(form, "InstallerPath").Text == client.ExecutablePath && C<TextBox>(form, "WatchRoot").Text.Length == 0,
                "initial chosen executable is carried into configuration without inferring an observation directory");
            C<Button>(viewer, "NewInstall").PerformClick();
            Require(ReferenceEquals(form, viewer.ActiveInstallForm) && installDialogs.InstallerCalls == 2,
                "existing task entry reuses its configuration window without another picker");
            form.Close(); viewer.Close();
        });
        await Check(Plan[1], async () =>
        {
            var root = TestRecordIsolation.Create("first-use-observation"); using var isolation = TestRecordIsolation.Use(root);
            var records = PersonalRecords.ForCurrentUser(); var dialogs = new ConfigurationTestDialogs();
            var bridge = new InstallClient(client.ExecutablePath);
            using var form = new InstallForm(bridge, (_, _) => Task.CompletedTask, dialogs); form.Show(owner);
            C<TextBox>(form, "InstallerPath").Text = client.ExecutablePath;
            Require(C<TextBox>(form, "WatchRoot").Text.Length == 0 && !C<Button>(form, "StartRecording").Enabled,
                "an executable alone cannot enable recording or imply a scope");
            Require(C<Label>(form, "WatchRootHint").Text.Contains("还需选择观察文件夹", StringComparison.Ordinal),
                "missing observation scope has an actionable ordinary-flow hint");
            Require(!C<CheckBox>(form, "AdvancedToggle").Checked && !C<Button>(form, "CheckEnvironment").Visible
                && !C<TextBox>(form, "RecordName").Visible && !C<CheckBox>(form, "SnapshotOnly").Visible,
                "ordinary setup hides optional diagnostics and technical choices");
            var before = form.PendingOperation; C<Button>(form, "StartRecording").PerformClick(); await Task.Delay(80);
            Require(ReferenceEquals(before, form.PendingOperation) && !bridge.CollectorStarted && form.LastStatus is null
                && !Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "missing scope starts no collector or installer and creates no library");
            var watched = Path.Combine(root, "chosen existing 中文 %_ folder"); Directory.CreateDirectory(watched);
            dialogs.WatchRoot = watched; C<Button>(form, "ChooseWatchRoot").PerformClick();
            Require(C<TextBox>(form, "WatchRoot").ReadOnly && C<TextBox>(form, "WatchRoot").Text == watched
                && C<Button>(form, "StartRecording").Enabled, "a selected existing directory is shown exactly and enables explicit Start");
            C<Button>(form, "CopyWatchRoot").PerformClick(); Require(Clipboard.GetText() == watched, "copy retains the exact selected observation path");
            var moved = watched + " moved"; Directory.Move(watched, moved);
            C<Button>(form, "StartRecording").PerformClick(); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(20));
            Require(!bridge.CollectorStarted && form.LastStatus?.InstallerStarted != true && !File.Exists(records.DatabasePath),
                "a directory invalidated after selection is refused before any collector or session creation");
            var explanation = C<Label>(form, "WatchRootHint").Text + C<TextBox>(form, "InstallStatus").Text;
            Require(explanation.Contains("重新", StringComparison.Ordinal) && (explanation.Contains("目录", StringComparison.Ordinal) || explanation.Contains("文件夹", StringComparison.Ordinal)),
                "invalidated scope points the user back to directory selection");
            dialogs.WatchRoot = moved; C<Button>(form, "ChooseWatchRoot").PerformClick();
            Require(C<Button>(form, "StartRecording").Enabled && C<TextBox>(form, "WatchRoot").Text == moved && !bridge.CollectorStarted,
                "scope can be corrected without a new task window or automatic start");
            form.Close();
        });
        await Check(Plan[2], async () =>
        {
            var root = TestRecordIsolation.Create("first-use-library-conflict"); using var isolation = TestRecordIsolation.Use(root);
            var records = PersonalRecords.ForCurrentUser(); var dialogs = new ConfigurationTestDialogs();
            var bridge = new InstallClient(client.ExecutablePath);
            using var form = new InstallForm(bridge, (_, _) => Task.CompletedTask, dialogs); form.Show(owner);
            C<TextBox>(form, "InstallerPath").Text = client.ExecutablePath; C<TextBox>(form, "WatchRoot").Text = root;
            C<Button>(form, "StartRecording").PerformClick(); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(20));
            Require(!bridge.CollectorStarted && !Directory.Exists(Path.GetDirectoryName(records.DatabasePath))
                && C<Button>(form, "ChangeSaveLocation").Visible, "personal-library scope conflict exposes correction before creation or launch");
            C<Button>(form, "ChangeSaveLocation").PerformClick();
            Require(C<CheckBox>(form, "AdvancedToggle").Checked && C<CheckBox>(form, "CustomDatabaseToggle").Checked,
                "change-save-location reveals the existing advanced destination controls");
            var outside = TestRecordIsolation.Create("first-use-outside-scope"); dialogs.Database = Path.Combine(outside, "chosen.db");
            C<Button>(form, "ChooseSaveDatabase").PerformClick();
            Require(form.Database == dialogs.Database && dialogs.DatabaseCalls == 1 && !File.Exists(dialogs.Database)
                && ReadRequest(form).Database == dialogs.Database, "corrected destination is the exact effective request without writing it early");
            C<CheckBox>(form, "AdvancedToggle").Checked = false;
            Require(C<Control>(form, "CaptureConfigurationPreview").Visible, "custom destination remains visible as an effective choice when advanced options collapse");
            form.Close();
        });
        await Check(Plan[3], async () =>
        {
            var root = TestRecordIsolation.Create("first-use-advanced"); using var isolation = TestRecordIsolation.Use(root);
            var watch = Path.Combine(root, "observed"); Directory.CreateDirectory(watch);
            using var form = new InstallForm(new InstallClient(client.ExecutablePath), (_, _) => Task.CompletedTask); form.Show(owner);
            C<TextBox>(form, "InstallerPath").Text = client.ExecutablePath; C<TextBox>(form, "WatchRoot").Text = watch;
            Require(!C<Control>(form, "CaptureConfigurationPreview").Visible, "unchanged defaults do not add an ordinary-page confirmation checklist");
            C<CheckBox>(form, "AdvancedToggle").Checked = true;
            C<TextBox>(form, "RecordName").Text = "SYNTHETIC 自定义记录名";
            var replacement = Path.Combine(root, "changed EXE 中文.exe"); File.Copy(client.ExecutablePath, replacement, false);
            C<TextBox>(form, "InstallerPath").Text = replacement;
            TestRecordIsolation.SelectCustomDatabase(form, Path.Combine(root, "chosen custom.db"));
            C<CheckBox>(form, "SnapshotOnly").Checked = true;
            C<TextBox>(form, "InstallerArguments").Text = "含 空格\r\n%_literal";
            C<TextBox>(form, "RegistryKey").Text = @"Software\Contain\FirstUseSynthetic";
            C<NumericUpDown>(form, "SettleMilliseconds").Value = 2000;
            C<NumericUpDown>(form, "MaxDrainMilliseconds").Value = 15000;
            C<CheckBox>(form, "AdvancedToggle").Checked = false;
            var request = ReadRequest(form); var arguments = InstallClient.BuildArguments(request);
            Require(request.Name == "SYNTHETIC 自定义记录名" && request.Installer == replacement && request.WatchRoot == watch,
                "manual record name survives changing the installer and exact scope remains selected");
            Require(request.SnapshotOnly && request.Database == Path.Combine(root, "chosen custom.db")
                && request.Arguments.SequenceEqual(new[] { "含 空格", "%_literal" }) && request.RegistryKey == @"Software\Contain\FirstUseSynthetic"
                && request.SettleMilliseconds == 2000 && request.MaxDrainMilliseconds == 15000,
                "collapsed advanced controls preserve every effective request value");
            Require(arguments.Contains("--no-etw") && !arguments.Contains("--require-etw") && arguments.TakeLast(2).SequenceEqual(request.Arguments),
                "explicit snapshot and literal argument boundaries reach the production CLI argument builder");
            var summary = C<Control>(form, "CaptureConfigurationPreview");
            Require(summary.Visible && summary.Text.Contains(request.Name, StringComparison.Ordinal) && summary.Text.Contains("快照", StringComparison.Ordinal)
                && summary.Text.Contains(request.Database, StringComparison.Ordinal), "changed name, destination and explicit snapshot remain clear near Start when collapsed");
            Require(!C<Button>(form, "CheckEnvironment").Visible && !File.Exists(request.Database) && !form.IsActive,
                "advanced configuration remains optional and does not start recording");
            form.Close(); await Task.CompletedTask;
        });
        await Check(Plan[4], async () =>
        {
            var root = TestRecordIsolation.Create("first-use-synthetic-permission"); using var isolation = TestRecordIsolation.Use(root);
            var watch = Path.Combine(root, "observed"); Directory.CreateDirectory(watch);
            var invocations = Path.Combine(root, "synthetic-invocations.jsonl");
            var previousEvidence = Path.Combine(root, "prior-synthetic-evidence.db");
            File.Copy(Path.Combine(options.Fixtures, "interrupted.db"), previousEvidence, false);
            var evidenceHash = Hash(previousEvidence);
            var previous = Environment.GetEnvironmentVariable("CONTAIN_SYNTHETIC_STATUS_SCENARIO");
            var previousCounter = Environment.GetEnvironmentVariable("CONTAIN_SYNTHETIC_FIRST_USE_INVOCATIONS");
            var previousEvidenceVariable = Environment.GetEnvironmentVariable("CONTAIN_SYNTHETIC_FIRST_USE_EVIDENCE");
            Environment.SetEnvironmentVariable("CONTAIN_SYNTHETIC_STATUS_SCENARIO", "first_use_permissions");
            Environment.SetEnvironmentVariable("CONTAIN_SYNTHETIC_FIRST_USE_INVOCATIONS", invocations);
            Environment.SetEnvironmentVariable("CONTAIN_SYNTHETIC_FIRST_USE_EVIDENCE", previousEvidence);
            try
            {
                var opened = 0; var bridge = new InstallClient(Environment.ProcessPath!);
                using var form = new InstallForm(bridge, (_, _) => { opened++; return Task.CompletedTask; }); form.Show(owner);
                C<TextBox>(form, "InstallerPath").Text = Environment.ProcessPath!; C<TextBox>(form, "WatchRoot").Text = watch;
                var defaultDatabase = PersonalRecords.ForCurrentUser().DatabasePath;
                Require(!Directory.Exists(Path.GetDirectoryName(defaultDatabase)) && !File.Exists(invocations),
                    "fresh personal-library setup creates no directory, database or child merely from choosing the two ordinary inputs");
                var start = C<Button>(form, "StartRecording"); start.PerformClick(); var first = form.PendingOperation; start.PerformClick();
                Require(ReferenceEquals(first, form.PendingOperation), "immediate repeated Start cannot replace the first task");
                await first.WaitAsync(TimeSpan.FromSeconds(20));
                Require(form.Result is { CanRetryBeforeInstaller: true, Succeeded: false } && form.LastStatus is { InstallerStarted: false, EtwRequested: true }
                    && form.Visible && opened == 0, "trusted synthetic permission refusal leaves correction visible and never starts the installer or opens another session");
                Require(!C<CheckBox>(form, "SnapshotOnly").Checked && start.Enabled && C<Button>(form, "TaskDetails").Enabled,
                    "permission refusal never silently switches modes and retains details plus explicit retry");
                Require(C<Label>(form, "InstallCorrectionHint").Text.Contains("权限", StringComparison.Ordinal)
                    && C<Button>(form, "ReviewCaptureMode").Visible,
                    "actual backend permission-error shape is translated into an actionable Chinese correction and optional mode entry");
                var firstArgs = InvocationArguments(invocations);
                Require(firstArgs.Length == 1 && firstArgs[0].Contains("--require-etw") && !firstArgs[0].Contains("--no-etw"),
                    "double Start produced exactly one required-ETW child request");
                Require(Directory.Exists(Path.GetDirectoryName(defaultDatabase)) && Hash(defaultDatabase) == evidenceHash && Hash(previousEvidence) == evidenceHash,
                    "only explicit Start prepares the library; the synthetic existing-evidence-file refusal preserves its fixed historical bytes");
                var retained = await client.ListAsync(defaultDatabase);
                Require(retained.Data.GetArrayLength() == 1 && retained.Data.EnumerateArray().All(record => record.GetProperty("id").GetString() != form.LastStatus!.SessionId),
                    "synthetic permission refusal does not claim a new persisted session or auto-open the prior historical record");
                C<Button>(form, "ReviewCaptureMode").PerformClick();
                Require(C<CheckBox>(form, "AdvancedToggle").Checked && !C<CheckBox>(form, "SnapshotOnly").Checked,
                    "reviewing capture mode reveals the option without selecting it on the user's behalf");
                C<CheckBox>(form, "SnapshotOnly").Checked = true;
                C<TextBox>(form, "RecordName").Text = "SYNTHETIC explicit retry 中文";
                var custom = Path.Combine(root, "explicit retry.db"); TestRecordIsolation.SelectCustomDatabase(form, custom);
                C<CheckBox>(form, "AdvancedToggle").Checked = false;
                Require(C<Control>(form, "CaptureConfigurationPreview").Text.Contains("快照", StringComparison.Ordinal),
                    "explicit fallback remains visible while advanced options are collapsed");
                start.PerformClick(); var second = form.PendingOperation; start.PerformClick();
                Require(!ReferenceEquals(first, second) && ReferenceEquals(second, form.PendingOperation), "one explicit retry starts one new task");
                await second.WaitAsync(TimeSpan.FromSeconds(20));
                var all = InvocationArguments(invocations);
                Require(form.Result is { Succeeded: true } && all.Length == 2 && all[1].Contains("--no-etw") && !all[1].Contains("--require-etw")
                    && all[1].Contains("--name=SYNTHETIC explicit retry 中文") && all[1].Contains(custom) && all[1].Contains(watch),
                    "explicit snapshot retry uses the exact chosen name, database and scope once through real pipes");
                Require(!File.Exists(custom), "synthetic protocol exercise never fabricates a stored record or ETW evidence");
                Require(Hash(defaultDatabase) == evidenceHash && Hash(previousEvidence) == evidenceHash, "explicit retry leaves prior synthetic evidence byte-identical");
                form.Close();
            }
            finally
            {
                Environment.SetEnvironmentVariable("CONTAIN_SYNTHETIC_STATUS_SCENARIO", previous);
                Environment.SetEnvironmentVariable("CONTAIN_SYNTHETIC_FIRST_USE_INVOCATIONS", previousCounter);
                Environment.SetEnvironmentVariable("CONTAIN_SYNTHETIC_FIRST_USE_EVIDENCE", previousEvidenceVariable);
            }
        });
        var accepted = priorChecksPassed && checks.Count == Plan.Length && checks.All(check => check.Status == "passed");
        File.WriteAllText(Path.Combine(options.PrivateOutput, "first-use-configuration-details.log"), privateErrors.ToString());
        File.WriteAllText(Path.Combine(options.Output, "first-use-configuration-summary.json"), JsonSerializer.Serialize(new
        {
            schema_version = 1, protocol_revision = "contain-desktop-first-use-configuration-verification-1", source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(), windows_version = Environment.OSVersion.Version.ToString(), synthetic = true,
            real_etw_executed = false, real_user_records_accessed = false,
            package_manifest_sha256 = Hash(Path.Combine(options.Package, "BUILD.json")), viewer_assembly_sha256 = Hash(typeof(ViewerForm).Assembly.Location),
            viewer_launcher_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.exe")), contain_sha256 = Hash(client.ExecutablePath),
            planned_checks = Plan, checks, assertions, passed = checks.Count(check => check.Status == "passed"), failed = checks.Count(check => check.Status == "failed"),
            skipped = 0, not_run = Plan.Length - checks.Count, completed = checks.Count == Plan.Length, accepted, retries = 0,
            scope = "Frozen production form controls and real argument/pipe handling over marked isolated data. Picker results are injected here; actual native pickers and the one real installation are separate install verification. Permission refusal is explicitly synthetic."
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return accepted;
    }

    private async Task Check(string name, Func<Task> action)
    {
        Console.WriteLine("first-use-configuration " + name + " started"); Console.Out.Flush();
        try { await action(); checks.Add(new(name, "passed")); }
        catch (Exception error)
        {
            checks.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : "first-use configuration verification failed"));
            privateErrors.AppendLine(name).AppendLine(error.ToString());
        }
    }
    private void Require(bool value, string message) { if (!value) throw new TestFailureException(message); assertions++; }
    private static T C<T>(Control owner, string name) where T : Control => (T)owner.Controls.Find(name, true).Single();
    private static InstallRequest ReadRequest(InstallForm form) => (InstallRequest)typeof(InstallForm).GetMethod("Request", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, null)!;
    private static string[][] InvocationArguments(string path) => File.ReadAllLines(path).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static async Task Idle(ViewerForm form) { await Task.Delay(80); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(60)); await Task.Delay(80); }
}

internal sealed class ConfigurationTestDialogs : IInstallDialogs
{
    internal string? Installer { get; set; }
    internal string? WatchRoot { get; set; }
    internal string? Database { get; set; }
    internal int InstallerCalls { get; private set; }
    internal int WatchCalls { get; private set; }
    internal int DatabaseCalls { get; private set; }
    public string? ChooseInstaller(IWin32Window owner) { InstallerCalls++; return Installer; }
    public string? ChooseWatchRoot(IWin32Window owner) { WatchCalls++; return WatchRoot; }
    public string? ChooseSaveDatabase(IWin32Window owner) { DatabaseCalls++; return Database; }
}
