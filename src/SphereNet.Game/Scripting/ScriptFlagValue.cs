using System.Globalization;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;

namespace SphereNet.Game.Scripting;

/// <summary>
/// A flag word as Source-X loads one: GetArgVal / GetArgULLVal, i.e. the expression
/// evaluator (Exp_GetVal). "FLAGS=&lt;FLAGS&gt;&amp;~statf_criminal",
/// "ATTR=&lt;ATTR&gt;|attr_newbie" and "FLAGS=0100" are all one number to it. Setters that
/// only split a '|' list read the first form as nothing and wiped the whole word.
/// </summary>
public static class ScriptFlagValue
{
    /// <summary>Evaluate <paramref name="text"/>: a Sphere number (leading 0 = hex), a
    /// '|' list of numbers and DEFNAMEs, or any expression over them. False when
    /// nothing in it can be read as a number.</summary>
    public static bool TryEvaluate(string? text, out long value)
    {
        value = 0;
        string t = (text ?? "").Trim();
        if (t.Length == 0)
            return false;
        if (ScriptNumber.TryParseToken(t, out value))
            return true;

        // The plain OR-list first: it needs no parser and covers DEFNAMEs the
        // expression evaluator would also resolve.
        long or = 0;
        bool listOk = true;
        foreach (string token in t.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ScriptNumber.TryParseToken(token, out long n) ||
                DefinitionLoader.StaticResources?.TryResolveDefNameValue(token, out n) == true)
            {
                or |= n;
                continue;
            }
            listOk = false;
            break;
        }
        if (listOk)
        {
            value = or;
            return true;
        }

        var parser = new SphereNet.Scripting.Expressions.ExpressionParser
        {
            VariableResolver = name => DefinitionLoader.StaticResources?.TryResolveDefNameValue(name, out long v) == true
                ? v.ToString(CultureInfo.InvariantCulture) : null
        };
        return parser.TryEvaluate(t, out value);
    }

    /// <summary>Whether <paramref name="text"/> is more than a flat name/number list -
    /// it carries an operator only an expression gives meaning to.</summary>
    public static bool IsExpression(string? text) =>
        !string.IsNullOrEmpty(text) && text.AsSpan().IndexOfAny("&~^()+-*/<>!") >= 0;
}
