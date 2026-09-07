namespace SphereNet.Core.Types;

/// <summary>
/// The boundary between the script world's numbers and the engine's own fields.
///
/// Source-X carries ARGN1/2/3 as int64 (CScriptTriggerArgs.h:21) while most game
/// fields it feeds — damage, a skill id, a delay in tenths — are narrower. The
/// transport stays 64-bit; the narrowing happens HERE, at the field that needs it,
/// and saturates rather than wrapping, so an out-of-range script value becomes an
/// extreme instead of flipping sign.
/// </summary>
public static class ScriptNumber
{
    /// <summary>Narrow a script number to an engine <see cref="int"/> field,
    /// saturating at the bounds instead of wrapping.</summary>
    public static int ToEngineInt(long value) =>
        value >= int.MaxValue ? int.MaxValue :
        value <= int.MinValue ? int.MinValue :
        (int)value;

    /// <summary>Read one Sphere numeric TOKEN. A leading '0' means hexadecimal
    /// (<c>010</c> is 16, <c>0A</c> is 10) - it is a base marker, not a digit - and an
    /// explicit <c>0x</c> prefix means the same. Everything else is decimal, so
    /// <c>10</c> is ten. Reading a bare decimal as hex silently addressed a different
    /// object; reading Sphere hex as decimal silently changed a count.</summary>
    public static bool TryParseToken(string? token, out long value)
    {
        value = 0;
        string t = (token ?? "").Trim();
        if (t.Length == 0) return false;

        bool negative = t[0] == '-';
        if (negative) t = t[1..].Trim();
        if (t.Length == 0) return false;

        bool ok;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            ok = long.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        else if (t.Length > 1 && t[0] == '0')
            ok = long.TryParse(t.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out value);
        else
            ok = long.TryParse(t, out value);

        if (!ok) return false;
        if (negative) value = -value;
        return true;
    }

    /// <summary>Read the EXPRESSION a command line starts with, and say how much of
    /// the line it used. Source-X reads a delay with Exp_Get64Val, which consumes the
    /// whole expression - parentheses, multiplication and the spaces around an
    /// operator included - and leaves the pointer on whatever follows
    /// (CObjBase.cpp:2777, CExpression.cpp:794/1256). Cutting the line at the first
    /// space instead meant "2*3, f_done" scheduled nothing and "1 + 1, f_done" queued
    /// a job called "+".
    ///
    /// Operands are Sphere numeric tokens (a leading zero is hexadecimal); the
    /// operators are + - * / % and parentheses. The scan stops at the first thing that
    /// cannot continue an expression - a comma, or the name that follows the delay -
    /// and <paramref name="consumed"/> is where that happened.</summary>
    public static bool TryEvaluatePrefix(string? text, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;
        string s = text ?? "";
        int i = 0;
        if (!ParseSum(s, ref i, out value))
            return false;
        consumed = i;
        return true;
    }

    private static bool ParseSum(string s, ref int i, out long value)
    {
        if (!ParseProduct(s, ref i, out value))
            return false;
        while (true)
        {
            int save = i;
            SkipSpace(s, ref i);
            if (i >= s.Length || (s[i] != '+' && s[i] != '-'))
            {
                i = save;
                return true;
            }
            char op = s[i++];
            if (!ParseProduct(s, ref i, out long rhs))
            {
                i = save;                       // a dangling operator is not ours
                return true;
            }
            value = op == '+' ? value + rhs : value - rhs;
        }
    }

    private static bool ParseProduct(string s, ref int i, out long value)
    {
        if (!ParseUnary(s, ref i, out value))
            return false;
        while (true)
        {
            int save = i;
            SkipSpace(s, ref i);
            if (i >= s.Length || (s[i] != '*' && s[i] != '/' && s[i] != '%'))
            {
                i = save;
                return true;
            }
            char op = s[i++];
            if (!ParseUnary(s, ref i, out long rhs))
            {
                i = save;
                return true;
            }
            if (op == '*') value *= rhs;
            else if (rhs == 0) { i = save; return true; }   // division by zero: stop here
            else if (op == '/') value /= rhs;
            else value %= rhs;
        }
    }

    private static bool ParseUnary(string s, ref int i, out long value)
    {
        value = 0;
        SkipSpace(s, ref i);
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
        {
            char sign = s[i];
            int inner = i + 1;
            if (!ParseUnary(s, ref inner, out long operand))
                return false;
            i = inner;
            value = sign == '-' ? -operand : operand;
            return true;
        }
        if (i < s.Length && s[i] == '(')
        {
            int inner = i + 1;
            if (!ParseSum(s, ref inner, out long grouped))
                return false;
            SkipSpace(s, ref inner);
            if (inner >= s.Length || s[inner] != ')')
                return false;
            i = inner + 1;
            value = grouped;
            return true;
        }
        return ParseNumberToken(s, ref i, out value);
    }

    private static bool ParseNumberToken(string s, ref int i, out long value)
    {
        value = 0;
        SkipSpace(s, ref i);
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.'))
            i++;
        if (i == start || !TryParseToken(s[start..i], out value))
        {
            i = start;                          // not a number: the expression ends
            return false;
        }
        return true;
    }

    private static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
            i++;
    }

    /// <summary>The AMOUNT field of a script factory line (NEWITEM, and the recipe
    /// rows behind it). Source-X evaluates it as an expression and hands the result
    /// straight to SetAmount, which stores a zero as a zero (CScriptObj.cpp:1358,
    /// CItem.cpp:2207): raising zero to one turned a row a script had switched off
    /// into a delivered item. An unreadable field keeps the default of one.</summary>
    public static ushort ToScriptAmount(string? text) =>
        TryParseArgument(text, out long value)
            ? (ushort)(value < 0 ? 0 : value > ushort.MaxValue ? ushort.MaxValue : value)
            : (ushort)1;

    /// <summary>Read a Sphere numeric ARGUMENT: a token, or a simple sum of them.
    /// Source-X arguments go through the expression parser (GetArgVal -&gt;
    /// Exp_GetVal, CScript.cpp:154), so <c>1+1</c> is two rather than a parse failure
    /// that quietly became a default. This covers the token-and-sum grammar the
    /// engine's own verb arguments use; a full expression belongs to the script
    /// interpreter, which resolves it before the verb ever sees it.</summary>
    public static bool TryParseArgument(string? text, out long value)
    {
        value = 0;
        string s = (text ?? "").Trim();
        if (s.Length == 0) return false;

        long total = 0;
        int i = 0, sign = 1;
        bool any = false;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && s[i] != '+' && !(i > start && s[i] == '-')) i++;
            string term = s[start..i].Trim();
            if (term.Length == 0) return false;
            if (!TryParseToken(term, out long termValue)) return false;
            total += sign * termValue;
            any = true;
            if (i < s.Length)
            {
                sign = s[i] == '-' ? -1 : 1;
                i++;
            }
        }
        if (!any) return false;
        value = total;
        return true;
    }
}
