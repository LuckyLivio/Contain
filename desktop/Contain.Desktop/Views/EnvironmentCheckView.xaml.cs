using System.Windows;
using System.Windows.Controls;

namespace Contain.Desktop.Views;

public partial class EnvironmentCheckView : UserControl
{
    public EnvironmentCheckView() => InitializeComponent();
    private MainWindow Shell => (MainWindow)DataContext;
    private async void OnPickFolder(object sender, RoutedEventArgs e) => await Shell.PickProjectFolderAsync();
    private async void OnConfirmOnboarding(object sender, RoutedEventArgs e) => await Shell.RunConfirmOnboardingAsync();
    private void OnCancelOnboarding(object sender, RoutedEventArgs e) => Shell.CancelProjectOnboarding();
    private async void OnStartCheck(object sender, RoutedEventArgs e) => await Shell.RunEnvironmentCheckAsync();
    private void OnToggleReport(object sender, RoutedEventArgs e) => Shell.EnvironmentCheck.ToggleDetailedReport();
    private void OnCopyReport(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.EnvironmentCheck.DetailedReportText);
    private async void OnStartBuild(object sender, RoutedEventArgs e) => await Shell.RunProjectBuildAsync();
    private void OnOpenArtifactFolder(object sender, RoutedEventArgs e) => Shell.OpenProjectArtifactFolder();
    private async void OnGenerateInstaller(object sender, RoutedEventArgs e) => await Shell.RunGenerateProjectInstallerAsync();
    private void OnOpenInstallerFolder(object sender, RoutedEventArgs e) => Shell.OpenProjectInstallerFolder();
    private void OnCopyInstallerPath(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.EnvironmentCheck.GeneratedInstallerFullPath);
    private void OnToggleBuildLog(object sender, RoutedEventArgs e) => Shell.EnvironmentCheck.ToggleBuildLog();
    private void OnCopyBuildLog(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.EnvironmentCheck.BuildLogText);
}
