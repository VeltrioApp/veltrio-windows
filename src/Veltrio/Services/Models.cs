using System.Text.Json.Serialization;
using uniffi.veltrio;
using CoreStream = uniffi.veltrio.Stream;

namespace Veltrio.Services;

// The core's records are internal (UniFFI generates them that way) and
// aren't serializable, so the app keeps its own shapes: these are what the
// pages bind to and what goes to disk.

/// <summary>An installed Stremio addon, as saved in addons.json.</summary>
public sealed class AddonInfo
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Logo { get; set; }
    public List<string> Types { get; set; } = new();
    public List<string> Resources { get; set; } = new();
    public List<CatalogInfo> Catalogs { get; set; } = new();
    public List<string> IdPrefixes { get; set; } = new();
    public string TransportUrl { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    internal static AddonInfo From(Manifest m) => new()
    {
        Id = m.id,
        Version = m.version,
        Name = m.name,
        Description = m.description,
        Logo = m.logo,
        Types = m.types.ToList(),
        Resources = m.resources.ToList(),
        Catalogs = m.catalogs.Select(c => new CatalogInfo
        {
            Type = c.type,
            Id = c.id,
            Name = c.name,
            Extras = c.extra.Select(e => e.name).ToList(),
            RequiredExtras = c.extra.Where(e => e.isRequired).Select(e => e.name).ToList(),
        }).ToList(),
        IdPrefixes = m.idPrefixes.ToList(),
        TransportUrl = m.transportUrl,
    };

    public bool Serves(string resource, string type) =>
        Resources.Contains(resource) && (Types.Count == 0 || Types.Contains(type));
}

public sealed class CatalogInfo
{
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Extras { get; set; } = new();
    public List<string> RequiredExtras { get; set; } = new();

    /// <summary>Catalogs that need an extra (search, genre…) can't be
    /// listed on their own.</summary>
    [JsonIgnore] public bool IsBrowsable => RequiredExtras.Count == 0;
    [JsonIgnore] public bool IsSearchable => Extras.Contains("search");
}

