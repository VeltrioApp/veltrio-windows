using uniffi.veltrio;

namespace Veltrio.Services;

/// <summary>M3U playlists and their channels, through the core's parser.
/// Xtream Codes providers are macOS-only for now: their client lives in
/// Swift, not in the core.</summary>
public static class IptvStore
{
    private const string File = "iptv-playlists.json";
    private const string SelectedFile = "iptv-selected.json";

    private static readonly IptvClient Client = new();
    private static readonly List<PlaylistRef> _playlists = Storage.Load(File, new List<PlaylistRef>());
    private static readonly Dictionary<Guid, Task<IReadOnlyList<ChannelItem>>> _channels = new();

    public static event Action? Changed;

    public static IReadOnlyList<PlaylistRef> Playlists => _playlists;

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

    public static string? Add(string name, string url)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "L'URL de la playlist doit commencer par http:// ou https://.";
        var playlist = new PlaylistRef
        {
            Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(),
            Url = url,
        };
        _playlists.Add(playlist);
        Storage.Save(File, _playlists);
        Selected = playlist;
        return null;
    }

    public static void Remove(PlaylistRef playlist)
    {
        _playlists.RemoveAll(p => p.Id == playlist.Id);
        _channels.Remove(playlist.Id);
        Storage.Save(File, _playlists);
        Changed?.Invoke();
    }

    /// <summary>The playlist's channels, loaded once per session unless
    /// <paramref name="refresh"/> is set.</summary>
    public static Task<IReadOnlyList<ChannelItem>> ChannelsAsync(PlaylistRef playlist, bool refresh = false)
    {
        if (!refresh && _channels.TryGetValue(playlist.Id, out var cached) && !cached.IsFaulted) return cached;
        var task = LoadAsync(playlist.Url);
        _channels[playlist.Id] = task;
        return task;
    }

    private static async Task<IReadOnlyList<ChannelItem>> LoadAsync(string url)
    {
        var playlist = await Client.LoadPlaylist(url);
        return playlist.channels.Select(ChannelItem.From).ToList();
    }

    /// <summary>Channels of every loaded playlist whose name matches, for search.</summary>
    public static async Task<IReadOnlyList<ChannelItem>> SearchAsync(string query)
    {
        var results = new List<ChannelItem>();
        foreach (var p in _playlists)
        {
            try
            {
                var channels = await ChannelsAsync(p);
                results.AddRange(channels.Where(c => c.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
            }
            catch
            {
                // An unreachable playlist just doesn't contribute.
            }
        }
        return results.Take(60).ToList();
    }
}
