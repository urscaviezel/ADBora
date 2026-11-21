using Avalonia;
using AdbTool.Core;

namespace AdbTool;

internal static class Program
{
    public static bool SmokeTest { get; private set; }
    /// <summary>Diagnostic: --ui-test speed &lt;seconds&gt; &lt;adb|direct&gt; &lt;out.png&gt;</summary>
    public static string[]? UiTest { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (Diagnostics.Run(args) is int code)
            return code;

        // Diagnostic: --viewer-test <serial> <quest|pfd|pico> <seconds> <out.png> – cast window with the real session, then a picture of it
        int viewer = Array.IndexOf(args, "--viewer-test");
        if (viewer >= 0 && viewer + 4 < args.Length)
        {
            UI.ViewerTest.Args = args[(viewer + 1)..(viewer + 5)];
            return AppBuilder.Configure<UI.ViewerTest>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
        }

        SmokeTest = args.Contains("--smoke-test");
        int uiTest = Array.IndexOf(args, "--ui-test");
        if (uiTest >= 0 && uiTest + 4 < args.Length)
        {
            UiTest = args[(uiTest + 1)..(uiTest + 5)];
            SmokeTest = true; // no single instance, settings are not saved
        }
        // Build smoke test / UI tests run against the real user profile: never write the user's settings
        AppSettings.SavingDisabled = SmokeTest;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };

        // Test switch: behave as if an older version were installed (update check).
        int pretend = Array.IndexOf(args, "--pretend-version");
        if (pretend >= 0 && pretend + 1 < args.Length)
            Updater.PretendVersion = args[pretend + 1];
        // Only one ADBora at a time (the build smoke test excepted)
        if (!SmokeTest && !SingleInstance.Acquire())
            return 0;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            return 3;
        }
        finally
        {
            if (!SmokeTest) SingleInstance.Release();
        }
    }

    /// <summary>Writes the error to error.log in the data folder.</summary>
    public static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    /// <summary>Unexpected error: log it; build check (--smoke-test) fails with exit code 3.</summary>
    public static void ReportCrash(Exception? ex)
    {
        LogCrash(ex);
        if (SmokeTest)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(3);
        }
    }

    // Also used by the designer
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<UI.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
