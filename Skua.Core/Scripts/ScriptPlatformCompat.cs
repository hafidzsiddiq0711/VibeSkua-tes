using System.Text.RegularExpressions;

namespace Skua.Core.Scripts;

/// <summary>
/// Small source fixes so scripts written for Windows run where Skua runs elsewhere (Skua Mobile
/// on Android). Applied when a script file is read, so compile caches follow the fixed source.
/// Script files on disk are never changed.
/// </summary>
public static partial class ScriptPlatformCompat
{
    public static bool Enabled { get; set; } = !OperatingSystem.IsWindows();

    // CoreBots' SkuaVersionCheckerAsync stops the bot unless OperatingSystem.IsWindowsVersionAtLeast(10).
    [GeneratedRegex(@"(?:global::)?(?:System\.)?OperatingSystem\.IsWindowsVersionAtLeast\s*\([^)]*\)")]
    private static partial Regex WindowsVersionCheck();

    public static string Apply(string source)
    {
        if (!Enabled || string.IsNullOrEmpty(source) || !source.Contains("IsWindowsVersionAtLeast", StringComparison.Ordinal))
            return source;
        return WindowsVersionCheck().Replace(source, "true");
    }

    public static string ReadScript(string path) => Apply(File.ReadAllText(path));
}
