using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Automation;

namespace Contain.Desktop.Tests;

/// <summary>Operates only the exact test installer's independent native window.</summary>
internal static class InteractiveInstaller
{
    internal static async Task<Screenshot> ChooseDirectoryAsync(string executable, string destination, string output)
    {
        var completion = new TaskCompletionSource<Screenshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Choose(executable, destination, output)); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(35));
    }

    private static Screenshot Choose(string executable, string destination, string output)
    {
        var deadline = Stopwatch.StartNew();
        AutomationElement? window = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var matches = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, "Contain controlled installer"));
            if (matches.Count > 1) throw new InvalidOperationException("Duplicate controlled installer windows.");
            if (matches.Count == 1) { window = matches[0]; break; }
            Thread.Sleep(100);
        }
        if (window is null) throw new TimeoutException("Interactive installer window was not observed.");
        using var process = Process.GetProcessById(window.Current.ProcessId);
        if (!string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer window executable identity differs.");
        var edit = Required(window, "101"); var install = Required(window, "102");
        var value = (ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern);
        if (!string.IsNullOrWhiteSpace(value.Current.Value)) throw new InvalidOperationException("Destination was preconfigured before installer interaction.");
        if (File.Exists(Path.Combine(destination, ".follow-go"))) throw new InvalidOperationException("Installer performed writes before explicit installation.");
        value.SetValue(destination);
        if (value.Current.Value != destination) throw new InvalidOperationException("Installer destination selection was not retained.");
        var handle = (IntPtr)window.Current.NativeWindowHandle;
        var bounds = window.Current.BoundingRectangle;
        if (bounds.Width < 300 || bounds.Height < 100 || !install.Current.IsEnabled || install.Current.IsOffscreen)
            throw new InvalidOperationException("Installer destination controls are not visible and enabled.");
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "interactive-installer-custom-destination.png");
        using (var bitmap = new Bitmap((int)bounds.Width, (int)bounds.Height))
        {
            using var graphics = Graphics.FromImage(bitmap); var dc = graphics.GetHdc();
            try { if (!PrintWindow(handle, dc, 2)) throw new IOException("Installer screenshot failed."); }
            finally { graphics.ReleaseHdc(dc); }
            bitmap.Save(path, ImageFormat.Png);
        }
        var dpi = GetDpiForWindow(handle);
        // Exactly one explicit install action, after the new destination is visible.
        ((InvokePattern)install.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return new(Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            (int)bounds.Width, (int)bounds.Height, dpi, "real-controlled-installer-directory-selection");
    }

    private static AutomationElement Required(AutomationElement window, string id)
    {
        var matches = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        if (matches.Count != 1 || matches[0].Current.ProcessId != window.Current.ProcessId)
            throw new InvalidOperationException("Installer control identity is not unique.");
        return matches[0];
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
