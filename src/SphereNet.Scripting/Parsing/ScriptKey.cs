namespace SphereNet.Scripting.Parsing;

/// <summary>
/// A parsed script line as KEY=VALUE pair.
/// Maps to CScriptKey in Source-X.
/// </summary>
public sealed class ScriptKey
{
    public string Key { get; private set; } = "";
    public string Arg { get; private set; } = "";
    public bool HasArg => Arg.Length > 0;

    /// <summary>The whole line as read (trimmed, comments already stripped by
    /// ScriptFile) — the equivalent of Source-X ReadKey's verbatim buffer.
    /// Consumers of raw-line sections (dialog TEXT, [TELEPORTERS], [NAMES],
    /// title lists...) must use this instead of reconstructing "Key Arg",
    /// which loses the separator character (comma vs space) and spacing.</summary>
    private string _rawLine = "";
    public string RawLine
    {
        get => _rawLine.Length > 0 ? _rawLine : (HasArg ? $"{Key} {Arg}" : Key);
        private set => _rawLine = value;
    }

    /// <summary>Source file path the line was parsed from (set by
    /// <see cref="ScriptFile"/>). Used for diagnostic messages so
    /// scriptdebug warnings can pinpoint the offending line; never
    /// affects execution. Empty for keys built in code.
    /// Interned to avoid duplicate path strings across thousands of keys.</summary>
    private string _sourceFile = "";
    public string SourceFile
    {
        get => _sourceFile;
        set => _sourceFile = string.IsNullOrEmpty(value) ? "" : string.Intern(value);
    }

    /// <summary>1-based source line number, set together with
    /// <see cref="SourceFile"/>. Zero when unknown.</summary>
    public int SourceLine { get; set; }

    public ScriptKey() { }

    /// <summary>Construct a pre-parsed key/arg pair — used when expanding
    /// or rewriting script lines without re-running the parser (e.g. the
    /// dialog FOR/IF pre-expander in GameClient).</summary>
    public ScriptKey(string key, string arg)
    {
        Key = key ?? "";
        Arg = arg ?? "";
    }

    /// <summary>Source-X CScriptKey::GetArgStr quote handling: when the value
    /// starts with a double quote, drop it and cut at the LAST double quote
    /// (any trailing text after the closing quote is discarded). Values not
    /// starting with a quote pass through unchanged.</summary>
    public static string StripQuotePair(string value)
    {
        if (value.Length == 0 || value[0] != '"')
            return value;
        string body = value[1..];
        int lastQuote = body.LastIndexOf('"');
        return lastQuote >= 0 ? body[..lastQuote] : body;
    }

    /// <summary>The Arg the way Source-X GetArgStr returns it — surrounding
    /// quote pair stripped.</summary>
    public string ArgUnquoted => StripQuotePair(Arg);

    /// <summary>Try to parse the line as a plain KEY=ARG pair using ONLY '='
    /// as separator — used by loaders that accept both "name=value" and bare
    /// value lines from raw sections.</summary>
    public static bool TrySplitOnEquals(string line, out string key, out string value)
    {
        int eq = line.IndexOf('=');
        if (eq < 0)
        {
            key = "";
            value = line;
            return false;
        }
        key = line[..eq].TrimEnd();
        value = line[(eq + 1)..].TrimStart();
        return true;
    }

