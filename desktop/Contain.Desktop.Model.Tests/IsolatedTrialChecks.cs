using System.Xml.Linq;
using Contain.Desktop.Model;

namespace Contain.Desktop.Model.Tests;

internal static class IsolatedTrialChecks
{
    private const string InstanceA = "11111111-2222-3333-4444-555555555555";
    private const string InstanceB = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();

        await Check("executable inspection validates x64 PE, rejects non-PE/ARM64/non-exe, preserves special chars and sibling hint", () =>
        {
            using var temp = new TempDir();
            var dir = temp.Sub("试用 样例 & 目录");
            var x64Exe = Path.Combine(dir, "隔离 样例 & 测试.exe");
            WriteMinimalPe(x64Exe, 0x8664);
            var sibling = Path.Combine(dir, "同目录未选文件.txt");
            File.WriteAllText(sibling, "sibling-only");

            var info = WindowsSandboxTrialService.InspectExecutableFile(x64Exe);
            Equal("隔离 样例 & 测试.exe", info.FileName, "exact file name with Chinese, spaces and ampersand");
            Equal(TrialExecutableArch.X64, info.Architecture, "detected x64 PE");
            True(info.HasSiblingFiles && info.SiblingFileCount == 1, "sibling file noticed without including it");

            var arm64Exe = Path.Combine(dir, "arm64.exe");
            WriteMinimalPe(arm64Exe, 0xAA64);
            ThrowsCode(() => WindowsSandboxTrialService.InspectExecutableFile(arm64Exe), "unsupported_arch");

            var fakeExe = Path.Combine(dir, "not-a-pe.exe");
            File.WriteAllText(fakeExe, new string('A', 256));
            ThrowsCode(() => WindowsSandboxTrialService.InspectExecutableFile(fakeExe), "invalid_pe");

            ThrowsCode(() => WindowsSandboxTrialService.InspectExecutableFile(sibling), "not_exe");
            ThrowsCode(() => WindowsSandboxTrialService.InspectExecutableFile(Path.Combine(dir, "missing.exe")), "file_missing");
            return Task.CompletedTask;
        });

        await Check("sandbox XML config enforces offline, no clipboard, read-only mounts and escapes special characters", () =>
        {
            using var temp = new TempDir();
            var inputDir = temp.Sub("输入 目录 & 1");
            var bootDir = temp.Sub("启动 目录 & 2");
            var xml = WindowsSandboxTrialService.BuildSandboxConfigXml(inputDir, bootDir);
            var doc = XDocument.Parse(xml);
            var root = doc.Root ?? throw new InvalidOperationException("Missing root Configuration");
            Equal("Disable", root.Element("Networking")?.Value ?? "", "networking disabled");
            Equal("Disable", root.Element("ClipboardRedirection")?.Value ?? "", "clipboard disabled");
            Equal("Disable", root.Element("VGpu")?.Value ?? "", "vgpu disabled");

            var mounts = root.Element("MappedFolders")?.Elements("MappedFolder").ToList() ?? [];
            Equal(2, mounts.Count, "exactly two mapped folders (input + bootstrap)");
            True(mounts.All(m => string.Equals(m.Element("ReadOnly")?.Value, "true", StringComparison.Ordinal)),
                "every mapped folder is strictly ReadOnly=true");
            Equal(Path.GetFullPath(inputDir), mounts[0].Element("HostFolder")?.Value ?? "", "escaped input path round-trips");
            Equal(WindowsSandboxTrialService.GuestInputFolder, mounts[0].Element("SandboxFolder")?.Value ?? "", "guest input path");
            Equal(WindowsSandboxTrialService.GuestBootstrapFolder, mounts[1].Element("SandboxFolder")?.Value ?? "", "guest bootstrap path");
            return Task.CompletedTask;
        });

