using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>Native TMDB catalog, search, and metadata service for Windows.
/// Replaces Cinemeta so every catalog row, hero, search result, title page,
/// and episode list is localized in the user's chosen language while resolving
/// IMDb IDs (external_ids.imdb_id) for Stremio stream addons.</summary>
public static class TmdbService
{
    public const string AddonId = "tmdb";
    public const string TransportUrl = "tmdb://api.themoviedb.org/3";

    private const string KeySecret = "tmdb.key";
    private const string LangSecret = "tmdb.lang";
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImgPoster = "https://image.tmdb.org/t/p/w500";
    private const string ImgBackdrop = "https://image.tmdb.org/t/p/w1280";
    private const string ImgStill = "https://image.tmdb.org/t/p/w500";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, LocalizedMeta> LocalizeCache = new();
    private static readonly ConcurrentDictionary<string, Dictionary<int, string>> GenreCache = new();

    public static readonly (string Code, string Label)[] Languages =
    {
        ("fr-FR", "Français"),
        ("en-US", "English"),
        ("es-ES", "Español"),
        ("de-DE", "Deutsch"),
        ("it-IT", "Italiano"),
        ("pt-BR", "Português"),
        ("ja-JP", "日本語"),
    };

    public record LocalizedMeta(string? Title, string? Overview);

    public static bool IsConnected => !string.IsNullOrWhiteSpace(SecretStore.Get(KeySecret));
    public static string Language
    {
        get
        {
            var saved = SecretStore.Get(LangSecret);
            return string.IsNullOrWhiteSpace(saved) ? DefaultLanguage() : saved;
        }
    }
    public static bool IsBusy { get; private set; }
    public static string? Error { get; private set; }

    public static event Action? Changed;

    private static string DefaultLanguage() => AppPrefs.IsFrench ? "fr-FR" : "en-US";

    public static AddonInfo VirtualAddon() => new()
    {
        Id = AddonId,
        Version = "3.0.0",
        Name = "TMDB",
        Description = AppPrefs.Tr("Catalogues et fiches localisées via The Movie Database.", "Localized catalogs and metadata via The Movie Database."),
        Types = new List<string> { "movie", "series" },
        Resources = new List<string> { "catalog", "meta" },
        Catalogs = new List<CatalogInfo>
        {
            new() { Type = "movie", Id = "trending", Name = AppPrefs.Tr("Films populaires", "Trending Movies"), Extras = new List<string> { "skip", "search" } },
            new() { Type = "series", Id = "trending", Name = AppPrefs.Tr("Séries populaires", "Trending Series"), Extras = new List<string> { "skip", "search" } },
            new() { Type = "movie", Id = "now_playing", Name = AppPrefs.Tr("Au cinéma", "Now Playing"), Extras = new List<string> { "skip" } },
            new() { Type = "series", Id = "on_the_air", Name = AppPrefs.Tr("Séries du moment", "On The Air"), Extras = new List<string> { "skip" } },
            new() { Type = "movie", Id = "top_rated", Name = AppPrefs.Tr("Films les mieux notés", "Top Rated Movies"), Extras = new List<string> { "skip" } },
            new() { Type = "series", Id = "top_rated", Name = AppPrefs.Tr("Séries les mieux notées", "Top Rated Series"), Extras = new List<string> { "skip" } },
        },
        IdPrefixes = new List<string> { "tmdb:", "tt" },
        TransportUrl = TransportUrl,
    };

    public static void SetLanguage(string lang)
    {
        var clean = string.IsNullOrWhiteSpace(lang) ? DefaultLanguage() : lang;
        SecretStore.Set(LangSecret, clean);
        LocalizeCache.Clear();
        GenreCache.Clear();
        CoreService.ClearCatalogCache();
        Changed?.Invoke();
    }

