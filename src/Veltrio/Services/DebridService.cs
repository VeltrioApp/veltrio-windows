using uniffi.veltrio;

namespace Veltrio.Services;

public enum DebridProvider { RealDebrid, AllDebrid, Premiumize, TorBox }

/// <summary>One provider's state, as the Settings cards show it.</summary>
public sealed class DebridAccount
{
    public required DebridProvider Provider { get; init; }
    public required string Name { get; init; }
    public required string Tagline { get; init; }
    public required string Color { get; init; }
    /// <summary>Sign-in with a code shown in the app (RD, AD) or an API key (TorBox, Premiumize).</summary>
    public required bool UsesCode { get; init; }
    public required string KeyPageUrl { get; init; }

    public string? Key { get; internal set; }
    public string? User { get; internal set; }
    public bool? Premium { get; internal set; }
    public bool IsConnected => Key is not null;

    /// <summary>A code flow in progress: what to show and where to go.</summary>
    public string? PendingCode { get; internal set; }
    public string? PendingUrl { get; internal set; }
    public string? Error { get; internal set; }
    public bool IsBusy { get; internal set; }
}

/// <summary>Debrid orchestration, as on macOS: provider sign-in, a cache
/// map per provider for the ⚡ badges, and resolving a torrent through the
/// first service that can serve it.</summary>
public static class DebridService
{
    private static readonly AllDebridClient AllDebrid = new();
    private static readonly Dictionary<(DebridProvider, string), bool> Cached = new();
    private static CancellationTokenSource? _flow;

    public static readonly IReadOnlyList<DebridAccount> Accounts = new[]
    {
        new DebridAccount { Provider = DebridProvider.RealDebrid, Name = "Real-Debrid", Color = "#6CAD3E", UsesCode = true,
            Tagline = "Le plus répandu. Connexion par code, sans mot de passe.", KeyPageUrl = "https://real-debrid.com/device" },
        new DebridAccount { Provider = DebridProvider.AllDebrid, Name = "AllDebrid", Color = "#16A34A", UsesCode = true,
            Tagline = "Connexion par code PIN. Lit aussi ce qui n'est pas encore en cache.", KeyPageUrl = "https://alldebrid.com/pin/" },
        new DebridAccount { Provider = DebridProvider.Premiumize, Name = "Premiumize", Color = "#FFA200", UsesCode = false,
            Tagline = "Clé d'API à copier depuis la page de votre compte.", KeyPageUrl = "https://www.premiumize.me/account" },
        new DebridAccount { Provider = DebridProvider.TorBox, Name = "TorBox", Color = "#4EA2FF", UsesCode = false,
            Tagline = "Clé d'API à copier depuis les réglages de votre compte.", KeyPageUrl = "https://torbox.app/settings" },
    };

    /// <summary>An account or the cache map changed (any thread → marshal yourself).</summary>
    public static event Action? Changed;

    public static DebridAccount Account(DebridProvider p) => Accounts.First(a => a.Provider == p);
    public static bool AnyConnected => Accounts.Any(a => a.IsConnected);

    private static string SecretName(DebridProvider p) => $"debrid.{p}".ToLowerInvariant();

    static DebridService()
    {
        foreach (var account in Accounts)
        {
            account.Key = SecretStore.Get(SecretName(account.Provider));
            if (account.Key is not null) _ = RefreshUserAsync(account);
        }
    }

    private static void Notify() => Changed?.Invoke();

    private static async Task RefreshUserAsync(DebridAccount account)
    {
        try
        {
            var key = account.Key!;
            switch (account.Provider)
            {
                case DebridProvider.RealDebrid:
                    (account.User, var rdPremium) = await RealDebridClient.UserAsync(key);
                    account.Premium = rdPremium;
                    break;
                case DebridProvider.AllDebrid:
                    var ad = await AllDebrid.UserInfo(key);
                    account.User = ad.username;
                    account.Premium = ad.isPremium;
                    break;
                case DebridProvider.TorBox:
                    (account.User, var paid) = await TorBoxClient.UserAsync(key);
                    account.Premium = paid;
                    break;
                case DebridProvider.Premiumize:
                    account.Premium = await PremiumizeClient.AccountAsync(key);
                    break;
            }
        }
        catch
        {
            // Offline or revoked: the card still shows "connected".
        }
        Notify();
    }

    // -------------------- sign-in --------------------

    /// <summary>TorBox / Premiumize: checks the key before keeping it, so a
    /// typo shows here rather than as streams that never resolve.</summary>
    public static async Task ConnectWithKeyAsync(DebridProvider provider, string raw)
    {
        var account = Account(provider);
        var key = raw.Trim();
        if (key.Length == 0) return;
        account.Error = null;
        account.IsBusy = true;
        Notify();
        try
        {
            account.Key = key;
            await (provider == DebridProvider.TorBox ? (Task)TorBoxClient.UserAsync(key) : PremiumizeClient.AccountAsync(key));
            SecretStore.Set(SecretName(provider), key);
            await RefreshUserAsync(account);
        }
        catch (Exception ex)
        {
            account.Key = null;
            account.Error = $"{account.Name} a refusé cette clé. {ex.Message}";
        }
        finally
        {
            account.IsBusy = false;
            Notify();
        }
    }

