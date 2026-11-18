using Contain.Desktop.Model;

namespace Contain.Desktop.Model.Tests;

internal static class PackageChecks
{
    private const string SampleExpectedSha = "c2b1d2a199493b528adec65a8f6916feccc21bbd5ec5beae4db0b1e08555b745";
    private const string SampleOtherSha = "1111111111111111111111111111111111111111111111111111111111111111";

    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();

        await Check("selecting ZIP auto-discovers sibling package-spec.json, shows file pass vs launch untested, and collapses hashes", async () =>
        {
            using var temp = new TempDir();
            var dir = temp.Sub("delivery-dir");
            var zipPath = Path.Combine(dir, "contain-source-windows-x64.zip");
            var specPath = Path.Combine(dir, "package-spec.json");
            File.WriteAllText(zipPath, "zip-placeholder");
            File.WriteAllText(specPath, "{}");

            var service = new ControlledPackageService
            {
                NextFactory = (_, _, _) => Task.FromResult(PassedStaticExecution())
            };

            using var vm = new PackageCheckViewModel(service);
            True(await vm.SelectZipAsync(zipPath), "auto-discovered spec verification passes");
            Equal(1, service.VerifyCalls, "ran static verify once");
            Equal("contain-source-windows-x64.zip", vm.ZipFileName, "selected ZIP name");
            Equal("package-spec.json", vm.SpecFileName, "adopted sibling spec name");
            True(vm.SpecStatusLine.Contains("package-spec.json", StringComparison.Ordinal)
                && vm.SpecStatusLine.Contains("自动找到", StringComparison.Ordinal), "spec status shows auto-discovered name");
            Equal(PackageCheckState.Passed, vm.State, "state passed");
            Equal("文件校验通过", vm.FileStatusBadge, "file badge passed");
            Equal("启动未测试", vm.LaunchStatusBadge, "launch badge explicitly untested");
            True(vm.VerifiedSummaryItems.Any(static s => s.Contains("SHA-256", StringComparison.Ordinal))
                && vm.VerifiedSummaryItems.Any(static s => s.Contains("Contain.Viewer.exe", StringComparison.Ordinal))
                && vm.VerifiedSummaryItems.Any(static s => s.Contains("coreclr.dll", StringComparison.Ordinal))
                && vm.VerifiedSummaryItems.Any(static s => s.Contains("CONTENTS.json", StringComparison.Ordinal)),
                "explains concrete items actually checked");
            True(!vm.TechnicalDetailsExpanded, "detailed hashes collapsed by default");
            True(vm.TechnicalDetailsText.Contains(SampleExpectedSha, StringComparison.Ordinal), "technical expander holds exact SHA-256");

            vm.CancelSelection();
            Equal(PackageCheckState.Passed, vm.State, "cancel preserves current package result");
        });

        await Check("missing sibling package-spec.json never fabricates expected hash and continues once user picks spec", async () =>
        {
            using var temp = new TempDir();
            var spacedDir = temp.Sub("Isolated Dir With Spaces");
            var zipPath = Path.Combine(spacedDir, "contain-source-windows-x64.zip");
            File.WriteAllText(zipPath, "zip-placeholder");

            var specDir = temp.Sub("original-spec-dir");
            var specPath = Path.Combine(specDir, "package-spec.json");
            File.WriteAllText(specPath, "{}");

            var service = new ControlledPackageService
            {
                NextFactory = (_, _, _) => Task.FromResult(PassedStaticExecution())
            };

            using var vm = new PackageCheckViewModel(service);
            True(!await vm.SelectZipAsync(zipPath), "selecting ZIP without sibling spec stops at MissingSpec");
            Equal(0, service.VerifyCalls, "must never synthesize expected hash or invoke verify without a spec");
            Equal(PackageCheckState.MissingSpec, vm.State, "entered MissingSpec state");
            True(vm.MissingSpecExplanation.Contains("交付者", StringComparison.Ordinal), "one-sentence explanation shown");
            True(vm.CanPickSpec && vm.PickSpecButtonText == "选择校验清单", "pick spec entry offered");

            True(await vm.SelectSpecAsync(specPath), "selecting original spec runs verification and passes");
            Equal(1, service.VerifyCalls, "ran verify once after manual spec selection");
            Equal(PackageCheckState.Passed, vm.State, "passed with manually selected spec");
            Equal("文件校验通过", vm.FileStatusBadge, "file check passed");
            Equal("启动未测试", vm.LaunchStatusBadge, "launch remains untested");
        });

