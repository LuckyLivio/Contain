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
        Run(passed, "config_utf8_bom_crlf_chinese_roundtrip_and_directional_previews", Utf8BomCrlfChineseRoundtripAndDirectionalPreviews);
        Run(passed, "config_asymmetric_line_counts_flip_between_browse_and_restore_preview", AsymmetricLineCountsFlipByDirection);
        Run(passed, "config_disambiguates_same_filename_and_cross_file_undo_preview", SameFilenameDifferentDirectoriesAndCrossFileUndo);
        Run(passed, "config_missing_file_restore_recreates_and_undo_preview_forecasts_removal", MissingFileRestoreAndUndoPreview);
        Run(passed, "config_unrecognized_encoding_shows_size_hash_and_restores_raw_bytes", UnrecognizedEncodingRawByteRestore);
        Run(passed, "config_bom_and_line_ending_only_change_restores_exact_bytes", BomAndLineEndingOnlyChange);
        Run(passed, "config_mid_confirmation_external_edit_blocks_and_refreshes_in_restore_direction", MidConfirmationExternalEdit);
        Run(passed, "config_corrupted_or_missing_backup_blocks_restore_and_preserves_live", CorruptedOrMissingBackup);
        Run(passed, "config_write_failure_locked_and_readonly_preserves_existing_file", WriteFailureLockedAndReadOnly);
        Run(passed, "config_undo_conflict_on_post_restore_external_edit_preview_and_mid_preview", UndoConflictOnPostRestoreExternalEdit);
        Run(passed, "config_undo_conflict_on_post_restore_active_deletion_preview_and_mid_preview", UndoConflictOnPostRestoreActiveDeletion);
        Run(passed, "config_rejects_directories_missing_and_device_paths", RejectsInvalidPaths);
        return Task.FromResult<IReadOnlyList<string>>(passed);
    }

    private static void Utf8BomCrlfChineseRoundtripAndDirectionalPreviews()
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

        var saved = vm1.SelectedPoint ?? throw new InvalidOperationException("Expected non-null SelectedPoint");
        Assert(saved.Sha256 == shaA && saved.ByteLength == bytesA.LongLength, "Saved point metadata must match A");
        Assert(saved.Note == "调整快捷键前", "Saved note must match");
        Assert(saved.EncodingLabel.Contains("BOM") && saved.LineEndingLabel.Contains("CRLF"), "Encoding/EOL detected");
        Assert(vm1.ComparisonStatus == ConfigComparisonStatus.Identical && !vm1.CanBeginRestore, "Identical disables restore");

        // Edit file to B, then simulate full Contain restart!
        File.WriteAllBytes(cfgPath, bytesB);
        var vm2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm2.Refresh();

        // Normal browse mode: "从备份到现在" (- light / + dark)
        Assert(vm2.TotalCount == 1 && vm2.ComparisonStatus == ConfigComparisonStatus.Changed, "Persisted point detects change after restart");
        Assert(vm2.ActiveDiffDirection == ConfigDiffDirection.BackupToCurrent, "Browse mode uses BackupToCurrent");
        Assert(vm2.DiffDirectionBadgeText == "从备份到现在", "Browse direction badge");
        Assert(vm2.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"light\"")), "Browse diff: - light");
        Assert(vm2.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"dark\"")), "Browse diff: + dark");

        // Enter Restore Preview -> flips direction to "当前文件 → 恢复后" (- dark / + light)!
        vm2.BeginRestoreConfirmation();
        Assert(vm2.IsConfirmingRestore && vm2.CanConfirmRestore, "Restore preview active");
        Assert(vm2.ActiveDiffDirection == ConfigDiffDirection.CurrentToBackup, "Restore preview flips to CurrentToBackup");
        Assert(vm2.DiffDirectionBadgeText == "当前文件 → 恢复后", "Restore preview direction badge");
        Assert(vm2.ConfirmRestoreTargetPath == Path.GetFullPath(cfgPath), "Exact target path displayed");
        Assert(vm2.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"dark\"")), "Restore preview MUST remove dark");
        Assert(vm2.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"light\"")), "Restore preview MUST write light");

        // Cancel restore confirmation -> flips cleanly back to BackupToCurrent without writing
        vm2.CancelRestoreConfirmation();
        Assert(!vm2.IsConfirmingRestore && vm2.ActiveDiffDirection == ConfigDiffDirection.BackupToCurrent, "Cancel restore reverts to browse direction");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == shaB, "Cancel restore writes nothing");

        // Re-enter Restore Preview & confirm -> disk becomes A byte-for-byte
        vm2.BeginRestoreConfirmation();
        vm2.ConfirmRestore();
        Assert(!vm2.IsConfirmingRestore && !vm2.IsOperationBannerError, "Restore succeeded");
        Assert(vm2.ComparisonStatus == ConfigComparisonStatus.Identical && vm2.CanUndoLastRestore, "Status identical and undo ready");
        var afterRestoreBytes = File.ReadAllBytes(cfgPath);
        Assert(Sha256Hex(afterRestoreBytes) == shaA && afterRestoreBytes.SequenceEqual(bytesA), "Restored file matches A byte-for-byte");

        // Open Lightweight Undo Preview -> direction "当前文件 → 撤销后" (- light / + dark)
        vm2.BeginUndoPreview();
        Assert(vm2.IsPreviewingUndo && vm2.CanConfirmUndo, "Undo preview opened cleanly");
        Assert(vm2.UndoPreviewTargetPath == Path.GetFullPath(cfgPath), "Undo preview shows exact target path");
        Assert(vm2.UndoPreviewDirectionLabel == "当前文件 → 撤销后", "Undo preview direction label");
        Assert(vm2.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"light\"")), "Undo preview removes light");
        Assert(vm2.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"dark\"")), "Undo preview writes dark");

        // Cancel Undo Preview -> MUST NOT write disk or consume undo record!
        vm2.CancelUndoPreview();
        Assert(!vm2.IsPreviewingUndo, "Undo preview closed on cancel");
        Assert(vm2.CanUndoLastRestore && vm2.LatestUndo is { Consumed: false }, "Cancel undo preview MUST NOT consume undo record");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == shaA, "Cancel undo preview MUST NOT modify disk");

        // Re-open Undo Preview & Confirm Undo -> disk returns byte-for-byte to B!
        vm2.BeginUndoPreview();
        Assert(vm2.IsPreviewingUndo && vm2.CanConfirmUndo, "Undo preview still usable after cancel");
        vm2.ConfirmUndoRestore();
        Assert(!vm2.IsPreviewingUndo && !vm2.IsOperationBannerError && !vm2.CanUndoLastRestore, "Undo succeeded and consumed");
        Assert(vm2.ComparisonStatus == ConfigComparisonStatus.Changed, "Comparison against A is Changed again");
        var afterUndoBytes = File.ReadAllBytes(cfgPath);
        Assert(Sha256Hex(afterUndoBytes) == shaB && afterUndoBytes.SequenceEqual(bytesB), "Undone file matches B byte-for-byte");
    }

    private static void AsymmetricLineCountsFlipByDirection()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.ini");

        // Backup A has 1 key (UTF-8 BOM + CRLF); Current B has 3 keys (UTF-8 no BOM + LF)
        var bytesA = EncodeUtf8Bom("theme=light\r\n");
        var bytesB = Utf8NoBom.GetBytes("theme=dark\nfont=14\nwrap=true\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();

        // Browse (Backup -> Current): -1 / +3
        Assert(vm.Comparison!.RemovedLineCount == 1 && vm.Comparison.AddedLineCount == 3,
            $"Browse counts expected -1/+3, got -{vm.Comparison.RemovedLineCount}/+{vm.Comparison.AddedLineCount}");

        // Restore Preview (Current -> Restored): -3 / +1, encoding Current(无 BOM · LF) -> Restored(含 BOM · CRLF)!
        vm.BeginRestoreConfirmation();
        Assert(vm.Comparison!.RemovedLineCount == 3 && vm.Comparison.AddedLineCount == 1,
            $"Restore preview counts expected -3/+1, got -{vm.Comparison.RemovedLineCount}/+{vm.Comparison.AddedLineCount}");
        Assert(vm.ConfirmRestoreFormatTransition.Contains("无 BOM") && vm.ConfirmRestoreFormatTransition.Contains("含 BOM"),
            "Restore preview must show Current -> Restored encoding transition");
    }

    private static void SameFilenameDifferentDirectoriesAndCrossFileUndo()
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

        File.WriteAllBytes(fileA, bytesA2);
        File.WriteAllBytes(fileB, bytesB2);

        // Restore ONLY alpha
        vm.SelectPoint(pointA);
        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(Sha256Hex(File.ReadAllBytes(fileA)) == Sha256Hex(bytesA1), "Alpha restored to A1");
        Assert(Sha256Hex(File.ReadAllBytes(fileB)) == Sha256Hex(bytesB2), "Beta untouched at B2");

        // Now user switches selection to SAME-NAMED file in another directory (profile-beta\settings.json)!
        vm.SelectPoint(pointB);
        Assert(vm.IsUndoTargetDifferentFromSelected, "Must detect undo target differs from currently selected file");
        Assert(vm.UndoButtonText.Contains(@"profile-alpha\settings.json"), $"Undo button must name actual target: {vm.UndoButtonText}");
        Assert(vm.UndoCrossTargetNotice.Contains(@"profile-alpha\settings.json") && vm.UndoCrossTargetNotice.Contains(@"profile-beta\settings.json"),
            "Cross-target warning must name both files");

        // Open Undo Preview while profile-beta is selected -> preview MUST target profile-alpha!
        vm.BeginUndoPreview();
        Assert(vm.UndoPreviewTargetPath == Path.GetFullPath(fileA), "Undo preview must point to profile-alpha");
        Assert(vm.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("light")), "Undo preview removes alpha light");
        Assert(vm.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("dark")), "Undo preview writes alpha dark");

        vm.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(fileA)) == Sha256Hex(bytesA2), "Alpha undone back to A2");
        Assert(Sha256Hex(File.ReadAllBytes(fileB)) == Sha256Hex(bytesB2), "Beta STILL untouched at B2");
    }

    private static void MissingFileRestoreAndUndoPreview()
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
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Missing && vm.CanBeginRestore, "Missing file can be restored");

        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(File.Exists(iniPath) && Sha256Hex(File.ReadAllBytes(iniPath)) == Sha256Hex(origBytes), "Restore recreated missing file");

        // Open Undo Preview -> MUST explicitly forecast removing the recreated file, without deleting yet!
        vm.BeginUndoPreview();
        Assert(vm.IsPreviewingUndo && vm.CanConfirmUndo && vm.IsUndoPreviewRemoveRecreated, "Undo preview recognizes recreated file");
        Assert(vm.UndoPreviewHeadline.Contains("移除本次重建的文件") || vm.UndoPreviewActionSummary.Contains("移除本次恢复重建的文件"),
            "Undo preview must explicitly forecast removal of recreated file");
        Assert(File.Exists(iniPath), "Opening preview MUST NOT delete file yet");

        // Cancel preview -> file still exists & undo not consumed
        vm.CancelUndoPreview();
        Assert(File.Exists(iniPath) && vm.CanUndoLastRestore, "Canceling preview keeps recreated file and undo record");

        // Re-open & Confirm -> cleanly removes recreated file
        vm.BeginUndoPreview();
        vm.ConfirmUndoRestore();
        Assert(!File.Exists(iniPath), "Confirming undo removes recreated file");
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Missing && !vm.CanUndoLastRestore, "Returned to Missing and undo consumed");
    }

    private static void UnrecognizedEncodingRawByteRestore()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var binCfg = Path.Combine(dir.Path, "prefs.dat");

        var rawA = new byte[] { 0x80, 0xFF, 0xFE, 0x00, 0x41, 0x42, 0xC3, 0x28, 0x0D, 0x0A };
        var rawB = new byte[] { 0x80, 0xFF, 0xFE, 0x00, 0x58, 0x59, 0xC3, 0x28, 0x0A };
        File.WriteAllBytes(binCfg, rawA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(binCfg);
        vm.CreateRestorePoint();

        File.WriteAllBytes(binCfg, rawB);
        vm.RefreshSelectedComparison();
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Changed && !vm.HasTextDiff && vm.ShowDiffFallback, "Binary fallback shown");

        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(File.ReadAllBytes(binCfg).SequenceEqual(rawA), "Restored raw binary A");

        vm.BeginUndoPreview();
        Assert(vm.CanConfirmUndo && !vm.HasUndoTextDiff && vm.ShowUndoDiffFallback, "Undo preview binary fallback shown");
        vm.ConfirmUndoRestore();
        Assert(File.ReadAllBytes(binCfg).SequenceEqual(rawB), "Undone raw binary B");
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
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Changed && vm.Comparison?.HasFormatOnlyDifference == true, "Format-only difference detected");

        vm.BeginRestoreConfirmation();
        Assert(vm.ComparisonDetail.Contains("当前：") && vm.ComparisonDetail.Contains("恢复后："), "Format-only restore preview shows Current -> Restored");
        vm.ConfirmRestore();
        Assert(File.ReadAllBytes(cfgPath).SequenceEqual(bomCrlf), "Restored exact BOM + CRLF bytes");
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

        // External edit to C while user sits on Restore Confirmation!
        File.WriteAllBytes(cfgPath, bytesC);
        vm.ConfirmRestore();
        Assert(vm.IsConfirmingRestore && vm.HasMidConfirmWarning, "Stays on confirm with mid-edit warning");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesC), "First confirm does NOT overwrite C");
        Assert(vm.ActiveDiffDirection == ConfigDiffDirection.CurrentToBackup, "Refreshed confirm diff stays in CurrentToBackup direction");
        Assert(vm.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("high-contrast")), "Refreshed diff removes high-contrast");
        Assert(vm.DiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("light")), "Refreshed diff writes light");

        vm.ConfirmRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesA), "Second confirm restores A");

        vm.BeginUndoPreview();
        Assert(vm.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("high-contrast")), "Undo preview shows writing back C");
        vm.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesC), "Undo returns to C");
    }

    private static void CorruptedOrMissingBackup()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.json");

        var bytesA = Utf8NoBom.GetBytes("{\"theme\":\"light\"}\n");
        var bytesB = Utf8NoBom.GetBytes("{\"theme\":\"dark\"}\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();
        var point = vm.SelectedPoint!;

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();

        var blobPath = Path.Combine(storageRoot, point.BackupBlobRelativePath);
        var corrupted = File.ReadAllBytes(blobPath);
        corrupted[0] ^= 0xFF;
        File.WriteAllBytes(blobPath, corrupted);

        vm.ConfirmRestore();
        Assert(vm.IsOperationBannerError && vm.ComparisonStatus == ConfigComparisonStatus.BackupCorrupted, "Corrupted backup blocked");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesB), "Live file B preserved intact");
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

        using (var writeLock = new FileStream(cfgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            vm.BeginRestoreConfirmation();
            vm.ConfirmRestore();
            Assert(vm.IsOperationBannerError && !vm.CanUndoLastRestore, "Locked write fails cleanly");
        }
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesB), "File B intact after write lock failure");
        Assert(Directory.GetFiles(targetDir, "*.contain-restore-*.tmp").Length == 0, "Zero stray temp files");
    }

    private static void UndoConflictOnPostRestoreExternalEdit()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.json");

        var bytesA = Utf8NoBom.GetBytes("{\"theme\":\"light\"}\n");
        var bytesB = Utf8NoBom.GetBytes("{\"theme\":\"dark\"}\n");
        var bytesC = Utf8NoBom.GetBytes("{\"theme\":\"user-edited-c\"}\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(vm.CanUndoLastRestore, "Undo ready after restore to A");

        // Case 1: Edit A -> C BEFORE opening Undo Preview
        File.WriteAllBytes(cfgPath, bytesC);
        vm.BeginUndoPreview();
        Assert(vm.IsPreviewingUndo && vm.IsUndoPreviewConflict && !vm.CanConfirmUndo, "Preview must detect post-restore external edit conflict");
        Assert(vm.UndoPreview!.Status == ConfigUndoPreviewStatus.TargetModifiedConflict, "Expected TargetModifiedConflict");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesC), "C preserved");
        vm.CancelUndoPreview();
        Assert(vm.CanUndoLastRestore, "Conflict preview cancel preserves undo record");

        // Case 2: Put A back so Undo Preview opens ready, THEN edit A -> C DURING Undo Preview!
        File.WriteAllBytes(cfgPath, bytesA);
        vm.BeginUndoPreview();
        Assert(vm.CanConfirmUndo, "Preview ready while file is A");
        File.WriteAllBytes(cfgPath, bytesC);

        vm.ConfirmUndoRestore();
        Assert(vm.IsPreviewingUndo && vm.IsUndoPreviewConflict && !vm.CanConfirmUndo, "Confirm Undo re-check catches mid-preview edit!");
        Assert(vm.UndoPreview!.Status == ConfigUndoPreviewStatus.TargetModifiedConflict, "Refreshed preview shows TargetModifiedConflict");
        Assert(Sha256Hex(File.ReadAllBytes(cfgPath)) == Sha256Hex(bytesC), "Mid-preview edit C MUST be preserved intact");
        Assert(vm.LatestUndo is { Consumed: false }, "Undo record NOT consumed on conflict");
    }

    private static void UndoConflictOnPostRestoreActiveDeletion()
    {
        using var dir = new TempDir();
        var storageRoot = Path.Combine(dir.Path, "store");
        var cfgPath = Path.Combine(dir.Path, "settings.json");

        var bytesA = Utf8NoBom.GetBytes("{\"theme\":\"light\"}\n");
        var bytesB = Utf8NoBom.GetBytes("{\"theme\":\"dark\"}\n");
        File.WriteAllBytes(cfgPath, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(storageRoot));
        vm.Refresh();
        vm.SelectCandidateFile(cfgPath);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfgPath, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(File.Exists(cfgPath), "Restored A exists");

        // Case 1: User actively deletes A BEFORE opening Undo Preview!
        File.Delete(cfgPath);
        vm.BeginUndoPreview();
        Assert(vm.IsPreviewingUndo && vm.IsUndoPreviewConflict && !vm.CanConfirmUndo, "Preview detects active deletion conflict");
        Assert(vm.UndoPreview!.Status == ConfigUndoPreviewStatus.TargetDeletedConflict, "Expected TargetDeletedConflict");
        Assert(!File.Exists(cfgPath), "Deleted file MUST stay deleted (not resurrected!)");
        vm.CancelUndoPreview();
        Assert(vm.LatestUndo is { Consumed: false }, "Undo record NOT silently consumed");

        // Case 2: Put A back so Undo Preview opens ready, THEN delete A DURING Undo Preview!
        File.WriteAllBytes(cfgPath, bytesA);
        vm.BeginUndoPreview();
        Assert(vm.CanConfirmUndo, "Preview ready while A exists");
        File.Delete(cfgPath);

        vm.ConfirmUndoRestore();
        Assert(vm.IsPreviewingUndo && vm.IsUndoPreviewConflict && !vm.CanConfirmUndo, "Confirm Undo re-check catches mid-preview deletion!");
        Assert(vm.UndoPreview!.Status == ConfigUndoPreviewStatus.TargetDeletedConflict, "Refreshed preview shows TargetDeletedConflict");
        Assert(!File.Exists(cfgPath), "Mid-preview deleted file MUST stay deleted!");
        Assert(vm.LatestUndo is { Consumed: false }, "Undo record NOT consumed on deletion conflict");
    }

    private static void RejectsInvalidPaths()
    {
        using var dir = new TempDir();
        var svc = new LocalConfigRestorePointService(Path.Combine(dir.Path, "store"));
        Assert(!svc.InspectCandidateFile(dir.Path).IsValid, "Directory rejected");
        Assert(!svc.InspectCandidateFile(Path.Combine(dir.Path, "does-not-exist.json")).IsValid, "Missing file rejected");
        Assert(!svc.InspectCandidateFile(@"\\.\PhysicalDrive0").IsValid, "Device path rejected");
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
