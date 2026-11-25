using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Contain.Desktop.Model;

public interface IConfigRestoreOperationHooks
{
    void AfterRestorePrepared(string undoId, string targetPath) { }
    void AfterTargetReplacedBeforeVerify(string undoId, string targetPath) { }
    void BeforeSaveRestoreCompleted(string undoId, string targetPath) { }
    void BeforePostRestoreRefresh(string undoId, string targetPath) { }
    void AfterUndoPrepared(string undoId, string targetPath) { }
    void AfterUndoTargetUpdatedBeforeMarkCompleted(string undoId, string targetPath) { }
    void BeforeVerifyStagedExportPackage(string tempPackagePath, string destinationPath) { }
    void BeforeReplaceExportDestination(string tempPackagePath, string destinationPath) { }
}

public interface IConfigRestorePointService
{
    string StorageRoot { get; }
    ConfigFilePreview InspectCandidateFile(string rawPath);
    ConfigRestorePointRecord CreateRestorePoint(string rawPath, string? note);
    IReadOnlyList<ConfigRestorePointRecord> LoadAllPoints();
    ConfigComparisonResult CompareRestorePoint(ConfigRestorePointRecord point, ConfigDiffDirection direction = ConfigDiffDirection.BackupToCurrent);
    ConfigRestoreResult RestoreFromPoint(ConfigRestorePointRecord point, ConfigLiveFileSnapshot expectedLiveSnapshot);
    IReadOnlyList<ConfigRecoveryReport> ReconcileOperationsOnStartup();
    ConfigRecoveryReport? ReconcileLatestOperation();
    bool IsTargetBusyInAnotherWindow(string targetPath, out ConfigTargetLockInfo? activeLock);
    void ArchiveRecoveryRecord(ConfigUndoSnapshot undoSnapshot, string reason);
    ConfigOriginalCopyPreview InspectOriginalCopy(ConfigUndoSnapshot undoSnapshot);
    ConfigOriginalCopyExportResult ExportOriginalCopy(ConfigUndoSnapshot undoSnapshot, string destinationPath);
    ConfigPortablePackageExportResult ExportRestorePointPackage(ConfigRestorePointRecord point, string destinationPath);
    ConfigPortablePackageInspection InspectPortablePackage(string packagePath);
    ConfigImportPreviewResult PreviewImportedPackageTarget(ConfigPortablePackageInspection package, string rawTargetPath);
    ConfigImportRestoreResult ImportAndRestorePackage(ConfigPortablePackageInspection package, string rawTargetPath, ConfigLiveFileSnapshot expectedLiveSnapshot);
    ConfigUndoSnapshot? LoadLatestActiveUndo();
    ConfigUndoPreviewResult InspectUndoPreview(ConfigUndoSnapshot undoSnapshot);
    ConfigUndoResult UndoRestore(ConfigUndoSnapshot undoSnapshot, ConfigLiveFileSnapshot? expectedLiveSnapshot = null);
}

public static class ConfigTextDecoder
{
    public const int MaxTextDiffBytes = 2 * 1024 * 1024; // 2 MiB text diff cap; raw byte restore handles larger files cleanly.

    private static readonly UTF8Encoding StrictUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16LeNoBom = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16BeNoBom = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    public static ConfigTextInspection Inspect(byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);

        if (rawBytes.Length > MaxTextDiffBytes)
        {
            return new ConfigTextInspection(
                IsRecognizedText: false,
                EncodingLabel: "大文件（已跳过文本解码）",
                HasUtf8Bom: HasUtf8BomPrefix(rawBytes),
                LineEndingLabel: "不适用",
                Lines: [],
                UnrecognizedReason: "文件超过 2 MB，跳过逐行文本对比，仅展示大小与 SHA-256；恢复与撤销仍将按原始字节完整执行。");
        }

        if (rawBytes.Length == 0)
        {
            return new ConfigTextInspection(
                IsRecognizedText: true,
                EncodingLabel: "UTF-8（无 BOM，空文件）",
                HasUtf8Bom: false,
                LineEndingLabel: "单行（无换行）",
                Lines: []);
        }

        // 1. UTF-8 BOM (EF BB BF)
        if (HasUtf8BomPrefix(rawBytes))
        {
            if (ContainsNullByte(rawBytes, 3))
                return Unrecognized("检测到 NUL 字节，非纯文本配置文件。");
            try
            {
                var text = StrictUtf8NoBom.GetString(rawBytes, 3, rawBytes.Length - 3);
                if (HasExcessiveControlChars(text))
                    return Unrecognized("包含无法作为纯文本可靠展示的控制字符。");
                return BuildRecognized(text, "UTF-8（含 BOM）", hasUtf8Bom: true);
            }
            catch (DecoderFallbackException)
            {
                return Unrecognized("带有 UTF-8 BOM，但后续字节包含无效 UTF-8 序列。");
            }
        }

        // 2. UTF-16 LE BOM (FF FE, excluding UTF-32 LE FF FE 00 00)
        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFF && rawBytes[1] == 0xFE &&
            !(rawBytes.Length >= 4 && rawBytes[2] == 0x00 && rawBytes[3] == 0x00))
        {
            if ((rawBytes.Length - 2) % 2 != 0)
                return Unrecognized("UTF-16 LE 字节长度无效。");
            try
            {
                var text = StrictUtf16LeNoBom.GetString(rawBytes, 2, rawBytes.Length - 2);
                if (HasExcessiveControlChars(text))
                    return Unrecognized("包含无法作为纯文本可靠展示的控制字符。");
                return BuildRecognized(text, "UTF-16 LE（含 BOM）", hasUtf8Bom: false);
            }
            catch (DecoderFallbackException)
            {
                return Unrecognized("UTF-16 LE 字节序列无效。");
            }
        }

        // 3. UTF-16 BE BOM (FE FF)
        if (rawBytes.Length >= 2 && rawBytes[0] == 0xFE && rawBytes[1] == 0xFF)
        {
            if ((rawBytes.Length - 2) % 2 != 0)
                return Unrecognized("UTF-16 BE 字节长度无效。");
            try
            {
                var text = StrictUtf16BeNoBom.GetString(rawBytes, 2, rawBytes.Length - 2);
                if (HasExcessiveControlChars(text))
                    return Unrecognized("包含无法作为纯文本可靠展示的控制字符。");
                return BuildRecognized(text, "UTF-16 BE（含 BOM）", hasUtf8Bom: false);
            }
            catch (DecoderFallbackException)
            {
                return Unrecognized("UTF-16 BE 字节序列无效。");
            }
        }

        // 4. UTF-8 without BOM
        if (ContainsNullByte(rawBytes, 0))
            return Unrecognized("检测到 NUL 字节，可能是二进制或未带 BOM 的多字节编码。");

        try
        {
            var text = StrictUtf8NoBom.GetString(rawBytes);
            if (HasExcessiveControlChars(text))
                return Unrecognized("包含非文本控制字符，编码无法可靠识别。");
            return BuildRecognized(text, "UTF-8（无 BOM）", hasUtf8Bom: false);
        }
        catch (DecoderFallbackException)
        {
            return Unrecognized("字节序列不是有效 UTF-8 且无编码 BOM，编码无法可靠识别。");
        }
    }

    private static bool HasUtf8BomPrefix(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static bool ContainsNullByte(byte[] bytes, int start)
    {
        for (var i = start; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x00) return true;
        }
        return false;
    }

    private static bool HasExcessiveControlChars(string text)
    {
        foreach (var ch in text)
        {
            if (ch < 0x20 && ch is not ('\r' or '\n' or '\t' or '\f' or '\v'))
                return true;
        }
        return false;
    }

    private static ConfigTextInspection Unrecognized(string reason) =>
        new(
            IsRecognizedText: false,
            EncodingLabel: "未识别编码 / 非纯文本",
            HasUtf8Bom: false,
            LineEndingLabel: "不适用",
            Lines: [],
            UnrecognizedReason: reason);

    private static ConfigTextInspection BuildRecognized(string text, string encodingLabel, bool hasUtf8Bom)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        var lines = new List<string>();
        var lineStart = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                lines.Add(text[lineStart..i]);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
                lineStart = i + 1;
            }
            else if (ch == '\n')
            {
                lines.Add(text[lineStart..i]);
                lf++;
                lineStart = i + 1;
            }
        }

        if (lineStart <= text.Length)
        {
            if (lineStart < text.Length)
                lines.Add(text[lineStart..]);
            else if (text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r'))
                lines.Add("");
        }

        var styles = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var eolLabel = styles switch
        {
            0 => "单行（无换行）",
            > 1 => $"混合换行（CRLF:{crlf}, LF:{lf}, CR:{cr}）",
            _ when crlf > 0 => "CRLF (Windows)",
            _ when lf > 0 => "LF (Unix)",
            _ => "CR"
        };

        return new ConfigTextInspection(
            IsRecognizedText: true,
            EncodingLabel: encodingLabel,
            HasUtf8Bom: hasUtf8Bom,
            LineEndingLabel: eolLabel,
            Lines: lines);
    }
}

public static class ConfigLineDiffer
{
    public const int ContextLines = 2;
    public const int MaxDisplayedRows = 300;
    private const long MaxLcsCells = 2_000_000;

    public sealed record DiffOutput(
        IReadOnlyList<ConfigDiffLine> Lines,
        int RemovedCount,
        int AddedCount,
        bool Truncated);

    public static DiffOutput Compute(IReadOnlyList<string> leftLines, IReadOnlyList<string> rightLines)
    {
        ArgumentNullException.ThrowIfNull(leftLines);
        ArgumentNullException.ThrowIfNull(rightLines);

        var prefix = 0;
        var minLen = Math.Min(leftLines.Count, rightLines.Count);
        while (prefix < minLen && string.Equals(leftLines[prefix], rightLines[prefix], StringComparison.Ordinal))
            prefix++;

        var suffix = 0;
        while (suffix < (minLen - prefix) &&
               string.Equals(leftLines[leftLines.Count - 1 - suffix], rightLines[rightLines.Count - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        var midOldCount = leftLines.Count - prefix - suffix;
        var midNewCount = rightLines.Count - prefix - suffix;

        if (midOldCount == 0 && midNewCount == 0)
            return new DiffOutput([], 0, 0, false);

        var rawOps = new List<ConfigDiffLine>(leftLines.Count + rightLines.Count);

        for (var i = 0; i < prefix; i++)
            rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Context, i + 1, i + 1, leftLines[i]));

        var removed = 0;
        var added = 0;

        if ((long)midOldCount * midNewCount <= MaxLcsCells && midOldCount > 0 && midNewCount > 0)
        {
            var midOps = ComputeLcsMiddle(leftLines, rightLines, prefix, midOldCount, midNewCount);
            foreach (var op in midOps)
            {
                if (op.Kind == ConfigDiffLineKind.Removed) removed++;
                else if (op.Kind == ConfigDiffLineKind.Added) added++;
                rawOps.Add(op);
            }
        }
        else
        {
            for (var i = 0; i < midOldCount; i++)
            {
                removed++;
                rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Removed, prefix + i + 1, null, leftLines[prefix + i]));
            }
            for (var j = 0; j < midNewCount; j++)
            {
                added++;
                rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Added, null, prefix + j + 1, rightLines[prefix + j]));
            }
        }

        for (var i = 0; i < suffix; i++)
        {
            var oldIdx = leftLines.Count - suffix + i;
            var newIdx = rightLines.Count - suffix + i;
            rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Context, oldIdx + 1, newIdx + 1, leftLines[oldIdx]));
        }

        var keep = new bool[rawOps.Count];
        for (var i = 0; i < rawOps.Count; i++)
        {
            if (rawOps[i].Kind is ConfigDiffLineKind.Removed or ConfigDiffLineKind.Added)
            {
                var lo = Math.Max(0, i - ContextLines);
                var hi = Math.Min(rawOps.Count - 1, i + ContextLines);
                for (var k = lo; k <= hi; k++) keep[k] = true;
            }
        }

        var output = new List<ConfigDiffLine>();
        var truncated = false;
        var skipped = 0;

        for (var i = 0; i < rawOps.Count; i++)
        {
            if (keep[i])
            {
                if (skipped > 0)
                {
                    if (output.Count < MaxDisplayedRows)
                        output.Add(new ConfigDiffLine(ConfigDiffLineKind.Ellipsis, null, null, $"… 省略 {skipped} 行未变化内容 …"));
                    skipped = 0;
                }

                if (output.Count < MaxDisplayedRows)
                    output.Add(rawOps[i]);
                else
                    truncated = true;
            }
            else
            {
                skipped++;
            }
        }

        if (skipped > 0 && output.Count > 0 && output.Count < MaxDisplayedRows)
            output.Add(new ConfigDiffLine(ConfigDiffLineKind.Ellipsis, null, null, $"… 省略末尾 {skipped} 行未变化内容 …"));

        return new DiffOutput(output, removed, added, truncated);
    }

    private static List<ConfigDiffLine> ComputeLcsMiddle(
        IReadOnlyList<string> leftLines,
        IReadOnlyList<string> rightLines,
        int offset,
        int m,
        int n)
    {
        var dp = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
        {
            var oldLine = leftLines[offset + i];
            for (var j = n - 1; j >= 0; j--)
            {
                if (string.Equals(oldLine, rightLines[offset + j], StringComparison.Ordinal))
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                else
                    dp[i, j] = dp[i + 1, j] >= dp[i, j + 1] ? dp[i + 1, j] : dp[i, j + 1];
            }
        }

        var result = new List<ConfigDiffLine>(m + n);
        var a = 0;
        var b = 0;
        while (a < m && b < n)
        {
            if (string.Equals(leftLines[offset + a], rightLines[offset + b], StringComparison.Ordinal))
            {
                result.Add(new ConfigDiffLine(ConfigDiffLineKind.Context, offset + a + 1, offset + b + 1, leftLines[offset + a]));
                a++;
                b++;
            }
            else if (dp[a + 1, b] >= dp[a, b + 1])
            {
                result.Add(new ConfigDiffLine(ConfigDiffLineKind.Removed, offset + a + 1, null, leftLines[offset + a]));
                a++;
            }
            else
            {
                result.Add(new ConfigDiffLine(ConfigDiffLineKind.Added, null, offset + b + 1, rightLines[offset + b]));
                b++;
            }
        }

        while (a < m)
        {
            result.Add(new ConfigDiffLine(ConfigDiffLineKind.Removed, offset + a + 1, null, leftLines[offset + a]));
            a++;
        }

        while (b < n)
        {
            result.Add(new ConfigDiffLine(ConfigDiffLineKind.Added, null, offset + b + 1, rightLines[offset + b]));
            b++;
        }

        return result;
    }
}

public sealed class LocalConfigRestorePointService : IConfigRestorePointService
{
    public const long MaxConfigFileBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly long CurrentProcessStartUtcTicks = TryGetCurrentProcessStartUtcTicks();

    private readonly string pointsDir;
    private readonly string blobsDir;
    private readonly string undoDir;
    private readonly string locksDir;
    private readonly string instanceId = Guid.NewGuid().ToString("N");
    private readonly IConfigRestoreOperationHooks? operationHooks;

    public LocalConfigRestorePointService(string? storageRootDirectory = null, IConfigRestoreOperationHooks? operationHooks = null)
    {
        StorageRoot = string.IsNullOrWhiteSpace(storageRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Contain", "ConfigRestorePoints")
            : Path.GetFullPath(storageRootDirectory);
        pointsDir = Path.Combine(StorageRoot, "Points");
        blobsDir = Path.Combine(StorageRoot, "Blobs");
        undoDir = Path.Combine(StorageRoot, "Undo");
        locksDir = Path.Combine(StorageRoot, "Locks");
        this.operationHooks = operationHooks;
    }

    public string StorageRoot { get; }

    public ConfigFilePreview InspectCandidateFile(string rawPath)
    {
        if (!TryNormalizeRegularFilePath(rawPath, mustExist: true, out var fullPath, out var error))
            return new ConfigFilePreview(false, rawPath?.Trim() ?? "", "", "", "", 0, "", "", "", error);

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxConfigFileBytes)
            {
                return new ConfigFilePreview(false, fullPath, Path.GetFileName(fullPath), Path.GetDirectoryName(fullPath) ?? "", GetParentFolderName(fullPath), info.Length, "", "", "",
                    $"文件大小超过单配置文件保护上限（{ConfigFormatting.FormatByteSize(MaxConfigFileBytes)}），请确认选择的是配置文件而非大型数据文件。");
            }

            var rawBytes = ReadAllBytesShared(fullPath);
            var sha = ComputeSha256Hex(rawBytes);
            var text = ConfigTextDecoder.Inspect(rawBytes);
            var dir = Path.GetDirectoryName(fullPath) ?? "";
            return new ConfigFilePreview(
                IsValid: true,
                FullPath: fullPath,
                FileName: Path.GetFileName(fullPath),
                DirectoryPath: dir,
                ParentFolderName: GetParentFolderName(fullPath),
                ByteLength: rawBytes.LongLength,
                Sha256: sha,
                EncodingLabel: text.EncodingLabel,
                LineEndingLabel: text.LineEndingLabel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigFilePreview(false, fullPath, Path.GetFileName(fullPath), Path.GetDirectoryName(fullPath) ?? "", GetParentFolderName(fullPath), 0, "", "", "",
                $"无法只读访问该文件：{ex.Message}");
        }
    }

    public ConfigRestorePointRecord CreateRestorePoint(string rawPath, string? note)
    {
        if (!TryNormalizeRegularFilePath(rawPath, mustExist: true, out var fullPath, out var error))
            throw new InvalidOperationException(error);

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxConfigFileBytes)
            throw new InvalidOperationException($"文件超过单配置文件保护上限（{ConfigFormatting.FormatByteSize(MaxConfigFileBytes)}）。");

        var rawBytes = ReadAllBytesShared(fullPath);
        var sha = ComputeSha256Hex(rawBytes);
        var inspection = ConfigTextDecoder.Inspect(rawBytes);

        Directory.CreateDirectory(pointsDir);
        Directory.CreateDirectory(blobsDir);

