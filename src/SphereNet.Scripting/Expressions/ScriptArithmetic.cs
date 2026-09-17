namespace SphereNet.Scripting.Expressions;

/// <summary>
/// Whether an assignment's right-hand side is arithmetic that must be worked out before
/// it is stored.
///
/// Upstream loads a numeric key with <c>s.GetArgVal()</c>, which is
/// <c>Exp_GetVal(m_pszArg)</c> - the whole argument goes through the expression engine,
/// operators and all (CScript.cpp:154). A string key uses <c>GetArgStr</c> instead and
/// keeps its text. This engine substitutes <c>&lt;...&gt;</c> into an assignment but
/// stops there, so <c>MORE2=&lt;MOREX&gt;/3</c> arrived at the object as the literal
/// "30/3" and parsed as zero.
///
/// The key-by-key numeric/string split upstream gets from its property tables does not
/// exist here, so the decision is made on the SHAPE of the value instead: a value made
/// only of Sphere numbers, operators and brackets is arithmetic and nothing else, and
/// anything carrying a letter that is not a hex digit, a quote, a comma, a decimal point
/// or a brace is left exactly as it was. That keeps every text key (NAME, EVENTS), the
/// coordinate lists (<c>P=-5,-3</c>), the brace forms (which
/// <see cref="BraceRange"/> owns) and the decimal skill form out of it.
/// </summary>
public static class ScriptArithmetic
{
    /// <summary>True when the text is one arithmetic expression over Sphere numbers -
    /// at least one binary operator, every operand a number, brackets balanced.</summary>
    public static bool IsPlainArithmetic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string s = text.Trim();
        if (s.Length == 0) return false;

        bool sawOperator = false;
        bool expectValue = true;  // at the start, and after an operator or '('
        int depth = 0;

        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '(')
            {
                if (!expectValue) return false;
                depth++; i++; continue;
            }
            if (c == ')')
            {
                if (expectValue || --depth < 0) return false;
                i++; continue;
            }

            if (expectValue)
            {
                if (c is '-' or '+' or '~')   // unary sign
                {
                    i++;
                    while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                    if (i >= s.Length) return false;
                }
                int start = i;
                while (i < s.Length && IsNumberChar(s[i])) i++;
                if (i == start) return false;
                if (!BraceRange.TryParseSphereInteger(s[start..i], out _)) return false;
                expectValue = false;
                continue;
            }

            if (!IsBinaryOperator(c)) return false;
            sawOperator = true;
            expectValue = true;
            i++;
        }

        return sawOperator && !expectValue && depth == 0;
    }

    /// <summary>Digits and the hex letters; whether the run is really hex is settled by
    /// <see cref="BraceRange.TryParseSphereInteger"/>, which needs the leading zero.</summary>
    private static bool IsNumberChar(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>The binary operators Sphere writes between two numbers. Shifts are not
    /// among them: they are spelled with the angle brackets that mark a substitution,
    /// so a value carrying one is not a finished argument.</summary>
    private static bool IsBinaryOperator(char c) =>
        c is '+' or '-' or '*' or '/' or '%' or '|' or '&' or '^';
}
