using System.Text;
using SphereNet.Core.Types;

namespace SphereNet.Game.Objects;

/// <summary>
/// The CObjBase keys that live in, or report on, the object's own base defs
/// (m_BaseDefs, CObjBase.h:75) and its built tooltip (m_TooltipData): the RECIPE*
/// numbers, PROPSAT / PROPSCOUNT, CLILOC / CLILOCCOUNT and the TEXTF formatter.
/// </summary>
public abstract partial class ObjBase
{
    /// <summary>OC_RECIPE* (CObjBase_props.tbl:49-59). Upstream spells the enum
    /// OC_RECIPECARTOPGRAHY but the script key is RECIPECARTOGRAPHY.</summary>
    public static readonly IReadOnlyList<string> RecipeKeys =
    [
        "RECIPEALCHEMY", "RECIPEBLACKSMITH", "RECIPEBOWCRAFT", "RECIPECARPENTRY",
        "RECIPECARTOGRAPHY", "RECIPECOOKING", "RECIPEGLASSBLOWING", "RECIPEINSCRIPTION",
        "RECIPEMASONRY", "RECIPETAILORING", "RECIPETINKERING",
    ];

    private static readonly HashSet<string> RecipeKeySet = new(RecipeKeys, StringComparer.OrdinalIgnoreCase);

    public static bool IsRecipeKey(string key) => RecipeKeySet.Contains(key);

    /// <summary>The RECIPE* numbers set on this object. Kept apart from the TAG map
    /// because upstream keeps them apart: they are base defs, saved bare as
    /// RECIPEALCHEMY=0.. (CVarDefMap::r_WritePrefix with no prefix,
    /// CObjBase.cpp:2092), not TAG.RECIPEALCHEMY.</summary>
    private SortedDictionary<string, long>? _recipeDefs;

    public IEnumerable<KeyValuePair<string, long>> RecipeDefs =>
        _recipeDefs ?? (IEnumerable<KeyValuePair<string, long>>)[];

    /// <summary>SetDefNum(key, GetArgLLVal()) (CObjBase.cpp:1834): fZero defaults to
    /// true, so writing 0 removes the key instead of storing it.</summary>
    public void SetRecipeDef(string key, long value)
    {
        string upper = key.ToUpperInvariant();
        if (value == 0)
        {
            _recipeDefs?.Remove(upper);
            return;
        }
        (_recipeDefs ??= new(StringComparer.OrdinalIgnoreCase))[upper] = value;
    }

    public long GetRecipeDef(string key) =>
        _recipeDefs != null && _recipeDefs.TryGetValue(key, out long v) ? v : 0;

