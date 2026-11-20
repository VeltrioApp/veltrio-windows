using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Veltrio.Services;

public enum MediaServerKind { Jellyfin, Emby, Plex }

public enum MediaSectionKind { Movies, Shows, Mixed }

/// <summary>A library on the server: "Films", "Séries TV", "Kids"…</summary>
public sealed record MediaLibrarySection(string Id, string Name, MediaSectionKind Kind);

/// <summary>A film or series in a library. Shaped for the poster template.</summary>
public sealed class MediaLibraryItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    /// <summary>"movie" or "series", as the rest of the app spells them.</summary>
    public required string Type { get; init; }
    /// <summary>Set when the server identified the title: it then opens on
    /// its title page, where this copy is listed among the streams.</summary>
    public string? ImdbId { get; init; }
    public string? PosterUrl { get; init; }
    /// <summary>Plex plays a media part, known from the listing; Jellyfin
    /// and Emby build the URL from the id.</summary>
    public string? StreamUrl { get; init; }
    public string Subtitle => Year?.ToString() ?? (Type == "series" ? "SÉRIE" : "FILM");
    /// <summary>What screen readers announce for the grid item.</summary>
    public override string ToString() => Title;
}

/// <summary>An episode of a series the server couldn't tie to an IMDB id.</summary>
public sealed class MediaLibraryEpisode
{
    public required string Id { get; init; }
    public int Season { get; init; }
    public int Number { get; init; }
    public required string Title { get; init; }
    public string? Image { get; init; }
    public string? StreamUrl { get; init; }
    public string Code => $"S{Season} · E{Number}";
    public double Progress { get; set; }
    public override string ToString() => $"{Code} {Title}";
}

public sealed record MediaLibraryPage(IReadOnlyList<MediaLibraryItem> Items, int Total);

/// <summary>A server's copy of an IMDB title, as the stream list shows it.</summary>
public sealed record MediaServerMatch(MediaServer Server, string ItemId, string Name, string? Video,
    IReadOnlyList<string> AudioLanguages, string StreamUrl);

/// <summary>One media server account: Jellyfin and Emby (address, user,
/// password), Plex (PIN on plex.tv). The token lives in secrets.dat, the
/// rest in media-servers.json.</summary>
public abstract class MediaServer
{
    protected static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public abstract MediaServerKind Kind { get; }
    public abstract string Name { get; }
    public abstract string Color { get; }

    public bool IsConnected => Token is not null && IsSignedIn;
    protected virtual bool IsSignedIn => true;
    public string? User { get; protected set; }
    /// <summary>Server host, shown under the name.</summary>
    public virtual string? Host => null;
    public bool IsBusy { get; protected set; }
    public string? Error { get; protected set; }
    public string? PendingCode { get; protected set; }
    public string? PendingUrl { get; protected set; }

    protected string? Token { get; set; }

    protected void Init() => Token = SecretStore.Get(SecretName);

    private string SecretName => $"mediaserver.{Kind}".ToLowerInvariant();

    protected void SaveToken(string? token)
    {
        Token = token;
        SecretStore.Set(SecretName, token);
    }

    protected void Notify() => MediaServers.RaiseChanged();

    public abstract void Disconnect();

    // Library browsing (Collection) and IMDB matching (title pages).
    public abstract Task<IReadOnlyList<MediaLibrarySection>> SectionsAsync();
    public abstract Task<MediaLibraryPage> ItemsAsync(MediaLibrarySection section, int start, int limit);
    public abstract Task<IReadOnlyList<MediaLibraryEpisode>> EpisodesAsync(MediaLibraryItem series);
    public abstract string? StreamUrl(string itemId);
    public abstract Task<IReadOnlyList<MediaServerMatch>> FindMovieAsync(string imdbId, string title, int? year);
    public abstract Task<IReadOnlyList<MediaServerMatch>> FindEpisodeAsync(string seriesImdbId, string title, int? year, int season, int episode);

    /// <summary>Title equality for items without an IMDB id: case and
    /// accents ignored, years within one of each other.</summary>
    public static bool SameTitle(string theirs, string ours, int? theirYear, int? year)
    {
        static string Fold(string s)
        {
            var d = s.Normalize(NormalizationForm.FormD);
            return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
                .ToLowerInvariant().Trim();
        }
        if (Fold(theirs) != Fold(ours)) return false;
        return year is not { } y || theirYear is not { } t || Math.Abs(t - y) <= 1;
    }

