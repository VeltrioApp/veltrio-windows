using Velopack;
using Velopack.Sources;

namespace Veltrio.Services;

/// <summary>Updates from the repository's GitHub releases, through
/// Velopack. Only an installed copy (Setup.exe) updates itself; a build run
/// from the source tree or the portable zip says so and does nothing.
/// Betas are releases too: the app is in beta.</summary>
public static class Updater
{
    private const string Repository = "https://github.com/VeltrioApp/veltrio-windows";

    private static readonly UpdateManager Manager = new(new GithubSource(Repository, null, prerelease: true));
    private static UpdateInfo? _ready;

    public enum UpdateState { NotInstalled, Idle, Checking, UpToDate, Downloading, Ready, Failed }

    public static UpdateState State { get; private set; } = Manager.IsInstalled ? UpdateState.Idle : UpdateState.NotInstalled;
    public static string? NewVersion { get; private set; }
    public static string? Error { get; private set; }
    public static string CurrentVersion => Manager.CurrentVersion?.ToString() ?? "développement";

    /// <summary>State changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    private static void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }

    /// <summary>Looks for a newer release and downloads it; it's applied
    /// when the app closes, or right away with <see cref="RestartNow"/>.</summary>
    public static async Task CheckAsync()
    {
        if (!Manager.IsInstalled || State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Ready) return;
        Error = null;
        Set(UpdateState.Checking);
        try
        {
            if (await Manager.CheckForUpdatesAsync() is not { } update)
            {
                Set(UpdateState.UpToDate);
                return;
            }
            NewVersion = update.TargetFullRelease.Version.ToString();
            Set(UpdateState.Downloading);
            await Manager.DownloadUpdatesAsync(update);
            _ready = update;
            // Applied as soon as the app exits, if not restarted before.
            Manager.WaitExitThenApplyUpdates(update, silent: true, restart: false);
            Set(UpdateState.Ready);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Set(UpdateState.Failed);
        }
    }

    public static void RestartNow()
    {
        if (_ready is { } update) Manager.ApplyUpdatesAndRestart(update);
    }
}
