namespace Contain.Desktop.Model;

public enum SandboxCapabilityStatus
{
    Unknown,
    Checking,
    Ready,
    UnsupportedPlatform,
    HomeEditionUnsupported,
    SupportedFeatureNotEnabled,
    CliUnavailable,
    VirtualizationDisabled,
    PolicyBlocked,
    RebootPending,
    Undetermined
}

public enum SandboxCapabilityPrimaryAction
{
    None,
    OpenWindowsFeatures,
    GoToProjectPackaging,
    Recheck
}

public sealed record SandboxCapabilityReport(
    SandboxCapabilityStatus Status,
    string Headline,
    string Detail,
    string NextStep,
    SandboxCapabilityPrimaryAction PrimaryAction = SandboxCapabilityPrimaryAction.Recheck,
    string PrimaryActionLabel = "重新检测",
    string? WsbExecutablePath = null,
    string? WsbVersion = null,
    string? WindowsEditionSummary = null,
    string? PermissionNote = null,
    string Diagnostic = "")
{
    public bool IsReady => Status == SandboxCapabilityStatus.Ready;
}

/// <summary>
/// Pure-data snapshot of non-elevated host Windows Sandbox evidence used for deterministic
/// capability classification without guessing.
/// </summary>
public sealed record HostSandboxEnvironmentSnapshot(
    bool IsWindows,
    bool IsNativeX64,
    string? EditionId,
    string? ProductName,
    uint? ProductType,
    bool EditionQueryDenied,
    bool HasWindowsSandboxExe,
    string? WsbExecutablePath,
    bool? WsbHelpSucceeded,
    string? WsbHelpOutput,
    IReadOnlyList<string> MissingWsbSubcommands,
    string? WsbVersion,
    bool? WsbListSucceeded,
    string? WsbListDiagnostic,
    bool? PolicyExplicitlyDisabled,
    string? PolicyEvidence,
    bool PolicyQueryDenied,
    bool? HypervisorPresent,
    bool? VirtualizationFirmwareEnabled,
    bool VirtualizationQueryDenied,
    bool? RebootPending,
    bool RebootPendingQueryDenied);

public enum TrialExecutableArch
{
    Unknown,
    X64,
    X86,
    Arm64
}

public enum TrialInputKind
{
    SingleFile,
    Folder
}

public sealed record TrialExecutableInfo(
    string FileName,
    string FullPath,
    long SizeBytes,
    string FormattedSize,
    TrialExecutableArch Architecture,
    string ArchitectureLabel,
    string? ProductVersion,
    string? FileVersion,
    bool HasSiblingFiles,
    int SiblingFileCount)
{
    public bool IsRunnableInX64Sandbox => Architecture is TrialExecutableArch.X64 or TrialExecutableArch.X86;

    public string DisplayVersion =>
        !string.IsNullOrWhiteSpace(ProductVersion)
            ? ProductVersion
            : !string.IsNullOrWhiteSpace(FileVersion)
                ? FileVersion
                : "未提供";
}

/// <summary>
/// Candidate executable discovered inside a selected portable software folder.
/// Identified uniquely by RelativePath so same-name EXEs in nested subdirectories stay distinct.
/// </summary>
public sealed record TrialFolderEntryCandidate(
    string RelativePath,
    string FileName,
    string FullPath,
    long SizeBytes,
    string FormattedSize,
    TrialExecutableArch Architecture,
    string ArchitectureLabel,
    string? ProductVersion,
    string? FileVersion)
{
    public bool IsRunnableInX64Sandbox => Architecture is TrialExecutableArch.X64 or TrialExecutableArch.X86;

    public string DisplayVersion =>
        !string.IsNullOrWhiteSpace(ProductVersion)
            ? ProductVersion
            : !string.IsNullOrWhiteSpace(FileVersion)
                ? FileVersion
                : "未提供";

    public string DisplayLabel => $"{RelativePath}（{FormattedSize} · {ArchitectureLabel}）";

    public TrialExecutableInfo ToExecutableInfo() => new(
        FileName,
        FullPath,
        SizeBytes,
        FormattedSize,
        Architecture,
        ArchitectureLabel,
        ProductVersion,
        FileVersion,
        HasSiblingFiles: false,
        SiblingFileCount: 0);
}

public sealed record TrialFolderInfo(
    string FolderName,
    string FullPath,
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    string FormattedTotalSize,
    IReadOnlyList<TrialFolderEntryCandidate> RunnableEntries,
    IReadOnlyList<TrialFolderEntryCandidate> UnsupportedEntries)
{
    public bool HasRunnableEntries => RunnableEntries.Count > 0;
}

public sealed record TrialFolderScanProgress(
    int DirectoriesScanned,
    int FilesScanned,
    long TotalBytes,
    int CandidateCount,
    string CurrentRelativePath);

