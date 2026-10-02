using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdbTool.Core;

internal sealed class SavedCommand
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
}

internal sealed class AppSettings
{
    public int Schema { get; set; } = 1;

    // General
    public string Language { get; set; } = "de";
    public string AdbPath { get; set; } = "";
    public string LastDeviceSerial { get; set; } = "";
    public bool StopAdbServerOnExit { get; set; } = true;
    public int LastTab { get; set; }
    public int WindowWidth { get; set; } = 1240;
    public int WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }

    // Updates
    public bool CheckUpdatesOnStart { get; set; } = true;
    public string SkippedUpdateVersion { get; set; } = "";

    // Backup
    public string BackupDestination { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "APK-Backups");
    public bool BackupSplitsAsApks { get; set; } = true;

    // Speed test
    public int SpeedPort { get; set; } = 5001;
    public double SpeedRateMbps { get; set; } = 1900;
    public double SpeedDurationSeconds { get; set; } = 600;

    // ADB commands
    public bool CommandTargetSelectedDevice { get; set; } = true;

    // APK install
    public bool InstallAllowDowngrade { get; set; }
    public bool InstallGrantPermissions { get; set; }
    public List<SavedCommand> SavedCommands { get; set; } = new();
    public List<string> CommandHistory { get; set; } = new();

    // ------------------------------------------------------------------

    /// <summary>
    /// Portable mode: a file "portable.flag" next to ADBora.exe keeps all
    /// settings and caches in the "Data" folder beside the program.
    /// Installed mode: %LOCALAPPDATA%\ADBora.
    /// </summary>
    public static bool IsPortable { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

    public static string DataDirectory { get; } = IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADBora");


    /// <summary>Temporary working files (APK label extraction).</summary>
    public static string TempDirectory => IsPortable
        ? Path.Combine(DataDirectory, "temp")
        : Path.Combine(Path.GetTempPath(), "ADBora");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        bool fresh = false;

        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : NewWithMigration(out fresh);
        }
        catch
        {
            settings = new AppSettings();
        }

        if (settings.SavedCommands.Count == 0 && fresh)
            settings.SavedCommands.AddRange(DefaultCommands());

        settings.Language = settings.Language == "en" ? "en" : "de";
        if (settings.SpeedPort is < 1 or > 65535) settings.SpeedPort = 5001;
        if (settings.SpeedDurationSeconds <= 0) settings.SpeedDurationSeconds = 600;
        if (settings.SpeedRateMbps <= 0) settings.SpeedRateMbps = 1900;
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch
        {
            // Settings are a convenience; the tool keeps working without them.
        }
    }

    /// <summary>
    /// First start: take over settings from the two former stand-alone tools
    /// (APK Backup Tool and ADB USB Speed Test) if they exist.
    /// </summary>
    private static AppSettings NewWithMigration(out bool fresh)
    {
        fresh = true;
        var settings = new AppSettings();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        try
        {
            string backupSettings = Path.Combine(local, "APKBackupTool", "settings.json");
            if (File.Exists(backupSettings) && JsonNode.Parse(File.ReadAllText(backupSettings)) is JsonObject o)
            {
                if (o["adb"]?.GetValue<string>() is { Length: > 0 } adb) settings.AdbPath = adb;
                if (o["destination"]?.GetValue<string>() is { Length: > 0 } dest) settings.BackupDestination = dest;
                if (o["language"]?.GetValue<string>() is "en") settings.Language = "en";
            }
        }
        catch { }

        try
        {
            string speedSettings = Path.Combine(local, "ADB_USB_Speed_Test", "settings.json");
            if (File.Exists(speedSettings) && JsonNode.Parse(File.ReadAllText(speedSettings)) is JsonObject o)
            {
                if (o["Port"] is JsonNode p && p.GetValue<int>() is int port and > 0 and < 65536) settings.SpeedPort = port;
                if (o["RateMbps"] is JsonNode r && r.GetValue<double>() is double rate and > 0) settings.SpeedRateMbps = rate;
                if (o["DurationSeconds"] is JsonNode d && d.GetValue<double>() is double dur and > 0) settings.SpeedDurationSeconds = dur;
            }
        }
        catch { }

        return settings;
    }

    public static IEnumerable<SavedCommand> DefaultCommands() => new[]
    {
        new SavedCommand { Name = "Geräteliste / Device list", Command = "devices -l" },
        new SavedCommand { Name = "Modell / Model", Command = "shell getprop ro.product.model" },
        new SavedCommand { Name = "Android-Version", Command = "shell getprop ro.build.version.release" },
        new SavedCommand { Name = "Akku / Battery", Command = "shell dumpsys battery" },
        new SavedCommand { Name = "Speicher / Storage", Command = "shell df -h /data /sdcard" },
        new SavedCommand { Name = "Nachinstallierte Apps / User apps", Command = "shell pm list packages -3" },
        new SavedCommand { Name = "Display", Command = "shell wm size" },
        new SavedCommand { Name = "Aktive App / Foreground app", Command = "shell dumpsys activity activities | grep -E \"mResumedActivity|topResumedActivity\"" },
        new SavedCommand { Name = "Logcat (letzte 200 Zeilen / last 200 lines)", Command = "logcat -d -t 200" },
    };
}
