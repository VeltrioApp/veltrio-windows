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
    private const string CachedFilter = "CACHE";

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
        // The list items mark right-clicks handled; listen anyway.
        StreamList.AddHandler(RightTappedEvent, new Microsoft.UI.Xaml.Input.RightTappedEventHandler(OnStreamRightTapped), handledEventsToo: true);
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
        _ = LocalizeDetailsAsync(d.Id, d.Type);

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
            AddPersonFact(Credits, AppPrefs.Tr("RÉALISATION", "DIRECTED"), d.Director.Take(3));
            AddFact(Credits, AppPrefs.Tr("SCÉNARIO", "WRITTEN"), d.Writer.Count > 0 ? string.Join(", ", d.Writer.Take(3)) : null);
            AddPersonFact(Credits, AppPrefs.Tr("DISTRIBUTION", "CAST"), d.Cast.Take(8));
        }

        if (!string.IsNullOrWhiteSpace(d.TrailerUrl))
        {
            TrailerLabel.Text = AppPrefs.Tr("BANDE-ANNONCE", "TRAILER");
            TrailerButton.Visibility = Visibility.Visible;
        }

        if (d.Recommendations.Count > 0)
        {
            RecommendationsTitle.Text = AppPrefs.Tr("TITRES SIMILAIRES", "MORE LIKE THIS");
            RecommendationsRow.Children.Clear();
            foreach (var rec in d.Recommendations)
            {
                var card = new StackPanel { Width = 110, Spacing = 6 };
                var imgBox = new Grid
                {
                    Height = 165,
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioSurfaceBrush"],
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioBorderBrush"],
                    BorderThickness = new Thickness(1),
                };
                if (Uri.TryCreate(rec.PosterUrl, UriKind.Absolute, out var pUri))
                    imgBox.Children.Add(new Image { Source = new BitmapImage(pUri), Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
                card.Children.Add(imgBox);
                card.Children.Add(new TextBlock
                {
                    Text = rec.Title,
                    FontSize = 11,
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
                var target = rec;
                btn.Click += (_, _) => Navigator.OpenTitle(target);
                RecommendationsRow.Children.Add(btn);
            }
            RecommendationsSection.Visibility = Visibility.Visible;
        }
    }

    private void AddPersonFact(StackPanel host, string label, IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (list.Count == 0) return;
        var row = new StackPanel { Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            CharacterSpacing = 120,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioTextTertiaryBrush"],
        });
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var person in list)
        {
            var name = person;
            var btn = new Button
            {
                Content = name,
                Style = (Style)Application.Current.Resources["VeltrioGhostButton"],
                Padding = new Thickness(10, 4, 10, 4),
            };
            btn.Click += async (_, _) => await ShowPersonFilmographyAsync(name);
            chips.Children.Add(btn);
        }
        scroller.Content = chips;
        row.Children.Add(scroller);
        host.Children.Add(row);
    }

    private async Task ShowPersonFilmographyAsync(string personName)
    {
        var content = new StackPanel { Spacing = 12, MinWidth = 520 };
        var status = new TextBlock
        {
            Text = AppPrefs.Tr("Chargement de la filmographie…", "Loading filmography…"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioTextSecondaryBrush"],
        };
        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = rowPanel,
        };
        content.Children.Add(status);
        content.Children.Add(scroll);

        var dialog = new ContentDialog
        {
            Title = $"{AppPrefs.Tr("Filmographie", "Filmography")} — {personName}",
            CloseButtonText = AppPrefs.Tr("Fermer", "Close"),
            Content = content,
            XamlRoot = XamlRoot,
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var items = await TmdbService.FetchPersonCreditsAsync(personName);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (items.Count == 0)
                    {
                        status.Text = AppPrefs.Tr("Aucun titre trouvé sur TMDB.", "No titles found on TMDB.");
                        return;
                    }
                    status.Visibility = Visibility.Collapsed;
                    foreach (var item in items)
                    {
                        var target = item;
                        var card = new StackPanel { Width = 110, Spacing = 6 };
                        var imgBox = new Grid
                        {
                            Height = 165,
                            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioSurfaceBrush"],
                            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioBorderBrush"],
                            BorderThickness = new Thickness(1),
                        };
                        if (Uri.TryCreate(target.PosterUrl, UriKind.Absolute, out var pUri))
                            imgBox.Children.Add(new Image { Source = new BitmapImage(pUri), Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
                        card.Children.Add(imgBox);
                        card.Children.Add(new TextBlock
                        {
                            Text = target.Title,
                            FontSize = 11,
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
                        btn.Click += (_, _) =>
                        {
                            dialog.Hide();
                            Navigator.OpenTitle(target);
                        };
                        rowPanel.Children.Add(btn);
                    }
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => status.Text = ex.Message);
            }
        });

        await dialog.ShowAsync();
    }

    private async void OnOpenTrailer(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_details?.TrailerUrl, UriKind.Absolute, out var uri))
            await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async Task LocalizeDetailsAsync(string id, string type)
    {
        var loc = await TmdbService.LocalizeAsync(id, type);
        if (loc is null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (loc.Title is not null) TitleText.Text = loc.Title;
            if (loc.Overview is not null)
            {
                Description.Text = loc.Overview;
                Description.Visibility = Visibility.Visible;
            }
        });
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
        // "⚡ En cache" only means something with a debrid service connected.
        var filters = DebridService.AnyConnected ? Qualities.Take(1).Append(CachedFilter).Concat(Qualities.Skip(1)) : Qualities;
        foreach (var q in filters)
        {
            var chip = new ToggleButton { Content = q == CachedFilter ? "⚡ EN CACHE" : q.ToUpperInvariant(), IsChecked = q == _quality, Style = (Style)Application.Current.Resources["VeltrioChip"] };
            chip.Click += (_, _) =>
            {
                _quality = q;
                foreach (var other in QualityChips.Children.OfType<ToggleButton>()) other.IsChecked = other == chip;
                ApplyFilters();
            };
            QualityChips.Children.Add(chip);
        }
    }

    /// <summary>The video id the stream list is for (film, or episode).</summary>
    private string _streamsId = "";

    private async Task LoadStreamsAsync(string type, string id)
    {
        var request = ++_streamRequest;
        _streamsId = id;
        _streams = Array.Empty<StreamItem>();
        StreamList.ItemsSource = null;
        StreamsEmpty.Visibility = Visibility.Collapsed;
        StreamsLoading.IsActive = true;
        StreamCount.Text = "";
        UpdatePlayButton(loading: true);

        var direct = DirectAsync(id);
        var streams = await CoreService.FetchStreamsAsync(type, id);
        // A slower answer for a previous episode mustn't replace this one.
        if (request != _streamRequest) return;

        // A copy downloaded for offline goes before everything.
        _streams = DownloadManager.LocalFile(id) is not null
            ? streams.Prepend(StreamItem.Local(DownloadManager.Item(id)!)).ToList()
            : streams;
        StreamsLoading.IsActive = false;
        RefreshSourceFilter();
        ApplyFilters();
        UpdatePlayButton();

        // Two lookups run alongside: the user's own copies (media servers,
        // IPTV provider), listed first once found, and what the debrid
        // services have cached (⚡ rows next). Neither holds the list back.
        var hashes = streams.Select(s => s.InfoHash).OfType<string>().ToList();
        var debrid = DebridService.AnyConnected && hashes.Count > 0 ? DebridService.PrefetchAsync(hashes) : null;
        if (!direct.IsCompleted) StreamCount.Text += " · RECHERCHE DANS VOS SERVEURS…";
        var copies = await direct;
        if (request != _streamRequest) return;
        if (copies.Count > 0)
        {
            // After the offline copy, before the addons.
            var local = _streams.TakeWhile(st => st.LocalPath is not null).ToList();
            _streams = local.Concat(copies).Concat(_streams.Skip(local.Count)).ToList();
            RefreshSourceFilter();
        }
        ApplyFilters();
        UpdatePlayButton();

        if (debrid is null) return;
        if (!debrid.IsCompleted) StreamCount.Text += " · VÉRIFICATION DEBRID…";
        await debrid;
        if (request != _streamRequest) return;
        _streams = _streams.OrderByDescending(s => s.Origin is not null).ThenByDescending(s => s.IsCached).ThenByDescending(s => s.Score).ToList();
        ApplyFilters();
        UpdatePlayButton();
    }

    private void RefreshSourceFilter()
    {
        var selected = SourceFilter.SelectedItem as string;
        SourceFilter.SelectionChanged -= OnFilterChanged;
        SourceFilter.Items.Clear();
        SourceFilter.Items.Add("Toutes les sources");
        foreach (var name in _streams.Select(s => s.AddonName).Distinct()) SourceFilter.Items.Add(name);
        SourceFilter.SelectedItem = selected is not null && SourceFilter.Items.Contains(selected) ? selected : SourceFilter.Items[0];
        SourceFilter.SelectionChanged += OnFilterChanged;
    }

    /// <summary>Media server and IPTV copies for a video id: tt123 for a
    /// film, tt123:season:episode for an episode.</summary>
    private async Task<IReadOnlyList<StreamItem>> DirectAsync(string id)
    {
        var parts = id.Split(':');
        if (!parts[0].StartsWith("tt") || (!MediaServers.Connected.Any() && !IptvStore.Playlists.Any(p => p.IsXtream)))
            return Array.Empty<StreamItem>();
        int? season = parts.Length == 3 && int.TryParse(parts[1], out var s) ? s : null;
        int? episode = parts.Length == 3 && int.TryParse(parts[2], out var e) ? e : null;
        int? year = _details?.Year is { Length: >= 4 } y && int.TryParse(y[..4], out var parsed) ? parsed : null;
        try
        {
            return await DirectSources.FindAsync(parts[0], _details?.Name ?? _request.Name, year, season, episode);
        }
        catch (Exception ex)
        {
            Player.PlayerLog.Write($"[direct] {ex.Message}");
            return Array.Empty<StreamItem>();
        }
    }

    private IEnumerable<StreamItem> Visible()
    {
        var source = SourceFilter.SelectedIndex > 0 ? SourceFilter.SelectedItem as string : null;
        return _streams.Where(s => (source is null || s.AddonName == source)
            && (_quality == "TOUT" || (_quality == CachedFilter ? s.IsCached : s.Quality == _quality)));
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

    /// <summary>Right-click: save this stream for offline, or manage the copy.</summary>
    private void OnStreamRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not StreamItem stream) return;
        var id = _streamsId;
        var menu = new MenuFlyout();
        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        switch (DownloadManager.Item(id))
        {
            case { Status: DownloadStatus.Completed }:
                Add("Supprimer la copie hors ligne", () => { DownloadManager.Remove(id); _ = LoadStreamsAsync(_request.Type, id); });
                break;
            case { Status: DownloadStatus.Downloading } running:
                Add($"Téléchargement en cours ({running.Progress:P0}) : mettre en pause", () => DownloadManager.Pause(id));
                break;
            default:
                if (stream.LocalPath is not null || CoreService.ExternalUrl(stream) is not null) return;
                Add("Télécharger pour regarder hors ligne", () => _ = DownloadManager.StartAsync(id, Request(stream, _episode)));
                break;
        }
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
    }

    private async void Play(StreamItem stream)
    {
        // Trailers and provider pages open in the browser.
        if (CoreService.ExternalUrl(stream) is { } external)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(external));
            return;
        }
        Navigator.Play(Request(stream, _episode));
    }

    /// <summary>The player request for a stream of the title (or of one
    /// episode), chained to the following episode for "next episode".</summary>
    private PlayRequest Request(StreamItem stream, EpisodeItem? episode)
    {
        var next = episode is null ? null : NextEpisode(episode);
        return new PlayRequest
        {
            Title = _details?.Name ?? _request.Name,
            Subtitle = episode is null ? stream.Name : $"{episode.Code} — {episode.Title}",
            Url = stream.LocalPath,
            Stream = stream,
            ContentType = _request.Type,
            ContentId = _details?.Id ?? _request.Id,
            VideoId = episode?.Id,
            Season = episode?.Season,
            Episode = episode?.Episode,
            Poster = _details?.Poster ?? _request.Poster,
            Background = _details?.Background ?? _request.Background,
            Addon = _details?.Addon ?? _request.Addon,
            NextLabel = next is null ? null : $"{next.Code.Replace(" · ", "")} · {next.Title}",
            Next = next is null ? null : () => ResolveNextAsync(stream, next),
        };
    }

    /// <summary>The episode after this one (the next season's first after
    /// a season's last); specials (season 0) aren't chained.</summary>
    private EpisodeItem? NextEpisode(EpisodeItem episode)
    {
        var list = _details!.Episodes.Where(e => e.Season is > 0).ToList();
        var index = list.FindIndex(e => e.Id == episode.Id);
        return index >= 0 && index + 1 < list.Count ? list[index + 1] : null;
    }

    /// <summary>The next episode's stream: same binge group (same release)
    /// as the one playing, else the same addon and quality, else the best.</summary>
    private async Task<PlayRequest?> ResolveNextAsync(StreamItem current, EpisodeItem next)
    {
        var streams = await CoreService.FetchStreamsAsync(_request.Type, next.Id);
        if (streams.Count == 0) return null;
        var playable = streams.Where(s => CoreService.ExternalUrl(s) is null).ToList();
        var pick = (current.BingeGroup is { } group ? playable.FirstOrDefault(s => s.BingeGroup == group) : null)
                   ?? playable.FirstOrDefault(s => s.AddonName == current.AddonName && s.Quality == current.Quality)
                   ?? playable.FirstOrDefault();
        return pick is null ? null : Request(pick, next);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();
}
