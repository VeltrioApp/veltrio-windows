namespace Veltrio.Services;

/// <summary>Tells Trakt and SIMKL what one playback is doing: "start" once
/// it's under way and again after a pause, "pause", and "stop" when the
/// player closes (80 % or more marks the title watched on both). Only
/// titles keyed by an IMDB id; fire-and-forget.</summary>
public sealed class Scrobbler(PlayRequest request)
{
    private string? _sent;

    private bool Applies =>
        !request.IsLive && request.ContentId is { } id && id.StartsWith("tt")
        && (TraktService.IsConnected || SimklService.IsConnected);

    /// <summary>Called with each progress report; sends only on changes.</summary>
    public void Update(double position, double duration, bool paused)
    {
        if (duration <= 0 || position <= 0) return;
        Send(paused ? "pause" : "start", position, duration);
    }

    public void Stop(double position, double duration)
    {
        if (_sent is null || duration <= 0) return;
        Send("stop", position, duration);
    }

    private void Send(string action, double position, double duration)
    {
        if (action == _sent || !Applies) return;
        _sent = action;
        var progress = position / duration * 100;
        var id = request.ContentId!;
        _ = TraktService.ScrobbleAsync(action, id, request.Season, request.Episode, progress);
        _ = SimklService.ScrobbleAsync(action, id, request.Season, request.Episode, progress);
    }
}
