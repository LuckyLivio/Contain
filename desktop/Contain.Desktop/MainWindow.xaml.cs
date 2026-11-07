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
    private readonly MyRecordsView recordsView = new();
    private readonly NewRecordView newRecordView = new();
    private readonly RecordDetailsView detailsView = new();
    private readonly DispatcherTimer timer;
    private string localError = "";
    private string localStatus = "";
    private long navigationVersion;
    private bool loaded;
    private bool dialogOpen;
    private bool closeInProgress;
    private bool closeCommitted;

    public MainWindow(PersonalRecords personalRecords)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "contain.exe");
        Records = new RecordsViewModel(new CoreRecordService(new CoreClient(executable)),
            personalRecords.DatabasePath, () => personalRecords.IsMissing);
        Install = new InstallViewModel(new InstallService(new InstallClient(executable), personalRecords),
            action => Dispatcher.BeginInvoke(action), OpenCapturedSessionAsync);
        Records.PropertyChanged += OnModelChanged;
        Install.PropertyChanged += OnModelChanged;
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
    public InstallViewModel Install { get; }
    public string WindowTitle => "Contain · " + ProductVersion.Version;
    public string ErrorMessage => localError.Length > 0 ? localError : Records.ErrorMessage;
    public string StatusMessage => localStatus.Length > 0 ? localStatus : Records.StatusMessage;
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        ShowRecords();
        await RunUiAsync(() => Records.LoadPersonalAsync());
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Records.ErrorMessage) or nameof(Records.StatusMessage))
        {
            localError = "";
            localStatus = "";
        }
        Changed(nameof(ErrorMessage));
        Changed(nameof(StatusMessage));
    }

    private void SetPage(UIElement page, bool recordNavigation)
    {
        navigationVersion++;
        Navigation.ReplaceContent(page, this);
        RecordsNavigation.IsActive = recordNavigation;
        NewNavigation.IsActive = !recordNavigation;
    }

    private bool GuardTaskNavigation()
    {
        if (!Install.Busy) return false;
        SetPage(newRecordView, false);
        localStatus = "本次记录仍在进行。请等待采集与保存结束，再切换记录库。";
        Changed(nameof(StatusMessage));
        return true;
    }

    public void ShowRecords()
    {
        if (GuardTaskNavigation()) return;
        recordsView.PrepareForShow();
        SetPage(recordsView, true);
    }

    public void ShowNewRecord()
    {
        SetPage(newRecordView, false);
        if (!Install.Busy && !Install.ConfigurationEnabled) Install.Reset();
    }

    public async Task RefreshRecordsAsync()
    {
        if (GuardTaskNavigation()) return;
        await RunUiAsync(() => Records.IsPersonalLibrary ? Records.LoadPersonalAsync() : Records.OpenDatabaseAsync(Records.DatabasePath));
    }

    public async Task OpenRecordAsync(string id)
    {
        if (GuardTaskNavigation()) return;
        SetPage(detailsView, true);
        detailsView.ShowOverview();
        await RunUiAsync(() => Records.OpenSessionAsync(id));
    }

    private async Task OpenCapturedSessionAsync(string database, string id)
    {
        await Records.OpenCapturedSessionAsync(database, id);
        if (Records.ActiveRecordId != id)
            throw new InvalidDataException("已保存记录的会话身份与本次任务不一致。未选择其他记录代替。");
        SetPage(detailsView, true);
        detailsView.ShowOverview();
        localStatus = Install.Result?.Succeeded == true
            ? "记录已保存，可以查看文件操作。"
            : "已打开本次保存的证据；记录未确认完整结束。";
        Changed(nameof(StatusMessage));
    }

    private async void OnMyRecords(object sender, RoutedEventArgs e)
    {
        if (GuardTaskNavigation()) return;
        ShowRecords();
        if (!Records.IsPersonalLibrary) await RunUiAsync(() => Records.LoadPersonalAsync());
    }

    private void OnNewRecord(object sender, RoutedEventArgs e) => ShowNewRecord();
    private void OnCurrentTask(object sender, RoutedEventArgs e)
    {
        SetPage(newRecordView, false);
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
            SetPage(newRecordView, false);
            newRecordView.ShowTask();
            return;
        }
        if (closeInProgress) return;
        closeInProgress = true;
        Navigation.IsEnabled = false;
        localStatus = "正在停止读取与导出，请稍候…";
        Changed(nameof(StatusMessage));
        try
        {
            await Records.CloseAsync();
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
        Install.PropertyChanged -= OnModelChanged;
        Records.Dispose();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Navigation is null) return;
        Navigation.IsPaneOpen = ActualWidth >= 900;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
