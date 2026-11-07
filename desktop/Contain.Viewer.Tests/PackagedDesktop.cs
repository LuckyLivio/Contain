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
    private readonly ReadyTrace readyTrace;
    private readonly int lookupLaunch = RequiredLookupDiagnostics.BeginLaunch();
    private int lookupFailureSampled;
    private IntPtr nativeReadyRoot;
    private sealed record CachedControl(IntPtr Root, IntPtr Target);
    private CachedControl? sessionTitleCache;
    internal int ProcessId => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal string Executable { get; }
    internal string WorkingDirectory { get; }
    internal int DatabasePathInteractions { get; private set; }
    internal int ReportDialogInteractions { get; private set; }
    internal int ReportDialogSaves { get; private set; }
    internal int InstallerPickerCancels { get; private set; }
    internal int InstallerPickerSelections { get; private set; }
    internal int WatchPickerSelections { get; private set; }
    internal int AdvancedInteractions { get; private set; }
    internal int EnvironmentCheckInteractions { get; private set; }
    internal string ConfigurationDialogStage { get; private set; } = "not_started";
    internal Action? ConfigurationProgress { get; set; }
    internal string ReportDialogStage { get; private set; } = "not_started";
    internal string ReportDialogDiagnostic { get; private set; } = string.Empty;
    internal Action? ReportDialogProgress { get; set; }
    internal string CleanupState { get; private set; } = "not_disposed";
    private string readyStage = "not_started";
    private string readyTimeoutKind = "none";
    private int readyDiscoveryAttempts;
    private int readyEnabledAttempts;
    private int readyProbeAttempts;
    private long readyElapsedMilliseconds;
    private long readyLastProbeMilliseconds;

    internal sealed record ReadyDiagnostic(string Launch, string Stage, string TimeoutKind,
        int DiscoveryAttempts, int EnabledAttempts, int UiProbeAttempts, long ElapsedMs, long LastUiProbeMs,
        int ProcessId, bool? ProcessAlive, int? ProcessExitCode, bool? OwnedMainWindowPresent,
        bool? MainWindowVisible, bool? MainWindowEnabled, bool? MainWindowHung, bool NativeSnapshotAvailable,
        ReadyTrace.Diagnostic Startup);

    internal PackagedDesktop(string package, string workingDirectory, bool savedRecordsOnly = false)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TestRecordIsolation.Variable)))
            throw new InvalidOperationException("A packaged test process requires the isolated record-library guard.");
        Executable = Path.GetFullPath(Path.Combine(package, "Contain.Viewer.exe"));
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        this.savedRecordsOnly = savedRecordsOnly;
        readyTrace = ReadyDiagnostics.Begin();
        try
        {
            process = Process.Start(new ProcessStartInfo(Executable)
            {
                UseShellExecute = false, WorkingDirectory = WorkingDirectory
            }) ?? throw new IOException("The packaged desktop process did not start.");
        }
        catch (Exception error) { readyTrace.StartFailed(error); throw; }
        readyTrace.Started(process.Id);
        // Retain the exact process handle and creation identity for the opt-in
        // saved-records cleanup. Never discover a replacement process by PID.
        try
        {
            _ = process.Handle;
            startedAt = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception error) { readyTrace.HandleSetupFailed(error); throw; }
    }

    internal async Task ReadyAsync(string launch = "unlabelled")
    {
        if (launch is not ("unlabelled" or "default_history" or "seeded_first" or "seeded_restart"))
            throw new InvalidOperationException("The readiness diagnostic requires a fixed launch label.");
        var clock = Stopwatch.StartNew();
        readyDiscoveryAttempts = readyEnabledAttempts = readyProbeAttempts = 0;
        readyLastProbeMilliseconds = 0;
        readyTimeoutKind = "none";
        readyStage = "discover_control";
        readyTrace.Stage(readyStage, launch);
        using var observation = new CancellationTokenSource();
        var observer = ObserveNativeReadinessAsync(observation.Token);
        try
        {
            await WaitAsync(async () =>
            {
                readyDiscoveryAttempts++;
                return !process.HasExited && await ReadyProbeAsync(() =>
                {
                    var found = Find("NewInstall", trace: readyTrace) is not null;
                    if (found) readyTrace.Found();
                    return found;
                });
            }, TimeSpan.FromSeconds(40));
            readyStage = "wait_enabled";
            readyTrace.Stage(readyStage, launch);
            await WaitAsync(() =>
            {
                readyEnabledAttempts++;
                return ReadyProbeAsync(() =>
                {
                    var control = Find("NewInstall", trace: readyTrace) ?? throw new RequiredProductControlMissingException("NewInstall");
                    var enabled = readyTrace.Call("target_is_enabled", () => control.Current.IsEnabled);
                    if (enabled) readyTrace.Found(enabled: true);
                    return enabled;
                });
            }, TimeSpan.FromSeconds(40));
            readyStage = "ready";
            readyTrace.Stage(readyStage, launch);
        }
        catch (TimeoutException)
        {
            if (readyTimeoutKind == "none")
                readyTimeoutKind = readyStage == "discover_control" ? "control_discovery_40s" : "control_enabled_40s";
            readyTrace.Timeout(readyTimeoutKind);
            throw;
        }
        finally
        {
            readyElapsedMilliseconds = clock.ElapsedMilliseconds;
            // Freeze before cancellation/cleanup: a timed-out Task.Run may
            // complete later, but must never overwrite the deadline evidence.
            readyTrace.Finish();
            observation.Cancel();
            // The observer adds no wait to the original readiness deadline.
            // Late native samples are ignored by the frozen trace; no UIA
            // request or product process lifetime depends on this observer.
            _ = observer.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private Task ObserveNativeReadinessAsync(CancellationToken cancellation) => Task.Run(async () =>
    {
        var checkpoints = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                readyTrace.Native(NativeReadiness());
                if (++checkpoints % 10 == 0) ReadyDiagnostics.Flush();
                await Task.Delay(100, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    });

    private ReadyTrace.NativeState NativeReadiness()
    {
        bool? alive = null, present = null, visible = null, enabled = null, hung = null;
        int? exitCode = null;
        var available = false;
        try
        {
            // Read the retained process handle without refreshing Process's
            // mutable cache concurrently with the existing readiness predicate.
            var wait = WaitForSingleObject(process.Handle, 0);
            alive = wait == 0x00000102 ? true : wait == 0 ? false : null;
            if (alive is null) return new(null, null, null, null, null, null, false);
            if (alive == false)
            {
                if (GetExitCodeProcess(process.Handle, out var code)) exitCode = unchecked((int)code);
                present = false;
            }
            else
            {
                var window = IntPtr.Zero;
                var enumerationCompleted = EnumWindows((candidate, _) =>
                {
                    GetWindowThreadProcessId(candidate, out var candidateOwner);
                    if (candidateOwner != process.Id || !IsWindowVisible(candidate) || GetWindow(candidate, 4) != IntPtr.Zero)
                        return true;
                    window = candidate; return false;
                }, IntPtr.Zero);
                if (!enumerationCompleted && window == IntPtr.Zero)
                    return new(alive, null, null, null, null, null, false);
                GetWindowThreadProcessId(window, out var owner);
                present = window != IntPtr.Zero && owner == process.Id;
                if (present == true)
                {
                    Interlocked.CompareExchange(ref nativeReadyRoot, window, IntPtr.Zero);
                    visible = IsWindowVisible(window);
                    enabled = IsWindowEnabled(window);
                    hung = IsHungAppWindow(window);
                }
            }
            available = true;
        }
        catch (Exception) { /* No captions, paths, UIA trees or native error messages. */ }
        return new(alive, exitCode, present, visible, enabled, hung, available);
    }

    private async Task<bool> ReadyProbeAsync(Func<bool> action)
    {
        readyProbeAttempts++;
        readyTrace.Queued(readyProbeAttempts);
        var clock = Stopwatch.StartNew();
        // Keep the exact existing UIA worker/deadline behavior. Only identify
        // whether its 20-second wrapper or the outer 40-second wait expired.
        var probe = Task.Run(() => { readyTrace.WorkerStarted(); return action(); });
        readyTrace.ArmProbeDeadline(20000);
        try { return await probe.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException error)
        {
            readyTimeoutKind = probe.IsFaulted && ReferenceEquals(probe.Exception?.InnerException, error)
                ? "uia_operation_timeout" : "uia_probe_20s";
            throw;
        }
        finally { readyLastProbeMilliseconds = clock.ElapsedMilliseconds; }
    }

    internal ReadyDiagnostic ReadySnapshot(string launch)
    {
        if (launch is not ("default_history" or "seeded_first" or "seeded_restart"))
            throw new InvalidOperationException("The readiness diagnostic requires a fixed launch label.");
        var native = NativeReadiness();
        return new(launch, readyStage, readyTimeoutKind, readyDiscoveryAttempts, readyEnabledAttempts,
            readyProbeAttempts, readyElapsedMilliseconds, readyLastProbeMilliseconds, process.Id, native.ProcessAlive,
            native.ExitCode, native.MainWindowPresent, native.MainWindowVisible, native.MainWindowEnabled,
            native.MainWindowHung, native.Available, readyTrace.Snapshot());
    }

    internal Task<bool> ExistsAsync(string id) => Ui(() => Find(id) is not null);
    internal Task<bool> EnabledAsync(string id) => Ui(() => Required(id).Current.IsEnabled);
    internal Task<string> TextAsync(string id) => Ui(() => Text(Required(id)));
    // A transient status window may close normally while a sample is being
    // taken. Keep discovery and reading in one UIA operation; required reads
    // and all completion assertions deliberately retain their fail-fast path.
    internal Task<string?> TryTextAsync(string id) => Ui(() => TryText(() => Find(id)));
    internal static string? TryText(Func<Automation.AutomationElement?> find)
    {
        try { return find() is { } element ? Text(element) : null; }
        catch (Automation.ElementNotAvailableException) { return null; }
    }
    // Static labels can expose a stable AccessibleName rather than their
    // displayed filename. Keep that accessibility metadata intact and read
    // the actual visible HWND caption for visual-text assertions.
    internal Task<string> NativeVisibleTextAsync(string id) => Ui(() =>
        NativeVisibleText(new IntPtr(Required(id).Current.NativeWindowHandle), process.Id));

    internal static string NativeVisibleText(IntPtr window, int expectedProcessId)
    {
        GetWindowThreadProcessId(window, out var owner);
        if (window == IntPtr.Zero || owner != expectedProcessId || !IsWindowVisible(window))
            throw new InvalidOperationException("The requested visual caption has no visible owned HWND.");
        if (SendMessageTimeoutValue(window, 0x000E, IntPtr.Zero, IntPtr.Zero, 0x0003, 2000, out var length) == IntPtr.Zero
            || length.ToUInt64() > 65536)
            throw new IOException("The visible caption length could not be read within its bound.");
        var value = new System.Text.StringBuilder(checked((int)length.ToUInt64() + 1));
        if (SendMessageTimeoutBuffer(window, 0x000D, new IntPtr(value.Capacity), value, 0x0003, 2000, out var copied) == IntPtr.Zero
            || copied.ToUInt64() != length.ToUInt64() || value.Length != (int)length.ToUInt64())
            throw new IOException("The complete visible caption could not be read consistently.");
        return value.ToString();
    }
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
        var root = GetAncestor(window, 2);
        for (var parent = GetParent(window); parent != IntPtr.Zero; parent = GetParent(parent))
        {
            if (!GetClientRect(parent, out var client)) return false;
            var origin = new NativePoint();
            if (!ClientToScreen(parent, ref origin)) return false;
            if (bounds.Left < origin.X || bounds.Top < origin.Y
                || bounds.Right > origin.X + client.Right || bounds.Bottom > origin.Y + client.Bottom) return false;
            if (parent == root) return true;
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
        // Reuse this pre-existing read. Diagnosis adds no UIA call to a
        // successful lookup, and never retains the element or its text.
        if (id == "SessionTitle" && sessionTitleCache is null)
        {
            var root = GetAncestor(nativeWindow, 2);
            GetWindowThreadProcessId(nativeWindow, out var targetOwner);
            GetWindowThreadProcessId(root, out var rootOwner);
            if (nativeWindow != IntPtr.Zero && root != IntPtr.Zero
                && targetOwner == process.Id && rootOwner == process.Id)
                Interlocked.CompareExchange(ref sessionTitleCache, new(root, nativeWindow), null);
        }
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
    internal Task InvokeAsync(string id) => Ui(() =>
    {
        if (id == "CheckEnvironment") EnvironmentCheckInteractions++;
        Invoke(Required(id));
    });

    internal Task<bool> FocusedAsync(string id) => Ui(() => Required(id).Current.HasKeyboardFocus);

    internal async Task KeyboardActivateAsync(string id, bool focus = true)
    {
        var root = await Ui(() =>
        {
            var element = Required(id);
            var window = new IntPtr(element.Current.NativeWindowHandle);
            var owner = GetAncestor(window, 2);
            SetForegroundWindow(owner);
            SetWindowPos(owner, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
            if (focus) element.SetFocus();
            return owner;
        });
        await WaitAsync(async () => GetForegroundWindow() == root && await FocusedAsync(id), TimeSpan.FromSeconds(5));
        if (id == "CheckEnvironment") EnvironmentCheckInteractions++;
        SendKeys.SendWait(" ");
    }

    internal async Task TabToAsync(string id)
    {
        for (var index = 0; index < 25; index++)
        {
            if (await FocusedAsync(id)) return;
            SendKeys.SendWait("{TAB}");
            await Task.Delay(40);
        }
        throw new InvalidOperationException("The ordinary keyboard tab order did not reach " + id + ".");
    }

    internal async Task ClickTwiceAsync(string id)
    {
        var window = await Ui(() => new IntPtr(Required(id).Current.NativeWindowHandle));
        ClickNativeButton(window); ClickNativeButton(window);
    }

    internal Task<uint> ControlWindowDpiAsync(string id) => Ui(() => GetDpiForWindow(ControlRoot(id)));

    internal async Task ResizeControlWindowAsync(string id, int width, int height)
    {
        await Ui(() =>
        {
            if (!SetWindowPos(ControlRoot(id), IntPtr.Zero, 0, 0, width, height, 0x0002 | 0x0004))
                throw new IOException("The owned configuration window could not resize.");
        });
        await Task.Delay(150);
    }

    internal Task<ImageResult> ScreenshotControlWindowAsync(string id, string directory, string name) => Ui(() =>
        ScreenshotWindow(ControlRoot(id), directory, name));

    private IntPtr ControlRoot(string id)
    {
        var root = GetAncestor(new IntPtr(Required(id).Current.NativeWindowHandle), 2);
        GetWindowThreadProcessId(root, out var owner);
        if (root == IntPtr.Zero || owner != process.Id) throw new InvalidOperationException("The configuration window is not owned by the actual test process.");
        return root;
    }

    internal Task ChooseInstallerAsync(string? path) => ChooseConfigurationPathAsync("NewInstall", path, false);
    internal Task ChooseWatchRootAsync(string path) => ChooseConfigurationPathAsync("ChooseWatchRoot", path, true);

    private async Task ChooseConfigurationPathAsync(string action, string? path, bool folder)
    {
        IntPtr dialog = IntPtr.Zero;
        try
        {
            Stage("activate_" + action);
            await KeyboardActivateAsync(action, focus: action == "NewInstall");
            Stage("wait_dialog");
            await WaitAsync(() => { dialog = FindReportDialog(); return Task.FromResult(dialog != IntPtr.Zero); }, TimeSpan.FromSeconds(15));
            SetForegroundWindow(dialog);
            if (path is null)
            {
                Stage("cancel"); SendKeys.SendWait("{ESC}");
            }
            else
            {
                path = Path.GetFullPath(path);
                if (folder ? !Directory.Exists(path) : !File.Exists(path)) throw new IOException("The native picker target must already exist.");
                Stage("enter_path");
                var controls = ReportControls(dialog);
                var edits = controls.Where(control => IsWindowVisible(control.Window) && control.Class.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                    && control.Id is 1001 or 1148 or 1152).ToArray();
                if (edits.Length != 1) throw new InvalidOperationException("The native configuration picker has no unique path entry.");
                // Focus the actual native edit, then type literal keys. As with
                // the report dialog, WM_SETTEXT alone is not an accepted path.
                await Ui(() => Automation.AutomationElement.FromHandle(edits[0].Window).SetFocus());
                await WaitAsync(() => Task.FromResult(HasNativeFocus(dialog, edits[0].Window)), TimeSpan.FromSeconds(5));
                SendKeys.SendWait("^a"); SendKeys.SendWait(KeysLiteral(path));
                await WaitAsync(() =>
                {
                    var value = new System.Text.StringBuilder(path.Length + 2);
                    return Task.FromResult(SendMessageTimeoutBuffer(edits[0].Window, 0x000D, new IntPtr(value.Capacity), value,
                        0x0003, 2000, out _) != IntPtr.Zero && value.ToString() == path);
                }, TimeSpan.FromSeconds(5));
                Stage("accept_path");
                var accept = GetDlgItem(dialog, 1);
                if (accept == IntPtr.Zero || !IsWindowVisible(accept))
                    accept = controls.Single(control => control.Id == 1 && IsWindowVisible(control.Window)
                        && control.Class.Equals("Button", StringComparison.OrdinalIgnoreCase)).Window;
                for (var index = 0; index < 20 && !HasNativeFocus(dialog, accept); index++)
                {
                    if (GetForegroundWindow() != dialog) throw new InvalidOperationException("Native picker lost foreground ownership before acceptance.");
                    SendKeys.SendWait("{TAB}"); await Task.Delay(40);
                }
                if (!HasNativeFocus(dialog, accept)) throw new InvalidOperationException("The native picker accept action is not keyboard reachable.");
                SendKeys.SendWait(" ");
            }
            await WaitAsync(() => Task.FromResult(!IsWindowVisible(dialog)), TimeSpan.FromSeconds(15));
            if (folder) WatchPickerSelections++;
            else if (path is null) InstallerPickerCancels++;
            else InstallerPickerSelections++;
            Stage(path is null ? "cancelled" : "selected");
        }
        catch
        {
            Stage("failed_" + ConfigurationDialogStage);
            if (dialog != IntPtr.Zero) PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
            throw;
        }
        void Stage(string stage) { ConfigurationDialogStage = stage; ConfigurationProgress?.Invoke(); }
    }

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
                Stage("focus_filename");
                ClickNativeButton(editable[0].Window);
                await WaitAsync(() => Task.FromResult(HasNativeFocus(dialog, editable[0].Window)), TimeSpan.FromSeconds(5));
                Stage("type_filename");
                // The shell's IFileDialog tracks typed input separately from
                // the Edit HWND text. WM_SETTEXT readback alone does not prove
                // its accepted filename changed. Exercise real keyboard input,
                // then leave the edit to commit it before clicking Save.
                SendKeys.SendWait("^a");
                SendKeys.SendWait(KeysLiteral(outputPath));
                SendKeys.SendWait("{TAB}");
                Stage("verify_typed_filename");
                // SendInput-backed SendKeys may return before a different
                // process drains its keyboard queue. Poll only the readback;
                // input is never repeated or accepted under another window.
                await WaitAsync(() =>
                {
                    if (GetForegroundWindow() != dialog) return Task.FromResult(false);
                    var readback = new System.Text.StringBuilder(outputPath.Length + 2);
                    return Task.FromResult(SendMessageTimeoutBuffer(editable[0].Window, 0x000D,
                        new IntPtr(readback.Capacity), readback, 0x0003, 2000, out _) != IntPtr.Zero
                        && readback.ToString() == outputPath);
                }, TimeSpan.FromSeconds(5));
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

    private bool HasNativeFocus(IntPtr dialog, IntPtr control)
    {
        var thread = GetWindowThreadProcessId(control, out var owner);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return owner == process.Id && GetForegroundWindow() == dialog
            && GetGUIThreadInfo(thread, ref info) && info.Focus == control;
    }

    private static string KeysLiteral(string value) => string.Concat(value.Select(character =>
        "+^%~(){}[]".Contains(character) ? "{" + character + "}" : character.ToString()));

    private void ClickNativeButton(IntPtr button) => SendNativeClick(button, process.Id);

    internal static void SendNativeClick(IntPtr button, int expectedProcessId)
    {
        if (button == IntPtr.Zero || !IsWindowVisible(button)) throw new InvalidOperationException("The native test button is absent or invisible.");
        GetWindowThreadProcessId(button, out var owner);
        if (owner != expectedProcessId) throw new InvalidOperationException("The native test button belongs to another process.");
        var root = GetAncestor(button, 2);
        if (root == IntPtr.Zero) throw new InvalidOperationException("The native test button has no root window.");
        SetForegroundWindow(root);
        SetWindowPos(root, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        if (!GetWindowRect(button, out var rectangle) || rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top)
            throw new InvalidOperationException("The native test button has no clickable bounds.");
        var point = new NativePoint { X = rectangle.Left + (rectangle.Right - rectangle.Left) / 2,
            Y = rectangle.Top + (rectangle.Bottom - rectangle.Top) / 2 };
        if (!NativeClickTargetIsExposed(button, expectedProcessId))
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

    internal static bool NativeClickTargetIsExposed(IntPtr control, int expectedProcessId)
    {
        if (control == IntPtr.Zero || !IsWindowVisible(control) || !GetWindowRect(control, out var bounds)
            || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        GetWindowThreadProcessId(control, out var owner);
        var root = GetAncestor(control, 2);
        if (owner != expectedProcessId || root == IntPtr.Zero) return false;
        var point = new NativePoint { X = bounds.Left + (bounds.Right - bounds.Left) / 2,
            Y = bounds.Top + (bounds.Bottom - bounds.Top) / 2 };
        var hit = WindowFromPoint(point);
        GetWindowThreadProcessId(hit, out var hitOwner);
        if (hitOwner != expectedProcessId || GetAncestor(hit, 2) != root) return false;
        // Preserve the enabled-control obstruction check exactly. Windows skips
        // disabled controls in WindowFromPoint, so that case needs an explicit
        // child hit test which still rejects an overlapping sibling.
        if (IsWindowEnabled(control)) return hit == control || IsChild(control, hit);
        for (var parent = GetParent(control); parent != IntPtr.Zero; parent = GetParent(parent))
        {
            if (!GetClientRect(parent, out var client)) return false;
            var origin = new NativePoint();
            if (!ClientToScreen(parent, ref origin) || bounds.Left < origin.X || bounds.Top < origin.Y
                || bounds.Right > origin.X + client.Right || bounds.Bottom > origin.Y + client.Bottom) return false;
            if (parent == root) break;
        }
        var current = root;
        for (var depth = 0; depth < 64; depth++)
        {
            var local = point;
            if (!ScreenToClient(current, ref local)) return false;
            // CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT; intentionally do not use
            // CWP_SKIPDISABLED. Every parent/child in the path remains owned.
            var child = ChildWindowFromPointEx(current, local, 0x0001 | 0x0004);
            if (child == IntPtr.Zero) return false;
            if (child == current) return current == control || IsChild(control, current);
            GetWindowThreadProcessId(child, out var childOwner);
            if (childOwner != expectedProcessId || !IsChild(current, child)) return false;
            current = child;
        }
        return false;
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
        if (id == "AdvancedToggle") AdvancedInteractions++;
        var element = Required(id);
        var toggle = (Automation.TogglePattern)element.GetCurrentPattern(Automation.TogglePattern.Pattern);
        if ((toggle.Current.ToggleState == Automation.ToggleState.On) != value) toggle.Toggle();
    });
    internal Task<bool> IsCheckedAsync(string id) => Ui(() =>
    {
        var element = Required(id);
        if (element.TryGetCurrentPattern(Automation.TogglePattern.Pattern, out var toggle))
            return ((Automation.TogglePattern)toggle).Current.ToggleState == Automation.ToggleState.On;
        if (element.TryGetCurrentPattern(Automation.SelectionItemPattern.Pattern, out var selection))
            return ((Automation.SelectionItemPattern)selection).Current.IsSelected;
        throw new InvalidOperationException("The native choice has no check/selection pattern: " + id);
    });

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
        return ScreenshotWindow(process.MainWindowHandle, directory, name);
    });

    private static ImageResult ScreenshotWindow(IntPtr window, string directory, string name)
    {
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
    }

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

    private Automation.AutomationElement? Find(string? id, string? name = null, ReadyTrace? trace = null,
        Action<Automation.AutomationElementCollection>? captureWindows = null)
    {
        T Observe<T>(string operation, Func<T> action) => trace is null ? action() : trace.Call(operation, action);
        var root = Observe("desktop_root_element", () => Automation.AutomationElement.RootElement);
        var windows = Observe("find_owned_windows", () => root.FindAll(Automation.TreeScope.Children,
            new Automation.PropertyCondition(Automation.AutomationElement.ProcessIdProperty, process.Id)));
        captureWindows?.Invoke(windows);
        var condition = id is not null
            ? new Automation.PropertyCondition(Automation.AutomationElement.AutomationIdProperty, id)
            : new Automation.PropertyCondition(Automation.AutomationElement.NameProperty, name!);
        var iterator = Observe("owned_windows_enumerator", windows.GetEnumerator);
        try
        {
            while (Observe("owned_windows_move_next", iterator.MoveNext))
            {
                var window = Observe("owned_window_current", () => (Automation.AutomationElement)iterator.Current);
                var found = Observe("find_target_control", () => window.FindFirst(Automation.TreeScope.Element | Automation.TreeScope.Descendants, condition));
                if (found is not null) return found;
            }
        }
        finally { (iterator as IDisposable)?.Dispose(); }
        return null;
    }

    private Automation.AutomationElement Required(string id)
    {
        Automation.AutomationElementCollection? windows = null;
        var fixedId = RequiredLookupDiagnostics.IsFixedId(id);
        var found = Find(id, captureWindows: fixedId ? value => windows = value : null);
        if (found is not null) return found;
        var original = new RequiredProductControlMissingException(id);
        if (fixedId && RequiredLookupDiagnostics.Current is { } invocation
            && RequiredLookupDiagnostics.TrySampleLaunch(ref lookupFailureSampled))
        {
            // Publish the original failure BEFORE even native diagnosis or disk
            // checkpoints can consume the remaining original Ui deadline.
            invocation.Preserve(original);
            var cached = id == "SessionTitle" ? sessionTitleCache : null;
            var root = cached?.Root ?? nativeReadyRoot;
            var target = cached?.Target ?? IntPtr.Zero;
            var before = LookupNativeState(root, target);
            var trace = RequiredLookupDiagnostics.BeginFailure(lookupLaunch, id, windows?.Count ?? 0, before,
                target == IntPtr.Zero ? "native_readiness_root_candidate" : "prior_same_control_native_handle",
                invocation.OriginalMissElapsedMs, invocation.OriginalMissRemainingMs);
            if (trace is not null)
            {
                invocation.Attach(trace);
                DiagnoseRequiredMiss(id, root, target, windows, before, trace, invocation);
            }
        }
        throw original;
    }

    private RequiredLookupTrace.NativeState LookupNativeState(IntPtr root, IntPtr target)
    {
        bool? alive = null, rootExists = null, rootOwned = null, rootVisible = null,
            rootEnabled = null, rootHung = null, targetExists = null, targetOwned = null,
            targetVisible = null, targetRoot = null;
        var available = false;
        try
        {
            var wait = WaitForSingleObject(process.Handle, 0);
            alive = wait == 0x00000102 ? true : wait == 0 ? false : null;
            if (alive is null) return new(null, root != IntPtr.Zero, null, null, null, null, null,
                target != IntPtr.Zero, null, null, null, null, false);
            rootExists = root != IntPtr.Zero ? IsWindow(root) : null;
            if (rootExists == true)
            {
                GetWindowThreadProcessId(root, out var owner); rootOwned = owner == process.Id && alive == true;
                rootVisible = IsWindowVisible(root); rootEnabled = IsWindowEnabled(root); rootHung = IsHungAppWindow(root);
            }
            targetExists = target != IntPtr.Zero ? IsWindow(target) : null;
            if (targetExists == true)
            {
                GetWindowThreadProcessId(target, out var owner); targetOwned = owner == process.Id && alive == true;
                targetVisible = IsWindowVisible(target); targetRoot = GetAncestor(target, 2) == root;
            }
            available = true;
        }
        catch (Exception) { }
        return new(alive, root != IntPtr.Zero, rootExists, rootOwned, rootVisible, rootEnabled, rootHung,
            target != IntPtr.Zero, targetExists, targetOwned, targetVisible, targetRoot, available);
    }

    private void DiagnoseRequiredMiss(string id, IntPtr root, IntPtr target,
        Automation.AutomationElementCollection? windows, RequiredLookupTrace.NativeState before,
        RequiredLookupTrace trace, LookupUiInvocation invocation)
    {
        trace.Budget(invocation.RemainingMs);
        try
        {
            if (before.ProcessAlive != true || before.RootOwned != true || windows is null)
            { trace.Finish("owned_cached_root_unavailable", false, null); return; }
            var count = Math.Min(windows.Count, RequiredLookupDiagnostics.WindowMetadataLimit);
            var included = false;
            var complete = windows.Count <= RequiredLookupDiagnostics.WindowMetadataLimit;
            for (var index = 0; index < count; index++)
            {
                try
                {
                    var captured = index;
                    var handle = trace.Call("original_window_native_handle", () => windows[captured].Current.NativeWindowHandle);
                    included |= new IntPtr(handle) == root;
                }
                catch (Automation.ElementNotAvailableException) { complete = false; }
                var allRead = complete && index + 1 == windows.Count;
                trace.Metadata(index + 1, included ? true : allRead ? false : null, allRead);
            }
            if (count == 0) trace.Metadata(0, false, true);
            // Recheck retained-process ownership immediately before passing any
            // cached HWND to UIA. A later observation is explicitly diagnostic.
            var current = LookupNativeState(root, target);
            if (current.ProcessAlive != true || current.RootOwned != true)
            { trace.After(current); trace.Finish("owned_cached_root_changed", false, null); return; }
            var directRoot = trace.Call("from_cached_owned_root", () => Automation.AutomationElement.FromHandle(root));
            var direct = trace.Call("find_target_from_cached_root", () => directRoot.FindFirst(
                Automation.TreeScope.Element | Automation.TreeScope.Descendants,
                new Automation.PropertyCondition(Automation.AutomationElement.AutomationIdProperty, id)));
            if (direct is null) trace.Direct(false, null, null, null, null);
            else
            {
                var matches = trace.Call("direct_target_id", () => direct.Current.AutomationId == id);
                var handle = new IntPtr(trace.Call("direct_target_native_handle", () => direct.Current.NativeWindowHandle));
                GetWindowThreadProcessId(handle, out var owner);
                trace.Direct(true, matches, handle == IntPtr.Zero ? null : owner == process.Id,
                    handle == IntPtr.Zero ? null : GetAncestor(handle, 2) == root,
                    handle == IntPtr.Zero || target == IntPtr.Zero ? null : handle == target);
            }
            trace.After(LookupNativeState(root, target));
            trace.Finish("comparison_completed_original_failure_preserved", true, null);
        }
        catch (Exception error)
        {
            trace.After(LookupNativeState(root, target));
            trace.Finish(invocation.RemainingMs <= 0 ? "existing_ui_deadline" : "comparison_failed_original_failure_preserved", false, error);
        }
    }
    // Public summaries already report exception type. Use a fixed type for this
    // harness failure without exposing the control id or a raw exception message.
    internal sealed class RequiredProductControlMissingException(string id)
        : InvalidOperationException("Actual product control is absent: " + id);
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
    private static Task<T> Ui<T>(Func<T> action) => RequiredLookupDiagnostics.RunUiAsync(action, TimeSpan.FromSeconds(20));
    private static Task Ui(Action action) => RequiredLookupDiagnostics.RunUiAsync(() => { action(); return true; }, TimeSpan.FromSeconds(20));

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
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public WindowBounds CaretBounds;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int item);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutBuffer(IntPtr window, uint message, IntPtr wParam, System.Text.StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutValue(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
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
