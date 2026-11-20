namespace Contain.Desktop.Model;

public sealed partial class EnvironmentCheckViewModel
{
    private enum FirstPackageStep { None, DetectPackager, Configure, Check, Build, Package, OpenFolder }

    private DotnetOnboardingPreviewData? savedPackagePlan;
    private string savedPackageManifestSha = "";
    private bool firstPackageBusy;
    private bool installerNeedsRebuild;

    // Only the manifest written for this consent is eligible for the simplified build action.
    // A separately supplied or edited manifest goes through the existing command review.
    private DotnetOnboardingPreviewData? FirstPackagePlan => IsOnboardingReadyToConfirm ? onboardingPreview
        : currentPreview?.RawFileSha256 == savedPackageManifestSha ? savedPackagePlan : null;

    private FirstPackageStep NextFirstPackageStep
    {
        get
        {
            if (FirstPackagePlan is null) return FirstPackageStep.None;
            if (IsOnboardingReadyToConfirm)
                return onboardingPreview!.PackagerDetected == false ? FirstPackageStep.DetectPackager : FirstPackageStep.Configure;
            if (installerMatchesCurrentBuild && HasValidInstaller && !HasInstallerFailure) return FirstPackageStep.OpenFolder;
            if (!CanShowStartBuild) return FirstPackageStep.Check;
            if (IsBuildSucceeded && !installerNeedsRebuild && !string.IsNullOrWhiteSpace(verifiedBuildAttestationJson))
                return FirstPackageStep.Package;
            return savedPackagePlan?.PackagerDetected == false ? FirstPackageStep.DetectPackager : FirstPackageStep.Build;
        }
    }

    public bool ShowFirstPackageFlow => FirstPackagePlan is not null;
    public bool ShowFirstPackageResult => NextFirstPackageStep == FirstPackageStep.OpenFolder;
    public bool CanRunFirstPackageAction => ShowFirstPackageFlow && !disposed && !firstPackageBusy
        && !checking && !applyingOnboarding && !building && !packaging;
    public string FirstPackageProjectName
    {
        get
        {
            if (FirstPackagePlan is not { } plan) return "";
            var stem = Path.GetFileNameWithoutExtension(plan.CsprojFile);
            return string.IsNullOrEmpty(stem) ? plan.AppName ?? ""
                : string.IsNullOrEmpty(plan.AppName) || plan.AppName == stem ? stem : $"{stem} · {plan.AppName}";
        }
    }
    public string FirstPackageProjectType => FirstPackagePlan?.EvidenceFields.FirstOrDefault(row => row.Label == "应用类型")?.Value ?? "";
    public string FirstPackageEntry => FirstPackagePlan?.EntryExecutable ?? "";
    public string FirstPackageToolSummary
    {
        get
        {
            if (FirstPackagePlan is not { } plan) return "";
            var sdk = ActionItems.Concat(PassedItems).FirstOrDefault(item => item.RawName.StartsWith("tool:dotnet", StringComparison.OrdinalIgnoreCase));
            var sdkText = sdk is not null && !IsOnboardingReadyToConfirm ? $".NET SDK：{sdk.SummaryLine}"
                : plan.HostDetectedCompatibleSdks.Count > 0 ? $".NET SDK：检测到 {string.Join("、", plan.HostDetectedCompatibleSdks)}"
                : $".NET SDK：常用目录中未发现所需版本（{plan.SdkRequirementSummary}），保存后将核实。";
            var packager = plan.PackagerDetected switch { true => "已检测到", false => "未检测到", null => "待核实" };
            return $"{sdkText}\nInno Setup 6：{packager}";
        }
    }

    public string FirstPackageActionText => NextFirstPackageStep switch
    {
        FirstPackageStep.DetectPackager => "重新检测打包工具",
        FirstPackageStep.Configure => "保存配置并检查",
        FirstPackageStep.Check => "重新检查环境",
        FirstPackageStep.Build => IsBuildFailed || installerNeedsRebuild ? "重新构建并生成安装包" : "构建并生成安装包",
        FirstPackageStep.Package => HasInstallerFailure ? "重试生成安装包" : "生成安装包",
        FirstPackageStep.OpenFolder => "打开安装包文件夹",
        _ => ""
    };