        await Check("wsb JSON parsers and exact per-instance remote session command-line matcher reject wrong IDs", () =>
        {
            Equal(InstanceA, WindowsSandboxTrialService.ParseStartedInstanceId($$"""{"Id":"{{InstanceA}}"}"""), "parse start ID");
            True(WindowsSandboxTrialService.ParseStartedInstanceId("""{"Id":"not-a-guid"}""") is null, "reject non-guid ID");
            True(WindowsSandboxTrialService.ParseStartedInstanceId("invalid-json") is null, "reject bad JSON");

            var ids = WindowsSandboxTrialService.ParseRunningInstanceIds(
                $$"""{"WindowsSandboxEnvironments":[{"Id":"{{InstanceA}}"},{"Id":"{{InstanceB}}"}]}""");
            Equal(2, ids.Count, "parsed two running IDs");
            Equal(InstanceA, ids[0], "first ID");
            Equal(InstanceB, ids[1], "second ID");
            Equal(0, WindowsSandboxTrialService.ParseRunningInstanceIds("""{"WindowsSandboxEnvironments":[]}""").Count, "empty list");

            Equal(0, WindowsSandboxTrialService.ParseExecExitCode("""{"ExitCode":0}"""), "guest exit 0");
            Equal(20, WindowsSandboxTrialService.ParseExecExitCode("""{"ExitCode":20}"""), "guest exit 20");
            Equal(21, WindowsSandboxTrialService.ParseExecExitCode("""{"ExitCode":21}"""), "guest exit 21");
            True(WindowsSandboxTrialService.ParseExecExitCode("{}") is null, "missing ExitCode returns null");

            True(WindowsSandboxTrialService.CommandLineMatchesInstanceId(
                $"\"WindowsSandboxRemoteSession.exe\" --id {InstanceA}", InstanceA), "matches --id GUID");
            True(WindowsSandboxTrialService.CommandLineMatchesInstanceId(
                $"WindowsSandboxRemoteSession.exe --id={InstanceA.ToUpperInvariant()}", InstanceA), "matches case-insensitive --id=GUID");
            True(WindowsSandboxTrialService.CommandLineMatchesInstanceId(
                $"WindowsSandboxRemoteSession.exe {{{InstanceA}}}", InstanceA), "matches braced GUID");
            True(!WindowsSandboxTrialService.CommandLineMatchesInstanceId(
                $"WindowsSandboxRemoteSession.exe --id {InstanceB}", InstanceA), "never matches another sandbox ID");
            True(!WindowsSandboxTrialService.CommandLineMatchesInstanceId(
                "WindowsSandboxRemoteSession.exe", InstanceA), "never matches generic process without ID");
            return Task.CompletedTask;
        });

        await Check("blocked capability keeps Start disabled and surfaces actionable guidance", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "demo.exe");
            WriteMinimalPe(exePath, 0x8664);

            var service = new ControlledTrialService
            {
                CapabilityReport = new SandboxCapabilityReport(
                    SandboxCapabilityStatus.FeatureNotEnabled,
                    "本机尚未启用 Windows 沙盒（Windows Sandbox）",
                    "未检测到 WindowsSandbox.exe。",
                    "请由你自行在 Windows 功能中启用并在重启后点击“重新检测”。")
            };

