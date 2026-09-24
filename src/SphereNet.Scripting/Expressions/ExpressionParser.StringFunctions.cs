namespace SphereNet.Scripting.Expressions;

/// <summary>
/// String functions of the CScriptObj function table (CScriptObj_functions.tbl) whose
/// argument parsing is peculiar enough to deserve a faithful port of the upstream
/// tokenizer rather than a split on commas: STRTOKEN, STRRANDRANGE, STRFIRSTCAP and
/// LISTCOL.
/// </summary>
public sealed partial class ExpressionParser
{
    /// <summary>Source-X CWebPageDef::sm_iListIndex: the row counter of the web page
    /// list verbs (CLIENTLIST / GUILDLIST / TOWNLIST / GMPAGELIST), read by LISTCOL.
    /// Process-wide like upstream's static; zero outside such a list.</summary>
    public static int WebListIndex { get; set; }

    /// <summary>Dispatch for the functions in this file. Returns false when the
    /// expression is none of them.</summary>
    private bool TryResolveStringTableFunction(string varExpr, out string result)
    {
        result = "";

        // LISTCOL (CScriptObj.cpp:614): the alternating row colour of a web page list.
        if (varExpr.Equals("LISTCOL", StringComparison.OrdinalIgnoreCase))
        {
            result = (WebListIndex & 1) != 0 ? "bgcolor=\"#E8E8E8\"" : "";
            return true;
        }

        if (MatchesFunction(varExpr, "STRTOKEN", out string tokArgs))
        {
            result = StrToken(ResolveAngleBrackets(tokArgs)) ?? "";
            return true;
        }

        if (MatchesFunction(varExpr, "STRRANDRANGE", out string rangeArgs))
        {
            result = StrRandRange(ResolveAngleBrackets(rangeArgs));
            return true;
        }

        // STRFIRSTCAP is in the function table (CScriptObj_functions.tbl:45) but has no
        // case of its own: r_WriteVal hands it to StringFunction (CScriptObj.cpp:1171),
        // whose switch knows only CHR / STRREVERSE / STRTOLOWER / STRTOUPPER, so the
        // result is resolved and empty. Answering the capitalised text would be a
        // behaviour upstream does not have.
        if (MatchesFunction(varExpr, "STRFIRSTCAP", out _))
        {
            result = "";
            return true;
        }

        return false;
    }

