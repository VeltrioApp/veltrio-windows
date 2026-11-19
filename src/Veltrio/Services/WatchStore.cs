namespace Veltrio.Services;

/// <summary>One title in the library / watch history, keyed by (type, id).
/// Same shape as the macOS app's WatchEntry.</summary>
public sealed class WatchEntry
{
    public string Type { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Poster { get; set; }
    public string? Background { get; set; }
    public string? AddonId { get; set; }
    public string? AddonName { get; set; }
    // Movie progress, in seconds. A series keeps its episodes below.
    public double Position { get; set; }
    public double Duration { get; set; }
    public bool Watched { get; set; }
    public Dictionary<string, EpisodeProgress> Episodes { get; set; } = new();
    public string? LastEpisodeId { get; set; }
    public bool InLibrary { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastWatchedAt { get; set; } = DateTime.UtcNow;

    public string Key => $"{Type}|{ContentId}";
    public override string ToString() => Name;

    private (double Pos, double Dur) Current =>
        Type == "series" && LastEpisodeId is { } last && Episodes.TryGetValue(last, out var ep)
            ? (ep.Position, ep.Duration)
            : (Position, Duration);

    /// <summary>0…1; for a series, of the last episode started.</summary>
    public double ProgressFraction => Current.Dur > 0 ? Math.Clamp(Current.Pos / Current.Dur, 0, 1) : 0;
    public double ProgressPercent => ProgressFraction * 100;

    public bool IsStarted => Type == "series" ? LastEpisodeId is not null : Position > 10;

    public string EpisodeCode =>
        Type == "series" && LastEpisodeId is { } last && Episodes.TryGetValue(last, out var ep)
        && ep.Season is { } s && ep.Episode is { } e ? $"S{s} · E{e}" : "";

    public string ResumeLabel
    {
        get
        {
            var (pos, dur) = Current;
            if (dur <= 0) return "PAS COMMENCÉ";
            var mins = (int)(Math.Max(0, dur - pos) / 60);
            return mins <= 1 ? "BIENTÔT FINI" : $"{mins} MIN RESTANTES";
        }
    }

    public string StatusLabel => Watched ? "VU" : IsStarted ? "EN COURS" : InLibrary ? "ENREGISTRÉ" : "";
}

public sealed class EpisodeProgress
{
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public double Position { get; set; }
    public double Duration { get; set; }
    public bool Watched { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The persisted library and watch history (watch.json). Every
/// change goes through here so Home, the title page and the Collection
/// stay in sync.</summary>
public static class WatchStore
{
    private const string File = "watch.json";
    private const double WatchedThreshold = 0.9;

    private static readonly Dictionary<string, WatchEntry> Entries =
        Storage.Load(File, new List<WatchEntry>()).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());

    public static event Action? Changed;

    public static WatchEntry? Entry(string type, string id) => Entries.GetValueOrDefault($"{type}|{id}");

    public static bool IsInLibrary(string type, string id) => Entry(type, id)?.InLibrary == true;

    /// <summary>In progress, most recent first. Live channels have no page
    /// to reopen, so they never show up here.</summary>
    public static IReadOnlyList<WatchEntry> ContinueWatching => Entries.Values
        .Where(e => e.Type is "movie" or "series" && e.IsStarted && !e.Watched)
        .OrderByDescending(e => e.LastWatchedAt)
        .ToList();

    public static IReadOnlyList<WatchEntry> LibraryItems => Entries.Values
        .Where(e => e.Type is "movie" or "series" && (e.InLibrary || e.IsStarted || e.Watched))
        .OrderByDescending(e => e.LastWatchedAt)
        .ToList();

    /// <summary>Seconds to resume at; a title within 30 s of its end
    /// starts over.</summary>
    public static double ResumePosition(string type, string id, string? videoId = null)
    {
        if (Entry(type, id) is not { } e) return 0;
        var (pos, dur) = type == "series" && videoId is not null && e.Episodes.TryGetValue(videoId, out var ep)
            ? (ep.Position, ep.Duration)
            : type == "series" ? (0, 0) : (e.Position, e.Duration);
        return dur > 0 && pos > dur - 30 ? 0 : pos;
    }

    public static EpisodeProgress? Episode(string type, string id, string videoId) =>
        Entry(type, id)?.Episodes.GetValueOrDefault(videoId);

    public static void ReportProgress(PlayRequest r, double position, double duration)
    {
        if (r.ContentType is null || r.ContentId is null || duration <= 0) return;
        var e = GetOrCreate(r.ContentType, r.ContentId, r.Title);
        if (r.Poster is not null) e.Poster = r.Poster;
        if (r.Background is not null) e.Background = r.Background;
        if (r.Addon is not null) { e.AddonId = r.Addon.Id; e.AddonName = r.Addon.Name; }
        e.LastWatchedAt = DateTime.UtcNow;
        if (r.ContentType == "series" && r.VideoId is { } vid)
        {
            var ep = e.Episodes.GetValueOrDefault(vid) ?? new EpisodeProgress();
            ep.Season = r.Season ?? ep.Season;
            ep.Episode = r.Episode ?? ep.Episode;
            ep.Position = position;
            ep.Duration = duration;
            if (position / duration >= WatchedThreshold) ep.Watched = true;
            ep.UpdatedAt = DateTime.UtcNow;
            e.Episodes[vid] = ep;
            e.LastEpisodeId = vid;
        }
        else
        {
            e.Position = position;
            e.Duration = duration;
            if (position / duration >= WatchedThreshold) e.Watched = true;
        }
        Save();
    }

    public static void ToggleLibrary(string type, string id, string name, string? poster, string? background, AddonInfo? addon)
    {
        var e = GetOrCreate(type, id, name);
        e.Poster = poster ?? e.Poster;
        e.Background = background ?? e.Background;
        if (addon is not null) { e.AddonId = addon.Id; e.AddonName = addon.Name; }
        e.InLibrary = !e.InLibrary;
        if (e.InLibrary) e.AddedAt = e.LastWatchedAt = DateTime.UtcNow;
        Save();
    }

    public static void MarkWatched(string type, string id, bool watched)
    {
        if (Entry(type, id) is not { } e) return;
        e.Watched = watched;
        e.Position = watched ? Math.Max(e.Position, e.Duration) : 0;
        e.LastWatchedAt = DateTime.UtcNow;
        Save();
    }

    public static void Remove(string type, string id)
    {
        Entries.Remove($"{type}|{id}");
        Save();
    }

    /// <summary>Clears progress but keeps a saved title in the library.</summary>
    public static void RemoveFromContinueWatching(string type, string id)
    {
        if (Entry(type, id) is not { } e) return;
        e.Position = e.Duration = 0;
        e.Episodes.Clear();
        e.LastEpisodeId = null;
        if (!e.InLibrary) Entries.Remove(e.Key);
        Save();
    }

    private static WatchEntry GetOrCreate(string type, string id, string name)
    {
        var key = $"{type}|{id}";
        if (!Entries.TryGetValue(key, out var e))
        {
            e = new WatchEntry { Type = type, ContentId = id };
            Entries[key] = e;
        }
        if (!string.IsNullOrEmpty(name)) e.Name = name;
        return e;
    }

    private static void Save()
    {
        Storage.Save(File, Entries.Values.ToList());
        Changed?.Invoke();
    }
}
