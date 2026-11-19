using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Veltrio.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Veltrio.Pages;

/// <summary>Full-window player. Resolves the stream (torrents through the
/// core's engine), resumes where the user stopped, and reports progress
/// every 10 s and on close, like the macOS player.</summary>
public sealed partial class PlayerPage : Page
{
    private PlayRequest _request = null!;
    private MediaPlayer? _player;
    private DispatcherQueueTimer? _progressTimer;
    private DispatcherQueueTimer? _hideTimer;
    private double _resumeAt;
    private bool _closing;

    public PlayerPage()
    {
        InitializeComponent();
        AddAccelerator(Windows.System.VirtualKey.Escape, () => { if (Navigator.Window.IsFullScreen) ToggleFullScreen(); else Close(); });
        AddAccelerator(Windows.System.VirtualKey.F, ToggleFullScreen);
        AddAccelerator(Windows.System.VirtualKey.Space, TogglePause);
        AddAccelerator(Windows.System.VirtualKey.Left, () => Seek(-10));
        AddAccelerator(Windows.System.VirtualKey.Right, () => Seek(30));
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void AddAccelerator(Windows.System.VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, e) => { action(); e.Handled = true; };
        KeyboardAccelerators.Add(accelerator);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not PlayRequest request) return;
        _request = request;
        TitleText.Text = request.Title;
        SubtitleText.Text = request.Subtitle ?? "";
        LiveBadge.Visibility = request.IsLive ? Visibility.Visible : Visibility.Collapsed;

        string url;
        try
        {
            if (request.Url is { } direct)
            {
                url = direct;
            }
            else
            {
                StatusText.Text = request.Stream?.InfoHash is null ? "Préparation du flux…" : "Connexion aux pairs du torrent…";
                url = await CoreService.ResolveAsync(request.Stream!);
            }
        }
        catch (Exception ex)
        {
            ShowError($"Impossible d'ouvrir ce flux. {CoreService.Humanize(ex)}");
            return;
        }
        if (_closing) return;

        _resumeAt = request.ContentType is { } type && request.ContentId is { } id
            ? WatchStore.ResumePosition(type, id, request.VideoId)
            : 0;

        _player = new MediaPlayer { AutoPlay = true };
        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.Source = MediaSource.CreateFromUri(new Uri(url));
        Player.SetMediaPlayer(_player);
        StatusText.Text = "Chargement…";

        if (!request.IsLive)
        {
            _progressTimer = DispatcherQueue.CreateTimer();
            _progressTimer.Interval = TimeSpan.FromSeconds(10);
            _progressTimer.Tick += (_, _) => ReportProgress();
            _progressTimer.Start();
        }

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = TimeSpan.FromSeconds(3);
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => TopBar.Visibility = Visibility.Collapsed;
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Status.Visibility = Visibility.Collapsed;
            if (_resumeAt > 0) sender.PlaybackSession.Position = TimeSpan.FromSeconds(_resumeAt);
            _hideTimer?.Start();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() => ShowError(
            "Le lecteur de Windows ne sait pas lire ce flux (format ou codec non pris en charge). "
            + "Essayez un autre flux, de préférence en MP4 / H.264."
            + (string.IsNullOrEmpty(args.ErrorMessage) ? "" : $"\n\n{args.ErrorMessage}")));
    }

    private void ShowError(string message)
    {
        Busy.IsActive = false;
        Busy.Visibility = Visibility.Collapsed;
        Status.Visibility = Visibility.Visible;
        StatusText.Text = message;
        TopBar.Visibility = Visibility.Visible;
    }

    private void ReportProgress()
    {
        if (_player?.PlaybackSession is not { } session) return;
        var duration = session.NaturalDuration.TotalSeconds;
        var position = session.Position.TotalSeconds;
        if (duration > 0 && position > 0) WatchStore.ReportProgress(_request, position, duration);
    }

    private void TogglePause()
    {
        if (_player is null) return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause(); else _player.Play();
    }

    private void Seek(double seconds)
    {
        if (_player?.PlaybackSession is not { CanSeek: true } session) return;
        var target = session.Position + TimeSpan.FromSeconds(seconds);
        session.Position = target < TimeSpan.Zero ? TimeSpan.Zero : target;
    }

    private void ToggleFullScreen()
    {
        var on = !Navigator.Window.IsFullScreen;
        Navigator.Window.SetFullScreen(on);
        FullScreenIcon.Glyph = on ? "" : "";
    }

    private void OnToggleFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        TopBar.Visibility = Visibility.Visible;
        _hideTimer?.Stop();
        if (Status.Visibility == Visibility.Collapsed) _hideTimer?.Start();
    }

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
        if (!_request.IsLive) ReportProgress();
        _progressTimer?.Stop();
        _hideTimer?.Stop();
        if (_player is not null)
        {
            _player.MediaOpened -= OnMediaOpened;
            _player.MediaFailed -= OnMediaFailed;
            _player.Pause();
            Player.SetMediaPlayer(null);
            _player.Dispose();
            _player = null;
        }
        if (_request.Stream?.InfoHash is { } hash) CoreService.StopTorrent(hash);
    }
}
