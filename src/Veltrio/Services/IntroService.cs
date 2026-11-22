using System.Collections.Concurrent;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>Automatic TV episode recap / intro / outro detection via IntroDB (api.introdb.app).</summary>
public static class IntroService
{
    public record IntroWindow(double StartSec, double EndSec);
    public record IntroSegments(IntroWindow? Recap, IntroWindow? Intro, IntroWindow? Outro);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, IntroSegments?> Cache = new();

    private static IntroWindow? ParseWindow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("start_sec", out var sEl) && sEl.TryGetDouble(out var s) &&
            el.TryGetProperty("end_sec", out var eEl) && eEl.TryGetDouble(out var e) &&
            e > s + 2.0)
            return new IntroWindow(s, e);
        return null;
    }

    public static async Task<IntroSegments?> FetchSegmentsAsync(string imdbId, int season, int episode)
    {
        if (!imdbId.StartsWith("tt", StringComparison.Ordinal) || season <= 0 || episode <= 0) return null;
        var key = $"{imdbId}|{season}|{episode}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        IntroSegments? segs = null;
        try
        {
            using var resp = await Http.GetAsync($"https://api.introdb.app/segments?imdb_id={imdbId}&season={season}&episode={episode}");
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var recap = root.TryGetProperty("recap", out var rEl) ? ParseWindow(rEl) : null;
                var intro = root.TryGetProperty("intro", out var iEl) ? ParseWindow(iEl) : null;
                var outro = root.TryGetProperty("outro", out var oEl) ? ParseWindow(oEl) : null;
                if (recap is not null || intro is not null || outro is not null)
                    segs = new IntroSegments(recap, intro, outro);
            }
        }
        catch { }
        if (segs is null)
        {
            try
            {
                using var resp = await Http.GetAsync($"https://api.introdb.app/intro?imdb_id={imdbId}&season={season}&episode={episode}");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (ParseWindow(doc.RootElement) is { } intro)
                        segs = new IntroSegments(null, intro, null);
                }
            }
            catch { }
        }
        Cache[key] = segs;
        return segs;
    }
}
