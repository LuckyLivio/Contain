using System.Windows;
using System.Windows.Controls;

namespace Contain.Desktop.Views;

public partial class EnvironmentCheckView : UserControl
{
    public EnvironmentCheckView() => InitializeComponent();
    private MainWindow Shell => (MainWindow)DataContext;
    private void OnPickFolder(object sender, RoutedEventArgs e) => Shell.PickProjectFolder();
    private async void OnStartCheck(object sender, RoutedEventArgs e) => await Shell.RunEnvironmentCheckAsync();
    private void OnToggleReport(object sender, RoutedEventArgs e) => Shell.EnvironmentCheck.ToggleDetailedReport();
    private void OnCopyReport(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.EnvironmentCheck.DetailedReportText);
}
