using System.Globalization;

namespace SphereNet.Scripting.Expressions;

/// <summary>
/// Expression evaluator. Maps to CExpression in Source-X.
/// Evaluates arithmetic, comparison, logical, and bitwise expressions.
/// Supports hex (0x), decimal, and variable references.
/// </summary>
public sealed class ExpressionParser
{
    private int _resolveDepth;
    private const int MaxResolveDepth = 128; // Source-X _iGetVal_Reentrant cap

    // Arithmetic-recursion guard. The right-fold parser (operators + parens) and
    // the unary parsers recurse on the native stack with no cap in Source-X, so a
    // pathological expression — thousands of parens, "1+1+1+...", "----1" — would
    // StackOverflow, which is uncatchable and process-fatal. Bound the logical
    // nesting depth and fail the evaluation instead. ~512 levels is far beyond any
    // real script yet an order of magnitude below the native stack limit.
    private int _evalDepth;
    private const int MaxEvalDepth = 512;
    // Absurd single-expression length is refused up front (defense in depth on top
    // of the depth guard). No legitimate expression approaches this.
    private const int MaxExpressionLength = 65536;

    private const int MaxRegexPatternLength = 512;
    private const int MaxRegexInputLength = 4096;
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Variable/property resolver callback.
    /// Given a variable name, returns its string value.
    /// </summary>
    public Func<string, string?>? VariableResolver { get; set; }

    /// <summary>Cleared by <see cref="ParsePrimary"/> when an atom resolves to a
    /// non-numeric string (an unresolved bareword or a &lt;...&gt; that isn't a
    /// number). Only meaningful across a single <see cref="TryEvaluate"/> call, which
    /// saves/restores it; the plain <see cref="Evaluate"/> path ignores it.</summary>
    private bool _numericOk = true;

    /// <summary>
    /// Dialog-response accessor. Set while executing a [Dialog X Button] On=
    /// block so <c>&lt;ArgN&gt;</c>, <c>&lt;Argtxt[N]&gt;</c>, <c>&lt;Argchk[N]&gt;</c>
    /// and <c>&lt;ArgV&gt;</c>/<c>&lt;ArgV[N]&gt;</c> resolve against the real
    /// gump response. Null outside a button handler.
    /// </summary>
    public Func<string, string?>? DialogArgResolver { get; set; }

    /// <summary>When true, unresolved <c>&lt;X&gt;</c> expressions are reported
    /// via <see cref="DiagnosticLogger"/>. Default off so production logs stay
    /// quiet; flip on with the <c>.SCRIPTDEBUG</c> command while hunting
    /// missing properties in imported Sphere scripts.
    ///
    /// Performance note: leaving this on under load is OK for development but
    /// not recommended in production — every unresolved &lt;X&gt; pays a
    /// string-format + log write, and noisy script packs (region/NPC ticks)
    /// can produce thousands of warnings per second. Default stays
    /// <c>false</c> for that reason.</summary>
    public bool DebugUnresolved { get; set; }

    /// <summary>Callback for diagnostic messages (unknown variables, commands).
    /// Host wires this up to write to the server console / log.</summary>
    public Action<string>? DiagnosticLogger { get; set; }

    /// <summary>Process-wide [OBSCENE] word-list checker for the ISOBSCENE
    /// intrinsic (Source-X g_Cfg.IsObscene). Static because ExpressionParser
    /// instances are created ad-hoc all over the engine; the host wires it to
    /// ResourceHolder.IsObscene at boot. Reset by test isolation.</summary>
    public static Func<string, bool>? ObsceneChecker { get; set; }

    /// <summary>
    /// Optional callback for resolving script function calls used inside
    /// angle-bracket expressions, e.g. <c>&lt;SetProcessDelay HelpPage,50&gt;</c>.
    /// Return <c>null</c> when the expression is not a callable script
    /// function so normal variable fallback can continue.
    /// </summary>
    public Func<string, string?>? FunctionResolver { get; set; }

    /// <summary>
    /// Per-thread "where am I" label used by <see cref="ReportUnresolved"/> when
    /// the caller doesn't pass an explicit context. ScriptInterpreter / dialog
    /// dispatcher / trigger runner push the current source location here before
    /// invoking expression evaluation, so unresolved warnings look like
    /// <c>[script] unresolved &lt;Eval&gt; (in d_admin.scp(412) @Click)</c>
    /// instead of the bare variable name. The field is thread-static so server
    /// tick threads and the network thread don't trample each other.
    /// </summary>
    [ThreadStatic]
    private static string? t_currentSourceLabel;

    /// <summary>Push a "where am I" label for the current thread. Returns a
    /// scope handle whose <c>Dispose</c> restores the previous label, so
    /// callers can use <c>using (parser.PushSourceLabel(...)) { ... }</c>
    /// without writing try/finally everywhere.</summary>
    public SourceLabelScope PushSourceLabel(string? label)
    {
        var prev = t_currentSourceLabel;
        t_currentSourceLabel = label;
        return new SourceLabelScope(prev);
    }

    /// <summary>Read-only view of the current thread's source label, or
    /// <c>null</c> if nothing has been pushed.</summary>
    public static string? CurrentSourceLabel => t_currentSourceLabel;

    public readonly struct SourceLabelScope : IDisposable
    {
        private readonly string? _previous;
        internal SourceLabelScope(string? previous) { _previous = previous; }
        public void Dispose() { t_currentSourceLabel = _previous; }
    }

    internal void ReportUnresolved(string varExpr, string context = "")
    {
        if (!DebugUnresolved || DiagnosticLogger == null) return;

        // Prefer the caller-supplied context, but fall back to whatever the
        // current ScriptInterpreter / dialog runner pushed for this thread.
        // This is what lets the warning name a concrete script + line even
        // for variables resolved deep inside nested <Eval>/<QVal> expansion.
        string ctx = !string.IsNullOrEmpty(context)
            ? context
            : (t_currentSourceLabel ?? "");

        string msg = string.IsNullOrEmpty(ctx)
            ? $"[script] unresolved <{varExpr}>"
            : $"[script] unresolved <{varExpr}> (in {ctx})";
        DiagnosticLogger(msg);
    }

    /// <summary>
    /// Evaluate a full expression string to a numeric value.
    /// Maps to CExpression::GetVal in Source-X.
    /// </summary>
    public long Evaluate(ReadOnlySpan<char> expr)
    {
        expr = expr.Trim();
        if (expr.IsEmpty) return 0;
        if (expr.Length > MaxExpressionLength)
        {
            DiagnosticLogger?.Invoke($"[script] expression too long ({expr.Length} chars); evaluation aborted");
            return 0;
        }

        int pos = 0;
        string text = expr.ToString();
        return ParseExpression(text, ref pos);
    }

    /// <summary>Evaluate <paramref name="expr"/> as a number, reporting whether it was
    /// actually a numeric expression. Returns false when any atom is a non-numeric
    /// string (an unresolved bareword such as a defname, or a &lt;...&gt; that resolved
    /// to text) or when trailing content is left unparsed — letting a caller (e.g. the
    /// RETURN handler) tell "this is the number 0" from "this is a string".</summary>
    public bool TryEvaluate(ReadOnlySpan<char> expr, out long value)
    {
        expr = expr.Trim();
        if (expr.IsEmpty) { value = 0; return false; }
        if (expr.Length > MaxExpressionLength)
        {
            DiagnosticLogger?.Invoke($"[script] expression too long ({expr.Length} chars); evaluation aborted");
            value = 0;
            return false;
        }

        bool prev = _numericOk;
        _numericOk = true;
        try
        {
            int pos = 0;
            string text = expr.ToString();
            value = ParseExpression(text, ref pos);
            SkipWhitespace(text, ref pos);
            return _numericOk && pos >= text.Length; // fully consumed, no string atom
        }
        finally
        {
            _numericOk = prev;
        }
    }

    /// <summary>
    /// Evaluate an IF/ELIF/WHILE condition the way Source-X does
    /// (EvaluateConditionalWhole): split the expression into subexpressions at
    /// TOP-LEVEL || and &amp;&amp; operators (parenthesis/quote aware), then combine
    /// them strictly left-to-right with short-circuiting — || and &amp;&amp; have
    /// EQUAL precedence here, unlike C. Each subexpression evaluates through
    /// the normal right-fold parser. A fully parenthesized subexpression
    /// (optionally negated with '!') recurses so nested logic works.
    /// </summary>
    public bool EvaluateConditional(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        return EvaluateConditionalInner(expr.Trim(), 0);
    }

    private enum CondOp { None, Or, And }

    private bool EvaluateConditionalInner(string expr, int depth)
    {
        if (depth > 16)
            return Evaluate(expr.AsSpan()) != 0;

        var subs = SplitConditionalSubexpressions(expr);
        if (subs.Count == 0)
            return false;

        bool value = EvaluateConditionalSub(subs[0].Text, depth);
        for (int i = 1; i < subs.Count; i++)
        {
            CondOp op = subs[i - 1].OpToNext;
            if (op == CondOp.Or)
            {
                if (value) return true; // short-circuit
                value = EvaluateConditionalSub(subs[i].Text, depth);
            }
            else if (op == CondOp.And)
            {
                if (!value) return false; // short-circuit
                value = EvaluateConditionalSub(subs[i].Text, depth);
            }
        }
        return value;
    }

