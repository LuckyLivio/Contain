using System.Text.Json;

namespace Contain.Viewer;

/// <summary>Strict additive install stream; human terminal text is never task state.</summary>
public sealed record InstallStatus(string SessionId, string Database, ulong Sequence, string Stage,
    bool InstallerStarted, uint? InstallerPid, string? InstallerCreationTime, bool InstallerExited,
    int? InstallerExitCode, bool CaptureFinished, bool DataSaved, bool EtwRequested,
    string? EtwProcess, string? EtwFile, string? EtwRegistry, string? QualityLevel,
    IReadOnlyList<string> QualityReasons, string? Error)
{
    public static InstallStatus Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            RejectDuplicates(root);
            Require(root.GetProperty("schema_version").GetInt32() == 3 && root.GetProperty("kind").GetString() == "install_status", "记录状态协议不匹配。");
            var data = root.GetProperty("data");
            string Text(string key) => data.GetProperty(key).GetString() ?? throw new InvalidDataException($"状态缺少 {key}。");
            string? Optional(string key) => data.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Text(key);
            bool Flag(string key) => data.GetProperty(key).GetBoolean();
            var session = Text("session_id");
            Require(Guid.TryParse(session, out _), "会话 ID 无效。");
            var database = Text("database");
            Require(Path.IsPathFullyQualified(database), "状态数据库不是绝对路径。");
            var stage = Text("stage");
            Require(stage is "preparing" or "capturing" or "finalizing" or "finished" or "failed", "未知记录状态，不能判断采集成功。");
            var pidField = data.GetProperty("installer_pid");
            uint? pid = pidField.ValueKind == JsonValueKind.Null ? null : pidField.GetUInt32();
            var creation = Optional("installer_creation_time");
            Require(creation is null || creation.Length > 0 && creation.All(c => c is >= '0' and <= '9'), "进程创建时间必须是精确十进制字符串。");
            var codeField = data.GetProperty("installer_exit_code");
            int? exitCode = codeField.ValueKind == JsonValueKind.Null ? null : codeField.GetInt32();
            string? qualityLevel = null;
            var reasons = new List<string>();
            var quality = data.GetProperty("quality");
            if (quality.ValueKind != JsonValueKind.Null)
            {
                qualityLevel = quality.GetProperty("level").GetString();
                Require(qualityLevel is "Degraded" or "Incomplete" or "Complete", "覆盖质量未知，不能判断成功。");
                foreach (var reason in quality.GetProperty("reasons").EnumerateArray()) reasons.Add(reason.GetString() ?? throw new InvalidDataException("质量原因无效。"));
            }
            var status = new InstallStatus(session, database, data.GetProperty("sequence").GetUInt64(), stage,
                Flag("installer_started"), pid, creation, Flag("installer_exited"), exitCode,
                Flag("capture_finished"), Flag("data_saved"), Flag("etw_requested"), Optional("etw_process"),
                Optional("etw_file"), Optional("etw_registry"), qualityLevel, reasons, Optional("error"));
            Require(!status.InstallerStarted || pid is > 0, "安装器 PID 未确认。");
            Require(status.InstallerStarted || pid is null && creation is null, "安装器尚未启动却包含进程身份。");
            Require(!status.InstallerExited || status.InstallerStarted, "安装器退出状态矛盾。");
            Require(status.InstallerExited || exitCode is null, "安装器尚未退出却含退出码。");
            Require(stage != "finished" || status.InstallerExited && status.CaptureFinished && status.DataSaved && qualityLevel is not null, "结束消息未确认采集和数据保存。");
            Require(stage != "preparing" || !status.InstallerStarted && !status.InstallerExited && !status.CaptureFinished, "准备状态含矛盾的启动或结束标记。");
            Require(stage != "capturing" || status.InstallerStarted && !status.CaptureFinished, "记录状态未确认启动或已经完成采集。");
            Require(stage != "finalizing" || status.InstallerStarted && status.InstallerExited && status.CaptureFinished, "整理状态缺少真实退出或来源停止标记。");
            Require(stage == "failed" || status.Error is null, "未失败的状态意外含错误。");
            Require(stage != "failed" || !string.IsNullOrWhiteSpace(status.Error), "失败状态缺少原因。");
            return status;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new InvalidDataException("CLI 返回无效的结构化记录状态。", error); }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) { Require(fields.Add(item.Name), "状态 JSON 含重复字段。"); RejectDuplicates(item.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

public sealed class InstallStatusSequence(string expectedDatabase)
{
    public InstallStatus? Last { get; private set; }
    public InstallStatus Accept(string line)
    {
        var current = InstallStatus.Parse(line);
        if (!string.Equals(Path.GetFullPath(current.Database), Path.GetFullPath(expectedDatabase), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CLI 状态指向其他数据库。");
        if (Last is null)
        {
            if (current.Sequence != 1 || current.Stage != "preparing") throw new InvalidDataException("缺少本次启动的初始状态。");
        }
        else
        {
            if (current.Sequence != Last.Sequence + 1 || current.SessionId != Last.SessionId || Last.Stage is "finished" or "failed")
                throw new InvalidDataException("记录状态顺序或会话 ID 不匹配。");
            if (Last.InstallerStarted && (!current.InstallerStarted || current.InstallerPid != Last.InstallerPid || current.InstallerCreationTime != Last.InstallerCreationTime)
                || Last.InstallerExited && (!current.InstallerExited || current.InstallerExitCode != Last.InstallerExitCode) || Last.DataSaved && !current.DataSaved
                || current.EtwRequested != Last.EtwRequested || Last.CaptureFinished && !current.CaptureFinished
                || Rank(current.Stage) < Rank(Last.Stage)) throw new InvalidDataException("记录状态出现矛盾或倒退。");
            if (current.Stage == "finalizing" && (Last.Stage != "capturing" || !Last.InstallerExited)
                || current.Stage == "finished" && Last.Stage != "finalizing") throw new InvalidDataException("记录缺少可靠的退出、整理阶段。");
        }
        Last = current;
        return current;
    }
    private static int Rank(string stage) => stage switch { "preparing" => 0, "capturing" => 1, "finalizing" => 2, _ => 3 };
}
