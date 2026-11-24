using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Player;
using Veltrio.Services;

namespace Veltrio.Pages;

/// <summary>Full-window player on libmpv, laid out like the macOS one:
/// Back and the glass buttons (tracks, speed, framing, presets, cast,
/// replay, info, full screen) along the top, the seek bar, transport and
/// volume at the bottom, side panels on the right. Resolves the stream
/// (debrid or the core's engine for torrents), resumes where the user
/// stopped, reports progress every 10 s and on close; skip intro and next
/// episode; for live TV now/next, zapping and replay.</summary>
public sealed partial class PlayerPage : Page
{
    private static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 };

    private PlayRequest _request = null!;
    private Scrobbler _scrobbler = null!;
    private string? _url;
    private MpvPlayer? _player;
    private VideoSurface? _surface;
    private DispatcherQueueTimer? _progressTimer;
    private DispatcherQueueTimer? _hideTimer;
    private bool _closing;
    private bool _loaded;

    public PlayerPage()
    {
        InitializeComponent();
        AddAccelerator(Windows.System.VirtualKey.Escape, () =>
        {
            var kind = Navigator.Window.AppWindow?.Presenter?.Kind;
            if (kind == Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay)
                Navigator.Window.AppWindow?.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            else if (Navigator.Window.IsFullScreen)
                ToggleFullScreen();
            else
                Close();
        });
        AddAccelerator(Windows.System.VirtualKey.F, ToggleFullScreen);
        AddAccelerator(Windows.System.VirtualKey.F11, ToggleFullScreen);
        AddAccelerator(Windows.System.VirtualKey.Space, () => _player?.TogglePause());
        AddAccelerator(Windows.System.VirtualKey.Left, () => _player?.SeekRelative(-10));
        AddAccelerator(Windows.System.VirtualKey.Right, () => _player?.SeekRelative(10));
        AddAccelerator(Windows.System.VirtualKey.Up, () => _player?.SetVolume((_player?.Volume ?? 100) + 5));
        AddAccelerator(Windows.System.VirtualKey.Down, () => _player?.SetVolume((_player?.Volume ?? 100) - 5));
        AddAccelerator(Windows.System.VirtualKey.M, () => _player?.ToggleMute());
        AddAccelerator(Windows.System.VirtualKey.PageUp, () => ZapBy(-1));
        AddAccelerator(Windows.System.VirtualKey.PageDown, () => ZapBy(1));
        AddAccelerator(Windows.System.VirtualKey.I, ToggleInfo);
        AddAccelerator(Windows.System.VirtualKey.S, SkipIntro);
        AddAccelerator(Windows.System.VirtualKey.N, () => _ = PlayNextAsync());
        AddAccelerator(Windows.System.VirtualKey.Z, () => _player?.SetSubDelay(_player.SubDelay - 0.1));
        AddAccelerator(Windows.System.VirtualKey.X, () => _player?.SetSubDelay(_player.SubDelay + 0.1));
        AddAccelerator(Windows.System.VirtualKey.A, CycleAspect);
        AddAccelerator(Windows.System.VirtualKey.L, () => { if (ChannelsButton.Visibility == Visibility.Visible) ToggleChannels(); });
        AddAccelerator(Windows.System.VirtualKey.G, ToggleEpgCards);
        // [ and ]: speed by 0.1, as on macOS (the keys of a US layout).
        AddAccelerator((Windows.System.VirtualKey)219, () => _player?.SetSpeed(Math.Max(0.25, (_player?.Speed ?? 1) - 0.1)));
        AddAccelerator((Windows.System.VirtualKey)221, () => _player?.SetSpeed(Math.Min(3, (_player?.Speed ?? 1) + 0.1)));
        Scrubber.Hovered += OnScrubHover;
        Scrubber.Dragging += value => PositionText.Text = TimeLabelConverter.Format(value);
        Scrubber.Committed += value => { _player?.SeekTo(value); ShowChrome(); };
        VolumeSlider.Maximum = 100;
        VolumeSlider.Value = 100;
        VolumeSlider.Dragging += value => _player?.SetVolume(value);
        VolumeSlider.Committed += value => _player?.SetVolume(value);
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        BuildSpeedMenu();
        // Keyboard focus on the page, not on Back: Space must pause, not "click".
        Loaded += (_, _) => Focus(FocusState.Programmatic);
    }

    private void AddAccelerator(Windows.System.VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, e) =>
        {
            if (key != Windows.System.VirtualKey.Escape &&
                XamlRoot is not null &&
                FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox)
                return;
            action();
            e.Handled = true;
            ShowChrome();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not PlayRequest request) return;
        _request = request;
        _scrobbler = new Scrobbler(request);
        ChromecastService.Changed += OnCastChanged;
        ChromecastService.StartDiscovery();
        UpdateCastBanner();
        TitleText.Text = request.Title;
        // Zapping keeps full screen: the icon says how to leave it.
        FullScreenIcon.Glyph = Navigator.Window.IsFullScreen ? "\uE73F" : "\uE740";
        SubtitleText.Text = request.Subtitle ?? "";
        var live = request.IsLive ? Visibility.Visible : Visibility.Collapsed;
        var notLive = request.IsLive ? Visibility.Collapsed : Visibility.Visible;
        LiveBadge.Visibility = LiveLabel.Visibility = live;
        ScrubRow.Visibility = BackButton.Visibility = ForwardButton.Visibility = TimeLabel.Visibility = SubtitleButton.Visibility = notLive;
        if (request.IsLive && Uri.TryCreate(request.Channel?.Logo, UriKind.Absolute, out var logo))
        {
            ChannelLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(logo);
            ChannelLogo.Visibility = Visibility.Visible;
        }
        if (request.IsLive && request.Channel is not null) _ = SetUpLiveAsync(request.Channel, request.Playlist);

        PlayerLog.Reset();
        try
        {
            _player = new MpvPlayer(DispatcherQueue);
            _player.Changed += OnPlayerChanged;
            _player.FileLoaded += OnFileLoaded;
            _player.Ended += OnEnded;
            _surface = new VideoSurface(Video, _player);
            var prefs = PlayerPrefs.Current;
            _player.ApplyProfile(prefs.Profile);
            if (prefs.ToneMapping != ToneMapping.Auto) _player.SetToneMapping(prefs.ToneMapping);
            if (prefs.Passthrough || prefs.Profile == PlaybackProfile.Audiophile) _player.SetPassthrough(true);
        }
        catch (Exception ex)
        {
            ShowError($"Le lecteur n'a pas pu démarrer. {ex.Message}", retry: false);
            return;
        }

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => HideChrome();

        NextButton.Visibility = request.Next is null ? Visibility.Collapsed : Visibility.Visible;
        if (request.NextLabel is { } next) ToolTipService.SetToolTip(NextButton, $"Épisode suivant (N) : {next}");
        if (request.ContentType is "movie" or "series") _ = FetchAddonSubtitlesAsync(request.ContentType, request.VideoId ?? request.ContentId!);
        if (request.ContentId is { } cid && cid.StartsWith("tt", StringComparison.Ordinal) && request.Season is { } s && request.Episode is { } ep)
            _ = FetchIntroWindowAsync(cid, s, ep);

        if (!request.IsLive)
        {
            _progressTimer = DispatcherQueue.CreateTimer();
            _progressTimer.Interval = TimeSpan.FromSeconds(10);
            _progressTimer.Tick += (_, _) => ReportProgress();
            _progressTimer.Start();
        }

        await OpenAsync();
    }

    private async Task OpenAsync()
    {
        Status.Visibility = Visibility.Visible;
        Busy.IsActive = true;
        Busy.Visibility = Visibility.Visible;
        ErrorIcon.Visibility = ErrorActions.Visibility = Visibility.Collapsed;
        StatusText.FontSize = 13;
        StatusDetail.Text = _request.Title;
        try
        {
            if (_url is null)
            {
                StatusText.Text = _request.IsLive ? "Connexion au direct…"
                    : _request.Stream?.InfoHash is null ? "Chargement du flux…"
                    : DebridService.AnyConnected ? "Résolution via le service debrid…" : "Préparation du torrent — connexion aux pairs…";
                _url = _request.Url ?? await CoreService.ResolveAsync(_request.Stream!, _request.Season, _request.Episode);
            }
        }
        catch (Exception ex)
        {
            ShowError(CoreService.Humanize(ex), retry: true);
            return;
        }
        if (_closing || _player is null) return;

        try
        {
            await _surface!.Ready;
        }
        catch (Exception ex)
        {
            ShowError($"La vidéo n'a pas pu démarrer : {ex.Message}", retry: false);
            return;
        }
        if (_closing) return;

        var resume = _request.ContentType is { } type && _request.ContentId is { } id
            ? WatchStore.ResumePosition(type, id, _request.VideoId)
            : 0;
        _player.Load(_url, resume);
    }

    // -------------------- live TV --------------------

    private List<ChannelRow> _zapRows = new();

    /// <summary>NOW / NEXT cards, the playlist's channels to zap through
    /// and, for Xtream channels with an archive, replay.</summary>
    private async Task SetUpLiveAsync(ChannelItem channel, PlaylistRef? playlist)
    {
        ChannelsButton.Visibility = playlist is null ? Visibility.Collapsed : Visibility.Visible;
        var replay = channel.HasCatchup && playlist?.IsXtream == true;
        ReplayButton.Visibility = replay ? Visibility.Visible : Visibility.Collapsed;
        if (replay) _ = LoadReplayAsync();
        await ShowEpgCardsAsync(channel, playlist, autoHide: true);

        if (playlist is null) return;
        try
        {
            var all = await IptvStore.ItemsAsync(playlist);
            var now = DateTime.UtcNow;
            _zapRows = all.Select(c => new ChannelRow(c) { IsCurrent = c.Id == channel.Id }).ToList();
            foreach (var row in _zapRows) row.Refresh(now);
            // Sections, opened on the channel's own.
            // The card at the top: the bouquet and the channel on screen.
            CurrentChannelGroup.Text = (channel.Group.Length > 0 ? $"EN COURS · {channel.Group}" : "EN COURS").ToUpperInvariant();
            CurrentChannelName.Text = channel.NumberLabel.Length > 0 ? $"{channel.NumberLabel}  {channel.Name}" : channel.Name;
            if (Uri.TryCreate(channel.Logo, UriKind.Absolute, out var logo))
                CurrentChannelLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(logo);
            _filteringChannels = true;
            ChannelGroups.Items.Clear();
            ChannelGroups.Items.Add("Toutes les chaînes");
            foreach (var group in all.Select(c => c.Group).Where(g => g.Length > 0).Distinct().OrderBy(g => g)) ChannelGroups.Items.Add(group);
            ChannelGroups.SelectedItem = channel.Group.Length > 0 && ChannelGroups.Items.Contains(channel.Group) ? channel.Group : ChannelGroups.Items[0];
            _filteringChannels = false;
            FilterChannels();
        }
        catch
        {
            ChannelsButton.Visibility = Visibility.Collapsed;
        }
    }

    private bool _filteringChannels;

    /// <summary>Back to the bouquet of the channel on screen, scrolled to it.</summary>
    private void OnShowCurrentChannel(object sender, RoutedEventArgs e)
    {
        if (_request.Channel is not { } channel) return;
        _filteringChannels = true;
        ChannelSearch.Text = "";
        ChannelGroups.SelectedItem = channel.Group.Length > 0 && ChannelGroups.Items.Contains(channel.Group) ? channel.Group : ChannelGroups.Items[0];
        _filteringChannels = false;
        FilterChannels();
        if (_zapRows.FirstOrDefault(r => r.IsCurrent) is { } current) ZapList.ScrollIntoView(current, ScrollIntoViewAlignment.Leading);
    }

    private void OnChannelFilter(object sender, object e)
    {
        if (!_filteringChannels) FilterChannels();
    }

    private void FilterChannels()
    {
        var group = ChannelGroups.SelectedIndex > 0 ? ChannelGroups.SelectedItem as string : null;
        var needle = ChannelSearch.Text.Trim();
        ZapList.ItemsSource = _zapRows
            .Where(r => group is null || r.Channel.Group == group)
            .Where(r => needle.Length == 0 || r.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
    }

    private bool _epgVisible;
    private DispatcherQueueTimer? _epgCardsTimer;

    /// <summary>G: the NOW / NEXT cards on or off.</summary>
    private void ToggleEpgCards()
    {
        if (!_request.IsLive || _request.Channel is not { } channel) return;
        if (_epgVisible)
        {
            _epgCardsTimer?.Stop();
            EpgCards.Visibility = Visibility.Collapsed;
            _epgVisible = false;
        }
        else
        {
            _ = ShowEpgCardsAsync(channel, _request.Playlist, autoHide: true);
        }
    }

    /// <summary>NOW and NEXT over the picture, bottom left, as the macOS
    /// player shows them on tune-in; they fade after a few seconds.</summary>
    private async Task ShowEpgCardsAsync(ChannelItem channel, PlaylistRef? playlist, bool autoHide)
    {
        var now = DateTime.UtcNow;
        var cards = new List<(string Label, bool IsNow, string Time, string Title, string? Description)>();
        if (EpgStore.Current(channel, now) is { } current)
        {
            cards.Add(("EN CE MOMENT", true, current.TimeRange, current.Title, current.Description));
            if (EpgStore.Upcoming(channel, now).FirstOrDefault() is { } next) cards.Add(("ENSUITE", false, next.TimeRange, next.Title, next.Description));
        }
        else if (playlist?.Xtream is { } account && channel.XtreamId is { } id)
        {
            var entries = await XtreamClient.ShortEpgAsync(account, id);
            foreach (var (label, isNow, entry) in new[] { ("EN CE MOMENT", true, entries.FirstOrDefault(p => p.Start <= now && now < p.End)), ("ENSUITE", false, entries.FirstOrDefault(p => p.Start > now)) })
                if (entry is not null) cards.Add((label, isNow, $"{entry.Start.ToLocalTime():HH:mm} – {entry.End.ToLocalTime():HH:mm}", entry.Title, entry.Description));
        }
        if (_closing || cards.Count == 0) return;
        EpgCards.Children.Clear();
        var white = Microsoft.UI.Colors.White;
        foreach (var (label, isNow, time, title, description) in cards)
        {
            var color = new Microsoft.UI.Xaml.Media.SolidColorBrush(isNow
                ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xE6, 0xA8, 0x4B) : Microsoft.UI.ColorHelper.FromArgb(0xB3, 0xFA, 0xF7, 0xF2));
            var panel = new StackPanel { Spacing = 6 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = label, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Black, CharacterSpacing = 240, Foreground = color });
            head.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 18, Height = 1, Fill = color, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = time, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VeltrioMono"], FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(white) { Opacity = 0.7 } });
            panel.Children.Add(head);
            panel.Children.Add(new TextBlock { Text = title, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VeltrioSerif"], FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(white), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(description))
                panel.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(white) { Opacity = 0.75 }, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
            EpgCards.Children.Add(new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xA6, 0x0D, 0x0D, 0x0D)),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = panel,
            });
        }
        EpgCards.Visibility = Visibility.Visible;
        _epgVisible = true;
        if (!autoHide) return;
        _epgCardsTimer ??= DispatcherQueue.CreateTimer();
        _epgCardsTimer.Stop();
        _epgCardsTimer.Interval = TimeSpan.FromSeconds(8);
        _epgCardsTimer.IsRepeating = false;
        _epgCardsTimer.Tick -= OnEpgCardsTimeout;
        _epgCardsTimer.Tick += OnEpgCardsTimeout;
        _epgCardsTimer.Start();
    }

    private void OnEpgCardsTimeout(DispatcherQueueTimer sender, object args)
    {
        EpgCards.Visibility = Visibility.Collapsed;
        _epgVisible = false;
    }

    private void OnToggleChannels(object sender, RoutedEventArgs e) => ToggleChannels();

    private void ToggleChannels()
    {
        var show = ChannelPanel.Visibility != Visibility.Visible;
        ClosePanels();
        ChannelPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _zapRows.FirstOrDefault(r => r.IsCurrent) is { } current) ZapList.ScrollIntoView(current, ScrollIntoViewAlignment.Leading);
        ShowChrome(keep: show);
    }

    private void OnZapClick(object sender, ItemClickEventArgs e) => ZapTo(((ChannelRow)e.ClickedItem).Channel);

    /// <summary>Previous / next channel (Page Up / Page Down).</summary>
    private void ZapBy(int step)
    {
        if (_zapRows.Count == 0 || _request.Channel is null) return;
        var index = _zapRows.FindIndex(r => r.Channel.Id == _request.Channel.Id);
        if (index < 0) return;
        ZapTo(_zapRows[(index + step + _zapRows.Count) % _zapRows.Count].Channel);
    }

    private void ZapTo(ChannelItem channel)
    {
        if (channel.Id == _request.Channel?.Id || _closing) return;
        _closing = true;
        Navigator.Zap(PlayRequest.ForChannel(channel, _request.Playlist));
    }

    /// <summary>The replay panel: past programmes the provider keeps, as
    /// the macOS catch-up sheet. Loaded with the channel, so the button can
    /// show how many there are.</summary>
    private Task<IReadOnlyList<XtreamProgramme>>? _replay;

    private Task<IReadOnlyList<XtreamProgramme>> LoadReplayAsync()
    {
        if (_replay is not null) return _replay;
        if (_request.Channel is not { XtreamId: { } id } || _request.Playlist?.Xtream is not { } account)
            return Task.FromResult<IReadOnlyList<XtreamProgramme>>(Array.Empty<XtreamProgramme>());
        _replay = Load();
        return _replay;

        async Task<IReadOnlyList<XtreamProgramme>> Load()
        {
            IReadOnlyList<XtreamProgramme> programmes;
            try { programmes = await XtreamClient.CatchupAsync(account, id); }
            catch { programmes = Array.Empty<XtreamProgramme>(); }
            if (!_closing && programmes.Count > 0)
            {
                ReplayBadgeText.Text = programmes.Count.ToString();
                ReplayBadge.Visibility = Visibility.Visible;
            }
            return programmes;
        }
    }

    private async void OnReplay(object sender, RoutedEventArgs e)
    {
        if (ReplayPanel.Visibility == Visibility.Visible)
        {
            ReplayPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ClosePanels();
        ReplayPanel.Visibility = Visibility.Visible;
        ShowChrome(keep: true);
        ReplayLoading.IsActive = true;
        var programmes = await LoadReplayAsync();
        ReplayLoading.IsActive = false;
        ReplayList.ItemsSource = programmes;
        ReplayHint.Text = programmes.Count == 0 ? "Aucun programme à revoir sur cette chaîne."
            : "Revenez à n'importe quel programme passé : il reprend depuis son début.";
    }

    private void OnReplayClick(object sender, ItemClickEventArgs e)
    {
        if (_request.Channel is not { XtreamId: { } id } channel || _request.Playlist?.Xtream is not { } account) return;
        var p = (XtreamProgramme)e.ClickedItem;
        _closing = true;
        Navigator.Zap(new PlayRequest
        {
            Title = channel.Name,
            Subtitle = $"Replay · {p.TimeRange} · {p.Title}",
            Url = XtreamClient.TimeshiftUrl(account, id, p),
            Channel = channel,
            Playlist = _request.Playlist,
        });
    }

    private void ClosePanels()
    {
        ChannelPanel.Visibility = ReplayPanel.Visibility = InfoPanel.Visibility = Visibility.Collapsed;
    }

    // -------------------- subtitles from addons --------------------

    private List<AddonSubtitle> _addonSubtitles = new();
    private readonly HashSet<string> _addedSubtitles = new();
    private bool _autoSubtitleDone;

    /// <summary>Subtitles from the addons that serve them (OpenSubtitles…).
    /// Once the file is loaded, one in a preferred language is added if the
    /// file has none of its own — mpv's slang only covers embedded tracks.</summary>
    private async Task FetchAddonSubtitlesAsync(string type, string id)
    {
        SubtitleBadge.Visibility = Visibility.Visible;
        _addonSubtitles = (await SubtitleFetcher.FetchAsync(type, id)).ToList();
        if (_closing) return;
        SubtitleBadge.Visibility = Visibility.Collapsed;
        BuildTrackMenus();
        AutoAddSubtitle();
    }

    private void AutoAddSubtitle()
    {
        if (_autoSubtitleDone || !_loaded || _player is null || _addonSubtitles.Count == 0) return;
        _autoSubtitleDone = true;
        if (PlayerPrefs.Current.SubtitleLang is not { Length: > 0 } language) return;
        var preferred = new[] { language };
        var embedded = _player.SubtitleTracks.Any(t => preferred.SelectMany(MediaTrack.Synonyms).Contains((t.Lang ?? "").ToLowerInvariant()));
        var audioIsPreferred = _player.AudioTracks.FirstOrDefault(t => t.Id == _player.AudioId) is { Lang: { } lang }
                               && preferred.SelectMany(MediaTrack.Synonyms).Contains(lang.ToLowerInvariant());
        if (embedded || audioIsPreferred) return;
        if (SubtitleFetcher.Pick(_addonSubtitles, preferred) is { } pick) AddAddonSubtitle(pick);
    }

    private void AddAddonSubtitle(AddonSubtitle subtitle)
    {
        if (_player is null) return;
        if (_addedSubtitles.Add(subtitle.Url)) _player.AddSubtitle(subtitle.Url, $"{subtitle.Language} ({subtitle.AddonName})", subtitle.Lang);
    }

    private async Task SearchManualSubtitlesAsync(string type, string id)
    {
        var found = await SubtitleFetcher.SearchOpenSubtitlesAsync(type, id);
        if (found.Count == 0) return;
        var existing = new HashSet<string>(_addonSubtitles.Select(s => s.Url));
        foreach (var s in found)
            if (existing.Add(s.Url)) _addonSubtitles.Add(s);
        DispatcherQueue.TryEnqueue(BuildTrackMenus);
    }

    private async Task LoadLocalSubtitleAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(Navigator.Window));
        foreach (var ext in new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is { } file) _player?.AddSubtitle(file.Path, file.Name);
    }

    // -------------------- skip intro / next episode --------------------

    private IntroService.IntroSegments? _introSegments;
    private bool _outroOffered;
    private DispatcherQueueTimer? _autoSkipTimer;
    private string? _autoSkipKey;

    private async Task FetchIntroWindowAsync(string imdbId, int season, int episode)
    {
        _introSegments = await IntroService.FetchSegmentsAsync(imdbId, season, episode);
    }

    private double? EffectiveIntroEnd()
    {
        if (_player is null || _player.Position < 2.0) return null;
        if (_introSegments?.Recap is { } r && _player.Position >= r.StartSec && _player.Position < r.EndSec - 1.0) return r.EndSec;
        if (_player.IntroEnd is { } end) return end;
        if (_introSegments?.Intro is { } w && _player.Position >= w.StartSec && _player.Position < w.EndSec - 1.0) return w.EndSec;
        return null;
    }

    private void UpdateSkipAndOutro(Player.MpvPlayer p)
    {
        var skipEnd = EffectiveIntroEnd();
        SkipIntroButton.Visibility = skipEnd is null ? Visibility.Collapsed : Visibility.Visible;
        if (skipEnd is { } end && !p.IsPaused)
        {
            var key = $"{(int)end}";
            if (_autoSkipKey != key)
            {
                _autoSkipKey = key;
                _autoSkipTimer?.Stop();
                _autoSkipTimer = DispatcherQueue.CreateTimer();
                _autoSkipTimer.Interval = TimeSpan.FromSeconds(5);
                _autoSkipTimer.IsRepeating = false;
                _autoSkipTimer.Tick += (_, _) =>
                {
                    if (_player is not null && !_player.IsPaused && EffectiveIntroEnd() is { } cur) _player.SeekTo(cur);
                };
                _autoSkipTimer.Start();
            }
        }
        else if (_autoSkipKey is not null)
        {
            _autoSkipKey = null;
            _autoSkipTimer?.Stop();
        }

        if (!_outroOffered && _request.Next is not null && NextOverlay.Visibility != Visibility.Visible)
        {
            var inOutro = (_introSegments?.Outro is { } o && p.Position >= o.StartSec && p.Position < o.EndSec)
                          || (p.Duration > 180 && p.Position > 0 && (p.Duration - p.Position) <= 15);
            if (inOutro)
            {
                _outroOffered = true;
                OfferNextEpisode();
            }
        }
    }

    private void SkipIntro()
    {
        _autoSkipTimer?.Stop();
        if (EffectiveIntroEnd() is { } end) _player?.SeekTo(end);
    }

    private void OnSkipIntro(object sender, RoutedEventArgs e) => SkipIntro();

    private DispatcherQueueTimer? _nextTimer;
    private int _nextCountdown;

    /// <summary>End of an episode with another after it: a countdown to the
    /// next one (when enabled), else back to the page.</summary>
    private void OfferNextEpisode()
    {
        NextTitle.Text = _request.NextLabel ?? "";
        NextOverlay.Visibility = Visibility.Visible;
        ShowChrome(keep: true);
        if (!PlayerPrefs.Current.AutoNextEpisode)
        {
            NextCountdown.Text = "";
            return;
        }
        _nextCountdown = 5;
        NextCountdown.Text = $"Épisode suivant dans {_nextCountdown} s";
        _nextTimer = DispatcherQueue.CreateTimer();
        _nextTimer.Interval = TimeSpan.FromSeconds(1);
        _nextTimer.Tick += (_, _) =>
        {
            _nextCountdown--;
            NextCountdown.Text = $"Épisode suivant dans {_nextCountdown} s";
            if (_nextCountdown <= 0) _ = PlayNextAsync();
        };
        _nextTimer.Start();
    }

    private void OnCancelNext(object sender, RoutedEventArgs e)
    {
        _nextTimer?.Stop();
        NextOverlay.Visibility = Visibility.Collapsed;
        if (_player is { Duration: > 0 } p && p.Position >= p.Duration - 1) Close();
    }

    private async void OnPlayNext(object sender, RoutedEventArgs e) => await PlayNextAsync();

    private bool _resolvingNext;

    private async Task PlayNextAsync()
    {
        if (_request.Next is null || _resolvingNext || _closing) return;
        _resolvingNext = true;
        _nextTimer?.Stop();
        NextCountdown.Text = "Recherche du flux…";
        PlayNextButton.IsEnabled = false;
        ReportProgress();
        PlayRequest? next = null;
        try { next = await _request.Next(); }
        catch (Exception ex) { PlayerLog.Write($"[next] {ex.Message}"); }
        if (_closing) return;
        if (next is null)
        {
            NextCountdown.Text = "Aucun flux trouvé pour cet épisode.";
            PlayNextButton.IsEnabled = true;
            _resolvingNext = false;
            return;
        }
        _closing = true;
        Navigator.Zap(next);
    }

    // -------------------- Chromecast --------------------

    private void OnCastChanged() => DispatcherQueue.TryEnqueue(UpdateCastBanner);

    private void UpdateCastBanner()
    {
        var state = ChromecastService.State;
        CastBanner.Visibility = state == ChromecastService.CastState.Idle ? Visibility.Collapsed : Visibility.Visible;
        var failed = state == ChromecastService.CastState.Failed;
        CastPlaying.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
        CastFailed.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        CastText.Text = state == ChromecastService.CastState.Casting
            ? $"Lecture sur {ChromecastService.DeviceName}" : $"Connexion à {ChromecastService.DeviceName}…";
        CastError.Text = ChromecastService.Error ?? "";
    }

    /// <summary>The devices found so far, or why this stream can't go to a TV.</summary>
    private void OnCastOpening(object sender, object e)
    {
        CastMenu.Items.Clear();
        void Note(string text) => CastMenu.Items.Add(new MenuFlyoutItem { Text = text, IsEnabled = false });
        if (!ChromecastService.CanCast(_url))
            Note("Ce flux se lit depuis ce PC (torrent ou fichier) : un Chromecast ne peut pas le récupérer. Un service debrid le rend diffusable.");
        else if (ChromecastService.Devices.Count == 0)
            Note("Recherche de Chromecasts sur le réseau…");
        else
            foreach (var device in ChromecastService.Devices)
            {
                var item = new MenuFlyoutItem { Text = device.Name, Icon = new FontIcon { Glyph = "\uE7F4" } };
                item.Click += (_, _) => CastTo(device);
                CastMenu.Items.Add(item);
            }
        if (ChromecastService.IsCasting)
        {
            var stop = new MenuFlyoutItem { Text = "Arrêter la diffusion" };
            stop.Click += (_, _) => ChromecastService.Stop();
            CastMenu.Items.Add(new MenuFlyoutSeparator());
            CastMenu.Items.Add(stop);
        }
    }

    /// <summary>Hands the stream to the TV at the current position and pauses here.</summary>
    private void CastTo(ChromecastService.Device device)
    {
        if (_url is null) return;
        ChromecastService.Cast(device, _url, _request.Title, _request.Poster, _request.IsLive, _player?.Position ?? 0);
        if (_player is { IsPaused: false }) _player.TogglePause();
    }

    private void OnStopCasting(object sender, RoutedEventArgs e) => ChromecastService.Stop();

    // -------------------- media info --------------------

    private void OnToggleInfo(object sender, RoutedEventArgs e) => ToggleInfo();

    private void ToggleInfo()
    {
        if (InfoPanel.Visibility == Visibility.Visible)
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ClosePanels();
        BuildInfo();
        InfoPanel.Visibility = Visibility.Visible;
        ShowChrome(keep: true);
    }

    private void BuildInfo()
    {
        if (_player is null) return;
        InfoLines.Children.Clear();
        var mono = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VeltrioMono"];
        Microsoft.UI.Xaml.Media.SolidColorBrush White(byte alpha) => new(Microsoft.UI.ColorHelper.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        void Section(string text)
        {
            InfoLines.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = White(0x26), Margin = new Thickness(0, 2, 0, 2) });
            InfoLines.Children.Add(new TextBlock { Text = text, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = 200, Foreground = White(0xA6) });
        }
        void Line(string label, string value)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = White(0x8C), TextWrapping = TextWrapping.Wrap });
            var v = new TextBlock { Text = value, FontSize = 11, FontFamily = mono, Foreground = White(0xEB), TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            InfoLines.Children.Add(row);
        }
        var info = _player.MediaInfo().ToList();
        foreach (var (label, value) in info) Line(label, value);
        if (info.Any(i => i.Value.Contains("HDR", StringComparison.OrdinalIgnoreCase) || i.Value.Contains("PQ") || i.Value.Contains("HLG")))
        {
            var hdr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            hdr.Children.Add(new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioAccentBrush"], Padding = new Thickness(6, 2, 6, 2),
                Child = new TextBlock { Text = "HDR", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Black, CharacterSpacing = 200, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VeltrioOnAccentBrush"] },
            });
            hdr.Children.Add(new TextBlock { Text = $"Tone mapping : {PlayerPrefs.Current.ToneMapping.Label()}", FontSize = 11, Foreground = White(0xBF), VerticalAlignment = VerticalAlignment.Center });
            InfoLines.Children.Add(hdr);
        }
        if (_player.AudioTracks.Count > 0)
        {
            Section($"PISTES AUDIO ({_player.AudioTracks.Count})");
            foreach (var t in _player.AudioTracks) Line(t.Id == _player.AudioId ? $"● {t.DisplayName}" : t.DisplayName, t.SecondaryLabel);
        }
        if (_player.SubtitleTracks.Count > 0)
        {
            Section($"SOUS-TITRES ({_player.SubtitleTracks.Count})");
            foreach (var t in _player.SubtitleTracks) Line(t.Id == _player.SubtitleId ? $"● {t.DisplayName}" : t.DisplayName, t.SecondaryLabel);
        }
        InfoProfile.Text = $"Profil · {PlayerPrefs.Current.Profile.Label()}";
    }

    // -------------------- picture, sound and delays --------------------

    private AspectMode _aspect = AspectMode.Auto;
    private double _zoom;

    /// <summary>A section title inside a menu, as the macOS menus have.</summary>
    private static MenuFlyoutItem Header(string text) => new() { Text = text, IsEnabled = false, FontSize = 11 };

    /// <summary>Framing and zoom, built when it opens so the checks are current.</summary>
    private void OnAspectOpening(object? sender, object e)
    {
        AspectMenu.Items.Clear();
        if (_player is not { } p) return;
        AspectMenu.Items.Add(Header("FORMAT D'IMAGE"));
        foreach (var mode in Enum.GetValues<AspectMode>())
            AspectMenu.Items.Add(Radio(mode.Label(), mode == _aspect, "aspect", () => SetAspect(mode)));
        AspectMenu.Items.Add(new MenuFlyoutSeparator());
        AspectMenu.Items.Add(Header("ZOOM"));
        AspectMenu.Items.Add(Item("Agrandir", () => SetZoom(_zoom + 0.1)));
        AspectMenu.Items.Add(Item("Réduire", () => SetZoom(_zoom - 0.1)));
        AspectMenu.Items.Add(Item($"Réinitialiser  ({_zoom:+0.0;-0.0;0.0})", () => SetZoom(0)));
    }

    private void SetAspect(AspectMode mode)
    {
        _aspect = mode;
        _player?.SetAspect(mode);
        UpdateBadges();
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Round(zoom, 1);
        _player?.SetZoom(_zoom);
        UpdateBadges();
    }

    /// <summary>A: the next framing.</summary>
    private void CycleAspect()
    {
        var modes = Enum.GetValues<AspectMode>();
        SetAspect(modes[(Array.IndexOf(modes, _aspect) + 1) % modes.Length]);
    }

    /// <summary>Playback profile, HDR tone mapping, passthrough.</summary>
    private void OnProfileOpening(object? sender, object e)
    {
        ProfileMenu.Items.Clear();
        if (_player is not { } p) return;
        var prefs = PlayerPrefs.Current;
        ProfileMenu.Items.Add(Header("PROFIL DE LECTURE"));
        foreach (var value in Enum.GetValues<PlaybackProfile>())
        {
            var item = Radio(value.Label(), value == prefs.Profile, "profile", () =>
            {
                prefs.Profile = value;
                prefs.Save();
                p.ApplyProfile(value);
                p.SetPassthrough(prefs.Passthrough || value == PlaybackProfile.Audiophile);
                UpdateBadges();
            });
            ToolTipService.SetToolTip(item, value.Blurb());
            ProfileMenu.Items.Add(item);
        }
        ProfileMenu.Items.Add(new MenuFlyoutSeparator());
        ProfileMenu.Items.Add(Header("TONE MAPPING"));
        foreach (var mapping in Enum.GetValues<ToneMapping>())
            ProfileMenu.Items.Add(Radio(mapping.Label(), mapping == prefs.ToneMapping, "tone", () => { prefs.ToneMapping = mapping; prefs.Save(); p.SetToneMapping(mapping); }));
        ProfileMenu.Items.Add(new MenuFlyoutSeparator());
        var passthrough = new ToggleMenuFlyoutItem { Text = "Son direct vers l'ampli (DTS / AC3 / EAC3)", IsChecked = prefs.Passthrough };
        passthrough.Click += (_, _) => { prefs.Passthrough = passthrough.IsChecked; prefs.Save(); p.SetPassthrough(passthrough.IsChecked); };
        ProfileMenu.Items.Add(passthrough);
    }

    /// <summary>The corner badges: speed when not 1×, framing when changed,
    /// the profile's initial when not Balanced.</summary>
    private void UpdateBadges()
    {
        var speed = _player?.Speed ?? 1;
        SpeedBadgeText.Text = $"{speed:0.##}×";
        SpeedBadge.Visibility = Math.Abs(speed - 1) > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        AspectBadgeText.Text = _aspect == AspectMode.Auto ? $"{_zoom:+0.0;-0.0}" : _aspect.Label();
        AspectBadge.Visibility = _aspect != AspectMode.Auto || _zoom != 0 ? Visibility.Visible : Visibility.Collapsed;
        var profile = PlayerPrefs.Current.Profile;
        ProfileBadgeText.Text = profile.Label()[..1].ToUpperInvariant();
        ProfileBadge.Visibility = profile == PlaybackProfile.Balanced ? Visibility.Collapsed : Visibility.Visible;
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    private static RadioMenuFlyoutItem Radio(string text, bool isChecked, string group, Action action)
    {
        var item = new RadioMenuFlyoutItem { Text = text, IsChecked = isChecked, GroupName = group };
        item.Click += (_, _) => action();
        return item;
    }

    // -------------------- player → UI --------------------

    private void OnFileLoaded()
    {
        _loaded = true;
        Status.Visibility = Visibility.Collapsed;
        UpdateBadges();
        ShowChrome();
        AutoAddSubtitle();
    }

    private bool _ended;

    private void OnEnded(string? error)
    {
        // eof-reached and end-file can both report the same end.
        if (error is null && _ended) return;
        if (error is null) _ended = true;
        if (error is not null)
        {
            ShowError($"Lecture impossible. {error}", retry: true);
            return;
        }
        // Natural end of a film or episode: record it as watched, then the
        // next episode or back to the page.
        ReportProgress();
        if (_request.IsLive) return;
        if (_request.Next is not null) OfferNextEpisode();
        else Close();
    }

    private void OnPlayerChanged(string property)
    {
        var p = _player!;
        switch (property)
        {
            case "time-pos":
            case "duration":
                Scrubber.Maximum = Math.Max(1, p.Duration);
                Scrubber.Value = Math.Clamp(p.Position, 0, Scrubber.Maximum);
                if (!Scrubber.IsDragging) PositionText.Text = TimeLabelConverter.Format(p.Position);
                RemainingText.Text = "-" + TimeLabelConverter.Format(Math.Max(0, p.Duration - p.Position));
                TimePosition.Text = TimeLabelConverter.Format(p.Position);
                TimeDuration.Text = TimeLabelConverter.Format(p.Duration);
                UpdateSkipAndOutro(p);
                break;
            case "pause":
                PlayPauseIcon.Glyph = p.IsPaused ? "\uE768" : "\uE769";
                ShowChrome(keep: p.IsPaused);
                if (!_request.IsLive) _scrobbler.Update(p.Position, p.Duration, p.IsPaused);
                break;
            case "buffering":
                BufferRing.Visibility = _loaded && p.IsBuffering ? Visibility.Visible : Visibility.Collapsed;
                LiveLabelText.Text = p.IsBuffering ? "Mise en mémoire tampon…" : "Direct";
                break;
            case "volume":
            case "mute":
                if (!VolumeSlider.IsDragging) VolumeSlider.Value = p.Volume;
                // As the macOS speaker: muted, then one to three waves.
                VolumeIcon.Glyph = p.IsMuted || p.Volume <= 0 ? "\uE74F" : p.Volume < 34 ? "\uE993" : p.Volume < 67 ? "\uE994" : "\uE995";
                break;
            case "speed":
                foreach (var item in SpeedMenu.Items.OfType<RadioMenuFlyoutItem>())
                    item.IsChecked = Math.Abs((double)item.Tag - p.Speed) < 0.01;
                UpdateBadges();
                break;
            case "aid":
            case "sid":
            case "tracks":
                BuildTrackMenus();
                if (InfoPanel.Visibility == Visibility.Visible) BuildInfo();
                break;
            case "chapter":
            case "chapters":
                SkipIntroButton.Visibility = EffectiveIntroEnd() is null ? Visibility.Collapsed : Visibility.Visible;
                Scrubber.Chapters = p.Chapters.Select(c => c.Start).ToList();
                break;
        }
    }

    private void BuildTrackMenus()
    {
        if (_closing || _player is null) return;
        var p = _player;
        AudioMenu.Items.Clear();
        foreach (var track in p.AudioTracks)
            AudioMenu.Items.Add(TrackItem(track.Label, track.Id == p.AudioId, "audio", () => p.SelectAudio(track.Id)));
        if (p.AudioDelay != 0)
        {
            AudioMenu.Items.Add(new MenuFlyoutSeparator());
            AudioMenu.Items.Add(Item($"Décalage : {p.AudioDelay:+0.0;-0.0} s — réinitialiser", () => p.SetAudioDelay(0)));
        }
        // Like macOS: the button only when there's a choice.
        AudioButton.Visibility = p.AudioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        SubtitleMenu.Items.Clear();
        SubtitleMenu.Items.Add(TrackItem("Désactivés", p.SubtitleId <= 0, "subs", () => p.SelectSubtitle(0)));
        if (p.SubtitleTracks.Count > 0)
        {
            SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
            SubtitleMenu.Items.Add(Header("INTÉGRÉS"));
            foreach (var track in p.SubtitleTracks)
                SubtitleMenu.Items.Add(TrackItem(track.Label, track.Id == p.SubtitleId, "subs", () => p.SelectSubtitle(track.Id)));
        }
        var online = _addonSubtitles.Where(s => !_addedSubtitles.Contains(s.Url)).ToList();
        if (online.Count > 0)
        {
            SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
            SubtitleMenu.Items.Add(Header("DES ADDONS"));
            foreach (var s in online) SubtitleMenu.Items.Add(Item($"{s.Language}   {s.AddonName}", () => AddAddonSubtitle(s)));
        }
        SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
        if (_request.ContentType is "movie" or "series" && (_request.VideoId ?? _request.ContentId) is { } subId && subId.StartsWith("tt", StringComparison.Ordinal))
        {
            SubtitleMenu.Items.Add(Item(AppPrefs.Tr("Rechercher OpenSubtitles", "Search OpenSubtitles", "Buscar en OpenSubtitles", "OpenSubtitles durchsuchen", "Cerca su OpenSubtitles"), () => _ = SearchManualSubtitlesAsync(_request.ContentType, subId)));
        }
        var sizeSub = new MenuFlyoutSubItem { Text = AppPrefs.Tr("Taille des sous-titres", "Subtitle size", "Tamaño de subtítulos", "Untertitelgröße", "Dimensione sottotitoli") };
        foreach (var (code, name) in PlayerPrefs.SubtitleScales)
        {
            var sc = double.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
            sizeSub.Items.Add(TrackItem(name, Math.Abs(PlayerPrefs.Current.SubtitleScale - sc) < 0.01, "sub-scale", () =>
            {
                PlayerPrefs.Current.SubtitleScale = sc;
                PlayerPrefs.Current.Save();
                p.SetSubtitleAppearance(sc, PlayerPrefs.Current.SubtitleColor);
            }));
        }
        SubtitleMenu.Items.Add(sizeSub);
        var colorSub = new MenuFlyoutSubItem { Text = AppPrefs.Tr("Couleur des sous-titres", "Subtitle color", "Color de subtítulos", "Untertitelfarbe", "Colore sottotitoli") };
        foreach (var (hex, name) in PlayerPrefs.SubtitleColors)
        {
            var cHex = hex;
            colorSub.Items.Add(TrackItem(name, PlayerPrefs.Current.SubtitleColor == cHex, "sub-color", () =>
            {
                PlayerPrefs.Current.SubtitleColor = cHex;
                PlayerPrefs.Current.Save();
                p.SetSubtitleAppearance(PlayerPrefs.Current.SubtitleScale, cHex);
            }));
        }
        SubtitleMenu.Items.Add(colorSub);
        SubtitleMenu.Items.Add(Item("Charger un fichier…", () => _ = LoadLocalSubtitleAsync()));
        if (p.SubDelay != 0)
        {
            SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
            SubtitleMenu.Items.Add(Item($"Décalage : {p.SubDelay:+0.0;-0.0} s — réinitialiser", () => p.SetSubDelay(0)));
        }
        // Filled when a subtitle is on, as the macOS icon.
        SubtitleIcon.Glyph = p.SubtitleId > 0 ? "\uED1F" : "\uED1E";
    }

    private static RadioMenuFlyoutItem TrackItem(string text, bool selected, string group, Action select)
    {
        var item = new RadioMenuFlyoutItem { Text = text, IsChecked = selected, GroupName = group };
        item.Click += (_, _) => select();
        return item;
    }

    private void BuildSpeedMenu()
    {
        foreach (var speed in Speeds)
        {
            var item = new RadioMenuFlyoutItem { Text = speed == 1 ? "Normale" : $"{speed:0.##}×", Tag = speed, GroupName = "speed", IsChecked = speed == 1 };
            item.Click += (_, _) => _player?.SetSpeed(speed);
            SpeedMenu.Items.Add(item);
        }
    }

    /// <summary>The macOS error state: a warning, what failed, Retry and Back.</summary>
    private void ShowError(string message, bool retry)
    {
        Busy.IsActive = false;
        Busy.Visibility = BufferRing.Visibility = Visibility.Collapsed;
        Status.Visibility = ErrorIcon.Visibility = ErrorActions.Visibility = Visibility.Visible;
        StatusText.Text = "Impossible d'ouvrir ce flux";
        StatusText.FontSize = 18;
        StatusDetail.Text = message;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        ShowChrome(keep: true);
    }

    private void ReportProgress()
    {
        if (_player is null || _request.IsLive) return;
        if (_player.Duration > 0 && _player.Position > 0) WatchStore.ReportProgress(_request, _player.Position, _player.Duration);
        _scrobbler.Update(_player.Position, _player.Duration, _player.IsPaused);
    }

    // -------------------- chrome --------------------

    private void ShowChrome(bool keep = false)
    {
        TopBar.Visibility = BottomBar.Visibility = Visibility.Visible;
        _player?.SetSubtitleLift(true);
        _hideTimer?.Stop();
        if (!keep && _player is { IsPaused: false } && Status.Visibility == Visibility.Collapsed) _hideTimer?.Start();
    }

    private void HideChrome()
    {
        if (_player is null || _player.IsPaused || Status.Visibility == Visibility.Visible) return;
        // Keep the bars while a menu is open.
        if (AudioMenu.IsOpen || SubtitleMenu.IsOpen || SpeedMenu.IsOpen || AspectMenu.IsOpen || ProfileMenu.IsOpen || CastMenu.IsOpen
            || Scrubber.IsDragging || VolumeSlider.IsDragging
            || ChannelPanel.Visibility == Visibility.Visible || ReplayPanel.Visibility == Visibility.Visible
            || InfoPanel.Visibility == Visibility.Visible || NextOverlay.Visibility == Visibility.Visible)
        {
            _hideTimer?.Start();
            return;
        }
        TopBar.Visibility = BottomBar.Visibility = Visibility.Collapsed;
        _player.SetSubtitleLift(false);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => ShowChrome();

    /// <summary>As on macOS, a click on the picture pauses or resumes while
    /// the controls are hidden; with them showing it only keeps them up.</summary>
    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource != Video && e.OriginalSource != Root) return;
        if (TopBar.Visibility == Visibility.Collapsed) _player?.TogglePause();
        ShowChrome();
    }

    // -------------------- UI → player --------------------

    /// <summary>The tooltip over the seek bar: the time there, and the
    /// chapter it falls in.</summary>
    private void OnScrubHover(double? at)
    {
        if (at is not { } t || _player is not { Duration: > 0 } p)
        {
            ScrubTip.Visibility = Visibility.Collapsed;
            return;
        }
        ScrubTipTime.Text = TimeLabelConverter.Format(t);
        var chapter = p.Chapters.LastOrDefault(c => c.Start <= t);
        ScrubTipChapter.Text = chapter.Title?.ToUpperInvariant() ?? "";
        ScrubTipChapter.Visibility = string.IsNullOrEmpty(chapter.Title) ? Visibility.Collapsed : Visibility.Visible;
        ScrubTipOffset.X = t / p.Duration * Scrubber.ActualWidth - 50;
        ScrubTip.Visibility = Visibility.Visible;
        ShowChrome();
    }

    private void OnTogglePause(object sender, RoutedEventArgs e) => _player?.TogglePause();
    private void OnBack10(object sender, RoutedEventArgs e) => _player?.SeekRelative(-10);
    private void OnForward10(object sender, RoutedEventArgs e) => _player?.SeekRelative(10);
    private void OnToggleMute(object sender, RoutedEventArgs e) => _player?.ToggleMute();
    private async void OnRetry(object sender, RoutedEventArgs e) => await OpenAsync();

    private void ToggleFullScreen()
    {
        var on = !Navigator.Window.IsFullScreen;
        Navigator.Window.SetFullScreen(on);
        FullScreenIcon.Glyph = on ? "\uE73F" : "\uE740";
    }

    private void OnTogglePip(object sender, RoutedEventArgs e)
    {
        var appWindow = Navigator.Window.AppWindow;
        if (appWindow is null) return;
        var isPip = appWindow.Presenter?.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay;
        if (!isPip)
        {
            Navigator.Window.SetFullScreen(false);
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay);
        }
        else
        {
            appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        }
    }

    private void OnToggleFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        if (_closing) return;
        _closing = true;
        if (Navigator.Window.AppWindow?.Presenter?.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.CompactOverlay)
            Navigator.Window.AppWindow?.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
        Navigator.Back();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _closing = true;
        ReportProgress();
        if (_player is not null) _scrobbler.Stop(_player.Position, _player.Duration);
        ChromecastService.Changed -= OnCastChanged;
        ChromecastService.StopDiscovery();
        _progressTimer?.Stop();
        _hideTimer?.Stop();
        _nextTimer?.Stop();
        _autoSkipTimer?.Stop();
        _epgCardsTimer?.Stop();
        // The render context goes before the mpv core.
        _surface?.Dispose();
        _surface = null;
        _player?.Dispose();
        _player = null;
        if (_request.Stream?.InfoHash is { } hash) CoreService.StopTorrent(hash);
    }
}

/// <summary>Seconds as "1:02:03" / "4:05", for the scrubber.</summary>
public sealed partial class TimeLabelConverter : IValueConverter
{
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public object Convert(object value, Type targetType, object parameter, string language) => Format(value is double d ? d : 0);

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