        await Check("mismatch distinguishes spec for another ZIP vs missing coreclr.dll and offers spec replacement", async () =>
        {
            using var temp = new TempDir();
            var dir = temp.Sub("mismatch-dir");
            var zipPath = Path.Combine(dir, "candidate.zip");
            var specPath = Path.Combine(dir, "package-spec.json");
            File.WriteAllText(zipPath, "zip-placeholder");
            File.WriteAllText(specPath, "{}");

            var mode = "other_zip";
            var service = new ControlledPackageService
            {
                NextFactory = (_, _, _) => Task.FromResult(mode == "other_zip"
                    ? new PackageVerificationExecution(
                        1,
                        new WorkflowReportData(1, "package", "windows", "x86_64", "x86_64", null, null,
                        [
                            new WorkflowCheckItem("package_identity", "failed", $"actual ZIP SHA-256 {SampleOtherSha}; expected {SampleExpectedSha}"),
                            new WorkflowCheckItem("static_entry", "passed", "Contain.Viewer.exe; EXE SHA-256 1234; Windows x64 PE header checked"),
                            new WorkflowCheckItem("static_file:coreclr.dll", "passed", "required package member"),
                            new WorkflowCheckItem("launch", "not_run", "static/identity gate failed; execution blocked")
                        ]),
                        "# package mismatch",
                        "{}")
                    : new PackageVerificationExecution(
                        1,
                        new WorkflowReportData(1, "package", "windows", "x86_64", "x86_64", null, null,
                        [
                            new WorkflowCheckItem("package_identity", "failed", $"actual ZIP SHA-256 {SampleOtherSha}; expected {SampleExpectedSha}"),
                            new WorkflowCheckItem("static_entry", "passed", "Contain.Viewer.exe; EXE SHA-256 1234; Windows x64 PE header checked"),
                            new WorkflowCheckItem("static_file:coreclr.dll", "failed", "required package member"),
                            new WorkflowCheckItem("static_contents", "failed", "existing Contain CONTENTS.json membership, size and SHA-256 contract"),
                            new WorkflowCheckItem("launch", "not_run", "static/identity gate failed; execution blocked")
                        ]),
                        "# missing coreclr",
                        "{}"))
            };

            using var vm = new PackageCheckViewModel(service);
            True(!await vm.SelectZipAsync(zipPath), "other-ZIP spec returns false");
            Equal(PackageCheckState.Failed, vm.State, "failed state");
            Equal("文件校验失败", vm.FileStatusBadge, "file badge failed");
            Equal("启动未测试", vm.LaunchStatusBadge, "launch badge untested");
            True(vm.Headline.Contains("这份清单对应另一个 ZIP", StringComparison.Ordinal), "identifies spec belongs to another ZIP");
            True(vm.CanPickSpec && vm.PickSpecButtonText == "更换校验清单", "offers spec replacement");

            mode = "missing_coreclr";
            True(!await vm.RecheckAsync(), "missing coreclr returns false");
            Equal("文件校验失败", vm.FileStatusBadge, "file badge failed");
            Equal("启动未测试", vm.LaunchStatusBadge, "launch badge untested");
            True(vm.Headline.Contains("身份不匹配", StringComparison.Ordinal)
                && vm.Headline.Contains("缺少 coreclr.dll", StringComparison.Ordinal),
                "headline surfaces both identity mismatch and missing coreclr.dll");
            True(vm.FailureItems.Any(static f => f.Title == "缺少 coreclr.dll")
                && vm.FailureItems.Any(static f => f.Title.Contains("SHA-256", StringComparison.Ordinal)),
                "failure list shows missing coreclr.dll and SHA-256 mismatch");
            True(!vm.TechnicalDetailsExpanded, "technical details remain collapsed by default on failure");
        });

        await Check("switching ZIPs discards superseded verification results and does not carry over previous manual spec", async () =>
        {
            using var temp = new TempDir();
            var dirA = temp.Sub("dir-a");
            var zipA = Path.Combine(dirA, "slow-a.zip");
            var specA = Path.Combine(dirA, "package-spec.json");
            File.WriteAllText(zipA, "a");
            File.WriteAllText(specA, "{}");

            var dirB = temp.Sub("dir-b-no-spec");
            var zipB = Path.Combine(dirB, "fast-b.zip");
            File.WriteAllText(zipB, "b");

            var slowA = new TaskCompletionSource<PackageVerificationExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new ControlledPackageService
            {
                NextFactory = (zip, _, _) => zip == zipA
                    ? slowA.Task
                    : Task.FromResult(PassedStaticExecution())
            };

            using var vm = new PackageCheckViewModel(service);
            var firstTask = vm.SelectZipAsync(zipA);
            True(vm.IsChecking, "slow-a verification running");

            var secondResult = await vm.SelectZipAsync(zipB);
            True(!secondResult, "fast-b has no sibling spec");
            Equal("fast-b.zip", vm.ZipFileName, "switched immediately to fast-b.zip");
            Equal(PackageCheckState.MissingSpec, vm.State, "fast-b shows MissingSpec and did not inherit specA");

            slowA.SetResult(PassedStaticExecution("stale-a-marker"));
            True(!await firstTask, "superseded slow-a result rejected");
            Equal("fast-b.zip", vm.ZipFileName, "page still bound to fast-b.zip");
            Equal(PackageCheckState.MissingSpec, vm.State, "stale-a pass did not overwrite fast-b");
            True(!vm.TechnicalDetailsText.Contains("stale-a-marker", StringComparison.Ordinal), "no stale report leaked");
        });

