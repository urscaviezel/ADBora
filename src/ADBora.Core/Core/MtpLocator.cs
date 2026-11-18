using System.Diagnostics;

namespace AdbTool.Core;

/// <summary>
/// Finds the device's file system (MTP) for the file manager.
/// Windows: the device under "This PC" (Shell.Application). Linux: a gvfs mount
/// ($XDG_RUNTIME_DIR/gvfs/mtp:host=…, GNOME and others); otherwise the generic mtp:/ view (KDE Dolphin / kio).
/// </summary>
internal static class MtpLocator
{
    private static string[] Names(AdbDevice device) => new[] { device.Model, device.DisplayName, device.Product, device.Device }
        .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();

    public static string? Find(AdbDevice device) => Platform.IsWindows ? FindWindows(device) : FindLinux(device);

    public static void Open(string path)
    {
        if (Platform.IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else Platform.Open(path);
    }

    /// <summary>Opens the list of all devices ("This PC" / mtp:/).</summary>
    public static void OpenOverview()
    {
        if (Platform.IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true });
        else Platform.Open("mtp:/");
    }

    private static string? FindLinux(AdbDevice device)
    {
        try
        {
            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "";
            string gvfs = Path.Combine(runtime, "gvfs");
            if (runtime.Length == 0 || !Directory.Exists(gvfs)) return null;
            // e.g. "mtp:host=Play_For_Dream_PFDM-MR_SN%3AD3H…" – model names use '_' for spaces
            string[] names = Names(device).Select(n => n.Replace(' ', '_')).ToArray();
            string[] mounts = Directory.GetDirectories(gvfs, "mtp:host=*");
            return mounts.FirstOrDefault(m => names.Any(n => Path.GetFileName(m).Contains(n, StringComparison.OrdinalIgnoreCase)))
                   ?? (mounts.Length == 1 ? mounts[0] : null);
        }
        catch { return null; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindWindowsCore(AdbDevice device)
    {
        Type? type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return null;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic? computer = shell.NameSpace(17); // ssfDRIVES = This PC
        if (computer is null) return null;

        string[] names = Names(device);
        string? fallback = null;
        foreach (dynamic item in computer.Items())
        {
            string name = (string)item.Name;
            string path = (string)item.Path;
            if (!path.Contains("usb#", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("::", StringComparison.Ordinal))
                continue; // normal drives
            if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return path;
            if (names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(name, StringComparison.OrdinalIgnoreCase)))
                fallback ??= path;
        }
        return fallback;
    }

    private static string? FindWindows(AdbDevice device)
    {
        try { return OperatingSystem.IsWindows() ? FindWindowsCore(device) : null; }
        catch { return null; }
    }
}
