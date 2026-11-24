using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Contain.Desktop.Model;

public interface IConfigRestorePointService
{
    string StorageRoot { get; }
    ConfigFilePreview InspectCandidateFile(string rawPath);
    ConfigRestorePointRecord CreateRestorePoint(string rawPath, string? note);
    IReadOnlyList<ConfigRestorePointRecord> LoadAllPoints();
    ConfigComparisonResult CompareRestorePoint(ConfigRestorePointRecord point);
    ConfigRestoreResult RestoreFromPoint(ConfigRestorePointRecord point, ConfigLiveFileSnapshot expectedLiveSnapshot);
    ConfigUndoSnapshot? LoadLatestActiveUndo();
    ConfigUndoResult UndoRestore(ConfigUndoSnapshot undoSnapshot);
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
                UnrecognizedReason: "文件超过 2 MB，跳过逐行文本对比，仅展示大小与 SHA-256；恢复仍将按原始字节完整执行。");
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
            {
                return Unrecognized("检测到 NUL 字节，非纯文本配置文件。");
            }
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

        // 4. UTF-8 without BOM (strict validation + no NUL bytes + no binary control bytes)
        if (ContainsNullByte(rawBytes, 0))
        {
            return Unrecognized("检测到 NUL 字节，可能是二进制或未带 BOM 的多字节编码。");
        }

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
            // Keep final segment (including trailing empty line if text ended with newline so trailing newline changes show up).
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

    public static DiffOutput Compute(IReadOnlyList<string> backupLines, IReadOnlyList<string> currentLines)
    {
        ArgumentNullException.ThrowIfNull(backupLines);
        ArgumentNullException.ThrowIfNull(currentLines);

        // Trim identical prefix and suffix in O(N)
        var prefix = 0;
        var minLen = Math.Min(backupLines.Count, currentLines.Count);
        while (prefix < minLen && string.Equals(backupLines[prefix], currentLines[prefix], StringComparison.Ordinal))
            prefix++;

        var suffix = 0;
        while (suffix < (minLen - prefix) &&
               string.Equals(backupLines[backupLines.Count - 1 - suffix], currentLines[currentLines.Count - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        var midOldCount = backupLines.Count - prefix - suffix;
        var midNewCount = currentLines.Count - prefix - suffix;

        if (midOldCount == 0 && midNewCount == 0)
            return new DiffOutput([], 0, 0, false);

        var rawOps = new List<ConfigDiffLine>(backupLines.Count + currentLines.Count);

        // Prefix context
        for (var i = 0; i < prefix; i++)
            rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Context, i + 1, i + 1, backupLines[i]));

        var removed = 0;
        var added = 0;

        if ((long)midOldCount * midNewCount <= MaxLcsCells && midOldCount > 0 && midNewCount > 0)
        {
            var midOps = ComputeLcsMiddle(backupLines, currentLines, prefix, midOldCount, midNewCount);
            foreach (var op in midOps)
            {
                if (op.Kind == ConfigDiffLineKind.Removed) removed++;
                else if (op.Kind == ConfigDiffLineKind.Added) added++;
                rawOps.Add(op);
            }
        }
        else
        {
            // Fallback for huge middle diffs: emit removed block then added block cleanly.
            for (var i = 0; i < midOldCount; i++)
            {
                removed++;
                rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Removed, prefix + i + 1, null, backupLines[prefix + i]));
            }
            for (var j = 0; j < midNewCount; j++)
            {
                added++;
                rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Added, null, prefix + j + 1, currentLines[prefix + j]));
            }
        }

        // Suffix context
        for (var i = 0; i < suffix; i++)
        {
            var oldIdx = backupLines.Count - suffix + i;
            var newIdx = currentLines.Count - suffix + i;
            rawOps.Add(new ConfigDiffLine(ConfigDiffLineKind.Context, oldIdx + 1, newIdx + 1, backupLines[oldIdx]));
        }

        // Collapse unchanged context runs outside ContextLines window around changes
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
        IReadOnlyList<string> backupLines,
        IReadOnlyList<string> currentLines,
        int offset,
        int m,
        int n)
    {
        var dp = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
        {
            var oldLine = backupLines[offset + i];
            for (var j = n - 1; j >= 0; j--)
            {
                if (string.Equals(oldLine, currentLines[offset + j], StringComparison.Ordinal))
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
            if (string.Equals(backupLines[offset + a], currentLines[offset + b], StringComparison.Ordinal))
            {
                result.Add(new ConfigDiffLine(ConfigDiffLineKind.Context, offset + a + 1, offset + b + 1, backupLines[offset + a]));
                a++;
                b++;
            }
            else if (dp[a + 1, b] >= dp[a, b + 1])
            {
                result.Add(new ConfigDiffLine(ConfigDiffLineKind.Removed, offset + a + 1, null, backupLines[offset + a]));
                a++;
            }
            else
            {
                result.Add(new ConfigDiffLine(ConfigDiffLineKind.Added, null, offset + b + 1, currentLines[offset + b]));
                b++;
            }
        }

        while (a < m)
        {
            result.Add(new ConfigDiffLine(ConfigDiffLineKind.Removed, offset + a + 1, null, backupLines[offset + a]));
            a++;
        }

        while (b < n)
        {
            result.Add(new ConfigDiffLine(ConfigDiffLineKind.Added, null, offset + b + 1, currentLines[offset + b]));
            b++;
        }

        return result;
    }
}

