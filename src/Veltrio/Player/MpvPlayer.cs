using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Veltrio.Player;

/// <summary>An audio or subtitle track as mpv reports it.</summary>
public sealed record MediaTrack(long Id, string Type, string? Lang, string? Title, string? Codec, bool IsDefault, bool IsForced)
{
    private static readonly Dictionary<string, string> Languages = new()
    {
        ["fre"] = "Français", ["fra"] = "Français", ["fr"] = "Français",
        ["eng"] = "Anglais", ["en"] = "Anglais",
        ["spa"] = "Espagnol", ["es"] = "Espagnol",
        ["ger"] = "Allemand", ["deu"] = "Allemand", ["de"] = "Allemand",
        ["ita"] = "Italien", ["it"] = "Italien",
        ["por"] = "Portugais", ["pt"] = "Portugais",
        ["rus"] = "Russe", ["ru"] = "Russe",
        ["jpn"] = "Japonais", ["ja"] = "Japonais",
        ["kor"] = "Coréen", ["ko"] = "Coréen",
        ["chi"] = "Chinois", ["zho"] = "Chinois", ["zh"] = "Chinois",
        ["ara"] = "Arabe", ["ar"] = "Arabe",
        ["dut"] = "Néerlandais", ["nld"] = "Néerlandais", ["nl"] = "Néerlandais",
        ["tur"] = "Turc", ["tr"] = "Turc",
        ["pol"] = "Polonais", ["pl"] = "Polonais",
        ["hin"] = "Hindi", ["hi"] = "Hindi",
        ["hun"] = "Hongrois", ["hu"] = "Hongrois",
        ["swe"] = "Suédois", ["sv"] = "Suédois",
        ["ukr"] = "Ukrainien", ["uk"] = "Ukrainien",
    };

    private static readonly string[][] SynonymGroups =
    {
        new[] { "fr", "fre", "fra" }, new[] { "en", "eng" }, new[] { "es", "spa" }, new[] { "de", "ger", "deu" },
        new[] { "it", "ita" }, new[] { "pt", "por", "pob" }, new[] { "ru", "rus" }, new[] { "ja", "jpn" },
        new[] { "ko", "kor" }, new[] { "zh", "chi", "zho" }, new[] { "ar", "ara" }, new[] { "nl", "dut", "nld" },
        new[] { "sv", "swe" }, new[] { "pl", "pol" }, new[] { "tr", "tur" }, new[] { "hi", "hin" }, new[] { "hu", "hun" },
    };

    /// <summary>A language code and its 2/3-letter equivalents (fr ↔ fre ↔ fra).</summary>
    public static HashSet<string> Synonyms(string code)
    {
        var lower = code.ToLowerInvariant();
        var set = new HashSet<string> { lower };
        foreach (var group in SynonymGroups) if (group.Contains(lower)) set.UnionWith(group);
        return set;
    }

    /// <summary>"Français" for fre/fra/fr, the code in capitals otherwise.</summary>
    public static string? LanguageName(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Languages.GetValueOrDefault(code.ToLowerInvariant(), code.ToUpperInvariant());

    /// <summary>The track's title, else its language, else its number.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Title) ? Title!
        : Lang is { Length: > 0 } l ? Languages.GetValueOrDefault(l.ToLowerInvariant(), l.ToUpperInvariant())
        : $"Piste {Id}";

    /// <summary>"FRE · EAC3 · Forcé": what the name doesn't say.</summary>
    public string SecondaryLabel
    {
        get
        {
            var extras = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title) && Lang is { Length: > 0 } lang) extras.Add(lang.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(Codec)) extras.Add(Codec!.ToUpperInvariant());
            if (IsForced) extras.Add("Forcé");
            return string.Join(" · ", extras);
        }
    }

    /// <summary>"Français   EAC3", the label the menus show (as macOS).</summary>
    public string Label => SecondaryLabel.Length == 0 ? DisplayName : $"{DisplayName}   {SecondaryLabel}";
}