        var pointId = Guid.NewGuid().ToString("N")[..12];
        var blobRelative = Path.Combine("Blobs", pointId + ".bin");
        var blobPath = Path.Combine(StorageRoot, blobRelative);

        WriteBytesAtomically(blobPath, rawBytes);

        var writtenSha = ComputeFileSha256Hex(blobPath);
        if (!string.Equals(writtenSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(blobPath);
            throw new IOException("备份字节写入后校验不一致，已放弃创建还原点。");
        }

        var record = new ConfigRestorePointRecord
        {
            SchemaVersion = 1,
            PointId = pointId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Note = (note ?? "").Trim(),
            TargetPath = fullPath,
            TargetFileName = Path.GetFileName(fullPath),
            TargetDirectory = Path.GetDirectoryName(fullPath) ?? "",
            ParentFolderName = GetParentFolderName(fullPath),
            ByteLength = rawBytes.LongLength,
            Sha256 = sha,
            BackupBlobRelativePath = blobRelative,
            EncodingLabel = inspection.EncodingLabel,
            LineEndingLabel = inspection.LineEndingLabel
        };

        var metadataPath = Path.Combine(pointsDir, pointId + ".json");
        WriteJsonAtomically(metadataPath, record);
        return record;
    }

    public IReadOnlyList<ConfigRestorePointRecord> LoadAllPoints()
    {
        if (!Directory.Exists(pointsDir))
            return [];

        var results = new List<ConfigRestorePointRecord>();
        foreach (var file in Directory.EnumerateFiles(pointsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(file, Utf8NoBom);
                var rec = JsonSerializer.Deserialize<ConfigRestorePointRecord>(json, JsonOptions);
                if (rec is not null && !string.IsNullOrWhiteSpace(rec.PointId) && !string.IsNullOrWhiteSpace(rec.TargetPath))
                    results.Add(rec);
            }
            catch
            {
                // Ignore unreadable stray files cleanly.
            }
        }

        results.Sort((a, b) =>
        {
            var cmp = b.CreatedAtUtc.CompareTo(a.CreatedAtUtc);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(b.PointId, a.PointId);
        });
        return results;
    }

