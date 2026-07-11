using System.Collections.Concurrent;

namespace SphereNet.Core.Security;

public sealed class IPBlockList
{
    private readonly ConcurrentDictionary<string, byte> _blocked = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? Blocked;

    public bool IsBlocked(string ip) =>
        !string.IsNullOrWhiteSpace(ip) && _blocked.ContainsKey(ip.Trim());

    public bool Add(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        string normalized = ip.Trim();
        if (!_blocked.TryAdd(normalized, 0))
            return false;
        Blocked?.Invoke(normalized);
        return true;
    }

    public bool Remove(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return _blocked.TryRemove(ip.Trim(), out _);
    }

    public IReadOnlyCollection<string> GetAll() => _blocked.Keys.ToArray();
}
