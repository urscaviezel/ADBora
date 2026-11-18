using System.Diagnostics;

namespace AdbTool.Core;

/// <summary>Differences between Windows and Linux in one place.</summary>
internal static class Platform
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>"adb" → "adb.exe" on Windows, "adb" elsewhere.</summary>
    public static string Exe(string name) => IsWindows ? name + ".exe" : name;

    /// <summary>File name of the ADBora program itself.</summary>
    public static string AppExe => Exe("ADBora");

    /// <summary>Home directory (Linux) or user profile (Windows).</summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Opens a URL or file with the default application of the desktop.</summary>
    public static void Open(string target)
    {
        try
        {
            if (IsWindows) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false })?.Dispose();
        }
        catch { }
    }

    /// <summary>Shows a folder in the file manager (Explorer / Dolphin, Nautilus …).</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            if (IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else Open(path);
        }
        catch { }
    }

    /// <summary>Directories searched for shared libraries on Linux (FFmpeg, PulseAudio).</summary>
    public static IEnumerable<string> LibraryDirectories()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return dir;
        foreach (string dir in new[] { "/usr/lib", "/usr/lib64", "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/local/lib", "/lib/x86_64-linux-gnu" })
            yield return dir;
    }

    /// <summary>
    /// Newest file of a native library. Windows: "avcodec-*.dll" in <paramref name="windowsFolder"/>;
    /// Linux: "libavcodec.so.N" in the folder first, then in the system library directories.
    /// </summary>
    public static string? FindLibrary(string baseName, string? windowsFolder)
    {
        static string? Newest(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                return Directory.EnumerateFiles(dir, pattern)
                    .OrderByDescending(f => VersionOf(f)).ThenByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            }
            catch { return null; }
        }
        if (IsWindows)
            return windowsFolder is null ? null : Newest(windowsFolder, baseName + "-*.dll");
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(windowsFolder)) dirs.Add(windowsFolder);
        dirs.AddRange(LibraryDirectories());
        foreach (string dir in dirs)
        {
            // the major version (libavcodec.so.63) is the ABI; plain .so is only a dev symlink
            if (Newest(dir, "lib" + baseName + ".so.*") is { } found)
                return Path.Combine(dir, "lib" + baseName + ".so." + VersionOf(found).Major);
        }
        return null;
    }

    private static Version VersionOf(string file)
    {
        string name = Path.GetFileName(file);
        int i = name.IndexOf(".so.", StringComparison.Ordinal);
        string v = i >= 0 ? name[(i + 4)..] : new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (!v.Contains('.')) v += ".0";
        return Version.TryParse(v, out Version? ver) ? ver : new Version(0, 0);
    }
}
