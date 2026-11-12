namespace Contain.Viewer;

/// <summary>The desktop and rebuilt capture core share the release version and strict v3 protocol.</summary>
public static class ProductVersion
{
    public const string Version = "0.4.0-beta.2";
    public const string CoreVersion = "0.4.0-beta.2";
    public static string Display => "Contain " + Version;
    public static string CliVersion => "contain " + CoreVersion;
}
