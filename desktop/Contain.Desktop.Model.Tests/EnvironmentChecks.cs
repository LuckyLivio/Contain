using Contain.Desktop.Model;

namespace Contain.Desktop.Model.Tests;

internal static class EnvironmentChecks
{
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

        await Check("version mismatch prioritizes actionable summary and collapses satisfied items", async () =>
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
                        new WorkflowCheckItem("platform", "passed", "requires Windows x64", "matched", "windows / x86_64", "windows / x86_64", null),
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
            Equal("下一步：Install .NET SDK 10.0.999 x64", vm.ActionItems[0].RemedyLine, "remedy shown");
            Equal(2, vm.PassedItems.Count, "two satisfied items retained");
            True(!vm.PassedExpanded, "satisfied items collapsed by default");
            True(vm.CanRecheck && vm.CanToggleDetailedReport, "recheck and report toggle available");

            vm.ToggleDetailedReport();
            True(vm.ShowDetailedReport && vm.DetailedReportText.Contains("mismatch-demo", StringComparison.Ordinal), "detailed report bound to project");
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

        await Check("checker or manifest error is distinct from missing developer tools", async () =>
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
