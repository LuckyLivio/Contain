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

public enum TrialHashState
{
    Idle,
    Computing,
    Computed,
    Failed
}

public enum IsolatedTrialPhase
{
    Idle,
    StartingSandbox,
    SandboxOpened,
    ProgramStarted,
    ProgramExitedAfterStart,
    Stopping,
    Ended,
    Failed
}

public enum TrialEndReason
{
    None,
    EndedByContain,
    SandboxWindowClosed,
    SandboxInstanceExited,
    ApplicationClosing
}

public sealed record TrialEvidenceEntry(
    string Timestamp,
    string Stage,
    string Summary);

public sealed record SandboxStartResult(
    string SessionId,
    string InstanceId,
    string StagingRootPath,
    string ReadOnlyInputPath,
    int? ConnectProcessId,
    DateTimeOffset OpenedAt);

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
    bool InstancePresentInList,
    bool? OwnedWindowRunning,
    string Diagnostic = "");

public sealed record SandboxStopResult(
    bool InstanceStopped,
    bool StagingCleaned,
    string Summary,
    string Diagnostic = "");

public sealed class IsolatedTrialException : Exception
{
    public string Code { get; }
    public string NextStep { get; }
    public string Diagnostic { get; }

    public IsolatedTrialException(
        string code,
        string message,
        string nextStep = "",
        string diagnostic = "",
        Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        NextStep = nextStep;
        Diagnostic = diagnostic;
    }
}

public interface IIsolatedTrialService
{
    Task<SandboxCapabilityReport> ProbeCapabilityAsync(CancellationToken cancellationToken = default);
    TrialExecutableInfo InspectExecutable(string executablePath);

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
    /// Creates an isolated staging folder with ONLY the selected EXE, starts a new offline
    /// clipboard-isolated Windows Sandbox instance, opens its interactive window, and verifies
    /// that the exact returned instance responds to a read-only ExistingLogin readiness probe.
    /// </summary>
    Task<SandboxStartResult> StartSandboxAsync(
        TrialExecutableInfo executable,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the selected EXE from the read-only mount into a writable guest-local directory
    /// using UTF-8 JSON metadata (never interpolating filenames into shell commands) and starts it
    /// inside the interactive guest session.
    /// </summary>
    Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(
        SandboxStartResult session,
        TrialExecutableInfo executable,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls ONLY the tracked instance ID via `wsb list --raw` and checks ONLY remote-session
    /// processes whose exact command line matches `--id &lt;instanceId&gt;`.
    /// </summary>
    Task<SandboxInstancePollResult> PollSessionAsync(
        SandboxStartResult session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops ONLY the tracked instance ID, closes ONLY any lingering remote-session window bound
    /// to that exact ID, and deletes ONLY this session's staging directory.
    /// </summary>
    Task<SandboxStopResult> StopAndCleanupAsync(
        string? instanceId,
        string? stagingRootPath,
        CancellationToken cancellationToken = default);
}
