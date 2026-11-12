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
internal sealed record FileTypeControlObservation(string AutomationId, string SafeName, bool Owned, bool Visible,
    bool ValueSupported, string? HtmlValue, bool SelectionSupported, int SelectionCount, string[] HtmlSelections);
internal sealed record SaveNameObservation(string Action, string UiaValue, string WindowMessageValue, string FileType,
    string? ExpectedFullName, bool FullMatch, bool HiddenExtensionMatch, bool ExtensionProbe,
    bool OwnedEdit, bool ExtensionVerified = false, string? ErrorType = null,
    FileTypeControlObservation[]? TypeControls = null, string? LegacyTypeMessage = null);
internal sealed record Probe(string Stage, string Control, long ElapsedMs, bool ProcessAlive, string? ErrorType);
internal sealed record ActionProbe(string Control, string Pattern, bool Passed, string? ErrorType);
internal sealed record RowSelectionProbe(string List, string Item, int ItemsInspected, bool TargetRealized,
    bool Passed, string? ErrorType);
internal sealed record ClickDiagnostic(string Control, string? TargetRole, string? HitRole, bool NativeOwned,
    string[] AncestorRoles, HitTargetGuard.Identity[] Ancestors, int? FixedIdMatches,
    bool FromPointObserved, bool TargetMatched, string? FromPointResult, HitTargetGuard.Viewport[] Viewports,
    bool GeometryReady, string Result, long ElapsedMs, string? ErrorType);
internal sealed record ClickReadinessDiagnostic(string Control, int Attempts, ClickDiagnostic? First,
    ClickDiagnostic? Last, bool PhysicalInputSent, long ElapsedMs, int SharedPickerDeadlineMs, string? ErrorType);
internal sealed record CloseDiagnostic(long RequestElapsedMs, long WaitElapsedMs, int WaitDeadlineMs,
    bool ProcessAlive, int? ExitCode, string? ErrorType);

