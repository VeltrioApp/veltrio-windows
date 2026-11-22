using System.Collections.Concurrent;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>Automatic TV episode intro detection via IntroDB (api.introdb.app).</summary>
public static class IntroService
{
    public record IntroWindow(double StartSec, double EndSec);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, IntroWindow?> Cache = new();

    public static async Task<IntroWindow?> FetchIntroAsync(string imdbId, int season, int episode)
    {
        if (!imdbId.StartsWith("tt", StringComparison.Ordinal) || season <= 0 || episode <= 0) return null;
        var key = $"{imdbId}|{season}|{episode}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        try
        {
            using var resp = await Http.GetAsync($"https://api.introdb.app/intro?imdb_id={imdbId}&season={season}&episode={episode}");
            if (!resp.IsSuccessStatusCode) { Cache[key] = null; return null; }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("start_sec", out var sEl) && sEl.TryGetDouble(out var s) &&
                root.TryGetProperty("end_sec", out var eEl) && eEl.TryGetDouble(out var e) &&
                e > s + 2.0)
            {
                var w = new IntroWindow(s, e);
                Cache[key] = w;
                return w;
            }
            Cache[key] = null;
            return null;
        }
        catch
        {
            Cache[key] = null;
            return null;
        }
    }
}
