using Microsoft.UI.Xaml;

namespace Veltrio;

public partial class App : Application
{
    private Window? _window;
    private readonly bool _light;

    public App()
    {
        // A crash otherwise just closes the window: leave a trace testers can send.
        UnhandledException += (_, e) => WriteCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        // The theme can only be chosen before the app's resources load.
        _light = Services.AppPrefs.IsLight(Services.AppPrefs.Current.Theme);
        RequestedTheme = _light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        InitializeComponent();
    }

    /// <summary>The macOS app's light side: warm paper, near-black type, the
    /// same amber (darker as text, for contrast).</summary>
    private static readonly (string Key, uint Argb)[] LightPalette =
    {
        ("VeltrioBackgroundBrush", 0xFFF7F3EC), ("VeltrioBackgroundAltBrush", 0xFFEFE9DF),
        ("VeltrioSurfaceBrush", 0xFFFFFFFF), ("VeltrioSurfaceHoverBrush", 0xFFECE6DB), ("VeltrioSurfaceElevatedBrush", 0xFFE4DDCF),
        ("VeltrioBorderBrush", 0x1A000000), ("VeltrioBorderStrongBrush", 0x47000000), ("VeltrioHairlineBrush", 0x24000000),
        ("VeltrioTextPrimaryBrush", 0xFF0E0E12), ("VeltrioTextSecondaryBrush", 0xB3000000),
        ("VeltrioTextTertiaryBrush", 0x75000000), ("VeltrioTextQuaternaryBrush", 0x4D000000),
        ("VeltrioAccentTextBrush", 0xFF9C5E0C),
        ("VeltrioCanvasColor", 0xFFF7F3EC), ("VeltrioCanvas80Color", 0xCCF7F3EC), ("VeltrioCanvas65Color", 0xA6F7F3EC),
        ("VeltrioCanvas55Color", 0x8CF7F3EC), ("VeltrioCanvas35Color", 0x59F7F3EC), ("VeltrioCanvas0Color", 0x00F7F3EC),
    };

    /// <summary>Brushes are recoloured in place, so everything already
    /// holding them (styles, templates) follows; colours are replaced at the
    /// root, where pages loaded later find them first.</summary>
    private void ApplyLightPalette()
    {
        foreach (var (key, argb) in LightPalette)
        {
            var color = Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            if (key.EndsWith("Brush"))
            {
                // The indexer also searches the merged dictionaries.
                if (Resources[key] is Microsoft.UI.Xaml.Media.SolidColorBrush brush) brush.Color = color;
            }
            else
            {
                Resources[key] = color;
            }
        }
    }

    private static void WriteCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Services.Storage.Folder);
            File.AppendAllText(Path.Combine(Services.Storage.Folder, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing left to do.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Resources can't be touched from the constructor; before the
        // window is still early enough for every page.
        if (_light) ApplyLightPalette();
        _window = new MainWindow();
        _window.Activate();
    }
}
