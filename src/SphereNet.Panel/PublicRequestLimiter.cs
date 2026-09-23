namespace SphereNet.Panel;

/// <summary>
/// Fixed-window per-address request limiter for the anonymous /public/ endpoints.
/// Bounded: once it tracks too many addresses, expired windows are dropped, and if
/// that is not enough the whole table is reset (a flood of distinct addresses must
/// not grow memory without limit).
/// </summary>
internal sealed class PublicRequestLimiter
{
    private const int MaxTrackedAddresses = 10_000;

    private readonly int _limit;
    private readonly long _windowMs;
    private readonly Func<long> _now;
    private readonly Dictionary<string, (long WindowStart, int Count)> _windows = new(StringComparer.Ordinal);

    public PublicRequestLimiter(int limit, TimeSpan window, Func<long>? nowMs = null)
    {
        _limit = Math.Max(1, limit);
        _windowMs = Math.Max(1, (long)window.TotalMilliseconds);
        _now = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Count one request; false when the address is over its limit.</summary>
    public bool TryAcquire(string address)
    {
        long now = _now();
        lock (_windows)
        {
            if (_windows.TryGetValue(address, out var w) && now - w.WindowStart < _windowMs)
            {
                if (w.Count >= _limit)
                    return false;
                _windows[address] = (w.WindowStart, w.Count + 1);
                return true;
            }

            if (_windows.Count >= MaxTrackedAddresses)
            {
                foreach (var key in _windows.Where(kv => now - kv.Value.WindowStart >= _windowMs)
                             .Select(kv => kv.Key).ToList())
                    _windows.Remove(key);
                if (_windows.Count >= MaxTrackedAddresses)
                    _windows.Clear();
            }
            _windows[address] = (now, 1);
            return true;
        }
    }
}
