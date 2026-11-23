using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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

    // Static, pure-ASCII PowerShell 5.1 guest launcher. Reads target filename strictly as UTF-8
    // JSON data (never interpolated into a command string), copies into writable guest-local
    // C:\ContainTrialRun so programs writing beside themselves work while C:\ContainTrialInput
    // remains read-only, and reports observable launch outcome via exact guest exit codes:
    //   0  = started and still running after 1s
    //   20 = started and exited cleanly with code 0 within 1s
    //   21 = started and exited non-zero within 1s
    //   12 = input EXE missing in read-only mount
    //   13 = copy to guest local run folder failed
    //   14 = Process.Start failed or returned no process handle/PID
    internal const string GuestLauncherScript = """
        $ErrorActionPreference = 'Stop'
        Set-StrictMode -Version Latest
        try {
            $metaPath = 'C:\ContainTrialBootstrap\target.json'
            if (!(Test-Path -LiteralPath $metaPath -PathType Leaf)) { exit 11 }
            $meta = [System.IO.File]::ReadAllText($metaPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            $name = [string]$meta.file_name
            if ([string]::IsNullOrWhiteSpace($name) -or $name.IndexOfAny([char[]]@('\', '/', ':')) -ge 0) { exit 11 }
            $src = [System.IO.Path]::Combine('C:\ContainTrialInput', $name)
            if (!(Test-Path -LiteralPath $src -PathType Leaf)) { exit 12 }
            $srcInfo = New-Object System.IO.FileInfo($src)
            $runDir = 'C:\ContainTrialRun'
            if (!(Test-Path -LiteralPath $runDir)) { [void][System.IO.Directory]::CreateDirectory($runDir) }
            $dst = [System.IO.Path]::Combine($runDir, $name)
            try {
                [System.IO.File]::Copy($src, $dst, $true)
                $dstInfo = New-Object System.IO.FileInfo($dst)
                if ($dstInfo.IsReadOnly) { $dstInfo.IsReadOnly = $false }
                if ($dstInfo.Length -ne $srcInfo.Length) { exit 13 }
            } catch {
                exit 13
            }
            try {
                $psi = New-Object System.Diagnostics.ProcessStartInfo
                $psi.FileName = $dst
                $psi.WorkingDirectory = $runDir
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

    private readonly string stagingBaseDirectory;
    private readonly TimeSpan commandTimeout;
    private readonly TimeSpan readinessTimeout;
    private readonly string? explicitWsbPath;

    public WindowsSandboxTrialService(
        string? stagingBaseDirectory = null,
        TimeSpan? commandTimeout = null,
        TimeSpan? readinessTimeout = null,
        string? explicitWsbPath = null)
    {
        this.stagingBaseDirectory = string.IsNullOrWhiteSpace(stagingBaseDirectory)
            ? Path.Combine(Path.GetTempPath(), "Contain-IsolatedTrial")
            : Path.GetFullPath(stagingBaseDirectory);
        this.commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(25);
        this.readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(60);
        this.explicitWsbPath = string.IsNullOrWhiteSpace(explicitWsbPath) ? null : Path.GetFullPath(explicitWsbPath);
    }

    public TrialExecutableInfo InspectExecutable(string executablePath) =>
        InspectExecutableFile(executablePath);

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
            throw new IsolatedTrialException("invalid_pe", "所选文件不是有效的 Windows PE 可执行程序，无法在沙箱中试用。");

        if (arch == TrialExecutableArch.Arm64)
            throw new IsolatedTrialException("unsupported_arch", "检测到 ARM64 可执行文件；本轮隔离试用仅支持 Windows x64 单文件程序。");

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

        var archLabel = arch switch
        {
            TrialExecutableArch.X64 => "Windows x64 (PE32+)",
            TrialExecutableArch.X86 => "Windows x86 (32 位，本轮以 x64 单文件为主)",
            _ => "未知架构"
        };

        return new TrialExecutableInfo(
            info.Name,
            fullPath,
            info.Length,
            FormatByteSize(info.Length),
            arch,
            archLabel,
            siblingCount > 0,
            siblingCount);
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

    public static IReadOnlyList<string> ParseRunningInstanceIds(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("WindowsSandboxEnvironments", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return [];

            var ids = new List<string>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("Id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.String)
                {
                    var raw = idProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(raw) && Guid.TryParse(raw, out var parsed))
                        ids.Add(parsed.ToString("D"));
                }
            }
            return ids;
        }
        catch (JsonException) { return []; }
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
        if (!OperatingSystem.IsWindows() || RuntimeInformation.OSArchitecture != Architecture.X64 || !Environment.Is64BitProcess)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.UnsupportedPlatform,
                "当前系统不是原生 Windows x64 环境",
                "隔离试用需要原生 64 位 Windows x64 宿主机与 Windows 沙盒（Windows Sandbox）支持。",
                "请在启用了 Windows 沙盒的 Windows 11 x64 电脑上运行 Contain。");
        }

        var wsbPath = ResolveWsbExecutablePath();
        var sandboxGuiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsSandbox.exe");
        var hasSandboxGui = File.Exists(sandboxGuiPath);

        if (wsbPath is null)
        {
            if (!hasSandboxGui)
            {
                return new SandboxCapabilityReport(
                    SandboxCapabilityStatus.FeatureNotEnabled,
                    "本机尚未启用 Windows 沙盒（Windows Sandbox）",
                    "未检测到 WindowsSandbox.exe 与 wsb.exe。通常是因为系统尚未开启“Windows 沙盒”可选功能，或当前系统版本（如 Windows 家庭版）不支持该功能。",
                    "如需使用，请由你自行打开“启用或关闭 Windows 功能”勾选“Windows 沙盒”（需开启硬件虚拟化），并在合适时机重启电脑后点击“重新检测”。Contain 不会自动修改系统功能或重启电脑。");
            }

            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.CliUnavailable,
                "缺少按实例管理沙箱所需的 wsb.exe 命令行工具",
                "检测到 WindowsSandbox.exe，但未找到支持按实例创建、连接、执行与结束的 wsb.exe CLI（Windows 11 24H2 及以上版本的 Windows 沙盒提供）。",
                "请确认已安装最新版本的 Windows 沙盒并重启完成后再点击“重新检测”。Contain 不会自动修改系统功能或重启电脑。");
        }

        // Verify required subcommands from `wsb --help`
        var help = await RunWsbCommandAsync(wsbPath, ["--help"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (help.TimedOut || help.ExitCode != 0)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.PolicyOrRuntimeBlocked,
                "无法调用 Windows 沙盒命令行（wsb.exe）",
                "执行 wsb --help 失败或超时，可能是系统策略限制、沙盒组件未完成初始化或权限不足。",
                "请确认系统未通过组策略禁用 Windows 沙盒，且上次启用功能后的重启已完成，随后点击“重新检测”。",
                WsbExecutablePath: wsbPath,
                Diagnostic: CombineDiagnostic(help));
        }

        var helpText = help.Stdout + "\n" + help.Stderr;
        var missingOps = RequiredSubcommands
            .Where(op => !helpText.Contains(op, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (missingOps.Count > 0)
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.CliUnavailable,
                "当前 Windows 沙盒 CLI 缺少所需的实例控制能力",
                $"wsb.exe 缺少必需子命令：{string.Join("、", missingOps)}，无法安全地按实例创建、连接和结束隔离试用。",
                "请更新 Windows 11 的 Windows 沙盒组件后重新检测。",
                WsbExecutablePath: wsbPath,
                Diagnostic: helpText.Trim());
        }

        var versionRes = await RunWsbCommandAsync(wsbPath, ["--version"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        var wsbVersion = !versionRes.TimedOut && versionRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(versionRes.Stdout)
            ? versionRes.Stdout.Trim().Split('\n')[0].Trim()
            : "unknown";

        // Verify live runtime query via `wsb list --raw`
        var listRes = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (listRes.TimedOut || listRes.ExitCode != 0 || !listRes.Stdout.TrimStart().StartsWith('{'))
        {
            return new SandboxCapabilityReport(
                SandboxCapabilityStatus.PolicyOrRuntimeBlocked,
                "Windows 沙盒运行服务当前不可用或被策略阻止",
                "执行 wsb list --raw 未能返回有效实例列表。可能原因包括未开启 BIOS 硬件虚拟化、组策略禁用 Windows 沙盒，或容器管理服务未就绪。",
                "请检查硬件虚拟化、Windows 沙盒功能状态或组策略；如需启用或重启电脑，请由你自行操作后点击“重新检测”。",
                WsbExecutablePath: wsbPath,
                WsbVersion: wsbVersion,
                Diagnostic: CombineDiagnostic(listRes));
        }

        return new SandboxCapabilityReport(
            SandboxCapabilityStatus.Ready,
            "Windows 沙盒已就绪",
            $"已核验本机支持按实例创建、连接、来宾执行与按 ID 结束操作（wsb {wsbVersion}）。",
            "选择一个本地单文件 Windows x64 .exe，即可在断网、禁用剪贴板的临时沙箱中试用。",
            WsbExecutablePath: wsbPath,
            WsbVersion: wsbVersion);
    }

    public async Task<SandboxStartResult> StartSandboxAsync(
        TrialExecutableInfo executable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var capability = await ProbeCapabilityAsync(cancellationToken).ConfigureAwait(false);
        if (!capability.IsReady || string.IsNullOrEmpty(capability.WsbExecutablePath))
        {
            throw new IsolatedTrialException(
                "capability_unavailable",
                capability.Headline,
                capability.NextStep,
                capability.Diagnostic);
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

            // Copy ONLY the selected single EXE into inputDir (preserving exact original file name)
            var stagedExePath = Path.Combine(inputDir, executable.FileName);
            try
            {
                await CopyFileAsync(executable.FullPath, stagedExePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IsolatedTrialException(
                    "source_read_failed",
                    $"无法读取所选程序“{executable.FileName}”：{ex.Message}",
                    "请确认原文件未被移动、删除或被其他程序独占占用，然后重新选择文件或再次点击“开始隔离试用”。",
                    $"{ex.GetType().Name}: {ex.Message}",
                    ex);
            }

            // Write UTF-8 JSON target metadata + static pure-ASCII launcher script into bootstrapDir
            var targetJson = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                session_id = sessionId,
                file_name = executable.FileName
            });
            await File.WriteAllTextAsync(Path.Combine(bootstrapDir, "target.json"), targetJson, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(bootstrapDir, "launch-trial.ps1"), GuestLauncherScript, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var configXml = BuildSandboxConfigXml(inputDir, bootstrapDir);

            // 1. Create isolated sandbox instance via `wsb start --config <xml> --raw`
            var startRes = await RunWsbCommandAsync(wsbPath, ["start", "--config", configXml, "--raw"], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (startRes.TimedOut)
            {
                throw new IsolatedTrialException(
                    "wsb_start_timeout",
                    "创建 Windows 沙箱实例超时。",
                    "请检查宿主机内存与虚拟化服务状态后重试；若持续超时，可确认 Windows 沙盒能否单独打开。",
                    CombineDiagnostic(startRes));
            }
            if (startRes.ExitCode != 0)
            {
                throw new IsolatedTrialException(
                    "wsb_start_failed",
                    $"创建 Windows 沙箱实例失败（退出码 {startRes.ExitCode}）。",
                    "请检查本机是否允许启动 Windows 沙盒（如内存不足、策略限制或虚拟化未就绪），处理后可重新点击“开始隔离试用”。",
                    CombineDiagnostic(startRes));
            }

            createdInstanceId = ParseStartedInstanceId(startRes.Stdout);
            if (string.IsNullOrEmpty(createdInstanceId))
            {
                throw new IsolatedTrialException(
                    "wsb_start_no_id",
                    "Windows 沙箱启动命令未返回有效的实例 ID。",
                    "无法安全跟踪该实例，请检查本机 Windows 沙盒 CLI 版本后重试。",
                    CombineDiagnostic(startRes));
            }

            // 2. Open interactive Sandbox window detached (NEVER redirect pipes or WaitForExit!)
            int? connectPid = LaunchDetachedConnect(wsbPath, createdInstanceId);

            // 3. Wait up to readinessTimeout (default 60s) for THIS exact instance to respond to
            //    a side-effect-free ExistingLogin probe confirming the read-only input mount.
            var readySw = Stopwatch.StartNew();
            WsbCommandResult? lastProbe = null;
            var ready = false;
            const string probeCmd = @"cmd.exe /c if exist C:\ContainTrialInput (if exist C:\ContainTrialBootstrap\target.json (exit 0) else (exit 3)) else (exit 2)";

            while (readySw.Elapsed < readinessTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Verify the instance is still alive before probing
                var listCheck = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                if (!listCheck.TimedOut && listCheck.ExitCode == 0)
                {
                    var ids = ParseRunningInstanceIds(listCheck.Stdout);
                    if (!ids.Contains(createdInstanceId, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new IsolatedTrialException(
                            "sandbox_exited_during_startup",
                            "Windows 沙箱在初始化完成前已关闭。",
                            "可能是窗口被提前关闭或沙箱内部启动中止；本次暂存目录已自动清理，可重新尝试。",
                            $"instance={createdInstanceId}");
                    }
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
                    "本次临时实例与暂存目录已自动清理；请确认交互窗口未被安全软件拦截后重试。",
                    lastProbe is null ? $"instance={createdInstanceId}" : $"instance={createdInstanceId}; {CombineDiagnostic(lastProbe)}");
            }

            return new SandboxStartResult(
                sessionId,
                createdInstanceId,
                stagingRoot,
                inputDir,
                connectPid,
                DateTimeOffset.Now);
        }
        catch
        {
            // Rollback ONLY the instance and staging folder created in this failed attempt
            await BestEffortStopAndCleanupAsync(wsbPath, createdInstanceId, stagingRoot).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<GuestProgramLaunchResult> LaunchProgramInSandboxAsync(
        SandboxStartResult session,
        TrialExecutableInfo executable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(executable);
        var wsbPath = ResolveWsbExecutablePath()
            ?? throw new IsolatedTrialException("wsb_missing", "找不到 wsb.exe，无法在沙箱内启动程序。");

        const string launchCmd = @"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\ContainTrialBootstrap\launch-trial.ps1";
        var execRes = await RunWsbCommandAsync(
            wsbPath,
            ["exec", "--id", session.InstanceId, "-r", "ExistingLogin", "-c", launchCmd, "--raw"],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (execRes.TimedOut)
        {
            return new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                -1,
                "沙箱已打开，但等待程序启动回执超时。你可以在沙箱窗口内的 C:\\ContainTrialRun 中查看实际情况，或点击“结束试用”。",
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
        return guestCode switch
        {
            0 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.StartedAndRunning,
                0,
                $"已将“{executable.FileName}”复制到来宾本地目录 C:\\ContainTrialRun 并启动进程（启动 1 秒后进程仍在运行）。"),
            20 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.StartedAndExitedZero,
                20,
                $"已在来宾本地目录 C:\\ContainTrialRun 启动“{executable.FileName}”，程序已执行完毕并正常退出（退出码 0）。"),
            21 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.StartedAndExitedNonZero,
                21,
                $"沙箱已打开，“{executable.FileName}”已尝试启动但立即异常退出。若该程序依赖同目录其他文件或需要联网下载，在离线单文件沙箱中可能无法运行。"),
            12 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                12,
                "沙箱已打开，但在只读输入目录 C:\\ContainTrialInput 中未找到目标程序。"),
            13 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                13,
                "沙箱已打开，但将程序复制到来宾本地目录 C:\\ContainTrialRun 时失败。"),
            14 => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                14,
                $"沙箱已打开，但无法启动“{executable.FileName}”（进程未能创建，可能不兼容当前离线环境或被系统阻止）。"),
            _ => new GuestProgramLaunchResult(
                GuestProgramLaunchOutcome.FailedToLaunch,
                guestCode,
                $"沙箱已打开，但来宾启动脚本返回异常状态（代码 {guestCode}）。",
                CombineDiagnostic(execRes))
        };
    }

    public async Task<SandboxInstancePollResult> PollSessionAsync(
        SandboxStartResult session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var wsbPath = ResolveWsbExecutablePath();
        if (wsbPath is null)
            return new SandboxInstancePollResult(false, null, "wsb.exe unavailable");

        var listRes = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        if (listRes.TimedOut || listRes.ExitCode != 0)
        {
            // Transient poll failure: assume still running to avoid falsely terminating user's session
            return new SandboxInstancePollResult(true, null, CombineDiagnostic(listRes));
        }

        var ids = ParseRunningInstanceIds(listRes.Stdout);
        var present = ids.Contains(session.InstanceId, StringComparer.OrdinalIgnoreCase);
        bool? ownedWindowRunning = null;
        if (OperatingSystem.IsWindows())
        {
            var matchedPids = FindOwnedRemoteSessionPids(session.InstanceId);
            if (matchedPids is not null)
                ownedWindowRunning = matchedPids.Count > 0;
        }

        return new SandboxInstancePollResult(present, ownedWindowRunning);
    }

    public async Task<SandboxStopResult> StopAndCleanupAsync(
        string? instanceId,
        string? stagingRootPath,
        CancellationToken cancellationToken = default)
    {
        var wsbPath = ResolveWsbExecutablePath();
        var diag = new StringBuilder();
        var instanceStopped = true;

        if (!string.IsNullOrWhiteSpace(instanceId) && wsbPath is not null)
        {
            var stopRes = await RunWsbCommandAsync(wsbPath, ["stop", "--id", instanceId, "--raw"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (stopRes.TimedOut)
            {
                instanceStopped = false;
                diag.AppendLine("wsb stop timed out: " + CombineDiagnostic(stopRes));
            }
            else if (stopRes.ExitCode != 0)
            {
                // Verify whether the instance is already absent from `wsb list --raw`
                var listCheck = await RunWsbCommandAsync(wsbPath, ["list", "--raw"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                var ids = ParseRunningInstanceIds(listCheck.Stdout);
                instanceStopped = !ids.Contains(instanceId, StringComparer.OrdinalIgnoreCase);
                if (!instanceStopped)
                    diag.AppendLine("wsb stop failed: " + CombineDiagnostic(stopRes));
            }

            // Close ONLY remote session window process(es) explicitly bound to `--id <instanceId>`
            if (OperatingSystem.IsWindows())
            {
                CloseOwnedRemoteSessionWindows(instanceId);
            }
        }

        var stagingCleaned = CleanupStagingDirectory(stagingRootPath, out var cleanErr);
        if (!string.IsNullOrEmpty(cleanErr))
            diag.AppendLine("staging cleanup: " + cleanErr);

        var summary = (instanceStopped, stagingCleaned) switch
        {
            (true, true) => "已结束本次沙箱实例并清理宿主机暂存目录，来宾内所有改动已丢弃。",
            (true, false) => "已结束本次沙箱实例，但宿主机暂存目录暂未完全删除（可能仍被占用）。",
            (false, true) => "已清理宿主机暂存目录，但停止沙箱实例时遇到异常，请核对本机运行中的沙箱。",
            _ => "停止沙箱实例与清理暂存目录均未完全成功，请查看技术详情。"
        };

        return new SandboxStopResult(instanceStopped, stagingCleaned, summary, diag.ToString().Trim());
    }

    private async Task BestEffortStopAndCleanupAsync(string wsbPath, string? instanceId, string stagingRootPath)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
        {
            try
            {
                await RunWsbCommandAsync(wsbPath, ["stop", "--id", instanceId, "--raw"], TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
                if (OperatingSystem.IsWindows()) CloseOwnedRemoteSessionWindows(instanceId);
            }
            catch { }
        }
        CleanupStagingDirectory(stagingRootPath, out _);
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
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
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

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        await using var dest = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
}
