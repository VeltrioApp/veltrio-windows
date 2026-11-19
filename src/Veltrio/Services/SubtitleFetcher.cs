using System.Text.Json;

namespace Veltrio.Services;

/// <summary>A subtitle from an addon's `subtitles` resource.</summary>
public sealed record AddonSubtitle(string Url, string? Lang, string AddonName)
{
    public string Language => Player.MediaTrack.LanguageName(Lang) ?? "Langue inconnue";
}

/// <summary>Subtitles from every enabled addon that serves them (Stremio:
/// GET {base}/subtitles/{type}/{id}.json → {subtitles:[{id,url,lang}]}), in
/// parallel, 10 s each — as the macOS SubtitleFetcher.</summary>
public static class SubtitleFetcher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <param name="id">The episode id for a series (tt…:s:e), else the title id.</param>
    public static async Task<IReadOnlyList<AddonSubtitle>> FetchAsync(string type, string id)
    {
        var addons = CoreService.EnabledAddons.Where(a => a.Serves("subtitles", type)).ToList();
        var chunks = await Task.WhenAll(addons.Select(async addon =>
        {
            try
            {
                var root = addon.TransportUrl.Trim();
                if (root.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)) root = root[..^"/manifest.json".Length];
                var url = $"{root.TrimEnd('/')}/subtitles/{type}/{Uri.EscapeDataString(id).Replace("%3A", ":")}.json";
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (!doc.RootElement.TryGetProperty("subtitles", out var list) || list.ValueKind != JsonValueKind.Array)
                    return Array.Empty<AddonSubtitle>();
                return list.EnumerateArray()
                    .Select(s => (Url: DebridHttp.Str(s, "url"), Lang: DebridHttp.Str(s, "lang")))
                    .Where(s => !string.IsNullOrEmpty(s.Url))
                    .Select(s => new AddonSubtitle(s.Url!, s.Lang, addon.Name))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<AddonSubtitle>();
            }
        }));
        return chunks.SelectMany(c => c).OrderBy(s => s.Language, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>The first subtitle in one of the preferred languages
    /// (ISO 639-1 or -2, any synonym, then prefix match).</summary>
    public static AddonSubtitle? Pick(IEnumerable<AddonSubtitle> subtitles, IEnumerable<string> preferred)
    {
        var list = subtitles.ToList();
        foreach (var code in preferred)
        {
            var synonyms = Player.MediaTrack.Synonyms(code);
            if (list.FirstOrDefault(s => synonyms.Contains((s.Lang ?? "").ToLowerInvariant())) is { } exact) return exact;
            if (list.FirstOrDefault(s => synonyms.Any(c => (s.Lang ?? "").ToLowerInvariant().StartsWith(c))) is { } prefix) return prefix;
        }
        return null;
    }
}