    protected static string? Str(JsonElement e, string name) => DebridHttp.Str(e, name);

    protected static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    protected static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    /// <summary>GET returning JSON, or null on any failure: browsing a
    /// server that went away shows an empty library, not an error.</summary>
    protected static async Task<JsonElement?> TryGetAsync(HttpRequestMessage request)
    {
        try
        {
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    protected static string NormalizeHost(string raw)
    {
        var h = raw.Trim().TrimEnd('/');
        if (h.Length > 0 && !h.Contains("://")) h = "http://" + h;
        return h;
    }
}

/// <summary>The three servers, and what's saved about them.</summary>
public static class MediaServers
{
    internal const string File = "media-servers.json";

    /// <summary>Per server: address, user and the install's device id.</summary>
    internal sealed class Saved
    {
        public string? Url { get; set; }
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? DeviceId { get; set; }
    }

    internal static readonly Dictionary<string, Saved> State = Storage.Load(File, new Dictionary<string, Saved>());

    internal static Saved For(MediaServerKind kind)
    {
        var key = kind.ToString().ToLowerInvariant();
        if (!State.TryGetValue(key, out var saved)) State[key] = saved = new Saved();
        // Stable per install: the server lists each device id once and
        // ties its tokens to it.
        if (saved.DeviceId is null)
        {
            saved.DeviceId = $"veltrio-{Guid.NewGuid().ToString("N")[..12]}";
            Persist();
        }
        return saved;
    }

    internal static void Persist() => Storage.Save(File, State);

    public static readonly MediaBrowserServer Jellyfin = new(MediaServerKind.Jellyfin);
    public static readonly MediaBrowserServer Emby = new(MediaServerKind.Emby);
    public static readonly PlexServer Plex = new();

    public static IReadOnlyList<MediaServer> All { get; } = new MediaServer[] { Jellyfin, Emby, Plex };
    public static IEnumerable<MediaServer> Connected => All.Where(s => s.IsConnected);

    /// <summary>A server's state changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    internal static void RaiseChanged() => Changed?.Invoke();
}

/// <summary>Jellyfin and Emby: same ancestor, same API; they differ in the
/// auth headers and the stream URL's token parameter.</summary>
public sealed class MediaBrowserServer : MediaServer
{
    private readonly MediaServerKind _kind;
    private readonly MediaServers.Saved _saved;

    internal MediaBrowserServer(MediaServerKind kind)
    {
        _kind = kind;
        _saved = MediaServers.For(kind);
        Init();
        User = _saved.UserName;
    }

    public override MediaServerKind Kind => _kind;
    public override string Name => _kind == MediaServerKind.Jellyfin ? "Jellyfin" : "Emby";
    public override string Color => _kind == MediaServerKind.Jellyfin ? "#00A4DC" : "#52B54B";
    protected override bool IsSignedIn => _saved.UserId is not null;
    public override string? Host => Uri.TryCreate(_saved.Url, UriKind.Absolute, out var u) ? u.Host : null;
    public string? Url => _saved.Url;

    private string BaseUrl => _saved.Url ?? "";

    private void Authorize(HttpRequestMessage request, string? token)
    {
        var value = $"MediaBrowser Client=\"Veltrio\", Device=\"Windows\", DeviceId=\"{_saved.DeviceId}\", Version=\"0.1.0\"";
        if (token is not null) value += $", Token=\"{token}\"";
        // Recent Jellyfin reads Authorization; older servers and Emby the legacy header.
        request.Headers.TryAddWithoutValidation("X-Emby-Authorization", value);
        if (_kind == MediaServerKind.Jellyfin) request.Headers.TryAddWithoutValidation("Authorization", value);
        else if (token is not null) request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
    }

    private HttpRequestMessage Get(string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
        Authorize(request, Token);
        return request;
    }

    public async Task ConnectAsync(string host, string user, string password)
    {
        Error = null;
        IsBusy = true;
        Notify();
        try
        {
            Error = await SignInAsync(NormalizeHost(host), user.Trim(), password);
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>The error to show, or null once signed in.</summary>
    private async Task<string?> SignInAsync(string url, string user, string password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return "Adresse de serveur invalide.";
        // Check a server of this kind answers before sending credentials, so
        // a wrong address or port isn't reported as a wrong password.
        try
        {
            using var probe = await Http.GetAsync(url + "/System/Info/Public");
            var json = probe.IsSuccessStatusCode ? JsonDocument.Parse(await probe.Content.ReadAsStringAsync()).RootElement : default;
            if (Str(json, "Version") is null)
                return _kind == MediaServerKind.Jellyfin
                    ? $"Aucun serveur Jellyfin ne répond à {url}. Vérifiez l'adresse et le port (8096 par défaut), et ajoutez le chemin de base si votre serveur en a un, par exemple /jellyfin."
                    : $"Aucun serveur Emby ne répond à {url}. Vérifiez l'adresse et le port (8096 par défaut).";
            var product = Str(json, "ProductName") ?? "";
            if (_kind == MediaServerKind.Jellyfin && product.Contains("emby", StringComparison.OrdinalIgnoreCase))
                return "C'est un serveur Emby : connectez-le avec la carte Emby.";
            if (_kind == MediaServerKind.Emby && product.Contains("jellyfin", StringComparison.OrdinalIgnoreCase))
                return "C'est un serveur Jellyfin : connectez-le avec la carte Jellyfin.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"Impossible de joindre {url} : {ex.Message}";
        }

        var request = new HttpRequestMessage(HttpMethod.Post, url + "/Users/AuthenticateByName")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { Username = user, Pw = password }), Encoding.UTF8, "application/json"),
        };
        Authorize(request, null);
        try
        {
            using var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode switch
                {
                    401 => "Nom d'utilisateur ou mot de passe incorrect.",
                    403 => "Ce compte n'a pas le droit de se connecter (désactivé, ou accès distant coupé pour lui).",
                    var code => $"{Name} a refusé la connexion (HTTP {code}){(body.Length > 0 ? " : " + body[..Math.Min(200, body.Length)].Trim() : "")}",
                };
            }
            var json = JsonDocument.Parse(body).RootElement;
            var userJson = json.TryGetProperty("User", out var u) ? u : default;
            var token = Str(json, "AccessToken");
            var userId = Str(userJson, "Id");
            if (token is null || userId is null) return "Réponse inattendue du serveur.";
            _saved.Url = url;
            _saved.UserId = userId;
            _saved.UserName = User = Str(userJson, "Name") ?? user;
            MediaServers.Persist();
            SaveToken(token);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ex.Message;
        }
    }

