using System.Text.RegularExpressions;

namespace Veltrio.Services;

/// <summary>Ready-to-play copies of an IMDB title that don't come from an
/// addon: a file on the user's Jellyfin, Emby or Plex server, or a film or
/// episode on their IPTV provider. Listed first among the title's streams.</summary>
public static class DirectSources
{
    /// <summary>Every connected server and every Xtream playlist, in parallel.</summary>
    /// <param name="imdbId">The title's IMDB id; for a series, the series'.</param>
    public static async Task<IReadOnlyList<StreamItem>> FindAsync(string imdbId, string title, int? year, int? season, int? episode)
    {
        var lookups = MediaServers.Connected.Select(s => ServerAsync(s, imdbId, title, year, season, episode))
            .Concat(IptvStore.Playlists.Where(p => p.IsXtream).Select(p => IptvAsync(p, title, year, season, episode)));
        return (await Task.WhenAll(lookups)).SelectMany(found => found).ToList();
    }

    private static async Task<IEnumerable<StreamItem>> ServerAsync(MediaServer server, string imdbId, string title, int? year, int? season, int? episode)
    {
        try
        {
            var matches = season is { } s && episode is { } e
                ? await server.FindEpisodeAsync(imdbId, title, year, s, e)
                : await server.FindMovieAsync(imdbId, title, year);
            return matches.Select(m => StreamItem.Direct(server.Name, m.Name, m.Video, m.AudioLanguages.Select(ShortLanguage), m.StreamUrl));
        }
        catch (Exception ex)
        {
            Player.PlayerLog.Write($"[direct] {server.Name}: {ex.Message}");
            return Enumerable.Empty<StreamItem>();
        }
    }

    /// <summary>Every Xtream playlist is asked, not only one: the user wants
    /// to see every copy they can play.</summary>
    private static async Task<IEnumerable<StreamItem>> IptvAsync(PlaylistRef playlist, string title, int? year, int? season, int? episode)
    {
        var account = playlist.Xtream!;
        var label = IptvStore.Playlists.Count(p => p.IsXtream) > 1 ? playlist.Name : null;
        string Detail(string group) => string.Join(" · ", new[] { group, label }.Where(t => !string.IsNullOrEmpty(t)));
        IEnumerable<string> Language(ChannelItem item) => XtreamMatching.Language(item.Group, item.Name) is { } l ? new[] { l } : Array.Empty<string>();
        try
        {
            var found = (await XtreamClient.SearchAsync(account, title, limit: 50))
                .Where(c => c.Kind == (season is null ? ChannelKind.Movie : ChannelKind.Series)
                            && XtreamMatching.IsSameTitle(c.Name, title, year))
                .ToList();
            if (season is not { } s || episode is not { } e)
                return found.Select(film => StreamItem.Direct("IPTV", film.Name, Detail(film.Group), Language(film), film.StreamUrl));
            var copies = new List<StreamItem>();
            // A title rarely has more than a couple of copies (one per
            // language); cap the info calls so a loose match can't fan out.
            foreach (var series in found.Where(c => c.XtreamId is not null).Take(4))
            {
                var info = await XtreamClient.SeriesInfoAsync(account, series.XtreamId!);
                if (info.Episodes.FirstOrDefault(ep => ep.Season == s && ep.Number == e) is { } ep)
                    copies.Add(StreamItem.Direct("IPTV", $"{series.Name} · {ep.Title}", Detail(series.Group), Language(series), ep.StreamUrl));
            }
            return copies;
        }
        catch (Exception ex)
        {
            Player.PlayerLog.Write($"[direct] {playlist.Name}: {ex.Message}");
            return Enumerable.Empty<StreamItem>();
        }
    }

    /// <summary>Servers report ISO 639-2 codes; show the short tags the IPTV rows use.</summary>
    private static string ShortLanguage(string code) => code.ToLowerInvariant() switch
    {
        "fre" or "fra" => "FR", "eng" => "EN", "ara" => "AR", "ger" or "deu" => "DE", "spa" => "ES", "ita" => "IT",
        "dut" or "nld" => "NL", "tur" => "TR", "por" => "PT", "jpn" => "JA", "kor" => "KO", "chi" or "zho" => "ZH",
        "rus" => "RU", "pol" => "PL",
        var other => other.ToUpperInvariant(),
    };
}

