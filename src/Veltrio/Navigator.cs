using Microsoft.UI.Xaml.Controls;
using Veltrio.Pages;
using Veltrio.Services;

namespace Veltrio;

/// <summary>What a title page is opened with.</summary>
public sealed record DetailRequest(AddonInfo Addon, string Type, string Id, string Name, string? Poster = null, string? Background = null);

/// <summary>What the "See all" grid is opened with.</summary>
public sealed record CatalogRequest(AddonInfo Addon, CatalogInfo Catalog);

/// <summary>App-wide navigation: the window's frame, reachable from any page.</summary>
public static class Navigator
{
    public static Frame Frame { get; set; } = null!;
    public static MainWindow Window { get; set; } = null!;

    public static void Go(Type page, object? parameter = null) => Frame.Navigate(page, parameter);

    public static bool Back()
    {
        if (!Frame.CanGoBack) return false;
        Frame.GoBack();
        return true;
    }

    public static void OpenTitle(PosterItem item) =>
        Go(typeof(DetailPage), new DetailRequest(item.Addon, item.Type, item.Id, item.Title, item.PosterUrl, item.BackgroundUrl));

    /// <summary>Reopens a library entry with the addon that surfaced it,
    /// or any metadata addon if that one is gone.</summary>
    public static bool OpenEntry(WatchEntry entry)
    {
        if (CoreService.MetaAddon(entry.Type, entry.AddonId) is not { } addon) return false;
        Go(typeof(DetailPage), new DetailRequest(addon, entry.Type, entry.ContentId, entry.Name, entry.Poster, entry.Background));
        return true;
    }

    public static void Play(PlayRequest request) => Go(typeof(PlayerPage), request);

    /// <summary>Another channel from inside the player: Back still returns
    /// to where the player was opened from.</summary>
    public static void Zap(PlayRequest request)
    {
        Go(typeof(PlayerPage), request);
        var stack = Frame.BackStack;
        if (stack.Count > 0 && stack[^1].SourcePageType == typeof(PlayerPage)) stack.RemoveAt(stack.Count - 1);
    }
}
