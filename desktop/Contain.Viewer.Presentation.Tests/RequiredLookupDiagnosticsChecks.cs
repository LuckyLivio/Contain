using System.Text.Json;
using Contain.Viewer.Tests;

internal static class RequiredLookupDiagnosticsChecks
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "contain-lookup-trace-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks = 0;
        void Require(bool value, string reason)
        { if (!value) throw new InvalidDataException(reason); checks++; }
        RequiredLookupTrace.NativeState Native() => new(true, true, true, true, true, true, false,
            true, true, true, true, true, true);
        try
        {
            RequiredLookupDiagnostics.Configure(directory);
            var sampled = 0;
            var accepted = 0;
            Parallel.For(0, 32, _ => { if (RequiredLookupDiagnostics.TrySampleLaunch(ref sampled)) Interlocked.Increment(ref accepted); });
            Require(accepted == 1, "only one comparison can be claimed by a launch under concurrent callers");
            var original = new InvalidOperationException("PRIVATE original failure payload");
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var workerDone = new ManualResetEventSlim();
            var lateCalls = 0;
            RequiredLookupTrace? trace = null;
            var operation = RequiredLookupDiagnostics.RunUiAsync<bool>(() =>
            {
                try
                {
                    var invocation = RequiredLookupDiagnostics.Current!;
                    invocation.Preserve(original);
                    trace = RequiredLookupDiagnostics.BeginFailure(1, "SessionTitle", 0, Native(), "prior_same_control_native_handle")!;
                    invocation.Attach(trace);
                    trace.Call("from_cached_owned_root", () =>
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic worker not released.");
                        return true;
                    });
                    trace.Call("find_target_from_cached_root", () => Interlocked.Increment(ref lateCalls));
                    throw original;
                }
                finally { workerDone.Set(); }
            }, TimeSpan.FromSeconds(1));
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(5)), "blocked diagnostic entered the actual invocation wrapper");
                using var checkpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "packaged-lookup-diagnostics.json")));
                Require(checkpoint.RootElement.GetProperty("failures")[0].GetProperty("original_result").GetString() == "required_control_missing",
                    "original miss checkpoint is present before the added blocking query");
                try { operation.GetAwaiter().GetResult(); throw new InvalidDataException("Expected original failure."); }
                catch (InvalidOperationException error) { Require(ReferenceEquals(error, original), "diagnostic deadline preserves the exact original failure"); }
                var frozen = JsonSerializer.Serialize(trace!.Snapshot());
                Require(trace.Snapshot() is { Outcome: "existing_ui_deadline", Stage: "from_cached_owned_root", Finished: true },
                    "deadline and active diagnostic API are explicit");
                release.Set();
                Require(workerDone.Wait(TimeSpan.FromSeconds(5)), "late blocked worker finished");
                Require(lateCalls == 0 && JsonSerializer.Serialize(trace.Snapshot()) == frozen,
                    "no diagnostic query or state mutation can follow the deadline");
            }
            finally { release.Set(); workerDone.Wait(TimeSpan.FromSeconds(5)); }

            var positive = RequiredLookupDiagnostics.BeginFailure(2, "SessionTitle", 0, Native(), "prior_same_control_native_handle")!;
            positive.Direct(true, true, true, true, true);
            positive.Finish("comparison_completed_original_failure_preserved", true, null);
            Require(positive.Snapshot() is { DirectTargetFound: true, OriginalResult: "required_control_missing", PreservedResult: "required_control_missing" },
                "a positive alternate lookup remains a failed original observation");
            Require(RequiredLookupDiagnostics.BeginFailure(3, "PRIVATE control name", 0, Native(), "unused") is null,
                "unreviewed control IDs cannot enter the public record");
            var bounded = RequiredLookupDiagnostics.BeginFailure(4, "NewInstall", 0, Native(), "native_readiness_root_candidate")!;
            for (var index = 0; index < 20; index++) bounded.Call("synthetic_counted_call", () => true);
            try { bounded.Call<bool>("synthetic_extra_call", () => throw new Exception("must not execute")); throw new InvalidDataException("Expected call bound."); }
            catch (InvalidOperationException) { Require(bounded.Snapshot().AddedUiaCalls == 20, "additional diagnostic calls have a hard bound"); }
            bounded.Finish("comparison_failed_original_failure_preserved", false, new IOException("PRIVATE diagnostic path"));
            for (var index = 0; index < 5; index++) RequiredLookupDiagnostics.BeginFailure(10 + index, "NewInstall", 0, Native(), "native_readiness_root_candidate");
            var text = File.ReadAllText(Path.Combine(directory, "packaged-lookup-diagnostics.json"));
            using var final = JsonDocument.Parse(text);
            Require(final.RootElement.GetProperty("failures").GetArrayLength() == 4
                && final.RootElement.GetProperty("omitted_failures").GetInt32() == 4,
                "global retained diagnostics have a hard limit and explicit omissions");
            Require(!text.Contains("PRIVATE", StringComparison.Ordinal), "exception messages and unreviewed IDs remain private");
            Console.WriteLine($"Required lookup diagnostic state: {checks} passed, 0 failed (synthetic recorder only; no native UIA claim).");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
