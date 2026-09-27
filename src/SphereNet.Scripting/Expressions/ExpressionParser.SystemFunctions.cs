using System.Diagnostics;

namespace SphereNet.Scripting.Expressions;

/// <summary>CScriptObj functions that reach outside the script world: RESDEF/RESDEF0
/// (the resource-defname table), BCRYPTHASH/BCRYPTVALIDATE (password hashing) and
/// SYSCMD/SYSSPAWN (operating-system processes, gated by OF_FileCommands).</summary>
public sealed partial class ExpressionParser
{
    /// <summary>RESDEF / RESDEF0 lookup: resource defname -> its resource uid (the
    /// value CResourceDef::SetResourceName stores in m_VarResDefs), or null when the
    /// name registers no resource. Wired per interpreter by TriggerRunner.</summary>
    public Func<string, long?>? ResourceDefResolver { get; set; }

    /// <summary>BCRYPTHASH backend: (password, prefix code, clamped cost) -> hash text,
    /// "" when the prefix cannot be hashed. Unset -> the keyword stays unresolved.</summary>
    public static Func<string, int, int, string>? BCryptHasher { get; set; }

    /// <summary>BCRYPTVALIDATE backend: (password, hash) -> matches.</summary>
    public static Func<string, string, bool>? BCryptValidator { get; set; }

    /// <summary>OF_FileCommands gate for SYSCMD/SYSSPAWN (CScriptObj.cpp:953). Unset or
    /// false -> both keywords are refused, exactly as upstream returns false.</summary>
    public static Func<bool>? FileCommandsEnabled { get; set; }

    /// <summary>Starts an OS process: (program, arguments, wait) -> exit code when
    /// waited, null otherwise or when the start failed. Replaceable for tests.</summary>
    public static Func<string, IReadOnlyList<string>, bool, int?> SystemProcessLauncher { get; set; }
        = DefaultSystemProcessLauncher;

    /// <summary>Diagnostic sink for SYSCMD/SYSSPAWN (upstream g_Log.EventDebug).</summary>
    public static Action<string>? SystemProcessDiagnostic { get; set; }