    public override void Disconnect()
    {
        SaveToken(null);
        _saved.UserId = _saved.UserName = User = null;
        MediaServers.Persist();
        Error = null;
        Notify();
    }

    // -------------------- browsing --------------------

    public override async Task<IReadOnlyList<MediaLibrarySection>> SectionsAsync()
    {
        if (!IsConnected || await TryGetAsync(Get($"/Users/{_saved.UserId}/Views")) is not { } json) return System.Array.Empty<MediaLibrarySection>();
        // Music, photos and live TV are left out: the app plays films and series.
        return Array(json, "Items").Select(v => Str(v, "CollectionType") switch
        {
            "movies" => new MediaLibrarySection(Str(v, "Id")!, Str(v, "Name") ?? "", MediaSectionKind.Movies),
            "tvshows" => new MediaLibrarySection(Str(v, "Id")!, Str(v, "Name") ?? "", MediaSectionKind.Shows),
            null or "mixed" or "homevideos" => new MediaLibrarySection(Str(v, "Id")!, Str(v, "Name") ?? "", MediaSectionKind.Mixed),
            _ => null,
        }).OfType<MediaLibrarySection>().ToList();
    }

    public override async Task<MediaLibraryPage> ItemsAsync(MediaLibrarySection section, int start, int limit)
    {
        var query = $"/Users/{_saved.UserId}/Items?ParentId={Uri.EscapeDataString(section.Id)}&IncludeItemTypes=Movie,Series&Recursive=true"
            + $"&SortBy=SortName&Fields=ProviderIds,ProductionYear&StartIndex={start}&Limit={limit}";
        if (!IsConnected || await TryGetAsync(Get(query)) is not { } json) return new MediaLibraryPage(System.Array.Empty<MediaLibraryItem>(), 0);
        var items = Array(json, "Items").Select(i =>
        {
            var id = Str(i, "Id")!;
            return new MediaLibraryItem
            {
                Id = id,
                Title = Str(i, "Name") ?? "",
                Year = Int(i, "ProductionYear"),
                Type = Str(i, "Type") == "Series" ? "series" : "movie",
                ImdbId = Imdb(i),
                PosterUrl = ImageUrl(i, 360),
            };
        }).ToList();
        return new MediaLibraryPage(items, Int(json, "TotalRecordCount") ?? items.Count);
    }

