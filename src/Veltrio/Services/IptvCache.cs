using System.Text.Json;

namespace Veltrio.Services;

/// <summary>The last channel list of each playlist (one per Xtream section)
/// and its TV guide, kept under %LOCALAPPDATA%\Veltrio\Cache\Iptv: a new
/// session shows them without asking the provider again. Providers limit
/// requests and connections, and refetching 20,000 channels and the guide
/// at every start got users blocked (HTTP 511). As on macOS, iOS and
/// Android.
///
/// Xtream passwords stay encrypted in <see cref="SecretStore"/>: stream
/// URLs carry the password in their path, so it's stored as a placeholder
/// and put back on load.</summary>
public static class IptvCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    public static readonly TimeSpan GuideMaxAge = TimeSpan.FromHours(6);
    private const string PasswordToken = "{veltrio-password}";

    private static readonly string Folder = Path.Combine(Storage.Folder, "Cache", "Iptv");
    private static readonly JsonSerializerOptions Options = new();

    public sealed record Entry(IReadOnlyList<ChannelItem> Items, DateTime SavedAt)
    {
        public bool IsFresh => DateTime.UtcNow - SavedAt < MaxAge;
    }

    private sealed class Stored
    {
        public DateTime SavedAt { get; set; }
        public List<ChannelItem> Items { get; set; } = new();
    }

    private sealed class StoredGuide
    {
        public DateTime SavedAt { get; set; }
        public Dictionary<string, List<EpgProgramme>> Programmes { get; set; } = new();
        public Dictionary<string, string> Matches { get; set; } = new();
    }

    private static string FileFor(Guid playlistId, string name) => Path.Combine(Folder, $"{playlistId}-{name}.json");

    /// <summary>The /user/password/ segment of the playlist's stream URLs.</summary>
    private static (string Plain, string Masked)? Secret(PlaylistRef playlist)
    {
        if (playlist.Xtream is not { } account || account.Password.Length == 0) return null;
        var user = Uri.EscapeDataString(account.Username);
        return ($"/{user}/{Uri.EscapeDataString(account.Password)}/", $"/{user}/{PasswordToken}/");
    }

    private static ChannelItem WithUrl(ChannelItem c, string url) => new()
    {
        Id = c.Id, Name = c.Name, StreamUrl = url, Logo = c.Logo, Group = c.Group, Kind = c.Kind,
        TvgId = c.TvgId, TvgName = c.TvgName, XtreamId = c.XtreamId, ArchiveDays = c.ArchiveDays,
        Rating = c.Rating, Year = c.Year, Number = c.Number,
    };

    public static async Task<Entry?> LoadAsync(PlaylistRef playlist, ChannelKind kind)
    {
        try
        {
            var path = FileFor(playlist.Id, kind.ToString());
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            var stored = await JsonSerializer.DeserializeAsync<Stored>(stream, Options);
            if (stored is null) return null;
            var secret = Secret(playlist);
            var items = secret is { } s
                ? stored.Items.Select(c => WithUrl(c, c.StreamUrl.Replace(s.Masked, s.Plain))).ToList()
                : stored.Items;
            return new Entry(items, stored.SavedAt);
        }
        catch
        {
            return null;
        }
    }

    public static Task SaveAsync(PlaylistRef playlist, ChannelKind kind, IReadOnlyList<ChannelItem> items) => Task.Run(() =>
    {
        var secret = Secret(playlist);
        var masked = secret is { } s
            ? items.Select(c => WithUrl(c, c.StreamUrl.Replace(s.Plain, s.Masked))).ToList()
            : items.ToList();
        Write(FileFor(playlist.Id, kind.ToString()), new Stored { SavedAt = DateTime.UtcNow, Items = masked });
    });

    /// <summary>The guide and its name matches, when one younger than
    /// <see cref="GuideMaxAge"/> is kept.</summary>
    public static (Dictionary<string, List<EpgProgramme>> Programmes, Dictionary<string, string> Matches, DateTime SavedAt)? LoadGuide(Guid playlistId)
    {
        try
        {
            var path = FileFor(playlistId, "guide");
            if (!File.Exists(path)) return null;
            var guide = JsonSerializer.Deserialize<StoredGuide>(File.ReadAllText(path), Options);
            if (guide is null || DateTime.UtcNow - guide.SavedAt >= GuideMaxAge) return null;
            return (guide.Programmes, guide.Matches, guide.SavedAt);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveGuide(Guid playlistId, Dictionary<string, List<EpgProgramme>> programmes, Dictionary<string, string> matches) =>
        Write(FileFor(playlistId, "guide"), new StoredGuide { SavedAt = DateTime.UtcNow, Programmes = programmes, Matches = matches });

    public static void Remove(Guid playlistId)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.GetFiles(Folder, $"{playlistId}-*")) File.Delete(file);
        }
        catch
        {
            // A leftover cache file is harmless.
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // A lost cache only means asking the provider next time.
        }
    }
}
