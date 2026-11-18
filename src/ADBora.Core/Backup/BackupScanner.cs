using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdbTool.Backup;
using AdbTool.Core;

namespace AdbTool.Backup;

/// <summary>One app folder of an ADBora / APK Backup Tool backup.</summary>
internal sealed class RestoreEntry
{
    public string Folder { get; init; } = "";
    public string Run { get; init; } = "";
    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Apks { get; } = new();
    public string? ObbDir { get; set; }
    public string? DataDir { get; set; }
    /// <summary>Internal app data (Internal/data.tar, via run-as) – part of "Data".</summary>
    public string? InternalTar { get; set; }
    public long ApkBytes { get; set; }
    public long ObbBytes { get; set; }
    public long DataBytes { get; set; }

    public bool Apk { get; set; }
    public bool Obb { get; set; }
    public bool Data { get; set; }

    public bool HasApk => Apks.Count > 0;
    public bool HasObb => ObbDir is not null;
    public bool HasData => DataDir is not null || InternalTar is not null;
    public bool AnySelected => Apk || Obb || Data;
    public long SelectedBytes => (Apk ? ApkBytes : 0) + (Obb ? ObbBytes : 0) + (Data ? DataBytes : 0);
}

/// <summary>Finds app folders ("Name [package]" with APK/, OBB/, Data/) in a backup.</summary>
internal static class BackupScanner
{
    private static readonly Regex BracketPackage = new(@"\[([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)(?:-[0-9a-f]+)?\]", RegexOptions.Compiled);
    private static readonly Regex PackageRe = new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", RegexOptions.Compiled);

    public static bool IsAppFolder(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "manifest.json"))) return true;
            string apk = Path.Combine(dir, "APK");
            return Directory.Exists(apk) && Directory.EnumerateFiles(apk).Any(ApkBundle.IsInstallable);
        }
        catch { return false; }
    }

    /// <summary>A backup run (Backup_…) or a folder containing several runs.</summary>
    public static bool ContainsAppFolders(string dir)
    {
        try
        {
            if (IsAppFolder(dir)) return false;
            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (IsAppFolder(sub)) return true;
                if (Directory.EnumerateDirectories(sub).Any(IsAppFolder)) return true;
            }
        }
        catch { }
        return false;
    }

    public static List<RestoreEntry> Scan(string path)
    {
        var folders = new List<(string Folder, string Run)>();
        if (IsAppFolder(path))
        {
            folders.Add((path, Path.GetFileName(Path.GetDirectoryName(path)) ?? ""));
        }
        else
        {
            foreach (string sub in Directory.EnumerateDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (IsAppFolder(sub))
                {
                    folders.Add((sub, Path.GetFileName(path)));
                    continue;
                }
                // Folder with several backup runs (e.g. "APK-Backups")
                foreach (string app in Directory.EnumerateDirectories(sub).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    if (IsAppFolder(app))
                        folders.Add((app, Path.GetFileName(sub)));
            }
        }

        var entries = new List<RestoreEntry>();
        foreach (var (folder, run) in folders)
        {
            try
            {
                RestoreEntry? e = Read(folder, run);
                if (e is not null && (e.HasApk || e.HasObb || e.HasData))
                    entries.Add(e);
            }
            catch { }
        }
        return entries
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Run, StringComparer.Ordinal)
            .ToList();
    }

    private static RestoreEntry? Read(string folder, string run)
    {
        var entry = new RestoreEntry { Folder = folder, Run = run };
        JsonNode? manifest = null;
        string manifestPath = Path.Combine(folder, "manifest.json");
        try { if (File.Exists(manifestPath)) manifest = JsonNode.Parse(File.ReadAllText(manifestPath)); } catch { }

        string folderName = Path.GetFileName(folder);
        entry.Package = manifest?["package"]?.GetValue<string>() ?? "";
        if (!PackageRe.IsMatch(entry.Package))
            entry.Package = BracketPackage.Match(folderName) is { Success: true } m ? m.Groups[1].Value : "";
        entry.Name = manifest?["name"]?.GetValue<string>() is { Length: > 0 } n ? n
            : folderName.Contains(" [") ? folderName[..folderName.IndexOf(" [", StringComparison.Ordinal)] : folderName;
        entry.Version = manifest?["version"]?.GetValue<string>() ?? "";

        // APKs: the file list of the manifest keeps base + splits together
        string apkDir = Path.Combine(folder, "APK");
        if (manifest?["apk_files"] is JsonArray files && files.Count > 0)
        {
            foreach (JsonNode? f in files)
            {
                string? rel = f?.GetValue<string>();
                if (rel is null) continue;
                string full = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) entry.Apks.Add(full);
            }
            if (entry.Apks.Count != files.Count) entry.Apks.Clear(); // incomplete: fall back below
        }
        if (entry.Apks.Count == 0 && Directory.Exists(apkDir))
            entry.Apks.AddRange(InstallFiles(apkDir));
        if (entry.Apks.Count == 0) // older backup layout: APKs directly in the app folder
            entry.Apks.AddRange(InstallFiles(folder));
        entry.ApkBytes = entry.Apks.Sum(f => new FileInfo(f).Length);

        if (entry.Package.Length == 0 && entry.Apks.Count > 0)
        {
            try { entry.Package = ApkMetadataReader.ReadPackage(entry.Apks[0]).Package; } catch { }
        }
        if (entry.Package.Length == 0)
            return null;

        (entry.ObbDir, entry.ObbBytes) = Payload(Path.Combine(folder, "OBB"));
        (entry.DataDir, entry.DataBytes) = Payload(Path.Combine(folder, "Data"));
        string internalTar = Path.Combine(folder, "Internal", AdbTool.Backup.BackupService.InternalTarName);
        if (File.Exists(internalTar))
        {
            entry.InternalTar = internalTar;
            entry.DataBytes += new FileInfo(internalTar).Length;
        }

        entry.Apk = entry.HasApk;
        entry.Obb = entry.HasObb;
        entry.Data = entry.HasData;
        return entry;
    }

    /// <summary>Single/split APKs, or – if there are none – one .apks/.xapk/.apkm bundle.</summary>
    private static IEnumerable<string> InstallFiles(string dir)
    {
        var apks = Directory.GetFiles(dir, "*.apk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (apks.Count > 0) return apks;
        return Directory.GetFiles(dir).Where(ApkBundle.IsBundle).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(1);
    }

    private static (string?, long) Payload(string dir)
    {
        if (!Directory.Exists(dir)) return (null, 0);
        long bytes = 0;
        int count = 0;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            count++;
            bytes += new FileInfo(f).Length;
        }
        return count > 0 ? (dir, bytes) : (null, 0);
    }
}

