using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;
using Windows.UI;

namespace Veltrio.Pages;

public sealed record HubRequest(
    string Eyebrow,
    string Title,
    uint ColorArgb,
    string? LogoUrl,
    int? ProviderId,
    int? MovieGenreId,
    int? TvGenreId);

public sealed partial class HubPage : Page
{
    public HubPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not HubRequest req) return;
        BackLabel.Text = AppPrefs.Tr("RETOUR", "BACK");
        EyebrowText.Text = req.Eyebrow.ToUpperInvariant();
        TitleText.Text = req.Title;

        var brand = FromArgb(req.ColorArgb);
        var grad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
        };
        grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(88, brand.R, brand.G, brand.B), Offset = 0 });
        grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, brand.R, brand.G, brand.B), Offset = 1 });
        HeaderBand.Background = grad;

        if (!string.IsNullOrEmpty(req.LogoUrl) && Uri.TryCreate(req.LogoUrl, UriKind.Absolute, out var uri))
        {
            LogoImage.Source = new BitmapImage(uri);
            LogoFrame.Visibility = Visibility.Visible;
        }
        else
        {
            LogoFrame.Visibility = Visibility.Collapsed;
        }

        _ = LoadAllRowsAsync(req);
    }

    private async Task LoadAllRowsAsync(HubRequest req)
    {
        RowsContainer.Children.Clear();
        var specs = new (int Num, string Title, string Sub, Func<Task<IReadOnlyList<PosterItem>>> Load)[]
        {
            (1, AppPrefs.Tr("Films populaires", "Popular Movies"), AppPrefs.Tr("TMDB · FILM", "TMDB · MOVIE"),
                () => TmdbService.DiscoverAsync("movie", req.MovieGenreId, req.ProviderId, 1, "popularity.desc")),
            (2, AppPrefs.Tr("Séries populaires", "Popular Series"), AppPrefs.Tr("TMDB · SÉRIE", "TMDB · SERIES"),
                () => TmdbService.DiscoverAsync("series", req.TvGenreId, req.ProviderId, 1, "popularity.desc")),
            (3, AppPrefs.Tr("Films les mieux notés", "Top Rated Movies"), AppPrefs.Tr("TMDB · FILM", "TMDB · MOVIE"),
                () => TmdbService.DiscoverAsync("movie", req.MovieGenreId, req.ProviderId, 1, "vote_average.desc", 150)),
            (4, AppPrefs.Tr("Séries les mieux notées", "Top Rated Series"), AppPrefs.Tr("TMDB · SÉRIE", "TMDB · SERIES"),
                () => TmdbService.DiscoverAsync("series", req.TvGenreId, req.ProviderId, 1, "vote_average.desc", 150)),
        };

        var lists = new List<ListView>();
        var rings = new List<ProgressRing>();
        foreach (var (num, title, sub, _) in specs)
        {
            var section = new StackPanel { Spacing = 14 };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            header.Children.Add(new TextBlock { Text = $"{num:00} —", Style = (Style)Application.Current.Resources["VeltrioNumeral"], VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["VeltrioSectionTitle"] });
            header.Children.Add(new TextBlock { Text = sub, Style = (Style)Application.Current.Resources["VeltrioEyebrow"], VerticalAlignment = VerticalAlignment.Center });
            section.Children.Add(header);

            var ring = new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left };
            section.Children.Add(ring);
            rings.Add(ring);

            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                IsItemClickEnabled = true,
                ItemTemplate = (DataTemplate)Application.Current.Resources["PosterTemplate"],
                ItemContainerStyle = (Style)Application.Current.Resources["VeltrioBareListItem"],
                Padding = new Thickness(0, 0, 48, 0),
                Visibility = Visibility.Collapsed,
            };
            WheelScroll.SetPassToPage(lv, true);
            ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollMode(lv, ScrollMode.Disabled);
            lv.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsStackPanel Orientation='Horizontal' /></ItemsPanelTemplate>");
            lv.ItemClick += (_, args) => { if (args.ClickedItem is PosterItem item) Navigator.OpenTitle(item); };
            section.Children.Add(lv);
            lists.Add(lv);

            RowsContainer.Children.Add(section);
        }

        var tasks = specs.Select(s => SafeLoadAsync(s.Load)).ToArray();
        var results = await Task.WhenAll(tasks);
        for (var i = 0; i < results.Length; i++)
        {
            rings[i].IsActive = false;
            rings[i].Visibility = Visibility.Collapsed;
            if (results[i].Count > 0)
            {
                lists[i].ItemsSource = results[i];
                lists[i].Visibility = Visibility.Visible;
            }
        }
    }

    private static async Task<IReadOnlyList<PosterItem>> SafeLoadAsync(Func<Task<IReadOnlyList<PosterItem>>> load)
    {
        try { return await load(); }
        catch { return Array.Empty<PosterItem>(); }
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));
}
