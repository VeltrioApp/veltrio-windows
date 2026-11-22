namespace Veltrio.Player;

/// <summary>Picture framing, as the macOS player offers.</summary>
public enum AspectMode { Auto, Fill, Stretch, R16x9, R4x3, R21x9, R235 }

public enum ToneMapping { Auto, Bt2446a, Bt2390, Hable, Reinhard, Clip }

public enum PlaybackProfile { Balanced, Quality, Performance, Audiophile }

public static class PlaybackOptionLabels
{
    public static string Label(this AspectMode m) => m switch
    {
        AspectMode.Fill => "Remplir (rogner)",
        AspectMode.Stretch => "Étirer",
        AspectMode.R16x9 => "16:9",
        AspectMode.R4x3 => "4:3",
        AspectMode.R21x9 => "21:9",
        AspectMode.R235 => "2,35:1",
        _ => "Automatique",
    };

    public static string MpvAspect(this AspectMode m) => m switch
    {
        AspectMode.R16x9 => "16:9",
        AspectMode.R4x3 => "4:3",
        AspectMode.R21x9 => "21:9",
        AspectMode.R235 => "2.35",
        _ => "-1",
    };

    public static string Label(this ToneMapping t) => t switch
    {
        ToneMapping.Bt2446a => "BT.2446a (équilibré)",
        ToneMapping.Bt2390 => "BT.2390 (cinéma)",
        ToneMapping.Hable => "Hable",
        ToneMapping.Reinhard => "Reinhard",
        ToneMapping.Clip => "Aucun (écrêter)",
        _ => "Automatique",
    };

    public static string MpvValue(this ToneMapping t) => t switch
    {
        ToneMapping.Bt2446a => "bt.2446a",
        ToneMapping.Bt2390 => "bt.2390",
        ToneMapping.Hable => "hable",
        ToneMapping.Reinhard => "reinhard",
        ToneMapping.Clip => "clip",
        _ => "auto",
    };

    public static string Label(this PlaybackProfile p) => p switch
    {
        PlaybackProfile.Quality => "Qualité maximale",
        PlaybackProfile.Performance => "Performances",
        PlaybackProfile.Audiophile => "Audiophile",
        _ => "Équilibré",
    };

    public static string Blurb(this PlaybackProfile p) => p switch
    {
        PlaybackProfile.Quality => "Meilleure image, plus de calcul ; tone mapping HDR soigné.",
        PlaybackProfile.Performance => "Peu de calcul, peu d'énergie ; filtres d'image simples.",
        PlaybackProfile.Audiophile => "Son en bitstream (DTS, AC3…) vers l'ampli ; tampon audio plus grand.",
        _ => "Réglages sûrs, bons sur toute machine.",
    };
}

/// <summary>Player choices that stick between sessions (player-prefs.json).</summary>
public sealed class PlayerPrefs
{
    public PlaybackProfile Profile { get; set; } = PlaybackProfile.Balanced;
    public ToneMapping ToneMapping { get; set; } = ToneMapping.Auto;
    public bool Passthrough { get; set; }
    public bool AutoNextEpisode { get; set; } = true;
    /// <summary>ISO 639-2 codes; empty = no preference (the file's default track).</summary>
    public string AudioLang { get; set; } = "fre";
    public string SubtitleLang { get; set; } = "fre";
    /// <summary>mpv's hwdec: auto-safe, auto, d3d11va or no.</summary>
    public string Hwdec { get; set; } = "auto-safe";
    /// <summary>Read-ahead buffer for network streams, in MiB.</summary>
    public int CacheMiB { get; set; } = 150;
    /// <summary>Subtitle size multiplier (mpv sub-scale).</summary>
    public double SubtitleScale { get; set; } = 1.0;
    /// <summary>Subtitle text color (mpv sub-color).</summary>
    public string SubtitleColor { get; set; } = "#FFFFFFFF";

    public static readonly (string Code, string Name)[] SubtitleScales =
    {
        ("0.75", "Petit"), ("1", "Normal"), ("1.25", "Grand"), ("1.5", "Très grand"),
    };

    public static readonly (string Code, string Name)[] SubtitleColors =
    {
        ("#FFFFFFFF", "Blanc"), ("#FFFFE066", "Jaune"), ("#FFD0D0D0", "Gris clair"),
    };

    /// <summary>Languages offered in Settings, ISO 639-2 as mpv and the servers report them.</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("", "Automatique"), ("fre", "Français"), ("eng", "Anglais"), ("spa", "Espagnol"), ("ger", "Allemand"),
        ("ita", "Italien"), ("por", "Portugais"), ("rus", "Russe"), ("jpn", "Japonais"), ("kor", "Coréen"),
        ("chi", "Chinois"), ("ara", "Arabe"), ("dut", "Néerlandais"), ("swe", "Suédois"), ("pol", "Polonais"),
        ("tur", "Turc"), ("hin", "Hindi"),
    };

    private const string File = "player-prefs.json";
    private static PlayerPrefs? _current;
    public static PlayerPrefs Current => _current ??= Services.Storage.Load(File, new PlayerPrefs());
    public void Save() => Services.Storage.Save(File, this);
}