    public ConfigComparisonResult CompareRestorePoint(ConfigRestorePointRecord point, ConfigDiffDirection direction = ConfigDiffDirection.BackupToCurrent)
    {
        ArgumentNullException.ThrowIfNull(point);

        if (!TryReadValidatedBackupBlob(point, out var backupBytes, out var integrityError))
        {
            var liveOnCorrupt = CaptureLiveSnapshot(point.TargetPath, out _);
            return new ConfigComparisonResult(
                Point: point,
                Direction: direction,
                Status: ConfigComparisonStatus.BackupCorrupted,
                BackupIntegrityValid: false,
                BackupIntegrityError: integrityError,
                LiveSnapshot: liveOnCorrupt,
                StatusHeadline: "备份文件已损坏或缺失",
                StatusDetail: $"{integrityError} 为保住现有文件，已禁止使用此还原点执行恢复。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: integrityError,
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        var liveSnapshot = CaptureLiveSnapshot(point.TargetPath, out var liveBytes);

        if (!liveSnapshot.Exists)
        {
            var detail = direction == ConfigDiffDirection.CurrentToBackup
                ? $"当前目标路径缺失该文件 → 恢复后将按备份原始字节重建（{point.DisplaySize}，{point.DisplayFormatSummary}，SHA-256：{point.DisplayShortSha256}）。"
                : "原位置已找不到该配置文件。你可以点击「恢复」，按备份原始字节重新创建该文件。";
            var fallback = direction == ConfigDiffDirection.CurrentToBackup
                ? $"当前状态：目标文件不存在 → 恢复后：按备份原始字节重建（{point.DisplaySize}，{point.DisplayFormatSummary}，SHA-256：{point.Sha256}）。"
                : $"当前目标路径无文件；备份共 {point.DisplaySize}，SHA-256 为 {point.Sha256}。";
            return new ConfigComparisonResult(
                Point: point,
                Direction: direction,
                Status: ConfigComparisonStatus.Missing,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "目标文件当前已缺失",
                StatusDetail: detail,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: fallback,
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        if (!liveSnapshot.IsReadable || liveBytes is null)
        {
            return new ConfigComparisonResult(
                Point: point,
                Direction: direction,
                Status: ConfigComparisonStatus.ReadFailed,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "当前文件读取失败",
                StatusDetail: string.IsNullOrWhiteSpace(liveSnapshot.ReadError)
                    ? "无法读取目标文件，请检查文件是否正被其他程序独占占用或权限受限。"
                    : liveSnapshot.ReadError,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"读取失败原因：{liveSnapshot.ReadError}；备份大小 {point.DisplaySize}，SHA-256：{point.Sha256}。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        if (liveSnapshot.ByteLength == point.ByteLength &&
            string.Equals(liveSnapshot.Sha256, point.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigComparisonResult(
                Point: point,
                Direction: direction,
                Status: ConfigComparisonStatus.Identical,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "当前文件与备份完全一致",
                StatusDetail: $"字节数（{point.DisplaySize}）与 SHA-256 完全一致，当前无需恢复。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: "当前文件内容、编码 BOM 与换行格式均与备份逐字节一致。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        var backupText = ConfigTextDecoder.Inspect(backupBytes);
        var liveText = ConfigTextDecoder.Inspect(liveBytes);
        var restoreDir = direction == ConfigDiffDirection.CurrentToBackup;

        if (!backupText.IsRecognizedText || !liveText.IsRecognizedText)
        {
            var reason = !backupText.IsRecognizedText ? backupText.UnrecognizedReason : liveText.UnrecognizedReason;
            var detail = restoreDir
                ? $"当前文件 → 恢复后：编码无法可靠识别（当前 {liveSnapshot.DisplaySize} → 恢复后 {point.DisplaySize}），恢复将直接写入备份原始字节。"
                : $"文件字节已与备份不同（备份 {point.DisplaySize} → 当前 {liveSnapshot.DisplaySize}）。编码无法可靠识别，已展示大小与 SHA-256；恢复仍将按原始字节执行。";
            return new ConfigComparisonResult(
                Point: point,
                Direction: direction,
                Status: ConfigComparisonStatus.Changed,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "当前文件已变化",
                StatusDetail: detail,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"{reason} 恢复时不会重新编码，始终写入备份的原始字节。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        // Direction follows user action:
        // - BackupToCurrent (browse):  left=backup -> right=current ("从备份到现在改了什么")
        // - CurrentToBackup (restore): left=current -> right=backup ("当前文件 → 恢复后")
        var diff = restoreDir
            ? ConfigLineDiffer.Compute(liveText.Lines, backupText.Lines)
            : ConfigLineDiffer.Compute(backupText.Lines, liveText.Lines);

        if (diff.RemovedCount == 0 && diff.AddedCount == 0)
        {
            var formatDetail = restoreDir
                ? $"文本行相同，但编码 BOM 或换行格式将变化（当前：{liveText.EncodingLabel} · {liveText.LineEndingLabel} → 恢复后：{backupText.EncodingLabel} · {backupText.LineEndingLabel}）。"
                : $"文本行内容相同，但原始字节有变化（备份：{backupText.EncodingLabel} · {backupText.LineEndingLabel} → 当前：{liveText.EncodingLabel} · {liveText.LineEndingLabel}）。恢复将完整还原原始字节。";
            return new ConfigComparisonResult(
                Point: point,
                Direction: direction,
                Status: ConfigComparisonStatus.Changed,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "当前文件已变化（仅编码 BOM 或换行格式不同）",
                StatusDetail: formatDetail,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: true,
                DiffFallbackNote: "文本行无增删，差异来自 UTF-8/UTF-16 BOM 或 CRLF/LF 换行格式；恢复将严格按原始字节保留 BOM 与换行符。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        var statusDetail = restoreDir
            ? $"当前文件 → 恢复后：将移除 {diff.RemovedCount} 行当前内容、写入 {diff.AddedCount} 行备份内容（当前 {liveSnapshot.DisplaySize}，{liveSnapshot.DisplayFormatSummary} → 恢复后 {point.DisplaySize}，{point.DisplayFormatSummary}）。"
            : $"从备份到现在：原备份 {diff.RemovedCount} 行变更/移除，当前文件 {diff.AddedCount} 行变更/新增（备份 {point.DisplaySize}，{point.DisplayFormatSummary} → 当前 {liveSnapshot.DisplaySize}，{liveSnapshot.DisplayFormatSummary}）。";

        return new ConfigComparisonResult(
            Point: point,
            Direction: direction,
            Status: ConfigComparisonStatus.Changed,
            BackupIntegrityValid: true,
            BackupIntegrityError: "",
            LiveSnapshot: liveSnapshot,
            StatusHeadline: "当前文件已变化",
            StatusDetail: statusDetail,
            IsTextDiffAvailable: true,
            HasFormatOnlyDifference: false,
            DiffFallbackNote: diff.Truncated ? $"差异较长，仅展示前 {ConfigLineDiffer.MaxDisplayedRows} 行；恢复仍会完整写入备份的全部原始字节。" : "",
            RemovedLineCount: diff.RemovedCount,
            AddedLineCount: diff.AddedCount,
            IsDiffTruncated: diff.Truncated,
            DiffLines: diff.Lines);
    }

    public ConfigRestoreResult RestoreFromPoint(ConfigRestorePointRecord point, ConfigLiveFileSnapshot expectedLiveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(expectedLiveSnapshot);

        if (!TryReadValidatedBackupBlob(point, out var backupBytes, out var integrityError))
        {
            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.Failed,
                Headline: "恢复已阻止：备份文件已损坏或缺失",
                Detail: $"{integrityError} 目标文件未被修改。",
                TargetPath: point.TargetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                UndoSnapshot: null,
                RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent));
        }

        if (!TryNormalizeRegularFilePath(point.TargetPath, mustExist: false, out var targetPath, out var pathError))
        {
            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.Failed,
                Headline: "恢复失败：目标路径不是受支持的普通文件",
                Detail: $"{pathError} 现有路径未被修改。",
                TargetPath: point.TargetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                UndoSnapshot: null,
                RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent));
        }

        // Cross-process target coordination: acquire OS exclusive write lock (FileShare.Read) BEFORE reading/mutating!
        if (!TryAcquireTargetLock(targetPath, "restore", "", out var targetLock, out var busyLock) || targetLock is null)
        {
            var pidNote = busyLock is { OwnerPid: > 0 } ? $"（PID {busyLock.OwnerPid}）" : "";
            var opLabel = busyLock?.OperationKind == "undo" ? "撤销" : "恢复";
            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.BusyInAnotherWindow,
                Headline: "操作已暂缓：另一个 Contain 窗口正在操作此文件",
                Detail: $"检测到另一个 Contain 窗口{pidNote}正在对该配置文件执行{opLabel}。为避免并发写冲突，本次未写入目标文件，也未修改或抢先标记对方操作记录；请待对方完成后再刷新核对。",
                TargetPath: targetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                UndoSnapshot: null,
                RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent));
        }

        using (targetLock)
        {
            // Re-read live target AFTER acquiring lock (catches changes made by another window while we sat on preview!)
            var freshLive = CaptureLiveSnapshot(targetPath, out var freshLiveBytes);
            if (!freshLive.MatchesContentOf(expectedLiveSnapshot))
            {
                var alreadyIdentical = freshLive.Exists && freshLive.IsReadable &&
                    freshLive.ByteLength == point.ByteLength &&
                    string.Equals(freshLive.Sha256, point.Sha256, StringComparison.OrdinalIgnoreCase);
                if (alreadyIdentical)
                {
                    return new ConfigRestoreResult(
                        Outcome: ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit,
                        Headline: "目标文件已与所选备份一致（可能已由其他窗口完成恢复）",
                        Detail: "确认前重新读取目标文件与操作记录，发现磁盘内容已经等于该还原点，本次未重复写入，已刷新最新状态。",
                        TargetPath: targetPath,
                        ExpectedSha256: point.Sha256,
                        ActualSha256: freshLive.Sha256,
                        ExistingFilePreserved: true,
                        UndoSnapshot: null,
                        RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent));
                }

                // Keep restore-preview direction ("当前文件 → 恢复后") so user immediately sees fresh diff!
                var refreshed = TryCompareSafely(point, ConfigDiffDirection.CurrentToBackup);
                return new ConfigRestoreResult(
                    Outcome: ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit,
                    Headline: "目标文件在确认期间已被外部修改",
                    Detail: "为避免误覆盖刚刚发生的改动，本次尚未写入文件。已按「当前文件 → 恢复后」刷新最新差异，请核对后再次点击「确认恢复」。",
                    TargetPath: targetPath,
                    ExpectedSha256: point.Sha256,
                    ActualSha256: freshLive.Sha256,
                    ExistingFilePreserved: true,
                    UndoSnapshot: null,
                    RefreshedComparison: refreshed);
            }

            if (freshLive.Exists && (!freshLive.IsReadable || freshLiveBytes is null))
            {
                return new ConfigRestoreResult(
                    Outcome: ConfigRestoreOutcomeKind.Failed,
                    Headline: "恢复失败：覆盖前无法读取并保存当前版本",
                    Detail: $"无法保住当前文件副本（{freshLive.ReadError}），因此未执行覆盖；现有文件未被修改。",
                    TargetPath: targetPath,
                    ExpectedSha256: point.Sha256,
                    ActualSha256: "",
                    ExistingFilePreserved: true,
                    UndoSnapshot: null,
                    RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent));
            }

            return ExecutePhasedRestoreUnderLock(point, targetPath, backupBytes, freshLive, freshLiveBytes, targetLock);
        }
    }

    private ConfigRestoreResult ExecutePhasedRestoreUnderLock(
        ConfigRestorePointRecord point,
        string targetPath,
        byte[] backupBytes,
        ConfigLiveFileSnapshot freshLive,
        byte[]? freshLiveBytes,
        TargetOperationLock targetLock)
    {
        // STEP R1: Persist & verify original copy (B) + "restore_prepared" intent BEFORE touching target!
        ConfigUndoSnapshot preparedUndo;
        string? undoBlobPath = null;
        string? undoMetaPath = null;
        try
        {
            Directory.CreateDirectory(undoDir);
            var undoId = Guid.NewGuid().ToString("N")[..12];
            targetLock.UpdateUndoId(undoId);
            var undoBlobRel = "";
            if (freshLive.Exists && freshLiveBytes is not null)
            {
                undoBlobRel = Path.Combine("Undo", undoId + ".bin");
                undoBlobPath = Path.Combine(StorageRoot, undoBlobRel);
                WriteBytesAtomically(undoBlobPath, freshLiveBytes);
                var savedUndoSha = ComputeFileSha256Hex(undoBlobPath);
                if (!string.Equals(savedUndoSha, freshLive.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("撤销副本写入后哈希校验不一致。");
            }

            preparedUndo = new ConfigUndoSnapshot
            {
                SchemaVersion = 1,
                UndoId = undoId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                RestoredPointId = point.PointId,
                RestoredPointNote = point.Note,
                TargetPath = targetPath,
                TargetExistedBeforeRestore = freshLive.Exists,
                PreRestoreByteLength = freshLive.Exists ? freshLive.ByteLength : 0,
                PreRestoreSha256 = freshLive.Exists ? freshLive.Sha256 : "",
                PreRestoreBlobRelativePath = undoBlobRel,
                PreRestoreEncodingLabel = freshLive.Exists ? freshLive.EncodingLabel : "",
                PreRestoreLineEndingLabel = freshLive.Exists ? freshLive.LineEndingLabel : "",
                RestoredBackupByteLength = point.ByteLength,
                RestoredBackupSha256 = point.Sha256,
                OperationState = ConfigOperationStates.RestorePrepared,
                OwnerPid = Environment.ProcessId,
                OwnerProcessStartUtcTicks = CurrentProcessStartUtcTicks,
                LockFileRelativePath = targetLock.RelativePath,
                Consumed = false
            };

            undoMetaPath = Path.Combine(undoDir, undoId + ".json");
            WriteJsonAtomically(undoMetaPath, preparedUndo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Target was NEVER touched yet; safe to remove half-prepared undo artifacts.
            if (undoBlobPath is not null) TryDeleteFile(undoBlobPath);
            if (undoMetaPath is not null) TryDeleteFile(undoMetaPath);
            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.Failed,
                Headline: "恢复失败：无法保存覆盖前的撤销副本",
                Detail: $"为保障可撤销，覆盖已被取消；原文件未被修改。（{ex.Message}）",
                TargetPath: targetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: freshLive.Sha256,
                ExistingFilePreserved: true,
                UndoSnapshot: null,
                RefreshedComparison: null);
        }

        // Deterministic interruption seam: B copy & restore_prepared saved, target NOT replaced yet!
        operationHooks?.AfterRestorePrepared(preparedUndo.UndoId, targetPath);

        // STEP R2: Replace or recreate target with backup A (isolated catch: NEVER delete B unless target is provably untouched!)
        try
        {
            SafeWriteRawBytesToTarget(targetPath, backupBytes, point.Sha256, targetCurrentlyExists: freshLive.Exists);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var afterFailLive = CaptureLiveSnapshot(targetPath, out _);
            var intact = afterFailLive.MatchesContentOf(freshLive);
            if (intact)
            {
                // Target is provably still at pre-restore state -> record restore_aborted so reopen never shows a false restore.
                TryPersistUndoMetadata(preparedUndo with
                {
                    OperationState = ConfigOperationStates.RestoreAborted,
                    Consumed = true,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ReconciledNote = "写入目标失败，已核对原文件未被修改。"
                });
            }

            var reason = DescribeWriteFailure(ex);
            var intactNote = intact
                ? "已核对现有文件未被修改，内容完整保留。"
                : "目标状态未能确认仍等于覆盖前版本，已完整保住覆盖前原始副本；请使用「重新核对」或查看/另存原副本。";

            return new ConfigRestoreResult(
                Outcome: intact ? ConfigRestoreOutcomeKind.Failed : ConfigRestoreOutcomeKind.VerificationFailedBackupPreserved,
                Headline: intact ? "恢复失败：目标文件未能写入" : "恢复中断：写入异常，覆盖前原副本已保住",
                Detail: $"{reason} {intactNote}",
                TargetPath: targetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: afterFailLive.Sha256,
                ExistingFilePreserved: intact,
                UndoSnapshot: intact ? null : preparedUndo,
                RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent),
                OriginalCopyPreserved: !intact && freshLive.Exists);
        }

        // STEP R3: Post-replace readback verification (ISOLATED catch: NEVER deletes B's saved copy!)
        string postSha;
        try
        {
            operationHooks?.AfterTargetReplacedBeforeVerify(preparedUndo.UndoId, targetPath);
            postSha = ComputeFileSha256Hex(targetPath);
            if (!string.Equals(postSha, point.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"写入完成但目标文件 SHA-256 核对不一致（期望 {point.Sha256}，实际 {postSha}）。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Target replacement ALREADY executed! MUST preserve undoBlobPath & preparedUndo (restore_prepared) intact!
            var afterVerifyLive = CaptureLiveSnapshot(targetPath, out _);
            var copyNote = freshLive.Exists
                ? "覆盖前的原始副本（B）已完整保住且未被删除，可随时核对、撤销或另存原副本。"
                : "已保留“恢复前原文件缺失”的操作记录，可随时核对或通过撤销预览移除重建文件。";
            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.VerificationFailedBackupPreserved,
                Headline: "目标已替换但回读校验失败（原副本已保住）",
                Detail: $"目标文件替换后回读校验未通过（{ex.Message}）。{copyNote}",
                TargetPath: targetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: afterVerifyLive.Sha256,
                ExistingFilePreserved: false,
                UndoSnapshot: preparedUndo,
                RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent),
                OriginalCopyPreserved: freshLive.Exists);
        }

        // STEP R4: Persist "restore_completed" state (ISOLATED catch: NEVER deletes B's saved copy!)
        var completedUndo = preparedUndo with
        {
            OperationState = ConfigOperationStates.RestoreCompleted,
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
        try
        {
            operationHooks?.BeforeSaveRestoreCompleted(preparedUndo.UndoId, targetPath);
            WriteJsonAtomically(undoMetaPath, completedUndo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Target IS verified as A and B's copy IS on disk in restore_prepared; next check/reopen reconciles cleanly!
            var copyNote = freshLive.Exists
                ? "覆盖前原始副本已完整保住，重开 Contain 或点击「重新核对」后仍可预览撤销。"
                : "重建记录已保留，重开 Contain 或点击「重新核对」后仍可预览移除。";
            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.VerificationFailedBackupPreserved,
                Headline: "目标已恢复为备份版本，但完成状态暂未保存（原副本已保住）",
                Detail: $"目标文件已写入备份并核验 SHA-256 一致（{postSha[..12]}…），但更新本地操作状态失败（{ex.Message}）。{copyNote}",
                TargetPath: targetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: postSha,
                ExistingFilePreserved: true,
                UndoSnapshot: preparedUndo,
                RefreshedComparison: TryCompareSafely(point, ConfigDiffDirection.BackupToCurrent),
                OriginalCopyPreserved: freshLive.Exists);
        }

        // STEP R5: Refresh comparison OUTSIDE any destructive catch — failure here NEVER loses B or invalidates the completed restore!
        ConfigComparisonResult? refreshedComparison = null;
        var refreshFailed = false;
        string refreshError = "";
        try
        {
            operationHooks?.BeforePostRestoreRefresh(completedUndo.UndoId, targetPath);
            refreshedComparison = CompareRestorePoint(point, ConfigDiffDirection.BackupToCurrent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            refreshFailed = true;
            refreshError = ex.Message;
        }

        var baseDetail = freshLive.Exists
            ? $"已将备份原始字节写入目标文件并校验 SHA-256 一致（{point.Sha256[..12]}…），保留了原始 BOM 与换行格式。覆盖前版本已保存，可点击撤销进入预览。"
            : $"已在目标路径按备份原始字节创建文件并校验 SHA-256 一致（{point.Sha256[..12]}…）。可点击撤销预览并移除本次新建的文件。";
        var finalDetail = refreshFailed
            ? $"{baseDetail}（提示：恢复后刷新对比视图失败：{refreshError}，可点击「重新检查」刷新，原副本不受影响。）"
            : baseDetail;

        return new ConfigRestoreResult(
            Outcome: ConfigRestoreOutcomeKind.Succeeded,
            Headline: "已恢复到所选还原点",
            Detail: finalDetail,
            TargetPath: targetPath,
            ExpectedSha256: point.Sha256,
            ActualSha256: postSha,
            ExistingFilePreserved: true,
            UndoSnapshot: completedUndo,
            RefreshedComparison: refreshedComparison,
            OriginalCopyPreserved: freshLive.Exists,
            RefreshFailed: refreshFailed);
    }

    public IReadOnlyList<ConfigRecoveryReport> ReconcileOperationsOnStartup()
    {
        // GOLDEN RULE: Startup reconciliation ONLY reads target files — NEVER writes, overwrites, or deletes user target files!
        if (!Directory.Exists(undoDir))
            return [];

        var reports = new List<ConfigRecoveryReport>();
        var files = Directory.EnumerateFiles(undoDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .ToList();

        var parsed = new List<(string FilePath, ConfigUndoSnapshot Snapshot)>();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file, Utf8NoBom);
                var snap = JsonSerializer.Deserialize<ConfigUndoSnapshot>(json, JsonOptions);
                if (snap is null || string.IsNullOrWhiteSpace(snap.UndoId) || string.IsNullOrWhiteSpace(snap.TargetPath))
                {
                    reports.Add(BuildCorruptMetadataNeedsVerificationReport(file, "本地操作记录缺少关键字段（undo_id 或 target_path）。"));
                    continue;
                }
                parsed.Add((file, snap));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                reports.Add(BuildCorruptMetadataNeedsVerificationReport(file, $"无法解析本地操作记录：{ex.Message}"));
            }
        }

        parsed.Sort((a, b) =>
        {
            var cmp = b.Snapshot.CreatedAtUtc.CompareTo(a.Snapshot.CreatedAtUtc);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(b.Snapshot.UndoId, a.Snapshot.UndoId);
        });

        foreach (var (filePath, snap) in parsed)
        {
            if (snap.Consumed)
                continue;

            if (string.Equals(snap.OperationState, ConfigOperationStates.RestoreCompleted, StringComparison.Ordinal))
                continue;

            if (snap.IsLegacyRecord)
            {
                var legacyReport = TryReconcileLegacyRecord(snap);
                if (legacyReport is not null)
                    reports.Add(legacyReport);
                continue;
            }

            if (string.Equals(snap.OperationState, ConfigOperationStates.RestorePrepared, StringComparison.Ordinal))
            {
                if (IsOperationOwnedByAnotherLiveWindow(snap, out var liveLock))
                {
                    reports.Add(BuildActiveInAnotherWindowReport(snap, liveLock, "恢复"));
                    continue;
                }
                reports.Add(ReconcilePreparedRestore(filePath, snap));
                continue;
            }

            if (string.Equals(snap.OperationState, ConfigOperationStates.UndoPrepared, StringComparison.Ordinal))
            {
                if (IsOperationOwnedByAnotherLiveWindow(snap, out var liveLock))
                {
                    reports.Add(BuildActiveInAnotherWindowReport(snap, liveLock, "撤销"));
                    continue;
                }
                reports.Add(ReconcilePreparedUndo(filePath, snap));
                continue;
            }

            // Unknown/unrecognized non-consumed operation_state -> insufficient evidence -> "待核对"
            var liveUnknown = CaptureLiveSnapshot(snap.TargetPath, out _);
            var hasValidB = snap.TargetExistedBeforeRestore && TryReadValidatedUndoBlob(snap, out _, out _);
            reports.Add(new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.NeedsVerification,
                OperationRecord: snap,
                LiveSnapshot: liveUnknown,
                StatusBadge: "待核对",
                Headline: $"待核对：无法识别“{snap.DisplayTargetScopedName}”的操作阶段状态",
                Detail: $"本地操作记录包含未知阶段标记“{snap.OperationState}”，证据不足，启动检查未修改目标文件。",
                DiskStateSummary: FormatDiskStateSummary(liveUnknown, "状态未知"),
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, ""),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: false,
                RequiresExplicitArchive: true,
                LocalMetadataUpdated: false));
        }

        return reports;
    }

    public ConfigRecoveryReport? ReconcileLatestOperation()
    {
        var all = ReconcileOperationsOnStartup();
        return all.Count > 0 ? all[0] : null;
    }

    public bool IsTargetBusyInAnotherWindow(string targetPath, out ConfigTargetLockInfo? activeLock)
    {
        activeLock = null;
        if (!TryNormalizeRegularFilePath(targetPath, mustExist: false, out var normalized, out _))
            return false;
        var probed = ProbeLiveTargetLock(normalized);
        if (probed is not null && !string.Equals(probed.InstanceId, instanceId, StringComparison.Ordinal))
        {
            activeLock = probed;
            return true;
        }
        return false;
    }

    public void ArchiveRecoveryRecord(ConfigUndoSnapshot undoSnapshot, string reason)
    {
        ArgumentNullException.ThrowIfNull(undoSnapshot);
        if (string.IsNullOrWhiteSpace(undoSnapshot.UndoId))
            return;

        var metaPath = Path.Combine(undoDir, undoSnapshot.UndoId + ".json");
        if (!File.Exists(metaPath))
            return;

        // Archive local metadata ONLY; NEVER delete the original copy blob or modify the user's target file!
        var archived = undoSnapshot with
        {
            OperationState = ConfigOperationStates.ArchivedAfterConflict,
            Consumed = true,
            ReconciledAtUtc = DateTimeOffset.UtcNow,
            ReconciledNote = string.IsNullOrWhiteSpace(reason) ? "用户已知悉并归档该中断记录。" : reason
        };
        WriteJsonAtomically(metaPath, archived);
    }

    public ConfigOriginalCopyPreview InspectOriginalCopy(ConfigUndoSnapshot undoSnapshot)
    {
        ArgumentNullException.ThrowIfNull(undoSnapshot);

        if (!undoSnapshot.TargetExistedBeforeRestore)
        {
            return new ConfigOriginalCopyPreview(
                UndoSnapshot: undoSnapshot,
                IsValid: false,
                ErrorMessage: "该文件在恢复前原本不存在，因此没有覆盖前文件副本。",
                ByteLength: 0,
                Sha256: "",
                EncodingLabel: "原本缺失",
                LineEndingLabel: "不适用",
                IsTextAvailable: false,
                FallbackNote: "恢复前目标路径无文件。",
                IsTruncated: false,
                Lines: []);
        }

        if (!TryReadValidatedUndoBlob(undoSnapshot, out var rawBytes, out var error))
        {
            return new ConfigOriginalCopyPreview(
                UndoSnapshot: undoSnapshot,
                IsValid: false,
                ErrorMessage: error,
                ByteLength: undoSnapshot.PreRestoreByteLength,
                Sha256: undoSnapshot.PreRestoreSha256,
                EncodingLabel: undoSnapshot.PreRestoreEncodingLabel,
                LineEndingLabel: undoSnapshot.PreRestoreLineEndingLabel,
                IsTextAvailable: false,
                FallbackNote: error,
                IsTruncated: false,
                Lines: []);
        }

        var inspection = ConfigTextDecoder.Inspect(rawBytes);
        if (!inspection.IsRecognizedText)
        {
            return new ConfigOriginalCopyPreview(
                UndoSnapshot: undoSnapshot,
                IsValid: true,
                ErrorMessage: "",
                ByteLength: rawBytes.LongLength,
                Sha256: undoSnapshot.PreRestoreSha256,
                EncodingLabel: inspection.EncodingLabel,
                LineEndingLabel: inspection.LineEndingLabel,
                IsTextAvailable: false,
                FallbackNote: $"{inspection.UnrecognizedReason} 仍可使用「另存原副本…」按原始字节完整导出。",
                IsTruncated: false,
                Lines: []);
        }

        const int maxPreviewLines = 300;
        var lines = new List<ConfigCopyPreviewLine>(Math.Min(inspection.Lines.Count, maxPreviewLines + 1));
        var truncated = inspection.Lines.Count > maxPreviewLines;
        var limit = Math.Min(inspection.Lines.Count, maxPreviewLines);
        for (var i = 0; i < limit; i++)
            lines.Add(new ConfigCopyPreviewLine(i + 1, inspection.Lines[i]));
        if (truncated)
            lines.Add(new ConfigCopyPreviewLine(null, $"… 仅预览前 {maxPreviewLines} 行（共 {inspection.Lines.Count} 行），另存原副本将导出全部原始字节 …"));

        return new ConfigOriginalCopyPreview(
            UndoSnapshot: undoSnapshot,
            IsValid: true,
            ErrorMessage: "",
            ByteLength: rawBytes.LongLength,
            Sha256: undoSnapshot.PreRestoreSha256,
            EncodingLabel: inspection.EncodingLabel,
            LineEndingLabel: inspection.LineEndingLabel,
            IsTextAvailable: true,
            FallbackNote: truncated ? $"共 {inspection.Lines.Count} 行，预览显示前 {maxPreviewLines} 行；另存原副本将按原始字节完整保存。" : "",
            IsTruncated: truncated,
            Lines: lines);
    }

    public ConfigOriginalCopyExportResult ExportOriginalCopy(ConfigUndoSnapshot undoSnapshot, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(undoSnapshot);

        if (!undoSnapshot.TargetExistedBeforeRestore)
        {
            return new ConfigOriginalCopyExportResult(
                Succeeded: false,
                DestinationPath: destinationPath ?? "",
                ByteLength: 0,
                Sha256: "",
                Headline: "无法另存原副本",
                Detail: "该文件在恢复前原本缺失，没有可导出的覆盖前文件副本。");
        }

        if (!TryNormalizeRegularFilePath(destinationPath, mustExist: false, out var normalizedDest, out var pathError))
        {
            return new ConfigOriginalCopyExportResult(
                Succeeded: false,
                DestinationPath: destinationPath ?? "",
                ByteLength: 0,
                Sha256: "",
                Headline: "另存原副本失败：目标路径无效",
                Detail: pathError);
        }

        if (string.Equals(normalizedDest, Path.GetFullPath(undoSnapshot.TargetPath), StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigOriginalCopyExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                ByteLength: 0,
                Sha256: "",
                Headline: "已阻止直接覆盖当前配置文件",
                Detail: "为避免意外覆盖当前目标文件（如你新修改的版本），请另存到新的文件名或目录。");
        }

        if (!TryReadValidatedUndoBlob(undoSnapshot, out var rawBytes, out var blobError))
        {
            return new ConfigOriginalCopyExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                ByteLength: 0,
                Sha256: "",
                Headline: "另存原副本失败：原副本校验未通过",
                Detail: blobError);
        }

        try
        {
            WriteBytesAtomically(normalizedDest, rawBytes);
            var writtenSha = ComputeFileSha256Hex(normalizedDest);
            if (!string.Equals(writtenSha, undoSnapshot.PreRestoreSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"导出后 SHA-256 校验不一致（期望 {undoSnapshot.PreRestoreSha256}，实际 {writtenSha}）。");

            return new ConfigOriginalCopyExportResult(
                Succeeded: true,
                DestinationPath: normalizedDest,
                ByteLength: rawBytes.LongLength,
                Sha256: writtenSha,
                Headline: "已另存覆盖前原副本",
                Detail: $"已将验证通过的覆盖前原始字节另存至：{normalizedDest}（{ConfigFormatting.FormatByteSize(rawBytes.LongLength)}，SHA-256：{writtenSha[..12]}…），当前配置文件与本地原副本均未被改动。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ConfigOriginalCopyExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                ByteLength: 0,
                Sha256: "",
                Headline: "另存原副本失败",
                Detail: DescribeWriteFailure(ex));
        }
    }

    public ConfigPortablePackageExportResult ExportRestorePointPackage(ConfigRestorePointRecord point, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(point);

        if (!TryNormalizeRegularFilePath(destinationPath, mustExist: false, out var normalizedDest, out var pathError))
        {
            return new ConfigPortablePackageExportResult(
                Succeeded: false,
                DestinationPath: destinationPath ?? "",
                DisplayFileName: point.TargetFileName,
                ByteLength: 0,
                Sha256: "",
                Headline: "导出还原点失败：保存路径无效",
                Detail: pathError);
        }

        if (!string.IsNullOrWhiteSpace(point.TargetPath))
        {
            try
            {
                if (string.Equals(normalizedDest, Path.GetFullPath(point.TargetPath), StringComparison.OrdinalIgnoreCase))
                {
                    return new ConfigPortablePackageExportResult(
                        Succeeded: false,
                        DestinationPath: normalizedDest,
                        DisplayFileName: point.TargetFileName,
                        ByteLength: 0,
                        Sha256: "",
                        Headline: "已阻止覆盖原配置文件",
                        Detail: "迁移包应另存为独立的 .contain-config-point 文件，不能直接覆盖当前配置文件。");
                }
            }
            catch
            {
                // Ignore malformed historical target path; export relies strictly on the saved backup blob.
            }
        }

        // Always read & verify the SAVED backup blob (A), regardless of whether the source file was modified to C or deleted!
        if (!TryReadValidatedBackupBlob(point, out var backupBytes, out var blobError))
        {
            return new ConfigPortablePackageExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                DisplayFileName: point.TargetFileName,
                ByteLength: 0,
                Sha256: "",
                Headline: "导出还原点失败：本地备份文件校验未通过",
                Detail: blobError);
        }

        // Pre-write migration cap guard: NEVER write or overwrite any target or temp file when raw backup exceeds 32 MiB!
        if (backupBytes.LongLength > ConfigPortablePackageConstants.MaxRawPayloadBytes)
        {
            return new ConfigPortablePackageExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                DisplayFileName: point.TargetFileName,
                ByteLength: backupBytes.LongLength,
                Sha256: point.Sha256,
                Headline: "导出还原点失败：超过 32 MiB 迁移上限",
                Detail: $"这份还原点仍可在本机恢复，超过 32 MiB 的配置暂不支持迁移。当前备份大小为 {ConfigFormatting.FormatByteSize(backupBytes.LongLength)}，未修改目标文件或写入任何临时文件。");
        }

        var displayFileName = SanitizeDisplayFileName(point.TargetFileName);
        if (string.IsNullOrWhiteSpace(displayFileName))
            displayFileName = "config.dat";

        var inspection = ConfigTextDecoder.Inspect(backupBytes);
        var package = new ConfigPortablePackageFile
        {
            SchemaVersion = ConfigPortablePackageConstants.CurrentSchemaVersion,
            Format = ConfigPortablePackageConstants.FormatId,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = point.CreatedAtUtc,
            DisplayFileName = displayFileName,
            Note = point.Note ?? "",
            ByteLength = backupBytes.LongLength,
            Sha256 = point.Sha256.ToLowerInvariant(),
            EncodingLabel = string.IsNullOrWhiteSpace(point.EncodingLabel) ? inspection.EncodingLabel : point.EncodingLabel,
            LineEndingLabel = string.IsNullOrWhiteSpace(point.LineEndingLabel) ? inspection.LineEndingLabel : point.LineEndingLabel,
            PayloadBase64 = Convert.ToBase64String(backupBytes)
        };

        var targetDir = Path.GetDirectoryName(normalizedDest);
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return new ConfigPortablePackageExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                DisplayFileName: displayFileName,
                ByteLength: 0,
                Sha256: "",
                Headline: "导出还原点失败：保存目录无效",
                Detail: "无法解析目标文件所在目录，未写入磁盘。");
        }

