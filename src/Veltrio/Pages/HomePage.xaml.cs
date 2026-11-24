using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Home: an editorial hero, then Continue watching and one
/// numbered row per catalog of the enabled addons.</summary>
public sealed partial class HomePage : Page
{
    private PosterItem? _hero;
    private bool _stale = true;
    private string _discoverType = "movie";
    private int? _discoverProvider;
    private int? _discoverGenreIndex;

    public HomePage()
    {
        InitializeComponent();
        CoreService.AddonsChanged += () => _stale = true;
        TmdbService.Changed += () => _stale = true;
        AppPrefs.LanguageChanged += () => _stale = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // Cached page: progress may have moved since it was last shown.
        RefreshContinueWatching();
        if (_stale) Rebuild();
    }

    private void Rebuild()
    {
        _stale = false;
        Hero.Visibility = Visibility.Collapsed;
        _hero = null;

        var offset = ContinueSection.Visibility == Visibility.Visible ? 2 : 1;
        var rows = CoreService.HomeCatalogs().Select((pair, i) => new HomeRow
        {
            Number = $"{offset + i:00} —",
            Title = pair.Catalog.Name,
            Subtitle = $"{pair.Addon.Name.ToUpperInvariant()} · {CoreService.TypeLabel(pair.Catalog.Type).ToUpperInvariant()}",
            Addon = pair.Addon,
            Catalog = pair.Catalog,
        }).ToList();
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RowsPanel.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiscoverSection.Visibility = TmdbService.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        if (TmdbService.IsConnected) BuildDiscoverChips();
        Rows.ItemsSource = rows;
        foreach (var row in rows) _ = LoadRowAsync(row);
    }

