namespace AdbTool.Core;

internal enum DeviceFamily { Android, Quest, Pico }

internal enum CommandRisk { Safe, Caution, Dangerous }

/// <summary>A ready-made ADB command with description (shown in the ADB Commands tab).</summary>
internal sealed record CommandPreset(
    string CategoryDe, string CategoryEn,
    string NameDe, string NameEn,
    string Command,
    string DescDe, string DescEn,
    CommandRisk Risk = CommandRisk.Safe,
    DeviceFamily? Family = null)
{
    public string Category => Loc.T(CategoryDe, CategoryEn);
    public string Name => Loc.T(NameDe, NameEn);
    public string Description => Loc.T(DescDe, DescEn);

    /// <summary>Contains a placeholder like &lt;paket&gt; that must be filled in before running.</summary>
    public bool NeedsInput => Command.Contains('<') && Command.Contains('>');
}

/// <summary>What ADBora knows about the active device (read once per device via getprop).</summary>
internal sealed record DeviceProfile(string Serial, string Manufacturer, string Brand, string Model, string Android, DeviceFamily Family)
{
    public string FamilyText => Family switch
    {
        DeviceFamily.Quest => "Meta Quest",
        DeviceFamily.Pico => "Pico",
        _ => "Android"
    };

    public static DeviceFamily Detect(string manufacturer, string brand, string model)
    {
        string all = $"{manufacturer} {brand} {model}".ToLowerInvariant();
        if (all.Contains("oculus") || all.Contains("quest") || manufacturer.Equals("meta", StringComparison.OrdinalIgnoreCase))
            return DeviceFamily.Quest;
        if (all.Contains("pico"))
            return DeviceFamily.Pico;
        return DeviceFamily.Android;
    }
}

/// <summary>Curated command packs: general Android commands plus device-specific ones.</summary>
internal static class CommandCatalog
{
    public static IEnumerable<CommandPreset> For(DeviceFamily? family) =>
        All.Where(p => p.Family is null || p.Family == family);

