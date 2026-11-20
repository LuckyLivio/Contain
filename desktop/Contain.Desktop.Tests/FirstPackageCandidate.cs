using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Contain.Desktop.Tests;

internal static class FirstPackageCandidate
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8 || args[6] is not ("Wpf" or "MissingInno"))
            throw new ArgumentException("Expected install, project, assembly, process ID, UI receipt, evidence directory, scenario and work directory.");
        var install = Path.GetFullPath(args[0]);
        var project = Path.GetFullPath(args[1]);
        var assembly = args[2];
        var pid = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var receipt = Path.GetFullPath(args[4]);
        var evidence = Path.GetFullPath(args[5]);
        var missing = args[6] == "MissingInno";
        var work = Path.GetFullPath(args[7]);
        var checks = new Dictionary<string, string>
        {
            ["launch_product"] = "not_run", ["select_project"] = "not_run", ["confirm_configuration"] = "not_run",
            ["check_environment"] = "not_run", ["build_project"] = "not_run", ["package_project"] = "not_run"
        };
        var stage = "launch_product";
        var control = "window";
        var status = "failed";
        var reason = "";
        var setupRelative = "";
        var projectType = "";
        var openFolder = false;
        var fixedInno = false;
        var chooserCount = 0;
        string? compilerSha = null;
        DesktopDriver? driver = null;
        FirstPackageMeasurement? measure = null;
        try
        {
            using var ui = driver = new DesktopDriver(pid, Path.Combine(install, "Contain.Viewer.exe"));
            measure = new FirstPackageMeasurement(ui, evidence);
            await measure.StartAsync();
            await TextEquals("EnvironmentPageTitle", "打包项目", 40);
            control = "PickProjectFolder";
            await ui.UntilAsync(stage, control, () => ui.VisibleInOwnedViewportAsync(control));
            await measure.ShotAsync("first-launch");
            checks[stage] = "passed";

            stage = "select_project";
            await measure.ChooseFolderAsync("PickProjectFolder", project);
            chooserCount = ui.ChooserInteractions;
            var action = missing ? "重新检测打包工具" : "保存配置并检查";
            await TextEquals("FirstPackageAction", action, 90);
            projectType = missing ? "控制台" : "WPF";
            await TextContains("FirstPackageProjectType", projectType);
            control = "FirstPackageProjectName";
            var projectStem = Path.GetFileName(project);
            var namePattern = @"(?<![a-zA-Z0-9_])" + Regex.Escape(projectStem) + @"(?![a-zA-Z0-9_])";
            await ui.UntilAsync(stage, control, async () => Regex.IsMatch(await ui.TryTextAsync(control) ?? "", namePattern));
            await TextContains("FirstPackageEntry", assembly + ".exe");
            await TextPresent("FirstPackageToolSummary");
            await TextPresent("FirstPackageNextStep");
            Require(await ui.VisibleInOwnedViewportAsync("FirstPackageAction") &&
                !await ui.VisibleAsync("FirstPackageProposedCommand"), "primary_or_collapsed_details");
            var manifest = Path.Combine(project, "contain.environment.json");
            Require(chooserCount == 1 && !File.Exists(manifest) && !Directory.Exists(Path.Combine(project, "target")),
                "selection_ran_or_wrote_project");
            await measure.ShotAsync("project-preview");
            if (missing)
            {
                await TextContains("FirstPackageToolSummary", "Inno Setup 6：未检测到");
                await TextContains("FirstPackageNextStep", "安装 Inno Setup 6");
                await measure.ShotAsync("missing-inno");
                compilerSha = RestoreRealCompiler(work);
                await measure.InvokeAsync("FirstPackageAction");
                await TextEquals("FirstPackageAction", "保存配置并检查", 90);
                Require(!File.Exists(manifest), "tool_recheck_wrote_project");
                await TextContains("FirstPackageToolSummary", "Inno Setup 6：已检测到");
                fixedInno = true;
            }
            checks[stage] = "passed";

            stage = "confirm_configuration";
            await TextContains("FirstPackageNextStep", "contain.environment.json");
            await measure.InvokeAsync("FirstPackageAction");
            await ui.UntilAsync(stage, "manifest_written", () => Task.FromResult(File.Exists(manifest)));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
            Require(json.RootElement.GetProperty("installer").GetProperty("entry").GetString() == assembly + ".exe",
                "manifest_entry_mismatch");
            checks[stage] = "passed";
            stage = "check_environment";
            await TextEquals("FirstPackageAction", "构建并生成安装包", 150);
            await TextContains("FirstPackageNextStep", "将构建此项目");
            await TextContains("FirstPackageNextStep", "请确认信任此项目");
            Require(!Directory.Exists(Path.Combine(project, "target", "workflow", "publish")), "build_ran_before_consent");
            checks[stage] = "passed";

            stage = "build_project";
            await measure.InvokeAsync("FirstPackageAction");
            var publishedEntry = Path.Combine(project, "target", "workflow", "publish", "win-x64", assembly + ".exe");
            await ui.UntilAsync(stage, "published_entry", () => Task.FromResult(File.Exists(publishedEntry)), TimeSpan.FromMinutes(6));
            checks[stage] = "passed";

            stage = "package_project";
            const string openFolderButton = "FirstPackageOpenFolder";
            await TextEquals(openFolderButton, "打开安装包文件夹", 360);
            var setupName = assembly + "-Setup-x64.exe";
            await TextEquals("FirstPackageResultName", setupName, 30);
            await measure.ShotAsync("package-complete");
            Require(await ui.VisibleInOwnedViewportAsync(openFolderButton) && await ui.VisibleInOwnedViewportAsync("FirstPackageResultName"),
                "success_action_or_filename_outside_viewport");
            Require(await ui.CollapsedAsync("FirstPackageDetails") && !await ui.VisibleAsync("FirstPackageResultPath")
                && !await ui.VisibleAsync("FirstPackageAction") && !await ui.VisibleAsync("FirstPackageNextStep"),
                "success_details_or_old_action_visible");

            var outputRoot = Path.Combine(project, "target", "workflow", "installers");
            var outputs = Directory.GetDirectories(outputRoot, "installer-*");
            Require(outputs.Length == 1, "expected_one_output_directory");
            var installer = Path.Combine(outputs[0], setupName);
            using var generatedReceipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputs[0], "installer-receipt.json")));
            var generated = generatedReceipt.RootElement;
            Require(generated.GetProperty("installer_file").GetString() == setupName && File.Exists(installer), "generated_setup_mismatch");
            string digest;
            using (var artifact = File.OpenRead(installer))
                digest = Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant();
            Require(generated.GetProperty("installer_sha256").GetString() == digest, "generated_setup_digest");
            setupRelative = Path.GetRelativePath(project, installer);

            // Folder opening is verified after the timed path has produced its first installer.
            control = openFolderButton;
            await measure.VerifyOutputFolderAsync(control, outputs[0], alreadyVisible: true);
            openFolder = true;
            control = "FirstPackageDetails";
            await ui.ExpandAsync(control);
            await TextEquals("FirstPackageResultPath", installer, 30);
            await TextContains("FirstPackageBuildCommand", "dotnet publish");
            await TextContains("FirstPackageReceipt", digest);
            await ui.CloseAsync();
            Require(ui.HasExited && ui.ExitCode == 0, "installed_viewer_close_failed");
            checks[stage] = "passed";
            status = "passed";

            async Task TextEquals(string id, string expected, int seconds)
            {
                control = id;
                await ui.UntilAsync(stage, id, async () => (await ui.TryTextAsync(id))?.Trim() == expected &&
                    (id is not ("FirstPackageAction" or "FirstPackageOpenFolder") || await ui.EnabledAsync(id)),
                    TimeSpan.FromSeconds(seconds));
            }
            async Task TextContains(string id, string expected)
            {
                control = id;
                await ui.UntilAsync(stage, id, async () => (await ui.TryTextAsync(id))?.Contains(expected, StringComparison.Ordinal) == true);
            }
            async Task TextPresent(string id)
            {
                control = id;
                await ui.UntilAsync(stage, id, async () => !string.IsNullOrWhiteSpace(await ui.TryTextAsync(id)));
            }
        }
        catch (Exception e)
        {
            reason = e is AssertionException ? e.Message : e.GetType().Name;
            checks[stage] = "failed";
        }
        if (measure != null) await measure.SaveAsync(status);
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            status, stage, control, reason, driver_stage = driver?.Stage, checks, product_pid = pid,
            chooser_count = chooserCount, installer_relative = setupRelative, project_type = projectType,
            missing_inno_recovered = fixedInno, restored_compiler_sha256 = compilerSha, explorer_opened_exact_folder = openFolder
        }));
        return status == "passed" ? 0 : 1;
    }

    private static string RestoreRealCompiler(string work)
    {
        Require(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "recovery_requires_hosted_runner");
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Require(work.StartsWith(temp, StringComparison.OrdinalIgnoreCase), "compiler_target_outside_work");
        var real = Path.GetFullPath(Environment.GetEnvironmentVariable("CONTAIN_TEST_ISCC_PATH")!);
        var source = Path.GetDirectoryName(real)!;
        var dest = Path.Combine(work, "isolated-inno");
        Require(File.Exists(real) && Path.GetFileName(real).Equals("ISCC.exe", StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(dest) && !dest.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "invalid_compiler_fixture");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        var digest = SHA256.HashData(File.ReadAllBytes(real));
        Require(SHA256.HashData(File.ReadAllBytes(Path.Combine(dest, "ISCC.exe"))).SequenceEqual(digest), "restored_compiler_digest");
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void Require(bool value, string reason)
    {
        if (!value) throw new AssertionException(reason);
    }
}
