using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Markup;
using System.Windows.Threading;
using Contain.Desktop.Controls;
using Wpf.Ui.Controls;
using Application = System.Windows.Application;

namespace Contain.Desktop.Controls.Tests;

internal static class Program
{
    private static readonly string[] Plan =
    [
        "library_selection_route_reproduces_null_target_failure", "action_peer_has_name_id_button_and_invoke",
        "action_invoke_routes_exactly_one_existing_click", "disabled_action_rejects_invoke",
        "queued_action_disabled_before_dispatch_does_not_click", "reenabled_action_remains_usable"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native WPF STA required.");
        if (args.Length == 2 && args[0] == "--external-probe")
            return ExternalProbe.RunAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult();
        if (args.Length == 1 && args[0] == "--external-host") return ExternalProbe.RunHost();
        if (args.Length != 1) throw new ArgumentException("Expected one new public output directory.");
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var results = new List<object>(); var passed = 0; var failed = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
              <ResourceDictionary.MergedDictionaries><ui:ThemesDictionary Theme="Light"/><ui:ControlsDictionary/></ResourceDictionary.MergedDictionaries>
            </ResourceDictionary>
            """);
        var old = new NavigationViewItem { Content = "SYNTHETIC previous action" };
        var action = new ActionNavigationItem { Caption = "SYNTHETIC accessible action", ActionId = "SyntheticAction" };
        var navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, IsPaneOpen = true };
        navigation.MenuItems.Add(old); navigation.MenuItems.Add(action);
        ActionNavigationItem.ConfigureNavigation(navigation);
        var window = new Window { Title = "Contain isolated control contract", Width = 640, Height = 480, Content = navigation };
        var clicks = 0; action.Click += (_, _) => clicks++;
        window.Loaded += async (_, _) =>
        {
            try
            {
                var oldPeer = UIElementAutomationPeer.CreatePeerForElement(old) ?? throw new InvalidOperationException("Original peer missing.");
                var peer = UIElementAutomationPeer.CreatePeerForElement(action.ActionButton) ?? throw new InvalidOperationException("Actual inner action button peer missing.");
                var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider ?? throw new InvalidOperationException("Invoke provider missing.");
                await Check(Plan[0], () =>
                {
                    Require(old.TargetPageType is null, "Original action has no page target.");
                    var selection = oldPeer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider ?? throw new InvalidOperationException("Original library selection provider missing.");
                    try { selection.Select(); throw new Exception("Original null-target navigation unexpectedly succeeded."); }
                    catch (InvalidOperationException error) when (error.Message.Contains("TargetPageType", StringComparison.Ordinal)) { }
                    return Task.CompletedTask;
                });
                await Check(Plan[1], () =>
                {
                    Require(peer.GetName() == "SYNTHETIC accessible action" && peer.GetAutomationId() == "SyntheticAction", "Accessible name and ID preserved.");
                    Require(peer.GetAutomationControlType() == AutomationControlType.Button && peer.GetPattern(PatternInterface.SelectionItem) is null, "Action role does not expose broken page selection.");
                    return Task.CompletedTask;
                });
                await Check(Plan[2], async () =>
                {
                    invoke.Invoke(); await Pump(); Require(clicks == 1, "One invocation triggers one real routed Click.");
                    Require(action.TargetPageType is null, "Action does not fabricate a navigation target.");
                });
                await Check(Plan[3], async () =>
                {
                    action.IsEnabled = false;
                    try { invoke.Invoke(); throw new Exception("Disabled invocation unexpectedly succeeded."); }
                    catch (ElementNotEnabledException) { }
                    await Pump(); Require(clicks == 1, "Disabled invocation causes no Click.");
                });
                await Check(Plan[4], async () =>
                {
                    action.IsEnabled = true; invoke.Invoke(); action.IsEnabled = false;
                    await Pump(); Require(clicks == 1, "Queued invocation rechecks enabled state.");
                });
                await Check(Plan[5], async () =>
                {
                    action.IsEnabled = true; invoke.Invoke(); await Pump(); Require(clicks == 2, "Reenabled action still invokes once.");
                });
            }
            catch (Exception error)
            {
                failed = true;
                if (results.Count == passed) results.Add(new { name = Plan[Math.Min(passed, Plan.Length - 1)], status = "failed", error_type = error.GetType().Name });
            }
            finally
            {
                foreach (var name in Plan.Skip(results.Count)) results.Add(new { name, status = "not_run", error_type = "prerequisite_failed" });
                File.WriteAllText(Path.Combine(output, "navigation-control-summary.json"), JsonSerializer.Serialize(new
                {
                    schema_version = 1, protocol = "contain-wpf-action-navigation-regression-1", source_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                    native_windows = true, synthetic = true, real_etw_executed = false, actual_packaged_executable = false,
                    production_source_link = "desktop/Contain.Desktop/Controls/ActionNavigationItem.cs", wpf_ui_version = "4.3.0",
                    planned_checks = Plan, checks = results, passed, failed = failed ? 1 : 0, not_run = Plan.Length - passed - (failed ? 1 : 0),
                    accepted = !failed && passed == Plan.Length, retries = 0
                }, new JsonSerializerOptions { WriteIndented = true }));
                window.Close(); app.Shutdown(failed ? 1 : 0);
            }
            async Task Check(string name, Func<Task> test)
            {
                try { await test(); passed++; results.Add(new { name, status = "passed", error_type = (string?)null }); }
                catch (Exception error) { results.Add(new { name, status = "failed", error_type = error.GetType().Name }); throw; }
            }
        };
        var localExit = app.Run(window);
        if (localExit != 0) return localExit;
        return ExternalProbe.RunAsync(output, gate: true).GetAwaiter().GetResult();
    }
    private static Task Pump() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
}
