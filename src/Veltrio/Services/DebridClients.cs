using System.Net.Http.Headers;
using System.Text.Json;

namespace Veltrio.Services;

/// <summary>A debrid service refused or failed a request; the message is
/// meant for the user.</summary>
public sealed class DebridException(string message) : Exception(message);

/// <summary>Which file of a torrent to play: the one named after the
/// episode when a season and episode are given, else the biggest video —
/// so a "Sample.mkv" or a pack's first episode isn't picked.</summary>
internal static class DebridFilePicker
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".mov", ".ts", ".m4v", ".webm" };

    public static T? Pick<T>(IEnumerable<T> files, Func<T, string> path, Func<T, long> size, int? season, int? episode) where T : class
    {
        var videos = files.Where(f => VideoExtensions.Contains(Path.GetExtension(path(f)))).ToList();
        if (season is { } s && episode is { } e)
        {
            var patterns = new[] { $"S{s:00}E{e:00}", $"{s}x{e:00}", $"S{s}E{e}", $" {s}x{e}" };
            var match = videos.FirstOrDefault(f => patterns.Any(p => path(f).ToUpperInvariant().Contains(p)));
            if (match is not null) return match;
        }
        return videos.MaxBy(size);
    }
}

internal static class DebridHttp
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };

    static DebridHttp()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("Veltrio-Windows/0.1");
    }

    public static async Task<JsonElement> SendAsync(HttpRequestMessage request, string service)
    {
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DebridException($"{service} injoignable : {ex.Message}");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            JsonElement json = default;
            if (text.Length > 0)
            {
                try { json = JsonDocument.Parse(text).RootElement.Clone(); }
                catch (JsonException) { /* not JSON: handled below */ }
            }
            if (!response.IsSuccessStatusCode)
            {
                var detail = json.ValueKind == JsonValueKind.Object
                    ? Str(json, "error") ?? Str(json, "detail") ?? Str(json, "message")
                    : null;
                throw new DebridException($"{service} a répondu {(int)response.StatusCode}{(detail is null ? "" : $" : {detail}")}");
            }
            return json;
        }
    }

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}

// -------------------- Real-Debrid --------------------

public sealed record RdDeviceCode(string DeviceCode, string UserCode, string VerificationUrl, int Interval, int ExpiresIn);

/// <summary>Real-Debrid: OAuth device flow with the public open-source
/// client id, then magnet → file → unrestricted HTTPS link.
/// https://api.real-debrid.com/</summary>
internal static class RealDebridClient
{
    private const string Api = "https://api.real-debrid.com/rest/1.0/";
    private const string OAuth = "https://api.real-debrid.com/oauth/v2/";
    // The public client id open-source Stremio clients use; no secret.
    private const string ClientId = "X245A4XAIBGVM";

    public static async Task<RdDeviceCode> StartDeviceAuthAsync()
    {
        var json = await DebridHttp.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{OAuth}device/code?client_id={ClientId}&new_credentials=yes"), "Real-Debrid");
        return new RdDeviceCode(
            DebridHttp.Str(json, "device_code")!, DebridHttp.Str(json, "user_code")!,
            DebridHttp.Str(json, "verification_url") ?? "https://real-debrid.com/device",
            (int)Math.Max(5, DebridHttp.Long(json, "interval")), (int)DebridHttp.Long(json, "expires_in"));
    }

