using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>A title's page, laid out like macOS: info and actions on the
/// left; on the right the episodes of a series, then the streams of the
/// one picked (straight to the streams for a film).</summary>
public sealed partial class DetailPage : Page
{
    private static readonly string[] Qualities = { "TOUT", "4K", "1080p", "720p" };

    private DetailRequest _request = null!;
    private TitleDetails? _details;
    private EpisodeItem? _episode;
    private IReadOnlyList<StreamItem> _streams = Array.Empty<StreamItem>();
    private string _quality = "TOUT";
    private int _streamRequest;
    private bool _loaded;

    public DetailPage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => LeftColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * 0.44, 380, 560) + 96);
        BuildQualityChips();
    }

    private bool IsSeries => _details?.Episodes.Count > 0;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not DetailRequest request) return;
        if (_loaded)
        {
            // Back from the player: progress may have moved.
            UpdateSaveButton();
            if (IsSeries) ShowSeason(_episode?.Season ?? CurrentSeason);
            return;
        }
        _loaded = true;
        _request = request;
        TitleText.Text = request.Name;
        AddonEyebrow.Text = request.Addon.Name.ToUpperInvariant();
        TypeEyebrow.Text = "· " + CoreService.TypeLabel(request.Type).ToUpperInvariant();
        SetImage(PosterImage, request.Poster);
        SetImage(Backdrop, request.Background ?? request.Poster);
        UpdateSaveButton();

        try
        {
            _details = await CoreService.FetchDetailsAsync(request.Addon, request.Type, request.Id);
            ShowDetails(_details);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }

        if (IsSeries)
        {
            StreamsPane.Visibility = Visibility.Collapsed;
            EpisodesPane.Visibility = Visibility.Visible;
            // Resume where the user was, else the first season.
            var lastId = WatchStore.Entry(request.Type, request.Id)?.LastEpisodeId;
            _episode = _details!.Episodes.FirstOrDefault(ep => ep.Id == lastId);
            BuildSeasonChips();
            ShowSeason(_episode?.Season ?? CurrentSeason);
            UpdatePlayButton();
        }
        else
        {
            await LoadStreamsAsync(request.Type, _details?.Id ?? request.Id);
        }
    }

    // -------------------- info column --------------------

    private void ShowDetails(TitleDetails d)
    {
        TitleText.Text = d.Name;
        TypeEyebrow.Text = "· " + CoreService.TypeLabel(d.Type).ToUpperInvariant();
        SetImage(PosterImage, d.Poster);
        SetImage(Backdrop, d.Background ?? d.Poster);
        Description.Text = d.Description ?? "";
        Description.Visibility = string.IsNullOrWhiteSpace(d.Description) ? Visibility.Collapsed : Visibility.Visible;

        AddFact(Overview, "SORTIE", d.Year);
        AddFact(Overview, "DURÉE", d.Runtime);
        AddFact(Overview, "GENRES", d.Genres.Count > 0 ? string.Join(", ", d.Genres) : null);
        if (d.Rating is not null)
        {
            Overview.Children.Add(new TextBlock
            {
                Text = $"★ {d.Rating}  IMDb",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VeltrioMono"],
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioAccentBrush"],
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        if (d.Cast.Count > 0 || d.Director.Count > 0)
        {
            Credits.Visibility = Visibility.Visible;
            AddFact(Credits, "RÉALISATION", d.Director.Count > 0 ? string.Join(", ", d.Director.Take(3)) : null);
            AddFact(Credits, "SCÉNARIO", d.Writer.Count > 0 ? string.Join(", ", d.Writer.Take(3)) : null);
            AddFact(Credits, "DISTRIBUTION", d.Cast.Count > 0 ? string.Join(", ", d.Cast.Take(6)) : null);
        }
    }

    private static void AddFact(Panel panel, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["VeltrioEyebrow"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioTextQuaternaryBrush"],
            CharacterSpacing = 180,
        });
        block.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioTextPrimaryBrush"],
        });
        panel.Children.Add(block);
    }

    private static void SetImage(Image image, string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) image.Source = new BitmapImage(uri);
    }

    private void UpdateSaveButton()
    {
        var saved = WatchStore.IsInLibrary(_request.Type, _request.Id);
        SaveLabel.Text = saved ? "DANS LA COLLECTION" : "ENREGISTRER";
        SaveIcon.Glyph = saved ? "" : "";
        SaveButton.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[saved ? "VeltrioAccentBrush" : "VeltrioTextPrimaryBrush"];
    }

    private void OnToggleSave(object sender, RoutedEventArgs e)
    {
        WatchStore.ToggleLibrary(_request.Type, _request.Id, _details?.Name ?? _request.Name,
            _details?.Poster ?? _request.Poster, _details?.Background ?? _request.Background, _details?.Addon ?? _request.Addon);
        UpdateSaveButton();
    }

    // -------------------- episodes --------------------

    private int? CurrentSeason => _details!.Episodes.Select(ep => ep.Season).FirstOrDefault(s => s is > 0)
                                  ?? _details.Episodes.FirstOrDefault()?.Season;

    private void BuildSeasonChips()
    {
        SeasonChips.Children.Clear();
        foreach (var season in _details!.Episodes.Select(ep => ep.Season).Distinct())
        {
            var chip = new ToggleButton
            {
                Content = season is null or 0 ? "BONUS" : $"SAISON {season}",
                Tag = season,
                Style = (Style)Application.Current.Resources["VeltrioChip"],
            };
            chip.Click += (_, _) => ShowSeason(season);
            SeasonChips.Children.Add(chip);
        }
    }

    private void ShowSeason(int? season)
    {
        foreach (var chip in SeasonChips.Children.OfType<ToggleButton>())
            chip.IsChecked = Equals(chip.Tag, season);
        var episodes = _details!.Episodes.Where(ep => ep.Season == season).ToList();
        foreach (var ep in episodes)
        {
            var progress = WatchStore.Episode(_request.Type, _request.Id, ep.Id);
            ep.Progress = progress is { Duration: > 0 } p ? Math.Clamp(p.Position / p.Duration * 100, 0, 100) : 0;
            ep.Watched = progress?.Watched == true;
        }
        EpisodeList.ItemsSource = null;
        EpisodeList.ItemsSource = episodes;
        EpisodeCount.Text = $"{episodes.Count} · {_details.Episodes.Count} AU TOTAL";
    }

    private async void OnEpisodeClick(object sender, ItemClickEventArgs e)
    {
        _episode = (EpisodeItem)e.ClickedItem;
        EpisodesPane.Visibility = Visibility.Collapsed;
        StreamsPane.Visibility = Visibility.Visible;
        BackToEpisodes.Visibility = EpisodeHeading.Visibility = Visibility.Visible;
        EpisodeHeading.Text = $"{_episode.Code} — {_episode.Title}";
        UpdatePlayButton();
        await LoadStreamsAsync(_request.Type, _episode.Id);
    }

    private void OnBackToEpisodes(object sender, RoutedEventArgs e)
    {
        StreamsPane.Visibility = Visibility.Collapsed;
        EpisodesPane.Visibility = Visibility.Visible;
        ShowSeason(_episode?.Season ?? CurrentSeason);
    }

    // -------------------- streams --------------------

    private void BuildQualityChips()
    {
        foreach (var q in Qualities)
        {
            var chip = new ToggleButton { Content = q.ToUpperInvariant(), IsChecked = q == _quality, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) =>
            {
                _quality = q;
                foreach (var other in QualityChips.Children.OfType<ToggleButton>()) other.IsChecked = other == chip;
                ApplyFilters();
            };
            QualityChips.Children.Add(chip);
        }
    }

    private async Task LoadStreamsAsync(string type, string id)
    {
        var request = ++_streamRequest;
        _streams = Array.Empty<StreamItem>();
        StreamList.ItemsSource = null;
        StreamsEmpty.Visibility = Visibility.Collapsed;
        StreamsLoading.IsActive = true;
        StreamCount.Text = "";
        UpdatePlayButton(loading: true);

        var streams = await CoreService.FetchStreamsAsync(type, id);
        // A slower answer for a previous episode mustn't replace this one.
        if (request != _streamRequest) return;

        _streams = streams;
        StreamsLoading.IsActive = false;
        SourceFilter.SelectionChanged -= OnFilterChanged;
        SourceFilter.Items.Clear();
        SourceFilter.Items.Add("Toutes les sources");
        foreach (var name in streams.Select(s => s.AddonName).Distinct()) SourceFilter.Items.Add(name);
        SourceFilter.SelectedIndex = 0;
        SourceFilter.SelectionChanged += OnFilterChanged;
        ApplyFilters();
        UpdatePlayButton();
    }

    private IEnumerable<StreamItem> Visible()
    {
        var source = SourceFilter.SelectedIndex > 0 ? SourceFilter.SelectedItem as string : null;
        return _streams.Where(s => (source is null || s.AddonName == source) && (_quality == "TOUT" || s.Quality == _quality));
    }

    private void ApplyFilters()
    {
        var visible = Visible().ToList();
        StreamList.ItemsSource = visible;
        StreamCount.Text = $"{visible.Count} SUR {_streams.Count}";
        StreamsEmpty.Visibility = visible.Count == 0 && !StreamsLoading.IsActive ? Visibility.Visible : Visibility.Collapsed;
        StreamsEmpty.Text = _streams.Count == 0
            ? CoreService.EnabledAddons.Any(a => a.Serves("stream", _request.Type))
                ? "Aucune source n'a répondu avec un flux pour ce titre."
                : "Aucune source installée ne fournit de flux. Ajoutez-en une dans Sources."
            : "Aucun flux ne correspond à ces filtres.";
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    private void UpdatePlayButton(bool loading = false)
    {
        var needsEpisode = IsSeries && StreamsPane.Visibility != Visibility.Visible;
        PlayLoading.IsActive = loading;
        PlayLoading.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        PlayIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        if (loading)
        {
            PlayLabel.Text = "CHARGEMENT DES FLUX";
            PlayButton.IsEnabled = false;
        }
        else if (needsEpisode)
        {
            // Series: the button picks up where the user left off.
            var target = _episode ?? _details!.Episodes.FirstOrDefault(ep => ep.Season == CurrentSeason);
            PlayLabel.Text = target is null ? "AUCUN ÉPISODE" : $"LIRE {target.Code.Replace(" · ", "")}";
            PlayButton.IsEnabled = target is not null;
        }
        else
        {
            var any = Visible().Any();
            PlayLabel.Text = !any ? "AUCUN FLUX"
                : _episode is not null ? $"LIRE {_episode.Code.Replace(" · ", "")}"
                : WatchStore.ResumePosition(_request.Type, _request.Id) > 0 ? "REPRENDRE" : "REGARDER";
            PlayButton.IsEnabled = any;
        }
    }

    private void OnPlayBest(object sender, RoutedEventArgs e)
    {
        if (IsSeries && StreamsPane.Visibility != Visibility.Visible)
        {
            var target = _episode ?? _details!.Episodes.FirstOrDefault(ep => ep.Season == CurrentSeason);
            if (target is not null) EpisodeList_Open(target);
            return;
        }
        if (Visible().FirstOrDefault() is { } best) Play(best);
    }

    private async void EpisodeList_Open(EpisodeItem episode)
    {
        _episode = episode;
        EpisodesPane.Visibility = Visibility.Collapsed;
        StreamsPane.Visibility = Visibility.Visible;
        BackToEpisodes.Visibility = EpisodeHeading.Visibility = Visibility.Visible;
        EpisodeHeading.Text = $"{episode.Code} — {episode.Title}";
        await LoadStreamsAsync(_request.Type, episode.Id);
    }

    private void OnStreamClick(object sender, ItemClickEventArgs e) => Play((StreamItem)e.ClickedItem);

    private async void Play(StreamItem stream)
    {
        // Trailers and provider pages open in the browser.
        if (CoreService.ExternalUrl(stream) is { } external)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(external));
            return;
        }
        var name = _details?.Name ?? _request.Name;
        Navigator.Play(new PlayRequest
        {
            Title = name,
            Subtitle = _episode is null ? stream.Name : $"{_episode.Code} — {_episode.Title}",
            Stream = stream,
            ContentType = _request.Type,
            ContentId = _request.Id,
            VideoId = _episode?.Id,
            Season = _episode?.Season,
            Episode = _episode?.Episode,
            Poster = _details?.Poster ?? _request.Poster,
            Background = _details?.Background ?? _request.Background,
            Addon = _details?.Addon ?? _request.Addon,
        });
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();
}
