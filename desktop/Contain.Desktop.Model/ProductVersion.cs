namespace Contain.Viewer;

/// <summary>The new desktop uses the exact accepted Beta.10 core and strict v3 protocol.</summary>
public static class ProductVersion
{
    public const string Version = "0.4.0-beta.1";
    public const string CoreVersion = "0.3.2-beta.10";
    public const string CoreSource = "b0d2510db34cdf6630d2d550b69ccdbfe6da3fd4";
    public static string Display => "Contain " + Version;
    public static string CliVersion => "contain " + CoreVersion;
}