    private void BuildDiscoverChips()
    {
        DiscoverChips.Children.Clear();
        var chipStyle = (Style)Application.Current.Resources["VeltrioChip"];
        void AddChip(string label, bool selected, Action onClick)
        {
            var c = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Content = label, IsChecked = selected, Style = chipStyle };
            c.Click += (_, _) => { onClick(); BuildDiscoverChips(); _ = LoadDiscoverAsync(); };
            DiscoverChips.Children.Add(c);
        }
        AddChip(AppPrefs.Tr("FILMS", "MOVIES"), _discoverType == "movie", () => _discoverType = "movie");
        AddChip(AppPrefs.Tr("SÉRIES", "SERIES"), _discoverType == "series", () => _discoverType = "series");
        foreach (var (id, name, logoUrl) in TmdbService.DiscoverProviders)
        {
            var pid = id;
            var selected = _discoverProvider == pid;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri))
            {
                row.Children.Add(new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(5),
                    Child = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri), Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill },
                });
            }
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var btn = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = row,
                IsChecked = selected,
                Style = chipStyle,
                Padding = new Thickness(6, 4, 12, 4),
            };
            btn.Click += (_, _) => { _discoverProvider = _discoverProvider == pid ? null : pid; BuildDiscoverChips(); _ = LoadDiscoverAsync(); };
            DiscoverChips.Children.Add(btn);
        }
        for (var i = 0; i < TmdbService.DiscoverGenres.Length; i++)
        {
            var idx = i;
            var g = TmdbService.DiscoverGenres[i];
            AddChip(AppPrefs.Tr(g.Fr, g.En).ToUpperInvariant(), _discoverGenreIndex == idx, () => _discoverGenreIndex = _discoverGenreIndex == idx ? null : idx);
        }
        if (_discoverProvider is not null || _discoverGenreIndex is not null)
            AddChip(AppPrefs.Tr("EFFACER", "CLEAR"), false, () => { _discoverProvider = null; _discoverGenreIndex = null; });
    }

    private async Task LoadDiscoverAsync()
    {
        var active = _discoverProvider is not null || _discoverGenreIndex is not null;
        if (!active)
        {
            DiscoverLoading.IsActive = false;
            DiscoverLoading.Visibility = Visibility.Collapsed;
            DiscoverResultsScroll.Visibility = Visibility.Collapsed;
            DiscoverResultsRow.Children.Clear();
            return;
        }
        DiscoverLoading.IsActive = true;
        DiscoverLoading.Visibility = Visibility.Visible;
        DiscoverResultsScroll.Visibility = Visibility.Collapsed;
        int? gid = _discoverGenreIndex is { } gi
            ? (_discoverType == "series" ? TmdbService.DiscoverGenres[gi].TvId : TmdbService.DiscoverGenres[gi].MovieId)
            : null;
        var items = await TmdbService.DiscoverAsync(_discoverType, gid, _discoverProvider);
        DiscoverLoading.IsActive = false;
        DiscoverLoading.Visibility = Visibility.Collapsed;
        DiscoverResultsRow.Children.Clear();
        foreach (var item in items)
        {
            var card = new StackPanel { Width = 140, Spacing = 6 };
            var box = new Grid
            {
                Height = 210,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioSurfaceBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioBorderBrush"],
                BorderThickness = new Thickness(1),
            };
            if (Uri.TryCreate(item.PosterUrl, UriKind.Absolute, out var uri))
                box.Children.Add(new Image { Source = new BitmapImage(uri), Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
            card.Children.Add(box);
            card.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 12,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioTextPrimaryBrush"],
            });
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["VeltrioLinkButton"],
                Padding = new Thickness(0),
                Content = card,
            };
            var target = item;
            btn.Click += (_, _) => Navigator.OpenTitle(target);
            DiscoverResultsRow.Children.Add(btn);
        }
        DiscoverResultsScroll.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadRowAsync(HomeRow row)
    {
        await row.LoadAsync();
        // The first row with artwork gives the hero, as on macOS.
        if (_hero is null && row.Items.FirstOrDefault(i => i.BackgroundUrl is not null) is { } featured)
            ShowHero(featured);
    }

    private void ShowHero(PosterItem item)
    {
        _hero = item;
        HeroImage.Source = new BitmapImage(new Uri(item.BackgroundUrl!));
        HeroSource.Text = $"· VIA {item.Addon.Name.ToUpperInvariant()}";
        HeroTitle.Text = item.Title;
        var facts = new List<string>();
        if (item.Rating is not null) facts.Add($"★ {item.Rating}");
        if (item.Year is not null) facts.Add(item.Year);
        facts.Add(CoreService.TypeLabel(item.Type).ToUpperInvariant());
        if (item.Genres.Count > 0) facts.Add(string.Join(" · ", item.Genres.Take(2)));
        HeroFacts.Text = string.Join("   ", facts);
        HeroDescription.Text = item.Description ?? "";
        HeroWatchLabel.Text = WatchStore.Entry(item.Type, item.Id)?.IsStarted == true ? "REPRENDRE" : "REGARDER";
        Hero.Visibility = Visibility.Visible;
        RowsPanel.Padding = new Thickness(48, 0, 0, 0);
    }

    private void RefreshContinueWatching()
    {
        var items = WatchStore.ContinueWatching;
        var visible = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ContinueSection.Visibility != visible) _stale = true; // row numbers shift
        ContinueSection.Visibility = visible;
        ContinueList.ItemsSource = items;
        ContinueCount.Text = $"{items.Count} EN COURS";
    }

    private void OnPosterClick(object sender, ItemClickEventArgs e) => Navigator.OpenTitle((PosterItem)e.ClickedItem);

    private void OnContinueClick(object sender, ItemClickEventArgs e) => Navigator.OpenEntry((WatchEntry)e.ClickedItem);

    private void OnHeroOpen(object sender, RoutedEventArgs e)
    {
        if (_hero is not null) Navigator.OpenTitle(_hero);
    }

    private void OnSeeAll(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HomeRow row)
            Navigator.Go(typeof(CatalogPage), new CatalogRequest(row.Addon, row.Catalog));
    }

    private void OnOpenSources(object sender, RoutedEventArgs e) => Navigator.Window.ShowSection(3);

    private async void OnConnectTmdb(object sender, RoutedEventArgs e)
    {
        ConnectTmdb.IsEnabled = false;
        var ok = await TmdbService.ConnectAsync(TmdbKeyInput.Password);
        ConnectTmdb.IsEnabled = true;
        EmptyError.Text = ok ? "" : (TmdbService.Error ?? "");
        if (ok)
        {
            TmdbKeyInput.Password = "";
            Rebuild();
        }
    }
}
