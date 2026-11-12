using System.Runtime.InteropServices;

namespace Contain.Desktop.Tests;

/// <summary>Old overview fixture setup, confined to a new marked copy before any product launch.</summary>
internal static class SyntheticSql
{
    internal const string LongRoot = @"C:\synthetic\观察目录 中文 & [x]\第一层很长的目录名称\第二层很长的目录名称\第三层很长的目录名称\第四层完整路径末尾";
    internal static readonly string ExportRecordName = "SYNTHETIC 中文:<安装?>|" + new string('长', 130) + " .";
    // Independent fixed oracle for the known input above; do not call the product sanitizer.
    internal static readonly string ExportNameComponent = "SYNTHETIC 中文__安装___" + new string('长', 77);
    internal static void PrepareExportNames(string root, string database)
        => Execute(root, database, "UPDATE applications SET name='" + ExportRecordName.Replace("'", "''", StringComparison.Ordinal) + "';");
    internal static void PrepareOutcomes(string root, string database)
    {
        var sql = "UPDATE applications SET name='SYNTHETIC 同名安装记录';"
            + "UPDATE installation_sessions SET exit_code=1603 WHERE id='synthetic-historical';"
            + "UPDATE capture_runs SET error='SYNTHETIC：受控记录来源错误。完整诊断末尾。' WHERE session_id='synthetic-interrupted';"
            + "UPDATE installation_sessions SET watch_roots_json='" + System.Text.Json.JsonSerializer.Serialize(new[] { LongRoot }).Replace("'", "''", StringComparison.Ordinal) + "';";
        Execute(root, database, sql);
    }
    private static void Execute(string root, string database, string sql)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!File.Exists(database) || !Path.GetFullPath(database).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || File.ReadAllText(Path.Combine(root, ".contain-records-test-root")) != "Contain isolated record library test data")
            throw new InvalidOperationException("Synthetic mutations require a marked isolated copy.");
        if (sqlite3_open_v2(database, out var connection, 2, IntPtr.Zero) != 0) throw new IOException("Synthetic copy open failed.");
        try
        {
            var result = sqlite3_exec(connection, sql, IntPtr.Zero, IntPtr.Zero, out var error);
            try { if (result != 0) throw new IOException("Synthetic overview setup failed."); }
            finally { if (error != IntPtr.Zero) sqlite3_free(error); }
        }
        finally { sqlite3_close(connection); }
    }
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, out IntPtr error);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(IntPtr value);
}
