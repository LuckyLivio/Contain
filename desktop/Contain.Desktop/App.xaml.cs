using System.Windows;
using System.Windows.Media;
using Contain.Viewer;
using Wpf.Ui.Appearance;

namespace Contain.Desktop;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        ApplicationThemeManager.Apply(ApplicationTheme.Light);
        ApplicationAccentColorManager.Apply(Color.FromRgb(19, 125, 116), ApplicationTheme.Light);
        try
        {
            var window = new MainWindow(PersonalRecords.ForCurrentUser());
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            // A failed isolation or personal-library resolution must never fall back
            // to another library. This is the only pre-window fatal error surface.
            MessageBox.Show($"无法启动 Contain：{error.GetType().Name}：{error.Message}\n未打开或创建替代记录库。",
                "Contain · 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
