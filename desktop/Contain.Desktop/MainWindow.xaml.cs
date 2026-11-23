using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Contain.Desktop.Model;
using Contain.Desktop.Views;
using Contain.Viewer;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Contain.Desktop;

public partial class MainWindow : FluentWindow, INotifyPropertyChanged
{
    private enum ActiveSection { Records, TrialRecordDetails, NewRecord, EnvironmentCheck, PackageCheck, IsolatedTrial }

    private readonly PersonalRecords personalRecords;
    private readonly MyRecordsView recordsView = new();
    private readonly NewRecordView newRecordView = new();
    private readonly RecordDetailsView detailsView = new();
    private readonly TrialRecordDetailsView trialDetailsView = new();
    private readonly EnvironmentCheckView environmentView = new();
    private readonly PackageCheckView packageView = new();
    private readonly IsolatedTrialView isolatedTrialView = new();
    private readonly DispatcherTimer timer;
    private ActiveSection activeSection = ActiveSection.Records;
    private bool isTrialHistoryCategory;
    private string localError = "";
    private string localStatus = "";
    private long navigationVersion;
    private bool loaded;
    private bool dialogOpen;
    private bool closeInProgress;
    private bool closeCommitted;

    public MainWindow(PersonalRecords personalRecords)
    {
        this.personalRecords = personalRecords;
        var executable = Path.Combine(AppContext.BaseDirectory, "contain.exe");
        var workflowExecutable = Path.Combine(AppContext.BaseDirectory, "contain-workflow.exe");
        Records = new RecordsViewModel(new CoreRecordService(new CoreClient(executable)),
            personalRecords.DatabasePath, () => personalRecords.IsMissing);
        Install = new InstallViewModel(new InstallService(new InstallClient(executable), personalRecords),
            action => Dispatcher.BeginInvoke(action), OpenCapturedSessionAsync);
        EnvironmentCheck = new EnvironmentCheckViewModel(
            new WorkflowCliEnvironmentService(workflowExecutable),
            dispatcher: action => Dispatcher.BeginInvoke(action));
        PackageCheck = new PackageCheckViewModel(new WorkflowCliPackageService(workflowExecutable));
        var appDataRoot = Path.GetDirectoryName(Path.GetDirectoryName(personalRecords.DatabasePath))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Contain");
        var pendingSessionFile = Path.Combine(appDataRoot, "IsolatedTrial", "pending-session.json");
        var trialHistoryStore = new JsonTrialHistoryStore(Path.Combine(appDataRoot, "IsolatedTrial", "History"));
        TrialHistory = new TrialHistoryViewModel(trialHistoryStore);
        IsolatedTrial = new IsolatedTrialViewModel(
            new WindowsSandboxTrialService(pendingSessionFilePath: pendingSessionFile),
            dispatch: action => Dispatcher.BeginInvoke(action),
            historyStore: trialHistoryStore,
            onHistoryUpdated: record => Dispatcher.BeginInvoke(() => TrialHistory.UpsertInMemory(record)));
        Records.PropertyChanged += OnModelChanged;
        TrialHistory.PropertyChanged += OnModelChanged;
        Install.PropertyChanged += OnModelChanged;
        EnvironmentCheck.PropertyChanged += OnModelChanged;
        PackageCheck.PropertyChanged += OnModelChanged;
        IsolatedTrial.PropertyChanged += OnModelChanged;
        InitializeComponent();
        Controls.ActionNavigationItem.ConfigureNavigation(Navigation);
        DataContext = this;
        // Use the actual working area rather than a large fixed minimum at high DPI.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => Install.Tick(), Dispatcher);
    }

