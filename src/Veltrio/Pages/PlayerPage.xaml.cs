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

/// <summary>Full-window player on libmpv, like macOS: resolves the stream
/// (debrid or the core's engine for torrents), resumes where the user
/// stopped, reports progress every 10 s and on close. Tracks, addon and
/// local subtitles, speed, volume, framing and zoom, audio/subtitle delay,
/// tone mapping, profiles, media info, skip intro and next episode; for
/// live TV now/next, zapping and replay.</summary>
public sealed partial class PlayerPage : Page
{
    private static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };

    private PlayRequest _request = null!;
    private string? _url;
    private MpvPlayer? _player;
    private VideoSurface? _surface;
    private DispatcherQueueTimer? _progressTimer;
    private DispatcherQueueTimer? _hideTimer;
    private bool _closing;
    private bool _syncingScrubber;
    private bool _syncingVolume;
    private bool _loaded;

    public PlayerPage()
    {
        InitializeComponent();
        AddAccelerator(Windows.System.VirtualKey.Escape, () => { if (Navigator.Window.IsFullScreen) ToggleFullScreen(); else Close(); });
        AddAccelerator(Windows.System.VirtualKey.F, ToggleFullScreen);
        AddAccelerator(Windows.System.VirtualKey.Space, () => _player?.TogglePause());
        AddAccelerator(Windows.System.VirtualKey.Left, () => _player?.SeekRelative(-10));
        AddAccelerator(Windows.System.VirtualKey.Right, () => _player?.SeekRelative(30));
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
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        BuildSpeedMenu();
        // Keyboard focus on the page, not on Back: Space must pause, not "click".
        Loaded += (_, _) => Focus(FocusState.Programmatic);
    }

    private void AddAccelerator(Windows.System.VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, e) => { action(); e.Handled = true; ShowChrome(); };
        KeyboardAccelerators.Add(accelerator);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not PlayRequest request) return;
        _request = request;
        TitleText.Text = request.Title;
        SubtitleText.Text = request.Subtitle ?? "";
        LiveBadge.Visibility = request.IsLive ? Visibility.Visible : Visibility.Collapsed;
        ScrubRow.Visibility = request.IsLive ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = ForwardButton.Visibility = request.IsLive ? Visibility.Collapsed : Visibility.Visible;
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
        RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            if (_url is null)
            {
                StatusText.Text = _request.Stream?.InfoHash is null ? "Préparation du flux…"
                    : DebridService.AnyConnected ? "Résolution via le service debrid…" : "Connexion aux pairs du torrent…";
                _url = _request.Url ?? await CoreService.ResolveAsync(_request.Stream!, _request.Season, _request.Episode);
            }
        }
        catch (Exception ex)
        {
            ShowError($"Impossible d'ouvrir ce flux. {CoreService.Humanize(ex)}", retry: true);
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

        StatusText.Text = "Chargement…";
        var resume = _request.ContentType is { } type && _request.ContentId is { } id
            ? WatchStore.ResumePosition(type, id, _request.VideoId)
            : 0;
        _player.Load(_url, resume);
    }

    // -------------------- live TV --------------------

    private List<ChannelRow> _zapRows = new();
    private DispatcherQueueTimer? _epgTimer;

    /// <summary>The guide banner, the playlist's channels to zap through
    /// and, for Xtream channels with an archive, replay.</summary>
    private async Task SetUpLiveAsync(ChannelItem channel, PlaylistRef? playlist)
    {
        ChannelsButton.Visibility = playlist is null ? Visibility.Collapsed : Visibility.Visible;
        ReplayButton.Visibility = channel.HasCatchup && playlist?.IsXtream == true ? Visibility.Visible : Visibility.Collapsed;
        _epgTimer = DispatcherQueue.CreateTimer();
        _epgTimer.Interval = TimeSpan.FromSeconds(30);
        _epgTimer.Tick += (_, _) => _ = UpdateEpgLineAsync(channel, playlist);
        _epgTimer.Start();
        await UpdateEpgLineAsync(channel, playlist);

        if (playlist is null) return;
        try
        {
            var all = await IptvStore.ItemsAsync(playlist);
            var now = DateTime.UtcNow;
            _zapRows = all.Select(c => new ChannelRow(c)).ToList();
            foreach (var row in _zapRows) row.Refresh(now);
            ZapList.ItemsSource = _zapRows;
            ZapList.SelectedIndex = _zapRows.FindIndex(r => r.Channel.Id == channel.Id);
            ChannelPanelTitle.Text = $"{playlist.Name.ToUpperInvariant()} · {all.Count} CHAÎNES";
        }
        catch
        {
            ChannelsButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task UpdateEpgLineAsync(ChannelItem channel, PlaylistRef? playlist)
    {
        var now = DateTime.UtcNow;
        string? line = null;
        if (EpgStore.Current(channel, now) is { } current)
        {
            var next = EpgStore.Upcoming(channel, now).FirstOrDefault();
            line = $"{current.TimeRange}  {current.Title}" + (next is null ? "" : $"     ·     À suivre {next.Start.ToLocalTime():HH:mm}  {next.Title}");
        }
        else if (playlist?.Xtream is { } account && channel.XtreamId is { } id)
        {
            var entries = await XtreamClient.ShortEpgAsync(account, id);
            var now2 = DateTime.UtcNow;
            var cur = entries.FirstOrDefault(p => p.Start <= now2 && now2 < p.End);
            var next = entries.FirstOrDefault(p => p.Start > now2);
            if (cur is not null)
                line = $"{cur.Start.ToLocalTime():HH:mm} – {cur.End.ToLocalTime():HH:mm}  {cur.Title}"
                       + (next is null ? "" : $"     ·     À suivre {next.Start.ToLocalTime():HH:mm}  {next.Title}");
        }
        if (_closing) return;
        EpgLine.Text = line ?? "";
        EpgLine.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
        if (!_epgCardsShown && line is not null)
        {
            _epgCardsShown = true;
            ShowEpgCards(channel, playlist);
        }
    }

    private bool _epgCardsShown;
    private DispatcherQueueTimer? _epgCardsTimer;

    /// <summary>NOW and NEXT cards over the picture for a few seconds on
    /// tune-in, as the macOS player does.</summary>
    private async void ShowEpgCards(ChannelItem channel, PlaylistRef? playlist)
    {
        var now = DateTime.UtcNow;
        var cards = new List<(string Label, string Time, string Title, string? Description)>();
        if (EpgStore.Current(channel, now) is { } current)
        {
            cards.Add(("EN CE MOMENT", current.TimeRange, current.Title, current.Description));
            if (EpgStore.Upcoming(channel, now).FirstOrDefault() is { } next) cards.Add(("ENSUITE", next.TimeRange, next.Title, next.Description));
        }
        else if (playlist?.Xtream is { } account && channel.XtreamId is { } id)
        {
            var entries = await XtreamClient.ShortEpgAsync(account, id);
            foreach (var (label, entry) in new[] { ("EN CE MOMENT", entries.FirstOrDefault(p => p.Start <= now && now < p.End)), ("ENSUITE", entries.FirstOrDefault(p => p.Start > now)) })
                if (entry is not null) cards.Add((label, $"{entry.Start.ToLocalTime():HH:mm} – {entry.End.ToLocalTime():HH:mm}", entry.Title, entry.Description));
        }
        if (_closing || cards.Count == 0) return;
        EpgCards.Children.Clear();
        foreach (var (label, time, title, description) in cards)
        {
            var panel = new StackPanel { Spacing = 5 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["VeltrioAccentEyebrow"] });
            head.Children.Add(new TextBlock { Text = time, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VeltrioMono"], FontSize = 10, Opacity = 0.75 });
            panel.Children.Add(head);
            panel.Children.Add(new TextBlock { Text = title, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VeltrioSerif"], FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(description))
                panel.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis });
            EpgCards.Children.Add(new Border
            {
                Padding = new Thickness(14, 10, 14, 10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xB8, 0, 0, 0)),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = panel,
            });
        }
        EpgCards.Visibility = Visibility.Visible;
        _epgCardsTimer ??= DispatcherQueue.CreateTimer();
        _epgCardsTimer.Interval = TimeSpan.FromSeconds(8);
        _epgCardsTimer.IsRepeating = false;
        _epgCardsTimer.Tick += (_, _) => EpgCards.Visibility = Visibility.Collapsed;
        _epgCardsTimer.Start();
    }

    private void OnToggleChannels(object sender, RoutedEventArgs e)
    {
        var show = ChannelPanel.Visibility != Visibility.Visible;
        ClosePanels();
        ChannelPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (ChannelPanel.Visibility == Visibility.Visible && ZapList.SelectedItem is { } item) ZapList.ScrollIntoView(item);
        ShowChrome(keep: ChannelPanel.Visibility == Visibility.Visible);
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
    /// the macOS catch-up sheet.</summary>
    private async void OnReplay(object sender, RoutedEventArgs e)
    {
        if (ReplayPanel.Visibility == Visibility.Visible)
        {
            ReplayPanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (_request.Channel is not { XtreamId: { } id } || _request.Playlist?.Xtream is not { } account) return;
        ClosePanels();
        ReplayPanel.Visibility = Visibility.Visible;
        ShowChrome(keep: true);
        ReplayLoading.IsActive = true;
        var programmes = await XtreamClient.CatchupAsync(account, id);
        ReplayLoading.IsActive = false;
        ReplayList.ItemsSource = programmes;
        ReplayHint.Text = programmes.Count == 0 ? "Aucun programme à revoir sur cette chaîne." : "Revenez à un programme passé : il reprend depuis son début.";
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

    private IReadOnlyList<AddonSubtitle> _addonSubtitles = Array.Empty<AddonSubtitle>();
    private readonly HashSet<string> _addedSubtitles = new();
    private bool _autoSubtitleDone;

    /// <summary>Subtitles from the addons that serve them (OpenSubtitles…).
    /// Once the file is loaded, one in a preferred language is added if the
    /// file has none of its own — mpv's slang only covers embedded tracks.</summary>
    private async Task FetchAddonSubtitlesAsync(string type, string id)
    {
        _addonSubtitles = await SubtitleFetcher.FetchAsync(type, id);
        if (_closing) return;
        BuildTrackMenus();
        AutoAddSubtitle();
    }

    private void AutoAddSubtitle()
    {
        if (_autoSubtitleDone || !_loaded || _player is null || _addonSubtitles.Count == 0) return;
        _autoSubtitleDone = true;
        var preferred = new[] { "fr" };
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

    private async Task LoadLocalSubtitleAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(Navigator.Window));
        foreach (var ext in new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is { } file) _player?.AddSubtitle(file.Path, file.Name);
    }

    // -------------------- skip intro / next episode --------------------

    private void SkipIntro()
    {
        if (_player?.IntroEnd is { } end) _player.SeekTo(end);
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
        _nextCountdown = 10;
        NextCountdown.Text = $"DANS {_nextCountdown} S";
        _nextTimer = DispatcherQueue.CreateTimer();
        _nextTimer.Interval = TimeSpan.FromSeconds(1);
        _nextTimer.Tick += (_, _) =>
        {
            _nextCountdown--;
            NextCountdown.Text = $"DANS {_nextCountdown} S";
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
        NextCountdown.Text = "RECHERCHE DU FLUX…";
        PlayNextButton.IsEnabled = false;
        ReportProgress();
        PlayRequest? next = null;
        try { next = await _request.Next(); }
        catch (Exception ex) { PlayerLog.Write($"[next] {ex.Message}"); }
        if (_closing) return;
        if (next is null)
        {
            NextCountdown.Text = "AUCUN FLUX TROUVÉ POUR CET ÉPISODE";
            PlayNextButton.IsEnabled = true;
            _resolvingNext = false;
            return;
        }
        _closing = true;
        Navigator.Zap(next);
    }

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
        void Title(string text) => InfoLines.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["VeltrioAccentEyebrow"], Margin = new Thickness(0, 12, 0, 4) });
        void Line(string label, string value)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            var v = new TextBlock { Text = value, FontSize = 12, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["VeltrioMono"], TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            InfoLines.Children.Add(row);
        }
        Title("INFOS MÉDIA");
        foreach (var (label, value) in _player.MediaInfo()) Line(label, value);
        Title($"PISTES AUDIO ({_player.AudioTracks.Count})");
        foreach (var t in _player.AudioTracks) Line(t.Id == _player.AudioId ? "● Active" : $"Piste {t.Id}", t.Label);
        Title($"SOUS-TITRES ({_player.SubtitleTracks.Count})");
        foreach (var t in _player.SubtitleTracks) Line(t.Id == _player.SubtitleId ? "● Actifs" : $"Piste {t.Id}", t.Label);
        Title("LECTURE");
        var prefs = PlayerPrefs.Current;
        Line("Profil", prefs.Profile.Label());
        Line("Tone mapping", prefs.ToneMapping.Label());
        Line("Décalage s.-t.", $"{_player.SubDelay:+0.0;-0.0;0} s");
        Line("Décalage audio", $"{_player.AudioDelay:+0.0;-0.0;0} s");
    }

    // -------------------- picture, sound and delays --------------------

    private AspectMode _aspect = AspectMode.Auto;
    private double _zoom;

    /// <summary>Built each time it opens so the checks and values are current.</summary>
    private void OnSettingsOpening(object? sender, object e)
    {
        SettingsMenu.Items.Clear();
        if (_player is null) return;
        var p = _player;
        var prefs = PlayerPrefs.Current;

        var aspect = new MenuFlyoutSubItem { Text = $"Format d'image · {_aspect.Label()}" };
        foreach (var mode in Enum.GetValues<AspectMode>())
            aspect.Items.Add(Radio(mode.Label(), mode == _aspect, "aspect", () => { _aspect = mode; p.SetAspect(mode); }));
        SettingsMenu.Items.Add(aspect);

        var zoom = new MenuFlyoutSubItem { Text = $"Zoom · {(_zoom == 0 ? "aucun" : $"{_zoom:+0.0;-0.0}")}" };
        zoom.Items.Add(Item("Agrandir", () => { _zoom += 0.1; p.SetZoom(_zoom); }));
        zoom.Items.Add(Item("Réduire", () => { _zoom -= 0.1; p.SetZoom(_zoom); }));
        zoom.Items.Add(Item("Réinitialiser", () => { _zoom = 0; p.SetZoom(0); _aspect = AspectMode.Auto; p.SetAspect(AspectMode.Auto); }));
        SettingsMenu.Items.Add(zoom);

        SettingsMenu.Items.Add(new MenuFlyoutSeparator());
        var subDelay = new MenuFlyoutSubItem { Text = $"Décalage des sous-titres · {p.SubDelay:+0.0;-0.0;0} s" };
        subDelay.Items.Add(Item("Plus tôt de 0,1 s (Z)", () => p.SetSubDelay(p.SubDelay - 0.1)));
        subDelay.Items.Add(Item("Plus tard de 0,1 s (X)", () => p.SetSubDelay(p.SubDelay + 0.1)));
        subDelay.Items.Add(Item("Plus tôt de 1 s", () => p.SetSubDelay(p.SubDelay - 1)));
        subDelay.Items.Add(Item("Plus tard de 1 s", () => p.SetSubDelay(p.SubDelay + 1)));
        subDelay.Items.Add(Item("Réinitialiser", () => p.SetSubDelay(0)));
        SettingsMenu.Items.Add(subDelay);
        var audioDelay = new MenuFlyoutSubItem { Text = $"Décalage du son · {p.AudioDelay:+0.0;-0.0;0} s" };
        audioDelay.Items.Add(Item("Plus tôt de 0,1 s", () => p.SetAudioDelay(p.AudioDelay - 0.1)));
        audioDelay.Items.Add(Item("Plus tard de 0,1 s", () => p.SetAudioDelay(p.AudioDelay + 0.1)));
        audioDelay.Items.Add(Item("Réinitialiser", () => p.SetAudioDelay(0)));
        SettingsMenu.Items.Add(audioDelay);

        SettingsMenu.Items.Add(new MenuFlyoutSeparator());
        var tone = new MenuFlyoutSubItem { Text = $"Tone mapping HDR · {prefs.ToneMapping.Label()}" };
        foreach (var mapping in Enum.GetValues<ToneMapping>())
            tone.Items.Add(Radio(mapping.Label(), mapping == prefs.ToneMapping, "tone", () => { prefs.ToneMapping = mapping; prefs.Save(); p.SetToneMapping(mapping); }));
        SettingsMenu.Items.Add(tone);
        var profile = new MenuFlyoutSubItem { Text = $"Profil · {prefs.Profile.Label()}" };
        foreach (var value in Enum.GetValues<PlaybackProfile>())
        {
            var item = Radio(value.Label(), value == prefs.Profile, "profile", () =>
            {
                prefs.Profile = value;
                prefs.Save();
                p.ApplyProfile(value);
                p.SetPassthrough(prefs.Passthrough || value == PlaybackProfile.Audiophile);
            });
            ToolTipService.SetToolTip(item, value.Blurb());
            profile.Items.Add(item);
        }
        SettingsMenu.Items.Add(profile);
        var passthrough = new ToggleMenuFlyoutItem { Text = "Son direct vers l'ampli (DTS, AC3…)", IsChecked = prefs.Passthrough };
        passthrough.Click += (_, _) => { prefs.Passthrough = passthrough.IsChecked; prefs.Save(); p.SetPassthrough(passthrough.IsChecked); };
        SettingsMenu.Items.Add(passthrough);
        var autoNext = new ToggleMenuFlyoutItem { Text = "Enchaîner les épisodes", IsChecked = prefs.AutoNextEpisode };
        autoNext.Click += (_, _) => { prefs.AutoNextEpisode = autoNext.IsChecked; prefs.Save(); };
        SettingsMenu.Items.Add(autoNext);
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
                _syncingScrubber = true;
                Scrubber.Maximum = Math.Max(1, p.Duration);
                Scrubber.Value = Math.Clamp(p.Position, 0, Scrubber.Maximum);
                _syncingScrubber = false;
                PositionText.Text = TimeLabelConverter.Format(p.Position);
                DurationText.Text = TimeLabelConverter.Format(p.Duration);
                break;
            case "pause":
                PlayPauseIcon.Glyph = p.IsPaused ? "" : "";
                ShowChrome(keep: p.IsPaused);
                break;
            case "buffering":
                if (_loaded)
                {
                    Status.Visibility = p.IsBuffering ? Visibility.Visible : Visibility.Collapsed;
                    StatusText.Text = "Mise en mémoire tampon…";
                }
                break;
            case "volume":
            case "mute":
                _syncingVolume = true;
                VolumeSlider.Value = p.Volume;
                _syncingVolume = false;
                VolumeIcon.Glyph = p.IsMuted || p.Volume <= 0 ? "" : "";
                break;
            case "speed":
                SpeedLabel.Text = $"{p.Speed:0.##}×";
                foreach (var item in SpeedMenu.Items.OfType<RadioMenuFlyoutItem>())
                    item.IsChecked = Math.Abs((double)item.Tag - p.Speed) < 0.01;
                break;
            case "aid":
            case "sid":
            case "tracks":
                BuildTrackMenus();
                if (InfoPanel.Visibility == Visibility.Visible) BuildInfo();
                break;
            case "chapter":
            case "chapters":
                SkipIntroButton.Visibility = p.IntroEnd is null ? Visibility.Collapsed : Visibility.Visible;
                break;
        }
    }

    private void BuildTrackMenus()
    {
        var p = _player!;
        AudioMenu.Items.Clear();
        foreach (var track in p.AudioTracks)
            AudioMenu.Items.Add(TrackItem(track.Label, track.Id == p.AudioId, "audio", () => p.SelectAudio(track.Id)));
        AudioButton.IsEnabled = p.AudioTracks.Count > 1;

        SubtitleMenu.Items.Clear();
        SubtitleMenu.Items.Add(TrackItem("Désactivés", p.SubtitleId <= 0, "subs", () => p.SelectSubtitle(0)));
        foreach (var track in p.SubtitleTracks)
            SubtitleMenu.Items.Add(TrackItem(track.Label, track.Id == p.SubtitleId, "subs", () => p.SelectSubtitle(track.Id)));
        // Addon subtitles not loaded yet, by language.
        var online = _addonSubtitles.Where(s => !_addedSubtitles.Contains(s.Url)).ToList();
        if (online.Count > 0)
        {
            var more = new MenuFlyoutSubItem { Text = $"En ligne ({online.Count})" };
            foreach (var language in online.GroupBy(s => s.Language))
            {
                var langItem = new MenuFlyoutSubItem { Text = $"{language.Key} ({language.Count()})" };
                foreach (var s in language.Take(15)) langItem.Items.Add(Item(s.AddonName, () => AddAddonSubtitle(s)));
                more.Items.Add(langItem);
            }
            SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
            SubtitleMenu.Items.Add(more);
        }
        SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
        SubtitleMenu.Items.Add(Item("Charger un fichier…", () => _ = LoadLocalSubtitleAsync()));
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
            var item = new RadioMenuFlyoutItem { Text = $"{speed:0.##}×", Tag = speed, GroupName = "speed", IsChecked = speed == 1 };
            item.Click += (_, _) => _player?.SetSpeed(speed);
            SpeedMenu.Items.Add(item);
        }
    }

    private void ShowError(string message, bool retry)
    {
        Busy.IsActive = false;
        Busy.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        StatusText.Text = message;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        ShowChrome(keep: true);
    }

    private void ReportProgress()
    {
        if (_player is null || _request.IsLive) return;
        if (_player.Duration > 0 && _player.Position > 0) WatchStore.ReportProgress(_request, _player.Position, _player.Duration);
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
        if (AudioMenu.IsOpen || SubtitleMenu.IsOpen || SpeedMenu.IsOpen || SettingsMenu.IsOpen
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

    private void OnSurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource == Video || e.OriginalSource == Root) _player?.TogglePause();
    }

    private void OnSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource == Video || e.OriginalSource == Root) ToggleFullScreen();
    }

    // -------------------- UI → player --------------------

    private void OnScrub(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingScrubber || _player is null) return;
        // Only jumps count; the 1 s ticks come from playback itself.
        if (Math.Abs(e.NewValue - _player.Position) > 1.5) _player.SeekTo(e.NewValue);
    }

    private void OnVolume(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_syncingVolume) _player?.SetVolume(e.NewValue);
    }

    private void OnTogglePause(object sender, RoutedEventArgs e) => _player?.TogglePause();
    private void OnBack10(object sender, RoutedEventArgs e) => _player?.SeekRelative(-10);
    private void OnForward30(object sender, RoutedEventArgs e) => _player?.SeekRelative(30);
    private void OnToggleMute(object sender, RoutedEventArgs e) => _player?.ToggleMute();
    private async void OnRetry(object sender, RoutedEventArgs e) => await OpenAsync();

    private void ToggleFullScreen()
    {
        var on = !Navigator.Window.IsFullScreen;
        Navigator.Window.SetFullScreen(on);
        FullScreenIcon.Glyph = on ? "" : "";
    }

    private void OnToggleFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        if (_closing) return;
        _closing = true;
        Navigator.Back();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _closing = true;
        ReportProgress();
        _progressTimer?.Stop();
        _hideTimer?.Stop();
        _epgTimer?.Stop();
        _nextTimer?.Stop();
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
