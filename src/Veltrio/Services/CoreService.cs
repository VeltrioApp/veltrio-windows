using System.Text.RegularExpressions;
using uniffi.veltrio;
using CoreStream = uniffi.veltrio.Stream;

namespace Veltrio.Services;

/// <summary>The app's single entry point into the Rust core: installed
/// addons, catalogs, metadata, streams and their resolution. The Windows
/// counterpart of the macOS AppStore.</summary>
public static class CoreService
{
    private static readonly StremioClient Client = new();
    private static readonly TorrentEngine Torrents = new();

    public static string Version => VeltrioMethods.CoreVersion();

    public static readonly IReadOnlyList<(string Name, string Url, string Blurb)> SuggestedAddons = new[]
    {
        ("Cinemeta", "https://v3-cinemeta.strem.io/manifest.json",
            "Les catalogues officiels de films et de séries (via TMDB)."),
        ("OpenSubtitles v3", "https://opensubtitles-v3.strem.io/manifest.json",
            "Des sous-titres dans plus de 50 langues."),
        ("WatchHub", "https://watchhub.strem.io/manifest.json",
            "Des liens de streaming légaux : Apple TV, Prime, etc."),
    };

    // -------------------- addons --------------------

    private const string AddonsFile = "addons.json";
    private const string DisabledFile = "addons-disabled.json";

    private static readonly List<AddonInfo> _addons = Storage.Load(AddonsFile, new List<AddonInfo>());
    private static readonly HashSet<string> _disabled = Storage.Load(DisabledFile, new HashSet<string>());

    /// <summary>Raised after any change to the installed addons.</summary>
    public static event Action? AddonsChanged;

    public static IReadOnlyList<AddonInfo> Addons => _addons;
    public static IReadOnlyList<AddonInfo> EnabledAddons => _addons.Where(a => !_disabled.Contains(a.Id)).ToList();
    public static bool IsEnabled(AddonInfo addon) => !_disabled.Contains(addon.Id);
    public static bool IsInstalled(string manifestUrl) =>
        _addons.Any(a => BaseUrl(a.TransportUrl) == BaseUrl(manifestUrl));

