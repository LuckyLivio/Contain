using System.Text.Json;
using Contain.Desktop.Model;
using Contain.Viewer;

namespace Contain.Desktop.Model.Tests;

internal static class InstallChecks
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();
        await Check("installer selection and cancellation never start a process", async () =>
        {
            var service = new ControlledInstall();
            var model = new InstallViewModel(service);
            model.SelectInstaller(null);
            await model.StartAsync();
            Equal(0, service.Starts, "cancelled initial selection must not start");
            model.SelectInstaller("first-installer.exe");
            Equal("first-installer", model.Name, "suggested name");
            model.SelectInstaller(null);
            Equal("first-installer.exe", model.InstallerPath, "cancel retains prior selection");
            Equal(0, service.Starts, "selecting an EXE does not start");
            model.SelectInstaller("second-installer.exe");
            Equal("second-installer", model.Name, "suggestion follows path until name edited");
            model.Name = "my installation";
            model.SelectInstaller("third-installer.exe");
            Equal("my installation", model.Name, "edited names retained");
        });
        await Check("start is exclusive and binds literal arguments and configuration", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service);
            model.ArgumentsText = "--target=C:\\literal path\\\r\n\r\n--value=%USERPROFILE%";
            model.RegistryKey = "";
            var run = model.StartAsync();
            True(model.Busy && model.IsActive && !model.ConfigurationEnabled, "active configuration locked");
            await model.StartAsync();
            Equal(1, service.Starts, "second start refused");
            var request = service.Request ?? throw new InvalidOperationException("request missing");
            True(request.FollowInstaller && !request.SnapshotOnly, "default follow and ETW preserved");
            Equal(service.PersonalDatabase, request.Database, "default personal library");
            Equal(3, request.Arguments.Count, "one literal argument per line, including empty");
            Equal("", request.Arguments[1], "empty literal preserved");
            Equal("--value=%USERPROFILE%", request.Arguments[2], "no shell expansion");
            model.Name = "late change";
            model.DatabasePath = "late.db";
            model.FollowInstaller = false;
            Equal("fixture", model.Name, "configuration cannot drift during active task");
            True(model.FollowInstaller, "recording mode cannot drift");
            service.Complete(Finished());
            await run;
            True(!model.Busy && !model.CanStart, "consumed task cannot restart implicitly");
        });
        await Check("progress dispatch is bounded and cannot replace a terminal result", async () =>
        {
            var service = new ControlledInstall();
            var callbacks = new Queue<Action>();
            var opened = new List<(string Database, string Session)>();
            var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var openingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var model = Ready(service, callbacks.Enqueue, (db, id) =>
            {
                opened.Add((db, id));
                openingEntered.SetResult();
                return opening.Task;
            });
            var run = model.StartAsync();
            for (var sequence = 2UL; sequence < 1002; sequence++)
                service.Progress!(Status("capturing", sequence));
            Equal(1, callbacks.Count, "one coalescing slot, not one UI post per status");
            service.Complete(Finished());
            await openingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var complete = model.LastStatus;
            callbacks.Dequeue()();
            True(ReferenceEquals(complete, model.LastStatus), "queued status cannot overwrite result while readback opens");
            Equal("finished", model.LastStatus?.Stage, "terminal stage retained");
            Equal(SessionId, opened.Single().Session, "exact backend UUID");
            Equal(service.PersonalDatabase, opened.Single().Database, "exact backend database");
            opening.SetResult();
            await run;
            Equal("记录已保存", model.StatusTitle, "complete title");
        });
        await Check("close is refused while recording without cancelling its process", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service);
            var run = model.StartAsync();
            model.RefuseClose();
            True(model.Busy && !run.IsCompleted, "recording remains active");
            Contains(model.StatusMessage, "不会强制终止安装器", "close explanation");
            model.Reset();
            Equal("fixture.exe", model.InstallerPath, "reset refused during task");
            service.Complete(Finished());
            await run;
        });
        await Check("pre-installer failure permits correction and retains the error", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service);
            var run = model.StartAsync();
            service.Complete(new(Status("failed", 2) with
            {
                InstallerStarted = false, InstallerPid = null, InstallerCreationTime = null,
                InstallerExited = false, InstallerExitCode = null, CaptureFinished = false, DataSaved = false,
                Error = "来源拒绝测试"
            }, 1, null, "raw source failure"));
            await run;
            True(model.CanStart && model.ConfigurationEnabled && model.HasError, "pre-start retry boundary");
            Contains(model.Diagnostics, "来源拒绝测试", "original error retained");
            Contains(model.Diagnostics, "raw source failure", "stderr retained");
            Equal("保存尚未确认", model.SavedState, "failed preparation is not saved");
        });
        await Check("nonzero installer exit and incomplete coverage remain separate from saving", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service);
            var run = model.StartAsync();
            service.Complete(Finished() with { LastStatus = Status("finished", 5) with { InstallerExitCode = 7, QualityLevel = "Incomplete" } });
            await run;
            Contains(model.InstallerOutcome, "退出码 7", "nonzero exit preserved");
            Contains(model.Coverage, "Incomplete", "incomplete preserved");
            Contains(model.SavedState, "记录已保存", "saving remains separate");
            True(!model.StatusMessage.Contains("安装成功", StringComparison.Ordinal), "no installation success claim");
        });
        await Check("unfinished saved evidence opens its exact session only on request", async () =>
        {
            var service = new ControlledInstall();
            var opened = new List<string>();
            var model = Ready(service, open: (_, id) => { opened.Add(id); return Task.CompletedTask; });
            var run = model.StartAsync();
            service.Complete(new(Status("finalizing", 4) with { DataSaved = true }, 2, "unexpected collector exit", "original stderr"));
            await run;
            Equal(0, opened.Count, "unfinished task not auto-promoted to finished result");
            Contains(model.SavedState, "尚未确认完整结束", "partial saving explicit");
            True(model.CanOpenSession, "partial evidence accessible");
            await model.OpenSessionAsync();
            Equal(SessionId, opened.Single(), "explicit access uses exact backend UUID");
        });
        await Check("missing measurements and real zero stay distinct", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service);
            var run = model.StartAsync();
            service.Progress!(Status("capturing", 2) with
            {
                ElapsedMilliseconds = null, StageElapsedMilliseconds = null, StepElapsedMilliseconds = null,
                ProcessedCount = null, ProcessedUnit = null, LastProgressElapsedMilliseconds = null,
                QualityLevel = null
            });
            model.Tick();
            Contains(model.Elapsed, "未提供", "missing time");
            Contains(model.Measurements, "已处理：未提供", "missing count");
            Contains(model.Coverage, "尚未确认", "missing quality");
            service.Progress!(Status("capturing", 3) with { ProcessedCount = 0, ProcessedUnit = "raw_events" });
            Contains(model.Measurements, "已处理：0 条原始事件", "actual zero");
            service.Complete(Finished()); await run;
        });
        await Check("environment probe locks start and close but never starts an installer", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service);
            var probe = model.CheckEnvironmentAsync();
            True(model.Busy && model.IsChecking && !model.IsActive, "probe lifecycle");
            await model.StartAsync();
            model.RefuseClose();
            Equal(0, service.Starts, "start rejected while probe active");
            True(!probe.IsCompleted, "close did not cancel probe");
            service.Doctor.SetResult(JsonDocument.Parse("{\"etw_process\":\"permission_denied\",\"etw_file\":\"permission_denied\",\"etw_registry\":\"not_requested\"}").RootElement.Clone());
            await probe;
            True(model.HasError && model.CanStart && !model.Busy, "probe results do not consume installation");
            Contains(model.StatusMessage, "不会自动提权或切换模式", "actionable permission boundary");
            Equal(0, service.Starts, "doctor alone does not run installer");
        });
        await Check("advanced options are retained in the immutable request", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service);
            model.FollowInstaller = false;
            model.WatchRoot = "C:\\fixture-watch";
            model.CustomDatabase = true; model.DatabasePath = "C:\\fixture-records\\custom.db";
            model.RegistryKey = "Software\\ContainFixture";
            model.SnapshotOnly = true; model.SettleMilliseconds = 1700; model.MaxDrainMilliseconds = 13000;
            var run = model.StartAsync();
            var request = service.Request!;
            True(!request.FollowInstaller && request.SnapshotOnly, "explicit manual snapshot configuration");
            Equal(model.WatchRoot, request.WatchRoot, "watch root");
            Equal(model.DatabasePath, request.Database, "custom save");
            Equal(model.RegistryKey, request.RegistryKey, "registry key");
            Equal(1700UL, request.SettleMilliseconds, "settle"); Equal(13000UL, request.MaxDrainMilliseconds, "drain");
            service.Complete(Finished()); await run;
            model.Reset();
            True(model.FollowInstaller && !model.CustomDatabase && !model.SnapshotOnly, "next explicit task returns to safe defaults");
            Equal(service.PersonalDatabase, model.DatabasePath, "custom database does not become personal default");
        });
        await Check("automatic readback errors do not erase a saved result", async () =>
        {
            var service = new ControlledInstall();
            var model = Ready(service, open: (_, _) => throw new InvalidDataException("exact session temporarily unavailable"));
            var run = model.StartAsync(); service.Complete(Finished()); await run;
            Contains(model.SavedState, "记录已保存", "saved facts retained");
            Contains(model.Error, "自动打开", "readback failure distinguished");
            Contains(model.Diagnostics, "exact session temporarily unavailable", "original error retained");
            True(model.CanOpenSession, "exact readback can be requested again");
        });
        return passed;

        async Task Check(string name, Func<Task> action) { await action(); passed.Add(name); }
    }

    private const string SessionId = "529d532e-653c-4675-8484-46c639c1c7ce";
    private static InstallViewModel Ready(ControlledInstall service, Action<Action>? dispatch = null,
        Func<string, string, Task>? open = null)
    {
        var model = new InstallViewModel(service, dispatch, open);
        model.SelectInstaller("fixture.exe"); return model;
    }
    private static InstallRunResult Finished() => new(Status("finished", 5), 0, null, "") { CollectorPid = 42, LastConfirmedStage = "finished" };
    private static InstallStatus Status(string stage, ulong sequence) => new(SessionId, "C:\\isolated\\contain.db", sequence, stage,
        true, 123, "134384898431494533", stage is "finalizing" or "finished", stage is "finalizing" or "finished" ? 0 : null,
        stage is "finalizing" or "finished", stage == "finished", true, "active", "active", "not_requested", "Degraded", [], null)
    {
        Step = stage == "finished" ? "completed" : "installer_running", ElapsedMilliseconds = 100,
        StageElapsedMilliseconds = 100, StepElapsedMilliseconds = 100, LastProgressElapsedMilliseconds = 100,
        ProcessedCount = 1, ProcessedUnit = "raw_events", InstallerInteraction = stage is "finished" or "finalizing" ? "not_needed" : "may_need_input"
    };
    private sealed class ControlledInstall : IInstallService
    {
        private readonly TaskCompletionSource<InstallRunResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> Doctor { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PersonalDatabase => "C:\\isolated\\contain.db";
        public bool CollectorStarted => Starts > 0;
        public bool DatabaseExists(string database) => true;
        public int Starts { get; private set; }
        public InstallRequest? Request { get; private set; }
        public Action<InstallStatus>? Progress { get; private set; }
        public async Task<InstallRunResult> RunAsync(InstallRequest request, Action<InstallStatus> progress)
        {
            Starts++; Request = request; Progress = progress;
            return await result.Task;
        }
        public Task<JsonElement> CheckEnvironmentAsync(string database) => Doctor.Task;
        public void Complete(InstallRunResult value) => result.SetResult(value);
    }
    private static void True(bool value, string context) { if (!value) throw new InvalidOperationException(context); }
    private static void Equal<T>(T expected, T actual, string context)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{context}: expected {expected}; actual {actual}"); }
    private static void Contains(string actual, string expected, string context)
    { True(actual.Contains(expected, StringComparison.Ordinal), context + ": missing " + expected); }
}
