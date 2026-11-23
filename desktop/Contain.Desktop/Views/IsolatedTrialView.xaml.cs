using System.Windows;
using System.Windows.Controls;

namespace Contain.Desktop.Views;

public partial class IsolatedTrialView : UserControl
{
    public IsolatedTrialView() => InitializeComponent();
    private MainWindow Shell => (MainWindow)DataContext;
    private void OnPickExe(object sender, RoutedEventArgs e) => Shell.PickTrialExecutable();
    private async void OnStartTrial(object sender, RoutedEventArgs e) => await Shell.StartIsolatedTrialAsync();
    private async void OnEndTrial(object sender, RoutedEventArgs e) => await Shell.EndIsolatedTrialAsync();
    private async void OnRecheckCapability(object sender, RoutedEventArgs e) => await Shell.RecheckSandboxCapabilityAsync();
    private void OnCopyTechnical(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.IsolatedTrial.TechnicalDetailsText);
}
