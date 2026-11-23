using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Contain.Desktop.Model;

/// <summary>
/// Real Windows Sandbox controller backed by the official Windows 11 `wsb.exe` CLI (`--raw` JSON).
/// Every sandbox uses Networking=Disable, ClipboardRedirection=Disable, strictly ReadOnly mapped
/// folders (never exposing a writable host folder to untrusted software), per-session staging
/// containing only the selected EXE, and exact per-instance UUID tracking for launch, polling,
/// and termination.
/// </summary>
public sealed class WindowsSandboxTrialService : IIsolatedTrialService
{
    public const string GuestInputFolder = @"C:\ContainTrialInput";
    public const string GuestBootstrapFolder = @"C:\ContainTrialBootstrap";
    public const string GuestRunFolder = @"C:\ContainTrialRun";

    private const int MaxStreamBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8NoBom = new(false, true);

    private static readonly string[] RequiredSubcommands =
        ["start", "list", "connect", "exec", "stop"];

    // Static, pure-ASCII PowerShell 5.1 guest launcher. Reads target metadata strictly as UTF-8
    // JSON data (never interpolated into a command string), recursively copies the full read-only
    // input snapshot (single EXE or portable folder preserving subdirectories) into writable
    // guest-local C:\ContainTrialRun, sets WorkingDirectory to the entry EXE's directory so sibling
    // DLLs, relative resources and local settings work naturally, and reports observable launch
    // outcome via exact guest exit codes:
    //   0  = started and still running after 1s
    //   20 = started and exited cleanly with code 0 within 1s
    //   21 = started and exited non-zero within 1s
    //   11 = invalid target metadata or entry path
    //   12 = input entry EXE missing in read-only mount
    //   13 = copy to guest local run folder failed or incomplete
    //   14 = Process.Start failed or returned no process handle/PID
    internal const string GuestLauncherScript = """
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        try {
            $metaPath = 'C:\ContainTrialBootstrap\target.json'
            if (!(Test-Path -LiteralPath $metaPath -PathType Leaf)) { exit 11 }
            $meta = [System.IO.File]::ReadAllText($metaPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            $entryRel = ''
            if ($null -ne $meta.PSObject.Properties['entry_relative_path']) {
                $entryRel = [string]$meta.entry_relative_path
            } elseif ($null -ne $meta.PSObject.Properties['file_name']) {
                $entryRel = [string]$meta.file_name
            }
            if ([string]::IsNullOrWhiteSpace($entryRel)) { exit 11 }
            $entryRel = $entryRel.Replace('/', '\').Trim()
            if ([System.IO.Path]::IsPathRooted($entryRel) -or $entryRel.Contains(':')) { exit 11 }
            foreach ($seg in $entryRel.Split('\')) {
                if ([string]::IsNullOrEmpty($seg) -or $seg -eq '.' -or $seg -eq '..') { exit 11 }
            }

            $inputRoot = [System.IO.Path]::GetFullPath('C:\ContainTrialInput')
            $srcEntry = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($inputRoot, $entryRel))
            if (!$srcEntry.StartsWith($inputRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) { exit 11 }
            if (!(Test-Path -LiteralPath $srcEntry -PathType Leaf)) { exit 12 }

            $runDir = [System.IO.Path]::GetFullPath('C:\ContainTrialRun')
            if (!(Test-Path -LiteralPath $runDir)) { [void][System.IO.Directory]::CreateDirectory($runDir) }

            $expectedCount = 0
            if ($null -ne $meta.PSObject.Properties['expected_file_count'] -and $null -ne $meta.expected_file_count) {
                $expectedCount = [int]$meta.expected_file_count
            }

            $copiedFiles = 0
            try {
                $stack = New-Object System.Collections.Generic.Stack[string]
                $stack.Push($inputRoot)
                while ($stack.Count -gt 0) {
                    $curDir = $stack.Pop()
                    $relDir = if ($curDir.Length -le $inputRoot.Length) { '' } else { $curDir.Substring($inputRoot.Length + 1) }
                    $dstDir = if ([string]::IsNullOrEmpty($relDir)) { $runDir } else { [System.IO.Path]::Combine($runDir, $relDir) }
                    if (!(Test-Path -LiteralPath $dstDir)) { [void][System.IO.Directory]::CreateDirectory($dstDir) }

                    foreach ($sub in [System.IO.Directory]::EnumerateDirectories($curDir)) {
                        $stack.Push($sub)
                    }
                    foreach ($file in [System.IO.Directory]::EnumerateFiles($curDir)) {
                        $srcInfo = New-Object System.IO.FileInfo($file)
                        $dstFile = [System.IO.Path]::Combine($dstDir, $srcInfo.Name)
                        [System.IO.File]::Copy($file, $dstFile, $true)
                        $dstInfo = New-Object System.IO.FileInfo($dstFile)
                        if ($dstInfo.IsReadOnly) { $dstInfo.IsReadOnly = $false }
                        if ($dstInfo.Length -ne $srcInfo.Length) { exit 13 }
                        $copiedFiles++
                    }
                }
                if ($expectedCount -gt 0 -and $copiedFiles -lt $expectedCount) { exit 13 }
            } catch {
                exit 13
            }

            $dstEntry = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($runDir, $entryRel))
            if (!(Test-Path -LiteralPath $dstEntry -PathType Leaf)) { exit 13 }
            $workDir = [System.IO.Path]::GetDirectoryName($dstEntry)
            if ([string]::IsNullOrEmpty($workDir)) { $workDir = $runDir }

            try {
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName = $dstEntry
                $psi.WorkingDirectory = $workDir
                $psi.UseShellExecute = $true
                $proc = [System.Diagnostics.Process]::Start($psi)
                if ($null -eq $proc -or $proc.Id -le 0) { exit 14 }
                Start-Sleep -Milliseconds 1000
                $proc.Refresh()
                if ($proc.HasExited) {
                    $code = $proc.ExitCode
                    if ($code -eq 0) { exit 20 } else { exit 21 }
                }
                exit 0
            } catch {
                exit 14
            }
        } catch {
            exit 15
        }
        """;

    private const long StagingDiskSafetyMarginBytes = 64L * 1024 * 1024;

    private readonly string stagingBaseDirectory;
    private readonly string pendingSessionFilePath;
    private readonly TimeSpan commandTimeout;
    private readonly TimeSpan readinessTimeout;
    private readonly string? explicitWsbPath;
    private readonly Func<string, long?>? availableFreeSpaceProvider;

    public WindowsSandboxTrialService(
        string? stagingBaseDirectory = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? readinessTimeout = null,
        string? explicitWsbPath = null,
        string? pendingSessionFilePath = null,
        Func<string, long?>? availableFreeSpaceProvider = null)
    {
        this.stagingBaseDirectory = string.IsNullOrWhiteSpace(stagingBaseDirectory)
            ? Path.Combine(Path.GetTempPath(), "Contain-IsolatedTrial")
            : Path.GetFullPath(stagingBaseDirectory);
        this.pendingSessionFilePath = string.IsNullOrWhiteSpace(pendingSessionFilePath)
            ? DefaultPendingSessionFilePath()
            : Path.GetFullPath(pendingSessionFilePath);
        this.commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(25);
        this.readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(60);
        this.explicitWsbPath = string.IsNullOrWhiteSpace(explicitWsbPath) ? null : Path.GetFullPath(explicitWsbPath);
        this.availableFreeSpaceProvider = availableFreeSpaceProvider;
    }