    /// <summary>
    /// Parse a raw line into key and argument.
    /// Handles KEY=VALUE, KEY VALUE, and plain KEY forms.
    /// Also handles =, +=, -=, .= operators by rewriting the argument.
    /// </summary>
    public void Parse(ReadOnlySpan<char> line)
    {
        line = line.Trim();
        if (line.IsEmpty)
        {
            Key = "";
            Arg = "";
            RawLine = "";
            return;
        }
        RawLine = line.ToString();

        // Increment / decrement forms: "FOO ++" / "FOO--" / "BAR++"
        // Rewrite into += 1 / -= 1 so the rest of the pipeline handles them
        // uniformly. Sphere scripts lean on this heavily ("Src.CTag0.X ++"
        // is the canonical counter bump in d_admin_main).
        if (line.Length >= 2)
        {
            var trimmed = line;
            if (trimmed[^1] == '+' && trimmed[^2] == '+' &&
                (trimmed.Length == 2 || trimmed[^3] != '+'))
            {
                string body = trimmed[..^2].TrimEnd().ToString();
                if (body.Length > 0)
                {
                    Key = body;
                    Arg = $"<EVAL <{body}>+1>";
                    return;
                }
            }
            if (trimmed[^1] == '-' && trimmed[^2] == '-' &&
                (trimmed.Length == 2 || trimmed[^3] != '-'))
            {
                string body = trimmed[..^2].TrimEnd().ToString();
                if (body.Length > 0)
                {
                    Key = body;
                    Arg = $"<EVAL <{body}>-1>";
                    return;
                }
            }
        }

        // Find the first key/arg separator that lies OUTSIDE any bracket
        // group or quoted span. Source-X Str_Parse uses the separator set
        // "=, \t" (equals, comma, space, tab), toggles an in-quote state on
        // '"', and tracks {} [] () <> depth. Sphere scripts routinely build
        // dynamic keys like
        //     UID.<CTag.Admin.C<Eval <ArgN>-10>>.Dialog d_SphereAdmin
        // — the first ' ' character lives inside `<Eval ...>`. A naive
        // IndexOf chops the key in the middle of the bracket, leaves a
        // dangling "<Eval" in Key, and the later expansion warns
        // "unresolved <Eval>". Walking with bracket depth tracking keeps
        // those expressions intact.
        int sepPos = -1;
        {
            int angleDepth = 0;
            int parenDepth = 0;
            int curlyDepth = 0;
            int squareDepth = 0;
            bool inQuotes = false;
            for (int p = 0; p < line.Length; p++)
            {
                char ch = line[p];
                if (ch == '"') { inQuotes = !inQuotes; continue; }
                if (inQuotes) continue;
                if (ch == '<')
                {
                    // Treat as nested bracket only when followed by a
                    // letter / underscore — same disambiguation used by the
                    // expression walker so "a < b" stays a comparison.
                    char nxt = p + 1 < line.Length ? line[p + 1] : '\0';
                    if (nxt == '_' || char.IsLetter(nxt)) angleDepth++;
                    continue;
                }
                if (ch == '>' && angleDepth > 0) { angleDepth--; continue; }
                if (ch == '(') { parenDepth++; continue; }
                if (ch == ')' && parenDepth > 0) { parenDepth--; continue; }
                if (ch == '{') { curlyDepth++; continue; }
                if (ch == '}' && curlyDepth > 0) { curlyDepth--; continue; }
                if (ch == '[') { squareDepth++; continue; }
                if (ch == ']' && squareDepth > 0) { squareDepth--; continue; }
                if (angleDepth > 0 || parenDepth > 0 || curlyDepth > 0 || squareDepth > 0) continue;

                if (ch is '=' or ',' or ' ' or '\t')
                {
                    sepPos = p;
                    break;
                }
            }
        }

        if (sepPos < 0)
        {
            Key = line.ToString();
            Arg = "";
            return;
        }

        Key = line[..sepPos].TrimEnd().ToString();
        var argSpan = line[(sepPos + 1)..].TrimStart();

        // Source-X Str_Parse: when the separator was WHITESPACE, the following
        // whitespace is skipped and ONE additional separator char is consumed.
        // This makes "KEY = VAL" / "KEY , VAL" yield Arg "VAL" — without it the
        // '=' leaks into the Arg and numeric [DEFNAME] constants written as
        // "name = value" parse as text "= value".
        if ((line[sepPos] == ' ' || line[sepPos] == '\t') &&
            argSpan.Length > 0 && (argSpan[0] == '=' || argSpan[0] == ','))
        {
            argSpan = argSpan[1..].TrimStart();
        }

        Arg = argSpan.ToString();

        // Handle compound assignment operators that collapse onto the
        // key side: "foo+=3", "foo.=x", "foo-=2". The "=" is the sep
        // here so the preceding char is the operator.
        if (Key.Length > 0 && line[sepPos] == '=')
        {
            char lastChar = Key[^1];
            if (lastChar is '+' or '-' or '*' or '/' or '%' or '.' or '|' or '&' or '^' or '!')
            {
                string realKey = Key[..^1].TrimEnd();
                string op = lastChar.ToString();
                Arg = RewriteCompoundAssignment(realKey, op, Arg);
                Key = realKey;
            }
        }

        // Handle the space-separated form where the operator is in the
        // Arg part: "foo += 3", "bar |= flag", "baz .= suffix". sepPos
        // fell on whitespace so the operator is the first token of Arg.
        if (Arg.Length >= 2)
        {
            char a0 = Arg[0];
            if (a0 is '+' or '-' or '*' or '/' or '%' or '.' or '|' or '&' or '^' or '!' && Arg[1] == '=')
            {
                Arg = RewriteCompoundAssignment(Key, a0.ToString(), Arg[2..].TrimStart());
            }
        }
    }

