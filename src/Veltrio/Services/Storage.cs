using System.Text.Json;

namespace Veltrio.Services;

/// <summary>JSON files under %LOCALAPPDATA%\Veltrio, the Windows
/// counterpart of the macOS app's Application Support folder.</summary>
public static class Storage
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Veltrio");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string file, T fallback)
    {
        try
        {
            var path = Path.Combine(Folder, file);
            if (!File.Exists(path)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Writes through a temporary file so a crash mid-write
    /// can't leave a truncated file behind.</summary>
    public static void Save<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, file);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Losing a save is better than crashing the UI.
        }
    }
}
