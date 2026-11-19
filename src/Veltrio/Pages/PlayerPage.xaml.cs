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
/// (torrents through the core's engine), resumes where the user stopped,
/// reports progress every 10 s and on close, and offers audio/subtitle
/// tracks, speed and volume.</summary>
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
    }

    private void OnToggleChannels(object sender, RoutedEventArgs e)
    {
        ChannelPanel.Visibility = ChannelPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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

    private async void OnReplay(object sender, RoutedEventArgs e)
    {
        if (_request.Channel is not { XtreamId: { } id } channel || _request.Playlist?.Xtream is not { } account) return;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        menu.Items.Add(new MenuFlyoutItem { Text = "Chargement…", IsEnabled = false });
        menu.ShowAt((FrameworkElement)sender);
        ShowChrome(keep: true);
        var programmes = await XtreamClient.CatchupAsync(account, id);
        menu.Items.Clear();
        if (programmes.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "Aucun programme à revoir.", IsEnabled = false });
        foreach (var p in programmes.Take(40))
        {
            var item = new MenuFlyoutItem { Text = $"{p.TimeRange}   {p.Title}" };
            item.Click += (_, _) =>
            {
                _closing = true;
                Navigator.Zap(new PlayRequest
                {
                    Title = channel.Name,
                    Subtitle = $"Replay · {p.TimeRange} · {p.Title}",
                    Url = XtreamClient.TimeshiftUrl(account, id, p),
                    Channel = channel,
                    Playlist = _request.Playlist,
                });
            };
            menu.Items.Add(item);
        }
    }

    // -------------------- player → UI --------------------

    private void OnFileLoaded()
    {
        _loaded = true;
        Status.Visibility = Visibility.Collapsed;
        ShowChrome();
    }

    private void OnEnded(string? error)
    {
        if (error is not null)
        {
            ShowError($"Lecture impossible. {error}", retry: true);
            return;
        }
        // Natural end of a film or episode: record it as watched and leave.
        ReportProgress();
        if (!_request.IsLive) Close();
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
        SubtitleButton.IsEnabled = p.SubtitleTracks.Count > 0;
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
        if (AudioMenu.IsOpen || SubtitleMenu.IsOpen || SpeedMenu.IsOpen || ChannelPanel.Visibility == Visibility.Visible) { _hideTimer?.Start(); return; }
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