public sealed record TrialStagingProgress(
    string Stage,
    int FilesCopied,
    int TotalFiles,
    long BytesCopied,
    long TotalBytes,
    string CurrentRelativePath,
    string? StagedEntrySha256 = null,
    string? CreatedInstanceId = null)
{
    public double Percent => TotalBytes > 0
        ? Math.Clamp(BytesCopied * 100.0 / TotalBytes, 0, 100)
        : TotalFiles > 0 ? Math.Clamp(FilesCopied * 100.0 / TotalFiles, 0, 100) : 0;
}

/// <summary>
/// Unified immutable launch specification for both single-file and portable-folder trials.
/// Flows through one shared sandbox lifecycle (staging -> wsb start -> readiness -> guest launch -> monitor -> recovery -> cleanup).
/// </summary>
public sealed record TrialLaunchTarget(
    TrialInputKind Kind,
    string DisplayName,
    string SourcePath,
    string EntryRelativePath,
    TrialExecutableInfo EntryExecutable,
    int TotalFileCount,
    long TotalBytes,
    string FormattedTotalSize,
    int DirectoryCount = 0,
    string? SessionId = null)
{
    public static TrialLaunchTarget FromSingleExecutable(TrialExecutableInfo executable, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return new TrialLaunchTarget(
            TrialInputKind.SingleFile,
            executable.FileName,
            executable.FullPath,
            executable.FileName,
            executable,
            TotalFileCount: 1,
            TotalBytes: executable.SizeBytes,
            FormattedTotalSize: executable.FormattedSize,
            DirectoryCount: 0,
            SessionId: sessionId);
    }

    public static TrialLaunchTarget FromFolder(TrialFolderInfo folder, TrialFolderEntryCandidate entry, string? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(entry);
        return new TrialLaunchTarget(
            TrialInputKind.Folder,
            folder.FolderName,
            folder.FullPath,
            entry.RelativePath,
            entry.ToExecutableInfo(),
            TotalFileCount: folder.FileCount,
            TotalBytes: folder.TotalBytes,
            FormattedTotalSize: folder.FormattedTotalSize,
            DirectoryCount: folder.DirectoryCount,
            SessionId: sessionId);
    }
}

public enum TrialHashState
{
    Idle,
    Computing,
    Computed,
    Failed
}

/// <summary>
/// Authoritative observed state of the tracked Windows Sandbox instance.
/// Never conflated with host staging cleanup state or transient CLI errors.
/// </summary>
public enum SandboxInstanceState
{
    NotCreated,
    Running,
    ConfirmedEnded,
    Unknown
}

/// <summary>
/// Authoritative observed state of the per-session host staging directory.
/// </summary>
public enum TrialStagingState
{
    NotCreated,
    Active,
    Cleaned,
    Remaining,
    Unconfirmed
}

public enum IsolatedTrialPhase
{
    Idle,
    Recovering,
    PreparingInput,
    StartingSandbox,
    SandboxOpened,
    ProgramStarted,
    ProgramExitedAfterStart,
    RecoveredRunning,
    Stopping,
    Ended,
    Unconfirmed,
    Failed
}

public enum TrialEndReason
{
    None,
    EndedByContain,
    SandboxWindowClosed,
    SandboxInstanceExited,
    RecoveredAndEnded,
    RecoveredAlreadyExited,
    ApplicationClosing,
    PreparationCancelled
}

public sealed record TrialEvidenceEntry(
    string Timestamp,
    string Stage,
    string Summary,
    DateTimeOffset TimestampUtc = default);

public sealed record SandboxStartResult(
    string SessionId,
    string InstanceId,
    string StagingRootPath,
    string ReadOnlyInputPath,
    int? ConnectProcessId,
    DateTimeOffset OpenedAt,
    TrialInputKind InputKind = TrialInputKind.SingleFile,
    string EntryRelativePath = "",
    int StagedFileCount = 1,
    string? EntrySha256 = null);

public sealed record PendingTrialSessionRecord(
    int SchemaVersion,
    string SessionId,
    string InstanceId,
    string StagingRootPath,
    string FileName,
    DateTimeOffset CreatedUtc,
    int OwnerProcessId,
    long OwnerStartTimeUtcTicks);

public enum PendingSessionLoadStatus
{
    None,
    Loaded,
    OwnedByAnotherLiveProcess,
    Corrupt
}

public sealed record PendingSessionLoadResult(
    PendingSessionLoadStatus Status,
    PendingTrialSessionRecord? Record = null,
    string Diagnostic = "");

public enum GuestProgramLaunchOutcome
{
    StartedAndRunning,
    StartedAndExitedZero,
    StartedAndExitedNonZero,
    FailedToLaunch
}

public sealed record GuestProgramLaunchResult(
    GuestProgramLaunchOutcome Outcome,
    int GuestLauncherExitCode,
    string Summary,
    string Diagnostic = "");

