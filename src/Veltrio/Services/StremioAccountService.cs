using System.Text;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>A Stremio account: sign in with email and password, then import
/// its library (saved titles, watched films, film progress) and its addons.
/// Import is one-way: nothing is written back to Stremio.</summary>
public static class StremioAccountService
{
    private const string Api = "https://api.strem.io/api/";
    private const string KeySecret = "stremio.authkey";
    private const string EmailSecret = "stremio.email";

    private static string? _authKey = SecretStore.Get(KeySecret);
    private static bool _didLaunchSync;

    /// <summary>Silently syncs Stremio watch history once per app launch.</summary>
    public static async Task SyncOnLaunchIfNeededAsync()
    {
        if (_didLaunchSync || !IsConnected) return;
        _didLaunchSync = true;
        await ImportLibraryAsync();
    }

    public static string? Email { get; private set; } = SecretStore.Get(EmailSecret);
    public static bool IsConnected => _authKey is not null;
    public static bool IsBusy { get; private set; }
    public static bool IsInstallingAddons { get; private set; }
    public static string? Error { get; private set; }
    /// <summary>"12 titres lus, 3 mis à jour", under the card after an import.</summary>
    public static string? LastResult { get; private set; }
    public static string? AddonResult { get; private set; }

    /// <summary>State changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    private static void Notify() => Changed?.Invoke();

    public static async Task SignInAsync(string email, string password)
    {
        Error = null;
        IsBusy = true;
        Notify();
        try
        {
            var login = await CallAsync("login", new { type = "Login", email, password, facebook = false });
            _authKey = DebridHttp.Str(login, "authKey") ?? throw new DebridException("Stremio n'a pas renvoyé de session.");
            Email = login.TryGetProperty("user", out var user) ? DebridHttp.Str(user, "email") ?? email : email;
            SecretStore.Set(KeySecret, _authKey);
            SecretStore.Set(EmailSecret, Email);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public static void SignOut()
    {
        if (_authKey is { } key) _ = CallAsync("logout", new { type = "Logout", authKey = key }).ContinueWith(_ => { });
        SecretStore.Set(KeySecret, null);
        SecretStore.Set(EmailSecret, null);
        _authKey = Email = Error = LastResult = AddonResult = null;
        Notify();
    }

    /// <summary>Saved and watched titles, and film progress, into the library.</summary>
    public static async Task ImportLibraryAsync()
    {
        if (_authKey is not { } key) return;
        Error = null;
        IsBusy = true;
        Notify();
        try
        {
            var items = await CallAsync("datastoreGet", new { authKey = key, collection = "libraryItem", ids = Array.Empty<string>(), all = true });
            var imported = items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Select(Imported).OfType<ImportedWatchItem>().ToList()
                : new List<ImportedWatchItem>();
            var changed = WatchStore.Merge(imported);
            LastResult = AccountText.ImportResult(imported.Count, changed);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>Installs the account's addons that aren't installed yet.</summary>
    public static async Task InstallAccountAddonsAsync()
    {
        if (_authKey is not { } key) return;
        Error = null;
        IsInstallingAddons = true;
        Notify();
        try
        {
            var collection = await CallAsync("addonCollectionGet", new { type = "AddonCollectionGet", authKey = key, update = true });
            var urls = collection.TryGetProperty("addons", out var addons) && addons.ValueKind == JsonValueKind.Array
                ? addons.EnumerateArray().Select(a => DebridHttp.Str(a, "transportUrl")).OfType<string>()
                    .Where(u => !CoreService.IsInstalled(u)).ToList()
                : new List<string>();
            var installed = 0;
            foreach (var url in urls)
                if (await CoreService.InstallAddonAsync(url) is null) installed++;
            AddonResult = installed switch
            {
                0 when urls.Count == 0 => "Tous les addons du compte sont déjà installés",
                0 => "Aucun addon n'a pu être installé",
                1 => "1 addon installé",
                _ => $"{installed} addons installés",
            };
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsInstallingAddons = false;
            Notify();
        }
    }

    /// <summary>A Stremio library item as an import, or null for anything
    /// that isn't a film or series keyed by an IMDB id.</summary>
    internal static ImportedWatchItem? Imported(JsonElement item)
    {
        var id = DebridHttp.Str(item, "_id");
        var type = DebridHttp.Str(item, "type");
        if (id is null || !id.StartsWith("tt") || type is not ("movie" or "series")) return null;
        var isFilm = type == "movie";
        var state = item.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        double? Ms(string name) =>
            state.ValueKind == JsonValueKind.Object && state.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() / 1000 : null;
        return new ImportedWatchItem(type, id, DebridHttp.Str(item, "name") ?? id)
        {
            Poster = DebridHttp.Str(item, "poster"),
            // "temp" items are ones Stremio tracks only because they were opened.
            InLibrary = !Flag(item, "removed") && !Flag(item, "temp"),
            Watched = isFilm && (DebridHttp.Long(state, "flaggedWatched") > 0 || DebridHttp.Long(state, "timesWatched") > 0),
            Position = isFilm ? Ms("timeOffset") : null,
            Duration = isFilm ? Ms("duration") : null,
            LastWatched = AccountText.ParseDate(DebridHttp.Str(state, "lastWatched")),
        };
    }

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The API answers 200 with either "result" or "error".</summary>
    private static async Task<JsonElement> CallAsync(string method, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Api + method)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        var json = await DebridHttp.SendAsync(request, "Stremio");
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
            return result;
        var message = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var error)
            ? DebridHttp.Str(error, "message") : null;
        throw new DebridException(message switch
        {
            null => "Stremio a refusé la demande.",
            "Wrong passphrase" or "User not found" => "E-mail ou mot de passe incorrect.",
            _ => $"Stremio : {message}",
        });
    }
}

/// <summary>Shared bits of the account services.</summary>
internal static class AccountText
{
    public static string ImportResult(int read, int changed) =>
        $"{read} titre{(read > 1 ? "s" : "")} lu{(read > 1 ? "s" : "")}, {changed} mis à jour";

    public static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;
}