            using var vm = new IsolatedTrialViewModel(service);
            await vm.RefreshCapabilityAsync();
            True(vm.IsCapabilityBlocked && !vm.IsCapabilityReady, "capability blocked");
            True(vm.SelectExecutable(exePath), "can still inspect selected exe");
            True(!vm.CanStartTrial, "cannot start when capability blocked");
            True(vm.Capability.NextStep.Contains("重新检测", StringComparison.Ordinal), "shows next step");
        });

        await Check("separates SandboxOpened from ProgramStarted and blocks duplicate rapid Start clicks", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "trial.exe");
            WriteMinimalPe(exePath, 0x8664);

            var startGate = new TaskCompletionSource<SandboxStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launchGate = new TaskCompletionSource<GuestProgramLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ControlledTrialService
            {
                StartFactory = (_, _) => startGate.Task,
                LaunchFactory = (_, _, _) => launchGate.Task
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected valid exe");
            True(vm.CanStartTrial, "start enabled");

            var first = vm.StartTrialAsync();
            var duplicate = vm.StartTrialAsync();
            True(!await duplicate, "rapid second click synchronously rejected");
            Equal(1, service.StartCalls, "only one sandbox start initiated");
            Equal(IsolatedTrialPhase.StartingSandbox, vm.Phase, "starting phase");
            Equal("正在创建并连接沙箱…", vm.SandboxStageBadge, "sandbox starting badge");
            Equal("等待沙箱就绪", vm.ProgramStageBadge, "program waiting badge");
            True(!vm.CanStartTrial && !vm.CanSelectExecutable, "inputs locked while starting");

            startGate.SetResult(new SandboxStartResult("sess-1", InstanceA, @"C:\temp\sess-1", @"C:\temp\sess-1\input", 4242, DateTimeOffset.UtcNow));
            await WaitUntilAsync(() => vm.Phase == IsolatedTrialPhase.SandboxOpened);
            Equal("沙箱已打开", vm.SandboxStageBadge, "sandbox opened badge");
            Equal("正在沙箱内启动程序…", vm.ProgramStageBadge, "program not yet marked started while launcher runs");
            True(vm.ActiveInstanceIdText.Contains(InstanceA, StringComparison.Ordinal), "shows exact instance ID");

            launchGate.SetResult(new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.StartedAndRunning,
                0,
                "已复制并启动 trial.exe（1 秒后仍在运行）。"));
            True(await first, "start trial completes cleanly");
            Equal(IsolatedTrialPhase.ProgramStarted, vm.Phase, "program started phase");
            Equal("沙箱已打开", vm.SandboxStageBadge, "sandbox remains open");
            Equal("程序已启动", vm.ProgramStageBadge, "program now truthfully marked started");
            True(vm.CanEndTrial && vm.IsSessionActive, "can end active session");

            True(await vm.EndTrialAsync(), "end trial from Contain succeeds");
            Equal(IsolatedTrialPhase.Ended, vm.Phase, "ended phase");
            Equal(TrialEndReason.EndedByContain, vm.EndReason, "ended by Contain");
            Equal(1, service.StoppedInstances.Count, "stopped exactly one instance");
            Equal(InstanceA, service.StoppedInstances[0], "stopped strictly the owned instance ID");
            True(vm.LastStagingCleaned && vm.CanStartTrial, "staging cleaned and ready for next trial");
        });

        await Check("launch failure inside open sandbox keeps SandboxOpened distinct from ProgramStarted", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "broken.exe");
            WriteMinimalPe(exePath, 0x8664);

            var service = new ControlledTrialService
            {
                LaunchFactory = (_, _, _) => Task.FromResult(new GuestProgramLaunchResult(
                    GuestProgramLaunchOutcome.FailedToLaunch,
                    14,
                    "沙箱已打开，但未能创建目标进程。",
                    "guest_exit=14"))
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "sandbox opened despite launch failure");
            Equal(IsolatedTrialPhase.SandboxOpened, vm.Phase, "phase stays SandboxOpened");
            Equal("沙箱已打开", vm.SandboxStageBadge, "sandbox is open");
            Equal("程序未成功启动", vm.ProgramStageBadge, "program explicitly NOT reported as started");
            True(vm.CanEndTrial, "user can still end open sandbox");
            await vm.CloseAsync();
        });

        await Check("closing sandbox window externally triggers scoped stop and cleanup for owned ID only", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "window-close.exe");
            WriteMinimalPe(exePath, 0x8664);

            var pollCount = 0;
            var service = new ControlledTrialService
            {
                PollFactory = (_, _) =>
                {
                    var n = Interlocked.Increment(ref pollCount);
                    // Poll 1: owned window observed open; Poll 2: owned window closed while VM lingers
                    return Task.FromResult(new SandboxInstancePollResult(true, n == 1));
                }
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromMilliseconds(20));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "started session");

            await WaitUntilAsync(() => vm.Phase == IsolatedTrialPhase.Ended, timeoutMs: 3000);
            Equal(TrialEndReason.SandboxWindowClosed, vm.EndReason, "detected external window close");
            Equal("会话已结束", vm.SandboxStageBadge, "badge shows ended");
            Equal(1, service.StoppedInstances.Count, "stopped owned instance after window close");
            Equal(InstanceA, service.StoppedInstances[0], "stopped exact owned ID");
            True(vm.LastStagingCleaned && vm.CanStartTrial, "cleaned staging and recovered for Session 2");
        });

        await Check("startup failure surfaces reason and next step, rolls back, and recovers on retry", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "retry.exe");
            WriteMinimalPe(exePath, 0x8664);

            var attempt = 0;
            var service = new ControlledTrialService
            {
                StartFactory = (_, _) =>
                {
                    if (Interlocked.Increment(ref attempt) == 1)
                    {
                        return Task.FromException<SandboxStartResult>(new IsolatedTrialException(
                            "wsb_start_failed",
                            "创建 Windows 沙箱实例失败（退出码 1）。",
                            "请确认虚拟化服务已就绪后重新点击“开始隔离试用”。",
                            "exit=1; stderr=synthetic failure"));
                    }
                    return Task.FromResult(new SandboxStartResult("sess-2", InstanceB, @"C:\temp\sess-2", @"C:\temp\sess-2\input", 5151, DateTimeOffset.UtcNow));
                }
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");

            True(!await vm.StartTrialAsync(), "first start fails cleanly");
            Equal(IsolatedTrialPhase.Failed, vm.Phase, "failed phase");
            True(vm.SessionHeadline.Contains("退出码 1", StringComparison.Ordinal), "shows concrete failure reason");
            True(vm.FailureNextStep.Contains("重新点击", StringComparison.Ordinal), "shows recovery next step");
            True(vm.CanStartTrial, "can immediately retry after failure");

            True(await vm.StartTrialAsync(), "second start succeeds after recovery");
            Equal(IsolatedTrialPhase.ProgramStarted, vm.Phase, "recovered cleanly");
            True(vm.ActiveInstanceIdText.Contains(InstanceB, StringComparison.Ordinal), "tracked new instance ID");

            await vm.CloseAsync();
            Equal(1, service.StoppedInstances.Count, "CloseAsync stops active instance");
            Equal(InstanceB, service.StoppedInstances[0], "CloseAsync stops strictly active ID");
        });

        return passed;

        async Task Check(string name, Func<Task> body)
        {
            await body();
            passed.Add("isolated_trial: " + name);
        }
    }

    private static void WriteMinimalPe(string path, ushort machine)
    {
        var bytes = new byte[0x80];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C, 4), 0x40);
        bytes[0x40] = (byte)'P';
        bytes[0x41] = (byte)'E';
        bytes[0x42] = 0;
        bytes[0x43] = 0;
        BitConverter.TryWriteBytes(bytes.AsSpan(0x44, 2), machine);
        File.WriteAllBytes(path, bytes);
    }

    private static void ThrowsCode(Action action, string expectedCode)
    {
        try
        {
            action();
            throw new InvalidOperationException($"Expected IsolatedTrialException({expectedCode}), but succeeded.");
        }
        catch (IsolatedTrialException ex)
        {
            Equal(expectedCode, ex.Code, $"expected error code {expectedCode}");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for expected state transition.");
            await Task.Delay(15);
        }
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected [{expected}], got [{actual}].");
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "contain-trial-tests-" + Guid.NewGuid().ToString("N")[..8]);
        public TempDir() => Directory.CreateDirectory(Root);
        public string Sub(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { }
        }
    }

    private sealed class ControlledTrialService : IIsolatedTrialService
    {
        public SandboxCapabilityReport CapabilityReport { get; set; } = new(
            SandboxCapabilityStatus.Ready,
            "Windows 沙盒已就绪",
            "synthetic ready",
            "ready",
            @"C:\synthetic\wsb.exe",
            "0.8.114.0");

        public Func<TrialExecutableInfo, CancellationToken, Task<SandboxStartResult>>? StartFactory { get; set; }
        public Func<SandboxStartResult, TrialExecutableInfo, CancellationToken, Task<GuestProgramLaunchResult>>? LaunchFactory { get; set; }
        public Func<SandboxStartResult, CancellationToken, Task<SandboxInstancePollResult>>? PollFactory { get; set; }

        public int StartCalls { get; private set; }
        public List<string> StoppedInstances { get; } = [];
        public List<string> CleanedStagingPaths { get; } = [];

        public Task<SandboxCapabilityReport> ProbeCapabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CapabilityReport);

        public TrialExecutableInfo InspectExecutable(string executablePath) =>
            WindowsSandboxTrialService.InspectExecutableFile(executablePath);

        public Task<SandboxStartResult> StartSandboxAsync(TrialExecutableInfo executable, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            if (StartFactory is not null) return StartFactory(executable, cancellationToken);
            return Task.FromResult(new SandboxStartResult("sess-default", InstanceA, @"C:\temp\sess-default", @"C:\temp\sess-default\input", 1001, DateTimeOffset.UtcNow));
        }

        public Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(SandboxStartResult session, TrialExecutableInfo executable, CancellationToken cancellationToken = default)
        {
            if (LaunchFactory is not null) return LaunchFactory(session, executable, cancellationToken);
            return Task.FromResult(new GuestProgramLaunchResult(GuestProgramLaunchOutcome.StartedAndRunning, 0, $"已启动 {executable.FileName}"));
        }

        public Task<SandboxInstancePollResult> PollSessionAsync(SandboxStartResult session, CancellationToken cancellationToken = default)
        {
            if (PollFactory is not null) return PollFactory(session, cancellationToken);
            return Task.FromResult(new SandboxInstancePollResult(true, true));
        }

        public Task<SandboxStopResult> StopAndCleanupAsync(string? instanceId, string? stagingRootPath, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(instanceId)) StoppedInstances.Add(instanceId);
            if (!string.IsNullOrEmpty(stagingRootPath)) CleanedStagingPaths.Add(stagingRootPath);
            return Task.FromResult(new SandboxStopResult(true, true, "已结束本次沙箱实例并清理暂存目录。"));
        }
    }
}
