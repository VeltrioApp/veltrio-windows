using System.Text;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>Trakt.tv: OAuth device code sign-in, scrobbling from the
/// player, and importing the watchlist and watched history. Scrobbling is
/// best-effort: Trakt failing never gets in the way of watching.
/// Needs a Trakt app (trakt.tv/oauth/applications), baked in at build time
/// from VELTRIO_TRAKT_CLIENT_ID and VELTRIO_TRAKT_CLIENT_SECRET; without it
/// the service stays unavailable. https://trakt.docs.apiary.io/</summary>
public static class TraktService
{
    private const string Api = "https://api.trakt.tv/";
    private static readonly string? ClientId = BuildSetting.Get("TraktClientId");
    private static readonly string? ClientSecret = BuildSetting.Get("TraktClientSecret");
    private const string TokenSecret = "trakt.token";

    private static string? _token = SecretStore.Get(TokenSecret);
    private static CancellationTokenSource? _flow;

    public static bool IsAvailable => ClientId is not null && ClientSecret is not null;
    public static bool IsConnected => _token is not null && IsAvailable;
    public static string? User { get; private set; }
    public static bool IsVip { get; private set; }
    public static string? PendingCode { get; private set; }
    public static string? PendingUrl { get; private set; }
    public static bool IsBusy { get; private set; }
    public static bool IsImporting { get; private set; }
    public static string? Error { get; private set; }
    public static string? LastResult { get; private set; }

    /// <summary>State changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    private static void Notify() => Changed?.Invoke();

    static TraktService()
    {
        if (IsConnected) _ = RefreshUserAsync();
    }

    private static async Task RefreshUserAsync()
    {
        try
        {
            var me = await GetAsync("users/me?extended=full");
            User = DebridHttp.Str(me, "name") is { Length: > 0 } name ? name : DebridHttp.Str(me, "username");
            IsVip = me.TryGetProperty("vip", out var vip) && vip.ValueKind == JsonValueKind.True;
        }
        catch
        {
            // Offline or revoked: the card still shows "connected".
        }
        Notify();
    }

    // -------------------- device code --------------------

    /// <summary>Shows a code, then polls until the user has entered it on trakt.tv.</summary>
    public static async Task StartDeviceAuthAsync()
    {
        if (!IsAvailable) return;
        CancelDeviceAuth();
        var flow = _flow = new CancellationTokenSource();
        Error = null;
        IsBusy = true;
        Notify();
        try
        {
            var code = await PostAsync("oauth/device/code", new { client_id = ClientId }, auth: false);
            var deviceCode = DebridHttp.Str(code, "device_code")!;
            PendingCode = DebridHttp.Str(code, "user_code");
            PendingUrl = DebridHttp.Str(code, "verification_url") ?? "https://trakt.tv/activate";
            Notify();
            var interval = Math.Max(5, DebridHttp.Long(code, "interval"));
            var deadline = DateTime.UtcNow.AddSeconds(DebridHttp.Long(code, "expires_in"));
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), flow.Token);
                JsonElement token;
                try
                {
                    token = await PostAsync("oauth/device/token",
                        new { code = deviceCode, client_id = ClientId, client_secret = ClientSecret }, auth: false);
                }
                catch (DebridException)
                {
                    // 400 while the code hasn't been entered yet: keep polling.
                    continue;
                }
                if (DebridHttp.Str(token, "access_token") is { } access)
                {
                    _token = access;
                    SecretStore.Set(TokenSecret, access);
                    _ = RefreshUserAsync();
                    return;
                }
            }
            Error = "Le code Trakt a expiré. Recommencez.";
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

    public static void CancelDeviceAuth()
    {
        _flow?.Cancel();
        _flow = null;
    }

    public static void Disconnect()
    {
        CancelDeviceAuth();
        SecretStore.Set(TokenSecret, null);
        _token = User = Error = LastResult = null;
        IsVip = false;
        Notify();
    }

    // -------------------- scrobble --------------------

    /// <summary>start, pause or stop; progress 0–100. Silent on every error.</summary>
    public static async Task ScrobbleAsync(string action, string imdbId, int? season, int? episode, double progress)
    {
        if (!IsConnected) return;
        object body = season is { } s && episode is { } e
            ? new { progress = Math.Clamp(progress, 0, 100), show = new { ids = new { imdb = imdbId.Split(':')[0] } }, episode = new { season = s, number = e } }
            : new { progress = Math.Clamp(progress, 0, 100), movie = new { ids = new { imdb = imdbId } } };
        try { await PostAsync($"scrobble/{action}", body, auth: true); }
        catch { /* best-effort */ }
    }

    // -------------------- import --------------------

    /// <summary>Watchlist (saved) and watched history, films and series.</summary>
    public static async Task ImportLibraryAsync()
    {
        if (!IsConnected) return;
        Error = null;
        IsImporting = true;
        Notify();
        try
        {
            async Task<List<JsonElement>> Rows(string path)
            {
                try
                {
                    var json = await GetAsync(path);
                    return json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToList() : new();
                }
                catch { return new(); }
            }
            var lists = await Task.WhenAll(
                Rows("sync/watchlist/movies"), Rows("sync/watchlist/shows"),
                Rows("sync/watched/movies"), Rows("sync/watched/shows"));
            var saved = lists[0].Concat(lists[1]).Select(r => Item(r, saved: true));
            var watched = lists[2].Concat(lists[3]).Select(r => Item(r, saved: false));
            var items = saved.Concat(watched).OfType<ImportedWatchItem>().ToList();
            LastResult = AccountText.ImportResult(items.Count, WatchStore.Merge(items));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsImporting = false;
            Notify();
        }
    }

    private static ImportedWatchItem? Item(JsonElement row, bool saved)
    {
        var isFilm = row.TryGetProperty("movie", out var title) && title.ValueKind == JsonValueKind.Object;
        if (!isFilm && !(row.TryGetProperty("show", out title) && title.ValueKind == JsonValueKind.Object)) return null;
        var imdb = title.TryGetProperty("ids", out var ids) ? DebridHttp.Str(ids, "imdb") : null;
        if (imdb is null || !imdb.StartsWith("tt")) return null;
        return new ImportedWatchItem(isFilm ? "movie" : "series", imdb, DebridHttp.Str(title, "title") ?? imdb)
        {
            InLibrary = saved,
            // A show in the watched list may be partly watched; only films get the flag.
            Watched = !saved && isFilm,
            LastWatched = AccountText.ParseDate(DebridHttp.Str(row, "last_watched_at") ?? DebridHttp.Str(row, "listed_at")),
        };
    }

    // -------------------- wire --------------------

    private static HttpRequestMessage Request(HttpMethod method, string path, bool auth)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Add("trakt-api-version", "2");
        request.Headers.Add("trakt-api-key", ClientId);
        if (auth && _token is { } token) request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    private static Task<JsonElement> GetAsync(string path) =>
        DebridHttp.SendAsync(Request(HttpMethod.Get, path, auth: true), "Trakt");

    private static Task<JsonElement> PostAsync(string path, object body, bool auth)
    {
        var request = Request(HttpMethod.Post, path, auth);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return DebridHttp.SendAsync(request, "Trakt");
    }
}