    public static string DefaultPendingSessionFilePath()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.GetTempPath();
        return Path.Combine(localData, "Contain", "IsolatedTrial", "pending-session.json");
    }

    public TrialExecutableInfo InspectExecutable(string executablePath) =>
        InspectExecutableFile(executablePath);

    public Task<TrialFolderInfo> ScanFolderAsync(
        string folderPath,
        IProgress<TrialFolderScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ScanPortableFolderAsync(folderPath, stagingBaseDirectory, progress, cancellationToken);

    public static Task<TrialFolderInfo> ScanPortableFolderAsync(
        string folderPath,
        string? forbiddenNestedDir = null,
        IProgress<TrialFolderScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanPortableFolderCore(folderPath, forbiddenNestedDir, progress, cancellationToken), cancellationToken);

    private static TrialFolderInfo ScanPortableFolderCore(
        string folderPath,
        string? forbiddenNestedDir,
        IProgress<TrialFolderScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || folderPath.Contains('\0'))
            throw new IsolatedTrialException("invalid_folder_path", "请选择有效的本地软件文件夹。", failedStage: "扫描软件文件夹");

        var trimmed = folderPath.Trim().Trim('"');
        string fullRoot;
        try { fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)); }
        catch (Exception ex)
        {
            throw new IsolatedTrialException("invalid_folder_path", "文件夹路径格式无效，请重新选择。", diagnostic: ex.Message, inner: ex, failedStage: "扫描软件文件夹");
        }

        if (!Directory.Exists(fullRoot))
            throw new IsolatedTrialException("folder_missing", "所选文件夹不存在或无法访问，请重新选择。", failedStage: "扫描软件文件夹");

        // Reject root drive or reparse point root
        if (Path.GetDirectoryName(fullRoot) is null)
            throw new IsolatedTrialException("folder_root_drive", "不能选择整个磁盘根目录作为软件文件夹，请选择具体程序目录。", failedStage: "扫描软件文件夹");

        if (IsReparsePointPath(fullRoot))
        {
            throw new IsolatedTrialException(
                "reparse_point_detected",
                $"所选目录本身是符号链接、联接点或重解析点（{Path.GetFileName(fullRoot)}），已停止扫描。",
                "为保证实际带入沙箱的范围与界面展示一致，Contain 不跟随目录链接、联接点或其他重解析点。请直接选择真实的源目录。",
                $"reparse_root={fullRoot}",
                failedStage: "扫描软件文件夹");
        }

        if (!string.IsNullOrWhiteSpace(forbiddenNestedDir))
        {
            var fullForbidden = Path.TrimEndingDirectorySeparator(Path.GetFullPath(forbiddenNestedDir));
            if (IsPathEqualOrUnder(fullForbidden, fullRoot))
            {
                throw new IsolatedTrialException(
                    "folder_contains_staging",
                    "所选目录包含 Contain 暂存目录，不能作为试用目录。",
                    "请选择具体的便携软件目录，避免包含系统临时目录。",
                    $"folder={fullRoot}; staging={fullForbidden}",
                    failedStage: "扫描软件文件夹");
            }
        }

        var folderName = Path.GetFileName(fullRoot);
        if (string.IsNullOrWhiteSpace(folderName)) folderName = fullRoot;

        var runnable = new List<TrialFolderEntryCandidate>();
        var unsupported = new List<TrialFolderEntryCandidate>();
        var fileCount = 0;
        var dirCount = 0;
        long totalBytes = 0;

        var stack = new Stack<string>();
        stack.Push(fullRoot);
        var lastReport = Stopwatch.StartNew();

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDir = stack.Pop();
            var relDir = GetRelativeNormalized(fullRoot, currentDir);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(currentDir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                var displayRel = string.IsNullOrEmpty(relDir) ? folderName : relDir;
                throw new IsolatedTrialException(
                    "folder_read_failed",
                    $"扫描目录时无法读取“{displayRel}”：{ex.Message}",
                    "请确认当前用户对该目录及子目录拥有读取权限，或改选其他文件夹。",
                    $"{ex.GetType().Name}: {ex.Message}",
                    ex,
                    failedStage: "扫描软件文件夹");
            }

            foreach (var entryPath in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relPath = GetRelativeNormalized(fullRoot, entryPath);

                FileAttributes attrs;
                try { attrs = File.GetAttributes(entryPath); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    throw new IsolatedTrialException(
                        "folder_read_failed",
                        $"扫描目录时无法访问“{relPath}”：{ex.Message}",
                        "请确认文件未被独占占用或权限受限后重试。",
                        $"{ex.GetType().Name}: {ex.Message}",
                        ex,
                        failedStage: "扫描软件文件夹");
                }

                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IsolatedTrialException(
                        "reparse_point_detected",
                        $"目录内包含链接、联接点或其他重解析点（{relPath}），已停止扫描。",
                        "为保证实际带入沙箱的范围与界面展示一致，Contain 不跟随目录链接、联接点或符号链接。请移除该链接或替换为真实文件/目录后重试。",
                        $"reparse_entry={relPath}",
                        failedStage: "扫描软件文件夹");
                }

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    dirCount++;
                    stack.Push(entryPath);
                    continue;
                }

                FileInfo fi;
                long len;
                try
                {
                    fi = new FileInfo(entryPath);
                    len = fi.Length;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    throw new IsolatedTrialException(
                        "folder_read_failed",
                        $"扫描目录时无法读取文件信息“{relPath}”：{ex.Message}",
                        "请确认文件可读后重试。",
                        $"{ex.GetType().Name}: {ex.Message}",
                        ex,
                        failedStage: "扫描软件文件夹");
                }

                fileCount++;
                totalBytes += len;

                if (entryPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var arch = ReadPeArchitecture(entryPath);
                    if (arch != TrialExecutableArch.Unknown)
                    {
                        string? productVersion = null;
                        string? fileVersion = null;
                        try
                        {
                            var vi = FileVersionInfo.GetVersionInfo(entryPath);
                            productVersion = NormalizeVersionText(vi.ProductVersion);
                            fileVersion = NormalizeVersionText(vi.FileVersion);
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }

                        var candidate = new TrialFolderEntryCandidate(
                            relPath,
                            fi.Name,
                            entryPath,
                            len,
                            FormatByteSize(len),
                            arch,
                            FormatArchitectureLabel(arch),
                            productVersion,
                            fileVersion);

                        if (candidate.IsRunnableInX64Sandbox)
                            runnable.Add(candidate);
                        else
                            unsupported.Add(candidate);
                    }
                }

                if (progress is not null && (fileCount % 16 == 0 || lastReport.ElapsedMilliseconds >= 80))
                {
                    progress.Report(new TrialFolderScanProgress(dirCount, fileCount, totalBytes, runnable.Count, relPath));
                    lastReport.Restart();
                }
            }
        }

        progress?.Report(new TrialFolderScanProgress(dirCount, fileCount, totalBytes, runnable.Count, ""));

        runnable.Sort((a, b) =>
        {
            var depthCmp = CountPathDepth(a.RelativePath).CompareTo(CountPathDepth(b.RelativePath));
            return depthCmp != 0 ? depthCmp : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        });
        unsupported.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));

        return new TrialFolderInfo(
            folderName,
            fullRoot,
            fileCount,
            dirCount,
            totalBytes,
            FormatByteSize(totalBytes),
            runnable,
            unsupported);
    }

    public static bool ValidateFolderEntryPath(string folderFullPath, string entryRelativePath, out string resolvedFullPath, out string reason)
    {
        resolvedFullPath = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(folderFullPath) || string.IsNullOrWhiteSpace(entryRelativePath) || entryRelativePath.Contains('\0'))
        {
            reason = "入口相对路径为空或无效。";
            return false;
        }

        var normalizedRel = entryRelativePath.Replace('/', '\\').Trim();
        if (Path.IsPathRooted(normalizedRel) || normalizedRel.Contains(':'))
        {
            reason = "入口路径必须是所选目录内的相对路径。";
            return false;
        }

        foreach (var seg in normalizedRel.Split('\\'))
        {
            if (string.IsNullOrEmpty(seg) || seg == "." || seg == "..")
            {
                reason = "入口相对路径包含非法目录段（如 ..）。";
                return false;
            }
        }

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderFullPath));
            var combined = Path.GetFullPath(Path.Combine(fullRoot, normalizedRel));
            if (!IsPathStrictlyUnder(combined, fullRoot))
            {
                reason = "入口程序越出所选软件目录范围。";
                return false;
            }
            if (!combined.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                reason = "入口文件必须是 .exe 可执行程序。";
                return false;
            }
            if (!File.Exists(combined))
            {
                reason = $"所选入口程序不存在：{normalizedRel}";
                return false;
            }

            // Ensure neither root, intermediate directories, nor entry file is a reparse point
            if (IsReparsePointPath(fullRoot))
            {
                reason = "所选软件根目录是链接或重解析点。";
                return false;
            }
            var cur = combined;
            while (!string.Equals(cur, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (IsReparsePointPath(cur))
                {
                    reason = $"入口路径包含链接或重解析点：{GetRelativeNormalized(fullRoot, cur)}";
                    return false;
                }
                var parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent)) break;
                cur = parent;
            }

            resolvedFullPath = combined;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static string FormatArchitectureLabel(TrialExecutableArch arch) => arch switch
    {
        TrialExecutableArch.X64 => "Windows x64 (PE32+)",
        TrialExecutableArch.X86 => "Windows x86 (32 位)",
        TrialExecutableArch.Arm64 => "Windows ARM64（仅可查看信息，不适用于 x64 沙箱运行）",
        _ => "未知架构"
    };

    private static bool IsReparsePointPath(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static bool IsPathEqualOrUnder(string candidate, string root)
    {
        var normRoot = Path.TrimEndingDirectorySeparator(root);
        var normCand = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(normCand, normRoot, StringComparison.OrdinalIgnoreCase)
            || normCand.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathStrictlyUnder(string candidate, string root)
    {
        var normRoot = Path.TrimEndingDirectorySeparator(root);
        var normCand = Path.TrimEndingDirectorySeparator(candidate);
        return normCand.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRelativeNormalized(string root, string fullPath)
    {
        var normRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(Path.TrimEndingDirectorySeparator(fullPath), normRoot, StringComparison.OrdinalIgnoreCase))
            return "";
        return Path.GetRelativePath(normRoot, fullPath).Replace('/', '\\');
    }

    private static int CountPathDepth(string relPath)
    {
        var depth = 0;
        foreach (var ch in relPath)
            if (ch == '\\' || ch == '/') depth++;
        return depth;
    }

    public Task<string> ComputeSha256Async(string executablePath, CancellationToken cancellationToken = default) =>
        ComputeFileSha256Async(executablePath, cancellationToken);

    public static async Task<string> ComputeFileSha256Async(string executablePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('\0'))
            throw new IsolatedTrialException("invalid_path", "无法计算 SHA-256：文件路径无效。");

        var fullPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
        if (!File.Exists(fullPath))
            throw new IsolatedTrialException("file_missing", "无法计算 SHA-256：文件不存在或已被移走。");

        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                useAsync: true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                hasher.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IsolatedTrialException(
                "hash_read_failed",
                $"读取文件计算 SHA-256 失败：{ex.Message}",
                "请确认文件未被其他程序独占锁定后重试。",
                $"{ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    public void OpenWindowsFeatures()
    {
        if (!OperatingSystem.IsWindows())
            throw new IsolatedTrialException("unsupported_platform", "当前非 Windows 系统，无法打开“Windows 功能”。");

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "optionalfeatures.exe");
        if (!File.Exists(exe))
            throw new IsolatedTrialException("optionalfeatures_missing", "未找到 optionalfeatures.exe，请在系统“设置 → 系统 → 可选功能”中自行查看。");

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            throw new IsolatedTrialException(
                "optionalfeatures_launch_failed",
                $"无法打开“启用或关闭 Windows 功能”：{ex.Message}",
                "请通过开始菜单搜索“启用或关闭 Windows 功能”自行打开。",
                $"{ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    public static TrialExecutableInfo InspectExecutableFile(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('\0'))
            throw new IsolatedTrialException("invalid_path", "请选择有效的本地 Windows .exe 文件。");

        var trimmed = executablePath.Trim().Trim('"');
        string fullPath;
        try { fullPath = Path.GetFullPath(trimmed); }
        catch (Exception error)
        {
            throw new IsolatedTrialException("invalid_path", "文件路径格式无效，请重新选择本地 .exe 文件。", diagnostic: error.Message, inner: error);
        }

        if (!File.Exists(fullPath))
            throw new IsolatedTrialException("file_missing", "所选文件不存在或无法访问，请重新选择。");

        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IsolatedTrialException("not_exe", "当前仅支持选择 .exe 可执行文件。");

        var info = new FileInfo(fullPath);
        if (info.Length < 64)
            throw new IsolatedTrialException("invalid_pe", "所选文件不是有效的 Windows 可执行文件（文件过小）。");

        var arch = ReadPeArchitecture(fullPath);
        if (arch == TrialExecutableArch.Unknown)
            throw new IsolatedTrialException("invalid_pe", "所选文件不是有效的 Windows PE 可执行程序。");

        string? productVersion = null;
        string? fileVersion = null;
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(fullPath);
            productVersion = NormalizeVersionText(versionInfo.ProductVersion);
            fileVersion = NormalizeVersionText(versionInfo.FileVersion);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var parentDir = Path.GetDirectoryName(fullPath);
        var siblingCount = 0;
        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(parentDir))
                {
                    if (!string.Equals(Path.GetFullPath(entry), fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        siblingCount++;
                        if (siblingCount >= 10) break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var archLabel = FormatArchitectureLabel(arch);

        return new TrialExecutableInfo(
            info.Name,
            fullPath,
            info.Length,
            FormatByteSize(info.Length),
            arch,
            archLabel,
            productVersion,
            fileVersion,
            siblingCount > 0,
            siblingCount);
    }

    private static string? NormalizeVersionText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    public static TrialExecutableArch ReadPeArchitecture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 0x40) return TrialExecutableArch.Unknown;
            Span<byte> dos = stackalloc byte[0x40];
            if (stream.Read(dos) < 0x40) return TrialExecutableArch.Unknown;
            if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return TrialExecutableArch.Unknown;

            var peOffset = BitConverter.ToInt32(dos[0x3C..0x40]);
            if (peOffset < 0x40 || peOffset > stream.Length - 6) return TrialExecutableArch.Unknown;

            stream.Seek(peOffset, SeekOrigin.Begin);
            Span<byte> peHeader = stackalloc byte[6];
            if (stream.Read(peHeader) < 6) return TrialExecutableArch.Unknown;
            if (peHeader[0] != (byte)'P' || peHeader[1] != (byte)'E' || peHeader[2] != 0 || peHeader[3] != 0)
                return TrialExecutableArch.Unknown;

            var machine = BitConverter.ToUInt16(peHeader[4..6]);
            return machine switch
            {
                0x8664 => TrialExecutableArch.X64,
                0x014C => TrialExecutableArch.X86,
                0xAA64 => TrialExecutableArch.Arm64,
                _ => TrialExecutableArch.Unknown
            };
        }
        catch (IOException) { return TrialExecutableArch.Unknown; }
        catch (UnauthorizedAccessException) { return TrialExecutableArch.Unknown; }
    }

    public static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", bytes / 1024.0);
        if (bytes < 1024L * 1024 * 1024) return string.Format(CultureInfo.InvariantCulture, "{0:0.00} MB", bytes / (1024.0 * 1024.0));
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", bytes / (1024.0 * 1024.0 * 1024.0));
    }

    public static string BuildSandboxConfigXml(string readOnlyInputHostDir, string readOnlyBootstrapHostDir)
    {
        var doc = new XDocument(
            new XElement("Configuration",
                new XElement("VGpu", "Disable"),
                new XElement("Networking", "Disable"),
                new XElement("ClipboardRedirection", "Disable"),
                new XElement("MappedFolders",
                    new XElement("MappedFolder",
                        new XElement("HostFolder", Path.GetFullPath(readOnlyInputHostDir)),
                        new XElement("SandboxFolder", GuestInputFolder),
                        new XElement("ReadOnly", "true")),
                    new XElement("MappedFolder",
                        new XElement("HostFolder", Path.GetFullPath(readOnlyBootstrapHostDir)),
                        new XElement("SandboxFolder", GuestBootstrapFolder),
                        new XElement("ReadOnly", "true")))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static string? ParseStartedInstanceId(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("Id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                var raw = idProp.GetString()?.Trim();
                if (!string.IsNullOrEmpty(raw) && Guid.TryParse(raw, out var parsed))
                    return parsed.ToString("D");
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    public static bool TryParseRunningInstanceIds(string rawJson, out IReadOnlyList<string> ids)
    {
        ids = [];
        if (string.IsNullOrWhiteSpace(rawJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("WindowsSandboxEnvironments", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return false;

            var parsedIds = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("Id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var raw = idProp.GetString()?.Trim();
                if (string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out var parsed))
                    return false;

                parsedIds.Add(parsed.ToString("D"));
            }

            ids = parsedIds;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> ParseRunningInstanceIds(string rawJson) =>
        TryParseRunningInstanceIds(rawJson, out var ids) ? ids : [];

    /// <summary>
    /// Evaluates authoritative sandbox instance state from `wsb stop` + optional fallback `wsb list --raw`.
    /// Never reports ConfirmedEnded unless stop succeeded OR a valid parsed instance list explicitly omits the ID.
    /// </summary>
    public static SandboxInstanceState EvaluateStopOutcome(
        bool stopTimedOut,
        int stopExitCode,
        bool? listTimedOut,
        int? listExitCode,
        string? listStdout,
        string targetInstanceId)
    {
        if (!stopTimedOut && stopExitCode == 0)
            return SandboxInstanceState.ConfirmedEnded;

        if (listTimedOut == false
            && listExitCode == 0
            && TryParseRunningInstanceIds(listStdout ?? "", out var ids))
        {
            return ids.Contains(targetInstanceId, StringComparer.OrdinalIgnoreCase)
                ? SandboxInstanceState.Running
                : SandboxInstanceState.ConfirmedEnded;
        }

        return SandboxInstanceState.Unknown;
    }

    public static int? ParseExecExitCode(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ExitCode", out var codeProp)
                && codeProp.TryGetInt32(out var code))
            {
                return code;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<SandboxCapabilityReport> ProbeCapabilityAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CollectHostEnvironmentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return ClassifyHostEnvironment(snapshot);
    }

    private async Task<HostSandboxEnvironmentSnapshot> CollectHostEnvironmentSnapshotAsync(CancellationToken cancellationToken)
    {
        var isWindows = OperatingSystem.IsWindows();
        var isNativeX64 = isWindows && RuntimeInformation.OSArchitecture == Architecture.X64 && Environment.Is64BitProcess;
        if (!isNativeX64)
        {
            return new HostSandboxEnvironmentSnapshot(
                isWindows, false, null, null, null, false, false, null,
                null, null, [], null, null, null,
                null, null, false, null, null, false, null, false);
        }

        // 1. Read Windows Edition using ordinary-user readable registry + GetProductInfo
        var editionRead = TryReadHklmString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID");
        var productNameRead = TryReadHklmString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
        uint? productType = GetProductInfo(10, 0, 0, 0, out var pt) ? pt : null;

        // 2. Check direct Group Policy evidence under Windows Sandbox policy key
        var policyAllowMapped = TryReadHklmDword(@"SOFTWARE\Policies\Microsoft\Windows\Sandbox", "AllowMappedFolders");
        var policyDisableSandbox = TryReadHklmDword(@"SOFTWARE\Policies\Microsoft\Windows\Sandbox", "DisableSandbox");
        var policyAllowSandbox = TryReadHklmDword(@"SOFTWARE\Policies\Microsoft\Windows\Sandbox", "AllowSandbox");
        bool? policyExplicitlyDisabled = null;
        string? policyEvidence = null;
        if (policyAllowMapped.Value == 0)
        {
            policyExplicitlyDisabled = true;
            policyEvidence = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Sandbox\AllowMappedFolders = 0";
        }
        else if (policyDisableSandbox.Value == 1)
        {
            policyExplicitlyDisabled = true;
            policyEvidence = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Sandbox\DisableSandbox = 1";
        }
        else if (policyAllowSandbox.Value == 0)
        {
            policyExplicitlyDisabled = true;
            policyEvidence = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\Sandbox\AllowSandbox = 0";
        }
        var policyQueryDenied = policyAllowMapped.AccessDenied || policyDisableSandbox.AccessDenied || policyAllowSandbox.AccessDenied;

        // 3. Check direct hardware virtualization evidence without elevation:
        //    When Hyper-V is active, CPUID leaf 1 ECX bit 31 (HypervisorPresent) is 1 and
        //    PF_VIRT_FIRMWARE_ENABLED is masked to false by the hypervisor. Only flag
        //    virtualization disabled when BOTH HypervisorPresent == false AND PF_VIRT_FIRMWARE_ENABLED == false.
        bool? hypervisorPresent = null;
        if (X86Base.IsSupported)
        {
            var (_, _, ecx, _) = X86Base.CpuId(1, 0);
            hypervisorPresent = (ecx & unchecked((int)0x80000000)) != 0;
        }
        bool? virtFirmwareEnabled = IsProcessorFeaturePresent(21); // PF_VIRT_FIRMWARE_ENABLED

        // 4. Check direct pending-reboot key readability
        var rebootCheck = TryCheckHklmKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");

        // 5. Check WindowsSandbox.exe and wsb.exe CLI
        var sandboxGuiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsSandbox.exe");
        var hasSandboxGui = File.Exists(sandboxGuiPath);
        var wsbPath = ResolveWsbExecutablePath();

        bool? wsbHelpOk = null;
        string? wsbHelpOut = null;
        List<string> missingOps = [];
        string? wsbVersion = null;
        bool? wsbListOk = null;
        string? wsbListDiag = null;

        if (wsbPath is not null)
        {
            var help = await RunWsbCommandAsync(wsbPath, ["--help"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            wsbHelpOk = !help.TimedOut && help.ExitCode == 0;
            wsbHelpOut = CombineDiagnostic(help);
            if (wsbHelpOk == true)
            {
                var helpText = help.Stdout + "\n" + help.Stderr;
                missingOps = RequiredSubcommands
                    .Where(op => !helpText.Contains(op, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (missingOps.Count == 0)
                {
                    var versionRes = await RunWsbCommandAsync(wsbPath, ["--version"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                    wsbVersion = !versionRes.TimedOut && versionRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(versionRes.Stdout)
                        ? versionRes.Stdout.Trim().Split('\n')[0].Trim()
                        : "unknown";

                    var listRes = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    wsbListOk = !listRes.TimedOut && listRes.ExitCode == 0 && listRes.Stdout.TrimStart().StartsWith('{');
                    if (wsbListOk != true)
                        wsbListDiag = CombineDiagnostic(listRes);
                }
            }
        }

        return new HostSandboxEnvironmentSnapshot(
            isWindows,
            isNativeX64,
            editionRead.Value,
            productNameRead.Value,
            productType,
            editionRead.AccessDenied,
            hasSandboxGui,
            wsbPath,
            wsbHelpOk,
            wsbHelpOut,
            missingOps,
            wsbVersion,
            wsbListOk,
            wsbListDiag,
            policyExplicitlyDisabled,
            policyEvidence,
            policyQueryDenied,
            hypervisorPresent,
            virtFirmwareEnabled,
            false,
            rebootCheck.Exists,
            rebootCheck.AccessDenied);
    }

    public static SandboxCapabilityReport ClassifyHostEnvironment(HostSandboxEnvironmentSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var editionSummary = FormatEditionSummary(s.EditionId, s.ProductName, s.ProductType);
        var permissionNote = BuildPermissionNote(s);

        if (!s.IsWindows || !s.IsNativeX64)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.UnsupportedPlatform,
                "当前系统不是原生 Windows x64 环境",
                "隔离试用需要原生 64 位 Windows x64 宿主机与 Windows 沙盒支持。Contain 的其他功能不受影响，本页仍可查看本地 .exe 信息与计算 SHA-256。",
                "如需在沙箱中试运行程序，请在受支持的 Windows x64 电脑上运行 Contain。",
                SandboxCapabilityPrimaryAction.GoToProjectPackaging,
                "前往打包项目",
                WindowsEditionSummary: editionSummary,
                PermissionNote: permissionNote);
        }

        // 1. Direct Home Edition check (Home editions do not include Windows Sandbox)
        if (IsHomeEdition(s.EditionId, s.ProductType) && !s.HasWindowsSandboxExe && s.WsbExecutablePath is null)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.HomeEditionUnsupported,
                "当前 Windows 家庭版不支持 Windows 沙盒",
                $"已检测到当前系统为 Windows 家庭版（{editionSummary ?? "Home / Core"}），系统本身不提供 Windows 沙盒功能。Contain 的「打包项目」「我的记录」「检查交付包」，以及本页的只读文件信息查看与 SHA-256 计算均可正常使用。",
                "你可以继续在本页查看本地 .exe 的架构、产品版本与 SHA-256，或切换到「打包项目」等其他功能。",
                SandboxCapabilityPrimaryAction.GoToProjectPackaging,
                "前往打包项目",
                WindowsEditionSummary: editionSummary,
                PermissionNote: permissionNote);
        }

        // 2. Direct Group Policy evidence explicitly blocking Sandbox or MappedFolders
        if (s.PolicyExplicitlyDisabled == true)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.PolicyBlocked,
                "系统策略当前阻止了隔离试用所需的沙盒能力",
                $"已检测到明确的组策略限制（{s.PolicyEvidence}）。Contain 不会修改系统策略；「打包项目」「我的记录」「检查交付包」以及本页只读文件查看与 SHA-256 计算均可正常使用。",
                "如为受管设备，请联系管理员或自行确认组策略配置，调整后再点击“重新检测”。",
                SandboxCapabilityPrimaryAction.Recheck,
                "重新检测",
                WsbExecutablePath: s.WsbExecutablePath,
                WsbVersion: s.WsbVersion,
                WindowsEditionSummary: editionSummary,
                PermissionNote: permissionNote,
                Diagnostic: s.PolicyEvidence ?? "");
        }

        // 3. wsb.exe present: check subcommands and live `wsb list --raw`
        if (!string.IsNullOrEmpty(s.WsbExecutablePath))
        {
            if (s.WsbHelpSucceeded == true && s.MissingWsbSubcommands.Count > 0)
            {
                return new SandboxCapabilityReport(
                    SandboxCapabilityStatus.CliUnavailable,
                    "沙盒可用，但缺少隔离试用所需的 wsb 命令",
                    $"检测到 wsb.exe，但缺少按实例控制必需的子命令：{string.Join("、", s.MissingWsbSubcommands)}。你仍可在本页查看 .exe 文件信息与计算 SHA-256。",
                    "请更新 Windows 11 的 Windows 沙盒组件后点击“重新检测”。",
                    SandboxCapabilityPrimaryAction.Recheck,
                    "重新检测",
                    WsbExecutablePath: s.WsbExecutablePath,
                    WindowsEditionSummary: editionSummary,
                    PermissionNote: permissionNote,
                    Diagnostic: s.WsbHelpOutput ?? "");
            }

            if (s.WsbHelpSucceeded == true && s.WsbListSucceeded == true)
            {
                return new SandboxCapabilityReport(
                    SandboxCapabilityStatus.Ready,
                    "Windows 沙盒已就绪",
                    $"已核验本机支持按实例创建、连接、来宾执行与按 ID 结束（wsb {s.WsbVersion ?? "unknown"}）。",
                    "选择一个本地单文件 Windows x64 .exe，即可查看文件信息或在断网、禁剪贴板的临时沙箱中试运行。",
                    SandboxCapabilityPrimaryAction.None,
                    "",
                    WsbExecutablePath: s.WsbExecutablePath,
                    WsbVersion: s.WsbVersion,
                    WindowsEditionSummary: editionSummary,
                    PermissionNote: permissionNote);
            }

            // wsb.exe exists, but help or list failed -> use ONLY direct evidence, never guess
            var failDiag = !string.IsNullOrWhiteSpace(s.WsbListDiagnostic) ? s.WsbListDiagnostic : (s.WsbHelpOutput ?? "");
            if (IsPolicyBlockedDiagnostic(failDiag))
            {
                return new SandboxCapabilityReport(
                    SandboxCapabilityStatus.PolicyBlocked,
                    "系统策略阻止调用 Windows 沙盒命令行",
                    "调用 wsb.exe 时返回了策略限制错误。Contain 不会修改系统策略，你仍可在本页查看文件信息与计算 SHA-256。",
                    "请确认设备策略是否允许运行 Windows 沙盒，完成后点击“重新检测”。",
                    SandboxCapabilityPrimaryAction.Recheck,
                    "重新检测",
                    WsbExecutablePath: s.WsbExecutablePath,
                    WsbVersion: s.WsbVersion,
                    WindowsEditionSummary: editionSummary,
                    PermissionNote: permissionNote,
                    Diagnostic: failDiag);
            }

            if (s.HypervisorPresent == false && s.VirtualizationFirmwareEnabled == false)
            {
                return new SandboxCapabilityReport(
                    SandboxCapabilityStatus.VirtualizationDisabled,
                    "检测到硬件虚拟化未开启，无法启动 Windows 沙盒",
                    "直接探测显示当前未运行虚拟化管理程序且处理器固件虚拟化未启用。本页仍可查看本地 .exe 文件信息与计算 SHA-256。",
                    "如需使用隔离试用，请由你自行在主板 BIOS/UEFI 中开启虚拟化（Intel VT-x / AMD-V）并重启电脑，再回来点击“重新检测”。",
                    SandboxCapabilityPrimaryAction.Recheck,
                    "重新检测",
                    WsbExecutablePath: s.WsbExecutablePath,
                    WsbVersion: s.WsbVersion,
                    WindowsEditionSummary: editionSummary,
                    PermissionNote: permissionNote,
                    Diagnostic: failDiag);
            }

            if (s.RebootPending == true)
            {
                return new SandboxCapabilityReport(
                    SandboxCapabilityStatus.RebootPending,
                    "Windows 沙盒尚未就绪，且系统存在待完成重启",
                    "已检测到 wsb.exe 但当前无法查询沙盒实例，同时系统存在未完成的重启挂起标记。你仍可在本页查看文件信息与计算 SHA-256。",
                    "如你刚启用或更新过 Windows 沙盒，请在合适时机自行重启电脑后再点击“重新检测”。Contain 不会自动重启电脑。",
                    SandboxCapabilityPrimaryAction.Recheck,
                    "重新检测",
                    WsbExecutablePath: s.WsbExecutablePath,
                    WsbVersion: s.WsbVersion,
                    WindowsEditionSummary: editionSummary,
                    PermissionNote: permissionNote,
                    Diagnostic: failDiag);
            }

            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.Undetermined,
                "检测到 wsb.exe，但沙盒当前无法响应（原因尚未确定）",
                "执行 wsb.exe 未返回有效结果，且普通用户权限下未观测到虚拟化关闭或明确组策略禁用的直接证据。Contain 不会猜测原因，也不影响你使用本页的只读文件查看和其他功能。",
                "请展开“技术详情”查看原始返回信息；确认 Windows 沙盒本身可单独启动后再点击“重新检测”。",
                SandboxCapabilityPrimaryAction.Recheck,
                "重新检测",
                WsbExecutablePath: s.WsbExecutablePath,
                WsbVersion: s.WsbVersion,
                WindowsEditionSummary: editionSummary,
                PermissionNote: permissionNote,
                Diagnostic: failDiag);
        }

        // 4. WindowsSandbox.exe exists, but wsb.exe CLI is absent
        if (s.HasWindowsSandboxExe)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.CliUnavailable,
                "本机已有 Windows 沙盒，但缺少所需的 wsb 命令",
                "检测到 WindowsSandbox.exe，但未找到按实例创建、连接和结束所需的 wsb.exe CLI（Windows 11 24H2 及以上版本的 Windows 沙盒提供）。本页的只读文件查看与 SHA-256 计算不受影响。",
                "请更新 Windows 沙盒组件或升级到提供 wsb.exe 的 Windows 11 版本后点击“重新检测”。",
                SandboxCapabilityPrimaryAction.Recheck,
                "重新检测",
                WindowsEditionSummary: editionSummary,
                PermissionNote: permissionNote);
        }

        // 5. Neither WindowsSandbox.exe nor wsb.exe exists
        if (s.HypervisorPresent == false && s.VirtualizationFirmwareEnabled == false)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.VirtualizationDisabled,
                "检测到硬件虚拟化未开启，暂无法使用 Windows 沙盒",
                "未检测到 Windows 沙盒组件，且直接探测显示硬件虚拟化未启用。Contain 的「打包项目」「我的记录」「检查交付包」以及本页的只读文件信息与 SHA-256 计算均可正常使用。",
                "如需使用隔离试用，请先由你自行在 BIOS/UEFI 中开启虚拟化，再开启“Windows 沙盒”功能并重启电脑，完成后点击“重新检测”。",
                SandboxCapabilityPrimaryAction.Recheck,
                "重新检测",
                WindowsEditionSummary: editionSummary,
                PermissionNote: permissionNote);
        }

        if (IsSupportedSandboxEdition(s.EditionId, s.ProductType))
        {
            var rebootHint = s.RebootPending == true
                ? "另检测到系统当前有待完成的重启；如果你刚勾选过“Windows 沙盒”，重启后即可生效。"
                : "";
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.SupportedFeatureNotEnabled,
                "当前系统支持 Windows 沙盒，但功能尚未启用",
                $"已确认为受支持的 Windows 版本（{editionSummary ?? s.EditionId}），目前未检测到 WindowsSandbox.exe 与 wsb.exe。{rebootHint}Contain 不会自动修改系统功能或重启电脑；在此之前你仍可在本页查看文件信息并计算 SHA-256。",
                "如需使用隔离试用，点击下方“打开 Windows 功能”，自行勾选“Windows 沙盒”并在合适时机重启电脑，完成后回到本页点击“重新检测”。",
                SandboxCapabilityPrimaryAction.OpenWindowsFeatures,
                "打开 Windows 功能",
                WindowsEditionSummary: editionSummary,
                PermissionNote: permissionNote);
        }

        return new SandboxCapabilityReport(
            SandboxCapabilityStatus.Undetermined,
            "未能确认 Windows 沙盒状态（原因尚未确定）",
            "未检测到 WindowsSandbox.exe 与 wsb.exe，且当前未能获取足够的系统版本或功能证据，无法区分是系统版本不支持还是功能尚未启用。Contain 不会擅自修改系统；「打包项目」「我的记录」「检查交付包」以及本页只读文件查看与 SHA-256 计算均可正常使用。",
            "确认当前 Windows 版本支持并已启用“Windows 沙盒”后，可点击“重新检测”。",
            SandboxCapabilityPrimaryAction.Recheck,
            "重新检测",
            WindowsEditionSummary: editionSummary,
            PermissionNote: permissionNote ?? "普通用户权限下未能读取完整的系统版本信息，未对不可用原因做猜测。");
    }

    internal static bool IsHomeEdition(string? editionId, uint? productType)
    {
        if (!string.IsNullOrWhiteSpace(editionId))
        {
            var e = editionId.Trim();
            if (e.Equals("Core", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // PRODUCT_CORE_N (0x62), PRODUCT_CORE_COUNTRYSPECIFIC (0x63), PRODUCT_CORE_SINGLELANGUAGE (0x64),
        // PRODUCT_CORE (0x65), PRODUCT_CORE_CONNECTED (0x67)
        return productType is 0x62 or 0x63 or 0x64 or 0x65 or 0x67;
    }

    internal static bool IsSupportedSandboxEdition(string? editionId, uint? productType)
    {
        if (IsHomeEdition(editionId, productType)) return false;
        if (!string.IsNullOrWhiteSpace(editionId))
        {
            var e = editionId.Trim();
            if (e.StartsWith("Professional", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
                || e.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase)
                || e.Equals("ServerRdsh", StringComparison.OrdinalIgnoreCase)
                || e.Equals("CloudEdition", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        // PRODUCT_ENTERPRISE (0x04), PRODUCT_PROFESSIONAL (0x30), PRODUCT_PROFESSIONAL_N (0x31),
        // PRODUCT_EDUCATION (0x79), PRODUCT_EDUCATION_N (0x7A), PRODUCT_ENTERPRISE_S (0x7D),
        // PRODUCT_PRO_WORKSTATION (0xA1), PRODUCT_PRO_WORKSTATION_N (0xA2), PRODUCT_IOTENTERPRISE (0xBC)
        return productType is 0x04 or 0x1B or 0x30 or 0x31 or 0x48 or 0x79 or 0x7A or 0x7D or 0x7E or 0xA1 or 0xA2 or 0xBC or 0xBF;
    }

    private static bool IsPolicyBlockedDiagnostic(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return false;
        return diagnostic.Contains("0x800704EC", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("ERROR_ACCESS_DISABLED_BY_POLICY", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("disabled by policy", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("组策略", StringComparison.Ordinal);
    }

    private static string? FormatEditionSummary(string? editionId, string? productName, uint? productType)
    {
        if (!string.IsNullOrWhiteSpace(productName) && !string.IsNullOrWhiteSpace(editionId))
            return $"{productName.Trim()} ({editionId.Trim()})";
        if (!string.IsNullOrWhiteSpace(editionId))
            return editionId.Trim();
        if (!string.IsNullOrWhiteSpace(productName))
            return productName.Trim();
        return productType is uint pt ? $"ProductType 0x{pt:X}" : null;
    }

    private static string? BuildPermissionNote(HostSandboxEnvironmentSnapshot s)
    {
        var denied = new List<string>();
        if (s.EditionQueryDenied) denied.Add("系统版本注册表");
        if (s.PolicyQueryDenied) denied.Add("组策略注册表");
        if (s.VirtualizationQueryDenied) denied.Add("虚拟化状态");
        if (s.RebootPendingQueryDenied) denied.Add("待重启标记");
        if (denied.Count == 0) return null;
        return $"当前以普通用户权限运行，以下项因权限受限未能读取：{string.Join("、", denied)}（未对其做猜测）。";
    }

    public sealed record StagedSnapshotSummary(
        int FileCount,
        int DirectoryCount,
        long TotalBytes,
        string EntryRelativePath);

    /// <summary>
    /// Copies a single EXE or verified portable folder snapshot into inputDir without following
    /// any reparse points, checking free disk space beforehand, supporting cancellation and live
    /// progress, and verifying byte-length completeness for every copied file.
    /// </summary>
    public async Task<StagedSnapshotSummary> StageInputSnapshotAsync(
        TrialLaunchTarget target,
        string inputDir,
        IProgress<TrialStagingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Directory.CreateDirectory(inputDir);

        if (target.Kind == TrialInputKind.SingleFile)
        {
            var srcPath = Path.GetFullPath(target.SourcePath);
            if (!File.Exists(srcPath))
                throw new IsolatedTrialException("source_missing", $"所选程序“{target.DisplayName}”不存在或已被移走。", "请重新选择本地 .exe 文件。", failedStage: "准备输入快照");
            if (IsReparsePointPath(srcPath))
                throw new IsolatedTrialException("reparse_point_detected", $"所选文件“{target.DisplayName}”是链接或重解析点，已停止本次准备。", "请直接选择真实的源 .exe 文件。", failedStage: "准备输入快照");

            long len;
            try { len = new FileInfo(srcPath).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IsolatedTrialException("source_read_failed", $"无法读取所选程序“{target.DisplayName}”：{ex.Message}", "请确认文件未被移动或独占占用。", $"{ex.GetType().Name}: {ex.Message}", ex, failedStage: "准备输入快照");
            }

            EnsureSufficientStagingSpace(inputDir, len, "准备输入快照");
            progress?.Report(new TrialStagingProgress("正在复制所选程序到本次暂存目录…", 0, 1, 0, len, target.EntryRelativePath));

            var dstPath = Path.Combine(inputDir, target.EntryRelativePath);
            var copiedBytes = await CopyFileCheckedAsync(srcPath, dstPath, len, target.EntryRelativePath, progress, 0, 1, 0, len, cancellationToken).ConfigureAwait(false);
            if (copiedBytes != len || !File.Exists(dstPath) || new FileInfo(dstPath).Length != len)
            {
                throw new IsolatedTrialException(
                    "staging_copy_incomplete",
                    $"复制“{target.DisplayName}”不完整（预期 {len:N0} 字节，实际 {copiedBytes:N0} 字节），已停止准备。",
                    "请确认源程序未在复制过程中被修改且磁盘空间充足后重试。",
                    failedStage: "核验输入快照");
            }

            progress?.Report(new TrialStagingProgress("输入快照准备完成，正在启动 Windows 沙箱…", 1, 1, len, len, ""));
            return new StagedSnapshotSummary(1, 0, len, target.EntryRelativePath);
        }

        // Folder mode: strictly validate source folder & entry containment (re-checking reparse points for TOCTOU safety)
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.SourcePath));
        if (!Directory.Exists(fullRoot))
            throw new IsolatedTrialException("folder_missing", $"所选软件文件夹“{target.DisplayName}”不存在或已被移走。", "请重新选择软件文件夹。", failedStage: "准备输入快照");
        if (IsReparsePointPath(fullRoot))
            throw new IsolatedTrialException("reparse_point_detected", $"所选软件文件夹“{target.DisplayName}”是链接或重解析点，已停止本次准备。", "请直接选择真实的源文件夹。", failedStage: "准备输入快照");
        if (IsPathEqualOrUnder(Path.GetFullPath(stagingBaseDirectory), fullRoot))
            throw new IsolatedTrialException("folder_contains_staging", "所选目录包含 Contain 暂存目录，已停止准备。", "请选择具体的便携软件目录。", failedStage: "准备输入快照");

        if (!ValidateFolderEntryPath(fullRoot, target.EntryRelativePath, out _, out var entryErr))
        {
            throw new IsolatedTrialException(
                "invalid_folder_entry",
                $"所选入口程序无效：{entryErr}",
                "请重新选择位于该软件文件夹内的有效 .exe 入口程序。",
                $"entry={target.EntryRelativePath}; reason={entryErr}",
                failedStage: "核验入口程序");
        }

        // Enumerate live folder plan (fail-closed on any reparse point or unreadable entry)
        var relDirs = new List<string>();
        var filesToCopy = new List<(string SourcePath, string RelativePath, long ExpectedLength)>();
        long totalBytes = 0;

        var stack = new Stack<string>();
        stack.Push(fullRoot);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cur = stack.Pop();
            var curRel = GetRelativeNormalized(fullRoot, cur);
            if (!string.IsNullOrEmpty(curRel))
                relDirs.Add(curRel);

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(cur).ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                var relLabel = string.IsNullOrEmpty(curRel) ? target.DisplayName : curRel;
                throw new IsolatedTrialException(
                    "staging_copy_read_failed",
                    $"准备输入快照时无法读取目录“{relLabel}”：{ex.Message}",
                    "请确认当前用户对该目录拥有读取权限后重试。",
                    $"{ex.GetType().Name}: {ex.Message}",
                    ex,
                    failedStage: "准备输入快照");
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rel = GetRelativeNormalized(fullRoot, entry);
                FileAttributes attrs;
                try { attrs = File.GetAttributes(entry); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    throw new IsolatedTrialException(
                        "staging_copy_read_failed",
                        $"准备输入快照时无法访问“{rel}”：{ex.Message}",
                        "请确认文件未被独占占用或权限受限后重试。",
                        $"{ex.GetType().Name}: {ex.Message}",
                        ex,
                        failedStage: "准备输入快照");
                }

                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IsolatedTrialException(
                        "reparse_point_detected",
                        $"准备输入快照时检测到链接、联接点或其他重解析点（{rel}），已停止本次准备并清理暂存。",
                        "为保证实际带入沙箱的范围与界面展示一致，Contain 不跟随目录链接、联接点或符号链接。请移除后重试。",
                        $"reparse_entry={rel}",
                        failedStage: "准备输入快照");
                }

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    stack.Push(entry);
                }
                else
                {
                    long fLen;
                    try { fLen = new FileInfo(entry).Length; }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        throw new IsolatedTrialException(
                            "staging_copy_read_failed",
                            $"准备输入快照时无法读取“{rel}”：{ex.Message}",
                            "请确认文件未被独占锁定或移动后重试。",
                            $"{ex.GetType().Name}: {ex.Message}",
                            ex,
                            failedStage: "准备输入快照");
                    }
                    filesToCopy.Add((entry, rel, fLen));
                    totalBytes += fLen;
                }
            }
        }

        EnsureSufficientStagingSpace(inputDir, totalBytes, "准备输入快照");

        var totalFiles = filesToCopy.Count;
        progress?.Report(new TrialStagingProgress("正在复制软件文件夹到本次暂存目录…", 0, totalFiles, 0, totalBytes, ""));

        // Create all subdirectories first to preserve empty relative directories
        foreach (var relDir in relDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(inputDir, relDir));
        }

        var filesCopied = 0;
        long bytesCopiedBeforeCurrent = 0;
        var lastReport = Stopwatch.StartNew();

        foreach (var (srcFile, relPath, expectedLen) in filesToCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dstFile = Path.Combine(inputDir, relPath);
            var dstParent = Path.GetDirectoryName(dstFile);
            if (!string.IsNullOrEmpty(dstParent))
                Directory.CreateDirectory(dstParent);

            var reportForFile = progress is not null && (lastReport.ElapsedMilliseconds >= 80 || expectedLen >= 256 * 1024 || filesCopied == 0)
                ? progress
                : null;
            if (reportForFile is not null) lastReport.Restart();

            var written = await CopyFileCheckedAsync(
                srcFile,
                dstFile,
                expectedLen,
                relPath,
                reportForFile,
                filesCopied,
                totalFiles,
                bytesCopiedBeforeCurrent,
                totalBytes,
                cancellationToken).ConfigureAwait(false);

            long actualDstLen;
            try { actualDstLen = new FileInfo(dstFile).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IsolatedTrialException(
                    "staging_copy_incomplete",
                    $"核验已复制文件“{relPath}”失败：{ex.Message}",
                    "请检查磁盘状态后重试。",
                    $"{ex.GetType().Name}: {ex.Message}",
                    ex,
                    failedStage: "核验输入快照");
            }

            if (written != expectedLen || actualDstLen != expectedLen)
            {
                throw new IsolatedTrialException(
                    "staging_copy_incomplete",
                    $"复制文件“{relPath}”不完整（预期 {expectedLen:N0} 字节，实际 {actualDstLen:N0} 字节），已清理本次暂存。",
                    "源文件可能在复制期间发生了变化或磁盘空间不足，请确认后重试。",
                    $"file={relPath}; expected={expectedLen}; written={written}; actual={actualDstLen}",
                    failedStage: "核验输入快照");
            }

            filesCopied++;
            bytesCopiedBeforeCurrent += written;
            if (progress is not null && (filesCopied % 8 == 0 || filesCopied == totalFiles || lastReport.ElapsedMilliseconds >= 80))
            {
                progress.Report(new TrialStagingProgress("正在复制软件文件夹到本次暂存目录…", filesCopied, totalFiles, bytesCopiedBeforeCurrent, totalBytes, relPath));
                lastReport.Restart();
            }
        }

        // Final integrity check: exact entry EXE exists inside inputDir snapshot
        var normalizedEntryRel = target.EntryRelativePath.Replace('/', '\\').Trim();
        var stagedEntryPath = Path.GetFullPath(Path.Combine(inputDir, normalizedEntryRel));
        if (!IsPathStrictlyUnder(stagedEntryPath, Path.GetFullPath(inputDir)) || !File.Exists(stagedEntryPath))
        {
            throw new IsolatedTrialException(
                "staging_entry_missing",
                $"输入快照核验失败：未在暂存目录中找到入口程序“{normalizedEntryRel}”。",
                "请重新扫描软件文件夹并选择入口程序后再试。",
                $"staged_entry={stagedEntryPath}",
                failedStage: "核验输入快照");
        }

        progress?.Report(new TrialStagingProgress("输入快照准备完成，正在启动 Windows 沙箱…", filesCopied, totalFiles, totalBytes, totalBytes, ""));
        return new StagedSnapshotSummary(filesCopied, relDirs.Count, totalBytes, normalizedEntryRel);
    }

    private void EnsureSufficientStagingSpace(string stagingPath, long requiredBytes, string failedStage)
    {
        long? avail = null;
        if (availableFreeSpaceProvider is not null)
        {
            avail = availableFreeSpaceProvider(stagingPath);
        }
        else
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(stagingPath));
                if (!string.IsNullOrEmpty(root))
                    avail = new DriveInfo(root).AvailableFreeSpace;
            }
            catch { avail = null; }
        }

        if (avail is long freeBytes)
        {
            var neededWithMargin = requiredBytes + StagingDiskSafetyMarginBytes;
            if (freeBytes < neededWithMargin)
            {
                throw new IsolatedTrialException(
                    "insufficient_disk_space",
                    $"暂存磁盘空间不足：准备该输入快照至少需要 {FormatByteSize(neededWithMargin)}（含缓冲），当前仅剩 {FormatByteSize(freeBytes)}。",
                    "请释放临时目录所在磁盘的空间，或改选更小的软件文件夹后再试。",
                    $"required={requiredBytes}; margin={StagingDiskSafetyMarginBytes}; available={freeBytes}",
                    failedStage: failedStage);
            }
        }
    }

    private static async Task<long> CopyFileCheckedAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string relativeDisplayPath,
        IProgress<TrialStagingProgress>? progress,
        int filesCopiedSoFar,
        int totalFiles,
        long bytesCopiedBeforeFile,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            await using var dest = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long copied = 0;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;

                if (progress is not null && sw.ElapsedMilliseconds >= 100)
                {
                    progress.Report(new TrialStagingProgress(
                        "正在复制软件文件夹到本次暂存目录…",
                        filesCopiedSoFar,
                        totalFiles,
                        bytesCopiedBeforeFile + copied,
                        totalBytes,
                        relativeDisplayPath));
                    sw.Restart();
                }
            }
            await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
            return copied;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Distinguish disk-full IOException (ERROR_DISK_FULL = 0x70, ERROR_HANDLE_DISK_FULL = 0x27)
            var hr = ex.HResult & 0xFFFF;
            if (ex is IOException && (hr == 0x70 || hr == 0x27))
            {
                throw new IsolatedTrialException(
                    "insufficient_disk_space",
                    $"复制“{relativeDisplayPath}”时暂存磁盘空间不足：{ex.Message}",
                    "请释放临时目录所在磁盘空间后重试。",
                    $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}",
                    ex,
                    failedStage: "准备输入快照");
            }

            throw new IsolatedTrialException(
                "staging_copy_read_failed",
                $"准备输入快照时无法读取或复制“{relativeDisplayPath}”：{ex.Message}",
                "请确认文件未被移动、删除或被其他程序独占锁定，且当前用户拥有读取权限后重试。",
                $"{ex.GetType().Name}: {ex.Message}",
                ex,
                failedStage: "准备输入快照");
        }
    }

    public Task<SandboxStartResult> StartSandboxAsync(
        TrialExecutableInfo executable,
        CancellationToken cancellationToken = default) =>
        StartSandboxAsync(TrialLaunchTarget.FromSingleExecutable(executable), null, cancellationToken);

    public async Task<SandboxStartResult> StartSandboxAsync(
        TrialLaunchTarget target,
        IProgress<TrialStagingProgress>? stagingProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.EntryExecutable.IsRunnableInX64Sandbox)
        {
            throw new IsolatedTrialException(
                "unsupported_arch",
                $"“{target.EntryRelativePath}”为 {target.EntryExecutable.ArchitectureLabel}，不能在 Windows x64 沙箱中运行。",
                "本页已保留该文件的只读信息与 SHA-256 计算功能；如需沙箱试运行，请选择 Windows x64/x86 入口程序。",
                failedStage: "核验入口架构");
        }
        var capability = await ProbeCapabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.IsReady || string.IsNullOrEmpty(capability.WsbExecutablePath))
        {
            throw new IsolatedTrialException(
                "capability_unavailable",
                capability.Headline,
                capability.NextStep,
                capability.Diagnostic,
                failedStage: "检测沙盒能力");
        }

        var wsbPath = capability.WsbExecutablePath;
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var stagingRoot = Path.Combine(stagingBaseDirectory, sessionId);
        var inputDir = Path.Combine(stagingRoot, "input");
        var bootstrapDir = Path.Combine(stagingRoot, "bootstrap");

        string? createdInstanceId = null;
        try
        {
            Directory.CreateDirectory(inputDir);
            Directory.CreateDirectory(bootstrapDir);

            // 1. Complete & verify input staging snapshot BEFORE starting Windows Sandbox!
            var staged = await StageInputSnapshotAsync(target, inputDir, stagingProgress, cancellationToken).ConfigureAwait(false);

            // Write UTF-8 JSON target metadata (schema 2) + static pure-ASCII launcher script into bootstrapDir
            var targetJson = JsonSerializer.Serialize(new
            {
                schema_version = 2,
                session_id = sessionId,
                input_kind = target.Kind == TrialInputKind.Folder ? "folder" : "single_file",
                entry_relative_path = staged.EntryRelativePath,
                file_name = target.EntryExecutable.FileName,
                expected_file_count = staged.FileCount
            });
            await File.WriteAllTextAsync(Path.Combine(bootstrapDir, "target.json"), targetJson, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(bootstrapDir, "launch-trial.ps1"), GuestLauncherScript, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var configXml = BuildSandboxConfigXml(inputDir, bootstrapDir);

            // 2. Create isolated sandbox instance via `wsb start --config <xml> --raw`
            var startRes = await RunWsbCommandAsync(wsbPath, ["start", "--config", configXml, "--raw"], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (startRes.TimedOut)
            {
                throw new IsolatedTrialException(
                    "wsb_start_timeout",
                    "创建 Windows 沙箱实例超时。",
                    "请检查宿主机内存与虚拟化服务状态后重试；若持续超时，可确认 Windows 沙盒能否单独打开。",
                    CombineDiagnostic(startRes),
                    failedStage: "启动 Windows 沙箱");
            }
            if (startRes.ExitCode != 0)
            {
                if (IsAnotherSandboxRunningError(startRes.ExitCode, startRes.Stderr))
                {
                    throw new IsolatedTrialException(
                        "sandbox_already_running",
                        "本机已有另一个正在运行的 Windows 沙箱实例。",
                        "Windows 沙盒当前仅允许同时运行一个实例；Contain 不会自动关闭你自行打开的其他沙箱。请先自行关闭另一个 Windows 沙箱后，再重新点击“开始隔离试用”。",
                        CombineDiagnostic(startRes),
                        failedStage: "启动 Windows 沙箱");
                }

                throw new IsolatedTrialException(
                    "wsb_start_failed",
                    $"创建 Windows 沙箱实例失败（退出码 {startRes.ExitCode}）。",
                    "请检查本机是否允许启动 Windows 沙盒（如内存不足、策略限制或虚拟化未就绪），处理后可重新点击“开始隔离试用”。",
                    CombineDiagnostic(startRes),
                    failedStage: "启动 Windows 沙箱");
            }

            createdInstanceId = ParseStartedInstanceId(startRes.Stdout);
            if (string.IsNullOrEmpty(createdInstanceId))
            {
                throw new IsolatedTrialException(
                    "wsb_start_no_id",
                    "Windows 沙箱启动命令未返回有效的实例 ID。",
                    "无法安全跟踪该实例，请检查本机 Windows 沙盒 CLI 版本后重试。",
                    CombineDiagnostic(startRes),
                    failedStage: "启动 Windows 沙箱");
            }

            // Persist minimal pending-session metadata immediately after obtaining the real UUID,
            // so even an abnormal exit during connect/readiness can still recover this exact instance.
            var recoveryDisplay = target.Kind == TrialInputKind.Folder
                ? $"{target.DisplayName} ({staged.EntryRelativePath})"
                : target.EntryExecutable.FileName;
            SavePendingSession(CreatePendingRecord(sessionId, createdInstanceId, stagingRoot, recoveryDisplay));

            // 3. Open interactive Sandbox window detached (NEVER redirect pipes or WaitForExit!)
            int? connectPid = LaunchDetachedConnect(wsbPath, createdInstanceId);

            // 4. Wait up to readinessTimeout (default 60s) for THIS exact instance to respond to
            //    a side-effect-free ExistingLogin probe confirming the read-only input mount.
            var readySw = Stopwatch.StartNew();
            WsbCommandResult? lastProbe = null;
            var ready = false;
            const string probeCmd = @"cmd.exe /c if exist C:\ContainTrialInput (if exist C:\ContainTrialBootstrap\target.json (exit 0) else (exit 3)) else (exit 2)";

            while (readySw.Elapsed < readinessTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Verify the instance is still alive before probing (only trust a validly parsed list!)
                var listCheck = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                if (!listCheck.TimedOut
                    && listCheck.ExitCode == 0
                    && TryParseRunningInstanceIds(listCheck.Stdout, out var ids)
                    && !ids.Contains(createdInstanceId, StringComparer.OrdinalIgnoreCase))
                {
                    throw new IsolatedTrialException(
                        "sandbox_exited_during_startup",
                        "Windows 沙箱在初始化完成前已关闭。",
                        "可能是窗口被提前关闭或沙箱内部启动中止；请确认回滚状态后再重新尝试。",
                        $"instance={createdInstanceId}",
                        failedStage: "等待沙箱就绪");
                }

                var remaining = readinessTimeout - readySw.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                var probeTimeout = remaining < TimeSpan.FromSeconds(8) ? remaining : TimeSpan.FromSeconds(8);
                lastProbe = await RunWsbCommandAsync(
                    wsbPath,
                    ["exec", "--id", createdInstanceId, "-r", "ExistingLogin", "-c", probeCmd, "--raw"],
                    probeTimeout,
                    cancellationToken).ConfigureAwait(false);

                if (!lastProbe.TimedOut && lastProbe.ExitCode == 0 && ParseExecExitCode(lastProbe.Stdout) == 0)
                {
                    ready = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
            }

            if (!ready)
            {
                throw new IsolatedTrialException(
                    "sandbox_ready_timeout",
                    $"等待 Windows 沙箱桌面就绪超时（已等待 {Math.Round(readySw.Elapsed.TotalSeconds)} 秒）。",
                    "请确认交互窗口未被安全软件拦截，并核对下方回滚状态后重试。",
                    lastProbe is null ? $"instance={createdInstanceId}" : $"instance={createdInstanceId}; {CombineDiagnostic(lastProbe)}",
                    failedStage: "等待沙箱就绪");
            }

            return new SandboxStartResult(
                sessionId,
                createdInstanceId,
                stagingRoot,
                inputDir,
                connectPid,
                DateTimeOffset.Now,
                target.Kind,
                staged.EntryRelativePath,
                staged.FileCount);
        }
        catch (Exception ex)
        {
            // Perform real verified rollback ONLY on the instance and staging folder created in this failed attempt,
            // and propagate the true observed InstanceState + StagingState (never hardcoding "cleaned").
            var rollback = await StopAndCleanupAsync(createdInstanceId, stagingRoot, CancellationToken.None).ConfigureAwait(false);
            if (rollback.IsFullyCleaned)
            {
                ClearPendingSession(createdInstanceId, sessionId);
            }

            if (ex is OperationCanceledException)
            {
                throw new IsolatedTrialException(
                    "preparation_cancelled",
                    createdInstanceId is null
                        ? "已取消准备本次输入快照，未启动沙箱。"
                        : "已取消启动本次隔离试用。",
                    "可随时重新点击“开始隔离试用”或改选其他程序/目录。",
                    rollback.Diagnostic,
                    ex,
                    rollbackResult: rollback,
                    createdInstanceId: createdInstanceId,
                    stagingRootPath: stagingRoot,
                    sessionId: sessionId,
                    failedStage: createdInstanceId is null ? "准备输入快照" : "启动 Windows 沙箱");
            }

            if (ex is IsolatedTrialException trialEx)
            {
                throw new IsolatedTrialException(
                    trialEx.Code,
                    trialEx.Message,
                    trialEx.NextStep,
                    string.IsNullOrWhiteSpace(rollback.Diagnostic)
                        ? trialEx.Diagnostic
                        : string.IsNullOrWhiteSpace(trialEx.Diagnostic)
                            ? rollback.Diagnostic
                            : $"{trialEx.Diagnostic}\nrollback: {rollback.Diagnostic}",
                    trialEx.InnerException,
                    rollbackResult: rollback,
                    createdInstanceId: createdInstanceId,
                    stagingRootPath: stagingRoot,
                    sessionId: sessionId,
                    failedStage: trialEx.FailedStage);
            }

            throw new IsolatedTrialException(
                "start_unexpected_error",
                ex.Message,
                "请先核对下方回滚状态与系统沙盒状态，再重新尝试。",
                string.IsNullOrWhiteSpace(rollback.Diagnostic)
                    ? $"{ex.GetType().Name}: {ex.Message}"
                    : $"{ex.GetType().Name}: {ex.Message}\nrollback: {rollback.Diagnostic}",
                ex,
                rollbackResult: rollback,
                createdInstanceId: createdInstanceId,
                stagingRootPath: stagingRoot,
                sessionId: sessionId,
                failedStage: createdInstanceId is null ? "准备输入快照" : "启动 Windows 沙箱");
        }
    }

    public Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(
        SandboxStartResult session,
        TrialExecutableInfo executable,
        CancellationToken cancellationToken = default) =>
        LaunchProgramInSandboxAsync(session, TrialLaunchTarget.FromSingleExecutable(executable), cancellationToken);

    public async Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(
        SandboxStartResult session,
        TrialLaunchTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);
        var wsbPath = ResolveWsbExecutablePath()
            ?? throw new IsolatedTrialException("wsb_missing", "找不到 wsb.exe，无法在沙箱内启动程序。");

        var entryDisplay = string.IsNullOrWhiteSpace(session.EntryRelativePath)
            ? target.EntryRelativePath
            : session.EntryRelativePath;
        var launchTimeoutSeconds = Math.Clamp(30 + (int)(target.TotalBytes / (2 * 1024 * 1024)), 30, 180);

        const string launchCmd = @"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\ContainTrialBootstrap\launch-trial.ps1";
        var execRes = await RunWsbCommandAsync(
            wsbPath,
            ["exec", "--id", session.InstanceId, "-r", "ExistingLogin", "-c", launchCmd, "--raw"],
            TimeSpan.FromSeconds(launchTimeoutSeconds),
            cancellationToken).ConfigureAwait(false);

        if (execRes.TimedOut)
        {
            return new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                -1,
                "沙箱已打开，但等待来宾复制并启动入口程序超时。你可以在沙箱窗口内的 C:\\ContainTrialRun 中查看实际情况，或点击“结束试用”。",
                CombineDiagnostic(execRes));
        }

        if (execRes.ExitCode != 0)
        {
            return new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                execRes.ExitCode,
                "沙箱已打开，但向来宾下发启动命令失败（可能沙箱会话尚未就绪或已被关闭）。",
                CombineDiagnostic(execRes));
        }

        var guestCode = ParseExecExitCode(execRes.Stdout) ?? -1;
        var guestWorkDir = Path.GetDirectoryName(Path.Combine(GuestRunFolder, entryDisplay)) ?? GuestRunFolder;
        var scopePhrase = target.Kind == TrialInputKind.Folder
            ? $"已将软件目录“{target.DisplayName}”（{session.StagedFileCount} 个文件）复制到来宾可写目录 {GuestRunFolder}，并以 {guestWorkDir} 为工作目录启动入口“{entryDisplay}”"
            : $"已将“{entryDisplay}”复制到来宾可写目录 {GuestRunFolder} 并启动入口";

        return guestCode switch
        {
            0 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.StartedAndRunning,
                0,
                $"{scopePhrase}（已确认启动，1 秒后入口进程仍在运行）。"),
            20 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.StartedAndExitedZero,
                20,
                $"{scopePhrase}（入口进程已执行完毕并正常退出，返回码 0；沙箱仍保持打开）。"),
            21 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.StartedAndExitedNonZero,
                21,
                target.Kind == TrialInputKind.Folder
                    ? $"{scopePhrase}，但入口进程启动后立即异常退出。若程序需要联网或系统运行库，在离线沙箱中可能无法运行。"
                    : $"{scopePhrase}，但入口进程启动后立即异常退出。若程序依赖同目录其他文件，请改用“选择软件文件夹”；若需联网下载，在离线沙箱中可能无法运行。"),
            12 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                12,
                $"沙箱已打开，但在只读输入目录 C:\\ContainTrialInput 中未找到入口程序“{entryDisplay}”。"),
            13 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                13,
                "沙箱已打开，但将输入快照复制到来宾可写目录 C:\\ContainTrialRun 时失败或不完整。"),
            14 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                14,
                $"沙箱已打开，但无法启动入口“{entryDisplay}”（进程未能创建，可能不兼容当前离线环境或被系统阻止）。"),
            _ => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                guestCode,
                $"沙箱已打开，但来宾启动脚本返回异常状态（代码 {guestCode}）。",
                CombineDiagnostic(execRes))
        };
    }

    public Task<SandboxInstancePollResult> PollSessionAsync(
        SandboxStartResult session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return QueryInstanceStateAsync(session.InstanceId, session.StagingRootPath, cancellationToken);
    }

    public async Task<SandboxInstancePollResult> QueryInstanceStateAsync(
        string instanceId,
        string? stagingRootPath,
        CancellationToken cancellationToken = default)
    {
        var stagingState = InspectStagingState(stagingRootPath);
        if (string.IsNullOrWhiteSpace(instanceId) || !Guid.TryParse(instanceId.Trim(), out var parsedGuid))
            return new SandboxInstancePollResult(SandboxInstanceState.Unknown, null, stagingState, "invalid_instance_id");

        var canonicalId = parsedGuid.ToString("D");
        var wsbPath = ResolveWsbExecutablePath();
        if (wsbPath is null)
            return new SandboxInstancePollResult(SandboxInstanceState.Unknown, null, stagingState, "wsb.exe unavailable");

        var listRes = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        if (listRes.TimedOut || listRes.ExitCode != 0)
            return new SandboxInstancePollResult(SandboxInstanceState.Unknown, null, stagingState, "wsb list failed: " + CombineDiagnostic(listRes));

        if (!TryParseRunningInstanceIds(listRes.Stdout, out var ids))
            return new SandboxInstancePollResult(SandboxInstanceState.Unknown, null, stagingState, "wsb list returned unparseable output: " + CombineDiagnostic(listRes));

        var present = ids.Contains(canonicalId, StringComparer.OrdinalIgnoreCase);
        bool? ownedWindowRunning = null;
        if (OperatingSystem.IsWindows())
        {
            var matchedPids = FindOwnedRemoteSessionPids(canonicalId);
            if (matchedPids is not null)
                ownedWindowRunning = matchedPids.Count > 0;
        }

        return new SandboxInstancePollResult(
            present ? SandboxInstanceState.Running : SandboxInstanceState.ConfirmedEnded,
            ownedWindowRunning,
            stagingState);
    }

    public async Task<SandboxStopResult> StopAndCleanupAsync(
        string? instanceId,
        string? stagingRootPath,
        CancellationToken cancellationToken = default)
    {
        var wsbPath = ResolveWsbExecutablePath();
        var diag = new StringBuilder();
        SandboxInstanceState instanceState;

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            instanceState = SandboxInstanceState.NotCreated;
        }
        else if (!Guid.TryParse(instanceId.Trim(), out var parsedGuid))
        {
            instanceState = SandboxInstanceState.Unknown;
            diag.AppendLine("invalid instance id");
        }
        else if (wsbPath is null)
        {
            instanceState = SandboxInstanceState.Unknown;
            diag.AppendLine("wsb.exe unavailable");
        }
        else
        {
            var canonicalId = parsedGuid.ToString("D");
            var stopRes = await RunWsbCommandAsync(wsbPath, ["stop", "--id", canonicalId, "--raw"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (!stopRes.TimedOut && stopRes.ExitCode == 0)
            {
                instanceState = SandboxInstanceState.ConfirmedEnded;
            }
            else
            {
                diag.AppendLine(stopRes.TimedOut
                    ? "wsb stop timed out: " + CombineDiagnostic(stopRes)
                    : "wsb stop failed: " + CombineDiagnostic(stopRes));

                // Never infer "ended" unless a freshly returned `wsb list --raw` succeeds AND parses cleanly
                var listCheck = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                instanceState = EvaluateStopOutcome(
                    stopRes.TimedOut,
                    stopRes.ExitCode,
                    listCheck.TimedOut,
                    listCheck.ExitCode,
                    listCheck.Stdout,
                    canonicalId);

                if (instanceState == SandboxInstanceState.Unknown)
                {
                    diag.AppendLine("wsb list verification inconclusive: " + CombineDiagnostic(listCheck));
                }
                else if (instanceState == SandboxInstanceState.Running)
                {
                    diag.AppendLine("wsb list confirmed instance is still running");
                }
                else if (instanceState == SandboxInstanceState.ConfirmedEnded)
                {
                    diag.AppendLine("wsb list confirmed target instance is no longer running");
                }
            }

            // Close ONLY remote session window process(es) explicitly bound to this exact instanceId
            if (OperatingSystem.IsWindows() && instanceState == SandboxInstanceState.ConfirmedEnded)
            {
                CloseOwnedRemoteSessionWindows(canonicalId);
            }
        }

        // Only delete verified staging folder when sandbox is confirmed not running (NotCreated / ConfirmedEnded).
        // Never delete a staging folder while the instance is still Running or Unknown.
        TrialStagingState stagingState;
        if (instanceState is SandboxInstanceState.NotCreated or SandboxInstanceState.ConfirmedEnded)
        {
            stagingState = CleanupStagingDirectoryVerified(stagingRootPath, out var cleanErr);
            if (!string.IsNullOrEmpty(cleanErr))
                diag.AppendLine("staging cleanup: " + cleanErr);
        }
        else
        {
            stagingState = InspectStagingState(stagingRootPath);
        }

        var summary = DescribeStopSummary(instanceState, stagingState);
        var result = new SandboxStopResult(instanceState, stagingState, summary, diag.ToString().Trim());
        if (result.IsFullyCleaned && !string.IsNullOrWhiteSpace(instanceId))
        {
            ClearPendingSession(instanceId);
        }
        return result;
    }

    public Task<SandboxStopResult> CleanupStagingOnlyAsync(
        string? stagingRootPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stagingState = CleanupStagingDirectoryVerified(stagingRootPath, out var cleanErr);
        var diag = string.IsNullOrEmpty(cleanErr) ? "" : "staging cleanup: " + cleanErr;
        var summary = DescribeStopSummary(SandboxInstanceState.ConfirmedEnded, stagingState);
        return Task.FromResult(new SandboxStopResult(SandboxInstanceState.ConfirmedEnded, stagingState, summary, diag));
    }

    public static string DescribeStopSummary(SandboxInstanceState instanceState, TrialStagingState stagingState) =>
        (instanceState, stagingState) switch
        {
            (SandboxInstanceState.NotCreated or SandboxInstanceState.ConfirmedEnded, TrialStagingState.NotCreated or TrialStagingState.Cleaned)
                => "已确认本次沙箱实例结束，并已清理本次宿主暂存目录。",
            (SandboxInstanceState.NotCreated or SandboxInstanceState.ConfirmedEnded, TrialStagingState.Remaining)
                => "已确认本次沙箱实例结束，但本次宿主暂存目录仍有残留（可能被占用），可点击“重试清理”。",
            (SandboxInstanceState.NotCreated or SandboxInstanceState.ConfirmedEnded, _)
                => "已确认本次沙箱实例结束，但本次宿主暂存目录状态尚未确认。",
            (SandboxInstanceState.Running, _)
                => "本次沙箱实例仍在运行，尚未清理临时文件，请点击“结束这次试用”重试。",
            _
                => "暂时无法确认本次沙箱实例是否已结束，未删除关联信息；请稍后点击“重新检查”或重试结束。"
        };

    public static bool IsVerifiedSessionStagingPath(string? candidatePath, string stagingBaseDir, string? expectedSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(stagingBaseDir))
            return false;

        try
        {
            if (!Path.IsPathFullyQualified(candidatePath)) return false;
            var fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingBaseDir));
            var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            var parent = Path.GetDirectoryName(fullCandidate);
            if (string.IsNullOrEmpty(parent) || !string.Equals(parent, fullBase, StringComparison.OrdinalIgnoreCase))
                return false;

            var leaf = Path.GetFileName(fullCandidate);
            if (string.IsNullOrEmpty(leaf) || leaf.Length < 8 || leaf.Length > 32)
                return false;
            foreach (var ch in leaf)
            {
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'z')))
                    return false;
            }
            if (!string.IsNullOrEmpty(expectedSessionId) && !string.Equals(leaf, expectedSessionId, StringComparison.Ordinal))
                return false;

            // Refuse to traverse or delete reparse points / junctions / symlinks
            if (Directory.Exists(fullBase) && (File.GetAttributes(fullBase) & FileAttributes.ReparsePoint) != 0)
                return false;
            if (Directory.Exists(fullCandidate) && (File.GetAttributes(fullCandidate) & FileAttributes.ReparsePoint) != 0)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public TrialStagingState InspectStagingState(string? stagingRootPath)
    {
        if (string.IsNullOrWhiteSpace(stagingRootPath))
            return TrialStagingState.NotCreated;
        if (!IsVerifiedSessionStagingPath(stagingRootPath, stagingBaseDirectory))
            return TrialStagingState.Unconfirmed;

        try
        {
            var full = Path.GetFullPath(stagingRootPath);
            return Directory.Exists(full) ? TrialStagingState.Remaining : TrialStagingState.Cleaned;
        }
        catch
        {
            return TrialStagingState.Unconfirmed;
        }
    }

    internal TrialStagingState CleanupStagingDirectoryVerified(string? stagingRootPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(stagingRootPath))
            return TrialStagingState.NotCreated;
        if (!IsVerifiedSessionStagingPath(stagingRootPath, stagingBaseDirectory))
        {
            error = "refused unverified staging path outside Contain staging root";
            return TrialStagingState.Unconfirmed;
        }

        return CleanupStagingDirectory(stagingRootPath, out error)
            ? TrialStagingState.Cleaned
            : TrialStagingState.Remaining;
    }

    internal static bool CleanupStagingDirectory(string? stagingRootPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(stagingRootPath)) return true;
        var full = Path.GetFullPath(stagingRootPath);
        if (!Directory.Exists(full)) return true;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(entry, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(full, recursive: true);
                if (!Directory.Exists(full)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                Thread.Sleep(250);
            }
        }
        return !Directory.Exists(full);
    }

    public PendingSessionLoadResult LoadPendingSession()
    {
        if (!File.Exists(pendingSessionFilePath))
            return new PendingSessionLoadResult(PendingSessionLoadStatus.None);

        try
        {
            if ((File.GetAttributes(pendingSessionFilePath) & FileAttributes.ReparsePoint) != 0)
                return new PendingSessionLoadResult(PendingSessionLoadStatus.Corrupt, Diagnostic: "pending session file is a reparse point");

            var raw = File.ReadAllText(pendingSessionFilePath, Utf8NoBom);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return QuarantineCorruptPendingRecord("root is not JSON object");

            if (!root.TryGetProperty("schema_version", out var verProp) || !verProp.TryGetInt32(out var ver) || ver != 1)
                return QuarantineCorruptPendingRecord("unsupported schema_version");

            var sessionId = root.TryGetProperty("session_id", out var sidProp) && sidProp.ValueKind == JsonValueKind.String
                ? sidProp.GetString()?.Trim() ?? ""
                : "";
            var rawInstanceId = root.TryGetProperty("instance_id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()?.Trim() ?? ""
                : "";
            var stagingRoot = root.TryGetProperty("staging_root_path", out var stageProp) && stageProp.ValueKind == JsonValueKind.String
                ? stageProp.GetString()?.Trim() ?? ""
                : "";
            var fileName = root.TryGetProperty("file_name", out var fnProp) && fnProp.ValueKind == JsonValueKind.String
                ? fnProp.GetString()?.Trim() ?? ""
                : "";
            var createdUtc = root.TryGetProperty("created_utc", out var tsProp)
                && tsProp.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(tsProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTs)
                ? parsedTs
                : DateTimeOffset.MinValue;
            var ownerPid = root.TryGetProperty("owner_pid", out var pidProp) && pidProp.TryGetInt32(out var parsedPid)
                ? parsedPid
                : 0;
            var ownerTicks = root.TryGetProperty("owner_start_time_utc_ticks", out var ticksProp) && ticksProp.TryGetInt64(out var parsedTicks)
                ? parsedTicks
                : 0L;

            if (!Guid.TryParse(rawInstanceId, out var parsedGuid))
                return QuarantineCorruptPendingRecord("invalid instance_id GUID");

            if (!IsVerifiedSessionStagingPath(stagingRoot, stagingBaseDirectory, sessionId))
                return QuarantineCorruptPendingRecord("staging_root_path failed Contain staging containment verification");

            var record = new PendingTrialSessionRecord(
                1,
                sessionId,
                parsedGuid.ToString("D"),
                Path.GetFullPath(stagingRoot),
                fileName,
                createdUtc,
                ownerPid,
                ownerTicks);

            if (ownerPid > 0 && ownerPid != Environment.ProcessId && IsOwnerProcessStillAlive(ownerPid, ownerTicks))
            {
                return new PendingSessionLoadResult(
                    PendingSessionLoadStatus.OwnedByAnotherLiveProcess,
                    record,
                    $"owned by live Contain process PID {ownerPid}");
            }

            return new PendingSessionLoadResult(PendingSessionLoadStatus.Loaded, record);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return QuarantineCorruptPendingRecord($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void SavePendingSession(PendingTrialSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Guid.TryParse(record.InstanceId, out var parsedGuid))
            return;
        if (!IsVerifiedSessionStagingPath(record.StagingRootPath, stagingBaseDirectory, record.SessionId))
            return;

        try
        {
            var dir = Path.GetDirectoryName(pendingSessionFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var payload = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                session_id = record.SessionId,
                instance_id = parsedGuid.ToString("D"),
                staging_root_path = Path.GetFullPath(record.StagingRootPath),
                file_name = record.FileName,
                created_utc = record.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
                owner_pid = record.OwnerProcessId,
                owner_start_time_utc_ticks = record.OwnerStartTimeUtcTicks
            }, new JsonSerializerOptions { WriteIndented = true });

            var tmpPath = pendingSessionFilePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(tmpPath, payload + Environment.NewLine, Utf8NoBom);
            File.Move(tmpPath, pendingSessionFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never fail a trial launch solely because optional recovery metadata could not be written
        }
    }

    public void ClearPendingSession(string? expectedInstanceId = null, string? expectedSessionId = null)
    {
        try
        {
            if (!File.Exists(pendingSessionFilePath)) return;
            if (!string.IsNullOrWhiteSpace(expectedInstanceId) || !string.IsNullOrWhiteSpace(expectedSessionId))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(pendingSessionFilePath, Utf8NoBom));
                    var root = doc.RootElement;
                    if (!string.IsNullOrWhiteSpace(expectedInstanceId)
                        && root.TryGetProperty("instance_id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String
                        && !string.Equals(idProp.GetString()?.Trim(), expectedInstanceId.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(expectedSessionId)
                        && root.TryGetProperty("session_id", out var sidProp)
                        && sidProp.ValueKind == JsonValueKind.String
                        && !string.Equals(sidProp.GetString()?.Trim(), expectedSessionId.Trim(), StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (JsonException) { /* Corrupt file may still be cleared explicitly if requested */ }
            }

            File.Delete(pendingSessionFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private PendingSessionLoadResult QuarantineCorruptPendingRecord(string reason)
    {
        try
        {
            if (File.Exists(pendingSessionFilePath))
            {
                var corruptPath = pendingSessionFilePath + ".corrupt";
                File.Move(pendingSessionFilePath, corruptPath, overwrite: true);
            }
        }
        catch { }
        return new PendingSessionLoadResult(PendingSessionLoadStatus.Corrupt, Diagnostic: "corrupt pending session record ignored: " + reason);
    }

    private static PendingTrialSessionRecord CreatePendingRecord(string sessionId, string instanceId, string stagingRoot, string fileName)
    {
        long startTicks = 0;
        try
        {
            using var current = Process.GetCurrentProcess();
            startTicks = current.StartTime.ToUniversalTime().Ticks;
        }
        catch { }

        return new PendingTrialSessionRecord(
            1,
            sessionId,
            instanceId,
            Path.GetFullPath(stagingRoot),
            fileName,
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            startTicks);
    }

    private static bool IsOwnerProcessStillAlive(int ownerPid, long ownerStartTimeUtcTicks)
    {
        try
        {
            using var proc = Process.GetProcessById(ownerPid);
            if (proc.HasExited) return false;
            if (ownerStartTimeUtcTicks <= 0) return true;
            var actualTicks = proc.StartTime.ToUniversalTime().Ticks;
            // Allow small clock/granularity drift (~2 seconds) while catching PID reuse
            return Math.Abs(actualTicks - ownerStartTimeUtcTicks) < TimeSpan.FromSeconds(2).Ticks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; /* Process exists but cannot read start time; fail closed and do not interfere */ }
    }

    private static int? LaunchDetachedConnect(string wsbPath, string instanceId)
    {
        var psi = new ProcessStartInfo(wsbPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("connect");
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add(instanceId);
        using var proc = Process.Start(psi);
        return proc?.Id;
    }

    private string? ResolveWsbExecutablePath()
    {
        if (!string.IsNullOrEmpty(explicitWsbPath))
            return File.Exists(explicitWsbPath) ? explicitWsbPath : null;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps", "wsb.exe");
            if (File.Exists(windowsApps)) return windowsApps;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "wsb.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        var sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsb.exe");
        return File.Exists(sys32) ? sys32 : null;
    }

    private sealed record WsbCommandResult(bool TimedOut, int ExitCode, string Stdout, string Stderr);

    private async Task<WsbCommandResult> RunWsbCommandAsync(
        string wsbPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(wsbPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            if (!process.Start())
                return new WsbCommandResult(false, -1, "", "Process.Start returned false");

            var stdoutTask = ReadLimitedAsync(process, process.StandardOutput.BaseStream, deadline.Token);
            var stderrTask = ReadLimitedAsync(process, process.StandardError.BaseStream, deadline.Token);
            var waitTask = process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdoutTask, stderrTask, waitTask).ConfigureAwait(false);
            return new WsbCommandResult(false, process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcessSafe(process);
            return new WsbCommandResult(true, -1, "", $"timeout after {Math.Round(timeout.TotalSeconds, 1)}s");
        }
        catch (OperationCanceledException)
        {
            KillProcessSafe(process);
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            KillProcessSafe(process);
            return new WsbCommandResult(false, -1, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string> ReadLimitedAsync(Process process, Stream stream, CancellationToken token)
    {
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buf.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, checked((int)ms.Length));
                if (ms.Length + read > MaxStreamBytes)
                {
                    KillProcessSafe(process);
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, checked((int)ms.Length));
                }
                ms.Write(buf, 0, read);
            }
        }
        catch
        {
            KillProcessSafe(process);
            throw;
        }
    }

    private static void KillProcessSafe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private static string CombineDiagnostic(WsbCommandResult res)
    {
        var parts = new List<string>();
        if (res.TimedOut) parts.Add("timed_out");
        parts.Add($"exit={res.ExitCode}");
        if (!string.IsNullOrWhiteSpace(res.Stderr)) parts.Add("stderr=" + res.Stderr.Trim());
        if (!string.IsNullOrWhiteSpace(res.Stdout)) parts.Add("stdout=" + res.Stdout.Trim());
        return string.Join("; ", parts);
    }

    /// <summary>
    /// Finds ONLY `WindowsSandboxRemoteSession` processes whose exact Win32 command line
    /// contains `--id &lt;instanceId&gt;` (case-insensitive). Returns null if command line
    /// inspection is unsupported on the current platform or access is unavailable.
    /// Never matches arbitrary or "first" remote session processes.
    /// </summary>
    internal static IReadOnlyList<int>? FindOwnedRemoteSessionPids(string instanceId)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(instanceId))
            return null;

        var matched = new List<int>();
        var inspectedAny = false;
        Process[] procs;
        try { procs = Process.GetProcessesByName("WindowsSandboxRemoteSession"); }
        catch { return null; }

        try
        {
            foreach (var p in procs)
            {
                var cmd = TryGetProcessCommandLineWindows(p.Id);
                if (cmd is null) continue;
                inspectedAny = true;
                if (CommandLineMatchesInstanceId(cmd, instanceId))
                    matched.Add(p.Id);
            }
            // If no WindowsSandboxRemoteSession processes exist at all, we authoritatively know 0 match.
            return procs.Length == 0 || inspectedAny ? matched : null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public static bool IsAnotherSandboxRunningError(int exitCode, string? stderr)
    {
        const int CoEAppSingleUse = unchecked((int)0x800401F6);
        if (exitCode == CoEAppSingleUse) return true;
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return stderr.Contains("0x800401F6", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("CO_E_APPSINGLEUSE", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CommandLineMatchesInstanceId(string commandLine, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(instanceId))
            return false;

        if (Guid.TryParse(instanceId.Trim(), out var guid))
        {
            return ContainsBoundedToken(commandLine, guid.ToString("D"))
                || ContainsBoundedToken(commandLine, guid.ToString("N"));
        }

        var idx = commandLine.IndexOf(instanceId, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        if (!HasTokenBoundary(commandLine, idx, instanceId.Length)) return false;
        var prefix = commandLine[..idx];
        return prefix.Contains("--id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsBoundedToken(string text, string token)
    {
        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var idx = text.IndexOf(token, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            if (HasTokenBoundary(text, idx, token.Length)) return true;
            searchFrom = idx + 1;
        }
        return false;
    }

    private static bool HasTokenBoundary(string text, int start, int length)
    {
        var beforeOk = start == 0 || !IsTokenWordChar(text[start - 1]);
        var end = start + length;
        var afterOk = end >= text.Length || !IsTokenWordChar(text[end]);
        return beforeOk && afterOk;
    }

    private static bool IsTokenWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '-';

    private static void CloseOwnedRemoteSessionWindows(string instanceId)
    {
        var pids = FindOwnedRemoteSessionPids(instanceId);
        if (pids is null) return;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    p.Kill(entireProcessTree: false);
            }
            catch { }
        }
    }

    private static string? TryGetProcessCommandLineWindows(int pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        const int ProcessCommandLineInformation = 60;

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 65536) return null;

            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _);
                if (status != 0) return null;
                var unicode = Marshal.PtrToStructure<UnicodeString>(buffer);
                if (unicode.Buffer == IntPtr.Zero || unicode.Length == 0) return "";
                return Marshal.PtrToStringUni(unicode.Buffer, unicode.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch { return null; }
        finally
        {
            CloseHandle(handle);
        }
    }

    private readonly record struct RegStringResult(string? Value, bool AccessDenied);
    private readonly record struct RegDwordResult(int? Value, bool AccessDenied);
    private readonly record struct RegExistsResult(bool? Exists, bool AccessDenied);

    private static readonly IntPtr HkeyLocalMachine = unchecked((IntPtr)0x80000002);
    private const uint KeyQueryValue = 0x0001;
    private const uint KeyWow6464Key = 0x0100;
    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const uint RegDword = 4;

    private static RegStringResult TryReadHklmString(string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return new(null, false);
        var openErr = RegOpenKeyExW(HkeyLocalMachine, subKey, 0, KeyQueryValue | KeyWow6464Key, out var hKey);
        if (openErr == ErrorAccessDenied) return new(null, true);
        if (openErr != ErrorSuccess || hKey == IntPtr.Zero) return new(null, false);
        try
        {
            uint size = 0;
            var qErr = RegQueryValueExW(hKey, valueName, IntPtr.Zero, out var type, IntPtr.Zero, ref size);
            if (qErr == ErrorAccessDenied) return new(null, true);
            if (qErr != ErrorSuccess || size == 0 || size > 4096 || (type != RegSz && type != RegExpandSz))
                return new(null, false);

            var buf = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                qErr = RegQueryValueExW(hKey, valueName, IntPtr.Zero, out type, buf, ref size);
                if (qErr == ErrorAccessDenied) return new(null, true);
                if (qErr != ErrorSuccess || size < 2) return new(null, false);
                var str = Marshal.PtrToStringUni(buf, checked((int)(size / 2)))?.TrimEnd('\0').Trim();
                return new(string.IsNullOrEmpty(str) ? null : str, false);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { _ = RegCloseKey(hKey); }
    }

    private static RegDwordResult TryReadHklmDword(string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return new(null, false);
        var openErr = RegOpenKeyExW(HkeyLocalMachine, subKey, 0, KeyQueryValue | KeyWow6464Key, out var hKey);
        if (openErr == ErrorAccessDenied) return new(null, true);
        if (openErr != ErrorSuccess || hKey == IntPtr.Zero) return new(null, false);
        try
        {
            uint size = 4;
            var buf = Marshal.AllocHGlobal(4);
            try
            {
                var qErr = RegQueryValueExW(hKey, valueName, IntPtr.Zero, out var type, buf, ref size);
                if (qErr == ErrorAccessDenied) return new(null, true);
                if (qErr != ErrorSuccess || type != RegDword || size != 4) return new(null, false);
                return new(Marshal.ReadInt32(buf), false);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { _ = RegCloseKey(hKey); }
    }

    private static RegExistsResult TryCheckHklmKeyExists(string subKey)
    {
        if (!OperatingSystem.IsWindows()) return new(null, false);
        var openErr = RegOpenKeyExW(HkeyLocalMachine, subKey, 0, KeyQueryValue | KeyWow6464Key, out var hKey);
        if (openErr == ErrorAccessDenied) return new(null, true);
        if (openErr == ErrorFileNotFound) return new(false, false);
        if (openErr != ErrorSuccess || hKey == IntPtr.Zero) return new(null, false);
        _ = RegCloseKey(hKey);
        return new(true, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessorFeaturePresent(uint processorFeature);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProductInfo(
        uint dwOSMajorVersion,
        uint dwOSMinorVersion,
        uint dwSpMajorVersion,
        uint dwSpMinorVersion,
        out uint pdwReturnedProductType);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(
        IntPtr hKey,
        string lpSubKey,
        uint ulOptions,
        uint samDesired,
        out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(
        IntPtr hKey,
        string lpValueName,
        IntPtr lpReserved,
        out uint lpType,
        IntPtr lpData,
        ref uint lpcbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
}
