using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using Contain.Desktop.Controls;
using Wpf.Ui.Controls;
using Application = System.Windows.Application;

namespace Contain.Desktop.Controls.Tests;

/// <summary>One fixed external UIA diagnosis of the unchanged M2 control integration.</summary>
internal static class ExternalProbe
{
    private static readonly string[] TargetIds = ["NewInstall", "ControlButton"];
    internal static readonly string[] GatePlan =
    [
        "external_unique_button_identity", "uia_navigation_invokes_exactly_once", "disabled_navigation_refuses_invoke",
        "reenabled_navigation_invokes_once", "keyboard_navigation_invokes_once", "owned_host_normal_exit"
    ];

    internal static int RunHost()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
              <ResourceDictionary.MergedDictionaries><ui:ThemesDictionary Theme="Light"/><ui:ControlsDictionary/></ResourceDictionary.MergedDictionaries>
            </ResourceDictionary>
            """);
        var navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, IsPaneOpen = true, OpenPaneLength = 192 };
        var action = new ActionNavigationItem { Caption = "新建记录", ActionId = "NewInstall", Symbol = Wpf.Ui.Controls.SymbolRegular.DocumentAdd24 };
        navigation.MenuItems.Add(action);
        ActionNavigationItem.ConfigureNavigation(navigation);
        var ordinary = new System.Windows.Controls.Button { Content = "SYNTHETIC control button", Padding = new Thickness(12), Margin = new Thickness(8) };
        AutomationProperties.SetAutomationId(ordinary, "ControlButton");
        var status = new System.Windows.Controls.TextBlock { Text = "NewInstall=0;ControlButton=0", Margin = new Thickness(12) };
        AutomationProperties.SetAutomationId(status, "ProbeStatus");
        var actionClicks = 0; var ordinaryClicks = 0;
        action.Click += (_, _) => status.Text = $"NewInstall={++actionClicks};ControlButton={ordinaryClicks}";
        ordinary.Click += (_, _) =>
        {
            action.IsEnabled = !action.IsEnabled;
            status.Text = $"NewInstall={actionClicks};ControlButton={++ordinaryClicks}";
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TitleBar { Title = "Contain controlled external UIA probe" };
        grid.Children.Add(title); grid.Children.Add(navigation); Grid.SetRow(navigation, 1);
        var footer = new StackPanel { Orientation = Orientation.Horizontal }; footer.Children.Add(ordinary); footer.Children.Add(status);
        grid.Children.Add(footer); Grid.SetRow(footer, 2);
        var window = new FluentWindow
        {
            Title = "Contain controlled external UIA probe", Width = 800, Height = 600, ExtendsContentIntoTitleBar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = grid
        };
        return app.Run(window);
    }

    internal static async Task<int> RunAsync(string output, bool gate = false)
    {
        Directory.CreateDirectory(output);
        var summaryPath = Path.Combine(output, gate ? "navigation-external-checks.json" : "navigation-external-probe.json");
        if (File.Exists(summaryPath)) throw new IOException("Previous external probe evidence must not be overwritten.");
        await Write(Path.Combine(output, "navigation-external-plan.json"), new
        {
            schema_version = 1, protocol = "contain-wpf-external-navigation-plan-1", source_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
            fixed_ids = TargetIds, hosts = 1, maximum_matches_per_id = 8, maximum_owned_windows = 4, maximum_ancestor_depth = 3,
            discovery_deadline_ms = 20000, uia_call_deadline_ms = 10000, click_observation_deadline_ms = 5000, close_deadline_ms = 5000,
            native_windows = true, synthetic = true, real_etw_executed = false, actual_packaged_executable = false,
            product_control_source = "desktop/Contain.Desktop/Controls/ActionNavigationItem.cs", retries = 0, regression_gate = gate,
            purpose = gate ? "Verify the actual production action composition from another process before packaged desktop acceptance."
                : "Diagnose external UIA export of the current control composition; no desktop acceptance is claimed."
        });
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Current host identity missing."))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--external-host");
        using var host = Process.Start(start) ?? throw new IOException("Controlled child host did not start.");
        _ = host.Handle;
        var stdout = host.StandardOutput.ReadToEndAsync(); var stderr = host.StandardError.ReadToEndAsync();
        var snapshots = new List<object>(); var invocation = new List<object>();
        var gateChecks = new List<object>(); var gatePassed = 0; var gateFailed = false;
        var completed = false; string? errorType = null; var normalExit = false; var forcedExit = false; var reproduced = false;
        try
        {
            await Wait(host, async () => await Uia(() => Find(host.Id, "NewInstall").Length > 0), TimeSpan.FromSeconds(20));
            if (gate)
            {
                foreach (var id in TargetIds) snapshots.Add(await Uia(() => Snapshot(host.Id, id)));
                await Check(GatePlan[0], async () =>
                {
                    foreach (var id in TargetIds)
                    {
                        var elements = await Uia(() => Find(host.Id, id));
                        Require(elements.Length == 1, "Exactly one stable action ID.");
                        await Uia(() =>
                        {
                            var element = elements[0]; var info = element.Current;
                            Require(info.ProcessId == host.Id && info.IsEnabled && !info.IsOffscreen, "Owned visibly enabled action.");
                            Require(info.ControlType == ControlType.Button && info.Name.Length > 0, "Real button role and accessible name.");
                            Require(element.TryGetCurrentPattern(InvokePattern.Pattern, out _), "External InvokePattern available.");
                            return true;
                        });
                    }
                });
                await Check(GatePlan[1], async () => { await Invoke("NewInstall"); await Counter(1, 0); });
                await Check(GatePlan[2], async () =>
                {
                    await Invoke("ControlButton"); await Counter(1, 1);
                    await Wait(host, async () => await Uia(() => !Find(host.Id, "NewInstall").Single().Current.IsEnabled), TimeSpan.FromSeconds(5));
                    try { await Invoke("NewInstall"); throw new InvalidOperationException("Disabled invocation succeeded."); }
                    catch (ElementNotEnabledException) { }
                    await Counter(1, 1);
                });
                await Check(GatePlan[3], async () =>
                {
                    await Invoke("ControlButton"); await Counter(1, 2);
                    await Wait(host, async () => await Uia(() => Find(host.Id, "NewInstall").Single().Current.IsEnabled), TimeSpan.FromSeconds(5));
                    await Invoke("NewInstall"); await Counter(2, 2);
                });
                await Check(GatePlan[4], async () =>
                {
                    await Uia(() => { host.Refresh(); SetForegroundWindow(host.MainWindowHandle); Find(host.Id, "NewInstall").Single().SetFocus(); return true; });
                    await Wait(host, async () => await Uia(() => Find(host.Id, "NewInstall").Single().Current.HasKeyboardFocus), TimeSpan.FromSeconds(5));
                    await Sta(() =>
                    {
                        GetWindowThreadProcessId(GetForegroundWindow(), out var owner);
                        Require(owner == host.Id, "Keyboard belongs to exact host.");
                        System.Windows.Forms.SendKeys.SendWait(" ");
                    });
                    await Counter(3, 2);
                });
                completed = true;
            }
            else
            {
            foreach (var id in TargetIds)
            {
                var snapshot = await Uia(() => Snapshot(host.Id, id)); snapshots.Add(snapshot);
                var elements = await Uia(() => Find(host.Id, id));
                if (elements.Length != 1)
                { invocation.Add(new { id, attempted = false, reason = "missing_or_duplicate_exact_id", count = elements.Length }); continue; }
                var element = elements[0];
                if (await Uia(() => element.Current.ProcessId) != host.Id)
                { invocation.Add(new { id, attempted = false, reason = "target_process_identity_differs" }); continue; }
                var supported = await Uia(() => element.TryGetCurrentPattern(InvokePattern.Pattern, out _));
                if (!supported)
                {
                    if (id == "NewInstall") reproduced = true;
                    invocation.Add(new { id, attempted = false, reason = "InvokePattern_unavailable" }); continue;
                }
                string? actionError = null; var observed = false;
                try
                {
                    await Uia(() => { ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return true; });
                    await Wait(host, async () => await Uia(() =>
                    {
                        var status = Find(host.Id, "ProbeStatus").Single();
                        return status.Current.Name.Contains(id + "=1", StringComparison.Ordinal);
                    }), TimeSpan.FromSeconds(5));
                    observed = true;
                }
                catch (Exception error) { actionError = error.GetType().Name; }
                invocation.Add(new { id, attempted = true, click_observed = observed, error_type = actionError });
            }
            completed = snapshots.Count == TargetIds.Length;
            }
        }
        catch (Exception error) { errorType = error.GetType().Name; }
        finally
        {
            try
            {
                if (!host.HasExited) host.CloseMainWindow();
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); normalExit = host.ExitCode == 0;
            }
            catch (TimeoutException)
            {
                // This exact retained process is a synthetic UI-only child, never a product/installer tree.
                host.Kill(entireProcessTree: false); forcedExit = true;
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (gate)
            {
                if (completed && normalExit)
                { gateChecks.Add(new { name = GatePlan[5], status = "passed", error_type = (string?)null }); gatePassed++; }
                else if (completed)
                { gateChecks.Add(new { name = GatePlan[5], status = "failed", error_type = "normal_exit_failed" }); gateFailed = true; }
                foreach (var name in GatePlan.Skip(gateChecks.Count)) gateChecks.Add(new { name, status = "not_run", error_type = "prerequisite_failed" });
            }
            await Write(summaryPath, new
            {
                schema_version = 1, protocol = "contain-wpf-external-navigation-probe-1", source_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                native_windows = true, synthetic = true, real_etw_executed = false, actual_packaged_executable = false,
                diagnostic_completed = completed && normalExit && errorType is null,
                accepted = gate && gatePassed == GatePlan.Length && !gateFailed && errorType is null && normalExit,
                external_uia = true, host_count = 1, regression_gate = gate, planned_checks = gate ? GatePlan : [], checks = gateChecks,
                passed = gatePassed, failed = gateFailed ? 1 : 0, not_run = gate ? GatePlan.Length - gatePassed - (gateFailed ? 1 : 0) : 0,
                missing_custom_invoke_reproduced = reproduced, host_process_id = host.Id, normal_exit = normalExit, forced_exit = forcedExit,
                exit_code = host.HasExited ? (int?)host.ExitCode : null, error_type = errorType,
                snapshots, invocation, stdout_characters = host.HasExited ? (await stdout).Length : (int?)null,
                stderr_characters = host.HasExited ? (await stderr).Length : (int?)null, retries = 0,
                note = "A completed diagnosis is not packaged desktop acceptance; M1 and M2 failures remain retained."
            });
            if (gate) await Combine(output, summaryPath);
        }
        return completed && normalExit && errorType is null && (!gate || gatePassed == GatePlan.Length && !gateFailed) ? 0 : 1;

        async Task Check(string name, Func<Task> action)
        {
            try { await action(); gateChecks.Add(new { name, status = "passed", error_type = (string?)null }); gatePassed++; }
            catch (Exception error) { gateFailed = true; gateChecks.Add(new { name, status = "failed", error_type = error.GetType().Name }); throw; }
        }
        Task Invoke(string id) => Uia(() => { ((InvokePattern)Find(host.Id, id).Single().GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return true; });
        Task Counter(int action, int ordinary) => Wait(host, async () => await Uia(() => Find(host.Id, "ProbeStatus").Single().Current.Name
            == $"NewInstall={action};ControlButton={ordinary}"), TimeSpan.FromSeconds(5));
    }

    private static async Task Combine(string output, string externalPath)
    {
        var target = Path.Combine(output, "navigation-control-summary.json");
        var local = JsonNode.Parse(await File.ReadAllTextAsync(target))!.AsObject();
        var external = JsonNode.Parse(await File.ReadAllTextAsync(externalPath))!.AsObject();
        File.Copy(target, Path.Combine(output, "navigation-local-summary.json"), false);
        local["protocol"] = "contain-wpf-action-navigation-regression-2";
        foreach (var item in external["planned_checks"]!.AsArray()) local["planned_checks"]!.AsArray().Add(item!.DeepClone());
        foreach (var item in external["checks"]!.AsArray()) local["checks"]!.AsArray().Add(item!.DeepClone());
        foreach (var key in new[] { "passed", "failed", "not_run" }) local[key] = local[key]!.GetValue<int>() + external[key]!.GetValue<int>();
        local["accepted"] = local["accepted"]!.GetValue<bool>() && external["accepted"]!.GetValue<bool>();
        local["external_uia"] = true; local["host_count"] = 1;
        local["external_snapshots"] = external["snapshots"]!.DeepClone();
        local["normal_exit"] = external["normal_exit"]!.DeepClone();
        local["forced_exit"] = external["forced_exit"]!.DeepClone();
        await File.WriteAllTextAsync(target, local.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Snapshot(int processId, string id)
    {
        var matches = Find(processId, id);
        return new
        {
            id, expected_process_id = processId, matched_count = matches.Length, matches = matches.Select(element =>
            {
                var info = element.Current; var ancestors = new List<object>(); var parent = element;
                for (var depth = 0; depth < 3; depth++)
                {
                    parent = TreeWalker.RawViewWalker.GetParent(parent);
                    if (parent is null || parent.Current.ProcessId != processId) break;
                    ancestors.Add(new { depth = depth + 1, class_name = parent.Current.ClassName, control_type = parent.Current.ControlType.ProgrammaticName, automation_id = parent.Current.AutomationId });
                }
                return new
                {
                    automation_id = info.AutomationId, class_name = info.ClassName, control_type = info.ControlType.ProgrammaticName,
                    name = info.Name, process_id = info.ProcessId, enabled = info.IsEnabled, offscreen = info.IsOffscreen,
                    patterns = element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).Order().ToArray(), ancestors
                };
            }).ToArray()
        };
    }
    private static AutomationElement[] Find(int processId, string id)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
        if (windows.Count > 4) throw new InvalidOperationException("Controlled host window bound exceeded.");
        var result = new List<AutomationElement>();
        foreach (AutomationElement window in windows)
            foreach (AutomationElement element in window.FindAll(TreeScope.Element | TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id)))
            {
                if (result.Count >= 8) throw new InvalidOperationException("Exact-ID match bound exceeded.");
                result.Add(element);
            }
        return result.ToArray();
    }
    private static Task<T> Uia<T>(Func<T> action) => Task.Run(action).WaitAsync(TimeSpan.FromSeconds(10));
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int owner);
    private static async Task Wait(Process host, Func<Task<bool>> condition, TimeSpan deadline)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < deadline)
        {
            if (host.HasExited) throw new InvalidOperationException("Controlled host exited early.");
            var remaining = deadline - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            if (await condition().WaitAsync(remaining)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Controlled external UIA observation deadline.");
    }
    private static Task Write(string path, object value) => File.WriteAllTextAsync(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
