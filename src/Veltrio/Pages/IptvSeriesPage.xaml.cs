using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

public sealed record IptvSeriesRequest(PlaylistRef Playlist, ChannelItem Series);

/// <summary>An Xtream series: seasons and episodes from get_series_info,
/// with resume per episode.</summary>
public sealed partial class IptvSeriesPage : Page
{
    private IptvSeriesRequest _request = null!;
    private XtreamSeries? _series;
    private int _season;

    public IptvSeriesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not IptvSeriesRequest request) return;
        if (_series is not null)
        {
            ShowSeason(_season); // back from the player: progress moved
            return;
        }
        _request = request;
        TitleText.Text = request.Series.Name;
        SourceEyebrow.Text = request.Playlist.Name.ToUpperInvariant();
        SetImage(Cover, request.Series.Logo);
        try
        {
            _series = await XtreamClient.SeriesInfoAsync(request.Playlist.Xtream!, request.Series.XtreamId!);
        }
        catch (Exception ex)
        {
            Loading.IsActive = false;
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Loading.IsActive = false;
        Show(_series);
    }

    private static void SetImage(Image image, string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) image.Source = new BitmapImage(uri);
    }

    private void Show(XtreamSeries series)
    {
        TitleText.Text = series.Name;
        SetImage(Cover, series.Cover ?? _request.Series.Logo);
        SetImage(Backdrop, series.Backdrop ?? series.Cover);
        Plot.Text = series.Plot ?? "";
        AddFact("SORTIE", series.Released);
        AddFact("GENRES", series.Genre);
        AddFact("NOTE", series.Rating is null ? null : $"★ {series.Rating}");

        SeasonChips.Children.Clear();
        foreach (var season in series.Episodes.Select(e => e.Season).Distinct())
        {
            var chip = new ToggleButton { Content = $"SAISON {season}", Tag = season, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) => ShowSeason(season);
            SeasonChips.Children.Add(chip);
        }
        // Open on the season of the episode last watched.
        var last = series.Episodes.Where(e => WatchStore.Entry("iptv", $"xep-{e.Id}") is { IsStarted: true })
            .OrderByDescending(e => WatchStore.Entry("iptv", $"xep-{e.Id}")!.LastWatchedAt).FirstOrDefault();
        ShowSeason(last?.Season ?? series.Episodes.FirstOrDefault()?.Season ?? 1);
        UpdatePlayButton(last);
    }

    private void AddFact(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["VeltrioEyebrow"], CharacterSpacing = 180 });
        block.Children.Add(new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 3 });
        Facts.Children.Add(block);
    }

    private void ShowSeason(int season)
    {
        if (_series is null) return;
        _season = season;
        foreach (var chip in SeasonChips.Children.OfType<ToggleButton>()) chip.IsChecked = (int)chip.Tag == season;
        var episodes = _series.Episodes.Where(e => e.Season == season).ToList();
        foreach (var e in episodes)
        {
            var entry = WatchStore.Entry("iptv", $"xep-{e.Id}");
            e.Progress = entry is { Duration: > 0 } w ? w.ProgressFraction * 100 : 0;
        }
        EpisodeList.ItemsSource = null;
        EpisodeList.ItemsSource = episodes;
        EpisodeCount.Text = $"{episodes.Count} · {_series.Episodes.Count} AU TOTAL";
    }

    private XtreamEpisode? _next;

    private void UpdatePlayButton(XtreamEpisode? last)
    {
        _next = last ?? _series?.Episodes.FirstOrDefault();
        PlayButton.IsEnabled = _next is not null;
        PlayLabel.Text = _next is null ? "AUCUN ÉPISODE"
            : last is not null ? $"REPRENDRE S{_next.Season}E{_next.Number}" : $"LIRE S{_next.Season}E{_next.Number}";
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_next is not null) Play(_next);
    }

    private void OnEpisodeClick(object sender, ItemClickEventArgs e) => Play((XtreamEpisode)e.ClickedItem);

    private void Play(XtreamEpisode episode) => Navigator.Play(new PlayRequest
    {
        Title = _series?.Name ?? _request.Series.Name,
        Subtitle = $"{episode.Code} — {episode.Title}",
        Url = episode.StreamUrl,
        ContentType = "iptv",
        ContentId = $"xep-{episode.Id}",
        Poster = episode.Image ?? _series?.Cover,
    });

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();
}
