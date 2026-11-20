using System.Windows;
using System.Windows.Controls;

namespace Contain.Desktop.Views;

public partial class PackageCheckView : UserControl
{
    public PackageCheckView() => InitializeComponent();
    private MainWindow Shell => (MainWindow)DataContext;
    private async void OnPickZip(object sender, RoutedEventArgs e) => await Shell.PickPackageZipAsync();
    private async void OnPickSpec(object sender, RoutedEventArgs e) => await Shell.PickPackageSpecAsync();
    private async void OnRecheck(object sender, RoutedEventArgs e) => await Shell.RecheckPackageAsync();
    private void OnCopyTechnical(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.PackageCheck.TechnicalDetailsText);
}