    private bool EvaluateConditionalSub(string sub, int depth)
    {
        sub = sub.Trim();
        if (sub.Length == 0)
            return false;

        // Peel top-level negations: "!(...)" / "!!x". A "!=" prefix is the
        // Source-X skip-quirk handled by the unary parser, not a negation.
        bool negate = false;
        while (sub.Length > 0 && sub[0] == '!' && !(sub.Length > 1 && sub[1] == '='))
        {
            negate = !negate;
            sub = sub[1..].TrimStart();
        }

        bool value;
        if (sub.Length >= 2 && sub[0] == '(' && FindMatchingParen(sub, 0) == sub.Length - 1)
        {
            // The whole subexpression is parenthesized — it may contain
            // nested || / && logic, so recurse through the splitter.
            value = EvaluateConditionalInner(sub[1..^1].Trim(), depth + 1);
        }
        else
        {
            value = Evaluate(sub.AsSpan()) != 0;
        }
        return negate ? !value : value;
    }

    /// <summary>Index of the ')' matching the '(' at <paramref name="openIdx"/>,
    /// or -1 when unbalanced.</summary>
    private static int FindMatchingParen(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>True when the '&lt;' at <paramref name="openIdx"/> has a
    /// balancing '&gt;' later in the string (same letter/'_' open rule).</summary>
    private static bool HasMatchingAngleClose(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<')
            {
                char nxt = i + 1 < s.Length ? s[i + 1] : '\0';
                if (i == openIdx || nxt == '_' || char.IsLetter(nxt)) depth++;
            }
            else if (c == '>')
            {
                depth--;
                if (depth == 0) return true;
            }
        }
        return false;
    }

    private readonly record struct CondSubexpr(string Text, CondOp OpToNext);

    /// <summary>Source-X GetConditionalSubexpressions — cut the condition at
    /// top-level || and &amp;&amp;, skipping bracketed/quoted spans.</summary>
    private static List<CondSubexpr> SplitConditionalSubexpressions(string expr)
    {
        var subs = new List<CondSubexpr>();
        int parenDepth = 0, angleDepth = 0, curlyDepth = 0;
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < expr.Length; i++)
        {
            char ch = expr[i];
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes) continue;
            if (ch == '(') { parenDepth++; continue; }
            if (ch == ')' && parenDepth > 0) { parenDepth--; continue; }
            if (ch == '{') { curlyDepth++; continue; }
            if (ch == '}' && curlyDepth > 0) { curlyDepth--; continue; }
            if (ch == '<')
            {
                // Open an angle span only when a matching '>' actually exists
                // ahead — otherwise "IF a<b || c" (no-space less-than) hangs
                // the depth counter and the '||' split is never seen.
                char nxt = i + 1 < expr.Length ? expr[i + 1] : '\0';
                if ((nxt == '_' || char.IsLetter(nxt)) && HasMatchingAngleClose(expr, i))
                    angleDepth++;
                continue;
            }
            if (ch == '>' && angleDepth > 0) { angleDepth--; continue; }
            if (parenDepth > 0 || angleDepth > 0 || curlyDepth > 0) continue;