    private static readonly CommandPreset[] All =
    {
        // ---------------- Meta Quest ----------------
        Q("Bildwiederholrate 72 Hz", "Refresh rate 72 Hz", "shell setprop debug.oculus.refreshRate 72",
          "Erzwingt 72 Hz für Apps, die es unterstützen. Gilt bis zum Neustart.", "Forces 72 Hz for apps that support it. Lasts until reboot."),
        Q("Bildwiederholrate 90 Hz", "Refresh rate 90 Hz", "shell setprop debug.oculus.refreshRate 90",
          "Erzwingt 90 Hz für Apps, die es unterstützen. Gilt bis zum Neustart.", "Forces 90 Hz for apps that support it. Lasts until reboot."),
        Q("Bildwiederholrate 120 Hz", "Refresh rate 120 Hz", "shell setprop debug.oculus.refreshRate 120",
          "Erzwingt 120 Hz (Quest 2/3/3S/Pro). Kostet Akku und Leistung. Gilt bis zum Neustart.", "Forces 120 Hz (Quest 2/3/3S/Pro). Costs battery and performance. Lasts until reboot."),
        Q("Texturgrösse 2048 × 2048", "Texture size 2048 × 2048", "shell setprop debug.oculus.textureWidth 2048; setprop debug.oculus.textureHeight 2048",
          "Render-Auflösung pro Auge für Apps, die die Standardgrösse nutzen. Wirkt beim nächsten App-Start, gilt bis zum Neustart.", "Render resolution per eye for apps using the default size. Applies at the next app start, lasts until reboot."),
        Q("Texturgrösse 2560 × 2560", "Texture size 2560 × 2560", "shell setprop debug.oculus.textureWidth 2560; setprop debug.oculus.textureHeight 2560",
          "Höhere Schärfe, deutlich mehr GPU-Last. Wirkt beim nächsten App-Start, gilt bis zum Neustart.", "Sharper image, much more GPU load. Applies at the next app start, lasts until reboot."),
        Q("Texturgrösse zurücksetzen", "Reset texture size", "shell setprop debug.oculus.textureWidth 0; setprop debug.oculus.textureHeight 0",
          "Zurück zur Standard-Auflösung der Apps.", "Back to the apps' default resolution."),
        Q("CPU-/GPU-Level 4 (max.)", "CPU/GPU level 4 (max)", "shell setprop debug.oculus.cpuLevel 4; setprop debug.oculus.gpuLevel 4",
          "Höchste Taktstufen. Mehr Leistung, mehr Wärme und Akkuverbrauch. Gilt bis zum Neustart.", "Highest clock levels. More performance, more heat and battery drain. Lasts until reboot.", CommandRisk.Caution),
        Q("Foveated Rendering aus", "Foveated rendering off", "shell setprop debug.oculus.foveation.level 0",
          "Schaltet Fixed Foveated Rendering ab (schärfere Ränder, mehr GPU-Last).", "Turns off fixed foveated rendering (sharper edges, more GPU load)."),
        Q("Näherungssensor aus", "Proximity sensor off", "shell am broadcast -a com.oculus.vrpowermanager.prox_close",
          "Headset bleibt aktiv, auch wenn es nicht getragen wird (z. B. für Casting oder Tests).", "Headset stays awake even when not worn (e.g. for casting or tests)."),
        Q("Näherungssensor wieder an", "Proximity sensor on again", "shell am broadcast -a com.oculus.vrpowermanager.automation_disable",
          "Normales Verhalten des Näherungssensors.", "Normal proximity sensor behaviour."),
        Q("Guardian pausieren", "Pause Guardian", "shell setprop debug.oculus.guardian_pause 1",
          "Blendet die Begrenzung aus (nur mit Entwicklermodus). Achtung auf die Umgebung!", "Hides the boundary (developer mode only). Mind your surroundings!", CommandRisk.Caution),
        Q("Guardian wieder aktivieren", "Re-enable Guardian", "shell setprop debug.oculus.guardian_pause 0",
          "Begrenzung wieder einschalten.", "Turns the boundary back on."),
        Q("Aufnahme-Auflösung 1920 × 1080", "Capture resolution 1920 × 1080", "shell setprop debug.oculus.capture.width 1920; setprop debug.oculus.capture.height 1080",
          "Auflösung für Videoaufnahmen auf dem Headset.", "Resolution for video recordings on the headset."),
        Q("Aktuelle Oculus-Einstellungen", "Current Oculus settings", "shell getprop | grep debug.oculus",
          "Zeigt alle gesetzten debug.oculus-Werte.", "Shows all debug.oculus values that are set."),
        Q("Controller-Akkus", "Controller batteries", "shell dumpsys OVRRemoteService | grep -i battery",
          "Akkustand der gekoppelten Controller.", "Battery level of the paired controllers."),

        // ---------------- Pico ----------------
        P("Pico-Systemwerte", "Pico system properties", "shell getprop | grep -i pvr",
          "Zeigt die Pico-spezifischen Systemwerte (pvr).", "Shows the Pico specific system properties (pvr)."),

        // ---------------- Device info (all) ----------------
        A("Geräteinfo", "Device info", "Modell und Hersteller", "Model and manufacturer", "shell getprop ro.product.manufacturer; getprop ro.product.model",
          "Hersteller und Modell.", "Manufacturer and model."),
        A("Geräteinfo", "Device info", "Android-Version und Patch", "Android version and patch", "shell getprop ro.build.version.release; getprop ro.build.version.security_patch",
          "Android-Version und Sicherheitspatch.", "Android version and security patch."),
        A("Geräteinfo", "Device info", "Akku", "Battery", "shell dumpsys battery",
          "Ladestand, Temperatur, Ladezustand.", "Level, temperature, charging state."),
        A("Geräteinfo", "Device info", "Speicherplatz", "Storage", "shell df -h /data /sdcard",
          "Belegter und freier Speicher.", "Used and free storage."),
        A("Geräteinfo", "Device info", "Arbeitsspeicher", "Memory", "shell cat /proc/meminfo | head -5",
          "RAM gesamt und frei.", "Total and free RAM."),
        A("Geräteinfo", "Device info", "Temperaturen", "Temperatures", "shell dumpsys thermalservice | head -60",
          "Temperatursensoren und Drosselungsstatus.", "Temperature sensors and throttling status."),
        A("Geräteinfo", "Device info", "Prozesse (Top 15)", "Processes (top 15)", "shell top -b -n 1 -m 15",
          "Welche Prozesse gerade CPU verbrauchen.", "Which processes use CPU right now."),
        A("Geräteinfo", "Device info", "Display", "Display", "shell wm size; wm density",
          "Auflösung und Pixeldichte.", "Resolution and density."),
        A("Geräteinfo", "Device info", "IP-Adresse (WLAN)", "IP address (Wi-Fi)", "shell ip -4 addr show wlan0",
          "IPv4-Adresse des WLAN.", "IPv4 address of the Wi-Fi."),

        // ---------------- Apps (all) ----------------
        A("Apps", "Apps", "Nachinstallierte Apps", "User apps", "shell pm list packages -3",
          "Alle nachinstallierten Pakete.", "All user-installed packages."),
        A("Apps", "Apps", "Aktive App", "Foreground app", "shell dumpsys activity activities | grep -E \"mResumedActivity|topResumedActivity\"",
          "Welche App gerade im Vordergrund ist.", "Which app is in the foreground."),
        A("Apps", "Apps", "App starten …", "Start app …", "shell monkey -p <paket> -c android.intent.category.LAUNCHER 1",
          "<paket> durch den Paketnamen ersetzen (siehe „Entdecken“ → Apps).", "Replace <paket> with the package name (see \"Explore\" → Apps)."),
        A("Apps", "Apps", "App beenden …", "Stop app …", "shell am force-stop <paket>",
          "Beendet die App sofort.", "Stops the app immediately."),
        A("Apps", "Apps", "App-Version …", "App version …", "shell dumpsys package <paket> | grep -E \"versionName|versionCode|firstInstallTime|lastUpdateTime\"",
          "Version und Installationsdatum einer App.", "Version and install date of an app."),
        A("Apps", "Apps", "App-Daten löschen …", "Clear app data …", "shell pm clear <paket>",
          "Löscht ALLE Daten der App (Logins, Spielstände). Nicht rückgängig zu machen!", "Deletes ALL data of the app (logins, save games). Cannot be undone!", CommandRisk.Dangerous),

        // ---------------- Input (all) ----------------
        A("Eingabe", "Input", "Display aufwecken", "Wake up display", "shell input keyevent KEYCODE_WAKEUP", "Weckt das Gerät auf.", "Wakes the device."),
        A("Eingabe", "Input", "Home", "Home", "shell input keyevent KEYCODE_HOME", "Home-Taste.", "Home key."),
        A("Eingabe", "Input", "Zurück", "Back", "shell input keyevent KEYCODE_BACK", "Zurück-Taste.", "Back key."),
        A("Eingabe", "Input", "Lauter", "Volume up", "shell input keyevent KEYCODE_VOLUME_UP", "Lautstärke +.", "Volume +."),
        A("Eingabe", "Input", "Leiser", "Volume down", "shell input keyevent KEYCODE_VOLUME_DOWN", "Lautstärke −.", "Volume −."),
        A("Eingabe", "Input", "Text eingeben …", "Type text …", "shell input text <text>",
          "Tippt Text in das aktive Eingabefeld (Leerzeichen als %s).", "Types text into the focused field (spaces as %s)."),

        // ---------------- Diagnose (all) ----------------
        A("Diagnose", "Diagnostics", "Logcat: Fehler (letzte 200)", "Logcat: errors (last 200)", "logcat -d -t 200 *:E",
          "Die letzten Fehlermeldungen des Systems und der Apps.", "The latest error messages of system and apps."),
        A("Diagnose", "Diagnostics", "Logcat leeren", "Clear logcat", "logcat -c", "Leert den Log-Puffer.", "Clears the log buffer."),
        A("Diagnose", "Diagnostics", "Screenshot aufs Gerät", "Screenshot to device", "shell screencap -p /sdcard/Pictures/adbora-screenshot.png",
          "Speichert einen Screenshot unter /sdcard/Pictures.", "Saves a screenshot to /sdcard/Pictures."),
        A("Diagnose", "Diagnostics", "Bildschirm-Timeout lesen", "Read screen timeout", "shell settings get system screen_off_timeout",
          "Zeit bis zum Ausschalten des Displays in Millisekunden.", "Time until the display turns off, in milliseconds."),

        // ---------------- System (all) ----------------
        A("System", "System", "Neustart", "Reboot", "reboot", "Startet das Gerät neu.", "Restarts the device.", CommandRisk.Caution),
        A("System", "System", "Neustart in Recovery", "Reboot to recovery", "reboot recovery",
          "Startet in den Wiederherstellungsmodus.", "Restarts into recovery mode.", CommandRisk.Dangerous),
        A("System", "System", "Neustart in Bootloader", "Reboot to bootloader", "reboot bootloader",
          "Startet in den Bootloader (Fastboot).", "Restarts into the bootloader (fastboot).", CommandRisk.Dangerous),
    };

    private static CommandPreset Q(string de, string en, string cmd, string dDe, string dEn, CommandRisk risk = CommandRisk.Safe) =>
        new("Meta Quest", "Meta Quest", de, en, cmd, dDe, dEn, risk, DeviceFamily.Quest);

    private static CommandPreset P(string de, string en, string cmd, string dDe, string dEn, CommandRisk risk = CommandRisk.Safe) =>
        new("Pico", "Pico", de, en, cmd, dDe, dEn, risk, DeviceFamily.Pico);

    private static CommandPreset A(string catDe, string catEn, string de, string en, string cmd, string dDe, string dEn, CommandRisk risk = CommandRisk.Safe) =>
        new(catDe, catEn, de, en, cmd, dDe, dEn, risk);
}
