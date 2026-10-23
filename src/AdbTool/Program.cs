using System.Reflection;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool;

internal static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    /// <summary>Version for display, e.g. "1.3.0 (Test)" for test builds.</summary>
    public static string DisplayVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } info &&
        info.Contains("-test", StringComparison.OrdinalIgnoreCase)
            ? Version + " (Test)"
            : Version;
}

internal static class Program
{
    private static bool _smokeTest;

    [STAThread]
    private static int Main(string[] args)
    {
        // Diagnostic mode: ADBora.exe --check-apk <file.apk>
        // Writes name/version/icon information next to the APK (<file>.check.txt).
        int check = Array.IndexOf(args, "--check-apk");
        if (check >= 0 && check + 1 < args.Length)
            return CheckApk(args[check + 1]);

        _smokeTest = args.Contains("--smoke-test");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        // Test switch: behave as if an older version were installed (update check).
        int pretend = Array.IndexOf(args, "--pretend-version");
        if (pretend >= 0 && pretend + 1 < args.Length)
            Updater.PretendVersion = args[pretend + 1];

        // Only one ADBora at a time – two instances would fight over the same ADB device (build smoke test excepted)
        Mutex? instance = null;
        if (!_smokeTest)
        {
            instance = new Mutex(true, InstanceMutexName, out bool created);
            if (!created)
            {
                // Running instance with a window: bring it to the front and quit
                if (ActivateRunningInstance())
                {
                    instance.Dispose();
                    return 0;
                }
                // No window (yet / any more), e.g. the old instance is just closing after an update: wait briefly
                bool acquired;
                try { acquired = instance.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    ActivateRunningInstance();
                    instance.Dispose();
                    return 0;
                }
            }
        }

        ApplicationConfiguration.Initialize();
        Theme.InitScale();
        try
        {
            Application.Run(new MainForm(_smokeTest));
        }
        catch (Exception ex) when (_smokeTest)
        {
            ReportCrash(ex);
        }
        finally
        {
            if (instance is not null)
            {
                try { instance.ReleaseMutex(); } catch { }
                instance.Dispose();
            }
        }
        return 0;
    }

    private const string InstanceMutexName = @"Local\ADBora.SingleInstance";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Restores and focuses the window of an already running ADBora. False if none was found.</summary>
    private static bool ActivateRunningInstance()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var other in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id) continue;
                IntPtr handle = other.MainWindowHandle;
                if (handle == IntPtr.Zero) continue;
                if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
                SetForegroundWindow(handle);
                return true;
            }
        }
        return false;
    }

    private static int CheckApk(string path)
    {
        string output = path + ".check.txt";
        try
        {
            ApkMetadata meta = ApkMetadataReader.Read(path);
            var app = new AppEntry(meta.Package) { Name = meta.Label, Version = meta.VersionName, Apks = { "/base.apk" } };
            using Bitmap? icon = ImageLoader.Decode(meta.Icon);
            File.WriteAllText(output,
                $"{meta.Label}\n{meta.VersionName}\n{meta.VersionCode}\n{meta.Package}\n{BackupService.ApkFileNames(app)[0]}\n" +
                $"icon={(icon is not null)} {icon?.Width}x{icon?.Height} bytes={meta.Icon.Length}\n");
            if (icon is not null)
                icon.Save(path + ".icon.png", System.Drawing.Imaging.ImageFormat.Png);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, "ERROR\n" + ex);
            return 1;
        }
    }

    private static void ReportCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.AppendAllText(Path.Combine(AppSettings.DataDirectory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            if (_smokeTest)
            {
                // Build check (BUILD.ps1): no dialog, fail with exit code 3
                Console.Error.WriteLine(ex);
                Environment.Exit(3);
            }
            MessageBox.Show(ex?.Message ?? "Unknown error", "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