        var destinationExistedBefore = File.Exists(normalizedDest);
        var tempPath = Path.Combine(targetDir, $".{Path.GetFileName(normalizedDest)}.contain-export-{Guid.NewGuid():N}.tmp");
        var committed = false;

        try
        {
            // Step 1: Write candidate package to a same-directory temp file first (never touch destination yet!)
            Directory.CreateDirectory(targetDir);
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(fs, package, JsonOptions);
                fs.WriteByte((byte)'\n');
                fs.Flush(flushToDisk: true);
            }

            // Step 2: Verify the staged temp package using the FORMAL importer BEFORE replacing destination!
            operationHooks?.BeforeVerifyStagedExportPackage(tempPath, normalizedDest);

            var tempInfo = new FileInfo(tempPath);
            if (!tempInfo.Exists || tempInfo.Length <= 0 || tempInfo.Length > ConfigPortablePackageConstants.MaxPackageFileBytes)
            {
                throw new InvalidDataException(
                    $"生成的迁移包体积超出安全上限（实际 {ConfigFormatting.FormatByteSize(tempInfo.Exists ? tempInfo.Length : 0)}，上限 {ConfigFormatting.FormatByteSize(ConfigPortablePackageConstants.MaxPackageFileBytes)}）。");
            }

            var verified = InspectPortablePackage(tempPath);
            if (!verified.IsValid ||
                !string.Equals(verified.Format, ConfigPortablePackageConstants.FormatId, StringComparison.Ordinal) ||
                verified.SchemaVersion != ConfigPortablePackageConstants.CurrentSchemaVersion ||
                verified.ByteLength != backupBytes.LongLength ||
                verified.RawBytes.LongLength != backupBytes.LongLength ||
                !string.Equals(verified.Sha256, point.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(verified.DisplayFileName, displayFileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(string.IsNullOrWhiteSpace(verified.ErrorMessage)
                    ? "新迁移包回读校验与原始还原点不一致。"
                    : verified.ErrorMessage);
            }

            // Step 3: Formal readback passed -> atomically commit temp file to destination!
            operationHooks?.BeforeReplaceExportDestination(tempPath, normalizedDest);

            if (File.Exists(normalizedDest))
            {
                File.Replace(tempPath, normalizedDest, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, normalizedDest, overwrite: true);
            }
            committed = true;

            return new ConfigPortablePackageExportResult(
                Succeeded: true,
                DestinationPath: normalizedDest,
                DisplayFileName: displayFileName,
                ByteLength: backupBytes.LongLength,
                Sha256: verified.Sha256,
                Headline: "已导出配置还原点迁移包",
                Detail: $"已将还原点保存的备份版本（{displayFileName}，{ConfigFormatting.FormatByteSize(backupBytes.LongLength)}，SHA-256：{verified.DisplayShortSha256}）校验通过并导出至：{normalizedDest}。迁移包不包含源机目录路径或撤销记录，请妥善保管其中可能包含的账号、令牌或私人配置内容。");
        }
        catch (InvalidDataException ex)
        {
            var preserveNote = destinationExistedBefore
                ? "原有目标文件已逐字节保留，临时文件已清理。"
                : "未创建新目标文件，临时文件已清理。";
            return new ConfigPortablePackageExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                DisplayFileName: displayFileName,
                ByteLength: 0,
                Sha256: "",
                Headline: "导出还原点失败：新迁移包校验未通过",
                Detail: $"{ex.Message} {preserveNote}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var preserveNote = destinationExistedBefore
                ? "原有目标文件未被修改，临时文件已清理。"
                : "未留下不完整的目标文件，临时文件已清理。";
            return new ConfigPortablePackageExportResult(
                Succeeded: false,
                DestinationPath: normalizedDest,
                DisplayFileName: displayFileName,
                ByteLength: 0,
                Sha256: "",
                Headline: "导出还原点失败",
                Detail: $"{DescribeWriteFailure(ex)} {preserveNote}");
        }
        finally
        {
            if (!committed)
                TryDeleteFile(tempPath);
        }
    }

