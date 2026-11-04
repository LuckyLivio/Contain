using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

/// <summary>
/// Actual Windows controls and CLI, using the existing synthetic read-only fixture.
/// Explicit Scale/font stress is deliberately distinct from a real monitor DPI change.
/// It reproduces the old fixed-row/large-text conflict without claiming Parallels coverage.
/// </summary>
internal sealed class LayoutVerification(Options options, bool viewerChecksPassed)
{
    private static readonly string[] Plan =
    [
        "native_default_layout_resize_maximize", "simulated_150_percent_layout",
        "simulated_200_percent_layout", "unchanged_read_only_fixture_after_layout"
    ];
    private readonly List<CheckResult> checks = [];
    private readonly List<ImageResult> screenshots = [];
    private readonly List<object> conditions = [];
    private readonly StringBuilder privateLog = new();
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private int assertions;
    private bool completed;

    public async Task<bool> RunAsync()
    {
        try
        {
            if (!viewerChecksPassed) return false;
            var database = Path.Combine(Path.GetPathRoot(options.Fixtures)!, "Contain Viewer Synthetic",
                options.Source[..12], "数据库 中文 & $ ; [x]", "finished 记录.db");
            foreach (var path in new[] { database, Path.Combine(options.Fixtures, "finished.db"), Path.Combine(options.Package, "samples", "viewer-demo.db") })
                hashes.Add(path, Hash(path));
            await Check(Plan[0], () => VerifyCondition(database, Plan[0], 1));
            await Check(Plan[1], () => VerifyCondition(database, Plan[1], 1.5f));
            await Check(Plan[2], () => VerifyCondition(database, Plan[2], 2));
            await Check(Plan[3], () =>
            {
                Require(hashes.Count == 3, "all source and opened synthetic databases fingerprinted");
                foreach (var pair in hashes) Require(Hash(pair.Key) == pair.Value, "layout navigation keeps database bytes unchanged");
                return Task.CompletedTask;
            });
            completed = true;
        }
        catch (Exception error)
        {
            checks.Add(new("layout_harness_execution", "failed", error.GetType().Name));
            privateLog.AppendLine(error.ToString());
        }
        finally { WriteSummary(); }
        return Accepted;
    }

    private bool Accepted => completed && OperatingSystem.IsWindows() && checks.Count == Plan.Length && checks.All(check => check.Status == "passed");

