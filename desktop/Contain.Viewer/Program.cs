namespace Contain.Viewer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ViewerForm(new CoreClient(Path.Combine(AppContext.BaseDirectory, "contain.exe"))));
    }
}