public sealed class LocalConfigRestorePointService : IConfigRestorePointService
{
    public const long MaxConfigFileBytes = 64L * 1024 * 1024; // 64 MiB safety cap for single config file

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string pointsDir;
    private readonly string blobsDir;
    private readonly string undoDir;

    public LocalConfigRestorePointService(string? storageRootDirectory = null)
    {
        StorageRoot = string.IsNullOrWhiteSpace(storageRootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Contain", "ConfigRestorePoints")
            : Path.GetFullPath(storageRootDirectory);
        pointsDir = Path.Combine(StorageRoot, "Points");
        blobsDir = Path.Combine(StorageRoot, "Blobs");
        undoDir = Path.Combine(StorageRoot, "Undo");
    }

    public string StorageRoot { get; }

    public ConfigFilePreview InspectCandidateFile(string rawPath)
    {
        if (!TryNormalizeRegularFilePath(rawPath, mustExist: true, out var fullPath, out var error))
        {
            return new ConfigFilePreview(false, rawPath?.Trim() ?? "", "", "", "", 0, "", "", "", error);
        }

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

        // Verify stored blob matches exact bytes before publishing metadata record
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
                // Ignore unreadable or half-written stray files cleanly without crashing the list.
            }
        }

        results.Sort((a, b) =>
        {
            var cmp = b.CreatedAtUtc.CompareTo(a.CreatedAtUtc);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(b.PointId, a.PointId);
        });
        return results;
    }

    public ConfigComparisonResult CompareRestorePoint(ConfigRestorePointRecord point)
    {
        ArgumentNullException.ThrowIfNull(point);

        // 1. Validate backup blob integrity first
        if (!TryReadValidatedBackupBlob(point, out var backupBytes, out var integrityError))
        {
            var liveOnCorrupt = CaptureLiveSnapshot(point.TargetPath, out _);
            return new ConfigComparisonResult(
                Point: point,
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

        // 2. Inspect current live target file
        var liveSnapshot = CaptureLiveSnapshot(point.TargetPath, out var liveBytes);

        if (!liveSnapshot.Exists)
        {
            return new ConfigComparisonResult(
                Point: point,
                Status: ConfigComparisonStatus.Missing,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "目标文件当前已缺失",
                StatusDetail: "原位置已找不到该配置文件。你可以点击「恢复此还原点」，按备份原始字节重新创建该文件。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"当前目标路径无文件；备份共 {point.DisplaySize}，SHA-256 为 {point.Sha256}。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        if (!liveSnapshot.IsReadable || liveBytes is null)
        {
            return new ConfigComparisonResult(
                Point: point,
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

        // 3. Exact byte comparison
        if (liveSnapshot.ByteLength == point.ByteLength &&
            string.Equals(liveSnapshot.Sha256, point.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigComparisonResult(
                Point: point,
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

        // 4. Changed: attempt reliable text diff (raw bytes remain authoritative for restore!)
        var backupText = ConfigTextDecoder.Inspect(backupBytes);
        var liveText = ConfigTextDecoder.Inspect(liveBytes);

        if (!backupText.IsRecognizedText || !liveText.IsRecognizedText)
        {
            var reason = !backupText.IsRecognizedText
                ? backupText.UnrecognizedReason
                : liveText.UnrecognizedReason;
            return new ConfigComparisonResult(
                Point: point,
                Status: ConfigComparisonStatus.Changed,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "当前文件已变化",
                StatusDetail: $"文件字节已与备份不同（备份 {point.DisplaySize} → 当前 {liveSnapshot.DisplaySize}）。编码无法可靠识别，已展示大小与 SHA-256；恢复仍将按原始字节执行。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: false,
                DiffFallbackNote: $"{reason} 恢复时不会重新编码，始终写入备份的原始字节。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        var diff = ConfigLineDiffer.Compute(backupText.Lines, liveText.Lines);
        if (diff.RemovedCount == 0 && diff.AddedCount == 0)
        {
            return new ConfigComparisonResult(
                Point: point,
                Status: ConfigComparisonStatus.Changed,
                BackupIntegrityValid: true,
                BackupIntegrityError: "",
                LiveSnapshot: liveSnapshot,
                StatusHeadline: "当前文件已变化（仅编码 BOM 或换行格式不同）",
                StatusDetail: $"文本行内容相同，但原始字节有变化（备份：{backupText.EncodingLabel} · {backupText.LineEndingLabel}；当前：{liveText.EncodingLabel} · {liveText.LineEndingLabel}）。恢复将完整还原原始字节。",
                IsTextDiffAvailable: false,
                HasFormatOnlyDifference: true,
                DiffFallbackNote: "文本行无增删，差异来自 UTF-8/UTF-16 BOM 或 CRLF/LF 换行格式；恢复将严格按原始字节保留 BOM 与换行符。",
                RemovedLineCount: 0,
                AddedLineCount: 0,
                IsDiffTruncated: false,
                DiffLines: []);
        }

        return new ConfigComparisonResult(
            Point: point,
            Status: ConfigComparisonStatus.Changed,
            BackupIntegrityValid: true,
            BackupIntegrityError: "",
            LiveSnapshot: liveSnapshot,
            StatusHeadline: "当前文件已变化",
            StatusDetail: $"与备份相比共 {diff.RemovedCount} 行原内容变更/移除、{diff.AddedCount} 行现内容变更/新增（备份 {point.DisplaySize} → 当前 {liveSnapshot.DisplaySize}）。恢复将按原始字节还原。",
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

        // 1. Validate backup integrity before touching anything
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
                RefreshedComparison: CompareRestorePoint(point));
        }

        // 2. Reject directory or reparse point at target path
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
                RefreshedComparison: CompareRestorePoint(point));
        }

        // 3. Re-read live file right before overwrite to detect mid-confirmation external edits!
        var freshLive = CaptureLiveSnapshot(targetPath, out var freshLiveBytes);
        if (!freshLive.MatchesContentOf(expectedLiveSnapshot))
        {
            var refreshed = CompareRestorePoint(point);
            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit,
                Headline: "目标文件在确认期间已被外部修改",
                Detail: "为避免误覆盖刚刚发生的改动，本次尚未写入文件。已刷新最新差异，请核对后再次点击「确认恢复」。",
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
                RefreshedComparison: CompareRestorePoint(point));
        }

        // 4. Persist durable undo snapshot BEFORE modifying the target file
        ConfigUndoSnapshot undo;
        string? undoBlobPath = null;
        string? undoMetaPath = null;
        try
        {
            Directory.CreateDirectory(undoDir);
            var undoId = Guid.NewGuid().ToString("N")[..12];
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

            undo = new ConfigUndoSnapshot
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
                RestoredBackupSha256 = point.Sha256,
                Consumed = false
            };

            undoMetaPath = Path.Combine(undoDir, undoId + ".json");
            WriteJsonAtomically(undoMetaPath, undo);
        }
        catch (Exception ex)
        {
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

        // 5. Safe atomic-replace write of raw backup bytes to targetPath
        try
        {
            SafeWriteRawBytesToTarget(targetPath, backupBytes, point.Sha256, targetCurrentlyExists: freshLive.Exists);
            var postSha = ComputeFileSha256Hex(targetPath);
            if (!string.Equals(postSha, point.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"写入完成但目标文件 SHA-256 核对不一致（期望 {point.Sha256}，实际 {postSha}）。");

            var detail = freshLive.Exists
                ? $"已将备份原始字节写入目标文件并校验 SHA-256 一致（{point.Sha256[..12]}…），保留了原始 BOM 与换行格式。覆盖前版本已保存，可随时点击「撤销本次恢复」。"
                : $"已在原路径按备份原始字节重新创建文件并校验 SHA-256 一致（{point.Sha256[..12]}…）。可随时点击「撤销本次恢复」移除本次重建的文件。";

            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.Succeeded,
                Headline: "已恢复到所选还原点",
                Detail: detail,
                TargetPath: targetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: postSha,
                ExistingFilePreserved: true,
                UndoSnapshot: undo,
                RefreshedComparison: CompareRestorePoint(point));
        }
        catch (Exception ex)
        {
            // Clean up unused undo snapshot because overwrite did not succeed
            if (undoBlobPath is not null) TryDeleteFile(undoBlobPath);
            if (undoMetaPath is not null) TryDeleteFile(undoMetaPath);

            var afterFailLive = CaptureLiveSnapshot(targetPath, out _);
            var intact = afterFailLive.MatchesContentOf(freshLive);
            var reason = DescribeWriteFailure(ex);
            var intactNote = intact
                ? "已核对现有文件未被修改，内容完整保留。"
                : "请检查目标文件状态并重新刷新对比。";

            return new ConfigRestoreResult(
                Outcome: ConfigRestoreOutcomeKind.Failed,
                Headline: "恢复失败：目标文件未能写入",
                Detail: $"{reason} {intactNote}",
                TargetPath: targetPath,
                ExpectedSha256: point.Sha256,
                ActualSha256: afterFailLive.Sha256,
                ExistingFilePreserved: intact,
                UndoSnapshot: null,
                RefreshedComparison: CompareRestorePoint(point));
        }
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
                if (latest is null || snap.CreatedAtUtc > latest.CreatedAtUtc)
                    latest = snap;
            }
            catch
            {
                // Ignore corrupt undo metadata cleanly.
            }
        }
        return latest;
    }

    public ConfigUndoResult UndoRestore(ConfigUndoSnapshot undoSnapshot)
    {
        ArgumentNullException.ThrowIfNull(undoSnapshot);

        if (undoSnapshot.Consumed)
        {
            return new ConfigUndoResult(
                Outcome: ConfigUndoOutcomeKind.Failed,
                Headline: "本次恢复已撤销",
                Detail: "该撤销记录已被使用，无需重复撤销。",
                TargetPath: undoSnapshot.TargetPath,
                ExpectedSha256: undoSnapshot.PreRestoreSha256,
                ActualSha256: "",
                ExistingFilePreserved: true);
        }

        if (!TryNormalizeRegularFilePath(undoSnapshot.TargetPath, mustExist: false, out var targetPath, out var pathError))
        {
            return new ConfigUndoResult(
                Outcome: ConfigUndoOutcomeKind.Failed,
                Headline: "撤销失败：目标路径不是受支持的普通文件",
                Detail: pathError,
                TargetPath: undoSnapshot.TargetPath,
                ExpectedSha256: undoSnapshot.PreRestoreSha256,
                ActualSha256: "",
                ExistingFilePreserved: true);
        }

        // Verify undo blob integrity first if target existed before restore
        byte[]? preRestoreBytes = null;
        if (undoSnapshot.TargetExistedBeforeRestore)
        {
            if (string.IsNullOrWhiteSpace(undoSnapshot.PreRestoreBlobRelativePath))
            {
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Failed,
                    Headline: "撤销失败：缺少覆盖前备份副本",
                    Detail: "找不到覆盖前保存的原始副本，目标文件保持不变。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: "",
                    ExistingFilePreserved: true);
            }

            var blobPath = ResolveSafeChildPath(StorageRoot, undoSnapshot.PreRestoreBlobRelativePath);
            if (blobPath is null || !File.Exists(blobPath))
            {
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Failed,
                    Headline: "撤销失败：覆盖前副本文件已丢失",
                    Detail: "本地撤销副本已不存在，目标文件保持不变。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: "",
                    ExistingFilePreserved: true);
            }

            preRestoreBytes = ReadAllBytesShared(blobPath);
            var blobSha = ComputeSha256Hex(preRestoreBytes);
            if (preRestoreBytes.LongLength != undoSnapshot.PreRestoreByteLength ||
                !string.Equals(blobSha, undoSnapshot.PreRestoreSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Failed,
                    Headline: "撤销失败：覆盖前副本已损坏",
                    Detail: "本地撤销副本的哈希与记录不符，已阻止写入以保住现有文件。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: "",
                    ExistingFilePreserved: true);
            }
        }

        // Check whether the file was modified externally AFTER our restore
        var live = CaptureLiveSnapshot(targetPath, out _);
        if (live.Exists && (!live.IsReadable || !string.Equals(live.Sha256, undoSnapshot.RestoredBackupSha256, StringComparison.OrdinalIgnoreCase)))
        {
            return new ConfigUndoResult(
                Outcome: ConfigUndoOutcomeKind.ExternalEditConflict,
                Headline: "撤销已中止：恢复后目标文件又发生了变化",
                Detail: "目标文件当前内容已不再等于刚才恢复的备份版本；为防止丢失你的新修改，已中止撤销且未修改现有文件。",
                TargetPath: targetPath,
                ExpectedSha256: undoSnapshot.PreRestoreSha256,
                ActualSha256: live.Sha256,
                ExistingFilePreserved: true);
        }

        try
        {
            if (undoSnapshot.TargetExistedBeforeRestore && preRestoreBytes is not null)
            {
                SafeWriteRawBytesToTarget(targetPath, preRestoreBytes, undoSnapshot.PreRestoreSha256, targetCurrentlyExists: live.Exists);
                var postSha = ComputeFileSha256Hex(targetPath);
                if (!string.Equals(postSha, undoSnapshot.PreRestoreSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"撤销后校验不一致（期望 {undoSnapshot.PreRestoreSha256}，实际 {postSha}）。");

                MarkUndoConsumed(undoSnapshot);
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Succeeded,
                    Headline: "已撤销本次恢复",
                    Detail: $"已将目标文件还原为覆盖前的原始字节（SHA-256：{postSha[..12]}…），BOM 与换行格式均已完整保留。",
                    TargetPath: targetPath,
                    ExpectedSha256: undoSnapshot.PreRestoreSha256,
                    ActualSha256: postSha,
                    ExistingFilePreserved: true);
            }
            else
            {
                if (live.Exists)
                    File.Delete(targetPath);
                MarkUndoConsumed(undoSnapshot);
                return new ConfigUndoResult(
                    Outcome: ConfigUndoOutcomeKind.Succeeded,
                    Headline: "已撤销本次恢复",
                    Detail: "恢复前目标文件原本不存在，已移除本次重建的文件。",
                    TargetPath: targetPath,
                    ExpectedSha256: "",
                    ActualSha256: "",
                    ExistingFilePreserved: true);
            }
        }
        catch (Exception ex)
        {
            var afterFail = CaptureLiveSnapshot(targetPath, out _);
            var intact = afterFail.MatchesContentOf(live);
            return new ConfigUndoResult(
                Outcome: ConfigUndoOutcomeKind.Failed,
                Headline: "撤销失败：无法写回覆盖前版本",
                Detail: $"{DescribeWriteFailure(ex)} {(intact ? "当前目标文件未被破坏。" : "请刷新检查当前文件状态。")}",
                TargetPath: targetPath,
                ExpectedSha256: undoSnapshot.PreRestoreSha256,
                ActualSha256: afterFail.Sha256,
                ExistingFilePreserved: intact);
        }
    }

    private void MarkUndoConsumed(ConfigUndoSnapshot undoSnapshot)
    {
        try
        {
            var metaPath = Path.Combine(undoDir, undoSnapshot.UndoId + ".json");
            WriteJsonAtomically(metaPath, undoSnapshot with { Consumed = true });
        }
        catch
        {
            // Non-fatal metadata update.
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
        {
            return new ConfigLiveFileSnapshot(true, false, 0, "", 0, "", "", "目标路径当前是一个文件夹，而非普通文件。");
        }

        if (!File.Exists(targetPath))
        {
            return new ConfigLiveFileSnapshot(false, false, 0, "", 0, "", "");
        }

        try
        {
            var attrs = File.GetAttributes(targetPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return new ConfigLiveFileSnapshot(true, false, 0, "", 0, "", "", "目标路径是符号链接或重解析点；第一版仅操作普通本地文件。");
            }

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
            // Write raw bytes directly with WriteThrough so temp file is flushed before replace
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
                // File.Replace uses Win32 ReplaceFileW: fails cleanly without truncating or corrupting targetPath if locked/read-only/permission-denied!
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                // Recreate missing file without overwriting if one suddenly appeared
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