    /// <summary>Real-Debrid device code or AllDebrid PIN: shows a code, then
    /// polls until the user has entered it on the provider's site.</summary>
    public static async Task StartCodeFlowAsync(DebridProvider provider)
    {
        CancelCodeFlow();
        var account = Account(provider);
        var flow = _flow = new CancellationTokenSource();
        account.Error = null;
        account.IsBusy = true;
        Notify();
        try
        {
            if (provider == DebridProvider.RealDebrid)
            {
                var code = await RealDebridClient.StartDeviceAuthAsync();
                account.PendingCode = code.UserCode;
                account.PendingUrl = code.VerificationUrl;
                Notify();
                var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(code.Interval), flow.Token);
                    if (await RealDebridClient.PollDeviceAuthAsync(code.DeviceCode) is { } token)
                    {
                        Connected(account, token);
                        return;
                    }
                }
            }
            else
            {
                var pin = await AllDebrid.StartPinAuth();
                account.PendingCode = pin.pin;
                account.PendingUrl = pin.userUrl;
                Notify();
                var deadline = DateTime.UtcNow.AddSeconds(pin.expiresInSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2.5), flow.Token);
                    try
                    {
                        if (await AllDebrid.PollPin(pin.pin, pin.checkToken) is { } key)
                        {
                            Connected(account, key);
                            return;
                        }
                    }
                    catch (VeltrioException)
                    {
                        // Transient; the deadline ends the loop.
                    }
                }
            }
            account.Error = "Le code a expiré. Recommencez.";
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user or by another flow.
        }
        catch (Exception ex)
        {
            account.Error = CoreService.Humanize(ex);
        }
        finally
        {
            account.PendingCode = account.PendingUrl = null;
            account.IsBusy = false;
            Notify();
        }
    }

    public static void CancelCodeFlow()
    {
        _flow?.Cancel();
        _flow = null;
    }

    private static void Connected(DebridAccount account, string key)
    {
        account.Key = key;
        SecretStore.Set(SecretName(account.Provider), key);
        _ = RefreshUserAsync(account);
    }

    public static void Disconnect(DebridProvider provider)
    {
        var account = Account(provider);
        account.Key = account.User = null;
        account.Premium = null;
        account.Error = null;
        SecretStore.Set(SecretName(provider), null);
        foreach (var k in Cached.Keys.Where(k => k.Item1 == provider).ToList()) Cached.Remove(k);
        Notify();
    }

    // -------------------- availability --------------------

    /// <summary>Asks every connected service which of these torrents it has
    /// cached. Real-Debrid can't be asked any more (it disabled the
    /// endpoint); its torrents are tried at playback instead.</summary>
    public static async Task PrefetchAsync(IEnumerable<string> infoHashes)
    {
        var hashes = infoHashes.Select(h => h.ToLowerInvariant()).Distinct().ToList();
        if (hashes.Count == 0) return;
        await Task.WhenAll(
            Check(DebridProvider.AllDebrid, hashes, async (key, missing) => await AllDebrid.CheckAvailability(key, missing.ToArray())),
            Check(DebridProvider.TorBox, hashes, (key, missing) => TorBoxClient.CheckCachedAsync(missing, key)),
            Check(DebridProvider.Premiumize, hashes, (key, missing) => PremiumizeClient.CheckCachedAsync(missing, key)));
        Notify();
    }

    private static async Task Check(DebridProvider provider, List<string> hashes,
        Func<string, IReadOnlyList<string>, Task<Dictionary<string, bool>>> check)
    {
        if (Account(provider).Key is not { } key) return;
        var missing = hashes.Where(h => !Cached.ContainsKey((provider, h))).ToList();
        if (missing.Count == 0) return;
        try
        {
            foreach (var (hash, cached) in await check(key, missing)) Cached[(provider, hash.ToLowerInvariant())] = cached;
        }
        catch
        {
            // Unknown availability: no badge, still tried at playback.
        }
    }

    /// <summary>The service that has this torrent cached, if any (the ⚡ tag).</summary>
    public static DebridProvider? CachedOn(string infoHash)
    {
        var h = infoHash.ToLowerInvariant();
        foreach (var account in Accounts)
            if (Cached.GetValueOrDefault((account.Provider, h))) return account.Provider;
        return null;
    }

    // -------------------- resolution --------------------

    /// <summary>A direct HTTPS link for a torrent, or null to fall back to
    /// the local engine. Services that have it cached first, then
    /// Real-Debrid (tried blind) and AllDebrid.</summary>
    public static async Task<string?> ResolveAsync(string infoHash, int? season, int? episode)
    {
        var h = infoHash.ToLowerInvariant();
        var order = Accounts.Where(a => a.IsConnected)
            .OrderByDescending(a => Cached.GetValueOrDefault((a.Provider, h)))
            .ToList();
        Exception? last = null;
        foreach (var account in order)
        {
            var cached = Cached.GetValueOrDefault((account.Provider, h));
            // TorBox and Premiumize only serve what they have cached.
            if (!cached && account.Provider is DebridProvider.TorBox or DebridProvider.Premiumize) continue;
            try
            {
                var url = account.Provider switch
                {
                    DebridProvider.RealDebrid => await RealDebridClient.ResolveAsync(h, season, episode, account.Key!),
                    DebridProvider.AllDebrid => await AllDebrid.ResolveStream(account.Key!, h, season, episode),
                    DebridProvider.TorBox => await TorBoxClient.ResolveAsync(h, season, episode, account.Key!),
                    _ => await PremiumizeClient.ResolveAsync(h, season, episode, account.Key!),
                };
                if (url is not null) return url;
            }
            catch (Exception ex)
            {
                last = ex;
                Player.PlayerLog.Write($"[debrid] {account.Name}: {ex.Message}");
            }
        }
        if (last is not null) Player.PlayerLog.Write($"[debrid] falling back to the local engine for {h}");
        return null;
    }
}
