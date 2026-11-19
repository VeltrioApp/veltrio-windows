namespace Veltrio.Player;

/// <summary>player.log next to the app's data: what mpv and the video
/// pipeline report, for diagnosing playback on machines we can't attach to.
/// Truncated each time the player opens.</summary>
internal static class PlayerLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(Services.Storage.Folder, "player.log");

    public static void Reset()
    {
        lock (Gate)
        {
            try { Directory.CreateDirectory(Services.Storage.Folder); File.WriteAllText(Path, ""); }
            catch { /* logging is best effort */ }
        }
    }

    public static void Write(string line)
    {
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch { /* logging is best effort */ }
        }
    }
}