            if (i + 1 < expr.Length)
            {
                if (ch == '|' && expr[i + 1] == '|')
                {
                    subs.Add(new CondSubexpr(expr[start..i], CondOp.Or));
                    i++;
                    start = i + 1;
                }
                else if (ch == '&' && expr[i + 1] == '&')
                {
                    subs.Add(new CondSubexpr(expr[start..i], CondOp.And));
                    i++;
                    start = i + 1;
                }
            }
        }

        subs.Add(new CondSubexpr(expr[start..], CondOp.None));
        return subs;
    }

    public double EvaluateFloat(ReadOnlySpan<char> expr)
    {
        expr = expr.Trim();
        if (expr.IsEmpty) return 0;
        if (expr.Length > MaxExpressionLength)
        {
            DiagnosticLogger?.Invoke($"[script] expression too long ({expr.Length} chars); evaluation aborted");
            return 0;
        }

        int pos = 0;
        string text = expr.ToString();
        return ParseFloatExpression(text, ref pos);
    }

    /// <summary>
    /// Evaluate a full expression string to a string value.
    /// Resolves &lt;TAG&gt; substitutions first.
    /// </summary>
    public string EvaluateStr(string expr)
    {
        if (string.IsNullOrEmpty(expr)) return "";
        return ResolveAngleBrackets(expr);
    }

    private long ParseExpression(string text, ref int pos)
    {
        if (++_evalDepth > MaxEvalDepth)
        {
            --_evalDepth;
            DiagnosticLogger?.Invoke("[script] expression nesting too deep; evaluation aborted");
            return 0;
        }
        try
        {
            // Source-X GetVal: parse ONE operand (GetSingle), then apply at most
            // one operator whose right side re-parses the ENTIRE remainder
            // (GetValMath). Sphere expressions therefore have NO operator
            // precedence and fold right-to-left: "2*3+1" is 2*(3+1)=8, and
            // "a/100*50" is a/(100*50) — old script packs rely on this.
            long val = ParseUnary(text, ref pos);
            return ApplyMathRightFold(val, text, ref pos);
        }
        finally { --_evalDepth; }
    }

    /// <summary>Source-X CExpression::GetValMath — apply one binary operator
    /// with the fully-folded remainder as the right operand.</summary>
    private long ApplyMathRightFold(long left, string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) return left;

        char c = text[pos];
        char c2 = pos + 1 < text.Length ? text[pos + 1] : '\0';
        switch (c)
        {
            case ')':
            case '}':
            case ']':
                // Expression end markers — the enclosing primary consumes them.
                return left;

            case '+':
                pos++;
                return left + ParseExpression(text, ref pos);

            case '-':
                // Do not consume the sign — subtraction is addition of the
                // negative right operand (Source-X keeps the '-').
                return left + ParseExpression(text, ref pos);

            case '*':
                pos++;
                return left * ParseExpression(text, ref pos);

            case '/':
            {
                pos++;
                long r = ParseExpression(text, ref pos);
                if (r == 0)
                {
                    DiagnosticLogger?.Invoke("[script] Evaluating math: divide by 0");
                    return left; // Source-X keeps the left value
                }
                return left / r;
            }

            case '%':
            {
                pos++;
                long r = ParseExpression(text, ref pos);
                if (r == 0)
                {
                    DiagnosticLogger?.Invoke("[script] Evaluating math: modulo 0");
                    return left;
                }
                return left % r;
            }

            case '|':
                if (c2 == '|')
                {
                    pos += 2;
                    long r = ParseExpression(text, ref pos);
                    return (r != 0 || left != 0) ? 1 : 0;
                }
                pos++;
                return left | ParseExpression(text, ref pos);

            case '&':
                if (c2 == '&')
                {
                    pos += 2;
                    long r = ParseExpression(text, ref pos);
                    return (r != 0 && left != 0) ? 1 : 0;
                }
                pos++;
                return left & ParseExpression(text, ref pos);

            case '^':
                pos++;
                return left ^ ParseExpression(text, ref pos);

            case '@':
            {
                pos++;
                long r = ParseExpression(text, ref pos);
                if (left == 0 && r <= 0)
                {
                    DiagnosticLogger?.Invoke("[script] Power of zero with zero or negative exponent is undefined");
                    return left;
                }
                return (long)Math.Pow(left, r);
            }

            case '>':
                if (c2 == '=') { pos += 2; return left >= ParseExpression(text, ref pos) ? 1 : 0; }
                if (c2 == '>') { pos += 2; return left >> (int)ParseExpression(text, ref pos); }
                pos++;
                return left > ParseExpression(text, ref pos) ? 1 : 0;

            case '<':
                if (c2 == '=') { pos += 2; return left <= ParseExpression(text, ref pos) ? 1 : 0; }
                if (c2 == '<') { pos += 2; return left << (int)ParseExpression(text, ref pos); }
                pos++;
                return left < ParseExpression(text, ref pos) ? 1 : 0;

            case '!':
                if (c2 == '=') { pos += 2; return left != ParseExpression(text, ref pos) ? 1 : 0; }
                return left; // bare '!' in operator position — not an operator

            case '=':
                // Sphere accepts any run of '=' as equality ("=", "==", "===").
                while (pos < text.Length && text[pos] == '=') pos++;
                return left == ParseExpression(text, ref pos) ? 1 : 0;

            default:
                return left;
        }
    }

    private long ParseUnary(string text, ref int pos)
    {
        if (++_evalDepth > MaxEvalDepth)
        {
            --_evalDepth;
            DiagnosticLogger?.Invoke("[script] expression nesting too deep; evaluation aborted");
            return 0;
        }
        try
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) return 0;

            char c = text[pos];
            if (c == '-') { pos++; return -ParseUnary(text, ref pos); }
            if (c == '!')
            {
                pos++;
                // Source-X quirk: a "!=x" prefix just skips the '=' and evaluates
                // the operand as-is.
                if (pos < text.Length && text[pos] == '=')
                {
                    pos++;
                    return ParseUnary(text, ref pos);
                }
                return ParseUnary(text, ref pos) == 0 ? 1 : 0;
            }
            if (c == '~') { pos++; return ~ParseUnary(text, ref pos); }
            if (c == '+') { pos++; return ParseUnary(text, ref pos); }

            return ParsePrimary(text, ref pos);
        }
        finally { --_evalDepth; }
    }

    private long ParsePrimary(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) return 0;

        // Parenthesized expression — Source-X GetSingle treats '[' like '('.
        if (text[pos] == '(' || text[pos] == '[')
        {
            char closer = text[pos] == '(' ? ')' : ']';
            pos++;
            long val = ParseExpression(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == closer) pos++;
            return val;
        }

        // Brace range / weighted value — Source-X GetRangeNumber:
        //   {lo hi}             -> random integer in [lo,hi]
        //   {v1 w1 v2 w2 ...}   -> weighted random pick among v1,v2,...
        //   {v}                 -> v
        if (text[pos] == '{')
        {
            pos++; // skip '{'
            int braceStart = pos;
            int depth = 1;
            while (pos < text.Length && depth > 0)
            {
                char bc = text[pos];
                if (bc == '{') depth++;
                else if (bc == '}') { depth--; if (depth == 0) break; }
                pos++;
            }
            string inner = text.Substring(braceStart, pos - braceStart);
            if (pos < text.Length && text[pos] == '}') pos++; // skip '}'
            return EvaluateBraceRange(inner);
        }

        // Angle bracket variable <...> — dispatch the whole reference through
        // ResolveVariable (Source-X resolves <> references fully before the
        // numeric read). Just expanding nested brackets left the outer
        // keyword (<EVAL ...>, <RAND(...)>, <TAG.X>) unresolved → 0.
        if (text[pos] == '<')
        {
            string inner = ReadAngleBracket(text, ref pos);
            string expanded = ResolveVariable(inner);
            if (long.TryParse(expanded, out long v)) return v;

            // Try hex (0x prefix or Sphere leading-zero form)
            if (expanded.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(expanded.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out v))
                return v;
            if (expanded.Length > 1 && expanded[0] == '0' &&
                long.TryParse(expanded, System.Globalization.NumberStyles.HexNumber, null, out v))
                return v;

            _numericOk = false; // <...> resolved to a non-numeric string
            return 0;
        }

        // Bare identifier — handles Sphere function calls written WITHOUT
        // angle-brackets inside an expression, e.g.
        //     If (!strcmp(<Account.Lang>,CSY))
        //     If (isnum(<argv[0]>))
        // Without this, the identifier resolved to 0 and the lang
        // detection chain in d_admin_main always took the first branch
        // (CSY), leaving CTag.AccountLang empty so every dialog DEF
        // lookup fell back to "0".
        if (pos < text.Length && (char.IsLetter(text[pos]) || text[pos] == '_'))
        {
            int idStart = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
                pos++;
            string ident = text[idStart..pos];

            string callExpr;
            if (pos < text.Length && text[pos] == '(')
            {
                int parenStart = pos;
                int depth = 0;
                int angleDepth = 0;
                while (pos < text.Length)
                {
                    char ch = text[pos];
                    if (ch == '<') angleDepth++;
                    else if (ch == '>' && angleDepth > 0) angleDepth--;
                    else if (angleDepth == 0)
                    {
                        if (ch == '(') depth++;
                        else if (ch == ')')
                        {
                            depth--;
                            if (depth == 0) { pos++; break; }
                        }
                    }
                    pos++;
                }
                callExpr = ident + text[parenStart..pos];
            }
            else
            {
                callExpr = ident;
            }

            string val = ResolveVariable(callExpr) ?? "";
            if (long.TryParse(val, out long fv)) return fv;
            if (val.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(val.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out fv))
                return fv;
            if (val.Length > 1 && val[0] == '0' &&
                long.TryParse(val, System.Globalization.NumberStyles.HexNumber, null, out fv))
                return fv;
            _numericOk = false; // bareword resolved to a non-numeric string / was unresolved
            return 0;
        }

        // Number literal
        return ReadNumber(text, ref pos);
    }

    /// <summary>Evaluate a Sphere brace expression. Two tokens = numeric range
    /// (random lo..hi); more = (value weight) weighted pairs; one = that value.
    /// Each token is itself evaluated so &lt;...&gt;/hex/identifiers work.</summary>
    private long EvaluateBraceRange(string inner)
    {
        inner = inner.Trim();
        if (inner.Length == 0) return 0;

        var tokens = SplitBraceTokens(inner);
        if (tokens.Count == 0) return 0;

        var vals = new long[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            int p = 0;
            vals[i] = ParseExpression(tokens[i], ref p);
        }

        if (vals.Length == 1) return vals[0];

        if (vals.Length == 2)
        {
            long lo = Math.Min(vals[0], vals[1]);
            long hi = Math.Max(vals[0], vals[1]);
            return NextInclusiveRandom(lo, hi);
        }

        // Weighted (value, weight) pairs — Source-X GetRangeNumber requires an
        // even token count; an odd count (>2) is a script error and yields 0.
        if ((vals.Length & 1) != 0)
        {
            DiagnosticLogger?.Invoke($"[script] Bad {{...}} range: odd number of values/weights ({vals.Length}) in '{{{inner}}}'");
            return 0;
        }

        long totalWeight = 0;
        for (int i = 1; i < vals.Length; i += 2)
        {
            if (vals[i] <= 0)
                DiagnosticLogger?.Invoke($"[script] Bad {{...}} range: non-positive weight {vals[i]} in '{{{inner}}}'");
            totalWeight += Math.Max(0, vals[i]);
        }
        if (totalWeight <= 0) return vals[0];

        long roll = Random.Shared.NextInt64(totalWeight);
        for (int i = 0; i + 1 < vals.Length; i += 2)
        {
            roll -= Math.Max(0, vals[i + 1]);
            if (roll < 0) return vals[i];
        }
        return vals[0];
    }

    /// <summary>Uniform random in the INCLUSIVE range [lo, hi] without the
    /// <c>hi + 1</c> that overflows (and throws) when hi == long.MaxValue — the
    /// bug that let <c>{1 0x7fffffffffffffff}</c> crash the tick. The short-R /
    /// RAND intrinsics already guard MaxValue; this covers the {lo hi} path.</summary>
    private static long NextInclusiveRandom(long lo, long hi)
    {
        if (lo >= hi) return lo; // lo == hi, and defensively any inverted range
        if (hi == long.MaxValue)
        {
            // hi + 1 would overflow. NextInt64(min,max) is exclusive of max, so
            // [lo, MaxValue) drops only the single endpoint MaxValue — acceptable
            // for a pathological range, and it never throws. The whole-range case
            // (lo == MinValue too) falls back to a non-negative draw.
            return lo == long.MinValue
                ? Random.Shared.NextInt64()
                : Random.Shared.NextInt64(lo, long.MaxValue);
        }
        return Random.Shared.NextInt64(lo, hi + 1);
    }

    /// <summary>Split brace content on whitespace, respecting nested &lt;...&gt;
    /// and {...} so a token may itself be an expression.</summary>
    private static List<string> SplitBraceTokens(string s)
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

    private long ReadNumber(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) return 0;

        // Source-X GetSingle legacy: a leading '.' before a digit is skipped
        // (".5" parses as decimal 5).
        if (text[pos] == '.' && pos + 1 < text.Length && char.IsDigit(text[pos + 1]))
            pos++;

        int start = pos;

        if (pos + 1 < text.Length && text[pos] == '0' && (text[pos + 1] == 'x' || text[pos + 1] == 'X'))
        {
            pos += 2;
            while (pos < text.Length && IsHexDigit(text[pos])) pos++;
        }
        else if (text[pos] == '0' && (pos + 1 >= text.Length || text[pos + 1] != '.'))
        {
            // Source-X: leading '0' NOT followed by '.' → hex; hex scan stops
            // at '.'; ≤8 significant nibbles sign-extend as int32 ("0ffffffff"
            // = -1), 9–16 as int64, >16 warns and yields -1.
            pos++;
            int hexStart = pos;
            while (pos < text.Length && IsHexDigit(text[pos])) pos++;
            if (pos == hexStart) return 0; // bare "0"
            if (Parsing.ScriptKey.TryParseLeadingZeroHex(text.AsSpan(hexStart, pos - hexStart), out long hv))
            {
                if (hv == -1 && pos - hexStart > 16)
                    DiagnosticLogger?.Invoke($"[script] Hex value overflows 64 bits: {text}");
                return hv;
            }
            return 0;
        }
        else
        {
            // Decimal path — '.' chars inside the token are grouping
            // separators and are skipped ("100.000" == 100000), matching
            // Source-X GetSingle's decimal scan. Overflow warns and yields -1.
            const long Lim10 = long.MaxValue / 10;
            const int LimDigit = (int)(long.MaxValue % 10);
            long val = 0;
            bool any = false, overflow = false;
            while (pos < text.Length)
            {
                char dc = text[pos];
                if (dc == '.') { if (!any) break; pos++; continue; }
                if (!char.IsDigit(dc)) break;
                int d = dc - '0';
                if (!overflow && (val > Lim10 || (val == Lim10 && d > LimDigit)))
                    overflow = true;
                else if (!overflow)
                    val = val * 10 + d;
                any = true;
                pos++;
            }
            if (overflow)
            {
                DiagnosticLogger?.Invoke($"[script] Decimal value overflows 64 bits: {text}");
                return -1;
            }
            return any ? val : 0;
        }

        if (pos == start) return 0;

        ReadOnlySpan<char> numText = text.AsSpan(start, pos - start);
        ReadOnlySpan<char> hexText = numText.Length > 2 &&
            numText[0] == '0' &&
            (numText[1] == 'x' || numText[1] == 'X')
            ? numText[2..]
            : numText;
        long.TryParse(hexText, System.Globalization.NumberStyles.HexNumber, null, out long hexVal);
        return hexVal;
    }

    private string ReadAngleBracket(string text, ref int pos)
    {
        if (pos >= text.Length || text[pos] != '<') return "";
        pos++; // skip '<'

        int depth = 1;
        int parenDepth = 0;
        int start = pos;
        while (pos < text.Length && depth > 0)
        {
            char c = text[pos];
            if (c == '(') parenDepth++;
            else if (c == ')' && parenDepth > 0) parenDepth--;
            else if (c == '<')
            {
                // Disambiguate '<' — same rule as the top-level
                // ResolveAngleBrackets walker: a bracket open needs an
                // identifier start (letter / '_') right after. "a < b"
                // keeps '<' literal, "a<foo>b" opens a nested bracket.
                char next = pos + 1 < text.Length ? text[pos + 1] : '\0';
                if (next == '_' || char.IsLetter(next))
                    depth++;
                // else: literal LT — pass through as content
            }
            else if (c == '>')
            {
                // Paren-depth GT-operator disambiguation only applies at
                // the outermost bracket (depth == 1). Inside a nested
                // <...>, a '>' always closes the nested bracket even if
                // that nested bracket is sitting inside parentheses —
                // otherwise <eval 62+(<local._for>*25)> swallows the
                // inner '>' and the outer read runs past the end.
                if (depth == 1 && parenDepth > 0)
                {
                    pos++;
                    continue;
                }
                depth--;
            }
            if (depth > 0) pos++;
        }

        string content = text[start..pos];
        if (pos < text.Length && text[pos] == '>') pos++;
        return content;
    }

    /// <summary>
    /// Resolve all &lt;...&gt; substitutions in a string.
    /// Maps to CExpression::ParseScriptText in Source-X.
    /// </summary>
    public string ResolveAngleBrackets(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('<'))
            return text;
        if (_resolveDepth >= MaxResolveDepth)
            return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int pos = 0;
        _resolveDepth++;
        try
        {
            while (pos < text.Length)
            {
                if (text[pos] == '<')
                {
                    char next = pos + 1 < text.Length ? text[pos + 1] : '\0';
                    bool isBracketOpen = next == '_' || char.IsLetter(next);
                    if (!isBracketOpen)
                    {
                        sb.Append('<');
                        pos++;
                        continue;
                    }

                    string inner = ReadAngleBracket(text, ref pos);
                    string resolved = ResolveVariable(inner);
                    sb.Append(resolved);
                }
                else
                {
                    sb.Append(text[pos]);
                    pos++;
                }
            }
        }
        finally { _resolveDepth--; }

        return sb.ToString();
    }

    private string ResolveVariable(string varExpr)
    {
        if (string.IsNullOrEmpty(varExpr)) return "";

        // Dialog response accessors — only active inside a [Dialog X Button]
        // handler. Checked first so a plain ARGN doesn't collide with a generic
        // variable of the same name elsewhere.
        if (DialogArgResolver != null)
        {
            if (varExpr.Equals("ARGN", StringComparison.OrdinalIgnoreCase) ||
                varExpr.Equals("ARGV", StringComparison.OrdinalIgnoreCase) ||
                varExpr.Equals("ARGCHK", StringComparison.OrdinalIgnoreCase) ||
                varExpr.Equals("ARGCHKID", StringComparison.OrdinalIgnoreCase) ||
                varExpr.StartsWith("ARGV[", StringComparison.OrdinalIgnoreCase) ||
                varExpr.StartsWith("ARGV.", StringComparison.OrdinalIgnoreCase) ||
                varExpr.StartsWith("ARGTXT[", StringComparison.OrdinalIgnoreCase) ||
                varExpr.StartsWith("ARGCHK[", StringComparison.OrdinalIgnoreCase))
            {
                string? v = DialogArgResolver(varExpr);
                if (v != null) return v;
            }
        }

        // EVAL keyword — numeric evaluation
        if (varExpr.StartsWith("EVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("EVAL\t", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return Evaluate(expanded.AsSpan()).ToString();
        }

        // HVAL — hex evaluation
        if (varExpr.StartsWith("HVAL ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return "0" + Evaluate(expanded.AsSpan()).ToString("X");
        }

        // QVAL paren form — <QVAL(v1,v2,lt,eq,gt)> numeric 3-way compare.
        if (varExpr.StartsWith("QVAL(", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateQval(ExtractFuncArg(varExpr, 4));
        }
        // QVAL — conditional: <QVAL condition?true_val:false_val>
        if (varExpr.StartsWith("QVAL ", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateQval(varExpr[5..]);
        }

        // STRARG — extract first whitespace-delimited token from ARGS
        if (varExpr.StartsWith("STRARG ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[7..].Trim());
            int sp = inner.IndexOf(' ');
            return sp > 0 ? inner[..sp] : inner;
        }

        // STRSUB — substring: <STRSUB start,length,string>
        if (varExpr.StartsWith("STRSUB ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRSUB(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 6, 3);
            if (parts.Count < 3) return "";
            if (!int.TryParse(parts[0], out int start)) return "";
            if (!int.TryParse(parts[1], out int length)) return "";
            string resolved = parts[2];
            if (start < 0) start = 0;
            if (start >= resolved.Length) return "";
            length = Math.Min(length, resolved.Length - start);
            if (length <= 0) return "";
            return resolved.Substring(start, length);
        }

        // STRLEN — string length: <STRLEN string>
        if (varExpr.StartsWith("STRLEN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[7..].Trim());
            return inner.Length.ToString();
        }

        // STREAT — consume first token from ARGS string and return the
        // remainder. Sphere treats both space and comma as token
        // separators here (moongate-style "X,Y,Z,label" strings feed
        // STREAT chains to peel off numeric prefixes and reach the
        // trailing name).
        if (varExpr.StartsWith("STREAT ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[7..].Trim());
            int sep = inner.IndexOfAny([' ', ',']);
            return sep >= 0 ? inner[(sep + 1)..].TrimStart(' ', ',') : "";
        }

        // STRMATCH — wildcard pattern match. Accept both:
        //   <STRMATCH pattern,string>
        //   strmatch("a","b")
        if (varExpr.StartsWith("STRMATCH ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRMATCH(", StringComparison.OrdinalIgnoreCase))
        {
            string body = varExpr.StartsWith("STRMATCH(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 8)
                : varExpr[9..];
            return EvaluateStrMatch(body);
        }

        // SEX <male>/<female> — pick value based on character sex.
        // Used heavily in dialogs: <Sex <Def.male>/<Def.female>> shows the
        // male label when the target is male, female label otherwise.
        if (varExpr.StartsWith("SEX ", StringComparison.OrdinalIgnoreCase))
        {
            string body = varExpr[4..].Trim();
            int slash = body.IndexOf('/');
            if (slash < 0) return ResolveAngleBrackets(body);
            string male = ResolveAngleBrackets(body[..slash]);
            string female = ResolveAngleBrackets(body[(slash + 1)..]);
            string? sexVal = VariableResolver?.Invoke("SEX");
            return sexVal == "1" ? female : male;
        }

        // FORMATMINUTES N — format a minute count as "HH:MM" for admin panels.
        if (varExpr.StartsWith("FORMATMINUTES ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[14..].Trim());
            if (long.TryParse(inner, out long mins))
            {
                long hours = mins / 60;
                long rem = mins % 60;
                return $"{hours}:{rem:D2}";
            }
            return inner;
        }

        // STRCMP — case-sensitive string compare. Accepts both Sphere
        // forms: angle-bracket variable <STRCMP a,b> AND bare expression
        // function call strcmp(a,b) (used e.g. by d_admin_main's language
        // detection chain `If (!strcmp(<Account.Lang>,CSY))`).
        if (varExpr.StartsWith("STRCMP(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRCMP ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 6, 2);
            if (parts.Count == 2)
                return string.Compare(parts[0], parts[1], StringComparison.Ordinal).ToString();
            return "0";
        }

        // STRLOWER / STRTOLOWER / STRUPPER / STRTOUPPER
        if (varExpr.StartsWith("STRTOLOWER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[11..].Trim()).ToLowerInvariant();
        if (varExpr.StartsWith("STRLOWER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[9..].Trim()).ToLowerInvariant();
        if (varExpr.StartsWith("STRTOUPPER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[11..].Trim()).ToUpperInvariant();
        if (varExpr.StartsWith("STRUPPER ", StringComparison.OrdinalIgnoreCase))
            return ResolveAngleBrackets(varExpr[9..].Trim()).ToUpperInvariant();

        // ISNUMBER / ISNUM — returns 1 if arg is numeric
        if (varExpr.StartsWith("ISNUMBER ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ISNUM ", StringComparison.OrdinalIgnoreCase))
        {
            int prefixLen = varExpr.StartsWith("ISNUM ", StringComparison.OrdinalIgnoreCase) ? 6 : 9;
            string inner = ResolveAngleBrackets(varExpr[prefixLen..].Trim());
            if (inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(inner.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out _) ? "1" : "0";
            return long.TryParse(inner, out _) ? "1" : "0";
        }

        // ASC — convert string to hex ASCII codes (space-separated)
        if (varExpr.StartsWith("ASC ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ASC(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("ASC(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 3) : ResolveAngleBrackets(varExpr[4..].Trim());
            return string.Join(" ", inner.Select(c => ((int)c).ToString("X2")));
        }

        // CHR — inverse of ASC: byte VALUE -> the character (Source-X
        // CScriptObj_functions.tbl CHR). The packet-rebuild scripts turn
        // received bytes back into text with <SERV.CHR <byte>>.
        if (varExpr.StartsWith("CHR ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("CHR(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("CHR(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 3) : ResolveAngleBrackets(varExpr[4..].Trim());
            long code = Evaluate(inner.AsSpan());
            // Reject the UTF-16 surrogate range (0xD800..0xDFFF) and out-of-plane
            // values: char.ConvertFromUtf32 throws on those, which would escape
            // to the tick. Rune.IsValid is exactly that predicate.
            return code is > 0 and <= 0x10FFFF && System.Text.Rune.IsValid((int)code)
                ? char.ConvertFromUtf32((int)code) : "";
        }

        // ASCPAD — convert string to hex ASCII codes, padded to fixed length
        if (varExpr.StartsWith("ASCPAD ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ASCPAD(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 6, 2);
            if (parts.Count == 2 && int.TryParse(parts[0], out int padCount))
            {
                // Clamp the pad count: an attacker-influenced value (e.g.
                // int.MaxValue) would build a multi-GB string → OutOfMemory.
                const int MaxAscPad = 4096;
                if (padCount < 0) padCount = 0;
                else if (padCount > MaxAscPad)
                {
                    DiagnosticLogger?.Invoke($"[script] ASCPAD count {padCount} exceeds max {MaxAscPad}; clamped");
                    padCount = MaxAscPad;
                }
                string str = parts[1].Trim('"');
                var sb = new System.Text.StringBuilder();
                for (int idx = 0; idx < padCount; idx++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(idx < str.Length ? ((int)str[idx]).ToString("X2") : "00");
                }
                return sb.ToString();
            }
            return "";
        }

        // BETWEEN2 — inverse proportional mapping: (iMax-iCurrent)*iAbsMax/(iMax-iMin)
        if (varExpr.StartsWith("BETWEEN2 ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("BETWEEN2(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 8);
            if (parts.Count >= 4)
            {
                long iMin = Evaluate(parts[0].AsSpan());
                long iMax = Evaluate(parts[1].AsSpan());
                long iCur = Evaluate(parts[2].AsSpan());
                long iAbsMax = Evaluate(parts[3].AsSpan());
                long range = iMax - iMin;
                return range != 0 ? ((iMax - iCur) * iAbsMax / range).ToString() : "0";
            }
            return "0";
        }

        // BETWEEN — proportional mapping: (iCurrent-iMin)*iAbsMax/(iMax-iMin)
        if (varExpr.StartsWith("BETWEEN ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("BETWEEN(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 7);
            if (parts.Count >= 4)
            {
                long iMin = Evaluate(parts[0].AsSpan());
                long iMax = Evaluate(parts[1].AsSpan());
                long iCur = Evaluate(parts[2].AsSpan());
                long iAbsMax = Evaluate(parts[3].AsSpan());
                long range = iMax - iMin;
                return range != 0 ? ((iCur - iMin) * iAbsMax / range).ToString() : "0";
            }
            return "0";
        }

        // CHR — ASCII code to character
        if (varExpr.StartsWith("CHR ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("CHR(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("CHR(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 3) : ResolveAngleBrackets(varExpr[4..].Trim());
            long code = Evaluate(inner.AsSpan());
            return code > 0 && code < 0x10000 ? ((char)code).ToString() : "";
        }

        // CLRBIT — clear a specific bit: value & ~(1 << bit)
        if (varExpr.StartsWith("CLRBIT ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("CLRBIT(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 6, 2);
            if (parts.Count == 2)
            {
                long val = Evaluate(parts[0].AsSpan());
                int bit = (int)Evaluate(parts[1].AsSpan());
                if (bit is >= 0 and < 64)
                    return (val & ~(1L << bit)).ToString();
            }
            return "0";
        }

        // SETBIT — set a specific bit: value | (1 << bit)
        if (varExpr.StartsWith("SETBIT ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("SETBIT(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 6, 2);
            if (parts.Count == 2)
            {
                long val = Evaluate(parts[0].AsSpan());
                int bit = (int)Evaluate(parts[1].AsSpan());
                if (bit is >= 0 and < 64)
                    return (val | (1L << bit)).ToString();
            }
            return "0";
        }

        // ISBIT — test if a specific bit is set
        if (varExpr.StartsWith("ISBIT ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ISBIT(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 5, 2);
            if (parts.Count == 2)
            {
                long val = Evaluate(parts[0].AsSpan());
                int bit = (int)Evaluate(parts[1].AsSpan());
                if (bit is >= 0 and < 64)
                    return (val & (1L << bit)) != 0 ? "1" : "0";
            }
            return "0";
        }

        // D — force decimal evaluation (e.g. <DHITS>, <dsrc.hits>, <ddef.X>)
        if (varExpr.Length > 1 && (varExpr[0] == 'D' || varExpr[0] == 'd') &&
            !varExpr.Equals("DARGV", StringComparison.OrdinalIgnoreCase) &&
            !varExpr.StartsWith("DEFMSG", StringComparison.OrdinalIgnoreCase) &&
            !varExpr.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) &&
            !varExpr.StartsWith("DEF0.", StringComparison.OrdinalIgnoreCase) &&
            char.IsLetterOrDigit(varExpr[1]))
        {
            // The leading D forces a DECIMAL reading of the REST
            // (<DHITS>, <DLOCAL.HEX>, <Dsrc.hits>): strip the D and resolve
            // the remainder first, coercing a 0-prefixed/hex result to decimal.
            string inner = varExpr[1..];
            string resolved = ResolveAngleBrackets(inner);
            string? varVal = VariableResolver?.Invoke(resolved);
            if (varVal != null)
            {
                if (varVal.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                    varVal.StartsWith("0", StringComparison.Ordinal))
                    return Evaluate(varVal.AsSpan()).ToString();
                return varVal;
            }
            // The stripped remainder is not a known variable — so the leading
            // 'd' was NOT a prefix but part of a real property name (DISPID,
            // DEX, DIR, ...) or a function call (DamTypesOfString, ...).
            // Resolve the full token as a variable, then as a function, before
            // giving up. (Regression: <dispid> mangled to <ispid>; the
            // D-prefixed [FUNCTION] DamTypesOfString lost its 'D' too.)
            string fullName = ResolveAngleBrackets(varExpr);
            string? fullVal = VariableResolver?.Invoke(fullName) ?? FunctionResolver?.Invoke(fullName);
            if (fullVal != null)
                return fullVal;
            return Evaluate(resolved.AsSpan()).ToString();
        }

        // EXPLODE — split string by separator chars into comma-delimited list
        if (varExpr.StartsWith("EXPLODE ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("EXPLODE(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 7, 2);
            if (parts.Count == 2)
            {
                string separators = parts[0];
                string str = parts[1].Trim('"');
                var tokens = str.Split(separators.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                return string.Join(",", tokens);
            }
            return parts.Count == 1 ? parts[0] : "";
        }

        // FEVAL — floating-point evaluation.
        if (varExpr.StartsWith("FEVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FEVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FEVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 5) : varExpr[6..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return FormatFloat(EvaluateFloat(expanded.AsSpan()));
        }

        // FHVAL — evaluate as float, then format the truncated integer as hex.
        if (varExpr.StartsWith("FHVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FHVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FHVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 5) : varExpr[6..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return "0" + ((long)EvaluateFloat(expanded.AsSpan())).ToString("X");
        }

        // FLOATVAL — floating point math.
        if (varExpr.StartsWith("FLOATVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FLOATVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FLOATVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 8) : varExpr[9..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return FormatFloat(EvaluateFloat(expanded.AsSpan()));
        }

        // FVAL — format as x.x (divides by 10): <FVAL 125> = "12.5"
        if (varExpr.StartsWith("FVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 4) : varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            long val = Evaluate(expanded.AsSpan());
            return $"{val / 10}.{Math.Abs(val % 10)}";
        }

        // MULDIV — safe (num*mul)/div with 64-bit math
        if (varExpr.StartsWith("MULDIV ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("MULDIV(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 6, 3);
            if (parts.Count == 3)
            {
                long num = Evaluate(parts[0].AsSpan());
                long mul = Evaluate(parts[1].AsSpan());
                long div = Evaluate(parts[2].AsSpan());
                return div != 0 ? (num * mul / div).ToString() : "0";
            }
            return "0";
        }

        // MD5HASH — compute MD5 hash of string
        if (varExpr.StartsWith("MD5HASH ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("MD5HASH(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("MD5HASH(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 7) : ResolveAngleBrackets(varExpr[8..].Trim());
            byte[] hash = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes(inner));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        // STRPOS — find character position in string
        if (varExpr.StartsWith("STRPOS ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRPOS(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 6, 2);
            if (parts.Count == 2)
            {
                string charCode = parts[0];
                string str = parts[1];
                char searchChar = int.TryParse(charCode, out int code) ? (char)code : (charCode.Length > 0 ? charCode[0] : '\0');
                return str.IndexOf(searchChar).ToString();
            }
            return "-1";
        }

        // STRREGEXNEW — regex match with pattern length: <STRREGEXNEW len, string, pattern>
        if (varExpr.StartsWith("STRREGEXNEW ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRREGEXNEW(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 11, 3);
            if (parts.Count == 3)
            {
                try
                {
                    return TrySafeRegexIsMatch(parts[1], parts[2], out bool isMatch)
                        ? (isMatch ? "1" : "0")
                        : "-1";
                }
                catch { return "-1"; }
            }
            return "0";
        }

        // STRREVERSE — reverse string characters
        if (varExpr.StartsWith("STRREVERSE ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRREVERSE(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRREVERSE(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 10) : ResolveAngleBrackets(varExpr[11..].Trim());
            char[] chars = inner.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        // STRTRIM — trim leading/trailing whitespace
        if (varExpr.StartsWith("STRTRIM ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRTRIM(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRTRIM(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 7) : ResolveAngleBrackets(varExpr[8..]);
            return inner.Trim();
        }

        // UVAL — unsigned value evaluation
        if (varExpr.StartsWith("UVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("UVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("UVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 4) : varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            long val = Evaluate(expanded.AsSpan());
            return ((ulong)val).ToString();
        }

        // ABS — absolute value
        if (varExpr.StartsWith("ABS(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ABS ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            return Math.Abs(Evaluate(inner.AsSpan())).ToString();
        }

        // MAX / MIN — two-argument intrinsics (Source-X INTRINSIC_MAX/MIN).
        if (varExpr.StartsWith("MAX(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("MAX ", StringComparison.OrdinalIgnoreCase))
        {
            var a = SplitArgsTopLevel(ExtractFuncArg(varExpr, 3));
            if (a.Count >= 2) return Math.Max(Evaluate(a[0].AsSpan()), Evaluate(a[1].AsSpan())).ToString();
            return a.Count == 1 ? Evaluate(a[0].AsSpan()).ToString() : "0";
        }
        if (varExpr.StartsWith("MIN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("MIN ", StringComparison.OrdinalIgnoreCase))
        {
            var a = SplitArgsTopLevel(ExtractFuncArg(varExpr, 3));
            if (a.Count >= 2) return Math.Min(Evaluate(a[0].AsSpan()), Evaluate(a[1].AsSpan())).ToString();
            return a.Count == 1 ? Evaluate(a[0].AsSpan()).ToString() : "0";
        }

        // SQRT — square root
        if (varExpr.StartsWith("SQRT(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("SQRT ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 4);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Sqrt(val)).ToString();
        }

        // ARCSIN / ARCCOS / ARCTAN — Source-X INTRINSIC_ARCSIN/ARCCOS/ARCTAN:
        // (llong)asin((double)GetVal(...)) — radians in, truncated integer out.
        // Checked before SIN/COS/TAN would be irrelevant (prefixes differ) but
        // kept adjacent for readability.
        if (varExpr.StartsWith("ARCSIN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ARCSIN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 6);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Asin(val)).ToString();
        }
        if (varExpr.StartsWith("ARCCOS(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ARCCOS ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 6);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Acos(val)).ToString();
        }
        if (varExpr.StartsWith("ARCTAN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ARCTAN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 6);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Atan(val)).ToString();
        }

        // SIN — Source-X INTRINSIC_SIN: (llong)sin((double)GetVal) — radians,
        // truncated to integer (no fixed-point scaling).
        if (varExpr.StartsWith("SIN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("SIN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Sin(val)).ToString();
        }

        // COS — Source-X INTRINSIC_COS: radians, truncated.
        if (varExpr.StartsWith("COS(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("COS ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Cos(val)).ToString();
        }

        // TAN — Source-X INTRINSIC_TAN: radians, truncated.
        if (varExpr.StartsWith("TAN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("TAN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 3);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Tan(val)).ToString();
        }

        // STRASCII — Source-X INTRINSIC_STRASCII: ASCII/char code of the first
        // character of the argument string (decimal), 0 for empty.
        if (varExpr.StartsWith("STRASCII(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRASCII ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRASCII(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 8) : ResolveAngleBrackets(varExpr[9..].Trim());
            return inner.Length > 0 ? ((int)inner[0]).ToString() : "0";
        }

        // LOGARITHM — log base-10, or log with custom base
        if (varExpr.StartsWith("LOGARITHM(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("LOGARITHM ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 9, 2);
            if (parts.Count == 0) return "0";
            long val = Evaluate(parts[0].AsSpan());
            if (val <= 0) return "0";
            if (parts.Count == 2)
            {
                string baseStr = parts[1].ToLowerInvariant();
                double logBase = baseStr switch
                {
                    "e" => Math.E,
                    "pi" => Math.PI,
                    _ => double.TryParse(baseStr, out double b) ? b : 10.0
                };
                return ((long)Math.Log(val, logBase)).ToString();
            }
            return ((long)Math.Log10(val)).ToString();
        }

        // NAPIERPOW — e^value
        if (varExpr.StartsWith("NAPIERPOW(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("NAPIERPOW ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 9);
            long val = Evaluate(inner.AsSpan());
            return ((long)Math.Exp(val)).ToString();
        }

        // R — the CLASSIC Sphere short random: <R1059,1101> inclusive range,
        // <R20> = [0,20). The live packs use this form everywhere (the flash
        // robe's DORAND color table, <R1,<ARRAYCOUNT ...>> picks); only the
        // long RAND form was implemented. Guarded to R-followed-by-a-digit so
        // RAND/RANDBELL and real defnames never match.
        if (varExpr.Length > 1 && (varExpr[0] == 'R' || varExpr[0] == 'r') &&
            char.IsDigit(varExpr[1]))
        {
            var shortParts = varExpr[1..].Split(',', 2);
            long shortMin = Evaluate(ResolveAngleBrackets(shortParts[0].Trim()).AsSpan());
            if (shortParts.Length == 2)
            {
                long shortMax = Evaluate(ResolveAngleBrackets(shortParts[1].Trim()).AsSpan());
                if (shortMax < shortMin) (shortMin, shortMax) = (shortMax, shortMin);
                return (shortMax == long.MaxValue
                    ? Random.Shared.NextInt64(shortMin, shortMax)
                    : Random.Shared.NextInt64(shortMin, shortMax + 1)).ToString();
            }
            return shortMin > 0 ? Random.Shared.NextInt64(shortMin).ToString() : "0";
        }

        // RAND — random: RAND(max) or RAND(min,max)
        if (varExpr.StartsWith("RAND(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("RAND ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 4);
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                // Source-X g_Rand.GetLLVal2(a,b) — 64-bit inclusive range.
                long rMin = Evaluate(ResolveAngleBrackets(parts[0].Trim()).AsSpan());
                long rMax2 = Evaluate(ResolveAngleBrackets(parts[1].Trim()).AsSpan());
                if (rMax2 < rMin) (rMin, rMax2) = (rMax2, rMin);
                return (rMax2 == long.MaxValue
                    ? Random.Shared.NextInt64(rMin, rMax2)
                    : Random.Shared.NextInt64(rMin, rMax2 + 1)).ToString();
            }
            // Source-X g_Rand.GetLLVal(x) — [0, x), 64-bit.
            long rMax1 = Evaluate(ResolveAngleBrackets(parts[0].Trim()).AsSpan());
            if (rMax1 > 0)
                return Random.Shared.NextInt64(rMax1).ToString();
            return "0";
        }

        // RANDBELL — bell curve random: RANDBELL(center, variance)
        if (varExpr.StartsWith("RANDBELL(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("RANDBELL ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 8);
            var parts = inner.Split(',', 2);
            if (parts.Length == 2)
            {
                long valDiff = Evaluate(ResolveAngleBrackets(parts[0].Trim()).AsSpan());
                long variance = Evaluate(ResolveAngleBrackets(parts[1].Trim()).AsSpan());
                return CalcGetBellCurve((int)valDiff, (int)variance).ToString();
            }
            return "0";
        }

        // STRCMPI — case-insensitive string compare
        if (varExpr.StartsWith("STRCMPI(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRCMPI ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 7, 2);
            if (parts.Count == 2)
                return string.Compare(parts[0], parts[1], StringComparison.OrdinalIgnoreCase).ToString();
            return "0";
        }

        // STRREPLACE — replace all literal matches: STRREPLACE(text, search, replacement)
        if (varExpr.StartsWith("STRREPLACE(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRREPLACE ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 10, 3);
            if (parts.Count >= 2)
            {
                string text = parts[0];
                string search = parts[1];
                string replacement = parts.Count >= 3 ? parts[2] : "";
                if (search.Length == 0)
                    return text;
                return text.Replace(search, replacement, StringComparison.Ordinal);
            }
            return "";
        }

        // STRJOIN — join arguments with a separator: STRJOIN(separator, a, b, ...)
        if (varExpr.StartsWith("STRJOIN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRJOIN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 7);
            var parts = inner.Split(',', StringSplitOptions.None);
            if (parts.Length < 2)
                return "";

            string separator = ResolveAngleBrackets(parts[0]);
            var values = new string[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
                values[i - 1] = ResolveAngleBrackets(parts[i].Trim());
            return string.Join(separator, values);
        }

        // STRINDEXOF — find substring: STRINDEXOF(text, search, start)
        if (varExpr.StartsWith("STRINDEXOF(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRINDEXOF ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 10, 3);
            if (parts.Count >= 2)
            {
                string text = parts[0];
                string search = parts[1];
                int start = parts.Count > 2 && int.TryParse(parts[2], out int s) ? s : 0;
                start = Math.Clamp(start, 0, text.Length);
                // Source-X Str_IndexOf is case-SENSITIVE.
                return text.IndexOf(search, start, StringComparison.Ordinal).ToString();
            }
            return "-1";
        }

        // STRREGEX — regex match: STRREGEX(pattern, text)
        if (varExpr.StartsWith("STRREGEX(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRREGEX ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, 8, 2);
            if (parts.Count == 2)
            {
                try
                {
                    return TrySafeRegexIsMatch(parts[1], parts[0], out bool isMatch) && isMatch ? "1" : "0";
                }
                catch { return "0"; }
            }
            return "0";
        }

        // ISOBSCENE — profanity check against the [OBSCENE] word list
        // (Source-X INTRINSIC_ISOBSCENE → g_Cfg.IsObscene). The host wires
        // ObsceneChecker to ResourceHolder.IsObscene at boot; without a
        // loaded list everything is clean.
        if (varExpr.StartsWith("ISOBSCENE(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ISOBSCENE ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("ISOBSCENE(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 9)
                : ResolveAngleBrackets(varExpr[10..].Trim());
            return ObsceneChecker?.Invoke(inner) == true ? "1" : "0";
        }

        // DATE — current date/time as formatted string
        if (varExpr.Equals("DATE", StringComparison.OrdinalIgnoreCase))
        {
            return DateTime.Now.ToString("MM/dd/yy");
        }

        // DATEOBJ — numeric access: DATEOBJ.YEAR, DATEOBJ.MONTH, etc.
        if (varExpr.StartsWith("DATEOBJ.", StringComparison.OrdinalIgnoreCase))
        {
            string field = varExpr[8..].Trim().ToUpperInvariant();
            var now = DateTime.Now;
            return field switch
            {
                "YEAR" => now.Year.ToString(),
                "MONTH" => now.Month.ToString(),
                "DAY" => now.Day.ToString(),
                "HOUR" => now.Hour.ToString(),
                "MINUTE" => now.Minute.ToString(),
                "SECOND" => now.Second.ToString(),
                "DAYOFWEEK" => ((int)now.DayOfWeek).ToString(),
                "DAYOFYEAR" => now.DayOfYear.ToString(),
                _ => "0"
            };
        }

        // ID — resolve defname/number, then strip the resource-type portion.
        // Source-X INTRINSIC_ID: ResGetIndex((dword)GetVal(...)) — keeps only
        // the low 20 index bits (RES_INDEX_MASK 0xFFFFF).
        if (varExpr.StartsWith("ID(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ID ", StringComparison.OrdinalIgnoreCase))
        {
            const long ResIndexMask = 0xFFFFF;
            string inner = ExtractFuncArg(varExpr, 2);
            string resolved = ResolveAngleBrackets(inner);
            // Try as hex first
            if (resolved.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(resolved[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
                return (hexVal & ResIndexMask).ToString();
            // Try as defname via variable resolver
            string? defVal = VariableResolver?.Invoke(resolved);
            if (defVal != null && long.TryParse(defVal, out long numVal))
                return (numVal & ResIndexMask).ToString();
            // Try direct number
            if (long.TryParse(resolved, out long directVal))
                return (directVal & ResIndexMask).ToString();
            return "0";
        }

        // ISEMPTY — returns 1 if arg is empty/0
        if (varExpr.StartsWith("ISEMPTY ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[8..].Trim());
            return (string.IsNullOrEmpty(inner) || inner == "0") ? "1" : "0";
        }

        // ISPLAYER — check if UID references a player character
        if (varExpr.Equals("ISPLAYER", StringComparison.OrdinalIgnoreCase))
        {
            string? val = VariableResolver?.Invoke("ISPLAYER");
            return val ?? "0";
        }

        // ISNPC — check if UID references an NPC character
        if (varExpr.Equals("ISNPC", StringComparison.OrdinalIgnoreCase))
        {
            string? val = VariableResolver?.Invoke("ISNPC");
            return val ?? "0";
        }

        // R — random: <R max> returns random 0..max-1. Sphere also accepts the
        // no-space form <Rmax> (e.g. <R999>); 'R' followed by a digit is the
        // random form, while 'R' followed by a letter (REF, REGION, ...) is not.
        if (varExpr.Length > 1 && (varExpr[0] == 'R' || varExpr[0] == 'r') &&
            (varExpr[1] == ' ' || varExpr[1] == '\t' || char.IsDigit(varExpr[1])))
        {
            string inner = ResolveAngleBrackets(varExpr[1..].Trim());
            if (int.TryParse(inner, out int rMax) && rMax > 0)
                return Random.Shared.Next(rMax).ToString();
            return "0";
        }

        // Try variable resolver
        string expanded2 = ResolveAngleBrackets(varExpr);
        string? result = VariableResolver?.Invoke(expanded2);
        if (result == null)
            result = FunctionResolver?.Invoke(expanded2);
        if (result == null)
            ReportUnresolved(expanded2);
        return result ?? "";
    }

    private string EvaluateQval(string expr)
    {
        // Find '?' / ':' the way Source-X EvaluateConditionalQval_ParseArg
        // does — skipping <...> spans so a nested <QVAL a?b:c> inside the
        // condition or branches doesn't donate its separators.
        int questionIdx = IndexOfOutsideAngles(expr, '?');
        if (questionIdx < 0)
        {
            // Source-X numeric 3-way form: QVAL v1,v2,lt,eq,gt
            //   v1 <  v2 -> lt,  v1 == v2 -> eq,  v1 > v2 -> gt
            // Only v1/v2/lt are required — missing eq/gt default to 0.
            var args = SplitArgsTopLevel(expr);
            if (args.Count >= 3)
            {
                long v1 = Evaluate(ResolveAngleBrackets(args[0]).AsSpan());
                long v2 = Evaluate(ResolveAngleBrackets(args[1]).AsSpan());
                string pick = v1 < v2
                    ? args[2]
                    : (v1 == v2
                        ? (args.Count > 3 ? args[3] : "0")
                        : (args.Count > 4 ? args[4] : "0"));
                return ResolveAngleBrackets(pick.Trim());
            }
            return "";
        }

        string condition = expr[..questionIdx].Trim();
        string rest = expr[(questionIdx + 1)..];

        int colonIdx = IndexOfOutsideAngles(rest, ':');
        string trueVal, falseVal;
        if (colonIdx >= 0)
        {
            trueVal = rest[..colonIdx].Trim();
            falseVal = rest[(colonIdx + 1)..].Trim();
        }
        else
        {
            trueVal = rest.Trim();
            falseVal = "";
        }

        long condResult = Evaluate(ResolveAngleBrackets(condition).AsSpan());
        return condResult != 0
            ? ResolveAngleBrackets(trueVal)
            : ResolveAngleBrackets(falseVal);
    }

    /// <summary>First index of <paramref name="target"/> outside any
    /// &lt;...&gt; span (letter/'_'-opened, same disambiguation as the
    /// bracket walker). -1 when not found.</summary>
    private static int IndexOfOutsideAngles(string s, char target)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<')
            {
                char nxt = i + 1 < s.Length ? s[i + 1] : '\0';
                if (nxt == '_' || char.IsLetter(nxt)) depth++;
                continue;
            }
            if (c == '>' && depth > 0) { depth--; continue; }
            if (depth == 0 && c == target) return i;
        }
        return -1;
    }

    private string EvaluateStrMatch(string expr)
    {
        // Format: pattern,string — wildcard match (* = any chars, ? = single char)
        var parts = expr.Split(',', 2);
        if (parts.Length < 2) return "0";
        string pattern = ResolveAngleBrackets(parts[0].Trim());
        string input = ResolveAngleBrackets(parts[1].Trim());
        return WildcardMatch(pattern, input) ? "1" : "0";
    }

    /// <summary>Source-X Calc_GetBellCurve — deterministic log curve.
    /// 0 diff = 500 (50.0%), diff == variance = 250, halves per variance period.</summary>
    private static int CalcGetBellCurve(int valDiff, int variance)
    {
        if (variance <= 0)
            return 500;
        if (valDiff < 0)
            valDiff = -valDiff;

        int chance = 500;
        while (valDiff > variance && chance != 0)
        {
            valDiff -= variance;
            chance /= 2; // chance is halved for each variance period
        }

        // Source-X IMulDiv(chance/2, valDiff, variance) with round-half-up
        // and negative-product correction (product can't be negative here).
        int mulDiv = ((chance / 2) * valDiff + variance / 2) / variance;
        return chance - mulDiv;
    }

    private static bool WildcardMatch(string pattern, string input)
    {
        int p = 0, i = 0, starP = -1, starI = -1;
        while (i < input.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(input[i])))
            { p++; i++; }
            else if (p < pattern.Length && pattern[p] == '*')
            { starP = p++; starI = i; }
            else if (starP >= 0)
            { p = starP + 1; i = ++starI; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool TrySafeRegexIsMatch(string input, string pattern, out bool isMatch)
    {
        isMatch = false;
        if (pattern.Length == 0 || pattern.Length > MaxRegexPatternLength || input.Length > MaxRegexInputLength)
            return false;

        try
        {
            isMatch = System.Text.RegularExpressions.Regex.IsMatch(input, pattern,
                System.Text.RegularExpressions.RegexOptions.NonBacktracking,
                RegexMatchTimeout);
            return true;
        }
        catch (System.Text.RegularExpressions.RegexParseException)
        {
            return false;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private double ParseFloatExpression(string text, ref int pos)
    {
        if (++_evalDepth > MaxEvalDepth)
        {
            --_evalDepth;
            DiagnosticLogger?.Invoke("[script] expression nesting too deep; evaluation aborted");
            return 0;
        }
        try { return ParseFloatAddSub(text, ref pos); }
        finally { --_evalDepth; }
    }

    private double ParseFloatAddSub(string text, ref int pos)
    {
        double left = ParseFloatMulDiv(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '+') { pos++; left += ParseFloatMulDiv(text, ref pos); }
            else if (c == '-') { pos++; left -= ParseFloatMulDiv(text, ref pos); }
            else break;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private double ParseFloatMulDiv(string text, ref int pos)
    {
        double left = ParseFloatUnary(text, ref pos);
        SkipWhitespace(text, ref pos);

        while (pos < text.Length)
        {
            char c = text[pos];
            if (c == '*') { pos++; left *= ParseFloatUnary(text, ref pos); }
            else if (c == '/')
            {
                pos++;
                double r = ParseFloatUnary(text, ref pos);
                left = Math.Abs(r) > double.Epsilon ? left / r : 0;
            }
            else break;
            SkipWhitespace(text, ref pos);
        }

        return left;
    }

    private double ParseFloatUnary(string text, ref int pos)
    {
        if (++_evalDepth > MaxEvalDepth)
        {
            --_evalDepth;
            DiagnosticLogger?.Invoke("[script] expression nesting too deep; evaluation aborted");
            return 0;
        }
        try
        {
            SkipWhitespace(text, ref pos);
            if (pos >= text.Length) return 0;

            char c = text[pos];
            if (c == '-') { pos++; return -ParseFloatUnary(text, ref pos); }
            if (c == '+') { pos++; return ParseFloatUnary(text, ref pos); }
            return ParseFloatPrimary(text, ref pos);
        }
        finally { --_evalDepth; }
    }

    private double ParseFloatPrimary(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        if (pos >= text.Length) return 0;

        if (text[pos] == '(')
        {
            pos++;
            double val = ParseFloatExpression(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == ')') pos++;
            return val;
        }

        if (text[pos] == '<')
        {
            string resolved = ReadAngleBracket(text, ref pos);
            string expanded = ResolveAngleBrackets(resolved);
            return ParseFloatLiteral(expanded);
        }

        if (char.IsLetter(text[pos]) || text[pos] == '_')
        {
            int idStart = pos;
            while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_'))
                pos++;
            string ident = text[idStart..pos];
            string val = ResolveVariable(ident) ?? "";
            return ParseFloatLiteral(val);
        }

        return ReadFloatNumber(text, ref pos);
    }

    private static double ReadFloatNumber(string text, ref int pos)
    {
        SkipWhitespace(text, ref pos);
        int start = pos;
        if (pos + 1 < text.Length && text[pos] == '0' && (text[pos + 1] == 'x' || text[pos + 1] == 'X'))
        {
            pos += 2;
            while (pos < text.Length && IsHexDigit(text[pos])) pos++;
            if (pos > start + 2 && long.TryParse(text.AsSpan(start + 2, pos - start - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
                return hex;
            return 0;
        }
        if (text[pos] == '0')
        {
            pos++;
            while (pos < text.Length && IsHexDigit(text[pos])) pos++;
            if (long.TryParse(text.AsSpan(start, pos - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
                return hex;
            return 0;
        }

        while (pos < text.Length && (char.IsDigit(text[pos]) || text[pos] == '.'))
            pos++;
        if (pos == start) return 0;
        return ParseFloatLiteral(text[start..pos]);
    }

    private static double ParseFloatLiteral(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            return double.IsFinite(result) ? result : 0;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
            return hex;
        return 0;
    }

    private static string FormatFloat(double value)
    {
        if (!double.IsFinite(value)) return "0";
        return value.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Extract the argument portion from a function call like "FUNC(args)" or "FUNC args".
    /// prefixLen is the length of the function name (e.g. 3 for "ABS", 4 for "SQRT").
    /// Strips parentheses if present.
    /// </summary>
    private string ExtractFuncArg(string varExpr, int prefixLen)
    {
        string rest = varExpr[prefixLen..];
        if (rest.StartsWith('('))
        {
            // Strip matching parentheses
            rest = rest[1..];
            if (rest.EndsWith(')'))
                rest = rest[..^1];
        }
        else
        {
            rest = rest.TrimStart();
        }
        return ResolveAngleBrackets(rest);
    }

    /// <summary>Extract a function's argument list WITHOUT resolving it first,
    /// split on top-level commas (bracket-aware), THEN resolve each part.
    /// Source-X splits raw text with a bracket-aware parser before evaluating —
    /// resolving first lets a value containing commas (a "1,2" coordinate)
    /// corrupt the split. <paramref name="maxArgs"/> &gt; 0 merges surplus
    /// parts back into the last argument.</summary>
    private List<string> SplitFuncArgsResolved(string varExpr, int prefixLen, int maxArgs = 0)
    {
        string rest = varExpr[prefixLen..];
        if (rest.StartsWith('('))
        {
            rest = rest[1..];
            if (rest.EndsWith(')'))
                rest = rest[..^1];
        }
        else
        {
            rest = rest.TrimStart();
        }

        var parts = SplitArgsTopLevel(rest);
        if (maxArgs > 0 && parts.Count > maxArgs)
        {
            string tail = string.Join(",", parts.Skip(maxArgs - 1));
            parts.RemoveRange(maxArgs - 1, parts.Count - (maxArgs - 1));
            parts.Add(tail);
        }
        for (int i = 0; i < parts.Count; i++)
            parts[i] = ResolveAngleBrackets(parts[i].Trim());
        return parts;
    }

    /// <summary>Split a function argument list on top-level commas, respecting
    /// nested &lt;...&gt; and (...).</summary>
    private static List<string> SplitArgsTopLevel(string s)
    {
        var args = new List<string>();
        int angle = 0, paren = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<') angle++;
            else if (c == '>' && angle > 0) angle--;
            else if (c == '(') paren++;
            else if (c == ')' && paren > 0) paren--;
            else if (c == ',' && angle == 0 && paren == 0)
            {
                args.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        if (start <= s.Length) args.Add(s[start..].Trim());
        return args;
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
    }

    private static bool IsHexDigit(char c) =>
        char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
