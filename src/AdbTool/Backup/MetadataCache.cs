using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdbTool.Core;

namespace AdbTool.Backup;

/// <summary>
/// Persistent display metadata only (name, version, icon) — never APKs or app data.
/// File format is compatible with the former Python APK Backup Tool; its cache
/// (%LOCALAPPDATA%\APKBackupTool\metadata-cache) is used read-only as a fallback.
/// </summary>
internal sealed class MetadataCache
{
    private readonly string _root;
    private readonly string? _legacyRoot;
    private readonly string _serial;
    private readonly string _user;

    public static string DefaultRoot => Path.Combine(AppSettings.DataDirectory, "metadata-cache");

    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APKBackupTool", "metadata-cache");

    public MetadataCache(string serial, string user, string? root = null, string? legacyRoot = null)
    {
        _root = root ?? DefaultRoot;
        _legacyRoot = legacyRoot ?? LegacyRoot;
        _serial = serial;
        _user = user;
    }

    private string FileName(string package)
    {
        // Same key as Python: json.dumps([serial, user, package], ensure_ascii=False)
        string key = "[" + string.Join(", ", new[] { _serial, _user, package }.Select(s =>
            JsonSerializer.Serialize(s, AppSettings.JsonOptions))) + "]";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash).ToLowerInvariant() + ".json";
    }

    public bool TryRestore(AppEntry app, IReadOnlyList<string>? signature)
    {
        if (signature is null || signature.Count == 0)
            return false;

        foreach (string? root in new[] { _root, _legacyRoot })
        {
            if (root is null) continue;
            if (TryRestoreFrom(Path.Combine(root, FileName(app.Package)), app, signature))
                return true;
        }
        return false;
    }

    private bool TryRestoreFrom(string path, AppEntry app, IReadOnlyList<string> signature)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024)
                return false;

            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject data)
                return false;

            if (data["schema"]?.GetValue<int>() != 1 || data["package"]?.GetValue<string>() != app.Package)
                return false;

            if (data["scope"] is not JsonArray scope || scope.Count != 2 ||
                scope[0]?.GetValue<string>() != _serial || scope[1]?.GetValue<string>() != _user)
                return false;

            if (data["signature"] is not JsonArray sig || sig.Count != signature.Count ||
                sig.Select(n => n?.GetValue<string>()).Where((s, i) => s != signature[i]).Any())
                return false;

            string? name = data["name"]?.GetValue<string>();
            string? version = data["version"]?.GetValue<string>();
            string? versionCode = data["version_code"]?.GetValue<string>();
            string? icon = data["icon"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name) || name.Length > 4096 || version is null || version.Length > 4096 ||
                versionCode is null || versionCode.Length > 4096 || icon is null)
                return false;

            byte[] iconBytes = Convert.FromBase64String(icon);
            if (iconBytes.Length > 5 * 1024 * 1024)
                return false;

            app.Name = name.Trim().Length > 0 ? name.Trim() : app.Package;
            app.Version = version;
            app.VersionCode = versionCode;
            app.Icon = iconBytes;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Save(AppEntry app, IReadOnlyList<string>? signature)
    {
        if (signature is null || signature.Count == 0)
            return;

        var data = new JsonObject
        {
            ["schema"] = 1,
            ["scope"] = new JsonArray(_serial, _user),
            ["package"] = app.Package,
            ["signature"] = new JsonArray(signature.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["name"] = app.Name,
            ["version"] = app.Version,
            ["version_code"] = app.VersionCode,
            ["icon"] = Convert.ToBase64String(app.Icon)
        };

        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, FileName(app.Package));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, data.ToJsonString(), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
