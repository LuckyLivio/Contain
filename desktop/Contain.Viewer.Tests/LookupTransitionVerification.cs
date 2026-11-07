using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

// One read-only packaged Viewer, fixed persisted synthetic inputs. This probes
// shared OpenRecord transitions, not InstallForm.Hide or actual installation.
internal sealed class LookupTransitionVerification(string package, string fixtures, string output)
{
    private const string ProductSource = "b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4";
    private static readonly string[] Sessions = ["synthetic-finished", "synthetic-historical", "synthetic-interrupted"];
    private readonly Transition[] cases = Enumerable.Range(0, 100)
        .Select(index => new Transition(index + 1, index % 2 == 0 ? "synthetic-finished" : "synthetic-interrupted")).ToArray();
    private readonly Transition initial = new(0, "synthetic-interrupted");
    private readonly string? harnessSource = Revision(Environment.GetEnvironmentVariable("GITHUB_SHA"));
    private string phase = "not_started";
    private string? errorType, database, databaseHash, sampleHash, manifestHash, viewerHash, launcherHash, coreHash;
    private string? databaseAfterHash, sampleAfterHash;
    private int? processId, exitCode;
    private bool actualExecutable, setupSelection, listBefore, listAfter, databaseUnchanged, sampleUnchanged, normalExit;
    private bool reproduced, completed;
    private long loopElapsed;

    private sealed class Transition(int index, string target)
    {
        public int Index { get; } = index;
        public string Target { get; } = target;
        public string Status { get; set; } = "not_run";
        public bool SelectionRequested { get; set; }
        public int Polls { get; set; }
        public int NonTargetSamples { get; set; }
        public int DisabledTargetSamples { get; set; }
        public long ElapsedMs { get; set; }
        public string? ErrorType { get; set; }
    }

    internal static int RunStandalone(string package, string fixtures, string output)
    {
        var publicOutput = Path.GetFullPath(output);
        var run = new LookupTransitionVerification(Path.GetFullPath(package), Path.GetFullPath(fixtures), publicOutput);
        Directory.CreateDirectory(publicOutput); Directory.CreateDirectory(Path.Combine(publicOutput, "private"));
        ReadyDiagnostics.Configure(publicOutput); RequiredLookupDiagnostics.Configure(publicOutput);
        run.WritePlan(); run.WriteSummary();
        using var owner = new Form { Text = "Contain isolated lookup transition diagnosis", ShowInTaskbar = false };
        owner.Shown += async (_, _) => { try { await run.RunAsync(); } finally { owner.Close(); } };
        Application.Run(owner);
        return run.completed ? 0 : 1;
    }

