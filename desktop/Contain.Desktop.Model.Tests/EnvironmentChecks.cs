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

        await Check("direct manifest reader builds preview without CLI call and rejects invalid schemas or unsafe paths", () =>
        {
            using var temp = new TempDir();
            var project = temp.Sub("console-preview");
            WriteValidConsoleManifest(project);

            var service = new ControlledEnvironmentService();
            using var vm = new EnvironmentCheckViewModel(service);
            vm.SelectProject(project);
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
            return Task.CompletedTask;
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
            vm.SelectProject(inferredProj);
            await vm.CheckAsync();
            True(!vm.CanShowStartBuild && vm.HasBuildPrerequisiteBlocker, "inferred-only manifest cannot start build");
            True(vm.BuildPrerequisiteMessage.Contains("contain.environment.json", StringComparison.Ordinal), "explains explicit manifest needed");

            vm.SelectProject(explicitProj);
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
            vm.SelectProject(project);
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

            vm.SelectProject(project);
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
            vm.SelectProject(project);
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
            vm.SelectProject(projectA);
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

            vm.SelectProject(projectB);
            True(!vm.HasValidInstaller && !vm.HasInstallerFailure && string.IsNullOrEmpty(vm.GeneratedInstallerFileName),
                "switching to project B completely clears project A installer results");
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
        public int CheckCalls { get; private set; }
        public int BuildCalls { get; private set; }
        public int InstallerCalls { get; private set; }
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
