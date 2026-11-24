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
        Run(passed, "config_beta3_boundary1_post_replace_readback_and_refresh_failure_preserve_original_b", Beta3Boundary1PostReplaceReadbackAndRefreshFailures);
        Run(passed, "config_beta3_boundary2_undo_completion_mark_save_failure_and_idempotent_restarts", Beta3Boundary2UndoMarkSaveFailureAndIdempotentRestarts);
        Run(passed, "config_beta3_moment1_crash_after_b_saved_before_target_replaced", Beta3Moment1CrashBeforeTargetReplaced);
        Run(passed, "config_beta3_moment2_crash_after_target_replaced_with_a_before_result_saved", Beta3Moment2CrashAfterTargetReplacedBeforeResultSaved);
        Run(passed, "config_beta3_moment3_crash_after_undo_wrote_b_before_consumed_saved", Beta3Moment3CrashAfterUndoWroteBBeforeConsumedSaved);
        Run(passed, "config_beta3_interrupted_then_external_edit_to_c_view_and_save_as_original_b", Beta3InterruptedThenExternalEditToCAndOriginalCopyRecovery);
        Run(passed, "config_beta3_originally_missing_file_interrupted_recreate_undo_and_external_c", Beta3OriginallyMissingFileInterruptions);
        Run(passed, "config_beta3_insufficient_evidence_shows_needs_verification_without_touching_target", Beta3InsufficientEvidenceShowsNeedsVerification);
        Run(passed, "config_beta3_backward_compatibility_with_beta1_and_beta2_points_and_undo_records", Beta3BackwardCompatibilityWithBeta1AndBeta2);
        Run(passed, "config_rc1_dual_window_live_restore_prepared_blocks_window2_without_hijacking", Rc1DualWindowLiveRestorePrepared);
        Run(passed, "config_rc1_dual_window_live_undo_prepared_blocks_window2_without_hijacking", Rc1DualWindowLiveUndoPrepared);
        Run(passed, "config_rc1_window2_stale_restore_and_undo_previews_reread_disk_after_window1_completes", Rc1StalePreviewsRereadAfterWindow1Completes);
        Run(passed, "config_rc1_owner_killed_releases_lock_and_window2_reconciles_cleanly_across_two_reopens", Rc1OwnerKilledTakeoverAndTwoReopens);
        Run(passed, "config_rc1_different_target_files_operate_independently_while_lock_held", Rc1DifferentTargetFilesIndependent);
        Run(passed, "config_rc1_completed_restore_by_another_live_pid_remains_undoable", Rc1CompletedRestoreByAnotherLivePidRemainsUndoable);
        Run(passed, "config_v080_export_saved_backup_a_after_source_modified_to_c_and_deleted_excludes_source_paths", V080ExportSavedBackupAAfterSourceChangedAndDeleted);
        Run(passed, "config_v080_import_onto_different_path_preview_cancel_restore_and_undo_to_b", V080ImportDifferentPathPreviewCancelRestoreUndo);
        Run(passed, "config_v080_import_onto_missing_target_creates_a_and_undo_removes_file", V080ImportMissingTargetCreateAndUndoRemoves);
        Run(passed, "config_v080_import_rejects_unknown_format_corrupt_length_mismatch_tamper_and_bad_name", V080ImportRejectsMalformedPackages);
        Run(passed, "config_v080_import_mid_confirmation_external_edit_preserves_b2_and_refreshes_preview", V080ImportMidConfirmationExternalEdit);
        Run(passed, "config_v080_import_reuses_v070_cross_process_lock_and_interruption_reconciliation", V080ImportReusesCrossProcessLockAndCrashReconciliation);
        Run(passed, "config_v080_import_unrecognized_binary_payload_and_identical_target", V080ImportBinaryPayloadAndIdenticalTarget);
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

    private static (byte[] BytesA, byte[] BytesB, byte[] BytesC, string ShaA, string ShaB, string ShaC) CreateLightDarkHighContrastFixtures()
    {
        var bytesA = EncodeUtf8Bom("{\r\n  \"appName\": \"示例配置\",\r\n  \"theme\": \"light\",\r\n  \"shortcut\": \"Ctrl+Shift+P\"\r\n}\r\n");
        var bytesB = EncodeUtf8Bom("{\r\n  \"appName\": \"示例配置\",\r\n  \"theme\": \"dark\",\r\n  \"shortcut\": \"Ctrl+Alt+K\"\r\n}\r\n");
        var bytesC = EncodeUtf8Bom("{\r\n  \"appName\": \"示例配置\",\r\n  \"theme\": \"high-contrast\",\r\n  \"shortcut\": \"Ctrl+Alt+H\"\r\n}\r\n");
        return (bytesA, bytesB, bytesC, Sha256Hex(bytesA), Sha256Hex(bytesB), Sha256Hex(bytesC));
    }

    private static void Beta3Boundary1PostReplaceReadbackAndRefreshFailures()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();

        // Subcase 1a: Target replaced with A, then post-write readback verification throws IOException!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var cfg = Path.Combine(dir.Path, "app", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllBytes(cfg, bytesA);

            var hooks = new DelegateHooks
            {
                OnAfterTargetReplacedBeforeVerify = (_, _) => throw new IOException("Simulated readback lock after File.Replace")
            };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh();
            vm.SelectCandidateFile(cfg);
            vm.NoteInput = "边界1a 回读校验失败";
            vm.CreateRestorePoint();

            File.WriteAllBytes(cfg, bytesB);
            vm.RefreshSelectedComparison();
            vm.BeginRestoreConfirmation();
            vm.ConfirmRestore();

            // Disk is A, and B's undo copy MUST STILL EXIST & pass SHA-256!
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "1a: Target was replaced with A");
            var blobs = Directory.GetFiles(Path.Combine(store, "Undo"), "*.bin");
            Assert(blobs.Length == 1 && Sha256Hex(File.ReadAllBytes(blobs[0])) == shaB, "1a: Original copy B MUST be preserved intact");
            Assert(vm.HasRecoveryCard && vm.RecoveryStatus == ConfigRecoveryStatus.RestoreAppliedNeedsReview, "1a: Recovery immediately verifies A & preserves B");

            // Full reopen -> can still undo via preview back to B!
            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(reopen.CanUndoLastRestore, "1a reopen: Undo available");
            reopen.BeginUndoPreview();
            Assert(reopen.CanConfirmUndo && reopen.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"dark\"")),
                "1a reopen: Undo preview shows writing back dark (B)");
            reopen.ConfirmUndoRestore();
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.ReadAllBytes(cfg).SequenceEqual(bytesB), "1a reopen: Restored B byte-for-byte");
        }

        // Subcase 1b: Target replaced with A & restore_completed saved, then CompareRestorePoint refresh throws!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var cfg = Path.Combine(dir.Path, "app", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllBytes(cfg, bytesA);

            var hooks = new DelegateHooks
            {
                OnBeforePostRestoreRefresh = (_, _) => throw new InvalidOperationException("Simulated post-restore UI refresh exception")
            };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh();
            vm.SelectCandidateFile(cfg);
            vm.CreateRestorePoint();

            File.WriteAllBytes(cfg, bytesB);
            vm.RefreshSelectedComparison();
            vm.BeginRestoreConfirmation();
            vm.ConfirmRestore();

            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "1b: Target replaced with A");
            Assert(!vm.IsOperationBannerError && vm.IsOperationBannerWarning, "1b: Succeeded with non-fatal refresh warning");
            Assert(vm.CanUndoLastRestore, "1b: Undo copy preserved and active");
            var blobs = Directory.GetFiles(Path.Combine(store, "Undo"), "*.bin");
            Assert(blobs.Length == 1 && Sha256Hex(File.ReadAllBytes(blobs[0])) == shaB, "1b: B blob intact");

            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(!reopen.HasRecoveryCard && reopen.CanUndoLastRestore, "1b reopen: Clean completed restore with active undo");
            reopen.BeginUndoPreview();
            reopen.ConfirmUndoRestore();
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "1b reopen: Undo cleanly restores B");
        }
    }

    private static void Beta3Boundary2UndoMarkSaveFailureAndIdempotentRestarts()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var hooks = new DelegateHooks
        {
            OnAfterUndoTargetUpdatedBeforeMarkCompleted = (_, _) => throw new IOException("Simulated failure saving undo_completed flag")
        };
        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
        vm.Refresh();
        vm.SelectCandidateFile(cfg);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfg, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Restored A");

        vm.BeginUndoPreview();
        vm.ConfirmUndoRestore();

        // Disk IS B, warning explains completion mark failed, and NO stale Undo bar is shown!
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "Target written back to B");
        Assert(vm.IsOperationBannerWarning && vm.OperationBannerHeadline.Contains("完成标记暂未保存"), "Honest warning instead of silent swallow");
        Assert(!vm.ShowUndoBar && !vm.CanUndoLastRestore, "Must NOT keep showing stale undo bar");

        var mtimeAfterUndo = File.GetLastWriteTimeUtc(cfg);
        File.SetAttributes(cfg, FileAttributes.ReadOnly); // Prove startup check NEVER writes target!
        try
        {
            // Reopen #1: Read-only reconciliation verifies B, shows actual result, finalizes local metadata
            var reopen1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen1.CheckInterruptedOperationOnStartup();
            Assert(reopen1.HasPendingRecoveryShellBanner && reopen1.RecoveryStatus == ConfigRecoveryStatus.UndoAlreadyApplied,
                "Reopen #1 startup check identifies UndoAlreadyApplied");
            reopen1.Refresh();
            Assert(reopen1.ShowRecoveryCard && reopen1.RecoveryStatus == ConfigRecoveryStatus.UndoAlreadyApplied,
                "Reopen #1 Refresh preserves session recovery card for user review");
            Assert(!reopen1.ShowUndoBar && !reopen1.CanUndoLastRestore, "Reopen #1 shows NO stale undo bar");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.GetLastWriteTimeUtc(cfg) == mtimeAfterUndo,
                "Reopen #1 performed ZERO writes to target");

            // Reopen #2: Second launch stays completely clean and performs ZERO writes!
            var reopen2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen2.CheckInterruptedOperationOnStartup();
            reopen2.Refresh();
            Assert(!reopen2.HasRecoveryCard && !reopen2.ShowUndoBar && !reopen2.CanUndoLastRestore,
                "Reopen #2 is clean with no repeated alerts or undo bar");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.GetLastWriteTimeUtc(cfg) == mtimeAfterUndo,
                "Reopen #2 performed ZERO writes to target");
        }
        finally
        {
            File.SetAttributes(cfg, FileAttributes.Normal);
        }
    }

    private static void Beta3Moment1CrashBeforeTargetReplaced()
    {
        var (bytesA, bytesB, _, _, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var hooks = new DelegateHooks
        {
            OnAfterRestorePrepared = (_, _) => throw new OperationCanceledException("Simulated hard process crash after B saved, before replace")
        };
        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
        vm.Refresh();
        vm.SelectCandidateFile(cfg);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfg, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();

        var crashed = false;
        try { vm.ConfirmRestore(); } catch (OperationCanceledException) { crashed = true; }
        Assert(crashed, "Simulated crash triggered at exact Moment 1 seam");
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "Target is still B before replace");
        var mtimeBeforeReopen = File.GetLastWriteTimeUtc(cfg);

        // Reopen #1: Disk is still B, NO false successful restore, NO false Undo bar!
        var reopen1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        reopen1.CheckInterruptedOperationOnStartup();
        reopen1.Refresh();
        Assert(reopen1.ShowRecoveryCard && reopen1.RecoveryStatus == ConfigRecoveryStatus.RestoreNotApplied,
            $"Moment 1 expected RestoreNotApplied, got {reopen1.RecoveryStatus}");
        Assert(!reopen1.ShowUndoBar && !reopen1.CanUndoLastRestore && !reopen1.CanUndoFromRecovery,
            "Moment 1 MUST NOT show a false successful restore or active undo button");
        Assert(reopen1.ComparisonStatus == ConfigComparisonStatus.Changed && reopen1.CanBeginRestore,
            "Moment 1 still shows A vs B changed so user can retry restore");
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.GetLastWriteTimeUtc(cfg) == mtimeBeforeReopen,
            "Moment 1 reopen did NOT write target");

        // Reopen #2: Clean state, still B
        var reopen2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        reopen2.CheckInterruptedOperationOnStartup();
        reopen2.Refresh();
        Assert(!reopen2.HasRecoveryCard && !reopen2.ShowUndoBar, "Moment 1 second reopen is completely clean");
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "Target remains B");
    }

    private static void Beta3Moment2CrashAfterTargetReplacedBeforeResultSaved()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();

        foreach (var crashPoint in new[] { "before_verify", "before_save_completed" })
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var cfg = Path.Combine(dir.Path, "app", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllBytes(cfg, bytesA);

            var hooks = new DelegateHooks
            {
                OnAfterTargetReplacedBeforeVerify = crashPoint == "before_verify"
                    ? (_, _) => throw new OperationCanceledException("Simulated crash right after replace, before verify")
                    : null,
                OnBeforeSaveRestoreCompleted = crashPoint == "before_save_completed"
                    ? (_, _) => throw new OperationCanceledException("Simulated crash right after verify, before restore_completed saved")
                    : null
            };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh();
            vm.SelectCandidateFile(cfg);
            vm.CreateRestorePoint();

            File.WriteAllBytes(cfg, bytesB);
            vm.RefreshSelectedComparison();
            vm.BeginRestoreConfirmation();

            var crashed = false;
            try { vm.ConfirmRestore(); } catch (OperationCanceledException) { crashed = true; }
            Assert(crashed, $"Moment 2 ({crashPoint}) crash triggered");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, $"Moment 2 ({crashPoint}) disk is already A");
            var mtimeBeforeReopen = File.GetLastWriteTimeUtc(cfg);

            // Reopen -> Read-only check verifies disk is A, B is intact, offers Undo Preview back to B!
            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(reopen.ShowRecoveryCard && reopen.RecoveryStatus == ConfigRecoveryStatus.RestoreAppliedNeedsReview,
                $"Moment 2 ({crashPoint}) expected RestoreAppliedNeedsReview, got {reopen.RecoveryStatus}");
            Assert(reopen.CanUndoFromRecovery && reopen.CanViewOriginalCopyFromRecovery,
                $"Moment 2 ({crashPoint}) offers undo preview and view original copy");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA && File.GetLastWriteTimeUtc(cfg) == mtimeBeforeReopen,
                $"Moment 2 ({crashPoint}) startup check never modified target");

            reopen.BeginUndoPreviewFromRecovery();
            Assert(reopen.ShowUndoPreviewPanel && reopen.CanConfirmUndo, $"Moment 2 ({crashPoint}) undo preview opens cleanly");
            Assert(reopen.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"light\"")),
                "Moment 2 undo preview removes light");
            Assert(reopen.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"dark\"")),
                "Moment 2 undo preview writes back dark");

            reopen.ConfirmUndoRestore();
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.ReadAllBytes(cfg).SequenceEqual(bytesB),
                $"Moment 2 ({crashPoint}) confirmed undo restored exact bytes of B");
            Assert(!reopen.HasRecoveryCard && !reopen.CanUndoLastRestore, $"Moment 2 ({crashPoint}) closed cleanly");
        }
    }

    private static void Beta3Moment3CrashAfterUndoWroteBBeforeConsumedSaved()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var hooks = new DelegateHooks
        {
            OnAfterUndoTargetUpdatedBeforeMarkCompleted = (_, _) => throw new OperationCanceledException("Simulated crash after undo wrote B, before undo_completed saved")
        };
        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
        vm.Refresh();
        vm.SelectCandidateFile(cfg);
        vm.CreateRestorePoint();

        File.WriteAllBytes(cfg, bytesB);
        vm.RefreshSelectedComparison();
        vm.BeginRestoreConfirmation();
        vm.ConfirmRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Restored A");

        vm.BeginUndoPreview();
        var crashed = false;
        try { vm.ConfirmUndoRestore(); } catch (OperationCanceledException) { crashed = true; }
        Assert(crashed, "Moment 3 crash triggered");
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "Disk already has B");
        var mtime = File.GetLastWriteTimeUtc(cfg);

        File.SetAttributes(cfg, FileAttributes.ReadOnly);
        try
        {
            var reopen1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen1.CheckInterruptedOperationOnStartup();
            reopen1.Refresh();
            Assert(reopen1.ShowRecoveryCard && reopen1.RecoveryStatus == ConfigRecoveryStatus.UndoAlreadyApplied,
                "Moment 3 Reopen #1 reports actual UndoAlreadyApplied result");
            Assert(!reopen1.ShowUndoBar && !reopen1.CanUndoLastRestore, "Moment 3 Reopen #1 has no stale Undo bar");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.GetLastWriteTimeUtc(cfg) == mtime, "Reopen #1 zero disk write");

            var reopen2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen2.CheckInterruptedOperationOnStartup();
            reopen2.Refresh();
            Assert(!reopen2.HasRecoveryCard && !reopen2.ShowUndoBar, "Moment 3 Reopen #2 clean");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.GetLastWriteTimeUtc(cfg) == mtime, "Reopen #2 zero disk write");
        }
        finally
        {
            File.SetAttributes(cfg, FileAttributes.Normal);
        }
    }

    private static void Beta3InterruptedThenExternalEditToCAndOriginalCopyRecovery()
    {
        var (bytesA, bytesB, bytesC, _, shaB, shaC) = CreateLightDarkHighContrastFixtures();

        // Test BOTH restore_prepared + external C AND undo_prepared + external C, plus active deletion after interruption!
        foreach (var phase in new[] { "restore_interrupted_then_c", "undo_interrupted_then_c", "restore_interrupted_then_deleted" })
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var cfg = Path.Combine(dir.Path, "app", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllBytes(cfg, bytesA);

            var hooks = new DelegateHooks
            {
                OnBeforeSaveRestoreCompleted = phase.StartsWith("restore_", StringComparison.Ordinal)
                    ? (_, _) => throw new OperationCanceledException("Crash before restore_completed")
                    : null,
                OnAfterUndoPrepared = phase == "undo_interrupted_then_c"
                    ? (_, _) => throw new OperationCanceledException("Crash after undo_prepared")
                    : null
            };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh();
            vm.SelectCandidateFile(cfg);
            vm.CreateRestorePoint();

            File.WriteAllBytes(cfg, bytesB);
            vm.RefreshSelectedComparison();
            vm.BeginRestoreConfirmation();
            if (phase.StartsWith("restore_", StringComparison.Ordinal))
            {
                try { vm.ConfirmRestore(); } catch (OperationCanceledException) { }
            }
            else
            {
                vm.ConfirmRestore();
                vm.BeginUndoPreview();
                try { vm.ConfirmUndoRestore(); } catch (OperationCanceledException) { }
            }

            if (phase == "restore_interrupted_then_deleted")
                File.Delete(cfg);
            else
                File.WriteAllBytes(cfg, bytesC);

            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();

            var expectedStatus = phase == "restore_interrupted_then_deleted"
                ? ConfigRecoveryStatus.TargetMissingAfterInterruption
                : ConfigRecoveryStatus.ExternalVersionCDetected;
            Assert(reopen.ShowRecoveryCard && reopen.RecoveryStatus == expectedStatus,
                $"{phase}: expected {expectedStatus}, got {reopen.RecoveryStatus}");
            Assert(!reopen.CanUndoFromRecovery && !reopen.ShowUndoBar, $"{phase}: does NOT offer blind undo over C/deleted");
            Assert(reopen.CanViewOriginalCopyFromRecovery && reopen.CanExportOriginalCopyFromRecovery,
                $"{phase}: offers View & Save As original copy B");

            // Check C or Deleted state preserved untouched!
            if (phase == "restore_interrupted_then_deleted")
                Assert(!File.Exists(cfg), "Deleted file NOT recreated on startup");
            else
                Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaC, $"{phase}: C preserved byte-for-byte");

            // View original copy B inline!
            reopen.BeginViewOriginalCopy();
            Assert(reopen.IsViewingOriginalCopy && reopen.OriginalCopyPreview is { IsValid: true } && reopen.OriginalCopyPreview.Sha256 == shaB,
                $"{phase}: View original copy shows verified B");
            Assert(reopen.OriginalCopyLines.Any(l => l.Text.Contains("\"theme\": \"dark\"")),
                $"{phase}: Inline original copy preview displays dark (B)");

            // Exporting directly onto active target path MUST be refused to protect C!
            var blockedExport = reopen.ExportOriginalCopy(cfg);
            Assert(blockedExport is { Succeeded: false }, $"{phase}: Export refuses overwriting active target path");

            // Exporting to separate path writes exact B bytes (BOM + CRLF intact)!
            var exportDest = Path.Combine(dir.Path, "recovered", "settings.before-restore.json");
            var exported = reopen.ExportOriginalCopy(exportDest);
            Assert(exported is { Succeeded: true } && exported.Sha256 == shaB, $"{phase}: Export succeeded with verified shaB");
            Assert(File.ReadAllBytes(exportDest).SequenceEqual(bytesB), $"{phase}: Exported file equals exact bytes of B");
            if (phase != "restore_interrupted_then_deleted")
                Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaC, $"{phase}: Target C STILL untouched after export");

            reopen.CloseOriginalCopyPreview();
            Assert(reopen.ShowRecoveryCard, $"{phase}: Closing copy preview returns cleanly to recovery card");

            // Explicit archive dismisses alert without touching C or deleting B blob!
            reopen.DismissOrArchiveRecovery();
            Assert(!reopen.HasRecoveryCard, $"{phase}: Archived cleanly");
            Assert(Directory.GetFiles(Path.Combine(store, "Undo"), "*.bin").Length == 1, $"{phase}: B blob still retained safely on disk");
        }
    }

    private static void Beta3OriginallyMissingFileInterruptions()
    {
        var bytesA = EncodeUtf8Bom("[ui]\r\ntheme=light\r\n");
        var bytesC = EncodeUtf8Bom("[ui]\r\ntheme=custom-c\r\n");
        var shaA = Sha256Hex(bytesA);
        var shaC = Sha256Hex(bytesC);

        // (a) Moment 1 on originally missing file: crash before recreate -> still missing, no false restore!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var ini = Path.Combine(dir.Path, "cfg", "settings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
            File.WriteAllBytes(ini, bytesA);

            var hooks = new DelegateHooks { OnAfterRestorePrepared = (_, _) => throw new OperationCanceledException("Crash pre-recreate") };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh(); vm.SelectCandidateFile(ini); vm.CreateRestorePoint();
            File.Delete(ini);
            vm.RefreshSelectedComparison(); vm.BeginRestoreConfirmation();
            try { vm.ConfirmRestore(); } catch (OperationCanceledException) { }
            Assert(!File.Exists(ini), "Missing (a): still missing");

            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup(); reopen.Refresh();
            Assert(reopen.RecoveryStatus == ConfigRecoveryStatus.RestoreNotApplied && !reopen.ShowUndoBar,
                "Missing (a): RestoreNotApplied and no false undo");
        }

        // (b) Moment 2 + Moment 3 on originally missing file: recreated A -> crash -> reopen -> Undo Preview removes -> crash before mark -> reopen!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var ini = Path.Combine(dir.Path, "cfg", "settings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
            File.WriteAllBytes(ini, bytesA);

            var hooks1 = new DelegateHooks { OnBeforeSaveRestoreCompleted = (_, _) => throw new OperationCanceledException("Crash post-recreate") };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks1));
            vm.Refresh(); vm.SelectCandidateFile(ini); vm.CreateRestorePoint();
            File.Delete(ini);
            vm.RefreshSelectedComparison(); vm.BeginRestoreConfirmation();
            try { vm.ConfirmRestore(); } catch (OperationCanceledException) { }
            Assert(File.Exists(ini) && Sha256Hex(File.ReadAllBytes(ini)) == shaA, "Missing (b): recreated A");

            // Reopen with Undo-mark crash hook -> verify recovery -> Undo preview forecasts removal -> crash before consumed saved!
            var hooks2 = new DelegateHooks { OnAfterUndoTargetUpdatedBeforeMarkCompleted = (_, _) => throw new OperationCanceledException("Crash post-delete") };
            var reopen1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks2));
            reopen1.CheckInterruptedOperationOnStartup(); reopen1.Refresh();
            Assert(reopen1.RecoveryStatus == ConfigRecoveryStatus.RestoreAppliedNeedsReview && reopen1.CanUndoFromRecovery,
                "Missing (b): Verified recreated A on reopen");
            reopen1.BeginUndoPreviewFromRecovery();
            Assert(reopen1.IsUndoPreviewRemoveRecreated && reopen1.CanConfirmUndo, "Missing (b): Undo preview forecasts removal");
            try { reopen1.ConfirmUndoRestore(); } catch (OperationCanceledException) { }
            Assert(!File.Exists(ini), "Missing (b): Recreated file deleted");

            // Reopen #2 -> UndoAlreadyApplied (missing confirmed), Reopen #3 clean!
            var reopen2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen2.CheckInterruptedOperationOnStartup(); reopen2.Refresh();
            Assert(reopen2.RecoveryStatus == ConfigRecoveryStatus.UndoAlreadyApplied && !reopen2.ShowUndoBar,
                "Missing (c): Verified recreated file removal on reopen");
            Assert(!File.Exists(ini), "Missing (c): Still missing");

            var reopen3 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen3.CheckInterruptedOperationOnStartup(); reopen3.Refresh();
            Assert(!reopen3.HasRecoveryCard && !reopen3.ShowUndoBar && !File.Exists(ini), "Missing (c): 2nd reopen clean");
        }

        // (d) Originally missing file recreated, interrupted, THEN externally edited to C -> preserve C, NEVER auto-delete!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var ini = Path.Combine(dir.Path, "cfg", "settings.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(ini)!);
            File.WriteAllBytes(ini, bytesA);

            var hooks = new DelegateHooks { OnBeforeSaveRestoreCompleted = (_, _) => throw new OperationCanceledException("Crash post-recreate") };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh(); vm.SelectCandidateFile(ini); vm.CreateRestorePoint();
            File.Delete(ini);
            vm.RefreshSelectedComparison(); vm.BeginRestoreConfirmation();
            try { vm.ConfirmRestore(); } catch (OperationCanceledException) { }

            File.WriteAllBytes(ini, bytesC);
            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup(); reopen.Refresh();
            Assert(reopen.RecoveryStatus == ConfigRecoveryStatus.ExternalVersionCDetected && !reopen.CanUndoFromRecovery && !reopen.CanViewOriginalCopyFromRecovery,
                "Missing (d): Detects C on recreated file with no pre-restore copy");
            Assert(File.Exists(ini) && Sha256Hex(File.ReadAllBytes(ini)) == shaC, "Missing (d): C preserved untouched");
        }
    }

    private static void Beta3InsufficientEvidenceShowsNeedsVerification()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();

        // (a) Target locked exclusively (unreadable) during startup check -> "待核对", B still viewable; unlock -> Recheck resolves!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var cfg = Path.Combine(dir.Path, "app", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllBytes(cfg, bytesA);

            var hooks = new DelegateHooks { OnBeforeSaveRestoreCompleted = (_, _) => throw new OperationCanceledException("Crash") };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh(); vm.SelectCandidateFile(cfg); vm.CreateRestorePoint();
            File.WriteAllBytes(cfg, bytesB);
            vm.RefreshSelectedComparison(); vm.BeginRestoreConfirmation();
            try { vm.ConfirmRestore(); } catch (OperationCanceledException) { }

            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            using (var exclusiveLock = new FileStream(cfg, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                reopen.CheckInterruptedOperationOnStartup();
                reopen.Refresh();
                Assert(reopen.RecoveryStatus == ConfigRecoveryStatus.NeedsVerification && reopen.RecoveryStatusBadge == "待核对",
                    $"Locked target must show 待核对, got {reopen.RecoveryStatus}/{reopen.RecoveryStatusBadge}");
                Assert(reopen.CanViewOriginalCopyFromRecovery && reopen.CanRecheckRecovery,
                    "待核对 still allows viewing B copy and clicking Recheck");
            }
            reopen.RecheckRecovery();
            Assert(reopen.RecoveryStatus == ConfigRecoveryStatus.RestoreAppliedNeedsReview && reopen.CanUndoFromRecovery,
                "Recheck after unlock resolves to RestoreAppliedNeedsReview");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Target remained A throughout");
        }

        // (b) Corrupt / incomplete metadata -> "待核对", target untouched!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var undoDir = Path.Combine(store, "Undo");
            Directory.CreateDirectory(undoDir);
            File.WriteAllText(Path.Combine(undoDir, "broken0001.json"), "{ invalid-json", Utf8NoBom);

            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(reopen.ShowRecoveryCard && reopen.RecoveryStatus == ConfigRecoveryStatus.NeedsVerification && reopen.RecoveryStatusBadge == "待核对",
                "Corrupt undo metadata must display 待核对");
        }

        // (c) Target is A under restore_prepared, but B blob corrupted -> "待核对", cannot blind undo!
        {
            using var dir = new TempDir();
            var store = Path.Combine(dir.Path, "store");
            var cfg = Path.Combine(dir.Path, "app", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllBytes(cfg, bytesA);

            var hooks = new DelegateHooks { OnBeforeSaveRestoreCompleted = (_, _) => throw new OperationCanceledException("Crash") };
            var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks));
            vm.Refresh(); vm.SelectCandidateFile(cfg); vm.CreateRestorePoint();
            File.WriteAllBytes(cfg, bytesB);
            vm.RefreshSelectedComparison(); vm.BeginRestoreConfirmation();
            try { vm.ConfirmRestore(); } catch (OperationCanceledException) { }

            var blob = Directory.GetFiles(Path.Combine(store, "Undo"), "*.bin").Single();
            var tampered = File.ReadAllBytes(blob);
            tampered[0] ^= 0xFF;
            File.WriteAllBytes(blob, tampered);

            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(reopen.RecoveryStatus == ConfigRecoveryStatus.NeedsVerification && reopen.RecoveryStatusBadge == "待核对",
                "Corrupted B blob must display 待核对");
            Assert(!reopen.CanUndoFromRecovery && !reopen.CanViewOriginalCopyFromRecovery, "Corrupted B blob blocks undo & view");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Target A untouched");
        }
    }

    private static void Beta3BackwardCompatibilityWithBeta1AndBeta2()
    {
        var (bytesA, bytesB, bytesC, shaA, shaB, shaC) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "legacy-app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        Directory.CreateDirectory(Path.Combine(store, "Points"));
        Directory.CreateDirectory(Path.Combine(store, "Blobs"));
        Directory.CreateDirectory(Path.Combine(store, "Undo"));

        // Write EXACT Beta.1/Beta.2 JSON format (NO operation_state or Beta.3 fields!)
        File.WriteAllBytes(Path.Combine(store, "Blobs", "legacypt0001.bin"), bytesA);
        File.WriteAllText(Path.Combine(store, "Points", "legacypt0001.json"), $$"""
            {
              "schema_version": 1,
              "point_id": "legacypt0001",
              "created_at_utc": "2026-11-24T01:00:00+00:00",
              "note": "Beta.1/Beta.2 历史还原点",
              "target_path": {{System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(cfg))}},
              "target_file_name": "settings.json",
              "target_directory": {{System.Text.Json.JsonSerializer.Serialize(Path.GetDirectoryName(Path.GetFullPath(cfg))!)}},
              "parent_folder_name": "legacy-app",
              "byte_length": {{bytesA.LongLength}},
              "sha256": "{{shaA}}",
              "backup_blob_relative_path": "Blobs\\legacypt0001.bin",
              "encoding_label": "UTF-8（含 BOM）",
              "line_ending_label": "CRLF (Windows)"
            }
            """, Utf8NoBom);

        File.WriteAllBytes(Path.Combine(store, "Undo", "legacyun0001.bin"), bytesB);
        var legacyUndoJson = $$"""
            {
              "schema_version": 1,
              "undo_id": "legacyun0001",
              "created_at_utc": "2026-11-24T01:05:00+00:00",
              "restored_point_id": "legacypt0001",
              "restored_point_note": "Beta.1/Beta.2 历史还原点",
              "target_path": {{System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(cfg))}},
              "target_existed_before_restore": true,
              "pre_restore_byte_length": {{bytesB.LongLength}},
              "pre_restore_sha256": "{{shaB}}",
              "pre_restore_blob_relative_path": "Undo\\legacyun0001.bin",
              "restored_backup_sha256": "{{shaA}}",
              "consumed": false
            }
            """;
        var legacyUndoPath = Path.Combine(store, "Undo", "legacyun0001.json");
        File.WriteAllText(legacyUndoPath, legacyUndoJson, Utf8NoBom);

        // (a) Disk == C on legacy Beta.2 active record -> Undo conflict preview + View/Save As B!
        File.WriteAllBytes(cfg, bytesC);
        var vmC = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vmC.CheckInterruptedOperationOnStartup();
        vmC.Refresh();
        Assert(vmC.TotalCount == 1 && !vmC.HasRecoveryCard && vmC.CanUndoLastRestore, "Legacy active record loads seamlessly");
        vmC.BeginUndoPreview();
        Assert(vmC.IsUndoPreviewConflict && !vmC.CanConfirmUndo && vmC.CanViewOriginalCopyFromUndoPreview,
            "Legacy record on C blocks undo and enables View/Save As original copy B");
        vmC.BeginViewOriginalCopy();
        Assert(vmC.IsViewingOriginalCopy && vmC.OriginalCopyPreview is { IsValid: true } && vmC.OriginalCopyPreview.Sha256 == shaB,
            "Can inspect B from legacy Beta.2 undo conflict");
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaC, "C untouched");

        // (b) Disk == B on legacy Beta.2 unclosed record (legacy Boundary 2 / Moment 1) -> 待核对, NO broken Undo bar!
        File.WriteAllBytes(cfg, bytesB);
        var vmB = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vmB.CheckInterruptedOperationOnStartup();
        vmB.Refresh();
        Assert(vmB.ShowRecoveryCard && vmB.RecoveryStatus == ConfigRecoveryStatus.NeedsVerification && vmB.RecoveryStatusBadge == "待核对",
            "Legacy unclosed record with disk==B surfaces 待核对");
        Assert(!vmB.ShowUndoBar && !vmB.CanUndoLastRestore, "Legacy unclosed record with disk==B hides broken undo bar");

        // (c) Disk == A on legacy Beta.2 active record -> normal active undo works and restores B!
        File.WriteAllBytes(cfg, bytesA);
        var vmA = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vmA.CheckInterruptedOperationOnStartup();
        vmA.Refresh();
        Assert(!vmA.HasRecoveryCard && vmA.CanUndoLastRestore, "Legacy normal active record needs no recovery alarm");
        vmA.BeginUndoPreview();
        Assert(vmA.CanConfirmUndo, "Legacy undo preview ready");
        vmA.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.ReadAllBytes(cfg).SequenceEqual(bytesB),
            "Legacy Beta.1/Beta.2 undo cleanly restores B byte-for-byte");
    }

    private static void Rc1DualWindowLiveRestorePrepared()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var setupVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        setupVm.Refresh();
        setupVm.SelectCandidateFile(cfg);
        setupVm.CreateRestorePoint();
        var point = setupVm.SelectedPoint!;
        File.WriteAllBytes(cfg, bytesB);

        using var paused = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var hooks1 = new DelegateHooks
        {
            OnAfterRestorePrepared = (_, _) => { paused.Set(); resume.Wait(TimeSpan.FromSeconds(15)); }
        };
        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks1));
        vm1.Refresh();
        vm1.BeginRestoreConfirmation();
        var w1Task = Task.Run(() => vm1.ConfirmRestore());
        Assert(paused.Wait(TimeSpan.FromSeconds(10)), "Window 1 reached restore_prepared pause");

        try
        {
            var svc2 = new LocalConfigRestorePointService(store);
            var vm2 = new ConfigRestorePointsViewModel(svc2);
            vm2.CheckInterruptedOperationOnStartup();
            vm2.Refresh();

            // Window 2 can browse read-only, sees ActiveInAnotherWindow, does NOT hijack or write!
            Assert(vm2.TotalCount == 1 && vm2.HasSelectedPoint, "Window 2 can browse restore points while Window 1 writes");
            Assert(vm2.ShowRecoveryCard && vm2.RecoveryStatus == ConfigRecoveryStatus.ActiveInAnotherWindow,
                $"Window 2 must report ActiveInAnotherWindow, got {vm2.RecoveryStatus}");
            Assert(vm2.RecoveryStatusBadge == "其他窗口正在操作", $"Unexpected badge: {vm2.RecoveryStatusBadge}");
            Assert(!vm2.CanBeginRestore && !vm2.ShowUndoBar && !vm2.CanUndoFromRecovery, "Window 2 blocks conflicting write/undo");

            // Even direct service call (stale preview racing) MUST return BusyInAnotherWindow and touch nothing!
            var directRes = svc2.RestoreFromPoint(point, vm2.Comparison!.LiveSnapshot);
            Assert(directRes.Outcome == ConfigRestoreOutcomeKind.BusyInAnotherWindow,
                $"Expected BusyInAnotherWindow, got {directRes.Outcome}");

            // Disk is still B, single undo record is still restore_prepared & unconsumed, B blob intact!
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "Target still B while Window 1 paused");
            var undoJsons = Directory.GetFiles(Path.Combine(store, "Undo"), "*.json");
            Assert(undoJsons.Length == 1, $"Expected exactly 1 undo record, got {undoJsons.Length}");
            var rawJson = File.ReadAllText(undoJsons[0], Utf8NoBom);
            Assert(rawJson.Contains("\"restore_prepared\"") && rawJson.Contains("\"consumed\": false"),
                "Window 2 MUST NOT mark Window 1's restore_prepared record as aborted/completed/consumed");
            var undoBlobs = Directory.GetFiles(Path.Combine(store, "Undo"), "*.bin");
            Assert(undoBlobs.Length == 1 && Sha256Hex(File.ReadAllBytes(undoBlobs[0])) == shaB, "B copy untouched");
        }
        finally
        {
            resume.Set();
            Assert(w1Task.Wait(TimeSpan.FromSeconds(15)), "Window 1 finished cleanly after resume");
        }

        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Window 1 finished restoring A");
        Assert(Directory.GetFiles(Path.Combine(store, "Undo"), "*.json").Length == 1, "Still strictly 1 undo record");

        // Consecutive two reopens: target A, 1 active undo, B intact!
        for (var i = 0; i < 2; i++)
        {
            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(!reopen.HasRecoveryCard && reopen.CanUndoLastRestore && reopen.ComparisonStatus == ConfigComparisonStatus.Identical,
                $"Reopen #{i + 1}: clean completed restore with active undo");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, $"Reopen #{i + 1}: disk remains A");
        }
    }

    private static void Rc1DualWindowLiveUndoPrepared()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var setupVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        setupVm.Refresh();
        setupVm.SelectCandidateFile(cfg);
        setupVm.CreateRestorePoint();
        File.WriteAllBytes(cfg, bytesB);
        setupVm.RefreshSelectedComparison();
        setupVm.BeginRestoreConfirmation();
        setupVm.ConfirmRestore();
        var activeUndoBefore = setupVm.LatestUndo!;
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Restored A before undo concurrency check");

        using var paused = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var hooks1 = new DelegateHooks
        {
            OnAfterUndoPrepared = (_, _) => { paused.Set(); resume.Wait(TimeSpan.FromSeconds(15)); }
        };
        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks1));
        vm1.Refresh();
        vm1.BeginUndoPreview();
        var w1Task = Task.Run(() => vm1.ConfirmUndoRestore());
        Assert(paused.Wait(TimeSpan.FromSeconds(10)), "Window 1 reached undo_prepared pause");

        try
        {
            var svc2 = new LocalConfigRestorePointService(store);
            var vm2 = new ConfigRestorePointsViewModel(svc2);
            vm2.CheckInterruptedOperationOnStartup();
            vm2.Refresh();

            Assert(vm2.ShowRecoveryCard && vm2.RecoveryStatus == ConfigRecoveryStatus.ActiveInAnotherWindow,
                $"Window 2 must detect ActiveInAnotherWindow on undo_prepared, got {vm2.RecoveryStatus}");
            Assert(!vm2.ShowUndoBar && !vm2.CanUndoFromRecovery && !vm2.CanBeginRestore, "Window 2 blocks concurrent undo/restore");

            var preview2 = svc2.InspectUndoPreview(activeUndoBefore);
            Assert(preview2.Status == ConfigUndoPreviewStatus.BusyInAnotherWindow && !preview2.CanConfirm,
                $"InspectUndoPreview must report BusyInAnotherWindow, got {preview2.Status}");
            var undoRes2 = svc2.UndoRestore(activeUndoBefore);
            Assert(undoRes2.Outcome == ConfigUndoOutcomeKind.BusyInAnotherWindow,
                $"UndoRestore must return BusyInAnotherWindow, got {undoRes2.Outcome}");

            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Disk remains A while Window 1 paused");
            var undoFile = Directory.GetFiles(Path.Combine(store, "Undo"), "*.json").Single();
            var rawJson = File.ReadAllText(undoFile, Utf8NoBom);
            Assert(rawJson.Contains("\"undo_prepared\"") && rawJson.Contains("\"consumed\": false"),
                "Window 2 MUST NOT revert or consume Window 1's undo_prepared record");
        }
        finally
        {
            resume.Set();
            Assert(w1Task.Wait(TimeSpan.FromSeconds(15)), "Window 1 completed undo cleanly");
        }

        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "Window 1 wrote B back cleanly");
        var mtime = File.GetLastWriteTimeUtc(cfg);

        // Two consecutive reopens: disk B untouched, no recovery card, no undo bar!
        for (var i = 0; i < 2; i++)
        {
            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(!reopen.HasRecoveryCard && !reopen.ShowUndoBar && !reopen.CanUndoLastRestore,
                $"Undo reopen #{i + 1}: clean state");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.GetLastWriteTimeUtc(cfg) == mtime,
                $"Undo reopen #{i + 1}: zero rewrites");
        }
    }

    private static void Rc1StalePreviewsRereadAfterWindow1Completes()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vm1.Refresh();
        vm1.SelectCandidateFile(cfg);
        vm1.CreateRestorePoint();
        File.WriteAllBytes(cfg, bytesB);

        // Sub-case A: Both windows open Restore Preview while disk == B; Window 1 completes first!
        vm1.RefreshSelectedComparison();
        vm1.BeginRestoreConfirmation();
        var vm2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vm2.Refresh();
        vm2.BeginRestoreConfirmation();

        vm1.ConfirmRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Window 1 restored A");
        var mtimeAfterW1Restore = File.GetLastWriteTimeUtc(cfg);

        // Window 2 confirms its STALE restore preview -> MUST re-read target & records, write NOTHING, create NO duplicate undo!
        vm2.ConfirmRestore();
        Assert(!vm2.IsConfirmingRestore && vm2.ComparisonStatus == ConfigComparisonStatus.Identical, "Window 2 refreshed to Identical");
        Assert(vm2.CanUndoLastRestore, "Window 2 picked up Window 1's active undo record");
        Assert(File.GetLastWriteTimeUtc(cfg) == mtimeAfterW1Restore && Sha256Hex(File.ReadAllBytes(cfg)) == shaA,
            "Window 2 stale restore preview did NOT rewrite disk");
        Assert(Directory.GetFiles(Path.Combine(store, "Undo"), "*.json").Length == 1,
            "Window 2 did NOT create duplicate undo record");

        // Sub-case B: Both windows open Undo Preview while disk == A; Window 1 completes undo first!
        vm1.BeginUndoPreview();
        vm2.BeginUndoPreview();
        Assert(vm1.CanConfirmUndo && vm2.CanConfirmUndo, "Both windows hold undo previews");

        vm1.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB, "Window 1 completed undo to B");
        var mtimeAfterW1Undo = File.GetLastWriteTimeUtc(cfg);

        // Window 2 confirms its STALE undo preview -> MUST re-read Undo/<id>.json, see consumed=true, NOT rewrite disk!
        vm2.ConfirmUndoRestore();
        Assert(!vm2.IsPreviewingUndo && !vm2.CanUndoLastRestore, "Window 2 closed stale undo preview cleanly");
        Assert(vm2.OperationBannerHeadline.Contains("操作记录已更新"), $"Expected stale handled banner, got: {vm2.OperationBannerHeadline}");
        Assert(File.GetLastWriteTimeUtc(cfg) == mtimeAfterW1Undo && Sha256Hex(File.ReadAllBytes(cfg)) == shaB,
            "Window 2 stale undo preview did NOT touch disk");
    }

    private static void Rc1OwnerKilledTakeoverAndTwoReopens()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var setup = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        setup.Refresh();
        setup.SelectCandidateFile(cfg);
        setup.CreateRestorePoint();
        File.WriteAllBytes(cfg, bytesB);

        using var paused = new ManualResetEventSlim(false);
        using var killNow = new ManualResetEventSlim(false);
        var hooks1 = new DelegateHooks
        {
            OnAfterTargetReplacedBeforeVerify = (_, _) =>
            {
                paused.Set();
                killNow.Wait(TimeSpan.FromSeconds(15));
                throw new OperationCanceledException("Simulated owner termination releasing OS lock");
            }
        };
        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks1));
        vm1.Refresh();
        vm1.BeginRestoreConfirmation();
        var w1Task = Task.Run(() => { try { vm1.ConfirmRestore(); } catch (OperationCanceledException) { } });
        Assert(paused.Wait(TimeSpan.FromSeconds(10)), "Window 1 paused post-replace while alive");

        var vm2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vm2.CheckInterruptedOperationOnStartup();
        vm2.Refresh();
        Assert(vm2.RecoveryStatus == ConfigRecoveryStatus.ActiveInAnotherWindow, "While Window 1 alive, Window 2 reports ActiveInAnotherWindow");

        // Now terminate Window 1 -> OS lock releases -> Window 2 rechecks and seamlessly takes over Beta.3 read-only reconciliation!
        killNow.Set();
        Assert(w1Task.Wait(TimeSpan.FromSeconds(15)), "Window 1 terminated");
        var mtimeA = File.GetLastWriteTimeUtc(cfg);

        vm2.RecheckRecovery();
        Assert(vm2.RecoveryStatus == ConfigRecoveryStatus.RestoreAppliedNeedsReview && vm2.CanUndoFromRecovery,
            $"After Window 1 died, Window 2 takes over reconciliation, got {vm2.RecoveryStatus}");
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA && File.GetLastWriteTimeUtc(cfg) == mtimeA,
            "Takeover reconciliation remained strictly read-only on target");

        // Two consecutive reopens after takeover -> target A untouched, undo available, B intact!
        for (var i = 0; i < 2; i++)
        {
            var reopen = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            reopen.CheckInterruptedOperationOnStartup();
            reopen.Refresh();
            Assert(!reopen.HasRecoveryCard && reopen.CanUndoLastRestore, $"Post-takeover reopen #{i + 1}: undo ready");
            Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA && File.GetLastWriteTimeUtc(cfg) == mtimeA, $"Post-takeover reopen #{i + 1}: disk A untouched");
            if (i == 1)
            {
                reopen.BeginUndoPreview();
                reopen.ConfirmUndoRestore();
                Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.ReadAllBytes(cfg).SequenceEqual(bytesB),
                    "Post-takeover undo restores exact bytes of B");
            }
        }
    }

    private static void Rc1DifferentTargetFilesIndependent()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfgAlpha = Path.Combine(dir.Path, "alpha", "settings.json");
        var cfgBeta = Path.Combine(dir.Path, "beta", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfgAlpha)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cfgBeta)!);
        File.WriteAllBytes(cfgAlpha, bytesA);
        File.WriteAllBytes(cfgBeta, bytesA);

        var setup = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        setup.Refresh();
        setup.SelectCandidateFile(cfgAlpha); setup.CreateRestorePoint();
        var ptAlpha = setup.SelectedPoint!;
        setup.SelectCandidateFile(cfgBeta); setup.CreateRestorePoint();
        var ptBeta = setup.SelectedPoint!;
        File.WriteAllBytes(cfgAlpha, bytesB);
        File.WriteAllBytes(cfgBeta, bytesB);

        using var paused = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var hooks1 = new DelegateHooks { OnAfterRestorePrepared = (_, _) => { paused.Set(); resume.Wait(TimeSpan.FromSeconds(15)); } };
        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store, hooks1));
        vm1.Refresh();
        vm1.SelectPoint(vm1.FilteredPoints.Single(p => p.PointId == ptAlpha.PointId));
        vm1.BeginRestoreConfirmation();
        var w1Task = Task.Run(() => vm1.ConfirmRestore());
        Assert(paused.Wait(TimeSpan.FromSeconds(10)), "Window 1 locked alpha");

        try
        {
            var vm2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
            vm2.Refresh();
            vm2.SelectPoint(vm2.FilteredPoints.Single(p => p.PointId == ptBeta.PointId));
            Assert(vm2.CanBeginRestore, "Beta must NOT be blocked while Alpha is locked");
            vm2.BeginRestoreConfirmation();
            vm2.ConfirmRestore();
            Assert(Sha256Hex(File.ReadAllBytes(cfgBeta)) == shaA, "Beta restored independently");
            Assert(Sha256Hex(File.ReadAllBytes(cfgAlpha)) == shaB, "Alpha untouched by Beta operation");
        }
        finally
        {
            resume.Set();
            Assert(w1Task.Wait(TimeSpan.FromSeconds(15)), "Window 1 finished alpha cleanly");
        }
        Assert(Sha256Hex(File.ReadAllBytes(cfgAlpha)) == shaA, "Alpha finished restoring A");
    }

    private static void Rc1CompletedRestoreByAnotherLivePidRemainsUndoable()
    {
        // Regression test for real multi-process case: Window 1 finishes restore (restore_completed, lock released)
        // and stays open on screen; Window 2 (different OS PID) MUST still see the Undo bar and be able to Undo!
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var store = Path.Combine(dir.Path, "store");
        var cfg = Path.Combine(dir.Path, "app", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        File.WriteAllBytes(cfg, bytesA);

        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vm1.Refresh();
        vm1.SelectCandidateFile(cfg);
        vm1.CreateRestorePoint();
        File.WriteAllBytes(cfg, bytesB);
        vm1.RefreshSelectedComparison();
        vm1.BeginRestoreConfirmation();
        vm1.ConfirmRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaA, "Window 1 restored A");

        // Stamp undo record with another genuinely alive OS process (e.g. a sibling live process or PID != current)
        // while NO active write lock is held (Window 1 finished its write and stayed open).
        var otherLive = System.Diagnostics.Process.GetProcesses()
            .FirstOrDefault(p =>
            {
                try { return p.Id != Environment.ProcessId && !p.HasExited && p.StartTime.Year >= 2000; }
                catch { return false; }
            });
        var undoPath = Directory.GetFiles(Path.Combine(store, "Undo"), "*.json").Single();
        if (otherLive is not null)
        {
            var raw = File.ReadAllText(undoPath, Utf8NoBom);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "\"owner_pid\"\\s*:\\s*\\d+", $"\"owner_pid\": {otherLive.Id}");
            raw = System.Text.RegularExpressions.Regex.Replace(raw, "\"owner_process_start_utc_ticks\"\\s*:\\s*\\d+", $"\"owner_process_start_utc_ticks\": {otherLive.StartTime.ToUniversalTime().Ticks}");
            File.WriteAllText(undoPath, raw, Utf8NoBom);
        }

        var vm2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(store));
        vm2.CheckInterruptedOperationOnStartup();
        vm2.Refresh();
        Assert(!vm2.HasRecoveryCard && vm2.CanUndoLastRestore,
            "Window 2 must still see Undo bar after Window 1 finished restore and stayed open");
        vm2.BeginUndoPreview();
        Assert(vm2.IsPreviewingUndo && vm2.CanConfirmUndo, "Window 2 undo preview must NOT be false-blocked");
        vm2.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(cfg)) == shaB && File.ReadAllBytes(cfg).SequenceEqual(bytesB),
            "Window 2 cleanly undoes completed restore from Window 1");
    }

    private static void V080ExportSavedBackupAAfterSourceChangedAndDeleted()
    {
        var (bytesA, _, bytesC, shaA, _, shaC) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var sourceStore = Path.Combine(dir.Path, "source-store");
        var sourceDir = Path.Combine(dir.Path, "SecretSourceMachineFolder", "EditorProfile");
        Directory.CreateDirectory(sourceDir);
        var sourceCfg = Path.Combine(sourceDir, "settings.json");
        File.WriteAllBytes(sourceCfg, bytesA);

        var vm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(sourceStore));
        vm.Refresh();
        vm.SelectCandidateFile(sourceCfg);
        vm.NoteInput = "旧电脑迁移基线";
        vm.CreateRestorePoint();
        var sourcePointId = vm.SelectedPoint!.PointId;

        // Modify source disk file to C!
        File.WriteAllBytes(sourceCfg, bytesC);
        vm.RefreshSelectedComparison();
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Changed && vm.CanBeginExportRestorePoint,
            "Export must be enabled even when source file changed to C");

        // Open export card and verify version identity & privacy reminder
        vm.BeginExportRestorePoint();
        Assert(vm.ShowExportPackageCard, "Export card visible");
        Assert(vm.ExportPackagePrivacyNotice.Contains("令牌") && vm.ExportPackagePrivacyNotice.Contains("原始字节"),
            "Privacy reminder mentions tokens and raw bytes");
        Assert(vm.ExportPackageVersionSummary.Contains("备份版本"), "Export summary clarifies saved backup A");

        // Refuse overwriting the source config file directly
        var blockedRes = vm.ExportRestorePointPackage(sourceCfg);
        Assert(blockedRes is { Succeeded: false }, "Export refuses overwriting source config directly");
        Assert(Sha256Hex(File.ReadAllBytes(sourceCfg)) == shaC, "Source disk remains C after blocked export");

        // Export package #1 while source disk is C
        var pkg1Path = Path.Combine(dir.Path, "exports", "settings.json.contain-config-point");
        vm.SelectExportPackagePath(pkg1Path);
        Assert(vm.CanConfirmExportPackage, "Confirm export enabled once destination chosen");
        var exp1 = vm.ConfirmExportRestorePoint();
        Assert(exp1 is { Succeeded: true } && exp1.Sha256 == shaA && exp1.ByteLength == bytesA.LongLength,
            "Exported package #1 must contain exact saved backup A");
        Assert(Sha256Hex(File.ReadAllBytes(sourceCfg)) == shaC, "Source disk remains C");

        // Inspect raw package JSON: MUST contain zero source directory paths, zero source point_id, zero undo records!
        var rawPkgJson = File.ReadAllText(pkg1Path, Utf8NoBom);
        Assert(!rawPkgJson.Contains("SecretSourceMachineFolder", StringComparison.OrdinalIgnoreCase), "Package must not leak source path");
        Assert(!rawPkgJson.Contains("EditorProfile", StringComparison.OrdinalIgnoreCase), "Package must not leak source parent folder");
        Assert(!rawPkgJson.Contains(sourcePointId, StringComparison.OrdinalIgnoreCase), "Package must not leak source point_id");
        Assert(!rawPkgJson.Contains("undo_id", StringComparison.OrdinalIgnoreCase), "Package must not leak undo metadata");

        // Delete source file completely -> MUST STILL export exact saved backup A!
        File.Delete(sourceCfg);
        vm.RefreshSelectedComparison();
        Assert(vm.ComparisonStatus == ConfigComparisonStatus.Missing && vm.CanBeginExportRestorePoint,
            "Export remains available even after source file is deleted");
        var pkg2Path = Path.Combine(dir.Path, "exports", "after-delete.contain-config-point");
        vm.BeginExportRestorePoint();
        vm.SelectExportPackagePath(pkg2Path);
        var exp2 = vm.ConfirmExportRestorePoint();
        Assert(exp2 is { Succeeded: true } && exp2.Sha256 == shaA && !File.Exists(sourceCfg),
            "Export after deletion still writes exact A without recreating source file");
    }

    private static void V080ImportDifferentPathPreviewCancelRestoreUndo()
    {
        var (bytesA, bytesB, bytesC, shaA, shaB, shaC) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var srcStore = Path.Combine(dir.Path, "src-store");
        var dstStore = Path.Combine(dir.Path, "dst-store");
        var srcCfg = Path.Combine(dir.Path, "old-pc", "settings.json");
        var dstCfg = Path.Combine(dir.Path, "new-pc", "custom-name-config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(srcCfg)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstCfg)!);
        File.WriteAllBytes(srcCfg, bytesA);
        File.WriteAllBytes(dstCfg, bytesB);

        // Source PC: create point A, change disk to C, export package
        var srcVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(srcStore));
        srcVm.Refresh();
        srcVm.SelectCandidateFile(srcCfg);
        srcVm.NoteInput = "旧电脑主题配置";
        srcVm.CreateRestorePoint();
        var srcPointId = srcVm.SelectedPoint!.PointId;
        File.WriteAllBytes(srcCfg, bytesC);
        var pkgPath = Path.Combine(dir.Path, "transfer", "migrated.contain-config-point");
        var exp = srcVm.ExportRestorePointPackage(pkgPath);
        Assert(exp is { Succeeded: true }, "Source exported package");

        // Receiver PC: independent store, different target path & different filename!
        var dstVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(dstStore));
        dstVm.Refresh();
        Assert(dstVm.TotalCount == 0, "Receiver starts with 0 points");
        dstVm.OpenImportWorkspace();
        Assert(dstVm.ShowImportPanel && !dstVm.ShowCreateCard, "Import panel open and top create card hidden");

        var dstMtimeBefore = File.GetLastWriteTimeUtc(dstCfg);
        dstVm.OpenImportPackage(pkgPath);
        Assert(dstVm.HasValidImportedPackage && dstVm.ImportedPackageDisplayFileName == "settings.json",
            "Package validated with display filename settings.json");
        Assert(File.ReadAllBytes(dstCfg).SequenceEqual(bytesB), "Opening package does not touch receiver target");

        dstVm.SelectImportTargetPath(dstCfg);
        Assert(dstVm.HasImportPreview && dstVm.IsImportReadyToOverwrite && dstVm.CanConfirmImportRestore,
            "Preview ready to overwrite receiver target");
        Assert(dstVm.ImportPreviewDirectionLabel == "本机当前 → 恢复后", "Import preview direction label");
        Assert(dstVm.ImportPreviewTargetPath == Path.GetFullPath(dstCfg), "Import preview shows receiver full path");
        Assert(dstVm.ImportDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"dark\"")),
            "Import preview removes receiver dark");
        Assert(dstVm.ImportDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"light\"")),
            "Import preview writes package light");

        // Cancel MUST NOT write target, MUST NOT create local point or undo record!
        dstVm.CancelImport();
        Assert(!dstVm.ShowImportPanel && dstVm.TotalCount == 0 && !dstVm.CanUndoLastRestore, "Cancel leaves receiver clean");
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaB && File.GetLastWriteTimeUtc(dstCfg) == dstMtimeBefore,
            "Cancel leaves receiver file B untouched");

        // Reopen and Confirm Restore
        dstVm.OpenImportWorkspace();
        dstVm.OpenImportPackage(pkgPath);
        dstVm.SelectImportTargetPath(dstCfg);
        var res = dstVm.ConfirmImportRestore();
        Assert(res is { Outcome: ConfigRestoreOutcomeKind.Succeeded, ImportedPoint: not null, UndoSnapshot: not null },
            "Import restore succeeded");
        Assert(res!.ImportedPoint!.PointId != srcPointId, "Receiver creates brand-new local PointId");
        Assert(res.ImportedPoint.TargetPath == Path.GetFullPath(dstCfg), "Receiver point binds strictly to receiver path");
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaA && File.ReadAllBytes(dstCfg).SequenceEqual(bytesA),
            "Receiver target now has exact raw bytes A (BOM + CRLF preserved)");
        Assert(Sha256Hex(File.ReadAllBytes(srcCfg)) == shaC, "Source disk remains C");
        Assert(!dstVm.ShowImportPanel && dstVm.TotalCount == 1 && dstVm.CanUndoLastRestore,
            "Import panel closes, new local point selected, and undo bar ready");

        // Undo preview & confirm -> restores receiver's exact B!
        dstVm.BeginUndoPreview();
        Assert(dstVm.IsPreviewingUndo && dstVm.CanConfirmUndo && dstVm.UndoPreviewTargetPath == Path.GetFullPath(dstCfg),
            "Undo preview targets receiver path");
        Assert(dstVm.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"light\"")),
            "Undo removes A (light)");
        Assert(dstVm.UndoDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Added && l.Text.Contains("\"theme\": \"dark\"")),
            "Undo writes back B (dark)");
        dstVm.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaB && File.ReadAllBytes(dstCfg).SequenceEqual(bytesB),
            "Undo restored receiver's original B byte-for-byte");
        Assert(!dstVm.CanUndoLastRestore, "Undo consumed after restoring B");
    }

    private static void V080ImportMissingTargetCreateAndUndoRemoves()
    {
        var (bytesA, _, _, shaA, _, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var srcStore = Path.Combine(dir.Path, "src-store");
        var dstStore = Path.Combine(dir.Path, "dst-store");
        var srcCfg = Path.Combine(dir.Path, "src", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(srcCfg)!);
        File.WriteAllBytes(srcCfg, bytesA);

        var srcService = new LocalConfigRestorePointService(srcStore);
        var pt = srcService.CreateRestorePoint(srcCfg, "新建目标测试");
        var pkgPath = Path.Combine(dir.Path, "pkg.contain-config-point");
        Assert(srcService.ExportRestorePointPackage(pt, pkgPath).Succeeded, "Exported package");

        var missingTarget = Path.Combine(dir.Path, "receiver-new-dir", "nested", "new-settings.json");
        Assert(!File.Exists(missingTarget), "Target starts missing");

        var dstVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(dstStore));
        dstVm.Refresh();
        dstVm.OpenImportWorkspace();
        dstVm.OpenImportPackage(pkgPath);
        dstVm.SelectImportTargetPath(missingTarget);

        Assert(dstVm.HasImportPreview && dstVm.IsImportReadyToCreate && dstVm.CanConfirmImportRestore,
            "Missing target preview ready to create");
        Assert(dstVm.ImportPreviewTargetPath == Path.GetFullPath(missingTarget), "Full target path shown");
        Assert(dstVm.ImportDiffFallbackNote.Contains(Path.GetFullPath(missingTarget), StringComparison.OrdinalIgnoreCase),
            "Creation preview explicitly shows full path to be created");

        // Cancel must NOT create file or directory
        dstVm.CancelImport();
        Assert(!File.Exists(missingTarget), "Cancel did not create missing target");

        // Confirm creates missing target with exact A
        dstVm.OpenImportWorkspace();
        dstVm.OpenImportPackage(pkgPath);
        dstVm.SelectImportTargetPath(missingTarget);
        var res = dstVm.ConfirmImportRestore();
        Assert(res is { Outcome: ConfigRestoreOutcomeKind.Succeeded }, "Missing target created");
        Assert(File.Exists(missingTarget) && Sha256Hex(File.ReadAllBytes(missingTarget)) == shaA && File.ReadAllBytes(missingTarget).SequenceEqual(bytesA),
            "Created file matches exact bytes A");

        // Undo preview MUST forecast removing the newly created file, and Confirm removes it!
        dstVm.BeginUndoPreview();
        Assert(dstVm.IsPreviewingUndo && dstVm.IsUndoPreviewRemoveRecreated && dstVm.CanConfirmUndo,
            "Undo preview forecasts removal of newly created file");
        dstVm.ConfirmUndoRestore();
        Assert(!File.Exists(missingTarget), "Undo removed the newly created file cleanly");
    }

    private static void V080ImportRejectsMalformedPackages()
    {
        var (bytesA, bytesB, _, _, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var srcStore = Path.Combine(dir.Path, "src");
        var dstStore = Path.Combine(dir.Path, "dst");
        var srcCfg = Path.Combine(dir.Path, "src-cfg", "settings.json");
        var dstCfg = Path.Combine(dir.Path, "dst-cfg", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(srcCfg)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstCfg)!);
        File.WriteAllBytes(srcCfg, bytesA);
        File.WriteAllBytes(dstCfg, bytesB);

        var srcSvc = new LocalConfigRestorePointService(srcStore);
        var pt = srcSvc.CreateRestorePoint(srcCfg, "base");
        var goodPkg = Path.Combine(dir.Path, "good.contain-config-point");
        Assert(srcSvc.ExportRestorePointPackage(pt, goodPkg).Succeeded, "Good package created");
        var goodJson = File.ReadAllText(goodPkg, Utf8NoBom);

        var cases = new List<(string Name, string Content)>
        {
            ("empty.contain-config-point", ""),
            ("unknown-plain.contain-config-point", "not json at all"),
            ("unknown-format.contain-config-point", goodJson.Replace(ConfigPortablePackageConstants.FormatId, "other-tool-package")),
            ("unsupported-version.contain-config-point", goodJson.Replace("\"schema_version\": 1", "\"schema_version\": 99")),
            ("bad-base64.contain-config-point", System.Text.RegularExpressions.Regex.Replace(goodJson, "\"payload_base64\": \"[^\"]+\"", "\"payload_base64\": \"@@@not-base64@@@\"")),
            ("length-mismatch.contain-config-point", System.Text.RegularExpressions.Regex.Replace(goodJson, "\"byte_length\": \\d+", $"\"byte_length\": {bytesA.LongLength + 17}")),
            ("sha-tamper.contain-config-point", System.Text.RegularExpressions.Regex.Replace(goodJson, "\"sha256\": \"[0-9a-f]+\"", "\"sha256\": \"0000000000000000000000000000000000000000000000000000000000000000\"")),
            ("traversal-name.contain-config-point", goodJson.Replace("\"display_file_name\": \"settings.json\"", "\"display_file_name\": \"..\\\\evil.json\""))
        };

        var dstVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(dstStore));
        dstVm.Refresh();
        dstVm.OpenImportWorkspace();

        foreach (var (name, content) in cases)
        {
            var badPath = Path.Combine(dir.Path, name);
            File.WriteAllText(badPath, content, Utf8NoBom);
            dstVm.OpenImportPackage(badPath);
            Assert(!dstVm.HasValidImportedPackage && dstVm.HasInvalidImportedPackage && !string.IsNullOrWhiteSpace(dstVm.ImportedPackageErrorMessage),
                $"Expected invalid package rejected for {name}");
            Assert(!dstVm.CanConfirmImportRestore, $"Confirm must be disabled for {name}");
            Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaB, $"Target untouched after rejecting {name}");
        }
    }

    private static void V080ImportMidConfirmationExternalEdit()
    {
        var (bytesA, bytesB, bytesB2, shaA, _, shaB2) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var srcStore = Path.Combine(dir.Path, "src");
        var dstStore = Path.Combine(dir.Path, "dst");
        var srcCfg = Path.Combine(dir.Path, "src-cfg", "settings.json");
        var dstCfg = Path.Combine(dir.Path, "dst-cfg", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(srcCfg)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstCfg)!);
        File.WriteAllBytes(srcCfg, bytesA);
        File.WriteAllBytes(dstCfg, bytesB);

        var srcSvc = new LocalConfigRestorePointService(srcStore);
        var pt = srcSvc.CreateRestorePoint(srcCfg, "mid-edit-test");
        var pkgPath = Path.Combine(dir.Path, "pkg.contain-config-point");
        Assert(srcSvc.ExportRestorePointPackage(pt, pkgPath).Succeeded, "Exported package");

        var dstVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(dstStore));
        dstVm.Refresh();
        dstVm.OpenImportWorkspace();
        dstVm.OpenImportPackage(pkgPath);
        dstVm.SelectImportTargetPath(dstCfg);
        Assert(dstVm.CanConfirmImportRestore, "Initial preview ready against B");

        // External edit changes receiver file B -> B2 before user clicks Confirm!
        File.WriteAllBytes(dstCfg, bytesB2);

        var firstConfirm = dstVm.ConfirmImportRestore();
        Assert(firstConfirm is { Outcome: ConfigRestoreOutcomeKind.RefreshedDueToExternalEdit },
            $"Expected RefreshedDueToExternalEdit, got {firstConfirm?.Outcome}");
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaB2, "Mid-confirm external edit B2 MUST be preserved");
        Assert(dstVm.ShowImportPanel && dstVm.HasImportMidConfirmWarning && dstVm.CanConfirmImportRestore,
            "Import panel stays open with refreshed diff and warning");
        Assert(dstVm.ImportDiffLines.Any(l => l.Kind == ConfigDiffLineKind.Removed && l.Text.Contains("\"theme\": \"high-contrast\"")),
            "Refreshed diff shows removing B2 (high-contrast)");

        // Second confirm writes A, and Undo MUST restore B2 (not stale B)!
        var secondConfirm = dstVm.ConfirmImportRestore();
        Assert(secondConfirm is { Outcome: ConfigRestoreOutcomeKind.Succeeded }, "Second confirm writes A");
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaA, "Receiver now has A");
        dstVm.BeginUndoPreview();
        dstVm.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaB2 && File.ReadAllBytes(dstCfg).SequenceEqual(bytesB2),
            "Undo restores exact refreshed pre-restore version B2");
    }

    private static void V080ImportReusesCrossProcessLockAndCrashReconciliation()
    {
        var (bytesA, bytesB, _, shaA, shaB, _) = CreateLightDarkHighContrastFixtures();
        using var dir = new TempDir();
        var srcStore = Path.Combine(dir.Path, "src");
        var dstStore = Path.Combine(dir.Path, "dst");
        var srcCfg = Path.Combine(dir.Path, "src-cfg", "settings.json");
        var dstCfg = Path.Combine(dir.Path, "dst-cfg", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(srcCfg)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstCfg)!);
        File.WriteAllBytes(srcCfg, bytesA);
        File.WriteAllBytes(dstCfg, bytesB);

        var srcSvc = new LocalConfigRestorePointService(srcStore);
        var pt = srcSvc.CreateRestorePoint(srcCfg, "crash-lock-test");
        var pkgPath = Path.Combine(dir.Path, "pkg.contain-config-point");
        Assert(srcSvc.ExportRestorePointPackage(pt, pkgPath).Succeeded, "Exported package");

        using var paused = new ManualResetEventSlim(false);
        using var killNow = new ManualResetEventSlim(false);
        var hooks1 = new DelegateHooks
        {
            OnAfterTargetReplacedBeforeVerify = (_, _) =>
            {
                paused.Set();
                killNow.Wait(TimeSpan.FromSeconds(15));
                throw new OperationCanceledException("Simulated abnormal exit during imported restore");
            }
        };

        var vm1 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(dstStore, hooks1));
        vm1.Refresh();
        vm1.OpenImportWorkspace();
        vm1.OpenImportPackage(pkgPath);
        vm1.SelectImportTargetPath(dstCfg);

        var w1Task = Task.Run(() => { try { vm1.ConfirmImportRestore(); } catch (OperationCanceledException) { } });
        Assert(paused.Wait(TimeSpan.FromSeconds(10)), "Window 1 paused inside imported restore while holding OS lock");

        // While Window 1 holds lock, Window 2 sees TargetBusyInAnotherWindow on import preview & ActiveInAnotherWindow on startup reconcile!
        var vm2 = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(dstStore));
        vm2.CheckInterruptedOperationOnStartup();
        vm2.Refresh();
        Assert(vm2.RecoveryStatus == ConfigRecoveryStatus.ActiveInAnotherWindow,
            $"Window 2 should see ActiveInAnotherWindow while Window 1 holds import lock, got {vm2.RecoveryStatus}");
        vm2.OpenImportWorkspace();
        vm2.OpenImportPackage(pkgPath);
        vm2.SelectImportTargetPath(dstCfg);
        Assert(vm2.ImportPreview?.Status == ConfigImportTargetStatus.TargetBusyInAnotherWindow && !vm2.CanConfirmImportRestore,
            "Window 2 import preview blocked while Window 1 holds lock");

        // Terminate Window 1 -> lock releases -> Window 2 reconciles and can undo back to exact B!
        killNow.Set();
        Assert(w1Task.Wait(TimeSpan.FromSeconds(15)), "Window 1 exited");
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaA, "Disk has A written before crash");

        vm2.CancelImport();
        vm2.RecheckRecovery();
        Assert(vm2.RecoveryStatus == ConfigRecoveryStatus.RestoreAppliedNeedsReview && vm2.CanUndoFromRecovery,
            $"Post-crash reconciliation detects RestoreAppliedNeedsReview, got {vm2.RecoveryStatus}");
        vm2.BeginUndoPreviewFromRecovery();
        vm2.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaB && File.ReadAllBytes(dstCfg).SequenceEqual(bytesB),
            "Post-crash undo restores receiver's exact B");
    }

    private static void V080ImportBinaryPayloadAndIdenticalTarget()
    {
        using var dir = new TempDir();
        var srcStore = Path.Combine(dir.Path, "src");
        var dstStore = Path.Combine(dir.Path, "dst");
        var srcCfg = Path.Combine(dir.Path, "src-cfg", "custom.dat");
        var dstCfg = Path.Combine(dir.Path, "dst-cfg", "custom.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(srcCfg)!);
        Directory.CreateDirectory(Path.GetDirectoryName(dstCfg)!);

        byte[] binA = [0x00, 0xFF, 0xFE, 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03];
        byte[] binB = [0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0x10, 0x20];
        var shaA = Sha256Hex(binA);
        var shaB = Sha256Hex(binB);
        File.WriteAllBytes(srcCfg, binA);
        File.WriteAllBytes(dstCfg, binB);

        var srcSvc = new LocalConfigRestorePointService(srcStore);
        var pt = srcSvc.CreateRestorePoint(srcCfg, "binary");
        var pkgPath = Path.Combine(dir.Path, "bin.contain-config-point");
        Assert(srcSvc.ExportRestorePointPackage(pt, pkgPath).Succeeded, "Exported binary package");

        var dstVm = new ConfigRestorePointsViewModel(new LocalConfigRestorePointService(dstStore));
        dstVm.Refresh();
        dstVm.OpenImportWorkspace();
        dstVm.OpenImportPackage(pkgPath);
        dstVm.SelectImportTargetPath(dstCfg);
        Assert(!dstVm.HasImportTextDiff && dstVm.ShowImportDiffFallback && dstVm.CanConfirmImportRestore,
            "Binary import falls back to size/hash summary without text diff");
        var res = dstVm.ConfirmImportRestore();
        Assert(res is { Outcome: ConfigRestoreOutcomeKind.Succeeded }, "Binary import restore succeeded");
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaA && File.ReadAllBytes(dstCfg).SequenceEqual(binA),
            "Binary target matches exact A bytes");

        // Re-preview same package against already-identical target -> IdenticalToTarget, Confirm disabled!
        dstVm.OpenImportWorkspace();
        dstVm.OpenImportPackage(pkgPath);
        dstVm.SelectImportTargetPath(dstCfg);
        Assert(dstVm.IsImportIdentical && !dstVm.CanConfirmImportRestore,
            "Identical target disables overwrite");
        dstVm.CancelImport();

        // Undo restores exact binary B
        dstVm.BeginUndoPreview();
        dstVm.ConfirmUndoRestore();
        Assert(Sha256Hex(File.ReadAllBytes(dstCfg)) == shaB && File.ReadAllBytes(dstCfg).SequenceEqual(binB),
            "Undo restores exact binary B");
    }

    private sealed class DelegateHooks : IConfigRestoreOperationHooks
    {
        public Action<string, string>? OnAfterRestorePrepared { get; init; }
        public Action<string, string>? OnAfterTargetReplacedBeforeVerify { get; init; }
        public Action<string, string>? OnBeforeSaveRestoreCompleted { get; init; }
        public Action<string, string>? OnBeforePostRestoreRefresh { get; init; }
        public Action<string, string>? OnAfterUndoPrepared { get; init; }
        public Action<string, string>? OnAfterUndoTargetUpdatedBeforeMarkCompleted { get; init; }

        public void AfterRestorePrepared(string undoId, string targetPath) => OnAfterRestorePrepared?.Invoke(undoId, targetPath);
        public void AfterTargetReplacedBeforeVerify(string undoId, string targetPath) => OnAfterTargetReplacedBeforeVerify?.Invoke(undoId, targetPath);
        public void BeforeSaveRestoreCompleted(string undoId, string targetPath) => OnBeforeSaveRestoreCompleted?.Invoke(undoId, targetPath);
        public void BeforePostRestoreRefresh(string undoId, string targetPath) => OnBeforePostRestoreRefresh?.Invoke(undoId, targetPath);
        public void AfterUndoPrepared(string undoId, string targetPath) => OnAfterUndoPrepared?.Invoke(undoId, targetPath);
        public void AfterUndoTargetUpdatedBeforeMarkCompleted(string undoId, string targetPath) => OnAfterUndoTargetUpdatedBeforeMarkCompleted?.Invoke(undoId, targetPath);
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
