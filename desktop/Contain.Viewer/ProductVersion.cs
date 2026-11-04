using System.Reflection;

namespace Contain.Viewer;

/// <summary>The assembly version comes from Cargo.toml through Directory.Build.props.</summary>
public static class ProductVersion
{
    public static string Version { get; } = typeof(ProductVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static string Display => "Contain " + Version;
    public static string CliVersion => "contain " + Version;
}
