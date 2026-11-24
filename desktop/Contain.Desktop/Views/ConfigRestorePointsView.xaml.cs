using System.Windows;
using System.Windows.Controls;

namespace Contain.Desktop.Views;

public partial class ConfigRestorePointsView : UserControl
{
    public ConfigRestorePointsView()
    {
        InitializeComponent();
    }

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    private void OnPickConfigFile(object sender, RoutedEventArgs e) => Host?.PickConfigRestoreFile();
    private void OnCreateRestorePoint(object sender, RoutedEventArgs e) => Host?.CreateConfigRestorePoint();
    private void OnRefreshList(object sender, RoutedEventArgs e) => Host?.RefreshConfigRestorePoints();
    private void OnClearSearch(object sender, RoutedEventArgs e) => Host?.ConfigRestorePoints.ClearSearch();
    private void OnRecheckComparison(object sender, RoutedEventArgs e) => Host?.RecheckConfigRestoreComparison();
    private void OnBeginRestore(object sender, RoutedEventArgs e) => Host?.BeginConfigRestoreConfirmation();
    private void OnConfirmRestore(object sender, RoutedEventArgs e) => Host?.ConfirmConfigRestore();
    private void OnCancelRestore(object sender, RoutedEventArgs e) => Host?.CancelConfigRestoreConfirmation();
    private void OnUndoRestore(object sender, RoutedEventArgs e) => Host?.UndoLastConfigRestore();
}
