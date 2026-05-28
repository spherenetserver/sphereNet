namespace SphereNet.Scripting.Variables;

/// <summary>
/// Dynamic key-value variable map. Maps to CVarDefMap (TAG system) in Source-X.
/// Supports TAG.*, VAR.*, and arbitrary named variables.
/// </summary>
public sealed class VarMap
{
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _vars.Count;

    public string? Get(string key)
    {
        return _vars.GetValueOrDefault(key);
    }

    public long GetInt(string key, long defaultValue = 0)
    {
        if (_vars.TryGetValue(key, out string? val) && val != null)
        {
            if (long.TryParse(val, out long result))
                return result;
            if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out result))
                return result;
        }
        return defaultValue;
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            _vars.Remove(key);
        else
            _vars[key] = value.Length > 4096 ? value[..4096] : value;
    }

    public void SetInt(string key, long value)
    {
        _vars[key] = value.ToString();
    }

    public bool Has(string key) => _vars.ContainsKey(key);

    public bool Remove(string key) => _vars.Remove(key);

    public void Clear() => _vars.Clear();

    /// <summary>Remove every key whose name starts with <paramref name="prefix"/>
    /// (case-insensitive). Returns the removal count. Used by the Source-X
    /// <c>CLEARCTAGS pattern</c> verb — <c>CLEARCTAGS Dialog.Admin</c> drops
    /// every tag under that scope.</summary>
    public int RemoveByPrefix(string prefix)
    {
        prefix = prefix.Trim();
        if (string.IsNullOrEmpty(prefix))
        {
            int n = _vars.Count;
            _vars.Clear();
            return n;
        }

        string[] keys = _vars.Keys.ToArray();
        int removed = 0;
        foreach (var key in keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && _vars.Remove(key))
                removed++;
        }
        return removed;
    }

    public IEnumerable<KeyValuePair<string, string>> GetAll() => _vars;

    /// <summary>
    /// Copy all entries from another VarMap.
    /// </summary>
    public void CopyFrom(VarMap other)
    {
        foreach (var kvp in other._vars)
            _vars[kvp.Key] = kvp.Value;
    }
}
