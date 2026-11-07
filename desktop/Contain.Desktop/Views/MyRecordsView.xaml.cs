using System.Windows;
using System.Windows.Controls;
using Contain.Desktop.Model;

namespace Contain.Desktop.Views;

public partial class MyRecordsView : UserControl
{
    public MyRecordsView() => InitializeComponent();
    public void PrepareForShow() => RecordList.SelectedItem = null;
    private MainWindow Shell => (MainWindow)DataContext;
    private void OnNew(object sender, RoutedEventArgs e) => Shell.ShowNewRecord();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Shell.RefreshRecordsAsync();
    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecordList.SelectedItem is SessionRow row) await Shell.OpenRecordAsync(row.Id);
    }
}
