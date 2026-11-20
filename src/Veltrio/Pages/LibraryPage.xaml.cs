using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Collection: saved titles and everything started, with the
/// same filters as macOS (right-click for watched / remove), or one
/// connected media server's libraries, a page at a time.</summary>
public sealed partial class LibraryPage : Page
{
    private static readonly (string Key, string Label)[] Filters =
    {
        ("all", "TOUT"), ("progress", "EN COURS"), ("saved", "ENREGISTRÉS"), ("watched", "VUS"),
    };
    private static readonly (string Key, string Label)[] Types =
    {
        ("all", "TOUS LES TYPES"), ("movie", "FILMS"), ("series", "SÉRIES"),
    };

    private const int PageSize = 60;

    private string _filter = "all";
    private string _type = "all";

    /// <summary>Set before showing the Collection to open on a server's
    /// libraries (Sources → Parcourir).</summary>
    public static MediaServerKind? RequestedServer { get; set; }

    // What a server shows survives leaving the page for a title or the
    // player and coming back.
    private static MediaServerKind? _server;
    private static MediaLibrarySection? _section;
    private static IReadOnlyList<MediaLibrarySection> _sections = Array.Empty<MediaLibrarySection>();
    private static List<MediaLibraryItem> _items = new();
    private static int _total;
    private bool _loading;

    public LibraryPage()
    {
        InitializeComponent();
        BuildChips(FilterChips, Filters, () => _filter, v => _filter = v);
        BuildChips(TypeChips, Types, () => _type, v => _type = v);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (RequestedServer is { } requested && requested != _server)
        {
            _server = requested;
            _sections = Array.Empty<MediaLibrarySection>();
            _section = null;
            _items = new();
        }
        RequestedServer = null;
        if (_server is { } kind && !Server(kind).IsConnected) _server = null;
        BuildSourceTabs();
        Refresh();
    }

    private static MediaServer Server(MediaServerKind kind) => MediaServers.All.First(s => s.Kind == kind);

