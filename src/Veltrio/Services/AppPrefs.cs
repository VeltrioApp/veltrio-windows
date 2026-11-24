using System.Globalization;

namespace Veltrio.Services;

public enum AppTheme { System, Dark, Light }

/// <summary>App-wide choices (app-prefs.json). The theme applies at launch:
/// WinUI only takes the app's theme before the first window.</summary>
public sealed class AppPrefs
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool OnboardingDone { get; set; }
    /// <summary>"" = follow Windows, "fr" / "en" / "es" / "de" / "it".</summary>
    public string AppLanguage { get; set; } = "";
    public bool KidsMode { get; set; }
    public string KidsPin { get; set; } = "";

    private static readonly string[] AdultGroupKeywords =
    {
        "adult", "xxx", "18+", "+18", "porn", "erotic", "érotique", "erotique",
        "charme", "nsfw", "x-rated", "for adults", "adults only",
    };

    public static bool IsAdultIptvGroup(string group)
    {
        var g = group.ToLowerInvariant();
        return AdultGroupKeywords.Any(k => g.Contains(k, StringComparison.Ordinal));
    }

    public static void SetKidsMode(bool enabled)
    {
        Current.KidsMode = enabled;
        Current.Save();
        CoreService.ClearCatalogCache();
        TmdbService.NotifyChanged();
    }

    private const string File = "app-prefs.json";
    private static AppPrefs? _current;
    public static AppPrefs Current => _current ??= Storage.Load(File, new AppPrefs());
    public void Save() => Storage.Save(File, this);

    public static event Action? LanguageChanged;

    public static string ResolvedCode => Current.AppLanguage switch
    {
        "fr" or "en" or "es" or "de" or "it" => Current.AppLanguage,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "fr" => "fr",
            "es" => "es",
            "de" => "de",
            "it" => "it",
            _ => "en",
        },
    };

    public static bool IsFrench => ResolvedCode == "fr";

    public static string Tr(string fr, string en, string? es = null, string? de = null, string? it = null) => ResolvedCode switch
    {
        "fr" => fr,
        "es" => es ?? en,
        "de" => de ?? en,
        "it" => it ?? en,
        _ => en,
    };

    public static string TmdbLanguage(string code) => code switch
    {
        "fr" => "fr-FR",
        "en" => "en-US",
        "es" => "es-ES",
        "de" => "de-DE",
        "it" => "it-IT",
        _ => TmdbLanguage(ResolvedCode),
    };

    public static void SetAppLanguage(string code)
    {
        Current.AppLanguage = code;
        Current.Save();
        TmdbService.SetLanguage(TmdbLanguage(code));
        LanguageChanged?.Invoke();
    }

    /// <summary>The theme the app runs in: System follows Windows' app mode.</summary>
    public static bool IsLight(AppTheme theme) => theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        // Windows' app background is white in light mode.
        _ => new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R > 128,
    };
}
