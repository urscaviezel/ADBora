using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdbTool.Core;

namespace AdbTool.Backup;

internal enum FolderState { Unchecked, Present, Missing, Inaccessible }

internal static class FolderStateText
{
    public static string Text(this FolderState state) => state switch
    {
        FolderState.Present => Loc.T("Vorhanden", "Available"),
        FolderState.Missing => Loc.T("Nicht vorhanden", "Not found"),
        FolderState.Inaccessible => Loc.T("Nicht prüfbar / gesperrt", "Unavailable / blocked"),
        _ => Loc.T("Ungeprüft", "Not checked")
    };
}

internal sealed class AppEntry
{
    public AppEntry(string package)
    {
        Package = package;
        Name = package;
    }

    public string Package { get; }
    public string Name { get; set; }
    public List<string> Apks { get; set; } = new();
    public FolderState Obb { get; set; } = FolderState.Unchecked;
    public FolderState Data { get; set; } = FolderState.Unchecked;
    public string Note { get; set; } = "";
    public string Version { get; set; } = "";
    public string VersionCode { get; set; } = "";
    public byte[] Icon { get; set; } = Array.Empty<byte>();
}

internal sealed record BackupSelection(AppEntry App, bool Apk, bool Obb, bool Data)
{
    public IEnumerable<string> Kinds()
    {
        if (Apk) yield return "APK";
        if (Obb) yield return "OBB";
        if (Data) yield return "Data";
    }
}

internal sealed class BackupAppRecord
{
    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Errors { get; } = new();
    public List<string> Completed { get; } = new();
    /// <summary>Files that could not be read (e.g. no read permission for adb); the component still counts as saved.</summary>
    public List<string> Skipped { get; } = new();
}

internal sealed class BackupReport
{
    public string Directory { get; set; } = "";
    public string Device { get; set; } = "";
    public string DeviceModel { get; set; } = "";
    public bool Cancelled { get; set; }
    public List<BackupAppRecord> Apps { get; } = new();
}

/// <summary>ADB scan and backup logic (port of the former Python core module).</summary>
internal sealed class BackupService
{
    private static readonly Regex PackageRe = new(@"^[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*$", RegexOptions.Compiled);
    private static readonly Regex StatLine = new(@"^-?\d+$", RegexOptions.Compiled);
    private static readonly Regex Sha256Re = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private readonly AdbClient _adb;
    private readonly Action<string> _log;
    private readonly Action<int, int> _progress;
    private readonly CancellationToken _token;

    /// <summary>Store split apps as one .apks file (like SAI / AnExplorer) instead of single APKs.</summary>
    public bool BundleSplits { get; init; }

    public BackupService(AdbClient adb, Action<string> log, Action<int, int> progress, CancellationToken token)
    {
        _adb = adb;
        _log = log;
        _progress = progress;
        _token = token;
    }

    // ------------------------------------------------------------------
    // Scan
    // ------------------------------------------------------------------

