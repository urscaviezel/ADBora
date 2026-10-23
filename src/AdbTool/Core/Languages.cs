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

/// <summary>A language file that could not be read (shown in the language dialog with the reason).</summary>
internal sealed record BrokenLanguage(string Code, string FileName, string Error, bool Online);

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
    public static List<LanguageFile> Installed() => Installed(out _);

    public static List<LanguageFile> Installed(out List<BrokenLanguage> broken)
    {
        broken = new List<BrokenLanguage>();
        var result = new Dictionary<string, LanguageFile>(StringComparer.OrdinalIgnoreCase);
        foreach ((string dir, bool bundled) in new[] { (BundledDirectory, true), (UserDirectory, false) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            {
                string code = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                if (code.StartsWith('_') || code == "template") continue;
                if (!IsValidCode(code))
                {
                    if (code is not ("de" or "en"))
                        broken.Add(new BrokenLanguage(code, System.IO.Path.GetFileName(file), Loc.T(
                            "Dateiname muss der Sprachcode sein, z. B. it.json", "File name must be the language code, e.g. it.json"), false));
                    continue;
                }
                string text;
                try { text = File.ReadAllText(file, Encoding.UTF8); }
                catch (Exception ex) { broken.Add(new BrokenLanguage(code, System.IO.Path.GetFileName(file), ex.Message, false)); continue; }
                LanguageFile? lang = Parse(text, code, out string error, file, bundled);
                if (lang is not null) result[code] = lang;
                else broken.Add(new BrokenLanguage(code, System.IO.Path.GetFileName(file), error, false));
            }
        }
        return result.Values.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static LanguageFile? Load(string code) =>
        Installed().FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses a language file; null (with the reason) if it is not usable. Tolerant: text before the first
    /// "{" / after the last "}" (e.g. copied from a chat), comments and trailing commas are ignored; "_meta" is
    /// optional (name then comes from Windows, e.g. "Italiano").
    /// </summary>
    public static LanguageFile? Parse(string json, string code, out string error, string path = "", bool bundled = false)
    {
        error = "";
        string text = json.TrimStart('\uFEFF');
        int first = text.IndexOf('{'), last = text.LastIndexOf('}');
        if (first < 0 || last <= first)
        {
            error = Loc.T("Kein JSON-Objekt gefunden.", "No JSON object found.");
            return null;
        }
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text[first..(last + 1)], documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject;
        }
        catch (JsonException ex)
        {
            long line = (ex.LineNumber ?? 0) + 1 + text[..first].Count(c => c == '\n');
            error = Loc.T($"Ungültiges JSON in Zeile {line}: {FirstSentence(ex.Message)}", $"Invalid JSON in line {line}: {FirstSentence(ex.Message)}");
            return null;
        }
        if (root is null)
        {
            error = Loc.T("Kein JSON-Objekt gefunden.", "No JSON object found.");
            return null;
        }
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in root)
        {
            if (key.StartsWith('_') || value is not JsonValue v || !v.TryGetValue(out string? t)) continue;
            texts[key] = t;
        }
        if (texts.Count == 0)
        {
            error = Loc.T("Die Datei enthält keine Texte.", "The file contains no texts.");
            return null;
        }
        JsonObject? meta = root["_meta"] as JsonObject;
        string name = meta?["name"] is JsonValue nv && nv.TryGetValue(out string? n) && n.Length > 0 && !n.StartsWith("Language name") ? n : NativeName(code);
        return new LanguageFile
        {
            Code = code,
            Name = name,
            Version = meta?["version"] is JsonValue vv && vv.TryGetValue(out string? ver) ? ver : "",
            Path = path,
            Bundled = bundled,
            Texts = texts
        };
    }

    public static LanguageFile? Parse(string json, string code, string path = "", bool bundled = false) => Parse(json, code, out _, path, bundled);

    private static string FirstSentence(string message)
    {
        int i = message.IndexOf(" Path:", StringComparison.Ordinal);
        return (i > 0 ? message[..i] : message).Trim();
    }

    /// <summary>Language name in that language (from Windows), e.g. "it" -> "Italiano".</summary>
    public static string NativeName(string code)
    {
        try
        {
            var culture = new System.Globalization.CultureInfo(code);
            string n = culture.NativeName;
            return n.Length > 0 ? char.ToUpper(n[0], culture) + n[1..] : code;
        }
        catch { return code; }
    }

    // ------------------------------------------------------------------ checking a language file

    private static readonly Lazy<string[]> TemplateKeys = new(() =>
    {
        try
        {
            using Stream? stream = typeof(Languages).Assembly.GetManifestResourceStream("lang.template.json");
            if (stream is null) return Array.Empty<string>();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return JsonNode.Parse(reader.ReadToEnd()) is JsonObject o
                ? o.Select(kv => kv.Key).Where(k => !k.StartsWith('_')).ToArray()
                : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    });

    private static readonly Regex Placeholder = new(@"\{\d+(?:,[^}:]*)?(?::[^}]*)?\}", RegexOptions.Compiled);

    /// <summary>Result of checking a language file against all texts of this ADBora version.</summary>
    internal sealed record CheckResult(int Translated, int Total, List<string> Problems);

    /// <summary>How many texts are translated, and texts whose placeholders ({0} …) or line breaks do not match.</summary>
    public static CheckResult Check(LanguageFile lang)
    {
        string[] keys = TemplateKeys.Value;
        var problems = new List<string>();
        int translated = 0;
        foreach (string key in keys)
        {
            if (!lang.Texts.TryGetValue(key, out string? value) || value.Length == 0) continue;
            translated++;
            var a = Placeholder.Matches(key).Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
            var b = Placeholder.Matches(value).Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
            if (!a.SequenceEqual(b))
                problems.Add(Loc.T($"Platzhalter: „{Short(key)}“", $"Placeholders: \"{Short(key)}\""));
            else if (key.Count(c => c == '\n') != value.Count(c => c == '\n'))
                problems.Add(Loc.T($"Zeilenumbrüche: „{Short(key)}“", $"Line breaks: \"{Short(key)}\""));
        }
        return new CheckResult(translated, keys.Length, problems);
    }

    private static string Short(string text)
    {
        string one = text.Replace("\n", " ").Trim();
        return one.Length > 60 ? one[..57] + "…" : one;
    }

    public static Version ParseVersion(string text) =>
        System.Version.TryParse(Regex.Match(text ?? "", @"\d+(\.\d+){0,3}").Value, out Version? v) ? v : new Version(0, 0);

    /// <summary>Lists the language files in the repository (downloads each file, they are small).</summary>
    public static async Task<List<OnlineLanguage>> OnlineAsync(CancellationToken token) => (await OnlineWithErrorsAsync(token)).Languages;

    public static async Task<(List<OnlineLanguage> Languages, List<BrokenLanguage> Broken)> OnlineWithErrorsAsync(CancellationToken token)
    {
        var broken = new List<BrokenLanguage>();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ADBora/{AppInfo.Version}");
        using HttpResponseMessage response = await client.GetAsync(OnlineSource, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return (new List<OnlineLanguage>(), broken);   // no "lang" folder (yet)
        response.EnsureSuccessStatusCode();
        var list = new List<OnlineLanguage>();
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) is not JsonArray files)
            return (list, broken);
        foreach (JsonNode? f in files)
        {
            string name = f?["name"]?.GetValue<string>() ?? "";
            string? url = f?["download_url"]?.GetValue<string>();
            if (url is null || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string code = name[..^5].ToLowerInvariant();
            if (code.StartsWith('_') || code == "template") continue;
            if (!IsValidCode(code))
            {
                if (code is not ("de" or "en"))
                    broken.Add(new BrokenLanguage(code, name, Loc.T("Dateiname muss der Sprachcode sein, z. B. it.json", "File name must be the language code, e.g. it.json"), true));
                continue;
            }
            try
            {
                string content = await client.GetStringAsync(url, token);
                if (Parse(content, code, out string error) is { } lang)
                    list.Add(new OnlineLanguage(code, lang.Name, lang.Version, content));
                else
                    broken.Add(new BrokenLanguage(code, name, error, true));
            }
            catch (HttpRequestException ex) { broken.Add(new BrokenLanguage(code, name, ex.Message, true)); }
        }
        return (list, broken);
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
