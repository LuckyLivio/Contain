using System.Security.Cryptography;
using System.Text;
using Contain.Desktop.Model;

namespace Contain.Desktop.Model.Tests;

internal static class ConfigRestorePointChecks
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();
        Run(passed, "config_utf8_bom_crlf_chinese_roundtrip_and_restart_persistence", Utf8BomCrlfChineseRoundtrip);
        Run(passed, "config_disambiguates_same_filename_in_different_directories", SameFilenameDifferentDirectories);
        Run(passed, "config_missing_file_restore_recreates_and_undo_removes", MissingFileRestoreAndUndo);
        Run(passed, "config_unrecognized_encoding_shows_size_hash_and_restores_raw_bytes", UnrecognizedEncodingRawByteRestore);
        Run(passed, "config_bom_and_line_ending_only_change_restores_exact_bytes", BomAndLineEndingOnlyChange);
        Run(passed, "config_mid_confirmation_external_edit_blocks_and_refreshes", MidConfirmationExternalEdit);
        Run(passed, "config_corrupted_or_missing_backup_blocks_restore_and_preserves_live", CorruptedOrMissingBackup);
        Run(passed, "config_write_failure_locked_and_readonly_preserves_existing_file", WriteFailureLockedAndReadOnly);
        Run(passed, "config_undo_aborts_if_externally_edited_after_restore", UndoAbortsOnPostRestoreExternalEdit);
        Run(passed, "config_rejects_directories_missing_and_device_paths", RejectsInvalidPaths);
        return Task.FromResult<IReadOnlyList<string>>(passed);
    }

    private static void Utf8BomCrlfChineseRoundtrip()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);

        var contentA = "{\r\n  \"appName\": \"示例配置\",\r\n  \"theme\": \"light\",\r\n  \"shortcut\": \"Ctrl+Shift+P\"\r\n}\r\n";
        var contentB = "{\r\n  \"appName\": \"示例配置\",\r\n  \"theme\": \"dark\",\r\n  \"shortcut\": \"Ctrl+Alt+K\"\r\n}\r\n";
        var bytesA = EncodeUtf8Bom(contentA);
        var bytesB = EncodeUtf8Bom(contentB);
        var shaA = Sha256Hex(bytesA);
        var shaB = Sha256Hex(bytesB);

        Assert(bytesA[0] == 0xEF && bytesA[1] == 0xBB && bytesA[2] == 0xBF, "Expected UTF-8 BOM in version A");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm1.Refresh();
        vm1.SelectCandidateFile(cfgPath);
        Assert(vm1.HasCandidatePreview && vm1.CanCreateRestorePoint, "Candidate preview should be valid");
        vm1.NoteInput = "调整快捷键前";
        vm1.CreateRestorePoint();

        Assert(vm1.TotalCount == 1, "Expected 1 restore point");
        var saved = vm1.SelectedPoint ?? throw new InvalidOperationException("Expected non-null SelectedPoint");
        Assert(saved.Sha256 == shaA, "Saved point SHA-256 must match A");
        Assert(saved.ByteLength == bytesA.LongLength, "Saved point byte length must match A");
        Assert(saved.Note == "调整快捷键前", "Saved note must match");
        Assert(saved.EncodingLabel.Contains("BOM"), "Encoding label should note UTF-8 BOM");
        Assert(saved.LineEndingLabel.Contains("CRLF"), "Line ending label should note CRLF");
        Assert(vm1.ComparisonStatus == ConfigComparisonStatus.Identical, "Initial comparison should be Identical");
        Assert(!vm1.CanBeginRestore, "Restore should be disabled when identical");

        // Edit file to B, then simulate full Contain restart with a brand-new Service + ViewModel!
        File.WriteAllBytes(cfgPath, bytesB);
        var vm2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm2.Refresh();

        Assert(vm2.TotalCount == 1, "Restore point must persist after restart");
        Assert(vm2.SelectedPoint is not null && vm2.SelectedPoint.Note == "调整快捷键前", "Note must persist after restart");
        Assert(vm2.ComparisonStatus == ConfigComparisonStatus.Changed, "Comparison after edit should be Changed");
        Assert(vm2.HasTextDiff, "Recognized UTF-8 BOM text must produce line diff");
        Assert(vm2.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"light\"")), "Diff must show light removed from backup");
        Assert(vm2.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"dark\"")), "Diff must show dark added in current");
        Assert(vm2.CanBeginRestore, "Restore button must be enabled when Changed");

        // Enter restore confirmation, check target path & action text, confirm restore to A
        vm2.BeginRestoreConfirmation();
        Assert(vm2.IsConfirmingRestore && vm2.CanConfirmRestore, "Should enter confirmation state");
        Assert(vm2.ConfirmRestoreTargetPath == Path.GetFullPath(cfgPath), "Confirmation must display exact target path");
        Assert(vm2.ConfirmRestoreOperationText.Contains("撤销本次恢复"), "Confirmation must mention undo capability");

        vm2.ConfirmRestore();
        Assert(!vm2.IsConfirmingRestore, "Confirmation should close after restore");
        Assert(!vm2.IsOperationBannerError, "Restore should succeed without error");
        Assert(vm2.ComparisonStatus == ConfigComparisonStatus.Identical, "After restore, status must be Identical");
        Assert(vm2.CanUndoLastRestore, "Undo must be available right after restore");

        var afterRestoreBytes = File.ReadAllBytes(cfgPath);
        Assert(Sha256Hex(afterRestoreBytes) == shaA, "Restored file SHA-256 must match A byte-for-byte");
        Assert(afterRestoreBytes.SequenceEqual(bytesA), "Restored bytes must preserve BOM, CRLF, and Chinese exactly");

        // Undo restore -> must return byte-for-byte to B!
        vm2.UndoLastRestore();
        Assert(!vm2.IsOperationBannerError, "Undo should succeed without error");
        Assert(!vm2.CanUndoLastRestore, "Undo should be consumed after use");
        Assert(vm2.ComparisonStatus == ConfigComparisonStatus.Changed, "After undo back to B, comparison against A must be Changed");

        var afterUndoBytes = File.ReadAllBytes(cfgPath);
        Assert(Sha256Hex(afterUndoBytes) == shaB, "Undone file SHA-256 must match B byte-for-byte");
        Assert(afterUndoBytes.SequenceEqual(bytesB), "Undone bytes must preserve BOM, CRLF, and Chinese exactly");
    }

    private static void SameFilenameDifferentDirectories()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var fileA = Path.Combine(dir.Path, "profile-alpha", "settings.json");
        var fileB = Path.Combine(dir.Path, "profile-beta", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(fileA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fileB)!);

        var bytesA1 = Utf8NoBom.GetBytes("{\"profile\":\"alpha\",\"theme\":\"light\"}\n");
        var bytesB1 = Utf8NoBom.GetBytes("{\"profile\":\"beta\",\"theme\":\"solarized\"}\n");
        var bytesA2 = Utf8NoBom.GetBytes("{\"profile\":\"alpha\",\"theme\":\"dark\"}\n");
        var bytesB2 = Utf8NoBom.GetBytes("{\"profile\":\"beta\",\"theme\":\"monokai\"}\n");
        File.WriteAllBytes(fileA, bytesA1);
        File.WriteAllBytes(fileB, bytesB1);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();

        vm.SelectCandidateFile(fileA);
        vm.NoteInput = "Alpha 初始配置";
        vm.CreateRestorePoint();
        var pointA = vm.SelectedPoint!;

        vm.SelectCandidateFile(fileB);
        vm.NoteInput = "Beta 初始配置";
        vm.CreateRestorePoint();
        var pointB = vm.SelectedPoint!;

        Assert(vm.TotalCount == 2, "Expected 2 distinct restore points");
        Assert(pointA.TargetFileName == "settings.json" && pointB.TargetFileName == "settings.json", "Both have same filename");
        Assert(pointA.DisplayScopedName == @"profile-alpha\settings.json", $"Unexpected scoped name A: {pointA.DisplayScopedName}");
        Assert(pointB.DisplayScopedName == @"profile-beta\settings.json", $"Unexpected scoped name B: {pointB.DisplayScopedName}");
        Assert(pointA.TargetDirectory != pointB.TargetDirectory, "Directories must remain distinct");

        // Search by folder name must filter cleanly
        vm.SearchQuery = "profile-alpha";
        Assert(vm.FilteredPoints.Count == 1 && vm.FilteredPoints[0].PointId == pointA.PointId, "Folder search should isolate alpha");
        vm.ClearSearch();
        Assert(vm.FilteredPoints.Count == 2, "Clearing search restores both points");

        // Mutate both files, restore ONLY alpha -> beta must remain untouched!
        File.WriteAllBytes(fileA, bytesA2);
        File.WriteAllBytes(fileB, bytesB2);

        vm.SelectPoint(pointA);
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Changed, "Alpha should be Changed");
        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();

        Assert(Sha256Hex(File.ReadAllBytes(fileA)) == Sha256Hex(bytesA1), "Alpha must be restored to A1");
        Assert(Sha256Hex(File.ReadAllBytes(fileB)) == Sha256Hex(bytesB2), "Beta must remain completely untouched at B2");
    }

    private static void MissingFileRestoreAndUndo()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var iniPath = Path.Combine(dir.Path, "cfg", "settings.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);

        var origBytes = Utf8NoBom.GetBytes("[ui]\r\ntheme=light\r\n");
        File.WriteAllBytes(iniPath, origBytes);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(iniPath);
        vm.CreateRestorePoint();

        File.Delete(iniPath);
        vm.RefreshSelectedComparison();
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Missing, "Deleted file must report Missing");
        Assert(vm.CanBeginRestore, "Missing file must still allow Restore");

        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(File.Exists(iniPath), "Restore must recreate missing file");
        Assert(Sha256Hex(File.ReadAllBytes(iniPath)) == Sha256Hex(origBytes), "Recreated file must match exact original bytes");

        vm.UndoLastRestore();
        Assert(!File.Exists(iniPath), "Undo of recreated missing file must remove it again");
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Missing, "Status after undo returns to Missing");
    }

    private static void UnrecognizedEncodingRawByteRestore()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var binCfg = Path.Combine(dir.Path, "prefs.dat");

        // Invalid UTF-8 bytes + NUL byte (cannot be decoded reliably as UTF-8/UTF-16 text)
        var rawA = new byte[] { 0x80, 0xFF, 0xFE, 0x00, 0x41, 0x42, 0xC3, 0x28, 0x0D, 0x0A };
        var rawB = new byte[] { 0x80, 0xFF, 0xFE, 0x00, 0x58, 0x59, 0xC3, 0x28, 0x0A };
        File.WriteAllBytes(binCfg, rawA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(binCfg);
        vm.CreateRestorePoint();

        File.WriteAllBytes(binCfg, rawB);
        vm.RefreshSelectedComparison();
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Changed, "Binary change must report Changed");
        Assert(!vm.HasTextDiff && vm.ShowDiffFallback, "Unrecognized encoding must show size/hash fallback instead of garbled diff");

        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(File.ReadAllBytes(binCfg).SequenceEqual(rawA), "Raw byte restore must restore exact original binary bytes");

        vm.UndoLastRestore();
        Assert(File.ReadAllBytes(binCfg).SequenceEqual(rawB), "Raw byte undo must restore exact pre-restore binary bytes");
    }

    private static void BomAndLineEndingOnlyChange()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.json");

        var bomCrlf = EncodeUtf8Bom("{\r\n  \"theme\": \"light\"\r\n}\r\n");
        var noBomLf = Utf8NoBom.GetBytes("{\n  \"theme\": \"light\"\n}\n");
        File.WriteAllBytes(cfgPath, bomCrlf);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfgPath, noBomLf);
        vm.RefreshSelectedComparison();
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Changed, "BOM/EOL-only difference must be detected as Changed");
        Assert(vm.Comparison?.HasFormatOnlyDifference == true, "HasFormatOnlyDifference should be true");
        Assert(vm.ShowDiffFallback, "Format-only difference should show calm explanation");

        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(File.ReadAllBytes(cfgPath).SequenceEqual(bomCrlf), "Restoring BOM/EOL-only change must restore exact BOM + CRLF bytes");
    }

    private static void MidConfirmationExternalEdit()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.json");

        var bytesA = EncodeUtf8Bom("{\r\n  \"theme\": \"light\"\r\n}\r\n");
        var bytesB = EncodeUtf8Bom("{\r\n  \"theme\": \"dark\"\r\n}\r\n");
        var bytesC = EncodeUtf8Bom("{\r\n  \"theme\": \"high-contrast\"\r\n}\r\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();
        Assert(vm.IsConfirmingRestore, "Confirmation opened against B");

        // External edit happens while user is sitting on confirmation screen!
        File.WriteAllBytes(cfgPath, bytesC);

        // First Confirm MUST refuse overwrite, refresh diff to C, and stay in confirmation!
        vm.ConfirmRestore();
        Assert(vm.IsConfirmingRestore, "Must stay on confirmation screen after mid-edit refresh");
        Assert(vm.HasMidConfirmWarning, "Must display mid-confirmation external edit warning");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesC), "First confirm after external edit MUST NOT overwrite C!");
        Assert(vm.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("high-contrast")), "Refreshed diff must show C");

        // Second Confirm succeeds -> restores A, and Undo returns to C (not stale B)!
        vm.ConfirmRestore();
        Assert(!vm.IsConfirmingRestore, "Second confirm completes restore");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesA), "Second confirm must restore A");

        vm.UndoLastRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesC), "Undo must return to C (actual pre-overwrite content)");
    }

    private static void CorruptedOrMissingBackup()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.json");

        var bytesA = Utf8NoBom.GetBytes("{\"theme\":\"light\"}\n");
        var bytesB = Utf8NoBom.GetBytes("{\"theme\":\"dark\"}\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var svc = new LocalConfigRestorePointService(storageRoot);
        var vm = new ConfigRestorePointsViewModel(svc);
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();
        var point = vm.SelectedPoint!;

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();

        // Corrupt the stored backup blob on disk right before user clicks Confirm!
        var blobPath = Path.Combine(storageRoot, point.BackupBlobRelativePath);
        var corrupted = File.ReadAllBytes(blobPath);
        corrupted[0] ^= 0xFF;
        File.WriteAllBytes(blobPath, corrupted);

        vm.ConfirmRestore();
        Assert(vm.IsOperationBannerError, "Corrupted backup must fail with error");
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.BackupCorrupted, "Status must update to BackupCorrupted");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesB), "Live file B must remain completely untouched");

        // Delete blob completely -> still BackupCorrupted and restore disabled
        File.Delete(blobPath);
        vm.RefreshSelectedComparison();
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.BackupCorrupted, "Missing blob must report BackupCorrupted");
        Assert(!vm.CanBeginRestore, "Restore must be disabled when backup is missing");
    }

    private static void WriteFailureLockedAndReadOnly()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var targetDir = Path.Combine(dir.Path, "locked-app");
        Directory.CreateDirectory(targetDir);
        var cfgPath = Path.Combine(targetDir, "settings.json");

        var bytesA = Utf8NoBom.GetBytes("{\"theme\":\"light\"}\n");
        var bytesB = Utf8NoBom.GetBytes("{\"theme\":\"dark\"}\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();

        // Case 1: Another process holds FileShare.Read write-lock (readable, but cannot be replaced!)
        using (var writeLock = new FileStream(cfgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            vm.BeginRestoreConfirmation();
            Assert(vm.IsConfirmingRestore, "Confirmation should open when file is readable");
            vm.ConfirmRestore();
            Assert(vm.IsOperationBannerError, "Locked write must report failure");
            Assert(!vm.CanUndoLastRestore, "Failed restore must not create dangling undo");
        }

        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesB), "Existing file B must stay 100% intact after locked write failure");
        var strayTemps1 = Directory.GetFiles(targetDir, "*.contain-restore-*.tmp");
        Assert(strayTemps1.Length == 0, "No stray temp files may remain after locked write failure");

        // Case 2: ReadOnly file attribute
        File.SetAttributes(cfgPath, FileAttributes.ReadOnly);
        try
        {
            vm.BeginRestoreConfirmation();
            vm.ConfirmRestore();
            Assert(vm.IsOperationBannerError, "Read-only file must fail restore cleanly");
            Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesB), "Read-only file must stay 100% intact");
            Assert(Directory.GetFiles(targetDir, "*.contain-restore-*.tmp").Length == 0, "No stray temp files after read-only failure");
        }
        finally
        {
            File.SetAttributes(cfgPath, FileAttributes.Normal);
        }

        // Case 3: Exclusive FileShare.None lock -> ReadFailed without crashing
        using (var exclLock = new FileStream(cfgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            vm.RefreshSelectedComparison();
            Assert(vm.ComparisonStatus == ConfigComparisonStatus.ReadFailed, "Exclusive lock must report ReadFailed");
            Assert(!vm.CanBeginRestore, "Restore button must be disabled on ReadFailed");
        }
    }

    private static void UndoAbortsOnPostRestoreExternalEdit()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.json");

        var bytesA = Utf8NoBom.GetBytes("{\"theme\":\"light\"}\n");
        var bytesB = Utf8NoBom.GetBytes("{\"theme\":\"dark\"}\n");
        var bytesD = Utf8NoBom.GetBytes("{\"theme\":\"custom-user-edit\"}\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(vm.CanUndoLastRestore, "Undo ready after restore");

        // User edits file externally AFTER restore -> Undo must NOT clobber D!
        File.WriteAllBytes(cfgPath, bytesD);
        vm.UndoLastRestore();
        Assert(vm.IsOperationBannerWarning, "Post-restore external edit must produce warning conflict");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesD), "Post-restore edit D must be preserved intact");
    }

    private static void RejectsInvalidPaths()
    {
        using var dir = new TempDir();
        var svc = new LocalConfigRestorePointService(Path.Combine(dir.Path, "store"));

        Assert(!svc.InspectCandidateFile(dir.Path).IsValid, "Directory must be rejected");
        Assert(!svc.InspectCandidateFile(Path.Combine(dir.Path, "does-not-exist.json")).IsValid, "Missing file must be rejected");
        Assert(!svc.InspectCandidateFile(@"\\.\PhysicalDrive0").IsValid, "Device path must be rejected");
    }

    private static byte[] EncodeUtf8Bom(string text)
    {
        var preamble = Utf8WithBom.GetPreamble();
        var payload = Utf8NoBom.GetBytes(text);
        var combined = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, combined, preamble.Length, payload.Length);
        return combined;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Run(List<string> passed, string name, Action check)
    {
        check();
        passed.Add(name);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("ASSERTION FAILED: " + message);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Contain-CfgRP-Test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch { }
        }
    }
}
