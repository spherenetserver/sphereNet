using System.Collections.Concurrent;

namespace SphereNet.Core.Security;

public sealed class ConnectionRateLimiter
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Func<DateTimeOffset> _clock;

    public ConnectionRateLimiter(
        int threshold = 10,
        TimeSpan? window = null,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Func<DateTimeOffset>? clock = null)
    {
        _threshold = Math.Max(1, threshold);
        _window = window ?? TimeSpan.FromSeconds(10);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromHours(1);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void RegisterAttempt(string ipKey)
    {
        var now = _clock();
        var key = NormalizeKey(ipKey);
        _entries.AddOrUpdate(key,
            _ => new Entry(1, now, DateTimeOffset.MinValue),
            (_, current) =>
            {
                int count = now - current.WindowStart > _window
                    ? 1
                    : current.Attempts + 1;
                var windowStart = count == 1 ? now : current.WindowStart;
                return new Entry(count, windowStart, current.ThrottledUntil);
            });
    }

    public bool ShouldThrottle(string ipKey)
    {
        var key = NormalizeKey(ipKey);
        if (!_entries.TryGetValue(key, out var entry))
            return false;

        var now = _clock();

        if (entry.ThrottledUntil > now)
            return true;

        if (entry.Attempts < _threshold)
            return false;

        int excess = entry.Attempts - _threshold;
        int exponent = Math.Min(excess, 20);
        double seconds = Math.Min(_maxDelay.TotalSeconds, _baseDelay.TotalSeconds * Math.Pow(2, exponent));
        var throttledUntil = now + TimeSpan.FromSeconds(seconds);
        _entries[key] = entry with { ThrottledUntil = throttledUntil };
        return true;
    }

    public void Reset(string ipKey)
    {
        _entries.TryRemove(NormalizeKey(ipKey), out _);
    }

    public void Cleanup()
    {
        var now = _clock();
        foreach (var kvp in _entries)
        {
            if (kvp.Value.ThrottledUntil <= now && now - kvp.Value.WindowStart > _window)
                _entries.TryRemove(kvp.Key, out _);
        }
    }

    private static string NormalizeKey(string key) =>
        string.IsNullOrWhiteSpace(key) ? "<empty>" : key.Trim();

    private readonly record struct Entry(int Attempts, DateTimeOffset WindowStart, DateTimeOffset ThrottledUntil);
}
