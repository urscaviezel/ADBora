using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdbTool.Core;

/// <summary>
/// scrcpy (https://github.com/Genymobile/scrcpy, Apache-2.0) – screen streaming over ADB.
/// ADBora does not bundle it; it is downloaded on request into the data folder.
/// </summary>
internal static class ScrcpyTool
{
    public const string ProjectUrl = "https://github.com/Genymobile/scrcpy";
    private const string ApiLatest = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
    private static readonly Regex AssetName = new(@"^scrcpy-win64-v[\d.]+\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ToolsDirectory => Path.Combine(AppSettings.DataDirectory, "tools");

    /// <summary>Configured path, else a downloaded copy, else scrcpy on PATH.</summary>
    public static string? Find(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        try
        {
            if (Directory.Exists(ToolsDirectory))
            {
                string? local = Directory.EnumerateFiles(ToolsDirectory, "scrcpy.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (local is not null) return local;
            }
        }
        catch { }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "scrcpy.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    public static async Task<string> VersionAsync(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            using Process p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(cts.Token);
            string first = output.Split('\n').FirstOrDefault(l => l.StartsWith("scrcpy", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "";
            Match m = Regex.Match(first, @"scrcpy\s+v?([\d.]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : first;
        }
        catch (Exception ex)
        {
            return Loc.T("unbekannt (", "unknown (") + ex.Message + ")";
        }
    }

    /// <summary>Downloads the latest Windows release from GitHub and returns the path of scrcpy.exe.</summary>
    public static async Task<string> DownloadLatestAsync(IProgress<(long Done, long Total)> progress, Action<string> log, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ADBora/{AppInfo.Version}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        JsonNode root = JsonNode.Parse(await client.GetStringAsync(ApiLatest, token))
                        ?? throw new InvalidDataException("GitHub: empty response");
        string tag = root["tag_name"]?.GetValue<string>() ?? "?";
        JsonNode asset = (root["assets"] as JsonArray)?.FirstOrDefault(a => AssetName.IsMatch(a?["name"]?.GetValue<string>() ?? ""))
                         ?? throw new InvalidDataException(Loc.T($"Kein Windows-Paket in scrcpy {tag} gefunden.", $"No Windows package found in scrcpy {tag}."));
        string name = asset["name"]!.GetValue<string>();
        string url = asset["browser_download_url"]!.GetValue<string>();
        long size = asset["size"]?.GetValue<long>() ?? 0;
        string digest = asset["digest"]?.GetValue<string>() ?? "";
        log(Loc.T($"Lade {name} ({size / 1024.0 / 1024.0:0.0} MB) von GitHub …", $"Downloading {name} ({size / 1024.0 / 1024.0:0.0} MB) from GitHub …"));

        string temp = Path.Combine(Path.GetTempPath(), "ADBora-scrcpy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        string zipPath = Path.Combine(temp, name);
        try
        {
            using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? size;
                await using Stream source = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
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

            if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(zipPath);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
                if (actual != digest[7..].ToLowerInvariant())
                    throw new InvalidDataException(Loc.T("Prüfsumme des scrcpy-Downloads stimmt nicht.", "Checksum of the scrcpy download does not match."));
                log(Loc.T("Prüfsumme (SHA-256) in Ordnung.", "Checksum (SHA-256) OK."));
            }

            string target = Path.Combine(ToolsDirectory, Path.GetFileNameWithoutExtension(name));
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            ZipFile.ExtractToDirectory(zipPath, target);
            string exe = Directory.EnumerateFiles(target, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidDataException(Loc.T("scrcpy.exe fehlt im Paket.", "scrcpy.exe is missing in the package."));
            log(Loc.T($"scrcpy {tag} installiert: {exe}", $"scrcpy {tag} installed: {exe}"));
            return exe;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
