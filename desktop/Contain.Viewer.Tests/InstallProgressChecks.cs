using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

/// <summary>Native synthetic checks. These test the bridge/UI, never claim ETW evidence.</summary>
internal static class InstallProgressChecks
{
    private const string ScenarioVariable = "CONTAIN_SYNTHETIC_STATUS_SCENARIO";
    private static readonly string[] Plan = ["additive_measurements_and_missing_values", "repeated_finalizing_and_stale_identity",
        "slow_finalizing_real_pipes_ui_timer_and_close_guard", "structured_failure_details_and_clipboard",
        "missing_terminal_result", "nonzero_collector_exit", "absent_progress_measurement", "disposed_form_callback_guard"];

    internal static int? TryRunSyntheticChild(string[] args)
    {
        var scenario = Environment.GetEnvironmentVariable(ScenarioVariable);
        if (scenario is null) return null;
        if (args.SequenceEqual(new[] { "--version" })) { Console.WriteLine(ProductVersion.CliVersion); return 0; }
        if (!args.Contains("install") || !args.Contains("--status-json")) return null;
        var index = Array.IndexOf(args, "--db");
        if (index < 0 || index + 1 >= args.Length) return 93;
        return WriteScenario(args[index + 1], scenario).GetAwaiter().GetResult();
    }