public sealed record SandboxInstancePollResult(
    SandboxInstanceState InstanceState,
    bool? OwnedWindowRunning,
    TrialStagingState StagingState = TrialStagingState.Unconfirmed,
    string Diagnostic = "");

public sealed record SandboxStopResult(
    SandboxInstanceState InstanceState,
    TrialStagingState StagingState,
    string Summary,
    string Diagnostic = "")
{
    public bool IsFullyCleaned =>
        InstanceState is SandboxInstanceState.NotCreated or SandboxInstanceState.ConfirmedEnded
        && StagingState is TrialStagingState.NotCreated or TrialStagingState.Cleaned;
}

public sealed class IsolatedTrialException : Exception
{
    public string Code { get; }
    public string NextStep { get; }
    public string Diagnostic { get; }
    public string FailedStage { get; }
    public SandboxStopResult? RollbackResult { get; }
    public string? CreatedInstanceId { get; }
    public string? StagingRootPath { get; }
    public string? SessionId { get; }

    public IsolatedTrialException(
        string code,
        string message,
        string nextStep = "",
        string diagnostic = "",
        Exception? inner = null,
        SandboxStopResult? rollbackResult = null,
        string? createdInstanceId = null,
        string? stagingRootPath = null,
        string? sessionId = null,
        string failedStage = "")
        : base(message, inner)
    {
        Code = code;
        NextStep = nextStep;
        Diagnostic = diagnostic;
        FailedStage = failedStage;
        RollbackResult = rollbackResult;
        CreatedInstanceId = createdInstanceId;
        StagingRootPath = stagingRootPath;
        SessionId = sessionId;
    }
}

public interface IIsolatedTrialService
{
    Task<SandboxCapabilityReport> ProbeCapabilityAsync(CancellationToken cancellationToken = default);
    TrialExecutableInfo InspectExecutable(string executablePath);

    /// <summary>
    /// Asynchronously scans a selected portable software folder read-only, rejecting directory links,
    /// junctions, or other reparse points so the displayed scope matches what will be staged.
    /// </summary>
    Task<TrialFolderInfo> ScanFolderAsync(
        string folderPath,
        IProgress<TrialFolderScanProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the lowercase hex SHA-256 digest of a local file strictly read-only without executing it.
    /// </summary>
    Task<string> ComputeSha256Async(string executablePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the built-in Windows "Turn Windows features on or off" dialog (`optionalfeatures.exe`)
    /// so the user can decide whether to enable Windows Sandbox and reboot later.
    /// </summary>
    void OpenWindowsFeatures();

    /// <summary>
    /// Stages the single EXE or full verified folder snapshot into a per-session host staging directory
    /// (supporting progress and cancellation and rejecting reparse points, unreadable files, or
    /// insufficient space before starting Sandbox), then launches the isolated offline sandbox instance
    /// and verifies readiness.
    /// </summary>
    Task<SandboxStartResult> StartSandboxAsync(
        TrialLaunchTarget target,
        IProgress<TrialStagingProgress>? stagingProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the staged input from the read-only mount into a writable guest-local directory
    /// preserving relative layout, sets WorkingDirectory to the entry EXE's directory, and launches
    /// the entry executable inside the interactive guest session.
    /// </summary>
    Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(
        SandboxStartResult session,
        TrialLaunchTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls ONLY the tracked instance ID via `wsb list --raw` and checks ONLY remote-session
    /// processes whose exact command line matches that exact instance ID.
    /// Never reports ConfirmedEnded unless a valid parsed instance list explicitly omits the ID.
    /// </summary>
    Task<SandboxInstancePollResult> PollSessionAsync(
        SandboxStartResult session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a bounded one-shot authoritative status query for an exact tracked instance ID
    /// and its verified Contain staging directory.
    /// </summary>
    Task<SandboxInstancePollResult> QueryInstanceStateAsync(
        string instanceId,
        string? stagingRootPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops ONLY the tracked instance ID, closes ONLY any lingering remote-session window bound
    /// to that exact ID, and cleans ONLY the verified staging directory under Contain's staging root.
    /// Separately reports real InstanceState and real StagingState without false positives.
    /// </summary>
    Task<SandboxStopResult> StopAndCleanupAsync(
        string? instanceId,
        string? stagingRootPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to clean ONLY a verified Contain-owned session staging directory (used when the
    /// sandbox instance is already confirmed ended and only temporary files remain).
    /// </summary>
    Task<SandboxStopResult> CleanupStagingOnlyAsync(
        string? stagingRootPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads and validates any pending session record left behind by an abnormal exit.
    /// Fails closed on corrupt records or sessions owned by another currently live Contain window.
    /// </summary>
    PendingSessionLoadResult LoadPendingSession();

    /// <summary>
    /// Removes the pending session record only if it matches the specified instance ID / session ID
    /// (preventing one window from clobbering another window's newer record).
    /// </summary>
    void ClearPendingSession(string? expectedInstanceId = null, string? expectedSessionId = null);
}
