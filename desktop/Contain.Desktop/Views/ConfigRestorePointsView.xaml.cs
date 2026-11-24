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
    private void OnBeginUndoPreview(object sender, RoutedEventArgs e) => Host?.BeginUndoConfigRestorePreview();
    private void OnConfirmUndoRestore(object sender, RoutedEventArgs e) => Host?.ConfirmUndoConfigRestore();
    private void OnCancelUndoPreview(object sender, RoutedEventArgs e) => Host?.CancelUndoConfigRestorePreview();
    private void OnRecheckRecovery(object sender, RoutedEventArgs e) => Host?.RecheckConfigRecovery();
    private void OnUndoFromRecovery(object sender, RoutedEventArgs e) => Host?.BeginUndoFromConfigRecovery();
    private void OnDismissRecovery(object sender, RoutedEventArgs e) => Host?.DismissOrArchiveConfigRecovery();
    private void OnViewOriginalCopy(object sender, RoutedEventArgs e) => Host?.ViewConfigOriginalCopy();
    private void OnExportOriginalCopy(object sender, RoutedEventArgs e) => Host?.ExportConfigOriginalCopy();
    private void OnCloseOriginalCopyPreview(object sender, RoutedEventArgs e) => Host?.CloseConfigOriginalCopyPreview();
    private void OnBeginExportRestorePoint(object sender, RoutedEventArgs e) => Host?.BeginExportConfigRestorePoint();
    private void OnPickExportPackagePath(object sender, RoutedEventArgs e) => Host?.PickConfigExportPackagePath();
    private void OnConfirmExportRestorePoint(object sender, RoutedEventArgs e) => Host?.ConfirmExportConfigRestorePoint();
    private void OnCancelExportRestorePoint(object sender, RoutedEventArgs e) => Host?.CancelExportConfigRestorePoint();
    private void OnOpenImportWorkspace(object sender, RoutedEventArgs e) => Host?.OpenConfigImportWorkspace();
    private void OnPickImportPackage(object sender, RoutedEventArgs e) => Host?.PickConfigImportPackage();
    private void OnPickImportTarget(object sender, RoutedEventArgs e) => Host?.PickConfigImportTarget();
    private void OnRecheckImportTarget(object sender, RoutedEventArgs e) => Host?.RecheckConfigImportTarget();
    private void OnToggleImportTargetHelp(object sender, RoutedEventArgs e) => Host?.ToggleConfigImportTargetHelp();
    private void OnConfirmImportRestore(object sender, RoutedEventArgs e) => Host?.ConfirmConfigImportRestore();
    private void OnCancelImport(object sender, RoutedEventArgs e) => Host?.CancelConfigImport();
}
