using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Veltrio;

/// <summary>The app's own entry point (XAML's generated Main is disabled):
/// Velopack must run first, so the installer's install, update and
/// uninstall hooks are handled before any window appears.</summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