    public static int? DefaultSystemProcessLauncher(string program, IReadOnlyList<string> args, bool wait)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false };
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null)
                return null;
            if (!wait)
                return null;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            SystemProcessDiagnostic?.Invoke($"process start failed for '{program}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Match <paramref name="keyword"/> as the head of <paramref name="varExpr"/>
    /// the way FindTableHeadSorted does: the next character must not continue the word.
    /// Returns the argument text after the keyword and its separators.</summary>
    private static bool TryMatchHead(string varExpr, string keyword, out string rest)
    {
        rest = "";
        if (varExpr.Length < keyword.Length ||
            !varExpr.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            return false;
        if (varExpr.Length > keyword.Length)
        {
            char next = varExpr[keyword.Length];
            if (char.IsLetterOrDigit(next) || next == '_')
                return false;
        }
        // SKIP_SEPARATORS: dots and whitespace between the key and its argument.
        int i = keyword.Length;
        while (i < varExpr.Length && (varExpr[i] == '.' || char.IsWhiteSpace(varExpr[i])))
            i++;
        rest = varExpr[i..];
        return true;
    }

    private bool TryResolveSystemFunction(string varExpr, out string result)
    {
        result = "";
        if (varExpr.Length < 6)
            return false;
        char c0 = char.ToUpperInvariant(varExpr[0]);
        if (c0 != 'R' && c0 != 'B' && c0 != 'S')
            return false;

        // RESDEF / RESDEF0 (CScriptObj.cpp:683-695): the resource uid a defname
        // registered; RESDEF0 answers 0 for an unknown name, RESDEF an empty string.
        if (ResourceDefResolver != null)
        {
            bool zero = TryMatchHead(varExpr, "RESDEF0", out string zeroKey);
            if (zero || TryMatchHead(varExpr, "RESDEF", out zeroKey))
            {
                string name = ResolveAngleBrackets(zeroKey).Trim();
                long? uid = name.Length > 0 ? ResourceDefResolver(name) : null;
                // CVarDefContNum::GetValStr -> hex (no DecimalVariables option here).
                result = uid.HasValue ? FormatSphereHex(uid.Value) : (zero ? "0" : "");
                return true;
            }
        }

        // BCRYPTHASH prefix,cost,password (CScriptObj.cpp:1096-1117).
        if (BCryptHasher != null && TryMatchHead(varExpr, "BCRYPTHASH", out string hashArgs))
        {
            var cmd = ParseCmds(ResolveAngleBrackets(hashArgs), 3, ", ");
            if (cmd.Count < 3 ||
                !Core.Types.ScriptNumber.TryParseToken(cmd[0], out long prefix) ||
                !Core.Types.ScriptNumber.TryParseToken(cmd[1], out long cost))
                return false;
            result = BCryptHasher(cmd[2], (int)prefix, (int)Math.Max(4, Math.Min(31, cost)));
            return true;
        }

        // BCRYPTVALIDATE password,hash (CScriptObj.cpp:1119-1128).
        if (BCryptValidator != null && TryMatchHead(varExpr, "BCRYPTVALIDATE", out string valArgs))
        {
            var cmd = ParseCmds(ResolveAngleBrackets(valArgs), 2, ", ");
            if (cmd.Count < 2)
                return false;
            result = BCryptValidator(cmd[0], cmd[1]) ? "1" : "0";
            return true;
        }

        // SYSCMD / SYSSPAWN (CScriptObj.cpp:950-1003): only with OF_FileCommands.
        bool sysCmd = TryMatchHead(varExpr, "SYSCMD", out string sysArgs);
        if (sysCmd || TryMatchHead(varExpr, "SYSSPAWN", out sysArgs))
        {
            if (FileCommandsEnabled?.Invoke() != true)
                return false;
            // Program plus at most nine arguments (Arg_ppCmd[10]).
            var cmd = ParseCmds(ResolveAngleBrackets(sysArgs).TrimStart(), 10, null);
            if (cmd.Count < 1)
                return false;
            var args = cmd.Skip(1).Select(StripQuotes).ToList();
            SystemProcessDiagnostic?.Invoke($"Process execution started ({(sysCmd ? "SYSCMD" : "SYSSPAWN")}).");
            int? exit = SystemProcessLauncher(StripQuotes(cmd[0]), args, sysCmd);
            SystemProcessDiagnostic?.Invoke("Process execution finished.");
            // The Windows build (_spawnl) never writes a value; the POSIX build hands
            // back SYSCMD's exit status in hex.
            result = sysCmd && exit.HasValue && !OperatingSystem.IsWindows()
                ? FormatSphereHex(exit.Value)
                : "";
            return true;
        }

        return false;
    }

    private static string StripQuotes(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    /// <summary>Str_ParseCmds (CExpression.cpp:137-302): split on the separator set
    /// (default "=, \t") outside quotes and brackets; the last slot keeps the rest of
    /// the line. After a whitespace separator, one following separator is also eaten.</summary>
    public static List<string> ParseCmds(string line, int max, string? separators)
    {
        string seps = separators ?? "=, \t";
        var result = new List<string>();
        int pos = 0;
        while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
        if (pos >= line.Length)
            return result;

        while (true)
        {
            if (result.Count == max - 1)
            {
                result.Add(line[pos..].Trim());
                break;
            }
            int start = pos;
            bool quotes = false;
            int curly = 0, square = 0, round = 0;
            int i = pos;
            for (; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"') { quotes = !quotes; continue; }
                if (quotes) continue;
                if (ch == '{' && !seps.Contains('{')) curly++;
                else if (ch == '}' && !seps.Contains('}') && curly > 0) curly--;
                else if (ch == '[' && !seps.Contains('[')) square++;
                else if (ch == ']' && !seps.Contains(']') && square > 0) square--;
                else if (ch == '(' && !seps.Contains('(')) round++;
                else if (ch == ')' && !seps.Contains(')') && round > 0) round--;
                if (curly <= 0 && square <= 0 && round <= 0 && seps.Contains(ch))
                    break;
            }
            if (i >= line.Length)
            {
                result.Add(line[start..].Trim());
                break;
            }
            result.Add(line[start..i].Trim());
            char sep = line[i];
            pos = i + 1;
            if (char.IsWhiteSpace(sep))
            {
                while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
                if (pos < line.Length && seps.Contains(line[pos])) pos++;
            }
            while (pos < line.Length && char.IsWhiteSpace(line[pos])) pos++;
            if (pos >= line.Length)
            {
                // A trailing separator still opens an (empty) argument.
                result.Add("");
                break;
            }
        }
        return result;
    }
}