    private async Task VerifyCondition(string database, string name, float scale)
    {
        var initialAssertions = assertions;
        var dialogs = new TestDialogs { Database = database };
        using var viewer = new ViewerForm(new CoreClient(Path.Combine(options.Package, "contain.exe")), dialogs);
        ApplyStress(viewer, scale);
        viewer.Show();
        await Settle(viewer);
        var area = Screen.FromControl(viewer).WorkingArea;
        var dpi = viewer.DeviceDpi;
        try
        {
            Require(dpi > 0, "Windows reports actual device DPI");
            C<Button>(viewer, "OpenDatabase").PerformClick();
            await Idle(viewer);
            Require(C<DataGridView>(viewer, "Sessions").Rows.Count == 1, "production open reads one existing synthetic record");
            var sessions = C<DataGridView>(viewer, "Sessions");
            sessions.Rows[0].Selected = true;
            sessions.CurrentCell = sessions.Rows[0].Cells[0];
            await Idle(viewer);

            // No test-side MinimumSize override: the production scrolling/minimum policy
            // must make its controls reachable in the actual runner's work area.
            viewer.WindowState = FormWindowState.Normal;
            viewer.Bounds = new Rectangle(area.Left + 12, area.Top + 12,
                Math.Min(area.Width - 24, (int)(1100 * scale)), Math.Min(area.Height - 24, (int)(760 * scale)));
            await Settle(viewer);
            await VerifyViewer(viewer);
            viewer.WindowState = FormWindowState.Maximized;
            await Settle(viewer);
            Require(viewer.Bounds.Left >= area.Left - 12 && viewer.Bounds.Top >= area.Top - 12
                && viewer.Bounds.Right <= area.Right + 12 && viewer.Bounds.Bottom <= area.Bottom + 12,
                "maximized production window fits actual Windows work area");
            await VerifyViewer(viewer);
            Screenshot(viewer, name + "-viewer.png");

            using var install = new InstallForm(new InstallClient(Path.Combine(options.Package, "contain.exe")), (_, _) => Task.CompletedTask);
            ApplyStress(install, scale);
            install.Show(viewer);
            await Settle(install);
            Require(!C<CheckBox>(install, "AdvancedToggle").Checked, "advanced parameters initially collapsed");
            var parent = Path.GetDirectoryName(database)!;
            var watch = Path.Combine(parent, "layout-observation-only");
            Directory.CreateDirectory(watch); // Explicit disposable test setup, never product target creation.
            C<TextBox>(install, "InstallerPath").Text = options.InstallFixture ?? Path.Combine(options.Package, "contain.exe");
            Require(C<TextBox>(install, "WatchRoot").Text.Length == 0, "choosing installer location does not infer observation scope");
            C<TextBox>(install, "RecordName").Text = "SYNTHETIC 布局检查 & 逐项范围";
            C<TextBox>(install, "WatchRoot").Text = watch;
            var destination = Path.Combine(parent, "layout-save-not-started.db");
            C<TextBox>(install, "DatabaseSavePath").Text = destination;
            C<CheckBox>(install, "AdvancedToggle").Checked = true;
            C<TextBox>(install, "RegistryKey").Text = @"Software\Contain\LayoutSynthetic";
            install.WindowState = FormWindowState.Normal;
            install.Bounds = new Rectangle(area.Left + 12, area.Top + 12, Math.Min(area.Width - 24, (int)(880 * scale)), Math.Min(area.Height - 24, (int)(790 * scale)));
            await Settle(install);
            await VerifyInstall(install, watch, destination);
            install.WindowState = FormWindowState.Maximized;
            await Settle(install);
            await VerifyInstall(install, watch, destination);
            Screenshot(install, name + "-install.png");
            Require(!install.IsActive && !install.IsChecking && !File.Exists(destination), "layout and configuration never launch installer or create database");
            install.Close();
        }
        finally
        {
            conditions.Add(new
            {
                name, device_dpi = dpi, requested_scale = scale,
                test_method = scale == 1 ? "native_default" : "simulated_native_controls",
                system_high_dpi_verified = false,
                working_area_width = area.Width, working_area_height = area.Height,
                assertions = assertions - initialAssertions,
                explanation = scale == 1
                    ? "Actual Windows DeviceDpi; real resize, maximize, tab, paging and explain actions."
                    : "Injected Control.Scale and font enlargement on real Windows controls. Does not change system/monitor DPI and does not validate Windows 11/Parallels DPI transitions."
            });
            viewer.Close();
        }
    }

    private async Task VerifyViewer(ViewerForm viewer)
    {
        var tabs = C<TabControl>(viewer, "Tabs");
        foreach (var index in new[] { 0, 1, 2, 0, 2, 1, 2 })
        {
            tabs.SelectedIndex = index;
            await Idle(viewer);
            Require(C<DataGridView>(viewer, "Changes").Visible == (index == 1), "only selected tab exposes state-change grid");
            Require(C<DataGridView>(viewer, "Events").Visible == (index == 2), "only selected tab exposes event grid");
            Require(C<TextBox>(viewer, "Overview").Visible == (index == 0), "overview visibility follows selected tab");
            ValidateText(viewer);
        }
        var events = C<DataGridView>(viewer, "Events");
        Require(events.ClientSize.Width >= events.Font.Height * 6 && events.ClientSize.Height >= events.ColumnHeadersHeight + events.Font.Height,
            "selected event grid has room for its header and a readable row");
        if (C<Button>(viewer, "PreviousEvents").Enabled)
        { await Reach(C<Button>(viewer, "PreviousEvents")); C<Button>(viewer, "PreviousEvents").PerformClick(); await Idle(viewer); }
        Require(events.Rows.Count == 50, "first event page remains bounded after tab switching");
        await Reach(C<Button>(viewer, "NextEvents"));
        C<Button>(viewer, "NextEvents").PerformClick();
        await Idle(viewer);
        Require(events.Rows.Count == 10 && !C<Button>(viewer, "NextEvents").Enabled, "visible next button loads final ten-event page");
        await Reach(C<Button>(viewer, "PreviousEvents"));
        C<Button>(viewer, "PreviousEvents").PerformClick();
        await Idle(viewer);
        Require(events.Rows.Count == 50, "visible previous button returns to first page");
        events.ClearSelection(); events.Rows[0].Selected = true; events.CurrentCell = events.Rows[0].Cells[0];
        await Idle(viewer);
        var id = ((JsonElement)events.Rows[0].Tag!).GetProperty("id").GetString()!;
        var details = C<TextBox>(viewer, "EventDetails");
        Require(details.ClientSize.Height >= details.Font.Height * 2, "selected event explanation retains a readable text area");
        Require(details.Text.Contains(id, StringComparison.Ordinal), "resized explanation belongs to selected event ID");
        await Reach(C<CheckBox>(viewer, "RawToggle"));
        C<CheckBox>(viewer, "RawToggle").Checked = true;
        await Settle(viewer);
        var raw = C<TextBox>(viewer, "RawDetails");
        Require(raw.Visible && !details.Visible && raw.Text.Contains(id, StringComparison.Ordinal), "raw and human explanation are mutually exclusive after tab and resize");
        Require(!BoundsOnForm(events, viewer).IntersectsWith(BoundsOnForm(raw, viewer)), "event grid and raw explanation do not overlap");
        C<CheckBox>(viewer, "RawToggle").Checked = false;
        await Settle(viewer);
        Require(details.Visible && !raw.Visible, "human explanation returns without overlaid raw control");
        Require(!BoundsOnForm(events, viewer).IntersectsWith(BoundsOnForm(details, viewer)), "event grid and human explanation do not overlap");
        foreach (var name in new[] { "NewInstall", "OpenDatabase", "ExportReport", "PreviousEvents", "NextEvents", "CopyEventId" })
        { await Reach(C<Button>(viewer, name)); ValidateCaption(C<Button>(viewer, name)); }
        Require(AllText(viewer).Contains("本页", StringComparison.Ordinal), "current-page filtering and report boundary remain visible");
    }

