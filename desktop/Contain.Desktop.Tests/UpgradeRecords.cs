namespace Contain.Desktop.Tests;

internal static class UpgradeRecords
{
    internal static void CheckDefaultFixture(string markedRoot)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var relative = Path.Combine("Contain", "Records", "contain.db");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || !File.ReadAllBytes(Path.Combine(markedRoot, relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(local, relative))))
            throw new InvalidOperationException("Default library must match the marked synthetic fixture on a hosted runner.");
    }

    internal static async Task VerifyAsync(string install, string root, bool seed)
    {
        var database = Path.Combine(root, "Contain", "Records", "contain.db");
        if (seed)
        {
            // The upgrade must preserve a personal library distinguishable from the packaged sample.
            if (File.ReadAllText(Path.Combine(root, ".contain-records-test-root")) != "Contain isolated record library test data")
                throw new InvalidOperationException("Expected a marked fixture root.");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            File.Copy(Path.Combine(install, "samples", "viewer-demo.db"), database, overwrite: false);
            SyntheticSql.PrepareOutcomes(root, database);
        }
        using var desktop = new DesktopDriver(install, Path.Combine(root, "working"), root, useHostedUpgradeFixture: !seed);
        await desktop.ReadyAsync();
        string[] expected = ["synthetic-finished", "synthetic-historical", "synthetic-interrupted"];
        await desktop.UntilAsync("read_persisted_records", "RecordList", async () =>
            (await desktop.RowIdsAsync("RecordList", "record:")).Order(StringComparer.Ordinal).SequenceEqual(expected));
        await desktop.SelectAsync("record:synthetic-finished");
        await desktop.UntilAsync("open_persisted_record", "SessionTitle", async () =>
            await desktop.TryHelpAsync("SessionTitle") == "session:synthetic-finished"
            && await desktop.TryTextAsync("SessionTitle") == "SYNTHETIC 同名安装记录");
        await desktop.CloseAsync();
    }
}
