using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Contain.Desktop.Model;

namespace Contain.Desktop.Views;

public partial class RecordDetailsView : UserControl
{
    public RecordDetailsView() => InitializeComponent();
    private MainWindow Shell => (MainWindow)DataContext;
    public void ShowOverview() => Tabs.SelectedIndex = 0;
    public void ShowEvents() => Tabs.SelectedIndex = 1;
    private void OnBack(object sender, RoutedEventArgs e) => Shell.ShowRecords();
    private void OnViewEvents(object sender, RoutedEventArgs e) => ShowEvents();
    private async void OnSearch(object sender, RoutedEventArgs e) => await Shell.RunUiAsync(() => Shell.Records.SearchAsync(Shell.Records.SearchDraft));
    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await Shell.RunUiAsync(() => Shell.Records.SearchAsync(Shell.Records.SearchDraft));
    }
    private async void OnClearSearch(object sender, RoutedEventArgs e) => await Shell.RunUiAsync(Shell.Records.ClearSearchAsync);
    private async void OnPreviousEvents(object sender, RoutedEventArgs e) => await Shell.RunUiAsync(Shell.Records.PreviousEventsAsync);
    private async void OnNextEvents(object sender, RoutedEventArgs e) => await Shell.RunUiAsync(Shell.Records.NextEventsAsync);
    private async void OnEventSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is EventRow row && row.Id != Shell.Records.SelectedEventId)
            await Shell.RunUiAsync(() => Shell.Records.SelectEventAsync(row));
    }
    private void OnCopyEventId(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.Records.SelectedEventId);
    private void OnCopyEventPath(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.Records.SelectedEventResource);
    private async void OnExportPage(object sender, RoutedEventArgs e) => await Shell.ExportAsync(false);
    private async void OnExportMatches(object sender, RoutedEventArgs e) => await Shell.ExportAsync(true);
    private void OnCancelExport(object sender, RoutedEventArgs e) => Shell.Records.CancelExport();
    private void OnChangeFilter(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && ChangeFilterBox.Text != Shell.Records.ChangeFilter)
            Shell.Records.SetChangeFilter(ChangeFilterBox.Text);
    }
    private void OnPreviousChanges(object sender, RoutedEventArgs e) => Shell.Records.PreviousChanges();
    private void OnNextChanges(object sender, RoutedEventArgs e) => Shell.Records.NextChanges();
    private void OnChangeSelection(object sender, SelectionChangedEventArgs e)
    {
        if (ChangeList.SelectedItem is ChangeRow row) Shell.Records.SelectChange(row);
    }
    private void OnCopyDiagnostics(object sender, RoutedEventArgs e) => Shell.CopyText(Shell.Records.DiagnosticSummary);
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (EventColumns is null) return;
        var compact = ActualWidth < 760;
        ListColumn.Width = new GridLength(compact ? 1 : 3, GridUnitType.Star);
        ExplanationColumn.Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Grid.SetColumn(ExplanationSurface, compact ? 0 : 1);
        Grid.SetRow(ExplanationSurface, compact ? 1 : 0);
        EventListSurface.Margin = new Thickness(0, 0, compact ? 0 : 16, 16);
        EventList.Height = compact ? 260 : 390;
    }
}