    private static async Task<int> WriteScenario(string database, string scenario)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var id = Guid.NewGuid().ToString();
        var clock = Stopwatch.StartNew();
        void Emit(int sequence, string stage, string step, ulong? count = null, string? error = null)
        {
            var started = stage != "preparing";
            var exited = stage is "finalizing" or "finished" || stage == "failed";
            var stopped = stage is "finalizing" or "finished" or "failed";
            var raw = Document(database, id, sequence, stage, started, exited, stopped,
                stage != "preparing", step, (ulong)clock.ElapsedMilliseconds, count, error);
            if (scenario == "missing_measurement")
            {
                var node = JsonNode.Parse(raw)!.AsObject();
                foreach (var key in new[] { "step", "elapsed_ms", "step_elapsed_ms", "stage_elapsed_ms", "processed_count", "processed_unit", "last_progress_elapsed_ms", "installer_interaction" })
                    node["data"]!.AsObject().Remove(key);
                raw = node.ToJsonString();
            }
            Console.WriteLine(raw); Console.Out.Flush();
        }
        Emit(1, "preparing", "database_open");
        await Task.Delay(60);
        if (scenario == "prelaunch_missing") return 0;
        if (scenario == "prelaunch_malformed") { Console.WriteLine("SYNTHETIC malformed status"); Console.Out.Flush(); return 2; }
        if (scenario == "prelaunch_trusted_failure")
        {
            Console.WriteLine(Document(database, id, 2, "failed", false, false, false, false, "database_open",
                (ulong)clock.ElapsedMilliseconds, null, "SYNTHETIC trusted prelaunch failure")); Console.Out.Flush(); return 7;
        }
        Emit(2, "capturing", "installer_running");
        await Task.Delay(60);
        // A distinct actual installer-exit checkpoint is retained by the real bridge.
        Console.WriteLine(Document(database, id, 3, "capturing", true, true, false, true,
            "descendant_drain", (ulong)clock.ElapsedMilliseconds)); Console.Out.Flush();
        Emit(4, "finalizing", "evidence_replay", 1);
        // Duplicate and foreign-session rows must neither replace task identity nor refresh progress.
        Console.WriteLine(Document(database, id, 2, "capturing", true, false, false, true, "installer_running", 60));
        Console.WriteLine(Document(database, Guid.NewGuid().ToString(), 90, "capturing", true, false, false, true, "installer_running", 60));
        Console.Out.Flush();
        await Console.Error.WriteAsync("SYNTHETIC diagnostic prefix 中文 " + new string('x', 131072));
        await Console.Error.FlushAsync();
        await Task.Delay(1100);
        if (scenario == "early_exit") return 0;
        if (scenario == "failure")
        {
            Emit(5, "failed", "sqlite_save", null, "SYNTHETIC SQLite 写入失败：" + new string('错', 1400));
            return 7;
        }
        Emit(6, "finalizing", "evidence_replay", 2); // gap is a deliberately coalesced count update
        await Task.Delay(100);
        Emit(7, "finalizing", "sqlite_save");
        Emit(8, "finished", "completed");
        return scenario == "nonzero" ? 9 : 0;
    }

    internal static int RunStandalone(string output)
    {
        var passed = false;
        using var host = new Form { ShowInTaskbar = false, Text = "Contain synthetic progress checks", ClientSize = new Size(420, 240) };
        host.Shown += async (_, _) =>
        {
            try { passed = await RunAsync(output, "working-tree", host); }
            finally { host.Close(); }
        };
        Application.Run(host);
        return passed ? 0 : 1;
    }

    internal static async Task<bool> RunAsync(string output, string source, Form owner, string? package = null, string? privateOutput = null)
    {
        Directory.CreateDirectory(output);
        var checks = new List<CheckResult>();
        var privateErrors = new StringBuilder();
        var assertions = 0;
        void Require(bool condition, string message) { if (!condition) throw new TestFailureException(message); assertions++; }
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new(name, "passed")); }
            catch (Exception error) { checks.Add(new(name, "failed", error.GetType().Name, "native synthetic progress assertion failed")); privateErrors.AppendLine(name).AppendLine(error.ToString()); }
        }
        void Reject(Action action) { var refused = false; try { action(); } catch (InvalidDataException) { refused = true; } Require(refused, "malformed measurement refused"); }
        var db = Path.Combine(Path.GetTempPath(), "contain synthetic progress.db");
        var id = Guid.NewGuid().ToString();
        var initial = Document(db, id, 1, "preparing", false, false, false, false, "database_open", 0);
        await Check(Plan[0], () =>
        {
            var node = JsonNode.Parse(initial)!.AsObject();
            foreach (var field in new[] { "step", "elapsed_ms", "step_elapsed_ms", "stage_elapsed_ms", "processed_count", "processed_unit", "last_progress_elapsed_ms", "installer_interaction" }) node["data"]!.AsObject().Remove(field);
            var missing = InstallStatus.Parse(node.ToJsonString());
            Require(missing.ElapsedMilliseconds is null && missing.ProcessedCount is null && missing.LastProgressElapsedMilliseconds is null, "missing measurements remain absent");
            Require(InstallForm.FormatStatus(missing).Contains("任务耗时：不可用") && InstallForm.FormatStatus(missing).Contains("最近实际进展：不可用"), "missing values never render as zero");
            node = JsonNode.Parse(initial)!.AsObject(); node["data"]!["elapsed_ms"] = 5; node["data"]!["step_elapsed_ms"] = 6;
            Reject(() => InstallStatus.Parse(node.ToJsonString()));
            node["data"]!["step_elapsed_ms"] = 0; node["data"]!["last_progress_elapsed_ms"] = 9;
            Reject(() => InstallStatus.Parse(node.ToJsonString()));
            return Task.CompletedTask;
        });
        await Check(Plan[1], () =>
        {
            var stream = new InstallStatusSequence(db);
            stream.Accept(initial);
            stream.Accept(Document(db, id, 2, "capturing", true, false, false, true, "installer_running", 5));
            stream.Accept(Document(db, id, 3, "capturing", true, true, false, true, "descendant_drain", 6));
            var replay = stream.Accept(Document(db, id, 4, "finalizing", true, true, true, true, "evidence_replay", 7, 1));
            var repeated = stream.Accept(Document(db, id, 9, "finalizing", true, true, true, true, "evidence_replay", 100, 2));
            Require(repeated.ProcessedCount == 2 && repeated.Sequence == 9, "same-stage finalizing and coalesced count gaps accepted");
            Require(!stream.TryAccept(Document(db, id, 4, "finalizing", true, true, true, true, "evidence_replay", 7, 1), out _), "older work ignored");
            Require(!stream.TryAccept(Document(db, Guid.NewGuid().ToString(), 99, "finalizing", true, true, true, true, "evidence_replay", 999, 99), out _), "wrong session ignored");
            Require(!stream.TryAccept(Document(db + "other", id, 99, "finalizing", true, true, true, true, "evidence_replay", 999, 99), out _), "wrong database ignored");
            Require(ReferenceEquals(stream.Last, repeated), "ignored messages preserve exact prior identity and progress");
            Reject(() => stream.Accept(Document(db, id, 10, "finalizing", true, true, true, true, "evidence_replay", 101, 1)));
            var before = InstallForm.FormatStatus(replay, null, 0, 0);
            var later = InstallForm.FormatStatus(replay, null, 1500, 1500);
            Require(replay.LastProgressElapsedMilliseconds == 7 && before != later && later.Contains("00:00:01前"), "local clock ages last real work without modifying backend measurement");
            Require(!new InstallRunResult(repeated, 0, null, "").Succeeded, "saved partial evidence and source stop never complete task");
            var localClock = new InstallProgressClock();
            var delayed = replay with { ElapsedMilliseconds = 10000, StageElapsedMilliseconds = 9000, StepElapsedMilliseconds = 8000, LastProgressElapsedMilliseconds = 7000 };
            var oldDisplay = localClock.Observe(delayed, 10000, 20000, true);
            var laterDisplay = localClock.Observe(delayed with { ElapsedMilliseconds = 12000, StageElapsedMilliseconds = 11000, StepElapsedMilliseconds = 10000 }, 0, 20001, true);
            Require(laterDisplay.Task >= oldDisplay.Task && laterDisplay.Stage >= oldDisplay.Stage && laterDisplay.Step >= oldDisplay.Step && laterDisplay.ProgressAge >= oldDisplay.ProgressAge,
                "late transport cannot move displayed clocks or age of unchanged actual progress backward");
            Require(delayed.LastProgressElapsedMilliseconds == 7000 && delayed.ProcessedCount == 1,
                "monotonic display estimates leave actual work measurements unchanged");
            return Task.CompletedTask;
        });
        await Check(Plan[2], () => RunCase("slow_success", async (form, operation) =>
        {
            var observed = await WaitFor(() => form.LastStatus is { Stage: "finalizing", ProcessedCount: 1 }, operation);
            Require(observed, "slow finalization observed in actual form");
            var first = form.LastStatus!;
            form.Close(); Require(!form.IsDisposed && form.IsActive, "active closing preserves task");
            var button = C<Button>(form, "StartRecording"); button.PerformClick(); Require(ReferenceEquals(form.PendingOperation, operation), "repeated start cannot launch another task");
            var phaseText = C<TextBox>(form, "InstallStatus").Text;
            await Task.Delay(600);
            Require(ReferenceEquals(first, form.LastStatus) && first.ProcessedCount == 1, "UI timer cannot invent work");
            Require(C<TextBox>(form, "InstallStatus").Text != phaseText, "native message loop and display timer remain responsive during child work");
            await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Require(form.Result is { Succeeded: true, ProtocolError: null } && form.LastStatus is { Stage: "finished" }, "same-stage progress and bounded stderr cannot corrupt completion");
            Require(form.Result!.Diagnostics.StartsWith("SYNTHETIC diagnostic prefix 中文") && form.Result.Diagnostics.Length > 65536, "concurrent stderr exceeds pipe capacity and drains independently");
        }));
        await Check(Plan[3], async () =>
        {
            await RunCase("failure", async (form, operation) =>
            {
            await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Require(form.Result is { Succeeded: false, CollectorExitCode: 7 } && form.LastStatus is { Stage: "failed", DataSaved: true }, "structured failed result preserves partial-save meaning");
            Require(C<TextBox>(form, "InstallStatus").Text.Contains("失败") && !C<TextBox>(form, "InstallStatus").Text.Contains(new string('错', 1000)), "concise default failure summary");
            Require(form.TaskDetailsText.Contains(new string('错', 1000)), "full structured error retained in details");
            C<Button>(form, "CopyTaskSummary").PerformClick();
            Require(Clipboard.GetText().Contains(form.LastStatus!.SessionId) && Clipboard.GetText().Contains("sqlite_save"), "task summary carries exact identity and actual failed step");
            C<Button>(form, "TaskDetails").PerformClick();
            var details = form.OwnedForms.Single(window => window.Name == "InstallTaskDetails");
            Require(C<TextBox>(details, "InstallTaskDiagnostics").Text.Contains(new string('错', 1000)), "long error inspectable in native details window");
            details.Close();
            });
            await RunCase("prelaunch_trusted_failure", async (form, operation) =>
            {
                await operation.WaitAsync(TimeSpan.FromSeconds(15));
                Require(form.Result is { CanRetryBeforeInstaller: true, CollectorPid: > 0 }
                    && C<Button>(form, "StartRecording").Enabled && C<Button>(form, "ChangeSaveLocation").Visible,
                    "trusted terminal failure before installer launch enables explicit correction and retry");
            });
        });
        await Check(Plan[4], async () =>
        {
            await RunCase("early_exit", async (form, operation) =>
            {
            await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Require(form.Result is { Succeeded: false, CollectorExitCode: 0, LastStatus.Stage: "finalizing", ProtocolError: not null }, "zero collector exit without terminal result remains unfinished");
            });
            foreach (var scenario in new[] { "prelaunch_missing", "prelaunch_malformed" })
                await RunCase(scenario, async (form, operation) =>
                {
                    await operation.WaitAsync(TimeSpan.FromSeconds(15));
                    Require(form.Result is { CanRetryBeforeInstaller: false, CollectorPid: > 0, ProtocolError: not null }
                        && !C<Button>(form, "StartRecording").Enabled && !C<Button>(form, "ChangeSaveLocation").Visible,
                        "spawned collector with missing/malformed protocol never silently re-enables start");
                });
        });
        await Check(Plan[5], () => RunCase("nonzero", async (form, operation) =>
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Require(form.Result is { Succeeded: false, CollectorExitCode: 9, LastStatus.Stage: "finished" }, "nonzero child overrides otherwise finished message");
        }));
        await Check(Plan[6], () => RunCase("missing_measurement", async (form, operation) =>
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Require(form.Result is { Succeeded: true } && form.LastStatus?.ElapsedMilliseconds is null,
                "legacy measurement-free stream remains supported");
            Require(C<TextBox>(form, "InstallStatus").Text.Contains("任务耗时：不可用"), "missing timing never fabricated");
        }));
        await Check(Plan[7], () => RunCase("slow_success", async (form, operation) =>
        {
            Require(await WaitFor(() => form.LastStatus?.Stage == "finalizing", operation), "active state before forced form disposal");
            form.Dispose();
            await operation.WaitAsync(TimeSpan.FromSeconds(15));
            Require(operation.IsCompletedSuccessfully, "disposed-form callbacks do not throw or kill child");
        }));
        var accepted = checks.Count == Plan.Length && checks.All(check => check.Status == "passed");
        var summaryJson = JsonSerializer.Serialize(new
        {
            schema_version = 1, protocol_revision = "contain-desktop-progress-verification-1", source_sha = source,
            native_windows = OperatingSystem.IsWindows(), synthetic = true, real_etw_executed = false,
            native_device_dpi = owner.DeviceDpi, viewer_assembly_sha256 = Hash(typeof(InstallForm).Assembly.Location),
            package_manifest_sha256 = package is null ? null : Hash(Path.Combine(package, "BUILD.json")),
            contain_sha256 = package is null ? null : Hash(Path.Combine(package, "contain.exe")),
            harness_sha256 = Hash(Environment.ProcessPath!), planned_checks = Plan, checks, assertions,
            passed = checks.Count(check => check.Status == "passed"), failed = checks.Count(check => check.Status == "failed"),
            not_run = Plan.Length - checks.Count, skipped = 0, retries = 0, accepted,
            completed = checks.Count == Plan.Length
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        // Exercise the serialized handoff consumed by the strict package gate.
        using (var handoff = JsonDocument.Parse(summaryJson))
        {
            var serializedChecks = handoff.RootElement.GetProperty("checks").EnumerateArray().ToArray();
            if (!serializedChecks.Select(check => check.GetProperty("name").GetString()).SequenceEqual(Plan)
                || serializedChecks.Any(check => check.GetProperty("status").GetString() is not ("passed" or "failed")))
                throw new InvalidDataException("Progress verification JSON handoff shape is invalid.");
        }
        File.WriteAllText(Path.Combine(output, "progress-summary.json"), summaryJson);
        if (privateErrors.Length > 0) File.WriteAllText(Path.Combine(privateOutput ?? output, "progress-private-errors.log"), privateErrors.ToString());
        return accepted;

        async Task RunCase(string scenario, Func<InstallForm, Task, Task> check)
        {
            var previous = Environment.GetEnvironmentVariable(ScenarioVariable);
            var root = Path.Combine(Path.GetTempPath(), "contain-progress-synthetic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var watch = Path.Combine(root, "watched"); Directory.CreateDirectory(watch);
            Environment.SetEnvironmentVariable(ScenarioVariable, scenario);
            var bridge = new InstallClient(Environment.ProcessPath!);
            using var form = new InstallForm(bridge, (_, _) => Task.CompletedTask);
            try
            {
                form.Show(owner);
                C<TextBox>(form, "InstallerPath").Text = Environment.ProcessPath!;
                C<TextBox>(form, "RecordName").Text = "SYNTHETIC progress protocol fixture";
                C<TextBox>(form, "WatchRoot").Text = watch;
                TestRecordIsolation.SelectCustomDatabase(form, Path.Combine(root, "not-created.db"));
                C<CheckBox>(form, "SnapshotOnly").Checked = true;
                C<Button>(form, "StartRecording").PerformClick();
                var operation = form.PendingOperation;
                try { await check(form, operation); }
                finally { await operation.WaitAsync(TimeSpan.FromSeconds(15)); }
                Require(bridge.CollectorStarted, "actual synthetic collector acquisition is retained after its exit");
            }
            finally { Environment.SetEnvironmentVariable(ScenarioVariable, previous); }
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, Task operation)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && !operation.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        return condition();
    }
    private static T C<T>(Control owner, string name) where T : Control => owner.Controls.Find(name, true).Single() as T ?? throw new InvalidOperationException(name);
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static string Document(string db, string id, int sequence, string stage, bool started, bool exited, bool stopped,
        bool saved, string step, ulong elapsed, ulong? count = null, string? error = null) => JsonSerializer.Serialize(new
        {
            schema_version = 3, kind = "install_status", data = new
            {
                session_id = id, database = db, sequence, stage,
                installer_started = started, installer_pid = started ? (uint?)1234 : null,
                installer_creation_time = started ? "134350000000000001" : null,
                installer_exited = exited, installer_exit_code = exited ? (int?)0 : null,
                capture_finished = stopped, data_saved = saved, etw_requested = false,
                etw_process = started ? "disabled" : null, etw_file = started ? "disabled" : null, etw_registry = started ? "not_requested" : null,
                quality = stage == "finished" ? new { level = "Degraded", reasons = new[] { "SYNTHETIC test only; no collection occurred" } } : null,
                error, step, elapsed_ms = elapsed, step_elapsed_ms = 0UL, stage_elapsed_ms = 0UL, processed_count = count,
                processed_unit = count is null ? null : "raw_events", last_progress_elapsed_ms = elapsed,
                installer_interaction = !started ? "not_started" : exited ? "not_needed" : "may_need_input"
            }
        });
}
