using Contain.Viewer;

namespace Contain.Desktop.Model;

/// <summary>The model queries the existing read-only CLI bridge, never SQLite directly.</summary>
public interface IRecordService
{
    Task<CoreResponse> ListAsync(string database, CancellationToken token);
    Task<CoreResponse> SummaryAsync(string database, string sessionId, CancellationToken token);
    Task<CoreResponse> DiffAsync(string database, string sessionId, CancellationToken token);
    Task<CoreResponse> HistoryAsync(string database, string sessionId, int limit, long offset, string? search, CancellationToken token);
    Task<CoreResponse> ExplainAsync(string database, string sessionId, string eventId, CancellationToken token);
    Task ExportPageAsync(string database, string sessionId, string? eventId, int pageSize, long offset, string target, CancellationToken token);
    Task<long> ExportMatchesAsync(string database, string sessionId, string search, string fingerprint, long count, string target, CancellationToken token);
}

public sealed class CoreRecordService(CoreClient client) : IRecordService
{
    public Task<CoreResponse> ListAsync(string database, CancellationToken token) => client.ListAsync(database, token);
    public Task<CoreResponse> SummaryAsync(string database, string sessionId, CancellationToken token) => client.SummaryAsync(database, sessionId, token);
    public Task<CoreResponse> DiffAsync(string database, string sessionId, CancellationToken token) => client.DiffAsync(database, sessionId, token);
    public Task<CoreResponse> HistoryAsync(string database, string sessionId, int limit, long offset, string? search, CancellationToken token) => client.HistoryAsync(database, sessionId, limit, offset, search, token);
    public Task<CoreResponse> ExplainAsync(string database, string sessionId, string eventId, CancellationToken token) => client.ExplainAsync(database, sessionId, eventId, token);
    public Task ExportPageAsync(string database, string sessionId, string? eventId, int pageSize, long offset, string target, CancellationToken token) =>
        // The existing bounded exporter includes synchronous HTML escaping between
        // asynchronous reads. Keep that work off the WPF dispatcher as well.
        Task.Run(() => ReportExporter.ExportAsync(client, database, sessionId, eventId, pageSize, offset, target, token), token);
    public Task<long> ExportMatchesAsync(string database, string sessionId, string search, string fingerprint, long count, string target, CancellationToken token) =>
        ReportExporter.ExportSearchAsync(client, database, sessionId, search, fingerprint, count, target, token);
}
