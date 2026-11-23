using System.Windows;
using System.Windows.Controls;

namespace Contain.Desktop.Views;

public partial class IsolatedTrialView : UserControl
{
    public IsolatedTrialView() => InitializeComponent();
    private MainWindow Shell => (MainWindow)DataContext;
    private void OnPickExe(object sender, RoutedEventArgs e) => Shell.PickTrialExecutable();
    private async void OnComputeSha256(object sender, RoutedEventArgs e) => await Shell.ComputeTrialSha256Async();
    private void OnCopySha256(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.IsolatedTrial.ComputedSha256);
    private async void OnStartTrial(object sender, RoutedEventArgs e) => await Shell.StartIsolatedTrialAsync();
    private async void OnEndTrial(object sender, RoutedEventArgs e) => await Shell.EndIsolatedTrialAsync();
    private async void OnRecheckPendingSession(object sender, RoutedEventArgs e) => await Shell.RecheckPendingTrialAsync();
    private async void OnRetryEndPendingSession(object sender, RoutedEventArgs e) => await Shell.RetryEndPendingTrialAsync();
    private async void OnRetryStagingCleanup(object sender, RoutedEventArgs e) => await Shell.RetryTrialStagingCleanupAsync();
    private async void OnRecheckCapability(object sender, RoutedEventArgs e) => await Shell.RecheckSandboxCapabilityAsync();
    private void OnOpenWindowsFeatures(object sender, RoutedEventArgs e) => Shell.OpenWindowsFeaturesFromTrial();
    private void OnGoToPackaging(object sender, RoutedEventArgs e) => Shell.ShowEnvironmentCheck();
    private void OnCopyTechnical(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.IsolatedTrial.TechnicalDetailsText);
}
