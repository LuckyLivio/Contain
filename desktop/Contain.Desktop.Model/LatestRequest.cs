namespace Contain.Desktop.Model;

/// <summary>Cancellation is advisory; generation also rejects providers that finish after cancellation.</summary>
internal sealed class LatestRequest : IDisposable
{
    private CancellationTokenSource? cancellation;
    private long version;
    private bool disposed;

    internal readonly record struct Ticket(long Version, CancellationToken Token);
    internal long Version => version;

    internal Ticket Begin()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var previous = cancellation;
        cancellation = new CancellationTokenSource();
        var ticket = new Ticket(++version, cancellation.Token);
        previous?.Cancel();
        previous?.Dispose();
        return ticket;
    }

    internal bool IsCurrent(Ticket ticket) => !disposed && ticket.Version == version && !ticket.Token.IsCancellationRequested;

    internal void Invalidate()
    {
        if (disposed) return;
        ++version;
        var previous = cancellation;
        cancellation = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ++version;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }
}
