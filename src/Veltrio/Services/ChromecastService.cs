using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Devices.Enumeration;

namespace Veltrio.Services;

/// <summary>Chromecast, as on macOS, without Google's SDK: devices are found
/// over DNS-SD (_googlecast._tcp, through Windows' own discovery), and
/// casting speaks Cast V2 — a TLS connection to the device (port 8009,
/// self-signed certificate) carrying length-prefixed protobuf CastMessages
/// with JSON inside. The Default Media Receiver (CC1AD845) is launched and
/// asked to LOAD the URL, which the Chromecast fetches itself: debrid, IPTV,
/// media servers and addon links work; the local torrent server
/// (127.0.0.1) doesn't.</summary>
public static class ChromecastService
{
    public sealed record Device(string Name, string Host, int Port);

    public enum CastState { Idle, Connecting, Casting, Failed }

    private static DeviceWatcher? _watcher;
    private static readonly Dictionary<string, Device> Found = new();
    private static CastSession? _session;

    public static IReadOnlyList<Device> Devices
    {
        get { lock (Found) return Found.Values.OrderBy(d => d.Name).ToList(); }
    }

    public static CastState State { get; private set; }
    public static string? DeviceName { get; private set; }
    public static string? Error { get; private set; }
    public static bool IsCasting => State is CastState.Connecting or CastState.Casting;

    /// <summary>Devices or the cast state changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    private static void Notify() => Changed?.Invoke();

    // -------------------- discovery --------------------

    private const string HostName = "System.Devices.Dnssd.HostName";
    private const string Port = "System.Devices.Dnssd.PortNumber";
    private const string Txt = "System.Devices.Dnssd.TextAttributes";
    private const string Addresses = "System.Devices.IpAddress";

    public static void StartDiscovery()
    {
        if (_watcher is not null) return;
        // DNS-SD services ("Aep" = association endpoint) on the local domain.
        const string query = "System.Devices.AepService.ProtocolId:=\"{4526e8c1-8aac-4153-9b16-55e86ada0e54}\" AND " +
                             "System.Devices.Dnssd.Domain:=\"local\" AND System.Devices.Dnssd.ServiceName:=\"_googlecast._tcp\"";
        try
        {
            _watcher = DeviceInformation.CreateWatcher(query, new[] { HostName, Port, Txt, Addresses }, DeviceInformationKind.AssociationEndpointService);
            _watcher.Added += (_, info) => Upsert(info.Id, info.Name, info.Properties);
            _watcher.Updated += (_, update) => Upsert(update.Id, null, update.Properties);
            _watcher.Removed += (_, update) =>
            {
                lock (Found) Found.Remove(update.Id);
                Notify();
            };
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Player.PlayerLog.Write($"[cast] discovery: {ex.Message}");
            _watcher = null;
        }
    }

    public static void StopDiscovery()
    {
        try { _watcher?.Stop(); }
        catch { /* already stopped */ }
        _watcher = null;
    }

    private static void Upsert(string id, string? instance, IReadOnlyDictionary<string, object> properties)
    {
        lock (Found)
        {
            var known = Found.GetValueOrDefault(id);
            // The friendly name is the TXT record's "fn"; the instance name is an opaque id.
            var name = properties.TryGetValue(Txt, out var txt) && txt is string[] attributes
                ? attributes.FirstOrDefault(a => a.StartsWith("fn="))?[3..]
                : null;
            name ??= known?.Name ?? instance ?? "Chromecast";
            var host = properties.TryGetValue(Addresses, out var ips) && ips is string[] { Length: > 0 } list
                ? list.FirstOrDefault(a => !a.Contains(':')) ?? list[0]
                : properties.TryGetValue(HostName, out var h) && h is string hostName ? hostName : known?.Host;
            var port = properties.TryGetValue(Port, out var p) && p is ushort number ? number : known?.Port ?? 8009;
            if (host is null) return;
            Found[id] = new Device(name, host, port);
        }
        Notify();
    }

    // -------------------- casting --------------------