    private async Task VerifyInstall(InstallForm install, string watch, string destination)
    {
        var preview = install.Controls.Find("CaptureConfigurationPreview", true).Single();
        Require(preview.Text.Contains(watch, StringComparison.Ordinal) && preview.Text.Contains(destination, StringComparison.Ordinal)
            && preview.Text.Contains(@"Software\Contain\LayoutSynthetic", StringComparison.Ordinal), "pre-start preview displays exact observation, registry and database choices");
        foreach (var name in new[] { "InstallerPath", "RecordName", "WatchRoot", "DatabaseSavePath", "InstallerArguments", "RegistryKey", "SettleMilliseconds", "MaxDrainMilliseconds", "SnapshotOnly", "AdvancedToggle", "ChooseInstaller", "ChooseWatchRoot", "ChooseSaveDatabase", "CheckEnvironment", "StartRecording", "ViewRecordedSession" })
        {
            var control = install.Controls.Find(name, true).Single();
            await Reach(control);
            if (control is ButtonBase button) ValidateCaption(button);
        }
        ValidateText(install);
    }

    private void ValidateText(System.Windows.Forms.Control root)
    {
        foreach (var control in Descendants(root).Where(control => control.Visible))
        {
            if (control is ButtonBase button) ValidateCaption(button);
            if (control is Label label && label.Text.Length > 0 && !label.AutoEllipsis)
            {
                var preferred = label.GetPreferredSize(new Size(Math.Max(1, label.ClientSize.Width), 0));
                Require(label.ClientSize.Height + 3 >= preferred.Height, "label preferred text height fits: " + SafeName(label));
            }
        }
    }

    private void ValidateCaption(ButtonBase button)
    {
        var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        Require(button.ClientSize.Width + 2 >= text.Width + (button is CheckBox ? 20 : 8), "button caption width fits: " + SafeName(button));
        Require(button.ClientSize.Height + 2 >= text.Height + 4, "button caption height fits: " + SafeName(button));
    }

