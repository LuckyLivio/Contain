using System.Text.Json;

namespace Contain.Desktop.Model.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var passed = new List<string>();
        string? failure = null;
        string? message = null;
        try
        {
            passed.AddRange(await InstallChecks.RunAsync());
            passed.AddRange(await RecordChecks.RunAsync());
            passed.AddRange(ExportChecks.Run());
        }
        catch (Exception error)
        {
            failure = error.GetType().Name;
            message = error.Message;
            Console.Error.WriteLine(error);
        }
        var result = JsonSerializer.Serialize(new
        {
            protocol = "contain-modern-model-checks-1", accepted = failure is null,
            synthetic = true, native_windows_ui = false, real_etw = false,
            checks = passed, passed = passed.Count, failure, message
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(result);
        if (args.Length == 1) await File.WriteAllTextAsync(args[0], result + Environment.NewLine);
        return failure is null ? 0 : 1;
    }
}