/// <summary>Matching provider entries to a title, as on macOS.</summary>
public static partial class XtreamMatching
{
    /// <summary>Provider names dress titles up: "NF - The Irishman (2019)",
    /// "FR | Dune [MULTI]". Both sides are stripped down to the bare title;
    /// when both carry a year they must agree to within one.</summary>
    public static bool IsSameTitle(string providerName, string title, int? year)
    {
        // A trailing number may be part of the title ("Blade Runner 2049")
        // or a year the provider appended ("Dune 2021"). Equal with it kept:
        // only a bracketed year can disagree. Equal only once it's dropped:
        // it was a year, and it has to agree.
        int? theirs;
        if (Normalized(providerName, keepTrailingYear: true) == Normalized(title, keepTrailingYear: true))
            theirs = Year(providerName, trailing: false);
        else if (Normalized(providerName) == Normalized(title))
            theirs = Year(providerName, trailing: true);
        else
            return false;
        return year is not { } y || theirs is not { } t || Math.Abs(t - y) <= 1;
    }

    public static string Normalized(string raw, bool keepTrailingYear = false)
    {
        var s = raw;
        // Leading tags: "NF - ", "FR | ", "[4K] ", "VOSTFR: "… A tag is upper
        // case and set off by a pipe or a spaced dash, so "Spider-Man" and
        // "ALIEN: Romulus" keep their first word.
        while (LeadingTag().Match(s) is { Success: true } m) s = s[m.Length..];
        // Anything in brackets or parentheses: years, "(Multi-Audio)"…
        s = Bracketed().Replace(s, " ");
        if (!keepTrailingYear) s = TrailingYear().Replace(s, " ");
        s = EpgStore.Fold(s.Replace("&", " and "));
        return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A bracketed year, "(2019)", and when trailing, a bare one at the end.</summary>
    public static int? Year(string name, bool trailing = true)
    {
        var bracketed = BracketedYear().Matches(name);
        if (bracketed.Count > 0) return int.Parse(bracketed[^1].Groups[1].Value);
        if (trailing && TrailingYearCapture().Match(name) is { Success: true } m) return int.Parse(m.Groups[1].Value);
        return null;
    }

    /// <summary>Language of a provider entry, from its category ("FR | Films",
    /// "VOSTFR - Séries"), failing that from its name's leading tag (scanning
    /// the whole name would read "It" or "Us" as a language).</summary>
    public static string? Language(string? category, string? name)
    {
        foreach (var text in new[] { category, name is null ? null : NameTag().Match(name).Value })
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var token in Regex.Split(text.ToUpperInvariant(), @"[^\p{L}]+"))
                if (LanguageCodes.TryGetValue(token, out var lang)) return lang;
        }
        return null;
    }

    private static readonly Dictionary<string, string> LanguageCodes = new()
    {
        ["FR"] = "FR", ["FRA"] = "FR", ["FRE"] = "FR", ["FRENCH"] = "FR", ["FRANCE"] = "FR", ["VF"] = "VF", ["VFF"] = "VF",
        ["VFQ"] = "VFQ", ["TRUEFRENCH"] = "VF", ["VOSTFR"] = "VOSTFR", ["VOST"] = "VOSTFR",
        ["EN"] = "EN", ["ENG"] = "EN", ["ENGLISH"] = "EN", ["UK"] = "EN", ["US"] = "EN", ["USA"] = "EN",
        ["AR"] = "AR", ["ARA"] = "AR", ["ARABIC"] = "AR", ["DE"] = "DE", ["GER"] = "DE", ["GERMAN"] = "DE", ["DEUTSCH"] = "DE",
        ["ES"] = "ES", ["ESP"] = "ES", ["SPA"] = "ES", ["SPANISH"] = "ES", ["IT"] = "IT", ["ITA"] = "IT", ["ITALIAN"] = "IT",
        ["NL"] = "NL", ["DUTCH"] = "NL", ["TR"] = "TR", ["TURK"] = "TR", ["TURKISH"] = "TR",
        ["PT"] = "PT", ["POR"] = "PT", ["BR"] = "PT-BR", ["PORTUGUESE"] = "PT",
        ["MULTI"] = "MULTI", ["MULTISUB"] = "MULTI",
    };

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\)|[A-Z0-9+]{1,8}\s*(\||\s-\s))\s*")]
    private static partial Regex LeadingTag();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s(19|20)\d{2}\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"\s((?:19|20)\d{2})\s*$")]
    private static partial Regex TrailingYearCapture();

    [GeneratedRegex(@"[\(\[]\s*((?:19|20)\d{2})\s*[\)\]]")]
    private static partial Regex BracketedYear();

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|[A-Z]{2,8}(?=\s*(\||\s-\s|:\s)))")]
    private static partial Regex NameTag();
}
