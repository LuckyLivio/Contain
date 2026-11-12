using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Contain.Desktop.Tests;

internal sealed record ClipboardObservation(string Purpose, uint SequenceBeforeOpen, uint SequenceWhileLocked,
    string OwnerRole, string ReadClass, int? Characters, bool MatchesExpected, int? Win32Error);

/// <summary>The text stays private; public evidence contains only the bounded classification.</summary>
internal sealed record ClipboardSnapshot(ClipboardObservation Observation, string? Text)
{
    internal bool Matches(string expected) => Observation.ReadClass == "unicode_text" && Text == expected;
}

internal static class NativeClipboard
{
    private const uint UnicodeText = 13;
    private const int MaximumBytes = 64 * 1024;

    internal static async Task<ClipboardSnapshot> ReadAsync(string purpose, string expected, int productPid)
        => await Task.Run(() => Read(purpose, expected, productPid)).WaitAsync(TimeSpan.FromSeconds(2));

    private static ClipboardSnapshot Read(string purpose, string expected, int productPid)
    {
        var before = GetClipboardSequenceNumber();
        if (!OpenClipboard(IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            return new(new(purpose, before, GetClipboardSequenceNumber(), OwnerRole(productPid),
                error is 0 or 5 ? "clipboard_busy" : "native_error", null, false, error), null);
        }
        try
        {
            ClipboardSnapshot Result(string kind, string? text = null, int? error = null)
                => new(new(purpose, before, GetClipboardSequenceNumber(), OwnerRole(productPid), kind,
                    text?.Length, kind == "unicode_text" && text == expected, error), text);
            if (!IsClipboardFormatAvailable(UnicodeText)) return Result("unicode_format_absent");
            var data = GetClipboardData(UnicodeText);
            if (data == IntPtr.Zero) return Result("native_error", error: Marshal.GetLastWin32Error());
            var bytes = GlobalSize(data).ToUInt64();
            if (bytes == 0) return Result("native_error", error: Marshal.GetLastWin32Error());
            if (bytes > MaximumBytes) return Result("allocation_over_bound");
            var address = GlobalLock(data);
            if (address == IntPtr.Zero) return Result("native_error", error: Marshal.GetLastWin32Error());
            try
            {
                var maximumCharacters = (int)bytes / 2;
                for (var count = 0; count < maximumCharacters; count++)
                    if (Marshal.ReadInt16(address, count * 2) == 0)
                        return Result("unicode_text", Marshal.PtrToStringUni(address, count) ?? "");
                return Result("unterminated_unicode_text");
            }
            finally { _ = GlobalUnlock(data); }
        }
        finally
        {
            if (!CloseClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error(), "Native clipboard close failed.");
        }
    }

    private static string OwnerRole(int productPid)
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return "none";
        GetWindowThreadProcessId(owner, out var pid);
        return pid == Environment.ProcessId ? "test" : pid == productPid ? "product" : "foreign";
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
}
