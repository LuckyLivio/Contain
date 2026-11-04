using System.Text.Json;

namespace Contain.Viewer;

/// <summary>Strict additive install stream; human terminal text is never task state.</summary>
public sealed record InstallStatus(string SessionId, string Database, ulong Sequence, string Stage,
    bool InstallerStarted, uint? InstallerPid, string? InstallerCreationTime, bool InstallerExited,
    int? InstallerExitCode, bool CaptureFinished, bool DataSaved, bool EtwRequested,
    string? EtwProcess, string? EtwFile, string? EtwRegistry, string? QualityLevel,
    IReadOnlyList<string> QualityReasons, string? Error)
{
    // Optional additive measurements: an older backend has no such measurement.
    public string? Step { get; init; }
    public ulong? ElapsedMilliseconds { get; init; }
    public ulong? StepElapsedMilliseconds { get; init; }
    public ulong? StageElapsedMilliseconds { get; init; }
    public ulong? ProcessedCount { get; init; }
    public string? ProcessedUnit { get; init; }
    public ulong? LastProgressElapsedMilliseconds { get; init; }
    public string? InstallerInteraction { get; init; }

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
            string? AdditiveText(string key) => !data.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null ? null : field.GetString();
            ulong? Measurement(string key) => !data.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null ? null : field.GetUInt64();
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
                Optional("etw_file"), Optional("etw_registry"), qualityLevel, reasons, Optional("error"))
            {
                Step = AdditiveText("step"), ElapsedMilliseconds = Measurement("elapsed_ms"),
                StepElapsedMilliseconds = Measurement("step_elapsed_ms"), StageElapsedMilliseconds = Measurement("stage_elapsed_ms"), ProcessedCount = Measurement("processed_count"),
                ProcessedUnit = AdditiveText("processed_unit"), LastProgressElapsedMilliseconds = Measurement("last_progress_elapsed_ms"),
                InstallerInteraction = AdditiveText("installer_interaction")
            };
            Require(status.StepElapsedMilliseconds is null || status.ElapsedMilliseconds is not null && status.StepElapsedMilliseconds <= status.ElapsedMilliseconds,
                "步骤耗时超出任务耗时或缺少任务时钟。");
            Require(status.StageElapsedMilliseconds is null || status.ElapsedMilliseconds is not null && status.StageElapsedMilliseconds <= status.ElapsedMilliseconds,
                "阶段耗时超出任务耗时或缺少任务时钟。");
            Require(status.LastProgressElapsedMilliseconds is null || status.ElapsedMilliseconds is not null && status.LastProgressElapsedMilliseconds <= status.ElapsedMilliseconds,
                "实际进展时间超出任务时钟。");
            Require(status.ProcessedCount is null || !string.IsNullOrWhiteSpace(status.ProcessedUnit), "已处理数量缺少计量单位。");
            Require(status.InstallerInteraction is null or "not_started" or "may_need_input" or "not_needed", "安装器交互状态无效。");
            Require(status.InstallerInteraction != "may_need_input" || status.InstallerStarted && !status.InstallerExited, "安装器交互状态与进程结果矛盾。");
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
    public InstallStatus Accept(string line) => Accept(InstallStatus.Parse(line));

    // A delayed observer message cannot replace the session bound by this child's
    // first message. Stale/duplicate/foreign rows do not refresh UI receipt time.
    public bool TryAccept(string line, out InstallStatus? accepted)
    {
        var current = InstallStatus.Parse(line);
        accepted = null;
        if (!SameDatabase(current.Database) || Last is not null &&
            (current.SessionId != Last.SessionId || current.Sequence <= Last.Sequence)) return false;
        accepted = Accept(current);
        return true;
    }

    private bool SameDatabase(string value) => string.Equals(Path.GetFullPath(value), Path.GetFullPath(expectedDatabase), StringComparison.OrdinalIgnoreCase);

    private InstallStatus Accept(InstallStatus current)
    {
        if (!SameDatabase(current.Database))
            throw new InvalidDataException("CLI 状态指向其他数据库。");
        if (Last is null)
        {
            if (current.Sequence != 1 || current.Stage != "preparing") throw new InvalidDataException("缺少本次启动的初始状态。");
        }
        else
        {
            if (current.Sequence <= Last.Sequence || current.SessionId != Last.SessionId || Last.Stage is "finished" or "failed")
                throw new InvalidDataException("记录状态顺序或会话 ID 不匹配。");
            if (Last.InstallerStarted && (!current.InstallerStarted || current.InstallerPid != Last.InstallerPid || current.InstallerCreationTime != Last.InstallerCreationTime)
                || Last.InstallerExited && (!current.InstallerExited || current.InstallerExitCode != Last.InstallerExitCode) || Last.DataSaved && !current.DataSaved
                || current.EtwRequested != Last.EtwRequested || Last.CaptureFinished && !current.CaptureFinished
                || Rank(current.Stage) < Rank(Last.Stage)) throw new InvalidDataException("记录状态出现矛盾或倒退。");
            if (current.ElapsedMilliseconds < Last.ElapsedMilliseconds || current.LastProgressElapsedMilliseconds < Last.LastProgressElapsedMilliseconds
                || current.Stage == Last.Stage && current.StageElapsedMilliseconds < Last.StageElapsedMilliseconds
                || current.Step == Last.Step && (current.StepElapsedMilliseconds < Last.StepElapsedMilliseconds
                    || current.ProcessedUnit == Last.ProcessedUnit && current.ProcessedCount < Last.ProcessedCount))
                throw new InvalidDataException("记录的测量时间或已处理数量出现倒退。");
            if (current.Stage == "finalizing" && Last.Stage != "finalizing" && (Last.Stage != "capturing" || !Last.InstallerExited)
                || current.Stage == "finished" && Last.Stage != "finalizing") throw new InvalidDataException("记录缺少可靠的退出、整理阶段。");
        }
        Last = current;
        return current;
    }
    private static int Rank(string stage) => stage switch { "preparing" => 0, "capturing" => 1, "finalizing" => 2, _ => 3 };
}
