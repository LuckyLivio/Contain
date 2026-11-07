namespace Contain.Desktop.Controls.Tests;

/// <summary>Prevents an abandoned STA observation from dispatching input after its caller timed out.</summary>
internal sealed class KeyboardDispatchGate(TimeSpan deadline, Func<TimeSpan> elapsed)
{
    private int abandoned;
    private int started;

    internal void Abandon() => Interlocked.Exchange(ref abandoned, 1);

    internal void SendOnce(Action input)
    {
        EnsureCurrent();
        if (Interlocked.CompareExchange(ref started, 1, 0) != 0)
            throw new InvalidOperationException("A controlled keyboard gesture may only be dispatched once.");
        EnsureCurrent(); // Recheck immediately before input, after acquiring its single-use slot.
        input();
    }

    private void EnsureCurrent()
    {
        if (Volatile.Read(ref abandoned) != 0 || elapsed() >= deadline)
            throw new TimeoutException("Controlled keyboard dispatch was abandoned or reached its original deadline.");
    }

    internal static async Task VerifyContractAsync()
    {
        var elapsed = TimeSpan.Zero; var inputs = 0;
        var gate = new KeyboardDispatchGate(TimeSpan.FromSeconds(5), () => elapsed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedObservation = Task.Run(async () =>
        {
            entered.SetResult(); await release.Task;
            gate.SendOnce(() => inputs++);
        });
        await entered.Task;
        elapsed = TimeSpan.FromSeconds(5); gate.Abandon(); release.SetResult();
        try { await delayedObservation; throw new InvalidOperationException("Late observation sent keyboard input."); }
        catch (TimeoutException) { }
        if (inputs != 0) throw new InvalidOperationException("Abandoned observation must not send input.");
        var normal = new KeyboardDispatchGate(TimeSpan.FromSeconds(5), () => TimeSpan.Zero);
        normal.SendOnce(() => inputs++);
        try { normal.SendOnce(() => inputs++); throw new Exception("Repeated input was accepted."); }
        catch (InvalidOperationException) { }
        if (inputs != 1) throw new InvalidOperationException("Ready dispatch must send exactly one gesture.");
        var original = new System.Runtime.InteropServices.COMException("SYNTHETIC dispatch failure");
        try { new KeyboardDispatchGate(TimeSpan.FromSeconds(5), () => TimeSpan.Zero).SendOnce(() => throw original); }
        catch (System.Runtime.InteropServices.COMException error) when (ReferenceEquals(error, original)) { return; }
        throw new InvalidOperationException("Original dispatch exception was not preserved.");
    }
}
