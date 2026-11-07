using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using Forms = System.Windows.Forms;

namespace Contain.Desktop.Tests;

internal sealed record Screenshot(string Name, string Sha256, int Width, int Height, uint Dpi, string DataKind);
internal sealed record Probe(string Stage, string Control, long ElapsedMs, bool ProcessAlive, string? ErrorType);
internal sealed record ActionProbe(string Control, string Pattern, bool Passed, string? ErrorType);
internal sealed record ClickDiagnostic(string Control, string? TargetRole, string? HitRole, bool NativeOwned,
    string[] AncestorRoles, bool TargetMatched, string Result, long ElapsedMs, string? ErrorType);
internal sealed record CloseDiagnostic(long RequestElapsedMs, long WaitElapsedMs, int WaitDeadlineMs,
    bool ProcessAlive, int? ExitCode, string? ErrorType);

/// <summary>Only talks to a separately launched, exact packaged executable. No product assembly reference.</summary>
internal sealed class DesktopDriver : IDisposable
{
    private readonly Process process;
    private readonly List<Probe> probes = [];
    private readonly List<ActionProbe> actions = [];
    private readonly List<ClickDiagnostic> clicks = [];
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    internal int ProcessId => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal IReadOnlyList<Probe> Probes => probes;
    internal IReadOnlyList<ActionProbe> Actions => actions;
    internal IReadOnlyList<ClickDiagnostic> Clicks => clicks;
    internal CloseDiagnostic? CloseObservation { get; private set; }
    internal string Stage { get; private set; } = "launch";
    internal string? LastControl { get; private set; }
    internal string? LastErrorType { get; private set; }
    internal bool LiveInstallationStarted { get; set; }
    internal int ChooserInteractions { get; private set; }

