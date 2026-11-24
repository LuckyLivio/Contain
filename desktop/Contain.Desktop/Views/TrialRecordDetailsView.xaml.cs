using System.Windows;
using System.Windows.Controls;

namespace Contain.Desktop.Views;

public partial class TrialRecordDetailsView : UserControl
{
    public TrialRecordDetailsView() => InitializeComponent();
    private MainWindow Shell => (MainWindow)DataContext;
    private void OnBack(object sender, RoutedEventArgs e) => Shell.ShowTrialHistoryCategory();
    private async void OnReplay(object sender, RoutedEventArgs e) => await Shell.ReplaySelectedTrialRecordAsync();
    private void OnExportReport(object sender, RoutedEventArgs e) => Shell.ExportSelectedTrialReport();
    private void OnCopyEntrySha256(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.TrialHistory.DetailEntrySha256);
}
