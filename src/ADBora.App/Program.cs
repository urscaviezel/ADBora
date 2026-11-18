using Avalonia;
using AdbTool.Core;

namespace AdbTool;

internal static class Program
{
    public static bool SmokeTest { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (Diagnostics.Run(args) is int code)
            return code;

        SmokeTest = args.Contains("--smoke-test");

        // Test switch: behave as if an older version were installed (update check).
        int pretend = Array.IndexOf(args, "--pretend-version");
        if (pretend >= 0 && pretend + 1 < args.Length)
            Updater.PretendVersion = args[pretend + 1];
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the designer
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UI.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
