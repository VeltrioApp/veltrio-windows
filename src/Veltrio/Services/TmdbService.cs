using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>Optional TMDB integration for localized movie and series titles
/// and synopses on top of Cinemeta / Stremio metadata. Accepts either a TMDB
/// v3 API key or a v4 Read Access Token.</summary>
public static class TmdbService
{
    private const string KeySecret = "tmdb.key";
    private const string LangSecret = "tmdb.lang";
    private const string BaseUrl = "https://api.themoviedb.org/3";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, LocalizedMeta> Cache = new();

    public static readonly (string Code, string Label)[] Languages =
    {
        ("fr-FR", "Français"),
        ("en-US", "English"),
        ("es-ES", "Español"),
        ("de-DE", "Deutsch"),
        ("it-IT", "Italiano"),
        ("pt-BR", "Português"),
        ("ja-JP", "日本語"),
        ("", "Désactivé (langue de l'addon)"),
    };

    public record LocalizedMeta(string? Title, string? Overview);

    public static bool IsConnected => !string.IsNullOrWhiteSpace(SecretStore.Get(KeySecret));
    public static string Language => SecretStore.Get(LangSecret) ?? "fr-FR";
    public static bool IsBusy { get; private set; }
    public static string? Error { get; private set; }

    public static event Action? Changed;

    public static void SetLanguage(string lang)
    {
        SecretStore.Set(LangSecret, lang);
        Changed?.Invoke();
    }

    public static async Task<bool> ConnectAsync(string rawKey)
    {
        var key = rawKey.Trim();
        if (string.IsNullOrEmpty(key))
        {
            Error = "Saisissez une clé API ou un jeton TMDB.";
            Changed?.Invoke();
            return false;
        }
        IsBusy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            using var req = BuildRequest("/configuration", key);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                Error = $"Clé TMDB invalide (HTTP {(int)resp.StatusCode}).";
                return false;
            }
            SecretStore.Set(KeySecret, key);
            if (SecretStore.Get(LangSecret) is null) SecretStore.Set(LangSecret, "fr-FR");
            Cache.Clear();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    public static void Disconnect()
    {
        SecretStore.Set(KeySecret, null);
        Cache.Clear();
        Error = null;
        Changed?.Invoke();
    }

    public static async Task<LocalizedMeta?> LocalizeAsync(string imdbId, string type)
    {
        var key = SecretStore.Get(KeySecret)?.Trim();
        var lang = Language;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(lang) || !imdbId.StartsWith("tt", StringComparison.Ordinal))
            return null;
        var cacheKey = $"{imdbId}|{type}|{lang}";
        if (Cache.TryGetValue(cacheKey, out var hit)) return hit;
        try
        {
            using var req = BuildRequest($"/find/{imdbId}?external_source=imdb_id&language={lang}", key);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
            var primary = FirstResult(root, isSeries ? "tv_results" : "movie_results")
                ?? FirstResult(root, isSeries ? "movie_results" : "tv_results");
            if (primary is not { } item) return null;
            string? title = null;
            if (item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
            else if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) title = n.GetString();
            string? overview = item.TryGetProperty("overview", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            var res = new LocalizedMeta(
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(overview) ? null : overview);
            Cache[cacheKey] = res;
            return res;
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? FirstResult(JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            return arr[0];
        return null;
    }

    private static HttpRequestMessage BuildRequest(string path, string key)
    {
        var isBearer = key.Length > 40 || key.Contains('.');
        var sep = path.Contains('?') ? '&' : '?';
        var url = isBearer ? $"{BaseUrl}{path}" : $"{BaseUrl}{path}{sep}api_key={key}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (isBearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return req;
    }
}
