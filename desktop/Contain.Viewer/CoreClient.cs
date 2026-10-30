using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Contain.Viewer;

/// <summary>Only the five record-reading commands are allowed; no shell or collector is launched.</summary>
public sealed class CoreClient
{
    private const int MaxOutputBytes = 64 * 1024 * 1024;
    private const int MaxErrorBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TimeSpan commandTimeout;
    private readonly SemaphoreSlim versionLock = new(1, 1);
    private bool versionChecked;

    public string ExecutablePath { get; }

    public CoreClient(string executablePath, TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = Path.GetFullPath(executablePath);
        this.commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(60);
        if (this.commandTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(commandTimeout));
    }

    public Task<CoreResponse> ListAsync(string database, CancellationToken cancellationToken = default) =>
        QueryAsync(database, "list", "list_details", ["--details", "--json"], cancellationToken);

    public async Task<CoreResponse> SummaryAsync(string database, string id, CancellationToken cancellationToken = default)
    {
        var response = await QueryAsync(database, "inspect", "inspect_summary", ["--summary", "--json", "--", Identity(id)], cancellationToken);
        EnsureIdentity(response.Data.GetProperty("id").GetString(), id);
        return response;
    }

    public async Task<CoreResponse> DiffAsync(string database, string id, CancellationToken cancellationToken = default)
    {
        var response = await QueryAsync(database, "diff", "diff", ["--state-only", "--json", "--", Identity(id)], cancellationToken);
        EnsureIdentity(response.Data.GetProperty("app_id").GetString(), id);
        return response;
    }

    public async Task<CoreResponse> HistoryAsync(string database, string id, int limit, long offset, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var response = await QueryAsync(database, "history", "history", ["--json", "--limit",
            limit.ToString(CultureInfo.InvariantCulture), "--offset", offset.ToString(CultureInfo.InvariantCulture), "--", Identity(id)], cancellationToken);
        EnsureIdentity(response.Data.GetProperty("app_id").GetString(), id);
        if (response.Data.GetProperty("events").GetArrayLength() > limit)
            throw new CoreClientException("page_contract", "CLI 返回的事件数量超过请求页大小。");
        return response;
    }

    public async Task<CoreResponse> ExplainAsync(string database, string eventId, CancellationToken cancellationToken = default)
    {
        var response = await QueryAsync(database, "explain", "explain", ["--json", "--", Identity(eventId)], cancellationToken);
        EnsureIdentity(response.Data.GetProperty("event").GetProperty("id").GetString(), eventId);
        return response;
    }

    // The original doctor probes providers and starts ETW. Viewing never calls it.
    public Task<CoreResponse> DoctorAsync(string database, CancellationToken cancellationToken = default) =>
        Task.FromException<CoreResponse>(new CoreClientException("unsupported_read_only_command",
            "只读查看器不运行 doctor；该命令会探测并启动跟踪源。"));

    private async Task<CoreResponse> QueryAsync(string database, string command, string kind,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var path = ExistingDatabase(database);
        await EnsureVersionAsync(cancellationToken);
        var all = new List<string> { "--read-only", "--db", path, command };
        all.AddRange(arguments);
        var output = await RunAsync(all, cancellationToken);
        return CoreResponse.Parse(output, kind);
    }

    private async Task EnsureVersionAsync(CancellationToken cancellationToken)
    {
        await versionLock.WaitAsync(cancellationToken);
        try
        {
            if (versionChecked) return;
            var version = (await RunAsync(["--version"], cancellationToken)).Trim();
            if (version != "contain 0.3.1")
                throw new CoreClientException("cli_version", "CLI 版本不匹配，请使用桌面包附带的 contain.exe。", diagnostic: version);
            versionChecked = true;
        }
        finally { versionLock.Release(); }
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(ExecutablePath))
            throw new CoreClientException("cli_missing", "找不到桌面包附带的 contain.exe，请完整解压试用包。");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
                StandardOutputEncoding = StrictUtf8,
                StandardErrorEncoding = StrictUtf8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(commandTimeout);
        try
        {
            if (!process.Start()) throw new CoreClientException("cli_start", "无法启动 contain.exe。");
            var stdout = ReadChildAsync(process.StandardOutput.BaseStream, MaxOutputBytes);
            var stderr = ReadChildAsync(process.StandardError.BaseStream, MaxErrorBytes);
            var exited = process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr, exited);
            if (process.ExitCode != 0)
                throw new CoreClientException("cli_exit", $"无法读取记录（CLI 退出码 {process.ExitCode}）。\n{(await stderr).Trim()}",
                    process.ExitCode, await stderr);
            return await stdout;

            async Task<string> ReadChildAsync(Stream stream, int maximumBytes)
            {
                try { return await ReadLimitedAsync(stream, maximumBytes, deadline.Token); }
                catch
                {
                    // Refusing oversized output must unblock its writer at once.
                    Kill(process);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new CoreClientException("cli_timeout", "读取超时；操作已停止，数据库未被修改。");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (CoreClientException)
        {
            Kill(process);
            throw;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or DecoderFallbackException or InvalidOperationException)
        {
            Kill(process);
            throw new CoreClientException("cli_process", "无法运行或读取 contain.exe，请检查文件与访问权限。", diagnostic: error.Message, inner: error);
        }
    }

    private static async Task<string> ReadLimitedAsync(Stream source, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            if (output.Length + count > maximumBytes)
                throw new CoreClientException("output_limit", "CLI 输出超过查看器读取上限；未截断为成功结果。请缩小查询范围。");
            output.Write(buffer, 0, count);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static string ExistingDatabase(string database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        var full = Path.GetFullPath(database);
        if (!File.Exists(full))
            throw new CoreClientException("database_missing", "记录数据库不存在或不可访问；不会新建空数据库。");
        return full;
    }

    private static string Identity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\0'))
            throw new CoreClientException("invalid_identity", "记录或事件 ID 无效。");
        return value;
    }

    private static void EnsureIdentity(string? actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new CoreClientException("identity_mismatch", "CLI 返回的记录或事件 ID 与所选对象不一致。");
    }
}
