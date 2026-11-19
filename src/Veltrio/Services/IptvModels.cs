using System.Text.Json.Serialization;
using uniffi.veltrio;

namespace Veltrio.Services;

/// <summary>An Xtream Codes account. The password isn't in the JSON: it's
/// kept encrypted in <see cref="SecretStore"/>, by playlist id.</summary>
public sealed class XtreamAccount
{
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Live container: "ts" or "m3u8".</summary>
    public string Output { get; set; } = "ts";
    [JsonIgnore] public string Password { get; set; } = "";

    /// <summary>"http://" added when missing, trailing slashes removed.</summary>
    [JsonIgnore]
    public string BaseUrl
    {
        get
        {
            var h = Host.Trim().TrimEnd('/');
            if (h.Length > 0 && !h.Contains("://")) h = "http://" + h;
            return h;
        }
    }
}

/// <summary>An IPTV source the user added: an M3U URL or an Xtream account.</summary>
public sealed class PlaylistRef
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>M3U URL; empty for Xtream.</summary>
    public string Url { get; set; } = "";
    public XtreamAccount? Xtream { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public bool IsXtream => Xtream is not null;
    [JsonIgnore] public string SourceLabel => IsXtream ? $"XTREAM · {Xtream!.BaseUrl}" : Url;
}

public enum ChannelKind { Live, Movie, Series }

/// <summary>A channel, or for Xtream a film or series, in one shape so the
/// lists and search don't need three.</summary>
public sealed class ChannelItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Playable URL; empty for a series (opens its page instead).</summary>
    public string StreamUrl { get; init; } = "";
    public string? Logo { get; init; }
    public string Group { get; init; } = "";
    public ChannelKind Kind { get; init; } = ChannelKind.Live;
    /// <summary>XMLTV channel id, to find the guide.</summary>
    public string? TvgId { get; init; }
    public string? TvgName { get; init; }
    /// <summary>Xtream stream id (live, film) or series id.</summary>
    public string? XtreamId { get; init; }
    /// <summary>Xtream keeps past programmes (catch-up), this many days.</summary>
    public int ArchiveDays { get; init; }
    public string? Rating { get; init; }
    public string? Year { get; init; }
    /// <summary>Position in the whole playlist, so numbers don't move when
    /// a group or a search narrows the list.</summary>
    public int Number { get; set; }

    public string NumberLabel => Number > 0 ? Number.ToString("000") : "";

    /// <summary>What screen readers announce for the list item.</summary>
    public override string ToString() => Name;
    public bool HasCatchup => ArchiveDays > 0;

    /// <summary>A playlist entry pointing at a file (provider VOD lists)
    /// rather than a live stream: it gets a scrubber.</summary>
    public bool IsFile
    {
        get
        {
            var path = Uri.TryCreate(StreamUrl, UriKind.Absolute, out var u) ? u.AbsolutePath : StreamUrl;
            return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string Subtitle => string.Join(" · ", new[] { Year, Rating is { Length: > 0 } r ? $"★ {r}" : null, Group }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    internal static ChannelItem From(IptvChannel c) => new()
    {
        Id = c.id,
        Name = c.name,
        StreamUrl = c.streamUrl,
        Logo = string.IsNullOrWhiteSpace(c.logoUrl) ? null : c.logoUrl,
        Group = string.IsNullOrWhiteSpace(c.group) ? "Sans groupe" : c.group!,
        TvgId = string.IsNullOrWhiteSpace(c.tvgId) ? null : c.tvgId,
        TvgName = string.IsNullOrWhiteSpace(c.tvgName) ? null : c.tvgName,
    };
}

/// <summary>An Xtream series: seasons of episodes.</summary>
public sealed class XtreamSeries
{
    public string Name { get; init; } = "";
    public string? Plot { get; init; }
    public string? Cover { get; init; }
    public string? Backdrop { get; init; }
    public string? Genre { get; init; }
    public string? Released { get; init; }
    public string? Rating { get; init; }
    public IReadOnlyList<XtreamEpisode> Episodes { get; init; } = Array.Empty<XtreamEpisode>();
}

public sealed class XtreamEpisode
{
    public string Id { get; init; } = "";
    public int Season { get; init; }
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string? Plot { get; init; }
    public string? Image { get; init; }
    public string? Duration { get; init; }
    public string StreamUrl { get; init; } = "";

    public string Code => $"S{Season} · E{Number}";
    public override string ToString() => $"{Code} {Title}";
    public double Progress { get; set; }
}

/// <summary>One programme of the TV guide.</summary>
public sealed class EpgProgramme
{
    public string ChannelId { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Category { get; init; }
    public string? Image { get; init; }

    public double ProgressAt(DateTime now) =>
        End > Start ? Math.Clamp((now - Start).TotalSeconds / (End - Start).TotalSeconds, 0, 1) : 0;

    public string TimeRange => $"{Start.ToLocalTime():HH:mm} – {End.ToLocalTime():HH:mm}";
}
