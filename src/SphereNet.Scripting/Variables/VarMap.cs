using System.Globalization;

namespace SphereNet.Scripting.Variables;

public enum VarValueKind
{
    String,
    Integer,
}

public readonly record struct VarEntry(string Key, VarValueKind Kind, string Value, long IntegerValue);

/// <summary>
/// Dynamic case-insensitive, key-sorted variable map. Maps to Source-X
/// <c>CVarDefMap</c>, including distinct string/integer storage.
/// </summary>
public sealed class VarMap
{
    private readonly record struct StoredValue(VarValueKind Kind, string? Text, long Integer)
    {
        public static StoredValue FromString(string value) => new(VarValueKind.String, value, 0);
        public static StoredValue FromInteger(long value) => new(VarValueKind.Integer, null, value);

        public string AsString() => Kind == VarValueKind.Integer
            ? Integer.ToString(CultureInfo.InvariantCulture)
            : Text ?? string.Empty;
    }

    // Source-X uses a case-insensitive sorted_vector. SortedDictionary gives
    // TAGAT/DEFAT and save enumeration the same deterministic key order.
    private readonly SortedDictionary<string, StoredValue> _vars =
        new(StringComparer.OrdinalIgnoreCase);

    public int Count => _vars.Count;

    public string? Get(string key) =>
        _vars.TryGetValue(key, out var value) ? value.AsString() : null;

    public long GetInt(string key, long defaultValue = 0)
    {
        if (!_vars.TryGetValue(key, out var value))
            return defaultValue;
        if (value.Kind == VarValueKind.Integer)
            return value.Integer;

        string text = value.Text ?? string.Empty;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result))
            return result;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result))
            return result;
        return defaultValue;
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            _vars.Remove(key);
        else
            _vars[key] = StoredValue.FromString(value.Length > 4096 ? value[..4096] : value);
    }

    public void SetInt(string key, long value) => _vars[key] = StoredValue.FromInteger(value);

    public bool Has(string key) => _vars.ContainsKey(key);

    public bool IsInteger(string key) =>
        _vars.TryGetValue(key, out var value) && value.Kind == VarValueKind.Integer;

    public bool Remove(string key) => _vars.Remove(key);

    public void Clear() => _vars.Clear();

    /// <summary>Remove every key whose name starts with <paramref name="prefix"/>
    /// (case-insensitive). Returns the removal count. Used by the Source-X
    /// <c>CLEARCTAGS pattern</c> verb.</summary>
    public int RemoveByPrefix(string prefix)
    {
        prefix = prefix.Trim();
        if (string.IsNullOrEmpty(prefix))
        {
            int count = _vars.Count;
            _vars.Clear();
            return count;
        }

        string[] keys = _vars.Keys.ToArray();
        int removed = 0;
        foreach (string key in keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && _vars.Remove(key))
                removed++;
        }
        return removed;
    }

    /// <summary>Enumerate string projections in deterministic Source-X key order.</summary>
    public IEnumerable<KeyValuePair<string, string>> GetAll()
    {
        foreach (var (key, value) in _vars)
            yield return new KeyValuePair<string, string>(key, value.AsString());
    }

    /// <summary>Enumerate values with their native storage type, in key order.</summary>
    public IEnumerable<VarEntry> GetAllEntries()
    {
        foreach (var (key, value) in _vars)
            yield return new VarEntry(key, value.Kind, value.AsString(), value.Integer);
    }

    /// <summary>Copy all entries from another map without losing numeric types.</summary>
    public void CopyFrom(VarMap other)
    {
        foreach (var (key, value) in other._vars)
            _vars[key] = value;
    }
}
