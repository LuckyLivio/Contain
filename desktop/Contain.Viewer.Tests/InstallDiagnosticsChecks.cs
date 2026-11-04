using System.Text;
using Contain.Viewer;

namespace Contain.Viewer.Tests;

internal static class InstallDiagnosticsChecks
{
    internal static async Task Run(Action<bool, string> require)
    {
        using var normal = new MemoryStream(Encoding.UTF8.GetBytes("SYNTHETIC 数据库写入失败：access denied\n"));
        var normalResult = await InstallDiagnosticOutput.ReadAsync(normal, 4096);
        require(normalResult.Text.Contains("access denied") && normalResult.Error is null && !normalResult.Truncated,
            "bounded diagnostics retain the actual error without inventing success or truncation");

        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes("SYNTHETIC specific database error " + new string('x', 32768)));
        var capped = await InstallDiagnosticOutput.ReadAsync(oversized, 64);
        require(capped.Text.StartsWith("SYNTHETIC specific database error") && capped.Text.Length == 64 && capped.Truncated && capped.Error is not null,
            "oversized diagnostics retain their useful prefix and remain an error");
        require(oversized.Position == oversized.Length, "oversized diagnostics drain the full child pipe after the retained prefix");

        var prefix = Encoding.UTF8.GetBytes("SYNTHETIC 具体原因：数据库只读\n");
        using var invalid = new MemoryStream([.. prefix, 0xff, .. Encoding.UTF8.GetBytes("trailing bytes")]);
        var malformed = await InstallDiagnosticOutput.ReadAsync(invalid, 4096);
        require(malformed.Text == Encoding.UTF8.GetString(prefix) && malformed.Error is not null,
            "invalid UTF-8 preserves the valid diagnostic prefix and cannot be success");
        require(invalid.Position == invalid.Length, "invalid diagnostic bytes do not leave the child pipe undrained");

        using var brokenDiagnostics = new FaultAfterPrefixStream(prefix);
        var broken = await InstallDiagnosticOutput.ReadAsync(brokenDiagnostics, 4096);
        require(broken.Text == Encoding.UTF8.GetString(prefix) && broken.Error?.Contains("SYNTHETIC pipe failure") == true && brokenDiagnostics.Disposed,
            "I/O failure retains prior diagnostics and closes the otherwise undrained read end");

        using var brokenStatus = new FaultAfterPrefixStream(Encoding.UTF8.GetBytes("SYNTHETIC status line\n"));
        using var reader = new StreamReader(brokenStatus, new UTF8Encoding(false, true));
        var lines = new List<string>();
        var statusError = await InstallClient.ReadStatusOutputAsync(reader, lines.Add);
        require(lines.SequenceEqual(["SYNTHETIC status line"]) && statusError?.Contains("SYNTHETIC pipe failure") == true && brokenStatus.Disposed,
            "a failed status reader closes its pipe while retaining only previously confirmed messages");

        var unfinished = new InstallRunResult(null, 1, "SYNTHETIC missing terminal result", "SYNTHETIC database error")
        { CollectorPid = 1234, LastConfirmedStage = "finalizing", DiagnosticsTruncated = true };
        require(!unfinished.Succeeded && unfinished.CollectorPid == 1234 && unfinished.LastConfirmedStage == "finalizing" && unfinished.DiagnosticsTruncated,
            "result metadata preserves collector identity and the last backend-confirmed phase without claiming completion");
    }

    private sealed class FaultAfterPrefixStream(byte[] prefix) : Stream
    {
        private bool delivered;
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (delivered) throw new IOException("SYNTHETIC pipe failure");
            delivered = true;
            prefix.CopyTo(buffer);
            return prefix.Length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
