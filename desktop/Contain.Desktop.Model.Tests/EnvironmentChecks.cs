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

            True(vm.SelectProject(projectA), "select valid project");
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
            vm.SelectProject(project);
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
            vm.SelectProject(project);

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
            vm.SelectProject(projectA);
            await vm.CheckAsync();
            await vm.CheckAsync();
            Equal(0, vm.ResolvedItems.Count, "failed -> needs_confirmation must not mark resolved");

            await vm.CheckAsync();
            await vm.CheckAsync();
            Equal(EnvironmentOutcomeKind.Ready, vm.Outcome, "lowered requirement passes for new manifest");
            Equal(0, vm.ResolvedItems.Count, "lowering project requirement must not claim item was resolved");
            True(vm.ComparisonNote.Contains("要求已变更", StringComparison.Ordinal), "comparison reset note shown when manifest changes");
            True(!vm.Headline.Contains("已解决", StringComparison.Ordinal), "headline does not claim resolved when manifest changed");

            vm.SelectProject(projectB);
            await vm.CheckAsync();
            Equal(0, vm.ResolvedItems.Count, "switching projects starts fresh comparison");
            Equal("", vm.ComparisonNote, "project switch has clean comparison note");
        });

        await Check("checker failure during recheck preserves last valid result with timestamps and allows subsequent recovery", async () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("preserve-on-error");
            File.WriteAllText(Path.Combine(project, "contain.environment.json"), "{}");

            var timestamps = new Queue<DateTimeOffset>(
            [
                new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 11, 18, 9, 5, 30, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 11, 18, 9, 8, 45, TimeSpan.FromHours(8))
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
            vm.SelectProject(project);
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
            vm.SelectProject(project);
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
            vm.SelectProject(project);
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
            vm.SelectProject(projectA);
            var firstTask = vm.CheckAsync();
            True(vm.IsChecking, "first check active");

            vm.SelectProject(projectB);
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

        return passed;

        async Task Check(string name, Func<Task> action)
        {
            await action();
            passed.Add("environment:" + name);
        }
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

    private sealed class ControlledEnvironmentService : IEnvironmentCheckService
    {
        public int CheckCalls { get; private set; }
        public Func<string, CancellationToken, Task<EnvironmentCheckExecution>> NextFactory { get; set; } =
            (_, _) => Task.FromResult(ReadyExecution());

        public ProjectFolderInfo InspectProject(string projectPath) =>
            WorkflowCliEnvironmentService.InspectProjectDirectory(projectPath);

        public Task<EnvironmentCheckExecution> CheckAsync(string projectPath, CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            return NextFactory(WorkflowCliEnvironmentService.NormalizeDirectoryPath(projectPath), cancellationToken);
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