    /// <summary>The access token once the user has entered the code, null
    /// while still waiting.</summary>
    public static async Task<string?> PollDeviceAuthAsync(string deviceCode)
    {
        JsonElement creds;
        try
        {
            creds = await DebridHttp.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                $"{OAuth}device/credentials?client_id={ClientId}&code={Uri.EscapeDataString(deviceCode)}"), "Real-Debrid");
        }
        catch (DebridException)
        {
            return null; // 403 until the code is entered
        }
        var id = DebridHttp.Str(creds, "client_id");
        var secret = DebridHttp.Str(creds, "client_secret");
        if (id is null || secret is null) return null;
        var token = await DebridHttp.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{OAuth}token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = id,
                ["client_secret"] = secret,
                ["code"] = deviceCode,
                ["grant_type"] = "http://oauth.net/grant_type/device/1.0",
            }),
        }, "Real-Debrid");
        return DebridHttp.Str(token, "access_token") ?? throw new DebridException("Real-Debrid a refusé l'autorisation.");
    }

    public static async Task<(string Name, bool Premium)> UserAsync(string token)
    {
        var json = await Get("user", token);
        return (DebridHttp.Str(json, "username") ?? "", DebridHttp.Str(json, "type") == "premium");
    }

    /// <summary>Magnet → file → link. Real-Debrid no longer says up front
    /// what it has cached, so this adds the magnet and waits a few seconds
    /// for it to be "downloaded"; a torrent it would have to fetch first is
    /// deleted again and null comes back (another service or the local
    /// engine takes over).</summary>
    public static async Task<string?> ResolveAsync(string infoHash, int? season, int? episode, string token)
    {
        var added = await Post("torrents/addMagnet", token, new() { ["magnet"] = $"magnet:?xt=urn:btih:{infoHash}" });
        var id = DebridHttp.Str(added, "id") ?? throw new DebridException("Real-Debrid n'a pas accepté le magnet.");
        try
        {
            var info = await WaitForAsync(id, token, i => i.TryGetProperty("files", out var f) && f.GetArrayLength() > 0
                || DebridHttp.Str(i, "status") is "magnet_error" or "error" or "dead");
            if (!info.TryGetProperty("files", out var files) || files.GetArrayLength() == 0) { await Delete(id, token); return null; }

            var pick = DebridFilePicker.Pick(files.EnumerateArray().Select(f => (object)f).ToList(),
                f => DebridHttp.Str((JsonElement)f, "path") ?? "", f => DebridHttp.Long((JsonElement)f, "bytes"), season, episode);
            if (pick is null) { await Delete(id, token); throw new DebridException("Aucune vidéo dans ce torrent."); }
            var fileId = DebridHttp.Long((JsonElement)pick, "id");
            if (DebridHttp.Str(info, "status") == "waiting_files_selection")
                await Post($"torrents/selectFiles/{id}", token, new() { ["files"] = fileId.ToString() });

            info = await WaitForAsync(id, token, i => DebridHttp.Str(i, "status") is "downloaded" or "magnet_error" or "error" or "dead");
            if (DebridHttp.Str(info, "status") != "downloaded"
                || !info.TryGetProperty("links", out var links) || links.GetArrayLength() == 0)
            {
                await Delete(id, token);
                return null;
            }
            var link = await Post("unrestrict/link", token, new() { ["link"] = links[0].GetString()! });
            return DebridHttp.Str(link, "download");
        }
        catch
        {
            await Delete(id, token);
            throw;
        }
    }

    private static async Task<JsonElement> WaitForAsync(string id, string token, Func<JsonElement, bool> done)
    {
        var info = await Get($"torrents/info/{id}", token);
        for (var i = 0; i < 12 && !done(info); i++)
        {
            await Task.Delay(700);
            info = await Get($"torrents/info/{id}", token);
        }
        return info;
    }

    private static async Task Delete(string id, string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{Api}torrents/delete/{id}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await DebridHttp.SendAsync(request, "Real-Debrid");
        }
        catch
        {
            // Leftover torrent in the account: harmless.
        }
    }

    private static Task<JsonElement> Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return DebridHttp.SendAsync(request, "Real-Debrid");
    }

    private static Task<JsonElement> Post(string path, string token, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return DebridHttp.SendAsync(request, "Real-Debrid");
    }
}

// -------------------- TorBox --------------------

/// <summary>TorBox, with the API key from the account page.
/// https://api.torbox.app/v1/api</summary>
internal static class TorBoxClient
{
    private const string Api = "https://api.torbox.app/v1/api/";

    public static async Task<(string? Email, bool Paid)> UserAsync(string key)
    {
        var data = await Call(HttpMethod.Get, "user/me", key);
        return (DebridHttp.Str(data, "email"), DebridHttp.Long(data, "plan") > 0);
    }