    private async Task RunAsync()
    {
        PackagedDesktop? desktop = null;
        try
        {
            Phase("validate_frozen_package");
            Require(OperatingSystem.IsWindows() && Directory.Exists(fixtures), "Native frozen inputs unavailable.");
            using var build = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(package, "BUILD.json")));
            Require(build.RootElement.GetProperty("source_sha").GetString() == ProductSource, "Frozen product source differs.");
            manifestHash = Hash(Path.Combine(package, "BUILD.json"));
            foreach (var name in new[] { "Contain.Viewer.dll", "Contain.Viewer.exe", "contain.exe" })
            {
                var expected = build.RootElement.GetProperty("files").EnumerateArray().Single(item => item.GetProperty("path").GetString() == name);
                Require(Hash(Path.Combine(package, name)) == expected.GetProperty("sha256").GetString(), "Frozen product member differs.");
            }
            viewerHash = Hash(Path.Combine(package, "Contain.Viewer.dll"));
            Require(viewerHash == Hash(typeof(ViewerForm).Assembly.Location), "Harness production DLL differs from original package.");
            launcherHash = Hash(Path.Combine(package, "Contain.Viewer.exe")); coreHash = Hash(Path.Combine(package, "contain.exe"));
            Phase("prepare_marked_library");
            var root = TestRecordIsolation.Create("lookup-transition");
            using var isolated = TestRecordIsolation.Use(root);
            database = PersonalRecords.ForCurrentUser().DatabasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            var sample = Path.Combine(package, "samples", "viewer-demo.db");
            sampleHash = Hash(sample); File.Copy(sample, database, false); databaseHash = Hash(database);
            Require(sampleHash == databaseHash, "Copied sample bytes differ.");
            var working = Path.Combine(root, "working"); Directory.CreateDirectory(working);
            Phase("launch_frozen_viewer");
            desktop = new PackagedDesktop(package, working, savedRecordsOnly: true);
            processId = desktop.ProcessId;
            await desktop.ReadyAsync(); actualExecutable = true;
            Phase("verify_fixed_record_list_before");
            var setup = Stopwatch.StartNew();
            while (!await AwaitBounded(() => FixedListVisible(desktop), setup, null))
                await AwaitBounded(() => Task.Delay(50), setup, null);
            listBefore = true;
            Phase("initial_interrupted_selection");
            await SelectAndObserve(desktop, initial, null); setupSelection = true;
            Phase("fixed_transition_loop");
            var loop = Stopwatch.StartNew();
            foreach (var item in cases)
            {
                CheckBudget(null, loop);
                try
                {
                    item.Status = "started"; WriteSummary();
                    await SelectAndObserve(desktop, item, loop);
                    item.Status = "passed";
                }
                catch (Exception error)
                {
                    item.Status = "failed"; item.ErrorType = error.GetType().Name;
                    loopElapsed = loop.ElapsedMilliseconds; WriteSummary(); throw;
                }
                loopElapsed = loop.ElapsedMilliseconds; WriteSummary();
            }
            Phase("verify_fixed_record_list_after");
            var after = Stopwatch.StartNew();
            listAfter = await AwaitBounded(() => FixedListVisible(desktop), after, null);
            Require(listAfter, "Known three record identities changed.");
            Phase("normal_exit");
            await desktop.CloseAsync(); exitCode = desktop.ExitCode; normalExit = desktop.HasExited && exitCode == 0;
            Require(normalExit, "Read-only Viewer did not exit normally.");
            Phase("verify_readonly_bytes");
            databaseAfterHash = Hash(database); sampleAfterHash = Hash(sample);
            databaseUnchanged = databaseAfterHash == databaseHash;
            sampleUnchanged = sampleAfterHash == sampleHash;
            Require(databaseUnchanged && sampleUnchanged, "Read-only source bytes changed.");
            completed = cases.All(item => item.Status == "passed");
            Phase("complete");
        }
        catch (Exception error)
        {
            RequiredLookupDiagnostics.StopAfterExperimentDeadline();
            errorType = error.GetType().Name;
            reproduced = error is PackagedDesktop.RequiredProductControlMissingException || RequiredLookupDiagnostics.HasOriginalMiss;
            WriteSummary();
        }
        finally
        {
            // Existing saved-record-only cleanup owns exactly this synthetic
            // Viewer; no installer, tree kill or additional launch is involved.
            try { desktop?.Dispose(); }
            catch (Exception error) { errorType ??= error.GetType().Name; completed = false; }
            try
            {
                if (database is not null && databaseHash is not null && File.Exists(database))
                { databaseAfterHash ??= Hash(database); databaseUnchanged = databaseAfterHash == databaseHash; }
                var sample = Path.Combine(package, "samples", "viewer-demo.db");
                if (sampleHash is not null && File.Exists(sample))
                { sampleAfterHash ??= Hash(sample); sampleUnchanged = sampleAfterHash == sampleHash; }
            }
            catch (Exception error) { errorType ??= error.GetType().Name; completed = false; }
            WriteSummary();
        }
    }

    private static async Task SelectAndObserve(PackagedDesktop desktop, Transition item, Stopwatch? loop)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await AwaitBounded(() =>
            {
                item.SelectionRequested = true;
                return desktop.SelectIdentityAsync("session:" + item.Target);
            }, clock, loop);
            while (true)
            {
                // Same required lookup used by ObserveRuntime, immediately
                // after selection while the product's queries may be in flight.
                var description = await AwaitBounded(() => desktop.DescriptionAsync("SessionTitle"), clock, loop);
                item.Polls++;
                if (description == "session:" + item.Target)
                {
                    if (await AwaitBounded(() => desktop.EnabledAsync("NewInstall"), clock, loop)) return;
                    item.DisabledTargetSamples++;
                }
                else item.NonTargetSamples++;
                await AwaitBounded(() => Task.Delay(50), clock, loop);
            }
        }
        finally { item.ElapsedMs = clock.ElapsedMilliseconds; }
    }

    private static async Task<bool> FixedListVisible(PackagedDesktop desktop)
    {
        var names = await desktop.NamesAsync("Sessions");
        var known = names.Select(name => name.LastIndexOf("session:", StringComparison.Ordinal) is var marker && marker >= 0
                ? name[(marker + "session:".Length)..] : null).Where(id => id is not null).Distinct().ToArray();
        return known.Length == Sessions.Length && Sessions.All(id => known.Contains(id, StringComparer.Ordinal));
    }

    private static TimeSpan CheckBudget(Stopwatch? transition, Stopwatch? loop)
    {
        var remaining = Math.Min(transition is null ? 30000 : 30000 - transition.ElapsedMilliseconds,
            loop is null ? 180000 : 180000 - loop.ElapsedMilliseconds);
        if (remaining <= 0) throw new TimeoutException("Declared transition deadline reached.");
        return TimeSpan.FromMilliseconds(remaining);
    }
    private static async Task<T> AwaitBounded<T>(Func<Task<T>> action, Stopwatch transition, Stopwatch? loop)
    {
        var timeout = CheckBudget(transition, loop);
        var task = action();
        try
        {
            var result = await task.WaitAsync(timeout);
            CheckBudget(transition, loop);
            return result;
        }
        catch (TimeoutException)
        {
            RequiredLookupDiagnostics.StopAfterExperimentDeadline();
            _ = task.ContinueWith(value => _ = value.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
    private static Task AwaitBounded(Func<Task> action, Stopwatch transition, Stopwatch? loop) =>
        AwaitBounded(async () => { await action(); return true; }, transition, loop);

    private void Phase(string value) { phase = value; WriteSummary(); }
    private void WritePlan() => Write("lookup-transition-plan.json", new
    {
        schema_version = 1, protocol_revision = "contain-lookup-transition-plan-1", product_source_sha = ProductSource,
        harness_source_sha = harnessSource, initial_selection = "synthetic-interrupted", transition_count = 100,
        targets = cases.Select(item => item.Target), maximum_selection_requests = 101, maximum_viewer_launches = 1,
        maximum_native_captures = 0, retries = 0, transition_deadline_ms = 30000, transition_loop_deadline_ms = 180000,
        poll_interval_ms = 50, original_uia_deadline_ms = 20000, fail_first_required_miss = true,
        alternate_lookup_is_fallback = false, accepted = false, release_accepted = false,
        scope = "Frozen packaged EXE and copied synthetic records; shared OpenRecord query transitions only. Not installer completion, InstallForm.Hide, ETW or original startup-cause verification."
    });
    private void WriteSummary() => Write("lookup-transition-summary.json", new
    {
        schema_version = 1, protocol_revision = "contain-lookup-transition-diagnosis-1", source_sha = ProductSource,
        product_source_sha = ProductSource, harness_source_sha = harnessSource, native_windows = OperatingSystem.IsWindows(),
        synthetic = true, actual_packaged_executable = actualExecutable, real_etw_executed = false,
        real_user_records_accessed = false, phase, error_type = errorType, initial_selection_completed = setupSelection,
        initial_selection_requested = initial.SelectionRequested,
        selection_requests = cases.Count(item => item.SelectionRequested) + (initial.SelectionRequested ? 1 : 0),
        attempted = cases.Count(item => item.Status != "not_run"), passed = cases.Count(item => item.Status == "passed"),
        failed = cases.Count(item => item.Status == "failed"), not_run = cases.Count(item => item.Status == "not_run"), cases,
        transitions_with_non_target_samples = cases.Count(item => item.NonTargetSamples > 0), loop_elapsed_ms = loopElapsed,
        reproduced_required_miss = reproduced, original_miss_recorded = RequiredLookupDiagnostics.HasOriginalMiss,
        surfaced_required_miss = errorType == nameof(PackagedDesktop.RequiredProductControlMissingException),
        completed_diagnostic = completed, accepted = false, release_accepted = false,
        all_three_fixed_ids_before = listBefore, all_three_fixed_ids_after = listAfter, database_unchanged = databaseUnchanged,
        package_sample_unchanged = sampleUnchanged, normal_exit = normalExit, process_id = processId, exit_code = exitCode,
        copied_database_before_sha256 = databaseHash, package_sample_before_sha256 = sampleHash,
        copied_database_after_sha256 = databaseAfterHash, package_sample_after_sha256 = sampleAfterHash,
        package_manifest_sha256 = manifestHash, viewer_assembly_sha256 = viewerHash, viewer_launcher_sha256 = launcherHash,
        contain_sha256 = coreHash, maximum_native_captures = 0, maximum_viewer_launches = 1, retries = 0
    });
    private void Write(string name, object value)
    {
        var path = Path.Combine(output, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions
            { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        File.Move(path + ".tmp", path, true);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new TestFailureException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string? Revision(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
}
