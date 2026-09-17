namespace SphereNet.Scripting.Expressions;

/// <summary>
/// What a Sphere <c>{...}</c> expression means, in one place.
///
/// Upstream evaluates braces inside the expression engine itself
/// (CExpression::GetSingle case '{' -> GetRangeNumber, CExpression.cpp:790), so every
/// value that is read as a number rolls the range - no matter which object or key it
/// was written for. This engine has two callers that need the same rule but cannot
/// share a token evaluator: <see cref="ExpressionParser"/> evaluates each token as a
/// full expression (so <c>&lt;...&gt;</c> works), while a property assignment arriving
/// at an object has only the plain text. They share the DECISION here instead, and each
/// supplies its own token values.
/// </summary>
public static class BraceRange
{
    /// <summary>Split brace content on whitespace, respecting nested &lt;...&gt; and
    /// {...} so an inner expression is not torn in half.</summary>
    public static List<string> SplitTokens(string s)
    {
        var tokens = new List<string>();
        int depth = 0, angle = 0, start = 0;
        bool inTok = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '{') depth++;
            else if (c == '}' && depth > 0) depth--;

            if (char.IsWhiteSpace(c) && angle == 0 && depth == 0)
            {
                if (inTok) { tokens.Add(s[start..i]); inTok = false; }
            }
            else if (!inTok)
            {
                start = i;
                inTok = true;
            }
        }
        if (inTok) tokens.Add(s[start..]);
        return tokens;
    }

    /// <summary>One token = that value. Two = a random value in the range. More = an
    /// even list of (value, weight) pairs; an odd count above two is a script error and
    /// yields 0, as upstream's GetRangeNumber does.</summary>
    public static long Pick(IReadOnlyList<long> vals, string inner, Action<string>? diag)
    {
        if (vals.Count == 0) return 0;
        if (vals.Count == 1) return vals[0];

        if (vals.Count == 2)
        {
            long lo = Math.Min(vals[0], vals[1]);
            long hi = Math.Max(vals[0], vals[1]);
            return NextInclusiveRandom(lo, hi);
        }

        if ((vals.Count & 1) != 0)
        {
            diag?.Invoke($"[script] Bad {{...}} range: odd number of values/weights ({vals.Count}) in '{{{inner}}}'");
            return 0;
        }

        long totalWeight = 0;
        for (int i = 1; i < vals.Count; i += 2)
        {
            if (vals[i] <= 0)
                diag?.Invoke($"[script] Bad {{...}} range: non-positive weight {vals[i]} in '{{{inner}}}'");
            totalWeight += Math.Max(0, vals[i]);
        }
        if (totalWeight <= 0) return vals[0];

        long roll = Random.Shared.NextInt64(totalWeight);
        for (int i = 0; i + 1 < vals.Count; i += 2)
        {
            roll -= Math.Max(0, vals[i + 1]);
            if (roll < 0) return vals[i];
        }
        return vals[0];
    }

    /// <summary>Uniform random in the INCLUSIVE range [lo, hi] without the
    /// <c>hi + 1</c> that overflows (and throws) when hi == long.MaxValue - the bug
    /// that let <c>{1 0x7fffffffffffffff}</c> crash the tick.</summary>
    public static long NextInclusiveRandom(long lo, long hi)
    {
        if (lo >= hi) return lo; // lo == hi, and defensively any inverted range
        if (hi == long.MaxValue)
        {
            // hi + 1 would overflow. NextInt64(min,max) is exclusive of max, so
            // [lo, MaxValue) drops only the single endpoint - acceptable for a
            // pathological range, and it never throws.
            return lo == long.MinValue
                ? Random.Shared.NextInt64()
                : Random.Shared.NextInt64(lo, long.MaxValue);
        }
        return Random.Shared.NextInt64(lo, hi + 1);
    }

    /// <summary>
    /// Roll a brace expression written as plain numbers, for callers holding text with
    /// no expression engine to hand - a property assignment that has already been
    /// through &lt;...&gt; substitution.
    ///
    /// Deliberately strict: the whole value must be one brace group and every token a
    /// Sphere integer (leading zero = hex). Anything else is left to the caller, which
    /// covers the two shapes that must NOT be rolled here - a resource list
    /// (<c>FRUIT={i_fruit_pumpkin ...}</c>), and the decimal skill form
    /// (<c>TACTICS={29.0 44.0}</c>), whose x10 scaling belongs to the character that
    /// knows the key names a skill.
    /// </summary>
    public static bool TryRollNumeric(string? value, out long rolled)
    {
        rolled = 0;
        if (value == null) return false;
        string text = value.Trim();
        if (text.Length < 3 || text[0] != '{' || text[^1] != '}') return false;

        string inner = text[1..^1];
        if (inner.Contains('{') || inner.Contains('}')) return false;

        var tokens = SplitTokens(inner.Replace(',', ' '));
        if (tokens.Count == 0) return false;

        var vals = new long[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!TryParseSphereInteger(tokens[i], out vals[i])) return false;
        }

        rolled = Pick(vals, inner, null);
        return true;
    }

    /// <summary>A Sphere integer and nothing else: optional sign, then either a hex
    /// run behind a leading zero or plain digits. A token carrying a decimal point is
    /// refused - see <see cref="TryRollNumeric"/>.</summary>
    public static bool TryParseSphereInteger(string token, out long value)
    {
        value = 0;
        if (token.Length == 0) return false;

        int pos = 0;
        bool negative = token[pos] == '-';
        if (negative) pos++;
        if (pos >= token.Length) return false;

        bool hex = token[pos] == '0' && token.Length - pos > 1;
        long acc = 0;
        for (; pos < token.Length; pos++)
        {
            char ch = char.ToUpperInvariant(token[pos]);
            int digit;
            if (ch is >= '0' and <= '9') digit = ch - '0';
            else if (hex && ch is >= 'A' and <= 'F') digit = ch - 'A' + 10;
            else return false;

            acc = acc * (hex ? 16 : 10) + digit;
            if (acc > int.MaxValue) return false;
        }

        value = negative ? -acc : acc;
        return true;
    }
}
