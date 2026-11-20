namespace Veltrio.Services;

public enum DownloadStatus { Downloading, Paused, Completed, Failed }

/// <summary>A film or episode saved for offline playback.</summary>
public sealed class DownloadItem
{
    /// <summary>The video id: tt123 for a film, tt123:1:3 for an episode.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? ContentType { get; set; }
    public string? ContentId { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public string? Poster { get; set; }
    public string SourceUrl { get; set; } = "";
    /// <summary>Set for a torrent played through the local engine: stopped
    /// once the file is down.</summary>
    public string? InfoHash { get; set; }
    public string LocalPath { get; set; } = "";
    public long BytesExpected { get; set; }
    public long BytesDownloaded { get; set; }
    public DownloadStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public double Progress => BytesExpected > 0 ? Math.Min(1, (double)BytesDownloaded / BytesExpected) : 0;
}

/// <summary>Offline copies, as on macOS: a stream resolved to an HTTP(S) URL
/// (direct, debrid, or the local torrent engine's server) is saved under
/// Downloads in the data folder. A stopped or interrupted download resumes
/// where it left off when the server supports ranges.</summary>
public static class DownloadManager
{
    private const string File = "downloads.json";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Dictionary<string, CancellationTokenSource> Running = new();
    private static readonly List<DownloadItem> _items = Load();

    public static string Folder { get; } = Path.Combine(Storage.Folder, "Downloads");
    public static IReadOnlyList<DownloadItem> Items => _items;

    /// <summary>A download changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    static DownloadManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Veltrio-Windows/0.1");
    }

    private static List<DownloadItem> Load()
    {
        var items = Storage.Load(File, new List<DownloadItem>());
        // Nothing runs at launch: an interrupted download waits to be resumed.
        foreach (var item in items.Where(i => i.Status == DownloadStatus.Downloading)) item.Status = DownloadStatus.Paused;
        return items;
    }

    private static void Save()
    {
        lock (_items) Storage.Save(File, _items.ToList());
        Changed?.Invoke();
    }

    public static DownloadItem? Item(string id) => _items.FirstOrDefault(i => i.Id == id);

    /// <summary>The finished local file for a video id, if any.</summary>
    public static string? LocalFile(string id) =>
        Item(id) is { Status: DownloadStatus.Completed } item && System.IO.File.Exists(item.LocalPath) ? item.LocalPath : null;

    /// <summary>Resolves the stream, then downloads it. The request carries
    /// what the player would need, so the copy plays with the same progress.</summary>
    public static async Task StartAsync(string id, PlayRequest request)
    {
        if (Item(id) is { Status: DownloadStatus.Downloading or DownloadStatus.Completed }) return;
        var item = Item(id) ?? new DownloadItem { Id = id };
        item.Title = request.Title;
        item.Subtitle = request.Subtitle;
        item.ContentType = request.ContentType;
        item.ContentId = request.ContentId;
        item.Season = request.Season;
        item.Episode = request.Episode;
        item.Poster = request.Poster;
        item.Status = DownloadStatus.Downloading;
        item.Error = null;
        if (!_items.Contains(item)) _items.Add(item);
        Save();
        try
        {
            item.SourceUrl = request.Url ?? await CoreService.ResolveAsync(request.Stream!, request.Season, request.Episode);
            item.InfoHash = request.Stream?.InfoHash;
        }
        catch (Exception ex)
        {
            Fail(item, CoreService.Humanize(ex));
            return;
        }
        var ext = Path.GetExtension(new Uri(item.SourceUrl).AbsolutePath);
        if (ext.Length is < 2 or > 5) ext = ".mkv";
        Directory.CreateDirectory(Folder);
        item.LocalPath = Path.Combine(Folder, string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ext);
        Run(item);
    }

    public static void Resume(string id)
    {
        if (Item(id) is not { Status: DownloadStatus.Paused or DownloadStatus.Failed } item || item.SourceUrl.Length == 0) return;
        item.Status = DownloadStatus.Downloading;
        item.Error = null;
        Save();
        Run(item);
    }

    public static void Pause(string id)
    {
        lock (Running) if (Running.Remove(id, out var cts)) cts.Cancel();
    }

    public static void Remove(string id)
    {
        Pause(id);
        if (Item(id) is not { } item) return;
        _items.Remove(item);
        try { System.IO.File.Delete(item.LocalPath); }
        catch { /* gone already, or still being closed */ }
        Save();
    }

    private static void Run(DownloadItem item)
    {
        var cts = new CancellationTokenSource();
        lock (Running) Running[item.Id] = cts;
        _ = Task.Run(() => DownloadAsync(item, cts.Token));
    }

    private static async Task DownloadAsync(DownloadItem item, CancellationToken token)
    {
        try
        {
            var have = System.IO.File.Exists(item.LocalPath) ? new FileInfo(item.LocalPath).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, item.SourceUrl);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            // A server that ignores the range sends the whole file again.
            var resuming = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resuming) have = 0;
            item.BytesExpected = have + (response.Content.Headers.ContentLength ?? 0);
            item.BytesDownloaded = have;
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(item.LocalPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[1 << 16];
            var lastReport = DateTime.UtcNow;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                item.BytesDownloaded += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(1))
                {
                    lastReport = DateTime.UtcNow;
                    Changed?.Invoke();
                }
            }
            item.BytesExpected = Math.Max(item.BytesExpected, item.BytesDownloaded);
            item.Status = DownloadStatus.Completed;
            Save();
            StopEngine(item);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            item.Status = DownloadStatus.Paused;
            Save();
        }
        catch (Exception ex)
        {
            Fail(item, ex.Message);
        }
        finally
        {
            lock (Running) Running.Remove(item.Id);
        }
    }

    private static void Fail(DownloadItem item, string message)
    {
        item.Status = DownloadStatus.Failed;
        item.Error = message;
        Save();
        StopEngine(item);
        Player.PlayerLog.Write($"[download] {item.Id}: {message}");
    }

    private static void StopEngine(DownloadItem item)
    {
        if (item.InfoHash is { } hash) CoreService.StopTorrent(hash);
    }
}