    public static async Task<Dictionary<string, bool>> CheckCachedAsync(IReadOnlyList<string> hashes, string key)
    {
        var result = new Dictionary<string, bool>();
        foreach (var chunk in hashes.Chunk(50))
        {
            var data = await Call(HttpMethod.Get, $"torrents/checkcached?format=object&hash={string.Join(",", chunk)}", key);
            var found = data.ValueKind == JsonValueKind.Object
                ? data.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).ToHashSet()
                : new HashSet<string>();
            foreach (var h in chunk) result[h.ToLowerInvariant()] = found.Contains(h.ToLowerInvariant());
        }
        return result;
    }

    public static async Task<string> ResolveAsync(string infoHash, int? season, int? episode, string key)
    {
        var form = new MultipartFormDataContent { { new StringContent($"magnet:?xt=urn:btih:{infoHash}"), "magnet" } };
        var created = await Call(HttpMethod.Post, "torrents/createtorrent", key, form);
        var torrentId = DebridHttp.Long(created, "torrent_id");
        var torrent = await Call(HttpMethod.Get, $"torrents/mylist?id={torrentId}&bypass_cache=true", key);
        if (!(torrent.TryGetProperty("download_present", out var present) && present.ValueKind == JsonValueKind.True))
            throw new DebridException("Ce torrent n'est pas en cache sur TorBox.");
        var files = torrent.TryGetProperty("files", out var f) ? f.EnumerateArray().Select(x => (object)x).ToList() : new List<object>();
        var pick = DebridFilePicker.Pick(files, x => DebridHttp.Str((JsonElement)x, "name") ?? "", x => DebridHttp.Long((JsonElement)x, "size"), season, episode)
                   ?? throw new DebridException("Aucune vidéo dans ce torrent.");
        var link = await Call(HttpMethod.Get, $"torrents/requestdl?token={Uri.EscapeDataString(key)}&torrent_id={torrentId}&file_id={DebridHttp.Long((JsonElement)pick, "id")}", key);
        return link.GetString() ?? throw new DebridException("TorBox n'a pas renvoyé de lien.");
    }

    /// <summary>TorBox wraps answers in {success, detail, data}.</summary>
    private static async Task<JsonElement> Call(HttpMethod method, string path, string key, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, Api + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var json = await DebridHttp.SendAsync(request, "TorBox");
        if (json.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True && json.TryGetProperty("data", out var data))
            return data;
        throw new DebridException(DebridHttp.Str(json, "detail") ?? "TorBox a refusé la requête.");
    }
}

// -------------------- Premiumize --------------------

/// <summary>Premiumize, with the API key from the account page.
/// https://www.premiumize.me/api</summary>
internal static class PremiumizeClient
{
    private const string Api = "https://www.premiumize.me/api/";

    public static async Task<bool> AccountAsync(string key)
    {
        var json = await Call("account/info", key);
        return DebridHttp.Long(json, "premium_until") > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static async Task<Dictionary<string, bool>> CheckCachedAsync(IReadOnlyList<string> hashes, string key)
    {
        var result = new Dictionary<string, bool>();
        foreach (var chunk in hashes.Chunk(50))
        {
            var json = await Call("cache/check?" + string.Join("&", chunk.Select(h => $"items[]={h}")), key);
            if (!json.TryGetProperty("response", out var answers)) continue;
            var i = 0;
            foreach (var answer in answers.EnumerateArray())
            {
                if (i >= chunk.Length) break;
                result[chunk[i++].ToLowerInvariant()] = answer.ValueKind == JsonValueKind.True;
            }
        }
        return result;
    }

    /// <summary>transfer/directdl answers a cached torrent with its files' links.</summary>
    public static async Task<string> ResolveAsync(string infoHash, int? season, int? episode, string key)
    {
        var json = await Call("transfer/directdl", key, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["src"] = $"magnet:?xt=urn:btih:{infoHash}",
        }));
        var files = json.TryGetProperty("content", out var c) ? c.EnumerateArray().Select(x => (object)x).ToList() : new List<object>();
        var pick = DebridFilePicker.Pick(files, x => DebridHttp.Str((JsonElement)x, "path") ?? "", x => DebridHttp.Long((JsonElement)x, "size"), season, episode);
        return (pick is null ? null : DebridHttp.Str((JsonElement)pick, "link")) ?? throw new DebridException("Aucune vidéo dans ce torrent.");
    }

    private static async Task<JsonElement> Call(string path, string key, HttpContent? form = null)
    {
        var url = $"{Api}{path}{(path.Contains('?') ? '&' : '?')}apikey={Uri.EscapeDataString(key)}";
        var json = await DebridHttp.SendAsync(new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, url) { Content = form }, "Premiumize");
        if (DebridHttp.Str(json, "status") is { } status && status != "success")
            throw new DebridException(DebridHttp.Str(json, "message") ?? "Premiumize a refusé la requête.");
        return json;
    }
}