    public ConfigPortablePackageInspection InspectPortablePackage(string packagePath)
    {
        if (!TryNormalizeRegularFilePath(packagePath, mustExist: true, out var normalizedPath, out var pathError))
            return InvalidPackage(packagePath ?? "", pathError);

        try
        {
            var info = new FileInfo(normalizedPath);
            if (info.Length == 0)
                return InvalidPackage(normalizedPath, "迁移包文件为空，无法识别。");
            if (info.Length > ConfigPortablePackageConstants.MaxPackageFileBytes)
                return InvalidPackage(normalizedPath, $"迁移包体积超过安全限制（{ConfigFormatting.FormatByteSize(info.Length)}），已拒绝读取。");

            var fileBytes = ReadAllBytesShared(normalizedPath);
            ReadOnlyMemory<byte> utf8Slice = fileBytes.Length >= 3 && fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF
                ? fileBytes.AsMemory(3)
                : fileBytes.AsMemory();

            using var doc = JsonDocument.Parse(utf8Slice);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return InvalidPackage(normalizedPath, "迁移包格式无法识别：根内容不是有效的 JSON 对象。");

            var root = doc.RootElement;
            if (!root.TryGetProperty("format", out var fmtProp) || fmtProp.ValueKind != JsonValueKind.String)
                return InvalidPackage(normalizedPath, "未知文件格式：缺少 Contain 配置还原点迁移包格式标识（format）。");

            var format = fmtProp.GetString() ?? "";
            if (!string.Equals(format, ConfigPortablePackageConstants.FormatId, StringComparison.Ordinal))
                return InvalidPackage(normalizedPath, $"不支持的迁移包格式“{format}”；请选择 Contain 导出的 .contain-config-point 迁移包。");

            if (!root.TryGetProperty("schema_version", out var verProp) ||
                verProp.ValueKind != JsonValueKind.Number ||
                !verProp.TryGetInt32(out var schemaVersion))
            {
                return InvalidPackage(normalizedPath, "迁移包缺少有效的格式版本号（schema_version）。");
            }

            if (schemaVersion != ConfigPortablePackageConstants.CurrentSchemaVersion)
                return InvalidPackage(normalizedPath, $"不支持的迁移包格式版本 v{schemaVersion}（当前仅支持 v{ConfigPortablePackageConstants.CurrentSchemaVersion}），请升级 Contain 后重试。");

            var parsed = JsonSerializer.Deserialize<ConfigPortablePackageFile>(utf8Slice.Span, JsonOptions);
            if (parsed is null)
                return InvalidPackage(normalizedPath, "迁移包结构解析失败，文件可能已损坏。");

            if (string.IsNullOrWhiteSpace(parsed.DisplayFileName) || !IsSafeLeafFileName(parsed.DisplayFileName))
                return InvalidPackage(normalizedPath, "迁移包中的显示文件名缺失或包含非法路径字符，已拒绝导入。");

            if (parsed.ByteLength < 0 || parsed.ByteLength > ConfigPortablePackageConstants.MaxRawPayloadBytes)
                return InvalidPackage(normalizedPath, $"迁移包声明的字节长度无效（{parsed.ByteLength}）。");

            if (!IsValidSha256Hex(parsed.Sha256))
                return InvalidPackage(normalizedPath, "迁移包缺少有效的 64 位 SHA-256 校验摘要，文件可能已损坏。");

            if (parsed.PayloadBase64 is null || (parsed.ByteLength > 0 && parsed.PayloadBase64.Length == 0))
                return InvalidPackage(normalizedPath, "迁移包缺少配置原始字节内容（payload_base64）。");

            byte[] rawBytes;
            try
            {
                rawBytes = parsed.PayloadBase64.Length == 0 ? [] : Convert.FromBase64String(parsed.PayloadBase64);
            }
            catch (FormatException)
            {
                return InvalidPackage(normalizedPath, "迁移包内容已损坏：原始字节载荷（Base64）无法解码。");
            }

            if (rawBytes.LongLength != parsed.ByteLength)
            {
                return InvalidPackage(
                    normalizedPath,
                    $"迁移包长度不符：声明 {parsed.ByteLength} 字节，实际解码为 {rawBytes.LongLength} 字节。已阻止导入，本机目标文件未被修改。");
            }

            var actualSha = ComputeSha256Hex(rawBytes);
            if (!string.Equals(actualSha, parsed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return InvalidPackage(
                    normalizedPath,
                    $"迁移包内容校验失败：声明 SHA-256 为 {parsed.Sha256[..12]}…，实际内容为 {actualSha[..12]}…。文件可能已损坏或被修改，已阻止导入。");
            }

            var textInfo = ConfigTextDecoder.Inspect(rawBytes);
            return new ConfigPortablePackageInspection(
                IsValid: true,
                PackagePath: normalizedPath,
                SchemaVersion: schemaVersion,
                Format: format,
                ExportedAtUtc: parsed.ExportedAtUtc,
                CreatedAtUtc: parsed.CreatedAtUtc,
                DisplayFileName: parsed.DisplayFileName.Trim(),
                Note: parsed.Note ?? "",
                ByteLength: rawBytes.LongLength,
                Sha256: actualSha,
                EncodingLabel: textInfo.EncodingLabel,
                LineEndingLabel: textInfo.LineEndingLabel,
                RawBytes: rawBytes);
        }
        catch (JsonException ex)
        {
            return InvalidPackage(normalizedPath, $"未知或损坏的迁移包格式（JSON 解析失败：{ex.Message}）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InvalidPackage(normalizedPath, $"无法读取迁移包文件：{ex.Message}");
        }
    }

    public ConfigImportPreviewResult PreviewImportedPackageTarget(ConfigPortablePackageInspection package, string rawTargetPath)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!package.IsValid)
        {
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: rawTargetPath ?? "",
                Status: ConfigImportTargetStatus.InvalidTargetPath,
                LiveSnapshot: new ConfigLiveFileSnapshot(false, false, 0, "", 0, "", "", package.ErrorMessage),
                DirectionLabel: "本机当前 → 恢复后",
                Headline: "迁移包未通过校验",
                Detail: package.ErrorMessage,
                ActionSummary: "请先选择有效的 .contain-config-point 迁移包。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: package.ErrorMessage,
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        if (!TryNormalizeRegularFilePath(rawTargetPath, mustExist: false, out var targetPath, out var pathError))
        {
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: rawTargetPath ?? "",
                Status: ConfigImportTargetStatus.InvalidTargetPath,
                LiveSnapshot: new ConfigLiveFileSnapshot(false, false, 0, "", 0, "", "", pathError),
                DirectionLabel: "本机当前 → 恢复后",
                Headline: "目标路径无效",
                Detail: pathError,
                ActionSummary: "请通过文件选择器指定本机上的普通配置文件路径（已有文件或待新建路径）。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: pathError,
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        var live = CaptureLiveSnapshot(targetPath, out var liveBytes);

        if (IsTargetBusyInAnotherWindow(targetPath, out var busyLock))
        {
            var pidNote = busyLock is { OwnerPid: > 0 } ? $"（PID {busyLock.OwnerPid}）" : "";
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: targetPath,
                Status: ConfigImportTargetStatus.TargetBusyInAnotherWindow,
                LiveSnapshot: live,
                DirectionLabel: "本机当前 → 恢复后（其他窗口正在操作）",
                Headline: $"另一个 Contain 窗口{pidNote}正在操作此目标文件",
                Detail: "为避免并发写入冲突，当前不可导入恢复到该文件；请待对方完成后点击刷新或重新预览。",
                ActionSummary: "尚未写入磁盘，目标文件保持原样。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: "另一个 Contain 窗口正持有此目标文件的写锁。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        if (!live.Exists)
        {
            var detail = $"本机当前路径尚无此文件 → 确认后将在以下完整路径按原始字节新建文件：{targetPath}（{package.DisplaySize}，{package.DisplayFormatSummary}，SHA-256：{package.DisplayShortSha256}）。";
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: targetPath,
                Status: ConfigImportTargetStatus.ReadyToCreate,
                LiveSnapshot: live,
                DirectionLabel: "本机当前（缺失） → 恢复后（将新建）",
                Headline: "目标文件当前缺失，确认后将新建该文件",
                Detail: detail,
                ActionSummary: "将使用本机新记录保存此还原点并按原始字节新建目标文件；若后续撤销，预览会预告移除本次新建的文件。取消不会写入磁盘。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"将新建完整路径：{targetPath}\n待写入大小与格式：{package.DisplaySize}（{package.DisplayFormatSummary}）\n待写入 SHA-256：{package.Sha256}",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        if (!live.IsReadable || liveBytes is null)
        {
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: targetPath,
                Status: ConfigImportTargetStatus.TargetReadFailed,
                LiveSnapshot: live,
                DirectionLabel: "本机当前 → 恢复后（读取失败）",
                Headline: "无法读取本机目标文件",
                Detail: string.IsNullOrWhiteSpace(live.ReadError) ? "目标文件可能正被其他程序独占占用或权限受限。" : live.ReadError,
                ActionSummary: "覆盖前需要读取并保存本机当前版本以便撤销，当前不可执行写入。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"读取失败原因：{live.ReadError}；迁移包内容 {package.DisplaySize}，SHA-256：{package.Sha256}。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        if (live.ByteLength == package.ByteLength &&
            string.Equals(live.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: targetPath,
                Status: ConfigImportTargetStatus.IdenticalToTarget,
                LiveSnapshot: live,
                DirectionLabel: "本机当前 → 恢复后（内容一致）",
                Headline: "本机目标文件已与迁移包完全一致",
                Detail: $"字节数（{package.DisplaySize}）、编码 BOM、换行格式与 SHA-256（{package.DisplayShortSha256}）均逐字节一致，当前无需覆盖写入。",
                ActionSummary: "未修改目标文件；如需迁移到其他配置路径，可在上方重新选择目标文件。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: "本机当前文件与迁移包内容逐字节一致，无需重复写入。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        var liveText = ConfigTextDecoder.Inspect(liveBytes);
        var pkgText = ConfigTextDecoder.Inspect(package.RawBytes);
        var actionSummary = "覆盖前自动保存本机撤销副本（B）并创建本机还原点，恢复后可随时逐字节撤销；取消不写入磁盘。";

        if (!liveText.IsRecognizedText || !pkgText.IsRecognizedText)
        {
            var reason = !liveText.IsRecognizedText ? liveText.UnrecognizedReason : pkgText.UnrecognizedReason;
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: targetPath,
                Status: ConfigImportTargetStatus.ReadyToOverwrite,
                LiveSnapshot: live,
                DirectionLabel: "本机当前 → 恢复后",
                Headline: "本机当前 → 恢复后：按原始字节覆盖目标文件",
                Detail: $"编码无法可靠识别（本机当前 {live.DisplaySize}，{live.DisplayShortSha256} → 恢复后 {package.DisplaySize}，{package.DisplayShortSha256}）；确认后将按原始字节精确写入。",
                ActionSummary: actionSummary,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"{reason} 不会重新编码，始终写入迁移包的原始字节。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        var diff = ConfigLineDiffer.Compute(liveText.Lines, pkgText.Lines);
        if (diff.RemovedCount == 0 && diff.AddedCount == 0)
        {
            return new ConfigImportPreviewResult(
                Package: package,
                TargetPath: targetPath,
                Status: ConfigImportTargetStatus.ReadyToOverwrite,
                LiveSnapshot: live,
                DirectionLabel: "本机当前 → 恢复后",
                Headline: "本机当前 → 恢复后：仅编码 BOM 或换行格式变化",
                Detail: $"文本行内容相同，但编码或换行将变化（本机当前：{live.DisplayFormatSummary} → 恢复后：{package.DisplayFormatSummary}）。",
                ActionSummary: actionSummary,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: true,
                DiffFallbackNote: "文本行无增删，确认后将严格按迁移包原始字节保留 BOM 与换行格式。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        return new ConfigImportPreviewResult(
            Package: package,
            TargetPath: targetPath,
            Status: ConfigImportTargetStatus.ReadyToOverwrite,
            LiveSnapshot: live,
            DirectionLabel: "本机当前 → 恢复后",
            Headline: $"本机当前 → 恢复后：将移除 {diff.RemovedCount} 行本机内容、写入 {diff.AddedCount} 行迁移包内容",
            Detail: $"本机当前 {live.DisplaySize}（{live.DisplayFormatSummary}，{live.DisplayShortSha256}）→ 恢复后 {package.DisplaySize}（{package.DisplayFormatSummary}，{package.DisplayShortSha256}）。",
            ActionSummary: actionSummary,
            IsTextDiffAvailable: true,
            HasFormatOnlyDifference: false,
            DiffFallbackNote: diff.Truncated ? $"差异较长，仅展示前 {ConfigLineDiffer.MaxDisplayedRows} 行；确认后仍将完整写入迁移包的全部原始字节。" : "",
            RemovedLineCount: diff.RemovedCount,
            AddedLineCount: diff.AddedCount,
            IsDiffTruncated: diff.Truncated,
            DiffLines: diff.Lines);
    }

    public ConfigImportRestoreResult ImportAndRestorePackage(
        ConfigPortablePackageInspection package,
        string rawTargetPath,
        ConfigLiveFileSnapshot expectedLiveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(expectedLiveSnapshot);

        if (!package.IsValid ||
            package.RawBytes.LongLength != package.ByteLength ||
            !string.Equals(ComputeSha256Hex(package.RawBytes), package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigImportRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.Failed,
                Headline: "导入恢复已阻止：迁移包校验未通过",
                Detail: string.IsNullOrWhiteSpace(package.ErrorMessage) ? "迁移包内容完整性校验失败，目标文件未被修改。" : package.ErrorMessage,
                TargetPath: rawTargetPath ?? "",
                ExpectedSha256: package.Sha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                ImportedPoint: null,
                UndoSnapshot: null);
        }

        if (!TryNormalizeRegularFilePath(rawTargetPath, mustExist: false, out var targetPath, out var pathError))
        {
            return new ConfigImportRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.Failed,
                Headline: "导入恢复失败：目标路径无效",
                Detail: $"{pathError} 现有路径未被修改。",
                TargetPath: rawTargetPath ?? "",
                ExpectedSha256: package.Sha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                ImportedPoint: null,
                UndoSnapshot: null);
        }

        // Reuse v0.7.0 cross-process per-target exclusive write lock!
        if (!TryAcquireTargetLock(targetPath, "restore", "", out var targetLock, out var busyLock) || targetLock is null)
        {
            var pidNote = busyLock is { OwnerPid: > 0 } ? $"（PID {busyLock.OwnerPid}）" : "";
            var opLabel = busyLock?.OperationKind == "undo" ? "撤销" : "恢复";
            return new ConfigImportRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.BusyInAnotherWindow,
                Headline: "操作已暂缓：另一个 Contain 窗口正在操作此文件",
                Detail: $"检测到另一个 Contain 窗口{pidNote}正在对该配置文件执行{opLabel}。为避免并发写冲突，本次未写入目标文件，请待对方完成后再刷新重试。",
                TargetPath: targetPath,
                ExpectedSha256: package.Sha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                ImportedPoint: null,
                UndoSnapshot: null,
                RefreshedImportPreview: PreviewImportedPackageTarget(package, targetPath));
        }

        using (targetLock)
        {
            // Re-read live target AFTER acquiring lock to catch any external edit during preview!
            var freshLive = CaptureLiveSnapshot(targetPath, out var freshLiveBytes);
            if (!freshLive.MatchesContentOf(expectedLiveSnapshot))
            {
                var refreshedPreview = PreviewImportedPackageTarget(package, targetPath);
                var alreadyIdentical = refreshedPreview.Status == ConfigImportTargetStatus.IdenticalToTarget;
                return new ConfigImportRestoreResult(
                    Outcome: ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit,
                    Headline: alreadyIdentical
                        ? "目标文件已与迁移包一致（未重复写入）"
                        : "目标文件在确认期间已被外部修改",
                    Detail: alreadyIdentical
                        ? "确认前重新读取目标文件，发现磁盘内容已等于该迁移包，本次未重复写入，已刷新最新状态。"
                        : "为保住目标文件的最新改动，本次尚未写入磁盘。已刷新「本机当前 → 恢复后」差异，请核对后再次确认。",
                    TargetPath: targetPath,
                    ExpectedSha256: package.Sha256,
                    ActualSha256: freshLive.Sha256,
                    ExistingFilePreserved: true,
                    ImportedPoint: null,
                    UndoSnapshot: null,
                    RefreshedImportPreview: refreshedPreview);
            }

            if (freshLive.Exists && (!freshLive.IsReadable || freshLiveBytes is null))
            {
                return new ConfigImportRestoreResult(
                    Outcome: ConfigRestoreOutcomeKind.Failed,
                    Headline: "导入恢复失败：覆盖前无法读取并保存本机当前版本",
                    Detail: $"无法保住本机当前文件副本（{freshLive.ReadError}），因此未执行覆盖；现有文件未被修改。",
                    TargetPath: targetPath,
                    ExpectedSha256: package.Sha256,
                    ActualSha256: "",
                    ExistingFilePreserved: true,
                    ImportedPoint: null,
                    UndoSnapshot: null,
                    RefreshedImportPreview: PreviewImportedPackageTarget(package, targetPath));
            }

            // Create a brand-new LOCAL restore point identity bound to THIS receiving machine's targetPath!
            // Source-machine target path and undo history are NEVER imported.
            ConfigRestorePointRecord localPoint;
            string? pointBlobPath = null;
            string? pointMetaPath = null;
            try
            {
                Directory.CreateDirectory(pointsDir);
                Directory.CreateDirectory(blobsDir);

                var pointId = Guid.NewGuid().ToString("N")[..12];
                var blobRel = Path.Combine("Blobs", pointId + ".bin");
                pointBlobPath = Path.Combine(StorageRoot, blobRel);
                WriteBytesAtomically(pointBlobPath, package.RawBytes);
                var savedSha = ComputeFileSha256Hex(pointBlobPath);
                if (!string.Equals(savedSha, package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("导入还原点副本写入后 SHA-256 校验不一致。");

                var dirPath = Path.GetDirectoryName(targetPath) ?? "";
                var parentFolder = Path.GetFileName(dirPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                localPoint = new ConfigRestorePointRecord
                {
                    SchemaVersion = 1,
                    PointId = pointId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Note = string.IsNullOrWhiteSpace(package.Note) ? $"导入自迁移包（{package.DisplayFileName}）" : package.Note.Trim(),
                    TargetPath = targetPath,
                    TargetFileName = Path.GetFileName(targetPath),
                    TargetDirectory = dirPath,
                    ParentFolderName = string.IsNullOrWhiteSpace(parentFolder) ? dirPath : parentFolder,
                    ByteLength = package.RawBytes.LongLength,
                    Sha256 = package.Sha256,
                    BackupBlobRelativePath = blobRel,
                    EncodingLabel = package.EncodingLabel,
                    LineEndingLabel = package.LineEndingLabel
                };

                pointMetaPath = Path.Combine(pointsDir, pointId + ".json");
                WriteJsonAtomically(pointMetaPath, localPoint);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (pointBlobPath is not null) TryDeleteFile(pointBlobPath);
                if (pointMetaPath is not null) TryDeleteFile(pointMetaPath);
                return new ConfigImportRestoreResult(
                    Outcome: ConfigRestoreOutcomeKind.Failed,
                    Headline: "导入恢复失败：无法在本机保存导入的还原点",
                    Detail: $"目标文件未被修改。（{ex.Message}）",
                    TargetPath: targetPath,
                    ExpectedSha256: package.Sha256,
                    ActualSha256: freshLive.Sha256,
                    ExistingFilePreserved: true,
                    ImportedPoint: null,
                    UndoSnapshot: null);
            }

            // Execute the EXACT SAME v0.7.0 phased crash-safe restore core under the held targetLock!
            var restoreRes = ExecutePhasedRestoreUnderLock(localPoint, targetPath, package.RawBytes, freshLive, freshLiveBytes, targetLock);

            // If restore failed before modifying the target (provably untouched), roll back the tentative local point so no clutter remains.
            if (restoreRes.Outcome == ConfigRestoreOutcomeKind.Failed && restoreRes.ExistingFilePreserved)
            {
                if (pointBlobPath is not null) TryDeleteFile(pointBlobPath);
                if (pointMetaPath is not null) TryDeleteFile(pointMetaPath);
                return new ConfigImportRestoreResult(
                    Outcome: ConfigRestoreOutcomeKind.Failed,
                    Headline: restoreRes.Headline,
                    Detail: restoreRes.Detail,
                    TargetPath: targetPath,
                    ExpectedSha256: package.Sha256,
                    ActualSha256: restoreRes.ActualSha256,
                    ExistingFilePreserved: true,
                    ImportedPoint: null,
                    UndoSnapshot: null,
                    RefreshedImportPreview: PreviewImportedPackageTarget(package, targetPath));
            }

            var headline = restoreRes.Outcome == ConfigRestoreOutcomeKind.Succeeded
                ? (freshLive.Exists ? "已导入还原点并恢复目标文件" : "已导入还原点并新建目标文件")
                : restoreRes.Headline;

            return new ConfigImportRestoreResult(
                Outcome: restoreRes.Outcome,
                Headline: headline,
                Detail: restoreRes.Detail,
                TargetPath: restoreRes.TargetPath,
                ExpectedSha256: restoreRes.ExpectedSha256,
                ActualSha256: restoreRes.ActualSha256,
                ExistingFilePreserved: restoreRes.ExistingFilePreserved,
                ImportedPoint: localPoint,
                UndoSnapshot: restoreRes.UndoSnapshot,
                OriginalCopyPreserved: restoreRes.OriginalCopyPreserved,
                RefreshFailed: restoreRes.RefreshFailed);
        }
    }

    private static ConfigPortablePackageInspection InvalidPackage(string path, string error) =>
        new(
            IsValid: false,
            PackagePath: path,
            SchemaVersion: 0,
            Format: "",
            ExportedAtUtc: default,
            CreatedAtUtc: default,
            DisplayFileName: "",
            Note: "",
            ByteLength: 0,
            Sha256: "",
            EncodingLabel: "",
            LineEndingLabel: "",
            RawBytes: [],
            ErrorMessage: error);

    private static string SanitizeDisplayFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var leaf = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or "..") return "";
        foreach (var bad in Path.GetInvalidFileNameChars())
            leaf = leaf.Replace(bad, '_');
        return leaf.Trim();
    }

    private static bool IsSafeLeafFileName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed is "." or "..") return false;
        if (trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.Contains(':') || trimmed.Contains("..")) return false;
        return trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool IsValidSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
        foreach (var ch in value)
        {
            var ok = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    public ConfigUndoSnapshot? LoadLatestActiveUndo()
    {
        if (!Directory.Exists(undoDir))
            return null;

        ConfigUndoSnapshot? latest = null;
        foreach (var file in Directory.EnumerateFiles(undoDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(file, Utf8NoBom);
                var snap = JsonSerializer.Deserialize<ConfigUndoSnapshot>(json, JsonOptions);
                if (snap is null || snap.Consumed || string.IsNullOrWhiteSpace(snap.UndoId) || string.IsNullOrWhiteSpace(snap.TargetPath))
                    continue;

                // ONLY completed restores (or unambiguous legacy Beta.1/Beta.2 active records) may appear in the active Undo bar!
                if (!string.Equals(snap.OperationState, ConfigOperationStates.RestoreCompleted, StringComparison.Ordinal))
                {
                    if (!snap.IsLegacyRecord)
                        continue;

                    // Legacy Beta.1/Beta.2 record without operation_state: if disk is already at pre-restore state,
                    // do NOT show a misleading Undo button — ReconcileOperationsOnStartup surfaces it as "待核对".
                    if (IsLegacyAlreadyAtPreRestoreState(snap))
                        continue;
                }

                if (IsOperationOwnedByAnotherLiveWindow(snap, out _))
                    continue;

                if (latest is null || snap.CreatedAtUtc > latest.CreatedAtUtc)
                    latest = snap;
            }
            catch
            {
                // Corrupt metadata is reported by ReconcileOperationsOnStartup.
            }
        }
        return latest;
    }

    public ConfigUndoPreviewResult InspectUndoPreview(ConfigUndoSnapshot undoSnapshot)
    {
        ArgumentNullException.ThrowIfNull(undoSnapshot);

        // Always re-read latest record from disk so stale previews never act on an already-consumed/updated record!
        var latestOnDisk = TryLoadUndoRecordById(undoSnapshot.UndoId);
        if (latestOnDisk is not null)
            undoSnapshot = latestOnDisk;

        if (IsOperationOwnedByAnotherLiveWindow(undoSnapshot, out var activeLock) ||
            IsTargetBusyInAnotherWindow(undoSnapshot.TargetPath, out activeLock))
        {
            var pidNote = activeLock is { OwnerPid: > 0 } ? $"（PID {activeLock.OwnerPid}）" : "";
            return BuildUndoErrorPreview(
                undoSnapshot,
                ConfigUndoPreviewStatus.BusyInAnotherWindow,
                CaptureLiveSnapshot(undoSnapshot.TargetPath, out _),
                "另一个 Contain 窗口正在操作此文件",
                $"检测到另一个 Contain 窗口{pidNote}正在对该目标文件执行写入。为避免并发冲突，当前不可执行撤销，也未修改或消耗撤销记录；请待对方完成后点击「刷新」。");
        }

        if (undoSnapshot.Consumed)
        {
            return BuildUndoErrorPreview(
                undoSnapshot,
                ConfigUndoPreviewStatus.UndoSnapshotInvalid,
                CaptureLiveSnapshot(undoSnapshot.TargetPath, out _),
                "本次恢复已撤销或状态已更新",
                "重新读取操作记录发现该撤销记录已使用或已关闭，无需重复撤销。");
        }

        if (!TryNormalizeRegularFilePath(undoSnapshot.TargetPath, mustExist: false, out var targetPath, out var pathError))
        {
            return BuildUndoErrorPreview(
                undoSnapshot,
                ConfigUndoPreviewStatus.TargetReadFailed,
                new ConfigLiveFileSnapshot(true, false, 0, "", 0, "", "", pathError),
                "无法撤销：目标路径不是受支持的普通文件",
                pathError);
        }

        // Validate undo blob integrity first if target existed before restore
        byte[]? preRestoreBytes = null;
        ConfigTextInspection? preText = null;
        if (undoSnapshot.TargetExistedBeforeRestore)
        {
            if (!TryReadValidatedUndoBlob(undoSnapshot, out preRestoreBytes, out var blobError))
            {
                return BuildUndoErrorPreview(
                    undoSnapshot,
                    ConfigUndoPreviewStatus.UndoSnapshotInvalid,
                    CaptureLiveSnapshot(targetPath, out _),
                    "无法撤销：覆盖前副本已损坏或缺失",
                    $"{blobError} 为保住现有文件，已阻止撤销。");
            }
            preText = ConfigTextDecoder.Inspect(preRestoreBytes);
        }
        var hasValidOriginalCopy = undoSnapshot.TargetExistedBeforeRestore && preRestoreBytes is not null;

        // Check live target existence & content match against RestoredBackupSha256!
        var live = CaptureLiveSnapshot(targetPath, out var liveBytes);

        if (!live.Exists)
        {
            return new ConfigUndoPreviewResult(
                UndoSnapshot: undoSnapshot,
                Status: ConfigUndoPreviewStatus.TargetDeletedConflict,
                LiveSnapshot: live,
                DirectionLabel: "当前文件 → 撤销后（存在冲突）",
                Headline: "撤销冲突：恢复后目标文件已被主动删除",
                Detail: $"目标文件在恢复写入版本（{undoSnapshot.DisplayRestoredShortSha}）之后已不存在。为保住你的最新删除状态，已阻止撤销，不会重新写入文件。",
                ActionSummary: hasValidOriginalCopy
                    ? "已保住用户删除后的当前状态，未修改磁盘，也不会静默消耗撤销记录；你可查看或另存覆盖前原副本。"
                    : "已保住用户删除后的当前状态，未修改磁盘，也不会静默消耗撤销记录。",
                PreRestoreEncodingLabel: preText?.EncodingLabel ?? "不适用",
                PreRestoreLineEndingLabel: preText?.LineEndingLabel ?? "不适用",
                PreRestoreByteLength: undoSnapshot.PreRestoreByteLength,
                PreRestoreSha256: undoSnapshot.PreRestoreSha256,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: "恢复写入的文件已从目标路径被删除；为避免覆盖或复活用户主动删除的文件，撤销已被阻止。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: [],
                HasValidOriginalCopy: hasValidOriginalCopy);
        }

        if (!live.IsReadable || liveBytes is null)
        {
            return BuildUndoErrorPreview(
                undoSnapshot,
                ConfigUndoPreviewStatus.TargetReadFailed,
                live,
                "无法撤销：当前目标文件读取失败",
                string.IsNullOrWhiteSpace(live.ReadError) ? "无法读取当前目标文件，请检查是否被占用或权限受限。" : live.ReadError,
                hasValidOriginalCopy);
        }

        if (!string.Equals(live.Sha256, undoSnapshot.RestoredBackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigUndoPreviewResult(
                UndoSnapshot: undoSnapshot,
                Status: ConfigUndoPreviewStatus.TargetModifiedConflict,
                LiveSnapshot: live,
                DirectionLabel: "当前文件 → 撤销后（存在冲突）",
                Headline: "撤销冲突：恢复后目标文件已被外部修改",
                Detail: $"恢复时写入的 SHA-256 为 {undoSnapshot.DisplayRestoredShortSha}，但当前文件已变为 {live.DisplayShortSha256}（{live.DisplaySize}）。为保住你的最新修改，已阻止撤销。",
                ActionSummary: hasValidOriginalCopy
                    ? "已保住当前文件最新内容与覆盖前原副本，未修改磁盘，也不会静默消耗撤销记录；你可查看或另存原副本。"
                    : "已保住当前文件最新内容，未修改磁盘，也不会静默消耗撤销记录。",
                PreRestoreEncodingLabel: preText?.EncodingLabel ?? "不适用",
                PreRestoreLineEndingLabel: preText?.LineEndingLabel ?? "不适用",
                PreRestoreByteLength: undoSnapshot.PreRestoreByteLength,
                PreRestoreSha256: undoSnapshot.PreRestoreSha256,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"当前文件（{live.Sha256}）已不再匹配上次恢复写入的版本（{undoSnapshot.RestoredBackupSha256}）；为防止丢失新修改，已禁止撤销。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: [],
                HasValidOriginalCopy: hasValidOriginalCopy);
        }

        // Case B: Originally missing, recreated by Contain during restore, and still unchanged since restore!
        if (!undoSnapshot.TargetExistedBeforeRestore)
        {
            return new ConfigUndoPreviewResult(
                UndoSnapshot: undoSnapshot,
                Status: ConfigUndoPreviewStatus.ReadyToRemoveRecreatedFile,
                LiveSnapshot: live,
                DirectionLabel: "当前文件 → 撤销后（移除重建文件）",
                Headline: "撤销预览：将移除本次重建的文件",
                Detail: $"该文件恢复前原本缺失、由 Contain 恢复时重建，且此后内容未变（当前 {live.DisplaySize}，SHA-256：{live.DisplayShortSha256}）。",
                ActionSummary: "确认撤销后将移除本次恢复重建的文件，使目标路径回到恢复前的缺失状态。点击「取消」不会写盘，也不会消耗撤销记录。",
                PreRestoreEncodingLabel: "原本缺失",
                PreRestoreLineEndingLabel: "不适用",
                PreRestoreByteLength: 0,
                PreRestoreSha256: "",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"将移除本次恢复重建的文件（当前 {live.DisplaySize}，{live.DisplayFormatSummary}，SHA-256：{live.Sha256}）。点击「确认撤销」前不会删除文件。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: [],
                HasValidOriginalCopy: false);
        }

        // Case A: File existed before restore -> compute Current -> Undo Target (preRestoreBytes) diff!
        var liveText = ConfigTextDecoder.Inspect(liveBytes);
        var targetText = preText!;
        var targetFmt = string.IsNullOrWhiteSpace(targetText.LineEndingLabel) || targetText.LineEndingLabel == "不适用"
            ? targetText.EncodingLabel
            : $"{targetText.EncodingLabel} · {targetText.LineEndingLabel}";
        var actionSummary = "将用覆盖前保存的原始字节写回目标文件（严格保留原 BOM 与换行格式）；取消不会写盘，也不消耗撤销记录。";

        if (!liveText.IsRecognizedText || !targetText.IsRecognizedText)
        {
            var reason = !liveText.IsRecognizedText ? liveText.UnrecognizedReason : targetText.UnrecognizedReason;
            return new ConfigUndoPreviewResult(
                UndoSnapshot: undoSnapshot,
                Status: ConfigUndoPreviewStatus.ReadyToRestorePreviousBytes,
                LiveSnapshot: live,
                DirectionLabel: "当前文件 → 撤销后",
                Headline: "撤销预览：按原始字节还原覆盖前版本",
                Detail: "当前文件 → 撤销后：编码无法可靠识别，已展示大小与 SHA-256；撤销仍将严格写入覆盖前原始字节。",
                ActionSummary: actionSummary,
                PreRestoreEncodingLabel: targetText.EncodingLabel,
                PreRestoreLineEndingLabel: targetText.LineEndingLabel,
                PreRestoreByteLength: undoSnapshot.PreRestoreByteLength,
                PreRestoreSha256: undoSnapshot.PreRestoreSha256,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"{reason} 撤销时不会重新编码，始终写回覆盖前原始字节。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: [],
                HasValidOriginalCopy: hasValidOriginalCopy);
        }

        // Undo diff direction is ALWAYS Current (live/restored A) -> After Undo (preRestore B)!
        var diff = ConfigLineDiffer.Compute(liveText.Lines, targetText.Lines);
        if (diff.RemovedCount == 0 && diff.AddedCount == 0)
        {
            return new ConfigUndoPreviewResult(
                UndoSnapshot: undoSnapshot,
                Status: ConfigUndoPreviewStatus.ReadyToRestorePreviousBytes,
                LiveSnapshot: live,
                DirectionLabel: "当前文件 → 撤销后",
                Headline: "撤销预览：仅恢复原有编码 BOM 或换行格式",
                Detail: $"文本行相同，撤销后编码与换行将还原（当前：{live.DisplayFormatSummary} → 撤销后：{targetFmt}）。",
                ActionSummary: actionSummary,
                PreRestoreEncodingLabel: targetText.EncodingLabel,
                PreRestoreLineEndingLabel: targetText.LineEndingLabel,
                PreRestoreByteLength: undoSnapshot.PreRestoreByteLength,
                PreRestoreSha256: undoSnapshot.PreRestoreSha256,
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: true,
                DiffFallbackNote: "文本行无增删，撤销将按原始字节还原覆盖前的 BOM 与换行格式。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: [],
                HasValidOriginalCopy: hasValidOriginalCopy);
        }

        return new ConfigUndoPreviewResult(
            UndoSnapshot: undoSnapshot,
            Status: ConfigUndoPreviewStatus.ReadyToRestorePreviousBytes,
            LiveSnapshot: live,
            DirectionLabel: "当前文件 → 撤销后",
            Headline: "撤销预览：将还原为覆盖前版本",
            Detail: $"当前文件 → 撤销后：将移除 {diff.RemovedCount} 行当前内容、写回 {diff.AddedCount} 行覆盖前内容（当前 {live.DisplaySize}，{live.DisplayFormatSummary} → 撤销后 {ConfigFormatting.FormatByteSize(undoSnapshot.PreRestoreByteLength)}，{targetFmt}）。",
            ActionSummary: actionSummary,
            PreRestoreEncodingLabel: targetText.EncodingLabel,
            PreRestoreLineEndingLabel: targetText.LineEndingLabel,
            PreRestoreByteLength: undoSnapshot.PreRestoreByteLength,
            PreRestoreSha256: undoSnapshot.PreRestoreSha256,
            IsTextDiffAvailable: true,
            HasFormatOnlyDifference: false,
            DiffFallbackNote: diff.Truncated ? $"差异较长，仅展示前 {ConfigLineDiffer.MaxDisplayedRows} 行；撤销仍会完整写回覆盖前全部原始字节。" : "",
            RemovedLineCount: diff.RemovedCount,
            AddedLineCount: diff.AddedCount,
            IsDiffTruncated: diff.Truncated,
            DiffLines: diff.Lines,
            HasValidOriginalCopy: hasValidOriginalCopy);
    }

    public ConfigUndoResult UndoRestore(ConfigUndoSnapshot undoSnapshot, ConfigLiveFileSnapshot? expectedLiveSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(undoSnapshot);

        if (!TryNormalizeRegularFilePath(undoSnapshot.TargetPath, mustExist: false, out var targetPath, out var pathError))
        {
            return new ConfigUndoResult(
                Outcome: ConfigUndoOutcomeKind.Failed,
                Headline: "撤销失败：目标路径不是受支持的普通文件",
                Detail: pathError,
                TargetPath: undoSnapshot.TargetPath,
                ExpectedSha256: undoSnapshot.PreRestoreSha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                RefreshedUndoPreview: InspectUndoPreview(undoSnapshot));
        }

        // Cross-process target coordination: acquire OS exclusive write lock (FileShare.Read) BEFORE re-reading/mutating!
        if (!TryAcquireTargetLock(targetPath, "undo", undoSnapshot.UndoId, out var targetLock, out var busyLock) || targetLock is null)
        {
            var pidNote = busyLock is { OwnerPid: > 0 } ? $"（PID {busyLock.OwnerPid}）" : "";
            var opLabel = busyLock?.OperationKind == "undo" ? "撤销" : "恢复";
            return new ConfigUndoResult(
                Outcome: ConfigUndoOutcomeKind.BusyInAnotherWindow,
                Headline: "撤销已暂缓：另一个 Contain 窗口正在操作此文件",
                Detail: $"检测到另一个 Contain 窗口{pidNote}正在对该配置文件执行{opLabel}。为避免并发冲突，本次未写入目标文件，也未修改或消耗撤销记录；请待对方完成后再刷新。",
                TargetPath: targetPath,
                ExpectedSha256: undoSnapshot.PreRestoreSha256,
                ActualSha256: "",
                ExistingFilePreserved: true,
                RefreshedUndoPreview: InspectUndoPreview(undoSnapshot));
        }

        using (targetLock)
        {
            // Re-read operation record from disk AFTER acquiring lock (catches Window 1 completing undo/restore while Window 2 held an old preview!)
            var freshRecord = TryLoadUndoRecordById(undoSnapshot.UndoId);
            if (freshRecord is null ||
                freshRecord.Consumed ||
                freshRecord.OperationState is ConfigOperationStates.UndoCompleted or ConfigOperationStates.RestoreAborted or ConfigOperationStates.ArchivedAfterConflict ||
                HasNewerActiveRestoreForTarget(targetPath, freshRecord))
            {
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.StaleAlreadyHandled,
                    Headline: "操作记录已更新（可能已由其他窗口撤销或完成）",
                    Detail: "确认前重新读取目标文件与操作记录，发现本次恢复已被其他窗口处理或已被更新的恢复替代。本次未重复写盘，已刷新最新状态。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: CaptureLiveSnapshot(targetPath, out _).Sha256,
                    ExistingFilePreserved: true);
            }

            undoSnapshot = freshRecord;

            byte[]? preRestoreBytes = null;
            if (undoSnapshot.TargetExistedBeforeRestore)
            {
                if (!TryReadValidatedUndoBlob(undoSnapshot, out preRestoreBytes, out var blobError))
                {
                    return new ConfigUndoResult(
                        Outcome: ConfigUndoOutcomeKind.Failed,
                        Headline: "撤销失败：覆盖前副本已损坏或缺失",
                        Detail: $"{blobError} 目标文件保持不变。",
                        TargetPath: targetPath,
                        ExpectedSha256: undoSnapshot.PreRestoreSha256,
                        ActualSha256: "",
                        ExistingFilePreserved: true,
                        RefreshedUndoPreview: InspectUndoPreview(undoSnapshot));
                }
            }

            // Re-check live target RIGHT BEFORE writing/deleting (protects against edits or deletions made after restore OR during Undo Preview!)
            var live = CaptureLiveSnapshot(targetPath, out _);

            if (!live.Exists)
            {
                var freshPreview = InspectUndoPreview(undoSnapshot);
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.TargetDeletedConflict,
                    Headline: "撤销已阻止：目标文件已被主动删除",
                    Detail: "核对发现恢复后的目标文件已不存在；为保住你的最新状态，未重新写入文件，且未消耗撤销记录。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: "",
                    ExistingFilePreserved: true,
                    RefreshedUndoPreview: freshPreview);
            }

            if (!live.IsReadable)
            {
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Failed,
                    Headline: "撤销已中止：当前无法读取目标文件",
                    Detail: $"{live.ReadError} 现有文件未被修改。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: "",
                    ExistingFilePreserved: true,
                    RefreshedUndoPreview: InspectUndoPreview(undoSnapshot));
            }

            var matchesRestored = string.Equals(live.Sha256, undoSnapshot.RestoredBackupSha256, StringComparison.OrdinalIgnoreCase);
            var matchesPreview = expectedLiveSnapshot is null || live.MatchesContentOf(expectedLiveSnapshot);
            if (!matchesRestored || !matchesPreview)
            {
                var freshPreview = InspectUndoPreview(undoSnapshot);
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.ExternalEditConflict,
                    Headline: "撤销已阻止：恢复后目标文件又被外部修改",
                    Detail: "实际写入前再次核对发现目标文件已不再等于恢复版本；为保住你的最新改动，已中止撤销且未修改现有文件。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: live.Sha256,
                    ExistingFilePreserved: true,
                    RefreshedUndoPreview: freshPreview);
            }

            // STEP U1: Persist "undo_prepared" intent BEFORE modifying or deleting target!
            var metaPath = Path.Combine(undoDir, undoSnapshot.UndoId + ".json");
            var preparedUndo = undoSnapshot with
            {
                OperationState = ConfigOperationStates.UndoPrepared,
                UndoStartedAtUtc = DateTimeOffset.UtcNow,
                OwnerPid = Environment.ProcessId,
                OwnerProcessStartUtcTicks = CurrentProcessStartUtcTicks,
                LockFileRelativePath = targetLock.RelativePath,
                Consumed = false
            };
            try
            {
                Directory.CreateDirectory(undoDir);
                WriteJsonAtomically(metaPath, preparedUndo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Failed,
                    Headline: "撤销未开始：无法保存撤销状态标记",
                    Detail: $"为避免中断后状态不明，尚未改动目标文件（{ex.Message}）。请检查权限后重试。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: live.Sha256,
                    ExistingFilePreserved: true,
                    RefreshedUndoPreview: InspectUndoPreview(undoSnapshot));
            }

            operationHooks?.AfterUndoPrepared(preparedUndo.UndoId, targetPath);

            // STEP U2: Perform target write-back (B) or delete recreated file, and verify!
            string actualShaAfter = "";
            try
            {
                if (undoSnapshot.TargetExistedBeforeRestore && preRestoreBytes is not null)
                {
                    SafeWriteRawBytesToTarget(targetPath, preRestoreBytes, undoSnapshot.PreRestoreSha256, targetCurrentlyExists: true);
                    actualShaAfter = ComputeFileSha256Hex(targetPath);
                    if (!string.Equals(actualShaAfter, undoSnapshot.PreRestoreSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"撤销后校验不一致（期望 {undoSnapshot.PreRestoreSha256}，实际 {actualShaAfter}）。");
                }
                else
                {
                    File.Delete(targetPath);
                    if (File.Exists(targetPath))
                        throw new IOException("删除本次恢复重建的文件失败，目标文件仍存在。");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var afterFail = CaptureLiveSnapshot(targetPath, out _);
                var intact = afterFail.MatchesContentOf(live);
                if (intact)
                {
                    // Target is provably still at A -> revert state back to restore_completed so user can retry cleanly!
                    TryPersistUndoMetadata(undoSnapshot with
                    {
                        OperationState = ConfigOperationStates.RestoreCompleted,
                        Consumed = false
                    });
                }
                // If target state changed or unreadable, leave undo_prepared + B blob intact so reconciliation verifies disk!
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Failed,
                    Headline: "撤销失败：无法写回或移除目标文件",
                    Detail: $"{DescribeWriteFailure(ex)} {(intact ? "当前目标文件未被破坏。" : "目标状态未能确认，覆盖前原副本已保住，请重新核对当前文件状态。")}",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: afterFail.Sha256,
                    ExistingFilePreserved: intact,
                    RefreshedUndoPreview: InspectUndoPreview(undoSnapshot));
            }

            // STEP U3: Persist "undo_completed" (consumed = true) — NEVER silently swallow failure!
            try
            {
                operationHooks?.AfterUndoTargetUpdatedBeforeMarkCompleted(preparedUndo.UndoId, targetPath);
                var completedUndo = preparedUndo with
                {
                    OperationState = ConfigOperationStates.UndoCompleted,
                    Consumed = true,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                };
                WriteJsonAtomically(metaPath, completedUndo);

                return undoSnapshot.TargetExistedBeforeRestore
                    ? new ConfigUndoResult(
                        Outcome: ConfigUndoOutcomeKind.Succeeded,
                        Headline: "已撤销本次恢复",
                        Detail: $"已将“{undoSnapshot.DisplayTargetScopedName}”还原为覆盖前的原始字节（SHA-256：{actualShaAfter[..12]}…），BOM 与换行格式均已完整保留。",
                        TargetPath: targetPath,
                        ExpectedSha256: undoSnapshot.PreRestoreSha256,
                        ActualSha256: actualShaAfter,
                        ExistingFilePreserved: true,
                        MetadataPersisted: true)
                    : new ConfigUndoResult(
                        Outcome: ConfigUndoOutcomeKind.Succeeded,
                        Headline: "已撤销本次恢复（已移除重建文件）",
                        Detail: $"“{undoSnapshot.DisplayTargetScopedName}”恢复前原本缺失，现已确认移除本次恢复重建的文件。",
                        TargetPath: targetPath,
                        ExpectedSha256: "",
                        ActualSha256: "",
                        ExistingFilePreserved: true,
                        MetadataPersisted: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Target IS verified back to B (or cleanly removed)! Metadata stays in undo_prepared;
                // next startup reconciliation will verify disk read-only and finalize without double-writing!
                var diskOutcome = undoSnapshot.TargetExistedBeforeRestore
                    ? $"目标文件已成功写回覆盖前版本（SHA-256：{actualShaAfter[..12]}…）"
                    : "本次恢复重建的文件已确认移除";
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.SucceededMetadataPending,
                    Headline: "目标已撤销还原，但完成标记暂未保存",
                    Detail: $"{diskOutcome}，但保存完成标记失败（{ex.Message}）。请勿重复执行；下次启动 Contain 将只读核对当前磁盘状态并补记完成，不会重复写盘。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: actualShaAfter,
                    ExistingFilePreserved: true,
                    MetadataPersisted: false);
            }
        }
    }

    private ConfigRecoveryReport ReconcilePreparedRestore(string filePath, ConfigUndoSnapshot snap)
    {
        var blobErr = "";
        var hasValidB = snap.TargetExistedBeforeRestore && TryReadValidatedUndoBlob(snap, out _, out blobErr);
        var copyErr = snap.TargetExistedBeforeRestore ? blobErr : "";
        var live = CaptureLiveSnapshot(snap.TargetPath, out _);

        var missingRequiredHash = string.IsNullOrWhiteSpace(snap.RestoredBackupSha256) ||
            (snap.TargetExistedBeforeRestore && string.IsNullOrWhiteSpace(snap.PreRestoreSha256));
        var ambiguousSameHash = snap.TargetExistedBeforeRestore &&
            !string.IsNullOrWhiteSpace(snap.PreRestoreSha256) &&
            string.Equals(snap.PreRestoreSha256, snap.RestoredBackupSha256, StringComparison.OrdinalIgnoreCase);

        if (missingRequiredHash || ambiguousSameHash || (live.Exists && !live.IsReadable))
        {
            var reason = missingRequiredHash
                ? "本地操作记录缺少足够哈希摘要，无法可靠判定恢复是否已落地。"
                : ambiguousSameHash
                    ? "记录中的覆盖前哈希与目标备份哈希相同，无法单凭哈希区分恢复是否执行。"
                    : $"当前无法读取目标文件（{live.ReadError}），暂时无法确认磁盘实际版本。";
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.NeedsVerification,
                OperationRecord: snap,
                LiveSnapshot: live,
                StatusBadge: "待核对",
                Headline: $"待核对：上次对“{snap.DisplayTargetScopedName}”的恢复中断",
                Detail: $"{reason} 启动检查只读核对，未修改目标文件；请解除占用后点击「重新核对」或查看/另存原副本。",
                DiskStateSummary: FormatDiskStateSummary(live, "无法确认"),
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: false,
                RequiresExplicitArchive: true,
                LocalMetadataUpdated: false);
        }

        // Case 1: Live disk matches backup A!
        if (live.Exists && string.Equals(live.Sha256, snap.RestoredBackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (snap.TargetExistedBeforeRestore && !hasValidB)
            {
                return new ConfigRecoveryReport(
                    Status: ConfigRecoveryStatus.NeedsVerification,
                    OperationRecord: snap,
                    LiveSnapshot: live,
                    StatusBadge: "待核对",
                    Headline: $"待核对：“{snap.DisplayTargetScopedName}”当前为备份版本，但覆盖前副本校验未通过",
                    Detail: $"已核实磁盘当前为备份版本（{live.DisplayShortSha256}），但覆盖前原副本不可用（{copyErr}），因此无法直接执行撤销。",
                    DiskStateSummary: FormatDiskStateSummary(live, "与恢复目标（备份 A）一致"),
                    OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB: false, copyErr),
                    HasValidOriginalCopy: false,
                    CanOfferUndoPreview: false,
                    RequiresExplicitArchive: true,
                    LocalMetadataUpdated: false);
            }

            var promoted = snap with
            {
                OperationState = ConfigOperationStates.RestoreCompleted,
                Consumed = false,
                ReconciledAtUtc = DateTimeOffset.UtcNow,
                ReconciledNote = "启动只读核对确认目标已替换为备份版本 A，补记完成状态并开放撤销预览。"
            };
            var updated = TryPersistUndoMetadataToPath(filePath, promoted);
            var copyText = snap.TargetExistedBeforeRestore
                ? $"覆盖前原版本（{snap.DisplayPreRestoreShortSha}）已完整保住，你可通过「预览并撤销恢复…」安全回退。"
                : "该文件恢复前原本缺失，现已确认按备份重建；你可通过「预览并撤销恢复…」确认移除重建文件。";
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.RestoreAppliedNeedsReview,
                OperationRecord: updated ? promoted : snap,
                LiveSnapshot: live,
                StatusBadge: "已核实：恢复已生效",
                Headline: $"上次恢复中断核对：“{snap.DisplayTargetScopedName}”当前已是备份版本",
                Detail: $"Contain 上次在替换目标后、记录结果前中断。启动只读核对确认磁盘当前已是备份版本（{live.DisplayShortSha256}），{copyText}",
                DiskStateSummary: FormatDiskStateSummary(live, "已替换为备份版本（A）"),
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: true,
                RequiresExplicitArchive: false,
                LocalMetadataUpdated: updated);
        }

        // Case 2: Live disk STILL matches pre-restore state (B, or Missing if originally missing) -> Moment 1!
        var matchesPre = snap.TargetExistedBeforeRestore
            ? (live.Exists && string.Equals(live.Sha256, snap.PreRestoreSha256, StringComparison.OrdinalIgnoreCase))
            : !live.Exists;
        if (matchesPre)
        {
            var aborted = snap with
            {
                OperationState = ConfigOperationStates.RestoreAborted,
                Consumed = true,
                ReconciledAtUtc = DateTimeOffset.UtcNow,
                ReconciledNote = "启动只读核对确认目标在中断前未被替换，未产生虚假恢复。"
            };
            var updated = TryPersistUndoMetadataToPath(filePath, aborted);
            var preDesc = snap.TargetExistedBeforeRestore
                ? $"磁盘当前仍为覆盖前原版本（{live.DisplayShortSha256}）"
                : "目标路径仍保持缺失状态";
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.RestoreNotApplied,
                OperationRecord: updated ? aborted : snap,
                LiveSnapshot: live,
                StatusBadge: "恢复未生效（原文件未变）",
                Headline: $"上次恢复在写入前中断：“{snap.DisplayTargetScopedName}”未被修改",
                Detail: $"Contain 在保存覆盖前副本后、替换目标前意外退出。启动只读核对确认{preDesc}，未发生覆盖，也不显示虚假的成功恢复。",
                DiskStateSummary: snap.TargetExistedBeforeRestore
                    ? FormatDiskStateSummary(live, "仍为覆盖前原版本（B）")
                    : "目标文件不存在（与恢复前一致）",
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: false,
                RequiresExplicitArchive: false,
                LocalMetadataUpdated: updated);
        }

        // Case 3: Previously existing file is now missing!
        if (!live.Exists && snap.TargetExistedBeforeRestore)
        {
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.TargetMissingAfterInterruption,
                OperationRecord: snap,
                LiveSnapshot: live,
                StatusBadge: "目标文件已缺失",
                Headline: $"恢复中断核对：“{snap.DisplayTargetScopedName}”当前已缺失",
                Detail: "上次恢复中断后目标文件已不存在。启动检查未自动创建或覆盖文件；覆盖前原副本已完整保留，你可以查看或另存原副本。",
                DiskStateSummary: "目标文件当前不存在（原文件恢复前曾存在）",
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: false,
                RequiresExplicitArchive: true,
                LocalMetadataUpdated: false);
        }

        // Case 4: Live disk is third version C (neither A nor B/Missing)!
        var cDetail = hasValidB
            ? $"当前文件（{live.DisplayShortSha256}）既不是备份版本（{snap.DisplayRestoredShortSha}），也不是覆盖前原版本（{snap.DisplayPreRestoreShortSha}）。已完整保留你最新的文件与覆盖前原副本，未自动覆盖磁盘；你可以查看或另存原副本。"
            : $"当前文件（{live.DisplayShortSha256}）与恢复备份（{snap.DisplayRestoredShortSha}）不同，且该文件恢复前原本缺失。已完整保留你最新的文件，未自动覆盖或删除。";
        return new ConfigRecoveryReport(
            Status: ConfigRecoveryStatus.ExternalVersionCDetected,
            OperationRecord: snap,
            LiveSnapshot: live,
            StatusBadge: "发现第三版（C）",
            Headline: $"恢复中断后目标已被修改：“{snap.DisplayTargetScopedName}”已保留最新内容",
            Detail: cDetail,
            DiskStateSummary: FormatDiskStateSummary(live, "已变为第三版（既非备份 A 也非原版 B）"),
            OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
            HasValidOriginalCopy: hasValidB,
            CanOfferUndoPreview: false,
            RequiresExplicitArchive: true,
            LocalMetadataUpdated: false);
    }

    private ConfigRecoveryReport ReconcilePreparedUndo(string filePath, ConfigUndoSnapshot snap)
    {
        var blobErr = "";
        var hasValidB = snap.TargetExistedBeforeRestore && TryReadValidatedUndoBlob(snap, out _, out blobErr);
        var copyErr = snap.TargetExistedBeforeRestore ? blobErr : "";
        var live = CaptureLiveSnapshot(snap.TargetPath, out _);

        var missingRequiredHash = string.IsNullOrWhiteSpace(snap.RestoredBackupSha256) ||
            (snap.TargetExistedBeforeRestore && string.IsNullOrWhiteSpace(snap.PreRestoreSha256));
        var ambiguousSameHash = snap.TargetExistedBeforeRestore &&
            !string.IsNullOrWhiteSpace(snap.PreRestoreSha256) &&
            string.Equals(snap.PreRestoreSha256, snap.RestoredBackupSha256, StringComparison.OrdinalIgnoreCase);

        if (missingRequiredHash || ambiguousSameHash || (live.Exists && !live.IsReadable))
        {
            var reason = missingRequiredHash || ambiguousSameHash
                ? "撤销操作记录缺少足够的区分哈希依据，无法确认撤销是否已落地。"
                : $"当前无法读取目标文件（{live.ReadError}），暂时无法确认撤销结果。";
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.NeedsVerification,
                OperationRecord: snap,
                LiveSnapshot: live,
                StatusBadge: "待核对",
                Headline: $"待核对：上次对“{snap.DisplayTargetScopedName}”的撤销中断",
                Detail: $"{reason} 启动检查只读核对，未修改目标文件；请点击「重新核对」或查看/另存原副本。",
                DiskStateSummary: FormatDiskStateSummary(live, "无法确认"),
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: false,
                RequiresExplicitArchive: true,
                LocalMetadataUpdated: false);
        }

        // Case 1: Live disk ALREADY matches post-undo expected state (B if existed, or Missing if originally missing) -> Moment 3!
        var matchesUndone = snap.TargetExistedBeforeRestore
            ? (live.Exists && string.Equals(live.Sha256, snap.PreRestoreSha256, StringComparison.OrdinalIgnoreCase))
            : !live.Exists;
        if (matchesUndone)
        {
            var finalized = snap with
            {
                OperationState = ConfigOperationStates.UndoCompleted,
                Consumed = true,
                CompletedAtUtc = snap.CompletedAtUtc ?? DateTimeOffset.UtcNow,
                ReconciledAtUtc = DateTimeOffset.UtcNow,
                ReconciledNote = "启动只读核对确认上次撤销已生效，已补记完成状态，未重复写盘。"
            };
            var updated = TryPersistUndoMetadataToPath(filePath, finalized);
            var actualDesc = snap.TargetExistedBeforeRestore
                ? $"目标文件当前已是覆盖前原版本（{live.DisplayShortSha256}）"
                : "本次恢复重建的文件已被移除（目标路径已恢复缺失状态）";
            var metaDesc = updated
                ? "已补记撤销完成状态，后续启动不会重复写盘。"
                : "本次启动未修改目标文件，但本地完成标记仍暂未写入，再次启动仍只读核对且不会重复写盘。";
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.UndoAlreadyApplied,
                OperationRecord: updated ? finalized : snap,
                LiveSnapshot: live,
                StatusBadge: "已核实：撤销已生效",
                Headline: $"上次撤销中断核对：“{snap.DisplayTargetScopedName}”已实际完成撤销",
                Detail: $"启动只读核对确认{actualDesc}。{metaDesc}",
                DiskStateSummary: snap.TargetExistedBeforeRestore
                    ? FormatDiskStateSummary(live, "已写回覆盖前原版本（B）")
                    : "目标文件不存在（重建文件已移除）",
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: false,
                RequiresExplicitArchive: false,
                LocalMetadataUpdated: updated);
        }

        // Case 2: Live disk STILL matches restored backup A -> Undo interrupted BEFORE writing B!
        if (live.Exists && string.Equals(live.Sha256, snap.RestoredBackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (snap.TargetExistedBeforeRestore && !hasValidB)
            {
                return new ConfigRecoveryReport(
                    Status: ConfigRecoveryStatus.NeedsVerification,
                    OperationRecord: snap,
                    LiveSnapshot: live,
                    StatusBadge: "待核对",
                    Headline: $"待核对：“{snap.DisplayTargetScopedName}”仍为恢复版本，但覆盖前副本校验未通过",
                    Detail: $"目标文件仍为恢复版本（{live.DisplayShortSha256}），但覆盖前原副本不可用（{copyErr}），已停止自动处理。",
                    DiskStateSummary: FormatDiskStateSummary(live, "仍为恢复版本（A）"),
                    OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB: false, copyErr),
                    HasValidOriginalCopy: false,
                    CanOfferUndoPreview: false,
                    RequiresExplicitArchive: true,
                    LocalMetadataUpdated: false);
            }

            var reverted = snap with
            {
                OperationState = ConfigOperationStates.RestoreCompleted,
                Consumed = false,
                ReconciledAtUtc = DateTimeOffset.UtcNow,
                ReconciledNote = "启动只读核对确认上次撤销在修改磁盘前中断，磁盘仍为备份 A，可重新预览撤销。"
            };
            var updated = TryPersistUndoMetadataToPath(filePath, reverted);
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.UndoNotApplied,
                OperationRecord: updated ? reverted : snap,
                LiveSnapshot: live,
                StatusBadge: "撤销未生效（仍为恢复版）",
                Headline: $"上次撤销在写盘前中断：“{snap.DisplayTargetScopedName}”仍为恢复版本",
                Detail: $"启动只读核对确认磁盘仍为恢复写入的备份版本（{live.DisplayShortSha256}），覆盖前原副本完好；你可随时重新点击「预览并撤销恢复…」。",
                DiskStateSummary: FormatDiskStateSummary(live, "仍为恢复版本（A）"),
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: true,
                RequiresExplicitArchive: false,
                LocalMetadataUpdated: updated);
        }

