using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>A short-EPG / catch-up entry from player_api.php.</summary>
public sealed class XtreamProgramme
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public bool HasArchive { get; init; }

    public string TimeRange => $"{Start.ToLocalTime():ddd HH:mm} – {End.ToLocalTime():HH:mm}";
    /// <summary>"lun. 20:30", the replay list's first column.</summary>
    public string StartLabel => $"{Start.ToLocalTime():ddd HH:mm}";
    public int DurationMinutes => Math.Max(1, (int)Math.Ceiling((End - Start).TotalMinutes));
}

/// <summary>Xtream Codes, through the provider's player_api.php, mapped to
/// the same <see cref="ChannelItem"/> as M3U entries. Same behaviour as
/// the macOS client: per-row tolerant decoding (panels mix strings and
/// numbers), auth failures told apart from empty lists.</summary>
public static class XtreamClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    static XtreamClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Veltrio-Windows/0.1");
    }

    public sealed class AuthException() : Exception("Identifiants Xtream refusés : vérifiez l'utilisateur et le mot de passe.");

    // -------------------- account --------------------

    /// <summary>Checks the account; returns the expiry date if the panel gives one.</summary>
    public static async Task<DateTime?> VerifyAsync(XtreamAccount account)
    {
        var json = await GetAsync(account, action: null);
        if (!json.TryGetProperty("user_info", out var user) || user.ValueKind != JsonValueKind.Object) throw new AuthException();
        if (Text(user, "auth") == "0") throw new AuthException();
        if (Text(user, "status") is { } status && status.ToLowerInvariant() is not ("active" or "trial"))
            throw new InvalidOperationException($"Compte Xtream {status}.");
        return long.TryParse(Text(user, "exp_date"), out var exp) && exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : null;
    }

    // -------------------- catalogues --------------------

    public static async Task<IReadOnlyList<ChannelItem>> LiveAsync(XtreamAccount account)
    {
        var categories = Categories(await GetAsync(account, "get_live_categories"));
        var ext = string.IsNullOrEmpty(account.Output) ? "ts" : account.Output;
        return Rows(await GetAsync(account, "get_live_streams")).Select(s =>
        {
            var id = Text(s, "stream_id");
            if (string.IsNullOrEmpty(id)) return null;
            var name = Text(s, "name") is { Length: > 0 } n ? n : $"Chaîne {id}";
            return new ChannelItem
            {
                Id = $"xtream-{id}",
                Name = name,
                StreamUrl = $"{account.BaseUrl}/live/{Seg(account.Username)}/{Seg(account.Password)}/{id}.{ext}",
                Logo = Nonempty(Text(s, "stream_icon")),
                Group = categories.GetValueOrDefault(Text(s, "category_id") ?? "") ?? "Sans groupe",
                TvgId = Nonempty(Text(s, "epg_channel_id")),
                TvgName = name,
                XtreamId = id,
                ArchiveDays = Text(s, "tv_archive") == "1" && int.TryParse(Text(s, "tv_archive_duration"), out var days) ? Math.Max(days, 1) : 0,
            };
        }).OfType<ChannelItem>().ToList();
    }

    public static async Task<IReadOnlyList<ChannelItem>> MoviesAsync(XtreamAccount account)
    {
        var categories = await CategoriesAsync(account, "get_vod_categories");
        var rows = Rows(await GetAsync(account, "get_vod_streams")).ToList();
        Remember(account, "get_vod_streams", rows);
        return rows.Select(s => Movie(account, s, categories)).OfType<ChannelItem>().ToList();
    }

    public static async Task<IReadOnlyList<ChannelItem>> SeriesAsync(XtreamAccount account)
    {
        var categories = await CategoriesAsync(account, "get_series_categories");
        var rows = Rows(await GetAsync(account, "get_series")).ToList();
        Remember(account, "get_series", rows);
        return rows.Select(s => Series(s, categories)).OfType<ChannelItem>().ToList();
    }

    private static ChannelItem? Movie(XtreamAccount account, JsonElement s, IReadOnlyDictionary<string, string> categories)
    {
        var id = Text(s, "stream_id");
        if (string.IsNullOrEmpty(id)) return null;
        var ext = Nonempty(Text(s, "container_extension")) ?? "mp4";
        return new ChannelItem
        {
            Id = $"xvod-{id}",
            Name = Text(s, "name") is { Length: > 0 } n ? n : $"Film {id}",
            StreamUrl = $"{account.BaseUrl}/movie/{Seg(account.Username)}/{Seg(account.Password)}/{id}.{ext}",
            Logo = Nonempty(Text(s, "stream_icon")),
            Group = categories.GetValueOrDefault(Text(s, "category_id") ?? "") ?? "Films",
            Kind = ChannelKind.Movie,
            XtreamId = id,
            Rating = Rating(Text(s, "rating")),
            Year = Nonempty(Text(s, "year")),
        };
    }

    private static ChannelItem? Series(JsonElement s, IReadOnlyDictionary<string, string> categories)
    {
        var id = Text(s, "series_id");
        if (string.IsNullOrEmpty(id)) return null;
        var released = Nonempty(Text(s, "releaseDate"));
        return new ChannelItem
        {
            Id = $"xseries-{id}",
            Name = Text(s, "name") is { Length: > 0 } n ? n : $"Série {id}",
            Logo = Nonempty(Text(s, "cover")),
            Group = categories.GetValueOrDefault(Text(s, "category_id") ?? "") ?? "Séries",
            Kind = ChannelKind.Series,
            XtreamId = id,
            Rating = Rating(Text(s, "rating")),
            Year = released?[..Math.Min(4, released.Length)],
        };
    }

    // -------------------- search --------------------

    /// <summary>Films then series matching the query, from the provider.
    /// Panels take a search= parameter on the list endpoints, so this is a
    /// server-side filter; many ignore it and send everything, so rows are
    /// also matched here, and such a full answer is kept for 15 minutes so
    /// the next search filters it locally instead of downloading it again.</summary>
    public static async Task<IReadOnlyList<ChannelItem>> SearchAsync(XtreamAccount account, string query, int limit = 40)
    {
        query = query.Trim();
        if (query.Length == 0) return Array.Empty<ChannelItem>();
        var films = SearchRowsAsync(account, "get_vod_streams", query);
        var series = SearchRowsAsync(account, "get_series", query);
        var vodCategories = CategoriesAsync(account, "get_vod_categories");
        var seriesCategories = CategoriesAsync(account, "get_series_categories");
        var result = new List<ChannelItem>();
        result.AddRange((await films).Where(r => TitleMatches(Text(r, "name"), query)).Take(limit)
            .Select(r => Movie(account, r, SafeResult(vodCategories))).OfType<ChannelItem>());
        result.AddRange((await series).Where(r => TitleMatches(Text(r, "name"), query)).Take(limit)
            .Select(r => Series(r, SafeResult(seriesCategories))).OfType<ChannelItem>());
        return result.Take(limit).ToList();
    }

    private static IReadOnlyDictionary<string, string> SafeResult(Task<IReadOnlyDictionary<string, string>> task) =>
        task.IsCompletedSuccessfully ? task.Result : new Dictionary<string, string>();

    private static async Task<IReadOnlyList<JsonElement>> SearchRowsAsync(XtreamAccount account, string action, string query)
    {
        if (Remembered(account, action) is { } full) return full;
        try
        {
            var rows = Rows(await GetAsync(account, action, $"&search={Uri.EscapeDataString(query)}")).ToList();
            if (rows.Count >= 500) Remember(account, action, rows);
            return rows;
        }
        catch
        {
            return Array.Empty<JsonElement>();
        }
    }

    /// <summary>Every word of the query in the name, ignoring case and accents.</summary>
    public static bool TitleMatches(string? name, string query)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var haystack = EpgStore.Fold(name);
        var words = EpgStore.Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(haystack.Contains);
    }

    // Full catalogues seen this session, per account and action, 15 minutes.
    private static readonly Dictionary<string, (DateTime Stored, IReadOnlyList<JsonElement> Rows)> Catalogues = new();
    private static readonly Dictionary<string, Task<IReadOnlyDictionary<string, string>>> CategoryCache = new();

    private static string Key(XtreamAccount a, string action) => $"{a.BaseUrl}|{a.Username}|{action}";

    private static void Remember(XtreamAccount account, string action, IReadOnlyList<JsonElement> rows)
    {
        lock (Catalogues) Catalogues[Key(account, action)] = (DateTime.UtcNow, rows);
    }

    private static IReadOnlyList<JsonElement>? Remembered(XtreamAccount account, string action)
    {
        lock (Catalogues)
            return Catalogues.TryGetValue(Key(account, action), out var e) && DateTime.UtcNow - e.Stored < TimeSpan.FromMinutes(15) ? e.Rows : null;
    }

    private static Task<IReadOnlyDictionary<string, string>> CategoriesAsync(XtreamAccount account, string action)
    {
        lock (CategoryCache)
        {
            var key = Key(account, action);
            if (CategoryCache.TryGetValue(key, out var cached) && !cached.IsFaulted) return cached;
            var task = Load();
            CategoryCache[key] = task;
            return task;
        }

        async Task<IReadOnlyDictionary<string, string>> Load() => Categories(await GetAsync(account, action));
    }

    public static async Task<XtreamSeries> SeriesInfoAsync(XtreamAccount account, string seriesId)
    {
        var json = await GetAsync(account, "get_series_info", $"&series_id={Uri.EscapeDataString(seriesId)}");
        if (json.ValueKind != JsonValueKind.Object) return new XtreamSeries { Name = $"Série {seriesId}" };
        var info = json.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var name = info.ValueKind == JsonValueKind.Object ? Nonempty(Text(info, "name")) : null;
        var episodes = new List<XtreamEpisode>();
        if (json.TryGetProperty("episodes", out var bySeason))
        {
            // An object keyed by season, or (some panels) an array of arrays.
            var groups = bySeason.ValueKind == JsonValueKind.Object
                ? bySeason.EnumerateObject().Select(p => (Key: p.Name, Rows: p.Value))
                : bySeason.ValueKind == JsonValueKind.Array
                    ? bySeason.EnumerateArray().Select((v, idx) => (Key: (idx + 1).ToString(), Rows: v))
                    : Enumerable.Empty<(string, JsonElement)>();
            foreach (var (key, rows) in groups)
            {
                if (rows.ValueKind != JsonValueKind.Array) continue;
                var index = 0;
                foreach (var e in rows.EnumerateArray())
                {
                    index++;
                    var id = Text(e, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var season = int.TryParse(Text(e, "season"), out var s) ? s : int.TryParse(key, out var k) ? k : 1;
                    var number = int.TryParse(Text(e, "episode_num"), out var n) ? n : index;
                    var epInfo = e.TryGetProperty("info", out var ei) && ei.ValueKind == JsonValueKind.Object ? ei : default;
                    var ext = Nonempty(Text(e, "container_extension")) ?? "mp4";
                    episodes.Add(new XtreamEpisode
                    {
                        Id = id,
                        Season = season,
                        Number = number,
                        Title = Nonempty(Text(e, "title")) ?? $"Épisode {number}",
                        Plot = epInfo.ValueKind == JsonValueKind.Object ? Nonempty(Text(epInfo, "plot")) : null,
                        Image = epInfo.ValueKind == JsonValueKind.Object ? Nonempty(Text(epInfo, "movie_image")) : null,
                        Duration = epInfo.ValueKind == JsonValueKind.Object ? Nonempty(Text(epInfo, "duration")) : null,
                        StreamUrl = $"{account.BaseUrl}/series/{Seg(account.Username)}/{Seg(account.Password)}/{id}.{ext}",
                    });
                }
            }
        }
        string? backdrop = null;
        if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("backdrop_path", out var bp))
            backdrop = bp.ValueKind == JsonValueKind.Array ? bp.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrEmpty(x))
                : Nonempty(bp.ValueKind == JsonValueKind.String ? bp.GetString() : null);
        return new XtreamSeries
        {
            Name = name ?? $"Série {seriesId}",
            Plot = info.ValueKind == JsonValueKind.Object ? Nonempty(Text(info, "plot")) : null,
            Cover = info.ValueKind == JsonValueKind.Object ? Nonempty(Text(info, "cover")) : null,
            Backdrop = backdrop,
            Genre = info.ValueKind == JsonValueKind.Object ? Nonempty(Text(info, "genre")) : null,
            Released = info.ValueKind == JsonValueKind.Object ? Nonempty(Text(info, "releaseDate")) : null,
            Rating = info.ValueKind == JsonValueKind.Object ? Rating(Text(info, "rating")) : null,
            Episodes = episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList(),
        };
    }

    // -------------------- guide & catch-up --------------------

    /// <summary>Now and next few programmes of a live stream.</summary>
    public static async Task<IReadOnlyList<XtreamProgramme>> ShortEpgAsync(XtreamAccount account, string streamId)
    {
        try { return Programmes(await GetAsync(account, "get_short_epg", $"&stream_id={streamId}&limit=6")); }
        catch { return Array.Empty<XtreamProgramme>(); }
    }

    /// <summary>The past programmes that can be replayed, most recent first.</summary>
    public static async Task<IReadOnlyList<XtreamProgramme>> CatchupAsync(XtreamAccount account, string streamId)
    {
        try
        {
            var now = DateTime.UtcNow;
            return Programmes(await GetAsync(account, "get_simple_data_table", $"&stream_id={streamId}"))
                .Where(p => p.End <= now.AddMinutes(1) && p.Start >= now.AddDays(-14))
                .OrderByDescending(p => p.Start)
                .ToList();
        }
        catch
        {
            return Array.Empty<XtreamProgramme>();
        }
    }

    /// <summary>/timeshift/{user}/{pass}/{minutes}/{yyyy-MM-dd:HH-mm}/{id}.ts —
    /// the start in the server's time, which panels give as UTC.</summary>
    public static string TimeshiftUrl(XtreamAccount account, string streamId, XtreamProgramme programme)
    {
        var start = programme.Start.ToString("yyyy-MM-dd:HH-mm", CultureInfo.InvariantCulture);
        return $"{account.BaseUrl}/timeshift/{Seg(account.Username)}/{Seg(account.Password)}/{programme.DurationMinutes}/{start}/{streamId}.ts";
    }

    /// <summary>The provider's full XMLTV guide.</summary>
    public static string GuideUrl(XtreamAccount account) =>
        $"{account.BaseUrl}/xmltv.php?username={Uri.EscapeDataString(account.Username)}&password={Uri.EscapeDataString(account.Password)}";

    // -------------------- plumbing --------------------

    private static IReadOnlyList<XtreamProgramme> Programmes(JsonElement json)
    {
        if (!json.TryGetProperty("epg_listings", out var list) || list.ValueKind != JsonValueKind.Array) return Array.Empty<XtreamProgramme>();
        var result = new List<XtreamProgramme>();
        foreach (var e in list.EnumerateArray())
        {
            DateTime start, end;
            if (long.TryParse(Text(e, "start_timestamp"), out var st) && long.TryParse(Text(e, "stop_timestamp"), out var et))
            {
                start = DateTimeOffset.FromUnixTimeSeconds(st).UtcDateTime;
                end = DateTimeOffset.FromUnixTimeSeconds(et).UtcDateTime;
            }
            else if (DateTime.TryParseExact(Text(e, "start"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out start)
                     && DateTime.TryParseExact(Text(e, "end") ?? Text(e, "stop"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out end))
            {
            }
            else continue;
            result.Add(new XtreamProgramme
            {
                Start = start,
                End = end,
                Title = Base64(Text(e, "title")) ?? "",
                Description = Base64(Text(e, "description")),
                HasArchive = Text(e, "has_archive") == "1",
            });
        }
        return result;
    }

    /// <summary>Titles and descriptions are base64 on most panels, plain on some.</summary>
    private static string? Base64(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return decoded.Length > 0 ? decoded : s;
        }
        catch (FormatException)
        {
            return s;
        }
    }

    private static Dictionary<string, string> Categories(JsonElement json) =>
        Rows(json).Select(c => (Id: Text(c, "category_id"), Name: Nonempty(Text(c, "category_name"))))
            .Where(c => c.Id is not null && c.Name is not null)
            .GroupBy(c => c.Id!)
            .ToDictionary(g => g.Key, g => g.First().Name!);

    private static IEnumerable<JsonElement> Rows(JsonElement json) =>
        json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object) : Enumerable.Empty<JsonElement>();

    private static async Task<JsonElement> GetAsync(XtreamAccount account, string? action, string extra = "")
    {
        if (account.BaseUrl.Length == 0) throw new InvalidOperationException("Adresse du serveur Xtream invalide.");
        var url = $"{account.BaseUrl}/player_api.php?username={Uri.EscapeDataString(account.Username)}&password={Uri.EscapeDataString(account.Password)}"
                  + (action is null ? "" : $"&action={action}") + extra;
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Le serveur Xtream a répondu {(int)response.StatusCode}.");
        var text = (await response.Content.ReadAsStringAsync()).Trim('﻿', ' ', '\r', '\n', '\t');
        if (text.Length == 0) return default;
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("Réponse Xtream illisible."); }
        // An object where a list was expected: an error envelope; bad
        // credentials say so with user_info.auth = 0.
        if (action is not null && action.StartsWith("get_") && action != "get_series_info" && action != "get_short_epg"
            && action != "get_simple_data_table" && json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("user_info", out var user) && Text(user, "auth") == "0")
            throw new AuthException();
        return json;
    }

    /// <summary>A property as text, whether the panel sent a string or a number.</summary>
    private static string? Text(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    private static string? Nonempty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Rating(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0 ? r.ToString("0.0", CultureInfo.InvariantCulture) : null;

    /// <summary>Path segment escaping strict enough for passwords with / ? # : @ %.</summary>
    private static string Seg(string s) => Uri.EscapeDataString(s);
}
