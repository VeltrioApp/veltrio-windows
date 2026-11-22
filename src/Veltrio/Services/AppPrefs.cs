using System.Globalization;

namespace Veltrio.Services;

public enum AppTheme { System, Dark, Light }

/// <summary>App-wide choices (app-prefs.json). The theme applies at launch:
/// WinUI only takes the app's theme before the first window.</summary>
public sealed class AppPrefs
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public bool OnboardingDone { get; set; }
    /// <summary>"" = follow Windows, "fr" = Français, "en" = English.</summary>
    public string AppLanguage { get; set; } = "";

    private const string File = "app-prefs.json";
    private static AppPrefs? _current;
    public static AppPrefs Current => _current ??= Storage.Load(File, new AppPrefs());
    public void Save() => Storage.Save(File, this);

    public static event Action? LanguageChanged;

    public static bool IsFrench => Current.AppLanguage switch
    {
        "fr" => true,
        "en" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase),
    };

    public static string Tr(string fr, string en) => IsFrench ? fr : en;

    public static void SetAppLanguage(string code)
    {
        Current.AppLanguage = code;
        Current.Save();
        TmdbService.SetLanguage(code switch
        {
            "fr" => "fr-FR",
            "en" => "en-US",
            _ => IsFrench ? "fr-FR" : "en-US",
        });
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
