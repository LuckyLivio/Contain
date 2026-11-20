using System.Runtime.InteropServices;
using System.Text.Json;

namespace Contain.Desktop.Tests;

internal sealed class FirstPackageMeasurement(DesktopDriver ui, string output)
{
    private const int Width = 960, Height = 650;
    private readonly List<(string Kind, string Control, string Page)> inputs = [];
    private readonly List<string> decisions = [];
    private readonly List<string> pages = [];
    private readonly List<object> shots = [];
    private string page = "unknown";
    private bool verifiedFolder;
    private string operation = "start";
    private int observedWidth, observedHeight;

    internal async Task StartAsync()
    {
        operation = "resize";
        await ui.ResizeAsync(Width, Height);
        await ui.UntilAsync("measurement_viewport", "window", async () =>
        {
            (observedWidth, observedHeight) = await ui.WindowSizeAsync();
            return observedWidth == Width && observedHeight == Height;
        }, TimeSpan.FromSeconds(8));
        await ObservePageAsync();
    }

    private async Task ObservePageAsync()
    {
        page = await ui.ExistsAsync("EnvironmentPageTitle") ? "project" :
            await ui.ExistsAsync("RecordList") || await ui.ExistsAsync("LibrarySource") ? "records" : "other";
        if (pages.Count == 0 || pages[^1] != page) pages.Add(page);
    }

    private async Task RevealAsync(string id, bool countInput = true)
    {
        for (int step = 0; step < 30; step++)
        {
            if (!await ui.ScrollTowardAsync(id)) return;
            if (countInput) inputs.Add(("scroll_page", id, page));
            await Task.Delay(120);
        }
        throw new InvalidOperationException("Could not reveal the measured control within the owned page.");
    }

    internal async Task InvokeAsync(string controlId)
    {
        operation = "click_" + controlId;
        await ObservePageAsync();
        await RevealAsync(controlId);
        await ui.MeasuredClickAsync(controlId);
        inputs.Add(("mouse_click_button", controlId, page));
        decisions.Add(controlId);
    }

    internal async Task ChooseFolderAsync(string controlId, string project)
    {
        operation = "choose_folder";
        if (controlId != "PickProjectFolder") throw new ArgumentException("Expected project folder picker.");
        await ObservePageAsync();
        await RevealAsync(controlId);
        await ui.ChooseProjectFolderAsync(project, kind => inputs.Add((kind, controlId, page)));
        decisions.Add(controlId);
    }

    internal async Task ShotAsync(string stage, string? reveal = null)
    {
        if (stage is not ("first-launch" or "project-preview" or "package-complete" or "missing-inno"))
            throw new ArgumentException("Unknown comparison stage.");
        await ObservePageAsync();
        if (reveal != null) await RevealAsync(reveal);
        operation = stage + "_bounds";
        (observedWidth, observedHeight) = await ui.WindowSizeAsync();
        var shot = await ui.CaptureAsync(output, stage + ".png", "synthetic-project", maskHostPaths: true,
            progress: step => operation = stage + "_" + step);
        if (shot.Width != Width || shot.Height != Height) throw new AssertionException("measurement_wrong_size");
        shots.Add(new { stage, page, file = shot.Name, sha256 = shot.Sha256, width = shot.Width, height = shot.Height, dpi = shot.Dpi });
    }

    internal async Task VerifyOutputFolderAsync(string control, string folder)
    {
        operation = "verify_output_folder";
        if (ExplorerShows(folder)) throw new AssertionException("folder_already_open");
        await RevealAsync(control, countInput: false);
        await ui.MeasuredClickAsync(control);
        await ui.UntilAsync("verify_output_folder", "explorer_output_folder", () => Task.FromResult(ExplorerShows(folder)),
            TimeSpan.FromSeconds(40));
        verifiedFolder = true;
    }

    private static bool ExplorerShows(string folder)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!)!;
        try
        {
            foreach (dynamic window in ((dynamic)shell).Windows())
            {
                try
                {
                    if (Path.GetFileName((string)window.FullName).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                        && Uri.TryCreate((string)window.LocationURL, UriKind.Absolute, out Uri? uri) && uri.IsFile
                        && Path.GetFullPath(uri.LocalPath).TrimEnd(Path.DirectorySeparatorChar).Equals(folder, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (COMException) { /* Explorer can be navigating between locations. */ }
                finally { Marshal.ReleaseComObject(window); }
            }
            return false;
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    internal async Task SaveAsync(string status)
    {
        var report = new
        {
            schema_version = 1, protocol = "contain-first-package-measurement-1", status,
            scope = "From first ready product window through displayed Setup; excludes launch, close and installer acceptance.",
            workflow_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"), run_id = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            product_source_sha = Environment.GetEnvironmentVariable("CANDIDATE_SOURCE_SHA"),
            viewport = new { width_px = Width, height_px = Height, x = 30, y = 30 },
            observed_window = new { width_px = observedWidth, height_px = observedHeight }, last_operation = operation, pages,
            page_transitions = Math.Max(0, pages.Count - 1), semantic_steps = decisions.Count, semantic_actions = decisions,
            pointer_clicks = inputs.Count(i => i.Kind.StartsWith("mouse_click_", StringComparison.Ordinal)),
            folder_entries = inputs.Count(i => i.Kind == "type_folder_path"),
            keyboard_operations = inputs.Count(i => i.Kind is "key_chord_select_all" or "type_folder_path" or "key_tab"),
            scroll_pages = inputs.Count(i => i.Kind == "scroll_page"),
            inputs = inputs.Select((i, index) => new { ordinal = index + 1, kind = i.Kind, control = i.Control, page = i.Page }),
            after_setup = new { explorer_opened_exact_folder = verifiedFolder }, screenshots = shots
        };
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "first-package-measurement.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
