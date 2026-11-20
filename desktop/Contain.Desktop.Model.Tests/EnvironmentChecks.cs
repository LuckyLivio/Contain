using Contain.Desktop.Model;

namespace Contain.Desktop.Model.Tests;

internal static class EnvironmentChecks
{
    private const string SampleShaA = "canonical JSON SHA-256 1111111111111111111111111111111111111111111111111111111111111111";
    private const string SampleShaB = "canonical JSON SHA-256 2222222222222222222222222222222222222222222222222222222222222222";

    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();

        await Check("folder selection only inspects declarations and cancel retains current project", async () =>
        {
            using var temp = new TempDir();
            var projectA = temp.Sub("project-a");
            File.WriteAllText(Path.Combine(projectA, "contain.environment.json"), "{}");

            var service = new ControlledEnvironmentService();
            using var vm = new EnvironmentCheckViewModel(service);
            True(!vm.HasProject && !vm.CanStartCheck && !vm.HasOutcome, "initial empty state");

            True(await vm.SelectProjectAsync(projectA), "select valid project");
            Equal("project-a", vm.ProjectName, "project name extracted");
            Equal(projectA, vm.ProjectPath, "normalized project path");
            Equal(0, service.CheckCalls, "selecting a folder must not execute CLI checks");
            True(vm.CanStartCheck && !vm.HasOutcome, "explicit start enabled without outcome");

            service.NextFactory = (_, _) => Task.FromResult(ReadyExecution());
            True(await vm.CheckAsync(), "explicit check succeeds");
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "ready outcome");

            vm.CancelSelection();
            Equal("project-a", vm.ProjectName, "cancel preserves project name");
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "cancel preserves existing result");
            Equal(1, service.CheckCalls, "cancel runs nothing");
        });

        foreach (var refresh in new[] { false, true })
        {
            var operation = refresh ? "watcher refresh" : "folder selection";
            await Check($"slow {operation} leaves the UI queue responsive and cannot replace a newer project", async () =>
            {
                using var temp = new TempDir();
                var oldProject = temp.Sub("old");
                var newProject = temp.Sub("new");
                var oldPreview = DotnetOnboardingParser.ParseInspection(
                    """{"schema_version":1,"protocol":"contain-dotnet-onboarding-1","status":"unsupported","unsupported_title":"old project"}""");
                var wpfPreview = oldPreview with
                {
                    Status = "ready_to_confirm", AppName = "WindowApp",
                    EvidenceFields = [new OnboardingFieldRow("应用类型", "WPF 桌面应用", "WindowApp.csproj")]
                };
                var service = new ControlledEnvironmentService { OnboardingInspectFactory = _ => oldPreview };
                using var vm = new EnvironmentCheckViewModel(service);
                if (refresh)
                    True(await vm.SelectProjectAsync(oldProject), "establish the watched project");

                // Model the dispatcher's single queue. A blocking inspection would hold up the second selection.
                var ui = new TaskFactory(new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler);
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new ManualResetEventSlim();
                var timeout = TimeSpan.FromSeconds(5);
                service.OnboardingInspectFactory = dir =>
                {
                    if (dir == newProject) return wpfPreview;
                    started.SetResult();
                    if (!release.Wait(timeout + timeout)) throw new TimeoutException("inspection gate");
                    return oldPreview;
                };
                var slow = ui.StartNew(() => refresh
                    ? vm.RefreshOnboardingPreviewFromDiskAsync()
                    : vm.SelectProjectAsync(oldProject)).Unwrap();
                try
                {
                    await started.Task.WaitAsync(timeout);
                    True(await ui.StartNew(() => vm.SelectProjectAsync(newProject)).Unwrap().WaitAsync(timeout),
                        "second project can be selected before releasing the slow inspection");
                    Equal("已识别 WindowApp · WPF 桌面应用", vm.OnboardingHeadline, "new WPF type displayed before old inspection returns");
                    True(!slow.IsCompleted, "older inspection is still pending");
                }
                finally { release.Set(); }

                True(!await slow.WaitAsync(timeout), "discard superseded inspection");
                Equal(newProject, vm.ProjectPath, "current project after late completion");
                Equal("已识别 WindowApp · WPF 桌面应用", vm.OnboardingHeadline, "late result cannot overwrite WPF classification");
                True(vm.CanConfirmOnboarding && !vm.IsOnboardingUnsupported, "WPF preview still available");
            });
        }

        await Check("version mismatch prioritizes actionable summary, labels provenance, and warns on synthetic 10.0.999", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("mismatch-demo");
            File.WriteAllText(Path.Combine(project, "contain.environment.json"), "{}");

            var service = new ControlledEnvironmentService
            {
                NextFactory = (_, _) => Task.FromResult(new EnvironmentCheckExecution(
                    1,
                    new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", "10.0.26300", "contain.environment.json",
                    [
                        new WorkflowCheckItem("platform", "passed", "requires Windows x64", "matched", "windows / x64", "windows / x64", null),
                        new WorkflowCheckItem("tool:rustc", "passed", "version matched", "version_matched", "1.100.0", "1.100.0", "rustup toolchain install 1.100.0"),
                        new WorkflowCheckItem("tool:dotnet", "failed", "version mismatch", "version_mismatch", "10.0.999", "10.0.403", "Install .NET SDK 10.0.999 x64")
                    ]),
                    "# Contain source\n- tool:dotnet — 失败",
                    "{\"schema_version\":1,\"workflow\":\"source\",\"checks\":[]}"))
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(project);
            True(await vm.CheckAsync(), "mismatch check completed with report");
            Equal(EnvironmentOutcomeKind.ActionRequired, vm.Outcome, "classified as action required");
            True(vm.IsActionRequired && !vm.IsCheckerError && !vm.IsInsufficientInfo, "distinct outcome flags");
            Equal("需要补齐 1 项：项目要求 .NET SDK (dotnet) 10.0.999，当前检测到 10.0.403", vm.Headline, "exact actionable headline");
            Equal(1, vm.ActionItems.Count, "one failed item prioritized");
            True(vm.ActionItems[0].OfficialGuidanceLine.Contains("10.0.999", StringComparison.Ordinal)
                && vm.ActionItems[0].OfficialGuidanceLine.Contains("不承诺", StringComparison.Ordinal)
                && !vm.ActionItems[0].OfficialGuidanceLine.Contains("https://dotnet.microsoft.com/download/dotnet/", StringComparison.Ordinal),
                "10.0.999 does not promise official download availability");
            Equal("项目清单（contain.environment.json）自带说明：Install .NET SDK 10.0.999 x64", vm.ActionItems[0].RemedyLine, "manifest remedy provenance labeled");
            Equal(2, vm.PassedItems.Count, "two satisfied items retained");
            True(!vm.PassedExpanded, "satisfied items collapsed by default");
            True(vm.CanRecheck && vm.CanToggleDetailedReport, "recheck and report toggle available");

            vm.ToggleDetailedReport();
            True(vm.ShowDetailedReport && vm.DetailedReportText.Contains("mismatch-demo", StringComparison.Ordinal), "detailed report bound to project");
        });

        await Check("supported .NET SDK shows explicit version, official download entry, and user-controlled install note", () =>
        {
            var supported10 = EnvironmentCheckViewModel.BuildDotNetSdkGuidance("10.0.404");
            True(supported10.IsSupportedReleaseBand, "10.0.404 recognized as supported release band");
            True(supported10.GuidanceLine.Contains("10.0.404", StringComparison.Ordinal)
                && supported10.GuidanceLine.Contains("https://dotnet.microsoft.com/download/dotnet/10.0", StringComparison.Ordinal)
                && supported10.GuidanceLine.Contains("是否安装由你决定", StringComparison.Ordinal),
                "10.0.404 includes explicit version, official channel URL, and user choice");

            var supported9 = EnvironmentCheckViewModel.BuildDotNetSdkGuidance("9.0.308");
            True(supported9.IsSupportedReleaseBand
                && supported9.GuidanceLine.Contains("https://dotnet.microsoft.com/download/dotnet/9.0", StringComparison.Ordinal),
                "9.0.308 points to official .NET 9.0 channel");

            var inferredRow = EnvironmentCheckViewModel.FormatRow(
                new WorkflowCheckItem("tool:dotnet", "failed", "version mismatch", "version_mismatch", "10.0.404", "10.0.403", "Install .NET SDK 10.0.404 from https://dotnet.microsoft.com/download"),
                "inferred_from_project_files");
            True(inferredRow.RemedyLine.StartsWith("项目声明文件（global.json）推断说明：", StringComparison.Ordinal), "global.json provenance labeled");
            return Task.CompletedTask;
        });

        await Check("recheck on same unchanged project surfaces resolved item while keeping remaining missing items pinned on top", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("recheck-project");
            File.WriteAllText(Path.Combine(project, "contain.environment.json"), "{}");

            var step = 0;
            var service = new ControlledEnvironmentService
            {
                NextFactory = (_, _) =>
                {
                    step++;
                    return step switch
                    {
                        1 => Task.FromResult(new EnvironmentCheckExecution(
                            1,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", "10.0.26300", "contain.environment.json",
                            [
                                new WorkflowCheckItem("platform", "passed", "requires Windows x64", "matched", "windows / x64", "windows / x64", null),
                                new WorkflowCheckItem("tool:dotnet", "failed", "version mismatch", "version_mismatch", "10.0.404", "10.0.403", "Install .NET SDK 10.0.404"),
                                new WorkflowCheckItem("tool:python", "failed", "unavailable", "unavailable", "3.12.10", null, "Install Python 3.12.10"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "# report 1",
                            "{}")),
                        2 => Task.FromResult(new EnvironmentCheckExecution(
                            1,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", "10.0.26300", "contain.environment.json",
                            [
                                new WorkflowCheckItem("platform", "passed", "requires Windows x64", "matched", "windows / x64", "windows / x64", null),
                                new WorkflowCheckItem("tool:dotnet", "passed", "version matched", "version_matched", "10.0.404", "10.0.404", "Install .NET SDK 10.0.404"),
                                new WorkflowCheckItem("tool:python", "failed", "unavailable", "unavailable", "3.12.10", null, "Install Python 3.12.10"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "# report 2",
                            "{}")),
                        _ => Task.FromResult(new EnvironmentCheckExecution(
                            0,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", "10.0.26300", "contain.environment.json",
                            [
                                new WorkflowCheckItem("platform", "passed", "requires Windows x64", "matched", "windows / x64", "windows / x64", null),
                                new WorkflowCheckItem("tool:dotnet", "passed", "version matched", "version_matched", "10.0.404", "10.0.404", "Install .NET SDK 10.0.404"),
                                new WorkflowCheckItem("tool:python", "passed", "version matched", "version_matched", "3.12.10", "3.12.10", "Install Python 3.12.10"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "# report 3",
                            "{}"))
                    };
                }
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(project);

            True(await vm.CheckAsync(), "initial check with 2 missing items");
            Equal(2, vm.ActionItems.Count, "two missing items initially");
            Equal(0, vm.ResolvedItems.Count, "nothing resolved on initial check");

            True(await vm.CheckAsync(), "second check resolves .NET SDK while Python stays missing");
            Equal(EnvironmentOutcomeKind.ActionRequired, vm.Outcome, "still action required while Python missing");
            Equal(1, vm.ActionItems.Count, "still-missing Python remains in ActionItems");
            Equal("tool:python", vm.ActionItems[0].RawName, "Python pinned at top");
            Equal(1, vm.ResolvedItems.Count, "resolved list shows newly passed .NET SDK");
            Equal("已解决：.NET SDK (dotnet) 10.0.404", vm.ResolvedItems[0].DisplayTitle, "exact resolved title for .NET SDK");
            True(vm.ResolvedItems[0].SummaryLine.Contains("10.0.403", StringComparison.Ordinal)
                && vm.ResolvedItems[0].SummaryLine.Contains("10.0.404", StringComparison.Ordinal), "resolved summary cites prior and confirmed versions");

            True(await vm.CheckAsync(), "third check resolves Python and reaches Ready");
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "all checks now ready");
            Equal(0, vm.ActionItems.Count, "no remaining missing items");
            Equal(1, vm.ResolvedItems.Count, "only newly resolved Python shown on step 3");
            Equal("已解决：Python 解释器 (python) 3.12.10", vm.ResolvedItems[0].DisplayTitle, "exact resolved title for Python");
            True(vm.Headline.StartsWith("已解决：Python 解释器 (python) 3.12.10", StringComparison.Ordinal), "ready headline highlights newly resolved item");

            True(await vm.CheckAsync(), "fourth check with no new transitions clears resolved banner");
            Equal(0, vm.ResolvedItems.Count, "already-passed items are not re-reported as newly resolved");
        });

        await Check("unconfirmed status, requirement edits, or project switches never claim item was resolved", async () =>
        {
            using var temp = new TempDir();
            var projectA = temp.Sub("project-req-change");
            var projectB = temp.Sub("project-other");
            File.WriteAllText(Path.Combine(projectA, "contain.environment.json"), "{}");
            File.WriteAllText(Path.Combine(projectB, "contain.environment.json"), "{}");

            var call = 0;
            var service = new ControlledEnvironmentService
            {
                NextFactory = (_, _) =>
                {
                    call++;
                    return call switch
                    {
                        1 => Task.FromResult(new EnvironmentCheckExecution(
                            1,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [
                                new WorkflowCheckItem("tool:dotnet", "failed", "mismatch", "version_mismatch", "10.0.404", "10.0.403", "Install 10.0.404"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "", "{}")),
                        // Call 2: status becomes needs_confirmation rather than passed -> must NOT be marked resolved.
                        2 => Task.FromResult(new EnvironmentCheckExecution(
                            2,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [
                                new WorkflowCheckItem("tool:dotnet", "needs_confirmation", "unsupported probe", "unsupported_probe", "10.0.404", null, "Install 10.0.404"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "", "{}")),
                        // Call 3: back to failed on 10.0.404 with SampleShaA.
                        3 => Task.FromResult(new EnvironmentCheckExecution(
                            1,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [
                                new WorkflowCheckItem("tool:dotnet", "failed", "mismatch", "version_mismatch", "10.0.404", "10.0.403", "Install 10.0.404"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "", "{}")),
                        // Call 4: manifest requirement lowered to 10.0.403 (SampleShaB) -> passes, but must NOT say resolved!
                        4 => Task.FromResult(new EnvironmentCheckExecution(
                            0,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [
                                new WorkflowCheckItem("tool:dotnet", "passed", "matched", "version_matched", "10.0.403", "10.0.403", "Install 10.0.403"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaB)
                            ]),
                            "", "{}")),
                        // Call 5: switched to project B where everything passes -> fresh comparison, no resolved items.
                        _ => Task.FromResult(new EnvironmentCheckExecution(
                            0,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [
                                new WorkflowCheckItem("tool:dotnet", "passed", "matched", "version_matched", "10.0.404", "10.0.404", "Install 10.0.404"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "", "{}"))
                    };
                }
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(projectA);
            await vm.CheckAsync();
            await vm.CheckAsync();
            Equal(0, vm.ResolvedItems.Count, "failed -> needs_confirmation must not mark resolved");

            await vm.CheckAsync();
            await vm.CheckAsync();
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "lowered requirement passes for new manifest");
            Equal(0, vm.ResolvedItems.Count, "lowering project requirement must not claim item was resolved");
            True(vm.ComparisonNote.Contains("要求已变更", StringComparison.Ordinal), "comparison reset note shown when manifest changes");
            True(!vm.Headline.Contains("已解决", StringComparison.Ordinal), "headline does not claim resolved when manifest changed");

            await vm.SelectProjectAsync(projectB);
            await vm.CheckAsync();
            Equal(0, vm.ResolvedItems.Count, "switching projects starts fresh comparison");
            Equal("", vm.ComparisonNote, "project switch has clean comparison note");
        });

        await Check("checker failure during recheck preserves last valid result with timestamps and allows subsequent recovery", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("preserve-on-error");
            File.WriteAllText(Path.Combine(project, "contain.environment.json"), "{}");

            var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Unspecified));
            var timestamps = new Queue<DateTimeOffset>(
            [
                new DateTimeOffset(2026, 11, 18, 9, 0, 0, localOffset),
                new DateTimeOffset(2026, 11, 18, 9, 5, 30, localOffset),
                new DateTimeOffset(2026, 11, 18, 9, 8, 45, localOffset)
            ]);

            var attempt = 0;
            var service = new ControlledEnvironmentService
            {
                NextFactory = (_, _) =>
                {
                    attempt++;
                    return attempt switch
                    {
                        1 => Task.FromResult(new EnvironmentCheckExecution(
                            1,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [
                                new WorkflowCheckItem("tool:dotnet", "failed", "mismatch", "version_mismatch", "10.0.404", "10.0.403", "Install 10.0.404"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "# initial valid report",
                            "{\"attempt\":1}")),
                        2 => Task.FromException<EnvironmentCheckExecution>(
                            new EnvironmentCheckException("report_not_generated", "检查程序未能生成报告。", "unexpected EOF at line 3")),
                        _ => Task.FromResult(new EnvironmentCheckExecution(
                            0,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [
                                new WorkflowCheckItem("tool:dotnet", "passed", "matched", "version_matched", "10.0.404", "10.0.404", "Install 10.0.404"),
                                new WorkflowCheckItem("environment_manifest", "passed", SampleShaA)
                            ]),
                            "# recovered report",
                            "{\"attempt\":3}"))
                    };
                }
            };

            using var vm = new EnvironmentCheckViewModel(service, () => timestamps.Dequeue());
            await vm.SelectProjectAsync(project);
            True(await vm.CheckAsync(), "attempt 1 succeeds with missing SDK");
            True(vm.LastCheckedText.Contains("09:00:00", StringComparison.Ordinal), "initial timestamp displayed");

            True(!await vm.CheckAsync(), "attempt 2 fails in checker");
            Equal(EnvironmentOutcomeKind.ActionRequired, vm.Outcome, "previous ActionRequired outcome preserved");
            Equal(1, vm.ActionItems.Count, "previous missing SDK row preserved");
            True(vm.DetailedReportText.Contains("initial valid report", StringComparison.Ordinal), "previous detailed report preserved");
            True(vm.HasRecheckFailure, "recheck failure banner visible");
            True(vm.LastCheckedText.Contains("09:00:00", StringComparison.Ordinal), "preserved result timestamp labeled");
            True(vm.RecheckFailureTitle.Contains("09:05:30", StringComparison.Ordinal), "failure timestamp labeled");
            True(vm.RecheckFailureDetail.Contains("unexpected EOF", StringComparison.Ordinal), "failure diagnostic shown");
            True(vm.CanRecheck, "user can retry recheck immediately");

            True(await vm.CheckAsync(), "attempt 3 succeeds after recovery");
            True(!vm.HasRecheckFailure, "recheck failure banner cleared on success");
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "now Ready");
            Equal(1, vm.ResolvedItems.Count, "resolved item still computed against preserved baseline");
            Equal("已解决：.NET SDK (dotnet) 10.0.404", vm.ResolvedItems[0].DisplayTitle, "resolved title after transient checker failure");
            True(vm.LastCheckedText.Contains("09:08:45", StringComparison.Ordinal), "updated check timestamp shown");
        });

        await Check("inferred project without manifest distinguishes insufficient info from missing tools", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("rust-only");
            File.WriteAllText(Path.Combine(project, "Cargo.toml"), "[package]\nname='x'\n");
            File.WriteAllText(Path.Combine(project, "rust-toolchain.toml"), "[toolchain]\nchannel='1.100.0'\n");

            var service = new ControlledEnvironmentService
            {
                NextFactory = (_, _) => Task.FromResult(new EnvironmentCheckExecution(
                    2,
                    new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", "10.0.26300", "inferred_from_project_files",
                    [
                        new WorkflowCheckItem("platform", "passed", "requires Windows x64", "matched", "windows / x86_64", "windows / x86_64", null),
                        new WorkflowCheckItem("tool:rustc", "passed", "version matched", "version_matched", "1.100.0", "1.100.0", "rustup"),
                        new WorkflowCheckItem("requirement", "needs_confirmation", "Confirm Windows SDK and MSVC prerequisites", "pending_confirmation", null, null, null)
                    ]),
                    "# Contain source",
                    "{\"schema_version\":1,\"workflow\":\"source\",\"checks\":[]}"))
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(project);
            True(vm.SelectionScopeNote.Contains("infer", StringComparison.Ordinal), "pre-check scope note mentions infer");
            True(await vm.CheckAsync(), "inferred check completed");
            Equal(EnvironmentOutcomeKind.InsufficientInfo, vm.Outcome, "classified as insufficient info");
            True(vm.IsInsufficientInfo && !vm.IsActionRequired && !vm.IsCheckerError && !vm.IsReady, "distinct from missing tools and ready");
            Equal(1, vm.PendingItems.Count, "pending item surfaced");
            Equal(2, vm.PassedItems.Count, "satisfied inferred items collapsed");
        });

        await Check("checker or manifest error on first check is distinct from missing developer tools", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("broken-manifest");
            File.WriteAllText(Path.Combine(project, "contain.environment.json"), "{broken");

            var service = new ControlledEnvironmentService
            {
                NextFactory = (_, _) => Task.FromException<EnvironmentCheckExecution>(
                    new EnvironmentCheckException("report_not_generated", "无法解析该项目的环境声明文件，或检查程序未能生成报告。", "EOF while parsing an object"))
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(project);
            True(!await vm.CheckAsync(), "checker error returns false");
            Equal(EnvironmentOutcomeKind.CheckerError, vm.Outcome, "classified as checker error");
            True(vm.IsCheckerError && !vm.IsActionRequired && !vm.IsInsufficientInfo, "not reported as missing SDK");
            True(vm.ErrorDetail.Contains("EOF while parsing", StringComparison.Ordinal), "diagnostic retained");
        });

        await Check("rapid project switch cancels and discards superseded project results", async () =>
        {
            using var temp = new TempDir();
            var projectA = temp.Sub("slow-project-a");
            var projectB = temp.Sub("fast-project-b");
            File.WriteAllText(Path.Combine(projectA, "contain.environment.json"), "{}");
            File.WriteAllText(Path.Combine(projectB, "contain.environment.json"), "{}");

            var slowCompletion = new TaskCompletionSource<EnvironmentCheckExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ControlledEnvironmentService
            {
                NextFactory = (path, _) => path == projectA
                    ? slowCompletion.Task // Deliberately ignore token to prove generation guard rejects late completion.
                    : Task.FromResult(ReadyExecution("fast-b-marker"))
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(projectA);
            var firstTask = vm.CheckAsync();
            True(vm.IsChecking, "first check active");

            await vm.SelectProjectAsync(projectB);
            Equal("fast-project-b", vm.ProjectName, "switched to project B immediately");
            True(!vm.HasOutcome && !vm.IsChecking, "project A state cleared upon switching");

            var secondTask = vm.CheckAsync();
            slowCompletion.SetResult(new EnvironmentCheckExecution(
                1,
                new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                [new WorkflowCheckItem("tool:python", "failed", "stale project A failure", "unavailable", "3.12.10", null, "install python")]),
                "stale-a-markdown",
                "{}"));

            True(!await firstTask, "superseded first check rejected");
            True(await secondTask, "second check accepted");
            Equal("fast-project-b", vm.ProjectName, "project B still active");
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "project A failure did not overwrite project B");
            True(vm.DetailedReportText.Contains("fast-b-marker", StringComparison.Ordinal)
                && !vm.DetailedReportText.Contains("stale-a-markdown", StringComparison.Ordinal), "report strictly belongs to project B");
        });

        await Check("report parser enforces schema 1 source contract and parses optional evidence", () =>
        {
            var report = WorkflowCliEnvironmentService.ParseReport("""
                {
                  "schema_version": 1,
                  "workflow": "source",
                  "host_os": "windows",
                  "host_arch": "x86_64",
                  "process_arch": "x86_64",
                  "manifest_source": "contain.environment.json",
                  "checks": [
                    {
                      "name": "tool:dotnet",
                      "status": "failed",
                      "detail": "version mismatch",
                      "outcome": "version_mismatch",
                      "required": "10.0.403",
                      "observed": "10.0.100",
                      "remedy": "Install 10.0.403"
                    }
                  ]
                }
                """);
            Equal(1, report.Checks.Count, "parsed one check");
            Equal("10.0.100", report.Checks[0].Observed, "parsed observed version");

            Throws<EnvironmentCheckException>(() => WorkflowCliEnvironmentService.ParseReport("{\"schema_version\":2,\"workflow\":\"source\",\"checks\":[]}"));
            Throws<EnvironmentCheckException>(() => WorkflowCliEnvironmentService.ParseReport("{\"schema_version\":1,\"workflow\":\"package\",\"checks\":[]}"));
            return Task.CompletedTask;
        });

        await Check("direct manifest reader builds preview without CLI call and rejects invalid schemas or unsafe paths", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("console-preview");
            WriteValidConsoleManifest(project);

            var service = new ControlledEnvironmentService();
            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(project);
            Equal(0, service.CheckCalls, "selection never calls check");
            Equal(0, service.BuildCalls, "preview never calls build");
            Equal(2, vm.BuildPreviewSteps.Count, "1 build + 1 verify step parsed in-process");
            True(!vm.BuildCommandsExpanded, "full commands collapsed by default");
            True(vm.BuildFullCommandsText.Contains("dotnet build Hello.csproj -c Release -o artifacts/app", StringComparison.Ordinal)
                && vm.BuildFullCommandsText.Contains("artifacts/app/Hello.exe --verify", StringComparison.Ordinal),
                "expandable full command text generated directly from manifest");

            var badUnknown = WorkflowManifestReader.ParsePreviewJson("p", project, """{"schema_version":1,"target":"windows-x64","tools":[],"required_variables":[],"build":[],"verify":[],"extra":1}""", "abc");
            Equal(ManifestPreviewStatus.Invalid, badUnknown.Status, "unknown field rejected");

            var badArtifact = WorkflowManifestReader.ParsePreviewJson("p", project, """{"schema_version":1,"target":"windows-x64","tools":[],"required_variables":[],"build":[],"verify":[],"artifact_dir":"../outside"}""", "abc");
            Equal(ManifestPreviewStatus.Invalid, badArtifact.Status, "unsafe relative artifact_dir rejected");
        });

        await Check("start build appears only when ready with explicit manifest and blocks preserved green result after failed recheck", async () =>
        {
            using var temp = new TempDir();
            var explicitProj = temp.Sub("explicit-proj");
            WriteValidConsoleManifest(explicitProj);

            var inferredProj = temp.Sub("inferred-proj");
            File.WriteAllText(Path.Combine(inferredProj, "Cargo.toml"), "[package]\nname='x'\n");

            var call = 0;
            var service = new ControlledEnvironmentService
            {
                NextFactory = (path, _) =>
                {
                    if (path == inferredProj)
                    {
                        return Task.FromResult(new EnvironmentCheckExecution(
                            2,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "inferred_from_project_files",
                            [new WorkflowCheckItem("requirement", "needs_confirmation", "confirm MSVC")]),
                            "", "{}"));
                    }

                    call++;
                    return call switch
                    {
                        1 => Task.FromResult(ReadyExecution()),
                        _ => Task.FromException<EnvironmentCheckExecution>(
                            new EnvironmentCheckException("cli_timeout", "检查超时"))
                    };
                }
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(inferredProj);
            await vm.CheckAsync();
            True(!vm.CanShowStartBuild && vm.HasBuildPrerequisiteBlocker, "inferred-only manifest cannot start build");
            True(vm.BuildPrerequisiteMessage.Contains("contain.environment.json", StringComparison.Ordinal), "explains explicit manifest needed");

            await vm.SelectProjectAsync(explicitProj);
            True(await vm.CheckAsync(), "first check passes");
            True(vm.CanShowStartBuild && vm.CanStartBuild, "explicit ready project exposes Start Build");
            Equal("explicit-proj", vm.BuildPreviewProjectName, "preview project name shown");
            Equal(explicitProj, vm.BuildPreviewWorkingDirectory, "preview working directory shown");

            // Now fail recheck while preserving green Ready result -> Start Build MUST be blocked ("上次保留的绿色结果仅供查看")!
            True(!await vm.CheckAsync(), "recheck fails");
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "green Ready outcome preserved for viewing");
            True(vm.HasRecheckFailure, "recheck failure flagged");
            True(!vm.CanShowStartBuild && !vm.CanStartBuild, "preserved green result after failed recheck must not allow build");
            True(vm.BuildPrerequisiteMessage.Contains("仅供查看", StringComparison.Ordinal), "explains preserved green result is view-only");
        });

        await Check("manifest edit refreshes preview and blocks build on click until rechecked", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("consistency-proj");
            WriteValidConsoleManifest(project, buildName: "Compile v1");

            var service = new ControlledEnvironmentService();
            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(project);
            True(await vm.CheckAsync(), "environment check passes on v1");
            True(vm.CanStartBuild, "ready to build v1");

            // Simulate user editing contain.environment.json right before clicking Start Build (even before watcher fires)
            WriteValidConsoleManifest(project, buildName: "Compile v2");
            True(!await vm.StartBuildAsync(), "click-time consistency check refuses changed manifest");
            Equal(0, service.BuildCalls, "contain-workflow build was never spawned");
            True(vm.IsManifestChangedSinceCheck && !vm.CanShowStartBuild, "marked changed since check");
            True(vm.BuildPreviewSteps[0].Name == "Compile v2", "preview refreshed to v2");
            True(vm.BuildPrerequisiteMessage.Contains("重新检查", StringComparison.Ordinal), "prompts recheck");

            // Recheck on v2 restores build eligibility
            True(await vm.CheckAsync(), "recheck on v2 passes");
            True(vm.CanShowStartBuild && vm.CanStartBuild, "ready to build v2 after recheck");
        });

        await Check("build prevents re-entrancy, streams real stage progress, fails cleanly despite stale artifacts, and opens confirmed artifact folder on success", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("build-lifecycle");
            WriteValidConsoleManifest(project);

            // Plant a stale artifact directory BEFORE the failing build!
            var artifactDir = Path.Combine(project, "artifacts", "app");
            Directory.CreateDirectory(artifactDir);
            var artifactExe = Path.Combine(artifactDir, "Hello.exe");
            File.WriteAllText(artifactExe, "stale-old-binary");

            var progressSeen = new List<string>();
            var buildGate = new TaskCompletionSource<ProjectBuildExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            var buildAttempt = 0;

            var service = new ControlledEnvironmentService
            {
                BuildFactory = (_, _, progress, _) =>
                {
                    buildAttempt++;
                    if (buildAttempt == 1)
                    {
                        progress?.Report(new BuildProgressUpdate("build", "running", 1, 1, "Compile console app"));
                        return buildGate.Task;
                    }

                    progress?.Report(new BuildProgressUpdate("build", "running", 1, 1, "Compile console app"));
                    progress?.Report(new BuildProgressUpdate("verify", "running", 1, 1, "Verify console output"));
                    return Task.FromResult(new ProjectBuildExecution(
                        0,
                        new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                        [
                            new WorkflowCheckItem("platform", "passed", "ok"),
                            new WorkflowCheckItem("Compile console app", "passed", "exit 0"),
                            new WorkflowCheckItem("Verify console output", "passed", "exit 0"),
                            new WorkflowCheckItem("build_artifacts", "passed", "verified")
                        ]),
                        "# build passed",
                        "{}",
                        [
                            new BuildStepExecutionResult("build", 0, "Compile console app", "passed", 0, 0, false, null, "Build succeeded.", ""),
                            new BuildStepExecutionResult("verify", 0, "Verify console output", "passed", 0, 0, false, null, "HELLO_CONTAIN", "")
                        ],
                        "=== full log ===",
                        artifactDir));
                }
            };

            string? openedDir = null;
            using var vm = new EnvironmentCheckViewModel(
                service,
                artifactFolderOpener: dir => openedDir = dir);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EnvironmentCheckViewModel.BuildProgressText) && vm.BuildProgressText.Length > 0)
                    progressSeen.Add(vm.BuildProgressText);
            };

            await vm.SelectProjectAsync(project);
            await vm.CheckAsync();

            // Start Build #1 (in-flight) -> duplicate StartBuildAsync must be blocked
            var firstBuildTask = vm.StartBuildAsync();
            True(vm.IsBuilding && !vm.CanStartBuild && !vm.CanStartCheck && !vm.CanRecheck, "duplicate launch and recheck blocked while building");
            True(!await vm.StartBuildAsync(), "concurrent StartBuildAsync rejected");
            Equal(1, service.BuildCalls, "only one build started");

            // Complete Build #1 as a compile error while stale Hello.exe still sits on disk!
            buildGate.SetResult(new ProjectBuildExecution(
                1,
                new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                [
                    new WorkflowCheckItem("platform", "passed", "ok"),
                    new WorkflowCheckItem("Compile console app", "failed", "expected exit 0, observed Some(1)"),
                    new WorkflowCheckItem("Verify console output", "not_run", "requires successful build"),
                    new WorkflowCheckItem("build_artifacts", "not_run", "requires successful build")
                ]),
                "# build failed",
                "{}",
                [
                    new BuildStepExecutionResult("build", 0, "Compile console app", "failed", 0, 1, false, null, "Program.cs(5,12): error CS1002: ; expected", ""),
                    new BuildStepExecutionResult("verify", 0, "Verify console output", "not_run", 0, null, false, null, "", "")
                ],
                "=== failed log ===",
                null));

            True(!await firstBuildTask, "failed build returns false");
            Equal(BuildOutcomeKind.Failed, vm.BuildOutcome, "marked Failed despite stale Hello.exe on disk");
            True(!vm.CanOpenArtifactFolder && !vm.OpenArtifactFolder(), "artifact folder button hidden and blocked on failed build");
            True(vm.BuildHeadline.Contains("Compile console app", StringComparison.Ordinal), "identifies failed step");
            True(vm.BuildFailureErrorSummary.Contains("error CS1002", StringComparison.Ordinal), "surfaces concrete compiler error");
            Equal("失败", vm.BuildStepRows[0].StatusBadge, "build step badge failed");
            Equal("未执行", vm.BuildStepRows[1].StatusBadge, "subsequent verify step marked unrun");
            True(vm.CanToggleBuildLog, "build log available on failure");

            // Now rebuild in the SAME ViewModel after fixing source -> succeeds and opens confirmed artifact folder!
            True(await vm.StartBuildAsync(), "second build succeeds");
            Equal(BuildOutcomeKind.Succeeded, vm.BuildOutcome, "succeeded on retry");
            True(vm.CanOpenArtifactFolder, "confirmed artifact folder available");
            True(vm.OpenArtifactFolder(), "opens artifact folder");
            Equal(artifactDir, openedDir, "opened exact confirmed artifact directory");

            // If artifact directory is deleted afterwards, clicking OpenArtifactFolder refuses cleanly
            Directory.Delete(artifactDir, recursive: true);
            True(!vm.OpenArtifactFolder(), "refuses opening deleted artifact directory");
            True(!vm.CanOpenArtifactFolder, "hides open button once directory no longer exists");
        });

        await Check("framework-dependent build keeps green Build Succeeded, shows immediate dotnet publish guidance, and parses optional installer manifest", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("fd-hello");
            File.WriteAllText(Path.Combine(project, "HelloContain.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            WriteValidConsoleManifest(project);

            var artifactDir = Path.Combine(project, "artifacts", "app");
            Directory.CreateDirectory(artifactDir);
            File.WriteAllText(Path.Combine(artifactDir, "Hello.exe"), "pe-stub");
            File.WriteAllText(
                Path.Combine(artifactDir, "Hello.runtimeconfig.json"),
                """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");

            var validWithInstaller = WorkflowManifestReader.ParsePreviewJson(
                "fd-hello",
                project,
                """
                {
                  "schema_version": 1,
                  "target": "windows-x64",
                  "tools": [],
                  "required_variables": [],
                  "build": [],
                  "verify": [],
                  "artifact_dir": "artifacts/publish",
                  "installer": {
                    "app_name": "HelloContain",
                    "version": "0.1.0",
                    "entry": "HelloContain.exe",
                    "publish_dir": "artifacts/publish",
                    "publish_kind": "dotnet-self-contained-win-x64"
                  }
                }
                """,
                "sha");
            Equal(ManifestPreviewStatus.Valid, validWithInstaller.Status, "optional installer block valid");
            Equal("0.1.0", validWithInstaller.Installer?.Version, "installer version parsed");

            var badEntry = WorkflowManifestReader.ParsePreviewJson(
                "fd-hello",
                project,
                """{"schema_version":1,"target":"windows-x64","tools":[],"required_variables":[],"build":[],"verify":[],"installer":{"entry":"run.cmd"}}""",
                "sha");
            Equal(ManifestPreviewStatus.Invalid, badEntry.Status, "non-exe installer entry rejected");

            var service = new ControlledEnvironmentService
            {
                BuildFactory = (_, _, _, _) => Task.FromResult(new ProjectBuildExecution(
                    0,
                    new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                    [
                        new WorkflowCheckItem("platform", "passed", "ok"),
                        new WorkflowCheckItem("Compile console app", "passed", "exit 0"),
                        new WorkflowCheckItem("Verify console output", "passed", "exit 0"),
                        new WorkflowCheckItem("build_artifacts", "passed", "verified")
                    ]),
                    "# ok",
                    "{}",
                    [],
                    "log",
                    artifactDir,
                    "{\"schema_version\":1}")),
                InstallerFactory = (_, _, _) => Task.FromResult(new ProjectInstallerExecution(
                    1,
                    new WorkflowReportData(1, "project_installer", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                    [
                        new WorkflowCheckItem(
                            "dotnet_self_contained_publish",
                            "failed",
                            "当前构建通过，但产物是依赖本机 .NET 运行时的普通编译结果。",
                            "framework_dependent_output",
                            "dotnet-self-contained-win-x64",
                            null,
                            "dotnet publish HelloContain.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/app"),
                        new WorkflowCheckItem(
                            "installer_version",
                            "failed",
                            "项目未明确声明应用版本号。",
                            "missing_version",
                            "explicit version",
                            null,
                            "请在 installer.version 中声明版本号。")
                    ]),
                    "# failed",
                    "{}",
                    null))
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(project);
            await vm.CheckAsync();
            True(await vm.StartBuildAsync(), "framework-dependent build passes");
            Equal(BuildOutcomeKind.Succeeded, vm.BuildOutcome, "BuildOutcome is Succeeded");
            True(vm.ShowGenerateInstallerButton && vm.CanGenerateInstaller, "Generate Installer button shown next to Open Artifact Folder");
            True(vm.HasPublishReadinessHint, "immediate self-contained publish hint shown right after dotnet build");
            True(vm.PublishReadinessHintCommand.Contains("dotnet publish HelloContain.csproj -c Release -r win-x64 --self-contained true", StringComparison.Ordinal),
                "proactive hint names current .csproj and self-contained flags");

            True(!await vm.GenerateInstallerAsync(), "packaging framework-dependent output returns false");
            Equal(BuildOutcomeKind.Succeeded, vm.BuildOutcome, "BuildOutcome remains Succeeded ('构建通过') after packaging guidance");
            True(vm.HasInstallerFailure, "actionable packaging guidance banner visible");
            True(vm.InstallerFailureTitle.Contains("依赖本机 .NET 运行时", StringComparison.Ordinal), "clear framework-dependent title");
            True(vm.InstallerFailureRemedy.Contains("dotnet publish HelloContain.csproj", StringComparison.Ordinal)
                && vm.InstallerFailureRemedy.Contains("installer.version", StringComparison.Ordinal),
                "surfaces both self-contained publish command and version remedy");
        });

        await Check("installer generation highlights Setup EXE, preserves last valid installer on subsequent failure, blocks stale EXE on failed build, and isolates across project switch", async () =>
        {
            using var temp = new TempDir();
            var projectA = temp.Sub("app-a");
            var projectB = temp.Sub("app-b");
            WriteValidConsoleManifest(projectA);
            WriteValidConsoleManifest(projectB);

            var pubDirA = Path.Combine(projectA, "artifacts", "app");
            Directory.CreateDirectory(pubDirA);
            File.WriteAllText(Path.Combine(pubDirA, "Hello.exe"), "self-contained-x64");

            var outInstallerDir = temp.Sub("installer-out-1");
            var fakeSetupPath = Path.Combine(outInstallerDir, "HelloContain-Setup-x64.exe");
            File.WriteAllText(fakeSetupPath, "MZ-fake-setup");

            var pkgCall = 0;
            var buildCall = 0;
            var service = new ControlledEnvironmentService
            {
                BuildFactory = (_, _, _, _) =>
                {
                    buildCall++;
                    if (buildCall == 2)
                    {
                        // Second build FAILS even though Hello.exe is still sitting on disk!
                        return Task.FromResult(new ProjectBuildExecution(
                            1,
                            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [new WorkflowCheckItem("Compile console app", "failed", "exit 1")]),
                            "# fail",
                            "{}",
                            [new BuildStepExecutionResult("build", 0, "Compile console app", "failed", 0, 1, false, null, "error CS0103", "")],
                            "fail log",
                            null,
                            null));
                    }
                    return Task.FromResult(new ProjectBuildExecution(
                        0,
                        new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                        [new WorkflowCheckItem("platform", "passed", "ok")]),
                        "# ok",
                        "{}",
                        [],
                        "ok log",
                        pubDirA,
                        "{\"schema_version\":1}"));
                },
                InstallerFactory = (_, _, _) =>
                {
                    pkgCall++;
                    if (pkgCall == 1)
                    {
                        return Task.FromResult(new ProjectInstallerExecution(
                            0,
                            new WorkflowReportData(1, "project_installer", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                            [new WorkflowCheckItem("installer_compile", "passed", "compiled")]),
                            "# ok",
                            "{}",
                            new ProjectInstallerReceiptData(
                                "app-a",
                                "HelloContain",
                                "a1b2c3d4-1111-4222-8333-444455556666",
                                "a1b2c3d4",
                                "generated_and_persisted",
                                "0.1.0",
                                "0.1.0.0",
                                null,
                                "dotnet-self-contained-win-x64",
                                "artifacts/app",
                                "HelloContain.exe",
                                new string('a', 64),
                                new string('b', 64),
                                new string('c', 64),
                                215,
                                "%LOCALAPPDATA%\\Programs\\HelloContain-a1b2c3d4",
                                "HelloContain (a1b2c3d4).lnk",
                                "HelloContain-Setup-x64.exe",
                                123456,
                                new string('d', 64),
                                "NotSigned",
                                false,
                                "6.7.3",
                                fakeSetupPath,
                                outInstallerDir,
                                "# Receipt summary")));
                    }
                    return Task.FromResult(new ProjectInstallerExecution(
                        1,
                        new WorkflowReportData(1, "project_installer", "windows", "x86_64", "x86_64", null, "contain.environment.json",
                        [
                            new WorkflowCheckItem(
                                "inno_setup_compiler",
                                "failed",
                                "ISCC.exe not found",
                                "inno_setup_missing",
                                "Inno Setup 6",
                                null,
                                "winget install --exact --id JRSoftware.InnoSetup --scope user（接收者无需安装）")
                        ]),
                        "# fail",
                        "{}",
                        null));
                }
            };

            string? openedFolder = null;
            using var vm = new EnvironmentCheckViewModel(service, artifactFolderOpener: dir => openedFolder = dir);
            await vm.SelectProjectAsync(projectA);
            await vm.CheckAsync();
            True(await vm.StartBuildAsync(), "build #1 succeeds");

            // 1) First installer generation succeeds
            True(await vm.GenerateInstallerAsync(), "installer #1 succeeds");
            True(vm.HasValidInstaller && !vm.HasInstallerFailure, "valid installer shown without error");
            Equal("HelloContain-Setup-x64.exe", vm.GeneratedInstallerFileName, "prominent installer filename");
            True(vm.GeneratedInstallerSummaryLine.Contains("未签名 (NotSigned)", StringComparison.Ordinal), "unsigned status noted");
            True(vm.OpenInstallerFolder(), "opens installer directory");
            Equal(outInstallerDir, openedFolder, "opened exact installer directory");

            // 2) Second installer generation fails -> preserves last valid installer AND surfaces failure clearly!
            True(!await vm.GenerateInstallerAsync(), "installer #2 fails");
            True(vm.HasInstallerFailure, "current failure banner visible");
            True(vm.InstallerFailureTitle.Contains("接收者无需安装", StringComparison.Ordinal), "explains receiver does not need Inno Setup");
            True(vm.HasValidInstaller, "previous valid installer preserved on screen");
            True(vm.GeneratedInstallerBadge.StartsWith("上次已生成的可用安装包", StringComparison.Ordinal), "preserved installer clearly labeled");
            Equal("HelloContain-Setup-x64.exe", vm.GeneratedInstallerFileName, "previous valid filename still accessible");

            // 3) Failed build #2 with stale Hello.exe on disk MUST NOT allow packaging or claim valid installer for failed build
            True(!await vm.StartBuildAsync(), "build #2 fails");
            Equal(BuildOutcomeKind.Failed, vm.BuildOutcome, "build #2 marked Failed");
            True(!vm.ShowGenerateInstallerButton && !vm.CanGenerateInstaller && !vm.HasValidInstaller,
                "failed build hides Generate Installer and never packages stale EXE");
            True(File.Exists(fakeSetupPath), "previous installer file on disk remains untouched");

            // 4) Build #3 succeeds + re-surfaces preserved installer if packaging fails, then switching to project B clears everything
            True(await vm.StartBuildAsync(), "build #3 succeeds");
            True(!await vm.GenerateInstallerAsync(), "packaging fails again");
            True(vm.HasValidInstaller, "previous valid installer for project A still preserved");

            await vm.SelectProjectAsync(projectB);
            True(!vm.HasValidInstaller && !vm.HasInstallerFailure && string.IsNullOrEmpty(vm.GeneratedInstallerFileName),
                "switching to project B completely clears project A installer results");
        });

        await Check("unconfigured C# console onboarding previews AssemblyName entry, cancel writes nothing, confirm writes reusable manifest and connects to check/build", async () =>
        {
            using var temp = new TempDir();
            var projectDir = temp.Sub("HelloDeskFolder");
            var csprojPath = Path.Combine(projectDir, "DeskStarter.Project.csproj");
            File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>HelloDesk</AssemblyName></PropertyGroup></Project>");

            var generatedManifestJson = """
                {
                  "schema_version": 1,
                  "target": "windows-x64",
                  "tools": [
                    {
                      "program": "dotnet",
                      "version": "10.0",
                      "version_args": ["--version"],
                      "remedy": "Install .NET 10.0 SDK",
                      "version_policy": "dotnet_major_or_newer"
                    }
                  ],
                  "required_variables": [],
                  "pending": [],
                  "artifact_dir": "target/workflow/publish/win-x64",
                  "artifacts": ["HelloDesk.exe"],
                  "installer": {
                    "app_name": "HelloDesk",
                    "version": "0.1.0",
                    "publish_dir": "target/workflow/publish/win-x64",
                    "entry": "HelloDesk.exe",
                    "publish_kind": "dotnet_self_contained_dir"
                  },
                  "build": [
                    {
                      "name": "Publish Windows x64 self-contained console app",
                      "program": "dotnet",
                      "args": ["publish", "DeskStarter.Project.csproj", "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=false", "-o", "target/workflow/publish/win-x64"],
                      "expected_exit": 0,
                      "timeout_ms": 600000
                    }
                  ],
                  "verify": []
                }
                """;

            var previewData = new DotnetOnboardingPreviewData(
                "ready_to_confirm",
                new string('a', 64),
                false,
                null,
                "DeskStarter.Project.csproj",
                "HelloDesk",
                "HelloDesk",
                "HelloDesk.exe",
                "net10.0",
                "0.1.0",
                "未在 .csproj 声明版本，建议初始安装包版本 0.1.0",
                ".NET SDK 10.0 或更高主版本",
                "根据 TargetFramework=net10.0 推导（项目无 global.json）",
                ["10.0.100", "10.0.403"],
                "target/workflow/publish/win-x64",
                "dotnet publish DeskStarter.Project.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o target/workflow/publish/win-x64",
                [
                    new OnboardingFieldRow("项目文件", "DeskStarter.Project.csproj", ".csproj 根目录声明"),
                    new OnboardingFieldRow("应用类型", "控制台应用 (OutputType=Exe)", "项目声明 OutputType=Exe"),
                    new OnboardingFieldRow("启动入口", "HelloDesk.exe", ".csproj <AssemblyName>")
                ],
                generatedManifestJson,
                null,
                null,
                null,
                null);

            var service = new ControlledEnvironmentService
            {
                OnboardingInspectFactory = dir => File.Exists(Path.Combine(dir, "contain.environment.json")) ? null : previewData,
                OnboardingApplyFactory = (dir, expectedSha, _) =>
                {
                    Equal(new string('a', 64), expectedSha, "passes expected source fingerprint");
                    File.WriteAllText(Path.Combine(dir, "contain.environment.json"), generatedManifestJson);
                    return Task.FromResult(new DotnetOnboardingApplyOutcome("applied", true, "contain.environment.json", previewData));
                }
            };

            using var vm = new EnvironmentCheckViewModel(service);

            // 1) Select -> preview only, zero files written, Start Check disabled until confirmed
            True(await vm.SelectProjectAsync(projectDir), "select unconfigured C# project");
            True(vm.IsOnboardingReadyToConfirm && !vm.IsOnboardingUnsupported, "ready to confirm card visible");
            True(!vm.CanStartCheck && vm.CanConfirmOnboarding, "Start Check gated until onboarding confirmed");
            Equal("已识别 HelloDesk · 控制台应用 (OutputType=Exe)", vm.OnboardingHeadline, "shows the console classification supplied by the CLI");
            True(vm.OnboardingProvenanceSummary.Contains("HelloDesk.exe", StringComparison.Ordinal)
                && vm.OnboardingProvenanceSummary.Contains("AssemblyName=HelloDesk", StringComparison.Ordinal)
                && vm.OnboardingProvenanceSummary.Contains("0.1.0", StringComparison.Ordinal),
                "provenance distinguishes AssemblyName entry and suggested version");
            True(vm.OnboardingDetailsText.Contains("target/workflow/publish/win-x64", StringComparison.Ordinal), "collapsible details include publish output dir");
            True(!File.Exists(Path.Combine(projectDir, "contain.environment.json")), "preview writes zero files");

            // 2) Cancel -> clears selection, leaves project directory identical
            vm.CancelOnboarding();
            True(!vm.HasProject && !vm.IsOnboardingReadyToConfirm, "cancel clears selection and card");
            Equal(1, Directory.GetFiles(projectDir).Length, "cancel leaves only original .csproj");

            // 3) Select again -> Confirm -> writes contain.environment.json and enables Check -> Build
            True(await vm.SelectProjectAsync(projectDir), "re-select unconfigured C# project");
            True(await vm.ConfirmOnboardingAsync(), "explicit confirmation succeeds");
            True(File.Exists(Path.Combine(projectDir, "contain.environment.json")), "contain.environment.json written after confirmation");
            True(!vm.IsOnboardingReadyToConfirm && vm.CanStartCheck, "transitions directly to Start Check");
            True(vm.HasOnboardingSavedNotice && vm.OnboardingSavedNotice.Contains("contain.environment.json", StringComparison.Ordinal),
                "clearly states saved configuration location");

            var parsedPreview = WorkflowManifestReader.ReadPreview(projectDir);
            Equal(ManifestPreviewStatus.Valid, parsedPreview.Status, "WorkflowManifestReader validates version_policy manifest");
            Equal("HelloDesk.exe", parsedPreview.Installer?.Entry, "installer entry uses AssemblyName.exe");

            True(await vm.CheckAsync(), "check runs cleanly on newly confirmed manifest");
            True(vm.CanShowStartBuild && vm.CanStartBuild, "Start Build enabled immediately after Ready check");

            // 4) Reopening same project with contain.installer.json reuses both config and AppId without onboarding card
            File.WriteAllText(Path.Combine(projectDir, "contain.installer.json"), "{\"schema_version\":1,\"app_id\":\"12345678-1234-4234-8234-123456789abc\"}");
            True(await vm.SelectProjectAsync(projectDir), "reopen configured project");
            True(!vm.IsOnboardingReadyToConfirm && vm.CanStartCheck, "reuses existing contain.environment.json directly");
            True(vm.SelectionScopeNote.Contains("12345678-1234-4234-8234-123456789abc", StringComparison.Ordinal),
                "notes reused persistent installer identity");
        });

        await Check("onboarding handles mid-preview source changes, preserves external contain.environment.json, rejects unsupported folders, and guides 2-part .NET bands", async () =>
        {
            var bandGuidance = EnvironmentCheckViewModel.BuildDotNetSdkGuidance("10.0");
            True(bandGuidance.IsSupportedReleaseBand
                && bandGuidance.GuidanceLine.Contains("https://dotnet.microsoft.com/download/dotnet/10.0", StringComparison.Ordinal),
                "2-part 10.0 major band points to official .NET 10.0 download page");

            using var temp = new TempDir();
            var changingDir = temp.Sub("ChangingConsole");
            File.WriteAllText(Path.Combine(changingDir, "App.csproj"), "<Project />");

            var initialPreview = new DotnetOnboardingPreviewData(
                "ready_to_confirm", new string('1', 64), false, null, "App.csproj", "AppV1", "AppV1", "AppV1.exe",
                "net10.0", "0.1.0", "建议 0.1.0", ".NET SDK 10.0+", "TargetFramework", ["10.0.100"],
                "target/workflow/publish/win-x64", "dotnet publish ...", [], "{}", null, null, null, null);
            var updatedPreview = initialPreview with
            {
                SourceFingerprintSha256 = new string('2', 64),
                AppName = "AppV2",
                AssemblyName = "AppV2",
                EntryExecutable = "AppV2.exe",
                TargetFramework = "net10.0-windows",
                EvidenceFields = [new OnboardingFieldRow("应用类型", "WPF 桌面应用", "App.csproj：UseWPF=true，OutputType=WinExe")]
            };

            var applyAttempt = 0;
            var service = new ControlledEnvironmentService
            {
                OnboardingInspectFactory = _ => initialPreview,
                OnboardingApplyFactory = (dir, _, _) =>
                {
                    applyAttempt++;
                    if (applyAttempt == 1)
                    {
                        return Task.FromResult(new DotnetOnboardingApplyOutcome(
                            "source_changed_refresh_required", false, null, updatedPreview));
                    }
                    WriteValidConsoleManifest(dir);
                    return Task.FromResult(new DotnetOnboardingApplyOutcome(
                        "preserved_existing_manifest", false, "contain.environment.json", updatedPreview));
                }
            };

            using var vm = new EnvironmentCheckViewModel(service);
            await vm.SelectProjectAsync(changingDir);
            Equal("已识别 AppV1", vm.OnboardingHeadline, "preview with no type evidence shows only the application name");

            // Confirm when source changed -> refreshes preview to V2 without writing
            True(!await vm.ConfirmOnboardingAsync(), "stale fingerprint requires re-confirmation");
            True(vm.IsOnboardingReadyToConfirm && vm.HasOnboardingChangeNotice, "shows refresh notice and keeps onboarding open");
            Equal("已识别 AppV2 · WPF 桌面应用", vm.OnboardingHeadline, "refreshed preview uses the new WPF classification");
            True(vm.OnboardingDetailsText.Contains("UseWPF=true", StringComparison.Ordinal), "WPF evidence source is readable in details");
            True(!File.Exists(Path.Combine(changingDir, "contain.environment.json")), "still zero manifest written");

            // Confirm when external manifest appeared -> preserves existing manifest cleanly
            True(await vm.ConfirmOnboardingAsync(), "preserved_existing_manifest transitions cleanly");
            True(!vm.IsOnboardingReadyToConfirm && vm.CanStartCheck && vm.HasOnboardingSavedNotice, "existing manifest preserved and usable");

            var wpfDir = temp.Sub("WindowApp");
            service.OnboardingInspectFactory = _ => updatedPreview;
            True(await vm.SelectProjectAsync(wpfDir) && vm.CanConfirmOnboarding, "WPF preview is available on folder selection");
            Equal("已识别 AppV2 · WPF 桌面应用", vm.OnboardingHeadline, "fresh selection shows WPF classification");
            True(vm.StatusMessage.Contains("AppV2", StringComparison.Ordinal)
                && !vm.StatusMessage.Contains("控制台", StringComparison.Ordinal), "selection status agrees with WPF preview");

            // Unsupported multi-project / class library folder -> clear reason + next step + zero files written
            var multiDir = temp.Sub("MultiProjectSolution");
            service.OnboardingInspectFactory = _ => new DotnetOnboardingPreviewData(
                "unsupported", new string('3', 64), false, null, null, null, null, null, null, null, null, null, null, [],
                null, null, [], null, "multi_project",
                "检测到多个 .NET 项目文件，未自动猜测启动项目",
                "当前文件夹包含 One.csproj、Two.csproj。",
                "下一步：请重新点击“选择文件夹”，直接选中要发布的单个控制台应用所在的子目录。");

            await vm.SelectProjectAsync(multiDir);
            True(vm.IsOnboardingUnsupported && !vm.IsOnboardingReadyToConfirm && !vm.CanStartCheck,
                "unsupported project blocks Start Check and shows guidance");
            True(vm.OnboardingUnsupportedNextStep.Contains("单个控制台应用", StringComparison.Ordinal),
                "surfaces single actionable next step");
            Equal(0, Directory.GetFiles(multiDir).Length, "unsupported selection writes zero files");
        });

        await Check("first package waits for both consents and opens the resulting setup folder", async () =>
        {
            foreach (var type in new[] { "控制台应用 (OutputType=Exe)", "WPF 桌面应用" })
            {
                using var temp = new TempDir();
                var project = temp.Sub("Selected");
                var service = FirstPackageService(project, type);
                string? opened = null;
                using var vm = new EnvironmentCheckViewModel(service, dispatcher: _ => { }, artifactFolderOpener: dir => opened = dir);
                True(await vm.SelectProjectAsync(project), "select project");
                Equal(type == "WPF 桌面应用" ? "Greeting" : "Desk · Greeting", vm.FirstPackageProjectName,
                    "display project name and assembly when they differ");
                Equal(type, vm.FirstPackageProjectType, "project type");
                Equal("Greeting.exe", vm.FirstPackageEntry, "packaged entry");
                Equal("保存配置并检查", vm.FirstPackageActionText, "configuration consent");
                True(!File.Exists(Path.Combine(project, "contain.environment.json")), "selection does not create the manifest");
                Equal((0, 0, 0, 0), service.OperationCounts, "selection only inspects");
                True(await vm.RunFirstPackageActionAsync(), "explicitly save and check");
                Equal((1, 1, 0, 0), service.OperationCounts, "check does not start a build");
                Equal("构建并生成安装包", vm.FirstPackageActionText, "separate execution consent");
                var build = service.BuildFactory;
                var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                service.BuildFactory = async (dir, preview, progress, token) =>
                {
                    await releaseBuild.Task;
                    return await build(dir, preview, progress, token);
                };
                var first = vm.RunFirstPackageActionAsync();
                True(vm.IsBuilding && !vm.CanRunFirstPackageAction, "busy while building");
                True(!await vm.RunFirstPackageActionAsync(), "second click cannot start another build or package");
                releaseBuild.SetResult();
                True(await first, "one action builds and then packages");
                Equal((1, 1, 1, 1), service.OperationCounts, "each stage ran once");
                Equal("Greeting-Setup-x64.exe", vm.GeneratedInstallerFileName, "actual filename from receipt");
                Equal("打开安装包文件夹", vm.FirstPackageActionText, "open folder offered as final action");
                True(await vm.RunFirstPackageActionAsync(), "open resulting folder");
                Equal(Path.GetDirectoryName(vm.GeneratedInstallerFullPath), opened, "opened the receipt directory");
                Equal((1, 1, 1, 1), service.OperationCounts, "opening the folder never rebuilds");
            }
        });

        await Check("first package retries missing prerequisites at the stage that observed them", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("MissingTools");
            var service = FirstPackageService(project, "WPF 桌面应用");
            var inspect = service.OnboardingInspectFactory;
            var compilerInstalled = false;
            service.OnboardingInspectFactory = dir => inspect(dir)! with { PackagerDetected = compilerInstalled };
            using var vm = new EnvironmentCheckViewModel(service, dispatcher: _ => { });
            True(await vm.SelectProjectAsync(project), "read project with missing Inno");
            Equal("重新检测打包工具", vm.FirstPackageActionText, "redetect Inno before saving");
            compilerInstalled = true;
            True(await vm.RunFirstPackageActionAsync(), "detect newly installed Inno");
            Equal((0, 0, 0, 0), service.OperationCounts, "retry detection performs no writes, build or checks");
            Equal("保存配置并检查", vm.FirstPackageActionText, "same project can now be configured");

            var missingSdk = new WorkflowCheckItem("tool:dotnet", "failed", "SDK unavailable", "not_installed",
                "10.0", null, "Install .NET SDK 10.0");
            var sdkCheck = ReadyExecution() with { ExitCode = 1, Report = ReadyExecution().Report with { Checks = [missingSdk] } };
            service.NextFactory = (_, _) => Task.FromResult(sdkCheck);
            True(await vm.RunFirstPackageActionAsync(), "save configuration and inspect SDK");
            Equal((1, 1, 0, 0), service.OperationCounts, "stopped at missing SDK");
            Equal("重新检查环境", vm.FirstPackageActionText, "retry SDK check");
            True(vm.FirstPackageNextStep.Contains("SDK", StringComparison.Ordinal), "missing SDK explains remedy");
            service.NextFactory = (_, _) => Task.FromResult(ReadyExecution());
            True(await vm.RunFirstPackageActionAsync(), "recover SDK");
            Equal((1, 2, 0, 0), service.OperationCounts, "retry does not rewrite config or start building");

            var package = service.InstallerFactory;
            service.InstallerFactory = (_, _, _) => Task.FromResult(new ProjectInstallerExecution(1,
                ReadyExecution().Report with { Workflow = "project_installer", Checks = [
                    new WorkflowCheckItem("inno_setup_compiler", "failed", "Inno Setup not found", "inno_setup_missing",
                        "Inno Setup 6", null, "Install Inno Setup 6")] }, "", "{}", null));
            True(!await vm.RunFirstPackageActionAsync(), "compiler removed after inspection prevents setup");
            Equal((1, 2, 1, 1), service.OperationCounts, "one verified build and one failed package");
            True(vm.FirstPackageNextStep.Contains("Inno Setup", StringComparison.Ordinal), "packager recovery guidance");
            Equal("重试生成安装包", vm.FirstPackageActionText, "only package again");
            service.InstallerFactory = package;
            True(await vm.RunFirstPackageActionAsync(), "package from same verified build");
            Equal((1, 2, 1, 2), service.OperationCounts, "package retry avoids redundant check, write and build");
        });

        await Check("first package rejects a replaced manifest and an untrusted write receipt", async () =>
        {
            foreach (var mode in new[] { "changed_after_check", "missing_write_digest", "mismatched_write_digest" })
            {
                using var temp = new TempDir();
                var project = temp.Sub(mode);
                var service = FirstPackageService(project, "控制台应用 (OutputType=Exe)");
                var apply = service.OnboardingApplyFactory;
                if (mode != "changed_after_check")
                    service.OnboardingApplyFactory = async (dir, sha, token) => (await apply(dir, sha, token)) with
                    { SavedManifestSha256 = mode == "missing_write_digest" ? null : new string('0', 64) };
                using var vm = new EnvironmentCheckViewModel(service, dispatcher: _ => { });
                await vm.SelectProjectAsync(project);
                await vm.RunFirstPackageActionAsync();
                if (mode == "changed_after_check")
                {
                    Equal("构建并生成安装包", vm.FirstPackageActionText, "original generated config was checked");
                    File.AppendAllText(Path.Combine(project, "contain.environment.json"), "\n");
                }
                True(!await vm.RunFirstPackageActionAsync(), "withhold build when consent and actual manifest do not match");
                True(!vm.ShowFirstPackageFlow, "return changed or unbound config to normal manifest review");
                Equal(0, service.BuildCalls, "did not start any manifest step");
                Equal(0, service.InstallerCalls, "did not package artifacts");
                True(vm.CanStartCheck, "existing explicit manifest review remains available");
            }
        });

        await Check("first package resumes a proven generated manifest and leaves other manifests to manual review", async () =>
        {
            foreach (var provenance in new[] { "exact", "raced", "manual" })
            {
                using var temp = new TempDir();
                var project = temp.Sub(provenance);
                var service = FirstPackageService(project, "控制台应用 (OutputType=Exe)");
                var plan = service.OnboardingInspectFactory(project)!;
                var manifest = Path.Combine(project, "contain.environment.json");
                File.WriteAllText(manifest, plan.ProposedManifestJson);
                var original = File.ReadAllBytes(manifest);
                var inspectedSha = WorkflowManifestReader.ReadPreview(project).RawFileSha256;
                service.OnboardingInspectFactory = _ => plan with
                {
                    Status = "existing_manifest",
                    SourceFingerprintSha256 = inspectedSha,
                    MatchingGeneratedManifestSha256 = provenance switch
                    {
                        "exact" => inspectedSha, "raced" => new string('0', 64), _ => null
                    }
                };
                using var reopened = new EnvironmentCheckViewModel(service, dispatcher: _ => { });
                True(await reopened.SelectProjectAsync(project), "reopen saved project");
                Equal(provenance == "exact", reopened.ShowFirstPackageFlow, "only exact generated manifest resumes guided flow");
                Equal((0, provenance == "exact" ? 1 : 0, 0, 0), service.OperationCounts,
                    "resume only checks the expected generated config and never writes or builds");
                True(File.ReadAllBytes(manifest).SequenceEqual(original), "config bytes preserved");
                if (provenance == "exact")
                    Equal("构建并生成安装包", reopened.FirstPackageActionText, "execution consent is still required after reopening");
                else
                    True(reopened.CanStartCheck, "permit manual check of other manifests");
            }
        });

        return passed;

        async Task Check(string name, Func<Task> action)
        {
            await action();
            passed.Add("environment:" + name);
        }
    }

    private static ControlledEnvironmentService FirstPackageService(string project, string type)
    {
        var csproj = type == "WPF 桌面应用" ? "Greeting.csproj" : "Desk.csproj";
        File.WriteAllText(Path.Combine(project, csproj), "<Project />");
        var manifest = $$"""
            {
              "schema_version":1,"target":"windows-x64","tools":[],"required_variables":[],
              "build":[{"name":"Publish","program":"dotnet","args":["publish","{{csproj}}","-r","win-x64","--self-contained","true","-o","artifacts/app"],"expected_exit":0,"timeout_ms":300000}],
              "verify":[],"artifact_dir":"artifacts/app","artifacts":["Greeting.exe"],
              "installer":{"app_name":"Greeting","version":"1.0.0","entry":"Greeting.exe","publish_dir":"artifacts/app","publish_kind":"dotnet-self-contained-win-x64"}
            }
            """;
        var preview = new DotnetOnboardingPreviewData("ready_to_confirm", new string('1', 64), false, null,
            csproj, "Greeting", "Greeting", "Greeting.exe", "net10.0", "1.0.0", null, ".NET SDK 10.0", null,
            ["10.0.403"], "artifacts/app", $"dotnet publish {csproj}", [new OnboardingFieldRow("应用类型", type, csproj)],
            manifest, null, null, null, null, true);
        const string attestation = """{"schema_version":1,"marker":"current-build"}""";
        return new ControlledEnvironmentService
        {
            OnboardingInspectFactory = dir => File.Exists(Path.Combine(dir, "contain.environment.json")) ? null : preview,
            OnboardingApplyFactory = (dir, fingerprint, _) =>
            {
                Equal(preview.SourceFingerprintSha256, fingerprint, "confirm the selected preview");
                File.WriteAllText(Path.Combine(dir, "contain.environment.json"), manifest);
                var savedSha = WorkflowManifestReader.ReadPreview(dir).RawFileSha256;
                return Task.FromResult(new DotnetOnboardingApplyOutcome("applied", true, "contain.environment.json", preview, savedSha));
            },
            BuildFactory = (dir, _, _, _) =>
            {
                var publish = Directory.CreateDirectory(Path.Combine(dir, "artifacts", "app")).FullName;
                File.WriteAllText(Path.Combine(publish, "Greeting.exe"), "fixture");
                return Task.FromResult(new ProjectBuildExecution(0, ReadyExecution().Report, "", "{}", [], "", publish, attestation));
            },
            InstallerFactory = (dir, passedAttestation, _) =>
            {
                Equal(attestation, passedAttestation, "package the verified build");
                var output = Directory.CreateDirectory(Path.Combine(dir, "setup")).FullName;
                const string filename = "Greeting-Setup-x64.exe";
                var setup = Path.Combine(output, filename);
                File.WriteAllText(setup, "fixture");
                var receipt = new ProjectInstallerReceiptData("Greeting", "Greeting", "id", "short", "existing", "1.0.0", "1.0.0.0",
                    null, "dotnet-self-contained-win-x64", "artifacts/app", "Greeting.exe", "entry", "manifest", "attestation", 1,
                    "install", "Greeting.lnk", filename, 7, "setup", "NotSigned", false, "6", setup, output, "");
                return Task.FromResult(new ProjectInstallerExecution(0, ReadyExecution().Report with { Workflow = "project_installer" },
                    "", "{}", receipt));
            }
        };
    }

    private static EnvironmentCheckExecution ReadyExecution(string marker = "ready-marker") =>
        new(
            0,
            new WorkflowReportData(1, "source", "windows", "x86_64", "x86_64", "10.0.26300", "contain.environment.json",
            [new WorkflowCheckItem("platform", "passed", "requires Windows x64", "matched", "windows / x86_64", "windows / x86_64", null)]),
            "# Contain source\n" + marker,
            "{\"schema_version\":1,\"workflow\":\"source\",\"checks\":[]}");

    private static void True(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Environment check failed: " + reason);
    }

    private static void Equal<T>(T expected, T actual, string reason)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Environment check failed ({reason}): expected '{expected}', actual '{actual}'.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected exception " + typeof(TException).Name);
    }

    private static void WriteValidConsoleManifest(string projectDir, string buildName = "Compile console app")
    {
        var json = $$"""
            {
              "schema_version": 1,
              "target": "windows-x64",
              "tools": [
                {
                  "program": "dotnet",
                  "version": "10.0.404",
                  "version_args": ["--version"],
                  "remedy": "Install .NET SDK 10.0.404"
                }
              ],
              "required_variables": [],
              "pending": [],
              "artifact_dir": "artifacts/app",
              "artifacts": ["Hello.exe"],
              "build": [
                {
                  "name": "{{buildName}}",
                  "program": "dotnet",
                  "args": ["build", "Hello.csproj", "-c", "Release", "-o", "artifacts/app"],
                  "expected_exit": 0,
                  "timeout_ms": 120000
                }
              ],
              "verify": [
                {
                  "name": "Verify console output",
                  "program": "artifacts/app/Hello.exe",
                  "args": ["--verify"],
                  "expected_exit": 0,
                  "timeout_ms": 30000
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(projectDir, "contain.environment.json"), json);
    }

    private sealed class ControlledEnvironmentService : IEnvironmentCheckService
    {
        public int ApplyCalls { get; private set; }
        public (int, int, int, int) OperationCounts => (ApplyCalls, CheckCalls, BuildCalls, InstallerCalls);
        public int CheckCalls { get; private set; }
        public int BuildCalls { get; private set; }
        public int InstallerCalls { get; private set; }
        public Func<string, DotnetOnboardingPreviewData?> OnboardingInspectFactory { get; set; } = _ => null;
        public Func<string, string, CancellationToken, Task<DotnetOnboardingApplyOutcome>> OnboardingApplyFactory { get; set; } =
            (_, _, _) => throw new InvalidOperationException("Unexpected ApplyDotnetOnboardingAsync call in test");
        public Func<string, CancellationToken, Task<EnvironmentCheckExecution>> NextFactory { get; set; } =
            (_, _) => Task.FromResult(ReadyExecution());
        public Func<string, BuildManifestPreview, IProgress<BuildProgressUpdate>?, CancellationToken, Task<ProjectBuildExecution>> BuildFactory { get; set; } =
            (_, _, _, _) => Task.FromResult(new ProjectBuildExecution(0, ReadyExecution().Report, "", "{}", [], "", null));
        public Func<string, string, CancellationToken, Task<ProjectInstallerExecution>> InstallerFactory { get; set; } =
            (_, _, _) => Task.FromResult(new ProjectInstallerExecution(1, new WorkflowReportData(1, "project_installer", "windows", "x86_64", "x86_64", null, "contain.environment.json", []), "", "{}", null));

        public ProjectFolderInfo InspectProject(string projectPath) =>
            WorkflowCliEnvironmentService.InspectProjectDirectory(projectPath);

        public BuildManifestPreview InspectBuildPreview(string projectPath) =>
            WorkflowManifestReader.ReadPreview(projectPath);

        public Task<DotnetOnboardingPreviewData?> InspectDotnetOnboardingAsync(string projectPath, CancellationToken cancellationToken) =>
            Task.FromResult(OnboardingInspectFactory(WorkflowCliEnvironmentService.NormalizeDirectoryPath(projectPath)));

        public Task<DotnetOnboardingApplyOutcome> ApplyDotnetOnboardingAsync(
            string projectPath,
            string expectedFingerprintSha256,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return OnboardingApplyFactory(WorkflowCliEnvironmentService.NormalizeDirectoryPath(projectPath), expectedFingerprintSha256, cancellationToken);
        }

        public Task<EnvironmentCheckExecution> CheckAsync(string projectPath, CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            return NextFactory(WorkflowCliEnvironmentService.NormalizeDirectoryPath(projectPath), cancellationToken);
        }

        public Task<ProjectBuildExecution> BuildAsync(
            string projectPath,
            BuildManifestPreview expectedPreview,
            IProgress<BuildProgressUpdate>? progress = null,
            CancellationToken cancellationToken = default)
        {
            BuildCalls++;
            return BuildFactory(WorkflowCliEnvironmentService.NormalizeDirectoryPath(projectPath), expectedPreview, progress, cancellationToken);
        }

        public Task<ProjectInstallerExecution> PackageInstallerAsync(
            string projectPath,
            string buildAttestationJson,
            CancellationToken cancellationToken = default)
        {
            InstallerCalls++;
            return InstallerFactory(WorkflowCliEnvironmentService.NormalizeDirectoryPath(projectPath), buildAttestationJson, cancellationToken);
        }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "contain-env-model-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(root);
        public string Sub(string name)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
        public void Dispose()
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }
}
