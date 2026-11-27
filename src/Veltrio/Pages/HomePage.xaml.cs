using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;
using Windows.UI;

namespace Veltrio.Pages;

/// <summary>Home: an editorial hero, then Continue watching and one
/// numbered row per catalog of the enabled addons.</summary>
public sealed partial class HomePage : Page
{
    private PosterItem? _hero;
    private bool _stale = true;

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
        if (TmdbService.IsConnected) BuildHubCards();
        Rows.ItemsSource = rows;
        foreach (var row in rows) _ = LoadRowAsync(row);
    }

    private void BuildHubCards()
    {
        ProviderCardsGrid.Children.Clear();
        ProviderCardsGrid.ColumnDefinitions.Clear();
        var providers = TmdbService.DiscoverProviders;
        for (var i = 0; i < providers.Length; i++)
        {
            ProviderCardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (id, name, logoUrl, colorArgb) = providers[i];
            var brand = FromArgb(colorArgb);
            var grad = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
            };
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(72, brand.R, brand.G, brand.B), Offset = 0 });
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(24, brand.R, brand.G, brand.B), Offset = 1 });

            var stack = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri))
            {
                stack.Children.Add(new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(10),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new Image { Source = new BitmapImage(uri), Stretch = Stretch.UniformToFill },
                });
            }
            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["VeltrioTextPrimaryBrush"],
            });

            var btn = new Button
            {
                Content = stack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 96,
                CornerRadius = new CornerRadius(12),
                Background = grad,
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, brand.R, brand.G, brand.B)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
            };
            var req = new HubRequest(AppPrefs.Tr("Plateforme", "Platform"), name, colorArgb, logoUrl, id, null, null);
            btn.Click += (_, _) => Frame?.Navigate(typeof(HubPage), req);
            Grid.SetColumn(btn, i);
            ProviderCardsGrid.Children.Add(btn);
        }

        GenreCardsGrid.Children.Clear();
        GenreCardsGrid.ColumnDefinitions.Clear();
        var genres = TmdbService.DiscoverGenres;
        for (var i = 0; i < genres.Length; i++)
        {
            GenreCardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (movieId, tvId, fr, en, colorArgb) = genres[i];
            var label = AppPrefs.Tr(fr, en);
            var brand = FromArgb(colorArgb);
            var grad = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 0),
            };
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(80, brand.R, brand.G, brand.B), Offset = 0 });
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(24, brand.R, brand.G, brand.B), Offset = 1 });

            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = label,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources["VeltrioTextPrimaryBrush"],
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 46,
                CornerRadius = new CornerRadius(10),
                Background = grad,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, brand.R, brand.G, brand.B)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
            };
            var req = new HubRequest(AppPrefs.Tr("Genre", "Genre"), label, colorArgb, null, null, movieId, tvId);
            btn.Click += (_, _) => Frame?.Navigate(typeof(HubPage), req);
            Grid.SetColumn(btn, i);
            GenreCardsGrid.Children.Add(btn);
        }
    }

    private static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

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
