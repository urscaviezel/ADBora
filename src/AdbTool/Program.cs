using System.Reflection;
using AdbTool.Backup;
using AdbTool.Core;
using AdbTool.UI;

namespace AdbTool;

internal static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Diagnostic mode: ADBora.exe --check-apk <file.apk>
        // Writes name/version/icon information next to the APK (<file>.check.txt).
        int check = Array.IndexOf(args, "--check-apk");
        if (check >= 0 && check + 1 < args.Length)
            return CheckApk(args[check + 1]);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Theme.InitScale();
        Application.Run(new MainForm(args.Contains("--smoke-test")));
        return 0;
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
            MessageBox.Show(ex?.Message ?? "Unknown error", "ADBora", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