    public RecordsViewModel Records { get; }
    public TrialHistoryViewModel TrialHistory { get; }
    public InstallViewModel Install { get; }
    public EnvironmentCheckViewModel EnvironmentCheck { get; }
    public PackageCheckViewModel PackageCheck { get; }
    public IsolatedTrialViewModel IsolatedTrial { get; }
    public bool IsTrialHistoryCategory => isTrialHistoryCategory;
    public bool IsInstallRecordsCategory => !isTrialHistoryCategory;
    public bool ShowPendingTrialBanner =>
        IsolatedTrial.HasPendingRecoveryBanner && activeSection != ActiveSection.IsolatedTrial;
    public string WindowTitle => "Contain · " + ProductVersion.Version;
    public string ErrorMessage => localError.Length > 0
        ? localError
        : activeSection switch
        {
            ActiveSection.EnvironmentCheck or ActiveSection.PackageCheck or ActiveSection.IsolatedTrial => "",
            ActiveSection.TrialRecordDetails => TrialHistory.ErrorMessage,
            ActiveSection.Records when isTrialHistoryCategory => TrialHistory.ErrorMessage,
            _ => Records.ErrorMessage
        };
    public string StatusMessage => localStatus.Length > 0
        ? localStatus
        : activeSection switch
        {
            ActiveSection.EnvironmentCheck => EnvironmentCheck.StatusMessage,
            ActiveSection.PackageCheck => PackageCheck.StatusMessage,
            ActiveSection.IsolatedTrial => IsolatedTrial.StatusMessage,
            ActiveSection.TrialRecordDetails => TrialHistory.StatusMessage,
            ActiveSection.Records when isTrialHistoryCategory => TrialHistory.StatusMessage,
            _ => Records.StatusMessage
        };
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        TrialHistory.Refresh();
        ShowRecords();
        await RunUiAsync(async () =>
        {
            if (personalRecords.IsMissing)
                ShowEnvironmentCheck();
            else
                await Records.LoadPersonalAsync();
        });
        // Non-blocking background check for any unfinished trial left by an abnormal Contain exit.
        _ = RunUiAsync(() => IsolatedTrial.CheckPendingSessionOnStartupAsync());
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Records.ErrorMessage) or nameof(Records.StatusMessage) or nameof(TrialHistory.ErrorMessage) or nameof(TrialHistory.StatusMessage) or nameof(EnvironmentCheck.StatusMessage) or nameof(PackageCheck.StatusMessage) or nameof(IsolatedTrial.StatusMessage))
        {
            localError = "";
            localStatus = "";
        }
        Changed(nameof(ErrorMessage));
        Changed(nameof(StatusMessage));
        Changed(nameof(ShowPendingTrialBanner));
    }

    private void SetPage(UIElement page, ActiveSection section)
    {
        navigationVersion++;
        activeSection = section;
        Navigation.ReplaceContent(page, this);
        RecordsNavigation.IsActive = section is ActiveSection.Records or ActiveSection.TrialRecordDetails;
        NewNavigation.IsActive = section == ActiveSection.NewRecord;
        EnvironmentNavigation.IsActive = section == ActiveSection.EnvironmentCheck;
        PackageNavigation.IsActive = section == ActiveSection.PackageCheck;
        IsolatedTrialNavigation.IsActive = section == ActiveSection.IsolatedTrial;
        Changed(nameof(ErrorMessage));
        Changed(nameof(StatusMessage));
        Changed(nameof(ShowPendingTrialBanner));
    }

    private bool GuardTaskNavigation()
    {
        if (!Install.Busy) return false;
        SetPage(newRecordView, ActiveSection.NewRecord);
        localStatus = "本次记录仍在进行。请等待采集与保存结束，再切换记录库。";
        Changed(nameof(StatusMessage));
        return true;
    }

    public void ShowRecords()
    {
        if (GuardTaskNavigation()) return;
        recordsView.PrepareForShow();
        SetPage(recordsView, ActiveSection.Records);
    }

    private void SetTrialHistoryCategory(bool showTrial)
    {
        if (isTrialHistoryCategory == showTrial) return;
        isTrialHistoryCategory = showTrial;
        Changed(nameof(IsTrialHistoryCategory));
        Changed(nameof(IsInstallRecordsCategory));
        Changed(nameof(ErrorMessage));
        Changed(nameof(StatusMessage));
    }

    public void ShowInstallRecordsCategory()
    {
        if (GuardTaskNavigation()) return;
        SetTrialHistoryCategory(false);
        ShowRecords();
    }

    public void ShowTrialHistoryCategory()
    {
        if (GuardTaskNavigation()) return;
        TrialHistory.Refresh();
        SetTrialHistoryCategory(true);
        ShowRecords();
    }

    public void RefreshTrialHistory() => RunUiOperation(() => TrialHistory.Refresh());

    public void OpenTrialRecord(string sessionId)
    {
        if (GuardTaskNavigation()) return;
        SetTrialHistoryCategory(true);
        if (TrialHistory.OpenRecord(sessionId))
            SetPage(trialDetailsView, ActiveSection.TrialRecordDetails);
    }

    public void ViewCurrentTrialRecord()
    {
        var id = IsolatedTrial.CurrentHistorySessionId;
        if (string.IsNullOrWhiteSpace(id)) return;
        OpenTrialRecord(id);
    }

    public async Task NavigateToIsolatedTrialAsync()
    {
        ShowIsolatedTrial();
        await RunUiAsync(() => IsolatedTrial.EnsureCapabilityCheckedAsync());
    }

    public async Task ReplaySelectedTrialRecordAsync()
    {
        var record = TrialHistory.SelectedRecord;
        if (record is null) return;
        ShowIsolatedTrial();
        var capabilityTask = RunUiAsync(() => IsolatedTrial.EnsureCapabilityCheckedAsync());
        await RunUiAsync(() => IsolatedTrial.PrepareReplayFromHistoryAsync(record));
        await capabilityTask;
    }

    public void ExportSelectedTrialReport()
    {
        if (!TrialHistory.HasSelectedRecord) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出本次隔离试用报告（版本化 JSON）",
            Filter = "JSON 报告|*.json",
            DefaultExt = ".json",
            FileName = TrialHistory.SuggestedExportFileName,
            OverwritePrompt = true,
            CheckPathExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        RunUiOperation(() => TrialHistory.ExportSelectedReport(dialog.FileName));
    }

    public void ShowNewRecord()
    {
        SetPage(newRecordView, ActiveSection.NewRecord);
        if (!Install.Busy && !Install.ConfigurationEnabled) Install.Reset();
    }

    public void ShowEnvironmentCheck() => SetPage(environmentView, ActiveSection.EnvironmentCheck);
    public void ShowPackageCheck() => SetPage(packageView, ActiveSection.PackageCheck);
    public void ShowIsolatedTrial() => SetPage(isolatedTrialView, ActiveSection.IsolatedTrial);

    public async Task RefreshRecordsAsync()
    {
        if (GuardTaskNavigation()) return;
        await RunUiAsync(() => Records.IsPersonalLibrary ? Records.LoadPersonalAsync() : Records.OpenDatabaseAsync(Records.DatabasePath));
    }

    public async Task OpenRecordAsync(string id)
    {
        if (GuardTaskNavigation()) return;
        SetPage(detailsView, ActiveSection.Records);
        detailsView.ShowOverview();
        await RunUiAsync(() => Records.OpenSessionAsync(id));
    }

    private async Task OpenCapturedSessionAsync(string database, string id)
    {
        await Records.OpenCapturedSessionAsync(database, id);
        if (Records.ActiveRecordId != id)
            throw new InvalidDataException("已保存记录的会话身份与本次任务不一致。未选择其他记录代替。");
        SetPage(detailsView, ActiveSection.Records);
        detailsView.ShowOverview();
        localStatus = Install.Result?.Succeeded == true
            ? "记录已保存，可以查看文件操作。"
            : "已打开本次保存的证据；记录未确认完整结束。";
        Changed(nameof(StatusMessage));
    }

    private async void OnMyRecords(object sender, RoutedEventArgs e)
    {
        if (GuardTaskNavigation()) return;
        TrialHistory.Refresh();
        ShowRecords();
        if (!Records.IsPersonalLibrary || Records.Sessions.Count == 0) await RunUiAsync(() => Records.LoadPersonalAsync());
    }

    private void OnNewRecord(object sender, RoutedEventArgs e) => ShowNewRecord();
    private void OnEnvironmentCheck(object sender, RoutedEventArgs e) => ShowEnvironmentCheck();
    private void OnPackageCheck(object sender, RoutedEventArgs e) => ShowPackageCheck();
    private async void OnIsolatedTrial(object sender, RoutedEventArgs e)
    {
        ShowIsolatedTrial();
        await RunUiAsync(() => IsolatedTrial.EnsureCapabilityCheckedAsync());
    }
    private void OnCurrentTask(object sender, RoutedEventArgs e)
    {
        SetPage(newRecordView, ActiveSection.NewRecord);
        newRecordView.ShowTask();
    }

    private async void OnOpenDatabase(object sender, RoutedEventArgs e)
    {
        if (GuardTaskNavigation()) return;
        var dialog = new OpenFileDialog
        {
            Title = "打开外部记录库（只读）", Filter = "Contain 记录数据库|*.db;*.sqlite;*.sqlite3|所有文件|*.*",
            CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        SetTrialHistoryCategory(false);
        ShowRecords();
        await RunUiAsync(() => Records.OpenDatabaseAsync(dialog.FileName));
    }

    public void PickInstaller()
    {
        if (!Install.ConfigurationEnabled) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择要记录的安装器", Filter = "Windows 安装程序|*.exe", CheckFileExists = true, Multiselect = false
        };
        // No installation or library mutation occurs on selection or cancel.
        if (dialog.ShowDialog(this) == true) Install.SelectInstaller(dialog.FileName);
    }

    public void PickWatchRoot()
    {
        if (!Install.ConfigurationEnabled) return;
        var dialog = new OpenFolderDialog { Title = "选择已有的观察文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) Install.WatchRoot = dialog.FolderName;
    }

    public async Task PickProjectFolderAsync()
    {
        if (!EnvironmentCheck.CanEditProjectSelection) return;
        var dialog = new OpenFolderDialog { Title = "选择包含项目文件的文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true)
            await RunUiAsync(() => EnvironmentCheck.SelectProjectAsync(dialog.FolderName));
        else EnvironmentCheck.CancelSelection();
    }

    public Task RunEnvironmentCheckAsync() => RunUiAsync(() => EnvironmentCheck.CheckAsync());

    public Task RunConfirmOnboardingAsync() => RunUiAsync(() => EnvironmentCheck.ConfirmOnboardingAsync());

    public void CancelProjectOnboarding() => RunUiOperation(() => EnvironmentCheck.CancelOnboarding());

    public Task RunProjectBuildAsync() => RunUiAsync(() => EnvironmentCheck.StartBuildAsync());

    public void OpenProjectArtifactFolder() => RunUiOperation(() => { EnvironmentCheck.OpenArtifactFolder(); });

    public Task RunGenerateProjectInstallerAsync() => RunUiAsync(() => EnvironmentCheck.GenerateInstallerAsync());

    public void OpenProjectInstallerFolder() => RunUiOperation(() => { EnvironmentCheck.OpenInstallerFolder(); });

    public async Task PickPackageZipAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要检查的 Windows 交付包 (.zip)",
            Filter = "ZIP 压缩包|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await RunUiAsync(() => PackageCheck.SelectZipAsync(dialog.FileName));
        else
            PackageCheck.CancelSelection();
    }

    public async Task PickPackageSpecAsync()
    {
        if (!PackageCheck.CanPickSpec) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择随包提供的校验清单 (package-spec.json)",
            Filter = "交付包校验清单|package-spec.json;*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await RunUiAsync(() => PackageCheck.SelectSpecAsync(dialog.FileName));
        else
            PackageCheck.CancelSelection();
    }

    public Task RecheckPackageAsync() => RunUiAsync(() => PackageCheck.RecheckAsync());

    public void PickTrialExecutable()
    {
        if (!IsolatedTrial.CanSelectExecutable) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择要隔离试用的 Windows 程序 (.exe)",
            Filter = "Windows 可执行程序|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            RunUiOperation(() => IsolatedTrial.SelectExecutable(dialog.FileName));
        else
            IsolatedTrial.CancelSelection();
    }

    public async Task PickTrialFolderAsync()
    {
        if (!IsolatedTrial.CanSelectFolder) return;
        var dialog = new OpenFolderDialog
        {
            Title = "选择要隔离试用的便携软件文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            await RunUiAsync(() => IsolatedTrial.SelectFolderAsync(dialog.FolderName));
        else
            IsolatedTrial.CancelSelection();
    }

    public void CancelTrialFolderScan() => RunUiOperation(() => IsolatedTrial.CancelFolderScan());

    public void CancelTrialReplayPreparation() => RunUiOperation(() => IsolatedTrial.CancelReplayPreparation());

    public void CancelTrialPreparation() => RunUiOperation(() => IsolatedTrial.CancelPreparation());

    public Task StartIsolatedTrialAsync() => RunUiAsync(() => IsolatedTrial.StartTrialAsync());

    public Task EndIsolatedTrialAsync() => RunUiAsync(() => IsolatedTrial.EndTrialAsync());

    public Task RecheckPendingTrialAsync() => RunUiAsync(() => IsolatedTrial.RecheckPendingSessionAsync());

    public Task RetryEndPendingTrialAsync() => RunUiAsync(() => IsolatedTrial.RetryEndPendingSessionAsync());

    public Task RetryTrialStagingCleanupAsync() => RunUiAsync(() => IsolatedTrial.RetryStagingCleanupAsync());

    public Task RecheckSandboxCapabilityAsync() => RunUiAsync(() => IsolatedTrial.RefreshCapabilityAsync());

    public Task ComputeTrialSha256Async() => RunUiAsync(() => IsolatedTrial.ComputeSha256Async());

    public void OpenWindowsFeaturesFromTrial() => RunUiOperation(() => IsolatedTrial.OpenWindowsFeatures());

    private void RunUiOperation(Action operation)
    {
        localError = "";
        localStatus = "";
        Changed(nameof(ErrorMessage));
        Changed(nameof(StatusMessage));
        try { operation(); }
        catch (Exception error) { ReportUiError(error); }
    }

    public void PickSaveDatabase()
    {
        if (!Install.ConfigurationEnabled) return;
        var dialog = new SaveFileDialog
        {
            Title = "更改记录保存位置", Filter = "Contain 记录数据库|*.db", DefaultExt = ".db",
            FileName = "contain.db", OverwritePrompt = false, CheckPathExists = true
        };
        // Existing databases are validated read-only by InstallClient at explicit Start.
        if (dialog.ShowDialog(this) != true) return;
        Install.CustomDatabase = true;
        Install.DatabasePath = dialog.FileName;
    }

    public async Task ExportAsync(bool matches)
    {
        var context = Records.CaptureExportContext(matches);
        if (context is null) return;
        var dialog = new SaveFileDialog
        {
            Title = matches ? "导出全部匹配结果" : "导出本页报告", Filter = "本地 HTML 报告|*.html",
            DefaultExt = ".html", FileName = matches ? "Contain-匹配结果.html" : "Contain-本页报告.html",
            OverwritePrompt = false, CheckPathExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        // Export uses the immutable selection/query captured before the modal dialog.
        // The existing exporter refuses any existing destination without replacing it.
        await RunUiAsync(() => Records.ExportAsync(context, dialog.FileName));
    }

    public void CopyText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            Clipboard.SetText(value);
            localStatus = "已复制。分享前请检查本地路径与记录内容。";
            Changed(nameof(StatusMessage));
        }
        catch (ExternalException error) { ReportUiError(error); }
    }

    public async Task RunUiAsync(Func<Task> operation)
    {
        localError = "";
        localStatus = "";
        Changed(nameof(ErrorMessage));
        Changed(nameof(StatusMessage));
        try { await operation(); }
        catch (OperationCanceledException) { /* The model discards only a superseded read/export. */ }
        catch (Exception error) { ReportUiError(error); }
    }

    private void ReportUiError(Exception error)
    {
        localError = $"{error.GetType().Name}：{error.Message}";
        Changed(nameof(ErrorMessage));
    }

    public void ShowInputError(string message)
    {
        localError = message;
        Changed(nameof(ErrorMessage));
    }

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var dialog = new ContentDialog(DialogHost)
            {
                Title = "关于 Contain", CloseButtonText = "关闭",
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"Contain {ProductVersion.Version}\n\n记录安装行为，找回记录，查看文件操作及其依据。\n\n桌面：WPF + WPF UI 4.3.0（第三方 MIT 开源组件）\nMVVM：CommunityToolkit.Mvvm 8.4.2\n运行时：.NET {Environment.Version}\n核心：{ProductVersion.CoreVersion}\n\nContain 的采集、证据关联、会话与报告属于项目业务实现。组件及运行库许可见包内 THIRD-PARTY-NOTICES。\n\n本 Beta 不承诺完整软件足迹、最终所有权或安全删除。",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 480
                }
            };
            await dialog.ShowAsync();
        }
        finally { dialogOpen = false; }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeCommitted) return;
        e.Cancel = true;
        if (Install.Busy)
        {
            Install.RefuseClose();
            SetPage(newRecordView, ActiveSection.NewRecord);
            newRecordView.ShowTask();
            return;
        }
        if (closeInProgress) return;
        closeInProgress = true;
        Navigation.IsEnabled = false;
        localStatus = IsolatedTrial.IsSessionActive || IsolatedTrial.IsStarting || IsolatedTrial.IsPreparingInput
            ? "正在结束当前隔离试用并清理沙箱，请稍候…"
            : "正在停止读取与导出，请稍候…";
        Changed(nameof(StatusMessage));
        try
        {
            await Task.WhenAll(
                Records.CloseAsync(),
                EnvironmentCheck.CloseAsync(),
                PackageCheck.CloseAsync(),
                IsolatedTrial.CloseAsync());
            // Empty-library cleanup can complete synchronously inside Closing.
            // Commit the second Close only after this cancelled event unwinds.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                closeCommitted = true;
                Close();
            }));
        }
        catch (Exception error)
        {
            closeInProgress = false;
            Navigation.IsEnabled = true;
            ReportUiError(error);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        timer.Stop();
        Records.PropertyChanged -= OnModelChanged;
        TrialHistory.PropertyChanged -= OnModelChanged;
        Install.PropertyChanged -= OnModelChanged;
        EnvironmentCheck.PropertyChanged -= OnModelChanged;
        PackageCheck.PropertyChanged -= OnModelChanged;
        IsolatedTrial.PropertyChanged -= OnModelChanged;
        Records.Dispose();
        EnvironmentCheck.Dispose();
        PackageCheck.Dispose();
        IsolatedTrial.Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Navigation is null) return;
        Navigation.IsPaneOpen = ActualWidth >= 900;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
