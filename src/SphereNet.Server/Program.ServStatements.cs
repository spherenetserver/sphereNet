using System.Globalization;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Game.Accounts;
using SphereNet.Game.Definitions;

namespace SphereNet.Server;

/// <summary>
/// The host half of the generic SERV.&lt;key&gt; statement and read paths.
///
/// Upstream a SERV line is CServer::r_Verb (CServer.cpp:1780): its own verb table,
/// then a [FUNCTION] of that name run on the server, then ACCOUNT.&lt;name&gt;.&lt;key&gt;,
/// and finally CScriptObj::r_Verb. The interpreter owns the dispatch order; the parts
/// that need the account table, the config or the main loop are answered here.
/// </summary>
public static partial class Program
{
    /// <summary>Set while a SERV read asks only for the server's own keys: the
    /// interpreter checks those first and, when none answers, calls a [FUNCTION] of
    /// that name before the constant fallbacks get a chance to claim the word.</summary>
    [ThreadStatic] private static bool t_servKeyOnly;

    /// <summary>&lt;SERV.key&gt; restricted to what the server itself answers - the
    /// config, world and server-definition keys (CServer::r_WriteVal up to the r_Call
    /// in CServerDef::r_WriteVal, CServerDef.cpp:509). Null when none of them does.</summary>
    private static string? ResolveServKeyOnly(string property)
    {
        bool saved = t_servKeyOnly;
        t_servKeyOnly = true;
        try { return ResolveServerProperty(property); }
        finally { t_servKeyOnly = saved; }
    }

    /// <summary>The tail of the SERV switch: the config/reference read-back, then -
    /// unless only server keys were asked for - the engine's own extensions, the
    /// defname constants and the script expression table.</summary>
    private static string? ResolveServDefault(string property, string upper)
    {
        bool keyOnly = t_servKeyOnly;
        t_servKeyOnly = false;
        return ResolveServReadback(property, upper)
            ?? (keyOnly ? null
                : ResolveServCustomVersionKey(upper) ?? ResolveDefConstant(upper) ?? ResolveServFunction(property));
    }