    public static async Task<bool> ConnectAsync(string rawKey)
    {
        var key = rawKey.Trim();
        if (string.IsNullOrEmpty(key))
        {
            Error = AppPrefs.Tr("Saisissez une clé API ou un jeton TMDB.", "Enter a TMDB API key or token.");
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
                Error = AppPrefs.Tr($"Clé TMDB invalide (HTTP {(int)resp.StatusCode}).", $"Invalid TMDB key (HTTP {(int)resp.StatusCode}).");
                return false;
            }
            SecretStore.Set(KeySecret, key);
            if (string.IsNullOrWhiteSpace(SecretStore.Get(LangSecret))) SecretStore.Set(LangSecret, DefaultLanguage());
            LocalizeCache.Clear();
            GenreCache.Clear();
            CoreService.ClearCatalogCache();
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
        LocalizeCache.Clear();
        GenreCache.Clear();
        CoreService.ClearCatalogCache();
        Error = null;
        Changed?.Invoke();
    }

    public static async Task<IReadOnlyList<PosterItem>> FetchCatalogAsync(string type, string catalogId, int skip = 0)
    {
        var key = SecretStore.Get(KeySecret)?.Trim();
        if (string.IsNullOrEmpty(key)) return Array.Empty<PosterItem>();
        var lang = Language;
        var page = (skip / 20) + 1;
        var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
        var path = (isSeries, catalogId) switch
        {
            (false, "trending") => $"/trending/movie/week?language={lang}&page={page}",
            (true, "trending") => $"/trending/tv/week?language={lang}&page={page}",
            (false, "now_playing") => $"/movie/now_playing?language={lang}&page={page}",
            (true, "on_the_air") => $"/tv/on_the_air?language={lang}&page={page}",
            (false, "top_rated") => $"/movie/top_rated?language={lang}&page={page}",
            (true, "top_rated") => $"/tv/top_rated?language={lang}&page={page}",
            _ => null,
        };
        if (path is null) return Array.Empty<PosterItem>();
        var genres = await GenreMapAsync(isSeries, lang, key);
        using var doc = await GetJsonAsync(path, key);
        var addon = VirtualAddon();
        var list = new List<PosterItem>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in results.EnumerateArray())
            {
                if (ParsePoster(el, isSeries ? "series" : "movie", genres, addon) is { } p) list.Add(p);
            }
        }
        return list;
    }

    public static async Task<IReadOnlyList<PosterItem>> SearchAsync(string query, string type)
    {
        var key = SecretStore.Get(KeySecret)?.Trim();
        if (string.IsNullOrEmpty(key)) return Array.Empty<PosterItem>();
        var lang = Language;
        var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
        var endpoint = isSeries ? "tv" : "movie";
        var encoded = Uri.EscapeDataString(query);
        var genres = await GenreMapAsync(isSeries, lang, key);
        using var doc = await GetJsonAsync($"/search/{endpoint}?query={encoded}&language={lang}&include_adult=false", key);
        var addon = VirtualAddon();
        var list = new List<PosterItem>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in results.EnumerateArray())
            {
                if (ParsePoster(el, isSeries ? "series" : "movie", genres, addon) is { } p) list.Add(p);
            }
        }
        return list;
    }

    public static readonly (int Id, string Name)[] DiscoverProviders =
    {
        (8, "Netflix"), (350, "Apple TV+"), (337, "Disney+"),
        (119, "Prime Video"), (1899, "Max"), (381, "Canal+"),
    };

    public static readonly (int MovieId, int TvId, string Fr, string En)[] DiscoverGenres =
    {
        (28, 10759, "Action", "Action"),
        (878, 10765, "Sci-Fi", "Sci-Fi"),
        (35, 35, "Comédie", "Comedy"),
        (18, 18, "Drame", "Drama"),
        (53, 9648, "Thriller", "Thriller"),
        (16, 16, "Animation", "Animation"),
        (27, 9648, "Horreur", "Horror"),
        (99, 99, "Documentaire", "Documentary"),
    };

    public static async Task<IReadOnlyList<PosterItem>> DiscoverAsync(string type, int? genreId = null, int? providerId = null, int page = 1)
    {
        var key = SecretStore.Get(KeySecret)?.Trim();
        if (string.IsNullOrEmpty(key)) return Array.Empty<PosterItem>();
        var lang = Language;
        var parts = lang.Split('-');
        var region = parts.Length > 1 ? parts[1].ToUpperInvariant() : "FR";
        var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
        var endpoint = isSeries ? "tv" : "movie";
        var q = new List<string> { $"language={lang}", "sort_by=popularity.desc", "include_adult=false", $"page={page}" };
        if (genreId is { } gid) q.Add($"with_genres={gid}");
        if (providerId is { } pid) { q.Add($"with_watch_providers={pid}"); q.Add($"watch_region={region}"); }
        var genres = await GenreMapAsync(isSeries, lang, key);
        using var doc = await GetJsonAsync($"/discover/{endpoint}?{string.Join("&", q)}", key);
        var addon = VirtualAddon();
        var list = new List<PosterItem>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            foreach (var el in results.EnumerateArray())
                if (ParsePoster(el, isSeries ? "series" : "movie", genres, addon) is { } p) list.Add(p);
        return list;
    }

    public static async Task<TitleDetails> FetchDetailsAsync(string type, string id)
    {
        var key = SecretStore.Get(KeySecret)?.Trim();
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(AppPrefs.Tr("Connectez votre clé TMDB dans les réglages.", "Connect your TMDB key in Settings."));
        var lang = Language;
        var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
        var tmdbId = await ResolveTmdbIdAsync(id, isSeries, lang, key)
            ?? throw new InvalidOperationException(AppPrefs.Tr("Titre introuvable sur TMDB.", "Title not found on TMDB."));

        var mediaType = isSeries ? "tv" : "movie";
        using var doc = await GetJsonAsync($"/{mediaType}/{tmdbId}?language={lang}&append_to_response=external_ids,credits,videos,recommendations", key);
        var root = doc.RootElement;

        string? imdbId = null;
        if (root.TryGetProperty("external_ids", out var ext) && ext.ValueKind == JsonValueKind.Object)
            imdbId = Str(ext, "imdb_id");
        imdbId ??= Str(root, "imdb_id");
        if (imdbId is null || !imdbId.StartsWith("tt", StringComparison.Ordinal))
            imdbId = id.StartsWith("tt", StringComparison.Ordinal) ? id : null;
        var canonicalId = imdbId ?? $"tmdb:{tmdbId}";

        var title = (Str(root, "title") ?? Str(root, "name") ?? Str(root, "original_title") ?? Str(root, "original_name") ?? "").Trim();
        var overview = Str(root, "overview");
        var poster = Str(root, "poster_path") is { } pp ? ImgPoster + pp : null;
        var backdrop = Str(root, "backdrop_path") is { } bp ? ImgBackdrop + bp : null;
        var rating = DoubleVal(root, "vote_average") is { } v && v > 0 ? v.ToString("0.0", CultureInfo.InvariantCulture) : null;
        var dateStr = Str(root, "release_date") ?? Str(root, "first_air_date");
        var year = dateStr is { Length: >= 4 } ? dateStr[..4] : null;
        int? runtimeMin = null;
        if (isSeries)
        {
            if (root.TryGetProperty("episode_run_time", out var ert) && ert.ValueKind == JsonValueKind.Array && ert.GetArrayLength() > 0 && ert[0].TryGetInt32(out var r))
                runtimeMin = r;
        }
        else if (root.TryGetProperty("runtime", out var rt) && rt.TryGetInt32(out var r))
        {
            runtimeMin = r;
        }
        var runtime = runtimeMin is > 0 ? $"{runtimeMin} min" : null;

        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var gArr) && gArr.ValueKind == JsonValueKind.Array)
            foreach (var g in gArr.EnumerateArray()) if (Str(g, "name") is { } gn) genres.Add(gn);

        var cast = new List<string>();
        var directors = new List<string>();
        var writers = new List<string>();
        if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            if (credits.TryGetProperty("cast", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                foreach (var c in cArr.EnumerateArray().Take(12)) if (Str(c, "name") is { } cn) cast.Add(cn);
            if (credits.TryGetProperty("crew", out var crewArr) && crewArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in crewArr.EnumerateArray())
                {
                    var name = Str(c, "name");
                    if (name is null) continue;
                    if (!isSeries && Str(c, "job") == "Director") directors.Add(name);
                    if (Str(c, "department") == "Writing" && !writers.Contains(name) && writers.Count < 4) writers.Add(name);
                }
            }
        }
        if (isSeries && root.TryGetProperty("created_by", out var cbArr) && cbArr.ValueKind == JsonValueKind.Array)
            foreach (var c in cbArr.EnumerateArray()) if (Str(c, "name") is { } cn) directors.Add(cn);

        var episodes = new List<EpisodeItem>();
        if (isSeries && root.TryGetProperty("seasons", out var sArr) && sArr.ValueKind == JsonValueKind.Array)
        {
            var seasonNums = sArr.EnumerateArray()
                .Select(s => s.TryGetProperty("season_number", out var sn) && sn.TryGetInt32(out var n) ? n : 0)
                .Where(n => n > 0).Distinct().OrderBy(n => n).Take(30).ToList();
            var chunks = await Task.WhenAll(seasonNums.Select(async sNum =>
            {
                try
                {
                    using var sDoc = await GetJsonAsync($"/tv/{tmdbId}/season/{sNum}?language={lang}", key);
                    var eps = new List<EpisodeItem>();
                    if (sDoc.RootElement.TryGetProperty("episodes", out var eArr) && eArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ep in eArr.EnumerateArray())
                        {
                            if (!ep.TryGetProperty("episode_number", out var en) || !en.TryGetInt32(out var epNum)) continue;
                            var epTitle = Str(ep, "name") ?? AppPrefs.Tr($"Épisode {epNum}", $"Episode {epNum}");
                            eps.Add(new EpisodeItem
                            {
                                Id = $"{canonicalId}:{sNum}:{epNum}",
                                Title = epTitle,
                                Season = sNum,
                                Episode = epNum,
                                Thumbnail = Str(ep, "still_path") is { } sp ? ImgStill + sp : null,
                                Overview = Str(ep, "overview"),
                                Released = Str(ep, "air_date"),
                            });
                        }
                    }
                    return eps;
                }
                catch
                {
                    return new List<EpisodeItem>();
                }
            }));
            foreach (var c in chunks) episodes.AddRange(c);
        }

        var langShort = lang.Split('-')[0];
        var trailerUrl = PickTrailer(root, langShort);
        var addon = VirtualAddon();
        var genresMap = await GenreMapAsync(isSeries, lang, key);
        var recs = new List<PosterItem>();
        if (root.TryGetProperty("recommendations", out var recObj) && recObj.TryGetProperty("results", out var recArr) && recArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in recArr.EnumerateArray().Take(12))
                if (ParsePoster(el, isSeries ? "series" : "movie", genresMap, addon) is { } p) recs.Add(p);
        }

        return new TitleDetails
        {
            Id = canonicalId,
            Type = isSeries ? "series" : "movie",
            Name = title,
            Poster = poster,
            Background = backdrop,
            Description = overview,
            Rating = rating,
            Year = year,
            Runtime = runtime,
            Genres = genres,
            Cast = cast,
            Director = directors,
            Writer = writers,
            Episodes = episodes,
            Addon = addon,
            TrailerUrl = trailerUrl,
            Recommendations = recs,
        };
    }

    private static string? PickTrailer(JsonElement root, string langShort)
    {
        if (!root.TryGetProperty("videos", out var vObj) || !vObj.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var yt = arr.EnumerateArray()
            .Where(v => string.Equals(Str(v, "site"), "YouTube", StringComparison.OrdinalIgnoreCase) && Str(v, "key") is not null)
            .ToList();
        if (yt.Count == 0) return null;
        var chosen = yt.FirstOrDefault(v => Str(v, "type") == "Trailer" && Str(v, "iso_639_1") == langShort);
        if (chosen.ValueKind == JsonValueKind.Undefined) chosen = yt.FirstOrDefault(v => Str(v, "type") == "Trailer");
        if (chosen.ValueKind == JsonValueKind.Undefined) chosen = yt.FirstOrDefault(v => Str(v, "type") == "Teaser");
        if (chosen.ValueKind == JsonValueKind.Undefined) chosen = yt[0];
        return Str(chosen, "key") is { } k ? $"https://www.youtube.com/watch?v={k}" : null;
    }

    private static async Task<string?> ResolveTmdbIdAsync(string id, bool isSeries, string lang, string key)
    {
        if (id.StartsWith("tmdb:", StringComparison.Ordinal)) return id[5..];
        if (id.StartsWith("tt", StringComparison.Ordinal))
        {
            using var doc = await GetJsonAsync($"/find/{id}?external_source=imdb_id&language={lang}", key);
            var primary = FirstResult(doc.RootElement, isSeries ? "tv_results" : "movie_results")
                ?? FirstResult(doc.RootElement, isSeries ? "movie_results" : "tv_results");
            if (primary is { } item && item.TryGetProperty("id", out var tid) && tid.TryGetInt64(out var n) && n > 0)
                return n.ToString(CultureInfo.InvariantCulture);
            return null;
        }
        return long.TryParse(id, out var parsed) ? parsed.ToString(CultureInfo.InvariantCulture) : null;
    }

    public static async Task<LocalizedMeta?> LocalizeAsync(string imdbId, string type)
    {
        var key = SecretStore.Get(KeySecret)?.Trim();
        var lang = Language;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(lang) || !imdbId.StartsWith("tt", StringComparison.Ordinal))
            return null;
        var cacheKey = $"{imdbId}|{type}|{lang}";
        if (LocalizeCache.TryGetValue(cacheKey, out var hit)) return hit;
        try
        {
            using var doc = await GetJsonAsync($"/find/{imdbId}?external_source=imdb_id&language={lang}", key);
            var root = doc.RootElement;
            var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
            var primary = FirstResult(root, isSeries ? "tv_results" : "movie_results")
                ?? FirstResult(root, isSeries ? "movie_results" : "tv_results");
            if (primary is not { } item) return null;
            var title = Str(item, "title") ?? Str(item, "name");
            var overview = Str(item, "overview");
            var res = new LocalizedMeta(title, overview);
            LocalizeCache[cacheKey] = res;
            return res;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Dictionary<int, string>> GenreMapAsync(bool isSeries, string lang, string key)
    {
        var cacheKey = $"{(isSeries ? "tv" : "movie")}|{lang}";
        if (GenreCache.TryGetValue(cacheKey, out var hit)) return hit;
        try
        {
            using var doc = await GetJsonAsync($"/genre/{(isSeries ? "tv" : "movie")}/list?language={lang}", key);
            var map = new Dictionary<int, string>();
            if (doc.RootElement.TryGetProperty("genres", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in arr.EnumerateArray())
                {
                    if (g.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var gid) && Str(g, "name") is { } name)
                        map[gid] = name;
                }
            }
            GenreCache[cacheKey] = map;
            return map;
        }
        catch
        {
            return new Dictionary<int, string>();
        }
    }

    private static PosterItem? ParsePoster(JsonElement el, string type, Dictionary<int, string> genres, AddonInfo addon)
    {
        if (!el.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var tmdbId) || tmdbId <= 0) return null;
        var title = Str(el, "title") ?? Str(el, "name") ?? Str(el, "original_title") ?? Str(el, "original_name");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var dateStr = Str(el, "release_date") ?? Str(el, "first_air_date");
        var year = dateStr is { Length: >= 4 } ? dateStr[..4] : null;
        var rating = DoubleVal(el, "vote_average") is { } v && v > 0 ? v.ToString("0.0", CultureInfo.InvariantCulture) : null;
        var itemGenres = new List<string>();
        if (el.TryGetProperty("genre_ids", out var gArr) && gArr.ValueKind == JsonValueKind.Array)
            foreach (var g in gArr.EnumerateArray())
                if (g.TryGetInt32(out var gid) && genres.TryGetValue(gid, out var gName)) itemGenres.Add(gName);

        return new PosterItem
        {
            Id = $"tmdb:{tmdbId}",
            Type = type,
            Title = title,
            PosterUrl = Str(el, "poster_path") is { } pp ? ImgPoster + pp : null,
            BackgroundUrl = Str(el, "backdrop_path") is { } bp ? ImgBackdrop + bp : null,
            Description = Str(el, "overview"),
            Year = year,
            Rating = rating,
            Genres = itemGenres,
            Addon = addon,
        };
    }

    private static async Task<JsonDocument> GetJsonAsync(string path, string key)
    {
        using var req = BuildRequest(path, key);
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static JsonElement? FirstResult(JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            return arr[0];
        return null;
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    private static double? DoubleVal(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.TryGetDouble(out var d) ? d : null;

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