/// <summary>Only talks to a separately launched, exact packaged executable. No product assembly reference.</summary>
internal sealed class DesktopDriver : IDisposable
{
    private readonly Process process;
    private readonly List<Probe> probes = [];
    private readonly List<ActionProbe> actions = [];
    private readonly List<RowSelectionProbe> rowSelections = [];
    private readonly List<ClickReadinessDiagnostic> clicks = [];
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    internal int ProcessId => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal IReadOnlyList<Probe> Probes => probes;
    internal IReadOnlyList<ActionProbe> Actions => actions;
    internal IReadOnlyList<RowSelectionProbe> RowSelections => rowSelections;
    internal IReadOnlyList<ClickReadinessDiagnostic> Clicks => clicks;
    internal CloseDiagnostic? CloseObservation { get; private set; }
    internal string Stage { get; private set; } = "launch";
    internal string? LastControl { get; private set; }
    internal string? LastErrorType { get; private set; }
    internal bool LiveInstallationStarted { get; set; }
    internal int ChooserInteractions { get; private set; }
    internal Dictionary<string, string> SaveDialogNames { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, SaveNameObservation> SaveDialogObservations { get; } = new(StringComparer.Ordinal);
    internal List<SaveNameObservation> SaveDialogHistory { get; } = [];
    internal List<Screenshot> PickerScreenshots { get; } = [];
    internal string? PickerEvidenceDirectory { get; set; }

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
    internal Task<bool> VisibleInOwnedViewportAsync(string id) => OptionalAsync(id, element =>
    {
        if (element.Current.ProcessId != process.Id || !Visible(element)) return false;
        var rectangle = element.Current.BoundingRectangle;
        var point = new Point { X = (int)(rectangle.X + rectangle.Width / 2), Y = (int)(rectangle.Y + rectangle.Height / 2) };
        return CheckTargetGeometry(element, rectangle, point, MainWindow(), _ => { }).Matches;
    });
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
        if (id.StartsWith("event:", StringComparison.Ordinal)) { SelectEventRow(id); return; }
        var element = Required(id);
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    });
    private void SelectEventRow(string id)
    {
        Stage = "select_event_row"; LastControl = id;
        var inspected = 0; var targetRealized = false; string? errorType = null;
        try
        {
            var list = Required("EventList");
            if (list.Current.ProcessId != process.Id) throw new InvalidOperationException("Event list is not owned by the retained product.");
            var container = (ItemContainerPattern)list.GetCurrentPattern(ItemContainerPattern.Pattern);
            AutomationElement? item = null, target = null;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            while ((item = container.FindItemByProperty(item, null, null)) is not null)
            {
                if (++inspected > 50) throw new InvalidOperationException("UI list exceeds its 50-item page bound.");
                if (item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized))
                    ((VirtualizedItemPattern)virtualized).Realize();
                var identity = item.Current.AutomationId;
                if (!identity.StartsWith("event:", StringComparison.Ordinal) || !identities.Add(identity))
                    throw new InvalidOperationException("Event page has missing or duplicate original identities.");
                if (identity == id) target = item;
            }
            if (target is null) throw new RequiredControlMissingException(id);
            // Enumeration can recycle earlier containers. Keep the exact item provider and
            // realize it again, instead of searching only currently rendered tree children.
            if (target.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var targetVirtualized))
            {
                ((VirtualizedItemPattern)targetVirtualized).Realize(); targetRealized = true;
            }
            if (target.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll))
                ((ScrollItemPattern)scroll).ScrollIntoView();
            if (target.Current.ProcessId != process.Id || target.Current.AutomationId != id)
                throw new InvalidOperationException("Realized event does not preserve the requested original identity.");
            ((SelectionItemPattern)target.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        }
        catch (Exception error) { errorType = error.GetType().Name; throw; }
        finally
        {
            if (rowSelections.Count < 128) rowSelections.Add(new("EventList", id, inspected,
                targetRealized, errorType is null, errorType));
        }
    }
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

    private async Task ClickAsync(string id, Stopwatch sharedClock, TimeSpan deadline)
    {
        Stage = "native_click_readiness"; LastControl = id;
        var attempts = 0; var clicked = false;
        ClickDiagnostic? first = null, last = null;
        string? errorType = null;
        ClickTarget? readyTarget = null;
        try
        {
            if (NativeDialog() != IntPtr.Zero) throw new UnexpectedNativeDialogException();
            await Ui(id, () => SetForegroundWindow(MainWindow()));
            await ObservedWait.ActOnceAsync(async () =>
            {
                attempts++;
                readyTarget = await ProbeClickAsync(id, attempts == 1, diagnostic => { first ??= diagnostic; last = diagnostic; });
                return readyTarget is not null;
            }, () => Ui(id, () =>
            {
                var ready = readyTarget!;
                // Readiness polling sends no input. Recheck geometry immediately before the
                // one permitted click and prevent a delayed provider call from sending late input.
                if (sharedClock.Elapsed >= deadline) throw new TimeoutException("Native click exceeded the shared picker deadline.");
                if (!ready.Element.Current.IsEnabled || !Visible(ready.Element)
                    || ready.Element.Current.BoundingRectangle != ready.Rectangle)
                    throw new ClickTargetUnavailableException(id);
                var mainWindow = MainWindow();
                var freshMatches = AutomationElement.FromHandle(mainWindow).FindAll(TreeScope.Element | TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                HitTargetGuard.RequireUniqueCount(freshMatches.Count);
                if (freshMatches[0].Current.ProcessId != process.Id || freshMatches[0].Current.ControlType != ControlType.Button
                    || !freshMatches[0].Current.IsEnabled || !Visible(freshMatches[0])
                    || freshMatches[0].Current.BoundingRectangle != ready.Rectangle)
                    throw new ClickTargetUnavailableException(id);
                if (!CheckTargetGeometry(freshMatches[0], ready.Rectangle, ready.Point, mainWindow, _ => { }).Matches)
                    throw new ClickTargetUnavailableException(id);
                GetWindowThreadProcessId(WindowFromPoint(ready.Point), out var owner);
                if (owner != process.Id) throw new ClickTargetUnavailableException(id);
                if (NativeDialog() != IntPtr.Zero) throw new UnexpectedNativeDialogException();
                if (sharedClock.Elapsed >= deadline) throw new TimeoutException("Native click exceeded the shared picker deadline.");
                GetCursorPos(out var old);
                try
                {
                    SetCursorPos(ready.Point.X, ready.Point.Y);
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                    clicked = true;
                }
                finally { SetCursorPos(old.X, old.Y); }
            }), EnsureAlive, deadline, () => sharedClock.Elapsed, Task.Delay);
        }
        catch (Exception error) { errorType = error.GetType().Name; throw; }
        finally
        {
            Record("native_click_readiness", id, sharedClock.ElapsedMilliseconds, errorType);
            if (clicks.Count < 128) clicks.Add(new(id, attempts, first, last, clicked,
                sharedClock.ElapsedMilliseconds, (int)deadline.TotalMilliseconds, errorType));
        }
    }

    private Task<ClickTarget?> ProbeClickAsync(string id, bool observeHit, Action<ClickDiagnostic> observe) => Ui<ClickTarget?>(id, () =>
    {
        var clock = Stopwatch.StartNew(); var identities = new List<HitTargetGuard.Identity>();
        var viewports = new List<HitTargetGuard.Viewport>();
        string? targetRole = null, hitRole = null, errorType = null;
        string? fromPointResult = null;
        int? fixedIdMatches = null;
        var geometryReady = false; var fromPointObserved = false;
        var nativeOwned = false; var matched = false; var result = "find_target";
        try
        {
            result = "unique_fixed_action";
            var window = MainWindow();
            var matches = AutomationElement.FromHandle(window).FindAll(TreeScope.Element | TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            fixedIdMatches = matches.Count;
            if (matches.Count == 0) { result = "missing_fixed_id"; return null; }
            HitTargetGuard.RequireUniqueCount(matches.Count);
            var element = matches[0];
            targetRole = element.Current.ControlType.ProgrammaticName;
            result = "visible_enabled";
            if (element.Current.ProcessId != process.Id || element.Current.ControlType != ControlType.Button
                || !element.Current.IsEnabled || !Visible(element)) return null;
            var rect = element.Current.BoundingRectangle;
            var point = new Point { X = (int)(rect.X + rect.Width / 2), Y = (int)(rect.Y + rect.Height / 2) };
            var clickPoint = new System.Windows.Point(point.X, point.Y);
            GetWindowThreadProcessId(WindowFromPoint(point), out var owner);
            nativeOwned = owner == process.Id;
            result = "native_owner";
            if (!nativeOwned) return null;

            var geometry = CheckTargetGeometry(element, rect, point, window, viewports.Add);
            geometryReady = geometry.Matches; result = geometry.Reason;
            // M8 retained 101 contradictory FromPoint observations over the original 15 s.
            // Keep one observation honestly; physical action eligibility uses target ancestry
            // clipping and the exact modal result, not this provider's contradictory hit branch.
            if (observeHit)
            {
                fromPointObserved = true;
                var hit = AutomationElement.FromPoint(clickPoint);
                HitTargetGuard.Identity? currentIdentity = null;
                var match = HitTargetGuard.Check(element, hit, _ => currentIdentity!.Owned,
                    (_, _) => currentIdentity!.Matches, TreeWalker.RawViewWalker.GetParent, e =>
                    {
                        var current = e.Current;
                        var role = current.ControlType.ProgrammaticName;
                        currentIdentity = new(role, current.ProcessId == process.Id, current.AutomationId == id,
                            current.ControlType == ControlType.Button, current.BoundingRectangle == rect,
                            current.IsEnabled, Visible(e), current.BoundingRectangle.Contains(clickPoint),
                            Automation.Compare(element, e));
                        hitRole ??= role; identities.Add(currentIdentity);
                    });
                matched = match.Matches; fromPointResult = match.Reason;
            }
            return geometryReady ? new ClickTarget(element, rect, point) : null;
        }
        catch (Exception error) { errorType = error.GetType().Name; LastErrorType = errorType; throw; }
        finally
        {
            // Cached fields only: failed UIA calls retain their original exception, with no
            // follow-up provider queries that could replace it or expose unrelated desktop text.
            observe(new(id, targetRole, hitRole, nativeOwned,
                identities.Select(item => item.Role).ToArray(), identities.ToArray(), fixedIdMatches,
                fromPointObserved, matched, fromPointResult, viewports.ToArray(), geometryReady,
                result, clock.ElapsedMilliseconds, errorType));
        }
    });

    private HitTargetGuard.Result CheckTargetGeometry(AutomationElement element, System.Windows.Rect rectangle,
        Point point, IntPtr mainWindow, Action<HitTargetGuard.Viewport> observe)
    {
        if (!GetWindowRect(mainWindow, out var window)) throw new IOException("Owned window bounds unavailable.");
        static HitTargetGuard.Rectangle BoundsOf(System.Windows.Rect value) => new(value.X, value.Y, value.Width, value.Height);
        var target = BoundsOf(rectangle);
        return HitTargetGuard.CheckGeometry(element, target,
            new(window.Left, window.Top, window.Right - window.Left, window.Bottom - window.Top), point.X, point.Y,
            ancestor =>
            {
                var state = ancestor.Current;
                var bounds = BoundsOf(state.BoundingRectangle);
                var isViewport = state.ClassName is "ScrollViewer" or "ScrollContentPresenter"
                    || ancestor.TryGetCurrentPattern(ScrollPattern.Pattern, out _);
                return new(state.ControlType.ProgrammaticName, state.ProcessId == process.Id, isViewport,
                    bounds.Contains(target), bounds.Contains(point.X, point.Y),
                    state.NativeWindowHandle == mainWindow.ToInt64() && state.ControlType == ControlType.Window);
            }, TreeWalker.RawViewWalker.GetParent, observe);
    }

    internal async Task ChooseFileAsync(string action, string? path, bool save = false, bool existingRefusal = false,
        string? expectedDefaultName = null, bool probeHtmlExtension = false)
    {
        Stage = "native_picker_" + action;
        if ((expectedDefaultName is not null || probeHtmlExtension) && !save) throw new ArgumentException("Default filename checks require a save dialog.");
        if (probeHtmlExtension && (path is null || Path.GetExtension(path) != ".html")) throw new ArgumentException("Extension probe needs a new HTML destination.");
        var typedPath = path is not null && probeHtmlExtension ? Path.ChangeExtension(Path.GetFullPath(path), null) : path is null ? null : Path.GetFullPath(path);
        if (existingRefusal && (!save || path is null || !File.Exists(path)))
            throw new IOException("Existing-target refusal requires a deliberate existing save target.");
        if (path is not null && (save ? !Directory.Exists(Path.GetDirectoryName(path)) || (File.Exists(path) && !existingRefusal) : !File.Exists(path)))
            throw new IOException("Picker test target violates isolated existing/new-file precondition.");
        var expectedTitle = action switch
        {
            "PickInstaller" => "选择要记录的安装器", "OpenDatabase" => "打开外部记录库（只读）",
            "ExportMatches" => "导出全部匹配结果", "ExportPage" => "导出本页报告",
            _ => throw new InvalidOperationException("No fixed native picker title contract for this action.")
        };
        var pickerClock = Stopwatch.StartNew(); var pickerDeadline = TimeSpan.FromSeconds(15);
        await ClickAsync(action, pickerClock, pickerDeadline); // Modal InvokePattern can synchronously block.
        IntPtr dialog = IntPtr.Zero;
        var remaining = pickerDeadline - pickerClock.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Native picker readiness exhausted the original discovery deadline.");
        await UntilAsync("native_picker_discovery", action, () =>
        {
            dialog = NativeDialog();
            if (dialog == IntPtr.Zero) return Task.FromResult(false);
            if (NativeText(dialog) != expectedTitle) throw new UnexpectedNativeDialogException();
            return Task.FromResult(true);
        }, remaining);
        Record("expected_native_dialog_confirmed", action, pickerClock.ElapsedMilliseconds, null);
        ChooserInteractions++;
        try
        {
            if (save)
            {
                var readyBudget = pickerDeadline - pickerClock.Elapsed;
                if (readyBudget <= TimeSpan.Zero) throw new TimeoutException("Save dialog default name exceeded original picker deadline.");
                await UntilAsync("native_save_default", action, () => Sta(() =>
                {
                    var edit = FilenameEdit(dialog);
                    GetWindowThreadProcessId(edit.Window, out var editOwner);
                    var element = AutomationElement.FromHandle(edit.Window);
                    var owned = editOwner == process.Id && element.Current.ProcessId == process.Id
                        && element.Current.NativeWindowHandle == edit.Window.ToInt64();
                    if (!owned) throw new InvalidOperationException("Filename edit is not owned by the retained product.");
                    var value = ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                    var messageValue = NativeText(edit.Window);
                    var legacyTypeMessage = NativeChildren(dialog).Where(item => item.Id == 1136 && item.Class == "ComboBox")
                        .Select(item => NativeText(item.Window)).SingleOrDefault() ?? "";
                    var type = ReadHtmlFileType(dialog, out var typeControls);
                    SaveDialogNames[action] = value;
                    var full = expectedDefaultName is not null && value == expectedDefaultName;
                    var hidden = expectedDefaultName is not null && value == Path.GetFileNameWithoutExtension(expectedDefaultName)
                        && IsHtmlFileType(type);
                    SaveDialogObservations[action] = new(action, value, messageValue, type, expectedDefaultName, full, hidden, probeHtmlExtension, owned,
                        TypeControls: typeControls, LegacyTypeMessage: legacyTypeMessage);
                    return expectedDefaultName is null || ((full || hidden) && IsHtmlFileType(type));
                }), readyBudget);
                if (PickerEvidenceDirectory is not null)
                    PickerScreenshots.Add(await Sta(() => CaptureWindow(dialog, PickerEvidenceDirectory,
                        "save-default-" + action + "-" + ChooserInteractions + ".png", "isolated-native-save-dialog-default")));
            }
            await Sta(() =>
            {
                SetForegroundWindow(dialog);
                if (path is null && !save) { Forms.SendKeys.SendWait("{ESC}"); return; }
                var edit = FilenameEdit(dialog);
                if (path is null) { Forms.SendKeys.SendWait("{ESC}"); return; }
                AutomationElement.FromHandle(edit.Window).SetFocus();
                Forms.SendKeys.SendWait("^a"); Forms.SendKeys.SendWait(KeysLiteral(typedPath!));
                Forms.SendKeys.SendWait("{TAB}");
            });
            if (path is not null)
            {
                await UntilAsync("native_picker_value", action, () => Task.FromResult(NativeChildren(dialog).Any(item =>
                    item.Id is 1001 or 1148 or 1152 && item.Class == "Edit" && IsWindowVisible(item.Window)
                    && (NativeText(item.Window) == typedPath || (probeHtmlExtension && NativeText(item.Window) == Path.GetFullPath(path))))), TimeSpan.FromSeconds(5));
                await Ui(action, () =>
                {
                    var accept = NativeChildren(dialog).Single(item => item.Id == 1 && item.Class == "Button" && IsWindowVisible(item.Window));
                    ((InvokePattern)AutomationElement.FromHandle(accept.Window).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                });
            }
            await UntilAsync("native_picker_closed", action, () => Task.FromResult(!IsWindowVisible(dialog)), TimeSpan.FromSeconds(15));
        }
        catch (Exception error)
        {
            if (save && SaveDialogObservations.TryGetValue(action, out var observation))
                SaveDialogObservations[action] = observation with { ErrorType = error.GetType().Name };
            if (save && dialog != IntPtr.Zero && IsWindowVisible(dialog) && PickerEvidenceDirectory is not null)
            {
                try { PickerScreenshots.Add(await Sta(() => CaptureWindow(dialog, PickerEvidenceDirectory,
                    "save-default-failed-" + action + "-" + ChooserInteractions + ".png", "failed-isolated-native-save-dialog"))); }
                catch { /* Preserve the original observation failure. */ }
            }
            if (dialog != IntPtr.Zero) PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
            throw;
        }
        finally
        {
            if (save && SaveDialogObservations.TryGetValue(action, out var observed)) SaveDialogHistory.Add(observed);
        }
    }

    private static bool IsHtmlFileType(string value) => value is "本地 HTML 报告" or "本地 HTML 报告 (*.html)";
    private string ReadHtmlFileType(IntPtr dialog, out FileTypeControlObservation[] observations)
    {
        var controls = AutomationElement.FromHandle(dialog).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox));
        if (controls.Count > 8) throw new InvalidOperationException("Native save dialog exceeds bounded ComboBox inspection.");
        var diagnostics = new List<FileTypeControlObservation>(); var matched = new List<string>();
        foreach (AutomationElement control in controls)
        {
            var current = control.Current;
            var owned = current.ProcessId == process.Id;
            var visible = !current.IsOffscreen && current.IsEnabled;
            var hasValue = control.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern);
            var value = hasValue ? ((ValuePattern)valuePattern).Current.Value : "";
            var hasSelection = control.TryGetCurrentPattern(SelectionPattern.Pattern, out var selectionPattern);
            var selection = hasSelection ? ((SelectionPattern)selectionPattern).Current.GetSelection() : [];
            if (selection.Length > 8) throw new InvalidOperationException("Native file-type selection exceeds bound.");
            var selected = selection.Where(item => item.Current.ProcessId == process.Id).Select(item => item.Current.Name).ToArray();
            var htmlValues = selected.Where(IsHtmlFileType).ToArray();
            // Publish only fixed type labels. Address/name ComboBoxes may contain paths.
            var safeName = current.Name is "Save as type:" or "Save as type" or "保存类型:" or "保存类型：" or "文件类型:" or "文件类型："
                || IsHtmlFileType(current.Name) ? current.Name : "unrelated-combobox";
            diagnostics.Add(new(current.AutomationId, safeName, owned, visible, hasValue,
                IsHtmlFileType(value) ? value : null, hasSelection, selection.Length, htmlValues));
            if (owned && visible)
            {
                if (hasSelection && selection.Length == 1 && htmlValues.Length == 1) matched.Add(htmlValues[0]);
                else if (hasValue && IsHtmlFileType(value)) matched.Add(value);
            }
        }
        observations = diagnostics.ToArray();
        if (matched.Count > 1) throw new InvalidOperationException("HTML file-type control is not unique.");
        return matched.SingleOrDefault() ?? "";
    }

    private static (IntPtr Window, int Id, string Class) FilenameEdit(IntPtr dialog)
    {
        var edits = NativeChildren(dialog).Where(item => item.Id is 1001 or 1148 or 1152 && item.Class == "Edit" && IsWindowVisible(item.Window)).ToArray();
        if (edits.Length != 1) throw new InvalidOperationException("Native file picker has no unique filename entry.");
        return edits[0];
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
    internal Task<Screenshot> CaptureAsync(string output, string name, string kind) => Ui("window", () => CaptureWindow(MainWindow(), output, name, kind));
    private static Screenshot CaptureWindow(IntPtr window, string output, string name, string kind)
    {
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
    }
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
    internal sealed class UnexpectedNativeDialogException() : InvalidOperationException("Owned native dialog does not match the fixed action title.");
    private sealed record ClickTarget(AutomationElement Element, System.Windows.Rect Rectangle, Point Point);
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