/// <summary>One mpv instance: options as on macOS, an event thread, and
/// property changes raised on the UI thread. Video goes through
/// <see cref="VideoSurface"/> (vo=libmpv).</summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly Thread _events;
    private volatile bool _disposed;

    internal IntPtr Handle { get; private set; }

    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsBuffering { get; private set; }
    public double Volume { get; private set; } = 100;
    public bool IsMuted { get; private set; }
    public double Speed { get; private set; } = 1;
    public long AudioId { get; private set; }
    public long SubtitleId { get; private set; }
    public double SubDelay { get; private set; }
    public double AudioDelay { get; private set; }
    public long Chapter { get; private set; } = -1;
    public IReadOnlyList<(string Title, double Start)> Chapters { get; private set; } = Array.Empty<(string, double)>();
    public IReadOnlyList<MediaTrack> AudioTracks { get; private set; } = Array.Empty<MediaTrack>();
    public IReadOnlyList<MediaTrack> SubtitleTracks { get; private set; } = Array.Empty<MediaTrack>();

    /// <summary>The last warning/error mpv logged, for the error screen.</summary>
    public string? LastLog { get; private set; }

    /// <summary>A property changed (name), on the UI thread.</summary>
    public event Action<string>? Changed;
    public event Action? FileLoaded;
    /// <summary>Playback ended: null at the natural end, else why it failed.</summary>
    public event Action<string?>? Ended;

    private enum Prop : ulong { TimePos = 1, Duration, Pause, PausedForCache, Volume, Mute, Speed, Aid, Sid, TrackList, SubDelay, AudioDelay, Chapter, EofReached }

    public MpvPlayer(DispatcherQueue ui)
    {
        _ui = ui;
        Handle = LibMpv.Create();
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("Impossible de créer le lecteur mpv.");

        // Same set as the macOS MPVPlayer.
        Set("vo", "libmpv");
        Set("force-window", "no");
        Set("terminal", "no");
        Set("config", "no");
        Set("input-default-bindings", "no");
        Set("input-vo-keyboard", "no");
        Set("osc", "no");
        Set("ytdl", "no");
        // Addons hand us URLs; keep mpv off local files and odd protocols.
        Set("demuxer-lavf-o", "protocol_whitelist=[file,crypto,data,http,https,tcp,tls,hls,rtmp,rtmps,udp,rtp]");
        Set("load-unsafe-playlists", "no");
        // mpv's TLS (OpenSSL) can't rely on Windows' root store, which only
        // fetches roots on demand: verify against curl's Mozilla bundle.
        var caFile = Path.Combine(AppContext.BaseDirectory, "cacert.pem");
        if (File.Exists(caFile)) Set("tls-ca-file", caFile);
        Set("tls-verify", "yes");
        var prefs = PlayerPrefs.Current;
        Set("hwdec", prefs.Hwdec);
        Set("keep-open", "yes");
        Set("audio-channels", "auto-safe");
        Set("cache", "yes");
        Set("demuxer-max-bytes", $"{prefs.CacheMiB}MiB");
        Set("demuxer-max-back-bytes", $"{prefs.CacheMiB / 2}MiB");
        // The preferred language under all its codes (fr, fre, fra); English
        // as the audio fallback.
        if (prefs.AudioLang.Length > 0)
            Set("alang", string.Join(",", MediaTrack.Synonyms(prefs.AudioLang).Concat(prefs.AudioLang == "eng" ? Array.Empty<string>() : new[] { "en", "eng" })));
        if (prefs.SubtitleLang.Length > 0) Set("slang", string.Join(",", MediaTrack.Synonyms(prefs.SubtitleLang)));
        Set("sub-font-size", "46");

        var error = LibMpv.Initialize(Handle);
        if (error < 0) throw new InvalidOperationException($"mpv : {LibMpv.Error(error)}");
        LibMpv.RequestLogMessages(Handle, "info");

        LibMpv.ObserveProperty(Handle, (ulong)Prop.TimePos, "time-pos", LibMpv.FormatDouble);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Duration, "duration", LibMpv.FormatDouble);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Pause, "pause", LibMpv.FormatFlag);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.PausedForCache, "paused-for-cache", LibMpv.FormatFlag);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Volume, "volume", LibMpv.FormatDouble);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Mute, "mute", LibMpv.FormatFlag);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Speed, "speed", LibMpv.FormatDouble);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Aid, "aid", LibMpv.FormatInt64);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Sid, "sid", LibMpv.FormatInt64);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.TrackList, "track-list/count", LibMpv.FormatInt64);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.SubDelay, "sub-delay", LibMpv.FormatDouble);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.AudioDelay, "audio-delay", LibMpv.FormatDouble);
        LibMpv.ObserveProperty(Handle, (ulong)Prop.Chapter, "chapter", LibMpv.FormatInt64);
        // keep-open: at the end mpv holds the last frame and sets eof-reached
        // instead of ending the file.
        LibMpv.ObserveProperty(Handle, (ulong)Prop.EofReached, "eof-reached", LibMpv.FormatFlag);

        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    private void Set(string name, string value)
    {
        var error = LibMpv.SetOptionString(Handle, name, value);
        if (error < 0) PlayerLog.Write($"[mpv] option {name}={value}: {LibMpv.Error(error)}");
    }

    // -------------------- commands --------------------

    /// <summary>Opens a URL, starting at <paramref name="start"/> seconds.</summary>
    public void Load(string url, double start = 0)
    {
        LibMpv.SetPropertyString(Handle, "start", start > 0 ? start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "none");
        LibMpv.Command(Handle, "loadfile", url, "replace");
    }

    public void TogglePause() => LibMpv.Command(Handle, "cycle", "pause");
    public void SetPaused(bool paused) => LibMpv.SetPropertyString(Handle, "pause", paused ? "yes" : "no");
    public void SeekRelative(double seconds) => LibMpv.Command(Handle, "seek", Num(seconds), "relative");
    public void SeekTo(double seconds) => LibMpv.Command(Handle, "seek", Num(seconds), "absolute");
    public void SetVolume(double volume) => LibMpv.SetPropertyString(Handle, "volume", Num(Math.Clamp(volume, 0, 130)));
    public void ToggleMute() => LibMpv.Command(Handle, "cycle", "mute");
    public void SetSpeed(double speed) => LibMpv.SetPropertyString(Handle, "speed", Num(speed));
    public void SelectAudio(long id) => LibMpv.SetPropertyString(Handle, "aid", id <= 0 ? "no" : id.ToString());
    public void SelectSubtitle(long id) => LibMpv.SetPropertyString(Handle, "sid", id <= 0 ? "no" : id.ToString());

    public void SetSubDelay(double seconds) => LibMpv.SetPropertyString(Handle, "sub-delay", Num(Math.Round(seconds, 2)));
    public void SetAudioDelay(double seconds) => LibMpv.SetPropertyString(Handle, "audio-delay", Num(Math.Round(seconds, 2)));

    /// <summary>Loads a subtitle file or URL and selects it.</summary>
    public void AddSubtitle(string url, string? title = null, string? lang = null)
    {
        var args = new List<string> { "sub-add", url, "select" };
        if (title is not null) { args.Add(title); if (lang is not null) args.Add(lang); }
        LibMpv.Command(Handle, args.ToArray());
    }

    public void SetAspect(AspectMode mode)
    {
        LibMpv.SetPropertyString(Handle, "video-aspect-override", mode.MpvAspect());
        LibMpv.SetPropertyString(Handle, "panscan", mode == AspectMode.Fill ? "1.0" : "0.0");
        LibMpv.SetPropertyString(Handle, "keepaspect", mode == AspectMode.Stretch ? "no" : "yes");
    }

    /// <summary>0 = fit; positive enlarges (crops), negative shrinks. log2 scale, as mpv.</summary>
    public void SetZoom(double zoom) => LibMpv.SetPropertyString(Handle, "video-zoom", Num(Math.Clamp(zoom, -1, 2)));

    public void SetToneMapping(ToneMapping mapping)
    {
        LibMpv.SetPropertyString(Handle, "tone-mapping", mapping.MpvValue());
        LibMpv.SetPropertyString(Handle, "hdr-compute-peak", "yes");
    }

    /// <summary>Bitstream AC3/E-AC3/DTS/TrueHD to an amplifier.</summary>
    public void SetPassthrough(bool on) => LibMpv.SetPropertyString(Handle, "audio-spdif", on ? "ac3,eac3,dts,dts-hd,truehd" : "");

    /// <summary>One-click tuning, the macOS profiles.</summary>
    public void ApplyProfile(PlaybackProfile profile)
    {
        void P(string name, string value) => LibMpv.SetPropertyString(Handle, name, value);
        switch (profile)
        {
            case PlaybackProfile.Quality:
                P("scale", "ewa_lanczossharp"); P("dscale", "mitchell"); P("cscale", "spline36");
                P("deband", "yes"); P("deband-iterations", "4"); P("video-sync", "display-resample");
                P("hdr-compute-peak", "yes");
                break;
            case PlaybackProfile.Performance:
                P("scale", "bilinear"); P("dscale", "bilinear"); P("cscale", "bilinear");
                P("deband", "no"); P("video-sync", "audio");
                break;
            case PlaybackProfile.Audiophile:
                P("audio-buffer", "0.5"); P("audio-exclusive", "yes");
                break;
            default:
                P("scale", "spline36"); P("dscale", "mitchell"); P("cscale", "spline36");
                P("deband", "yes"); P("video-sync", "display-resample");
                break;
        }
    }

    /// <summary>What the media-info panel lists, read now.</summary>
    public IReadOnlyList<(string Label, string Value)> MediaInfo()
    {
        var info = new List<(string, string)>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) info.Add((label, value)); }
        var w = GetString("width");
        var h = GetString("height");
        Add("Résolution", w is null || h is null ? null : $"{w} × {h}");
        Add("Codec vidéo", GetString("video-codec"));
        Add("Images/s", double.TryParse(GetString("estimated-vf-fps"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps) ? $"{fps:0.###}" : null);
        Add("Débit vidéo", long.TryParse(GetString("video-bitrate"), out var vb) && vb > 0 ? $"{vb / 1_000_000.0:0.0} Mb/s" : null);
        var gamma = GetString("video-params/gamma") ?? "";
        Add("Plage dynamique", gamma is "pq" or "hlg" ? $"HDR ({gamma.ToUpperInvariant()})" : gamma.Length > 0 ? "SDR" : null);
        Add("Primaires", GetString("video-params/primaries"));
        Add("Décodage", GetString("hwdec-current") is { Length: > 0 } hw && hw != "no" ? $"matériel ({hw})" : "logiciel");
        Add("Codec audio", GetString("audio-codec-name") ?? GetString("audio-codec"));
        Add("Canaux audio", GetString("audio-params/channel-count"));
        Add("Débit audio", long.TryParse(GetString("audio-bitrate"), out var ab) && ab > 0 ? $"{ab / 1000} kb/s" : null);
        Add("Conteneur", GetString("file-format"));
        Add("Cache", double.TryParse(GetString("demuxer-cache-duration"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cache) ? $"{cache:0} s" : null);
        return info;
    }

    private void ReadChapters()
    {
        var count = int.TryParse(GetString("chapter-list/count"), out var n) ? n : 0;
        var list = new List<(string, double)>();
        for (var i = 0; i < count; i++)
        {
            var title = GetString($"chapter-list/{i}/title") ?? $"Chapitre {i + 1}";
            var start = double.TryParse(GetString($"chapter-list/{i}/time"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0;
            list.Add((title, start));
        }
        Post(() => { Chapters = list; Changed?.Invoke("chapters"); });
    }

    private static readonly string[] IntroWords = { "intro", "opening", "op ", "theme song", "generique", "générique", "ouverture" };

    /// <summary>Where "Skip intro" jumps to: the next chapter's start, when
    /// the current chapter's title reads like an intro.</summary>
    public double? IntroEnd
    {
        get
        {
            if (Chapter < 0 || Chapter + 1 >= Chapters.Count) return null;
            var title = Chapters[(int)Chapter].Title.ToLowerInvariant() + " ";
            return IntroWords.Any(title.Contains) ? Chapters[(int)Chapter + 1].Start : null;
        }
    }

    /// <summary>Raises subtitles above the control bar while it shows.</summary>
    public void SetSubtitleLift(bool lifted) => LibMpv.SetPropertyString(Handle, "sub-pos", lifted ? "84" : "100");

    private static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private string? GetString(string name)
    {
        var p = LibMpv.GetPropertyString(Handle, name);
        if (p == IntPtr.Zero) return null;
        try { return LibMpv.Utf8(p); }
        finally { LibMpv.Free(p); }
    }

    // -------------------- events --------------------

    private void EventLoop()
    {
        while (!_disposed)
        {
            var e = LibMpv.WaitEvent(Handle, -1);
            if (e == null) continue;
            switch (e->EventId)
            {
                case LibMpv.EventShutdown:
                    return;
                case LibMpv.EventPropertyChange:
                    OnProperty((Prop)e->ReplyUserdata, (LibMpv.EventProperty*)e->Data);
                    break;
                case LibMpv.EventFileLoaded:
                    ReadTracks();
                    ReadChapters();
                    Post(() => FileLoaded?.Invoke());
                    break;
                case LibMpv.EventEnd:
                    var end = (LibMpv.EventEndFile*)e->Data;
                    PlayerLog.Write($"[mpv] end-file reason={end->Reason} error={end->Error}");
                    if (end->Reason == LibMpv.EndFileError)
                    {
                        var why = LibMpv.Error(end->Error);
                        Post(() => Ended?.Invoke(LastLog is null ? why : $"{why} — {LastLog}"));
                    }
                    else if (end->Reason == LibMpv.EndFileEof)
                    {
                        Post(() => Ended?.Invoke(null));
                    }
                    break;
                case LibMpv.EventLog:
                    var log = (LibMpv.EventLogMessage*)e->Data;
                    var text = $"{LibMpv.Utf8(log->Prefix)}: {LibMpv.Utf8(log->Text)?.TrimEnd()}";
                    PlayerLog.Write($"[mpv] {text}");
                    if (LibMpv.Utf8(log->Level) is "error" or "fatal") LastLog = text;
                    break;
            }
        }
    }

    private void OnProperty(Prop prop, LibMpv.EventProperty* p)
    {
        // FormatNone: the property is unavailable (no file, "no" track…).
        var has = p->Format != LibMpv.FormatNone && p->Data != IntPtr.Zero;
        double D() => has ? *(double*)p->Data : 0;
        bool F() => has && *(int*)p->Data != 0;
        long L() => has ? *(long*)p->Data : 0;

        switch (prop)
        {
            case Prop.TimePos: { var v = D(); Post(() => { Position = v; Changed?.Invoke("time-pos"); }); break; }
            case Prop.Duration: { var v = D(); Post(() => { Duration = v; Changed?.Invoke("duration"); }); break; }
            case Prop.Pause: { var v = F(); Post(() => { IsPaused = v; Changed?.Invoke("pause"); }); break; }
            case Prop.PausedForCache: { var v = F(); Post(() => { IsBuffering = v; Changed?.Invoke("buffering"); }); break; }
            case Prop.Volume: { var v = D(); Post(() => { Volume = v; Changed?.Invoke("volume"); }); break; }
            case Prop.Mute: { var v = F(); Post(() => { IsMuted = v; Changed?.Invoke("mute"); }); break; }
            case Prop.Speed: { var v = has ? D() : 1; Post(() => { Speed = v; Changed?.Invoke("speed"); }); break; }
            case Prop.Aid: { var v = L(); Post(() => { AudioId = v; Changed?.Invoke("aid"); }); break; }
            case Prop.Sid: { var v = L(); Post(() => { SubtitleId = v; Changed?.Invoke("sid"); }); break; }
            case Prop.TrackList: ReadTracks(); break;
            case Prop.SubDelay: { var v = D(); Post(() => { SubDelay = v; Changed?.Invoke("sub-delay"); }); break; }
            case Prop.AudioDelay: { var v = D(); Post(() => { AudioDelay = v; Changed?.Invoke("audio-delay"); }); break; }
            case Prop.Chapter: { var v = has ? L() : -1; Post(() => { Chapter = v; Changed?.Invoke("chapter"); }); break; }
            case Prop.EofReached: if (F()) Post(() => Ended?.Invoke(null)); break;
        }
    }

    private void ReadTracks()
    {
        var count = int.TryParse(GetString("track-list/count"), out var n) ? n : 0;
        var audio = new List<MediaTrack>();
        var subs = new List<MediaTrack>();
        for (var i = 0; i < count; i++)
        {
            var type = GetString($"track-list/{i}/type");
            if (type is not ("audio" or "sub")) continue;
            var track = new MediaTrack(
                long.TryParse(GetString($"track-list/{i}/id"), out var id) ? id : 0,
                type,
                GetString($"track-list/{i}/lang"),
                GetString($"track-list/{i}/title"),
                GetString($"track-list/{i}/codec"),
                GetString($"track-list/{i}/default") == "yes",
                GetString($"track-list/{i}/forced") == "yes");
            (type == "audio" ? audio : subs).Add(track);
        }
        Post(() =>
        {
            AudioTracks = audio;
            SubtitleTracks = subs;
            Changed?.Invoke("tracks");
        });
    }

    private void Post(Action action)
    {
        if (!_disposed) _ui.TryEnqueue(() => { if (!_disposed) action(); });
    }

    /// <summary>Call after the <see cref="VideoSurface"/> is disposed: its
    /// render context must go before the core.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LibMpv.Command(Handle, "stop");
        LibMpv.Wakeup(Handle);
        // terminate_destroy can block while mpv drains a slow stream: off
        // the UI thread, as on macOS. The event thread is done with the
        // handle first.
        _ = Task.Run(() =>
        {
            _events.Join(TimeSpan.FromSeconds(5));
            LibMpv.TerminateDestroy(Handle);
            Handle = IntPtr.Zero;
        });
    }
}