/// <summary>A poster: a catalog or search result.</summary>
public sealed class PosterItem
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string? PosterUrl { get; init; }
    public string? BackgroundUrl { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public string? Rating { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    /// <summary>The addon that surfaced it, to open its page with.</summary>
    public AddonInfo Addon { get; init; } = null!;

    public string Subtitle => string.Join(" · ", new[] { Year, Rating is null ? null : $"★ {Rating}" }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>What screen readers announce for the list item.</summary>
    public override string ToString() => Title;

    internal static PosterItem From(MetaPreview m, AddonInfo addon) => new()
    {
        Id = m.id,
        Type = m.type,
        Title = m.name,
        PosterUrl = m.poster,
        BackgroundUrl = m.background,
        Description = m.description,
        Year = m.releaseInfo,
        Rating = m.imdbRating,
        Genres = m.genres,
        Addon = addon,
    };
}

/// <summary>A row on the home page or in search.</summary>
public sealed class CatalogRow
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Number { get; init; } = "";
    public AddonInfo Addon { get; init; } = null!;
    public CatalogInfo? Catalog { get; init; }
    public IReadOnlyList<PosterItem> Items { get; init; } = null!;
    public bool CanSeeAll => Catalog is not null && Items.Count >= 10;
}

/// <summary>A title's full page.</summary>
public sealed class TitleDetails
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Poster { get; init; }
    public string? Background { get; init; }
    public string? Description { get; init; }
    public string? Rating { get; init; }
    public string? Year { get; init; }
    public string? Runtime { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Cast { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Director { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Writer { get; init; } = Array.Empty<string>();
    public IReadOnlyList<EpisodeItem> Episodes { get; init; } = Array.Empty<EpisodeItem>();
    public AddonInfo Addon { get; init; } = null!;

    internal static TitleDetails From(Meta m, AddonInfo addon) => new()
    {
        Id = m.id,
        Type = m.type,
        Name = m.name,
        Poster = m.poster,
        Background = m.background,
        Description = m.description,
        Rating = m.imdbRating,
        Year = m.releaseInfo,
        Runtime = m.runtime,
        Genres = m.genres,
        Cast = m.cast,
        Director = m.director,
        Writer = m.writer,
        Episodes = m.videos
            .Select(v => new EpisodeItem
            {
                Id = v.id,
                Title = string.IsNullOrWhiteSpace(v.title) ? $"Épisode {v.episode}" : v.title,
                Season = v.season,
                Episode = v.episode,
                Thumbnail = v.thumbnail,
                Overview = v.overview,
                Released = v.released,
            })
            .OrderBy(e => e.Season is 0 ? int.MaxValue : e.Season ?? 0)
            .ThenBy(e => e.Episode ?? 0)
            .ToList(),
        Addon = addon,
    };
}

public sealed class EpisodeItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? Thumbnail { get; init; }
    public string? Overview { get; init; }
    public string? Released { get; init; }
    public double Progress { get; set; }
    public bool Watched { get; set; }
    public Microsoft.UI.Xaml.Visibility WatchedVisibility => Watched ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string Code => Season is { } s && Episode is { } e ? $"S{s} · E{e}" : "";
    public override string ToString() => $"{Code} {Title}";
    public string ReleasedDate => Released is { Length: >= 10 } r ? r[..10] : Released ?? "";
}

/// <summary>A playable stream from an addon, with what the list shows.</summary>
public sealed class StreamItem
{
    internal CoreStream Stream { get; }
    public string AddonName { get; }
    public int Score { get; init; }

    internal StreamItem(CoreStream stream, string addonName)
    {
        Stream = stream;
        AddonName = addonName;
    }

    public string Name => (Stream.name ?? AddonName).Replace('\n', ' ');
    public override string ToString() => $"{Name} {Title}";
    public string Title => (Stream.title ?? Stream.description ?? "").Trim();
    public string Kind => Stream.source switch
    {
        StreamSource.Torrent when IsCached => CacheLabel,
        StreamSource.Torrent => "TORRENT",
        StreamSource.Url => "DIRECT",
        StreamSource.YouTube => "YOUTUBE",
        _ => "EXTERNE",
    };

    public string Quality
    {
        get
        {
            var hay = $"{Stream.name} {Stream.title}".ToUpperInvariant();
            if (hay.Contains("2160P") || hay.Contains("4K")) return "4K";
            if (hay.Contains("1080P")) return "1080p";
            if (hay.Contains("720P")) return "720p";
            if (hay.Contains("480P")) return "480p";
            return "";
        }
    }

    public string? InfoHash => Stream.source is StreamSource.Torrent t ? t.infoHash : null;

    /// <summary>Stremio's behaviorHints.bingeGroup: streams of the same
    /// release across episodes, so "next episode" picks the same one.</summary>
    public string? BingeGroup
    {
        get
        {
            if (Stream.behaviorHintsJson is not { Length: > 0 } json) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("bingeGroup", out var g) ? g.GetString() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The debrid service that has this torrent cached, if any.</summary>
    public DebridProvider? CachedOn => InfoHash is { } h ? DebridService.CachedOn(h) : null;
    public bool IsCached => CachedOn is not null;
    public string CacheLabel => CachedOn switch
    {
        DebridProvider.RealDebrid => "⚡ RD",
        DebridProvider.AllDebrid => "⚡ AD",
        DebridProvider.Premiumize => "⚡ PM",
        DebridProvider.TorBox => "⚡ TB",
        _ => "",
    };
}

/// <summary>What the player is asked to open.</summary>
public sealed class PlayRequest
{
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    /// <summary>A ready URL (live channel), or null to resolve <see cref="Stream"/>.</summary>
    public string? Url { get; init; }
    public StreamItem? Stream { get; init; }

    // For progress tracking; null for live TV.
    public string? ContentType { get; init; }
    public string? ContentId { get; init; }
    public string? VideoId { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? Poster { get; init; }
    public string? Background { get; init; }
    public AddonInfo? Addon { get; init; }
    public bool IsLive { get; init; }

    /// <summary>Live TV: the channel and its playlist, for the guide
    /// overlay, zapping and catch-up in the player.</summary>
    public ChannelItem? Channel { get; init; }
    public PlaylistRef? Playlist { get; init; }

    /// <summary>Series: what comes next ("S1E3 · Titre") and how to get a
    /// request for it (its streams resolved), or null at the last episode.</summary>
    public string? NextLabel { get; init; }
    public Func<Task<PlayRequest?>>? Next { get; init; }

    /// <summary>A live channel (or a provider film/episode) as the player opens it.</summary>
    public static PlayRequest ForChannel(ChannelItem channel, PlaylistRef? playlist) => new()
    {
        Title = channel.Name,
        Subtitle = channel.Group,
        Url = channel.StreamUrl,
        IsLive = channel.Kind == ChannelKind.Live && !channel.IsFile,
        Channel = channel,
        Playlist = playlist,
        Poster = channel.Logo,
        // Provider films and episodes resume like any title ("iptv" never
        // shows in Continue watching: there's no page to reopen).
        ContentType = channel.Kind == ChannelKind.Live ? null : "iptv",
        ContentId = channel.Kind == ChannelKind.Live ? null : channel.Id,
    };
}
