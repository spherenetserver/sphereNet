using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace SphereNet.Panel.Auth;

public sealed class TokenStore
{
    private readonly ConcurrentDictionary<string, DateTime> _tokens = new();
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTime> _clock;

    public TokenStore(TimeSpan? lifetime = null, Func<DateTime>? clock = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromHours(24);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public int Count => _tokens.Count;

    public string Create()
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = _clock().Add(_lifetime);
        return token;
    }

    public bool Validate(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_tokens.TryGetValue(token, out var expiry))
            return false;
        if (expiry > _clock())
            return true;

        _tokens.TryRemove(token, out _);
        return false;
    }

    public void Revoke(string token) => _tokens.TryRemove(token, out _);

    public void PurgeExpired()
    {
        var now = _clock();
        foreach (var pair in _tokens)
            if (pair.Value <= now)
                _tokens.TryRemove(pair.Key, out _);
    }
}
