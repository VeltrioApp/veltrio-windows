using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Veltrio.Services;

/// <summary>One addon in the catalogue.</summary>
public sealed class CatalogAddon
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Slug { get; init; } = "";
    public string? Description { get; init; }
    public string? Logo { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Resources { get; init; } = Array.Empty<string>();
    /// <summary>The manifest to install. Community entries get it from
    /// their detail page, on demand.</summary>
    public string? TransportUrl { get; set; }

    /// <summary>"CATALOGUES · FLUX · SOUS-TITRES", what it brings.</summary>
    public string Capabilities => string.Join(" · ", Resources.Select(r => r switch
    {
        "catalog" => "CATALOGUES",
        "stream" => "FLUX",
        "meta" => "FICHES",
        "subtitles" => "SOUS-TITRES",
        "addon_catalog" => "ADDONS",
        _ => null,
    }).OfType<string>().Distinct());

    public string TypesLabel => string.Join(" · ", Types.Select(t => CoreService.TypeLabel(t, plural: true)));
}

/// <summary>The addon catalogue, from two places as on macOS: Stremio's
/// official collection (a JSON list with manifest URLs) and the community
/// site stremio-addons.net (a Next.js app whose list pages embed manifests
/// in their React Server Components payload, scraped here).</summary>
public static class AddonCatalogService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    public const int CommunityPageCount = 70;

    private static List<CatalogAddon>? _official;
    private static readonly List<CatalogAddon> _community = new();

    public static int CommunityPagesLoaded { get; private set; }

    static AddonCatalogService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Veltrio-Windows/0.1");
    }

    public static async Task<IReadOnlyList<CatalogAddon>> OfficialAsync()
    {
        if (_official is not null) return _official;
        var json = JsonDocument.Parse(await Http.GetStringAsync("https://api.strem.io/addonscollection.json")).RootElement;
        _official = json.EnumerateArray()
            .Where(e => e.TryGetProperty("manifest", out _))
            .Select(e => FromManifest(e.GetProperty("manifest"), slug: "", transport: e.GetProperty("transportUrl").GetString()))
            .OfType<CatalogAddon>()
            .ToList();
        return _official;
    }

    public static IReadOnlyList<CatalogAddon> Community => _community;

    /// <summary>Fetches the next community pages (10 addons each).</summary>
    public static async Task LoadMoreCommunityAsync(int pages = 3)
    {
        var start = CommunityPagesLoaded + 1;
        for (var page = start; page < start + pages && page <= CommunityPageCount; page++)
        {
            var html = await Http.GetStringAsync(page == 1 ? "https://stremio-addons.net/addons" : $"https://stremio-addons.net/addons?page={page}");
            var known = _community.Select(a => a.Id).ToHashSet();
            var fresh = ParseCommunityPage(html).Where(a => known.Add(a.Id)).ToList();
            _community.AddRange(fresh);
            CommunityPagesLoaded = page;
            if (fresh.Count == 0) break;
        }
    }

    /// <summary>A community addon's manifest URL: the first mirror its
    /// detail page lists.</summary>
    public static async Task<string?> ResolveTransportAsync(CatalogAddon addon)
    {
        if (addon.TransportUrl is not null) return addon.TransportUrl;
        if (addon.Slug.Length == 0) return null;
        var html = await Http.GetStringAsync($"https://stremio-addons.net/addons/{addon.Slug}");
        var match = Regex.Match(html, @"https://[A-Za-z0-9._-]+[A-Za-z0-9._/%-]*manifest\.json");
        return addon.TransportUrl = match.Success ? match.Value : null;
    }

    // -------------------- parsing --------------------

    private static CatalogAddon? FromManifest(JsonElement m, string slug, string? transport)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("id", out var id) || !m.TryGetProperty("name", out var name)) return null;
        // `resources` mixes plain names and {name, types…} objects.
        var resources = m.TryGetProperty("resources", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()
                : x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToList()
            : new List<string>();
        var types = m.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new List<string>();
        return new CatalogAddon
        {
            Id = id.GetString() ?? "",
            Name = name.GetString() ?? "",
            Slug = slug,
            Description = DebridHttp.Str(m, "description"),
            Logo = DebridHttp.Str(m, "logo"),
            Version = DebridHttp.Str(m, "version"),
            Types = types,
            Resources = resources,
            TransportUrl = transport,
        };
    }

    /// <summary>Each addon in a list page is a `"href":"/addons/slug"`
    /// followed by its `"manifest":{…}`, inside the payload strings the page
    /// pushes with `self.__next_f.push([1,"…"])`.</summary>
    internal static List<CatalogAddon> ParseCommunityPage(string html)
    {
        var result = new List<CatalogAddon>();
        foreach (var body in PayloadBodies(html))
        {
            var from = 0;
            while ((from = body.IndexOf("\"href\":\"/addons/", from, StringComparison.Ordinal)) >= 0)
            {
                var slugStart = from + "\"href\":\"/addons/".Length;
                var slugEnd = slugStart;
                while (slugEnd < body.Length && (char.IsLetterOrDigit(body[slugEnd]) || body[slugEnd] is '.' or '-' or '_')) slugEnd++;
                var slug = body[slugStart..slugEnd];
                var key = body.IndexOf("\"manifest\":", slugEnd, StringComparison.Ordinal);
                from = slugEnd;
                if (slug.Length == 0 || key < 0) continue;
                var objStart = key + "\"manifest\":".Length;
                var objEnd = BalancedObjectEnd(body, objStart);
                if (objEnd < 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(body[objStart..objEnd]);
                    if (FromManifest(doc.RootElement, slug, transport: null) is { } addon) result.Add(addon);
                }
                catch (JsonException)
                {
                    // Not a manifest object after all.
                }
                from = objEnd;
            }
        }
        return result;
    }

    /// <summary>Unescaped JS string literals of the page's payload pushes,
    /// kept apart so a quote in one can't throw off the next.</summary>
    private static IEnumerable<string> PayloadBodies(string html)
    {
        const string marker = "self.__next_f.push([1,\"";
        var i = 0;
        while ((i = html.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
        {
            i += marker.Length;
            var sb = new StringBuilder();
            while (i < html.Length && html[i] != '"')
            {
                if (html[i] == '\\' && i + 1 < html.Length)
                {
                    var esc = html[i + 1];
                    if (esc == 'u' && i + 5 < html.Length && int.TryParse(html.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                    {
                        sb.Append((char)code);
                        i += 6;
                        continue;
                    }
                    sb.Append(esc switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => esc });
                    i += 2;
                    continue;
                }
                sb.Append(html[i++]);
            }
            yield return sb.ToString();
        }
    }

    /// <summary>Index just past the `{…}` object starting at <paramref name="start"/>, or -1.</summary>
    private static int BalancedObjectEnd(string text, int start)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
            }
            else if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return i + 1;
        }
        return -1;
    }
}
