namespace Contain.Viewer;

internal sealed record InstallDisplayTimes(ulong? Task, ulong? Stage, ulong? Step, ulong? ProgressAge);

/// <summary>Local display estimates only. Never changes backend state or progress stamps.</summary>
internal sealed class InstallProgressClock
{
    private InstallDisplayTimes? previous;
    private string? stage;
    private string? step;
    private ulong? progressStamp;

    internal InstallDisplayTimes Observe(InstallStatus value, ulong sinceReceipt, ulong localTaskElapsed, bool active)
    {
        static ulong Add(ulong left, ulong right) => ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
        static ulong? Floor(ulong? measured, ulong? floor) => measured is null ? null : Math.Max(measured.Value, floor ?? 0);
        var advance = active ? sinceReceipt : 0;
        var measuredAge = value.ElapsedMilliseconds is { } elapsed && value.LastProgressElapsedMilliseconds is { } last
            ? (ulong?)Add(elapsed - last, advance) : null;
        var result = new InstallDisplayTimes(
            Floor(value.ElapsedMilliseconds is { } task ? Math.Max(Add(task, advance), localTaskElapsed) : null, previous?.Task),
            Floor(value.StageElapsedMilliseconds is { } phase ? Add(phase, advance) : null, stage == value.Stage ? previous?.Stage : null),
            Floor(value.StepElapsedMilliseconds is { } work ? Add(work, advance) : null, step == value.Step ? previous?.Step : null),
            Floor(measuredAge, progressStamp == value.LastProgressElapsedMilliseconds ? previous?.ProgressAge : null));
        previous = result; stage = value.Stage; step = value.Step; progressStamp = value.LastProgressElapsedMilliseconds;
        return result;
    }
}
