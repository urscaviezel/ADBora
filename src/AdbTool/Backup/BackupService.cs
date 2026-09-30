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

    public static string InstallInstructions(List<string> fileNames)
    {
        string arguments = string.Join(" ", fileNames.Select(n => "'.\\APK\\" + n.Replace("'", "''") + "'"));
        string verb = fileNames.Count > 1 ? "install-multiple" : "install";
        string command = $"& adb -s 'DEVICE_SERIAL' {verb} -r {arguments}";
        return
            "APK INSTALLATION / INSTALLATION DER APKs\n\n" +
            "DEUTSCH\n" +
            "Die APK-Dateien sind unveränderte Kopien vom Gerät, einschließlich ihrer Signaturen.\n" +
            "Bei mehreren APKs gehören Basis und alle Splits zusammen. Nicht einzeln installieren.\n" +
            "Öffne PowerShell in diesem Ordner. Verbinde das Zielgerät und bestätige USB-Debugging.\n" +
            "Führe adb devices aus und ersetze DEVICE_SERIAL unten durch die gewünschte Seriennummer.\n" +
            "ADB muss im PATH liegen; alternativ ersetze adb durch den in Anführungszeichen gesetzten\n" +
            "vollständigen Pfad zur adb.exe. Der Befehl installiert bzw. aktualisiert die App.\n\n" +
            command + "\n\n" +
            "ENGLISH\n" +
            "These APK files are unchanged copies from the device, including their signatures.\n" +
            "For split apps, install the base APK and all splits together, never individually.\n" +
            "Open PowerShell in this folder. Connect the target device and authorize USB debugging.\n" +
            "Run adb devices and replace DEVICE_SERIAL above with the desired device serial.\n" +
            "ADB must be on PATH; otherwise replace adb with the quoted full path to adb.exe.\n" +
            "The command installs or updates the app.\n\n" +
            "DE: OBB/Data werden nicht durch diesen Befehl wiederhergestellt. Gerätekompatibilität,\n" +
            "Android-Version, App-Signatur, Systemrechte und Lizenz können eine Installation verhindern.\n" +
            "EN: This command does not restore OBB/Data. Device compatibility, Android version,\n" +
            "app signatures, system permissions and licensing may prevent installation.\n";
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
                            if (kind == "APK")
                            {
                                if (app.Apks.Count == 0)
                                    throw new AdbException(Loc.T("Keine APK-Pfade vorhanden", "No APK paths available"));
                                fileNames = ApkFileNames(app);
                                for (int i = 0; i < app.Apks.Count; i++)
                                    await PullApkAsync(app.Apks[i], Path.Combine(staging, fileNames[i]));
                            }
                            else
                            {
                                FolderState state = kind == "OBB" ? app.Obb : app.Data;
                                if (state != FolderState.Present)
                                    throw new AdbException(Loc.T("Ordner ist nicht zugänglich", "Folder is inaccessible"));
                                string source = $"/sdcard/Android/{(kind == "OBB" ? "obb" : "data")}/{app.Package}";
                                await _adb.RunAsync(_token, TimeSpan.FromHours(1), "pull", source, Path.Combine(staging, "content"));
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
                                await File.WriteAllTextAsync(Path.Combine(folder, "INSTALL.txt"), InstallInstructions(fileNames), new UTF8Encoding(false), CancellationToken.None);
                                json["apk_type"] = fileNames.Count > 1 ? "split" : "single";
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