    public async Task<List<AppEntry>> ScanAsync(bool resolveNames)
    {
        string userId = (await _adb.ShellAsync(_token, "am", "get-current-user")).Trim();
        if (!userId.All(char.IsDigit) || userId.Length == 0)
            throw new AdbException(Loc.T("Aktuelles Android-Benutzerprofil konnte nicht bestimmt werden.",
                "Could not determine the current Android user profile."));
        if (userId != "0")
            throw new AdbException(Loc.T("Bitte zum Android-Hauptbenutzer wechseln. Weitere Benutzerprofile werden noch nicht unterstützt.",
                "Switch to the primary Android user. Other user profiles are not supported yet."));

        // Ask Android's package manager for user-installed apps; no filesystem scan.
        string output = await _adb.ShellAsync(_token, "pm", "list", "packages", "-3", "--user", "0");
        var packages = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:"))
            .Select(l => l[8..].Trim())
            .Where(p => PackageRe.IsMatch(p))
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var apps = new List<AppEntry>();
        if (packages.Count == 0)
            return apps;

        var cache = new MetadataCache(_adb.Serial, userId);
        var parentCache = new Dictionary<string, List<string>?>();
        int hits = 0, loaded = 0;
        string tempRoot = Path.Combine(AppSettings.TempDirectory, "apk-label-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            for (int index = 0; index < packages.Count; index++)
            {
                _token.ThrowIfCancellationRequested();
                string package = packages[index];
                _log(Loc.T($"Prüfe {package}", $"Checking {package}"));
                var app = new AppEntry(package);

                try
                {
                    string paths = await _adb.ShellAsync(_token, "pm", "path", "--user", "0", package);
                    app.Apks = paths.Split('\n').Select(l => l.Trim())
                        .Where(l => l.StartsWith("package:/") && l.EndsWith(".apk"))
                        .Select(l => l[8..]).ToList();
                    app.Obb = await DirectoryStatusAsync($"/sdcard/Android/obb/{package}", parentCache);
                    app.Data = await DirectoryStatusAsync($"/sdcard/Android/data/{package}", parentCache);

                    if (resolveNames && app.Apks.Count > 0)
                    {
                        try
                        {
                            List<string>? signature = await ApkSignatureAsync(app.Apks);
                            if (cache.TryRestore(app, signature))
                            {
                                hits++;
                            }
                            else
                            {
                                _log(Loc.T($"Lade App-Details: {package}", $"Loading app details: {package}"));
                                string baseApk = app.Apks.FirstOrDefault(p => p.EndsWith("/base.apk")) ?? app.Apks[0];
                                Directory.CreateDirectory(tempRoot);
                                string local = Path.Combine(tempRoot, "base.apk");
                                try
                                {
                                    await PullApkAsync(baseApk, local);
                                    ApkMetadata meta = await Task.Run(() => ApkMetadataReader.Read(local), _token);
                                    app.Name = string.IsNullOrWhiteSpace(meta.Label) ? package : meta.Label;
                                    app.Version = meta.VersionName;
                                    app.VersionCode = meta.VersionCode;
                                    app.Icon = meta.Icon;
                                }
                                finally
                                {
                                    TryDelete(local);
                                }
                                loaded++;

                                List<string>? after = signature is null ? null : await ApkSignatureAsync(app.Apks);
                                if (signature is not null && after is not null && signature.SequenceEqual(after))
                                {
                                    try { cache.Save(app, signature); }
                                    catch (Exception ex)
                                    {
                                        _log(Loc.T($"Metadaten-Cache konnte nicht gespeichert werden: {ex.Message}",
                                            $"Could not save metadata cache: {ex.Message}"));
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            app.Note = Loc.T($"App-Name nicht ermittelbar; Paketname verwendet. {ex.Message}",
                                $"Could not resolve app name; using package name. {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (AdbException ex)
                {
                    app.Note = ex.Message;
                }

                apps.Add(app);
                _progress(index + 1, packages.Count);
            }
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }

        if (resolveNames)
            _log(Loc.T($"App-Details: {hits} aus Cache, {loaded} neu eingelesen.", $"App details: {hits} cached, {loaded} newly read."));

        return apps
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Package, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<FolderState> DirectoryStatusAsync(string path, Dictionary<string, List<string>?> parentCache)
    {
        int slash = path.LastIndexOf('/');
        string parent = path[..slash], name = path[(slash + 1)..];
        try
        {
            if (!parentCache.TryGetValue(parent, out List<string>? entries))
            {
                try
                {
                    entries = (await _adb.ShellAsync(_token, "ls", "-1", parent)).Split('\n').Select(l => l.Trim()).ToList();
                }
                catch (AdbException)
                {
                    parentCache[parent] = null;
                    throw;
                }
                parentCache[parent] = entries;
            }

            if (entries is null)
                return FolderState.Inaccessible;
            if (!entries.Contains(name))
                return FolderState.Missing;

            await _adb.ShellAsync(_token, "ls", "-a", path);
            return FolderState.Present;
        }
        catch (AdbException)
        {
            return FolderState.Inaccessible;
        }
    }

    /// <summary>Cheap change detection: paths, sizes, mtime, ctime and inodes of all splits.</summary>
    private async Task<List<string>?> ApkSignatureAsync(List<string> paths)
    {
        if (paths.Count == 0)
            return null;
        try
        {
            var args = new List<string> { "stat", "-c", "%n|%s|%Y|%Z|%i" };
            args.AddRange(paths);
            var lines = (await _adb.ShellAsync(_token, args.ToArray())).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count != paths.Count)
                return null;
            for (int i = 0; i < paths.Count; i++)
            {
                string[] fields = RSplit(lines[i], '|', 4);
                if (fields.Length != 5 || fields[0] != paths[i] || !fields.Skip(1).All(f => StatLine.IsMatch(f)))
                    return null;
            }
            return lines;
        }
        catch (AdbException)
        {
            return null;
        }
    }

    private static string[] RSplit(string value, char separator, int maxSplits)
    {
        var parts = new List<string>();
        string rest = value;
        for (int i = 0; i < maxSplits; i++)
        {
            int idx = rest.LastIndexOf(separator);
            if (idx < 0) break;
            parts.Insert(0, rest[(idx + 1)..]);
            rest = rest[..idx];
        }
        parts.Insert(0, rest);
        return parts.ToArray();
    }

    /// <summary>
    /// Copies one APK. Some devices deny adbd sync access (adb pull) but allow
    /// the shell to read APKs, so "exec-out cat" is used as fallback. The file
    /// is verified (size, ZIP/manifest, SHA-256 if available) before it is kept.
    /// </summary>
    public async Task PullApkAsync(string source, string target)
    {
        string temporary = target + ".transfer";
        try
        {
            string sizeText = (await _adb.ShellAsync(_token, "stat", "-c", "%s", source)).Trim();
            if (!long.TryParse(sizeText, out long expected) || expected <= 0)
                throw new AdbException(Loc.T("Die APK auf dem Gerät ist leer oder ihre Größe ist nicht lesbar.",
                    "The APK on the device is empty or its size cannot be read."));

            try
            {
                await _adb.RunAsync(_token, TimeSpan.FromHours(1), "pull", source, temporary);
            }
            catch (AdbException)
            {
                // Same device and ordinary shell permissions, no root.
                await _adb.StreamFileAsync(source, temporary, _token);
            }

            long actual = new FileInfo(temporary).Length;
            if (actual != expected)
                throw new AdbException(Loc.T($"APK unvollständig: erwartet {expected} Bytes, erhalten {actual}.",
                    $"Incomplete APK: expected {expected} bytes, received {actual}."));

            try
            {
                using ZipArchive zip = ZipFile.OpenRead(temporary);
                if (zip.GetEntry("AndroidManifest.xml") is null)
                    throw new InvalidDataException();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                throw new AdbException(Loc.T("Die übertragene Datei ist kein vollständiges APK-Archiv.",
                    "The transferred file is not a complete APK archive."));
            }

            string remoteDigest = "";
            try
            {
                remoteDigest = (await _adb.ShellAsync(_token, "sha256sum", source)).Split(' ', '\t')[0].Trim().ToLowerInvariant();
            }
            catch (AdbException) { }

            if (Sha256Re.IsMatch(remoteDigest) && await Sha256Async(temporary) != remoteDigest)
                throw new AdbException(Loc.T("APK-Prüfsumme stimmt nicht mit dem Gerät überein.", "The APK checksum does not match the device."));

            _token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private async Task<string> Sha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, _token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ------------------------------------------------------------------
    // Backup
    // ------------------------------------------------------------------

    public static string SafeName(string value, int limit = 65)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            sb.Append(c < 0x20 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        string result = sb.ToString().Trim(' ', '.');
        if (result.Length > limit) result = result[..limit];
        result = result.TrimEnd(' ', '.');

        string stem = result.Split('.')[0].ToUpperInvariant();
        bool reserved = stem is "CON" or "PRN" or "AUX" or "NUL" ||
                        (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
        if (result.Length == 0 || reserved)
            result = "_" + result;
        return result;
    }

    /// <summary>Keeps raw APKs (and their signatures); never unpacks or merges splits.</summary>
    public static List<string> ApkFileNames(AppEntry app)
    {
        if (app.Apks.Count == 1)
        {
            string stem = SafeName(app.Name, 65);
            if (app.Version.Length > 0)
                stem += "-" + SafeName(app.Version, 24);
            return new List<string> { stem + ".apk" };
        }

        var names = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < app.Apks.Count; index++)
        {
            string file = app.Apks[index][(app.Apks[index].LastIndexOf('/') + 1)..];
            string stem = file.EndsWith(".apk") ? file[..^4] : file;
            if (app.Version.Length > 0)
            {
                string prefix = SafeName(app.Name, 45) + "-" + SafeName(app.Version, 20);
                stem = stem == "base" ? prefix : prefix + "-" + SafeName(stem, 45);
            }
            string name = SafeName(stem, 115) + ".apk";
            while (used.Contains(name))
                name = $"{index:00}_" + name;
            used.Add(name);
            names.Add(name);
        }
        return names;
    }

    private static string FirstLine(string text)
    {
        string line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? text;
        return line.Length > 160 ? line[..160] + " …" : line;
    }

    /// <summary>
    /// Copies a device folder file by file; unreadable files are skipped.
    /// Files that are already complete locally (same size) are kept.
    /// Returns the relative paths of the skipped files.
    /// </summary>
    private async Task<List<string>> PullTreeTolerantAsync(string source, string localRoot)
    {
        Directory.CreateDirectory(localRoot);
        var skipped = new List<string>();

        // Folders (keeps empty ones) and files with their sizes
        AdbResult dirs = await _adb.CaptureAsync(_token, TimeSpan.FromMinutes(5), "shell", AdbClient.ShellJoin(new[] { "find", source, "-type", "d" }));
        foreach (string dir in dirs.Output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith(source + "/", StringComparison.Ordinal)))
        {
            try { Directory.CreateDirectory(LocalPath(localRoot, dir[(source.Length + 1)..])); } catch { }
        }

        AdbResult files = await _adb.CaptureAsync(_token, TimeSpan.FromMinutes(5), "shell",
            AdbClient.ShellJoin(new[] { "find", source, "-type", "f", "-exec", "stat", "-c", "%s|%n", "{}", "+" }));
        var list = new List<(long Size, string Path)>();
        foreach (string line in files.Output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            int bar = line.IndexOf('|');
            if (bar <= 0 || !long.TryParse(line[..bar], out long size)) continue;
            string path = line[(bar + 1)..];
            if (path.StartsWith(source + "/", StringComparison.Ordinal)) list.Add((size, path));
        }
        if (list.Count == 0 && !files.Ok)
            throw new AdbException(files.Combined.Trim());

        foreach (var (size, remote) in list)
        {
            _token.ThrowIfCancellationRequested();
            string relative = remote[(source.Length + 1)..];
            string local;
            try { local = LocalPath(localRoot, relative); }
            catch { skipped.Add(relative); continue; }

            if (File.Exists(local) && new FileInfo(local).Length == size)
                continue; // already copied by the first "adb pull"

            AdbResult pull = await _adb.CaptureAsync(_token, TimeSpan.FromHours(1), "pull", remote, local);
            if (!pull.Ok)
            {
                skipped.Add(relative);
                TryDelete(local);
            }
        }
        return skipped;
    }

    private static string LocalPath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid path: " + relative);
        return full;
    }

    /// <summary>INSTALL.txt next to the APK folder.</summary>
    public static string InstallInstructions(List<string> fileNames, List<string> bundleEntries)
    {
        bool bundle = bundleEntries.Count > 0;
        string arguments = string.Join(" ", fileNames.Select(n => "'.\\APK\\" + n.Replace("'", "''") + "'"));
        string verb = fileNames.Count > 1 ? "install-multiple" : "install";
        string command = $"adb {verb} -r {arguments}";
        string manualDe = bundle
            ? "Die Split-APKs liegen zusammen in einer .apks-Datei (ZIP). Installieren mit ADBora (Tab „APK Install“,\n" +
              "Datei hineinziehen), mit SAI / AnExplorer oder per INSTALL.cmd. Manuell: .apks wie ein ZIP entpacken und\n" +
              "alle enthaltenen APKs gemeinsam mit „adb install-multiple -r <alle .apk>“ installieren.\n"
            : "Öffne PowerShell in DIESEM Ordner (Explorer: Adressleiste „powershell“ eintippen) und führe aus:\n\n" + command + "\n\n" +
              "Bei mehreren verbundenen Geräten nach „adb“ noch „-s <Seriennummer>“ einfügen (Seriennummer: adb devices).\n";
        string manualEn = bundle
            ? "The split APKs are stored together in one .apks file (ZIP). Install it with ADBora (\"APK install\" tab,\n" +
              "drop the file), with SAI / AnExplorer or via INSTALL.cmd. Manually: extract the .apks like a ZIP and\n" +
              "install all contained APKs together with \"adb install-multiple -r <all .apk>\".\n"
            : "Open PowerShell in THIS folder (Explorer: type \"powershell\" into the address bar) and run:\n\n" + command + "\n\n" +
              "With several connected devices add \"-s <serial>\" after \"adb\" (serial: adb devices).\n";
        return
            "APK INSTALLATION / INSTALLATION DER APKs\n\n" +
            "DEUTSCH\n" +
            "Am einfachsten: Gerät verbinden (USB-Debugging erlaubt) und INSTALL.cmd doppelklicken.\n" +
            "Bei mehreren Geräten: INSTALL.cmd <Seriennummer> in der Eingabeaufforderung starten.\n" +
            "Die APK-Dateien sind unveränderte Kopien vom Gerät, einschließlich ihrer Signaturen.\n" +
            "Bei Split-Apps gehören Basis und alle Splits zusammen – nie einzeln installieren.\n\n" +
            manualDe + "\n" +
            "ENGLISH\n" +
            "Easiest: connect the device (USB debugging allowed) and double-click INSTALL.cmd.\n" +
            "With several devices: run INSTALL.cmd <serial> from a command prompt.\n" +
            "The APK files are unchanged copies from the device, including their signatures.\n" +
            "For split apps, base and all splits belong together – never install them individually.\n\n" +
            manualEn + "\n" +
            "DE: OBB/Data werden nur von ADBora (Batch-Restore) wiederhergestellt, nicht von INSTALL.cmd.\n" +
            "EN: OBB/Data are restored by ADBora (batch restore) only, not by INSTALL.cmd.\n";
    }

    /// <summary>INSTALL.cmd: double-click installer for the app (single, split or .apks bundle).</summary>
    public static string InstallScript(List<string> fileNames, List<string> bundleEntries, string adbPath)
    {
        static string Cmd(string value) => value.Replace("%", "%%");
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("chcp 65001 >nul\r\n");
        sb.Append("rem ADBora - installs this app on the connected device. Usage: INSTALL.cmd [serial]\r\n");
        sb.Append("cd /d \"%~dp0\"\r\n");
        sb.Append("set \"ADB=adb\"\r\n");
        sb.Append($"where adb >nul 2>nul || set \"ADB={Cmd(adbPath)}\"\r\n");
        sb.Append("set \"SERIAL=\"\r\n");
        sb.Append("if not \"%~1\"==\"\" set \"SERIAL=-s %~1\"\r\n");
        if (bundleEntries.Count > 0)
        {
            sb.Append("set \"T=%TEMP%\\adbora-install-%RANDOM%%RANDOM%\"\r\n");
            // Path via environment variable: PowerShell cannot use folders with [ ] as its location.
            sb.Append($"set \"APKS=%~dp0APK\\{Cmd(fileNames[0])}\"\r\n");
            sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -Command \"Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::ExtractToDirectory($env:APKS, $env:T)\"\r\n");
            sb.Append("if errorlevel 1 goto failed\r\n");
            sb.Append("\"%ADB%\" %SERIAL% install-multiple -r " + string.Join(" ", bundleEntries.Select(e => $"\"%T%\\{Cmd(e)}\"")) + "\r\n");
            sb.Append("set \"RC=%ERRORLEVEL%\"\r\n");
            sb.Append("rmdir /s /q \"%T%\" >nul 2>nul\r\n");
            sb.Append("if not \"%RC%\"==\"0\" goto failed\r\n");
        }
        else
        {
            string verb = fileNames.Count > 1 ? "install-multiple" : "install";
            sb.Append($"\"%ADB%\" %SERIAL% {verb} -r " + string.Join(" ", fileNames.Select(n => $"\"APK\\{Cmd(n)}\"")) + "\r\n");
            sb.Append("if errorlevel 1 goto failed\r\n");
        }
        sb.Append("echo.\r\necho OK\r\npause\r\nexit /b 0\r\n");
        sb.Append(":failed\r\necho.\r\necho Installation fehlgeschlagen / installation failed\r\npause\r\nexit /b 1\r\n");
        return sb.ToString();
    }

    public async Task<BackupReport> BackupAsync(List<BackupSelection> selections, string destination, string deviceModel)
    {
        Directory.CreateDirectory(destination);
        string runDir = Path.Combine(destination,
            "Backup_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(runDir);

        var summary = new BackupReport { Directory = runDir, Device = _adb.Serial, DeviceModel = deviceModel };
        var appsJson = new JsonArray();
        var report = new JsonObject
        {
            ["format"] = 1,
            ["tool"] = "ADBora " + AppInfo.Version,
            ["created_utc"] = DateTime.UtcNow.ToString("o"),
            ["device"] = _adb.Serial,
            ["device_model"] = deviceModel,
            ["directory"] = runDir,
            ["apps"] = appsJson,
            ["cancelled"] = false
        };

        try
        {
            for (int index = 0; index < selections.Count; index++)
            {
                _token.ThrowIfCancellationRequested();
                BackupSelection selection = selections[index];
                AppEntry app = selection.App;

                string folderName = $"{SafeName(app.Name, 48)} [{SafeName(app.Package, 96)}]";
                string folder = Path.Combine(runDir, folderName);
                int collision = 1;
                while (Directory.Exists(folder) || File.Exists(folder))
                {
                    collision++;
                    folder = Path.Combine(runDir, $"{folderName} ({collision})");
                }
                Directory.CreateDirectory(folder);

                var record = new BackupAppRecord { Package = app.Package, Name = app.Name };
                summary.Apps.Add(record);
                var files = new JsonArray();
                var json = new JsonObject
                {
                    ["package"] = app.Package,
                    ["name"] = app.Name,
                    ["version"] = app.Version,
                    ["version_code"] = app.VersionCode,
                    ["folder"] = Path.GetFileName(folder),
                    ["requested"] = new JsonArray(selection.Kinds().OrderBy(k => k, StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()),
                };
                appsJson.Add(json);

                try
                {
                    foreach (string kind in selection.Kinds())
                    {
                        _log(Loc.T($"Sichere {app.Name} · {kind}", $"Backing up {app.Name} · {kind}"));
                        string staging = Path.Combine(folder, $".{kind}.partial");
                        Directory.CreateDirectory(staging);

                        try
                        {
                            List<string> fileNames = new();
                            List<string> bundleEntries = new();
                            if (kind == "APK")
                            {
                                if (app.Apks.Count == 0)
                                    throw new AdbException(Loc.T("Keine APK-Pfade vorhanden", "No APK paths available"));
                                fileNames = ApkFileNames(app);
                                for (int i = 0; i < app.Apks.Count; i++)
                                    await PullApkAsync(app.Apks[i], Path.Combine(staging, fileNames[i]));

                                if (BundleSplits && fileNames.Count > 1)
                                {
                                    // One .apks file (ZIP) with the original device file names, like SAI / AnExplorer
                                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    var entries = new List<(string File, string EntryName)>();
                                    for (int i = 0; i < fileNames.Count; i++)
                                    {
                                        string entryName = app.Apks[i][(app.Apks[i].LastIndexOf('/') + 1)..];
                                        while (!used.Add(entryName)) entryName = $"{i:00}_" + entryName;
                                        entries.Add((Path.Combine(staging, fileNames[i]), entryName));
                                    }
                                    string bundleName = SafeName(app.Name, 65) + (app.Version.Length > 0 ? "-" + SafeName(app.Version, 24) : "") + ".apks";
                                    ApkBundle.Create(Path.Combine(staging, bundleName), entries);
                                    foreach (var (file, _) in entries) File.Delete(file);
                                    bundleEntries = entries.Select(e => e.EntryName).ToList();
                                    fileNames = new List<string> { bundleName };
                                }
                            }
                            else
                            {
                                FolderState state = kind == "OBB" ? app.Obb : app.Data;
                                if (state != FolderState.Present)
                                    throw new AdbException(Loc.T("Ordner ist nicht zugänglich", "Folder is inaccessible"));
                                string source = $"/sdcard/Android/{(kind == "OBB" ? "obb" : "data")}/{app.Package}";
                                string content = Path.Combine(staging, "content");
                                try
                                {
                                    await _adb.RunAsync(_token, TimeSpan.FromHours(1), "pull", source, content);
                                }
                                catch (AdbException ex)
                                {
                                    // "adb pull" stops at the first unreadable file (e.g. a shader cache the app
                                    // created with private permissions). Copy the rest file by file instead.
                                    _token.ThrowIfCancellationRequested();
                                    _log(Loc.T($"{app.Name} · {kind}: {FirstLine(ex.Message)} – kopiere die übrigen Dateien einzeln …",
                                               $"{app.Name} · {kind}: {FirstLine(ex.Message)} – copying the remaining files one by one …"));
                                    List<string> skipped = await PullTreeTolerantAsync(source, content);
                                    foreach (string file in skipped)
                                        record.Skipped.Add($"{kind}/{file}");
                                    if (skipped.Count > 0)
                                        _log(Loc.T($"{app.Name} · {kind}: {skipped.Count} Datei(en) ohne Leserechte übersprungen: {string.Join(", ", skipped.Take(5))}{(skipped.Count > 5 ? " …" : "")}",
                                                   $"{app.Name} · {kind}: skipped {skipped.Count} file(s) without read permission: {string.Join(", ", skipped.Take(5))}{(skipped.Count > 5 ? " …" : "")}"));
                                }
                            }

                            string payload = kind == "APK" ? staging : Path.Combine(staging, "content");
                            var hashes = new List<JsonObject>();
                            if (Directory.Exists(payload))
                            {
                                foreach (string file in Directory.EnumerateFileSystemEntries(payload, "*", SearchOption.AllDirectories)
                                             .OrderBy(f => f, StringComparer.Ordinal))
                                {
                                    var info = new FileInfo(file);
                                    if (info.LinkTarget is not null)
                                        throw new AdbException(Loc.T("Symbolischer Link im Backup wird nicht unterstützt",
                                            "Symbolic links in backups are not supported"));
                                    if (!File.Exists(file))
                                        continue;
                                    string relative = Path.GetRelativePath(payload, file).Replace('\\', '/');
                                    hashes.Add(new JsonObject
                                    {
                                        ["path"] = kind + "/" + relative,
                                        ["bytes"] = info.Length,
                                        ["sha256"] = await Sha256Async(file)
                                    });
                                }
                            }

                            if (kind == "APK")
                            {
                                Directory.Move(staging, Path.Combine(folder, "APK"));
                                await File.WriteAllTextAsync(Path.Combine(folder, "INSTALL.txt"), InstallInstructions(fileNames, bundleEntries), new UTF8Encoding(false), CancellationToken.None);
                                await File.WriteAllTextAsync(Path.Combine(folder, "INSTALL.cmd"), InstallScript(fileNames, bundleEntries, _adb.Executable), new UTF8Encoding(false), CancellationToken.None);
                                json["apk_type"] = bundleEntries.Count > 0 ? "split-apks" : fileNames.Count > 1 ? "split" : "single";
                                if (bundleEntries.Count > 0)
                                    json["apks_entries"] = new JsonArray(bundleEntries.Select(n => (JsonNode?)n).ToArray());
                                json["apk_files"] = new JsonArray(fileNames.Select(n => (JsonNode?)("APK/" + n)).ToArray());
                            }
                            else
                            {
                                Directory.Move(payload, Path.Combine(folder, kind));
                                Directory.Delete(staging);
                            }

                            foreach (JsonObject h in hashes) files.Add(h);
                            record.Completed.Add(kind);
                        }
                        catch (OperationCanceledException)
                        {
                            record.Errors.Add(Loc.T($"{kind}: abgebrochen; .partial enthält ggf. unvollständige Dateien",
                                $"{kind}: cancelled; .partial may contain incomplete files"));
                            throw;
                        }
                        catch (Exception ex) when (ex is IOException or AdbException or UnauthorizedAccessException)
                        {
                            record.Errors.Add($"{kind}: {ex.Message}");
                            _log(Loc.T($"FEHLER · {app.Name} · {kind}: {ex.Message}", $"ERROR · {app.Name} · {kind}: {ex.Message}"));
                        }
                    }
                }
                finally
                {
                    json["completed"] = new JsonArray(record.Completed.Select(c => (JsonNode?)c).ToArray());
                    json["errors"] = new JsonArray(record.Errors.Select(c => (JsonNode?)c).ToArray());
                    if (record.Skipped.Count > 0)
                        json["skipped_unreadable"] = new JsonArray(record.Skipped.Select(c => (JsonNode?)c).ToArray());
                    json["files"] = files;
                    try
                    {
                        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"),
                            json.ToJsonString(AppSettings.JsonOptions), new UTF8Encoding(false), CancellationToken.None);
                    }
                    catch { }
                }

                _progress(index + 1, selections.Count);
            }
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
            report["cancelled"] = true;
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(runDir, "backup-report.json"),
                    report.ToJsonString(AppSettings.JsonOptions), new UTF8Encoding(false), CancellationToken.None);
            }
            catch { }
        }

        return summary;
    }
}