    public override async Task<IReadOnlyList<MediaLibraryEpisode>> EpisodesAsync(MediaLibraryItem series)
    {
        if (!IsConnected || await TryGetAsync(Get($"/Shows/{series.Id}/Episodes?UserId={_saved.UserId}")) is not { } json)
            return System.Array.Empty<MediaLibraryEpisode>();
        return Array(json, "Items").Select(e =>
        {
            var number = Int(e, "IndexNumber") ?? 0;
            return new MediaLibraryEpisode
            {
                Id = Str(e, "Id")!,
                Season = Int(e, "ParentIndexNumber") ?? 1,
                Number = number,
                Title = Str(e, "Name") ?? $"Épisode {number}",
                Image = ImageUrl(e, 480),
            };
        }).ToList();
    }

    private string? ImageUrl(JsonElement item, int width) =>
        item.TryGetProperty("ImageTags", out var tags) && Str(tags, "Primary") is { } tag
            ? $"{BaseUrl}/Items/{Str(item, "Id")}/Images/Primary?maxWidth={width}&tag={tag}&quality=90"
            : null;

    private static string? Imdb(JsonElement item) =>
        item.TryGetProperty("ProviderIds", out var ids) && ids.ValueKind == JsonValueKind.Object
            ? ids.EnumerateObject().FirstOrDefault(p => p.Name.Equals("imdb", StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null
            : null;

    /// <summary>Direct play: the server sends the file as is ("Static").
    /// Jellyfin before 10.10 still wants api_key, newer ones ApiKey.</summary>
    public override string? StreamUrl(string itemId) => Token is not { } token ? null
        : _kind == MediaServerKind.Jellyfin
            ? $"{BaseUrl}/Videos/{itemId}/stream?ApiKey={token}&api_key={token}&Static=true"
            : $"{BaseUrl}/Videos/{itemId}/stream?X-Emby-Token={token}&Static=true";

    // -------------------- matching an IMDB title --------------------

    /// <summary>The library's copies of a film: matched on the IMDB id the
    /// server stores, title and year for items it couldn't identify.</summary>
    public override async Task<IReadOnlyList<MediaServerMatch>> FindMovieAsync(string imdbId, string title, int? year) =>
        (await FindItemsAsync("Movie", imdbId, title, year)).Select(Match).ToList();

    public override async Task<IReadOnlyList<MediaServerMatch>> FindEpisodeAsync(string seriesImdbId, string title, int? year, int season, int episode)
    {
        var found = new List<MediaServerMatch>();
        foreach (var series in await FindItemsAsync("Series", seriesImdbId, title, year))
        {
            if (await TryGetAsync(Get($"/Shows/{Str(series, "Id")}/Episodes?UserId={_saved.UserId}&Season={season}&Fields=MediaStreams")) is not { } json) continue;
            found.AddRange(Array(json, "Items")
                .Where(e => Int(e, "ParentIndexNumber") == season && Int(e, "IndexNumber") == episode)
                .Select(Match));
        }
        return found;
    }

    private async Task<List<JsonElement>> FindItemsAsync(string type, string imdbId, string title, int? year)
    {
        if (!IsConnected) return new();
        var query = $"/Users/{_saved.UserId}/Items?SearchTerm={Uri.EscapeDataString(title)}&IncludeItemTypes={type}&Recursive=true"
            + "&Fields=ProviderIds,ProductionYear,MediaStreams&Limit=20";
        if (await TryGetAsync(Get(query)) is not { } json) return new();
        return Array(json, "Items").Where(r => Imdb(r) is { } imdb
            ? imdb.Equals(imdbId, StringComparison.OrdinalIgnoreCase)
            : SameTitle(Str(r, "Name") ?? "", title, Int(r, "ProductionYear"), year)).ToList();
    }

    private MediaServerMatch Match(JsonElement r)
    {
        var streams = Array(r, "MediaStreams").ToList();
        var languages = streams.Where(s => Str(s, "Type") == "Audio").Select(s => Str(s, "Language"))
            .OfType<string>().Where(l => l.Length > 0).Distinct().ToList();
        var id = Str(r, "Id")!;
        return new MediaServerMatch(this, id, Str(r, "Name") ?? "",
            streams.Where(s => Str(s, "Type") == "Video").Select(s => Str(s, "DisplayTitle")).FirstOrDefault(),
            languages, StreamUrl(id)!);
    }
}

/// <summary>Plex: PIN sign-in on plex.tv/link, then the user's Plex Media
/// Server, found through plex.tv's resources; playback uses the media
/// part's own URL.</summary>
public sealed class PlexServer : MediaServer
{
    private readonly MediaServers.Saved _saved;
    private CancellationTokenSource? _flow;
    private (string Base, string Token)? _access;

    internal PlexServer()
    {
        _saved = MediaServers.For(MediaServerKind.Plex);
        Init();
        User = _saved.UserName;
    }

    public override MediaServerKind Kind => MediaServerKind.Plex;
    public override string Name => "Plex";
    public override string Color => "#E5A00D";
    public override string? Host => _access is { } a && Uri.TryCreate(a.Base, UriKind.Absolute, out var u) ? u.Host : null;

    private HttpRequestMessage Request(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Plex-Client-Identifier", _saved.DeviceId);
        request.Headers.TryAddWithoutValidation("X-Plex-Product", "Veltrio");
        request.Headers.TryAddWithoutValidation("X-Plex-Device", "Windows");
        request.Headers.TryAddWithoutValidation("X-Plex-Platform", "Windows");
        request.Headers.TryAddWithoutValidation("X-Plex-Version", "0.1.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (token is not null) request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        return request;
    }

    // -------------------- PIN --------------------

    public async Task StartPinFlowAsync()
    {
        CancelPinFlow();
        var flow = _flow = new CancellationTokenSource();
        Error = null;
        IsBusy = true;
        Notify();
        try
        {
            var create = Request(HttpMethod.Post, "https://plex.tv/api/v2/pins", null);
            // A short PIN, typed at plex.tv/link like on a TV.
            create.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["strong"] = "false" });
            var pin = await DebridHttp.SendAsync(create, "Plex");
            var id = DebridHttp.Long(pin, "id");
            var code = Str(pin, "code")!;
            PendingCode = code;
            PendingUrl = "https://plex.tv/link";
            Notify();
            var deadline = DateTime.UtcNow.AddSeconds(DebridHttp.Long(pin, "expiresIn") is > 0 and var s ? s : 900);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), flow.Token);
                JsonElement check;
                try { check = await DebridHttp.SendAsync(Request(HttpMethod.Get, $"https://plex.tv/api/v2/pins/{id}", null), "Plex"); }
                catch (DebridException) { continue; }
                if (Str(check, "authToken") is { Length: > 0 } token)
                {
                    SaveToken(token);
                    await RefreshUserAsync();
                    return;
                }
            }
            Error = "Le code Plex a expiré. Recommencez.";
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user.
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            PendingCode = PendingUrl = null;
            IsBusy = false;
            Notify();
        }
    }

    public void CancelPinFlow()
    {
        _flow?.Cancel();
        _flow = null;
    }

    private async Task RefreshUserAsync()
    {
        if (await TryGetAsync(Request(HttpMethod.Get, "https://plex.tv/api/v2/user", Token)) is { } me)
        {
            _saved.UserName = User = Str(me, "username") ?? Str(me, "title");
            MediaServers.Persist();
        }
    }

    public override void Disconnect()
    {
        CancelPinFlow();
        SaveToken(null);
        _saved.UserName = User = null;
        _access = null;
        MediaServers.Persist();
        Error = null;
        Notify();
    }

    // -------------------- server --------------------

    /// <summary>The server's address and the token it accepts (a server
    /// shared by someone else only takes its own). Connections are tried
    /// local first, and the first that answers is kept for the session.</summary>
    private async Task<(string Base, string Token)?> AccessAsync()
    {
        if (_access is not null) return _access;
        if (Token is not { } token) return null;
        if (await TryGetAsync(Request(HttpMethod.Get, "https://clients.plex.tv/api/v2/resources?includeHttps=1&includeRelay=1", token)) is not { } json
            || json.ValueKind != JsonValueKind.Array) return null;
        foreach (var server in json.EnumerateArray().Where(r => (Str(r, "provides") ?? "").Contains("server")))
        {
            var serverToken = Str(server, "accessToken") ?? token;
            var connections = Array(server, "connections")
                .OrderByDescending(c => c.TryGetProperty("local", out var l) && l.ValueKind == JsonValueKind.True)
                .ThenBy(c => c.TryGetProperty("relay", out var r) && r.ValueKind == JsonValueKind.True)
                .Select(c => Str(c, "uri")).OfType<string>();
            foreach (var uri in connections)
            {
                var probe = Request(HttpMethod.Get, uri + "/identity", serverToken);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try
                {
                    using var response = await Http.SendAsync(probe, cts.Token);
                    if (!response.IsSuccessStatusCode) continue;
                }
                catch
                {
                    continue;
                }
                _access = (uri.TrimEnd('/'), serverToken);
                Notify();
                return _access;
            }
        }
        return null;
    }

    private async Task<List<JsonElement>> MetadataAsync(string path)
    {
        if (await AccessAsync() is not { } a) return new();
        var url = a.Base + path;
        if (await TryGetAsync(Request(HttpMethod.Get, url, a.Token)) is not { } json) return new();
        var container = json.TryGetProperty("MediaContainer", out var c) ? c : default;
        return Array(container, "Metadata").ToList();
    }

    private static string WithToken(string url, string token) => $"{url}{(url.Contains('?') ? '&' : '?')}X-Plex-Token={token}";

    private string? ImageUrl(JsonElement m) =>
        _access is { } a && Str(m, "thumb") is { } thumb ? WithToken(a.Base + thumb, a.Token) : null;

    private string? PartUrl(JsonElement m) =>
        _access is { } a && Array(m, "Media").FirstOrDefault() is { ValueKind: JsonValueKind.Object } media
        && Array(media, "Part").FirstOrDefault() is { ValueKind: JsonValueKind.Object } part && Str(part, "key") is { } key
            ? WithToken(a.Base + key, a.Token) : null;

    private static string? Imdb(JsonElement m) =>
        Array(m, "Guid").Select(g => Str(g, "id")).FirstOrDefault(id => id?.StartsWith("imdb://") == true)?["imdb://".Length..];

    private async Task<List<JsonElement>> PlexSectionsAsync()
    {
        if (await AccessAsync() is not { } a) return new();
        if (await TryGetAsync(Request(HttpMethod.Get, a.Base + "/library/sections", a.Token)) is not { } json) return new();
        var container = json.TryGetProperty("MediaContainer", out var c) ? c : default;
        return Array(container, "Directory").ToList();
    }

    // -------------------- browsing --------------------

    public override async Task<IReadOnlyList<MediaLibrarySection>> SectionsAsync() =>
        (await PlexSectionsAsync()).Select(s => Str(s, "type") switch
        {
            "movie" => new MediaLibrarySection(Str(s, "key")!, Str(s, "title") ?? "", MediaSectionKind.Movies),
            "show" => new MediaLibrarySection(Str(s, "key")!, Str(s, "title") ?? "", MediaSectionKind.Shows),
            _ => null,
        }).OfType<MediaLibrarySection>().ToList();

    public override async Task<MediaLibraryPage> ItemsAsync(MediaLibrarySection section, int start, int limit)
    {
        var empty = new MediaLibraryPage(System.Array.Empty<MediaLibraryItem>(), 0);
        if (await AccessAsync() is not { } a) return empty;
        var url = $"{a.Base}/library/sections/{section.Id}/all?type={(section.Kind == MediaSectionKind.Shows ? 2 : 1)}"
            + $"&includeGuids=1&sort=titleSort&X-Plex-Container-Start={start}&X-Plex-Container-Size={limit}";
        if (await TryGetAsync(Request(HttpMethod.Get, url, a.Token)) is not { } json) return empty;
        var container = json.TryGetProperty("MediaContainer", out var c) ? c : default;
        var rows = Array(container, "Metadata").ToList();
        var items = rows.Select(m => new MediaLibraryItem
        {
            Id = Str(m, "ratingKey")!,
            Title = Str(m, "title") ?? "",
            Year = Int(m, "year"),
            Type = Str(m, "type") == "show" ? "series" : "movie",
            ImdbId = Imdb(m),
            PosterUrl = ImageUrl(m),
            StreamUrl = PartUrl(m),
        }).ToList();
        return new MediaLibraryPage(items, Int(container, "totalSize") ?? start + rows.Count);
    }

    public override async Task<IReadOnlyList<MediaLibraryEpisode>> EpisodesAsync(MediaLibraryItem series) =>
        (await MetadataAsync($"/library/metadata/{series.Id}/allLeaves")).Select(e => new MediaLibraryEpisode
        {
            Id = Str(e, "ratingKey")!,
            Season = Int(e, "parentIndex") ?? 1,
            Number = Int(e, "index") ?? 0,
            Title = Str(e, "title") ?? "",
            Image = ImageUrl(e),
            StreamUrl = PartUrl(e),
        }).ToList();

    /// <summary>Plex URLs come from the listing, not from an id.</summary>
    public override string? StreamUrl(string itemId) => null;

    // -------------------- matching an IMDB title --------------------

    public override async Task<IReadOnlyList<MediaServerMatch>> FindMovieAsync(string imdbId, string title, int? year)
    {
        var found = new List<MediaServerMatch>();
        foreach (var section in (await PlexSectionsAsync()).Where(s => Str(s, "type") == "movie"))
            foreach (var item in await MetadataAsync($"/library/sections/{Str(section, "key")}/all?type=1&includeGuids=1&title={Uri.EscapeDataString(title)}"))
                if (IsSame(item, imdbId, title, year) && await MatchAsync(item) is { } m) found.Add(m);
        return found;
    }

    public override async Task<IReadOnlyList<MediaServerMatch>> FindEpisodeAsync(string seriesImdbId, string title, int? year, int season, int episode)
    {
        var found = new List<MediaServerMatch>();
        foreach (var section in (await PlexSectionsAsync()).Where(s => Str(s, "type") == "show"))
            foreach (var show in await MetadataAsync($"/library/sections/{Str(section, "key")}/all?type=2&includeGuids=1&title={Uri.EscapeDataString(title)}"))
            {
                if (!IsSame(show, seriesImdbId, title, year)) continue;
                foreach (var ep in await MetadataAsync($"/library/metadata/{Str(show, "ratingKey")}/allLeaves"))
                    if (Int(ep, "parentIndex") == season && Int(ep, "index") == episode && await MatchAsync(ep) is { } m) found.Add(m);
            }
        return found;
    }

    private static bool IsSame(JsonElement item, string imdbId, string title, int? year) =>
        Imdb(item) is { } imdb
            ? imdb.Equals(imdbId, StringComparison.OrdinalIgnoreCase)
            : SameTitle(Str(item, "title") ?? "", title, Int(item, "year"), year);

    /// <summary>The listing omits audio streams, so the item's own metadata
    /// is fetched for its languages.</summary>
    private async Task<MediaServerMatch?> MatchAsync(JsonElement item)
    {
        var key = Str(item, "ratingKey")!;
        var full = (await MetadataAsync($"/library/metadata/{key}")).FirstOrDefault();
        if (full.ValueKind != JsonValueKind.Object) full = item;
        if (PartUrl(full) is not { } url) return null;
        var media = Array(full, "Media").First();
        var part = Array(media, "Part").First();
        var languages = Array(part, "Stream").Where(s => Int(s, "streamType") == 2)
            .Select(s => Str(s, "languageCode")).OfType<string>().Distinct().ToList();
        var resolution = Str(media, "videoResolution");
        var video = string.Join(" ", new[]
        {
            resolution is null ? null
                : resolution.Equals("4k", StringComparison.OrdinalIgnoreCase) ? "4K"
                : resolution.All(char.IsDigit) ? $"{resolution}p" : resolution.ToUpperInvariant(),
            Str(media, "videoCodec")?.ToUpperInvariant(),
        }.OfType<string>());
        return new MediaServerMatch(this, key, Str(item, "title") ?? "", video.Length > 0 ? video : null, languages, url);
    }
}
