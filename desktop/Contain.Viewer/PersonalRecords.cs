using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Contain.Viewer;

public enum PersonalRecordState { Missing, Existing }

/// <summary>Stable personal library location. Resolving it never creates files or directories.</summary>
public sealed class PersonalRecords
{
    private const string TestRootVariable = "CONTAIN_VIEWER_TEST_ROOT";
    private const string TestMarker = ".contain-records-test-root";
    private const string TestMarkerText = "Contain isolated record library test data";
    public string DatabasePath { get; }
    private PersonalRecords(string directory) => DatabasePath = Path.Combine(directory, "contain.db");

    public static PersonalRecords ForCurrentUser()
    {
        var localData = LocalApplicationData();
        var trueDirectory = Path.Combine(localData, "Contain", "Records");
        var testRoot = Environment.GetEnvironmentVariable(TestRootVariable);
        if (testRoot is null) return new PersonalRecords(trueDirectory);
        try
        {
            if (string.IsNullOrWhiteSpace(testRoot) || !Path.IsPathFullyQualified(testRoot))
                throw new InvalidDataException("测试记录根目录必须是绝对路径。");
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testRoot));
            RequireOrdinaryExistingDirectory(root);
            for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("测试记录根目录不能经过重解析点。");
            var canonicalRoot = PhysicalPath(root);
            var canonicalPersonal = PhysicalPath(trueDirectory);
            if (Within(canonicalRoot, canonicalPersonal) || Within(canonicalPersonal, canonicalRoot))
                throw new InvalidDataException("测试记录根目录不能与真实个人记录目录重叠。");
            var marker = Path.Combine(root, TestMarker);
            var markerAttributes = Attributes(marker);
            if (markerAttributes is null || (markerAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || File.ReadAllText(marker, new UTF8Encoding(false, true)) != TestMarkerText)
                throw new InvalidDataException("测试记录根目录缺少有效的隔离标记。");
            var isolated = new PersonalRecords(Path.Combine(root, "Contain", "Records"));
            // The marked root alone is insufficient: an existing suffix could
            // redirect Records to a real library through a junction or short path.
            ValidateExistingParents(isolated.DatabasePath);
            var canonicalIsolated = PhysicalPath(isolated.DatabasePath);
            if (Within(canonicalIsolated, canonicalPersonal) || Within(canonicalPersonal, canonicalIsolated))
                throw new InvalidDataException("隔离记录位置不能指向真实个人记录目录。");
            return isolated;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Invalid isolation is fatal. Never fall back to a real user's library.
            throw new InvalidDataException("隔离测试记录目录不可用；未使用真实个人记录目录。", error);
        }
    }

    public bool IsMissing => InspectExisting() == PersonalRecordState.Missing;
    public PersonalRecordState InspectExisting() => InspectDatabase(DatabasePath);
    internal bool IsPersonalDatabase(string database) => string.Equals(Path.GetFullPath(database), DatabasePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Called only by explicit Start, after the existing database was checked read-only.</summary>
    public InstallRequest PrepareForRecording(InstallRequest request)
    {
        var personal = IsPersonalDatabase(request.Database);
        var valid = request.Validate(allowMissingDatabaseDirectory: personal);
        _ = InspectDatabase(valid.Database);
        var parent = Path.GetDirectoryName(valid.Database)!;
        try
        {
            if (personal)
            {
                // This is the sole library-directory creation point. Input and physical
                // watch-scope validation above must succeed before reaching it.
                Directory.CreateDirectory(parent);
                RequireOrdinaryExistingDirectory(parent);
            }
            var probe = Path.Combine(parent, ".contain-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            using var writable = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("记录保存位置不可写。安装器未启动；可选择“更改保存位置”。", error); }
        return valid.Validate();
    }

    internal static PersonalRecordState InspectDatabase(string database)
    {
        try
        {
            var path = Path.GetFullPath(database);
            var main = Attributes(path);
            var wal = Attributes(path + "-wal");
            var shm = Attributes(path + "-shm");
            if (main is null)
            {
                if (wal is not null || shm is not null)
                    throw new InvalidDataException("记录主文件缺失，但仍有 WAL/SHM 证据。为保护记录，不会创建或覆盖数据库；请保留这些文件并更改保存位置。");
                ValidateExistingParents(path);
                return PersonalRecordState.Missing;
            }
            foreach (var attributes in new[] { main, wal, shm })
                if (attributes is { } value && (value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("记录位置或 WAL/SHM 不是普通文件。已有内容保持不变，请更改保存位置。");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[16];
            if (input.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidDataException("已有记录不是可识别的 SQLite 数据库。不会覆盖或重新创建，请更改保存位置。");
            return PersonalRecordState.Existing;
        }
        catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && error is not InvalidDataException)
        { throw new InvalidDataException("无法安全读取记录保存位置。已有文件保持不变，可更改保存位置。", error); }
    }

    internal static void EnsureOutsideWatch(string database, string watchRoot)
    {
        var root = PhysicalPath(watchRoot);
        foreach (var path in new[] { database, database + "-wal", database + "-shm" })
            if (Within(PhysicalPath(path), root))
                throw new InvalidDataException("记录数据库及 WAL/SHM 与观察目录重叠。请更改保存位置；安装器未启动。");
    }

    private static bool Within(string candidate, string parent) => string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ValidateExistingParents(string path)
    {
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
        {
            var value = Attributes(parent.FullName);
            if (value is null) continue;
            if ((value & FileAttributes.Directory) == 0)
                throw new InvalidDataException("记录目录被已有文件占用，请更改保存位置。");
            if ((value & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("记录目录经过重解析点，不能自动创建。请更改保存位置。");
        }
    }

    private static void RequireOrdinaryExistingDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("记录目录必须是已存在的普通目录。");
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    // Resolve the longest existing prefix through a native handle, so junctions,
    // short names and not-yet-created database parent suffixes share one boundary.
    private static string PhysicalPath(string path)
    {
        var current = Path.GetFullPath(path);
        var suffix = new Stack<string>();
        while (Attributes(current) is null)
        {
            suffix.Push(Path.GetFileName(current));
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("记录路径没有可确认的现有根目录。");
        }
        using var handle = CreateFile(current, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("无法确认记录路径的实际位置。", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw new IOException("无法确认记录路径的实际位置。", new Win32Exception(Marshal.GetLastWin32Error()));
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            if (GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0) == 0)
                throw new IOException("无法读取完整记录路径。", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        var result = buffer.ToString();
        if (result.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) result = "\\\\" + result[8..];
        else if (result.StartsWith("\\\\?\\", StringComparison.Ordinal)) result = result[4..];
        foreach (var piece in suffix) result = Path.Combine(result, piece);
        return Path.TrimEndingDirectorySeparator(result);
    }

    private static string LocalApplicationData()
    {
        var id = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        var result = SHGetKnownFolderPath(in id, 0x4000, IntPtr.Zero, out var pointer); // DONT_VERIFY: never create.
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        try { return Marshal.PtrToStringUni(pointer) ?? throw new InvalidDataException("无法确认 Windows 个人数据目录。"); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
