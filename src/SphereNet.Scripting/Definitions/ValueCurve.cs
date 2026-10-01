using SphereNet.Scripting.Expressions;

namespace SphereNet.Scripting.Definitions;

/// <summary>
/// Skill-level value curve used by [SKILL] definitions (ADV_RATE, DELAY,
/// EFFECT) and by [SPELL] definitions (CAST_TIME, EFFECT, DURATION,
/// INTERRUPT). Port of the reference curve type (credit: Sphere 0.56 /
/// Source-X CValueCurveDef, CValueDefs.cpp): a list of values spread evenly
/// across skill 0.0-100.0, linearly interpolated segment by segment. The
/// number of points is kept, so an explicit 0 endpoint ("10,0") is a real
/// point and not "no top value".
///
/// Every point is a full script expression (Str_ParseCmds hands each argument
/// to Exp_GetVal, CExpression.cpp:305), so "3*60.0" is one point worth 1800.
/// Script numbers use the legacy fixed-point convention - the decimal point is
/// skipped and digits concatenate ("2.5" → 25, "200.0" → 2000), and a leading
/// zero marks HEX ("0480" → 0x480) unless followed by a dot. The caller decides
/// the unit; the curve itself never rescales.
/// </summary>
public sealed class ValueCurve
{
    public static readonly ValueCurve Empty = new(Array.Empty<int>());

    /// <summary>Reference Arg_piCmd[101] - at most this many points are read.</summary>
    private const int MaxPoints = 101;

    /// <summary>Default argument separators of the reference Str_Parse
    /// (CExpression.cpp:144).</summary>
    private const string Separators = "=, \t";

    private readonly int[] _values;

    public ValueCurve(int[] values)
    {
        _values = values;
    }

    public bool IsEmpty => _values.Length == 0;
    public int Count => _values.Length;
    public int this[int index] => _values[index];

    /// <summary>Parse a curve the way CValueCurveDef::Load does
    /// (CValueDefs.cpp:72): split the line into arguments, evaluate each one as
    /// an expression, keep every point.</summary>
    public static ValueCurve Parse(string? text) => Parse(text, null);

    /// <summary>As <see cref="Parse(string?)"/>, evaluating each point through
    /// <paramref name="parser"/> (so defnames resolve) when one is given.</summary>
    public static ValueCurve Parse(string? text, ExpressionParser? parser)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;

        var tokens = SplitArgs(text);
        if (tokens.Count == 0)
            return Empty;