    /// <summary>m_BaseDefs.Copy (CObjBase.cpp:3680) for the RECIPE* numbers.</summary>
    public void CopyRecipeDefsFrom(ObjBase src)
    {
        _recipeDefs = src._recipeDefs == null ? null : new(src._recipeDefs, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The object's base defs as PROPSAT / PROPSCOUNT walk them: every
    /// key set on the object itself (not its definition), sorted the way CVarDefMap
    /// keeps them (a sorted_vector ordered case-insensitively). Numbers read back the
    /// way CVarDefContNum::GetValStr formats them - hex, "0" prefixed
    /// (CVarDefMap.cpp:45).</summary>
    protected virtual void CollectBaseDefs(List<KeyValuePair<string, string>> sink)
    {
        if (OName.Length > 0)
            sink.Add(new("ONAME", OName));
        if (_recipeDefs != null)
        {
            foreach (var (k, v) in _recipeDefs)
                sink.Add(new(k, FormatDefHex(v)));
        }
    }

    /// <summary>Sphere hex (Str_FromInt_Fast base 16, sstring.cpp:531-575): '0'
    /// prefix, uppercase, a value that fits 32 bits (negatives included) shown as its
    /// 32-bit pattern, zero as "00".</summary>
    public static string FormatDefHex(long v) => v <= uint.MaxValue
        ? $"0{unchecked((uint)(int)v):X}"
        : $"0{unchecked((ulong)v):X}";

    private List<KeyValuePair<string, string>> GetSortedBaseDefs()
    {
        var list = new List<KeyValuePair<string, string>>();
        CollectBaseDefs(list);
        list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
        return list;
    }

    /// <summary>Reads for the base-def / tooltip family. Returns false for keys it
    /// does not own, and also where upstream's r_WriteVal returns false (an index
    /// out of range, an unknown sub-key), so the key stays unresolved.</summary>
    protected bool TryGetObjectBaseDefKey(string key, string upper, out string value)
    {
        value = "";
        if (upper.Length < 5)
            return false;
        switch (upper[0])
        {
            case 'R':
                // OC_RECIPE* (CObjBase.cpp:1034-1049): GetDefKey(key, true), formatted
                // as hex, 0 when unset.
                if (!RecipeKeySet.Contains(upper))
                    return false;
                value = FormatDefHex(GetRecipeDef(upper));
                return true;
            case 'P':
                if (upper == "PROPSCOUNT")
                {
                    // OC_PROPSCOUNT (CObjBase.cpp:1763).
                    value = GetSortedBaseDefs().Count.ToString();
                    return true;
                }
                if (upper.StartsWith("PROPSAT", StringComparison.Ordinal))
                    return TryGetPropsAt(upper, out value);
                return false;
            case 'C':
                if (upper == "CLILOCCOUNT")
                {
                    // OC_CLILOCCOUNT (CObjBase.cpp:1723): the size of the built
                    // tooltip list.
                    value = (TooltipCache?.Properties.Length ?? 0).ToString();
                    return true;
                }
                if (upper.StartsWith("CLILOC.", StringComparison.Ordinal))
                    return TryGetClilocAt(key, upper, out value);
                return false;
            case 'T':
                if (upper.StartsWith("TEXTF", StringComparison.Ordinal) &&
                    (upper.Length == 5 || !char.IsLetterOrDigit(upper[5])))
                    return TryFormatTextF(key[5..], out value);
                return false;
        }
        return false;
    }

    /// <summary>OC_PROPSAT (CObjBase.cpp:1726-1761): PROPSAT.n is "KEY=VAL",
    /// .KEY / .VAL the halves; anything else, or n past the end, is unresolved.</summary>
    private bool TryGetPropsAt(string upper, out string value)
    {
        value = "";
        if (upper.Length <= 7 || upper[7] != '.')
            return false;
        string rest = upper[8..];
        int dot = rest.IndexOf('.');
        string idxText = dot >= 0 ? rest[..dot] : rest;
        string field = dot >= 0 ? rest[(dot + 1)..] : "";
        if (!ScriptNumber.TryParseArgument(idxText, out long idx) || idx < 0)
            return false;
        var defs = GetSortedBaseDefs();
        if (idx >= defs.Count)
            return false;
        var pair = defs[(int)idx];
        if (field.Length == 0)
            value = $"{pair.Key}={pair.Value}";
        else if (field.StartsWith("KEY", StringComparison.Ordinal))
            value = pair.Key;
        else if (field.StartsWith("VAL", StringComparison.Ordinal))
            value = pair.Value;
        else
            return false;
        return true;
    }

    /// <summary>OC_CLILOC (CObjBase.cpp:1695-1721): CLILOC.n is "id=args", .ID the
    /// cliloc number, .VAL its arguments.</summary>
    private bool TryGetClilocAt(string key, string upper, out string value)
    {
        value = "";
        string rest = upper[7..];
        int dot = rest.IndexOf('.');
        string idxText = dot >= 0 ? rest[..dot] : rest;
        string field = dot >= 0 ? rest[(dot + 1)..] : "";
        if (!ScriptNumber.TryParseArgument(idxText, out long idx) || idx < 0)
            return false;
        var props = TooltipCache?.Properties;
        if (props == null || idx >= props.Length)
            return false;
        var (id, args) = props[(int)idx];
        if (field.Length == 0)
            value = $"{id}={args}";
        else if (field.StartsWith("ID", StringComparison.Ordinal))
            value = id.ToString();
        else if (field.StartsWith("VAL", StringComparison.Ordinal))
            value = args;
        else
            return false;
        return true;
    }

    /// <summary>OC_TEXTF (CObjBase.cpp:1171-1201): Str_ParseCmds splits the rest of
    /// the key into at most four arguments; the first, stripped of its quotes, is a
    /// printf format and the others are handed to it as strings. Fewer than two
    /// arguments is an error (unresolved).</summary>
    private static bool TryFormatTextF(string rest, out string value)
    {
        value = "";
        var args = ParseCmds(rest, 4);
        if (args.Count < 2)
            return false;
        string fmt = args[0];
        if (fmt.StartsWith('"'))
            fmt = fmt[1..];
        int q = fmt.LastIndexOf('"');
        if (q >= 0)
            fmt = fmt[..q];
        value = FormatPrintfStrings(fmt, args.GetRange(1, args.Count - 1));
        return true;
    }

    /// <summary>Str_ParseCmds with the default "=, \t" separators
    /// (CExpression.cpp:137/284): quote- and bracket-aware, a whitespace separator
    /// swallows one following separator, and once <paramref name="max"/> arguments
    /// are split the last one keeps the rest of the line.</summary>
    internal static List<string> ParseCmds(string line, int max)
    {
        const string seps = "=, \t";
        var result = new List<string>();
        string s = line.TrimStart();
        if (s.Length == 0)
            return result;
        int start = 0;
        while (true)
        {
            if (result.Count == max - 1)
            {
                result.Add(s[start..].Trim());
                return result;
            }
            bool quotes = false;
            int curly = 0, square = 0, round = 0;
            int cut = -1;
            for (int i = start; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '"') { quotes = !quotes; continue; }
                if (quotes) continue;
                switch (ch)
                {
                    case '{': if (square == 0 && round == 0) curly++; break;
                    case '[': if (curly == 0 && round == 0) square++; break;
                    case '(': if (curly == 0 && square == 0) round++; break;
                    case '}': if (curly > 0) curly--; break;
                    case ']': if (square > 0) square--; break;
                    case ')': if (round > 0) round--; break;
                }
                if (curly <= 0 && square <= 0 && round <= 0 && seps.IndexOf(ch) >= 0)
                {
                    cut = i;
                    break;
                }
            }
            if (cut < 0)
            {
                result.Add(s[start..].TrimEnd());
                return result;
            }
            result.Add(s[start..cut]);
            int next = cut + 1;
            if (char.IsWhiteSpace(s[cut]))
            {
                while (next < s.Length && char.IsWhiteSpace(s[next])) next++;
                if (next < s.Length && seps.IndexOf(s[next]) >= 0) next++;
            }
            while (next < s.Length && char.IsWhiteSpace(s[next])) next++;
            start = next;
        }
    }

    /// <summary>The printf the TEXTF arguments go through. Every argument is a
    /// string, so only the %s conversion (with its flags, width and precision) and
    /// %% have a defined meaning; any other conversion is copied through unchanged.
    /// A %s with no argument left prints "(null)", as the C runtimes do for the
    /// nullptr upstream passes in that slot.</summary>
    private static string FormatPrintfStrings(string fmt, List<string> args)
    {
        var sb = new StringBuilder(fmt.Length + 16);
        int argIdx = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            char c = fmt[i];
            if (c != '%' || i + 1 >= fmt.Length)
            {
                sb.Append(c);
                continue;
            }
            if (fmt[i + 1] == '%')
            {
                sb.Append('%');
                i++;
                continue;
            }
            int j = i + 1;
            bool left = false;
            while (j < fmt.Length && "-+ #0".IndexOf(fmt[j]) >= 0)
            {
                if (fmt[j] == '-') left = true;
                j++;
            }
            int width = 0;
            while (j < fmt.Length && char.IsAsciiDigit(fmt[j]))
                width = width * 10 + (fmt[j++] - '0');
            int precision = -1;
            if (j < fmt.Length && fmt[j] == '.')
            {
                j++;
                precision = 0;
                while (j < fmt.Length && char.IsAsciiDigit(fmt[j]))
                    precision = precision * 10 + (fmt[j++] - '0');
            }
            if (j < fmt.Length && fmt[j] == 's')
            {
                string arg = argIdx < args.Count ? args[argIdx] : "(null)";
                argIdx++;
                if (precision >= 0 && arg.Length > precision)
                    arg = arg[..precision];
                sb.Append(left ? arg.PadRight(width) : arg.PadLeft(width));
                i = j;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Writes for the RECIPE* numbers (OC_RECIPE*, CObjBase.cpp:1823-1836).</summary>
    protected bool TrySetObjectBaseDefKey(string upper, string value)
    {
        if (!RecipeKeySet.Contains(upper))
            return false;
        ScriptNumber.TryParseArgument(value, out long n);
        SetRecipeDef(upper, n);
        return true;
    }
}
