using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SphereNet.Tests;

public class SourceXVerbInventoryGuardrailTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static readonly Regex TableVerb = new(@"ADD\([^,]+,\s*""([^""]+)""\)", RegexOptions.Compiled);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex DispatchLine = new(
        @"^.*(?:\bcase\b|=>|==|\bis\b|\.Equals\s*\(|\.StartsWith\s*\().*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex UppercaseLiteral = new(@"""(_?[A-Z][A-Z0-9_.]*)", RegexOptions.Compiled);

    private static readonly Dictionary<string, string[]> SphereNetImplementationSources = new()
    {
        // ObjBase verbs can be completed by the client console bridge (dialogs,
        // menus, prompts and targeting). Character and Item inherit both paths.
        ["CObjBase_functions.tbl"] =
        [
            "src/SphereNet.Game/Objects/ObjBase.cs",
            "src/SphereNet.Game/Objects/Characters/Character.cs",
            "src/SphereNet.Game/Objects/Items/Item.cs",
            "src/SphereNet.Scripting/Execution/ScriptInterpreter.cs",
            "src/SphereNet.Game/Clients/ClientScriptConsoleHandler.cs"
        ],
        ["CChar_functions.tbl"] =
        [
            "src/SphereNet.Game/Objects/ObjBase.cs",
            "src/SphereNet.Game/Objects/Characters/Character.cs",
            "src/SphereNet.Scripting/Execution/ScriptInterpreter.cs",
            "src/SphereNet.Game/Clients/ClientScriptConsoleHandler.cs"
        ],
        ["CItem_functions.tbl"] =
        [
            "src/SphereNet.Game/Objects/ObjBase.cs",
            "src/SphereNet.Game/Objects/Items/Item.cs",
            "src/SphereNet.Scripting/Execution/ScriptInterpreter.cs",
            "src/SphereNet.Game/Clients/ClientScriptConsoleHandler.cs"
        ],
        ["CClient_functions.tbl"] =
        [
            "src/SphereNet.Game/Objects/ObjBase.cs",
            "src/SphereNet.Game/Objects/Characters/Character.cs",
            "src/SphereNet.Scripting/Execution/ScriptInterpreter.cs",
            "src/SphereNet.Game/Clients/ClientScriptConsoleHandler.cs"
        ],
        ["CServer.cpp"] =
        [
            "src/SphereNet.Scripting/Execution/ScriptInterpreter.cs",
            "src/SphereNet.Server/Program.Scripting.cs"
        ]
    };

    private static readonly Dictionary<string, string[]> ExpectedSourceXVerbs = new()
    {
        ["CObjBase_functions.tbl"] =
        [
            "ADDCLILOC", "BASEPROPLIST", "BASETAGLIST", "CLICK", "CLILOCLIST", "DAMAGE",
            "DCLICK", "DIALOG", "DIALOGCLOSE", "EDIT", "EFFECT", "EFFECTLOCATION", "EMOTE",
            "FIX", "FLIP", "GOAWAKE", "GOSLEEP", "INFO", "INPDLG", "MENU", "MESSAGE",
            "MESSAGEUA", "MOVE", "MOVENEAR", "MOVETO", "MSG", "NUDGEDOWN", "NUDGEUP", "P",
            "PROMPTCONSOLE", "PROMPTCONSOLEU", "PROPLIST", "REMOVE", "REMOVECLILOC",
            "REMOVEFROMVIEW", "REPLACECLILOC", "RESENDTOOLTIP", "SAY", "SAYU", "SAYUA",
            "SDIALOG", "SOUND", "SPELLEFFECT", "TAGLIST", "TARGET", "TIMERF", "TIMERFMS",
            "TRIGGER", "TRY", "TRYP", "TRYSRC", "TRYSRV", "UID", "UPDATE", "UPDATEX",
            "USEITEM", "Z"
        ],
        ["CChar_functions.tbl"] =
        [
            "AFK", "ALLSKILLS", "ANIM", "ATTACK", "BANK", "BARK", "BOUNCE", "BOW", "CONSUME",
            "CONTROL", "CRIMINAL", "CURE", "DESTROY", "DISCONNECT", "DROP", "DUPE", "EQUIP",
            "EQUIPARMOR", "EQUIPHALO", "EQUIPWEAPON", "FACE", "FIXWEIGHT", "FORGIVE", "GO",
            "GOCHAR", "GOCHARID", "GOCLI", "GOITEMID", "GONAME", "GOSOCK", "GOTYPE", "GOUID",
            "HEAR", "HUNGRY", "INVIS", "INVUL", "JAIL", "KILL", "MAKEITEM", "MOUNT",
            "NEWBIESKILL", "NEWGOLD", "NEWLOOT", "NOTOCLEAR", "NOTOUPDATE", "OWNER", "PACK",
            "POISON", "POLY", "PRIVSET", "REMOVE", "RESURRECT", "REVEAL", "SALUTE", "SKILL",
            "SKILLGAIN", "SLEEP", "SMSG", "SMSGL", "SMSGLEX", "SMSGU", "SUICIDE", "SUMMONCAGE",
            "SUMMONTO", "SYSMESSAGE", "SYSMESSAGEF", "SYSMESSAGELOC", "SYSMESSAGELOCEX",
            "SYSMESSAGEUA", "TARGETCLOSE", "UNDERWEAR", "UNEQUIP", "WAKE", "WHERE"
        ],
        ["CItem_functions.tbl"] =
        [
            "BOUNCE", "CARVECORPSE", "CONSUME", "CONTCONSUME", "DECAY", "DESTROY", "DROP",
            "DUPE", "EQUIP", "REPAIR", "SMELT", "UNEQUIP", "USE", "USEDOOR"
        ],
        ["CClient_functions.tbl"] =
        [
            "ADD", "ADDBUFF", "ADDCHAR", "ADDCONTEXTENTRY", "ADDITEM", "ARROWQUEST",
            "BADSPAWN", "BANKSELF", "CAST", "CHANGEFACE", "CHARLIST", "CLEARCTAGS",
            "CLOSEPAPERDOLL", "CLOSEPROFILE", "CLOSESTATUS", "CODEXOFWISDOM", "CTAGLIST",
            "DYE", "EVERBTARG", "EXTRACT", "FLUSH", "GMPAGE", "GOTARG", "INFO", "INFORMATION",
            "LAST", "LINK", "MAPWAYPOINT", "MENU", "MIDILIST", "NUDGE", "NUKE", "NUKECHAR",
            "OPENPAPERDOLL", "OPENTRADEWINDOW", "REMOVEBUFF", "REPAIR", "RESEND", "SAVE",
            "SCROLL", "SELF", "SENDPACKET", "SHOWSKILLS", "SKILLMENU", "SKILLSELECT",
            "SKILLUPDATE", "SMSG", "SMSGL", "SMSGLEX", "SMSGU", "SUMMON", "SYSMESSAGE",
            "SYSMESSAGEF", "SYSMESSAGELOC", "SYSMESSAGELOCEX", "SYSMESSAGEUA", "TELE", "TILE",
            "UNEXTRACT", "VERSION", "WEBLINK"
        ],
        ["CServer.cpp"] =
        [
            "ACCOUNT", "ACCOUNTS", "ALLCLIENTS", "B", "BLOCKIP", "CALCCRYPT", "CHARS",
            "CLEARLISTS", "CONSOLE", "EXPORT", "GARBAGE", "GMPAGES", "HEARALL", "IMPORT",
            "INFORMATION", "ITEMS", "LOAD", "LOG", "PRINTLISTS", "RESPAWN", "RESTOCK",
            "RESTORE", "RESYNC", "SAVE", "SAVECOUNT", "SAVESTATICS", "SECURE", "SHRINKMEM",
            "SHUTDOWN", "TIME", "UNBLOCKIP", "VARLIST"
        ]
    };

    /// <summary>Verified implementation backlog. The implementation guardrail
    /// below fails when an unlisted verb loses its dispatch route, and also
    /// fails when one of these entries gains a route without being removed
    /// from this explicit debt list.</summary>
    private static readonly Dictionary<string, string[]> KnownPartialOrDeferred = new()
    {
        ["CObjBase_functions.tbl"] =
        [
            "DAMAGE"
        ],
        ["CClient_functions.tbl"] =
        [
            "ADD", "ADDCHAR", "ADDITEM", "CLOSEPAPERDOLL", "CTAGLIST", "GMPAGE",
            "INFORMATION", "RESEND", "SAVE", "SELF", "SKILLSELECT", "VERSION"
        ]
    };

    [Fact]
    public void SourceXVerbSurfaces_MatchPinnedInventory()
    {
        // The Source-X reference tree lives only on dev machines (oldSphere/
        // is gitignored) — on CI this guardrail has nothing to diff against
        // and was the single red step in every GitHub Actions run.
        if (!Directory.Exists(Path.Combine(RepoRoot(), "oldSphere", "Source-X-full", "src", "tables")))
            return;

        foreach (var (surface, expected) in ExpectedSourceXVerbs)
        {
            var actual = surface.EndsWith(".tbl", StringComparison.Ordinal)
                ? ReadFunctionTable(surface)
                : ReadServerVerbs();

            Assert.Equal(
                expected.OrderBy(x => x, StringComparer.Ordinal),
                actual.OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void KnownBacklog_OnlyReferencesPinnedSourceXVerbs()
    {
        foreach (var (surface, verbs) in KnownPartialOrDeferred)
        {
            var upstream = ExpectedSourceXVerbs[surface].ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(verbs, verb => Assert.Contains(verb, upstream));
        }
    }

    [Fact]
    public void SourceXVerbSurfaces_AreRoutedBySphereNetImplementation()
    {
        var failures = new List<string>();
        foreach (var (surface, expected) in ExpectedSourceXVerbs)
        {
            var implemented = ReadImplementationVerbs(
                SphereNetImplementationSources[surface],
                normalizeServerPrefix: surface == "CServer.cpp");
            var deferred = KnownPartialOrDeferred.GetValueOrDefault(surface, []);
            string[] missing = expected
                .Except(implemented, StringComparer.OrdinalIgnoreCase)
                .Except(deferred, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            if (missing.Length > 0)
                failures.Add($"{surface}: {string.Join(", ", missing)}");

            string[] staleDebt = deferred
                .Intersect(implemented, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            if (staleDebt.Length > 0)
                failures.Add($"{surface} has implemented verbs still marked deferred: " +
                    string.Join(", ", staleDebt));
        }

        Assert.True(failures.Count == 0,
            "Pinned Source-X verbs with no SphereNet dispatch route:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures));
    }

    private static List<string> ReadFunctionTable(string fileName)
    {
        string path = Path.Combine(RepoRoot(), "oldSphere", "Source-X-full", "src", "tables", fileName);
        return File.ReadLines(path)
            .Select(line => TableVerb.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToList();
    }

    private static List<string> ReadServerVerbs()
    {
        string path = Path.Combine(RepoRoot(), "oldSphere", "Source-X-full", "src", "game", "CServer.cpp");
        var verbs = new List<string>();
        bool inTable = false;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Contains("CServer::sm_szVerbKeys", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }
            if (!inTable)
                continue;
            if (line.Contains("nullptr", StringComparison.Ordinal))
                break;

            var match = Regex.Match(line, @"""([A-Z0-9_]+)""");
            if (match.Success && !match.Groups[1].Value.Equals("CRASH", StringComparison.Ordinal))
                verbs.Add(match.Groups[1].Value);
        }
        return verbs;
    }

    private static HashSet<string> ReadImplementationVerbs(
        IEnumerable<string> relativePaths,
        bool normalizeServerPrefix)
    {
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in relativePaths)
        {
            string path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            string source = File.ReadAllText(path);
            source = BlockComment.Replace(source, "");
            source = LineComment.Replace(source, "");

            foreach (Match line in DispatchLine.Matches(source))
            foreach (Match literal in UppercaseLiteral.Matches(line.Value))
            {
                string token = literal.Groups[1].Value.TrimStart('_').TrimEnd('.');
                verbs.Add(token);
                if (normalizeServerPrefix && token.StartsWith("SERV.", StringComparison.Ordinal))
                    verbs.Add(token[5..]);
            }
        }
        return verbs;
    }
}