    private async Task Reach(System.Windows.Forms.Control control)
    {
        Require(control.Visible, "control is visible in selected page: " + SafeName(control));
        for (var ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor is ScrollableControl { AutoScroll: true } scroll) scroll.ScrollControlIntoView(control);
        await Task.Delay(20);
        for (var ancestor = control.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is not ScrollableControl { AutoScroll: true } scroll) continue;
            var rectangle = scroll.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
            var viewport = scroll.ClientRectangle;
            viewport.Inflate(3, 3);
            Require(viewport.Contains(rectangle), "scrolling makes complete control reachable: " + SafeName(control));
        }
    }

    private static void ApplyStress(Form form, float scale)
    {
        if (scale == 1) return;
        var originalFonts = new[] { (System.Windows.Forms.Control)form }.Concat(Descendants(form))
            .Select(control => (Control: control, Font: control.Font)).ToArray();
        form.Scale(new SizeF(scale, scale));
        foreach (var item in originalFonts)
            item.Control.Font = new Font(item.Font.FontFamily, item.Font.Size * scale, item.Font.Style, item.Font.Unit);
    }

    private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent)
    {
        foreach (System.Windows.Forms.Control control in parent.Controls)
        { yield return control; foreach (var nested in Descendants(control)) yield return nested; }
    }

    private static string SafeName(System.Windows.Forms.Control control) => control.Name.Length > 0 ? control.Name : control.GetType().Name;
    private static Rectangle BoundsOnForm(System.Windows.Forms.Control control, Form form) => form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
    private static string AllText(System.Windows.Forms.Control parent) => parent.Text + "\n" + string.Join("\n", parent.Controls.Cast<System.Windows.Forms.Control>().Select(AllText));
    private static T C<T>(System.Windows.Forms.Control parent, string name) where T : System.Windows.Forms.Control => parent.Controls.Find(name, true).Single() as T ?? throw new InvalidOperationException("Control unavailable: " + name);
    private static async Task Settle(Form form) { form.PerformLayout(); await Task.Delay(100); }
    private static async Task Idle(ViewerForm form)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await Task.Delay(30);
            await form.PendingOperation.WaitAsync(TimeSpan.FromSeconds(90));
            if (!form.IsBusy) { await Task.Delay(50); if (!form.IsBusy) return; }
        } while (watch.Elapsed < TimeSpan.FromSeconds(100));
        throw new TimeoutException("Read-only layout operation did not settle.");
    }

    private async Task Check(string name, Func<Task> action)
    {
        try { await action(); checks.Add(new(name, "passed")); }
        catch (Exception error)
        {
            checks.Add(new(name, "failed", error.GetType().Name, error is TestFailureException ? error.Message : null));
            privateLog.AppendLine(name).AppendLine(error.ToString());
        }
    }
    private void Require(bool condition, string reason)
    { if (!condition) throw new TestFailureException(reason); assertions++; }
    private void Screenshot(Form form, string name)
    {
        var directory = Path.Combine(options.Output, "screenshots"); Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        var path = Path.Combine(directory, name); bitmap.Save(path, ImageFormat.Png);
        screenshots.Add(new(name, Hash(path), bitmap.Width, bitmap.Height));
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void WriteSummary()
    {
        Directory.CreateDirectory(options.Output); Directory.CreateDirectory(options.PrivateOutput);
        foreach (var name in Plan.Where(name => !checks.Any(check => check.Name == name))) checks.Add(new(name, "not_run", "viewer_prerequisite_or_harness_unavailable"));
        File.WriteAllText(Path.Combine(options.PrivateOutput, "layout-details.log"), privateLog.ToString(), new UTF8Encoding(false));
        var summary = new
        {
            schema_version = 1, protocol_revision = "contain-desktop-layout-verification-1", source_sha = options.Source,
            native_windows = OperatingSystem.IsWindows(), completed, accepted = Accepted, synthetic = true, real_etw_executed = false,
            package_manifest_sha256 = Hash(Path.Combine(options.Package, "BUILD.json")),
            viewer_assembly_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.dll")),
            viewer_launcher_sha256 = Hash(Path.Combine(options.Package, "Contain.Viewer.exe")), contain_sha256 = Hash(Path.Combine(options.Package, "contain.exe")),
            planned_checks = Plan, checks, conditions, screenshots, assertions_passed = assertions,
            passed = checks.Count(check => check.Status == "passed"), failed = checks.Count(check => check.Status == "failed"), skipped = 0, not_run = checks.Count(check => check.Status == "not_run"),
            manual_checks = new[] { new { name = "windows_11_parallels_actual_high_dpi", status = "not_run", reason = "CI Windows Server display is not the user's Windows 11/Parallels monitor; guest architecture and original DPI are unconfirmed." } },
            screenshot_method = "Actual production Windows controls rendered by DrawToBitmap; synthetic fixture/configuration only. Simulated scale/font images are explicitly named and are not actual high-DPI monitor evidence."
        };
        File.WriteAllText(Path.Combine(options.Output, "layout-summary.json"), JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"Desktop layout checks: {summary.passed} passed, {summary.failed} failed, {summary.not_run} not run; simulated high-DPI stress is not system high-DPI verification.");
    }
}
