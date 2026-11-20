using System.Text.RegularExpressions;

namespace Contain.Desktop.Tests;

internal static class ScreenshotPrivacy
{
    internal static bool ContainsHostPath(string text) => Regex.IsMatch(text,
        @"(?<![a-z])(?!(?:c:[\\/]ContainDemo(?:[\\/]|$)))[a-z]:[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static void VerifyContract()
    {
        if (ContainsHostPath("请从 https://jrsoftware.org/isdl.php 安装 Inno Setup 6。")
            || ContainsHostPath(@"C:\ContainDemo\HelloWpf")
            || !ContainsHostPath(@"输出C:\Users\runner\project")
            || !ContainsHostPath(@"https://dotnet.microsoft.com/download 和 D:\ContainDemo\personal")
            || !ContainsHostPath(@"C:\ContainDemo\HelloWpf 后附 C:\Users\runner"))
            throw new AssertionException("screenshot_host_path_mask");
    }
}
