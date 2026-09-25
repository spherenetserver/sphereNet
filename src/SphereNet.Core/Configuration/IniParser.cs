namespace SphereNet.Core.Configuration;

/// <summary>
/// INI file parser. Reads sphere.ini style configuration files.
/// Supports [SECTION] blocks and KEY=VALUE lines.
/// </summary>
public sealed class IniParser
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    // Which keys something actually asked for. An ini key the engine never reads is
    // silent today: an operator who mistypes a setting, or carries one over from a
    // Source-X build that supports it, gets no word either way and concludes the
    // value took effect. Every read goes through GetValue, so marking there is enough
    // to answer "what did this file say that nobody listened to".
    private readonly HashSet<string> _consulted = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, Dictionary<string, string>> Sections => _sections;

    public void Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"INI file not found: {filePath}");

        string currentSection = "";
        _sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawLine in File.ReadLines(filePath))
        {
            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();

            if (line.IsEmpty || line[0] == '/' && line.Length > 1 && line[1] == '/')
                continue;

            int commentIdx = line.IndexOf("//");
            if (commentIdx > 0)
                line = line[..commentIdx].TrimEnd();
            if (line.IsEmpty) continue;

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end > 1)
                {
                    currentSection = line[1..end].ToString().Trim();
                    if (!_sections.ContainsKey(currentSection))
                        _sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            int eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                string key = line[..eqIdx].TrimEnd().ToString();
                string value = line[(eqIdx + 1)..].TrimStart().ToString();
                _sections[currentSection][key] = value;
            }
        }
    }

    public string? GetValue(string section, string key)
    {
        _consulted.Add(section + "|" + key);
        if (_sections.TryGetValue(section, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        return null;
    }

    /// <summary>Keys the file sets that nothing ever asked for, as "SECTION|KEY".
    ///
    /// Two kinds end up here and the operator needs to tell them apart: a key this
    /// engine deliberately does not support (documented in config/sphere.ini with its
    /// own marker), and a key that is simply misspelled. Neither is visible while the
    /// parser stays quiet, and the second one is the expensive kind - the setting
    /// looks present and does nothing.</summary>
    public IEnumerable<string> UnreadKeys()
    {
        foreach (var (section, keys) in _sections)
            foreach (string key in keys.Keys)
                if (!_consulted.Contains(section + "|" + key))
                    yield return section + "|" + key;
    }

    public int GetInt(string section, string key, int defaultValue = 0)
    {
        string? val = GetValue(section, key);
        if (val == null) return defaultValue;

        if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || val.StartsWith("0X"))
        {
            if (int.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int hexResult))
                return hexResult;
        }

        return int.TryParse(val, out int result) ? result : defaultValue;
    }

    /// <summary>
    /// A number the reference ini may write as a product of Sphere numbers, e.g.
    /// <c>MinCharDeleteTime=7*24*60*60</c> (Source-X evaluates the line with
    /// GetArgLLVal). Each term follows the leading-zero-is-hex rule. Anything else,
    /// or a product that overflows an int, keeps the default.
    /// </summary>
    public int GetIntProduct(string section, string key, int defaultValue = 0)
    {
        string? val = GetValue(section, key);
        if (string.IsNullOrWhiteSpace(val)) return defaultValue;

        long acc = 1;
        foreach (string term in val.Split('*', StringSplitOptions.TrimEntries))
        {
            if (!SphereNet.Core.Types.ScriptNumber.TryParseToken(term, out long factor))
                return defaultValue;
            acc *= factor;
            if (acc is > int.MaxValue or < int.MinValue)
                return defaultValue;
        }
        return (int)acc;
    }

    /// <summary>
    /// A FLAG key, in the form the reference ini writes them:
    /// <c>RevealFlags=01|02|04|08|010|040|080|0200</c>.
    ///
    /// Two things <see cref="GetInt"/> cannot do. The '|' is an OR of several flags,
    /// and each term follows Sphere's numeric rule where a LEADING ZERO means
    /// hexadecimal - so <c>010</c> is sixteen, not ten. Reading such a line as a
    /// decimal gives a number that is wrong in a way nothing complains about: the
    /// server starts, some flags are set, and they are not the ones the operator asked
    /// for.
    /// </summary>
    public int GetFlags(string section, string key, int defaultValue = 0)
    {
        string? val = GetValue(section, key);
        if (string.IsNullOrWhiteSpace(val)) return defaultValue;

        long acc = 0;
        bool any = false;
        foreach (string term in val.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SphereNet.Core.Types.ScriptNumber.TryParseToken(term, out long bits))
                return defaultValue;
            acc |= bits;
            any = true;
        }
        return any ? (int)acc : defaultValue;
    }

    public bool GetBool(string section, string key, bool defaultValue = false)
    {
        string? val = GetValue(section, key);
        if (val == null) return defaultValue;
        return val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               val.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public IEnumerable<KeyValuePair<string, string>> GetSectionValues(string section)
    {
        if (_sections.TryGetValue(section, out var dict))
            return dict;
        return Enumerable.Empty<KeyValuePair<string, string>>();
    }
}
