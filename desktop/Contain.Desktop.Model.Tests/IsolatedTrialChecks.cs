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

            startGate.SetResult(new SandboxStartResult("a1b2c3d4e5f6", InstanceA, @"C:\temp\a1b2c3d4e5f6", @"C:\temp\a1b2c3d4e5f6\input", 4242, DateTimeOffset.UtcNow));
            await WaitUntilAsync(() => vm.Phase == IsolatedTrialPhase.SandboxOpened);
            Equal("沙箱已打开", vm.SandboxStageBadge, "sandbox opened");
            Equal("正在沙箱内启动程序…", vm.ProgramStageBadge, "program launching");

            launchGate.SetResult(new GuestProgramLaunchResult(GuestProgramLaunchOutcome.StartedAndRunning, 0, "已启动"));
            True(await first, "start trial completes");
            Equal(IsolatedTrialPhase.ProgramStarted, vm.Phase, "ProgramStarted");
            Equal("程序已启动", vm.ProgramStageBadge, "program started");

            await vm.CloseAsync();
            Equal(1, service.StoppedInstances.Count, "CloseAsync stops owned instance");
            Equal(InstanceA, service.StoppedInstances[0], "CloseAsync stops exact ID");
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
        public Func<string, CancellationToken, Task<string>>? HashFactory { get; set; }
        public Func<TrialExecutableInfo, CancellationToken, Task<SandboxStartResult>>? StartFactory { get; set; }
        public Func<SandboxStartResult, TrialExecutableInfo, CancellationToken, Task<GuestProgramLaunchResult>>? LaunchFactory { get; set; }
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

        public Task<string> ComputeSha256Async(string executablePath, CancellationToken cancellationToken = default) =>
            HashFactory is not null
                ? HashFactory(executablePath, cancellationToken)
                : WindowsSandboxTrialService.ComputeFileSha256Async(executablePath, cancellationToken);

        public void OpenWindowsFeatures() => OpenWindowsFeaturesCalls++;

        public Task<SandboxStartResult> StartSandboxAsync(TrialExecutableInfo executable, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            if (StartFactory is not null) return StartFactory(executable, cancellationToken);
            return Task.FromResult(new SandboxStartResult("a1b2c3d4e5f6", InstanceA, @"C:\temp\a1b2c3d4e5f6", @"C:\temp\a1b2c3d4e5f6\input", 1001, DateTimeOffset.UtcNow));
        }

        public Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(SandboxStartResult session, TrialExecutableInfo executable, CancellationToken cancellationToken = default)
        {
            if (LaunchFactory is not null) return LaunchFactory(session, executable, cancellationToken);
            return Task.FromResult(new GuestProgramLaunchResult(GuestProgramLaunchOutcome.StartedAndRunning, 0, $"已启动 {executable.FileName}"));
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
}