        await Check("package report parser enforces schema 1 package contract and distinguishes invalid spec JSON", async () =>
        {
            Throws<EnvironmentCheckException>(() => WorkflowCliPackageService.ParsePackageReport("{\"schema_version\":1,\"workflow\":\"source\",\"checks\":[]}"));
            Throws<EnvironmentCheckException>(() => WorkflowCliPackageService.ParsePackageReport("{\"schema_version\":2,\"workflow\":\"package\",\"checks\":[]}"));

            using var temp = new TempDir();
            var dir = temp.Sub("bad-spec");
            var zip = Path.Combine(dir, "pkg.zip");
            var spec = Path.Combine(dir, "package-spec.json");
            File.WriteAllText(zip, "z");
            File.WriteAllText(spec, "{invalid");

            var service = new ControlledPackageService
            {
                NextFactory = (_, _, _) => Task.FromException<PackageVerificationExecution>(
                    new EnvironmentCheckException("spec_or_report_invalid", "无法读取或解析所选的校验清单。", "expected value at line 1"))
            };

            using var vm = new PackageCheckViewModel(service);
            True(!await vm.SelectZipAsync(zip), "invalid spec returns false");
            Equal(PackageCheckState.CheckerOrSpecError, vm.State, "distinct spec/checker error state");
            True(vm.CanPickSpec, "allows replacing invalid spec");
        });

        return passed;

        async Task Check(string name, Func<Task> action)
        {
            await action();
            passed.Add("package:" + name);
        }
    }

    private static PackageVerificationExecution PassedStaticExecution(string marker = "ok-marker") =>
        new(
            2,
            new WorkflowReportData(1, "package", "windows", "x86_64", "x86_64", "10.0.26300", null,
            [
                new WorkflowCheckItem("package_identity", "passed", $"actual ZIP SHA-256 {SampleExpectedSha}; expected {SampleExpectedSha}"),
                new WorkflowCheckItem("static_entry", "passed", "Contain.Viewer.exe; EXE SHA-256 698d192e; Windows x64 PE header checked (not a loader test)"),
                new WorkflowCheckItem("static_file:coreclr.dll", "passed", "required package member"),
                new WorkflowCheckItem("static_file:LICENSE", "passed", "required package member"),
                new WorkflowCheckItem("static_test:Core exact version", "passed", "test executable must be present in package"),
                new WorkflowCheckItem("static_contents", "passed", "existing Contain CONTENTS.json membership, size and SHA-256 contract"),
                new WorkflowCheckItem("launch", "not_run", "--run not requested"),
                new WorkflowCheckItem("Core exact version", "not_run", "--run not requested")
            ]),
            "# Contain package\n" + marker,
            "{\"schema_version\":1,\"workflow\":\"package\",\"checks\":[]}");

    private static void True(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Package check failed: " + reason);
    }

    private static void Equal<T>(T expected, T actual, string reason)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Package check failed ({reason}): expected '{expected}', actual '{actual}'.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected exception " + typeof(TException).Name);
    }

    private sealed class ControlledPackageService : IPackageCheckService
    {
        public int VerifyCalls { get; private set; }
        public Func<string, string, CancellationToken, Task<PackageVerificationExecution>> NextFactory { get; set; } =
            (_, _, _) => Task.FromResult(PassedStaticExecution());

        public PackageZipSelection InspectZip(string zipPath) =>
            WorkflowCliPackageService.InspectZipFile(zipPath);

        public PackageSpecSelection InspectSpec(string specPath, bool isAutoDiscovered) =>
            WorkflowCliPackageService.InspectSpecFile(specPath, isAutoDiscovered);

        public Task<PackageVerificationExecution> VerifyStaticAsync(
            string zipPath,
            string specPath,
            CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            return NextFactory(Path.GetFullPath(zipPath), Path.GetFullPath(specPath), cancellationToken);
        }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "contain-pkg-model-test-" + Guid.NewGuid().ToString("N"));
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
