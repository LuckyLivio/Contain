namespace Contain.Desktop.Tests;

/// <summary>The actual native driver's bounded state wait, with controllable clock for deterministic contract tests.</summary>
internal static class ObservedWait
{
    internal static async Task RunAsync(Func<Task<bool>> condition, Action ensureAlive, TimeSpan deadline,
        Func<TimeSpan> elapsed, Func<TimeSpan, Task> delay)
    {
        while (elapsed() < deadline)
        {
            ensureAlive();
            var remaining = deadline - elapsed();
            if (remaining <= TimeSpan.Zero) break;
            if (await condition().WaitAsync(remaining)) return;
            remaining = deadline - elapsed();
            if (remaining > TimeSpan.Zero) await delay(remaining < TimeSpan.FromMilliseconds(80) ? remaining : TimeSpan.FromMilliseconds(80));
        }
        throw new TimeoutException("Required desktop state was not reached within its declared deadline.");
    }

    internal static async Task VerifyContractAsync()
    {
        static async Task<int> Sequence(string?[] states, int? exitAt = null, Exception? unexpected = null)
        {
            var position = 0; var elapsed = TimeSpan.Zero;
            await RunAsync(() =>
            {
                if (unexpected is not null) throw unexpected;
                var state = states[Math.Min(position++, states.Length - 1)];
                return Task.FromResult(state == "session:expected");
            }, () => { if (position == exitAt) throw new DesktopDriver.ProductExitedException(9); }, TimeSpan.FromMilliseconds(240),
                () => elapsed, duration => { elapsed += duration; return Task.CompletedTask; });
            return position;
        }
        if (await Sequence([null, "session:old", "session:expected"]) != 3)
            throw new InvalidOperationException("Transient absence and old session must wait for exact identity.");
        foreach (var states in new[] { new string?[] { null }, ["session:old"] })
        {
            try { await Sequence(states); throw new InvalidOperationException("Persistent wrong state was accepted."); }
            catch (TimeoutException) { }
        }
        try { await Sequence([null], exitAt: 1); throw new InvalidOperationException("Exited product was accepted."); }
        catch (DesktopDriver.ProductExitedException) { }
        var original = new System.Runtime.InteropServices.COMException("SYNTHETIC unexpected UIA failure");
        try { await Sequence([null], unexpected: original); throw new InvalidOperationException("UIA exception swallowed."); }
        catch (System.Runtime.InteropServices.COMException error) when (ReferenceEquals(original, error)) { }
    }
}
