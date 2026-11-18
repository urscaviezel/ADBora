using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdbTool.Core;

/// <summary>
/// Installs PFD-Metrics (https://github.com/urscaviezel/PFD-Metrics) on a Play For Dream MR – the same
/// steps as its setup.ps1, but without PowerShell, so it works on Windows and Linux alike:
/// download both APKs of the latest release (SHA-256 from release.json), install them, grant the
/// permissions / app-ops of the HUD, verify and start the app.
/// </summary>
internal static class PfdMetricsInstaller
{
    public const string HudPackage = "com.pfd.metricstool.hud";
    public const string UiPackage = "com.pfd.metricstool";
    private const string ApiLatest = "https://api.github.com/repos/urscaviezel/PFD-Metrics/releases/latest";
    private static readonly string[] Apks = { "PFDMetricsHUD.apk", "PFDMetricsTool.apk" };
    private static readonly string[] Permissions = { "READ_LOGS", "DUMP", "POST_NOTIFICATIONS" };
    private static readonly string[] AppOps = { "SYSTEM_ALERT_WINDOW", "GET_USAGE_STATS", "MANAGE_EXTERNAL_STORAGE", "WRITE_SETTINGS" };

    /// <summary>Returns the installed version (release tag); throws with a readable message on failure.</summary>
    public static async Task<string> InstallAsync(AdbClient adb, Action<string> status, CancellationToken token)
    {
        // Same device check as the official setup
        string model = (await adb.CaptureAsync(token, TimeSpan.FromSeconds(10), "shell", "getprop ro.product.model")).Output.Trim();
        string sdk = (await adb.CaptureAsync(token, TimeSpan.FromSeconds(10), "shell", "getprop ro.build.version.sdk")).Output.Trim();
        if (!Regex.IsMatch(model, "^PFDM[ _]MR$") || sdk != "34")
            throw new InvalidOperationException(Loc.T(
                $"PFD-Metrics ist für PFDM MR mit Android 14 (SDK 34) getestet. Erkannt: {model} / SDK {sdk}. Keine Installation ausgeführt.",
                $"PFD-Metrics is tested for PFDM MR with Android 14 (SDK 34). Detected: {model} / SDK {sdk}. Nothing installed."));

        status(Loc.T("Suche die neueste PFD-Metrics-Version auf GitHub …", "Looking up the latest PFD-Metrics release on GitHub …"));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ADBora/{AppInfo.Version}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        JsonNode root = JsonNode.Parse(await client.GetStringAsync(ApiLatest, token)) ?? throw new InvalidDataException("GitHub: empty response");
        string tag = root["tag_name"]?.GetValue<string>() ?? "?";
        var assets = (root["assets"] as JsonArray ?? new JsonArray())
            .Where(a => a is not null)
            .ToDictionary(a => a!["name"]?.GetValue<string>() ?? "", a => a!["browser_download_url"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase);

        // release.json carries the expected SHA-256 of both APKs and the version
        if (!assets.TryGetValue("release.json", out string? manifestUrl))
            throw new InvalidDataException(Loc.T($"release.json fehlt im Release {tag}.", $"release.json is missing in release {tag}."));
        JsonNode manifest = JsonNode.Parse(await client.GetStringAsync(manifestUrl, token)) ?? throw new InvalidDataException("release.json");
        string version = manifest["version"]?.GetValue<string>() ?? tag.TrimStart('v');

        string dir = Path.Combine(Path.GetTempPath(), "ADBora-PFDMetrics-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string name in Apks)
            {
                if (!assets.TryGetValue(name, out string? url))
                    throw new InvalidDataException(Loc.T($"{name} fehlt im Release {tag}.", $"{name} is missing in release {tag}."));
                status(Loc.T($"Lade {name} …", $"Downloading {name} …"));
                string file = Path.Combine(dir, name);
                await using (Stream src = await client.GetStreamAsync(url, token))
                await using (FileStream dst = File.Create(file))
                    await src.CopyToAsync(dst, token);
                string? expected = manifest["sha256"]?[name]?.GetValue<string>();
                if (expected is not null)
                {
                    await using FileStream stream = File.OpenRead(file);
                    string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
                    if (actual != expected.ToLowerInvariant())
                        throw new InvalidDataException(Loc.T($"Prüfsumme von {name} stimmt nicht.", $"Checksum of {name} does not match."));
                }

                status(Loc.T($"Installiere {name} …", $"Installing {name} …"));
                AdbResult r = await adb.CaptureAsync(token, TimeSpan.FromMinutes(5), "install", "--no-incremental", "-r", file);
                if (!Regex.IsMatch(r.Combined, @"(?m)^Success\s*$"))
                    throw new InvalidOperationException(Loc.T(
                        $"Installation von {name} fehlgeschlagen. Bei INSTALL_FAILED_UPDATE_INCOMPATIBLE stammt die vorhandene App von einem anderen Signierschlüssel (nicht automatisch deinstalliert, Einstellungen bleiben erhalten).\n{r.Combined.Trim()}",
                        $"Installation of {name} failed. INSTALL_FAILED_UPDATE_INCOMPATIBLE means the existing app uses a different signing key (not uninstalled automatically, settings are kept).\n{r.Combined.Trim()}"));
            }

            status(Loc.T("Erteile Berechtigungen …", "Granting permissions …"));
            foreach (string p in Permissions)
                await adb.CaptureAsync(token, TimeSpan.FromSeconds(15), "shell", $"pm grant {HudPackage} android.permission.{p}");
            foreach (string op in AppOps)
                await adb.CaptureAsync(token, TimeSpan.FromSeconds(15), "shell", $"appops set {HudPackage} {op} allow");

            status(Loc.T("Prüfe Installation und Rechte …", "Checking installation and permissions …"));
            foreach (string pkg in new[] { HudPackage, UiPackage })
            {
                string dump = (await adb.CaptureAsync(token, TimeSpan.FromSeconds(20), "shell", $"dumpsys package {pkg}")).Output;
                if (!Regex.IsMatch(dump, "versionName=" + Regex.Escape(version) + @"(\s|$)"))
                    throw new InvalidOperationException(Loc.T($"Unerwartete Version für {pkg}.", $"Unexpected version for {pkg}."));
                if (pkg == HudPackage)
                    foreach (string p in Permissions)
                        if (!dump.Contains($"android.permission.{p}: granted=true"))
                            throw new InvalidOperationException(Loc.T($"Berechtigung {p} wurde nicht erteilt.", $"Permission {p} was not granted."));
            }
            foreach (string op in AppOps)
            {
                string r = (await adb.CaptureAsync(token, TimeSpan.FromSeconds(15), "shell", $"appops get {HudPackage} {op}")).Output;
                if (!r.Contains(op + ": allow"))
                    throw new InvalidOperationException(Loc.T($"AppOp {op} nicht erlaubt: {r.Trim()}", $"AppOp {op} not allowed: {r.Trim()}"));
            }

            await adb.CaptureAsync(token, TimeSpan.FromSeconds(15), "shell", $"am start -n {UiPackage}/.MainActivity");
            return tag;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
