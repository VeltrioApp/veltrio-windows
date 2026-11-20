using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

public sealed record MediaServerSeriesRequest(MediaServer Server, MediaLibraryItem Series);

/// <summary>A media server's series that has no IMDB id, so no title page:
/// its episodes straight from the server, with resume per episode.</summary>
public sealed partial class MediaServerSeriesPage : Page
{
    private MediaServerSeriesRequest _request = null!;
    private IReadOnlyList<MediaLibraryEpisode>? _episodes;
    private int _season;
    private MediaLibraryEpisode? _next;

    public MediaServerSeriesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not MediaServerSeriesRequest request) return;
        _request = request;
        TitleText.Text = request.Series.Title;
        YearText.Text = request.Series.Year?.ToString() ?? "";
        SourceEyebrow.Text = request.Server.Name.ToUpperInvariant();
        if (Uri.TryCreate(request.Series.PosterUrl, UriKind.Absolute, out var poster)) Cover.Source = new BitmapImage(poster);
        _episodes = (await request.Server.EpisodesAsync(request.Series))
            .OrderBy(ep => ep.Season).ThenBy(ep => ep.Number).ToList();
        Loading.IsActive = false;
        if (_episodes.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            PlayLabel.Text = "AUCUN ÉPISODE";
            return;
        }
        foreach (var season in _episodes.Select(ep => ep.Season).Distinct())
        {
            var chip = new ToggleButton { Content = $"SAISON {season}", Tag = season, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) => ShowSeason(season);
            SeasonChips.Children.Add(chip);
        }
        // Open on the season of the episode last watched.
        var last = _episodes.Where(ep => Entry(ep) is { IsStarted: true })
            .OrderByDescending(ep => Entry(ep)!.LastWatchedAt).FirstOrDefault();
        ShowSeason(last?.Season ?? _episodes[0].Season);
        _next = last ?? _episodes[0];
        PlayButton.IsEnabled = true;
        PlayLabel.Text = last is not null ? $"REPRENDRE S{_next.Season}E{_next.Number}" : $"LIRE S{_next.Season}E{_next.Number}";
    }

    private string ContentId(MediaLibraryEpisode episode) => $"{_request.Server.Kind}-{episode.Id}".ToLowerInvariant();

    private WatchEntry? Entry(MediaLibraryEpisode episode) => WatchStore.Entry("server", ContentId(episode));

    private void ShowSeason(int season)
    {
        if (_episodes is null) return;
        _season = season;
        foreach (var chip in SeasonChips.Children.OfType<ToggleButton>()) chip.IsChecked = (int)chip.Tag == season;
        var episodes = _episodes.Where(ep => ep.Season == season).ToList();
        foreach (var ep in episodes) ep.Progress = Entry(ep) is { Duration: > 0 } w ? w.ProgressFraction * 100 : 0;
        EpisodeList.ItemsSource = null;
        EpisodeList.ItemsSource = episodes;
        EpisodeCount.Text = $"{episodes.Count} · {_episodes.Count} AU TOTAL";
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_next is not null) Play(_next);
    }

    private void OnEpisodeClick(object sender, ItemClickEventArgs e) => Play((MediaLibraryEpisode)e.ClickedItem);

    private void Play(MediaLibraryEpisode episode)
    {
        if (Request(episode) is { } request) Navigator.Play(request);
    }

    /// <summary>An episode's request, chained to the next one.</summary>
    private PlayRequest? Request(MediaLibraryEpisode episode)
    {
        if ((episode.StreamUrl ?? _request.Server.StreamUrl(episode.Id)) is not { } url) return null;
        var list = _episodes ?? Array.Empty<MediaLibraryEpisode>();
        var index = list.ToList().FindIndex(ep => ep.Id == episode.Id);
        var next = index >= 0 && index + 1 < list.Count ? list[index + 1] : null;
        return new PlayRequest
        {
            Title = _request.Series.Title,
            Subtitle = $"S{episode.Season}E{episode.Number} — {episode.Title}",
            Url = url,
            ContentType = "server",
            ContentId = ContentId(episode),
            Poster = episode.Image ?? _request.Series.PosterUrl,
            NextLabel = next is null ? null : $"S{next.Season}E{next.Number} · {next.Title}",
            Next = next is null ? null : () => Task.FromResult(Request(next)),
        };
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();
}
