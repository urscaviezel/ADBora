using System.Text.RegularExpressions;

namespace AdbTool.Core;

/// <summary>
/// "Hide serial numbers and user folder" (General settings): device serial numbers are shown
/// shortened in the UI (e.g. "2G0••••84") and the user profile folder (C:\Users\name) as
/// %USERPROFILE%, so screenshots can be shared without editing. IP addresses of Wi-Fi ADB
/// devices stay visible; ADB itself always uses the full serial, paths are expanded where used.
/// </summary>
internal static class Privacy
{
    private static bool _hide;

    public static bool HideSerial
    {
        get => _hide;
        set { if (_hide == value) return; _hide = value; Changed?.Invoke(); }
    }

    /// <summary>Setting switched (UI thread).</summary>
    public static event Action? Changed;

    public static string Mask(string serial)
    {
        if (!_hide || string.IsNullOrEmpty(serial)) return serial;
        if (Regex.IsMatch(serial, @"^\d+\.\d+\.\d+\.\d+:\d+$")) return serial;
        Match m = Regex.Match(serial, @"^adb-([^-]+)-(.+)$"); // wireless debugging (mDNS): adb-<serial>-<id>._adb-tls-connect._tcp
        if (m.Success) return "adb-" + Cut(m.Groups[1].Value) + "-" + m.Groups[2].Value;
        return Cut(serial);
    }

    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');

    /// <summary>Replaces the user profile folder in a text (paths, log lines) with %USERPROFILE%.</summary>
    public static string MaskPaths(string text)
    {
        if (!_hide || string.IsNullOrEmpty(text) || Profile.Length < 4) return text;
        return text.Replace(Profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Text box with a path: show masked or full, depending on the setting.</summary>
    public static string ShowPath(string path) => _hide ? MaskPaths(Environment.ExpandEnvironmentVariables(path)) : Environment.ExpandEnvironmentVariables(path);

    private static string Cut(string s) => s.Length <= 6 ? "••••" : s[..3] + "••••" + s[^2..];
}
