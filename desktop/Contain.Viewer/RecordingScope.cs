using System.Text.Json;

namespace Contain.Viewer;

/// <summary>Saved backend mode is authoritative; empty watch roots alone say nothing.</summary>
internal static class RecordingScope
{
    internal static bool IsFollowInstaller(JsonElement summary) => summary.ValueKind == JsonValueKind.Object
        && summary.TryGetProperty("backend", out var backend) && backend.ValueKind == JsonValueKind.Object
        && backend.TryGetProperty("follow_installer", out var follow) && follow.ValueKind == JsonValueKind.Object;
}
