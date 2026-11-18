using System.IO.Compression;
using System.Text.RegularExpressions;
using AdbTool.Core;

namespace AdbTool.Backup;

/// <summary>
/// App bundles that contain several APKs in one ZIP file:
/// .apks (SAI, AnExplorer, ADBora), .xapk (APKPure, optionally with OBB) and
/// unencrypted .apkm (APKMirror).
/// </summary>
internal static class ApkBundle
{
    private static readonly string[] Extensions = { ".apks", ".xapk", ".apkm" };
    private static readonly Regex ObbPath = new(@"^Android/obb/([^/]+)/(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsBundle(string path) =>
        Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool IsInstallable(string path) =>
        path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || IsBundle(path);

    public sealed record Expanded(List<string> Apks, string? ObbFolder, string? ObbPackage);

    /// <summary>Extracts the APKs (and OBB files of an .xapk) into a new folder below <paramref name="tempRoot"/>.</summary>
    public static Expanded Expand(string bundle, string tempRoot)
    {
        string target = Path.Combine(tempRoot, "bundle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);
        var apks = new List<string>();
        string? obbFolder = null, obbPackage = null;

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(bundle); }
        catch (InvalidDataException)
        {
            throw new AdbException(Loc.T($"{Path.GetFileName(bundle)} ist kein lesbares ZIP-Archiv (verschlüsselte .apkm-Dateien werden nicht unterstützt).",
                $"{Path.GetFileName(bundle)} is not a readable ZIP archive (encrypted .apkm files are not supported)."));
        }

        using (zip)
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;
                string name = entry.FullName.Replace('\\', '/');
                Match obb = ObbPath.Match(name);
                if (obb.Success)
                {
                    obbPackage ??= obb.Groups[1].Value;
                    obbFolder ??= Path.Combine(target, "obb", obbPackage);
                    string file = SafeCombine(obbFolder, obb.Groups[2].Value);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    entry.ExtractToFile(file, overwrite: true);
                }
                else if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    // Splits may sit in sub folders; keep only the file name (unique per bundle).
                    string file = Path.Combine(target, Unique(target, Path.GetFileName(name)));
                    entry.ExtractToFile(file, overwrite: false);
                    apks.Add(file);
                }
            }
        }

        if (apks.Count == 0)
            throw new AdbException(Loc.T($"In {Path.GetFileName(bundle)} wurde keine APK gefunden.", $"No APK found in {Path.GetFileName(bundle)}."));

        // base.apk first (not required by adb, but makes the log readable)
        apks = apks.OrderBy(a => Path.GetFileName(a).Equals("base.apk", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                   .ThenBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        return new Expanded(apks, obbFolder, obbPackage);
    }

    private static string Unique(string folder, string name)
    {
        string candidate = name;
        for (int i = 2; File.Exists(Path.Combine(folder, candidate)); i++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}";
        return candidate;
    }

    private static string SafeCombine(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            throw new AdbException("Invalid path in bundle: " + relative);
        return full;
    }

    /// <summary>Packs the split APKs of one app into an .apks file (entries: original device file names).</summary>
    public static void Create(string bundlePath, IReadOnlyList<(string File, string EntryName)> apks)
    {
        using var stream = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (file, entryName) in apks)
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.NoCompression); // APKs are already compressed
    }

    public static void TryDelete(string? folder)
    {
        try { if (folder is not null && Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
    }
}
