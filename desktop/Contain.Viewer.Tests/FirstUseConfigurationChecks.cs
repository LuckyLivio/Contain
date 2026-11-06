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
    private object? folderGuidance;
    private object? followConfiguration;

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
            await VerifyFollowConfiguration(form, root, installDialogs);
            form.Close(); viewer.Close();
        });
        await Check(Plan[1], async () =>
        {
            var root = TestRecordIsolation.Create("first-use-observation"); using var isolation = TestRecordIsolation.Use(root);
            var records = PersonalRecords.ForCurrentUser(); var dialogs = new ConfigurationTestDialogs();
            var bridge = new InstallClient(client.ExecutablePath);
            using var form = new InstallForm(bridge, (_, _) => Task.CompletedTask, dialogs); form.Show(owner);
            TestRecordIsolation.SelectManualObservation(form);
            C<TextBox>(form, "InstallerPath").Text = client.ExecutablePath;
            Require(C<TextBox>(form, "WatchRoot").Text.Length == 0 && !C<Button>(form, "StartRecording").Enabled,
                "an executable alone cannot enable recording or imply a scope");
            var folderTitle = C<Label>(form, "WatchRootLabel");
            var folderNote = C<Label>(form, "WatchRootNote");
            var missingHint = C<Label>(form, "WatchRootHint").Text;
            Require(folderTitle.Text == "要记录哪个文件夹里的变化？"
                && folderNote.Text == "通常选软件准备安装到的已有文件夹。安装包的下载位置不代表安装目标；这里仅决定观察范围。"
                && folderTitle.Visible && folderNote.Visible && dialogs.WatchCalls == 0,
                "the visible ordinary page distinguishes installer location from installation target before opening a picker");
            Require(missingHint == "请选择一个已有文件夹，再开始记录。",
                "missing observation scope has an actionable ordinary-flow hint");
            Require(!C<CheckBox>(form, "AdvancedToggle").Checked && !C<Button>(form, "CheckEnvironment").Visible
                && !C<TextBox>(form, "RecordName").Visible && !C<CheckBox>(form, "SnapshotOnly").Visible,
                "ordinary setup hides optional diagnostics and technical choices");
            await VerifyNativeInputBoundary(form);
            var before = form.PendingOperation; C<Button>(form, "StartRecording").PerformClick(); await Task.Delay(80);
            Require(ReferenceEquals(before, form.PendingOperation) && !bridge.CollectorStarted && form.LastStatus is null
                && !Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "missing scope starts no collector or installer and creates no library");
            var watched = Path.Combine(root, "chosen existing 中文 %_ folder"); Directory.CreateDirectory(watched);
            dialogs.WatchRoot = watched; C<Button>(form, "ChooseWatchRoot").PerformClick();
            Require(C<TextBox>(form, "WatchRoot").ReadOnly && C<TextBox>(form, "WatchRoot").Text == watched
                && C<Button>(form, "StartRecording").Enabled, "a selected existing directory is shown exactly and enables explicit Start");
            folderGuidance = new { protocol_revision = "contain-folder-guidance-verification-1", title = folderTitle.Text,
                note = folderNote.Text, missing_hint = missingHint, visible_before_picker = true, explicit_selection_preserved = true };
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
            TestRecordIsolation.SelectManualObservation(form);
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
            TestRecordIsolation.SelectManualObservation(form);
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
                TestRecordIsolation.SelectManualObservation(form);
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
            folder_guidance_verification = folderGuidance,
            follow_configuration_verification = followConfiguration,
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

    private async Task VerifyFollowConfiguration(InstallForm form, string root, ConfigurationTestDialogs dialogs)
    {
        var follow = C<RadioButton>(form, "FollowInstallerMode");
        var manual = C<RadioButton>(form, "ManualWatchMode");
        var start = C<Button>(form, "StartRecording");
        Require(follow.Checked && !manual.Checked && C<TextBox>(form, "WatchRoot").Text.Length == 0 && start.Enabled,
            "the accepted executable alone enables default installer following without guessing a directory");
        Require(!C<Control>(form, "ManualObservation").Visible && !C<CheckBox>(form, "AdvancedToggle").Checked,
            "default following exposes no mandatory directory or advanced configuration");
        var request = ReadRequest(form);
        // Argument validation uses an existing isolated destination parent. Merely
        // configuring the real default library must still create no directories.
        var arguments = InstallClient.BuildArguments(request with { Database = Path.Combine(root, "argument-only.db") });
        Require(request.FollowInstaller && request.WatchRoot.Length == 0 && arguments.Contains("--follow-installer")
            && arguments.Contains("--require-etw") && !arguments.Contains("--watch") && !arguments.Contains("--no-etw")
            && !arguments.Any(argument => argument.StartsWith("--registry-key", StringComparison.Ordinal)),
            "default follow arguments explicitly request ETW and contain no preset directory or registry scope");
        C<Button>(form, "CopyInstallerPath").PerformClick();
        Require(Clipboard.GetText() == client.ExecutablePath, "installer path copy preserves the exact selected executable");
        var records = PersonalRecords.ForCurrentUser();
        Require(!Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "default follow configuration never creates the personal library");

        C<CheckBox>(form, "AdvancedToggle").Checked = true;
        C<CheckBox>(form, "SnapshotOnly").Checked = true;
        var statusControl = C<TextBox>(form, "InstallStatus");
        var correctionControl = C<Label>(form, "InstallCorrectionHint");
        var statusBeforeConflict = statusControl.Text;
        var correctionBeforeConflict = correctionControl.Text;
        var correctionWasVisible = correctionControl.Visible;
        start.PerformClick(); await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(20));
        Require(follow.Checked, "snapshot conflict never silently changes the selected follow mode");
        Require(C<CheckBox>(form, "SnapshotOnly").Checked, "snapshot conflict preserves the user's explicit snapshot selection");
        Require(form.LastStatus is null, "snapshot conflict is refused before any structured collector status or session");
        Require(form.Result is null, "snapshot conflict is refused before returning a collector result");
        Require(!form.IsActive, "snapshot conflict leaves no active recording after prelaunch refusal");
        Require(!Directory.Exists(Path.GetDirectoryName(records.DatabasePath)), "snapshot conflict creates no personal-library directory");
        var correction = C<Label>(form, "InstallCorrectionHint").Text + C<TextBox>(form, "InstallStatus").Text
            + C<Label>(form, "CaptureModeNote").Text;
        Require(correction.Contains("指定文件夹", StringComparison.Ordinal) && correction.Contains("快照", StringComparison.Ordinal),
            "snapshot conflict provides the explicit manual-folder correction next to the recording action");
        Require(correctionControl.Visible && (!correctionWasVisible || correctionControl.Text != correctionBeforeConflict)
            && correctionControl.Text.Contains("快照", StringComparison.Ordinal)
            && correctionControl.Text.Contains("指定文件夹", StringComparison.Ordinal),
            "the actual Start action exposes this conflict's new visible manual-folder correction");
        Require(statusControl.Visible && statusControl.Text != statusBeforeConflict
            && statusControl.Text.Contains("启动或记录失败：", StringComparison.Ordinal)
            && statusControl.Text.Contains("ETW", StringComparison.Ordinal),
            "the actual Start action reports a new prelaunch mode-validation failure in the visible status");
        C<CheckBox>(form, "SnapshotOnly").Checked = false;
        C<CheckBox>(form, "AdvancedToggle").Checked = false;

        manual.Checked = true;
        Require(!start.Enabled && C<Control>(form, "ManualObservation").Visible,
            "manual observation still requires an explicitly chosen existing folder");
        var watched = Path.Combine(root, "manual retained 中文 %_ folder"); Directory.CreateDirectory(watched);
        dialogs.WatchRoot = watched; C<Button>(form, "ChooseWatchRoot").PerformClick();
        Require(start.Enabled && C<TextBox>(form, "WatchRoot").Text == watched && !ReadRequest(form).FollowInstaller,
            "manual folder selection remains exact and uses the manual request mode");
        follow.Checked = true;
        var following = ReadRequest(form);
        var switchedArguments = InstallClient.BuildArguments(following with { Database = Path.Combine(root, "argument-only.db") });
        Require(following.FollowInstaller && following.WatchRoot.Length == 0 && !switchedArguments.Contains("--watch"),
            "returning to follow mode does not turn a remembered manual folder into a hidden preset scope");
        manual.Checked = true;
        Require(C<TextBox>(form, "WatchRoot").Text == watched && start.Enabled,
            "switching modes preserves the user's explicit manual selection for reuse");
        follow.Checked = true;
        followConfiguration = new
        {
            protocol_revision = "contain-follow-configuration-verification-1", accepted = true,
            default_follow_no_scope = true, start_enabled = true, no_watch_argument = true,
            manual_folder_required = true, manual_selection_preserved = true, snapshot_conflict_explicit = true
        };
    }

    private async Task VerifyNativeInputBoundary(InstallForm form)
    {
        var filename = C<Label>(form, "InstallerFileName");
        Require(filename.AccessibilityObject.Name == "安装器文件名" && filename.Text == Path.GetFileName(client.ExecutablePath)
            && PackagedDesktop.NativeVisibleText(filename.Handle, Environment.ProcessId) == filename.Text,
            "native caption reading returns the complete displayed filename independently of its fixed accessibility name");

        var start = C<Button>(form, "StartRecording");
        C<Panel>(form, "InstallViewport").ScrollControlIntoView(start);
        form.Activate(); await Task.Delay(80);
        var clicks = 0; EventHandler clicked = (_, _) => clicks++;
        var before = form.PendingOperation;
        start.Click += clicked;
        try
        {
            using var receipts = new NativeMouseReceipts(start);
            PackagedDesktop.SendNativeClick(start.Handle, Environment.ProcessId);
            PackagedDesktop.SendNativeClick(start.Handle, Environment.ProcessId);
            await PumpUntil(() => receipts.Ups >= 2);
            Require(receipts.Downs == 2 && receipts.Ups == 2,
                "two real mouse gestures reach the disabled Start region, including the native double-click message");
            Require(clicks == 0 && ReferenceEquals(before, form.PendingOperation) && form.LastStatus is null
                && !Directory.Exists(Path.GetDirectoryName(PersonalRecords.ForCurrentUser().DatabasePath)),
                "physical double-click on missing-scope Start invokes no recording handler and creates no library");
        }
        finally { start.Click -= clicked; }

        // A harmless real HWND provides a positive input control and independent
        // occlusion cases without ever enabling or invoking the installer action.
        using var probe = new Form { Text = "SYNTHETIC native input boundary", ClientSize = new Size(360, 160),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        using var target = new Button { Text = "SYNTHETIC click counter", Bounds = new Rectangle(20, 20, 280, 50) };
        using var overlay = new Button { Text = "SYNTHETIC covering sibling", Bounds = target.Bounds, Visible = false };
        probe.Controls.Add(target); probe.Controls.Add(overlay);
        var handled = 0; target.Click += (_, _) => handled++;
        probe.Show(form); probe.Activate(); await Task.Delay(80);
        PackagedDesktop.SendNativeClick(target.Handle, Environment.ProcessId);
        await PumpUntil(() => handled != 0);
        Require(handled == 1, "the same native sender delivers an enabled button click to its actual event handler");
        target.Enabled = false;
        Require(PackagedDesktop.NativeClickTargetIsExposed(target.Handle, Environment.ProcessId),
            "an unobstructed disabled native button remains a valid physical gesture target");
        overlay.Visible = true; overlay.BringToFront();
        foreach (var enabled in new[] { true, false })
        {
            target.Enabled = enabled;
            Require(!PackagedDesktop.NativeClickTargetIsExposed(target.Handle, Environment.ProcessId),
                "an overlapping sibling blocks the intended native target in both enabled and disabled states");
            var refused = false;
            try { PackagedDesktop.SendNativeClick(target.Handle, Environment.ProcessId); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "the native sender refuses an occluded target before injecting a click");
        }
        overlay.Hide();
        Require(PackagedDesktop.NativeClickTargetIsExposed(target.Handle, Environment.ProcessId)
            && !PackagedDesktop.NativeClickTargetIsExposed(target.Handle, Environment.ProcessId + 1),
            "hiding the covering sibling restores the intended target but cannot bypass process ownership");
        target.Hide();
        Require(!PackagedDesktop.NativeClickTargetIsExposed(target.Handle, Environment.ProcessId),
            "a hidden native target is never treated as clickable");
        Require(handled == 1, "rejected native targeting cases invoke no extra click handler");
        probe.Close(); form.Activate();
    }

    private static async Task PumpUntil(Func<bool> completed)
    {
        for (var attempt = 0; attempt < 100 && !completed(); attempt++) await Task.Delay(20);
    }

    /// <summary>Observes actual queued mouse input; never sends or handles an action.</summary>
    private sealed class NativeMouseReceipts : IMessageFilter, IDisposable
    {
        private readonly Rectangle region;
        private readonly HashSet<IntPtr> receivers = [];
        internal int Downs { get; private set; }
        internal int Ups { get; private set; }
        internal NativeMouseReceipts(Control target)
        {
            region = target.RectangleToScreen(target.ClientRectangle);
            for (Control? control = target; control is not null; control = control.Parent) receivers.Add(control.Handle);
            Application.AddMessageFilter(this);
        }
        public bool PreFilterMessage(ref Message message)
        {
            if (message.Msg is not (0x0201 or 0x0202 or 0x0203) || !receivers.Contains(message.HWnd)
                || Control.FromHandle(message.HWnd) is not { } receiver) return false;
            var packed = message.LParam.ToInt64();
            var point = new Point(unchecked((short)(packed & 0xffff)), unchecked((short)((packed >> 16) & 0xffff)));
            if (!region.Contains(receiver.PointToScreen(point))) return false;
            if (message.Msg == 0x0202) Ups++; else Downs++;
            return false;
        }
        public void Dispose() => Application.RemoveMessageFilter(this);
    }

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