    /// <summary>The core keeps the addon's base URL; suggestions use the
    /// manifest's. Both reduce to the same base.</summary>
    private static string BaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)) url = url[..^"/manifest.json".Length];
        return url.ToLowerInvariant();
    }

    /// <summary>Installs the addon behind a manifest URL. Returns an error
    /// message, or null on success.</summary>
    public static async Task<string?> InstallAddonAsync(string url)
    {
        url = url.Trim();
        // stremio:// links are https:// manifests in disguise.
        if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url["stremio://".Length..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "L'URL d'un addon doit commencer par http:// ou https://.";
        try
        {
            var addon = AddonInfo.From(await Client.FetchManifest(url));
            if (_addons.Any(a => a.Id == addon.Id))
                return $"L'addon {addon.Name} est déjà installé.";
            _addons.Add(addon);
            SaveAddons();
            return null;
        }
        catch (Exception ex)
        {
            return Humanize(ex);
        }
    }

    public static void RemoveAddon(AddonInfo addon)
    {
        _addons.RemoveAll(a => a.Id == addon.Id);
        _disabled.Remove(addon.Id);
        Storage.Save(DisabledFile, _disabled);
        SaveAddons();
    }

    public static void SetEnabled(AddonInfo addon, bool enabled)
    {
        if (enabled) _disabled.Remove(addon.Id); else _disabled.Add(addon.Id);
        Storage.Save(DisabledFile, _disabled);
        AddonsChanged?.Invoke();
    }

    public static void Move(AddonInfo addon, int delta)
    {
        var from = _addons.FindIndex(a => a.Id == addon.Id);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _addons.Count) return;
        _addons.RemoveAt(from);
        _addons.Insert(to, addon);
        SaveAddons();
    }

    private static void SaveAddons()
    {
        Storage.Save(AddonsFile, _addons);
        AddonsChanged?.Invoke();
    }

    // -------------------- catalogs --------------------

    private static readonly Dictionary<string, Task<IReadOnlyList<PosterItem>>> _catalogCache = new();

    /// <summary>A catalog's first page, cached for the session so going
    /// back to Home doesn't refetch every row.</summary>
    public static Task<IReadOnlyList<PosterItem>> LoadCatalogAsync(AddonInfo addon, CatalogInfo catalog)
    {
        var key = $"{addon.TransportUrl}|{catalog.Type}|{catalog.Id}";
        if (_catalogCache.TryGetValue(key, out var cached) && !cached.IsFaulted) return cached;
        var task = WithTimeout(FetchCatalogAsync(addon, catalog, Array.Empty<string>()), 20);
        _catalogCache[key] = task;
        return task;
    }

    /// <summary>Further pages for the "See all" grid (Stremio's skip= extra).</summary>
    public static async Task<IReadOnlyList<PosterItem>> LoadCatalogPageAsync(AddonInfo addon, CatalogInfo catalog, int skip)
    {
        try
        {
            return await WithTimeout(FetchCatalogAsync(addon, catalog, skip > 0 ? new[] { $"skip={skip}" } : Array.Empty<string>()), 15);
        }
        catch
        {
            return Array.Empty<PosterItem>();
        }
    }

    private static async Task<IReadOnlyList<PosterItem>> FetchCatalogAsync(AddonInfo addon, CatalogInfo catalog, string[] extras)
    {
        var metas = await Client.FetchCatalog(addon.TransportUrl, catalog.Type, catalog.Id, extras);
        return metas.Select(m => PosterItem.From(m, addon)).ToList();
    }

    /// <summary>Every browsable catalog of the enabled addons, in order.</summary>
    public static IEnumerable<(AddonInfo Addon, CatalogInfo Catalog)> HomeCatalogs() =>
        EnabledAddons.SelectMany(a => a.Catalogs.Where(c => c.IsBrowsable).Select(c => (a, c)));

    // -------------------- metadata --------------------

    /// <summary>The addon to open a title's page with: the preferred one
    /// when still installed, else the first metadata addon for the type.</summary>
    public static AddonInfo? MetaAddon(string type, string? preferredId = null) =>
        _addons.FirstOrDefault(a => a.Id == preferredId)
        ?? _addons.FirstOrDefault(a => a.Serves("meta", type));

    /// <summary>Tries the preferred addon, then every other enabled addon
    /// serving metadata for the type; the first answer wins.</summary>
    public static async Task<TitleDetails> FetchDetailsAsync(AddonInfo preferred, string type, string id)
    {
        var ordered = new List<AddonInfo> { preferred };
        ordered.AddRange(EnabledAddons.Where(a => a.Id != preferred.Id && a.Serves("meta", type)));
        Exception? last = null;
        foreach (var addon in ordered)
        {
            try
            {
                var meta = await WithTimeout(Client.FetchMeta(addon.TransportUrl, type, id), 15);
                return TitleDetails.From(meta, addon);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(last is null ? "Aucun addon n'a pu fournir la fiche." : Humanize(last));
    }

    // -------------------- streams --------------------

    /// <summary>Streams from every enabled addon serving this type, fetched
    /// in parallel, 15 s each; an addon that fails or hangs is left out.
    /// Best first.</summary>
    public static async Task<IReadOnlyList<StreamItem>> FetchStreamsAsync(string type, string id)
    {
        var eligible = EnabledAddons.Where(a => a.Serves("stream", type)).ToList();
        var chunks = await Task.WhenAll(eligible.Select(async addon =>
        {
            try
            {
                var streams = await WithTimeout(Client.FetchStreams(addon.TransportUrl, type, id), 15);
                return streams.Select(s => new StreamItem(s, addon.Name) { Score = Score(s) }).ToArray();
            }
            catch
            {
                return Array.Empty<StreamItem>();
            }
        }));
        return chunks.SelectMany(c => c).OrderByDescending(s => s.Score).ToList();
    }

    /// <summary>Same ranking as macOS: direct links before torrents, then
    /// resolution, HDR/Dolby/Atmos, then seeds.</summary>
    private static int Score(CoreStream s)
    {
        var hay = $"{s.name} {s.title}".ToUpperInvariant();
        var score = s.source switch
        {
            StreamSource.Url => 5_000,
            StreamSource.External => 3_000,
            _ => 0,
        };
        if (hay.Contains("2160P") || hay.Contains("4K")) score += 2_000;
        else if (hay.Contains("1080P")) score += 1_500;
        else if (hay.Contains("720P")) score += 1_000;
        else if (hay.Contains("480P")) score += 500;
        if (hay.Contains("HDR")) score += 200;
        if (hay.Contains("DOLBY")) score += 150;
        if (hay.Contains("ATMOS")) score += 150;
        var seeds = Regex.Match(s.title ?? "", @"👤\s*(\d+)");
        if (seeds.Success && int.TryParse(seeds.Groups[1].Value, out var n)) score += Math.Min(n, 500);
        return score;
    }

    /// <summary>Turns a stream into a URL the player can open: links as
    /// they are; torrents through a debrid service when one is connected
    /// (a direct HTTPS link, no seeding), else the core's local engine.</summary>
    public static async Task<string> ResolveAsync(StreamItem item, int? season = null, int? episode = null)
    {
        switch (item.Stream.source)
        {
            case StreamSource.Url u when IsPlayable(u.url):
                return u.url;
            case StreamSource.Torrent t:
                if (DebridService.AnyConnected && await DebridService.ResolveAsync(t.infoHash, season, episode) is { } direct)
                    return direct;
                return await Torrents.StartStream(t.infoHash, t.sources, t.fileIdx);
            default:
                throw new InvalidOperationException("Ce flux ne se lit pas dans l'app.");
        }
    }

    /// <summary>Links meant for the browser (YouTube, external pages).</summary>
    public static string? ExternalUrl(StreamItem item) => item.Stream.source switch
    {
        StreamSource.YouTube y => $"https://www.youtube.com/watch?v={y.videoId}",
        StreamSource.External e when e.externalUrl.StartsWith("http") => e.externalUrl,
        _ => null,
    };

    /// <summary>Stops seeding a torrent once the player closes.</summary>
    public static void StopTorrent(string infoHash)
    {
        _ = Task.Run(async () =>
        {
            try { await Torrents.StopStream(infoHash, false); }
            catch { /* already gone */ }
        });
    }

    /// <summary>A hostile addon shouldn't make the app open file:// or
    /// similar.</summary>
    public static bool IsPlayable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme is "http" or "https" or "rtmp" or "rtmps" or "rtsp" or "udp";

    // -------------------- search --------------------

    /// <summary>One row per searchable catalog that answered with results,
    /// in addon order. 10 s per catalog.</summary>
    public static async Task<IReadOnlyList<CatalogRow>> SearchAsync(string query)
    {
        var targets = EnabledAddons
            .SelectMany(a => a.Catalogs.Where(c => c.IsSearchable).Select(c => (a, c)))
            .ToList();
        var rows = await Task.WhenAll(targets.Select(async t =>
        {
            try
            {
                var items = await WithTimeout(FetchCatalogAsync(t.a, t.c, new[] { $"search={query}" }), 10);
                return items.Count == 0 ? null : new CatalogRow
                {
                    Title = TypeLabel(t.c.Type, plural: true),
                    Subtitle = $"{t.a.Name} · {t.c.Name}",
                    Addon = t.a,
                    Items = items,
                };
            }
            catch
            {
                return null;
            }
        }));
        return rows.OfType<CatalogRow>().ToList();
    }

    // -------------------- helpers --------------------

    public static string TypeLabel(string type, bool plural = false) => type switch
    {
        "movie" => plural ? "Films" : "Film",
        "series" => plural ? "Séries" : "Série",
        "channel" => plural ? "Chaînes" : "Chaîne",
        "tv" => "TV",
        _ => type,
    };

    private static async Task<T> WithTimeout<T>(Task<T> task, int seconds)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        if (done != task) throw new TimeoutException("Délai dépassé");
        return await task;
    }

    public static string Humanize(Exception ex) => ex switch
    {
        VeltrioException.Network n => $"Réseau : {n.message}",
        VeltrioException.Parse p => $"Réponse illisible : {p.message}",
        VeltrioException.InvalidUrl u => $"URL invalide : {u.message}",
        VeltrioException.NotFound f => $"Introuvable : {f.message}",
        VeltrioException.Unsupported s => $"Non pris en charge : {s.message}",
        VeltrioException.Internal i => $"Erreur interne : {i.message}",
        TimeoutException => "Délai dépassé",
        _ => ex.Message,
    };
}
