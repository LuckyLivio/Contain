namespace Contain.Viewer;

public interface IInstallDialogs
{
    string? ChooseInstaller(IWin32Window owner);
    string? ChooseWatchRoot(IWin32Window owner);
    string? ChooseSaveDatabase(IWin32Window owner);
}

public sealed class InstallDialogs : IInstallDialogs
{
    public string? ChooseInstaller(IWin32Window owner)
    {
        using var dialog = new OpenFileDialog { Title = "选择 EXE 安装器", Filter = "EXE 安装器 (*.exe)|*.exe",
            CheckFileExists = true, CheckPathExists = true, Multiselect = false, RestoreDirectory = true };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public string? ChooseWatchRoot(IWin32Window owner)
    {
        using var dialog = new FolderBrowserDialog { Description = "选择已存在的观察目录（不会创建软件目标目录）", UseDescriptionForTitle = true, ShowNewFolderButton = false };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? ChooseSaveDatabase(IWin32Window owner)
    {
        using var dialog = new SaveFileDialog { Title = "选择观察范围之外的记录数据库", Filter = "Contain 数据库 (*.db)|*.db", DefaultExt = "db",
            AddExtension = true, CheckPathExists = true, OverwritePrompt = false, RestoreDirectory = true,
            FileName = "contain-record-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db" };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }
}
