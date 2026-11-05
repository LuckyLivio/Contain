namespace Contain.Viewer;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var records = PersonalRecords.ForCurrentUser();
            Application.Run(new ViewerForm(new CoreClient(Path.Combine(AppContext.BaseDirectory, "contain.exe")), records: records));
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show("无法定位我的记录：" + error.Message + "\r\n未打开或创建替代记录库。", "Contain · 记录库不可用", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
