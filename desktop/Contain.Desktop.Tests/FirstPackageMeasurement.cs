using System.Text.Json;

namespace Contain.Desktop.Tests;

internal sealed class FirstPackageMeasurement(DesktopDriver ui, string output)
{
    private readonly List<(string Kind, string Control, string Page)> inputs = [];
    private readonly List<string> decisions = [];
    private readonly List<string> pages = [];
    private readonly List<object> shots = [];
    private string page = "unknown";

    internal async Task StartAsync()
    {
        await ui.ResizeAsync(1120, 720);
        await ObservePageAsync();
    }

    private async Task ObservePageAsync()
    {
        page = await ui.ExistsAsync("EnvironmentPageTitle") ? "project" :
            await ui.ExistsAsync("RecordList") || await ui.ExistsAsync("LibrarySource") ? "records" : "other";
        if (pages.Count == 0 || pages[^1] != page) pages.Add(page);
    }

    private async Task RevealAsync(string id)
    {
        for (int step = 0; step < 30; step++)
        {
            if (!await ui.ScrollTowardAsync(id)) return;
            inputs.Add(("scroll_page", id, page));
            await Task.Delay(120);
        }
        throw new InvalidOperationException("Could not reveal the measured control within the owned page.");
    }

    internal async Task InvokeAsync(string controlId)
    {
        await ObservePageAsync();
        await RevealAsync(controlId);
        await ui.MeasuredClickAsync(controlId);
        inputs.Add(("mouse_click_button", controlId, page));
        decisions.Add(controlId);
    }

    internal async Task ChooseFolderAsync(string controlId, string project)
    {
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
        var shot = await ui.CaptureAsync(output, stage + ".png", "synthetic-project", maskHostPaths: true);
        if (shot.Width != 1120 || shot.Height != 720) throw new InvalidOperationException("Unexpected capture size.");
        shots.Add(new { stage, page, file = shot.Name, sha256 = shot.Sha256, width = shot.Width, height = shot.Height, dpi = shot.Dpi });
    }

    internal async Task SaveAsync(string status)
    {
        var report = new
        {
            schema_version = 1, protocol = "contain-first-package-measurement-1", status,
            scope = "From first ready product window through displayed Setup; excludes launch, close and installer acceptance.",
            workflow_sha = Environment.GetEnvironmentVariable("GITHUB_SHA"), run_id = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            product_source_sha = Environment.GetEnvironmentVariable("CANDIDATE_SOURCE_SHA"),
            viewport = new { width_px = 1120, height_px = 720, x = 30, y = 30 }, pages,
            page_transitions = Math.Max(0, pages.Count - 1), semantic_steps = decisions.Count, semantic_actions = decisions,
            pointer_clicks = inputs.Count(i => i.Kind.StartsWith("mouse_click_", StringComparison.Ordinal)),
            folder_entries = inputs.Count(i => i.Kind == "type_folder_path"),
            keyboard_operations = inputs.Count(i => i.Kind is "key_chord_select_all" or "type_folder_path" or "key_tab"),
            scroll_pages = inputs.Count(i => i.Kind == "scroll_page"),
            inputs = inputs.Select((i, index) => new { ordinal = index + 1, kind = i.Kind, control = i.Control, page = i.Page }),
            screenshots = shots
        };
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "first-package-measurement.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
