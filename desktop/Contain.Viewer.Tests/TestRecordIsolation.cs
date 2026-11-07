namespace Contain.Viewer.Tests;

/// <summary>Every harness and child process is bound to explicitly marked test data.</summary>
internal static class TestRecordIsolation
{
    internal const string Variable = "CONTAIN_VIEWER_TEST_ROOT";
    internal const string Marker = ".contain-records-test-root";
    internal const string MarkerText = "Contain isolated record library test data";
    internal static string Root { get; private set; } = "";

    internal static void Initialize(string[] args)
    {
        var existing = Environment.GetEnvironmentVariable(Variable);
        if (existing is not null)
        {
            Validate(existing);
            Root = Path.GetFullPath(existing);
            return;
        }
        var option = Array.IndexOf(args, "--private-output");
        var output = option >= 0 && option + 1 < args.Length ? Path.GetFullPath(args[option + 1])
            : args.Length == 4 && args[0] is "--personal-records-checks-only" or "--overview-checks-only" or "--lookup-transition-checks-only" ? Path.Combine(Path.GetFullPath(args[3]), "private")
            : args.Length == 2 && args[0].EndsWith("checks-only", StringComparison.Ordinal)
                ? Path.GetFullPath(args[1]) : Path.GetTempPath();
        var root = Path.Combine(output, "contain-records-isolated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Marker), MarkerText);
        Validate(root);
        Environment.SetEnvironmentVariable(Variable, root);
        Root = root;
    }

    internal static string Create(string label)
    {
        var root = Path.Combine(Root, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, Marker), MarkerText);
        Validate(root);
        return root;
    }

    internal static IDisposable Use(string root)
    {
        Validate(root);
        var previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, root);
        return new Restore(() => Environment.SetEnvironmentVariable(Variable, previous));
    }

    internal static void SelectCustomDatabase(Form form, string database)
    {
        ((CheckBox)form.Controls.Find("AdvancedToggle", true).Single()).Checked = true;
        ((CheckBox)form.Controls.Find("CustomDatabaseToggle", true).Single()).Checked = true;
        ((TextBox)form.Controls.Find("DatabaseSavePath", true).Single()).Text = database;
    }

    internal static void SelectManualObservation(Form form)
    {
        ((RadioButton)form.Controls.Find("ManualWatchMode", true).Single()).Checked = true;
    }

    private static void Validate(string root)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || !File.Exists(Path.Combine(root, Marker))
            || File.ReadAllText(Path.Combine(root, Marker)) != MarkerText)
            throw new InvalidDataException("The harness requires an existing, non-reparse, explicitly marked personal-record test root.");
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
