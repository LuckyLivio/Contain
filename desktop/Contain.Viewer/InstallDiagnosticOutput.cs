using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("Contain.Viewer.Tests")]

namespace Contain.Viewer;

internal sealed record InstallDiagnosticRead(string Text, string? Error, bool Truncated);

/// <summary>Retain a bounded stderr prefix while draining the entire child pipe.</summary>
internal static class InstallDiagnosticOutput
{
    internal static async Task<InstallDiagnosticRead> ReadAsync(Stream stream, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var retained = new MemoryStream();
        var buffer = new byte[8192];
        var truncated = false;
        string? error = null;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer);
                if (count == 0) break;
                var keep = Math.Min(count, limit - (int)retained.Length);
                if (keep > 0) retained.Write(buffer, 0, keep);
                truncated |= keep < count;
            }
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            // Without a reader, leaving this pipe open could block the collector.
            stream.Dispose();
            error = "CLI 标准错误读取失败；保留此前诊断：" + failure.Message;
        }
        var bytes = retained.ToArray();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException failure)
        {
            // Do not replace a useful operating-system/database error with only a
            // generic decoder exception. Invalid output still prevents success.
            text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Max(0, failure.Index)));
            error ??= "CLI 标准错误含无效 UTF-8；保留此前诊断。";
        }
        if (truncated) error ??= "CLI 标准错误超过读取上限；仅保留诊断前缀，未视为成功结果。";
        return new(text, error, truncated);
    }
}
