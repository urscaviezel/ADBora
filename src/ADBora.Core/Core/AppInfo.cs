using System.Reflection;

namespace AdbTool;

internal static class AppInfo
{
    // The version lives in the app's project file; the entry assembly is the app (ADBora).
    private static Assembly Main => Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

    public static string Version =>
        Main.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    /// <summary>Version for display, e.g. "1.3.0 (Test)" for test builds.</summary>
    public static string DisplayVersion =>
        Main.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } info &&
        info.Contains("-test", StringComparison.OrdinalIgnoreCase)
            ? Version + " (Test)"
            : Version;
}
