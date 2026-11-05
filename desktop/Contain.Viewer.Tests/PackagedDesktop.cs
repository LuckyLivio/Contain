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
    internal int ProcessId => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal string Executable { get; }
    internal string WorkingDirectory { get; }
    internal int DatabasePathInteractions { get; private set; }

    internal PackagedDesktop(string package, string workingDirectory)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TestRecordIsolation.Variable)))
            throw new InvalidOperationException("A packaged test process requires the isolated record-library guard.");
        Executable = Path.GetFullPath(Path.Combine(package, "Contain.Viewer.exe"));
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        process = Process.Start(new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, WorkingDirectory = WorkingDirectory
        }) ?? throw new IOException("The packaged desktop process did not start.");
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
        if (!process.HasExited) process.CloseMainWindow();
        // Never kill the product/installer tree following a failed assertion.
        process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
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