    private void BuildSourceTabs()
    {
        SourceTabs.Children.Clear();
        var servers = MediaServers.Connected.ToList();
        SourceTabs.Visibility = servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (servers.Count == 0) return;
        void Add(string label, MediaServerKind? kind)
        {
            var tab = new ToggleButton { Content = label, IsChecked = kind == _server, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            tab.Click += (_, _) =>
            {
                if (kind != _server)
                {
                    _server = kind;
                    _sections = Array.Empty<MediaLibrarySection>();
                    _section = null;
                    _items = new();
                }
                BuildSourceTabs();
                Refresh();
            };
            SourceTabs.Children.Add(tab);
        }
        Add("ENREGISTRÉS", null);
        foreach (var server in servers) Add(server.Name.ToUpperInvariant(), server.Kind);
    }

    private void Refresh()
    {
        var onServer = _server is not null;
        SavedFilters.Visibility = onServer ? Visibility.Collapsed : Visibility.Visible;
        SectionChips.Visibility = onServer ? Visibility.Visible : Visibility.Collapsed;
        Items.Visibility = onServer ? Visibility.Collapsed : Visibility.Visible;
        ServerItems.Visibility = onServer ? Visibility.Visible : Visibility.Collapsed;
        if (_server is { } kind) _ = ShowServerAsync(Server(kind));
        else RefreshSaved();
    }

    private void BuildChips(Panel panel, (string Key, string Label)[] options, Func<string> get, Action<string> set)
    {
        foreach (var (key, label) in options)
        {
            var chip = new ToggleButton { Content = label, Tag = key, IsChecked = key == get(), Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) =>
            {
                set(key);
                foreach (var other in panel.Children.OfType<ToggleButton>()) other.IsChecked = (string)other.Tag == key;
                Refresh();
            };
            panel.Children.Add(chip);
        }
    }

    private void RefreshSaved()
    {
        ServerLoading.IsActive = false;
        EmptyTitle.Text = "Rien d'enregistré,\npour l'instant.";
        EmptyBody.Text = "Enregistrez un titre depuis sa fiche : il arrive ici, avec tout ce que vous avez commencé à regarder.";
        var all = WatchStore.LibraryItems;
        var items = all
            .Where(e => _type == "all" || e.Type == _type)
            .Where(e => _filter switch
            {
                "progress" => e.IsStarted && !e.Watched,
                "saved" => e.InLibrary,
                "watched" => e.Watched,
                _ => true,
            })
            .ToList();
        Eyebrow.Text = $"N° {all.Count} DANS VOTRE COLLECTION";
        Items.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count > 0)
        {
            EmptyTitle.Text = "Rien ne correspond.";
            EmptyBody.Text = "Aucun titre ne correspond à ces filtres.";
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (!Navigator.OpenEntry((WatchEntry)e.ClickedItem))
            EmptyBody.Text = "Aucune source installée ne fournit la fiche de ce titre.";
    }

    private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not WatchEntry entry) return;
        var menu = new MenuFlyout();
        var watched = new MenuFlyoutItem { Text = entry.Watched ? "Marquer comme non vu" : "Marquer comme vu" };
        watched.Click += (_, _) => { WatchStore.MarkWatched(entry.Type, entry.ContentId, !entry.Watched); Refresh(); };
        menu.Items.Add(watched);
        if (entry.InLibrary)
        {
            var unsave = new MenuFlyoutItem { Text = "Retirer de la collection" };
            unsave.Click += (_, _) =>
            {
                WatchStore.ToggleLibrary(entry.Type, entry.ContentId, entry.Name, null, null, null);
                Refresh();
            };
            menu.Items.Add(unsave);
        }
        var forget = new MenuFlyoutItem { Text = "Supprimer de l'historique" };
        forget.Click += (_, _) => { WatchStore.Remove(entry.Type, entry.ContentId); Refresh(); };
        menu.Items.Add(forget);
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
    }

    // -------------------- media server --------------------

    private async Task ShowServerAsync(MediaServer server)
    {
        Eyebrow.Text = $"{server.Name.ToUpperInvariant()} · {server.User?.ToUpperInvariant()}";
        EmptyState.Visibility = Visibility.Collapsed;
        if (_sections.Count == 0)
        {
            ServerLoading.IsActive = true;
            _sections = await server.SectionsAsync();
            ServerLoading.IsActive = false;
            if (_server != server.Kind) return;
            _section = _sections.FirstOrDefault();
        }
        BuildSectionChips();
        if (_section is null)
        {
            ShowEmpty("Aucune bibliothèque.", "Ce serveur n'a pas de bibliothèque de films ou de séries, ou ne répond pas.");
            return;
        }
        if (_items.Count == 0) await LoadPageAsync(server);
        else ShowItems();
    }

    private void BuildSectionChips()
    {
        SectionChips.Children.Clear();
        foreach (var section in _sections)
        {
            var chip = new ToggleButton { Content = section.Name.ToUpperInvariant(), IsChecked = section == _section, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) =>
            {
                if (_server is not { } kind) return;
                _section = section;
                _items = new();
                _total = 0;
                BuildSectionChips();
                _ = LoadPageAsync(Server(kind));
            };
            SectionChips.Children.Add(chip);
        }
    }

    private async Task LoadPageAsync(MediaServer server)
    {
        if (_section is not { } section || _loading) return;
        _loading = true;
        ServerLoading.IsActive = _items.Count == 0;
        LoadMore.IsEnabled = false;
        var page = await server.ItemsAsync(section, _items.Count, PageSize);
        _loading = false;
        ServerLoading.IsActive = false;
        LoadMore.IsEnabled = true;
        // The library may have changed while the page was loading.
        if (section != _section || _server != server.Kind) return;
        _items.AddRange(page.Items);
        _total = page.Total;
        ShowItems();
    }

    private void ShowItems()
    {
        ServerItems.ItemsSource = null;
        ServerItems.ItemsSource = _items;
        LoadMore.Visibility = _items.Count < _total ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count == 0) ShowEmpty("Bibliothèque vide.", "Cette bibliothèque ne contient aucun film ni aucune série.");
        else EmptyState.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty(string title, string body)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void OnLoadMore(object sender, RoutedEventArgs e)
    {
        if (_server is { } kind) _ = LoadPageAsync(Server(kind));
    }

    /// <summary>Identified titles open on their title page, where this copy
    /// sits among the other sources. Films the server couldn't identify
    /// play directly; such series list their episodes.</summary>
    private void OnServerItemClick(object sender, ItemClickEventArgs e)
    {
        if (_server is not { } kind) return;
        var server = Server(kind);
        var item = (MediaLibraryItem)e.ClickedItem;
        if (item.ImdbId is { } imdb && CoreService.MetaAddon(item.Type) is { } addon)
            Navigator.Go(typeof(DetailPage), new DetailRequest(addon, item.Type, imdb, item.Title, item.PosterUrl));
        else if (item.Type == "series")
            Navigator.Go(typeof(MediaServerSeriesPage), new MediaServerSeriesRequest(server, item));
        else if ((item.StreamUrl ?? server.StreamUrl(item.Id)) is { } url)
            Navigator.Play(new PlayRequest
            {
                Title = item.Title,
                Subtitle = server.Name,
                Url = url,
                ContentType = "server",
                ContentId = $"{kind}-{item.Id}".ToLowerInvariant(),
                Poster = item.PosterUrl,
            });
    }
}
