using System.Runtime.InteropServices;
using System.Text;

namespace Veltrio.Services;

/// <summary>API keys and tokens, encrypted for the current Windows user
/// with DPAPI (the role the Keychain plays on macOS) in secrets.dat.</summary>
public static class SecretStore
{
    private const string File = "secrets.dat";
    private static readonly Dictionary<string, string> Values = Load();

    public static string? Get(string name) => Values.GetValueOrDefault(name);

    public static void Set(string name, string? value)
    {
        if (value is null) Values.Remove(name); else Values[name] = value;
        Save();
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            var path = Path.Combine(Storage.Folder, File);
            if (!System.IO.File.Exists(path)) return new();
            var json = Encoding.UTF8.GetString(Unprotect(System.IO.File.ReadAllBytes(path)));
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            // Unreadable (another user's file, corrupted): start over.
            return new();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Storage.Folder);
            var bytes = Protect(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(Values)));
            System.IO.File.WriteAllBytes(Path.Combine(Storage.Folder, File), bytes);
        }
        catch
        {
            // Keys then live for this session only.
        }
    }

    // -------------------- DPAPI --------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, "Veltrio", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
