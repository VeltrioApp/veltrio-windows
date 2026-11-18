using uniffi.veltrio;

namespace Veltrio.Services;

/// <summary>A poster as the pages show it. The core's records are internal
/// (UniFFI generates them that way), so pages bind to this instead.</summary>
public sealed record PosterItem(string Id, string Type, string Title, string? PosterUrl, string? Subtitle);

/// <summary>A catalog row on the home page.</summary>
public sealed record CatalogRow(string Title, string Subtitle, IReadOnlyList<PosterItem> Items);

/// <summary>The app's single entry point into the Rust core.</summary>
public static class CoreService
{
    /// <summary>Cinemeta, Stremio's official catalog addon: what the app
    /// shows before the user installs anything.</summary>
    public const string CinemetaManifestUrl = "https://v3-cinemeta.strem.io/manifest.json";

    private static readonly StremioClient Client = new();

    public static string Version => VeltrioMethods.CoreVersion();

    /// <summary>The first catalogs of an addon, loaded in parallel. A
    /// catalog that fails is left out rather than failing the page.</summary>
    public static async Task<IReadOnlyList<CatalogRow>> LoadHomeAsync(string manifestUrl = CinemetaManifestUrl, int maxRows = 6)
    {
        var manifest = await Client.FetchManifest(manifestUrl);
        var tasks = manifest.catalogs.Take(maxRows).Select(async catalog =>
        {
            try
            {
                var metas = await Client.FetchCatalog(manifest.transportUrl, catalog.type, catalog.id, Array.Empty<string>());
                var items = metas.Select(m => new PosterItem(m.id, m.type, m.name, m.poster, m.releaseInfo)).ToList();
                return new CatalogRow(catalog.name, $"{manifest.name} · {catalog.type}", items);
            }
            catch
            {
                return null;
            }
        });
        var rows = await Task.WhenAll(tasks);
        return rows.Where(r => r is { Items.Count: > 0 }).Select(r => r!).ToList();
    }
}
