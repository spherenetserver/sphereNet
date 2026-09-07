namespace SphereNet.Core.Types;

/// <summary>
/// The pattern language the engine matches command names with — Source-X Str_Match
/// (sstring.cpp:1750). A pattern matches the WHOLE text, case-insensitively:
/// <c>?</c> stands for one character, <c>*</c> for any run of them, <c>[abc]</c> /
/// <c>[a-z]</c> for a set (with <c>!</c> or <c>^</c> inverting it and <c>\</c>
/// escaping), and everything else is a literal.
///
/// A plain name is therefore an EXACT match, not a prefix: reducing this to
/// "starts with" made "f_job" cancel "f_job_extra" as well, and made "f_jo?" cancel
/// nothing at all.
/// </summary>
public static class SpherePattern
{
    /// <summary>Does <paramref name="text"/> match <paramref name="pattern"/> in full?
    /// A null or empty pattern matches only empty text, as an empty C string does
    /// upstream.</summary>
    public static bool Matches(string? pattern, string? text)
        => Match(pattern ?? "", 0, text ?? "", 0);

    private static bool Match(string p, int pi, string t, int ti)
    {
        while (pi < p.Length)
        {
            // Text exhausted: only a trailing '*' can still match.
            if (ti >= t.Length)
                return p[pi] == '*' && pi + 1 == p.Length;

            switch (p[pi])
            {
                case '?':
                    break;

                case '*':
                    // Skip the run of wildcards, taking one character per '?'.
                    while (pi < p.Length && (p[pi] == '*' || p[pi] == '?'))
                    {
                        if (p[pi] == '?' && ti++ >= t.Length)
                            return false;
                        pi++;
                    }
                    if (pi >= p.Length)
                        return true;            // '*' at the end takes the rest
                    for (int at = ti; at <= t.Length; at++)
                    {
                        if (Match(p, pi, t, at))
                            return true;
                    }
                    return false;

                case '[':
                {
                    if (!MatchSet(p, ref pi, char.ToLowerInvariant(t[ti])))
                        return false;
                    break;
                }

                default:
                    if (char.ToLowerInvariant(p[pi]) != char.ToLowerInvariant(t[ti]))
                        return false;
                    break;
            }
            pi++;
            ti++;
        }
        return ti >= t.Length;
    }

    /// <summary>One <c>[..]</c> construct. <paramref name="pi"/> enters on the '[' and
    /// leaves on the closing ']' so the caller's step lands after it.</summary>
    private static bool MatchSet(string p, ref int pi, char c)
    {
        int i = pi + 1;
        bool invert = false;
        if (i < p.Length && (p[i] == '!' || p[i] == '^'))
        {
            invert = true;
            i++;
        }

        bool matched = false;
        bool closed = false;
        while (i < p.Length)
        {
            if (p[i] == ']')
            {
                closed = true;
                break;
            }

            char lo;
            if (p[i] == '\\' && i + 1 < p.Length)
                lo = char.ToLowerInvariant(p[++i]);
            else
                lo = char.ToLowerInvariant(p[i]);

            char hi = lo;
            // "a-z": the '-' is only a range when something follows it.
            if (i + 2 < p.Length && p[i + 1] == '-' && p[i + 2] != ']')
            {
                i += 2;
                hi = p[i] == '\\' && i + 1 < p.Length
                    ? char.ToLowerInvariant(p[++i])
                    : char.ToLowerInvariant(p[i]);
            }

            if (hi < lo) (lo, hi) = (hi, lo);
            if (c >= lo && c <= hi)
                matched = true;
            i++;
        }

        // A construct with no closing bracket is a malformed pattern; upstream stops
        // matching rather than guessing.
        if (!closed)
        {
            pi = p.Length;
            return false;
        }

        pi = i;                                  // sit on the ']'
        return matched != invert;
    }
}