    /// <summary>Sphere 56T custom-version SERV keys that Source-X does not have. They
    /// sit AFTER the script [FUNCTION] lookup, so a Source-X pack that defines a
    /// function of the same name (a pack-side [FUNCTION RTIMESQL]) keeps answering
    /// with its own.</summary>
    private static string? ResolveServCustomVersionKey(string upper) => upper switch
    {
        // RTIMESQL: the local time as an SQL DATETIME literal, "YYYY-MM-DD HH:MM:SS" -
        // the packs write it straight into INSERT/UPDATE date columns.
        "RTIMESQL" => DateTime.Now.ToString("yyyy'-'MM'-'dd HH':'mm':'ss", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>SV_RESYNC (CServer.cpp:2191) only flags the request; the resync runs
    /// on the main loop, never inside the script that asked for it.</summary>
    private static string? HandleServResyncRequest()
    {
        _mainLoopActions.Enqueue(PerformScriptResync);
        return "";
    }

    /// <summary>SERV.ACCOUNT &lt;args&gt; - CAccounts::Account_OnCmd (CAccount.cpp:422).
    /// Payload "&lt;caller plevel&gt;|&lt;args&gt;". The caller must be an Admin; the
    /// arguments split the way Str_ParseCmds splits them ("=, \t", at most five).</summary>
    private static string? HandleServAccountVerb(string payload)
    {
        if (_accounts == null) return "0";
        int bar = payload.IndexOf('|');
        if (bar < 0 || !int.TryParse(payload.AsSpan(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out int plevel))
            return "0";
        if (plevel < (int)PrivLevel.Admin)
            return "0";

        string[] cmd = ParseSphereCmds(payload[(bar + 1)..], 5);
        if (cmd.Length == 0 || cmd[0].Length == 0)
            return "0";

        switch (cmd[0].ToUpperInvariant())
        {
            case "ADD":
            case "ADDMD5":
            {
                // Cmd_AddNew: name required, an existing name refused, then
                // SetPassword(arg, md5) - ADDMD5 stores a 32-character digest as given.
                string name = cmd.Length > 1 ? cmd[1] : "";
                string pass = cmd.Length > 2 ? cmd[2] : "";
                if (name.Length == 0 || _accounts.FindAccount(name) != null)
                    return "0";
                bool md5 = cmd[0].Equals("ADDMD5", StringComparison.OrdinalIgnoreCase);
                var created = _accounts.CreateAccount(name, md5 ? Guid.NewGuid().ToString("N") : pass);
                if (created == null)
                    return "0";
                if (md5)
                    SetAccountMd5Password(created, pass);
                return "1";
            }
            case "UPDATE":
                _mainLoopActions.Enqueue(SaveAccountsToDisk);
                return "1";
            case "BLOCKED":
            case "HELP":
            case "JAILED":
            case "UNUSED":
                // Listing commands: their output is a console report, not a script value.
                return "0";
        }

        var account = _accounts.FindAccount(cmd[0]);
        if (account == null)
        {
            _log?.LogDebug("SERV.ACCOUNT: account '{Name}' does not exist", cmd[0]);
            return "0";
        }
        if (cmd.Length < 2 || cmd[1].Length == 0)
            return "1";

        // The verb and up to three arguments, re-joined with spaces (CAccount.cpp:515).
        string args = string.Join(" ", cmd.Skip(2).Take(3).Where(a => a.Length > 0));
        return ApplyAccountVerb(account, cmd[1], args, (PrivLevel)plevel) ? "1" : "0";
    }

    /// <summary>SERV.ACCOUNT.&lt;name&gt;.&lt;key&gt; value - pAccount-&gt;r_LoadVal with the
    /// line's own argument (CServer.cpp:1805-1829). Payload "&lt;name&gt;.&lt;key&gt;=&lt;value&gt;".</summary>
    private static string? HandleServAccountSet(string payload)
    {
        if (_accounts == null) return "0";
        int eq = payload.IndexOf('=');
        string path = eq < 0 ? payload : payload[..eq];
        string value = eq < 0 ? "" : payload[(eq + 1)..];
        int dot = path.IndexOf('.');
        if (dot <= 0 || dot == path.Length - 1)
            return "0";
        var account = _accounts.FindAccount(path[..dot]);
        if (account == null)
            return "0";
        return LoadAccountKey(account, path[(dot + 1)..].Trim(), value) ? "1" : "0";
    }

    /// <summary>CAccount::r_Verb (CAccount.cpp:1633): the account's verbs, then its
    /// keys, then a script function on the account.</summary>
    private static bool ApplyAccountVerb(Account account, string key, string args, PrivLevel callerLevel)
    {
        // Can't change accounts higher than you, unless you are an Admin.
        if (callerLevel < account.PrivLevel && callerLevel < PrivLevel.Admin)
            return false;

        switch (key.ToUpperInvariant())
        {
            case "BLOCK":
                // No argument, or a non-zero one, blocks; zero unblocks.
                _accounts.SetAccountBlocked(account.Name,
                    args.Length == 0 || !SphereNet.Core.Types.ScriptNumber.TryParseToken(args, out long block) || block != 0);
                return true;
            case "DELETE":
                return _accounts.DeleteAccount(account.Name);
            case "KICK":
            case "TAGLIST":
                return true;
        }

        if (LoadAccountKey(account, key, args))
            return true;

        return _triggerRunner != null && _triggerRunner.HasFunction(key) &&
            _triggerRunner.TryRunFunction(key, account, ScriptServerConsole.Instance,
                CreateFunctionArgs(args), out _);
    }

    private static SphereNet.Scripting.Execution.TriggerArgs CreateFunctionArgs(string args)
    {
        var funcArgs = new SphereNet.Scripting.Execution.TriggerArgs();
        funcArgs.InitFromRaw(args);
        return funcArgs;
    }

    /// <summary>CAccount::r_LoadVal for one key. PLEVEL goes through the account table
    /// so an online player's level follows; MD5PASSWORD stores a digest as given.</summary>
    private static bool LoadAccountKey(Account account, string key, string value)
    {
        switch (key.ToUpperInvariant())
        {
            case "PLEVEL":
            {
                // GetPrivLevelText: a level name or its number.
                string text = value.Trim();
                PrivLevel level;
                if (SphereNet.Core.Types.ScriptNumber.TryParseToken(text, out long n) &&
                    n >= (long)PrivLevel.Guest && n <= (long)PrivLevel.Owner)
                    level = (PrivLevel)n;
                else if (!Enum.TryParse(text, true, out level) || !Enum.IsDefined(level))
                    return false;
                _accounts.SetAccountPrivLevel(account.Name, level);
                SyncOnlineAccountPrivLevel(account.Name, level);
                return true;
            }
            case "MD5PASSWORD":
                SetAccountMd5Password(account, value.Trim());
                return true;
        }
        return account.TrySetProperty(key, value);
    }

    /// <summary>CAccount::SetPassword(hash, true) (CAccount.cpp:1010): with
    /// MD5PASSWORDS on, a 32-character digest is stored as it is and anything else is
    /// ignored; with it off the text is an ordinary password.</summary>
    private static void SetAccountMd5Password(Account account, string hash)
    {
        if (account.UseMd5Passwords)
        {
            if (hash.Length == 32)
                account.PasswordHash = hash;
            return;
        }
        account.SetPassword(hash);
    }

    /// <summary>Str_ParseCmds with the default separators (CExpression.cpp:137/284):
    /// split on '=', ',', space or tab outside quotes and brackets; a space separator
    /// also swallows one following separator; the last slot keeps the remainder; a
    /// separator at the very end still yields an empty final argument.</summary>
    internal static string[] ParseSphereCmds(string line, int max, string separators = "=, \t")
    {
        var parts = new List<string>();
        string rest = line.Trim();
        if (rest.Length == 0)
            return [];
        while (true)
        {
            if (parts.Count == max - 1)
            {
                parts.Add(rest);
                break;
            }
            int sep = FindTopLevelSeparator(rest, separators);
            if (sep < 0)
            {
                parts.Add(rest);
                break;
            }
            parts.Add(rest[..sep]);
            char ch = rest[sep];
            string next = rest[(sep + 1)..];
            if (char.IsWhiteSpace(ch))
            {
                next = next.TrimStart();
                if (next.Length > 0 && separators.Contains(next[0]))
                    next = next[1..];
            }
            rest = next.Trim();
            if (rest.Length == 0)
            {
                parts.Add("");
                break;
            }
        }
        return parts.ToArray();
    }

    private static int FindTopLevelSeparator(string text, string separators)
    {
        bool quoted = false;
        int curly = 0, square = 0, round = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            switch (ch)
            {
                case '{': curly++; continue;
                case '}': if (curly > 0) curly--; continue;
                case '[': square++; continue;
                case ']': if (square > 0) square--; continue;
                case '(': round++; continue;
                case ')': if (round > 0) round--; continue;
            }
            if (curly == 0 && square == 0 && round == 0 && separators.Contains(ch))
                return i;
        }
        return -1;
    }

    /// <summary>SERV.LOOKUPSKILL &lt;name&gt;: the pack's own skill names first - KEY
    /// renames included, which the C# enum does not know - then the enum and the
    /// defname table, then upstream's prefix match over the skill names
    /// (CServerConfig::SkillLookup, CServerConfig.cpp:1603). -1 when nothing matches.</summary>
    private static string ResolveLookupSkillName(string name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            return "-1";

        if (DefinitionLoader.TryGetSkillIndexByName(trimmed, out int byName) && byName >= 0)
            return byName.ToString(CultureInfo.InvariantCulture);

        if (Enum.TryParse<SkillType>(trimmed, true, out var sk) && sk != (SkillType)(-1) && Enum.IsDefined(sk))
            return ((int)sk).ToString(CultureInfo.InvariantCulture);

        if (_resources != null)
        {
            var rid = _resources.ResolveDefName(trimmed);
            if (rid.IsValid && rid.Type == ResType.SkillDef)
                return rid.Index.ToString(CultureInfo.InvariantCulture);
        }

        for (int i = 0; i < 256; i++)
        {
            var def = DefinitionLoader.GetSkillDef(i);
            if (def == null) continue;
            string label = !string.IsNullOrEmpty(def.Name) ? def.Name : def.DefName ?? "";
            if (label.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                return i.ToString(CultureInfo.InvariantCulture);
        }
        return "-1";
    }
}
