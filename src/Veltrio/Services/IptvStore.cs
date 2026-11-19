using uniffi.veltrio;

namespace Veltrio.Services;

/// <summary>IPTV sources: M3U playlists through the core's parser, and
/// Xtream Codes accounts (live, films, series) through
/// <see cref="XtreamClient"/>. Xtream passwords are kept encrypted.</summary>
public static class IptvStore
{
    private const string File = "iptv-playlists.json";
    private const string SelectedFile = "iptv-selected.json";

    private static readonly IptvClient Client = new();
    private static readonly List<PlaylistRef> _playlists = LoadPlaylists();
    private static readonly Dictionary<(Guid, ChannelKind), Task<IReadOnlyList<ChannelItem>>> _cache = new();

    public static event Action? Changed;

    public static IReadOnlyList<PlaylistRef> Playlists => _playlists;

    private static List<PlaylistRef> LoadPlaylists()
    {
        var list = Storage.Load(File, new List<PlaylistRef>());
        foreach (var p in list)
            if (p.Xtream is { } x) x.Password = SecretStore.Get(SecretName(p.Id)) ?? "";
        return list;
    }

    private static string SecretName(Guid id) => $"xtream.{id}";

    public static PlaylistRef? Selected
    {
        get
        {
            var id = Storage.Load<Guid?>(SelectedFile, null);
            return _playlists.FirstOrDefault(p => p.Id == id) ?? _playlists.FirstOrDefault();
        }
        set
        {
            Storage.Save<Guid?>(SelectedFile, value?.Id);
            Changed?.Invoke();
        }
    }

    // -------------------- adding / removing --------------------

    public static string? AddM3u(string name, string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "L'URL de la playlist doit commencer par http:// ou https://.";
        Add(new PlaylistRef { Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(), Url = url });
        return null;
    }

    /// <summary>A .m3u / .m3u8 file on disk: read again each time it loads,
    /// so edits to it show up.</summary>
    public static string? AddLocalFile(string name, string path)
    {
        path = path.Trim().Trim('"');
        if (!System.IO.File.Exists(path)) return "Fichier introuvable.";
        Add(new PlaylistRef { Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim(), Url = path });
        return null;
    }

    /// <summary>Checks the account against the server before keeping it.
    /// Returns an error message, or null.</summary>
    public static async Task<string?> AddXtreamAsync(string name, string host, string username, string password, string output)
    {
        var account = new XtreamAccount { Host = host.Trim(), Username = username.Trim(), Password = password, Output = output };
        if (account.BaseUrl.Length == 0 || account.Username.Length == 0 || password.Length == 0)
            return "Renseignez le serveur, l'utilisateur et le mot de passe.";
        try
        {
            await XtreamClient.VerifyAsync(account);
        }
        catch (Exception ex)
        {
            return ex is HttpRequestException or TaskCanceledException ? $"Serveur injoignable : {ex.Message}" : ex.Message;
        }
        var playlist = new PlaylistRef
        {
            Name = string.IsNullOrWhiteSpace(name) ? new Uri(account.BaseUrl).Host : name.Trim(),
            Xtream = account,
        };
        SecretStore.Set(SecretName(playlist.Id), password);
        Add(playlist);
        return null;
    }

    private static void Add(PlaylistRef playlist)
    {
        _playlists.Add(playlist);
        Storage.Save(File, _playlists);
        Selected = playlist;
    }

    public static void Remove(PlaylistRef playlist)
    {
        _playlists.RemoveAll(p => p.Id == playlist.Id);
        foreach (var key in _cache.Keys.Where(k => k.Item1 == playlist.Id).ToList()) _cache.Remove(key);
        if (playlist.IsXtream) SecretStore.Set(SecretName(playlist.Id), null);
        Storage.Save(File, _playlists);
        Changed?.Invoke();
    }

    // -------------------- content --------------------

    /// <summary>Live channels (M3U or Xtream), or an Xtream account's films
    /// or series. Loaded once per session unless <paramref name="refresh"/>.</summary>
    public static Task<IReadOnlyList<ChannelItem>> ItemsAsync(PlaylistRef playlist, ChannelKind kind = ChannelKind.Live, bool refresh = false)
    {
        if (!playlist.IsXtream && kind != ChannelKind.Live) return Task.FromResult<IReadOnlyList<ChannelItem>>(Array.Empty<ChannelItem>());
        var key = (playlist.Id, kind);
        if (!refresh && _cache.TryGetValue(key, out var cached) && !cached.IsFaulted) return cached;
        var task = LoadAsync(playlist, kind);
        _cache[key] = task;
        return task;
    }

    private static async Task<IReadOnlyList<ChannelItem>> LoadAsync(PlaylistRef playlist, ChannelKind kind)
    {
        IReadOnlyList<ChannelItem> items = playlist.Xtream is { } account
            ? kind switch
            {
                ChannelKind.Movie => await XtreamClient.MoviesAsync(account),
                ChannelKind.Series => await XtreamClient.SeriesAsync(account),
                _ => await XtreamClient.LiveAsync(account),
            }
            : playlist.IsLocalFile
                ? Client.ParseInline(await System.IO.File.ReadAllTextAsync(playlist.Url)).channels.Select(ChannelItem.From).ToList()
                : (await Client.LoadPlaylist(playlist.Url)).channels.Select(ChannelItem.From).ToList();
        // Channel numbers follow the playlist's order.
        for (var i = 0; i < items.Count; i++) items[i].Number = i + 1;
        return items;
    }

    /// <summary>Live channels of every playlist whose name matches, then
    /// the films and series of each Xtream account, searched on the
    /// provider's server (as on macOS).</summary>
    public static async Task<IReadOnlyList<ChannelItem>> SearchAsync(string query)
    {
        var perSource = await Task.WhenAll(_playlists.Select(async p =>
        {
            var found = new List<ChannelItem>();
            try
            {
                found.AddRange((await ItemsAsync(p)).Where(c => XtreamClient.TitleMatches(c.Name, query)).Take(40));
                if (p.Xtream is { } account)
                {
                    var vod = await XtreamClient.SearchAsync(account, query);
                    // Keep the session's lists consistent: same objects for PlaylistOf.
                    _searchHits[p.Id] = vod;
                    found.AddRange(vod);
                }
            }
            catch
            {
                // An unreachable source just doesn't contribute.
            }
            return found;
        }));
        return perSource.SelectMany(r => r).Take(120).ToList();
    }

    private static readonly Dictionary<Guid, IReadOnlyList<ChannelItem>> _searchHits = new();

    public static PlaylistRef? PlaylistOf(ChannelItem item) =>
        _playlists.FirstOrDefault(p => (_searchHits.TryGetValue(p.Id, out var hits) && hits.Contains(item))
            || _cache.Any(c => c.Key.Item1 == p.Id && c.Value.IsCompletedSuccessfully && c.Value.Result.Contains(item)));
}
