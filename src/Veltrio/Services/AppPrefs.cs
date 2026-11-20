namespace Veltrio.Services;

public enum AppTheme { System, Dark, Light }

/// <summary>App-wide choices (app-prefs.json). The theme applies at launch:
/// WinUI only takes the app's theme before the first window.</summary>
public sealed class AppPrefs
{
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    private const string File = "app-prefs.json";
    private static AppPrefs? _current;
    public static AppPrefs Current => _current ??= Storage.Load(File, new AppPrefs());
    public void Save() => Storage.Save(File, this);

    /// <summary>The theme the app runs in: System follows Windows' app mode.</summary>
    public static bool IsLight(AppTheme theme) => theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        // Windows' app background is white in light mode.
        _ => new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R > 128,
    };
}