    /// <summary>NAME followed by a separator (SKIP_SEPARATORS, CScriptObj.cpp:600) or
    /// an opening parenthesis.</summary>
    private static bool MatchesFunction(string varExpr, string name, out string args)
    {
        args = "";
        if (!varExpr.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (varExpr.Length == name.Length)
            return true;
        char next = varExpr[name.Length];
        if (next is not (' ' or '\t' or '(' or '.' or ','))
            return false;
        args = varExpr[name.Length..].TrimStart(' ', '\t', '.');
        return true;
    }

    /// <summary>Source-X SSC_StrToken (CScriptObj.cpp:1003):
    /// <c>STRTOKEN "text",index[-end],"separator"</c>. Index 0 answers the token
    /// count, n answers token n (1-based), n-m answers tokens n..m rejoined with the
    /// separator (m of 0 or past the end means "to the end"). A negative index or one
    /// past the count is a failed read. Only the first character of the separator
    /// counts.</summary>
    public string? StrToken(string args)
    {
        var ppArgs = ParseCmdsAdv(args, 3, ",");
        if (ppArgs.Count < 3)
            return null;

        string sep = UnQuote(ppArgs[2]);
        if (sep.Length > 1)
            sep = sep[..1];

        string scriptArgs = UnQuote(ppArgs[0]);
        var ppCmd = ParseCmdsAdv(scriptArgs, 255, sep);
        int count = ppCmd.Count;

        // Str_ParseCmdsAdv(ppArgs[1], ..., "-") cuts the index argument at its first
        // '-' in place, so Exp_GetLLVal(ppArgs[1]) then reads only the first half.
        var ppArrays = ParseCmdsAdv(ppArgs[1], 2, "-");
        string first = ppArrays.Count > 0 ? ppArrays[0] : "";
        long iValue = EvaluateArg(first);
        long iValueEnd = iValue;
        if (ppArrays.Count > 1)
        {
            iValue = EvaluateArg(ppArrays[0]);
            iValueEnd = EvaluateArg(ppArrays[1]);
            if (iValueEnd <= 0 || iValueEnd > count)
                iValueEnd = count;
        }

        if (iValue < 0)
            return null;
        if (iValue == 0)
            return count.ToString();
        if (iValue > count)
            return null;
        if (iValue == iValueEnd)
            return ppCmd[(int)iValue - 1];

        var sb = new System.Text.StringBuilder(ppCmd[(int)iValue - 1]);
        for (long i = iValue + 1; i <= iValueEnd; ++i)
        {
            sb.Append(sep);
            sb.Append(ppCmd[(int)i - 1]);
        }
        return sb.ToString();
    }

    private long EvaluateArg(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return 0;
        return Evaluate(t.AsSpan());
    }

    /// <summary>Source-X CExpression::GetRangeString (CExpression.cpp:2079), the body of
    /// STRRANDRANGE: <c>value</c> answers the value, <c>v1 w1 v2 w2 ...</c> picks one
    /// value by weight. The values are returned as written - quotes included.</summary>
    public string StrRandRange(string expr)
    {
        var args = GetRangeArgs(expr);
        int qty = args.Count;
        if (qty <= 0)
            return "";

        // A single element is copied with its length minus one (CExpression.cpp:2098),
        // which drops its last character when the range is not closed by a '}'.
        if (qty == 1)
        {
            var (s0, e0) = args[0];
            int len = e0 - s0 - 1;
            return len > 0 ? expr.Substring(s0, len) : "";
        }

        // An odd number of elements is an unpaired value-weight list: invalid.
        if ((qty % 2) == 1)
            return "";

        long total = 0;
        var weights = new long[qty];
        for (int i = 1; i + 1 <= qty; i += 2)
        {
            var (s, e) = args[i];
            string weight = expr[s..e];
            if (!IsSimpleNumberString(weight))
                return "";
            weights[i] = Evaluate(weight.AsSpan());
            total += weights[i];
        }
        if (total <= 0)
            return "";

        long roll = Random.Shared.NextInt64(total) + 1;
        int k = 1;
        for (; k + 1 <= qty; k += 2)
        {
            roll -= weights[k];
            if (roll <= 0)
                break;
        }
        if (k >= qty)
            return "";
        var (vs, ve) = args[k - 1];
        return expr[vs..ve];
    }

    /// <summary>Port of GetRangeArgsPos (CExpression.cpp:1869) with
    /// fIgnoreMissingEndBracket: the [start, end) of every argument of a range, which
    /// may be separated by whitespace or commas and end at '}' or the end of the text.</summary>
    private static List<(int Start, int End)> GetRangeArgs(string expr)
    {
        const int kiRangeMaxArgs = 96;
        var result = new List<(int, int)>();
        int p = 0;
        int n = expr.Length;
        char At(int i) => i < n ? expr[i] : '\0';

        while (At(p) != '\0')
        {
            if (At(p) == ';')
                return result;
            if (At(p) == ',')
                ++p;

            if (result.Count + 1 >= kiRangeMaxArgs)
                return result;

            while (At(p) != '\0' && char.IsWhiteSpace(At(p))) ++p;
            int start = p;

            if (At(p) == '{')
            {
                int depth = 1;
                while (depth != 0)
                {
                    ++p;
                    if (At(p) == '\0') { result.Add((start, p)); return result; }
                    if (At(p) == '{') ++depth;
                    else if (At(p) == '}') --depth;
                }
                ++p;
                result.Add((start, p));
                continue;
            }

            while (true)
            {
                char ch = At(p);
                if (ch == '\0')
                {
                    result.Add((start, p));
                    return result;
                }
                if (char.IsWhiteSpace(ch) || ch == ',')
                {
                    result.Add((start, p));
                    while (At(p) != '\0' && char.IsWhiteSpace(At(p))) ++p;
                    if (At(p) == '}' || At(p) == '\0')
                        return result;
                    break;
                }
                if (ch == '}')
                {
                    result.Add((start, p));
                    return result;
                }
                ++p;
            }
        }
        return result;
    }

    /// <summary>Port of IsSimpleNumberString (sstring.cpp:898).</summary>
    internal static bool IsSimpleNumberString(string s)
    {
        if (s.Length == 0)
            return false;
        bool mathSep = true, hexStart = false, white = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is (>= 'A' and <= 'F') or (>= 'a' and <= 'f'))
            {
                if (!hexStart) return false;
                white = false;
                mathSep = false;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                hexStart = false;
                white = true;
                continue;
            }
            if (ch is >= '0' and <= '9')
            {
                if (white && !mathSep) return false;
                if (ch == '0') hexStart = true;
                white = false;
                mathSep = false;
                continue;
            }
            if (ch == '/' && (i + 1 >= s.Length || s[i + 1] != '/'))
                mathSep = true;
            else
                mathSep = "+-\\*~|&!%^()".IndexOf(ch) >= 0;
            if (!mathSep) return false;
            hexStart = false;
            white = false;
        }
        return true;
    }

    /// <summary>Port of Str_UnQuote (sstring.cpp:1891): skip leading whitespace, drop
    /// an opening quote, and cut the text at its LAST quote character.</summary>
    internal static string UnQuote(string s)
    {
        int start = 0;
        while (start < s.Length && char.IsWhiteSpace(s[start])) start++;
        if (start < s.Length && s[start] is '"' or '\'')
            start++;
        string rest = s[start..];
        for (int i = rest.Length - 1; i >= 0; --i)
        {
            if (rest[i] is '"' or '\'')
                return rest[..i];
        }
        return rest;
    }

    /// <summary>Port of Str_ParseCmdsAdv / Str_ParseAdv (CExpression.cpp:325-507): split
    /// on any character of <paramref name="seps"/>, but not inside quotes or inside
    /// {}, [] and () (unless that bracket is itself a separator). At most
    /// <paramref name="max"/> arguments; the last keeps the rest of the line.</summary>
    public static List<string> ParseCmdsAdv(string line, int max, string seps)
    {
        var result = new List<string>();
        // The upstream parser works in place on a NUL-terminated buffer; model it so
        // the argument boundaries (and the trailing-whitespace trim applied to every
        // remainder) come out exactly the same.
        char[] buf = new char[line.Length + 1];
        line.CopyTo(0, buf, 0, line.Length);
        buf[^1] = '\0';

        int p = 0;
        while (buf[p] != '\0' && IsSpaceChar(buf[p])) p++;
        if (buf[p] == '\0')
            return result;

        var starts = new List<int> { p };
        while (ParseAdv(buf, starts[^1], seps, out int next))
        {
            starts.Add(next);
            if (starts.Count >= max)
                break;
        }

        foreach (int s in starts)
        {
            int e = s;
            while (buf[e] != '\0') e++;
            result.Add(new string(buf, s, e - s));
        }
        return result;
    }

    private static bool IsSpaceChar(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

    private static bool ParseAdv(char[] buf, int p, string seps, out int nextArg)
    {
        nextArg = -1;
        while (buf[p] != '\0' && IsSpaceChar(buf[p])) p++;

        bool quotes = false;
        int iQuotes = 0;
        int curly = 0, square = 0, round = 0, angle = 0;
        bool sepCurly = false, sepSquare = false, sepRound = false, sepAngle = false;
        foreach (char sc in seps)
        {
            if (sc is '{' or '}') sepCurly = true;
            else if (sc is '[' or ']') sepSquare = true;
            else if (sc is '(' or ')') sepRound = true;
            else if (sc is '<' or '>') sepAngle = true;
        }

        char ch;
        for (; ; ++p)
        {
            ch = buf[p];
            if (ch is '"' or '\'')
            {
                if (!quotes)
                {
                    quotes = true;
                }
                else
                {
                    int q = p + 1;
                    char chNext = buf[q];
                    while (chNext is '"' or '\'')
                    {
                        ++q;
                        chNext = buf[q];
                    }
                    if (chNext is '\0' or ',' or ' ' or '\'')
                        --iQuotes;
                    else
                        ++iQuotes;
                    if (iQuotes < 0)
                    {
                        iQuotes = 0;
                        quotes = false;
                    }
                }
            }
            else if (ch == '\0')
            {
                nextArg = p;
                return false;
            }
            else if (!quotes)
            {
                if (ch == '{') { if (!sepCurly && square == 0 && round == 0 && angle == 0) ++curly; }
                else if (ch == '[') { if (!sepSquare && curly == 0 && round == 0 && angle == 0) ++square; }
                else if (ch == '(') { if (!sepRound && curly == 0 && square == 0 && angle == 0) ++round; }
                else if (ch == '<') { if (!sepAngle && curly == 0 && square == 0 && round == 0) ++angle; }
                else if (ch == '}') { if (!sepCurly && curly != 0) --curly; }
                else if (ch == ']') { if (!sepSquare && square != 0) --square; }
                else if (ch == ')') { if (!sepRound && round != 0) --round; }
                else if (ch == '>') { if (!sepAngle && angle != 0) --angle; }

                if (curly <= 0 && square <= 0 && round <= 0 && seps.IndexOf(ch) >= 0)
                    break;
            }
        }

        buf[p] = '\0';
        ++p;
        if (IsSpaceChar(ch))
        {
            while (buf[p] != '\0' && IsSpaceChar(buf[p])) p++;
            if (buf[p] != '\0' && seps.IndexOf(buf[p]) >= 0)
                ++p;
        }

        // Str_TrimWhitespace on the remainder: leading and trailing whitespace go.
        while (buf[p] != '\0' && IsSpaceChar(buf[p])) p++;
        int end = p;
        while (buf[end] != '\0') end++;
        while (end > p && IsSpaceChar(buf[end - 1]))
            buf[--end] = '\0';
        nextArg = p;

        return !(curly != 0 || square != 0 || round != 0 || quotes);
    }
}
