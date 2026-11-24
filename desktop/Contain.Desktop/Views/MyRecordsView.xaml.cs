using System.Windows;
using System.Windows.Controls;
using Contain.Desktop.Model;

namespace Contain.Desktop.Views;

public partial class MyRecordsView : UserControl
{
    public MyRecordsView() => InitializeComponent();

    public void PrepareForShow()
    {
        RecordList.SelectedItem = null;
        TrialHistoryList.SelectedItem = null;
    }

    private MainWindow Shell => (MainWindow)DataContext;
    private void OnNew(object sender, RoutedEventArgs e) => Shell.ShowNewRecord();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Shell.RefreshRecordsAsync();
    private void OnSelectInstallCategory(object sender, RoutedEventArgs e) => Shell.ShowInstallRecordsCategory();
    private void OnSelectTrialCategory(object sender, RoutedEventArgs e) => Shell.ShowTrialHistoryCategory();
    private void OnRefreshTrialHistory(object sender, RoutedEventArgs e) => Shell.RefreshTrialHistory();
    private void OnClearTrialSearch(object sender, RoutedEventArgs e) => Shell.TrialHistory.ClearSearch();
    private async void OnGoToTrial(object sender, RoutedEventArgs e) => await Shell.NavigateToIsolatedTrialAsync();

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordList.SelectedItem is SessionRow row) await Shell.OpenRecordAsync(row.Id);
    }

    private void OnTrialSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrialHistoryList.SelectedItem is TrialHistoryRecord record)
            Shell.OpenTrialRecord(record.SessionId);
    }
}
