using System.Security.Cryptography;
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

        await Check("executable inspection validates PE, allows read-only ARM64 inspection, and preserves special chars + neutral sibling hint", () =>
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
            True(info.IsRunnableInX64Sandbox, "x64 PE runnable in x64 sandbox");
            Equal("未提供", info.DisplayVersion, "missing version resource reports 未提供");
            True(info.HasSiblingFiles && info.SiblingFileCount == 1, "sibling file noticed without including it");

            // ARM64 PE can still be inspected read-only (name, size, arch, SHA-256), but is marked non-runnable in x64 Sandbox
            var arm64Exe = Path.Combine(dir, "arm64.exe");
            WriteMinimalPe(arm64Exe, 0xAA64);
            var armInfo = WindowsSandboxTrialService.InspectExecutableFile(arm64Exe);
            Equal(TrialExecutableArch.Arm64, armInfo.Architecture, "detected ARM64 PE");
            True(!armInfo.IsRunnableInX64Sandbox, "ARM64 PE blocked from x64 sandbox launch");

            var fakeExe = Path.Combine(dir, "not-a-pe.exe");
            File.WriteAllText(fakeExe, new string('A', 256));
            ThrowsCode(() => WindowsSandboxTrialService.InspectExecutableFile(fakeExe), "invalid_pe");

            ThrowsCode(() => WindowsSandboxTrialService.InspectExecutableFile(sibling), "not_exe");
            ThrowsCode(() => WindowsSandboxTrialService.InspectExecutableFile(Path.Combine(dir, "missing.exe")), "file_missing");
            return Task.CompletedTask;
        });

        await Check("host environment classifier accurately distinguishes Ready, ProNotEnabled, Home, CliUnavailable, VirtDisabled, Policy, RebootPending, and Undetermined", () =>
        {
            // Ready (even when Hyper-V masks PF_VIRT_FIRMWARE_ENABLED to false while HypervisorPresent=true)
            var ready = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional",
                ProductName = "Windows 11 Pro",
                ProductType = 0x30,
                HasWindowsSandboxExe = true,
                WsbExecutablePath = @"C:\wsb.exe",
                WsbHelpSucceeded = true,
                WsbVersion = "0.8.114.0",
                WsbListSucceeded = true,
                HypervisorPresent = true,
                VirtualizationFirmwareEnabled = false
            });
            Equal(SandboxCapabilityStatus.Ready, ready.Status, "ready with active hypervisor");
            Equal(SandboxCapabilityPrimaryAction.None, ready.PrimaryAction, "ready has no blocked action");

            // Supported edition (Pro) without Sandbox binaries -> SupportedFeatureNotEnabled + OpenWindowsFeatures
            var proNotEnabled = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional",
                ProductName = "Windows 11 Pro",
                ProductType = 0x30,
                HypervisorPresent = true,
                VirtualizationFirmwareEnabled = false
            });
            Equal(SandboxCapabilityStatus.SupportedFeatureNotEnabled, proNotEnabled.Status, "Pro missing binaries -> SupportedFeatureNotEnabled");
            Equal(SandboxCapabilityPrimaryAction.OpenWindowsFeatures, proNotEnabled.PrimaryAction, "Pro not enabled offers Open Windows Features");

            // Home edition -> HomeEditionUnsupported + GoToProjectPackaging
            var home = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Core",
                ProductName = "Windows 11 Home",
                ProductType = 0x65
            });
            Equal(SandboxCapabilityStatus.HomeEditionUnsupported, home.Status, "Core -> HomeEditionUnsupported");
            Equal(SandboxCapabilityPrimaryAction.GoToProjectPackaging, home.PrimaryAction, "Home offers GoToProjectPackaging");
            True(home.Detail.Contains("打包项目", StringComparison.Ordinal) && home.Detail.Contains("SHA-256", StringComparison.Ordinal),
                "Home explains available Contain features");

            // Sandbox GUI present without wsb.exe -> CliUnavailable
            var noCli = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional",
                HasWindowsSandboxExe = true,
                WsbExecutablePath = null
            });
            Equal(SandboxCapabilityStatus.CliUnavailable, noCli.Status, "WindowsSandbox.exe without wsb.exe -> CliUnavailable");

            // wsb.exe missing required subcommands -> CliUnavailable
            var partialCli = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional",
                HasWindowsSandboxExe = true,
                WsbExecutablePath = @"C:\wsb.exe",
                WsbHelpSucceeded = true,
                MissingWsbSubcommands = ["connect", "stop"]
            });
            Equal(SandboxCapabilityStatus.CliUnavailable, partialCli.Status, "incomplete wsb subcommands -> CliUnavailable");

            // Direct evidence of hardware virtualization disabled (BOTH HypervisorPresent=false AND VirtFirmware=false)
            var virtOff = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional",
                HypervisorPresent = false,
                VirtualizationFirmwareEnabled = false
            });
            Equal(SandboxCapabilityStatus.VirtualizationDisabled, virtOff.Status, "both hypervisor & firmware virt false -> VirtualizationDisabled");

            // Direct evidence of Group Policy blocking mapped folders -> PolicyBlocked
            var policy = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Enterprise",
                PolicyExplicitlyDisabled = true,
                PolicyEvidence = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Sandbox\AllowMappedFolders = 0"
            });
            Equal(SandboxCapabilityStatus.PolicyBlocked, policy.Status, "explicit policy -> PolicyBlocked");

            // Direct RebootPending when wsb.exe exists but fails list -> RebootPending
            var reboot = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional",
                HasWindowsSandboxExe = true,
                WsbExecutablePath = @"C:\wsb.exe",
                WsbHelpSucceeded = true,
                WsbListSucceeded = false,
                WsbListDiagnostic = "exit=1",
                HypervisorPresent = true,
                RebootPending = true
            });
            Equal(SandboxCapabilityStatus.RebootPending, reboot.Status, "wsb failure + RebootPending -> RebootPending");

            // Missing binaries + edition unreadable (access denied) -> Undetermined with honest PermissionNote (never guesses)
            var undetermined = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = null,
                ProductName = null,
                ProductType = null,
                EditionQueryDenied = true,
                PolicyQueryDenied = true
            });
            Equal(SandboxCapabilityStatus.Undetermined, undetermined.Status, "unreadable edition -> Undetermined");
            True(!string.IsNullOrWhiteSpace(undetermined.PermissionNote) && undetermined.PermissionNote.Contains("普通用户权限", StringComparison.Ordinal),
                "honestly explains query permission limitation");
            return Task.CompletedTask;
        });

        await Check("sandbox-unavailable user can still inspect EXE metadata, compute/copy SHA-256, and stale hash never lands on switched file", async () =>
        {
            using var temp = new TempDir();
            var dir = temp.Sub("inspect");
            var exeA = Path.Combine(dir, "fileA.exe");
            var exeB = Path.Combine(dir, "fileB.exe");
            WriteMinimalPe(exeA, 0x8664, extraByte: 0x11);
            WriteMinimalPe(exeB, 0x8664, extraByte: 0x22);
            var expectedHashB = await WindowsSandboxTrialService.ComputeFileSha256Async(exeB);
            Equal(64, expectedHashB.Length, "real SHA-256 is 64 hex chars");

            var slowHashGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hashCall = 0;
            var service = new ControlledTrialService
            {
                CapabilityReport = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
                {
                    EditionId = "Professional",
                    ProductName = "Windows 11 Pro",
                    ProductType = 0x30,
                    HypervisorPresent = true
                }),
                HashFactory = (path, ct) =>
                {
                    var n = Interlocked.Increment(ref hashCall);
                    if (n == 1) return slowHashGate.Task;
                    return WindowsSandboxTrialService.ComputeFileSha256Async(path, ct);
                }
            };

            using var vm = new IsolatedTrialViewModel(service);
            await vm.RefreshCapabilityAsync();
            True(vm.IsCapabilityBlocked && vm.ShowOpenWindowsFeaturesButton, "Pro not enabled shows Open Windows Features");
            True(vm.CanSelectExecutable, "file inspection remains enabled without Sandbox");

            // Open Windows Features delegates cleanly without launching any EXE
            vm.OpenWindowsFeatures();
            Equal(1, service.OpenWindowsFeaturesCalls, "OpenWindowsFeatures invoked once");

            // Select File A and start slow hash
            True(vm.SelectExecutable(exeA), "selected File A without Sandbox");
            True(!vm.CanStartTrial, "Start stays disabled without Sandbox");
            True(vm.CanComputeSha256, "Compute SHA-256 enabled without Sandbox");
            True(vm.SiblingFilesNoticeText.StartsWith("仅带入所选文件", StringComparison.Ordinal), "neutral sibling hint text");

            var hashTaskA = vm.ComputeSha256Async();
            True(vm.IsComputingSha256, "hash computing state active");
            True(vm.CanSelectExecutable, "UI remains responsive to switch file while hashing");

            // Switch to File B while File A's hash is still in flight
            True(vm.SelectExecutable(exeB), "switched to File B mid-hash");
            True(!vm.IsComputingSha256 && !vm.HasComputedSha256 && vm.ComputedSha256 == "", "old hash state cleared on switch");

            // Resolve File A's late hash -> MUST be discarded and never appear on File B!
            slowHashGate.SetResult("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var lateRes = await hashTaskA;
            True(lateRes is null, "stale hash returned null");
            True(!vm.HasComputedSha256 && vm.ComputedSha256 == "", "stale hash from File A never contaminates File B");

            // Compute real SHA-256 on File B
            var digestB = await vm.ComputeSha256Async();
            Equal(expectedHashB, digestB ?? "", "File B computed exact real SHA-256");
            Equal(expectedHashB, vm.ComputedSha256, "ViewModel exposes File B SHA-256");
            True(vm.CanCopySha256, "Copy SHA-256 enabled");
            Equal(0, service.StartCalls, "no sandbox or host EXE ever started");
        });

        await Check("ARM64 EXE allows file viewing and SHA-256 even when Sandbox is ready, while blocking x64 Sandbox run", async () =>
        {
            using var temp = new TempDir();
            var armExe = Path.Combine(temp.Sub("arm"), "tool-arm64.exe");
            WriteMinimalPe(armExe, 0xAA64);

            var service = new ControlledTrialService();
            using var vm = new IsolatedTrialViewModel(service);
            await vm.RefreshCapabilityAsync();
            True(vm.IsCapabilityReady, "sandbox ready");
            True(vm.SelectExecutable(armExe), "ARM64 file still inspectable");
            True(vm.HasRunCompatibilityWarning, "shows real warning for ARM64 run incompatibility");
            True(!vm.CanStartTrial, "blocks Start for ARM64");
            True(vm.CanComputeSha256, "still allows SHA-256 calculation");
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

        await Check("wsb JSON parsers, single-instance conflict detector, and exact per-instance command-line matcher reject wrong IDs", () =>
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

            True(WindowsSandboxTrialService.IsAnotherSandboxRunningError(unchecked((int)0x800401F6), ""), "detects CO_E_APPSINGLEUSE exit code");
            True(WindowsSandboxTrialService.IsAnotherSandboxRunningError(1, "error 0x800401F6 (CO_E_APPSINGLEUSE)"), "detects CO_E_APPSINGLEUSE stderr");
            True(!WindowsSandboxTrialService.IsAnotherSandboxRunningError(1, "other error"), "ignores unrelated errors");
            return Task.CompletedTask;
        });

        await Check("separates SandboxOpened from ProgramStarted, blocks duplicate rapid Start clicks, and surfaces concise ended result", async () =>
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
            True(!vm.CanStartTrial && !vm.CanSelectExecutable, "inputs locked while starting");

            startGate.SetResult(new SandboxStartResult("sess-1", InstanceA, @"C:\temp\sess-1", @"C:\temp\sess-1\input", 4242, DateTimeOffset.UtcNow));
            await WaitUntilAsync(() => vm.Phase == IsolatedTrialPhase.SandboxOpened);
            Equal("沙箱已打开", vm.SandboxStageBadge, "sandbox opened badge");
            Equal("正在沙箱内启动程序…", vm.ProgramStageBadge, "program not yet marked started while launcher runs");

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
            Equal("试用已结束", vm.SessionHeadline, "concise ended headline");
            Equal("本次临时文件已清理", vm.CleanupStatusText, "clear cleanup confirmation");
            True(!vm.IsCleanupWarning && vm.CanRetryTrial, "cleanup succeeded and Retry enabled");
            True(vm.TechnicalDetailsText.Contains(InstanceA, StringComparison.Ordinal)
                && vm.TechnicalDetailsText.Contains("ExistingLogin", StringComparison.Ordinal),
                "UUID and ExistingLogin live in collapsible Technical Details");
            Equal(1, service.StoppedInstances.Count, "stopped exactly one instance");
            Equal(InstanceA, service.StoppedInstances[0], "stopped strictly the owned instance ID");
        });

        await Check("failed staging cleanup surfaces truthful warning instead of claiming cleaned", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "locked-staging.exe");
            WriteMinimalPe(exePath, 0x8664);

            var service = new ControlledTrialService
            {
                StopFactory = (id, path, _) => Task.FromResult(new SandboxStopResult(
                    InstanceStopped: true,
                    StagingCleaned: false,
                    Summary: "已结束本次沙箱实例，但宿主机暂存目录暂未完全删除。",
                    Diagnostic: "sharing violation"))
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "started");
            True(!await vm.EndTrialAsync(), "EndTrialAsync reflects incomplete cleanup");
            Equal(IsolatedTrialPhase.Ended, vm.Phase, "ended phase");
            True(vm.IsCleanupWarning, "shows truthful cleanup warning");
            True(vm.CleanupStatusText.Contains("尚未完全清理", StringComparison.Ordinal), "explains cleanup incomplete");
            True(!vm.LastStagingCleaned, "LastStagingCleaned is false");
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
                    return Task.FromResult(new SandboxInstancePollResult(true, n == 1));
                }
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromMilliseconds(20));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "started session");

            await WaitUntilAsync(() => vm.Phase == IsolatedTrialPhase.Ended, timeoutMs: 3000);
            Equal(TrialEndReason.SandboxWindowClosed, vm.EndReason, "detected external window close");
            Equal("试用已结束", vm.SessionHeadline, "concise ended headline after external close");
            Equal("本次临时文件已清理", vm.CleanupStatusText, "cleanup confirmed after external close");
            Equal(1, service.StoppedInstances.Count, "stopped owned instance after window close");
            Equal(InstanceA, service.StoppedInstances[0], "stopped exact owned ID");
            True(vm.LastStagingCleaned && vm.CanRetryTrial, "cleaned staging and Retry ready");
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
            True(vm.CanStartTrial && vm.CanRetryTrial, "can immediately retry after failure");

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

    private static HostSandboxEnvironmentSnapshot BaseSnapshot() => new(
        IsWindows: true,
        IsNativeX64: true,
        EditionId: null,
        ProductName: null,
        ProductType: null,
        EditionQueryDenied: false,
        HasWindowsSandboxExe: false,
        WsbExecutablePath: null,
        WsbHelpSucceeded: null,
        WsbHelpOutput: null,
        MissingWsbSubcommands: [],
        WsbVersion: null,
        WsbListSucceeded: null,
        WsbListDiagnostic: null,
        PolicyExplicitlyDisabled: null,
        PolicyEvidence: null,
        PolicyQueryDenied: false,
        HypervisorPresent: null,
        VirtualizationFirmwareEnabled: null,
        VirtualizationQueryDenied: false,
        RebootPending: false,
        RebootPendingQueryDenied: false);

    private static void WriteMinimalPe(string path, ushort machine, byte extraByte = 0)
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
        bytes[0x7F] = extraByte;
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
            SandboxCapabilityPrimaryAction.None,
            "",
            @"C:\synthetic\wsb.exe",
            "0.8.114.0",
            "Windows 11 Pro (Professional)");

        public Func<string, CancellationToken, Task<string>>? HashFactory { get; set; }
        public Func<TrialExecutableInfo, CancellationToken, Task<SandboxStartResult>>? StartFactory { get; set; }
        public Func<SandboxStartResult, TrialExecutableInfo, CancellationToken, Task<GuestProgramLaunchResult>>? LaunchFactory { get; set; }
        public Func<SandboxStartResult, CancellationToken, Task<SandboxInstancePollResult>>? PollFactory { get; set; }
        public Func<string?, string?, CancellationToken, Task<SandboxStopResult>>? StopFactory { get; set; }

        public int StartCalls { get; private set; }
        public int OpenWindowsFeaturesCalls { get; private set; }
        public List<string> StoppedInstances { get; } = [];
        public List<string> CleanedStagingPaths { get; } = [];

        public Task<SandboxCapabilityReport> ProbeCapabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CapabilityReport);

        public TrialExecutableInfo InspectExecutable(string executablePath) =>
            WindowsSandboxTrialService.InspectExecutableFile(executablePath);

        public Task<string> ComputeSha256Async(string executablePath, CancellationToken cancellationToken = default) =>
            HashFactory is not null
                ? HashFactory(executablePath, cancellationToken)
                : WindowsSandboxTrialService.ComputeFileSha256Async(executablePath, cancellationToken);

        public void OpenWindowsFeatures() => OpenWindowsFeaturesCalls++;

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
            if (StopFactory is not null) return StopFactory(instanceId, stagingRootPath, cancellationToken);
            return Task.FromResult(new SandboxStopResult(true, true, "已结束本次沙箱实例并清理暂存目录。"));
        }
    }
}
