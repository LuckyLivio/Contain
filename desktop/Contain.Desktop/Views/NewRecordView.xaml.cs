using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Contain.Desktop.Views;

public partial class NewRecordView : UserControl
{
    public NewRecordView() => InitializeComponent();
    public void ShowTask() => Dispatcher.InvokeAsync(PageScroll.ScrollToTop, DispatcherPriority.Loaded);
    private MainWindow Shell => (MainWindow)DataContext;
    private void OnPickInstaller(object sender, RoutedEventArgs e) => Shell.PickInstaller();
    private void OnCopyInstaller(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.Install.InstallerPath);
    private void OnPickWatchRoot(object sender, RoutedEventArgs e) => Shell.PickWatchRoot();
    private void OnPickDatabase(object sender, RoutedEventArgs e) => Shell.PickSaveDatabase();
    private async void OnCheckEnvironment(object sender, RoutedEventArgs e) => await Shell.RunUiAsync(Shell.Install.CheckEnvironmentAsync);
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (HasValidationError(this))
        {
            Shell.ShowInputError("高级等待参数必须是有效的非负整数；请修正标出的输入后再开始。");
            return;
        }
        var task = Shell.RunUiAsync(Shell.Install.StartAsync);
        await Dispatcher.InvokeAsync(PageScroll.ScrollToTop, DispatcherPriority.Loaded);
        await task;
    }
    private async void OnOpenSession(object sender, RoutedEventArgs e) => await Shell.RunUiAsync(Shell.Install.OpenSessionAsync);
    private void OnCopyTask(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.Install.TaskSummary);

    private static bool HasValidationError(DependencyObject parent)
    {
        if (Validation.GetHasError(parent)) return true;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (HasValidationError(VisualTreeHelper.GetChild(parent, index))) return true;
        return false;
    }
}
