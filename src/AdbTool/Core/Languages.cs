using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdbTool.Core;

/// <summary>A language file lang\&lt;code&gt;.json (texts keyed by the English original).</summary>
internal sealed class LanguageFile
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Path { get; init; } = "";
    /// <summary>File shipped next to ADBora.exe (read only) instead of the data folder.</summary>
    public bool Bundled { get; init; }
    public Dictionary<string, string> Texts { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>An entry of the language list in the GitHub repository.</summary>
internal sealed record OnlineLanguage(string Code, string Name, string Version, string Content);

/// <summary>
/// Additional languages besides the built-in German / English. Downloaded languages live in
/// &lt;data folder&gt;\lang (installer: %LOCALAPPDATA%\ADBora\lang, portable: Data\lang); files in a
/// "lang" folder next to ADBora.exe are used too. Online source: folder "lang" of the GitHub repository.
/// </summary>
internal static class Languages
{
    public const string OnlineSource = "https://api.github.com/repos/urscaviezel/ADBora/contents/lang?ref=main";
    private static readonly Regex CodeRe = new(@"^[a-z]{2,3}(-[a-z0-9]{2,8})?$", RegexOptions.Compiled);

    public static string UserDirectory => System.IO.Path.Combine(AppSettings.DataDirectory, "lang");
    public static string BundledDirectory => System.IO.Path.Combine(AppContext.BaseDirectory, "lang");

    public static bool IsValidCode(string code) => CodeRe.IsMatch(code) && code is not ("de" or "en");

    /// <summary>Installed language files (data folder wins over bundled files with the same code).</summary>
    public static List<LanguageFile> Installed()
    {
        var result = new Dictionary<string, LanguageFile>(StringComparer.OrdinalIgnoreCase);
        foreach ((string dir, bool bundled) in new[] { (BundledDirectory, true), (UserDirectory, false) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                string code = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (!IsValidCode(code)) continue;
                LanguageFile? lang = Read(file, code, bundled);
                if (lang is not null) result[code] = lang;
            }
        }
        return result.Values.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static LanguageFile? Load(string code) =>
        Installed().FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private static LanguageFile? Read(string path, string code, bool bundled)
    {
        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8), code, path, bundled);
        }
        catch { return null; }
    }

    /// <summary>Parses a language file; null if it is not a valid ADBora language file.</summary>
    public static LanguageFile? Parse(string json, string code, string path = "", bool bundled = false)
    {
        if (JsonNode.Parse(json.TrimStart('﻿')) is not JsonObject root || root["_meta"] is not JsonObject meta)
            return null;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in root)
        {
            if (key.StartsWith('_') || value is not JsonValue v || !v.TryGetValue(out string? text)) continue;
            texts[key] = text;
        }
        return new LanguageFile
        {
            Code = code,
            Name = meta["name"]?.GetValue<string>() is { Length: > 0 } n ? n : code,
            Version = meta["version"]?.GetValue<string>() ?? "",
            Path = path,
            Bundled = bundled,
            Texts = texts
        };
    }

    public static Version ParseVersion(string text) =>
        System.Version.TryParse(Regex.Match(text ?? "", @"\d+(\.\d+){0,3}").Value, out Version? v) ? v : new Version(0, 0);

    /// <summary>Lists the language files in the repository (downloads each file, they are small).</summary>
    public static async Task<List<OnlineLanguage>> OnlineAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ADBora/{AppInfo.Version}");
        using HttpResponseMessage response = await client.GetAsync(OnlineSource, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new List<OnlineLanguage>();   // no "lang" folder (yet)
        response.EnsureSuccessStatusCode();
        var list = new List<OnlineLanguage>();
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) is not JsonArray files)
            return list;
        foreach (JsonNode? f in files)
        {
            string name = f?["name"]?.GetValue<string>() ?? "";
            string? url = f?["download_url"]?.GetValue<string>();
            if (url is null || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string code = name[..^5].ToLowerInvariant();
            if (!IsValidCode(code)) continue;
            try
            {
                string content = await client.GetStringAsync(url, token);
                if (Parse(content, code) is { } lang)
                    list.Add(new OnlineLanguage(code, lang.Name, lang.Version, content));
            }
            catch (HttpRequestException) { }
        }
        return list;
    }

    public static void Install(OnlineLanguage language)
    {
        if (!IsValidCode(language.Code) || Parse(language.Content, language.Code) is null)
            throw new InvalidDataException(Loc.T($"Ungültige Sprachdatei: {language.Code}", $"Invalid language file: {language.Code}"));
        Directory.CreateDirectory(UserDirectory);
        File.WriteAllText(System.IO.Path.Combine(UserDirectory, language.Code + ".json"), language.Content, new UTF8Encoding(false));
    }

    public static void Remove(LanguageFile language)
    {
        if (!language.Bundled && File.Exists(language.Path))
            File.Delete(language.Path);
    }
}
