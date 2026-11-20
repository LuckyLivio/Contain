using System.Text.Json;

namespace Contain.Desktop.Tests;

internal static class InstalledWpfWorkflow
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (5 or 6)) throw new ArgumentException("Expected install, project, assembly, installed PID and receipt.");
        var install = Path.GetFullPath(args[0]);
        var project = Path.GetFullPath(args[1]);
        var assembly = args[2];
        var installedPid = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var receipt = Path.GetFullPath(args[4]);
        var checks = new Dictionary<string, string>
        {
            ["launch_product"] = "not_run", ["select_project"] = "not_run", ["confirm_configuration"] = "not_run",
            ["check_environment"] = "not_run", ["build_project"] = "not_run", ["package_project"] = "not_run"
        };
        var stage = "launch_product";
        var control = "window";
        var reason = "";
        var status = "failed";
        var setupRelative = "";
        var productPid = 0;
        var chooserCount = 0;
        DesktopDriver? driver = null;
        FirstPackageMeasurement? measure = null;
        try
        {
            using var ui = driver = new DesktopDriver(installedPid, Path.Combine(install, "Contain.Viewer.exe"));
            productPid = ui.ProcessId;
            await ui.ReadyAsync();
            if (args.Length == 6)
            {
                measure = new FirstPackageMeasurement(ui, Path.GetFullPath(args[5]));
                await measure.StartAsync();
                await measure.ShotAsync("first-launch");
            }
            checks[stage] = "passed";

            stage = "select_project";
            await Invoke("CheckProjectEnvironment");
            control = "PickProjectFolder";
            if (measure != null) await measure.ChooseFolderAsync(control, project);
            else await ui.ChooseProjectFolderAsync(project);
            chooserCount = ui.ChooserInteractions;
            await TextEquals("OnboardingHeadline", $"已识别 {assembly} · WPF 桌面应用", 60);
            Require(chooserCount == 1 && Path.GetFullPath((await ui.TextAsync("ProjectPathInput")).Trim()) == project,
                "project_selection_mismatch");
            var manifestFile = Path.Combine(project, "contain.environment.json");
            Require(!File.Exists(manifestFile), "preview_wrote_manifest");
            if (measure != null) await measure.ShotAsync("project-preview");
            checks[stage] = "passed";

            stage = "confirm_configuration";
            await Invoke("ConfirmOnboardingButton");
            await Enabled("StartEnvironmentCheck", 30);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile));
            Require(manifest.RootElement.GetProperty("installer").GetProperty("entry").GetString() == assembly + ".exe",
                "manifest_entry_mismatch");
            checks[stage] = "passed";

            stage = "check_environment";
            await Invoke("StartEnvironmentCheck");
            await TextEquals("EnvironmentOutcomeBadge", "环境已满足", 150);
            checks[stage] = "passed";

            stage = "build_project";
            await Invoke("StartProjectBuild");
            await TextEquals("BuildOutcomeBadge", "构建通过", 360);
            checks[stage] = "passed";

            stage = "package_project";
            await Invoke("GenerateInstallerButton");
            var setupName = assembly + "-Setup-x64.exe";
            await TextEquals("GeneratedInstallerFileName", setupName, 360);
            control = "GeneratedInstallerPathText";
            var installer = Path.GetFullPath((await ui.TextAsync(control)).Trim());
            var outputRoot = Path.GetFullPath(Path.Combine(project, "target", "workflow", "installers")) + Path.DirectorySeparatorChar;
            Require(installer.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(installer) == setupName && File.Exists(installer), "displayed_installer_mismatch");
            setupRelative = Path.GetRelativePath(project, installer);
            if (measure != null) await measure.ShotAsync("package-complete", "GeneratedInstallerFileName");
            control = "window_close";
            await ui.CloseAsync();
            Require(ui.HasExited && ui.ExitCode == 0, "installed_product_close_failed");
            checks[stage] = "passed";
            status = "passed";

            async Task Invoke(string id)
            {
                await Enabled(id, 40);
                if (measure != null) await measure.InvokeAsync(id);
                else await ui.InvokeAsync(id);
            }
            async Task Enabled(string id, int seconds)
            {
                control = id;
                await ui.UntilAsync(stage, id, async () => await ui.ExistsAsync(id) && await ui.EnabledAsync(id),
                    TimeSpan.FromSeconds(seconds));
            }
            async Task TextEquals(string id, string expected, int seconds)
            {
                control = id;
                await ui.UntilAsync(stage, id, async () =>
                {
                    var value = (await ui.TryTextAsync(id))?.Trim();
                    if (id == "EnvironmentOutcomeBadge" && value is "检查出缺项 / 版本不匹配" or "检查程序或配置出错" or "信息不足 · 含待确认项")
                        throw new AssertionException("environment_check_not_ready");
                    if (id == "BuildOutcomeBadge" && value == "构建未通过") throw new AssertionException("project_build_failed");
                    if (id == "GeneratedInstallerFileName" && !string.IsNullOrWhiteSpace(await ui.TryTextAsync("InstallerFailureTitle")))
                        throw new AssertionException("project_packaging_failed");
                    return value == expected;
                }, TimeSpan.FromSeconds(seconds));
            }
        }
        catch (Exception error)
        {
            reason = error is AssertionException ? error.Message : error.GetType().Name;
            checks[stage] = "failed";
        }
        if (measure != null) await measure.SaveAsync(status);
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            status, stage, control, reason, driver_stage = driver?.Stage, checks, product_pid = productPid, chooser_count = chooserCount,
            installer_relative = setupRelative
        }));
        return status == "passed" ? 0 : 1;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new AssertionException(reason);
    }
}
