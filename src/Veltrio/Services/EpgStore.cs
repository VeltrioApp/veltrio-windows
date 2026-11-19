using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Veltrio.Services;

/// <summary>The XMLTV guide of the selected playlist. An Xtream provider
/// serves it at xmltv.php; an M3U playlist names it in its header
/// (url-tvg / x-tvg-url). Guides are often tens of megabytes and gzipped,
/// so the file is streamed and only the playlist's channels, from an hour
/// back to a day ahead, are kept — as on macOS.</summary>
public static class EpgStore
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    private static Dictionary<string, List<EpgProgramme>> _programmes = new();
    /// <summary>Playlist channel id → XMLTV channel id, for channels matched by name.</summary>
    private static Dictionary<string, string> _nameMatches = new();
    private static Guid? _loadedFor;
    private static DateTime _loadedAt;
    private static Task? _loading;

    public static bool IsLoading => _loading is { IsCompleted: false };
    public static bool HasGuide => _programmes.Count > 0;
    public static string? Error { get; private set; }

    /// <summary>The guide finished loading (any thread).</summary>
    public static event Action? Changed;

    static EpgStore()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Veltrio-Windows/0.1");
    }

    public static Task LoadAsync(PlaylistRef playlist, IReadOnlyList<ChannelItem> channels, bool force = false)
    {
        if (!force && _loadedFor == playlist.Id && DateTime.UtcNow - _loadedAt < MaxAge) return Task.CompletedTask;
        if (!force && _loadedFor == playlist.Id && _loading is { IsCompleted: false }) return _loading;
        _loadedFor = playlist.Id;
        _loadedAt = DateTime.UtcNow;
        Error = null;
        var live = channels.Where(c => c.Kind == ChannelKind.Live).ToList();
        _loading = Task.Run(async () =>
        {
            try
            {
                var url = await GuideUrlAsync(playlist);
                if (url is null)
                {
                    Apply(playlist.Id, new(), new());
                    return;
                }
                var (programmes, matches) = await FetchAndParseAsync(url, live);
                Apply(playlist.Id, programmes, matches);
            }
            catch (Exception ex)
            {
                Error = $"Guide TV indisponible : {ex.Message}";
                Apply(playlist.Id, new(), new());
            }
        });
        return _loading;
    }

    private static void Apply(Guid id, Dictionary<string, List<EpgProgramme>> programmes, Dictionary<string, string> matches)
    {
        if (_loadedFor != id) return; // another playlist was picked meanwhile
        _programmes = programmes;
        _nameMatches = matches;
        Changed?.Invoke();
    }

    public static void Clear()
    {
        _programmes = new();
        _nameMatches = new();
        _loadedFor = null;
    }

    // -------------------- queries --------------------

    public static IReadOnlyList<EpgProgramme> Schedule(ChannelItem channel)
    {
        if (channel.TvgId is { } tvg && _programmes.TryGetValue(tvg.ToLowerInvariant(), out var list)) return list;
        if (_nameMatches.TryGetValue(channel.Id, out var mapped) && _programmes.TryGetValue(mapped, out list)) return list;
        return Array.Empty<EpgProgramme>();
    }

    public static EpgProgramme? Current(ChannelItem channel, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        return Schedule(channel).FirstOrDefault(p => p.Start <= now && now < p.End);
    }

    public static IEnumerable<EpgProgramme> Upcoming(ChannelItem channel, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        return Schedule(channel).Where(p => p.Start > now);
    }

    // -------------------- fetching --------------------

    /// <summary>Xtream: xmltv.php. M3U: the header of the playlist, read
    /// from its first few kilobytes rather than downloading it all.</summary>
    private static async Task<string?> GuideUrlAsync(PlaylistRef playlist)
    {
        if (playlist.Xtream is { } account) return XtreamClient.GuideUrl(account);
        if (playlist.IsLocalFile)
        {
            using var reader = new StreamReader(playlist.Url);
            return HeaderGuideUrl(await reader.ReadLineAsync() ?? "");
        }
        if (!Uri.TryCreate(playlist.Url, UriKind.Absolute, out var uri) || !uri.Scheme.StartsWith("http")) return null;
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[8192];
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
        var header = Encoding.UTF8.GetString(buffer, 0, read).Split('\n')[0];
        return HeaderGuideUrl(header);
    }

    internal static string? HeaderGuideUrl(string header)
    {
        if (!header.TrimStart('﻿').StartsWith("#EXTM3U")) return null;
        foreach (var attribute in new[] { "url-tvg", "x-tvg-url" })
        {
            var at = header.IndexOf($"{attribute}=\"", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var start = at + attribute.Length + 2;
            var end = header.IndexOf('"', start);
            if (end < 0) continue;
            // Some headers list several guides, comma-separated: take the first.
            var first = header[start..end].Split(',')[0].Trim();
            if (Uri.TryCreate(first, UriKind.Absolute, out _)) return first;
        }
        return null;
    }

    private static async Task<(Dictionary<string, List<EpgProgramme>>, Dictionary<string, string>)> FetchAndParseAsync(
        string url, IReadOnlyList<ChannelItem> channels)
    {
        var wantedIds = channels.Where(c => c.TvgId is not null).Select(c => c.TvgId!.ToLowerInvariant()).ToHashSet();
        var wantedNames = channels.GroupBy(c => NameKey(c.TvgName ?? c.Name)).ToDictionary(g => g.Key, g => g.First().Id);
        var now = DateTime.UtcNow;
        var from = now.AddHours(-1);
        var to = now.AddHours(24);

        var file = Path.GetTempFileName();
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(file);
                await response.Content.CopyToAsync(output);
            }

            await using var raw = File.OpenRead(file);
            var magic = new byte[2];
            var gzipped = raw.Read(magic, 0, 2) == 2 && magic[0] == 0x1f && magic[1] == 0x8b;
            raw.Position = 0;
            await using Stream input = gzipped ? new GZipStream(raw, CompressionMode.Decompress) : raw;

            var programmes = new Dictionary<string, List<EpgProgramme>>();
            var matches = new Dictionary<string, string>();
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
            });

            // Each branch advances the reader itself: Skip() and the
            // Read*Content calls already move past the element.
            string? channelId = null;
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }
                switch (reader.Name)
                {
                    case "channel":
                        channelId = reader.GetAttribute("id")?.ToLowerInvariant();
                        reader.Read();
                        break;
                    case "display-name" when channelId is not null:
                        // A playlist channel without a matching tvg-id is matched by name.
                        var display = reader.ReadElementContentAsString();
                        if (wantedNames.TryGetValue(NameKey(display), out var playlistId) && !matches.ContainsKey(playlistId))
                        {
                            matches[playlistId] = channelId;
                            wantedIds.Add(channelId);
                        }
                        break;
                    case "programme":
                        var ch = reader.GetAttribute("channel")?.ToLowerInvariant();
                        var start = ParseDate(reader.GetAttribute("start"));
                        var end = ParseDate(reader.GetAttribute("stop")) ?? start?.AddMinutes(30);
                        if (ch is null || !wantedIds.Contains(ch) || start is null || end is null || end < from || start > to)
                        {
                            reader.Skip();
                            break;
                        }
                        var programme = ReadProgramme(reader.ReadSubtree(), ch, start.Value, end.Value);
                        reader.Skip(); // past </programme>
                        if (programme is not null)
                        {
                            if (!programmes.TryGetValue(ch, out var list)) programmes[ch] = list = new();
                            list.Add(programme);
                        }
                        break;
                    default:
                        reader.Read();
                        break;
                }
            }
            foreach (var list in programmes.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
            return (programmes, matches);
        }
        finally
        {
            try { File.Delete(file); } catch { /* temp file */ }
        }
    }

    private static EpgProgramme? ReadProgramme(XmlReader sub, string channel, DateTime start, DateTime end)
    {
        string? title = null, desc = null, category = null, image = null;
        using (sub)
        {
            sub.Read(); // <programme>
            sub.Read();
            while (!sub.EOF)
            {
                if (sub.NodeType != XmlNodeType.Element) { sub.Read(); continue; }
                switch (sub.Name)
                {
                    case "title" when title is null: title = sub.ReadElementContentAsString().Trim(); break;
                    case "desc" when desc is null: desc = sub.ReadElementContentAsString().Trim(); break;
                    case "category" when category is null: category = sub.ReadElementContentAsString().Trim(); break;
                    case "icon" when image is null: image = sub.GetAttribute("src"); sub.Skip(); break;
                    default: sub.Skip(); break;
                }
            }
        }
        return string.IsNullOrEmpty(title) ? null : new EpgProgramme
        {
            ChannelId = channel, Start = start, End = end, Title = title,
            Description = string.IsNullOrEmpty(desc) ? null : desc,
            Category = string.IsNullOrEmpty(category) ? null : category,
            Image = image,
        };
    }

    /// <summary>"20261119203000 +0100", or without the zone (UTC).</summary>
    internal static DateTime? ParseDate(string? s)
    {
        s = s?.Trim();
        if (s is null || s.Length < 14
            || !DateTime.TryParseExact(s[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        var zone = s[14..].Trim();
        var offset = TimeSpan.Zero;
        if (zone.Length >= 5 && (zone[0] == '+' || zone[0] == '-')
            && int.TryParse(zone.AsSpan(1, 2), out var hours) && int.TryParse(zone.AsSpan(3, 2), out var minutes))
            offset = new TimeSpan(hours, minutes, 0) * (zone[0] == '-' ? -1 : 1);
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    /// <summary>Lower case, accents removed, punctuation as spaces.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return sb.ToString();
    }

    /// <summary>Letters and digits only, case and accents folded.</summary>
    internal static string NameKey(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
