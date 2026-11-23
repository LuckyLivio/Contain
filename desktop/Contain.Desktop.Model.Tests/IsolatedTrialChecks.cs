using System.Diagnostics;
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
            var ready = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional", ProductName = "Windows 11 Pro", ProductType = 0x30,
                HasWindowsSandboxExe = true, WsbExecutablePath = @"C:\wsb.exe",
                WsbHelpSucceeded = true, WsbVersion = "0.8.114.0", WsbListSucceeded = true,
                HypervisorPresent = true, VirtualizationFirmwareEnabled = false
            });
            Equal(SandboxCapabilityStatus.Ready, ready.Status, "ready with active hypervisor");
            Equal(SandboxCapabilityPrimaryAction.None, ready.PrimaryAction, "ready has no blocked action");

            var proNotEnabled = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional", ProductName = "Windows 11 Pro", ProductType = 0x30,
                HypervisorPresent = true, VirtualizationFirmwareEnabled = false
            });
            Equal(SandboxCapabilityStatus.SupportedFeatureNotEnabled, proNotEnabled.Status, "Pro missing binaries -> SupportedFeatureNotEnabled");
            Equal(SandboxCapabilityPrimaryAction.OpenWindowsFeatures, proNotEnabled.PrimaryAction, "Pro not enabled offers Open Windows Features");

            var home = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Core", ProductName = "Windows 11 Home", ProductType = 0x65
            });
            Equal(SandboxCapabilityStatus.HomeEditionUnsupported, home.Status, "Core -> HomeEditionUnsupported");
            Equal(SandboxCapabilityPrimaryAction.GoToProjectPackaging, home.PrimaryAction, "Home offers GoToProjectPackaging");

            var noCli = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional", HasWindowsSandboxExe = true, WsbExecutablePath = null
            });
            Equal(SandboxCapabilityStatus.CliUnavailable, noCli.Status, "WindowsSandbox.exe without wsb.exe -> CliUnavailable");

            var partialCli = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional", HasWindowsSandboxExe = true, WsbExecutablePath = @"C:\wsb.exe",
                WsbHelpSucceeded = true, MissingWsbSubcommands = ["connect", "stop"]
            });
            Equal(SandboxCapabilityStatus.CliUnavailable, partialCli.Status, "incomplete wsb subcommands -> CliUnavailable");

            var virtOff = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional", HypervisorPresent = false, VirtualizationFirmwareEnabled = false
            });
            Equal(SandboxCapabilityStatus.VirtualizationDisabled, virtOff.Status, "both hypervisor & firmware virt false -> VirtualizationDisabled");

            var policy = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Enterprise", PolicyExplicitlyDisabled = true,
                PolicyEvidence = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Sandbox\AllowMappedFolders = 0"
            });
            Equal(SandboxCapabilityStatus.PolicyBlocked, policy.Status, "explicit policy -> PolicyBlocked");

            var reboot = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = "Professional", HasWindowsSandboxExe = true, WsbExecutablePath = @"C:\wsb.exe",
                WsbHelpSucceeded = true, WsbListSucceeded = false, WsbListDiagnostic = "exit=1",
                HypervisorPresent = true, RebootPending = true
            });
            Equal(SandboxCapabilityStatus.RebootPending, reboot.Status, "wsb failure + RebootPending -> RebootPending");

            var undetermined = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
            {
                EditionId = null, ProductName = null, ProductType = null,
                EditionQueryDenied = true, PolicyQueryDenied = true
            });
            Equal(SandboxCapabilityStatus.Undetermined, undetermined.Status, "unreadable edition -> Undetermined");
            True(!string.IsNullOrWhiteSpace(undetermined.PermissionNote), "honestly explains query permission limitation");
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
                    EditionId = "Professional", ProductName = "Windows 11 Pro", ProductType = 0x30, HypervisorPresent = true
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

            vm.OpenWindowsFeatures();
            Equal(1, service.OpenWindowsFeaturesCalls, "OpenWindowsFeatures invoked once");

            True(vm.SelectExecutable(exeA), "selected File A without Sandbox");
            True(!vm.CanStartTrial && vm.CanComputeSha256, "Start disabled, SHA-256 enabled");

            var hashTaskA = vm.ComputeSha256Async();
            True(vm.IsComputingSha256 && vm.CanSelectExecutable, "hashing leaves UI responsive");

            True(vm.SelectExecutable(exeB), "switched to File B mid-hash");
            True(!vm.IsComputingSha256 && !vm.HasComputedSha256 && vm.ComputedSha256 == "", "old hash state cleared");

            slowHashGate.SetResult("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            True(await hashTaskA is null, "stale hash discarded");
            True(!vm.HasComputedSha256 && vm.ComputedSha256 == "", "stale hash never contaminates File B");

            var digestB = await vm.ComputeSha256Async();
            Equal(expectedHashB, digestB ?? "", "File B computed exact real SHA-256");
            True(vm.CanCopySha256, "Copy SHA-256 enabled");
            Equal(0, service.StartCalls, "no sandbox or host EXE started");
        });

        await Check("ARM64 EXE allows file viewing and SHA-256 even when Sandbox is ready, while blocking x64 Sandbox run", async () =>
        {
            using var temp = new TempDir();
            var armExe = Path.Combine(temp.Sub("arm"), "tool-arm64.exe");
            WriteMinimalPe(armExe, 0xAA64);

            var service = new ControlledTrialService();
            using var vm = new IsolatedTrialViewModel(service);
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(armExe), "ARM64 file still inspectable");
            True(vm.HasRunCompatibilityWarning && !vm.CanStartTrial && vm.CanComputeSha256, "ARM64 blocks launch but permits inspection");
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
            Equal(2, mounts.Count, "exactly two mapped folders");
            True(mounts.All(m => string.Equals(m.Element("ReadOnly")?.Value, "true", StringComparison.Ordinal)), "all mounts ReadOnly=true");
            return Task.CompletedTask;
        });

        await Check("strict wsb list parser and EvaluateStopOutcome distinguish reliable empty list from timeout, error, or corrupt output", () =>
        {
            True(WindowsSandboxTrialService.TryParseRunningInstanceIds("""{"WindowsSandboxEnvironments":[]}""", out var emptyIds) && emptyIds.Count == 0,
                "valid empty JSON array parses true");
            True(WindowsSandboxTrialService.TryParseRunningInstanceIds($$"""{"WindowsSandboxEnvironments":[{"Id":"{{InstanceA}}"}]}""", out var oneIds) && oneIds.Count == 1,
                "valid non-empty JSON parses true");
            True(!WindowsSandboxTrialService.TryParseRunningInstanceIds("", out _), "empty string fails strict parse");
            True(!WindowsSandboxTrialService.TryParseRunningInstanceIds("{bad-json", out _), "truncated JSON fails strict parse");
            True(!WindowsSandboxTrialService.TryParseRunningInstanceIds("{}", out _), "missing WindowsSandboxEnvironments fails strict parse");
            True(!WindowsSandboxTrialService.TryParseRunningInstanceIds("""{"WindowsSandboxEnvironments":[{"Id":"not-a-guid"}]}""", out _), "invalid GUID element fails strict parse");

            // Case 1: wsb stop failed, reliable wsb list confirms instance absent -> ConfirmedEnded
            Equal(SandboxInstanceState.ConfirmedEnded,
                WindowsSandboxTrialService.EvaluateStopOutcome(false, 1, false, 0, """{"WindowsSandboxEnvironments":[]}""", InstanceA),
                "stop failed + valid list omits ID -> ConfirmedEnded");

            // Case 2: wsb stop failed, reliable wsb list confirms instance STILL present -> Running
            Equal(SandboxInstanceState.Running,
                WindowsSandboxTrialService.EvaluateStopOutcome(false, 1, false, 0, $$"""{"WindowsSandboxEnvironments":[{"Id":"{{InstanceA}}"}]}""", InstanceA),
                "stop failed + valid list contains ID -> Running");

            // Case 3: wsb stop failed + wsb list timed out / error / unparseable -> Unknown (NEVER ConfirmedEnded!)
            Equal(SandboxInstanceState.Unknown,
                WindowsSandboxTrialService.EvaluateStopOutcome(false, 1, true, -1, "", InstanceA),
                "stop failed + list timed out -> Unknown");
            Equal(SandboxInstanceState.Unknown,
                WindowsSandboxTrialService.EvaluateStopOutcome(false, 1, false, 1, "", InstanceA),
                "stop failed + list exit nonzero -> Unknown");
            Equal(SandboxInstanceState.Unknown,
                WindowsSandboxTrialService.EvaluateStopOutcome(false, 1, false, 0, "corrupt-output", InstanceA),
                "stop failed + list unparseable -> Unknown");
            Equal(SandboxInstanceState.Unknown,
                WindowsSandboxTrialService.EvaluateStopOutcome(true, -1, false, 0, "{}", InstanceA),
                "stop timed out + list schema invalid -> Unknown");

            True(WindowsSandboxTrialService.CommandLineMatchesInstanceId($"\"WindowsSandboxRemoteSession.exe\" --id {InstanceA}", InstanceA), "matches --id GUID");
            True(!WindowsSandboxTrialService.CommandLineMatchesInstanceId($"WindowsSandboxRemoteSession.exe --id {InstanceB}", InstanceA), "rejects other ID");
            True(WindowsSandboxTrialService.IsAnotherSandboxRunningError(unchecked((int)0x800401F6), ""), "detects CO_E_APPSINGLEUSE");
            return Task.CompletedTask;
        });

        await Check("stop fails but reliable query confirms instance vanished -> shows 试用已结束, cleans staging, clears pending record", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "trial.exe");
            WriteMinimalPe(exePath, 0x8664);

            var service = new ControlledTrialService
            {
                StopFactory = (_, _, _) => Task.FromResult(new SandboxStopResult(
                    SandboxInstanceState.ConfirmedEnded,
                    TrialStagingState.Cleaned,
                    "已确认本次沙箱实例结束，并已清理本次宿主暂存目录。",
                    "wsb stop failed: exit=1\nwsb list confirmed target instance is no longer running"))
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "started");

            True(await vm.EndTrialAsync(), "EndTrialAsync succeeds when list confirms absent + staging cleaned");
            Equal(IsolatedTrialPhase.Ended, vm.Phase, "Ended phase");
            Equal(SandboxInstanceState.ConfirmedEnded, vm.InstanceState, "ConfirmedEnded");
            Equal(TrialStagingState.Cleaned, vm.StagingState, "Cleaned");
            Equal("试用已结束", vm.SessionHeadline, "main headline is 试用已结束");
            Equal("已确认结束", vm.SandboxStageBadge, "badge confirms ended");
            Equal("已清理", vm.StagingStageBadge, "staging badge confirms cleaned");
            Equal("本次临时文件已清理", vm.CleanupStatusText, "cleanup status matches");
            True(vm.CanRetryTrial && !vm.CanRecheckPendingSession && !vm.CanRetryStagingCleanup, "only 再次试用 is offered");
            Equal(1, service.ClearPendingCalls, "pending record removed once both confirmed");
        });

        await Check("stop AND query both fail -> never shows 试用已结束, shows 暂时无法确认 with Recheck/Retry and retains pending record", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "trial.exe");
            WriteMinimalPe(exePath, 0x8664);

            var service = new ControlledTrialService
            {
                StopFactory = (_, _, _) => Task.FromResult(new SandboxStopResult(
                    SandboxInstanceState.Unknown,
                    TrialStagingState.Remaining,
                    "暂时无法确认本次沙箱实例是否已结束，未删除关联信息。",
                    "wsb stop failed: exit=1\nwsb list verification inconclusive: timed_out"))
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "started");

            True(!await vm.EndTrialAsync(), "EndTrialAsync returns false when status unconfirmed");
            Equal(IsolatedTrialPhase.Unconfirmed, vm.Phase, "Unconfirmed phase (NOT Ended!)");
            Equal(SandboxInstanceState.Unknown, vm.InstanceState, "InstanceState is Unknown");
            Equal(TrialStagingState.Remaining, vm.StagingState, "StagingState truthfully Remaining");
            True(vm.SessionHeadline != "试用已结束" && vm.SessionHeadline.Contains("暂时无法确认", StringComparison.Ordinal),
                "headline truthfully refuses to say 试用已结束");
            Equal("状态未知", vm.SandboxStageBadge, "badge shows 状态未知");
            Equal("仍有残留", vm.StagingStageBadge, "staging badge shows 仍有残留");
            True(vm.CanRecheckPendingSession && vm.CanRetryEndPendingSession, "offers 重新检查 and 重试结束");
            True(!vm.CanRetryTrial && !vm.CanStartTrial, "blocks starting a second trial until confirmed");
            Equal(0, service.ClearPendingCalls, "pending record RETAINED for recovery");
            True(vm.TechnicalDetailsText.Contains("状态未知", StringComparison.Ordinal) && vm.TechnicalDetailsText.Contains("仍有残留", StringComparison.Ordinal),
                "technical details align with badges and headline");
        });

        await Check("CLI temporarily unavailable during recovery then recovers -> transitions Unconfirmed -> RecoveredRunning -> Ended cleanly", async () =>
        {
            var pending = new PendingTrialSessionRecord(
                1, "abcd1234ef56", InstanceA, @"C:\temp\abcd1234ef56", "sample.exe",
                DateTimeOffset.UtcNow.AddMinutes(-2), 99999, 123456);

            var queryAttempt = 0;
            var service = new ControlledTrialService
            {
                PendingLoadResult = new PendingSessionLoadResult(PendingSessionLoadStatus.Loaded, pending),
                QueryFactory = (_, _, _) =>
                {
                    var n = Interlocked.Increment(ref queryAttempt);
                    if (n == 1)
                        return Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Unknown, null, TrialStagingState.Remaining, "wsb.exe unavailable"));
                    return Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Running, true, TrialStagingState.Remaining));
                }
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.CheckPendingSessionOnStartupAsync();

            // Step 1: CLI unavailable -> Unconfirmed with 重新检查 and pending record retained
            Equal(IsolatedTrialPhase.Unconfirmed, vm.Phase, "startup query failure -> Unconfirmed");
            Equal("暂时无法确认上次试用状态", vm.SessionHeadline, "headline explains cannot confirm previous trial");
            True(vm.CanRecheckPendingSession && vm.HasPendingRecoveryBanner, "offers 重新检查 and non-blocking shell banner");
            Equal(0, service.ClearPendingCalls, "pending record retained while Unknown");

            // Step 2: CLI recovers -> RecheckPendingSessionAsync confirms instance is Running
            True(await vm.RecheckPendingSessionAsync(), "recheck resolves concrete Running state");
            Equal(IsolatedTrialPhase.RecoveredRunning, vm.Phase, "RecoveredRunning phase");
            Equal("上次试用仍在运行", vm.SessionHeadline, "headline shows 上次试用仍在运行");
            Equal("仍在运行", vm.SandboxStageBadge, "sandbox badge shows 仍在运行");
            Equal("使用中", vm.StagingStageBadge, "staging badge shows 使用中 while running");
            Equal("结束这次试用", vm.EndTrialButtonText, "primary action is 结束这次试用");
            True(vm.CanEndTrial && !vm.CanRecheckPendingSession, "can end recovered session");
            // Entering the tab while RecoveredRunning still completes read-only capability detection
            await vm.EnsureCapabilityCheckedAsync();
            True(vm.IsCapabilityReady, "capability check works during RecoveredRunning without getting stuck on 未检测");

            // Step 3: User clicks 结束这次试用 -> confirmed ended & cleaned -> pending record cleared
            True(await vm.EndTrialAsync(), "ending recovered session succeeds");
            Equal(IsolatedTrialPhase.Ended, vm.Phase, "Ended phase");
            Equal("试用已结束", vm.SessionHeadline, "headline now 试用已结束");
            Equal("本次临时文件已清理", vm.CleanupStatusText, "cleanup confirmed");
            Equal(1, service.ClearPendingCalls, "pending record removed only after both ended and cleaned");
            True(!vm.HasPendingRecoveryBanner, "recovery banner dismissed");
        });

        await Check("instance confirmed ended but staging cleanup fails -> keeps record, offers 重试清理, clears record once cleanup succeeds", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "locked-staging.exe");
            WriteMinimalPe(exePath, 0x8664);

            var cleanAttempts = 0;
            var service = new ControlledTrialService
            {
                StopFactory = (_, _, _) => Task.FromResult(new SandboxStopResult(
                    SandboxInstanceState.ConfirmedEnded,
                    TrialStagingState.Remaining,
                    "已确认本次沙箱实例结束，但本次宿主暂存目录仍有残留。",
                    "sharing violation")),
                CleanupOnlyFactory = (_, _) =>
                {
                    var n = Interlocked.Increment(ref cleanAttempts);
                    return Task.FromResult(n == 1
                        ? new SandboxStopResult(SandboxInstanceState.ConfirmedEnded, TrialStagingState.Remaining, "仍有残留", "still locked")
                        : new SandboxStopResult(SandboxInstanceState.ConfirmedEnded, TrialStagingState.Cleaned, "已清理"));
                }
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "started");

            True(!await vm.EndTrialAsync(), "EndTrialAsync returns false when staging remains");
            Equal(IsolatedTrialPhase.Ended, vm.Phase, "Ended phase");
            Equal(SandboxInstanceState.ConfirmedEnded, vm.InstanceState, "instance confirmed ended");
            Equal(TrialStagingState.Remaining, vm.StagingState, "staging still remaining");
            True(vm.SessionHeadline.Contains("临时文件仍有残留", StringComparison.Ordinal), "headline reflects remaining temp files");
            Equal("仍有残留", vm.StagingStageBadge, "staging badge truthfully shows 仍有残留");
            True(vm.IsCleanupWarning && vm.CanRetryStagingCleanup && !vm.CanRetryTrial, "offers 重试清理 and blocks new trial");
            Equal(0, service.ClearPendingCalls, "pending record NOT removed while staging remains");

            // First retry still locked
            True(!await vm.RetryStagingCleanupAsync(), "first retry still remaining");
            Equal(TrialStagingState.Remaining, vm.StagingState, "still remaining");
            Equal(0, service.ClearPendingCalls, "pending record still kept");

            // Second retry succeeds
            True(await vm.RetryStagingCleanupAsync(), "second retry cleans staging");
            Equal(TrialStagingState.Cleaned, vm.StagingState, "staging now Cleaned");
            Equal("试用已结束", vm.SessionHeadline, "headline updates cleanly to 试用已结束");
            Equal("本次临时文件已清理", vm.CleanupStatusText, "cleanup confirmed");
            True(!vm.IsCleanupWarning && !vm.CanRetryStagingCleanup && vm.CanRetryTrial, "ready for 再次试用");
            Equal(1, service.ClearPendingCalls, "pending record removed only after both ended and cleaned");
        });

        await Check("startup failure reads real rollback state instead of hardcoding cleaned", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "rollback.exe");
            WriteMinimalPe(exePath, 0x8664);

            var service = new ControlledTrialService
            {
                StartFactory = (_, _) => Task.FromException<SandboxStartResult>(new IsolatedTrialException(
                    "sandbox_ready_timeout",
                    "等待 Windows 沙箱桌面就绪超时（已等待 60 秒）。",
                    "请核对回滚状态。",
                    "timeout",
                    rollbackResult: new SandboxStopResult(SandboxInstanceState.Unknown, TrialStagingState.Remaining, "回滚停止状态未知", "wsb stop timeout"),
                    createdInstanceId: InstanceB,
                    stagingRootPath: @"C:\temp\sess-rb",
                    sessionId: "sess-rb"))
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");

            True(!await vm.StartTrialAsync(), "start fails");
            Equal(IsolatedTrialPhase.Unconfirmed, vm.Phase, "enters Unconfirmed when rollback stop is Unknown");
            Equal(SandboxInstanceState.Unknown, vm.InstanceState, "reads real rollback InstanceState");
            Equal(TrialStagingState.Remaining, vm.StagingState, "reads real rollback StagingState");
            True(vm.IsCleanupWarning && vm.CanRecheckPendingSession && vm.CanRetryEndPendingSession,
                "does NOT claim cleaned; offers Recheck and Retry End");
        });

        await Check("pending session persistence rejects corrupt JSON, outside staging paths, and another live Contain owner", () =>
        {
            using var temp = new TempDir();
            var stagingBase = temp.Sub("Contain-IsolatedTrial");
            var outsideDir = temp.Sub("OutsideUserFolder");
            var outsideFile = Path.Combine(outsideDir, "keep-me.txt");
            File.WriteAllText(outsideFile, "important");
            var pendingFile = Path.Combine(temp.Sub("AppData"), "pending-session.json");

            var realSvc = new WindowsSandboxTrialService(
                stagingBaseDirectory: stagingBase,
                pendingSessionFilePath: pendingFile);

            // 1. Path containment check
            var validSessionDir = Path.Combine(stagingBase, "a1b2c3d4e5f6");
            Directory.CreateDirectory(validSessionDir);
            True(WindowsSandboxTrialService.IsVerifiedSessionStagingPath(validSessionDir, stagingBase, "a1b2c3d4e5f6"), "accepts verified child dir");
            True(!WindowsSandboxTrialService.IsVerifiedSessionStagingPath(outsideDir, stagingBase), "rejects outside dir");
            True(!WindowsSandboxTrialService.IsVerifiedSessionStagingPath(stagingBase, stagingBase), "rejects staging root itself");
            True(!WindowsSandboxTrialService.IsVerifiedSessionStagingPath(Path.Combine(stagingBase, "..", "OutsideUserFolder"), stagingBase), "rejects traversal");

            // 2. Corrupt JSON fails closed and quarantines file without touching outside dir
            File.WriteAllText(pendingFile, "{not-valid-json");
            var corruptRes = realSvc.LoadPendingSession();
            Equal(PendingSessionLoadStatus.Corrupt, corruptRes.Status, "corrupt JSON rejected");
            True(File.Exists(outsideFile), "outside directory untouched");

            // 3. Tampered staging_root_path pointing outside stagingBase is rejected & never deleted
            File.WriteAllText(pendingFile, $$"""
                {"schema_version":1,"session_id":"a1b2c3d4e5f6","instance_id":"{{InstanceA}}","staging_root_path":{{System.Text.Json.JsonSerializer.Serialize(outsideDir)}},"file_name":"x.exe","created_utc":"2026-11-23T06:00:00Z","owner_pid":0,"owner_start_time_utc_ticks":0}
                """);
            var tamperedRes = realSvc.LoadPendingSession();
            Equal(PendingSessionLoadStatus.Corrupt, tamperedRes.Status, "tampered staging path rejected");
            var refusedClean = realSvc.CleanupStagingOnlyAsync(outsideDir).GetAwaiter().GetResult();
            Equal(TrialStagingState.Unconfirmed, refusedClean.StagingState, "CleanupStagingOnlyAsync refuses outside path");
            True(File.Exists(outsideFile), "outside user file strictly preserved");

            // 4. Valid save + reload (dead owner PID) + conditional ClearPendingSession protection
            var rec = new PendingTrialSessionRecord(1, "a1b2c3d4e5f6", InstanceA, validSessionDir, "demo.exe", DateTimeOffset.UtcNow, 0, 0);
            realSvc.SavePendingSession(rec);
            var loaded = realSvc.LoadPendingSession();
            Equal(PendingSessionLoadStatus.Loaded, loaded.Status, "valid pending record loaded");
            Equal(InstanceA, loaded.Record?.InstanceId ?? "", "instance ID preserved");

            // Mismatched ID must NOT clear the newer record
            realSvc.ClearPendingSession(expectedInstanceId: InstanceB);
            True(File.Exists(pendingFile), "mismatched ClearPendingSession preserves current record");

            // Matching ID clears record cleanly
            realSvc.ClearPendingSession(expectedInstanceId: InstanceA, expectedSessionId: "a1b2c3d4e5f6");
            True(!File.Exists(pendingFile), "matching ClearPendingSession removes record");

            // 5. Another live process owning the record -> OwnedByAnotherLiveProcess (two Contain windows guard)
            var currentPid = Environment.ProcessId;
            var otherLive = Process.GetProcesses()
                .FirstOrDefault(p =>
                {
                    try { return p.Id != currentPid && p.Id > 4 && !p.HasExited && p.StartTime != default; }
                    catch { return false; }
                });
            if (otherLive is not null)
            {
                using (otherLive)
                {
                    var liveOwnerRec = new PendingTrialSessionRecord(
                        1, "a1b2c3d4e5f6", InstanceA, validSessionDir, "demo.exe", DateTimeOffset.UtcNow,
                        otherLive.Id, otherLive.StartTime.ToUniversalTime().Ticks);
                    realSvc.SavePendingSession(liveOwnerRec);
                    var ownedRes = realSvc.LoadPendingSession();
                    Equal(PendingSessionLoadStatus.OwnedByAnotherLiveProcess, ownedRes.Status, "live owner PID prevents second window from interfering");
                }
            }
            return Task.CompletedTask;
        });

        await Check("active monitor ignores transient Unknown poll and ends only on ConfirmedEnded or owned window close", async () =>
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
                    if (n == 1)
                        return Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Unknown, null, TrialStagingState.Active, "transient wsb timeout"));
                    if (n == 2)
                        return Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Running, true, TrialStagingState.Active));
                    return Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Running, false, TrialStagingState.Active));
                }
            };

            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromMilliseconds(20));
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected exe");
            True(await vm.StartTrialAsync(), "started session");

            await WaitUntilAsync(() => vm.Phase == IsolatedTrialPhase.Ended, timeoutMs: 3000);
            True(pollCount >= 3, "transient Unknown poll did NOT falsely terminate session");
            Equal(TrialEndReason.SandboxWindowClosed, vm.EndReason, "detected external window close cleanly");
            Equal("试用已结束", vm.SessionHeadline, "concise ended headline");
            Equal("本次临时文件已清理", vm.CleanupStatusText, "cleanup confirmed");
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
            True(vm.SelectExecutable(exePath), "selected exe");

            var first = vm.StartTrialAsync();
            var duplicate = vm.StartTrialAsync();
            True(!await duplicate, "rapid duplicate click synchronously rejected");
            Equal(1, service.StartCalls, "only one start initiated");

            startGate.SetResult(new SandboxStartResult("a1b2c3d4e5f6", InstanceA, @"C:\temp\a1b2c3d4e5f6", @"C:\temp\a1b2c3d4e5f6\input", 4242, DateTimeOffset.UtcNow, TrialInputKind.SingleFile, "trial.exe", 1));
            await WaitUntilAsync(() => vm.Phase == IsolatedTrialPhase.SandboxOpened);
            Equal("沙箱已打开", vm.SandboxStageBadge, "sandbox opened");
            Equal("正在沙箱内启动入口…", vm.ProgramStageBadge, "program launching");

            launchGate.SetResult(new GuestProgramLaunchResult(GuestProgramLaunchOutcome.StartedAndRunning, 0, "已启动"));
            True(await first, "start trial completes");
            Equal(IsolatedTrialPhase.ProgramStarted, vm.Phase, "ProgramStarted");
            Equal("沙箱运行中", vm.SessionHeadline, "active headline is 沙箱运行中");
            Equal("沙箱运行中", vm.SandboxStageBadge, "sandbox badge is 沙箱运行中");
            Equal("已启动入口：trial.exe", vm.ProgramStageBadge, "action badge records 已启动入口");
            True(vm.SessionSummary.Contains("安装完成并退出后仍可在同一沙箱继续使用", StringComparison.Ordinal), "summary explains installer exit keeps sandbox usable");

            await vm.CloseAsync();
            Equal(1, service.StoppedInstances.Count, "CloseAsync stops owned instance");
            Equal(InstanceA, service.StoppedInstances[0], "CloseAsync stops exact ID");
        });

        await Check("folder mode scans nested Chinese/space/& portable app, auto-selects single entry, stages full tree, and labels entry SHA-256", async () =>
        {
            using var temp = new TempDir();
            var appRoot = temp.Sub("便携 样例 & 软件");
            var binDir = Path.Combine(appRoot, "子目录 & bin");
            var resDir = Path.Combine(appRoot, "assets");
            var emptySub = Path.Combine(appRoot, "empty-cache");
            Directory.CreateDirectory(binDir);
            Directory.CreateDirectory(resDir);
            Directory.CreateDirectory(emptySub);

            var entryExe = Path.Combine(binDir, "便携 入口 & App.exe");
            WriteMinimalPe(entryExe, 0x8664, extraByte: 0x33);
            File.WriteAllText(Path.Combine(binDir, "Helper.dll"), "fake-dll-payload");
            File.WriteAllText(Path.Combine(resDir, "greeting.txt"), "Hello from relative resource!");

            var realSvc = new WindowsSandboxTrialService(stagingBaseDirectory: temp.Sub("staging-base"));
            var scanned = await realSvc.ScanFolderAsync(appRoot);
            Equal("便携 样例 & 软件", scanned.FolderName, "folder name preserved");
            Equal(3, scanned.FileCount, "all 3 files counted");
            Equal(3, scanned.DirectoryCount, "all 3 subdirectories counted");
            Equal(1, scanned.RunnableEntries.Count, "single runnable EXE discovered in nested subdir");
            Equal(@"子目录 & bin\便携 入口 & App.exe", scanned.RunnableEntries[0].RelativePath, "exact nested relative path");

            // Stage real snapshot and verify directory structure & byte counts preserved intact
            var stagedInput = temp.Sub("staged-input");
            var target = TrialLaunchTarget.FromFolder(scanned, scanned.RunnableEntries[0]);
            var stagedSummary = await realSvc.StageInputSnapshotAsync(target, stagedInput);
            Equal(3, stagedSummary.FileCount, "staged all 3 files");
            True(File.Exists(Path.Combine(stagedInput, @"子目录 & bin\便携 入口 & App.exe")), "entry EXE staged at relative path");
            True(File.Exists(Path.Combine(stagedInput, @"子目录 & bin\Helper.dll")), "sibling DLL staged beside entry");
            True(File.Exists(Path.Combine(stagedInput, @"assets\greeting.txt")), "relative resource staged");
            True(Directory.Exists(Path.Combine(stagedInput, "empty-cache")), "empty subdir preserved");

            // ViewModel auto-selects single entry, labels entry SHA-256, and starts unified folder session
            TrialLaunchTarget? capturedTarget = null;
            var ctrl = new ControlledTrialService
            {
                TargetStartFactory = (t, prog, _) =>
                {
                    capturedTarget = t;
                    prog?.Report(new TrialStagingProgress("输入快照准备完成，正在启动 Windows 沙箱…", t.TotalFileCount, t.TotalFileCount, t.TotalBytes, t.TotalBytes, ""));
                    return Task.FromResult(new SandboxStartResult("f1e2d3c4b5a6", InstanceA, @"C:\temp\f1e2d3c4b5a6", @"C:\temp\f1e2d3c4b5a6\input", 2001, DateTimeOffset.UtcNow, t.Kind, t.EntryRelativePath, t.TotalFileCount));
                }
            };
            using var vm = new IsolatedTrialViewModel(ctrl, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(await vm.SelectFolderAsync(appRoot), "folder selected");
            True(vm.IsFolderSelected && vm.SelectedFolderEntry is not null, "auto-selected unique entry");
            Equal(@"子目录 & bin\便携 入口 & App.exe", vm.SelectedFolderEntry!.RelativePath, "selected nested entry");
            True(vm.Sha256SectionTitle.Contains("入口文件", StringComparison.Ordinal), "SHA-256 explicitly labeled as entry file hash");
            Equal("只读快照传入所选目录", vm.IsolationMountBadgeText, "isolation badge shows folder snapshot");
            True(vm.CanStartTrial, "Start enabled once unique entry auto-selected");

            var entryHash = await vm.ComputeSha256Async();
            Equal(await WindowsSandboxTrialService.ComputeFileSha256Async(entryExe), entryHash ?? "", "entry SHA-256 computed accurately");

            True(await vm.StartTrialAsync(), "folder trial started");
            True(capturedTarget?.Kind == TrialInputKind.Folder && capturedTarget.TotalFileCount == 3, "unified StartSandboxAsync received folder target");
            Equal("沙箱运行中", vm.SessionHeadline, "active folder trial headline is 沙箱运行中");
            Equal(@"已启动入口：子目录 & bin\便携 入口 & App.exe", vm.ProgramStageBadge, "action badge shows nested entry relative path");
            await vm.CloseAsync();
        });

        await Check("folder mode disambiguates same-name EXEs in nested folders, clears stale SHA-256 on entry switch, and explains missing runnable entries", async () =>
        {
            using var temp = new TempDir();
            var multiRoot = temp.Sub("MultiEntryApp");
            var dirA = Path.Combine(multiRoot, "app");
            var dirB = Path.Combine(multiRoot, "tools");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);
            WriteMinimalPe(Path.Combine(dirA, "Tool.exe"), 0x8664, extraByte: 0x01);
            WriteMinimalPe(Path.Combine(dirB, "Tool.exe"), 0x8664, extraByte: 0x02);

            var ctrl = new ControlledTrialService();
            using var vm = new IsolatedTrialViewModel(ctrl, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(await vm.SelectFolderAsync(multiRoot), "scanned multi-entry folder");
            Equal(2, vm.FolderEntryCandidates.Count, "both same-name EXEs listed");
            True(vm.SelectedFolderEntry is null && vm.NeedsFolderEntrySelection && !vm.CanStartTrial, "requires explicit entry selection when >1 EXE");

            True(vm.SelectFolderEntryByRelativePath(@"app\Tool.exe"), "selected app\\Tool.exe");
            True(vm.CanStartTrial, "Start enabled after picking entry");
            var hashA = await vm.ComputeSha256Async();
            True(!string.IsNullOrEmpty(hashA), "computed hash A");

            True(vm.SelectFolderEntryByRelativePath(@"tools\Tool.exe"), "switched to tools\\Tool.exe");
            True(!vm.HasComputedSha256 && vm.ComputedSha256 == "", "switching entry clears prior SHA-256");
            var hashB = await vm.ComputeSha256Async();
            True(hashA != hashB, "distinct hash for second same-name EXE");

            // ARM64-only folder explains incompatibility and disables Start
            var armOnly = temp.Sub("ArmOnlyFolder");
            WriteMinimalPe(Path.Combine(armOnly, "only-arm64.exe"), 0xAA64);
            True(await vm.SelectFolderAsync(armOnly), "scanned ARM64-only folder");
            True(vm.HasNoRunnableFolderEntries && !vm.CanStartTrial, "ARM64-only blocks Start");
            True(vm.FolderNoEntryReasonText.Contains("ARM64", StringComparison.Ordinal), "explains incompatible ARM64 EXE");

            // Empty folder explains no EXE found
            var emptyFolder = temp.Sub("EmptyApp");
            True(await vm.SelectFolderAsync(emptyFolder), "scanned empty folder");
            True(vm.HasNoRunnableFolderEntries && !vm.CanStartTrial, "empty folder blocks Start");
            True(vm.FolderNoEntryReasonText.Contains("为空", StringComparison.Ordinal), "explains empty folder");
        });

        await Check("folder mode rejects reparse points/junctions during scan and staging, and rejects entry path traversal", async () =>
        {
            using var temp = new TempDir();
            var outside = temp.Sub("OutsideSecret");
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "do-not-stage");

            var folder = temp.Sub("PortableWithLink");
            WriteMinimalPe(Path.Combine(folder, "Main.exe"), 0x8664);

            // ValidateFolderEntryPath rejects traversal & outside paths
            True(!WindowsSandboxTrialService.ValidateFolderEntryPath(folder, @"..\OutsideSecret\evil.exe", out _, out _), "rejects .. traversal");
            True(!WindowsSandboxTrialService.ValidateFolderEntryPath(folder, @"C:\Windows\notepad.exe", out _, out _), "rejects rooted entry path");
            True(WindowsSandboxTrialService.ValidateFolderEntryPath(folder, "Main.exe", out _, out _), "accepts contained entry");

            if (OperatingSystem.IsWindows())
            {
                var junctionPath = Path.Combine(folder, "linked-dir");
                using (var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junctionPath}\" \"{outside}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                })!)
                {
                    mk.WaitForExit(5000);
                }
                True(Directory.Exists(junctionPath), "junction created for test");

                var realSvc = new WindowsSandboxTrialService(stagingBaseDirectory: temp.Sub("staging-rp"));
                try
                {
                    await realSvc.ScanFolderAsync(folder);
                    throw new InvalidOperationException("ScanFolderAsync should have rejected junction!");
                }
                catch (IsolatedTrialException ex)
                {
                    Equal("reparse_point_detected", ex.Code, "scan rejects directory junction");
                }

                // TOCTOU defense: even if a TrialLaunchTarget was constructed earlier, staging rejects junction & stages nothing outside
                var syntheticFolderInfo = new TrialFolderInfo("PortableWithLink", folder, 1, 0, 128, "128 B",
                    [new TrialFolderEntryCandidate("Main.exe", "Main.exe", Path.Combine(folder, "Main.exe"), 128, "128 B", TrialExecutableArch.X64, "Windows x64 (PE32+)", null, null)],
                    []);
                var target = TrialLaunchTarget.FromFolder(syntheticFolderInfo, syntheticFolderInfo.RunnableEntries[0]);
                var stagedInput = temp.Sub("staged-rp");
                try
                {
                    await realSvc.StageInputSnapshotAsync(target, stagedInput);
                    throw new InvalidOperationException("StageInputSnapshotAsync should have rejected junction!");
                }
                catch (IsolatedTrialException ex)
                {
                    Equal("reparse_point_detected", ex.Code, "staging rejects directory junction");
                    Equal("准备输入快照", ex.FailedStage, "reports exact failing stage");
                }
                True(!File.Exists(Path.Combine(stagedInput, "linked-dir", "secret.txt")), "outside linked file never staged");

                // Clean junction safely before TempDir teardown
                Directory.Delete(junctionPath, recursive: false);
            }
        });

        await Check("cancelling staging preparation cleans partial staging and never starts Sandbox", async () =>
        {
            using var temp = new TempDir();
            var appRoot = temp.Sub("CancelApp");
            WriteMinimalPe(Path.Combine(appRoot, "App.exe"), 0x8664);

            var copyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ctrl = new ControlledTrialService
            {
                TargetStartFactory = async (t, prog, ct) =>
                {
                    prog?.Report(new TrialStagingProgress("正在复制软件文件夹到本次暂存目录…", 0, 10, 0, 10240, "big.bin"));
                    copyStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        throw new InvalidOperationException("Should have been cancelled");
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new IsolatedTrialException(
                            "preparation_cancelled",
                            "已取消准备本次输入快照，未启动沙箱。",
                            rollbackResult: new SandboxStopResult(SandboxInstanceState.NotCreated, TrialStagingState.Cleaned, "已清理本次暂存目录。"),
                            stagingRootPath: @"C:\temp\cancelled-staging",
                            sessionId: "cancelsess",
                            failedStage: "准备输入快照",
                            inner: ex);
                    }
                }
            };

            using var vm = new IsolatedTrialViewModel(ctrl, pollInterval: TimeSpan.FromHours(1));
            await vm.RefreshCapabilityAsync();
            True(await vm.SelectFolderAsync(appRoot), "folder selected");

            var startTask = vm.StartTrialAsync();
            await copyStarted.Task;
            Equal(IsolatedTrialPhase.PreparingInput, vm.Phase, "entered PreparingInput during copy");
            True(vm.CanCancelPreparation, "取消准备 button enabled during copy");

            vm.CancelPreparation();
            True(!await startTask, "StartTrialAsync returns false when cancelled");
            Equal(IsolatedTrialPhase.Ended, vm.Phase, "cancelled cleanly");
            Equal(TrialEndReason.PreparationCancelled, vm.EndReason, "PreparationCancelled end reason");
            Equal("已取消准备", vm.SessionHeadline, "headline shows 已取消准备");
            Equal("本次暂存目录已清理", vm.CleanupStatusText, "staging cleanup confirmed");
            True(!vm.IsCleanupWarning && vm.CanRetryTrial, "allows retrying cleanly");
        });

        await Check("unreadable file and insufficient disk space abort during staging and clean up without starting Sandbox", async () =>
        {
            using var temp = new TempDir();
            var appRoot = temp.Sub("ErrorApp");
            var exePath = Path.Combine(appRoot, "App.exe");
            var lockedPath = Path.Combine(appRoot, "locked.dat");
            WriteMinimalPe(exePath, 0x8664);
            File.WriteAllText(lockedPath, "secret-data");

            var stagingBase = temp.Sub("staging-err");
            var scanned = await WindowsSandboxTrialService.ScanPortableFolderAsync(appRoot);
            var target = TrialLaunchTarget.FromFolder(scanned, scanned.RunnableEntries[0]);

            // 1. Insufficient disk space detected before copying
            var lowSpaceSvc = new WindowsSandboxTrialService(
                stagingBaseDirectory: stagingBase,
                availableFreeSpaceProvider: _ => 1024L);
            try
            {
                await lowSpaceSvc.StageInputSnapshotAsync(target, Path.Combine(stagingBase, "lowspace-input"));
                throw new InvalidOperationException("Expected insufficient_disk_space!");
            }
            catch (IsolatedTrialException ex)
            {
                Equal("insufficient_disk_space", ex.Code, "insufficient disk space rejected");
                Equal("准备输入快照", ex.FailedStage, "reports staging stage");
            }

            // 2. Exclusively locked unreadable file fails cleanly during staging copy
            var normalSvc = new WindowsSandboxTrialService(stagingBaseDirectory: stagingBase);
            var lockedStagingDir = Path.Combine(stagingBase, "locked-input");
            using (var exclusiveLock = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try
                {
                    await normalSvc.StageInputSnapshotAsync(target, lockedStagingDir);
                    throw new InvalidOperationException("Expected staging_copy_read_failed on exclusively locked file!");
                }
                catch (IsolatedTrialException ex)
                {
                    Equal("staging_copy_read_failed", ex.Code, "unreadable file fails with staging_copy_read_failed");
                    True(ex.Message.Contains("locked.dat", StringComparison.OrdinalIgnoreCase), "error names unreadable file");
                    Equal("准备输入快照", ex.FailedStage, "reports staging stage");
                }
            }
        });

        await Check("trial history ignores file browsing/hashing, creates two independent records for two runs of same EXE, and searches newest-first", async () =>
        {
            using var temp = new TempDir();
            var historyDir = Path.Combine(temp.Root, "IsolatedTrial", "History");
            var exePath = Path.Combine(temp.Sub("app"), "RepeatTool.exe");
            WriteMinimalPe(exePath, 0x8664, extraByte: 0x33);
            var expectedHash = await WindowsSandboxTrialService.ComputeFileSha256Async(exePath);

            var store = new JsonTrialHistoryStore(historyDir);
            var historyVm = new TrialHistoryViewModel(store);
            historyVm.Refresh();
            True(!Directory.Exists(historyDir), "empty read-only Refresh never creates History directory");
            Equal(0, historyVm.TotalCount, "empty history has 0 records");

            var now = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(8));
            var service = new ControlledTrialService
            {
                TargetStartFactory = (target, _, _) => Task.FromResult(new SandboxStartResult(
                    target.SessionId ?? "sess00000001", InstanceA, @"C:\temp\" + (target.SessionId ?? "sess00000001"),
                    @"C:\temp\input", 1001, now, target.Kind, target.EntryRelativePath, target.TotalFileCount, expectedHash))
            };

            using var vm = new IsolatedTrialViewModel(
                service,
                clock: () => now,
                pollInterval: TimeSpan.FromHours(1),
                historyStore: store,
                onHistoryUpdated: r => historyVm.UpsertInMemory(r));

            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected EXE");
            var _ = await vm.ComputeSha256Async();
            Equal(0, store.LoadAll().Count, "browsing and computing SHA-256 create 0 trial history records");
            True(!Directory.Exists(historyDir), "still no history directory after browse/hash");

            // First trial
            True(await vm.StartTrialAsync(), "first trial starts");
            var firstId = vm.CurrentHistorySessionId;
            True(vm.HasCurrentHistoryRecord && !string.IsNullOrEmpty(firstId), "current history record available for 查看本次记录");
            True(await vm.EndTrialAsync(), "first trial ends");

            // Second trial of the exact same EXE 5 minutes later
            now = now.AddMinutes(5);
            True(await vm.StartTrialAsync(), "second trial starts");
            var secondId = vm.CurrentHistorySessionId;
            True(!string.Equals(firstId, secondId, StringComparison.Ordinal), "second trial generates distinct session_id");
            True(await vm.EndTrialAsync(), "second trial ends");

            // Simulate app restart via fresh TrialHistoryViewModel
            var reloadedVm = new TrialHistoryViewModel(store);
            reloadedVm.Refresh();
            Equal(2, reloadedVm.TotalCount, "two independent runs persisted across restart");
            Equal(secondId, reloadedVm.FilteredRecords[0].SessionId, "newest run ordered first");
            Equal(firstId, reloadedVm.FilteredRecords[1].SessionId, "older run ordered second");
            Equal(expectedHash, reloadedVm.FilteredRecords[0].Report.EntrySha256 ?? "", "recorded authoritative entry SHA-256");

            // Search by software name or entry
            reloadedVm.SearchQuery = "RepeatTool";
            Equal(2, reloadedVm.FilteredRecords.Count, "search matches software name");
            reloadedVm.SearchQuery = "NonExistentApp";
            Equal(0, reloadedVm.FilteredRecords.Count, "non-matching search returns 0");
            True(reloadedVm.IsSearchEmpty, "flags empty search result");
            reloadedVm.ClearSearch();
            Equal(2, reloadedVm.FilteredRecords.Count, "clearing search restores list");
        });

        await Check("single session keeps exactly 1 history record across unconfirmed stop, recheck, staging remaining, and retry cleanup", async () =>
        {
            using var temp = new TempDir();
            var historyDir = Path.Combine(temp.Root, "HistoryLifecycle");
            var exePath = Path.Combine(temp.Sub("app"), "LifecycleTool.exe");
            WriteMinimalPe(exePath, 0x8664, extraByte: 0x44);

            var store = new JsonTrialHistoryStore(historyDir);
            var now = new DateTimeOffset(2026, 11, 23, 11, 0, 0, TimeSpan.FromHours(8));
            var service = new ControlledTrialService
            {
                // Start without staged hash to verify "未记录" fallback
                TargetStartFactory = (target, _, _) => Task.FromResult(new SandboxStartResult(
                    target.SessionId!, InstanceA, @"C:\temp\" + target.SessionId!, @"C:\temp\input", 2001, now, target.Kind, target.EntryRelativePath, 1, EntrySha256: null)),
                StopFactory = (_, _, _) => Task.FromResult(new SandboxStopResult(
                    SandboxInstanceState.Unknown, TrialStagingState.Remaining,
                    "暂时无法确认本次沙箱实例是否已结束，未删除关联信息。", "wsb list timeout")),
                QueryFactory = (_, _, _) => Task.FromResult(new SandboxInstancePollResult(
                    SandboxInstanceState.ConfirmedEnded, false, TrialStagingState.Remaining)),
                CleanupOnlyFactory = (_, _) => Task.FromResult(new SandboxStopResult(
                    SandboxInstanceState.ConfirmedEnded, TrialStagingState.Remaining, "仍有残留", "locked"))
            };

            using var vm = new IsolatedTrialViewModel(service, clock: () => now, pollInterval: TimeSpan.FromHours(1), historyStore: store);
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected");
            True(await vm.StartTrialAsync(), "started");
            var sid = vm.CurrentHistorySessionId;

            // Stop times out -> Unknown + Remaining, EndedAtUtc must still be null
            True(!await vm.EndTrialAsync(), "stop unconfirmed");
            var afterUnknown = store.LoadAll();
            Equal(1, afterUnknown.Count, "still 1 record after unconfirmed stop");
            Equal(sid, afterUnknown[0].SessionId, "same session_id");
            Equal("Unknown", afterUnknown[0].Report.SandboxInstanceState, "records Unknown sandbox state separately");
            Equal("Remaining", afterUnknown[0].Report.HostStagingState, "records Remaining staging state separately");
            True(afterUnknown[0].Report.EndedAtUtc is null, "unconfirmed session has no fake end timestamp");
            Equal("未记录", afterUnknown[0].Report.DisplayEntrySha256, "missing hash honestly displays 未记录");

            // Recheck confirms ended, staging still Remaining -> updates SAME record
            now = now.AddMinutes(1);
            True(await vm.RecheckPendingSessionAsync(), "recheck resolves ConfirmedEnded");
            var afterRecheck = store.LoadAll();
            Equal(1, afterRecheck.Count, "still 1 record after recheck");
            Equal("ConfirmedEnded", afterRecheck[0].Report.SandboxInstanceState, "sandbox now ConfirmedEnded");
            Equal("Remaining", afterRecheck[0].Report.HostStagingState, "staging still Remaining");
            True(afterRecheck[0].Report.EndedAtUtc is not null, "end timestamp populated once confirmed ended");

            // Retry cleanup succeeds -> updates SAME record
            service.CleanupOnlyFactory = (_, _) => Task.FromResult(new SandboxStopResult(
                SandboxInstanceState.ConfirmedEnded, TrialStagingState.Cleaned, "已清理"));
            now = now.AddMinutes(1);
            True(await vm.RetryStagingCleanupAsync(), "retry cleanup succeeds");
            var afterClean = store.LoadAll();
            Equal(1, afterClean.Count, "still strictly 1 record after retry cleanup");
            Equal("ConfirmedEnded", afterClean[0].Report.SandboxInstanceState, "final sandbox ConfirmedEnded");
            Equal("Cleaned", afterClean[0].Report.HostStagingState, "final staging Cleaned");
            Equal("已确认结束", afterClean[0].Report.OverallResultLabel, "final overall result 已确认结束");
        });

        await Check("crash recovery hydrates and updates strictly 1 history record preserving original metadata and hash", async () =>
        {
            using var temp = new TempDir();
            var historyDir = Path.Combine(temp.Root, "HistoryRecovery");
            var appRoot = temp.Sub("便携 恢复样例");
            var binDir = Path.Combine(appRoot, "bin");
            Directory.CreateDirectory(binDir);
            var exePath = Path.Combine(binDir, "App.exe");
            WriteMinimalPe(exePath, 0x8664, extraByte: 0x55);
            var expectedHash = await WindowsSandboxTrialService.ComputeFileSha256Async(exePath);

            var store = new JsonTrialHistoryStore(historyDir);
            var startedAt = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.FromHours(8));
            string crashedSessionId = "";

            // Session 1: starts, acquires instanceId, then crashes (no EndTrialAsync called!)
            var svc1 = new ControlledTrialService
            {
                TargetStartFactory = (target, progress, _) =>
                {
                    crashedSessionId = target.SessionId!;
                    progress?.Report(new TrialStagingProgress("输入快照准备完成，正在启动 Windows 沙箱…", 1, 1, 128, 128, "", StagedEntrySha256: expectedHash));
                    progress?.Report(new TrialStagingProgress("沙箱实例已创建，等待桌面就绪…", 1, 1, 128, 128, "", StagedEntrySha256: expectedHash, CreatedInstanceId: InstanceA));
                    return Task.FromResult(new SandboxStartResult(crashedSessionId, InstanceA, @"C:\temp\" + crashedSessionId, @"C:\temp\in", 3001, startedAt, target.Kind, target.EntryRelativePath, target.TotalFileCount, expectedHash));
                }
            };
            using (var vm1 = new IsolatedTrialViewModel(svc1, clock: () => startedAt, pollInterval: TimeSpan.FromHours(1), historyStore: store))
            {
                await vm1.RefreshCapabilityAsync();
                True(await vm1.SelectFolderAsync(appRoot), "folder scanned");
                True(await vm1.StartTrialAsync(), "started before simulated crash");
            }

            Equal(1, store.LoadAll().Count, "1 record persisted before crash");

            // Session 2: fresh Contain restart recovers pending-session with same session_id
            var pending = new PendingTrialSessionRecord(1, crashedSessionId, InstanceA, @"C:\temp\" + crashedSessionId, "便携 恢复样例 (bin\\App.exe)", startedAt.ToUniversalTime(), 99999, 123456);
            var recoveredAt = startedAt.AddMinutes(3);
            var svc2 = new ControlledTrialService
            {
                PendingLoadResult = new PendingSessionLoadResult(PendingSessionLoadStatus.Loaded, pending),
                QueryFactory = (_, _, _) => Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Running, true, TrialStagingState.Remaining))
            };

            using var vm2 = new IsolatedTrialViewModel(svc2, clock: () => recoveredAt, pollInterval: TimeSpan.FromHours(1), historyStore: store);
            await vm2.CheckPendingSessionOnStartupAsync();
            Equal(IsolatedTrialPhase.RecoveredRunning, vm2.Phase, "recovered as running");
            Equal(1, store.LoadAll().Count, "still strictly 1 record during recovery");

            True(await vm2.EndTrialAsync(), "ended recovered session");
            var finalRecords = store.LoadAll();
            Equal(1, finalRecords.Count, "strictly 1 record after crash + recovery + end");
            var finalReport = finalRecords[0].Report;
            Equal(crashedSessionId, finalReport.SessionId, "preserved exact session_id");
            Equal(startedAt.ToUniversalTime(), finalReport.StartedAtUtc, "preserved original start time");
            Equal(expectedHash, finalReport.EntrySha256 ?? "", "preserved exact staged entry SHA-256 across crash");
            Equal("folder", finalReport.InputKind, "preserved folder input mode across crash");
            Equal(@"bin\App.exe", finalReport.EntryRelativePath, "preserved relative entry path across crash");
            Equal("ConfirmedEnded", finalReport.SandboxInstanceState, "final sandbox ConfirmedEnded");
            Equal("Cleaned", finalReport.HostStagingState, "final staging Cleaned");
            True(finalReport.Timeline.Any(t => t.Stage == "启动恢复检查") && finalReport.Timeline.Any(t => t.Stage == "试用已结束"),
                "timeline merges pre-crash and post-recovery events");
        });

        await Check("staged snapshot hashing, same-name disambiguation, replay change warning (文件已变化), missing target, and immutable old history", async () =>
        {
            using var temp = new TempDir();
            var historyDir = Path.Combine(temp.Root, "HistoryReplay");
            var stagingBase = temp.Sub("staging-hash");
            var dirA = temp.Sub("same-a");
            var dirB = temp.Sub("same-b");
            var exeA = Path.Combine(dirA, "SameName.exe");
            var exeB = Path.Combine(dirB, "SameName.exe");
            WriteMinimalPe(exeA, 0x8664, extraByte: 0x11);
            WriteMinimalPe(exeB, 0x8664, extraByte: 0x22);

            // Verify real StageInputSnapshotAsync computes authoritative staged entry SHA-256 in single pass
            var realSvc = new WindowsSandboxTrialService(stagingBaseDirectory: stagingBase);
            var targetA = TrialLaunchTarget.FromSingleExecutable(WindowsSandboxTrialService.InspectExecutableFile(exeA));
            var targetB = TrialLaunchTarget.FromSingleExecutable(WindowsSandboxTrialService.InspectExecutableFile(exeB));
            var snapA = await realSvc.StageInputSnapshotAsync(targetA, Path.Combine(stagingBase, "inA"));
            var snapB = await realSvc.StageInputSnapshotAsync(targetB, Path.Combine(stagingBase, "inB"));
            var expectedHashA = await WindowsSandboxTrialService.ComputeFileSha256Async(exeA);
            var expectedHashB = await WindowsSandboxTrialService.ComputeFileSha256Async(exeB);
            Equal(expectedHashA, snapA.EntrySha256 ?? "", "staged snapshot A hash equals exact file bytes");
            Equal(expectedHashB, snapB.EntrySha256 ?? "", "staged snapshot B hash equals exact file bytes");
            True(!string.Equals(snapA.EntrySha256, snapB.EntrySha256, StringComparison.Ordinal), "two same-name EXEs produce distinct staged hashes");

            var store = new JsonTrialHistoryStore(historyDir);
            var now = new DateTimeOffset(2026, 11, 23, 13, 0, 0, TimeSpan.FromHours(8));
            var service = new ControlledTrialService
            {
                TargetStartFactory = async (target, _, ct) =>
                {
                    var digest = await WindowsSandboxTrialService.ComputeFileSha256Async(target.EntryExecutable.FullPath, ct);
                    return new SandboxStartResult(target.SessionId!, InstanceA, @"C:\temp\" + target.SessionId!, @"C:\temp\in", 4001, now, target.Kind, target.EntryRelativePath, target.TotalFileCount, digest);
                }
            };

            using var vm = new IsolatedTrialViewModel(service, clock: () => now, pollInterval: TimeSpan.FromHours(1), historyStore: store);
            await vm.RefreshCapabilityAsync();

            // Run A and B
            True(vm.SelectExecutable(exeA), "selected exeA");
            True(await vm.StartTrialAsync(), "started A");
            var idA = vm.CurrentHistorySessionId;
            True(await vm.EndTrialAsync(), "ended A");

            now = now.AddMinutes(1);
            True(vm.SelectExecutable(exeB), "selected exeB");
            True(await vm.StartTrialAsync(), "started B");
            var idB = vm.CurrentHistorySessionId;
            True(await vm.EndTrialAsync(), "ended B");

            var recA = store.LoadBySessionId(idA)!;
            var recB = store.LoadBySessionId(idB)!;
            True(!string.Equals(recA.Report.EntrySha256, recB.Report.EntrySha256, StringComparison.Ordinal), "history distinguishes two same-name EXEs by SHA-256");

            // Replace exeA with different bytes! Old record must remain unchanged; replay must warn "文件已变化" and NOT auto-start
            WriteMinimalPe(exeA, 0x8664, extraByte: 0x99);
            var modifiedHashA = await WindowsSandboxTrialService.ComputeFileSha256Async(exeA);
            var startCallsBeforeReplay = service.StartCalls;
            True(await vm.PrepareReplayFromHistoryAsync(recA), "replay prepares confirmation screen");
            Equal(startCallsBeforeReplay, service.StartCalls, "replay NEVER auto-starts Sandbox");
            True(vm.HasReplayNotice && vm.IsReplayNoticeWarning && vm.ReplayNoticeText.Contains("文件已变化", StringComparison.Ordinal),
                "replay explicitly warns 文件已变化 when source bytes changed");
            Equal(expectedHashA, store.LoadBySessionId(idA)!.Report.EntrySha256 ?? "", "old history record's recorded SHA-256 remains untouched");

            // Confirming Start after replay creates a NEW record with new SHA-256 while old record stays intact
            now = now.AddMinutes(1);
            True(await vm.StartTrialAsync(), "confirmed replay starts");
            var idReplay = vm.CurrentHistorySessionId;
            True(!string.Equals(idReplay, idA, StringComparison.Ordinal), "replay start creates new session_id");
            True(await vm.EndTrialAsync(), "replay ends");
            Equal(3, store.LoadAll().Count, "now 3 distinct records");
            Equal(expectedHashA, store.LoadBySessionId(idA)!.Report.EntrySha256 ?? "", "original record still retains old SHA-256");
            Equal(modifiedHashA, store.LoadBySessionId(idReplay)!.Report.EntrySha256 ?? "", "new replay record captures modified SHA-256");

            // Deleting source file before replay allows clean re-selection with explicit missing notice
            File.Delete(exeB);
            True(!await vm.PrepareReplayFromHistoryAsync(recB), "deleted file reports missing");
            True(vm.HasReplayNotice && vm.IsReplayNoticeWarning && vm.ReplayNoticeText.Contains("已移动、删除", StringComparison.Ordinal),
                "missing file warns clearly and allows re-selecting");

            // Folder mode replay re-scans directory and detects entry hash modification
            var portDir = temp.Sub("PortableReplay");
            var subBin = Path.Combine(portDir, "bin");
            Directory.CreateDirectory(subBin);
            var portExe = Path.Combine(subBin, "Demo.exe");
            WriteMinimalPe(portExe, 0x8664, extraByte: 0x61);
            File.WriteAllText(Path.Combine(portDir, "readme.txt"), "hello");
            var origPortHash = await WindowsSandboxTrialService.ComputeFileSha256Async(portExe);

            now = now.AddMinutes(1);
            True(await vm.SelectFolderAsync(portDir), "scanned portable dir");
            True(await vm.StartTrialAsync(), "started portable dir");
            var portId = vm.CurrentHistorySessionId;
            True(await vm.EndTrialAsync(), "ended portable dir");
            var portRec = store.LoadBySessionId(portId)!;
            Equal(origPortHash, portRec.Report.EntrySha256 ?? "", "portable record saved exact entry hash");

            WriteMinimalPe(portExe, 0x8664, extraByte: 0x62);
            File.WriteAllText(Path.Combine(portDir, "extra.txt"), "added-file");
            True(await vm.PrepareReplayFromHistoryAsync(portRec), "folder replay re-scans directory");
            Equal(3, vm.SelectedFolder?.FileCount ?? 0, "folder replay reflects updated live file count (3 files)");
            Equal(@"bin\Demo.exe", vm.SelectedFolderEntry?.RelativePath ?? "", "folder replay auto-selects historical entry");
            True(vm.IsReplayNoticeWarning && vm.ReplayNoticeText.Contains("文件已变化", StringComparison.Ordinal), "folder replay detects modified entry hash");
        });

        await Check("history persistence write failure shows warning but NEVER blocks ending or cleaning up Sandbox", async () =>
        {
            using var temp = new TempDir();
            var exePath = Path.Combine(temp.Sub("app"), "Resilient.exe");
            WriteMinimalPe(exePath, 0x8664);

            var failingStore = new ThrowingHistoryStore();
            var service = new ControlledTrialService();
            using var vm = new IsolatedTrialViewModel(service, pollInterval: TimeSpan.FromHours(1), historyStore: failingStore);
            await vm.RefreshCapabilityAsync();
            True(vm.SelectExecutable(exePath), "selected");

            True(await vm.StartTrialAsync(), "start succeeds even when history Save throws");
            True(vm.HasHistorySaveError && vm.HistorySaveErrorMessage.Contains("记录保存失败", StringComparison.Ordinal),
                "shows visible history save warning");

            True(await vm.EndTrialAsync(), "EndTrialAsync STILL stops and cleans Sandbox cleanly despite history Save failures");
            Equal(IsolatedTrialPhase.Ended, vm.Phase, "Ended phase reached");
            Equal(SandboxInstanceState.ConfirmedEnded, vm.InstanceState, "Sandbox confirmed ended");
            Equal(TrialStagingState.Cleaned, vm.StagingState, "Staging cleaned");
            True(vm.HasHistorySaveError, "save warning remains visible to user");
        });

        await Check("sandbox-unavailable machine can browse history and export versioned JSON report free of host paths, usernames, and raw CLI", async () =>
        {
            using var temp = new TempDir();
            var historyDir = Path.Combine(temp.Root, "HistoryExport");
            var secretFolderName = "便携 导出测试";
            var appRoot = temp.Sub(secretFolderName);
            var binDir = Path.Combine(appRoot, "bin");
            Directory.CreateDirectory(binDir);
            var exePath = Path.Combine(binDir, "Tool.exe");
            WriteMinimalPe(exePath, 0x8664, extraByte: 0x77);
            var expectedHash = await WindowsSandboxTrialService.ComputeFileSha256Async(exePath);

            var store = new JsonTrialHistoryStore(historyDir);
            var now = new DateTimeOffset(2026, 11, 23, 14, 0, 0, TimeSpan.FromHours(8));
            var service = new ControlledTrialService
            {
                TargetStartFactory = (target, _, _) => Task.FromResult(new SandboxStartResult(
                    target.SessionId!, InstanceA, @"C:\Users\SecretHostUser\AppData\Local\Temp\Contain-IsolatedTrial\" + target.SessionId!,
                    @"C:\Users\SecretHostUser\AppData\Local\Temp\input", 5001, now, target.Kind, target.EntryRelativePath, target.TotalFileCount, expectedHash)),
                StopFactory = (_, _, _) => Task.FromResult(new SandboxStopResult(
                    SandboxInstanceState.ConfirmedEnded, TrialStagingState.Cleaned,
                    "已确认本次沙箱实例结束，并已清理本次宿主暂存目录。",
                    @"raw diag: wsb.exe stop --id 11111111-2222-3333-4444-555555555555 C:\Users\SecretHostUser\secret"))
            };

            using (var vm = new IsolatedTrialViewModel(service, clock: () => now, pollInterval: TimeSpan.FromHours(1), historyStore: store))
            {
                await vm.RefreshCapabilityAsync();
                True(await vm.SelectFolderAsync(appRoot), "scanned folder");
                True(await vm.StartTrialAsync(), "started folder trial");
                True(await vm.EndTrialAsync(), "ended folder trial");
            }

            // Simulate a Home-edition machine without Windows Sandbox opening TrialHistoryViewModel
            var homeService = new ControlledTrialService
            {
                CapabilityReport = WindowsSandboxTrialService.ClassifyHostEnvironment(BaseSnapshot() with
                {
                    EditionId = "Core", ProductName = "Windows 11 Home", ProductType = 0x65
                })
            };
            using var homeTrialVm = new IsolatedTrialViewModel(homeService, historyStore: store);
            await homeTrialVm.RefreshCapabilityAsync();
            True(homeTrialVm.IsCapabilityBlocked, "Home machine cannot start Sandbox");

            var historyVm = new TrialHistoryViewModel(store);
            historyVm.Refresh();
            Equal(1, historyVm.TotalCount, "Home machine still loads existing trial history");
            True(historyVm.OpenRecord(historyVm.FilteredRecords[0].SessionId), "opens record detail without Sandbox");

            var exportPath = Path.Combine(temp.Root, "exported-report.json");
            True(historyVm.ExportSelectedReport(exportPath), "exports versioned JSON report without Sandbox");
            var exportedJson = File.ReadAllText(exportPath);
            var parsed = JsonTrialHistoryStore.DeserializeReportJson(exportedJson)
                ?? throw new InvalidOperationException("Failed to deserialize exported report JSON.");

            Equal(1, parsed.SchemaVersion, "schema_version == 1");
            Equal(TrialSessionReport.ProtocolName, parsed.Protocol, "protocol matches contain-isolated-trial-report-1");
            Equal(historyVm.DetailTitle, parsed.SoftwareName, "detail view and JSON share exact software name");
            Equal(historyVm.DetailEntryRelativePath, parsed.EntryRelativePath, "detail view and JSON share exact relative entry");
            Equal(historyVm.DetailEntrySha256, parsed.EntrySha256 ?? "", "detail view and JSON share exact entry SHA-256");
            Equal(historyVm.DetailSandboxStateLabel, parsed.SandboxInstanceStateLabel, "detail view and JSON share sandbox state");
            Equal(historyVm.DetailStagingStateLabel, parsed.HostStagingStateLabel, "detail view and JSON share staging state");
            True(parsed.FolderScope is not null && parsed.FolderScope.FileCount == 1, "folder scope preserved in JSON");

            // Strict privacy assertions: NO host absolute paths, NO usernames, NO raw diagnostic CLI commands!
            True(!exportedJson.Contains(temp.Root, StringComparison.OrdinalIgnoreCase), "exported JSON omits host temp root");
            True(!exportedJson.Contains(appRoot, StringComparison.OrdinalIgnoreCase), "exported JSON omits host source folder path");
            True(!exportedJson.Contains("SecretHostUser", StringComparison.OrdinalIgnoreCase), "exported JSON omits host staging username");
            True(!exportedJson.Contains("wsb.exe", StringComparison.OrdinalIgnoreCase), "exported JSON omits raw diagnostic commands");
            True(!exportedJson.Contains("local_hints", StringComparison.OrdinalIgnoreCase), "exported JSON omits local replay hints");
            if (!string.IsNullOrWhiteSpace(Environment.UserName) && Environment.UserName.Length >= 3)
            {
                True(!exportedJson.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase),
                    "exported JSON omits current Windows username");
            }
        });

        return passed;

        async Task Check(string name, Func<Task> body)
        {
            await body();
            passed.Add("isolated_trial: " + name);
        }
    }

    private static HostSandboxEnvironmentSnapshot BaseSnapshot() => new(
        IsWindows: true, IsNativeX64: true,
        EditionId: null, ProductName: null, ProductType: null, EditionQueryDenied: false,
        HasWindowsSandboxExe: false, WsbExecutablePath: null, WsbHelpSucceeded: null, WsbHelpOutput: null,
        MissingWsbSubcommands: [], WsbVersion: null, WsbListSucceeded: null, WsbListDiagnostic: null,
        PolicyExplicitlyDisabled: null, PolicyEvidence: null, PolicyQueryDenied: false,
        HypervisorPresent: null, VirtualizationFirmwareEnabled: null, VirtualizationQueryDenied: false,
        RebootPending: false, RebootPendingQueryDenied: false);

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

        public PendingSessionLoadResult PendingLoadResult { get; set; } = new(PendingSessionLoadStatus.None);
        public Func<string, IProgress<TrialFolderScanProgress>?, CancellationToken, Task<TrialFolderInfo>>? ScanFolderFactory { get; set; }
        public Func<string, CancellationToken, Task<string>>? HashFactory { get; set; }
        public Func<TrialExecutableInfo, CancellationToken, Task<SandboxStartResult>>? StartFactory { get; set; }
        public Func<TrialLaunchTarget, IProgress<TrialStagingProgress>?, CancellationToken, Task<SandboxStartResult>>? TargetStartFactory { get; set; }
        public Func<SandboxStartResult, TrialExecutableInfo, CancellationToken, Task<GuestProgramLaunchResult>>? LaunchFactory { get; set; }
        public Func<SandboxStartResult, TrialLaunchTarget, CancellationToken, Task<GuestProgramLaunchResult>>? TargetLaunchFactory { get; set; }
        public Func<SandboxStartResult, CancellationToken, Task<SandboxInstancePollResult>>? PollFactory { get; set; }
        public Func<string, string?, CancellationToken, Task<SandboxInstancePollResult>>? QueryFactory { get; set; }
        public Func<string?, string?, CancellationToken, Task<SandboxStopResult>>? StopFactory { get; set; }
        public Func<string?, CancellationToken, Task<SandboxStopResult>>? CleanupOnlyFactory { get; set; }

        public int StartCalls { get; private set; }
        public int OpenWindowsFeaturesCalls { get; private set; }
        public int ClearPendingCalls { get; private set; }
        public List<string> StoppedInstances { get; } = [];
        public List<string> CleanedStagingPaths { get; } = [];

        public Task<SandboxCapabilityReport> ProbeCapabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CapabilityReport);

        public TrialExecutableInfo InspectExecutable(string executablePath) =>
            WindowsSandboxTrialService.InspectExecutableFile(executablePath);

        public Task<TrialFolderInfo> ScanFolderAsync(
            string folderPath,
            IProgress<TrialFolderScanProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            ScanFolderFactory is not null
                ? ScanFolderFactory(folderPath, progress, cancellationToken)
                : WindowsSandboxTrialService.ScanPortableFolderAsync(folderPath, null, progress, cancellationToken);

        public Task<string> ComputeSha256Async(string executablePath, CancellationToken cancellationToken = default) =>
            HashFactory is not null
                ? HashFactory(executablePath, cancellationToken)
                : WindowsSandboxTrialService.ComputeFileSha256Async(executablePath, cancellationToken);

        public void OpenWindowsFeatures() => OpenWindowsFeaturesCalls++;

        public Task<SandboxStartResult> StartSandboxAsync(
            TrialLaunchTarget target,
            IProgress<TrialStagingProgress>? stagingProgress = null,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            if (TargetStartFactory is not null) return TargetStartFactory(target, stagingProgress, cancellationToken);
            if (StartFactory is not null) return StartFactory(target.EntryExecutable, cancellationToken);
            var sid = target.SessionId ?? "a1b2c3d4e5f6";
            return Task.FromResult(new SandboxStartResult(sid, InstanceA, @"C:\temp\" + sid, @"C:\temp\" + sid + @"\input", 1001, DateTimeOffset.UtcNow, target.Kind, target.EntryRelativePath, target.TotalFileCount));
        }

        public Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(
            SandboxStartResult session,
            TrialLaunchTarget target,
            CancellationToken cancellationToken = default)
        {
            if (TargetLaunchFactory is not null) return TargetLaunchFactory(session, target, cancellationToken);
            if (LaunchFactory is not null) return LaunchFactory(session, target.EntryExecutable, cancellationToken);
            return Task.FromResult(new GuestProgramLaunchResult(GuestProgramLaunchOutcome.StartedAndRunning, 0, $"已启动 {target.EntryRelativePath}"));
        }

        public Task<SandboxInstancePollResult> PollSessionAsync(SandboxStartResult session, CancellationToken cancellationToken = default)
        {
            if (PollFactory is not null) return PollFactory(session, cancellationToken);
            return Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Running, true, TrialStagingState.Active));
        }

        public Task<SandboxInstancePollResult> QueryInstanceStateAsync(string instanceId, string? stagingRootPath, CancellationToken cancellationToken = default)
        {
            if (QueryFactory is not null) return QueryFactory(instanceId, stagingRootPath, cancellationToken);
            return Task.FromResult(new SandboxInstancePollResult(SandboxInstanceState.Running, true, TrialStagingState.Remaining));
        }

        public Task<SandboxStopResult> StopAndCleanupAsync(string? instanceId, string? stagingRootPath, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(instanceId)) StoppedInstances.Add(instanceId);
            if (!string.IsNullOrEmpty(stagingRootPath)) CleanedStagingPaths.Add(stagingRootPath);
            if (StopFactory is not null) return StopFactory(instanceId, stagingRootPath, cancellationToken);
            return Task.FromResult(new SandboxStopResult(SandboxInstanceState.ConfirmedEnded, TrialStagingState.Cleaned, "已确认本次沙箱实例结束，并已清理本次宿主暂存目录。"));
        }

        public Task<SandboxStopResult> CleanupStagingOnlyAsync(string? stagingRootPath, CancellationToken cancellationToken = default)
        {
            if (CleanupOnlyFactory is not null) return CleanupOnlyFactory(stagingRootPath, cancellationToken);
            return Task.FromResult(new SandboxStopResult(SandboxInstanceState.ConfirmedEnded, TrialStagingState.Cleaned, "已清理本次宿主暂存目录。"));
        }

        public PendingSessionLoadResult LoadPendingSession() => PendingLoadResult;

        public void ClearPendingSession(string? expectedInstanceId = null, string? expectedSessionId = null) => ClearPendingCalls++;
    }

    private sealed class ThrowingHistoryStore : ITrialHistoryStore
    {
        public string HistoryDirectoryPath => @"C:\unwritable\history";
        public IReadOnlyList<TrialHistoryRecord> LoadAll() => [];
        public TrialHistoryRecord? LoadBySessionId(string sessionId) => null;
        public void Save(TrialHistoryRecord record) => throw new IOException("simulated disk full on history directory");
        public void ExportReport(TrialSessionReport report, string destinationPath) => throw new IOException("simulated export failure");
    }
}
