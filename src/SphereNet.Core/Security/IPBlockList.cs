using System.Collections.Concurrent;

namespace SphereNet.Core.Security;

public sealed class IPBlockList
{
    private readonly ConcurrentDictionary<string, byte> _blocked = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBlocked(string ip) =>
        !string.IsNullOrWhiteSpace(ip) && _blocked.ContainsKey(ip.Trim());

    public bool Add(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return _blocked.TryAdd(ip.Trim(), 0);
    }

    public bool Remove(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return _blocked.TryRemove(ip.Trim(), out _);
    }

    public IReadOnlyCollection<string> GetAll() => _blocked.Keys.ToArray();
}