        // Case 3: Previously existing file is now missing!
        if (!live.Exists && snap.TargetExistedBeforeRestore)
        {
            return new ConfigRecoveryReport(
                Status: ConfigRecoveryStatus.TargetMissingAfterInterruption,
                OperationRecord: snap,
                LiveSnapshot: live,
                StatusBadge: "目标文件已缺失",
                Headline: $"撤销中断核对：“{snap.DisplayTargetScopedName}”当前已缺失",
                Detail: "上次撤销中断后目标文件已不存在。启动检查未自动写盘，并已保留覆盖前原副本；你可以查看或另存原副本。",
                DiskStateSummary: "目标文件当前不存在",
                OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
                HasValidOriginalCopy: hasValidB,
                CanOfferUndoPreview: false,
                RequiresExplicitArchive: true,
                LocalMetadataUpdated: false);
        }

        // Case 4: Third version C!
        var cDetail = hasValidB
            ? $"当前文件（{live.DisplayShortSha256}）既不是恢复版本（{snap.DisplayRestoredShortSha}），也不是覆盖前原版本（{snap.DisplayPreRestoreShortSha}）。已完整保留你的最新文件与覆盖前原副本，未自动覆盖磁盘；你可以查看或另存原副本。"
            : $"当前文件（{live.DisplayShortSha256}）已不再等于恢复重建的备份版本（{snap.DisplayRestoredShortSha}）。已完整保留你的最新文件，未自动删除或覆盖。";
        return new ConfigRecoveryReport(
            Status: ConfigRecoveryStatus.ExternalVersionCDetected,
            OperationRecord: snap,
            LiveSnapshot: live,
            StatusBadge: "发现第三版（C）",
            Headline: $"撤销中断后目标已被修改：“{snap.DisplayTargetScopedName}”已保留最新内容",
            Detail: cDetail,
            DiskStateSummary: FormatDiskStateSummary(live, "已变为第三版（既非备份 A 也非原版 B）"),
            OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, copyErr),
            HasValidOriginalCopy: hasValidB,
            CanOfferUndoPreview: false,
            RequiresExplicitArchive: true,
            LocalMetadataUpdated: false);
    }

    private ConfigRecoveryReport? TryReconcileLegacyRecord(ConfigUndoSnapshot snap)
    {
        if (!IsLegacyAlreadyAtPreRestoreState(snap))
            return null;

        var live = CaptureLiveSnapshot(snap.TargetPath, out _);
        var blobErr = "";
        var hasValidB = snap.TargetExistedBeforeRestore && TryReadValidatedUndoBlob(snap, out _, out blobErr);
        var desc = snap.TargetExistedBeforeRestore
            ? $"检测到旧版（Beta.1/Beta.2）未关闭的撤销记录，但磁盘当前已是覆盖前原版本（{live.DisplayShortSha256}）。旧版记录不含操作阶段标记，无法区分上次是写盘前中断还是撤销后未记完；启动检查未修改磁盘，且已隐藏失效的撤销按钮。"
            : "检测到旧版（Beta.1/Beta.2）未关闭的撤销记录，且目标路径当前仍为缺失状态。因旧版记录不含阶段标记，启动检查未修改磁盘，请确认后归档该记录。";
        return new ConfigRecoveryReport(
            Status: ConfigRecoveryStatus.NeedsVerification,
            OperationRecord: snap,
            LiveSnapshot: live,
            StatusBadge: "待核对",
            Headline: $"待核对：旧版未关闭记录与磁盘现状需确认（{snap.DisplayTargetScopedName}）",
            Detail: desc,
            DiskStateSummary: snap.TargetExistedBeforeRestore
                ? FormatDiskStateSummary(live, "已与记录中的覆盖前原版本（B）一致")
                : "目标文件不存在（与记录中的覆盖前缺失状态一致）",
            OriginalCopySummary: FormatOriginalCopySummary(snap, hasValidB, snap.TargetExistedBeforeRestore ? blobErr : ""),
            HasValidOriginalCopy: hasValidB,
            CanOfferUndoPreview: false,
            RequiresExplicitArchive: true,
            LocalMetadataUpdated: false);
    }

    private bool IsLegacyAlreadyAtPreRestoreState(ConfigUndoSnapshot snap)
    {
        var live = CaptureLiveSnapshot(snap.TargetPath, out _);
        if (snap.TargetExistedBeforeRestore)
        {
            return live.Exists &&
                live.IsReadable &&
                !string.IsNullOrWhiteSpace(snap.PreRestoreSha256) &&
                string.Equals(live.Sha256, snap.PreRestoreSha256, StringComparison.OrdinalIgnoreCase);
        }
        return !live.Exists;
    }

    private static ConfigRecoveryReport BuildCorruptMetadataNeedsVerificationReport(string filePath, string reason)
    {
        var fileName = Path.GetFileName(filePath);
        var placeholder = new ConfigUndoSnapshot
        {
            UndoId = Path.GetFileNameWithoutExtension(fileName),
            TargetPath = filePath
        };
        return new ConfigRecoveryReport(
            Status: ConfigRecoveryStatus.NeedsVerification,
            OperationRecord: placeholder,
            LiveSnapshot: new ConfigLiveFileSnapshot(false, false, 0, "", 0, "", "", reason),
            StatusBadge: "待核对",
            Headline: $"待核对：本地操作记录损坏或证据不足（{fileName}）",
            Detail: $"{reason} 启动检查未修改任何用户配置文件，请人工核对目标文件与备份目录。",
            DiskStateSummary: "无法根据损坏的操作记录定位目标文件",
            OriginalCopySummary: "无法验证覆盖前副本（元数据损坏）",
            HasValidOriginalCopy: false,
            CanOfferUndoPreview: false,
            RequiresExplicitArchive: false,
            LocalMetadataUpdated: false);
    }

    private static string FormatDiskStateSummary(ConfigLiveFileSnapshot live, string verdictTag)
    {
        if (!live.Exists)
            return $"磁盘现状：文件不存在（{verdictTag}）";
        if (!live.IsReadable)
            return $"磁盘现状：文件存在但无法读取（{live.ReadError}）";
        return $"磁盘现状：{live.DisplaySize} · {live.DisplayFormatSummary} · SHA-256：{live.DisplayShortSha256}（{verdictTag}）";
    }

    private static string FormatOriginalCopySummary(ConfigUndoSnapshot snap, bool hasValidB, string blobErr)
    {
        if (!snap.TargetExistedBeforeRestore)
            return "覆盖前原副本：原文件在恢复前原本缺失（无原文件内容）";
        if (hasValidB)
            return $"覆盖前原副本：已完整保住并通过 SHA-256 校验（{ConfigFormatting.FormatByteSize(snap.PreRestoreByteLength)} · {snap.DisplayPreRestoreShortSha}）";
        return $"覆盖前原副本：校验未通过（{blobErr}）";
    }

    private ConfigComparisonResult? TryCompareSafely(ConfigRestorePointRecord point, ConfigDiffDirection direction)
    {
        try
        {
            return CompareRestorePoint(point, direction);
        }
        catch
        {
            return null;
        }
    }

    private static ConfigUndoPreviewResult BuildUndoErrorPreview(
        ConfigUndoSnapshot undoSnapshot,
        ConfigUndoPreviewStatus status,
        ConfigLiveFileSnapshot live,
        string headline,
        string detail,
        bool hasValidOriginalCopy = false) =>
        new(
            UndoSnapshot: undoSnapshot,
            Status: status,
            LiveSnapshot: live,
            DirectionLabel: "当前文件 → 撤销后（不可执行）",
            Headline: headline,
            Detail: detail,
            ActionSummary: hasValidOriginalCopy
                ? "未修改目标文件，也不会消耗撤销记录；你仍可查看或另存覆盖前原副本。"
                : "未修改目标文件，也不会消耗撤销记录。",
            PreRestoreEncodingLabel: "不适用",
            PreRestoreLineEndingLabel: "不适用",
            PreRestoreByteLength: undoSnapshot.PreRestoreByteLength,
            PreRestoreSha256: undoSnapshot.PreRestoreSha256,
            IsTextDiffAvailable: false,
            HasFormatOnlyDifference: false,
            DiffFallbackNote: detail,
            RemovedLineCount: 0,
            AddedLineCount: 0,
            IsDiffTruncated: false,
            DiffLines: [],
            HasValidOriginalCopy: hasValidOriginalCopy);

    private bool TryPersistUndoMetadata(ConfigUndoSnapshot undoSnapshot)
    {
        var metaPath = Path.Combine(undoDir, undoSnapshot.UndoId + ".json");
        return TryPersistUndoMetadataToPath(metaPath, undoSnapshot);
    }

    private static bool TryPersistUndoMetadataToPath(string metaPath, ConfigUndoSnapshot undoSnapshot)
    {
        try
        {
            WriteJsonAtomically(metaPath, undoSnapshot);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private ConfigUndoSnapshot? TryLoadUndoRecordById(string undoId)
    {
        if (string.IsNullOrWhiteSpace(undoId)) return null;
        var metaPath = Path.Combine(undoDir, undoId + ".json");
        if (!File.Exists(metaPath)) return null;
        try
        {
            var json = File.ReadAllText(metaPath, Utf8NoBom);
            return JsonSerializer.Deserialize<ConfigUndoSnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private bool HasNewerActiveRestoreForTarget(string normalizedTargetPath, ConfigUndoSnapshot currentRecord)
    {
        if (!Directory.Exists(undoDir)) return false;
        foreach (var file in Directory.EnumerateFiles(undoDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var snap = JsonSerializer.Deserialize<ConfigUndoSnapshot>(File.ReadAllText(file, Utf8NoBom), JsonOptions);
                if (snap is null || snap.Consumed || string.Equals(snap.UndoId, currentRecord.UndoId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(snap.TargetPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (snap.CreatedAtUtc > currentRecord.CreatedAtUtc)
                    return true;
            }
            catch
            {
                // Ignore unreadable sibling metadata here.
            }
        }
        return false;
    }

    private string GetTargetLockRelativePath(string normalizedTargetPath)
    {
        var key = ComputeSha256Hex(Utf8NoBom.GetBytes(normalizedTargetPath.ToUpperInvariant()))[..16];
        return Path.Combine("Locks", $"target-{key}.lock");
    }

    private bool TryAcquireTargetLock(
        string normalizedTargetPath,
        string operationKind,
        string undoId,
        out TargetOperationLock? acquired,
        out ConfigTargetLockInfo? busyLockInfo)
    {
        acquired = null;
        busyLockInfo = null;
        Directory.CreateDirectory(locksDir);
        var relPath = GetTargetLockRelativePath(normalizedTargetPath);
        var fullPath = Path.Combine(StorageRoot, relPath);

        FileStream? stream = null;
        try
        {
            // FileShare.Read guarantees kernel-level single-writer exclusivity across independent processes AND instances
            // while allowing readers to inspect owner PID / operation kind, and preventing File.Delete while held!
            stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var info = new ConfigTargetLockInfo
            {
                SchemaVersion = 1,
                OwnerPid = Environment.ProcessId,
                OwnerProcessStartUtcTicks = CurrentProcessStartUtcTicks,
                InstanceId = instanceId,
                OperationKind = operationKind,
                UndoId = undoId,
                TargetPath = normalizedTargetPath,
                StartedAtUtc = DateTimeOffset.UtcNow
            };
            acquired = new TargetOperationLock(stream, fullPath, relPath, info);
            acquired.PersistPayload();
            return true;
        }
        catch (IOException)
        {
            stream?.Dispose();
            busyLockInfo = TryReadLockInfoShared(fullPath) ?? new ConfigTargetLockInfo
            {
                OperationKind = operationKind,
                TargetPath = normalizedTargetPath
            };
            return false;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private ConfigTargetLockInfo? ProbeLiveTargetLock(string targetPath)
    {
        if (!TryNormalizeRegularFilePath(targetPath, mustExist: false, out var normalized, out _))
            return null;
        var fullPath = Path.Combine(StorageRoot, GetTargetLockRelativePath(normalized));
        if (!File.Exists(fullPath))
            return null;

        try
        {
            using var probe = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            // Opening with ReadWrite/Share.Read succeeded -> NO live writer holds this lock (previous owner exited or was killed!).
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            // Sharing violation -> a live writer process/instance DEFINITELY holds this lock right now!
            return TryReadLockInfoShared(fullPath) ?? new ConfigTargetLockInfo
            {
                TargetPath = normalized
            };
        }
        catch (UnauthorizedAccessException)
        {
            return TryReadLockInfoShared(fullPath) ?? new ConfigTargetLockInfo
            {
                TargetPath = normalized
            };
        }

        // Stale lock file from a terminated process -> best-effort cleanup (harmless if re-acquired meanwhile).
        TryDeleteFile(fullPath);
        return null;
    }

    private bool IsOperationOwnedByAnotherLiveWindow(ConfigUndoSnapshot snap, out ConfigTargetLockInfo? liveLock)
    {
        liveLock = null;
        var probed = ProbeLiveTargetLock(snap.TargetPath);
        if (probed is not null && !string.Equals(probed.InstanceId, instanceId, StringComparison.Ordinal))
        {
            // OS kernel lock (FileAccess.ReadWrite, FileShare.Read) is the authoritative live-writer signal:
            // it cannot be deleted while held and is immediately released on normal completion, exception, or Process.Kill().
            liveLock = probed.OwnerPid > 0 || snap.OwnerPid <= 0
                ? probed
                : probed with
                {
                    OwnerPid = snap.OwnerPid,
                    OwnerProcessStartUtcTicks = snap.OwnerProcessStartUtcTicks
                };
            return true;
        }

        return false;
    }

    private ConfigRecoveryReport BuildActiveInAnotherWindowReport(ConfigUndoSnapshot snap, ConfigTargetLockInfo? liveLock, string defaultOpLabel)
    {
        var live = CaptureLiveSnapshot(snap.TargetPath, out _);
        var opLabel = liveLock?.OperationKind switch
        {
            "undo" => "撤销",
            "restore" => "恢复",
            _ => defaultOpLabel
        };
        var pidText = liveLock is { OwnerPid: > 0 } ? $"（PID {liveLock.OwnerPid}）" : "";
        return new ConfigRecoveryReport(
            Status: ConfigRecoveryStatus.ActiveInAnotherWindow,
            OperationRecord: snap,
            LiveSnapshot: live,
            StatusBadge: "其他窗口正在操作",
            Headline: $"另一个 Contain 窗口{pidText}正在对“{snap.DisplayTargetScopedName}”执行{opLabel}",
            Detail: $"检测到对方进程仍在活跃运行，已暂缓对该文件的中断核对与写入，不会抢先将记录标为中断、完成或已消耗。你可以继续浏览记录，待对方完成后点击「重新核对」或「刷新」。",
            DiskStateSummary: FormatDiskStateSummary(live, "对方窗口正在写入，当前仅只读观察"),
            OriginalCopySummary: snap.TargetExistedBeforeRestore
                ? $"覆盖前原副本：由对方窗口维护中（{ConfigFormatting.FormatByteSize(snap.PreRestoreByteLength)} · {snap.DisplayPreRestoreShortSha}）"
                : "覆盖前原副本：原文件恢复前原本缺失",
            HasValidOriginalCopy: false,
            CanOfferUndoPreview: false,
            RequiresExplicitArchive: false,
            LocalMetadataUpdated: false);
    }

    private static ConfigTargetLockInfo? TryReadLockInfoShared(string lockPath)
    {
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Utf8NoBom);
            var text = sr.ReadToEnd();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<ConfigTargetLockInfo>(text, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static long TryGetCurrentProcessStartUtcTicks()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return proc.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private sealed class TargetOperationLock : IDisposable
    {
        private readonly FileStream stream;
        private readonly string fullPath;
        private ConfigTargetLockInfo payload;
        private bool disposed;

        public TargetOperationLock(FileStream stream, string fullPath, string relativePath, ConfigTargetLockInfo payload)
        {
            this.stream = stream;
            this.fullPath = fullPath;
            RelativePath = relativePath;
            this.payload = payload;
        }

        public string RelativePath { get; }

        public void UpdateUndoId(string undoId)
        {
            payload = payload with { UndoId = undoId };
            PersistPayload();
        }

        public void PersistPayload()
        {
            var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(payload, JsonOptions) + "\n");
            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.SetLength(bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { stream.Dispose(); } catch { }
            // Safe on Windows: if another process already acquired the file with FileShare.Read (no FileShare.Delete),
            // File.Delete fails harmlessly and is ignored without affecting the new owner's handle!
            TryDeleteFile(fullPath);
        }
    }

    private bool TryReadValidatedUndoBlob(ConfigUndoSnapshot undoSnapshot, out byte[] bytes, out string error)
    {
        bytes = [];
        error = "";
        if (string.IsNullOrWhiteSpace(undoSnapshot.PreRestoreBlobRelativePath))
        {
            error = "撤销记录缺少覆盖前副本路径。";
            return false;
        }

        var blobPath = ResolveSafeChildPath(StorageRoot, undoSnapshot.PreRestoreBlobRelativePath);
        if (blobPath is null)
        {
            error = "撤销副本路径越界，已拒绝读取。";
            return false;
        }
        if (!File.Exists(blobPath))
        {
            error = "本地覆盖前副本文件已丢失。";
            return false;
        }

        try
        {
            var raw = ReadAllBytesShared(blobPath);
            var sha = ComputeSha256Hex(raw);
            if (raw.LongLength != undoSnapshot.PreRestoreByteLength ||
                !string.Equals(sha, undoSnapshot.PreRestoreSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = $"覆盖前副本校验失败（记录 {undoSnapshot.PreRestoreByteLength} B / {undoSnapshot.DisplayPreRestoreShortSha}，实际 {raw.LongLength} B / {(sha.Length >= 12 ? sha[..12] + "…" : sha)}）。";
                return false;
            }
            bytes = raw;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"无法读取覆盖前副本：{ex.Message}";
            return false;
        }
    }

    private bool TryReadValidatedBackupBlob(ConfigRestorePointRecord point, out byte[] bytes, out string error)
    {
        bytes = [];
        error = "";

        if (string.IsNullOrWhiteSpace(point.BackupBlobRelativePath) || string.IsNullOrWhiteSpace(point.Sha256))
        {
            error = "还原点元数据缺少备份文件位置或校验摘要。";
            return false;
        }

        var blobPath = ResolveSafeChildPath(StorageRoot, point.BackupBlobRelativePath);
        if (blobPath is null)
        {
            error = "还原点备份路径越界，已拒绝读取。";
            return false;
        }

        if (!File.Exists(blobPath))
        {
            error = "本地备份文件已丢失。";
            return false;
        }

        try
        {
            var raw = ReadAllBytesShared(blobPath);
            var actualSha = ComputeSha256Hex(raw);
            if (raw.LongLength != point.ByteLength ||
                !string.Equals(actualSha, point.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = $"本地备份文件校验失败（记录 {point.ByteLength} B / {point.DisplayShortSha256}，实际 {raw.LongLength} B / {(actualSha.Length >= 12 ? actualSha[..12] + "…" : actualSha)}）。";
                return false;
            }

            bytes = raw;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"无法读取本地备份文件：{ex.Message}";
            return false;
        }
    }

    private static ConfigLiveFileSnapshot CaptureLiveSnapshot(string targetPath, out byte[]? rawBytes)
    {
        rawBytes = null;

        if (Directory.Exists(targetPath))
            return new ConfigLiveFileSnapshot(true, false, 0, "", 0, "", "", "目标路径当前是一个文件夹，而非普通文件。");

        if (!File.Exists(targetPath))
            return new ConfigLiveFileSnapshot(false, false, 0, "", 0, "", "");

        try
        {
            var attrs = File.GetAttributes(targetPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                return new ConfigLiveFileSnapshot(true, false, 0, "", 0, "", "", "目标路径是符号链接或重解析点；第一版仅操作普通本地文件。");

            var info = new FileInfo(targetPath);
            if (info.Length > MaxConfigFileBytes)
            {
                return new ConfigLiveFileSnapshot(true, false, info.Length, "", info.LastWriteTimeUtc.Ticks, "", "",
                    $"当前文件超过单配置文件保护上限（{ConfigFormatting.FormatByteSize(MaxConfigFileBytes)}）。");
            }

            var bytes = ReadAllBytesShared(targetPath);
            var sha = ComputeSha256Hex(bytes);
            var text = ConfigTextDecoder.Inspect(bytes);
            rawBytes = bytes;

            return new ConfigLiveFileSnapshot(
                Exists: true,
                IsReadable: true,
                ByteLength: bytes.LongLength,
                Sha256: sha,
                LastWriteTimeUtcTicks: info.LastWriteTimeUtc.Ticks,
                EncodingLabel: text.EncodingLabel,
                LineEndingLabel: text.LineEndingLabel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigLiveFileSnapshot(true, false, 0, "", 0, "", "", $"读取目标文件失败：{ex.Message}");
        }
    }

    private static void SafeWriteRawBytesToTarget(string targetPath, byte[] rawBytes, string expectedSha256, bool targetCurrentlyExists)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(dir))
            throw new IOException("无法解析目标文件所在目录。");

        if (!Directory.Exists(dir))
        {
            if (targetCurrentlyExists)
                throw new DirectoryNotFoundException($"目标目录不存在：{dir}");
            Directory.CreateDirectory(dir);
        }

        var fileName = Path.GetFileName(targetPath);
        var tempPath = Path.Combine(dir, $".{fileName}.contain-restore-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                fs.Write(rawBytes, 0, rawBytes.Length);
                fs.Flush(flushToDisk: true);
            }

            var tempSha = ComputeFileSha256Hex(tempPath);
            if (!string.Equals(tempSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("临时还原文件哈希核对失败，已中止覆盖。");

            if (targetCurrentlyExists)
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                if (File.Exists(targetPath))
                    throw new IOException("目标路径在写入瞬间已出现新文件，已中止覆盖。");
                File.Move(tempPath, targetPath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static bool TryNormalizeRegularFilePath(string? rawPath, bool mustExist, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";

        var trimmed = (rawPath ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "请先选择一个本地配置文件。";
            return false;
        }

        if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal) || trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            error = "不支持设备路径或原始命名空间路径；请选择普通本地配置文件。";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"路径格式无效：{ex.Message}";
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            error = "所选路径是文件夹而非文件；第一版仅支持单个本地配置文件。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
        {
            error = "路径缺少具体文件名；请指定单个本地配置文件路径。";
            return false;
        }

        if (File.Exists(fullPath))
        {
            try
            {
                var attrs = File.GetAttributes(fullPath);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    error = "所选文件是符号链接或重解析点；第一版仅操作普通本地文件。";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"无法检查目标文件属性：{ex.Message}";
                return false;
            }
            return true;
        }

        if (mustExist)
        {
            error = "未找到指定的本地配置文件，请检查路径是否正确。";
            return false;
        }

        return true;
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream(fs.CanSeek && fs.Length <= int.MaxValue ? (int)fs.Length : 0);
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteBytesAtomically(string destinationPath, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void WriteJsonAtomically<T>(string destinationPath, T payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions) + "\n";
        WriteBytesAtomically(destinationPath, Utf8NoBom.GetBytes(json));
    }

    private static string? ResolveSafeChildPath(string rootDir, string relativePath)
    {
        try
        {
            var fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            return candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetParentFolderName(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(dir)) return "";
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? dir : leaf;
    }

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ComputeFileSha256Hex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore best-effort temp cleanup failure.
        }
    }

    private static string DescribeWriteFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException => $"写入被系统拒绝（目标文件可能为只读或当前账户无写入权限）：{ex.Message}",
        IOException => $"目标文件无法写入（可能正被其他程序占用或发生 I/O 错误）：{ex.Message}",
        _ => $"写入时发生异常（{ex.GetType().Name}）：{ex.Message}"
    };
}
