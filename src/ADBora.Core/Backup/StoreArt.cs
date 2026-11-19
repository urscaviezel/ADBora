using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AdbTool.Core;

namespace AdbTool.Backup;

/// <summary>Where store artwork replaces the icon from the APK.</summary>
internal enum StoreArtMode
{
    Off,
    /// <summary>Meta Quest: every app found in the store shows its store cover (as in the headset library).</summary>
    All,
    /// <summary>Other headsets (ports of Quest APKs): only apps without icon or with an engine placeholder.</summary>
    Gaps,
}

/// <summary>
/// Square cover images from the Meta Horizon Store. The store has no official lookup by package
/// name, so the app name from the APK is searched (the same public GraphQL access the store web
/// pages use) and only an exact name match is taken; with several matches the installed version
/// code decides. Sends app names to Meta – switchable in the settings.
/// </summary>
internal static class StoreArt
{
    private const string Endpoint = "https://graph.oculus.com/graphql";
    private const string Token = "OC|752908224809889|";
    private const string SearchDoc = "2983924295012880";
    private const string VersionsDoc = "1586217024733717";
    private const int MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>Version of the matching rules; misses of an older revision are looked up again.</summary>
    public const int Revision = 2;

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ADBora/{AppInfo.Version}");
        return client;
    }

    /// <summary>
    /// The cover (PNG/JPEG bytes) or an empty array when the store has no matching app.
    /// A store app matches when its name equals the APK name, or when the APK name is part of the
    /// store name (internal names such as "Wrath2" → "Asgard's Wrath 2") and the installed version
    /// code exists in the store. Network and server errors throw – tried again on the next scan.
    /// </summary>
    public static async Task<byte[]> FindCoverAsync(HttpClient client, string name, string versionCode, CancellationToken token)
    {
        string wanted = Normalize(name);
        if (wanted.Length < 3)
            return Array.Empty<byte>();

        var queries = new List<string> { name };
        string split = SplitWords(name);
        if (split != name) queries.Add(split);

        foreach (string query in queries)
        {
            JsonNode search = await QueryAsync(client, SearchDoc,
                new JsonObject { ["query"] = query, ["hmdType"] = "MONTEREY", ["firstSearchResultItems"] = 8 }, token);
            var results = (search["data"]?["application_search"]?["results"]?["edges"] as JsonArray ?? new JsonArray())
                .Select(e => e?["node"])
                .Where(n => n is not null)
                .Select(n => (Id: n!["id"]?.GetValue<string>() ?? "", Name: Normalize(n["display_name"]?.GetValue<string>() ?? ""),
                              Cover: n["cover_square_image"]?["uri"]?.GetValue<string>() ?? ""))
                .Where(m => m.Id.Length > 0 && m.Name.Length > 0 && m.Cover.StartsWith("https://", StringComparison.Ordinal))
                .GroupBy(m => m.Id).Select(g => g.First())
                .Take(5)
                .ToList();

            var exact = results.Where(m => m.Name == wanted).ToList();
            var related = results.Where(m => m.Name != wanted && m.Name.Contains(wanted, StringComparison.Ordinal)).ToList();

            // Prefer a confirmed version: exact names first, then related names
            foreach (var m in exact.Concat(related))
                if (await HasVersionCodeAsync(client, m.Id, versionCode, token))
                    return await DownloadAsync(client, m.Cover, token);
            // Exact name alone is enough when it is unique (older / newer version installed)
            if (exact.Count == 1)
                return await DownloadAsync(client, exact[0].Cover, token);
        }
        return Array.Empty<byte>();
    }

    private static async Task<byte[]> DownloadAsync(HttpClient client, string url, CancellationToken token)
    {
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxImageBytes)
            return Array.Empty<byte>();
        byte[] image = await response.Content.ReadAsByteArrayAsync(token);
        if (image.Length > MaxImageBytes)
            return Array.Empty<byte>();
        using var check = ImageLoader.Decode(image);
        return check is null ? Array.Empty<byte>() : image;
    }

    /// <summary>"NexusVR" → "Nexus VR", "SyberiaVR" → "Syberia VR", "LivingRoom" → "Living Room".</summary>
    internal static string SplitWords(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && sb.Length > 0 && sb[^1] != ' ')
            {
                char p = name[i - 1];
                bool lowerToUpper = char.IsLower(p) && char.IsUpper(c);
                bool acronymEnd = char.IsUpper(p) && char.IsUpper(c) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                bool letterDigit = char.IsLetter(p) != char.IsLetter(c) && char.IsLetterOrDigit(p) && char.IsLetterOrDigit(c);
                if (lowerToUpper || acronymEnd || letterDigit) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static async Task<bool> HasVersionCodeAsync(HttpClient client, string appId, string versionCode, CancellationToken token)
    {
        if (!long.TryParse(versionCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
            return false;
        JsonNode versions = await QueryAsync(client, VersionsDoc, new JsonObject { ["id"] = appId }, token);
        return (versions["data"]?["node"]?["supportedBinaries"]?["edges"] as JsonArray ?? new JsonArray())
            .Any(e => e?["node"]?["versionCode"] is JsonValue v && v.TryGetValue(out long c) && c == code);
    }

    private static async Task<JsonNode> QueryAsync(HttpClient client, string docId, JsonObject variables, CancellationToken token)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["access_token"] = Token,
            ["doc_id"] = docId,
            ["variables"] = variables.ToJsonString(),
        });
        using HttpResponseMessage response = await client.PostAsync(Endpoint, form, token);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) ?? throw new InvalidDataException("empty store response");
    }

    /// <summary>Letters and digits only, lower case; "&amp;" = "and"; ™ ® © dropped.</summary>
    internal static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Replace("™", "").Replace("®", "").Replace("©", "").Replace("&", "and").Normalize(NormalizationForm.FormKD))
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
