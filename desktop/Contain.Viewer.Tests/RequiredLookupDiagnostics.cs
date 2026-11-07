using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Contain.Viewer.Tests;

// Diagnosis only: no alternate result can replace the original Required failure.
// Fixed IDs and fixed aggregate fields; no window handles, text or paths escape.
internal static class RequiredLookupDiagnostics
{
    internal const int FailureLimit = 4;
    internal const int WindowMetadataLimit = 16;
    private static readonly object gate = new();
    private static readonly List<RequiredLookupTrace> failures = [];
    private static readonly AsyncLocal<LookupUiInvocation?> current = new();
    private static string? output;
    private static int launches, omitted;

    internal static void Configure(string directory)
    {
        lock (gate)
        {
            output = Path.Combine(directory, "packaged-lookup-diagnostics.json");
            failures.Clear(); launches = omitted = 0;
        }
        Flush();
    }

    internal static int BeginLaunch() => Interlocked.Increment(ref launches);
    internal static bool TrySampleLaunch(ref int sampled) => Interlocked.Exchange(ref sampled, 1) == 0;
    internal static LookupUiInvocation? Current => current.Value;
    internal static bool IsFixedId(string id) => id is "SessionTitle" or "NewInstall";

    internal static RequiredLookupTrace? BeginFailure(int launch, string id, int originalWindows,
        RequiredLookupTrace.NativeState before, string rootOrigin, long? originalMissElapsedMs = null,
        long? originalMissRemainingMs = null)
    {
        if (!IsFixedId(id)) return null;
        RequiredLookupTrace? trace;
        lock (gate)
        {
            if (output is null) return null;
            if (failures.Count >= FailureLimit) { omitted++; trace = null; }
            else
            {
                trace = new(launch, id, originalWindows, before, rootOrigin, originalMissElapsedMs, originalMissRemainingMs);
                failures.Add(trace);
            }
        }
        Flush(); // Original miss is durable BEFORE any additional UIA query.
        return trace;
    }

    internal static async Task<T> RunUiAsync<T>(Func<T> action, TimeSpan timeout)
    {
        var invocation = new LookupUiInvocation(timeout);
        var worker = Task.Run(() =>
        {
            current.Value = invocation;
            try { return action(); }
            finally { current.Value = null; }
        });
        try { return await worker.WaitAsync(timeout); }
        catch (TimeoutException) when (invocation.OriginalFailure is not null)
        {
            // The old lookup had already failed. A diagnostic COM call consuming
            // the remaining original deadline must not change that failure type.
            invocation.Trace?.Finish("existing_ui_deadline", false, null);
            _ = worker.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            ExceptionDispatchInfo.Capture(invocation.OriginalFailure!).Throw();
            throw;
        }
    }