    /// <summary>Whether a Chromecast could fetch this URL itself.</summary>
    public static bool CanCast(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && !(uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "::1");

    public static void Cast(Device device, string url, string title, string? poster, bool isLive, double startAt)
    {
        Stop();
        State = CastState.Connecting;
        DeviceName = device.Name;
        Error = null;
        Notify();
        var session = new CastSession(device, new CastSession.Media(url, title, poster, isLive, startAt));
        session.Event += e =>
        {
            if (_session != session) return;
            switch (e)
            {
                case CastSession.Outcome.Playing:
                    State = CastState.Casting;
                    break;
                case CastSession.Outcome.Closed:
                    if (State == CastState.Connecting)
                    {
                        State = CastState.Failed;
                        Error = "Le Chromecast a fermé la connexion.";
                    }
                    else
                    {
                        State = CastState.Idle;
                    }
                    _session = null;
                    break;
                default:
                    State = CastState.Failed;
                    Error = session.Failure ?? "Le Chromecast n'a pas pu lire ce flux.";
                    _session = null;
                    break;
            }
            Notify();
        };
        _session = session;
        _ = session.RunAsync();
    }

    public static void Stop()
    {
        var session = _session;
        _session = null;
        session?.Stop();
        if (State != CastState.Idle)
        {
            State = CastState.Idle;
            Notify();
        }
    }
}

/// <summary>One connection to one device, playing one item.</summary>
internal sealed class CastSession(ChromecastService.Device device, CastSession.Media media)
{
    public sealed record Media(string Url, string Title, string? Poster, bool IsLive, double StartAt);

    public enum Outcome { Playing, Failed, Closed }

    private const string DefaultMediaReceiver = "CC1AD845";
    private const string NsConnection = "urn:x-cast:com.google.cast.tp.connection";
    private const string NsHeartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    private const string NsReceiver = "urn:x-cast:com.google.cast.receiver";
    private const string NsMedia = "urn:x-cast:com.google.cast.media";

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SslStream? _stream;
    private int _requestId = 1;
    private string? _sessionId;
    private bool _loaded;
    private bool _playing;

    public event Action<Outcome>? Event;
    public string? Failure { get; private set; }

    public async Task RunAsync()
    {
        try
        {
            using var tcp = new TcpClient();
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(8));
                await tcp.ConnectAsync(device.Host, device.Port, connect.Token);
            }
            // Chromecasts present a device certificate no CA signs.
            _stream = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await _stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = device.Host }, _stop.Token);
            await SendAsync(NsConnection, "receiver-0", new JsonObject { ["type"] = "CONNECT" });
            await SendAsync(NsReceiver, "receiver-0", new JsonObject { ["type"] = "LAUNCH", ["appId"] = DefaultMediaReceiver, ["requestId"] = NextId() });
            _ = HeartbeatAsync();
            while (!_stop.IsCancellationRequested && await ReadAsync() is { } message)
                if (!await HandleAsync(message)) break;
            Event?.Invoke(Outcome.Closed);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Stopped by the user.
        }
        catch (Exception ex) when (_playing && ex is IOException or SocketException)
        {
            // The device went away or another sender took over.
            Event?.Invoke(Outcome.Closed);
        }
        catch (Exception ex)
        {
            Failure = $"Connexion au Chromecast impossible : {ex.Message}";
            Event?.Invoke(Outcome.Failed);
        }
        finally
        {
            _stop.Cancel();
        }
    }

    public void Stop()
    {
        if (_stream is not null && _sessionId is { } session)
            _ = SendAsync(NsReceiver, "receiver-0", new JsonObject { ["type"] = "STOP", ["sessionId"] = session, ["requestId"] = NextId() })
                .ContinueWith(_ => _stop.Cancel());
        else
            _stop.Cancel();
    }

    private int NextId() => Interlocked.Increment(ref _requestId);

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                await SendAsync(NsHeartbeat, "receiver-0", new JsonObject { ["type"] = "PING" });
            }
        }
        catch
        {
            // The session is over.
        }
    }

    /// <summary>False when the session is over.</summary>
    private async Task<bool> HandleAsync(CastMessage message)
    {
        JsonObject? json;
        try { json = JsonNode.Parse(message.Payload) as JsonObject; }
        catch (JsonException) { return true; }
        switch (json?["type"]?.GetValue<string>())
        {
            case "PING":
                await SendAsync(NsHeartbeat, message.Source, new JsonObject { ["type"] = "PONG" });
                break;
            case "RECEIVER_STATUS":
                var app = (json["status"]?["applications"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(a => a["appId"]?.GetValue<string>() == DefaultMediaReceiver);
                if (_loaded || app?["transportId"]?.GetValue<string>() is not { } transport) break;
                _loaded = true;
                _sessionId = app["sessionId"]?.GetValue<string>();
                await SendAsync(NsConnection, transport, new JsonObject { ["type"] = "CONNECT" });
                await SendAsync(NsMedia, transport, LoadRequest());
                break;
            case "MEDIA_STATUS":
                var state = (json["status"] as JsonArray)?.FirstOrDefault()?["playerState"]?.GetValue<string>();
                if (!_playing && state is "PLAYING" or "BUFFERING")
                {
                    _playing = true;
                    Event?.Invoke(Outcome.Playing);
                }
                break;
            case "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST" or "LAUNCH_ERROR":
                Failure = "Le Chromecast n'a pas pu lire ce flux.";
                Event?.Invoke(Outcome.Failed);
                _stop.Cancel();
                return false;
            case "CLOSE":
                return false;
        }
        return true;
    }

    private JsonObject LoadRequest()
    {
        var metadata = new JsonObject { ["metadataType"] = 0, ["title"] = media.Title };
        if (media.Poster is { } poster) metadata["images"] = new JsonArray(new JsonObject { ["url"] = poster });
        return new JsonObject
        {
            ["type"] = "LOAD",
            ["requestId"] = NextId(),
            ["autoplay"] = true,
            ["currentTime"] = media.IsLive ? 0 : media.StartAt,
            ["media"] = new JsonObject
            {
                ["contentId"] = media.Url,
                ["contentType"] = ContentType(media.Url),
                ["streamType"] = media.IsLive ? "LIVE" : "BUFFERED",
                ["metadata"] = metadata,
            },
        };
    }

    public static string ContentType(string url) =>
        Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant() switch
        {
            ".m3u8" => "application/x-mpegURL",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".ts" => "video/mp2t",
            _ => "video/mp4",
        };

    private async Task SendAsync(string ns, string destination, JsonObject payload)
    {
        if (_stream is null) return;
        var body = new CastMessage("sender-0", destination, ns, payload.ToJsonString()).Encode();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        await _writeLock.WaitAsync();
        try { await _stream.WriteAsync(frame, _stop.Token); }
        finally { _writeLock.Release(); }
    }

    private async Task<CastMessage?> ReadAsync()
    {
        var header = new byte[4];
        await _stream!.ReadExactlyAsync(header, _stop.Token);
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is <= 0 or > 1 << 20) return null;
        var body = new byte[length];
        await _stream.ReadExactlyAsync(body, _stop.Token);
        return CastMessage.Decode(body) ?? new CastMessage("", "", "", "{}");
    }
}

