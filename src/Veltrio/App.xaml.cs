using Microsoft.UI.Xaml;

namespace Veltrio;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        // A crash otherwise just closes the window: leave a trace testers can send.
        UnhandledException += (_, e) => WriteCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception);
        InitializeComponent();
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
        _window = new MainWindow();
        _window.Activate();
    }
}
