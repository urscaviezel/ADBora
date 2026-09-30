using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AdbTool.Core;

/// <summary>How this copy of ADBora was deployed.</summary>
internal enum InstallKind { Installed, Portable, Other }

internal sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

internal sealed record ReleaseInfo(Version Version, string Tag, string Title, string Notes, string PageUrl, DateTime? Published, List<ReleaseAsset> Assets)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>
/// Update check against the GitHub releases of urscaviezel/ADBora and
/// installation of a downloaded update (installer or portable ZIP).
/// </summary>
internal static class Updater
{
    public const string RepoUrl = "https://github.com/urscaviezel/ADBora";
    public const string ReleasesUrl = RepoUrl + "/releases";
    private const string ApiLatest = "https://api.github.com/repos/urscaviezel/ADBora/releases/latest";

    /// <summary>Test switch (--pretend-version x.y.z): compare as if this version were installed.</summary>
    public static string? PretendVersion { get; set; }

    public static Version CurrentVersion
    {
        get
        {
            if (PretendVersion is not null && TryParseVersion(PretendVersion, out Version pretend))
                return pretend;
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string BuildDate =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "-";

    public static InstallKind Kind
    {
        get
        {
            if (AppSettings.IsPortable) return InstallKind.Portable;
            try
            {
                if (Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe").Any())
                    return InstallKind.Installed;
            }
            catch { }
            return InstallKind.Other;
        }
    }

    public static string KindText => Kind switch
    {
        InstallKind.Installed => Loc.T("Installiert", "Installed"),
        InstallKind.Portable => Loc.T("Portabel", "Portable"),
        _ => Loc.T("Ohne Installation (Entwicklungs-Build)", "Not installed (development build)")
    };

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ADBora/{CurrentVersion.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) text = text[..cut];
        if (Version.TryParse(text, out Version? parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }
        if (int.TryParse(text, out int major))
        {
            version = new Version(major, 0, 0);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    public static async Task<ReleaseInfo> GetLatestAsync(CancellationToken token)
    {
        using HttpClient client = CreateClient(TimeSpan.FromSeconds(20));
        using HttpResponseMessage response = await client.GetAsync(ApiLatest, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(Loc.T("Auf GitHub wurde noch keine Version veröffentlicht.", "No release has been published on GitHub yet."));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub: {(int)response.StatusCode} {response.ReasonPhrase}");

        JsonNode root = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))
                        ?? throw new InvalidDataException("GitHub: empty response");
        string tag = root["tag_name"]?.GetValue<string>() ?? "";
        if (!TryParseVersion(tag, out Version version))
            throw new InvalidDataException(Loc.T($"Unbekanntes Versionsformat: {tag}", $"Unknown version format: {tag}"));

        var assets = new List<ReleaseAsset>();
        if (root["assets"] is JsonArray list)
        {
            foreach (JsonNode? a in list)
            {
                if (a is null) continue;
                string digest = a["digest"]?.GetValue<string>() ?? "";
                assets.Add(new ReleaseAsset(
                    a["name"]?.GetValue<string>() ?? "",
                    a["browser_download_url"]?.GetValue<string>() ?? "",
                    a["size"]?.GetValue<long>() ?? 0,
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null));
            }
        }

        DateTime? published = DateTime.TryParse(root["published_at"]?.GetValue<string>(), out DateTime p) ? p.ToLocalTime() : null;
        return new ReleaseInfo(version, tag,
            root["name"]?.GetValue<string>() ?? tag,
            root["body"]?.GetValue<string>() ?? "",
            root["html_url"]?.GetValue<string>() ?? ReleasesUrl,
            published, assets);
    }

    public static bool IsNewer(ReleaseInfo release) => release.Version > CurrentVersion;

    /// <summary>The download matching this installation (setup or portable ZIP); null = manual update.</summary>
    public static ReleaseAsset? PackageFor(ReleaseInfo release) => Kind switch
    {
        InstallKind.Installed => release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("ADBora-Setup", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
        InstallKind.Portable => release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("ADBora-Portable", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)),
        _ => null
    };

    /// <summary>Portable update needs write access to the program folder.</summary>
    public static bool CanWriteProgramFolder()
    {
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, ".write-test-" + Guid.NewGuid().ToString("N")[..6]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string UpdateDirectory => Path.Combine(Path.GetTempPath(), "ADBora-Update");

    /// <summary>Removes leftovers of earlier updates (ignored if still in use).</summary>
    public static void CleanupDownloads()
    {
        try { if (Directory.Exists(UpdateDirectory)) Directory.Delete(UpdateDirectory, true); } catch { }
    }

    public static async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<(long Done, long Total)> progress, CancellationToken token)
    {
        Directory.CreateDirectory(UpdateDirectory);
        string target = Path.Combine(UpdateDirectory, Path.GetFileName(asset.Name));
        string partial = target + ".download";

        using (HttpClient client = CreateClient(TimeSpan.FromMinutes(30)))
        using (HttpResponseMessage response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using Stream source = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            byte[] buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                progress.Report((done, total));
            }
        }

        long length = new FileInfo(partial).Length;
        if (asset.Size > 0 && length != asset.Size)
            throw new InvalidDataException(Loc.T($"Download unvollständig ({length} von {asset.Size} Bytes).", $"Incomplete download ({length} of {asset.Size} bytes)."));
        if (asset.Sha256 is { } expected)
        {
            await using var stream = File.OpenRead(partial);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (actual != expected)
                throw new InvalidDataException(Loc.T("Prüfsumme des Downloads stimmt nicht (SHA-256).", "Download checksum mismatch (SHA-256)."));
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Starts the installation of a downloaded package. ADBora must exit
    /// right afterwards; the new version is started automatically.
    /// </summary>
    public static void StartInstall(string package)
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;

        if (Kind == InstallKind.Installed)
        {
            // Same install mode as before: per-machine installs need admin rights (UAC prompt).
            bool allUsers = IsUnder(exe, Environment.SpecialFolder.ProgramFiles) || IsUnder(exe, Environment.SpecialFolder.ProgramFilesX86);
            string args = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL /RELAUNCH=1 " + (allUsers ? "/ALLUSERS" : "/CURRENTUSER");
            Process.Start(new ProcessStartInfo(package, args) { UseShellExecute = true });
            return;
        }

        if (Kind == InstallKind.Portable)
        {
            string extract = Path.Combine(UpdateDirectory, "portable-" + Guid.NewGuid().ToString("N")[..8]);
            ZipFile.ExtractToDirectory(package, extract);
            string source = extract;
            if (!File.Exists(Path.Combine(source, "ADBora.exe")))
                source = Directory.GetDirectories(extract).FirstOrDefault(d => File.Exists(Path.Combine(d, "ADBora.exe")))
                         ?? throw new InvalidDataException(Loc.T("ADBora.exe fehlt im Update-Paket.", "ADBora.exe is missing in the update package."));

            string script = Path.Combine(UpdateDirectory, "apply-portable-update.ps1");
            File.WriteAllText(script, PortableScript, new UTF8Encoding(true));
            string target = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (string a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                         "-ProcessId", Environment.ProcessId.ToString(), "-Source", source, "-Target", target, "-Exe", exe })
                psi.ArgumentList.Add(a);
            Process.Start(psi);
            return;
        }

        throw new InvalidOperationException(Loc.T("Diese Kopie kann nicht automatisch aktualisiert werden.", "This copy cannot be updated automatically."));
    }

    private static bool IsUnder(string path, Environment.SpecialFolder folder)
    {
        string root = Environment.GetFolderPath(folder);
        return root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Waits for ADBora to exit, copies the new files over the portable folder and restarts it.</summary>
    private const string PortableScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Exe)
        $ErrorActionPreference = 'Stop'
        try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch { }
        Start-Sleep -Milliseconds 700
        $ok = $false
        $err = $null
        for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
            try {
                Get-ChildItem -LiteralPath $Source -Force | Where-Object { $_.Name -ne 'Data' } | ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination $Target -Recurse -Force
                }
                $ok = $true
            } catch {
                $err = $_
                Start-Sleep -Milliseconds 700
            }
        }
        if (-not $ok) {
            try {
                $dataDir = Join-Path $Target 'Data'
                New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
                "$(Get-Date -Format s) portable update failed: $err" | Out-File -FilePath (Join-Path $dataDir 'update.log') -Append -Encoding utf8
            } catch { }
        }
        Start-Process -FilePath $Exe
        Remove-Item -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue
        """;
}