    public string FirstPackageNextStep
    {
        get
        {
            if (applyingOnboarding) return "正在保存项目配置…";
            if (checking) return "正在检查本机环境…";
            if (building) return BuildProgressText;
            if (packaging) return PackagingProgressText;
            return NextFirstPackageStep switch
            {
                FirstPackageStep.DetectPackager => "请从 https://jrsoftware.org/isdl.php 安装 Inno Setup 6，然后点击“重新检测打包工具”。",
                FirstPackageStep.Configure => "保存后将在项目目录创建 contain.environment.json，并自动检查本机环境。",
                FirstPackageStep.Check => ActionItems.FirstOrDefault() is { } item
                    ? item.RawName.StartsWith("tool:dotnet", StringComparison.OrdinalIgnoreCase)
                        ? $"{item.DisplayTitle}：{item.SummaryLine}\n请从 https://dotnet.microsoft.com/download/dotnet 安装所需的 .NET SDK x64，然后点击“重新检查环境”。"
                        : $"{item.DisplayTitle}：{item.SummaryLine}\n{item.RemedyLine}\n处理后点击“重新检查环境”。"
                    : HasRecheckFailure ? RecheckFailureText : OutcomeNote,
                FirstPackageStep.Build when IsBuildFailed => BuildFailureErrorSummary.Length > 0 ? BuildFailureErrorSummary : BuildNote,
                FirstPackageStep.Build when installerNeedsRebuild => InstallerFailureRemedy,
                FirstPackageStep.Build => "确认后将构建此项目并生成 Windows x64 安装包。构建会执行项目代码，请确认信任此项目。",
                FirstPackageStep.Package when savedPackagePlan?.PackagerDetected == false =>
                    "请从 https://jrsoftware.org/isdl.php 安装 Inno Setup 6，然后点击“重试生成安装包”。将复用已验证的构建产物。",
                FirstPackageStep.Package => HasInstallerFailure ? $"{InstallerFailureTitle}\n{InstallerFailureRemedy}" : "将使用已验证的构建产物生成安装包。",
                FirstPackageStep.OpenFolder => $"已生成 {GeneratedInstallerFileName}，可以打开所在文件夹。",
                _ => ""
            };
        }
    }

    public Task<bool> RunFirstPackageActionAsync() => Track(RunFirstPackageActionCoreAsync);

    private async Task<bool> RunFirstPackageActionCoreAsync()
    {
        if (!CanRunFirstPackageAction || currentProject is null) return false;
        var step = NextFirstPackageStep;
        var project = currentProject;
        firstPackageBusy = true;
        NotifyFirstPackage();
        try
        {
            switch (step)
            {
                case FirstPackageStep.DetectPackager:
                    if (IsOnboardingReadyToConfirm) return await RefreshOnboardingPreviewCoreAsync();
                    // A resumed plan already has its manifest. Re-inspect it including the current tool locations.
                    return await SelectProjectCoreAsync(project.ProjectPath);
                case FirstPackageStep.Configure:
                    return await ConfirmOnboardingCoreAsync(firstPackage: true);
                case FirstPackageStep.Check:
                    return await CheckCoreAsync();
                case FirstPackageStep.Build:
                    if (!await StartBuildCoreAsync() || !ReferenceEquals(currentProject, project) || FirstPackagePlan is null)
                        return false;
                    return await GenerateInstallerCoreAsync();
                case FirstPackageStep.Package:
                    return await GenerateInstallerCoreAsync();
                case FirstPackageStep.OpenFolder:
                    return OpenInstallerFolder();
                default:
                    return false;
            }
        }
        finally
        {
            firstPackageBusy = false;
            NotifyFirstPackage();
        }
    }

    private void NotifyFirstPackage()
    {
        OnPropertyChanged(nameof(ShowFirstPackageFlow));
        OnPropertyChanged(nameof(ShowFirstPackageResult));
        OnPropertyChanged(nameof(CanRunFirstPackageAction));
        OnPropertyChanged(nameof(FirstPackageProjectName));
        OnPropertyChanged(nameof(FirstPackageProjectType));
        OnPropertyChanged(nameof(FirstPackageEntry));
        OnPropertyChanged(nameof(FirstPackageToolSummary));
        OnPropertyChanged(nameof(FirstPackageActionText));
        OnPropertyChanged(nameof(FirstPackageNextStep));
    }
}
