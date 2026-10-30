namespace Contain.Viewer;

public interface IViewerDialogs
{
    string? ChooseDatabase(IWin32Window owner);
    string? ChooseReport(IWin32Window owner, string suggestedName);
}

public sealed class ViewerDialogs : IViewerDialogs
{
    public string? ChooseDatabase(IWin32Window owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "打开已有 Contain 记录数据库",
            Filter = "Contain / SQLite 数据库 (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public string? ChooseReport(IWin32Window owner, string suggestedName)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "保存本地只读报告（请选择新文件）",
            Filter = "HTML 本地报告 (*.html)|*.html",
            FileName = suggestedName,
            DefaultExt = "html",
            AddExtension = true,
            OverwritePrompt = true,
            CheckPathExists = true,
            RestoreDirectory = true
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }
}