    /// <summary>Source-X ReadKeyParse OP= rewrite: ".=" concatenates, keys
    /// under the FLOAT. namespace evaluate through FLOATVAL, everything else
    /// through EVAL. Operator set mirrors Source-X ".*+-/%|&amp;!^".</summary>
    private static string RewriteCompoundAssignment(string realKey, string op, string rest)
    {
        if (op == ".")
            return $"<{realKey}>{rest}"; // .= is string concatenation
        if (realKey.StartsWith("FLOAT.", StringComparison.OrdinalIgnoreCase))
            return $"<FLOATVAL <{realKey}>{op}({rest})>";
        return $"<EVAL <{realKey}>{op}{rest}>";
    }

    /// <summary>
    /// Try to parse the argument as an integer.
    /// Supports hex (0x prefix) and decimal.
    /// </summary>
    public bool TryGetArgInt(out long value)
    {
        return TryParseNumber(Arg.AsSpan(), out value);
    }

    public long GetArgInt(long defaultValue = 0)
    {
        return TryGetArgInt(out long val) ? val : defaultValue;
    }

    internal static bool TryParseNumber(ReadOnlySpan<char> text, out long value)
    {
        text = text.Trim();
        value = 0;
        if (text.IsEmpty) return false;

        // Explicit 0x/0X prefix → hex
        if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
        {
            return long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        // Leading '0' with length > 1 → hex (Source-X convention)
        // Source-X treats all numbers starting with '0' as hex: 09ae1, 06F7, 00000020, etc.
        if (text.Length > 1 && text[0] == '0')
        {
            return TryParseLeadingZeroHex(text[1..], out value);
        }

        // Plain decimal
        return long.TryParse(text, out value);
    }

    /// <summary>Source-X GetSingle hex semantics for leading-zero numbers:
    /// count significant nibbles after the zeros — ≤8 nibbles reinterpret as
    /// a signed 32-bit value (sign-extended, so "0ffffffff" = -1), 9–16 as a
    /// signed 64-bit value, more than 16 overflows to -1.</summary>
    internal static bool TryParseLeadingZeroHex(ReadOnlySpan<char> digits, out long value)
    {
        value = 0;
        if (digits.IsEmpty) return false;

        ulong acc = 0;
        int sig = 0;
        bool seenNonZero = false;
        foreach (char c in digits)
        {
            int v;
            if (c >= '0' && c <= '9') v = c - '0';
            else if (c >= 'a' && c <= 'f') v = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') v = c - 'A' + 10;
            else return false; // not a pure hex token

            if (!seenNonZero)
            {
                if (v == 0) continue; // leading zeros don't count
                seenNonZero = true;
                sig = 1;
                acc = (ulong)v;
            }
            else
            {
                if (sig == 16)
                {
                    value = -1; // Source-X: >16 significant nibbles overflows to -1
                    return true;
                }
                acc = (acc << 4) | (uint)v;
                sig++;
            }
        }

        value = sig <= 8
            ? (int)(uint)acc // reinterpret as int32 → sign-extend
            : unchecked((long)acc); // int64 two's complement
        return true;
    }

    public override string ToString() => HasArg ? $"{Key}={Arg}" : Key;
}
