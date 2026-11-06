using System.Text.Json;
using Contain.Viewer.Tests;

internal static class ReadyDiagnosticsChecks
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "contain-ready-trace-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var checks = 0;
        void Require(bool value, string reason)
        {
            if (!value) throw new InvalidDataException(reason);
            checks++;
        }
        try
        {
            ReadyDiagnostics.Configure(directory);
            var trace = ReadyDiagnostics.Begin();
            trace.Started(123);
            trace.Stage("discover_control", "default_history");
            trace.Queued(3);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var worker = Task.Run(() =>
            {
                trace.WorkerStarted();
                return trace.Call("find_owned_windows", () =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Synthetic worker was not released.");
                    trace.Found(enabled: true);
                    return true;
                });
            });
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(5)), "synthetic worker entered the real trace wrapper");
                trace.Native(new(true, null, true, true, true, false, true));
                // Exercise the recorder transition directly; this is not a
                // replacement implementation of UIA or its real 20 s deadline.
                trace.ArmProbeDeadline(0);
                trace.Timeout("uia_probe_20s");
                trace.Finish();
                var frozen = trace.Snapshot();
                var frozenJson = JsonSerializer.Serialize(frozen);
                Require(frozen.DeadlineUiCall is { Probe: 3, Ordinal: 1, Operation: "find_owned_windows", Completed: false }
                    && frozen.FirstControlFoundMs is null, "deadline identifies the active call before any target was found");
                using var checkpoint = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "packaged-ready-diagnostics.json")));
                Require(checkpoint.RootElement.GetProperty("launches")[0].GetProperty("finished").GetBoolean(),
                    "finished checkpoint exists before cleanup or late worker completion");
                release.Set();
                Require(worker.Wait(TimeSpan.FromSeconds(5)) && worker.Result, "late worker actually completed");
                Require(JsonSerializer.Serialize(trace.Snapshot()) == frozenJson,
                    "late worker completion cannot overwrite deadline, first-found or stage costs");
            }
            finally { release.Set(); worker.GetAwaiter().GetResult(); }

            var failed = ReadyDiagnostics.Begin();
            failed.StartFailed(new IOException("PRIVATE fixture path must never enter an aggregate"));
            Require(failed.Snapshot() is { ProcessStarted: false, ProcessStartFailureType: "IOException", Finished: true },
                "process launch failure remains separate from readiness");
            Require(!File.ReadAllText(Path.Combine(directory, "packaged-ready-diagnostics.json")).Contains("PRIVATE", StringComparison.Ordinal),
                "checkpoint omits exception messages");
            for (var index = 0; index < 65; index++) ReadyDiagnostics.Begin();
            using var bounded = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "packaged-ready-diagnostics.json")));
            Require(bounded.RootElement.GetProperty("launches").GetArrayLength() == 64
                && bounded.RootElement.GetProperty("omitted_launches").GetInt32() == 3,
                "retained launch summaries remain bounded and omissions are explicit");
            Console.WriteLine($"Startup diagnostic state: {checks} passed, 0 failed (synthetic recorder only; no UIA or Windows startup claim).");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
