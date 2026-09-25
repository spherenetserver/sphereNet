using System.Globalization;

namespace SphereNet.Scripting.Expressions;

/// <summary>
/// Expression evaluator. Maps to CExpression in Source-X.
/// Evaluates arithmetic, comparison, logical, and bitwise expressions.
/// Supports hex (0x), decimal, and variable references.
/// </summary>
public sealed partial class ExpressionParser
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

    /// <summary>Last resort for a bare identifier in a numeric context: the value of a
    /// resource name (CExpression GetSingle -> ResourceGetID). Null when it names none.</summary>
    public Func<string, long?>? ResourceValueResolver { get; set; }

    /// <summary>RESOURCETYPE / RESOURCEINDEX for any context, not only a client's:
    /// (argument, wantIndex) -> hex value. Spawner timers run with no client.</summary>
    public Func<string, bool, string>? ResourceTypeIndexResolver { get; set; }

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

    /// <summary>Read one Source-X GetSingle operand. Grouped expressions are
    /// evaluated fully; an ungrouped trailing operator belongs to the caller.</summary>
    public long EvaluateSingle(ReadOnlySpan<char> expr)
        => EvaluateSingle(expr, out _);

    /// <summary>Read one operand and report the characters consumed, allowing
    /// sequential GetSingle-style callers to retain the remaining arguments.</summary>
    public long EvaluateSingle(ReadOnlySpan<char> expr, out int consumed)
    {
        consumed = 0;
        if (expr.IsEmpty || expr.Length > MaxExpressionLength) return 0;
        int pos = 0;
        long value = ParseUnary(expr.ToString(), ref pos);
        consumed = pos;
        return value;
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

        // No '|' and no '&' anywhere means there is no || or && to split on, so the
        // whole expression is a single subexpression. Skipping the splitter saves a
        // List plus a substring per condition - and the overwhelming majority of IF
        // lines in a script pack are one comparison.
        if (expr.IndexOf('|') < 0 && expr.IndexOf('&') < 0)
            return EvaluateConditionalSub(expr, depth);

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
            // A resource name (or a [DEFNAME] alias of one) counts as its resource,
            // not as 0: the worldgen tests IF (<LOCAL.SPAWN_ARRAY>) holding
            // "giantserpent,gianttoad,..." and skipped every multi-creature spawner.
            if (ReferenceEquals(callExpr, ident) && ResourceValueResolver?.Invoke(ident) is long rv)
                return rv;
            _numericOk = false; // bareword resolved to a non-numeric string / was unresolved
            return 0;
        }

        // Number literal
        return ReadNumber(text, ref pos);
    }

    /// <summary>Evaluate a Sphere brace expression: each token is evaluated as a full
    /// expression (so &lt;...&gt;/hex/identifiers work), then <see cref="BraceRange"/>
    /// decides what the list means.</summary>
    private long EvaluateBraceRange(string inner)
    {
        inner = inner.Trim();
        if (inner.Length == 0) return 0;

        var tokens = BraceRange.SplitTokens(inner);
        if (tokens.Count == 0) return 0;

        var vals = new long[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            int p = 0;
            vals[i] = ParseExpression(tokens[i], ref p);
        }

        return BraceRange.Pick(vals, inner, DiagnosticLogger);
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
                    // <<X>> reads the key NAMED BY X. Upstream gets this from the
                    // recursion in ParseScriptText: a bracket's contents are parsed
                    // first, and the bracket is then looked up with what they produced
                    // (CExpression.cpp:2590). Without that second pass the whole thing
                    // came back as the literal text "<ALCHEMY>", brackets and all.
                    //
                    // The second '<' has to open an identifier for this to be the
                    // indirection rather than a shift: inside an expression << is the
                    // shift operator (CExpression.cpp:1378), and 1 << 3 must stay one.
                    char afterSecond = pos + 2 < text.Length ? text[pos + 2] : ' ';
                    if (next == '<' && (afterSecond == '_' || char.IsLetter(afterSecond)))
                    {
                        string named = ReadAngleBracket(text, ref pos);
                        sb.Append(ResolveVariable(ResolveAngleBrackets(named)));
                        continue;
                    }

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
                varExpr.StartsWith("ARGCHK", StringComparison.OrdinalIgnoreCase) ||
                varExpr.StartsWith("ARGTXT", StringComparison.OrdinalIgnoreCase) ||
                varExpr.StartsWith("ARGV[", StringComparison.OrdinalIgnoreCase) ||
                varExpr.StartsWith("ARGV.", StringComparison.OrdinalIgnoreCase))
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
            // FormatLLHex (CScriptObj.cpp:736): -1 is "0FFFFFFFF", 0 is "00".
            return FormatSphereHex(Evaluate(expanded.AsSpan()));
        }

        // QVAL paren form — <QVAL(v1,v2,lt,eq,gt)> numeric 3-way compare.
        if (varExpr.StartsWith("QVAL(", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateQval(ExtractFuncArg(varExpr, 4), intrinsicForm: true);
        }
        // QVAL — conditional: <QVAL condition?true_val:false_val>
        if (varExpr.StartsWith("QVAL ", StringComparison.OrdinalIgnoreCase))
        {
            return EvaluateQval(varExpr[5..]);
        }

        // STRTOKEN / STRRANDRANGE / STRFIRSTCAP / LISTCOL (CScriptObj_functions.tbl).
        if (TryResolveStringTableFunction(varExpr, out string tableFnResult))
            return tableFnResult;

        // STRARG — extract first whitespace-delimited token from ARGS
        if (varExpr.StartsWith("STRARG ", StringComparison.OrdinalIgnoreCase))
        {
            // The first argument ends at whitespace OR a comma (CScriptObj.cpp:864), the
            // same pair STREAT skips. Stopping at spaces only handed back the whole of
            // "a,b,c" - the worldgen's multi-creature spawners took that as one name.
            string inner = ResolveAngleBrackets(varExpr[7..].Trim());
            if (inner.StartsWith('"')) inner = inner[1..];
            int end = 0;
            while (end < inner.Length && !char.IsWhiteSpace(inner[end]) && inner[end] != ',')
                end++;
            return inner[..end];
        }

        // STRSUB — substring: <STRSUB start,length,string>. Source-X SSC_StrSub
        // (CScriptObj.cpp:823) splits with Str_ParseCmds, whose separators are
        // "=, 	" - packs write both <STRSUB 0,3,x> and <STRSUB 0 3 <ARGS>> - and the
        // third slot keeps the rest of the line. A negative start counts back from
        // the end, a zero or overlong count runs to the end, quotes are dropped.
        if (varExpr.StartsWith("STRSUB ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRSUB(", StringComparison.OrdinalIgnoreCase))
        {
            string rest = varExpr[6..];
            if (rest.StartsWith('('))
                rest = rest.EndsWith(')') ? rest[1..^1] : rest[1..];
            rest = ResolveAngleBrackets(rest).TrimStart();
            if (!TryTakeCmdToken(ref rest, out string posTok) || !TryTakeCmdToken(ref rest, out string cntTok))
                return "";
            if (!TryEvaluate(posTok, out long pos) || !TryEvaluate(cntTok, out long cnt) || cnt < 0)
                return "";
            string str = rest;
            if (str.StartsWith('"')) str = str[1..];
            int lastQuote = str.LastIndexOf('"');
            if (lastQuote >= 0) str = str[..lastQuote];
            long len = str.Length;
            if (pos < 0) pos = len - cnt;
            if (pos > len || pos < 0) pos = 0;
            if (pos + cnt > len || cnt == 0) cnt = len - pos;
            return str.Substring((int)pos, (int)cnt);
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
            // INTRINSIC_STRCMP, CExpression.cpp:1133: a single argument compares
            // unequal (1); no argument at all is the "missing arguments" 0.
            var parts = SplitFuncArgsResolved(varExpr, 6, 2);
            if (parts.Count == 2)
                return Math.Sign(string.CompareOrdinal(parts[0], parts[1])).ToString();
            return parts.Count == 1 && parts[0].Length > 0 ? "1" : "0";
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

        // ISNUM (CScriptObj.cpp:781): skip leading whitespace and ONE minus sign, then
        // the rest must be all digits - a leading zero admits a-f (IsStrNumeric).
        // "+5", "0x10" and trailing blanks are not numbers. ISNUMBER is a different
        // test (skip to the first digit) and is answered further down.
        if (varExpr.StartsWith("ISNUM ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ResolveAngleBrackets(varExpr[6..]).TrimStart();
            if (inner.StartsWith('-')) inner = inner[1..];
            if (inner.Length == 0) return "0";
            bool hexOk = inner[0] == '0';
            foreach (char c in inner)
            {
                if (char.IsAsciiDigit(c)) continue;
                if (hexOk && char.ToLowerInvariant(c) is >= 'a' and <= 'f') continue;
                return "0";
            }
            return "1";
        }

        // ASC (CScriptObj.cpp:901): one Sphere-hex token per byte, space separated -
        // "hello" is "068 065 06C 06C 06F", an empty string is "00". A leading quote
        // is dropped and the next quote ends the text.
        if (varExpr.StartsWith("ASC ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ASC(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("ASC(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 3) : ResolveAngleBrackets(varExpr[4..].TrimStart());
            if (inner.StartsWith('"')) inner = inner[1..];
            int endQuote = inner.IndexOf('"');
            if (endQuote >= 0) inner = inner[..endQuote];
            if (inner.Length == 0) return "00";
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(inner);
            // tchar is signed upstream: a byte above 0x7F formats as a negative char.
            return string.Join(" ", bytes.Select(b => FormatSphereHex((sbyte)b)));
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
            // Format("%c", Exp_GetSingle(...)) (CScriptObj.cpp:410): ONE byte, the value
            // taken modulo 256; a zero byte ends the string, so it reads as nothing.
            byte b = unchecked((byte)code);
            return b == 0 ? "" : ((char)b).ToString();
        }

        // ASCPAD — convert string to hex ASCII codes, padded to fixed length
        if (varExpr.StartsWith("ASCPAD ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ASCPAD(", StringComparison.OrdinalIgnoreCase))
        {
            // Str_ParseCmds(args, 2) with the default "=, \t" separators: the count,
            // then EVERYTHING after it is the text (CScriptObj.cpp:918). The count is
            // an expression; a negative one refuses. Each byte is Sphere hex ("068"),
            // the padding "00".
            string body = varExpr.StartsWith("ASCPAD(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 6) : ResolveAngleBrackets(varExpr[7..]);
            string padRest = body.TrimStart();
            if (!TryTakeCmdToken(ref padRest, out string padToken) || padRest.Length == 0)
                return "";
            long padCount = Evaluate(padToken.AsSpan());
            if (padCount < 0)
                return "";
            // Clamp the pad count: an attacker-influenced value (e.g.
            // int.MaxValue) would build a multi-GB string → OutOfMemory.
            const int MaxAscPad = 4096;
            if (padCount > MaxAscPad)
            {
                DiagnosticLogger?.Invoke($"[script] ASCPAD count {padCount} exceeds max {MaxAscPad}; clamped");
                padCount = MaxAscPad;
            }
            if (padCount == 0) padCount = 1;
            string str = padRest.Trim();
            if (str.StartsWith('"')) str = str[1..];
            if (str.EndsWith('"')) str = str[..^1];
            byte[] padBytes = System.Text.Encoding.UTF8.GetBytes(str);
            var sb = new System.Text.StringBuilder();
            for (int idx = 0; idx < padCount; idx++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(idx < padBytes.Length ? FormatSphereHex((sbyte)padBytes[idx]) : "00");
            }
            return sb.ToString();
        }

        // BETWEEN / BETWEEN2 min,max,cur,absmax (CScriptObj.cpp:700): scale cur out of
        // absmax onto min..max - cur*(max-min)/absmax + min - clamped to min when
        // min>=max, absmax<=0 or cur<=0, and to max when cur>=absmax. BETWEEN2 first
        // turns cur into absmax-cur. Comma or whitespace separate the arguments.
        bool between2 = varExpr.StartsWith("BETWEEN2 ", StringComparison.OrdinalIgnoreCase) ||
                        varExpr.StartsWith("BETWEEN2(", StringComparison.OrdinalIgnoreCase);
        if (between2 ||
            varExpr.StartsWith("BETWEEN ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("BETWEEN(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitFuncArgsResolved(varExpr, between2 ? 8 : 7);
            if (parts.Count == 1)
                parts = parts[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ToList();
            long Arg(int i) => i < parts.Count ? Evaluate(parts[i].AsSpan()) : 0;
            long iMin = Arg(0), iMax = Arg(1), iCur = Arg(2), iAbsMax = Arg(3);
            if (between2)
                iCur = iAbsMax - iCur;
            if (iMin >= iMax || iAbsMax <= 0 || iCur <= 0)
                return iMin.ToString();
            if (iCur >= iAbsMax)
                return iMax.ToString();
            return (iCur * (iMax - iMin) / iAbsMax + iMin).ToString();
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
                // The MASKED value, not a yes/no: ISBIT(8,3) is 8
                // (FormatLLVal(val & (1ULL << bit)), CScriptObj.cpp:769).
                if (bit is >= 0 and < 64)
                    return (val & (1L << bit)).ToString();
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
                // Upstream converts UNCONDITIONALLY: "<dSOMEVAL> same as
                // <eval <SOMEVAL>>", and the code is
                //     if (*sVal != '-') sVal.FormatLLVal(Str_ToLL(sVal));
                // (CScriptObj.cpp:543-551) — the only value left as written is one
                // starting with '-'. Converting only when the value happened to
                // start with a '0' meant <dX> handed back whatever text X held, and
                // a comparison against it then read the leading number and stopped:
                // the pack's IsBlank does <ASC> (which answers "68 65 6C 6C 6F" for
                // "hello", as upstream's does) and then ELSEIF (<dLOCAL.ASC> == 0).
                // With the text passed through, that condition evaluated to 68 — the
                // leading number, with "== 0" never reached — so it was TRUE for
                // every non-empty string and IsBlank answered "blank" for all of
                // them. Every ISBLANK gate in the pack fired, GM pages included.
                if (varVal.StartsWith('-'))
                    return varVal;
                return Evaluate(varVal.AsSpan()).ToString();
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

        // H — force a HEX reading of the rest, the mirror of the D above
        // (<hSOMEVAL> is upstream's shorthand for <HVAL <SOMEVAL>>,
        // CScriptObj.cpp:553). A negative value is left alone, as it is there.
        //
        // The result carries the leading zero every other hex read in this engine
        // writes (<MORE1> answers 08981), because that zero is what marks a number as
        // hex when it is read back: <hSTR> of 16 written as "10" would come back as
        // ten, and as "010" it comes back as sixteen. Upstream's own commented-out
        // line in FormatLLHex says 0%x for the same reason.
        if (varExpr.Length > 1 && (varExpr[0] == 'H' || varExpr[0] == 'h') &&
            char.IsLetterOrDigit(varExpr[1]))
        {
            string hInner = ResolveAngleBrackets(varExpr[1..]);
            string? hVal = VariableResolver?.Invoke(hInner);
            if (hVal != null)
            {
                // The mirror of the D above, and converted on the same terms:
                // upstream runs FormatLLHex(Str_ToLL(sVal)) for anything that does
                // not start with '-' (CScriptObj.cpp:554-562). Str_ToLL reads the
                // leading number and ignores the rest, so a value that is not a
                // clean number becomes one rather than passing through as text.
                if (hVal.StartsWith('-'))
                    return hVal;
                long hNum = Evaluate(hVal.AsSpan());
                return "0" + hNum.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            }

            // Not a known member once the H is removed, so the H belonged to the name:
            // HITS, HOME, HITPOINTS, or a [FUNCTION] whose name starts with one.
            string hFull = ResolveAngleBrackets(varExpr);
            string? hFullVal = VariableResolver?.Invoke(hFull) ?? FunctionResolver?.Invoke(hFull);
            if (hFullVal != null)
                return hFullVal;
        }

        // EXPLODE — split string by separator chars into comma-delimited list
        if (varExpr.StartsWith("EXPLODE ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("EXPLODE(", StringComparison.OrdinalIgnoreCase))
        {
            // "seps,text" (CScriptObj.cpp:1053): the separators are the raw characters
            // up to the first comma (at most 15), the rest is cut with Str_ParseCmds on
            // them - empty tokens KEPT ("a;;b" -> "a,,b"), each trimmed, quotes and
            // brackets respected and left in place. No separators leaves the text whole.
            string body = varExpr.StartsWith("EXPLODE(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 7) : ResolveAngleBrackets(varExpr[8..]);
            body = body.TrimStart();
            int comma = body.IndexOf(',');
            string separators = comma >= 0 ? body[..comma] : body;
            if (separators.Length > 15) separators = separators[..15];
            int textStart = separators.Length + 1;
            if (textStart >= body.Length)
                return "";
            string text = body[textStart..];
            return string.Join(",", ParseCmdsWithSeparators(text, separators, 255));
        }

        // FEVAL = FormatVal(atoi(text)), FHVAL = FormatHex(atoi(text)): a C atoi of the
        // argument text, not an evaluation - "12.7" is 12 (CScriptObj.cpp:741-745).
        if (varExpr.StartsWith("FEVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FEVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FEVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 5) : varExpr[6..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return CAtoi(expanded).ToString(CultureInfo.InvariantCulture);
        }

        if (varExpr.StartsWith("FHVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FHVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FHVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 5) : varExpr[6..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            return FormatSphereHex(CAtoi(expanded));
        }

        // FLOATVAL — floating point math, printed with "%f" (CFloatMath::FloatMath,
        // CFloatMath.cpp:17): six decimals, "1.500000".
        if (varExpr.StartsWith("FLOATVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FLOATVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FLOATVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 8) : varExpr[9..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            double fv = EvaluateFloat(expanded.AsSpan());
            return double.IsFinite(fv) ? fv.ToString("F6", CultureInfo.InvariantCulture) : "0.000000";
        }

        // FVAL — format as x.x (divides by 10): <FVAL 125> = "12.5"
        if (varExpr.StartsWith("FVAL ", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("FVAL(", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("FVAL(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 4) : varExpr[5..].Trim();
            string expanded = ResolveAngleBrackets(inner);
            long val = Evaluate(expanded.AsSpan());
            // The sign belongs to the whole value (CScriptObj.cpp:729): -5 is "-0.5".
            long valAbs = Math.Abs(val);
            return $"{(val < 0 ? "-" : "")}{valAbs / 10}.{valAbs % 10}";
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
                if (div == 0)
                    return "0";
                // Upstream ROUNDS: ((a*b + c/2) / c) - IsNegative(a*b)
                // (IMulDivLL, common.h:207). Truncating instead was off by one
                // wherever the product did not divide evenly - and MULDIV is how
                // every house price and maintenance figure in the housing pack is
                // worked out, so the prices were quietly a shade cheap.
                long product = num * mul;
                long rounded = (product + div / 2) / div;
                if (product < 0)
                    rounded -= 1;
                return rounded.ToString();
            }
            return "0";
        }

        // RESOURCETYPE / RESOURCEINDEX — also where no client is attached (a spawner's
        // @Timer): the worldgen checks every spawn-list member with RESOURCEINDEX there
        // and logged "DOES NOT EXIST" for each one when this answered nothing.
        if (ResourceTypeIndexResolver != null &&
            (varExpr.StartsWith("RESOURCETYPE ", StringComparison.OrdinalIgnoreCase) ||
             varExpr.StartsWith("RESOURCEINDEX ", StringComparison.OrdinalIgnoreCase)))
        {
            int sp = varExpr.IndexOf(' ');
            bool wantIndex = varExpr[..sp].Equals("RESOURCEINDEX", StringComparison.OrdinalIgnoreCase);
            return ResourceTypeIndexResolver(ResolveAngleBrackets(varExpr[(sp + 1)..].Trim()), wantIndex);
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
            // Fewer than two arguments answers 0, not the lone argument
            // (INTRINSIC_MAX, CExpression.cpp:857: iCount < 2 -> iResult = 0).
            var a = SplitArgsTopLevel(ExtractFuncArg(varExpr, 3));
            if (a.Count >= 2) return Math.Max(Evaluate(a[0].AsSpan()), Evaluate(a[1].AsSpan())).ToString();
            return "0";
        }
        if (varExpr.StartsWith("MIN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("MIN ", StringComparison.OrdinalIgnoreCase))
        {
            // INTRINSIC_MIN, CExpression.cpp:869 - same two-argument rule as MAX.
            var a = SplitArgsTopLevel(ExtractFuncArg(varExpr, 3));
            if (a.Count >= 2) return Math.Min(Evaluate(a[0].AsSpan()), Evaluate(a[1].AsSpan())).ToString();
            return "0";
        }

        // SQRT — square root
        if (varExpr.StartsWith("SQRT(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("SQRT ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = ExtractFuncArg(varExpr, 4);
            long val = Evaluate(inner.AsSpan());
            // A negative argument takes the real part of the complex root, which
            // is 0 (INTRINSIC_SQRT, CExpression.cpp:943 - std::complex sqrt).
            return val < 0 ? "0" : ((long)Math.Sqrt(val)).ToString();
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

        // STRLEN — length of the argument text (INTRINSIC_STRLEN, CExpression.cpp:1151:
        // strlen of the argument as it stands, after the leading whitespace Str_Parse
        // skips). The packs reach for it while logging a packet's payload.
        if (varExpr.StartsWith("STRLEN(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("STRLEN ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("STRLEN(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 6) : ResolveAngleBrackets(varExpr[7..].Trim());
            return inner.Trim().Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // ISNUMBER — upstream skips forward to the first digit and then asks whether
        // what remains is a number (INTRINSIC_ISNUMBER, CExpression.cpp:1163). A
        // leading zero puts it in hex, so 0ff counts; 12x does not, and text with no
        // digit at all runs off the end and counts as false.
        if (varExpr.StartsWith("ISNUMBER(", StringComparison.OrdinalIgnoreCase) ||
            varExpr.StartsWith("ISNUMBER ", StringComparison.OrdinalIgnoreCase))
        {
            string inner = varExpr.StartsWith("ISNUMBER(", StringComparison.OrdinalIgnoreCase)
                ? ExtractFuncArg(varExpr, 8) : ResolveAngleBrackets(varExpr[9..].Trim());
            return IsSphereStrNumeric(inner) ? "1" : "0";
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
                // INTRINSIC_LOGARITHM, CExpression.cpp:881: "e" / "pi" name the base,
                // anything else is read with GetVal - so a hex base (010 = 16), a
                // defname or an expression all work - and a base <= 0 answers 0.
                string baseStr = parts[1].Trim();
                if (baseStr.Equals("e", StringComparison.OrdinalIgnoreCase))
                    return ((long)Math.Log(val)).ToString();
                if (baseStr.Equals("pi", StringComparison.OrdinalIgnoreCase))
                    return ((long)(Math.Log(val) / Math.Log(Math.PI))).ToString();
                long logBase = Evaluate(baseStr.AsSpan());
                if (logBase <= 0)
                    return "0";
                return ((long)(Math.Log(val) / Math.Log(logBase))).ToString();
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
            // INTRINSIC_STRCMPI, CExpression.cpp:1142 - strcmpi folds to LOWER case,
            // so '_' (0x5F) sorts before 'a' where an upper-case fold put it after.
            var parts = SplitFuncArgsResolved(varExpr, 7, 2);
            if (parts.Count == 2)
                return StrCmpI(parts[0], parts[1]).ToString();
            return parts.Count == 1 && parts[0].Length > 0 ? "1" : "0";
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
                // The offset is read with GetVal (hex, defnames, arithmetic), and
                // Str_IndexOf (sstring.cpp:1662) answers -1 for a negative offset, an
                // offset at or past the end, a search longer than the text and an
                // empty search - it never clamps.
                long start = parts.Count > 2 ? Evaluate(parts[2].AsSpan()) : 0;
                if (start < 0 || start >= text.Length || search.Length == 0 || search.Length > text.Length)
                    return "-1";
                // Case-SENSITIVE, as upstream.
                return text.IndexOf(search, (int)start, StringComparison.Ordinal).ToString();
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
            // Upstream reads the argument with GetVal - the ordinary number path -
            // so a leading zero means hex, a defname resolves, and arithmetic works
            // (INTRINSIC_ID, CExpression.cpp:842). Reading it as decimal instead made
            // the whole call a no-op: an id written 0401234 came back as the decimal
            // 401234, which is below the 20-bit mask, so nothing was ever stripped.
            return TryEvaluate(inner.AsSpan(), out long idVal)
                ? (idVal & ResIndexMask).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "0";
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

    private string EvaluateQval(string expr, bool intrinsicForm = false)
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
            // INTRINSIC_QVAL, CExpression.cpp:1170: fewer than three arguments is 0.
            return intrinsicForm ? "0" : "";
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

    /// <summary>STRMATCH's test: Str_Match (sstring.cpp:1750) == MATCH_VALID.</summary>
    private static bool WildcardMatch(string pattern, string input)
        => StrMatch(pattern, 0, input, 0) == MatchResult.Valid;

    private enum MatchResult { Invalid, Valid, End, Abort, Pattern, Literal, Range }

    private static char CharAt(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>ASCII-only lower fold, as the C-locale tolower upstream calls.</summary>
    private static char LowerAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    /// <summary>strcmpi: compare after an ASCII lower-case fold, answering -1/0/1.</summary>
    private static int StrCmpI(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = LowerAscii(a[i]) - LowerAscii(b[i]);
            if (d != 0) return Math.Sign(d);
        }
        return Math.Sign(a.Length - b.Length);
    }

    /// <summary>
    /// Port of Source-X Str_Match (sstring.cpp:1750): case-independent wildcard match
    /// with '?' (one char), '*' (any run) and '[...]' sets - ranges "a-z", inversion
    /// "[!...]" / "[^...]", and '\' escapes. Unlike a plain glob, an empty text only
    /// matches a pattern that is exactly "*", and a malformed set fails the match.
    /// </summary>
    private static MatchResult StrMatch(string p, int pi, string t, int ti)
    {
        for (; pi < p.Length; ++pi, ++ti)
        {
            if (ti >= t.Length)
                return (p[pi] == '*' && pi + 1 == p.Length) ? MatchResult.Valid : MatchResult.Abort;

            switch (p[pi])
            {
                case '?':
                    break;
                case '*':
                    return StrMatchAfterStar(p, pi, t, ti);
                case '[':
                {
                    ++pi;
                    bool invert = false;
                    if (CharAt(p, pi) is '!' or '^')
                    {
                        invert = true;
                        ++pi;
                    }
                    if (CharAt(p, pi) == ']')
                        return MatchResult.Pattern;

                    bool member = false;
                    for (;;)
                    {
                        if (CharAt(p, pi) == ']')
                            break;
                        char rangeStart, rangeEnd;
                        if (CharAt(p, pi) == '\\')
                            rangeStart = rangeEnd = LowerAscii(CharAt(p, ++pi));
                        else
                            rangeStart = rangeEnd = LowerAscii(CharAt(p, pi));
                        if (pi >= p.Length)
                            return MatchResult.Pattern;

                        if (CharAt(p, ++pi) == '-')
                        {
                            rangeEnd = LowerAscii(CharAt(p, ++pi));
                            if (rangeEnd is '\0' or ']')
                                return MatchResult.Pattern;
                            if (rangeEnd == '\\')
                            {
                                rangeEnd = LowerAscii(CharAt(p, ++pi));
                                if (rangeEnd == '\0')
                                    return MatchResult.Pattern;
                            }
                            ++pi;
                        }

                        char chText = LowerAscii(t[ti]);
                        if (rangeStart < rangeEnd
                                ? chText >= rangeStart && chText <= rangeEnd
                                : chText >= rangeEnd && chText <= rangeStart)
                        {
                            member = true;
                            break;
                        }
                    }

                    if ((invert && member) || !(invert || member))
                        return MatchResult.Range;

                    if (member)
                    {
                        while (CharAt(p, pi) != ']')
                        {
                            if (pi >= p.Length)
                                return MatchResult.Pattern;
                            if (p[pi] == '\\' && ++pi >= p.Length)
                                return MatchResult.Pattern;
                            ++pi;
                        }
                    }
                    break;
                }
                default:
                    if (LowerAscii(p[pi]) != LowerAscii(t[ti]))
                        return MatchResult.Literal;
                    break;
            }
        }
        return ti < t.Length ? MatchResult.End : MatchResult.Valid;
    }

    private static MatchResult StrMatchAfterStar(string p, int pi, string t, int ti)
    {
        // Pass over the run of '?' and '*': each '?' consumes one text char.
        for (; pi < p.Length && p[pi] is '?' or '*'; ++pi)
        {
            if (p[pi] == '?' && ti++ >= t.Length)
                return MatchResult.Abort;
        }
        if (pi >= p.Length)
            return MatchResult.Valid;

        char nextp = LowerAscii(p[pi]);
        MatchResult match = MatchResult.Invalid;
        do
        {
            if (nextp == LowerAscii(CharAt(t, ti)) || nextp == '[')
            {
                match = StrMatch(p, pi, t, ti);
                if (match == MatchResult.Valid)
                    break;
            }
            if (ti++ >= t.Length)
                return MatchResult.Abort;
        } while (match != MatchResult.Abort && match != MatchResult.Pattern);
        return match;
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

            // An intrinsic call. Upstream dispatches the same table here as in the
            // integer evaluator (CFloatMath.cpp:254), so every one of these has to
            // answer in a float expression too - they were all reading 0.
            if (pos < text.Length && text[pos] == '(' && IntrinsicNames.Contains(ident))
            {
                string args = ReadBalancedArgs(text, ref pos);
                return EvaluateFloatIntrinsic(ident, args);
            }

            string val = ResolveVariable(ident) ?? "";
            return ParseFloatLiteral(val);
        }


        return ReadFloatNumber(text, ref pos);
    }

    /// <summary>The intrinsic table, spelled once (sm_IntrinsicFunctions,
    /// CExpression.h:109). The integer evaluator matches these by prefix as it walks
    /// its chain; the float one needs the names up front to tell a call from a
    /// variable.</summary>
    private static readonly HashSet<string> IntrinsicNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABS", "ARCCOS", "ARCSIN", "ARCTAN", "COS", "ID", "ISNUMBER", "ISOBSCENE",
        "LOGARITHM", "MAX", "MIN", "NAPIERPOW", "QVAL", "RAND", "RANDBELL", "SIN",
        "SQRT", "STRASCII", "STRCMP", "STRCMPI", "STRINDEXOF", "STRLEN", "STRMATCH",
        "STRREGEX", "TAN"
    };

    /// <summary>Read the text between a call's parentheses, leaving pos past the
    /// closing one. Nested calls keep their own parentheses.</summary>
    private static string ReadBalancedArgs(string text, ref int pos)
    {
        ++pos;                       // past '('
        int start = pos, depth = 1;
        while (pos < text.Length && depth > 0)
        {
            if (text[pos] == '(') ++depth;
            else if (text[pos] == ')' && --depth == 0) break;
            ++pos;
        }
        string args = text[start..Math.Min(pos, text.Length)];
        if (pos < text.Length) ++pos; // past ')'
        return args;
    }

    /// <summary>
    /// The intrinsics whose float form genuinely differs from the integer one: upstream
    /// computes them in real arithmetic and does NOT truncate, so SQRT(2) is 1.414...
    /// here where the integer evaluator answers 1. Their arguments run back through the
    /// float parser, so a nested call keeps its precision too.
    ///
    /// Everything else - the string tests, the comparisons, ID - has the same answer
    /// in both, so it is delegated to the one implementation rather than written twice.
    /// </summary>
    private double EvaluateFloatIntrinsic(string name, string args)
    {
        double Arg(int i)
        {
            var parts = SplitTopLevel(args);
            if (i >= parts.Count) return 0;
            int p = 0;
            return ParseFloatExpression(ResolveAngleBrackets(parts[i]), ref p);
        }

        switch (name.ToUpperInvariant())
        {
            // Upstream has no ABS case in the float switch, so it falls to the default
            // and answers 0 with a console error. Answering it is the deliberate
            // difference: every other function in a float expression works, and a
            // silent 0 from this one is a trap rather than a behaviour to match.
            case "ABS":       return Math.Abs(Arg(0));
            // A negative argument is the real part of the complex root: 0
            // (CFloatMath.cpp INTRINSIC_SQRT, std::complex sqrt).
            case "SQRT":      { double v = Arg(0); return v < 0 ? 0 : Math.Sqrt(v); }
            // The float evaluator works in DEGREES, unlike the integer one:
            // sin(x * M_PI / 180) in, asin(x) * 180 / M_PI out (CFloatMath.cpp
            // INTRINSIC_SIN/COS/TAN/ARCSIN/ARCCOS/ARCTAN).
            case "SIN":       return Math.Sin(Arg(0) * Math.PI / 180);
            case "COS":       return Math.Cos(Arg(0) * Math.PI / 180);
            case "TAN":       return Math.Tan(Arg(0) * Math.PI / 180);
            case "ARCSIN":    return Math.Asin(Arg(0)) * 180 / Math.PI;
            case "ARCCOS":    return Math.Acos(Arg(0)) * 180 / Math.PI;
            case "ARCTAN":    return Math.Atan(Arg(0)) * 180 / Math.PI;
            case "NAPIERPOW": return Math.Exp(Arg(0));
            // Fewer than two arguments is 0 (CFloatMath.cpp INTRINSIC_MAX/MIN).
            case "MAX":       return SplitTopLevel(args).Count < 2 ? 0 : Math.Max(Arg(0), Arg(1));
            case "MIN":       return SplitTopLevel(args).Count < 2 ? 0 : Math.Min(Arg(0), Arg(1));
            case "LOGARITHM":
            {
                double v = Arg(0);
                if (v <= 0) return 0;
                var parts = SplitTopLevel(args);
                if (parts.Count < 2) return Math.Log10(v);
                // "e" / "pi" name the base; a base <= 0 answers 0
                // (CFloatMath.cpp INTRINSIC_LOGARITHM).
                string baseTok = parts[1].Trim();
                if (baseTok.Equals("e", StringComparison.OrdinalIgnoreCase)) return Math.Log(v);
                if (baseTok.Equals("pi", StringComparison.OrdinalIgnoreCase)) return Math.Log(v) / Math.Log(Math.PI);
                double b = Arg(1);
                return b <= 0 ? 0 : Math.Log(v) / Math.Log(b);
            }
            default:
                return ParseFloatLiteral(ResolveVariable($"{name}({args})") ?? "0");
        }
    }

    /// <summary>Split an argument list on commas that are not inside parentheses or
    /// angle brackets.</summary>
    private static List<string> SplitTopLevel(string args)
    {
        var outList = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < args.Length; ++i)
        {
            char c = args[i];
            if (c is '(' or '<') ++depth;
            else if (c is ')' or '>') --depth;
            else if (c == ',' && depth == 0)
            {
                outList.Add(args[start..i]);
                start = i + 1;
            }
        }
        outList.Add(args[start..]);
        return outList;
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

    /// <summary>Sphere's hex text for a number (CSString::FormatLLHex ->
    /// Str_FromLL_Fast base 16, sstring.cpp:487): a '0' prefix and uppercase
    /// digits; zero is "00"; anything up to UINT32_MAX - negatives included - is
    /// shown as a 32-bit two's-complement word, so -1 is "0FFFFFFFF".</summary>
    internal static string FormatSphereHex(long value)
    {
        if (value == 0) return "00";
        return value <= uint.MaxValue
            ? "0" + unchecked((uint)value).ToString("X", CultureInfo.InvariantCulture)
            : "0" + value.ToString("X", CultureInfo.InvariantCulture);
    }

    /// <summary>C atoi: skip leading whitespace, an optional sign, then decimal digits
    /// only, stopping at the first other character; 32-bit. FEVAL/FHVAL use exactly
    /// this, not an evaluation (CScriptObj.cpp:741-745).</summary>
    private static int CAtoi(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        bool neg = false;
        if (i < text.Length && (text[i] == '-' || text[i] == '+')) { neg = text[i] == '-'; i++; }
        long v = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            v = v * 10 + (text[i] - '0');
            if (v > int.MaxValue + 1L) v = int.MaxValue + 1L;
            i++;
        }
        return unchecked((int)(neg ? -v : v));
    }

    /// <summary>Str_ParseCmds with a caller separator set (CExpression.cpp:137/284):
    /// quote- and bracket-aware, EMPTY tokens kept ("a;;b" is three), each token
    /// trimmed, a whitespace separator swallowing one following separator, at most
    /// <paramref name="max"/> tokens.</summary>
    private static List<string> ParseCmdsWithSeparators(string line, string seps, int max)
    {
        var result = new List<string>();
        string s = line.TrimStart();
        if (s.Length == 0) return result;
        bool sepCurly = seps.IndexOfAny(['{', '}']) >= 0, sepSquare = seps.IndexOfAny(['[', ']']) >= 0,
             sepRound = seps.IndexOfAny(['(', ')']) >= 0, sepAngle = seps.IndexOfAny(['<', '>']) >= 0;
        int pos = 0;
        while (result.Count < max)
        {
            bool quotes = false;
            int curly = 0, square = 0, round = 0, angle = 0;
            int start = pos;
            int i = pos;
            int cut = -1;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '"') { quotes = !quotes; continue; }
                if (quotes) continue;
                switch (ch)
                {
                    case '{': if (!sepCurly && square == 0 && round == 0 && angle == 0) curly++; break;
                    case '[': if (!sepSquare && curly == 0 && round == 0 && angle == 0) square++; break;
                    case '(': if (!sepRound && curly == 0 && square == 0 && angle == 0) round++; break;
                    case '<': if (!sepAngle && curly == 0 && square == 0 && round == 0) angle++; break;
                    case '}': if (!sepCurly && curly > 0) curly--; break;
                    case ']': if (!sepSquare && square > 0) square--; break;
                    case ')': if (!sepRound && round > 0) round--; break;
                    case '>': if (!sepAngle && angle > 0) angle--; break;
                }
                if (curly <= 0 && square <= 0 && round <= 0 && seps.IndexOf(ch) >= 0) { cut = i; break; }
            }
            if (cut < 0)
            {
                result.Add(s[start..].Trim());
                break;
            }
            result.Add(s[start..cut].Trim());
            pos = cut + 1;
            if (char.IsWhiteSpace(s[cut]))
            {
                while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
                if (pos < s.Length && seps.IndexOf(s[pos]) >= 0) pos++;
            }
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }
        return result;
    }

    /// <summary>
    /// Extract the argument portion from a function call like "FUNC(args)" or "FUNC args".
    /// prefixLen is the length of the function name (e.g. 3 for "ABS", 4 for "SQRT").
    /// Strips parentheses if present.
    /// </summary>
    /// <summary>ISNUMBER's test: skip to the first digit (SKIP_NONNUM), then read the
    /// rest as a number, where a leading zero admits hex digits (IsStrNumeric,
    /// sstring.cpp). Running off the end without finding a digit is false.</summary>
    private static bool IsSphereStrNumeric(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsAsciiDigit(text[i]))
            ++i;
        if (i >= text.Length)
            return false;

        bool hex = text[i] == '0';
        for (; i < text.Length; ++i)
        {
            char c = text[i];
            if (char.IsAsciiDigit(c))
                continue;
            if (hex && char.ToLowerInvariant(c) is >= 'a' and <= 'f')
                continue;
            return false;
        }
        return true;
    }

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
    /// <summary>One Str_Parse step with the default "=, 	" separators: take the
    /// leading token and leave <paramref name="rest"/> at the next argument.</summary>
    private static bool TryTakeCmdToken(ref string rest, out string token)
    {
        int i = rest.IndexOfAny(CmdSeparators);
        if (i < 0)
        {
            token = rest;
            rest = "";
            return token.Length > 0;
        }
        token = rest[..i];
        int j = i;
        while (j < rest.Length && Array.IndexOf(CmdSeparators, rest[j]) >= 0) j++;
        rest = rest[j..];
        return token.Length > 0;
    }

    private static readonly char[] CmdSeparators = ['=', ',', ' ', '	'];

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
