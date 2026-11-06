using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Automation = System.Windows.Automation;

namespace Contain.Viewer.Tests;

/// <summary>UI Automation against the real packaged process, never harness forms.</summary>
internal sealed class PackagedDesktop : IDisposable
{
    private readonly Process process;
    private readonly bool savedRecordsOnly;
    private readonly long startedAt;
    internal int ProcessId => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal string Executable { get; }
    internal string WorkingDirectory { get; }
    internal int DatabasePathInteractions { get; private set; }
    internal int ReportDialogInteractions { get; private set; }
    internal int ReportDialogSaves { get; private set; }
    internal string ReportDialogStage { get; private set; } = "not_started";
    internal string ReportDialogDiagnostic { get; private set; } = string.Empty;
    internal Action? ReportDialogProgress { get; set; }
    internal string CleanupState { get; private set; } = "not_disposed";

    internal PackagedDesktop(string package, string workingDirectory, bool savedRecordsOnly = false)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TestRecordIsolation.Variable)))
            throw new InvalidOperationException("A packaged test process requires the isolated record-library guard.");
        Executable = Path.GetFullPath(Path.Combine(package, "Contain.Viewer.exe"));
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        this.savedRecordsOnly = savedRecordsOnly;
        process = Process.Start(new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, WorkingDirectory = WorkingDirectory
        }) ?? throw new IOException("The packaged desktop process did not start.");
        // Retain the exact process handle and creation identity for the opt-in
        // saved-records cleanup. Never discover a replacement process by PID.
        _ = process.Handle;
        startedAt = process.StartTime.ToUniversalTime().Ticks;
    }

    internal async Task ReadyAsync()
    {
        await WaitAsync(async () => !process.HasExited && await ExistsAsync("NewInstall"), TimeSpan.FromSeconds(40));
        await WaitAsync(() => EnabledAsync("NewInstall"), TimeSpan.FromSeconds(40));
    }

    internal Task<bool> ExistsAsync(string id) => Ui(() => Find(id) is not null);
    internal Task<bool> EnabledAsync(string id) => Ui(() => Required(id).Current.IsEnabled);
    internal Task<string> TextAsync(string id) => Ui(() => Text(Required(id)));
    internal Task<uint> DeviceDpiAsync() => Ui(() => { process.Refresh(); return GetDpiForWindow(process.MainWindowHandle); });
    internal Task<bool> OnscreenAsync(string id) => Ui(() =>
    {
        var control = Required(id); process.Refresh();
        if (control.Current.IsOffscreen || !GetWindowRect(process.MainWindowHandle, out var window)) return false;
        var bounds = control.Current.BoundingRectangle;
        return bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= window.Left && bounds.Top >= window.Top
            && bounds.Right <= window.Right && bounds.Bottom <= window.Bottom;
    });
    // UIA IsOffscreen and the outer-window rectangle alone can miss a child
    // clipped by a scrolled TabPage. Check the full native control rectangle
    // against every ancestor's client viewport, including panels and tabs.
    internal Task<bool> FullyVisibleAsync(string id) => Ui(() =>
    {
        var control = Required(id);
        var window = new IntPtr(control.Current.NativeWindowHandle);
        if (control.Current.IsOffscreen || window == IntPtr.Zero || !GetWindowRect(window, out var bounds)
            || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        process.Refresh();
        for (var parent = GetParent(window); parent != IntPtr.Zero; parent = GetParent(parent))
        {
            if (!GetClientRect(parent, out var client)) return false;
            var origin = new NativePoint();
            if (!ClientToScreen(parent, ref origin)) return false;
            if (bounds.Left < origin.X || bounds.Top < origin.Y
                || bounds.Right > origin.X + client.Right || bounds.Bottom > origin.Y + client.Bottom) return false;
            if (parent == process.MainWindowHandle) return true;
        }
        return false;
    });
    internal Task ResizeAsync(int width, int height) => Ui(() =>
    {
        process.Refresh();
        if (!SetWindowPos(process.MainWindowHandle, IntPtr.Zero, 0, 0, width, height, 0x0002 | 0x0004))
            throw new IOException("The actual overview window did not resize.");
    });
    internal Task<string> DescriptionAsync(string id) => Ui(() =>
    {
        var element = Required(id);
        var nativeWindow = new IntPtr(element.Current.NativeWindowHandle);
        if (nativeWindow != IntPtr.Zero)
        {
            var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
            if (AccessibleObjectFromWindow(nativeWindow, 0xfffffffc, ref iid, out var accessible) == 0 && accessible is not null)
            {
                try { var description = accessible.get_accDescription(0); if (!string.IsNullOrEmpty(description)) return description; }
                finally { if (Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
            }
        }
        return element.Current.HelpText;
    });
    internal Task SetTextAsync(string id, string text) => Ui(() =>
    {
        if (id == "DatabaseSavePath") DatabasePathInteractions++;
        var element = Required(id);
        if (!element.TryGetCurrentPattern(Automation.ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException("The actual editable control lacks ValuePattern: " + id);
        ((Automation.ValuePattern)pattern).SetValue(text);
    });
    internal Task InvokeAsync(string id) => Ui(() => Invoke(Required(id)));

    /// <summary>Operate the real process's native SaveFileDialog, including cancel.</summary>
    internal async Task ExportReportAsync(string? outputPath)
    {
        if (outputPath is not null)
        {
            outputPath = Path.GetFullPath(outputPath);
            if (!Directory.Exists(Path.GetDirectoryName(outputPath)) || File.Exists(outputPath) || Directory.Exists(outputPath))
                throw new InvalidOperationException("The native save test requires a new file in its isolated existing directory.");
        }
        IntPtr dialog = IntPtr.Zero;
        ReportDialogDiagnostic = string.Empty;
        try
        {
            Stage("click_export");
            // A synchronous UIA InvokePattern can remain blocked in ShowDialog.
            // Resolve the production button, then issue real mouse input instead.
            var exportButton = await Ui(() =>
            {
                var control = Required("ExportReport");
                if (!control.Current.IsEnabled) throw new InvalidOperationException("The actual export control is disabled.");
                return new IntPtr(control.Current.NativeWindowHandle);
            });
            ClickNativeButton(exportButton);
            Stage("wait_save_dialog");
            await WaitAsync(() =>
            {
                dialog = FindReportDialog();
                return Task.FromResult(dialog != IntPtr.Zero);
            }, TimeSpan.FromSeconds(15));
            ReportDialogInteractions++;
            Stage("locate_native_controls");
            var controls = ReportControls(dialog);
            ReportDialogDiagnostic = "controls=" + controls.Count + "; " + string.Join(",", controls
                .Where(control => control.Class.Equals("Edit", StringComparison.OrdinalIgnoreCase) || control.Id is 1 or 2 or 1001 or 1148 or 1152)
                .Select(control => control.Class + ":" + control.Id + (IsWindowVisible(control.Window) ? ":visible" : ":hidden")));
            ReportDialogProgress?.Invoke();
            if (outputPath is not null)
            {
                var editable = controls.Where(control => IsWindowVisible(control.Window) && control.Class.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                    && control.Id is 1001 or 1148 or 1152).ToArray();
                if (editable.Length != 1)
                    throw new InvalidOperationException("The native save dialog has no unambiguous filename Edit control.");
                Stage("set_filename");
                // WM_SETTEXT is Unicode and literal; no SendKeys interpolation or
                // shell parsing can alter Chinese, spaces, %, _ or ampersands.
                if (SendMessageTimeoutText(editable[0].Window, 0x000C, IntPtr.Zero, outputPath,
                        0x0003, 2000, out var setResult) == IntPtr.Zero || setResult == UIntPtr.Zero)
                    throw new IOException("The native filename edit did not accept bounded WM_SETTEXT.");
                var readback = new System.Text.StringBuilder(outputPath.Length + 2);
                if (SendMessageTimeoutBuffer(editable[0].Window, 0x000D, new IntPtr(readback.Capacity), readback,
                        0x0003, 2000, out _) == IntPtr.Zero || readback.ToString() != outputPath)
                    throw new IOException("The native filename edit did not preserve the exact path.");
            }
            var command = outputPath is null ? 2 : 1;
            var button = GetDlgItem(dialog, command);
            if (button == IntPtr.Zero || !IsWindowVisible(button))
            {
                var matches = controls.Where(control => IsWindowVisible(control.Window) && control.Id == command && control.Class.Equals("Button", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("The native Save/Cancel button is not unambiguous.");
                button = matches[0].Window;
            }
            Stage(outputPath is null ? "click_cancel" : "click_save");
            ClickNativeButton(button);
            Stage("wait_dialog_close");
            await WaitAsync(() => Task.FromResult(!IsWindowVisible(dialog)), TimeSpan.FromSeconds(15));
            if (outputPath is not null) ReportDialogSaves++;
            Stage(outputPath is null ? "cancelled" : "save_submitted");
        }
        catch
        {
            Stage("failed_" + ReportDialogStage);
            // Only this process's modal window is asked to close. Every helper
            // call is bounded; no blocked UIA Invoke task remains after failure.
            if (dialog != IntPtr.Zero)
            {
                PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
                try { await WaitAsync(() => Task.FromResult(!IsWindowVisible(dialog)), TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { }
            }
            throw;
        }

        void Stage(string stage)
        {
            ReportDialogStage = stage;
            // Progress executes on the harness continuation, never a UIA worker.
            ReportDialogProgress?.Invoke();
        }
    }

    private void ClickNativeButton(IntPtr button)
    {
        if (button == IntPtr.Zero || !IsWindowVisible(button)) throw new InvalidOperationException("The native test button is absent or invisible.");
        GetWindowThreadProcessId(button, out var owner);
        if (owner != process.Id) throw new InvalidOperationException("The native test button belongs to another process.");
        var root = GetAncestor(button, 2);
        if (root == IntPtr.Zero) throw new InvalidOperationException("The native test button has no root window.");
        SetForegroundWindow(root);
        SetWindowPos(root, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        if (!GetWindowRect(button, out var rectangle) || rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top)
            throw new InvalidOperationException("The native test button has no clickable bounds.");
        var point = new NativePoint { X = rectangle.Left + (rectangle.Right - rectangle.Left) / 2,
            Y = rectangle.Top + (rectangle.Bottom - rectangle.Top) / 2 };
        var hit = WindowFromPoint(point);
        GetWindowThreadProcessId(hit, out var hitOwner);
        if (hitOwner != process.Id || (hit != button && !IsChild(button, hit)))
            throw new InvalidOperationException("Another native control covers the test button's click point.");
        GetCursorPos(out var previous);
        try
        {
            if (!SetCursorPos(point.X, point.Y)) throw new IOException("The native test click could not position the cursor.");
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
        finally { SetCursorPos(previous.X, previous.Y); }
    }

    private static List<(IntPtr Window, int Id, string Class)> ReportControls(IntPtr dialog)
    {
        var result = new List<(IntPtr Window, int Id, string Class)>();
        EnumChildWindows(dialog, (window, _) =>
        {
            var name = new System.Text.StringBuilder(80);
            GetClassName(window, name, name.Capacity);
            // Only native class names and numeric IDs are retained as diagnostics;
            // filenames, paths, captions and edit values never enter evidence.
            var safeClass = string.Concat(name.ToString().Where(c => char.IsAsciiLetterOrDigit(c) || c is '#' or '.' or '_'));
            result.Add((window, GetDlgCtrlID(window), safeClass));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private IntPtr FindReportDialog()
    {
        var found = IntPtr.Zero;
        var count = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            var name = new System.Text.StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (owner != process.Id || name.ToString() != "#32770" || !IsWindowVisible(window)) return true;
            count++;
            found = window;
            return true;
        }, IntPtr.Zero);
        if (count > 1) throw new InvalidOperationException("More than one native report dialog belongs to the test process.");
        return found;
    }
    internal Task ToggleAsync(string id, bool value) => Ui(() =>
    {
        if (id == "CustomDatabaseToggle") DatabasePathInteractions++;
        var element = Required(id);
        var toggle = (Automation.TogglePattern)element.GetCurrentPattern(Automation.TogglePattern.Pattern);
        if ((toggle.Current.ToggleState == Automation.ToggleState.On) != value) toggle.Toggle();
    });
    internal Task<bool> IsCheckedAsync(string id) => Ui(() =>
        ((Automation.TogglePattern)Required(id).GetCurrentPattern(Automation.TogglePattern.Pattern)).Current.ToggleState == Automation.ToggleState.On);

    internal Task SelectIdentityAsync(string identity) => Ui(() =>
    {
        var element = FindIdentity(identity) ?? throw new InvalidOperationException("Exact identity is absent from actual UI: " + identity);
        if (element.TryGetCurrentPattern(Automation.ScrollItemPattern.Pattern, out var scroll))
            ((Automation.ScrollItemPattern)scroll).ScrollIntoView();
        if (element.TryGetCurrentPattern(Automation.SelectionItemPattern.Pattern, out var selection))
            ((Automation.SelectionItemPattern)selection).Select();
        else if (element.TryGetCurrentPattern(Automation.InvokePattern.Pattern, out var invoke))
            ((Automation.InvokePattern)invoke).Invoke();
        else throw new InvalidOperationException("Exact UI identity cannot be selected: " + identity);
    });

    internal Task ClickIdentityAsync(string identity) => Ui(() =>
    {
        var element = FindIdentity(identity) ?? throw new InvalidOperationException("Exact click identity absent.");
        process.Refresh(); SetForegroundWindow(process.MainWindowHandle);
        var rectangle = element.Current.BoundingRectangle;
        var point = new NativePoint { X = (int)(rectangle.Left + rectangle.Width / 2), Y = (int)(rectangle.Top + rectangle.Height / 2) };
        GetWindowThreadProcessId(WindowFromPoint(point), out var owner);
        if (owner != process.Id || rectangle.Width <= 0 || rectangle.Height <= 0)
            throw new InvalidOperationException("The exact test cell is not exposed on the actual process window.");
        GetCursorPos(out var previous);
        try { SetCursorPos(point.X, point.Y); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
        finally { SetCursorPos(previous.X, previous.Y); }
    });

    internal Task<string> SelectionStateAsync(string gridId) => Ui(() =>
    {
        var grid = Required(gridId); var result = new System.Text.StringBuilder();
        var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
        if (AccessibleObjectFromWindow(new IntPtr(grid.Current.NativeWindowHandle), 0xfffffffc, ref iid, out var accessible) != 0 || accessible is null)
            throw new InvalidOperationException("Native grid accessibility unavailable.");
        try { Describe(accessible, 0, 0); }
        finally { if (Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
        return result.ToString();
        void Describe(global::Accessibility.IAccessible item, object childId, int depth)
        {
            var name = item.get_accName(childId);
            result.AppendLine($"{new string(' ', depth)}{name} | state={item.get_accState(childId)} | role={item.get_accRole(childId)}");
            if (depth >= 3 || !Equals(childId, 0)) return;
            for (var index = 1; index <= item.accChildCount; index++)
            {
                var child = item.get_accChild(index) as global::Accessibility.IAccessible;
                try { if (child is not null) Describe(child, 0, depth + 1); else Describe(item, index, depth + 1); }
                finally { if (child is not null && Marshal.IsComObject(child)) Marshal.ReleaseComObject(child); }
            }
        }
    });

    internal static int DiagnoseSelection(string package, string root, string output, bool events = false)
    {
        using var isolation = TestRecordIsolation.Use(root);
        using var owner = new Form { ShowInTaskbar = false };
        var passed = false;
        owner.Shown += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(output);
                foreach (var method in events ? new[] { "event-invoke" } : new[] { "invoke", "mouse" })
                {
                    using var desktop = new PackagedDesktop(package, root); await desktop.ReadyAsync();
                    var log = new System.Text.StringBuilder();
                    var grid = events ? "Events" : "Sessions";
                    if (events)
                    {
                        await desktop.SelectIdentityAsync("session:synthetic-interrupted");
                        await WaitAsync(async () => await desktop.DescriptionAsync("SessionTitle") == "session:synthetic-interrupted", TimeSpan.FromSeconds(30));
                        await WaitAsync(() => desktop.EnabledAsync("ViewEvents"), TimeSpan.FromSeconds(30));
                        await desktop.InvokeAsync("ViewEvents");
                        await WaitAsync(() => desktop.EnabledAsync("NewInstall"), TimeSpan.FromSeconds(30));
                    }
                    log.AppendLine("before").AppendLine(await desktop.SelectionStateAsync(grid));
                    if (events) await desktop.SelectIdentityAsync("event:synthetic-interrupted-event-2");
                    else if (method == "invoke") await desktop.SelectIdentityAsync("session:synthetic-interrupted");
                    else await desktop.ClickIdentityAsync("session:synthetic-interrupted");
                    await Task.Delay(1200);
                    log.AppendLine("after").AppendLine(await desktop.SelectionStateAsync(grid));
                    log.AppendLine("bound=" + await desktop.DescriptionAsync(events ? "EventDetails" : "SessionTitle"));
                    log.AppendLine(await desktop.DumpAsync());
                    File.WriteAllText(Path.Combine(output, method + ".txt"), log.ToString());
                    await desktop.ScreenshotAsync(output, method + ".png");
                    await desktop.CloseAsync();
                }
                passed = true;
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(output, "probe-error.txt"), error.ToString()); }
            finally { owner.Close(); }
        };
        Application.Run(owner); return passed ? 0 : 1;
    }

    internal Task SelectTabAsync(string caption) => Ui(() =>
    {
        var element = Find(null, caption);
        if (element is not null && element.TryGetCurrentPattern(Automation.SelectionItemPattern.Pattern, out var selection))
        { ((Automation.SelectionItemPattern)selection).Select(); return; }
        // WinForms' UIA projection can omit a nonselected last tab. Its native
        // MSAA child still exposes the real header and its default select action.
        var tabs = Required("Tabs"); var iid = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");
        if (AccessibleObjectFromWindow(new IntPtr(tabs.Current.NativeWindowHandle), 0xfffffffc, ref iid, out var accessible) == 0 && accessible is not null)
        {
            try
            {
                for (var index = 1; index <= accessible.accChildCount; index++)
                {
                    var child = accessible.get_accChild(index) as global::Accessibility.IAccessible;
                    try
                    {
                        var name = child?.get_accName(0) ?? accessible.get_accName(index);
                        if (name != caption) continue;
                        if (child is not null) child.accDoDefaultAction(0); else accessible.accDoDefaultAction(index);
                        return;
                    }
                    finally { if (child is not null && Marshal.IsComObject(child)) Marshal.ReleaseComObject(child); }
                }
            }
            finally { if (Marshal.IsComObject(accessible)) Marshal.ReleaseComObject(accessible); }
        }
        throw new InvalidOperationException("Actual tab header is absent from both UIA and MSAA: " + caption);
    });

    internal Task<string[]> NamesAsync(string id) => Ui(() =>
    {
        var elements = Required(id).FindAll(Automation.TreeScope.Descendants, Automation.Condition.TrueCondition);
        return elements.Cast<Automation.AutomationElement>().Select(element => element.Current.Name).ToArray();
    });

    internal Task<string> DumpAsync() => Ui(() =>
    {
        var result = new System.Text.StringBuilder();
        var windows = Automation.AutomationElement.RootElement.FindAll(Automation.TreeScope.Children,
            new Automation.PropertyCondition(Automation.AutomationElement.ProcessIdProperty, process.Id));
        foreach (Automation.AutomationElement window in windows)
            foreach (Automation.AutomationElement element in window.FindAll(Automation.TreeScope.Element | Automation.TreeScope.Descendants, Automation.Condition.TrueCondition))
                result.AppendLine($"{element.Current.ControlType.ProgrammaticName} | {element.Current.AutomationId} | {element.Current.Name} | help={element.Current.HelpText} | value={Text(element)} | patterns={string.Join(',', element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName))}");
        return result.ToString();
    });

    internal static int Diagnose(string package, string root, string output)
    {
        using var isolation = TestRecordIsolation.Use(root);
        using var owner = new Form { ShowInTaskbar = false };
        var success = false;
        owner.Shown += async (_, _) =>
        {
            try
            {
                using var desktop = new PackagedDesktop(package, root); await desktop.ReadyAsync(); await Task.Delay(1500);
                Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "actual-uia-tree.txt"), await desktop.DumpAsync());
                await desktop.CloseAsync(); success = true;
            }
            finally { owner.Close(); }
        };
        Application.Run(owner); return success ? 0 : 1;
    }

    internal async Task CloseAsync()
    {
        if (process.HasExited) return;
        process.Refresh();
        if (!process.CloseMainWindow()) throw new InvalidOperationException("The actual desktop window could not receive a normal close gesture.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (process.ExitCode != 0) throw new InvalidDataException("The packaged desktop process exited nonzero.");
    }

    internal Task RequestCloseAsync() => Ui(() =>
    {
        process.Refresh();
        if (process.MainWindowHandle == IntPtr.Zero || !PostMessage(process.MainWindowHandle, 0x0010, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("The actual main window did not receive the close request.");
    });

    internal Task<ImageResult> ScreenshotAsync(string directory, string name) => Ui(() =>
    {
        process.Refresh();
        var window = process.MainWindowHandle;
        if (window == IntPtr.Zero || !GetWindowRect(window, out var bounds)) throw new IOException("The actual process window has no bounds.");
        using var image = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        using (var graphics = Graphics.FromImage(image))
        {
            var dc = graphics.GetHdc();
            try { if (!PrintWindow(window, dc, 2)) throw new IOException("Actual native-window rendering failed."); }
            finally { graphics.ReleaseHdc(dc); }
        }
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        image.Save(path, ImageFormat.Png);
        return new ImageResult(name, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), image.Width, image.Height);
    });

    internal static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The actual packaged UI did not reach the expected condition.");
    }

    private Automation.AutomationElement? Find(string? id, string? name = null)
    {
        var windows = Automation.AutomationElement.RootElement.FindAll(Automation.TreeScope.Children,
            new Automation.PropertyCondition(Automation.AutomationElement.ProcessIdProperty, process.Id));
        var condition = id is not null
            ? new Automation.PropertyCondition(Automation.AutomationElement.AutomationIdProperty, id)
            : new Automation.PropertyCondition(Automation.AutomationElement.NameProperty, name!);
        foreach (Automation.AutomationElement window in windows)
        {
            var found = window.FindFirst(Automation.TreeScope.Element | Automation.TreeScope.Descendants, condition);
            if (found is not null) return found;
        }
        return null;
    }

    private Automation.AutomationElement Required(string id) => Find(id) ?? throw new InvalidOperationException("Actual product control is absent: " + id);
    private Automation.AutomationElement? FindIdentity(string identity)
    {
        var windows = Automation.AutomationElement.RootElement.FindAll(Automation.TreeScope.Children,
            new Automation.PropertyCondition(Automation.AutomationElement.ProcessIdProperty, process.Id));
        foreach (Automation.AutomationElement window in windows)
            foreach (Automation.AutomationElement element in window.FindAll(Automation.TreeScope.Descendants, Automation.Condition.TrueCondition))
                if (element.Current.Name == identity || element.Current.Name.EndsWith("；" + identity, StringComparison.Ordinal)) return element;
        return null;
    }
    private static string Text(Automation.AutomationElement element)
    {
        if (element.TryGetCurrentPattern(Automation.ValuePattern.Pattern, out var value)) return ((Automation.ValuePattern)value).Current.Value;
        if (element.TryGetCurrentPattern(Automation.TextPattern.Pattern, out var text)) return ((Automation.TextPattern)text).DocumentRange.GetText(-1);
        return element.Current.Name;
    }
    private static void Invoke(Automation.AutomationElement element)
    {
        if (element.TryGetCurrentPattern(Automation.InvokePattern.Pattern, out var invoke)) ((Automation.InvokePattern)invoke).Invoke();
        else throw new InvalidOperationException("The actual control cannot be invoked.");
    }
    private static async Task<T> Ui<T>(Func<T> action) => await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(20));
    private static async Task Ui(Action action) => await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(20));

    public void Dispose()
    {
        try
        {
            if (process.HasExited) { CleanupState = "already_exited"; return; }
            if (!savedRecordsOnly)
            {
                process.CloseMainWindow();
                CleanupState = "normal_close_requested";
                // Live installation verification never terminates a product or
                // installer tree following an assertion failure.
                return;
            }
            // This opt-in plan launches only a read-only viewer over synthetic
            // saved records. Close its modal dialogs first, then its main window.
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner == process.Id) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            if (process.WaitForExit(2000)) { CleanupState = "normal_close_completed"; return; }
            if (process.StartTime.ToUniversalTime().Ticks != startedAt)
                throw new InvalidOperationException("The saved-record viewer creation identity changed.");
            process.Kill(entireProcessTree: false);
            CleanupState = process.WaitForExit(3000) ? "owned_saved_record_viewer_terminated" : "owned_saved_record_viewer_exit_timeout";
        }
        finally { process.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int item);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutText(IntPtr window, uint message, IntPtr wParam, string text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutBuffer(IntPtr window, uint message, IntPtr wParam, System.Text.StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out WindowBounds bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out global::Accessibility.IAccessible? accessible);
}
