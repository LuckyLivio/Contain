using System.Diagnostics;
using System.Text.Json;

namespace Contain.Viewer.Tests;

// Fixed-size, test-only startup observations. No UIA calls are added by this
// recorder. A checkpoint survives a later assertion/cleanup/driver timeout.
internal static class ReadyDiagnostics
{
    private const int LaunchLimit = 64;
    private static readonly object gate = new();
    private static readonly List<ReadyTrace> launches = [];
    private static string? output;
    private static int omitted;

    internal static void Configure(string directory)
    {
        lock (gate)
        {
            output = Path.Combine(directory, "packaged-ready-diagnostics.json");
            launches.Clear(); omitted = 0;
        }
        Flush();
    }

    internal static ReadyTrace Begin()
    {
        ReadyTrace trace;
        lock (gate)
        {
            trace = new(launches.Count + omitted + 1);
            if (launches.Count < LaunchLimit) launches.Add(trace); else omitted++;
        }
        Flush();
        return trace;
    }

    internal static void Flush()
    {
        lock (gate)
        {
            if (output is null) return;
            try
            {
                var data = new
                {
                    schema_version = 1, protocol_revision = "contain-packaged-startup-diagnostics-1",
                    diagnostic_only = true, launch_limit = LaunchLimit, omitted_launches = omitted,
                    native_observation_interval_ms = 100, periodic_checkpoint_interval_ms = 1000,
                    ui_probe_timeout_ms = 20000, control_discovery_timeout_ms = 40000, control_enabled_timeout_ms = 40000,
                    probe_deadline_basis = "Declared deadline armed immediately before the unchanged WaitAsync; Stopwatch milliseconds. Deadline call and timeout-observed call are distinct.",
                    native_window_definition = "First visible unowned top-level HWND belonging to the retained Viewer process; may be an error dialog. Target control discovery separately proves application readiness.",
                    ui_automation_calls_added = 0, launches = launches.Select(trace => trace.Snapshot()).ToArray()
                };
                var temporary = output + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions
                    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
                File.Move(temporary, output, true);
            }
            catch (IOException) { /* Diagnostics must not replace the original test outcome. */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class ReadyTrace(int launchNumber)
{
    internal sealed record NativeState(bool? ProcessAlive, int? ExitCode, bool? MainWindowPresent,
        bool? MainWindowVisible, bool? MainWindowEnabled, bool? MainWindowHung, bool Available);
    internal sealed record UiCall(int Probe, int Ordinal, string Operation, long StartedMs, long ElapsedMs, bool Completed);
    internal sealed record StageCost(string Operation, int Calls, long TotalMs, long MaximumMs);
    internal sealed record Diagnostic(int LaunchNumber, string Launch, bool? ProcessStarted, int? ProcessId,
        long? ProcessStartReturnedMs, string? ProcessStartFailureType, string Stage, string TimeoutKind,
        int Probe, long? WorkerQueuedMs, long? WorkerStartedMs, long? FirstMainWindowObservedMs,
        long? FirstProcessExitObservedMs, long? FirstControlFoundMs, long? FirstControlEnabledMs,
        int NativeSamples, long? LastNativeSampleMs, long ElapsedMs, bool Finished, NativeState? LastNativeState,
        long? UiProbeDeadlineMs, long? TimeoutObservedMs, UiCall? LatestUiCall, UiCall? DeadlineUiCall,
        UiCall? TimeoutObservedUiCall, IReadOnlyList<StageCost> UiStageCosts);

    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<string, StageCost> costs = [];
    private string launch = "unlabelled", stage = "process_start", timeoutKind = "none";
    private bool? processStarted;
    private int? processId;
    private long? startReturned, firstWindow, firstExit, firstFound, firstEnabled, workerQueued, workerStarted;
    private long? nativeSampleMs, probeDeadlineMs, timeoutObservedMs;
    private string? startFailure;
    private int probe, ordinal, nativeSamples;
    private bool finished;
    private long finishedMs;
    private NativeState? native;
    private UiCall? latestCall, deadlineCall, timeoutObservedCall;

    internal void Started(int id)
    {
        lock (gate) { processStarted = true; processId = id; startReturned = clock.ElapsedMilliseconds; stage = "started"; }
        ReadyDiagnostics.Flush();
    }

    internal void StartFailed(Exception error)
    {
        lock (gate)
        {
            processStarted = false; startFailure = error.GetType().Name; stage = "process_start_failed";
            finished = true; finishedMs = clock.ElapsedMilliseconds;
        }
        ReadyDiagnostics.Flush();
    }

    internal void HandleSetupFailed(Exception error)
    {
        lock (gate)
        {
            startFailure = error.GetType().Name; stage = "process_handle_setup_failed";
            finished = true; finishedMs = clock.ElapsedMilliseconds;
        }
        ReadyDiagnostics.Flush();
    }

    internal void Stage(string value, string label)
    {
        lock (gate) { if (finished) return; stage = value; launch = label; }
        ReadyDiagnostics.Flush();
    }

    internal void Queued(int attempt)
    {
        lock (gate)
        {
            if (finished) return;
            probe = attempt; workerQueued = clock.ElapsedMilliseconds; workerStarted = null; latestCall = null;
            probeDeadlineMs = null; deadlineCall = null;
        }
    }

    internal void ArmProbeDeadline(int milliseconds)
    {
        lock (gate) { if (!finished) probeDeadlineMs = clock.ElapsedMilliseconds + milliseconds; }
    }

    internal void WorkerStarted()
    {
        lock (gate) { if (!finished) workerStarted = clock.ElapsedMilliseconds; }
    }

    internal T Call<T>(string operation, Func<T> action)
    {
        UiCall call;
        lock (gate)
        {
            call = new(probe, ++ordinal, operation, clock.ElapsedMilliseconds, 0, false);
            if (!finished) latestCall = call;
        }
        try { return action(); }
        finally
        {
            lock (gate)
            {
                if (!finished)
                {
                    var elapsed = clock.ElapsedMilliseconds - call.StartedMs;
                    if (probeDeadlineMs is { } deadline && call.StartedMs <= deadline
                        && call.StartedMs + elapsed >= deadline && deadlineCall is null)
                        deadlineCall = call with { ElapsedMs = deadline - call.StartedMs };
                    latestCall = call with { ElapsedMs = elapsed, Completed = true };
                    costs.TryGetValue(operation, out var previous);
                    costs[operation] = new(operation, (previous?.Calls ?? 0) + 1,
                        (previous?.TotalMs ?? 0) + elapsed, Math.Max(previous?.MaximumMs ?? 0, elapsed));
                }
            }
        }
    }

    internal void Found(bool enabled = false)
    {
        lock (gate)
        {
            if (finished) return;
            firstFound ??= clock.ElapsedMilliseconds;
            if (enabled) firstEnabled ??= clock.ElapsedMilliseconds;
        }
    }

    internal void Native(NativeState value)
    {
        lock (gate)
        {
            if (finished) return;
            native = value; nativeSamples++; nativeSampleMs = clock.ElapsedMilliseconds;
            if (value.MainWindowPresent == true) firstWindow ??= clock.ElapsedMilliseconds;
            if (value.ProcessAlive == false) firstExit ??= clock.ElapsedMilliseconds;
        }
    }

    internal void Timeout(string kind)
    {
        lock (gate)
        {
            if (finished) return;
            timeoutKind = kind;
            timeoutObservedMs = clock.ElapsedMilliseconds;
            timeoutObservedCall = CurrentCall();
            if (kind == "uia_probe_20s" && deadlineCall is null && probeDeadlineMs is { } deadline
                && timeoutObservedCall is { Completed: false } call && call.StartedMs <= deadline
                && timeoutObservedMs >= deadline)
                deadlineCall = call with { ElapsedMs = deadline - call.StartedMs };
            latestCall = timeoutObservedCall; finishedMs = timeoutObservedMs.Value; finished = true;
        }
        ReadyDiagnostics.Flush();
    }

    internal void Finish()
    {
        lock (gate)
        {
            if (finished) return;
            latestCall = CurrentCall(); finishedMs = clock.ElapsedMilliseconds; finished = true;
        }
        ReadyDiagnostics.Flush();
    }

    private UiCall? CurrentCall() => latestCall is { Completed: false } call
        ? call with { ElapsedMs = clock.ElapsedMilliseconds - call.StartedMs } : latestCall;

    internal Diagnostic Snapshot()
    {
        lock (gate) return new(launchNumber, launch, processStarted, processId, startReturned, startFailure,
            stage, timeoutKind, probe, workerQueued, workerStarted, firstWindow, firstExit, firstFound, firstEnabled,
            nativeSamples, nativeSampleMs, finished ? finishedMs : clock.ElapsedMilliseconds, finished, native,
            probeDeadlineMs, timeoutObservedMs, finished ? latestCall : CurrentCall(), deadlineCall, timeoutObservedCall,
            costs.Values.OrderBy(value => value.Operation).ToArray());
    }
}