        var values = new int[tokens.Count];
        ExpressionParser? eval = parser;
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (IsPlainNumber(token))
            {
                values[i] = ParseSphereNumber(token);
                continue;
            }
            eval ??= new ExpressionParser();
            values[i] = (int)eval.Evaluate(token);
        }
        return new ValueCurve(values);
    }

    /// <summary>Port of Str_ParseCmds / Str_Parse with the default separators
    /// (CExpression.cpp:137-303): split at '=', ',', space or tab outside
    /// quotes and brackets; a run of whitespace followed by one separator is a
    /// single break, so "2 , 1" is two arguments.</summary>
    private static List<string> SplitArgs(string text)
    {
        var args = new List<string>();
        int pos = 0;
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
        if (pos >= text.Length)
            return args;

        while (args.Count < MaxPoints)
        {
            int start = pos;
            bool quotes = false;
            int curly = 0, square = 0, round = 0, angle = 0;
            bool hitSeparator = false;
            char sep = '\0';
            for (; pos < text.Length; pos++)
            {
                char ch = text[pos];
                if (ch == '"') { quotes = !quotes; continue; }
                if (quotes) continue;
                switch (ch)
                {
                    case '{': if (square == 0 && round == 0 && angle == 0) curly++; break;
                    case '[': if (curly == 0 && round == 0 && angle == 0) square++; break;
                    case '(': if (curly == 0 && square == 0 && angle == 0) round++; break;
                    case '<': if (curly == 0 && square == 0 && round == 0) angle++; break;
                    case '}': if (curly > 0) curly--; break;
                    case ']': if (square > 0) square--; break;
                    case ')': if (round > 0) round--; break;
                    case '>': if (angle > 0) angle--; break;
                }
                if (curly <= 0 && square <= 0 && round <= 0 && Separators.IndexOf(ch) >= 0)
                {
                    hitSeparator = true;
                    sep = ch;
                    break;
                }
            }

            args.Add(text[start..pos].Trim());
            if (!hitSeparator)
                break;

            pos++; // past the separator
            if (char.IsWhiteSpace(sep))
            {
                while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
                if (pos < text.Length && Separators.IndexOf(text[pos]) >= 0)
                    pos++;
            }
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (quotes || curly > 0 || square > 0 || round > 0)
                break;
        }
        return args;
    }

    /// <summary>True for a lone (optionally signed) Sphere number - digits, hex
    /// letters and fixed-point dots - which <see cref="ParseSphereNumber"/>
    /// reads exactly as the expression engine would.</summary>
    private static bool IsPlainNumber(string token)
    {
        if (token.Length == 0) return true;
        int i = token[0] == '-' ? 1 : 0;
        if (i >= token.Length) return false;
        for (; i < token.Length; i++)
        {
            char c = token[i];
            if (c is not ((>= '0' and <= '9') or '.' or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                return false;
        }
        // A hex-looking word with no leading zero ("abc", "dead") is a name, not a number.
        int first = token[0] == '-' ? 1 : 0;
        return token[first] is (>= '0' and <= '9') or '.';
    }

    /// <summary>
    /// Legacy script number parse (reference ahextoi): skips a decimal point
    /// so digits concatenate, and treats a leading '0' (not followed by '.')
    /// as hex.
    /// </summary>
    public static int ParseSphereNumber(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return 0;

        int pos = 0;
        while (pos < token.Length && char.IsWhiteSpace(token[pos]))
            pos++;
        if (pos >= token.Length)
            return 0;

        bool negative = token[pos] == '-';
        if (negative)
            pos++;

        bool hex = pos < token.Length && token[pos] == '0' &&
                   (pos + 1 >= token.Length || token[pos + 1] != '.');

        int value = 0;
        for (; pos < token.Length; pos++)
        {
            char ch = char.ToUpperInvariant(token[pos]);
            int digit;
            if (ch is >= '0' and <= '9')
                digit = ch - '0';
            else if (hex && ch is >= 'A' and <= 'F')
                digit = ch - 'A' + 10;
            else if (!hex && ch == '.')
                continue;
            else
                break;

            value = value * (hex ? 16 : 10) + digit;
        }

        return negative ? -value : value;
    }

    /// <summary>Reference IMulDiv / IMulDivLL (common.h:192, :207): a*b/c
    /// rounded half up, with one subtracted for a negative product - integer
    /// division truncates toward zero in both languages.</summary>
    private static long MulDivRound(long a, long b, long c)
    {
        long ab = a * b;
        return ((ab + (c / 2)) / c) - (ab < 0 ? 1 : 0);
    }

    /// <summary>
    /// Linear interpolation across the curve - port of CValueCurveDef::GetLinear
    /// (CValueDefs.cpp:90). <paramref name="skillPercent"/> is 0-1000 (0% to
    /// 100.0%); a value above 1000 extrapolates along the last segment exactly as
    /// the reference does (a skill above 100.0 is not clamped). Skill values are
    /// unsigned upstream, so a negative input reads as 0. A result at or below 0
    /// is 0.
    /// </summary>
    public int GetLinear(int skillPercent)
    {
        if (skillPercent < 0)
            skillPercent = 0;

        int loIdx;
        int segSize;
        int count = _values.Length;
        switch (count)
        {
            case 0:
                return 0;
            case 1:
                return _values[0];
            case 2:
                loIdx = 0;
                segSize = 1000;
                break;
            case 3:
                if (skillPercent >= 500)
                {
                    loIdx = 1;
                    skillPercent -= 500;
                }
                else
                {
                    loIdx = 0;
                }
                segSize = 500;
                break;
            default:
                loIdx = (int)MulDivRound(skillPercent, count, 1000);
                count--;
                if (loIdx >= count)
                    loIdx = count - 1;
                segSize = 1000 / count;
                skillPercent -= loIdx * segSize;
                break;
        }

        int loVal = _values[loIdx];
        int hiVal = _values[loIdx + 1];
        int chance = loVal + (int)MulDivRound(hiVal - loVal, skillPercent, segSize);
        return chance <= 0 ? 0 : chance;
    }

    /// <summary>
    /// ADV_RATE chance: the curve values express "skill uses per 0.1 gain"
    /// (fixed-point ×10); the gain chance per use is the inverse. Returns a
    /// per-mille chance (may exceed 1000 for trivially easy gains). Exact
    /// port of the reference GetChancePercent.
    /// </summary>
    public int GetChancePercent(int skillPercent)
    {
        int uses = GetLinear(skillPercent);
        if (uses <= 0)
            return 0;
        return 100000 / uses;
    }

    /// <summary>The script form of the curve (CValueCurveDef::Write,
    /// CValueDefs.cpp:55): every point, comma separated.</summary>
    public string Write() => string.Join(",", _values);

    public override string ToString() => Write();
}
