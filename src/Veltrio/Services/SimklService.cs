using System.Text;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>SIMKL: PIN sign-in, scrobbling, and importing the account's
/// lists. SIMKL needs an app-specific client id (simkl.com/settings/developer),
/// baked in at build time from VELTRIO_SIMKL_CLIENT_ID; without it the
/// service stays unavailable.</summary>
public static class SimklService
{
    private const string Api = "https://api.simkl.com";
    private const string TokenSecret = "simkl.token";

    private static readonly string? ClientId = BuildSetting.Get("SimklClientId");

    private static string? _token = SecretStore.Get(TokenSecret);
    private static CancellationTokenSource? _flow;

    public static bool IsAvailable => ClientId is not null;
    public static bool IsConnected => _token is not null && IsAvailable;
    public static string? PendingCode { get; private set; }
    public static string? PendingUrl { get; private set; }
    public static bool IsBusy { get; private set; }
    public static bool IsImporting { get; private set; }
    public static string? Error { get; private set; }
    public static string? LastResult { get; private set; }

    /// <summary>State changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    private static void Notify() => Changed?.Invoke();

    // -------------------- PIN --------------------

    public static async Task StartPinFlowAsync()
    {
        if (ClientId is null) return;
        CancelPinFlow();
        var flow = _flow = new CancellationTokenSource();
        Error = null;
        IsBusy = true;
        Notify();
        try
        {
            var pin = await SendAsync(HttpMethod.Get, $"/oauth/pin?client_id={ClientId}", null, auth: false);
            var code = DebridHttp.Str(pin, "user_code")!;
            PendingCode = code;
            PendingUrl = DebridHttp.Str(pin, "verification_url") ?? "https://simkl.com/pin";
            Notify();
            var interval = DebridHttp.Long(pin, "interval") is > 0 and var i ? i : 5;
            var deadline = DateTime.UtcNow.AddSeconds(DebridHttp.Long(pin, "expires_in") is > 0 and var e ? e : 900);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), flow.Token);
                JsonElement answer;
                try { answer = await SendAsync(HttpMethod.Get, $"/oauth/pin/{code}?client_id={ClientId}", null, auth: false); }
                catch (DebridException) { continue; }
                if (DebridHttp.Str(answer, "access_token") is { } token)
                {
                    _token = token;
                    SecretStore.Set(TokenSecret, token);
                    return;
                }
            }
            Error = "Le code SIMKL a expiré. Recommencez.";
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

    public static void CancelPinFlow()
    {
        _flow?.Cancel();
        _flow = null;
    }

    public static void Disconnect()
    {
        CancelPinFlow();
        SecretStore.Set(TokenSecret, null);
        _token = Error = LastResult = null;
        Notify();
    }

    // -------------------- import --------------------

    /// <summary>Plan-to-watch and watching (saved) and completed (watched) lists.</summary>
    public static async Task ImportLibraryAsync()
    {
        if (!IsConnected) return;
        Error = null;
        IsImporting = true;
        Notify();
        try
        {
            var all = await SendAsync(HttpMethod.Get, "/sync/all-items/", null, auth: true);
            IEnumerable<JsonElement> Rows(string name) =>
                all.ValueKind == JsonValueKind.Object && all.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray() : Enumerable.Empty<JsonElement>();
            var items = Rows("movies").Select(r => Item(r, isFilm: true))
                .Concat(Rows("shows").Concat(Rows("anime")).Select(r => Item(r, isFilm: false)))
                .OfType<ImportedWatchItem>().ToList();
            LastResult = AccountText.ImportResult(items.Count, WatchStore.Merge(items));
        }
        catch (Exception ex)
        {
            Error = $"Impossible de lire les listes SIMKL. {ex.Message}";
        }
        finally
        {
            IsImporting = false;
            Notify();
        }
    }

    private static ImportedWatchItem? Item(JsonElement row, bool isFilm)
    {
        if (!(row.TryGetProperty(isFilm ? "movie" : "show", out var title) && title.ValueKind == JsonValueKind.Object)) return null;
        var imdb = title.TryGetProperty("ids", out var ids) ? DebridHttp.Str(ids, "imdb") : null;
        if (imdb is null || !imdb.StartsWith("tt")) return null;
        var status = DebridHttp.Str(row, "status");
        return new ImportedWatchItem(isFilm ? "movie" : "series", imdb, DebridHttp.Str(title, "title") ?? imdb)
        {
            InLibrary = status is "plantowatch" or "watching",
            Watched = isFilm && status == "completed",
            LastWatched = AccountText.ParseDate(DebridHttp.Str(row, "last_watched_at") ?? DebridHttp.Str(row, "added_to_watchlist_at")),
        };
    }

    // -------------------- scrobble --------------------

    /// <summary>start, pause or stop; SIMKL marks a title watched when
    /// "stop" comes with 80 % or more. Silent on every error.</summary>
    public static async Task ScrobbleAsync(string action, string imdbId, int? season, int? episode, double progress)
    {
        if (!IsConnected || !imdbId.StartsWith("tt")) return;
        object body = season is { } s && episode is { } e
            ? new { progress = Math.Clamp(progress, 0, 100), show = new { ids = new { imdb = imdbId.Split(':')[0] } }, episode = new { season = s, number = e } }
            : new { progress = Math.Clamp(progress, 0, 100), movie = new { ids = new { imdb = imdbId } } };
        try { await SendAsync(HttpMethod.Post, $"/scrobble/{action}", body, auth: true); }
        catch { /* best-effort */ }
    }

    // -------------------- wire --------------------

    private static Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, bool auth)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Add("simkl-api-key", ClientId);
        if (auth && _token is { } token) request.Headers.Authorization = new("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return DebridHttp.SendAsync(request, "SIMKL");
    }
}

/// <summary>Values baked into the build from environment variables (see
/// Veltrio.csproj); null when the variable was unset.</summary>
internal static class BuildSetting
{
    public static string? Get(string key) =>
        System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                System.Reflection.Assembly.GetExecutingAssembly())
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() is { Length: > 0 } value ? value : null;
}
