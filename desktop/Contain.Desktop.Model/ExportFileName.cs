using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Contain.Desktop.Model;

/// <summary>Portable Windows filename rules, independent of the host running model checks.</summary>
public static class ExportFileName
{
    public static string Create(string name, string startedAt, string sessionId, bool matches)
    {
        // Require a complete timestamp and explicit offset. A permissive parse
        // of a time-only string would invent today's date or the host timezone.
        var time = Regex.IsMatch(startedAt, @"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)
            && DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var started)
            ? "开始" + started.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "UTC"
            : "开始时间未提供";
        var id = Guid.TryParse(sessionId, out var uuid) ? uuid.ToString("N")[..8]
            : Component(sessionId, 8, "会话未知");
        return $"{Component(name, 96, "未命名记录")}_{time}_{id}_{(matches ? "匹配结果" : "本页报告")}.html";
    }

    private static string Component(string value, int maximum, string fallback)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            var text = Rune.IsControl(rune) || (rune.IsAscii && "<>:\"/\\|?*".Contains((char)rune.Value))
                ? "_" : rune.ToString();
            if (result.Length + text.Length > maximum) break;
            result.Append(text);
        }
        var safe = result.ToString().Trim().TrimEnd('.', ' ');
        if (safe.Length == 0) return fallback;
        var stem = safe.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && "123456789¹²³".Contains(stem[3])))
            safe = "_" + safe;
        return safe;
    }
}
