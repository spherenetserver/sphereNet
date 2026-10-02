using System.Collections.Concurrent;

namespace SphereNet.Core.Security;

/// <summary>
/// Blocked-IP registry with optional timed blocks (Source-X BLOCKIP decay). An
/// entry is either permanent (expiry 0) or expires after a requested number of
/// seconds, at which point it is dropped lazily on the next lookup.
/// </summary>
public sealed class IPBlockList
{
    // value = absolute expiry in Unix ms; 0 means permanent.
    private readonly ConcurrentDictionary<string, long> _blocked = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? Blocked;

    /// <summary>Asked as an IP becomes blocked, with the block time in seconds (-1 =
    /// permanent); the answer is the time used. Source-X HistoryIP::setBlocked
    /// (CIPHistoryManager.cpp:36-48) runs f_onserver_blockip with ARGN1 = the timeout
    /// and reads ARGN1 back: below 0 is permanent, otherwise the block lapses that
    /// many seconds from now.</summary>
    public Func<string, int, int>? BlockTimeout { get; set; }

    /// <summary>Clock source (Unix ms) — overridable so tests can advance time.</summary>
    public Func<long> NowMsProvider { get; set; } =
        () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool IsBlocked(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return false;
        string key = ip.Trim();
        if (!_blocked.TryGetValue(key, out long expiry))
            return false;
        if (expiry != 0 && NowMsProvider() >= expiry)
        {
            _blocked.TryRemove(key, out _); // lazy expiry
            return false;
        }
        return true;
    }

    /// <summary>Block an IP. <paramref name="durationSeconds"/> &lt;= 0 blocks
    /// permanently; a positive value expires the block after that many seconds.
    /// Re-blocking an existing IP refreshes its expiry. Returns true when the IP
    /// was not already blocked.</summary>
    public bool Add(string ip, int durationSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return false;
        string key = ip.Trim();
        bool isNew = !_blocked.ContainsKey(key);
        long expiry = durationSeconds > 0
            ? NowMsProvider() + durationSeconds * 1000L
            : 0;
        if (isNew && BlockTimeout is { } timeout)
        {
            int seconds = timeout(key, durationSeconds > 0 ? durationSeconds : -1);
            expiry = seconds < 0 ? 0 : NowMsProvider() + seconds * 1000L;
        }
        _blocked[key] = expiry;
        if (isNew)
            Blocked?.Invoke(key);
        return isNew;
    }

    public bool Remove(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return _blocked.TryRemove(ip.Trim(), out _);
    }

    /// <summary>Currently-blocked IPs, dropping any that have expired.</summary>
    public IReadOnlyCollection<string> GetAll()
    {
        long now = NowMsProvider();
        var live = new List<string>();
        foreach (var kv in _blocked)
        {
            if (kv.Value != 0 && now >= kv.Value)
                _blocked.TryRemove(kv.Key, out _);
            else
                live.Add(kv.Key);
        }
        return live;
    }
}