/// <summary>The six fields of CastMessage (cast_channel.proto), encoded by
/// hand: 1 protocol_version (CASTV2_1_0 = 0), 2 source_id, 3 destination_id,
/// 4 namespace, 5 payload_type (STRING = 0), 6 payload_utf8.</summary>
internal sealed record CastMessage(string Source, string Destination, string Namespace, string Payload)
{
    public byte[] Encode()
    {
        var d = new List<byte>();
        void Varint(ulong v)
        {
            while (v >= 0x80)
            {
                d.Add((byte)(v & 0x7F | 0x80));
                v >>= 7;
            }
            d.Add((byte)v);
        }
        void Text(int field, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            Varint((ulong)(field << 3 | 2));
            Varint((ulong)bytes.Length);
            d.AddRange(bytes);
        }
        Varint(1 << 3); Varint(0);
        Text(2, Source);
        Text(3, Destination);
        Text(4, Namespace);
        Varint(5 << 3); Varint(0);
        Text(6, Payload);
        return d.ToArray();
    }

    public static CastMessage? Decode(byte[] bytes)
    {
        var fields = new Dictionary<int, string>();
        var i = 0;
        ulong? Varint()
        {
            ulong result = 0;
            for (var shift = 0; i < bytes.Length && shift < 64; shift += 7)
            {
                var b = bytes[i++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            return null;
        }
        while (i < bytes.Length)
        {
            if (Varint() is not { } tag) return null;
            switch (tag & 7)
            {
                case 0:
                    if (Varint() is null) return null;
                    break;
                case 2:
                    if (Varint() is not { } length || i + (int)length > bytes.Length) return null;
                    fields[(int)(tag >> 3)] = Encoding.UTF8.GetString(bytes, i, (int)length);
                    i += (int)length;
                    break;
                default:
                    return null;
            }
        }
        return fields.TryGetValue(4, out var ns)
            ? new CastMessage(fields.GetValueOrDefault(2, ""), fields.GetValueOrDefault(3, ""), ns, fields.GetValueOrDefault(6, ""))
            : null;
    }
}
