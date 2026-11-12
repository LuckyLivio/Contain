using System.Text.Json;
using Contain.Desktop.Model;
using Contain.Viewer;

namespace Contain.Desktop.Model.Tests;

public static class ExportChecks
{
    private const string Session = "88db2f6a-f3ff-41d6-90b8-83658775dd9f";
    private const string Started = "2026-11-07T06:46:28.114192400+00:00";

    public static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        void Check(string name, Action test) { test(); passed.Add(name); }

        Check("export filenames use record start UTC exact session suffix and export kind", () =>
        {
            Require(ExportFileName.Create("7z-0.4-b1", Started, Session, true)
                == "7z-0.4-b1_开始20261107-064628UTC_88db2f6a_匹配结果.html", "match filename meaning");
            Require(ExportFileName.Create("7z-0.4-b1", "2026-11-07T14:46:28.1141924+08:00", Session, false)
                == "7z-0.4-b1_开始20261107-064628UTC_88db2f6a_本页报告.html", "page name and explicit timezone");
        });
        Check("Windows invalid characters and trailing spaces or dots are sanitized while Chinese stays readable", () =>
        {
            var name = ExportFileName.Create(" 中文<>:\"/\\|?*\u0001 . ", Started, Session, true);
            Require(name.StartsWith("中文__________", StringComparison.Ordinal) && !name.Contains(" ._", StringComparison.Ordinal)
                && name.IndexOfAny("<>:\"/\\|?*".ToCharArray()) < 0 && !name.Any(char.IsControl), "portable Windows rules and readable Chinese");
            Require(ExportFileName.Create(" . . ", "", Session, false).StartsWith("未命名记录_开始时间未提供_", StringComparison.Ordinal), "empty trimmed fallback and unavailable time");
        });
        Check("Windows device names and extension variants are escaped case insensitively", () =>
        {
            foreach (var name in new[] { "CON", "prn", "AUX.txt", "NUL .log", "COM1", "LPT9.txt", "COM¹", "lpt³.zip", "CONIN$", "CONOUT$" })
                Require(ExportFileName.Create(name, Started, Session, true).StartsWith("_" + name, StringComparison.Ordinal), "device name: " + name);
            Require(ExportFileName.Create("COM10", Started, Session, true).StartsWith("COM10_", StringComparison.Ordinal), "ordinary similar name stays readable");
        });
        Check("long export names stay bounded without splitting Unicode scalars", () =>
        {
            var filename = ExportFileName.Create(new string('中', 95) + "😀尾" + new string('文', 300), Started, Session, false);
            Require(filename.Length < 180 && filename.StartsWith(new string('中', 95) + "_", StringComparison.Ordinal)
                && filename.EndsWith("_88db2f6a_本页报告.html", StringComparison.Ordinal) && !filename.Any(char.IsSurrogate), "bounded name retains identity and type");
        });
        Check("frozen export metadata rejects changed record identity name and start before committing output", () =>
        {
            var identity = new ReportExportIdentity(Session, "中文记录", Started);
            var summary = Data(new { id = Session, name = "中文记录", started_at = Started });
            identity.ValidateSummary(summary);
            identity.ValidateSearch(Data(new { app_id = Session, name = "中文记录" }));
            Reject(() => identity.ValidateSummary(Data(new { id = "other-session", name = "中文记录", started_at = Started })));
            Reject(() => identity.ValidateSummary(Data(new { id = Session, name = "changed", started_at = Started })));
            Reject(() => identity.ValidateSummary(Data(new { id = Session, name = "中文记录", started_at = "changed" })));
            Reject(() => identity.ValidateSearch(Data(new { app_id = "other-session", name = "中文记录" })));
            Reject(() => identity.ValidateSearch(Data(new { app_id = Session, name = "changed" })));
        });
        return passed;
    }

    private static JsonElement Data(object value) => JsonSerializer.SerializeToElement(value);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (CoreClientException error) when (error.Code == "export_record_changed") { return; }
        throw new InvalidOperationException("changed export metadata was accepted");
    }
}
