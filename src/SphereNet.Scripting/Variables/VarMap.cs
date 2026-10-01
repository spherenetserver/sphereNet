using System.Globalization;
using SphereNet.Scripting.Expressions;

namespace SphereNet.Scripting.Variables;

public enum VarValueKind
{
    String,
    Integer,
}

/// <summary>How a value is written back to a save line (Source-X
/// <c>CVarDefMap::r_WritePrefix</c>, CVarDefMap.cpp:672): a number bare, a string
/// inside quotes.</summary>
public enum VarSaveForm : byte
{
    /// <summary>Set from engine code, or read from a save without quotes: the text
    /// decides. A plain number literal is written as it is, anything else quoted.</summary>
    Auto,
    /// <summary>A Source-X string var (CVarDefContStr): always written quoted. A
    /// quoted assignment or a quoted save value makes one, even when it reads as a
    /// number ("5") and even when it is empty.</summary>
    Quoted,
    /// <summary>A Source-X number var (CVarDefContNum): an unquoted script value
    /// that IsSimpleNumberString accepts. Written as the evaluated number in Sphere
    /// hex (CVarDefContNum::GetValStr).</summary>
    Number,
}

public readonly record struct VarEntry(string Key, VarValueKind Kind, string Value, long IntegerValue,
    VarSaveForm SaveForm = VarSaveForm.Auto);

/// <summary>
/// Dynamic case-insensitive, key-sorted variable map. Maps to Source-X
/// <c>CVarDefMap</c>, including distinct string/integer storage.
/// </summary>
public sealed class VarMap
{
    private readonly record struct StoredValue(VarValueKind Kind, string? Text, long Integer,
        VarSaveForm Form = VarSaveForm.Auto)
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

    /// <summary>A script or save assignment, as Source-X <c>CVarDefMap::SetStr</c>
    /// (CVarDefMap.cpp:467) decides it: an unquoted empty value removes the key; an
    /// unquoted value IsSimpleNumberString accepts is a number (and removes the key
    /// when <paramref name="deleteZero"/> is set and it is zero - TAG0); anything
    /// else, and every quoted value - the empty one included - is a string. The text
    /// is stored as given, so what a script reads back does not change; the decision
    /// only shapes the save line.</summary>
    public void SetStr(string key, bool quoted, string value, bool deleteZero = false)
    {
        if (!quoted && value.Length == 0)
        {
            _vars.Remove(key);
            return;
        }
        Set(key, value);
        ApplySetStrForm(key, quoted, value, deleteZero);
    }

    /// <summary>The <see cref="SetStr"/> decision for a value an owner has already
    /// stored through its own setter (which may route special keys elsewhere): mark
    /// the entry a Source-X string or number, store a quoted empty value, or drop a
    /// TAG0 zero. Does nothing when the entry no longer holds this text.</summary>
    public void ApplySetStrForm(string key, bool quoted, string value, bool deleteZero = false)
    {
        if (quoted)
        {
            if (value.Length == 0)
            {
                _vars[key] = new StoredValue(VarValueKind.String, string.Empty, 0, VarSaveForm.Quoted);
                return;
            }
            if (_vars.TryGetValue(key, out var stored) && stored.Kind == VarValueKind.String &&
                string.Equals(stored.Text, value, StringComparison.Ordinal))
                _vars[key] = stored with { Form = VarSaveForm.Quoted };
            return;
        }

        if (!ExpressionParser.IsSimpleNumberString(value))
            return;
        if (deleteZero && EvaluateNumber(value) == 0)
        {
            _vars.Remove(key);
            return;
        }
        if (_vars.TryGetValue(key, out var num) && num.Kind == VarValueKind.String &&
            string.Equals(num.Text, value, StringComparison.Ordinal))
            _vars[key] = num with { Form = VarSaveForm.Number };
    }

    /// <summary>Read a save line's value the way Source-X <c>CScriptKey::GetArgStr</c>
    /// does (CScript.cpp:61): a value opening with a quote loses it and is cut at its
    /// LAST quote, and only a value that has that closing quote counts as quoted.</summary>
    public static string UnquoteSaveValue(string raw, out bool quoted)
    {
        quoted = false;
        if (raw.Length == 0 || raw[0] != '"')
            return raw;
        string body = raw[1..];
        int last = body.LastIndexOf('"');
        if (last < 0)
            return body;
        quoted = true;
        return body[..last];
    }

    /// <summary>The save text of an engine-set value (<see cref="VarSaveForm.Auto"/>):
    /// a plain number literal goes out as it is - it reads back the same in both
    /// servers - and anything else in quotes, as a Source-X string var is written.
    /// Text shaped like an unevaluated expression ("2026-10-01", "1+2") is quoted
    /// too: written bare, a load would evaluate it into a different value.</summary>
    public static string FormatAutoSaveValue(string text) =>
        IsPlainNumberLiteral(text) ? text : Quote(text);

    /// <summary>One entry's value as <c>r_WritePrefix</c> writes it.</summary>
    public string? GetSaveText(string key) =>
        _vars.TryGetValue(key, out var value) ? FormatSaveValue(value) : null;

    private static string FormatSaveValue(StoredValue value)
    {
        if (value.Kind == VarValueKind.Integer)
            return ExpressionParser.FormatSphereHex(value.Integer);
        string text = value.Text ?? string.Empty;
        return value.Form switch
        {
            VarSaveForm.Quoted => Quote(text),
            VarSaveForm.Number => ExpressionParser.FormatSphereHex(EvaluateNumber(text)),
            _ => FormatAutoSaveValue(text),
        };
    }

    private static string Quote(string text) => "\"" + text + "\"";

    /// <summary>Optional '-', then decimal digits, or a '0'-led Sphere hex token.</summary>
    private static bool IsPlainNumberLiteral(string text)
    {
        int i = text.Length > 0 && text[0] == '-' ? 1 : 0;
        if (i >= text.Length)
            return false;
        bool hex = text[i] == '0';
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
                continue;
            if (hex && char.IsAsciiHexDigit(c))
                continue;
            return false;
        }
        return true;
    }

    /// <summary>Exp_Get64Val of a value IsSimpleNumberString accepted: digits, Sphere
    /// hex and arithmetic only, so no resolver is involved.</summary>
    private static long EvaluateNumber(string text) => new ExpressionParser().Evaluate(text);

    public bool Has(string key) => _vars.ContainsKey(key);

    public bool IsInteger(string key) =>
        _vars.TryGetValue(key, out var value) && value.Kind == VarValueKind.Integer;

    /// <summary>How the entry will be written to a save, or null when absent.</summary>
    public VarSaveForm? GetSaveForm(string key) =>
        _vars.TryGetValue(key, out var value) ? value.Form : null;

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
            yield return new VarEntry(key, value.Kind, value.AsString(), value.Integer, value.Form);
    }

    /// <summary>Copy all entries from another map without losing numeric types.</summary>
    public void CopyFrom(VarMap other)
    {
        foreach (var (key, value) in other._vars)
            _vars[key] = value;
    }
}
