using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Contain.Viewer;

public sealed record InstallRequest(string Installer, string Name, string WatchRoot, string Database,
    IReadOnlyList<string> Arguments, string? RegistryKey = null, bool SnapshotOnly = false,
    ulong SettleMilliseconds = 1500, ulong MaxDrainMilliseconds = 10000)
{
    public InstallRequest Validate()
    {
        if (!File.Exists(Installer) || !string.Equals(Path.GetExtension(Installer), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择已存在的 .exe 安装器。当前不支持直接启动 .msi、脚本或服务代理安装。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Contains('\0')) throw new InvalidDataException("请输入记录名称。");
        if (!Directory.Exists(WatchRoot)) throw new InvalidDataException("观察目录必须已存在；请自行选择实际需要观察的目录。不会自动创建软件目标目录。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WatchRoot));
        var database = Path.GetFullPath(Database);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (string.Equals(root, database, StringComparison.OrdinalIgnoreCase) || database.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("数据库及 WAL/SHM 必须位于观察目录之外。");
        if (!Directory.Exists(Path.GetDirectoryName(database)) || Directory.Exists(database)) throw new InvalidDataException("数据库父目录必须已存在，保存位置必须是文件。");
        if (Arguments.Any(x => x.Contains('\0'))) throw new InvalidDataException("安装器参数不能含 NUL 字符。");
        if (SettleMilliseconds > MaxDrainMilliseconds || MaxDrainMilliseconds > 600000) throw new InvalidDataException("高级等待参数无效；最长整理等待上限为 600000 毫秒。");
        return this with { Installer = Path.GetFullPath(Installer), WatchRoot = root, Database = database };
    }
}

public sealed record InstallRunResult(InstallStatus? LastStatus, int CollectorExitCode, string? ProtocolError, string Diagnostics)
{
    public bool Succeeded => CollectorExitCode == 0 && ProtocolError is null && LastStatus is { Stage: "finished", CaptureFinished: true, DataSaved: true };
}

/// <summary>Explicit capture path, separate from the viewer's read-only query client.</summary>
public sealed class InstallClient(string executablePath)
{
    private readonly string executable = Path.GetFullPath(executablePath);
    private int active;
    public bool IsActive => Volatile.Read(ref active) != 0;

    public static IReadOnlyList<string> BuildArguments(InstallRequest request)
    {
        var valid = request.Validate();
        var args = new List<string> { "--db", valid.Database, "install", "--status-json", "--name=" + valid.Name,
            "--watch", valid.WatchRoot, "--settle-ms", valid.SettleMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-drain-ms", valid.MaxDrainMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        args.Add(valid.SnapshotOnly ? "--no-etw" : "--require-etw");
        if (!string.IsNullOrEmpty(valid.RegistryKey)) args.Add("--registry-key=" + valid.RegistryKey);
        args.Add(valid.Installer);
        args.Add("--");
        args.AddRange(valid.Arguments);
        return args;
    }

    public async Task<JsonElement> CheckEnvironmentAsync(string database, CancellationToken token = default)
    {
        await VerifyVersionAsync(token);
        using var process = NewProcess(["--db", Path.GetFullPath(database), "doctor", "--json"]);
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, 1024 * 1024);
        var stderr = ReadBoundedAsync(process.StandardError, 1024 * 1024);
        // A probe owns only its temporary trace sessions; there is no installer to stop.
        await WaitProbeAsync(process, token);
        var output = await stdout;
        var diagnostics = await stderr;
        if (process.ExitCode != 0) throw new InvalidDataException("环境检查失败：" + diagnostics);
        using var document = JsonDocument.Parse(output);
        var envelope = document.RootElement;
        if (envelope.GetProperty("schema_version").GetInt32() != 3 || envelope.GetProperty("kind").GetString() != "doctor") throw new InvalidDataException("环境检查协议无效。");
        var data = envelope.GetProperty("data");
        foreach (var name in new[] { "etw_process", "etw_file", "etw_registry" }) _ = data.GetProperty(name).GetString() ?? throw new InvalidDataException("环境结果缺少来源状态。");
        _ = data.GetProperty("elevated").GetBoolean();
        _ = data.GetProperty("process_observation").GetBoolean();
        return data.Clone();
    }

    public async Task<InstallRunResult> RunAsync(InstallRequest request, Action<InstallStatus> progress)
    {
        var valid = request.Validate();
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) throw new InvalidOperationException("已有一个活动记录任务。");
        Semaphore? gate = null;
        var acquired = false;
        try
        {
            gate = new Semaphore(1, 1, "Local\\Contain.InstallRecord.0.3.1");
            acquired = gate.WaitOne(0);
            if (!acquired) throw new InvalidOperationException("另一个 Contain 窗口正在记录安装；请等待该任务退出。");
            await VerifyVersionAsync(CancellationToken.None);
            using var process = NewProcess(BuildArguments(valid));
            var sequence = new InstallStatusSequence(valid.Database);
            string? protocolError = null;
            if (!process.Start()) throw new InvalidOperationException("无法启动采集进程；安装器未由桌面启动。");
            var stderr = ReadBoundedAsync(process.StandardError, 1024 * 1024);
            var stdout = ReadLinesAsync(process.StandardOutput, line =>
            {
                if (protocolError is not null) return;
                try { progress(sequence.Accept(line)); }
                catch (Exception error) { protocolError = error.Message; }
            });
            // Never kill this process tree: it owns the real installer. Drain output even
            // after a protocol error, and keep the window open until the collector exits.
            await process.WaitForExitAsync();
            try { await stdout; } catch (Exception error) { protocolError ??= error.Message; }
            string diagnostics;
            try { diagnostics = await stderr; }
            catch (Exception error) { protocolError ??= error.Message; diagnostics = error.Message; }
            if (sequence.Last?.Stage is not ("finished" or "failed")) protocolError ??= "采集进程已退出，但缺少可靠的结束结果。已保存证据可能属于未完成会话。";
            return new(sequence.Last, process.ExitCode, protocolError, diagnostics);
        }
        finally
        {
            if (acquired) gate!.Release();
            gate?.Dispose();
            Volatile.Write(ref active, 0);
        }
    }

    private Process NewProcess(IReadOnlyList<string> args)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到附带的 contain.exe；请完整解压试用包。", executable);
        var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true),
            WorkingDirectory = Path.GetDirectoryName(executable)! } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        return process;
    }

    private async Task VerifyVersionAsync(CancellationToken token)
    {
        using var process = NewProcess(["--version"]);
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, 4096);
        var stderr = ReadBoundedAsync(process.StandardError, 4096);
        await WaitProbeAsync(process, token);
        if (process.ExitCode != 0 || (await stdout).Trim() != "contain 0.3.1") throw new InvalidDataException("CLI 版本不匹配；请使用附带的 0.3.1 程序。");
        _ = await stderr;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        var oversized = false;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer);
                if (count == 0) break;
                oversized |= text.Length + count > limit;
                if (text.Length < limit) text.Append(buffer, 0, Math.Min(count, limit - text.Length));
            }
        }
        catch (DecoderFallbackException) { await reader.BaseStream.CopyToAsync(Stream.Null); throw new InvalidDataException("CLI 输出含无效 UTF-8。"); }
        if (oversized) throw new InvalidDataException("CLI 输出超过读取上限；未视为成功结果。");
        return text.ToString();
    }

    private static async Task WaitProbeAsync(Process process, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            // This helper is used only for version and doctor, before any installer.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw new InvalidDataException("环境或版本检查超时，检查已停止；安装器未启动。");
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> consume)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        Exception? error = null;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer);
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (error is null) consume(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else if (line.Length < 1024 * 1024) line.Append(buffer[i]);
                    else { error ??= new InvalidDataException("记录状态行超过读取上限。"); line.Clear(); }
                }
            }
        }
        catch (DecoderFallbackException) { await reader.BaseStream.CopyToAsync(Stream.Null); throw new InvalidDataException("记录状态流含无效 UTF-8。"); }
        if (line.Length > 0) error ??= new InvalidDataException("记录状态流意外截断。");
        if (error is not null) throw error;
    }
}
