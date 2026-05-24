using System.Collections.Concurrent;

namespace SphereNet.Core.Security;

/// <summary>
/// Small in-memory backoff limiter for login endpoints. It is intentionally
/// deterministic and dependency-free so game, panel and tests can share it.
/// </summary>
public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly int _threshold;
    private readonly Func<DateTimeOffset> _clock;

    public LoginRateLimiter(
        int threshold = 5,
        TimeSpan? window = null,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Func<DateTimeOffset>? clock = null)
    {
        _threshold = Math.Max(1, threshold);
        _window = window ?? TimeSpan.FromMinutes(5);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsLimited(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(NormalizeKey(key), out var entry))
            return false;

        var now = _clock();
        if (entry.BlockedUntil <= now)
            return false;

        retryAfter = entry.BlockedUntil - now;
        return true;
    }

    public TimeSpan RegisterFailure(string key)
    {
        var now = _clock();
        var normalized = NormalizeKey(key);
        var entry = _entries.AddOrUpdate(normalized,
            _ => new Entry(1, now, now),
            (_, current) =>
            {
                int failures = now - current.FirstFailureAt > _window
                    ? 1
                    : current.Failures + 1;
                return new Entry(failures, failures == 1 ? now : current.FirstFailureAt, current.BlockedUntil);
            });

        if (entry.Failures < _threshold)
            return TimeSpan.Zero;

        int exponent = Math.Min(entry.Failures - _threshold, 6);
        double seconds = Math.Min(_maxDelay.TotalSeconds, _baseDelay.TotalSeconds * Math.Pow(2, exponent));
        var delay = TimeSpan.FromSeconds(seconds);
        var blocked = entry with { BlockedUntil = now + delay };
        _entries[normalized] = blocked;
        return delay;
    }

    public void RegisterSuccess(string key)
    {
        _entries.TryRemove(NormalizeKey(key), out _);
    }

    private static string NormalizeKey(string key) => string.IsNullOrWhiteSpace(key) ? "<empty>" : key.Trim();

    private readonly record struct Entry(int Failures, DateTimeOffset FirstFailureAt, DateTimeOffset BlockedUntil);
}