    internal static void Flush()
    {
        lock (gate)
        {
            if (output is null) return;
            try
            {
                var value = new
                {
                    schema_version = 1, protocol_revision = "contain-required-lookup-diagnostics-1",
                    diagnostic_only = true, control_ids = new[] { "SessionTitle", "NewInstall" },
                    failure_limit = FailureLimit, comparisons_per_launch = 1, omitted_failures = omitted,
                    window_metadata_limit = WindowMetadataLimit, added_uia_calls_per_failure_limit = 20,
                    added_uia_cache_calls = 0, ui_deadline_ms = 20000,
                    comparison_timing = "Sequential after original miss; never a simultaneous observation or a fallback result.",
                    cache_origin = "Reuse existing DescriptionAsync native-handle read, otherwise native readiness root candidate; no additional successful-path UIA calls.",
                    failures = failures.Select(item => item.Snapshot()).ToArray()
                };
                var temporary = output + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions
                    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
                File.Move(temporary, output, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class LookupUiInvocation(TimeSpan timeout)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Exception? failure;
    private RequiredLookupTrace? trace;
    internal Exception? OriginalFailure => Volatile.Read(ref failure);
    internal RequiredLookupTrace? Trace => Volatile.Read(ref trace);
    internal long OriginalMissElapsedMs { get; private set; }
    internal long OriginalMissRemainingMs { get; private set; }
    internal long RemainingMs => Math.Max(0, (long)timeout.TotalMilliseconds - clock.ElapsedMilliseconds);
    internal void Preserve(Exception original)
    {
        OriginalMissElapsedMs = clock.ElapsedMilliseconds;
        OriginalMissRemainingMs = RemainingMs;
        Interlocked.CompareExchange(ref failure, original, null);
    }
    internal void Attach(RequiredLookupTrace trace)
    {
        Volatile.Write(ref this.trace, trace);
        if (RemainingMs <= 0) trace.Finish("existing_ui_deadline", false, null);
    }
}

internal sealed class RequiredLookupTrace(int launch, string controlId, int originalWindows,
    RequiredLookupTrace.NativeState before, string rootOrigin, long? originalMissElapsedMs, long? originalMissRemainingMs)
{
    internal sealed record NativeState(bool? ProcessAlive, bool RootCached, bool? RootExists,
        bool? RootOwned, bool? RootVisible, bool? RootEnabled, bool? RootHung,
        bool TargetCached, bool? TargetExists, bool? TargetOwned, bool? TargetVisible,
        bool? TargetHasCachedRoot, bool Available);
    internal sealed record Diagnostic(int LaunchNumber, string ControlId, string OriginalResult,
        string PreservedResult, int OriginalWindowCount, string RootOrigin, NativeState Before,
        NativeState? After, string Stage, string Outcome, bool Finished, int AddedUiaCalls, int MetadataWindowsRead,
        bool? OriginalSetContainsCachedRoot, bool MetadataComplete, long? RemainingOriginalBudgetMs,
        long? OriginalMissElapsedMs, long? OriginalMissRemainingMs, long ElapsedMs, bool? DirectTargetFound, bool? DirectIdMatches, bool? DirectTargetOwned,
        bool? DirectTargetHasCachedRoot, bool? DirectTargetEqualsCachedTarget, bool ComparisonCompleted,
        string? ErrorType);
    private readonly object gate = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private string stage = "original_required_miss";
    private string outcome = "not_completed";
    private bool finished, metadataComplete, comparisonCompleted;
    private int calls, metadataRead;
    private long? remaining;
    private long finishedMs;
    private bool? rootIncluded, found, idMatches, targetOwned, sameRoot, sameTarget;
    private NativeState? after;
    private string? errorType;

    internal void Budget(long milliseconds) { lock (gate) { if (!finished) remaining = milliseconds; } }
    internal T Call<T>(string operation, Func<T> action)
    {
        lock (gate)
        {
            if (finished || RequiredLookupDiagnostics.Current?.RemainingMs is <= 0)
                throw new OperationCanceledException("Diagnostic deadline reached.");
            if (calls >= 20) throw new InvalidOperationException("Diagnostic call bound reached.");
            stage = operation; calls++;
        }
        return action();
    }
    internal void Metadata(int read, bool? included, bool complete)
    {
        lock (gate) { if (!finished) { metadataRead = read; rootIncluded = included; metadataComplete = complete; } }
    }
    internal void Direct(bool didFind, bool? matches, bool? owned, bool? rootMatches, bool? targetMatches)
    {
        lock (gate)
        {
            if (finished) return;
            found = didFind; idMatches = matches; targetOwned = owned; sameRoot = rootMatches; sameTarget = targetMatches;
        }
    }
    internal void After(NativeState value) { lock (gate) { if (!finished) after = value; } }
    internal void Finish(string result, bool completed, Exception? error)
    {
        lock (gate)
        {
            if (finished) return;
            // Keep the active API name if the original deadline fired in it.
            if (result != "existing_ui_deadline") stage = result;
            outcome = result;
            comparisonCompleted = completed; errorType = error?.GetType().Name;
            finishedMs = clock.ElapsedMilliseconds; finished = true;
        }
        RequiredLookupDiagnostics.Flush();
    }
    internal Diagnostic Snapshot()
    {
        lock (gate) return new(launch, controlId, "required_control_missing", "required_control_missing",
            originalWindows, rootOrigin, before, after, stage, outcome, finished, calls, metadataRead, rootIncluded,
            metadataComplete, remaining, originalMissElapsedMs, originalMissRemainingMs, finished ? finishedMs : clock.ElapsedMilliseconds,
            found, idMatches, targetOwned, sameRoot, sameTarget, comparisonCompleted, errorType);
    }
}