    internal DesktopDriver(string package, string working, string isolatedRoot, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!Path.IsPathFullyQualified(isolatedRoot)
            || File.ReadAllText(Path.Combine(isolatedRoot, ".contain-records-test-root")) != "Contain isolated record library test data")
            throw new InvalidOperationException("A marked isolated personal-library root is mandatory.");
        Directory.CreateDirectory(working);
        var start = new ProcessStartInfo(Path.Combine(package, "Contain.Viewer.exe"))
        {
            UseShellExecute = false, WorkingDirectory = working
        };
        start.Environment["CONTAIN_VIEWER_TEST_ROOT"] = isolatedRoot;
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        process = Process.Start(start) ?? throw new IOException("Packaged desktop did not start.");
        _ = process.Handle;
    }

    internal async Task ReadyAsync()
    {
        await UntilAsync("ready", "NewInstall", async () => await OptionalAsync("NewInstall", e => e.Current.IsEnabled) == true,
            TimeSpan.FromSeconds(40));
        foreach (var id in new[] { "NewInstall", "MyRecords", "OpenDatabase", "About" })
            await Ui(id, () =>
            {
                if (!Required(id).TryGetCurrentPattern(InvokePattern.Pattern, out _)) throw new RequiredInvokePatternMissingException(id);
            });
    }

    internal async Task UntilAsync(string stage, string id, Func<Task<bool>> predicate, TimeSpan? timeout = null)
    {
        Stage = stage; LastControl = id;
        var clock = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        try
        {
            await ObservedWait.RunAsync(predicate, EnsureAlive, limit, () => clock.Elapsed, Task.Delay);
            Record(stage, id, clock.ElapsedMilliseconds, null);
        }
        catch (Exception error) { Record(stage, id, clock.ElapsedMilliseconds, error.GetType().Name); throw; }
    }

    private void Record(string stage, string id, long ms, string? error)
    {
        LastErrorType = error;
        if (probes.Count < 256) probes.Add(new(stage, id, ms, !process.HasExited, error));
    }
    private void EnsureAlive()
    {
        if (process.HasExited) throw new ProductExitedException(process.ExitCode);
    }
    internal Task<string> TextAsync(string id) => Ui(id, () => Text(Required(id)));
    internal Task<string> HelpAsync(string id) => Ui(id, () => Required(id).Current.HelpText);
    internal Task<bool> EnabledAsync(string id) => Ui(id, () => Required(id).Current.IsEnabled);
    internal Task<bool> SupportsInvokeAsync(string id) => Ui(id, () => Required(id).TryGetCurrentPattern(InvokePattern.Pattern, out _));
    internal Task<bool> ExistsAsync(string id) => OptionalAsync(id, _ => true);
    internal Task<string?> TryHelpAsync(string id) => OptionalAsync(id, e => e.Current.HelpText);
    internal Task<string?> TryTextAsync(string id) => OptionalAsync(id, Text);
    // Boolean state probes accept absence while a view is being constructed.
    // Required text/action reads remain strict, and unexpected UIA errors propagate.
    internal Task<bool> VisibleAsync(string id) => OptionalAsync(id, Visible);
    internal Task<string[]> RowIdsAsync(string id, string prefix) => Ui(id, () =>
    {
        var list = Required(id);
        var result = new List<string>();
        if (list.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var pattern))
        {
            var container = (ItemContainerPattern)pattern;
            AutomationElement? item = null;
            while ((item = container.FindItemByProperty(item, null, null)) is not null)
            {
                if (result.Count >= 50) throw new InvalidOperationException("UI list exceeds its 50-item page bound.");
                if (item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized)) ((VirtualizedItemPattern)virtualized).Realize();
                var value = item.Current.AutomationId;
                if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("List item lacks its stable original identity.");
                result.Add(value[prefix.Length..]);
            }
        }
        else
        {
            foreach (AutomationElement item in list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            {
                var value = item.Current.AutomationId;
                if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("List item lacks its stable original identity.");
                result.Add(value[prefix.Length..]);
            }
        }
        return result.ToArray();
    });

    internal Task SetTextAsync(string id, string value) => Ui(id, () =>
    {
        var element = Required(id);
        if (!element.Current.IsEnabled || !element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("Required edit is not enabled or has no ValuePattern: " + id);
        ((ValuePattern)pattern).SetValue(value);
    });
    internal Task InvokeAsync(string id) => Ui(id, () =>
    {
        var attempted = "find_control";
        try
        {
            var element = Required(id);
            attempted = "check_enabled";
            if (!element.Current.IsEnabled) throw new ElementNotEnabledException(id);
            var navigationAction = id is "NewInstall" or "MyRecords" or "CurrentTask" or "OpenDatabase" or "About";
            if (navigationAction && !Visible(element)) throw new InvalidOperationException("Required navigation action is not visible.");
            attempted = "find_pattern";
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            { attempted = "InvokePattern.Invoke"; ((InvokePattern)invoke).Invoke(); }
            else if (navigationAction)
            { attempted = "require_navigation_InvokePattern"; throw new RequiredInvokePatternMissingException(id); }
            else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            { attempted = "SelectionItemPattern.Select"; ((SelectionItemPattern)select).Select(); }
            else throw new InvalidOperationException("Required action has no supported invocation pattern: " + id);
            if (actions.Count < 128) actions.Add(new(id, attempted, true, null));
        }
        catch (Exception error)
        {
            if (actions.Count < 128) actions.Add(new(id, attempted, false, error.GetType().Name));
            throw;
        }
    });
    internal Task SelectAsync(string id) => Ui(id, () =>
    {
        var element = Required(id);
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    });
    internal Task ExpandAsync(string id) => Ui(id, () =>
    {
        var element = Required(id);
        var expand = (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded) expand.Expand();
    });
    internal Task CollapseAsync(string id) => Ui(id, () =>
    {
        var collapse = (ExpandCollapsePattern)Required(id).GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (collapse.Current.ExpandCollapseState != ExpandCollapseState.Collapsed) collapse.Collapse();
    });
    internal async Task KeyboardReachAsync(string id)
    {
        await Ui(id, () => { SetForegroundWindow(MainWindow()); Required(id).SetFocus(); });
        await UntilAsync("keyboard_focus", id, () => Ui(id, () => Required(id).Current.HasKeyboardFocus), TimeSpan.FromSeconds(5));
    }
    internal async Task KeyboardActivateAsync(string id)
    {
        await KeyboardReachAsync(id);
        await Sta(() =>
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var owner);
            if (owner != process.Id) throw new InvalidOperationException("Keyboard action lost its owned foreground window.");
            Forms.SendKeys.SendWait(" ");
        });
    }
    internal Task ToggleAsync(string id, bool value) => Ui(id, () =>
    {
        var toggle = (TogglePattern)Required(id).GetCurrentPattern(TogglePattern.Pattern);
        if ((toggle.Current.ToggleState == ToggleState.On) != value) toggle.Toggle();
    });
    internal Task<bool> CheckedAsync(string id) => Ui(id, () =>
        ((TogglePattern)Required(id).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On);

    internal Task ClickAsync(string id) => Ui(id, () =>
    {
        Stage = "native_click";
        var clock = Stopwatch.StartNew(); var roles = new List<string>();
        string? targetRole = null, hitRole = null, errorType = null;
        var nativeOwned = false; var matched = false; var result = "find_target";
        try
        {
            var element = Required(id);
            targetRole = element.Current.ControlType.ProgrammaticName;
            result = "visible_enabled";
            if (!element.Current.IsEnabled || !Visible(element)) throw new ClickTargetUnavailableException(id);
            var rect = element.Current.BoundingRectangle;
            SetForegroundWindow(MainWindow());
            var point = new Point { X = (int)(rect.X + rect.Width / 2), Y = (int)(rect.Y + rect.Height / 2) };
            GetWindowThreadProcessId(WindowFromPoint(point), out var owner);
            nativeOwned = owner == process.Id;
            result = "native_owner";
            if (!nativeOwned) throw new ClickTargetUnavailableException(id);

            // A WPF child can be clipped by a ScrollViewer while its rectangle is still
            // inside the main HWND. Require the actual hit target, not merely window ownership.
            result = "uia_hit_target";
            var hit = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            var match = HitTargetGuard.Check(element, hit, e => e.Current.ProcessId == process.Id,
                Automation.Compare, TreeWalker.RawViewWalker.GetParent, e =>
                {
                    var role = e.Current.ControlType.ProgrammaticName;
                    hitRole ??= role; roles.Add(role);
                });
            matched = match.Matches; result = match.Reason;
            if (!matched) throw new ClickTargetUnavailableException(id);
            GetCursorPos(out var old);
            try { SetCursorPos(point.X, point.Y); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
            finally { SetCursorPos(old.X, old.Y); }
            result = "clicked_exact_target";
        }
        catch (Exception error) { errorType = error.GetType().Name; LastErrorType = errorType; throw; }
        finally
        {
            // Cached fields only: failed UIA calls retain their original exception, with no
            // follow-up provider queries that could replace it or expose unrelated desktop text.
            if (clicks.Count < 128) clicks.Add(new(id, targetRole, hitRole, nativeOwned, roles.ToArray(),
                matched, result, clock.ElapsedMilliseconds, errorType));
        }
    });

    internal async Task ChooseFileAsync(string action, string? path, bool save = false, bool existingRefusal = false)
    {
        Stage = "native_picker_" + action;
        if (existingRefusal && (!save || path is null || !File.Exists(path)))
            throw new IOException("Existing-target refusal requires a deliberate existing save target.");
        if (path is not null && (save ? !Directory.Exists(Path.GetDirectoryName(path)) || (File.Exists(path) && !existingRefusal) : !File.Exists(path)))
            throw new IOException("Picker test target violates isolated existing/new-file precondition.");
        await ClickAsync(action); // InvokePattern may synchronously block in the product's modal ShowDialog.
        IntPtr dialog = IntPtr.Zero;
        await UntilAsync("native_picker_discovery", action, () => Task.FromResult((dialog = NativeDialog()) != IntPtr.Zero), TimeSpan.FromSeconds(15));
        ChooserInteractions++;
        try
        {
            await Sta(() =>
            {
                SetForegroundWindow(dialog);
                if (path is null) { Forms.SendKeys.SendWait("{ESC}"); return; }
                var edits = NativeChildren(dialog).Where(item => item.Id is 1001 or 1148 or 1152 && item.Class == "Edit" && IsWindowVisible(item.Window)).ToArray();
                if (edits.Length != 1) throw new InvalidOperationException("Native file picker has no unique filename entry.");
                AutomationElement.FromHandle(edits[0].Window).SetFocus();
                Forms.SendKeys.SendWait("^a"); Forms.SendKeys.SendWait(KeysLiteral(Path.GetFullPath(path)));
                Forms.SendKeys.SendWait("{TAB}");
            });
            if (path is not null)
            {
                await UntilAsync("native_picker_value", action, () => Task.FromResult(NativeChildren(dialog).Any(item =>
                    item.Id is 1001 or 1148 or 1152 && item.Class == "Edit" && IsWindowVisible(item.Window)
                    && NativeText(item.Window) == Path.GetFullPath(path))), TimeSpan.FromSeconds(5));
                await Ui(action, () =>
                {
                    var accept = NativeChildren(dialog).Single(item => item.Id == 1 && item.Class == "Button" && IsWindowVisible(item.Window));
                    ((InvokePattern)AutomationElement.FromHandle(accept.Window).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                });
            }
            await UntilAsync("native_picker_closed", action, () => Task.FromResult(!IsWindowVisible(dialog)), TimeSpan.FromSeconds(15));
        }
        catch { if (dialog != IntPtr.Zero) PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero); throw; }
    }

    internal Task ResizeAsync(int width, int height) => Ui("window", () =>
    {
        if (!SetWindowPos(MainWindow(), IntPtr.Zero, 30, 30, width, height, 0x0004)) throw new IOException("Resize failed.");
    });
    internal Task RequestCloseAsync() => Ui("window", () => PostMessage(MainWindow(), 0x0010, IntPtr.Zero, IntPtr.Zero));
    internal async Task CloseAsync()
    {
        Stage = "normal_close"; LastControl = "window";
        var request = Stopwatch.StartNew(); var wait = new Stopwatch(); string? errorType = null;
        try
        {
            await RequestCloseAsync(); request.Stop(); wait.Start();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (process.ExitCode != 0) throw new ProductExitedException(process.ExitCode);
        }
        catch (Exception error) { errorType = error.GetType().Name; throw; }
        finally
        {
            request.Stop(); wait.Stop();
            CloseObservation = new(request.ElapsedMilliseconds, wait.ElapsedMilliseconds, 20000,
                !process.HasExited, process.HasExited ? process.ExitCode : null, errorType);
            Record("normal_close", "window", request.ElapsedMilliseconds + wait.ElapsedMilliseconds, errorType);
        }
    }
    internal Task<Screenshot> CaptureAsync(string output, string name, string kind) => Ui("window", () =>
    {
        var window = MainWindow();
        if (!GetWindowRect(window, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            throw new IOException("Actual window bounds unavailable.");
        SetForegroundWindow(window);
        using var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(window, dc, 2)) throw new IOException("Native screenshot failed."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        Directory.CreateDirectory(output); var path = Path.Combine(output, name); bitmap.Save(path, ImageFormat.Png);
        return new Screenshot(name, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), bitmap.Width, bitmap.Height, GetDpiForWindow(window), kind);
    });
    internal static Task<string> ClipboardAsync() => Sta(Forms.Clipboard.GetText);
    internal static Task SetClipboardAsync(string value) => Sta(() => Forms.Clipboard.SetText(value));
    internal Task<object> NavigationDiagnosticsAsync() => Ui<object>("fixed_navigation_diagnostics", () =>
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
        if (windows.Count > 4) throw new InvalidOperationException("Owned diagnostic window bound exceeded.");
        var rows = new List<object>();
        foreach (var id in new[] { "NewInstall", "MyRecords", "CurrentTask", "OpenDatabase", "About" })
        {
            var matches = new List<object>();
            foreach (AutomationElement window in windows)
            {
                var ownedRoot = window.Current;
                if (ownedRoot.ProcessId != process.Id) throw new InvalidOperationException("Diagnostic root ownership differs.");
                foreach (AutomationElement item in window.FindAll(TreeScope.Element | TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, id)))
                {
                    if (matches.Count >= 8) throw new InvalidOperationException("Exact-ID diagnostic match bound exceeded.");
                    var info = item.Current; var ancestors = new List<object>(); var parent = item;
                    for (var depth = 0; depth < 3; depth++)
                    {
                        parent = TreeWalker.RawViewWalker.GetParent(parent);
                        if (parent is null || parent.Current.ProcessId != process.Id) break;
                        ancestors.Add(new { depth = depth + 1, class_name = parent.Current.ClassName,
                            control_type = parent.Current.ControlType.ProgrammaticName, automation_id = parent.Current.AutomationId });
                    }
                    matches.Add(new
                    {
                        automation_id = info.AutomationId, class_name = info.ClassName, control_type = info.ControlType.ProgrammaticName,
                        name = info.Name, process_id = info.ProcessId, enabled = info.IsEnabled, offscreen = info.IsOffscreen,
                        owned_root_process_id = ownedRoot.ProcessId, owned_root_class = ownedRoot.ClassName,
                        patterns = item.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName).Order().ToArray(), ancestors
                    });
                }
            }
            rows.Add(new { id, matched_count = matches.Count, matches });
        }
        return new { fixed_ids_only = true, maximum_owned_windows = 4, maximum_matches_per_id = 8, maximum_ancestor_depth = 3, rows };
    });

    private Task<T?> OptionalAsync<T>(string id, Func<AutomationElement, T> action) => Ui<T?>(id, () =>
    {
        // Only the documented transition probe accepts absent or stale elements.
        // COM/access/timeout failures remain visible and fail the test.
        try { return Find(id) is { } element ? action(element) : default; }
        catch (ElementNotAvailableException) { return default; }
    });
    private Task<T> Ui<T>(string id, Func<T> action)
    {
        EnsureAlive(); LastControl = id;
        return Task.Run(action).WaitAsync(TimeSpan.FromSeconds(20));
    }
    private Task Ui(string id, Action action) => Ui(id, () => { action(); return true; });
    private AutomationElement? Find(string id)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
        foreach (AutomationElement window in windows)
        {
            var match = window.FindFirst(TreeScope.Element | TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (match is not null) return match;
        }
        return null;
    }
    private AutomationElement Required(string id) => Find(id) ?? throw new RequiredControlMissingException(id);
    private bool Visible(AutomationElement element)
    {
        var bounds = element.Current.BoundingRectangle;
        if (element.Current.IsOffscreen || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return false;
        if (!GetWindowRect(MainWindow(), out var window)) return false;
        return bounds.Left >= window.Left && bounds.Top >= window.Top && bounds.Right <= window.Right && bounds.Bottom <= window.Bottom;
    }
    private IntPtr MainWindow()
    {
        process.Refresh(); var window = process.MainWindowHandle;
        GetWindowThreadProcessId(window, out var owner);
        if (window == IntPtr.Zero || owner != process.Id) throw new InvalidOperationException("Owned main window unavailable.");
        return window;
    }
    private IntPtr NativeDialog()
    {
        var matches = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner); var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
            if (owner == process.Id && IsWindowVisible(window) && name.ToString() == "#32770") matches.Add(window);
            return true;
        }, IntPtr.Zero);
        if (matches.Count > 1) throw new InvalidOperationException("Multiple owned native modal dialogs.");
        return matches.SingleOrDefault();
    }
    private static List<(IntPtr Window, int Id, string Class)> NativeChildren(IntPtr window)
    {
        var result = new List<(IntPtr, int, string)>();
        EnumChildWindows(window, (child, _) => { var name = new StringBuilder(80); GetClassName(child, name, name.Capacity); result.Add((child, GetDlgCtrlID(child), name.ToString())); return true; }, IntPtr.Zero);
        return result;
    }
    private static string NativeText(IntPtr window)
    {
        var text = new StringBuilder(32768);
        if (SendMessageTimeout(window, 0x000D, new IntPtr(text.Capacity), text, 0x0003, 2000, out _) == IntPtr.Zero)
            throw new IOException("Native filename read timed out.");
        return text.ToString();
    }
    private static string Text(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(-1);
        return element.Current.Name;
    }
    private static string KeysLiteral(string value) => string.Concat(value.Select(c => c is '+' or '^' or '%' or '~' or '(' or ')' or '[' or ']' or '{' or '}' ? "{" + c + "}" : c.ToString()));
    private static Task Sta(Action action) => Sta(() => { action(); return true; });
    private static Task<T> Sta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public void Dispose()
    {
        // Never kill a viewer/collector/installer tree on a live-install assertion failure.
        if (!process.HasExited) process.CloseMainWindow();
        process.Dispose();
    }
    internal sealed class ProductExitedException(int code) : IOException("Packaged product exited before required state; exit=" + code);
    internal sealed class RequiredControlMissingException(string id) : InvalidOperationException("Required control missing: " + id);
    internal sealed class RequiredInvokePatternMissingException(string id) : InvalidOperationException("Navigation action lacks required InvokePattern: " + id);
    internal sealed class ClickTargetUnavailableException(string id) : InvalidOperationException("Required click target is clipped, disabled, or obstructed: " + id);
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Bounds rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wparam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
}
